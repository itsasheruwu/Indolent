import AppKit
import Foundation

public protocol OpenCodeSetupProviding: Sendable {
    func ensureReady(progress: (@Sendable (String) -> Void)?) async -> OpenCodeSetupResult
}

public final class CodexModelCatalogService: @unchecked Sendable {
    private let cacheURL: URL

    public init(cacheURL: URL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent(".codex", isDirectory: true)
        .appendingPathComponent("models_cache.json")) {
        self.cacheURL = cacheURL
    }

    public func loadAvailableModels() async -> [ProviderModelOption] {
        guard
            FileManager.default.fileExists(atPath: cacheURL.path),
            let data = try? Data(contentsOf: cacheURL),
            let cache = try? JSONDecoder().decode(ModelsCache.self, from: data)
        else {
            return []
        }

        return cache.models
            .filter {
                $0.shellType.caseInsensitiveCompare("shell_command") == .orderedSame &&
                $0.visibility.caseInsensitiveCompare("list") == .orderedSame
            }
            .sorted {
                if $0.priority != $1.priority {
                    return $0.priority < $1.priority
                }
                return $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending
            }
            .map {
                ProviderModelOption(
                    slug: $0.slug,
                    displayName: $0.displayName.isEmpty ? $0.slug : $0.displayName,
                    description: $0.description,
                    visibility: $0.visibility,
                    priority: $0.priority,
                    defaultReasoningLevel: $0.defaultReasoningLevel,
                    supportedReasoningLevels: $0.supportedReasoningLevels
                )
            }
    }

    private struct ModelsCache: Decodable {
        let models: [Model]
    }

    private struct Model: Decodable {
        let slug: String
        let displayName: String
        let description: String
        let visibility: String
        let priority: Int
        let shellType: String
        let defaultReasoningLevel: String
        let supportedReasoningLevels: [ReasoningLevelOption]

        enum CodingKeys: String, CodingKey {
            case slug
            case displayName = "display_name"
            case description
            case visibility
            case priority
            case shellType = "shell_type"
            case defaultReasoningLevel = "default_reasoning_level"
            case supportedReasoningLevels = "supported_reasoning_levels"
        }
    }
}

public final class CodexProviderRuntime: ProviderRuntime {
    private let runner: CommandRunning
    private let modelCatalog: CodexModelCatalogService
    private let transcriptStore: TranscriptStore
    private let workingDirectory: String

    public init(
        runner: CommandRunning,
        modelCatalog: CodexModelCatalogService = CodexModelCatalogService(),
        workingDirectory: String = FileManager.default.currentDirectoryPath
    ) {
        self.runner = runner
        self.modelCatalog = modelCatalog
        self.workingDirectory = workingDirectory.isEmpty ? FileManager.default.homeDirectoryForCurrentUser.path : workingDirectory
        self.transcriptStore = TranscriptStore(
            initialText: "Shared Codex CLI transcript.\nThis view shows the actual Codex commands the app runs.\n\n",
            logFileURL: AppSupportPaths.logsDirectory.appendingPathComponent("codex-\(Self.logTimestamp()).log")
        )
    }

    public var providerID: ProviderID { .openAICodex }
    public var displayName: String { "OpenAI Codex" }
    public var logsDirectoryURL: URL { AppSupportPaths.logsDirectory }
    public var terminalTranscript: String { transcriptStore.text }

    public func runPreflight() async -> ProviderPreflightResult {
        let result = await runShellCommand("codex -V", label: "preflight", timeout: 10)
        guard result.exitCode == 0 else {
            return ProviderPreflightResult(
                isInstalled: false,
                blockingMessage: "Install Codex CLI first. Indolent blocks answering until `codex` is available on PATH."
            )
        }

        return ProviderPreflightResult(
            isInstalled: true,
            version: result.standardOutput.trimmingCharacters(in: .whitespacesAndNewlines),
            isLoggedIn: true
        )
    }

