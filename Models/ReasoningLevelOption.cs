namespace Indolent.Models;

public sealed class ReasoningLevelOption
{
    public required string Effort { get; init; }

    public string Description { get; init; } = string.Empty;

    public string DisplayName => Effort switch
    {
        "xhigh" => "Extra High",
        "high" => "High",
        "medium" => "Medium",
        "low" => "Low",
        "minimal" => "Minimal",
        _ => Effort
    };
}
