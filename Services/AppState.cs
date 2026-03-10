namespace Indolent.Services;

public sealed class AppState : ObservableObject
{
    private readonly SemaphoreSlim persistenceGate = new(1, 1);

    private AppSettings settings = new();
    private CodexPreflightResult preflight = new();
    private string selectedModel = string.Empty;
    private string selectedReasoningEffort = string.Empty;
    private string lastAnswerSummary = "No answer yet.";
    private string lastAnswerDetail = string.Empty;
    private bool isAnswering;

    public IReadOnlyList<string> RecentModels => settings.RecentModels;

    public bool StartWithWidget
    {
        get => settings.StartWithWidget;
        set
        {
            if (settings.StartWithWidget == value)
            {
                return;
            }

            settings.StartWithWidget = value;
            OnPropertyChanged();
        }
    }

    public WidgetBounds WidgetBounds => settings.WidgetBounds;

    public CodexPreflightResult Preflight
    {
        get => preflight;
        private set
        {
            preflight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAnswer));
        }
    }

    public string SelectedModel
    {
        get => selectedModel;
        private set
        {
            if (SetProperty(ref selectedModel, value))
            {
                OnPropertyChanged(nameof(CanAnswer));
            }
        }
    }

    public string SelectedReasoningEffort
    {
        get => selectedReasoningEffort;
        private set => SetProperty(ref selectedReasoningEffort, value);
    }

    public string LastAnswerSummary
    {
        get => lastAnswerSummary;
        private set => SetProperty(ref lastAnswerSummary, value);
    }

    public string LastAnswerDetail
    {
        get => lastAnswerDetail;
        private set => SetProperty(ref lastAnswerDetail, value);
    }

    public bool IsAnswering
    {
        get => isAnswering;
        private set
        {
            if (SetProperty(ref isAnswering, value))
            {
                OnPropertyChanged(nameof(CanAnswer));
            }
        }
    }

    public bool CanAnswer => Preflight.IsInstalled && !string.IsNullOrWhiteSpace(SelectedModel) && !IsAnswering;

    public async Task InitializeAsync(ISettingsStore settingsStore, ICodexCliService codexCliService, CancellationToken cancellationToken = default)
    {
        settings = await settingsStore.LoadAsync();
        settings.RecentModels ??= [];
        settings.WidgetBounds ??= new WidgetBounds();

        var configuredModel = await codexCliService.ReadConfiguredModelAsync(cancellationToken);
        var configuredReasoningEffort = await codexCliService.ReadConfiguredReasoningEffortAsync(cancellationToken);
        var initialModel = FirstNonEmpty(settings.SelectedModel, settings.LastSuccessfulModel, configuredModel, "gpt-5.4");
        var initialReasoningEffort = FirstNonEmpty(
            settings.SelectedReasoningEffort,
            settings.LastSuccessfulReasoningEffort,
            configuredReasoningEffort,
            "low");

        SelectedModel = initialModel;
        SelectedReasoningEffort = initialReasoningEffort;
        AddRecentModel(initialModel);
        LastAnswerSummary = "No answer yet.";
        LastAnswerDetail = string.Empty;

        OnPropertyChanged(nameof(RecentModels));
        OnPropertyChanged(nameof(StartWithWidget));
    }

    public void UpdatePreflight(CodexPreflightResult result) => Preflight = result;

    public void BeginAnswer()
    {
        IsAnswering = true;
        LastAnswerSummary = "Thinking...";
        LastAnswerDetail = string.Empty;
    }

    public void CompleteAnswer(AnswerResult result)
    {
        IsAnswering = false;

        if (result.IsSuccess)
        {
            LastAnswerSummary = result.Text;
            LastAnswerDetail = result.Text;
            settings.LastSuccessfulModel = SelectedModel;
            settings.LastSuccessfulReasoningEffort = SelectedReasoningEffort;
            AddRecentModel(SelectedModel);
        }
        else
        {
            LastAnswerSummary = result.ErrorMessage;
            LastAnswerDetail = result.ErrorMessage;
        }

        OnPropertyChanged(nameof(RecentModels));
    }

    public void SetSelectedModel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        SelectedModel = trimmed;
        settings.SelectedModel = trimmed;
        AddRecentModel(trimmed);
        OnPropertyChanged(nameof(RecentModels));
    }

    public void SetSelectedReasoningEffort(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        SelectedReasoningEffort = trimmed;
        settings.SelectedReasoningEffort = trimmed;
    }

    public void UpdateWidgetBounds(double x, double y, double width, double height)
    {
        settings.WidgetBounds.X = x;
        settings.WidgetBounds.Y = y;
        settings.WidgetBounds.Width = width;
        settings.WidgetBounds.Height = height;
    }

    public async Task PersistAsync(ISettingsStore settingsStore, CancellationToken cancellationToken = default)
    {
        await persistenceGate.WaitAsync(cancellationToken);

        try
        {
            settings.SelectedModel = SelectedModel;
            settings.SelectedReasoningEffort = SelectedReasoningEffort;
            await settingsStore.SaveAsync(settings);
        }
        finally
        {
            persistenceGate.Release();
        }
    }

    private void AddRecentModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        settings.RecentModels.RemoveAll(existing => string.Equals(existing, model, StringComparison.OrdinalIgnoreCase));
        settings.RecentModels.Insert(0, model);

        if (settings.RecentModels.Count > 8)
        {
            settings.RecentModels.RemoveRange(8, settings.RecentModels.Count - 8);
        }
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value))!;
}
