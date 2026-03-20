import Foundation
import CoreGraphics
import IndolentKit

actor InMemorySettingsStore: SettingsStore {
    var settings: AppSettings

    init(settings: AppSettings) {
        self.settings = settings
    }

    func load() async throws -> AppSettings {
        settings
    }

    func save(_ settings: AppSettings) async throws {
        self.settings = settings
    }

    func snapshot() -> AppSettings {
        settings
    }
}

final class StubRuntime: ProviderRuntime {
    let providerID: ProviderID
    let defaults: ProviderDefaults

    init(providerID: ProviderID, defaults: ProviderDefaults) {
        self.providerID = providerID
        self.defaults = defaults
    }

    var displayName: String { providerID == .openCode ? "Open Code" : "OpenAI Codex" }
    var logsDirectoryURL: URL { FileManager.default.temporaryDirectory }
    var terminalTranscript: String { "" }

    func runPreflight() async -> ProviderPreflightResult { ProviderPreflightResult(isInstalled: true, isLoggedIn: true) }
    func readConfiguredDefaults() async -> ProviderDefaults { defaults }
    func loadModels() async -> [ProviderModelOption] { [] }
    func answer(_ request: AnswerRequest) async -> AnswerResult { AnswerResult(status: .success, text: "ok") }
    func runTerminalCommand(_ arguments: String) async -> TerminalCommandResult { TerminalCommandResult(exitCode: 0) }
    func clearTerminalTranscript() {}
}

final class RecordingClickPerformer: @unchecked Sendable, MouseClickPerforming {
    private(set) var clickedPoint: CGPoint?

    func click(at point: CGPoint) throws {
        clickedPoint = point
    }
}
