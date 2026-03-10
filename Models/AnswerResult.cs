namespace Indolent.Models;

public sealed class AnswerResult
{
    public AnswerStatus Status { get; init; }

    public string Text { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }

    public bool IsSuccess => Status == AnswerStatus.Success && !string.IsNullOrWhiteSpace(Text);
}
