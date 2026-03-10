using Indolent.Services;

namespace Indolent.ViewModels;

public sealed class WidgetWindowViewModel : ObservableObject
{
    private readonly AppState appState;

    private bool isHovered;
    private string messageText = string.Empty;
    private string tooltipText = "Capture the current screen, extract text with OCR, and ask Codex for a brief answer.";
    private bool isError;

    public WidgetWindowViewModel(AppState appState)
    {
        this.appState = appState;
        appState.PropertyChanged += (_, _) => SyncFromState();
    }

    public bool ShowActionButton => isHovered
        && !appState.IsAnswering
        && appState.Preflight.IsReady
        && string.IsNullOrWhiteSpace(messageText);

    public bool ShowMessage => appState.IsAnswering || !string.IsNullOrWhiteSpace(messageText);

    public string MessageText
    {
        get => messageText;
        private set => SetProperty(ref messageText, value);
    }

    public string TooltipText
    {
        get => tooltipText;
        private set => SetProperty(ref tooltipText, value);
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
        IsError = false;
        MessageText = "Thinking...";
        TooltipText = "Indolent is waiting on Codex CLI.";
        OnPropertyChanged(nameof(ShowMessage));
        OnPropertyChanged(nameof(ShowActionButton));
    }

    public void SetAnswerResult(AnswerResult result)
    {
        IsError = !result.IsSuccess;
        MessageText = result.IsSuccess ? result.Text : result.ErrorMessage;
        TooltipText = result.IsSuccess ? result.Text : result.ErrorMessage;
        OnPropertyChanged(nameof(ShowMessage));
        OnPropertyChanged(nameof(ShowActionButton));
    }

    private void SyncFromState()
    {
        if (appState.IsAnswering)
        {
            SetThinking();
            return;
        }

        if (string.Equals(appState.LastAnswerSummary, "No answer yet.", StringComparison.Ordinal))
        {
            MessageText = string.Empty;
            TooltipText = "Capture the current screen, extract text with OCR, and ask Codex for a brief answer.";
            IsError = false;
        }
        else if (!string.IsNullOrWhiteSpace(appState.LastAnswerDetail))
        {
            MessageText = appState.LastAnswerSummary;
            TooltipText = appState.LastAnswerDetail;
            IsError = appState.LastAnswerSummary.StartsWith("Codex", StringComparison.OrdinalIgnoreCase)
                || appState.LastAnswerSummary.StartsWith("Capture", StringComparison.OrdinalIgnoreCase);
        }

        OnPropertyChanged(nameof(ShowMessage));
        OnPropertyChanged(nameof(ShowActionButton));
        OnPropertyChanged(nameof(IsBusy));
    }
}