    public func readConfiguredDefaults() async -> ProviderDefaults {
        let configURL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex", isDirectory: true)
            .appendingPathComponent("config.toml")
        guard
            let text = try? String(contentsOf: configURL),
            !text.isEmpty
        else {
            return ProviderDefaults()
        }

        let model = Self.firstMatch(in: text, pattern: #"^model\s*=\s*"([^"]+)""#)
        let reasoning = Self.firstMatch(in: text, pattern: #"^model_reasoning_effort\s*=\s*"([^"]+)""#)
        return ProviderDefaults(selectedModel: model ?? "", selectedReasoningEffort: reasoning ?? "")
    }

    public func loadModels() async -> [ProviderModelOption] {
        await modelCatalog.loadAvailableModels()
    }

    public func answer(_ request: AnswerRequest) async -> AnswerResult {
        let outputURL = FileManager.default.temporaryDirectory.appendingPathComponent("indolent-\(UUID().uuidString).txt")
        let command = buildAnswerCommand(request: request, outputPath: outputURL.path)
        let started = Date()
        let result = await runShellCommand(command, label: "answer", standardInput: buildPrompt(request), timeout: 60)

        defer { try? FileManager.default.removeItem(at: outputURL) }

        if result.timedOut {
            return AnswerResult(status: .timeout, errorMessage: "Codex timed out before returning an answer.", duration: Date().timeIntervalSince(started))
        }

        guard result.exitCode == 0 else {
            return AnswerResult(
                status: .failed,
                errorMessage: "Codex failed: \(Self.firstNonEmpty(result.standardError, result.standardOutput, "Unknown CLI failure."))",
                duration: Date().timeIntervalSince(started)
            )
        }

        guard
            let text = try? String(contentsOf: outputURL).trimmingCharacters(in: .whitespacesAndNewlines),
            !text.isEmpty
        else {
            return AnswerResult(status: .failed, errorMessage: "Codex did not return an output message file.", duration: Date().timeIntervalSince(started))
        }

        transcriptStore.append("[assistant]\n\(text)\n\n")
        return AnswerResult(status: .success, text: text, duration: Date().timeIntervalSince(started))
    }

    public func runTerminalCommand(_ arguments: String) async -> TerminalCommandResult {
        let resolved = arguments.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "--help" : arguments
        let result = await runShellCommand("codex \(resolved)", label: "terminal", timeout: 120)
        return TerminalCommandResult(
            exitCode: result.exitCode,
            standardOutput: result.standardOutput,
            standardError: result.standardError,
            timedOut: result.timedOut
        )
    }

    public func clearTerminalTranscript() {
        transcriptStore.clear()
    }

    private func buildAnswerCommand(request: AnswerRequest, outputPath: String) -> String {
        var parts = [
            "codex",
            "-C", Shell.quote(workingDirectory),
            "exec",
            "--skip-git-repo-check",
            "--ephemeral",
            "--color", "never",
            "--output-last-message", Shell.quote(outputPath),
            "-m", Shell.quote(request.model)
        ]

        if !request.screenshotPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            parts.append(contentsOf: ["--image", Shell.quote(request.screenshotPath)])
        }
        if !request.reasoningEffort.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            parts.append(contentsOf: ["-c", Shell.quote(#"model_reasoning_effort="\#(request.reasoningEffort)""#)])
        }
        parts.append("-")
        return parts.joined(separator: " ")
    }

    private func runShellCommand(
        _ command: String,
        label: String,
        standardInput: String? = nil,
        timeout: TimeInterval
    ) async -> ProcessOutput {
        transcriptStore.append("[\(Self.timeStamp())] \(label): \(command)\n")
        if let standardInput, !standardInput.isEmpty {
            transcriptStore.append("[stdin]\n\(standardInput.trimmingCharacters(in: .newlines))\n")
        }

        let result = await runner.run(command: command, standardInput: standardInput, timeoutSeconds: timeout, environment: [:])
        if !result.standardOutput.isEmpty {
            transcriptStore.append("[stdout]\n\(result.standardOutput.trimmingCharacters(in: .newlines))\n")
        }
        if !result.standardError.isEmpty {
            transcriptStore.append("[stderr]\n\(result.standardError.trimmingCharacters(in: .newlines))\n")
        }
        transcriptStore.append("[\(result.timedOut ? "timed out" : "exit \(result.exitCode)")]\n\n")
        return result
    }

    private func buildPrompt(_ request: AnswerRequest) -> String {
        var text = request.prompt
        if !request.screenText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            text += "\n\nOCR text:\n\(request.screenText)"
        }
        return text
    }

    private static func firstMatch(in value: String, pattern: String) -> String? {
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.anchorsMatchLines]) else {
            return nil
        }
        let nsRange = NSRange(value.startIndex..<value.endIndex, in: value)
        guard
            let match = regex.firstMatch(in: value, range: nsRange),
            let range = Range(match.range(at: 1), in: value)
        else {
            return nil
        }
        return String(value[range])
    }

    private static func firstNonEmpty(_ values: String...) -> String {
        values.first { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } ?? ""
    }

    static func timeStamp() -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        return formatter.string(from: .now)
    }

    static func logTimestamp() -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        return formatter.string(from: .now)
    }
}

