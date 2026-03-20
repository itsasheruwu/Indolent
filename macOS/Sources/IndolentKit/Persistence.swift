import Foundation
import Observation

public protocol SettingsStore: Sendable {
    func load() async throws -> AppSettings
    func save(_ settings: AppSettings) async throws
}

public final class JSONSettingsStore: SettingsStore {
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder
    private let settingsURL: URL

    public init(settingsURL: URL = AppSupportPaths.settingsFile) {
        self.settingsURL = settingsURL
        self.encoder = JSONEncoder()
        self.decoder = JSONDecoder()
        self.encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    }

    public func load() async throws -> AppSettings {
        guard FileManager.default.fileExists(atPath: settingsURL.path) else {
            return AppSettings()
        }

        let data = try Data(contentsOf: settingsURL)
        return try decoder.decode(AppSettings.self, from: data)
    }

    public func save(_ settings: AppSettings) async throws {
        AppSupportPaths.ensureDirectoriesExist()
        let data = try encoder.encode(settings)
        try data.write(to: settingsURL, options: .atomic)
    }
}

public protocol ProviderRuntime: AnyObject, Sendable {
    var providerID: ProviderID { get }
    var displayName: String { get }
    var logsDirectoryURL: URL { get }
    var terminalTranscript: String { get }

    func runPreflight() async -> ProviderPreflightResult
    func readConfiguredDefaults() async -> ProviderDefaults
    func loadModels() async -> [ProviderModelOption]
    func answer(_ request: AnswerRequest) async -> AnswerResult
    func runTerminalCommand(_ arguments: String) async -> TerminalCommandResult
    func clearTerminalTranscript()
}

public final class ProviderRuntimeRegistry: @unchecked Sendable {
    private let runtimesByID: [ProviderID: ProviderRuntime]

    public init(runtimes: [ProviderRuntime]) {
        self.runtimesByID = Dictionary(uniqueKeysWithValues: runtimes.map { ($0.providerID, $0) })
    }

    public var providers: [ProviderOption] {
        ProviderID.allCases.compactMap { id in
            guard let runtime = runtimesByID[id] else {
                return nil
            }

            return ProviderOption(id: runtime.providerID.rawValue, displayName: runtime.displayName)
        }
    }

    public func isKnownProvider(_ providerID: String?) -> Bool {
        runtimesByID[ProviderID.normalize(providerID)] != nil
    }

    public func provider(for providerID: String?) -> ProviderRuntime {
        runtimesByID[ProviderID.normalize(providerID)] ?? runtimesByID[.openAICodex]!
    }
}

@MainActor
@Observable
public final class AppState {
    private var settings = AppSettings()
    public private(set) var preflight = ProviderPreflightResult()
    public private(set) var selectedProviderID = ProviderID.openAICodex.rawValue
    public private(set) var selectedModel = ""
    public private(set) var selectedReasoningEffort = ""
    public private(set) var lastAnswerSummary = "No answer yet."
    public private(set) var lastAnswerDetail = ""
    public private(set) var isAnswering = false

    public init() {}

    public var recentModels: [String] {
        currentSelection.recentModels
    }

    public var startWithWidget: Bool {
        get { settings.startWithWidget }
        set { settings.startWithWidget = newValue }
    }

    public var agentModeEnabled: Bool {
        get { settings.agentModeEnabled }
        set { settings.agentModeEnabled = newValue }
    }

    public var agentLoopEnabled: Bool {
        get { settings.agentLoopEnabled }
        set { settings.agentLoopEnabled = newValue }
    }

    public var saveCurrentModelOnRestart: Bool {
        get { settings.saveCurrentModelOnRestart }
        set { settings.saveCurrentModelOnRestart = newValue }
    }

    public var widgetBounds: WidgetBounds {
        get { settings.widgetBounds }
        set { settings.widgetBounds = newValue }
    }

    public var canAnswer: Bool {
        preflight.isReady && !selectedModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !isAnswering
    }

    public func initialize(settingsStore: SettingsStore, providerRegistry: ProviderRuntimeRegistry) async throws {
        settings = try await settingsStore.load()
        migrateLegacySelections()

        let provider = providerRegistry.provider(for: settings.selectedProviderId)
        selectedProviderID = provider.providerID.rawValue
        settings.selectedProviderId = provider.providerID.rawValue
        try await loadProviderSelection(using: provider)
        lastAnswerSummary = "No answer yet."
        lastAnswerDetail = ""
    }

    public func setSelectedProvider(_ value: String, providerRegistry: ProviderRuntimeRegistry) async throws {
        let provider = providerRegistry.provider(for: value)
        guard provider.providerID.rawValue.caseInsensitiveCompare(selectedProviderID) != .orderedSame else {
            return
        }

        selectedProviderID = provider.providerID.rawValue
        settings.selectedProviderId = selectedProviderID
        preflight = ProviderPreflightResult()
        try await loadProviderSelection(using: provider)
    }

    public func updatePreflight(_ value: ProviderPreflightResult) {
        preflight = value
    }

    public func beginAnswer() {
        isAnswering = true
        lastAnswerSummary = "Thinking..."
        lastAnswerDetail = ""
    }

