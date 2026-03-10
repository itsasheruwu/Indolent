namespace Indolent.Models;

public sealed class AppSettings
{
    public string SelectedModel { get; set; } = string.Empty;

    public string SelectedReasoningEffort { get; set; } = string.Empty;

    public List<string> RecentModels { get; set; } = [];

    public WidgetBounds WidgetBounds { get; set; } = new();

    public bool StartWithWidget { get; set; } = true;

    public string LastSuccessfulModel { get; set; } = string.Empty;

    public string LastSuccessfulReasoningEffort { get; set; } = string.Empty;
}