public final class OpenCodeProviderRuntime: ProviderRuntime {
    private let runner: CommandRunning
    private let transcriptStore: TranscriptStore
    private let workingDirectory: String
    private let session: URLSession

    public init(runner: CommandRunning, workingDirectory: String = FileManager.default.currentDirectoryPath, session: URLSession = .shared) {
        self.runner = runner
        self.workingDirectory = workingDirectory.isEmpty ? FileManager.default.homeDirectoryForCurrentUser.path : workingDirectory
        self.session = session
        self.transcriptStore = TranscriptStore(
            initialText: "Shared Open Code transcript.\nThis view shows the actual Open Code commands the app runs.\n\n",
            logFileURL: AppSupportPaths.openCodeLogsDirectory.appendingPathComponent("opencode-\(CodexProviderRuntime.logTimestamp()).log")
        )
    }

    public var providerID: ProviderID { .openCode }
    public var displayName: String { "Open Code" }
    public var logsDirectoryURL: URL { AppSupportPaths.openCodeLogsDirectory }
    public var terminalTranscript: String { transcriptStore.text }

    public func runPreflight() async -> ProviderPreflightResult {
        clearStaleStagedScreenshots()
        let version = await runShellCommand("opencode --version", label: "preflight", timeout: 10, environment: [:])
        guard version.exitCode == 0 else {
            return ProviderPreflightResult(isInstalled: false, blockingMessage: "Install Open Code first. Indolent blocks answering until `opencode` is available on PATH.")
        }

        let ollamaState = await probeOllama()
        if !ollamaState.isReachable {
            return ProviderPreflightResult(isInstalled: true, version: version.standardOutput.trimmingCharacters(in: CharacterSet.whitespacesAndNewlines), isLoggedIn: false, blockingMessage: "Open Code is installed, but Ollama is not reachable at `http://localhost:11434`.")
        }
        if !ollamaState.hasGemma {
            return ProviderPreflightResult(isInstalled: true, version: version.standardOutput.trimmingCharacters(in: CharacterSet.whitespacesAndNewlines), isLoggedIn: false, blockingMessage: "Ollama is reachable, but model `gemma3:4b` is not available.")
        }

        return ProviderPreflightResult(isInstalled: true, version: version.standardOutput.trimmingCharacters(in: CharacterSet.whitespacesAndNewlines), isLoggedIn: true)
    }

    public func readConfiguredDefaults() async -> ProviderDefaults {
        ProviderDefaults(selectedModel: "ollama/gemma3:4b", selectedReasoningEffort: "")
    }

    public func loadModels() async -> [ProviderModelOption] {
        [
            ProviderModelOption(
                slug: "ollama/gemma3:4b",
                displayName: "Gemma 3",
                description: "Ollama (local)",
                visibility: "list"
            )
        ]
    }

    public func answer(_ request: AnswerRequest) async -> AnswerResult {
        let started = Date()
        let stagedPath = stageScreenshot(request.screenshotPath)

        defer {
            if let stagedPath {
                try? FileManager.default.removeItem(atPath: stagedPath)
            }
        }

        var parts = [
            "opencode",
            "run",
            "--format", "default",
            "--dir", Shell.quote(workingDirectory),
            "--agent", "summary",
            "-m", Shell.quote(request.model)
        ]
        if let stagedPath {
            parts.append(contentsOf: ["-f", Shell.quote(stagedPath)])
        }
        parts.append(contentsOf: ["--", Shell.quote(buildPrompt(request))])

        let result = await runShellCommand(parts.joined(separator: " "), label: "answer", timeout: 60, environment: [
            "OPENCODE_CONFIG_CONTENT": Self.runtimeConfig
        ])

        if result.timedOut {
            return AnswerResult(status: .timeout, errorMessage: "Open Code timed out before returning an answer.", duration: Date().timeIntervalSince(started))
        }
        guard result.exitCode == 0 else {
            return AnswerResult(status: .failed, errorMessage: "Open Code failed: \(Self.firstNonEmpty(result.standardError, result.standardOutput, "Unknown CLI failure."))", duration: Date().timeIntervalSince(started))
        }

        let text = result.standardOutput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else {
            return AnswerResult(status: .empty, errorMessage: "Open Code returned an empty answer.", duration: Date().timeIntervalSince(started))
        }

        transcriptStore.append("[assistant]\n\(text)\n\n")
        return AnswerResult(status: .success, text: text, duration: Date().timeIntervalSince(started))
    }

