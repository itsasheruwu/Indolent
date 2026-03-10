namespace Indolent.Models;

public sealed class AnswerRequest
{
    public required string Model { get; init; }

    public string ScreenText { get; init; } = string.Empty;

    public string ScreenshotPath { get; init; } = string.Empty;

    public required string Prompt { get; init; }

    public string ReasoningEffort { get; init; } = string.Empty;

    public DateTimeOffset RequestedAt { get; init; }
}
