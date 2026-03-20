import IndolentKit
import Testing

struct AppStateTests {
    @Test
    @MainActor
    func initializesDefaultSelectionsAndPersistsRecentModels() async throws {
        let state = AppState()
        let store = InMemorySettingsStore(settings: AppSettings())
        let runtime = StubRuntime(
            providerID: .openAICodex,
            defaults: ProviderDefaults(selectedModel: "gpt-5.4", selectedReasoningEffort: "low")
        )
        let registry = ProviderRuntimeRegistry(runtimes: [runtime])

        try await state.initialize(settingsStore: store, providerRegistry: registry)
        #expect(state.selectedProviderID == ProviderID.openAICodex.rawValue)
        #expect(state.selectedModel == "gpt-5.4")

        state.setSelectedModel("gpt-5.4-mini")
        try await state.persist(settingsStore: store)
        let saved = await store.snapshot()
        #expect(saved.providerSelections[ProviderID.openAICodex.rawValue]?.recentModels.first == "gpt-5.4-mini")
    }
}