    public func runTerminalCommand(_ arguments: String) async -> TerminalCommandResult {
        let resolved = arguments.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "--help" : arguments
        let result = await runShellCommand("opencode \(resolved)", label: "terminal", timeout: 120, environment: [
            "OPENCODE_CONFIG_CONTENT": Self.runtimeConfig
        ])
        return TerminalCommandResult(
            exitCode: result.exitCode,
            standardOutput: result.standardOutput,
            standardError: result.standardError,
            timedOut: result.timedOut
        )
    }

    public func clearTerminalTranscript() {
        transcriptStore.clear()
    }

    private func buildPrompt(_ request: AnswerRequest) -> String {
        var text = request.prompt
        if !request.screenText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            text += "\n\nOCR text:\n\(request.screenText)"
        }
        return text
    }

    private func stageScreenshot(_ path: String) -> String? {
        let trimmed = path.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, FileManager.default.fileExists(atPath: trimmed) else {
            return nil
        }

        try? FileManager.default.createDirectory(at: AppSupportPaths.openCodeScreenshotStagingDirectory, withIntermediateDirectories: true)
        let stagedURL = AppSupportPaths.openCodeScreenshotStagingDirectory
            .appendingPathComponent("indolent-opencode-\(UUID().uuidString)")
            .appendingPathExtension(URL(fileURLWithPath: trimmed).pathExtension)
        try? FileManager.default.copyItem(atPath: trimmed, toPath: stagedURL.path)
        return stagedURL.path
    }

    private func clearStaleStagedScreenshots() {
        guard let items = try? FileManager.default.contentsOfDirectory(at: AppSupportPaths.openCodeScreenshotStagingDirectory, includingPropertiesForKeys: nil) else {
            return
        }
        for item in items {
            try? FileManager.default.removeItem(at: item)
        }
    }

    private func probeOllama() async -> (isReachable: Bool, hasGemma: Bool) {
        guard let url = URL(string: "http://localhost:11434/api/tags") else {
            return (false, false)
        }
        do {
            let (data, response) = try await session.data(from: url)
            guard let http = response as? HTTPURLResponse, (200 ..< 300).contains(http.statusCode) else {
                return (false, false)
            }
            let payload = try JSONDecoder().decode(OllamaTagsResponse.self, from: data)
            let hasGemma = payload.models.contains { $0.name.caseInsensitiveCompare("gemma3:4b") == .orderedSame }
            return (true, hasGemma)
        } catch {
            return (false, false)
        }
    }

    private func runShellCommand(
        _ command: String,
        label: String,
        timeout: TimeInterval,
        environment: [String: String]
    ) async -> ProcessOutput {
        transcriptStore.append("[\(CodexProviderRuntime.timeStamp())] \(label): \(command)\n")
        let result = await runner.run(command: command, standardInput: nil, timeoutSeconds: timeout, environment: OpenCodeEnvironment.shellEnvironment(environment))
        if !result.standardOutput.isEmpty {
            transcriptStore.append("[stdout]\n\(result.standardOutput.trimmingCharacters(in: .newlines))\n")
        }
        if !result.standardError.isEmpty {
            transcriptStore.append("[stderr]\n\(result.standardError.trimmingCharacters(in: .newlines))\n")
        }
        transcriptStore.append("[\(result.timedOut ? "timed out" : "exit \(result.exitCode)")]\n\n")
        return result
    }

    private static let runtimeConfig = """
    {
      "$schema": "https://opencode.ai/config.json",
      "provider": {
        "ollama": {
          "name": "Ollama (local)",
          "npm": "@ai-sdk/openai-compatible",
          "models": {
            "gemma3:4b": {
              "name": "Gemma 3"
            }
          },
          "options": {
            "baseURL": "http://localhost:11434/v1"
          }
        }
      }
    }
    """

    private static func firstNonEmpty(_ values: String...) -> String {
        values.first { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } ?? ""
    }

    private struct OllamaTagsResponse: Decodable {
        let models: [OllamaTagModel]
    }

    private struct OllamaTagModel: Decodable {
        let name: String
    }
}

public final class OpenCodeSetupService: OpenCodeSetupProviding {
    private let runner: CommandRunning
    private let session: URLSession

    public init(runner: CommandRunning, session: URLSession = .shared) {
        self.runner = runner
        self.session = session
    }

