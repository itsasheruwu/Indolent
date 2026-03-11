namespace Indolent.Models;

public sealed class AgentClickResult
{
    public bool Clicked { get; init; }

    public string MatchedText { get; init; } = string.Empty;

    public string FailureReason { get; init; } = string.Empty;
}
