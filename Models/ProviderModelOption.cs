namespace Indolent.Models;

public sealed class ProviderModelOption
{
    public required string Slug { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public string Visibility { get; init; } = string.Empty;

    public int Priority { get; init; }

    public string DefaultReasoningLevel { get; init; } = string.Empty;

    public IReadOnlyList<ReasoningLevelOption> SupportedReasoningLevels { get; init; } = [];

    public bool SupportsReasoningSelection => SupportedReasoningLevels.Count > 1;
}