    public func ensureReady(progress: (@Sendable (String) -> Void)?) async -> OpenCodeSetupResult {
        var details: [String] = []

        func add(_ line: String) {
            details.append(line)
            progress?(line)
        }

        add("Checking Open Code...")
        let openCodeCheck = await runner.run(command: "command -v opencode >/dev/null 2>&1", standardInput: nil, timeoutSeconds: 10, environment: OpenCodeEnvironment.shellEnvironment())
        if openCodeCheck.exitCode != 0 {
            add("Installing Open Code with npm...")
            let npmInstall = await runner.run(
                command: "npm install -g --prefix \(Shell.quote(AppSupportPaths.openCodeNpmPrefixDirectory.path)) opencode-ai",
                standardInput: nil,
                timeoutSeconds: 180,
                environment: OpenCodeEnvironment.shellEnvironment()
            )
            if npmInstall.exitCode != 0 {
                return OpenCodeSetupResult(isSuccess: false, summary: "Open Code could not be installed automatically.", detail: Self.detail(from: details, output: npmInstall))
            }
        }

        add("Checking Ollama...")
        let ollamaReachable = await isOllamaReachable()
        if !ollamaReachable {
            let hasBrew = await runner.run(command: "command -v brew >/dev/null 2>&1", standardInput: nil, timeoutSeconds: 10, environment: [:]).exitCode == 0
            if hasBrew {
                add("Installing Ollama with Homebrew...")
                let brewInstall = await runner.run(command: "brew install --cask ollama", standardInput: nil, timeoutSeconds: 300, environment: [:])
                if brewInstall.exitCode != 0 {
                    return OpenCodeSetupResult(isSuccess: false, summary: "Ollama could not be installed automatically.", detail: Self.detail(from: details, output: brewInstall))
                }
            } else {
                add("Homebrew is unavailable. Opening the Ollama download page...")
                await MainActor.run {
                    if let url = URL(string: "https://ollama.com/download/mac") {
                        NSWorkspace.shared.open(url)
                    }
                }
                return OpenCodeSetupResult(
                    isSuccess: false,
                    summary: "Install Ollama manually, then rerun setup.",
                    detail: details.joined(separator: "\n")
                )
            }
        }

        add("Starting Ollama...")
        _ = await runner.run(command: "pgrep -x ollama >/dev/null 2>&1 || nohup ollama serve >/tmp/indolent-ollama.log 2>&1 &", standardInput: nil, timeoutSeconds: 10, environment: [:])

        for _ in 0 ..< 20 {
            if await isOllamaReachable() {
                break
            }
            try? await Task.sleep(nanoseconds: 1_000_000_000)
        }

        guard await isOllamaReachable() else {
            return OpenCodeSetupResult(isSuccess: false, summary: "Ollama is not reachable.", detail: details.joined(separator: "\n"))
        }

        add("Checking Gemma 3...")
        if !(await hasModel(named: "gemma3:4b")) {
            add("Downloading Gemma 3...")
            let pull = await runner.run(
                command: "curl -s http://localhost:11434/api/pull -d '{\"model\":\"gemma3:4b\",\"stream\":false}' -H 'Content-Type: application/json'",
                standardInput: nil,
                timeoutSeconds: 300,
                environment: [:]
            )
            let hasModelAfterPull = await hasModel(named: "gemma3:4b")
            if pull.exitCode != 0 || !hasModelAfterPull {
                return OpenCodeSetupResult(isSuccess: false, summary: "Gemma 3 could not be downloaded automatically.", detail: Self.detail(from: details, output: pull))
            }
        }

        add("Open Code setup completed successfully.")
        return OpenCodeSetupResult(isSuccess: true, summary: "Open Code, Ollama, and Gemma 3 are ready.", detail: details.joined(separator: "\n"))
    }

    private func isOllamaReachable() async -> Bool {
        guard let url = URL(string: "http://localhost:11434/api/tags") else {
            return false
        }
        do {
            let (_, response) = try await session.data(from: url)
            guard let http = response as? HTTPURLResponse else {
                return false
            }
            return (200 ..< 300).contains(http.statusCode)
        } catch {
            return false
        }
    }

    private func hasModel(named modelName: String) async -> Bool {
        guard let url = URL(string: "http://localhost:11434/api/tags") else {
            return false
        }
        do {
            let (data, response) = try await session.data(from: url)
            guard let http = response as? HTTPURLResponse, (200 ..< 300).contains(http.statusCode) else {
                return false
            }
            let payload = try JSONDecoder().decode(OllamaTagsPayload.self, from: data)
            return payload.models.contains { $0.name.caseInsensitiveCompare(modelName) == .orderedSame }
        } catch {
            return false
        }
    }

    private static func detail(from lines: [String], output: ProcessOutput) -> String {
        var combined = lines.joined(separator: "\n")
        if !output.standardOutput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            combined += "\n" + output.standardOutput.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if !output.standardError.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            combined += "\n" + output.standardError.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return combined
    }

    private struct OllamaTagsPayload: Decodable {
        let models: [OllamaTag]
    }

    private struct OllamaTag: Decodable {
        let name: String
    }
}
