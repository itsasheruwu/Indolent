import AppKit
import ApplicationServices
import CoreGraphics
import Foundation
import ScreenCaptureKit
import Vision

public protocol PermissionProviding: Sendable {
    func hasScreenRecordingPermission() -> Bool
    func requestScreenRecordingPermission() -> Bool
    func hasAccessibilityPermission(prompt: Bool) -> Bool
}

public final class MacPermissionService: PermissionProviding {
    public init() {}

    public func hasScreenRecordingPermission() -> Bool {
        CGPreflightScreenCaptureAccess()
    }

    public func requestScreenRecordingPermission() -> Bool {
        CGRequestScreenCaptureAccess()
    }

    public func hasAccessibilityPermission(prompt: Bool) -> Bool {
        let options = ["AXTrustedCheckOptionPrompt": prompt] as CFDictionary
        return AXIsProcessTrustedWithOptions(options)
    }
}

public protocol ScreenCaptureProviding: Sendable {
    func captureDisplayUnderCursor() async throws -> ScreenCaptureResult
}

public final class ScreenCaptureService: ScreenCaptureProviding {
    public init() {}

    public func captureDisplayUnderCursor() async throws -> ScreenCaptureResult {
        let mouseLocation = NSEvent.mouseLocation
        guard let screen = NSScreen.screens.first(where: { NSMouseInRect(mouseLocation, $0.frame, false) }) ?? NSScreen.main else {
            throw NSError(domain: "Indolent.ScreenCapture", code: 1, userInfo: [NSLocalizedDescriptionKey: "Unable to locate the display under the cursor."])
        }

        let displayID = (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0
        let displayBounds = CGDisplayBounds(displayID)
        let image: CGImage
        if #available(macOS 15.2, *) {
            image = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<CGImage, Error>) in
                SCScreenshotManager.captureImage(in: screen.frame) { image, error in
                    if let error {
                        continuation.resume(throwing: error)
                    } else if let image {
                        continuation.resume(returning: image)
                    } else {
                        continuation.resume(throwing: NSError(domain: "Indolent.ScreenCapture", code: 2, userInfo: [NSLocalizedDescriptionKey: "ScreenCaptureKit did not return a CGImage."]))
                    }
                }
            }
        } else if let fallback = CGDisplayCreateImage(displayID) {
            image = fallback
        } else {
            throw NSError(domain: "Indolent.ScreenCapture", code: 3, userInfo: [NSLocalizedDescriptionKey: "Unable to capture the selected display."])
        }

        let destinationURL = FileManager.default.temporaryDirectory.appendingPathComponent("indolent-capture-\(UUID().uuidString).png")
        guard let representation = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
            throw NSError(domain: "Indolent.ScreenCapture", code: 4, userInfo: [NSLocalizedDescriptionKey: "Unable to encode the screenshot as PNG."])
        }

        try representation.write(to: destinationURL)
        return ScreenCaptureResult(
            imagePath: destinationURL.path,
            bounds: Rect(displayBounds),
            imagePixelWidth: Double(image.width),
            imagePixelHeight: Double(image.height)
        )
    }
}

public protocol OCRProviding: Sendable {
    func extractLayout(imagePath: String) async throws -> OcrLayoutResult
    func extractText(imagePath: String) async throws -> String
}

public final class VisionOCRService: OCRProviding {
    public init() {}

