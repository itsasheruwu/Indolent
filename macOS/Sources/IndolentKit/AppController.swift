import AppKit
import Foundation
import Observation

@MainActor
@Observable
public final class AppController {
    public let appState: AppState
    public let widgetCoordinator: WidgetAnswerCoordinator
    public let permissionService: PermissionProviding

    private let settingsStore: SettingsStore
    private let providerRegistry: ProviderRuntimeRegistry
    private let openCodeSetupService: OpenCodeSetupProviding

    public var availableProviders: [ProviderOption] = []
    public var availableModels: [ProviderModelOption] = []
    public var reasoningOptions: [ReasoningLevelOption] = []
    public var terminalCommandText = "--help"
    public var setupStatusText = ""
    public var isRefreshing = false
    public var isRunningTerminalCommand = false
    public var isRunningGuidedSetup = false
    public var hasStarted = false

    public init(
        appState: AppState,
        settingsStore: SettingsStore,
        providerRegistry: ProviderRuntimeRegistry,
        openCodeSetupService: OpenCodeSetupProviding,
        permissionService: PermissionProviding,
        widgetCoordinator: WidgetAnswerCoordinator
    ) {
        self.appState = appState
        self.settingsStore = settingsStore
        self.providerRegistry = providerRegistry
        self.openCodeSetupService = openCodeSetupService
        self.permissionService = permissionService
        self.widgetCoordinator = widgetCoordinator
        self.availableProviders = providerRegistry.providers
    }

    public var currentProvider: ProviderRuntime {
        providerRegistry.provider(for: appState.selectedProviderID)
    }

    public var terminalTranscript: String {
        currentProvider.terminalTranscript
    }

    public var logsDirectoryPath: String {
        currentProvider.logsDirectoryURL.path
    }

    public var installGuideURL: URL? {
        switch currentProvider.providerID {
        case .openCode:
            URL(string: "https://opencode.ai/docs")
        case .openAICodex:
            URL(string: "https://help.openai.com/en/articles/11096431-openai-codex-cli-getting-started")
        }
    }

    public func start() async {
        guard !hasStarted else {
            return
        }

        hasStarted = true
        do {
            try await appState.initialize(settingsStore: settingsStore, providerRegistry: providerRegistry)
            await reloadModels()
            await refreshPreflight()
        } catch {
            setupStatusText = "Startup failed: \(error.localizedDescription)"
        }
    }

    public func refreshPreflight() async {
        isRefreshing = true
        defer { isRefreshing = false }
        let result = await currentProvider.runPreflight()
        appState.updatePreflight(result)
        await reloadModels()
    }

    public func reloadModels() async {
        availableModels = await currentProvider.loadModels()
        syncReasoningOptions()
    }

    public func setProvider(_ providerID: String) async {
        do {
            try await appState.setSelectedProvider(providerID, providerRegistry: providerRegistry)
            await reloadModels()
            await refreshPreflight()
            try await appState.persist(settingsStore: settingsStore)
        } catch {
            setupStatusText = "Unable to switch providers: \(error.localizedDescription)"
        }
    }

    public func setModel(_ model: String) async {
        appState.setSelectedModel(model)
        syncReasoningOptions()
        try? await appState.persist(settingsStore: settingsStore)
    }

    public func setReasoning(_ reasoning: String) async {
        appState.setSelectedReasoningEffort(reasoning)
        try? await appState.persist(settingsStore: settingsStore)
    }

    public func runTerminalCommand() async {
        guard !isRunningTerminalCommand else {
            return
        }
        isRunningTerminalCommand = true
        defer { isRunningTerminalCommand = false }
        _ = await currentProvider.runTerminalCommand(terminalCommandText)
    }

    public func clearTranscript() {
        currentProvider.clearTerminalTranscript()
    }

    public func runGuidedSetup() async {
        guard currentProvider.providerID == .openCode, !isRunningGuidedSetup else {
            return
        }

        isRunningGuidedSetup = true
        setupStatusText = ""
        let result = await openCodeSetupService.ensureReady { [weak self] message in
            Task { @MainActor in
                self?.setupStatusText = message
            }
        }
        setupStatusText = result.isSuccess ? result.summary : "\(result.summary)\n\n\(result.detail)"
        isRunningGuidedSetup = false
        await refreshPreflight()
    }

    public func openLogsDirectory() {
        AppSupportPaths.ensureDirectoriesExist()
        NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: logsDirectoryPath)
    }

    public func openInstallGuide() {
        guard let installGuideURL else {
            return
        }
        NSWorkspace.shared.open(installGuideURL)
    }

    public func requestScreenRecordingPermission() {
        _ = permissionService.requestScreenRecordingPermission()
    }

    public func requestAccessibilityPermission() {
        _ = permissionService.hasAccessibilityPermission(prompt: true)
    }

    public func persistSettings() async {
        try? await appState.persist(settingsStore: settingsStore)
    }

    private func syncReasoningOptions() {
        let selectedOption = availableModels.first(where: { $0.slug.caseInsensitiveCompare(appState.selectedModel) == .orderedSame })
        reasoningOptions = selectedOption?.supportedReasoningLevels ?? []

        if reasoningOptions.isEmpty, currentProvider.providerID == .openAICodex {
            reasoningOptions = [
                .init(effort: "low", description: "Lower latency reasoning."),
                .init(effort: "medium", description: "Balanced reasoning."),
                .init(effort: "high", description: "Higher depth reasoning.")
            ]
        }

        if appState.selectedReasoningEffort.isEmpty, let first = reasoningOptions.first {
            appState.setSelectedReasoningEffort(first.effort)
        }
    }
}
