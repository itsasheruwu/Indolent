import IndolentKit
import Testing

struct HeuristicsTests {
    @Test
    func agentClickRanksExactMatchHighest() {
        let layout = OcrLayoutResult(
            text: "A. Blue\nB. Green\nC. Red",
            lines: [
                .init(text: "A. Blue", bounds: .init(x: 0, y: 0, width: 100, height: 20)),
                .init(text: "B. Green", bounds: .init(x: 0, y: 30, width: 100, height: 20))
            ]
        )

        let candidates = AgentClickService.rankCandidates(answerText: "B. Green", ocrLayout: layout)
        #expect(AgentClickService.selectConfidentWinner(from: candidates)?.region.text == "B. Green")
    }

    @Test
    func videoHeuristicsExtractProgressAndEscalateReasoning() {
        let layout = OcrLayoutResult(text: "00:15 / 01:00  Settings  Pause")
        #expect(WidgetHeuristics.containsVideoSignal(layout))
        #expect(WidgetHeuristics.extractVideoProgress(from: layout.text)?.total == 60)
        #expect(WidgetHeuristics.nextReasoning(after: "low") == "medium")
    }

    @Test
    @MainActor
    func agentClickMapsRetinaOCRBoundsBackToDisplayCoordinates() async {
        let appState = AppState()
        let runtime = StubRuntime(providerID: .openAICodex, defaults: ProviderDefaults())
        let registry = ProviderRuntimeRegistry(runtimes: [runtime])
        let clickPerformer = RecordingClickPerformer()
        let service = AgentClickService(appState: appState, providerRegistry: registry, clickPerformer: clickPerformer)

        let capture = ScreenCaptureResult(
            imagePath: "",
            bounds: .init(x: 100, y: 50, width: 1920, height: 1080),
            imagePixelWidth: 3840,
            imagePixelHeight: 2160
        )
        let layout = OcrLayoutResult(
            text: "B. Green",
            lines: [
                .init(text: "B. Green", bounds: .init(x: 960, y: 540, width: 960, height: 200))
            ]
        )

        let result = await service.tryClickAnswer(
            answerText: "B. Green",
            capture: capture,
            ocrLayout: layout,
            model: "gpt-5.4-mini",
            reasoningEffort: "medium"
        )

        #expect(result.clicked)
        #expect(abs((clickPerformer.clickedPoint?.x ?? 0) - 820) < 0.001)
        #expect(abs((clickPerformer.clickedPoint?.y ?? 0) - 370) < 0.001)
    }
}
