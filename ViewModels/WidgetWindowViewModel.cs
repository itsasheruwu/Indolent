using Indolent.Services;

namespace Indolent.ViewModels;

public sealed class WidgetWindowViewModel : ObservableObject
{
    private enum WidgetStatusPhase
    {
        None,
        Thinking,
        VideoStandby,
        ScreenshotTaken,
        ExtractingText
    }

    private readonly AppState appState;

    private bool isHovered;
    private string messageText = string.Empty;
    private string statusText = string.Empty;
    private bool isError;
    private WidgetStatusPhase statusPhase;

    public WidgetWindowViewModel(AppState appState)
    {
        this.appState = appState;
        appState.PropertyChanged += (_, _) => SyncFromState();
    }

    public bool ShowActionButton => isHovered
        && !appState.IsAnswering
        && appState.Preflight.IsReady
        && string.IsNullOrWhiteSpace(messageText);

    public bool ShowMessage => !string.IsNullOrWhiteSpace(messageText);

    public bool ShowStatus => appState.IsAnswering && !string.IsNullOrWhiteSpace(statusText);

    public bool ShowStatusSpinner => statusPhase is WidgetStatusPhase.Thinking or WidgetStatusPhase.VideoStandby;

    public bool ShowScreenshotStatusIcon => statusPhase == WidgetStatusPhase.ScreenshotTaken;

    public bool ShowOcrStatusIcon => statusPhase == WidgetStatusPhase.ExtractingText;

    public bool UseWarningStatusStyle => statusPhase is WidgetStatusPhase.ScreenshotTaken or WidgetStatusPhase.ExtractingText;

    public string MessageText
    {
        get => messageText;
        private set => SetProperty(ref messageText, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public bool IsBusy => appState.IsAnswering;

    public bool IsError
    {
        get => isError;
        private set => SetProperty(ref isError, value);
    }

    public void SetHovered(bool value)
    {
        isHovered = value;
        OnPropertyChanged(nameof(ShowActionButton));
    }

    public void SetThinking()
    {
        SetStatus(WidgetStatusPhase.Thinking, "Thinking...");
    }

    public void SetVideoStandby(TimeSpan? remaining = null)
    {
        var text = remaining.HasValue
            ? $"Waiting for video to finish... {FormatDuration(remaining.Value)} left"
            : "Waiting for video to finish...";
        SetStatus(WidgetStatusPhase.VideoStandby, text);
    }

    public void SetScreenshotTaken()
    {
        SetStatus(WidgetStatusPhase.ScreenshotTaken, "Screenshot taken");
    }

    public void SetExtractingText()
    {
        SetStatus(WidgetStatusPhase.ExtractingText, "Extracting text");
    }

    public void SetAnswerResult(AnswerResult result)
    {
        ClearStatus();
        IsError = !result.IsSuccess;
        MessageText = result.IsSuccess ? result.Text : result.ErrorMessage;
        NotifyStateChanged();
    }

    private void SyncFromState()
    {
        if (appState.IsAnswering)
        {
            if (statusPhase == WidgetStatusPhase.None)
            {
                SetThinking();
            }

            return;
        }

        ClearStatus();
        if (string.Equals(appState.LastAnswerSummary, "No answer yet.", StringComparison.Ordinal))
        {
            MessageText = string.Empty;
            IsError = false;
        }
        else if (!string.IsNullOrWhiteSpace(appState.LastAnswerDetail))
        {
            MessageText = appState.LastAnswerSummary;
            IsError = appState.LastAnswerSummary.StartsWith("Codex", StringComparison.OrdinalIgnoreCase)
                || appState.LastAnswerSummary.StartsWith("Open Code", StringComparison.OrdinalIgnoreCase)
                || appState.LastAnswerSummary.StartsWith("Capture", StringComparison.OrdinalIgnoreCase);
        }

        NotifyStateChanged();
    }

    private void SetStatus(WidgetStatusPhase phase, string text)
    {
        statusPhase = phase;
        IsError = false;
        MessageText = string.Empty;
        StatusText = text;
        NotifyStateChanged();
    }

    private void ClearStatus()
    {
        statusPhase = WidgetStatusPhase.None;
        StatusText = string.Empty;
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(ShowStatus));
        OnPropertyChanged(nameof(ShowStatusSpinner));
        OnPropertyChanged(nameof(ShowScreenshotStatusIcon));
        OnPropertyChanged(nameof(ShowOcrStatusIcon));
        OnPropertyChanged(nameof(UseWarningStatusStyle));
        OnPropertyChanged(nameof(ShowMessage));
        OnPropertyChanged(nameof(ShowActionButton));
        OnPropertyChanged(nameof(IsBusy));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return duration.ToString(@"h\:mm\:ss");
        }

        return duration.ToString(@"m\:ss");
    }
}