    public func extractLayout(imagePath: String) async throws -> OcrLayoutResult {
        let imageURL = URL(fileURLWithPath: imagePath)
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = false

        let handler = VNImageRequestHandler(url: imageURL)
        try handler.perform([request])

        guard
            let cgImageSource = NSImage(contentsOf: imageURL),
            let cgImage = cgImageSource.cgImage(forProposedRect: nil, context: nil, hints: nil)
        else {
            throw NSError(domain: "Indolent.OCR", code: 1, userInfo: [NSLocalizedDescriptionKey: "Unable to load screenshot for OCR."])
        }

        let observations = request.results ?? []
        let width = CGFloat(cgImage.width)
        let height = CGFloat(cgImage.height)

        var lines: [OcrTextRegion] = []
        var words: [OcrTextRegion] = []

        for observation in observations {
            guard let candidate = observation.topCandidates(1).first else {
                continue
            }

            let lineText = candidate.string.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !lineText.isEmpty else {
                continue
            }

            lines.append(OcrTextRegion(text: lineText, bounds: Rect(Self.pixelRect(for: observation.boundingBox, width: width, height: height))))

            let nsString = lineText as NSString
            nsString.enumerateSubstrings(in: NSRange(location: 0, length: nsString.length), options: .byWords) { _, substringRange, _, _ in
                guard
                    substringRange.location != NSNotFound,
                    let wordRange = Range(substringRange, in: lineText),
                    let box = try? candidate.boundingBox(for: wordRange)
                else {
                    return
                }

                let word = String(lineText[wordRange]).trimmingCharacters(in: .whitespacesAndNewlines)
                guard !word.isEmpty else {
                    return
                }

                words.append(OcrTextRegion(text: word, bounds: Rect(Self.pixelRect(for: box.boundingBox, width: width, height: height))))
            }
        }

        lines.sort { lhs, rhs in
            if lhs.bounds.y != rhs.bounds.y {
                return lhs.bounds.y < rhs.bounds.y
            }
            return lhs.bounds.x < rhs.bounds.x
        }
        words.sort { lhs, rhs in
            if lhs.bounds.y != rhs.bounds.y {
                return lhs.bounds.y < rhs.bounds.y
            }
            return lhs.bounds.x < rhs.bounds.x
        }

        return OcrLayoutResult(
            text: lines.map(\.text).joined(separator: "\n"),
            lines: lines,
            words: words
        )
    }

    public func extractText(imagePath: String) async throws -> String {
        try await extractLayout(imagePath: imagePath).text
    }

    private static func pixelRect(for normalizedRect: CGRect, width: CGFloat, height: CGFloat) -> CGRect {
        let x = normalizedRect.origin.x * width
        let y = (1 - normalizedRect.origin.y - normalizedRect.height) * height
        return CGRect(x: x, y: y, width: normalizedRect.width * width, height: normalizedRect.height * height)
    }
}

public protocol MouseClickPerforming: Sendable {
    func click(at point: CGPoint) throws
}

public final class QuartzMouseClickService: MouseClickPerforming {
    public init() {}

    public func click(at point: CGPoint) throws {
        guard
            let move = CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: point, mouseButton: .left),
            let down = CGEvent(mouseEventSource: nil, mouseType: .leftMouseDown, mouseCursorPosition: point, mouseButton: .left),
            let up = CGEvent(mouseEventSource: nil, mouseType: .leftMouseUp, mouseCursorPosition: point, mouseButton: .left)
        else {
            throw NSError(domain: "Indolent.Click", code: 1, userInfo: [NSLocalizedDescriptionKey: "Unable to synthesize a click event."])
        }

        move.post(tap: .cghidEventTap)
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
    }
}

public final class AgentClickService {
    private let appState: AppState
    private let providerRegistry: ProviderRuntimeRegistry
    private let clickPerformer: MouseClickPerforming

    public init(appState: AppState, providerRegistry: ProviderRuntimeRegistry, clickPerformer: MouseClickPerforming) {
        self.appState = appState
        self.providerRegistry = providerRegistry
        self.clickPerformer = clickPerformer
    }

    @MainActor
    public func tryClickAnswer(
        answerText: String,
        capture: ScreenCaptureResult,
        ocrLayout: OcrLayoutResult,
        model: String,
        reasoningEffort: String
    ) async -> AgentClickResult {
        let localCandidates = Self.rankCandidates(answerText: answerText, ocrLayout: ocrLayout)
        if let winner = Self.selectConfidentWinner(from: localCandidates) {
            do {
                try click(region: winner.region.bounds, in: capture)
                return AgentClickResult(clicked: true, matchedText: winner.region.text)
            } catch {
                return AgentClickResult(clicked: false, failureReason: error.localizedDescription)
            }
        }

        let fallbackCandidates = Self.buildFallbackCandidates(answerText: answerText, ocrLayout: ocrLayout)
        guard !fallbackCandidates.isEmpty else {
            return AgentClickResult(clicked: false, failureReason: "No confident click target found.")
        }

        let provider = providerRegistry.provider(for: appState.selectedProviderID)
        guard provider.providerID != .openCode else {
            return AgentClickResult(clicked: false, failureReason: "No confident click target found.")
        }

        let choice = await Self.resolveFallbackCandidate(
            answerText: answerText,
            screenshotPath: capture.imagePath,
            candidates: fallbackCandidates,
            provider: provider,
            model: model,
            reasoningEffort: reasoningEffort
        )

        guard let choice else {
            return AgentClickResult(clicked: false, failureReason: "No confident click target found.")
        }

        do {
            try click(region: choice.region.bounds, in: capture)
            return AgentClickResult(clicked: true, matchedText: choice.region.text)
        } catch {
            return AgentClickResult(clicked: false, failureReason: error.localizedDescription)
        }
    }

