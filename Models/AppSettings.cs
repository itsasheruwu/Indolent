namespace Indolent.Models;

public sealed class AppSettings
{
    public string SelectedProviderId { get; set; } = ProviderIds.OpenAiCodex;

    public Dictionary<string, ProviderSelectionSettings> ProviderSelections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string SelectedModel { get; set; } = "gpt-5.4-mini";

    public string SelectedReasoningEffort { get; set; } = "medium";

    public bool SaveCurrentModelOnRestart { get; set; } = true;

    public List<string> RecentModels { get; set; } = [];

    public WidgetBounds WidgetBounds { get; set; } = new();

    public bool StartWithWidget { get; set; } = true;

    public bool AgentModeEnabled { get; set; }

    public bool AgentLoopEnabled { get; set; }

    public string LastSuccessfulModel { get; set; } = "gpt-5.4-mini";

    public string LastSuccessfulReasoningEffort { get; set; } = "medium";
}
