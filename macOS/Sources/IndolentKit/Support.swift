import Darwin
import Foundation

public enum AppSupportPaths {
    public static let appSupportDirectory: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return base.appendingPathComponent("Indolent", isDirectory: true)
    }()

    public static let logsDirectory = appSupportDirectory.appendingPathComponent("logs", isDirectory: true)
    public static let openCodeLogsDirectory = logsDirectory.appendingPathComponent("opencode", isDirectory: true)
    public static let openCodeNpmPrefixDirectory = appSupportDirectory.appendingPathComponent("npm-global", isDirectory: true)
    public static let settingsFile = appSupportDirectory.appendingPathComponent("settings.json")
    public static let openCodeScreenshotStagingDirectory = FileManager.default.temporaryDirectory
        .appendingPathComponent("Indolent", isDirectory: true)
        .appendingPathComponent("opencode-screenshots", isDirectory: true)

    public static func ensureDirectoriesExist() {
        let manager = FileManager.default
        try? manager.createDirectory(at: appSupportDirectory, withIntermediateDirectories: true)
        try? manager.createDirectory(at: logsDirectory, withIntermediateDirectories: true)
        try? manager.createDirectory(at: openCodeLogsDirectory, withIntermediateDirectories: true)
        try? manager.createDirectory(at: openCodeNpmPrefixDirectory, withIntermediateDirectories: true)
    }
}

public enum OpenCodeEnvironment {
    public static func shellEnvironment(_ overrides: [String: String] = [:]) -> [String: String] {
        var environment = overrides
        let prefixPath = AppSupportPaths.openCodeNpmPrefixDirectory.path
        let binPath = AppSupportPaths.openCodeNpmPrefixDirectory.appendingPathComponent("bin", isDirectory: true).path
        let currentPath = ProcessInfo.processInfo.environment["PATH"] ?? ""

        environment["PATH"] = [binPath, currentPath].filter { !$0.isEmpty }.joined(separator: ":")
        environment["NPM_CONFIG_PREFIX"] = prefixPath
        return environment
    }
}

public enum Shell {
    public static func quote(_ value: String) -> String {
        let escaped = value.replacingOccurrences(of: "'", with: "'\"'\"'")
        return "'\(escaped)'"
    }
}

public struct ProcessOutput: Sendable {
    public var exitCode: Int32
    public var standardOutput: String
    public var standardError: String
    public var timedOut: Bool

    public init(exitCode: Int32, standardOutput: String, standardError: String, timedOut: Bool) {
        self.exitCode = exitCode
        self.standardOutput = standardOutput
        self.standardError = standardError
        self.timedOut = timedOut
    }
}

public protocol CommandRunning: Sendable {
    func run(
        command: String,
        standardInput: String?,
        timeoutSeconds: TimeInterval,
        environment: [String: String]
    ) async -> ProcessOutput
}

public final class ShellCommandRunner: CommandRunning, @unchecked Sendable {
    public init() {}

    public func run(
        command: String,
        standardInput: String? = nil,
        timeoutSeconds: TimeInterval = 60,
        environment: [String: String] = [:]
    ) async -> ProcessOutput {
        let process = Process()
        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        let stdinPipe = Pipe()

        process.executableURL = URL(fileURLWithPath: "/bin/zsh")
        process.arguments = ["-lc", command]
        process.standardOutput = stdoutPipe
        process.standardError = stderrPipe
        process.environment = ProcessInfo.processInfo.environment.merging(environment) { _, newValue in newValue }

        if standardInput != nil {
            process.standardInput = stdinPipe
        }

        let stdoutTask = Task.detached(priority: .utility) {
            stdoutPipe.fileHandleForReading.readDataToEndOfFile()
        }
        let stderrTask = Task.detached(priority: .utility) {
            stderrPipe.fileHandleForReading.readDataToEndOfFile()
        }

        do {
            try process.run()
        } catch {
            return ProcessOutput(exitCode: -1, standardOutput: "", standardError: error.localizedDescription, timedOut: false)
        }

        let timedOut = await withTaskCancellationHandler(operation: {
            if let standardInput {
                let data = Data(standardInput.utf8)
                stdinPipe.fileHandleForWriting.write(data)
                try? stdinPipe.fileHandleForWriting.close()
            }

            return await withTaskGroup(of: Bool.self) { group in
                group.addTask {
                    await Self.waitUntilExit(process)
                    return false
                }
                group.addTask {
                    let nanos = UInt64(max(timeoutSeconds, 0) * 1_000_000_000)
                    try? await Task.sleep(nanoseconds: nanos)
                    return true
                }

                let result = await group.next() ?? false
                group.cancelAll()
                return result
            }
        }, onCancel: {
            if process.isRunning {
                process.terminate()
                if process.isRunning {
                    kill(process.processIdentifier, SIGKILL)
                }
            }
        })

        if timedOut || Task.isCancelled {
            if process.isRunning {
                process.terminate()
                if process.isRunning {
                    kill(process.processIdentifier, SIGKILL)
                }
                await Self.waitUntilExit(process)
            }
        }

        let stdout = String(decoding: await stdoutTask.value, as: UTF8.self)
        let stderr = String(decoding: await stderrTask.value, as: UTF8.self)

        return ProcessOutput(
            exitCode: (timedOut || Task.isCancelled) ? -1 : process.terminationStatus,
            standardOutput: stdout,
            standardError: stderr,
            timedOut: timedOut || Task.isCancelled
        )
    }

    private static func waitUntilExit(_ process: Process) async {
        if !process.isRunning {
            return
        }

        await withCheckedContinuation { continuation in
            process.terminationHandler = { _ in
                continuation.resume()
            }
        }
    }
}

public final class TranscriptStore: @unchecked Sendable {
    private let lock = NSLock()
    private let logFileURL: URL
    private var textStorage: String

    public init(initialText: String, logFileURL: URL) {
        self.logFileURL = logFileURL
        self.textStorage = initialText
        AppSupportPaths.ensureDirectoriesExist()
    }

    public var text: String {
        lock.lock()
        defer { lock.unlock() }
        return textStorage
    }

    public func clear() {
        lock.lock()
        textStorage = ""
        lock.unlock()
    }

    public func append(_ chunk: String) {
        guard !chunk.isEmpty else {
            return
        }

        lock.lock()
        textStorage += chunk
        lock.unlock()

        AppSupportPaths.ensureDirectoriesExist()
        try? chunk.appendLine(to: logFileURL)
    }
}

private extension String {
    func appendLine(to url: URL) throws {
        if let data = data(using: .utf8) {
            if FileManager.default.fileExists(atPath: url.path) {
                let handle = try FileHandle(forWritingTo: url)
                defer { try? handle.close() }
                try handle.seekToEnd()
                try handle.write(contentsOf: data)
            } else {
                try data.write(to: url, options: .atomic)
            }
        }
    }
}
