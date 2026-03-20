import CoreGraphics
import Foundation

public enum ProviderID: String, Codable, CaseIterable, Sendable {
    case openAICodex = "openai-codex"
    case openCode = "open-code"

    public static func normalize(_ value: String?) -> ProviderID {
        guard let value else {
            return .openAICodex
        }

        return Self.allCases.first(where: { $0.rawValue.caseInsensitiveCompare(value) == .orderedSame }) ?? .openAICodex
    }
}

public struct Rect: Codable, Hashable, Sendable {
    public var x: Double
    public var y: Double
    public var width: Double
    public var height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }

    public init(_ rect: CGRect) {
        self.init(x: rect.origin.x, y: rect.origin.y, width: rect.size.width, height: rect.size.height)
    }

    public var cgRect: CGRect {
        CGRect(x: x, y: y, width: width, height: height)
    }

    public var center: CGPoint {
        CGPoint(x: x + (width / 2), y: y + (height / 2))
    }
}

public struct WidgetBounds: Codable, Hashable, Sendable {
    public var x: Double = 140
    public var y: Double = 140
    public var width: Double = 420
    public var height: Double = 112

    public init(x: Double = 140, y: Double = 140, width: Double = 420, height: Double = 112) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }

    public var rect: CGRect {
        CGRect(x: x, y: y, width: width, height: height)
    }
}

public struct ProviderSelectionSettings: Codable, Hashable, Sendable {
    public var selectedModel: String = ""
    public var selectedReasoningEffort: String = ""
    public var recentModels: [String] = []
    public var lastSuccessfulModel: String = ""
    public var lastSuccessfulReasoningEffort: String = ""

    public init() {}
}

public struct AppSettings: Codable, Hashable, Sendable {
    public var selectedProviderId: String = ProviderID.openAICodex.rawValue
    public var providerSelections: [String: ProviderSelectionSettings] = [:]
    public var selectedModel: String = "gpt-5.4-mini"
    public var selectedReasoningEffort: String = "medium"
    public var saveCurrentModelOnRestart: Bool = true
    public var recentModels: [String] = []
    public var widgetBounds: WidgetBounds = .init()
    public var startWithWidget: Bool = true
    public var agentModeEnabled: Bool = false
    public var agentLoopEnabled: Bool = false
    public var lastSuccessfulModel: String = "gpt-5.4-mini"
    public var lastSuccessfulReasoningEffort: String = "medium"

    public init() {}

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        selectedProviderId = try container.decodeIfPresent(String.self, forKey: .selectedProviderId) ?? ProviderID.openAICodex.rawValue
        providerSelections = try container.decodeIfPresent([String: ProviderSelectionSettings].self, forKey: .providerSelections) ?? [:]
        selectedModel = try container.decodeIfPresent(String.self, forKey: .selectedModel) ?? "gpt-5.4-mini"
        selectedReasoningEffort = try container.decodeIfPresent(String.self, forKey: .selectedReasoningEffort) ?? "medium"
        saveCurrentModelOnRestart = try container.decodeIfPresent(Bool.self, forKey: .saveCurrentModelOnRestart) ?? true
        recentModels = try container.decodeIfPresent([String].self, forKey: .recentModels) ?? []
        widgetBounds = try container.decodeIfPresent(WidgetBounds.self, forKey: .widgetBounds) ?? .init()
        startWithWidget = try container.decodeIfPresent(Bool.self, forKey: .startWithWidget) ?? true
        agentModeEnabled = try container.decodeIfPresent(Bool.self, forKey: .agentModeEnabled) ?? false
        agentLoopEnabled = try container.decodeIfPresent(Bool.self, forKey: .agentLoopEnabled) ?? false
        lastSuccessfulModel = try container.decodeIfPresent(String.self, forKey: .lastSuccessfulModel) ?? "gpt-5.4-mini"
        lastSuccessfulReasoningEffort = try container.decodeIfPresent(String.self, forKey: .lastSuccessfulReasoningEffort) ?? "medium"
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(selectedProviderId, forKey: .selectedProviderId)
        try container.encode(providerSelections, forKey: .providerSelections)
        try container.encode(selectedModel, forKey: .selectedModel)
        try container.encode(selectedReasoningEffort, forKey: .selectedReasoningEffort)
        try container.encode(saveCurrentModelOnRestart, forKey: .saveCurrentModelOnRestart)
        try container.encode(recentModels, forKey: .recentModels)
        try container.encode(widgetBounds, forKey: .widgetBounds)
        try container.encode(startWithWidget, forKey: .startWithWidget)
        try container.encode(agentModeEnabled, forKey: .agentModeEnabled)
        try container.encode(agentLoopEnabled, forKey: .agentLoopEnabled)
        try container.encode(lastSuccessfulModel, forKey: .lastSuccessfulModel)
        try container.encode(lastSuccessfulReasoningEffort, forKey: .lastSuccessfulReasoningEffort)
    }

    private enum CodingKeys: String, CodingKey {
        case selectedProviderId
        case providerSelections
        case selectedModel
        case selectedReasoningEffort
        case saveCurrentModelOnRestart
        case recentModels
        case widgetBounds
        case startWithWidget
        case agentModeEnabled
        case agentLoopEnabled
        case lastSuccessfulModel
        case lastSuccessfulReasoningEffort
    }
}