    public func cancelAnswer() {
        isAnswering = false
        lastAnswerSummary = "Stopped."
        lastAnswerDetail = "Stopped."
    }

    public func completeAnswer(_ result: AnswerResult) {
        isAnswering = false

        if result.isSuccess {
            lastAnswerSummary = result.text
            lastAnswerDetail = result.text
            var selection = currentSelection
            selection.lastSuccessfulModel = selectedModel
            selection.lastSuccessfulReasoningEffort = selectedReasoningEffort
            updateSelection(selection, for: selectedProviderID)
            addRecentModel(selectedModel)
        } else {
            lastAnswerSummary = result.errorMessage
            lastAnswerDetail = result.errorMessage
        }
    }

    public func setSelectedModel(_ value: String) {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return
        }

        selectedModel = trimmed
        var selection = currentSelection
        selection.selectedModel = trimmed
        updateSelection(selection, for: selectedProviderID)
        addRecentModel(trimmed)
    }

    public func setSelectedReasoningEffort(_ value: String) {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return
        }

        selectedReasoningEffort = trimmed
        var selection = currentSelection
        selection.selectedReasoningEffort = trimmed
        updateSelection(selection, for: selectedProviderID)
    }

    public func persist(settingsStore: SettingsStore) async throws {
        settings.selectedProviderId = selectedProviderID
        var selection = currentSelection
        selection.selectedModel = selectedModel
        selection.selectedReasoningEffort = selectedReasoningEffort
        updateSelection(selection, for: selectedProviderID)
        try await settingsStore.save(settings)
    }

    private func loadProviderSelection(using provider: ProviderRuntime) async throws {
        let configuredDefaults = await provider.readConfiguredDefaults()
        let selection = selection(for: provider.providerID.rawValue)
        let initialModel = settings.saveCurrentModelOnRestart
            ? firstNonEmpty(
                selection.selectedModel,
                selection.lastSuccessfulModel,
                configuredDefaults.selectedModel,
                provider.providerID == .openAICodex ? "gpt-5.4-mini" : "ollama/gemma3:4b"
            )
            : firstNonEmpty(
                configuredDefaults.selectedModel,
                provider.providerID == .openAICodex ? "gpt-5.4-mini" : "ollama/gemma3:4b"
            )
        let initialReasoning = firstNonEmpty(
            selection.selectedReasoningEffort,
            selection.lastSuccessfulReasoningEffort,
            configuredDefaults.selectedReasoningEffort,
            provider.providerID == .openAICodex ? "medium" : ""
        )

        selectedModel = initialModel
        selectedReasoningEffort = initialReasoning

        if !initialModel.isEmpty {
            addRecentModel(initialModel)
        }
    }

    private func migrateLegacySelections() {
        if settings.providerSelections.isEmpty {
            settings.providerSelections = [:]
        }

        var codex = selection(for: ProviderID.openAICodex.rawValue)
        if codex.selectedModel.isEmpty, !settings.selectedModel.isEmpty {
            codex.selectedModel = settings.selectedModel
        }
        if codex.selectedReasoningEffort.isEmpty, !settings.selectedReasoningEffort.isEmpty {
            codex.selectedReasoningEffort = settings.selectedReasoningEffort
        }
        if codex.lastSuccessfulModel.isEmpty, !settings.lastSuccessfulModel.isEmpty {
            codex.lastSuccessfulModel = settings.lastSuccessfulModel
        }
        if codex.lastSuccessfulReasoningEffort.isEmpty, !settings.lastSuccessfulReasoningEffort.isEmpty {
            codex.lastSuccessfulReasoningEffort = settings.lastSuccessfulReasoningEffort
        }
        if codex.recentModels.isEmpty, !settings.recentModels.isEmpty {
            codex.recentModels = settings.recentModels
        }

        updateSelection(codex, for: ProviderID.openAICodex.rawValue)
        _ = selection(for: ProviderID.openCode.rawValue)
    }

    private var currentSelection: ProviderSelectionSettings {
        selection(for: selectedProviderID)
    }

    private func selection(for providerID: String) -> ProviderSelectionSettings {
        let normalized = ProviderID.normalize(providerID).rawValue
        return settings.providerSelections[normalized] ?? ProviderSelectionSettings()
    }

    private func updateSelection(_ selection: ProviderSelectionSettings, for providerID: String) {
        let normalized = ProviderID.normalize(providerID).rawValue
        settings.providerSelections[normalized] = selection
    }

    private func addRecentModel(_ model: String) {
        let trimmed = model.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return
        }

        var selection = currentSelection
        selection.recentModels.removeAll { $0.caseInsensitiveCompare(trimmed) == .orderedSame }
        selection.recentModels.insert(trimmed, at: 0)
        if selection.recentModels.count > 8 {
            selection.recentModels.removeLast(selection.recentModels.count - 8)
        }

        updateSelection(selection, for: selectedProviderID)
    }

    private func firstNonEmpty(_ values: String?...) -> String {
        values.compactMap { $0 }.first(where: { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) ?? ""
    }
}
