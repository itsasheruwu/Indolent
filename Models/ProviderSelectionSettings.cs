namespace Indolent.Models;

public sealed class ProviderSelectionSettings
{
    public string SelectedModel { get; set; } = string.Empty;

    public string SelectedReasoningEffort { get; set; } = string.Empty;

    public List<string> RecentModels { get; set; } = [];

    public string LastSuccessfulModel { get; set; } = string.Empty;

    public string LastSuccessfulReasoningEffort { get; set; } = string.Empty;
}
