import AppKit
import Foundation
import Observation

@MainActor
public protocol WidgetWindowing: AnyObject {
    func showWidget()
    func hideWidget()
    func bringToFront()
}

@MainActor
@Observable
public final class WidgetViewModel {
    public enum StatusPhase: Equatable {
        case none
        case thinking
        case screenshotTaken
        case extractingText
        case waitingVideo(TimeInterval?)
    }

    public var isHovered = false
    public var messageText = ""
    public var statusText = ""
    public var isError = false
    public var statusPhase: StatusPhase = .none

    public init() {}

    public var showActionButton: Bool {
        isHovered && messageText.isEmpty && statusText.isEmpty
    }

    public var isBusy: Bool {
        statusPhase != .none
    }
}

public enum WidgetHeuristics {
    private static let videoTerms = [
        "play", "pause", "resume", "captions", "subtitles", "settings", "rewind",
        "forward", "speed", "playback", "volume", "watch again", "next video"
    ]

    private static let resultsTerms = [
        "results", "summary", "score", "completed", "correct answers", "review answers"
    ]

    private static let skipTerms = [
        "skip", "skip question", "skip video", "next question"
    ]

    public static let reasoningEscalationOrder = ["minimal", "low", "medium", "high", "xhigh"]

    public static func containsVideoSignal(_ layout: OcrLayoutResult) -> Bool {
        containsVideoPhrase(layout.text) || hasVisibleVideoControl(layout.text) || extractVideoProgress(from: layout.text) != nil
    }

    public static func containsResultsSummarySignal(_ text: String) -> Bool {
        let normalized = normalize(text)
        return resultsTerms.contains { normalized.contains($0) }
    }

    public static func containsSkipSignal(_ text: String) -> Bool {
        let normalized = normalize(text)
        return skipTerms.contains { normalized.contains($0) }
    }

    public static func isLikelyQuestionScreen(_ layout: OcrLayoutResult) -> Bool {
        let normalized = normalize(layout.text)
        guard !normalized.isEmpty else { return false }
        if normalized.contains("?") {
            return true
        }
        if normalized.contains("question") {
            return true
        }
        let choiceCount = layout.lines.filter { line in
            line.text.range(of: #"(^|\s)([A-Da-d][\.\)]|[1-4][\.\)])\s"#, options: .regularExpression) != nil
        }.count
        return choiceCount >= 2
    }

    public static func buildLoopSignature(_ text: String) -> String {
        normalize(text)
            .components(separatedBy: .whitespacesAndNewlines)
            .filter { $0.count > 2 }
            .prefix(24)
            .joined(separator: " ")
    }

    public static func nextReasoning(after current: String) -> String? {
        guard let index = reasoningEscalationOrder.firstIndex(of: current.lowercased()), index + 1 < reasoningEscalationOrder.count else {
            return nil
        }
        return reasoningEscalationOrder[index + 1]
    }

    public static func extractVideoProgress(from text: String) -> (current: TimeInterval, total: TimeInterval)? {
        let pattern = #"(\d{1,2}:\d{2}(?::\d{2})?)\s*/\s*(\d{1,2}:\d{2}(?::\d{2})?)"#
        guard
            let regex = try? NSRegularExpression(pattern: pattern),
            let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..<text.endIndex, in: text)),
            let currentRange = Range(match.range(at: 1), in: text),
            let totalRange = Range(match.range(at: 2), in: text),
            let current = parseDuration(String(text[currentRange])),
            let total = parseDuration(String(text[totalRange]))
        else {
            return nil
        }
        return (current, total)
    }

    public static func waitDuration(for layout: OcrLayoutResult, playbackRateAdjusted: Bool) -> TimeInterval {
        guard let progress = extractVideoProgress(from: layout.text) else {
            return 45
        }

        let remaining = max(0, progress.total - progress.current)
        let adjusted = playbackRateAdjusted ? (remaining / 1.5) + 1 : remaining + 1
        return min(max(adjusted, 1), 600)
    }

    private static func containsVideoPhrase(_ text: String) -> Bool {
        let normalized = normalize(text)
        return videoTerms.contains { normalized.contains($0) }
    }

    private static func hasVisibleVideoControl(_ text: String) -> Bool {
        extractVideoProgress(from: text) != nil || containsVideoPhrase(text)
    }

    private static func normalize(_ text: String) -> String {
        text.lowercased().replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
    }

    private static func parseDuration(_ text: String) -> TimeInterval? {
        let segments = text.split(separator: ":").compactMap { Double($0) }
        switch segments.count {
        case 2:
            return (segments[0] * 60) + segments[1]
        case 3:
            return (segments[0] * 3600) + (segments[1] * 60) + segments[2]
        default:
            return nil
        }
    }
}

