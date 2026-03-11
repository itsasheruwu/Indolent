using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using Indolent.Helpers;

using Microsoft.Extensions.Logging;

namespace Indolent.Services;

public sealed partial class AgentClickService(
    ICodexCliService codexCliService,
    ILogger<AgentClickService> logger) : IAgentClickService
{
    private const string FallbackPrompt = "Pick the candidate that should be clicked for the correct answer. Return only the candidate id.";

    public async Task<AgentClickResult> TryClickAnswerAsync(
        string answerText,
        ScreenCaptureResult capture,
        OcrLayoutResult ocrLayout,
        string model,
        string reasoningEffort,
        CancellationToken cancellationToken = default)
    {
        var localCandidates = RankCandidates(answerText, ocrLayout).ToArray();
        var localWinner = TrySelectConfidentWinner(localCandidates);
        if (localWinner is not null)
        {
            ClickRegion(capture.Bounds, localWinner.Region.Bounds);
            logger.LogInformation("Agent click matched locally on '{MatchedText}'.", localWinner.Region.Text);
            return new AgentClickResult
            {
                Clicked = true,
                MatchedText = localWinner.Region.Text
            };
        }

        var fallbackCandidates = BuildFallbackCandidates(answerText, ocrLayout).ToArray();
        if (fallbackCandidates.Length == 0)
        {
            logger.LogInformation("Agent click skipped. No confident local candidates were available.");
            return new AgentClickResult
            {
                Clicked = false,
                FailureReason = "No confident click target found."
            };
        }

        var fallbackChoice = await ResolveFallbackCandidateAsync(
            answerText,
            capture.ImagePath,
            fallbackCandidates,
            model,
            reasoningEffort,
            cancellationToken);

        if (fallbackChoice is null)
        {
            logger.LogInformation("Agent click skipped. Fallback did not return a valid candidate.");
            return new AgentClickResult
            {
                Clicked = false,
                FailureReason = "No confident click target found."
            };
        }

        ClickRegion(capture.Bounds, fallbackChoice.Region.Bounds);
        logger.LogInformation("Agent click matched by fallback on '{MatchedText}'.", fallbackChoice.Region.Text);
        return new AgentClickResult
        {
            Clicked = true,
            MatchedText = fallbackChoice.Region.Text
        };
    }

    private static IEnumerable<MatchCandidate> RankCandidates(string answerText, OcrLayoutResult ocrLayout)
    {
        var normalizedAnswer = Normalize(answerText);
        if (string.IsNullOrWhiteSpace(normalizedAnswer))
        {
            yield break;
        }

        var label = ExtractLeadingLabel(answerText);
        var requestedSpeed = ExtractSpeedValue(answerText);
        foreach (var line in ocrLayout.Lines.Where(region => !string.IsNullOrWhiteSpace(region.Text)))
        {
            var normalizedLine = Normalize(line.Text);
            if (string.IsNullOrWhiteSpace(normalizedLine))
            {
                continue;
            }

            var score = 0d;
            var lineSpeed = ExtractSpeedValue(line.Text);
            if (requestedSpeed.HasValue && lineSpeed.HasValue)
            {
                score = Math.Abs(requestedSpeed.Value - lineSpeed.Value) < 0.001
                    ? 1.0
                    : 0;
            }
            else if (requestedSpeed.HasValue)
            {
                score = ContainsExactSpeedToken(line.Text, requestedSpeed.Value) ? 0.98 : 0;
            }
            else if (string.Equals(normalizedLine, normalizedAnswer, StringComparison.Ordinal))
            {
                score = 1.0;
            }
            else if (normalizedLine.Contains(normalizedAnswer, StringComparison.Ordinal))
            {
                score = 0.95;
            }
            else if (normalizedAnswer.Contains(normalizedLine, StringComparison.Ordinal) && normalizedLine.Length >= 4)
            {
                score = 0.85;
            }
            else if (!string.IsNullOrWhiteSpace(label) && StartsWithLabel(line.Text, label))
            {
                score = 0.9;
            }
            else
            {
                score = TokenOverlapScore(normalizedAnswer, normalizedLine);
            }

            if (score >= 0.55)
            {
                yield return new MatchCandidate(line, score);
            }
        }

        foreach (var word in ocrLayout.Words.Where(region => !string.IsNullOrWhiteSpace(region.Text)))
        {
            var normalizedWord = Normalize(word.Text);
            if (string.IsNullOrWhiteSpace(normalizedWord))
            {
                continue;
            }

            var wordSpeed = ExtractSpeedValue(word.Text);
            var score = requestedSpeed.HasValue && wordSpeed.HasValue
                ? Math.Abs(requestedSpeed.Value - wordSpeed.Value) < 0.001 ? 0.92 : 0
                : requestedSpeed.HasValue
                    ? ContainsExactSpeedToken(word.Text, requestedSpeed.Value) ? 0.9 : 0
                : string.Equals(normalizedWord, normalizedAnswer, StringComparison.Ordinal)
                    ? 0.88
                    : !string.IsNullOrWhiteSpace(label) && string.Equals(normalizedWord, label, StringComparison.Ordinal)
                        ? 0.82
                        : 0;

            if (score > 0)
            {
                yield return new MatchCandidate(word, score);
            }
        }
    }

    private static MatchCandidate? TrySelectConfidentWinner(IReadOnlyList<MatchCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Region.Bounds.Top)
            .ThenBy(candidate => candidate.Region.Bounds.Left)
            .ToArray();

        var winner = ordered[0];
        if (winner.Score >= 0.92)
        {
            return winner;
        }

        if (ordered.Length == 1 && winner.Score >= 0.8)
        {
            return winner;
        }

        if (ordered.Length > 1 && winner.Score - ordered[1].Score >= 0.12 && winner.Score >= 0.82)
        {
            return winner;
        }

        return null;
    }

    private static IEnumerable<MatchCandidate> BuildFallbackCandidates(string answerText, OcrLayoutResult ocrLayout)
    {
        var ranked = RankCandidates(answerText, ocrLayout)
            .Where(candidate => candidate.Score >= 0.35)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Region.Bounds.Top)
            .ThenBy(candidate => candidate.Region.Bounds.Left)
            .ToList();

        if (ranked.Count == 0)
        {
            ranked = ocrLayout.Lines
                .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                .Select(line => new MatchCandidate(line, 0.2))
                .OrderBy(candidate => candidate.Region.Bounds.Top)
                .ThenBy(candidate => candidate.Region.Bounds.Left)
                .Take(6)
                .ToList();
        }

        return ranked
            .GroupBy(candidate => Normalize(candidate.Region.Text))
            .Select(group => group.First())
            .Take(6);
    }

    private async Task<MatchCandidate?> ResolveFallbackCandidateAsync(
        string answerText,
        string screenshotPath,
        IReadOnlyList<MatchCandidate> candidates,
        string model,
        string reasoningEffort,
        CancellationToken cancellationToken)
    {
        var screenText = BuildFallbackScreenText(answerText, candidates);
        var result = await codexCliService.AnswerAsync(new AnswerRequest
        {
            Model = model,
            ScreenText = screenText,
            ScreenshotPath = screenshotPath,
            Prompt = FallbackPrompt,
            ReasoningEffort = reasoningEffort,
            RequestedAt = DateTimeOffset.Now
        }, cancellationToken);

        if (!result.IsSuccess)
        {
            return null;
        }

        var match = CandidateIdRegex().Match(result.Text);
        if (!match.Success || !int.TryParse(match.Groups["id"].Value, out var index))
        {
            return null;
        }

        index -= 1;
        return index >= 0 && index < candidates.Count
            ? candidates[index]
            : null;
    }

    private static string BuildFallbackScreenText(string answerText, IReadOnlyList<MatchCandidate> candidates)
    {
        var builder = new StringBuilder()
            .AppendLine($"Answer target: {answerText.Trim()}")
            .AppendLine()
            .AppendLine("Candidates:");

        for (var index = 0; index < candidates.Count; index++)
        {
            builder.Append(index + 1)
                .Append(": ")
                .AppendLine(candidates[index].Region.Text.Trim());
        }

        return builder.ToString();
    }

    private static string Normalize(string value)
    {
        var sanitized = NonAlphaNumericRegex().Replace(value, " ");
        return SpaceRegex().Replace(sanitized, " ").Trim().ToLowerInvariant();
    }

    private static string ExtractLeadingLabel(string value)
        => LeadingLabelRegex().Match(value) is { Success: true } match
            ? match.Groups["label"].Value.ToLowerInvariant()
            : string.Empty;

    private static double? ExtractSpeedValue(string value)
        => SpeedRegex().Match(value) is { Success: true } match
            && double.TryParse(match.Groups["speed"].Value, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var speed)
                ? speed
                : null;

    private static bool ContainsExactSpeedToken(string value, double requestedSpeed)
    {
        var requested = requestedSpeed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return ExactSpeedTokenRegex().Matches(value)
            .Any(match => string.Equals(match.Groups["speed"].Value, requested, StringComparison.Ordinal));
    }

    private static bool StartsWithLabel(string lineText, string label)
        => LeadingLabelRegex().Match(lineText) is { Success: true } match
            && string.Equals(match.Groups["label"].Value, label, StringComparison.OrdinalIgnoreCase);

    private static double TokenOverlapScore(string normalizedAnswer, string normalizedLine)
    {
        var answerTokens = normalizedAnswer
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 1)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (answerTokens.Length == 0)
        {
            return 0;
        }

        var matches = answerTokens.Count(token => normalizedLine.Contains(token, StringComparison.Ordinal));
        return (double)matches / answerTokens.Length * 0.74;
    }

    private static void ClickRegion(Rectangle captureBounds, Rectangle regionBounds)
    {
        var targetX = captureBounds.Left + regionBounds.Left + (regionBounds.Width / 2);
        var targetY = captureBounds.Top + regionBounds.Top + (regionBounds.Height / 2);

        var inputs = new[]
        {
            CreateMouseInput(targetX, targetY, NativeMethods.MouseeventfMove | NativeMethods.MouseeventfAbsolute | NativeMethods.MouseeventfVirtualdesk),
            CreateMouseInput(targetX, targetY, NativeMethods.MouseeventfLeftdown | NativeMethods.MouseeventfAbsolute | NativeMethods.MouseeventfVirtualdesk),
            CreateMouseInput(targetX, targetY, NativeMethods.MouseeventfLeftup | NativeMethods.MouseeventfAbsolute | NativeMethods.MouseeventfVirtualdesk)
        };

        var inputSize = Marshal.SizeOf<NativeMethods.Input>();
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, inputSize);
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException("Windows rejected the injected click input.");
        }
    }

    private static NativeMethods.Input CreateMouseInput(int x, int y, uint flags)
        => new()
        {
            Type = NativeMethods.InputMouse,
            Data = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput
                {
                    X = NormalizeCoordinate(x, NativeMethods.SmXvirtualscreen, NativeMethods.SmCxvirtualscreen),
                    Y = NormalizeCoordinate(y, NativeMethods.SmYvirtualscreen, NativeMethods.SmCyvirtualscreen),
                    MouseData = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

    private static int NormalizeCoordinate(int value, int originMetric, int sizeMetric)
    {
        var origin = NativeMethods.GetSystemMetrics(originMetric);
        var size = Math.Max(1, NativeMethods.GetSystemMetrics(sizeMetric) - 1);
        return (int)Math.Round((value - origin) * 65535d / size);
    }

    private sealed record MatchCandidate(OcrTextRegion Region, double Score);

    [GeneratedRegex(@"^\s*(?<label>[A-Za-z0-9])(?:[\.\)\:\-]\s*|\s+)")]
    private static partial Regex LeadingLabelRegex();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"(?<id>\d+)")]
    private static partial Regex CandidateIdRegex();

    [GeneratedRegex(@"(?<speed>\d+(?:\.\d+)?)\s*x", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedRegex();

    [GeneratedRegex(@"(?<speed>\d+(?:\.\d+)?)\s*x?", RegexOptions.IgnoreCase)]
    private static partial Regex ExactSpeedTokenRegex();
}