public struct ReasoningLevelOption: Codable, Hashable, Sendable, Identifiable {
    public var effort: String
    public var description: String

    public init(effort: String, description: String = "") {
        self.effort = effort
        self.description = description
    }

    public var id: String { effort }

    public var displayName: String {
        switch effort.lowercased() {
        case "xhigh":
            "Extra High"
        case "high":
            "High"
        case "medium":
            "Medium"
        case "low":
            "Low"
        case "minimal":
            "Minimal"
        default:
            effort
        }
    }
}

public struct ProviderOption: Codable, Hashable, Sendable, Identifiable {
    public var id: String
    public var displayName: String

    public init(id: String, displayName: String) {
        self.id = id
        self.displayName = displayName
    }
}

public struct ProviderModelOption: Codable, Hashable, Sendable, Identifiable {
    public var slug: String
    public var displayName: String
    public var description: String = ""
    public var visibility: String = ""
    public var priority: Int = 0
    public var defaultReasoningLevel: String = ""
    public var supportedReasoningLevels: [ReasoningLevelOption] = []

    public init(
        slug: String,
        displayName: String,
        description: String = "",
        visibility: String = "",
        priority: Int = 0,
        defaultReasoningLevel: String = "",
        supportedReasoningLevels: [ReasoningLevelOption] = []
    ) {
        self.slug = slug
        self.displayName = displayName
        self.description = description
        self.visibility = visibility
        self.priority = priority
        self.defaultReasoningLevel = defaultReasoningLevel
        self.supportedReasoningLevels = supportedReasoningLevels
    }

    public var id: String { slug }
    public var supportsReasoningSelection: Bool { supportedReasoningLevels.count > 1 }
}

public struct ProviderDefaults: Codable, Hashable, Sendable {
    public var selectedModel: String = ""
    public var selectedReasoningEffort: String = ""

    public init(selectedModel: String = "", selectedReasoningEffort: String = "") {
        self.selectedModel = selectedModel
        self.selectedReasoningEffort = selectedReasoningEffort
    }
}

public struct ProviderPreflightResult: Codable, Hashable, Sendable {
    public var isInstalled: Bool = false
    public var version: String = ""
    public var isLoggedIn: Bool = false
    public var blockingMessage: String = ""

    public init(
        isInstalled: Bool = false,
        version: String = "",
        isLoggedIn: Bool = false,
        blockingMessage: String = ""
    ) {
        self.isInstalled = isInstalled
        self.version = version
        self.isLoggedIn = isLoggedIn
        self.blockingMessage = blockingMessage
    }

