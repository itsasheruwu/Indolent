using Indolent.Services;

namespace Indolent.ViewModels;

public sealed class WidgetWindowViewModel : ObservableObject
{
    private readonly AppState appState;

    private bool isHovered;
    private string messageText = string.Empty;
    private bool isError;
    private bool isVideoStandby;

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

    public bool IsBusy => appState.IsAnswering && !isVideoStandby;

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
        isVideoStandby = false;
        IsError = false;
        MessageText = "Thinking...";
        OnPropertyChanged(nameof(ShowMessage));
        OnPropertyChanged(nameof(ShowActionButton));
        OnPropertyChanged(nameof(IsBusy));
    }

    public void SetVideoStandby()
    {
        isVideoStandby = true;
        IsError = false;
        MessageText = "Waiting for video to finish...";
        OnPropertyChanged(nameof(ShowMessage));
        OnPropertyChanged(nameof(ShowActionButton));
        OnPropertyChanged(nameof(IsBusy));
    }

    public void SetAnswerResult(AnswerResult result)
    {
        isVideoStandby = false;
        IsError = !result.IsSuccess;
        MessageText = result.IsSuccess ? result.Text : result.ErrorMessage;
        OnPropertyChanged(nameof(ShowMessage));
        OnPropertyChanged(nameof(ShowActionButton));
        OnPropertyChanged(nameof(IsBusy));
    }

    private void SyncFromState()
    {
        if (appState.IsAnswering)
        {
            if (!isVideoStandby)
            {
                SetThinking();
            }

            return;
        }

        isVideoStandby = false;
        if (string.Equals(appState.LastAnswerSummary, "No answer yet.", StringComparison.Ordinal))
        {
            MessageText = string.Empty;
            IsError = false;
        }
        else if (!string.IsNullOrWhiteSpace(appState.LastAnswerDetail))
        {
            MessageText = appState.LastAnswerSummary;
            IsError = appState.LastAnswerSummary.StartsWith("Codex", StringComparison.OrdinalIgnoreCase)
                || appState.LastAnswerSummary.StartsWith("Capture", StringComparison.OrdinalIgnoreCase);
        }

        OnPropertyChanged(nameof(ShowMessage));
        OnPropertyChanged(nameof(ShowActionButton));
        OnPropertyChanged(nameof(IsBusy));
    }
}