    public static func rankCandidates(answerText: String, ocrLayout: OcrLayoutResult) -> [MatchCandidate] {
        let normalizedAnswer = normalize(answerText)
        guard !normalizedAnswer.isEmpty else {
            return []
        }

        let label = extractLeadingLabel(answerText)
        let requestedSpeed = extractSpeedValue(answerText)
        var results: [MatchCandidate] = []

        for region in ocrLayout.lines where !region.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let normalizedLine = normalize(region.text)
            guard !normalizedLine.isEmpty else { continue }

            let lineSpeed = extractSpeedValue(region.text)
            let score: Double
            if let requestedSpeed, let lineSpeed {
                score = abs(requestedSpeed - lineSpeed) < 0.001 ? 1.0 : 0
            } else if let requestedSpeed {
                score = containsExactSpeedToken(region.text, requestedSpeed: requestedSpeed) ? 0.98 : 0
            } else if normalizedLine == normalizedAnswer {
                score = 1.0
            } else if normalizedLine.contains(normalizedAnswer) {
                score = 0.95
            } else if normalizedAnswer.contains(normalizedLine), normalizedLine.count >= 4 {
                score = 0.85
            } else if !label.isEmpty, startsWithLabel(region.text, label: label) {
                score = 0.9
            } else {
                score = tokenOverlapScore(normalizedAnswer: normalizedAnswer, normalizedLine: normalizedLine)
            }

            if score >= 0.55 {
                results.append(MatchCandidate(region: region, score: score))
            }
        }

        for region in ocrLayout.words where !region.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let normalizedWord = normalize(region.text)
            guard !normalizedWord.isEmpty else { continue }

            let wordSpeed = extractSpeedValue(region.text)
            let score: Double
            if let requestedSpeed, let wordSpeed {
                score = abs(requestedSpeed - wordSpeed) < 0.001 ? 0.92 : 0
            } else if let requestedSpeed {
                score = containsExactSpeedToken(region.text, requestedSpeed: requestedSpeed) ? 0.9 : 0
            } else if normalizedWord == normalizedAnswer {
                score = 0.88
            } else if !label.isEmpty, normalizedWord == label {
                score = 0.82
            } else {
                score = 0
            }