@MainActor
public final class WidgetAnswerCoordinator {
    private let appState: AppState
    private let settingsStore: SettingsStore
    private let providerRegistry: ProviderRuntimeRegistry
    private let screenCaptureService: ScreenCaptureProviding
    private let ocrService: OCRProviding
    private let permissionService: PermissionProviding
    private let agentClickService: AgentClickService
    private weak var windowing: WidgetWindowing?
    private var activeAnswerTask: Task<Void, Never>?

    public let viewModel: WidgetViewModel

    public init(
        appState: AppState,
        settingsStore: SettingsStore,
        providerRegistry: ProviderRuntimeRegistry,
        screenCaptureService: ScreenCaptureProviding,
        ocrService: OCRProviding,
        permissionService: PermissionProviding,
        agentClickService: AgentClickService,
        windowing: WidgetWindowing? = nil,
        viewModel: WidgetViewModel = WidgetViewModel()
    ) {
        self.appState = appState
        self.settingsStore = settingsStore
        self.providerRegistry = providerRegistry
        self.screenCaptureService = screenCaptureService
        self.ocrService = ocrService
        self.permissionService = permissionService
        self.agentClickService = agentClickService
        self.windowing = windowing
        self.viewModel = viewModel
    }

    public func attachWindowing(_ windowing: WidgetWindowing) {
        self.windowing = windowing
    }

    public var isAnswerRunning: Bool {
        activeAnswerTask != nil
    }

    public func triggerAnswer() async {
        guard activeAnswerTask == nil, appState.canAnswer else {
            return
        }
        guard permissionService.hasScreenRecordingPermission() || permissionService.requestScreenRecordingPermission() else {
            viewModel.isError = true
            viewModel.messageText = "Screen Recording permission is required."
            return
        }
        guard permissionService.hasAccessibilityPermission(prompt: appState.agentModeEnabled) || !appState.agentModeEnabled else {
            viewModel.isError = true
            viewModel.messageText = "Accessibility permission is required for agent clicks."
            return
        }

        let task = Task { [weak self] in
            guard let self else {
                return
            }
            await self.performAnswerFlow()
        }
        activeAnswerTask = task
        await task.value
        activeAnswerTask = nil
    }

    public func cancelCurrentAnswer() {
        activeAnswerTask?.cancel()
        appState.cancelAnswer()
        viewModel.statusPhase = .none
        viewModel.statusText = ""
        viewModel.messageText = "Stopped."
        viewModel.isError = false
    }

    private func performAnswerFlow() async {
        guard !Task.isCancelled, appState.canAnswer else {
            return
        }

        appState.beginAnswer()
        viewModel.statusPhase = .thinking
        viewModel.statusText = "Thinking..."
        viewModel.messageText = ""
        viewModel.isError = false

        let result = appState.agentModeEnabled && appState.agentLoopEnabled
            ? await runAgentLoop()
            : await runSingleAnswer()

        guard !Task.isCancelled else {
            finishCancelledAnswer()
            return
        }

        guard let result else {
            finishCancelledAnswer()
            return
        }

        appState.completeAnswer(result)
        viewModel.statusPhase = .none
        viewModel.statusText = ""
        viewModel.messageText = result.isSuccess ? result.text : result.errorMessage
        viewModel.isError = !result.isSuccess

        try? await appState.persist(settingsStore: settingsStore)
        windowing?.showWidget()
        windowing?.bringToFront()
    }

    private func runSingleAnswer() async -> AnswerResult? {
        guard !Task.isCancelled else {
            return nil
        }
        let iteration = await executeAnswerIteration(reasoningOverride: nil)
        return Task.isCancelled ? nil : iteration.result
    }