    public var isReady: Bool {
        isInstalled && isLoggedIn
    }
}

public struct AnswerRequest: Sendable {
    public var model: String
    public var screenText: String = ""
    public var screenshotPath: String = ""
    public var prompt: String
    public var reasoningEffort: String = ""
    public var requestedAt: Date = .now

    public init(
        model: String,
        screenText: String = "",
        screenshotPath: String = "",
        prompt: String,
        reasoningEffort: String = "",
        requestedAt: Date = .now
    ) {
        self.model = model
        self.screenText = screenText
        self.screenshotPath = screenshotPath
        self.prompt = prompt
        self.reasoningEffort = reasoningEffort
        self.requestedAt = requestedAt
    }
}

public enum AnswerStatus: String, Codable, Sendable {
    case success
    case timeout
    case failed
    case empty
}

public struct AnswerResult: Codable, Hashable, Sendable {
    public var status: AnswerStatus
    public var text: String = ""
    public var errorMessage: String = ""
    public var duration: TimeInterval = 0

    public init(status: AnswerStatus, text: String = "", errorMessage: String = "", duration: TimeInterval = 0) {
        self.status = status
        self.text = text
        self.errorMessage = errorMessage
        self.duration = duration
    }

    public var isSuccess: Bool {
        status == .success && !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

public struct TerminalCommandResult: Codable, Hashable, Sendable {
    public var exitCode: Int32
    public var standardOutput: String = ""
    public var standardError: String = ""
    public var timedOut: Bool = false

    public init(exitCode: Int32, standardOutput: String = "", standardError: String = "", timedOut: Bool = false) {
        self.exitCode = exitCode
        self.standardOutput = standardOutput
        self.standardError = standardError
        self.timedOut = timedOut
    }

    public var isSuccess: Bool {
        !timedOut && exitCode == 0
    }
}

public struct OcrTextRegion: Codable, Hashable, Sendable, Identifiable {
    public var text: String
    public var bounds: Rect

    public init(text: String, bounds: Rect) {
        self.text = text
        self.bounds = bounds
    }

    public var id: String {
        "\(text)|\(bounds.x)|\(bounds.y)|\(bounds.width)|\(bounds.height)"
    }
}

public struct OcrLayoutResult: Codable, Hashable, Sendable {
    public var text: String = ""
    public var lines: [OcrTextRegion] = []
    public var words: [OcrTextRegion] = []

    public init(text: String = "", lines: [OcrTextRegion] = [], words: [OcrTextRegion] = []) {
        self.text = text
        self.lines = lines
        self.words = words
    }
}

public struct ScreenCaptureResult: Codable, Hashable, Sendable {
    public var imagePath: String
    public var bounds: Rect
    public var imagePixelWidth: Double
    public var imagePixelHeight: Double

    public init(
        imagePath: String,
        bounds: Rect,
        imagePixelWidth: Double = 0,
        imagePixelHeight: Double = 0
    ) {
        self.imagePath = imagePath
        self.bounds = bounds
        self.imagePixelWidth = imagePixelWidth
        self.imagePixelHeight = imagePixelHeight
    }
}

public struct AgentClickResult: Codable, Hashable, Sendable {
    public var clicked: Bool = false
    public var matchedText: String = ""
    public var failureReason: String = ""

    public init(clicked: Bool = false, matchedText: String = "", failureReason: String = "") {
        self.clicked = clicked
        self.matchedText = matchedText
        self.failureReason = failureReason
    }
}

public struct OpenCodeSetupResult: Codable, Hashable, Sendable {
    public var isSuccess: Bool
    public var summary: String
    public var detail: String

    public init(isSuccess: Bool, summary: String, detail: String) {
        self.isSuccess = isSuccess
        self.summary = summary
        self.detail = detail
    }
}
