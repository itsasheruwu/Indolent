namespace Indolent.Models;

public sealed class AppSettings
{
    public string SelectedModel { get; set; } = "gpt-5.4";

    public string SelectedReasoningEffort { get; set; } = "low";

    public List<string> RecentModels { get; set; } = [];

    public WidgetBounds WidgetBounds { get; set; } = new();

    public bool StartWithWidget { get; set; } = true;

    public bool AgentModeEnabled { get; set; }

    public bool AgentLoopEnabled { get; set; }

    public string LastSuccessfulModel { get; set; } = "gpt-5.4";

    public string LastSuccessfulReasoningEffort { get; set; } = "low";
}