            if score > 0 {
                results.append(MatchCandidate(region: region, score: score))
            }
        }

        return results
    }

    public static func selectConfidentWinner(from candidates: [MatchCandidate]) -> MatchCandidate? {
        let ordered = candidates.sorted {
            if $0.score != $1.score {
                return $0.score > $1.score
            }
            if $0.region.bounds.y != $1.region.bounds.y {
                return $0.region.bounds.y < $1.region.bounds.y
            }
            return $0.region.bounds.x < $1.region.bounds.x
        }

        guard let winner = ordered.first else {
            return nil
        }

        if winner.score >= 0.92 {
            return winner
        }
        if ordered.count == 1, winner.score >= 0.8 {
            return winner
        }
        if ordered.count > 1, winner.score >= 0.82, winner.score - ordered[1].score >= 0.12 {
            return winner
        }
        return nil
    }

    public static func buildFallbackCandidates(answerText: String, ocrLayout: OcrLayoutResult) -> [MatchCandidate] {
        var ranked = rankCandidates(answerText: answerText, ocrLayout: ocrLayout)
            .filter { $0.score >= 0.35 }
            .sorted {
                if $0.score != $1.score {
                    return $0.score > $1.score
                }
                if $0.region.bounds.y != $1.region.bounds.y {
                    return $0.region.bounds.y < $1.region.bounds.y
                }
                return $0.region.bounds.x < $1.region.bounds.x
            }

        if ranked.isEmpty {
            ranked = ocrLayout.lines.prefix(6).map { MatchCandidate(region: $0, score: 0.2) }
        }

        var deduped: [String: MatchCandidate] = [:]
        for candidate in ranked {
            let key = normalize(candidate.region.text)
            if deduped[key] == nil {
                deduped[key] = candidate
            }
        }

        return Array(deduped.values).sorted {
            if $0.score != $1.score {
                return $0.score > $1.score
            }
            if $0.region.bounds.y != $1.region.bounds.y {
                return $0.region.bounds.y < $1.region.bounds.y
            }
            return $0.region.bounds.x < $1.region.bounds.x
        }.prefix(6).map { $0 }
    }

    private static func resolveFallbackCandidate(
        answerText: String,
        screenshotPath: String,
        candidates: [MatchCandidate],
        provider: ProviderRuntime,
        model: String,
        reasoningEffort: String
    ) async -> MatchCandidate? {
        let screenText = buildFallbackScreenText(answerText: answerText, candidates: candidates)
        let result = await provider.answer(AnswerRequest(
            model: model,
            screenText: screenText,
            screenshotPath: screenshotPath,
            prompt: "Pick the candidate that should be clicked for the correct answer. Return only the candidate id.",
            reasoningEffort: reasoningEffort
        ))

        guard result.isSuccess else {
            return nil
        }

        let digits = result.text.components(separatedBy: CharacterSet.decimalDigits.inverted).joined()
        guard let id = Int(digits), id > 0, id <= candidates.count else {
            return nil
        }
        return candidates[id - 1]
    }

    private func click(region: Rect, in capture: ScreenCaptureResult) throws {
        let xScale = capture.imagePixelWidth > 0 ? capture.bounds.width / capture.imagePixelWidth : 1
        let yScale = capture.imagePixelHeight > 0 ? capture.bounds.height / capture.imagePixelHeight : 1
        let target = CGPoint(
            x: capture.bounds.x + ((region.x + (region.width / 2)) * xScale),
            y: capture.bounds.y + ((region.y + (region.height / 2)) * yScale)
        )
        try clickPerformer.click(at: target)
    }

    private static func buildFallbackScreenText(answerText: String, candidates: [MatchCandidate]) -> String {
        var lines = ["Answer target: \(answerText.trimmingCharacters(in: .whitespacesAndNewlines))", "", "Candidates:"]
        for (index, candidate) in candidates.enumerated() {
            lines.append("\(index + 1): \(candidate.region.text.trimmingCharacters(in: .whitespacesAndNewlines))")
        }
        return lines.joined(separator: "\n")
    }

    private static func normalize(_ value: String) -> String {
        let sanitized = value.replacingOccurrences(of: #"[^A-Za-z0-9]+"#, with: " ", options: .regularExpression)
        return sanitized.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
    }

    private static func extractLeadingLabel(_ value: String) -> String {
        guard let regex = try? NSRegularExpression(pattern: #"^\s*([A-Za-z0-9])(?:[\.\)\:\-]\s*|\s+)"#) else {
            return ""
        }
        let nsRange = NSRange(value.startIndex..<value.endIndex, in: value)
        guard
            let match = regex.firstMatch(in: value, range: nsRange),
            let range = Range(match.range(at: 1), in: value)
        else {
            return ""
        }
        return String(value[range]).lowercased()
    }

    private static func extractSpeedValue(_ value: String) -> Double? {
        guard let regex = try? NSRegularExpression(pattern: #"(\d+(?:\.\d+)?)\s*x"#, options: [.caseInsensitive]) else {
            return nil
        }
        let nsRange = NSRange(value.startIndex..<value.endIndex, in: value)
        guard
            let match = regex.firstMatch(in: value, range: nsRange),
            let range = Range(match.range(at: 1), in: value)
        else {
            return nil
        }
        return Double(value[range])
    }

    private static func containsExactSpeedToken(_ value: String, requestedSpeed: Double) -> Bool {
        let token = requestedSpeed == floor(requestedSpeed) ? String(Int(requestedSpeed)) : String(requestedSpeed)
        return value.range(of: #"(?i)\b\#(token)\s*x?\b"#, options: .regularExpression) != nil
    }

    private static func startsWithLabel(_ value: String, label: String) -> Bool {
        extractLeadingLabel(value) == label.lowercased()
    }

    private static func tokenOverlapScore(normalizedAnswer: String, normalizedLine: String) -> Double {
        let answerTokens = Array(Set(normalizedAnswer.split(separator: " ").map(String.init).filter { $0.count > 1 }))
        guard !answerTokens.isEmpty else {
            return 0
        }

        let matches = answerTokens.filter { normalizedLine.contains($0) }.count
        return Double(matches) / Double(answerTokens.count) * 0.74
    }

    public struct MatchCandidate: Hashable, Sendable {
        public var region: OcrTextRegion
        public var score: Double
    }
}