    private func runAgentLoop() async -> AnswerResult? {
        guard !Task.isCancelled else {
            return nil
        }
        var answered = 0
        var currentReasoning = appState.selectedReasoningEffort
        var previousSignature = ""

        while answered < 50 {
            guard !Task.isCancelled else {
                return nil
            }
            let iteration = await executeAnswerIteration(reasoningOverride: currentReasoning)
            guard !Task.isCancelled else {
                return nil
            }
            guard iteration.result.isSuccess else {
                return iteration.result
            }
            guard iteration.clickResult.clicked else {
                return AnswerResult(status: .success, text: answered == 0 ? iteration.result.text : "Finished \(answered) question\(answered == 1 ? "" : "s").")
            }

            do {
                try await Task.sleep(nanoseconds: 600_000_000)
            } catch {
                return nil
            }
            guard !Task.isCancelled else {
                return nil
            }
            let nextCapture = try? await screenCaptureService.captureDisplayUnderCursor()
            guard let nextCapture else {
                return AnswerResult(status: .success, text: "Finished \(answered) question\(answered == 1 ? "" : "s").")
            }
            guard !Task.isCancelled else {
                return nil
            }

            defer { try? FileManager.default.removeItem(atPath: nextCapture.imagePath) }
            let nextLayout = (try? await ocrService.extractLayout(imagePath: nextCapture.imagePath)) ?? OcrLayoutResult()
            guard !Task.isCancelled else {
                return nil
            }
            let nextSignature = WidgetHeuristics.buildLoopSignature(nextLayout.text)
            if nextSignature.isEmpty || nextSignature == previousSignature {
                return AnswerResult(status: .success, text: "Finished \(answered + 1) question\((answered + 1) == 1 ? "" : "s").")
            }
            if WidgetHeuristics.containsSkipSignal(nextLayout.text), let stronger = WidgetHeuristics.nextReasoning(after: currentReasoning) {
                currentReasoning = stronger
            }

            previousSignature = nextSignature
            answered += 1
        }

        return AnswerResult(status: .success, text: "Finished \(answered) questions.")
    }

    private func executeAnswerIteration(reasoningOverride: String?) async -> (result: AnswerResult, clickResult: AgentClickResult) {
        if Task.isCancelled {
            return (AnswerResult(status: .failed, errorMessage: "Cancelled."), AgentClickResult())
        }

        viewModel.statusPhase = .screenshotTaken
        viewModel.statusText = "Screenshot taken"

        guard let capture = try? await screenCaptureService.captureDisplayUnderCursor() else {
            return (AnswerResult(status: .failed, errorMessage: "Capture failed."), AgentClickResult())
        }
        guard !Task.isCancelled else {
            return (AnswerResult(status: .failed, errorMessage: "Cancelled."), AgentClickResult())
        }

        defer { try? FileManager.default.removeItem(atPath: capture.imagePath) }

        viewModel.statusPhase = .extractingText
        viewModel.statusText = "Extracting text"

        let ocrLayout = (try? await ocrService.extractLayout(imagePath: capture.imagePath)) ?? OcrLayoutResult()
        guard !Task.isCancelled else {
            return (AnswerResult(status: .failed, errorMessage: "Cancelled."), AgentClickResult())
        }
        let questionLayout = await waitForVideoToFinish(initialLayout: ocrLayout, capturePath: capture.imagePath)
        guard !Task.isCancelled else {
            return (AnswerResult(status: .failed, errorMessage: "Cancelled."), AgentClickResult())
        }
        let reasoning = (reasoningOverride?.isEmpty == false ? reasoningOverride : appState.selectedReasoningEffort) ?? appState.selectedReasoningEffort
        let result = await getBestAnswer(capturePath: capture.imagePath, ocrLayout: questionLayout, reasoningEffort: reasoning)
        guard !Task.isCancelled else {
            return (AnswerResult(status: .failed, errorMessage: "Cancelled."), AgentClickResult())
        }

        var clickResult = AgentClickResult()
        if appState.agentModeEnabled, result.isSuccess {
            windowing?.hideWidget()
            clickResult = await agentClickService.tryClickAnswer(
                answerText: result.text,
                capture: capture,
                ocrLayout: questionLayout,
                model: appState.selectedModel,
                reasoningEffort: reasoning
            )
            windowing?.showWidget()
            windowing?.bringToFront()
        }

        return (result, clickResult)
    }

    private func waitForVideoToFinish(initialLayout: OcrLayoutResult, capturePath: String) async -> OcrLayoutResult {
        var layout = initialLayout
        var playbackAdjusted = false

        while WidgetHeuristics.containsVideoSignal(layout), !WidgetHeuristics.containsResultsSummarySignal(layout.text) {
            let delay = WidgetHeuristics.waitDuration(for: layout, playbackRateAdjusted: playbackAdjusted)
            viewModel.statusPhase = .waitingVideo(delay)
            viewModel.statusText = "Waiting for video to finish..."
            do {
                try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            } catch {
                return layout
            }
            guard !Task.isCancelled else {
                return layout
            }

            guard let capture = try? await screenCaptureService.captureDisplayUnderCursor() else {
                return layout
            }
            guard !Task.isCancelled else {
                return layout
            }

            defer { try? FileManager.default.removeItem(atPath: capture.imagePath) }
            layout = (try? await ocrService.extractLayout(imagePath: capture.imagePath)) ?? layout
            guard !Task.isCancelled else {
                return layout
            }
            playbackAdjusted = true

            if providerRegistry.provider(for: appState.selectedProviderID).providerID == .openAICodex, WidgetHeuristics.containsVideoSignal(layout) {
                let videoCheck = await providerRegistry.provider(for: appState.selectedProviderID).answer(AnswerRequest(
                    model: appState.selectedModel,
                    screenText: layout.text,
                    screenshotPath: capture.imagePath,
                    prompt: "Check whether this screenshot is a video/player state rather than a question screen. Return only VIDEO or NOT_VIDEO.",
                    reasoningEffort: "low"
                ))
                if videoCheck.isSuccess, videoCheck.text.uppercased().contains("NOT_VIDEO") {
                    break
                }
            }
        }

        viewModel.statusPhase = .thinking
        viewModel.statusText = "Thinking..."
        return layout
    }

    private func getBestAnswer(capturePath: String, ocrLayout: OcrLayoutResult, reasoningEffort: String) async -> AnswerResult {
        if Task.isCancelled {
            return AnswerResult(status: .failed, errorMessage: "Cancelled.")
        }

        let provider = providerRegistry.provider(for: appState.selectedProviderID)
        let agentMode = appState.agentModeEnabled
        let ocrPrompt = agentMode
            ? "Answer the user's question from this OCR text. OCR may contain noise. Return only the exact answer text or option label as shown on screen. No explanation."
            : "Answer the user's question from this OCR text. OCR may contain noise. If it is multiple choice, return only the best option unless a short clarification is necessary. Keep it brief."
        let screenshotPrompt = agentMode
            ? "Answer the visible question from this screenshot. Use the image as the source of truth and OCR only as a hint. Return only the exact answer text or option label as shown on screen. No explanation."
            : "Answer the visible question from this screenshot. Use the image as the source of truth, and use any OCR text only as a hint. If it is multiple choice, return only the best option unless a short clarification is necessary. Keep it brief."

        if provider.providerID == .openCode, ocrLayout.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return AnswerResult(status: .failed, errorMessage: "Open Code can only answer when local OCR extracts readable question text.")
        }

        if !ocrLayout.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let ocrResult = await provider.answer(AnswerRequest(
                model: appState.selectedModel,
                screenText: ocrLayout.text,
                prompt: ocrPrompt,
                reasoningEffort: reasoningEffort
            ))
            if Task.isCancelled {
                return AnswerResult(status: .failed, errorMessage: "Cancelled.")
            }
            if ocrResult.isSuccess {
                return ocrResult
            }
            if provider.providerID == .openCode {
                return AnswerResult(status: .failed, errorMessage: "Open Code could not answer from OCR alone. Screenshot fallback is disabled for this provider.")
            }
        }

        return await provider.answer(AnswerRequest(
            model: appState.selectedModel,
            screenText: ocrLayout.text,
            screenshotPath: capturePath,
            prompt: screenshotPrompt,
            reasoningEffort: reasoningEffort
        ))
    }

    private func finishCancelledAnswer() {
        appState.cancelAnswer()
        viewModel.statusPhase = .none
        viewModel.statusText = ""
        viewModel.messageText = "Stopped."
        viewModel.isError = false
    }
}
