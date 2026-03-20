namespace Indolent.Services;

public sealed class AppState : ObservableObject
{
    private readonly SemaphoreSlim persistenceGate = new(1, 1);

    private AppSettings settings = new();
    private ProviderPreflightResult preflight = new();
    private string selectedProviderId = ProviderIds.OpenAiCodex;
    private string selectedModel = string.Empty;
    private string selectedReasoningEffort = string.Empty;
    private string lastAnswerSummary = "No answer yet.";
    private string lastAnswerDetail = string.Empty;
    private bool isAnswering;

    public IReadOnlyList<string> RecentModels => GetCurrentSelection().RecentModels;

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

    public bool AgentModeEnabled
    {
        get => settings.AgentModeEnabled;
        set
        {
            if (settings.AgentModeEnabled == value)
            {
                return;
            }

            settings.AgentModeEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool AgentLoopEnabled
    {
        get => settings.AgentLoopEnabled;
        set
        {
            if (settings.AgentLoopEnabled == value)
            {
                return;
            }

            settings.AgentLoopEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool SaveCurrentModelOnRestart
    {
        get => settings.SaveCurrentModelOnRestart;
        set
        {
            if (settings.SaveCurrentModelOnRestart == value)
            {
                return;
            }

            settings.SaveCurrentModelOnRestart = value;
            OnPropertyChanged();
        }
    }

    public WidgetBounds WidgetBounds => settings.WidgetBounds;

    public ProviderPreflightResult Preflight
    {
        get => preflight;
        private set
        {
            preflight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAnswer));
        }
    }

    public string SelectedProviderId
    {
        get => selectedProviderId;
        private set
        {
            if (SetProperty(ref selectedProviderId, value))
            {
                OnPropertyChanged(nameof(CanAnswer));
            }
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

    public bool CanAnswer => Preflight.IsReady && !string.IsNullOrWhiteSpace(SelectedModel) && !IsAnswering;

    public async Task InitializeAsync(ISettingsStore settingsStore, IProviderRuntimeRegistry providerRegistry, CancellationToken cancellationToken = default)
    {
        settings = await settingsStore.LoadAsync();
        settings.WidgetBounds ??= new WidgetBounds();
        MigrateLegacyProviderSelections();

        var initialProviderId = providerRegistry.IsKnownProvider(settings.SelectedProviderId)
            ? ProviderIds.Normalize(settings.SelectedProviderId)
            : ProviderIds.OpenAiCodex;
        SelectedProviderId = initialProviderId;
        settings.SelectedProviderId = initialProviderId;

        await LoadProviderSelectionAsync(providerRegistry.GetProvider(initialProviderId), cancellationToken);
        LastAnswerSummary = "No answer yet.";
        LastAnswerDetail = string.Empty;

        OnPropertyChanged(nameof(SelectedProviderId));
        OnPropertyChanged(nameof(RecentModels));
        OnPropertyChanged(nameof(StartWithWidget));
        OnPropertyChanged(nameof(AgentModeEnabled));
        OnPropertyChanged(nameof(AgentLoopEnabled));
        OnPropertyChanged(nameof(SaveCurrentModelOnRestart));
    }

    public async Task SetSelectedProviderAsync(string providerId, IProviderRuntimeRegistry providerRegistry, CancellationToken cancellationToken = default)
    {
        var normalizedProviderId = providerRegistry.IsKnownProvider(providerId)
            ? ProviderIds.Normalize(providerId)
            : ProviderIds.OpenAiCodex;

        if (string.Equals(SelectedProviderId, normalizedProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedProviderId = normalizedProviderId;
        settings.SelectedProviderId = normalizedProviderId;
        Preflight = new ProviderPreflightResult();
        await LoadProviderSelectionAsync(providerRegistry.GetProvider(normalizedProviderId), cancellationToken);
        OnPropertyChanged(nameof(RecentModels));
    }

    public void UpdatePreflight(ProviderPreflightResult result) => Preflight = result;

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
            var selection = GetCurrentSelection();
            selection.LastSuccessfulModel = SelectedModel;
            selection.LastSuccessfulReasoningEffort = SelectedReasoningEffort;
            AddRecentModel(SelectedModel);
        }
        else
        {
            LastAnswerSummary = result.ErrorMessage;
            LastAnswerDetail = result.ErrorMessage;
        }

        OnPropertyChanged(nameof(RecentModels));
    }

    public void CancelAnswer()
    {
        IsAnswering = false;
        LastAnswerSummary = "Stopped.";
        LastAnswerDetail = "Stopped.";
    }

    public void SetSelectedModel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        SelectedModel = trimmed;
        GetCurrentSelection().SelectedModel = trimmed;
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
        GetCurrentSelection().SelectedReasoningEffort = trimmed;
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
            settings.SelectedProviderId = SelectedProviderId;
            var selection = GetCurrentSelection();
            selection.SelectedModel = SelectedModel;
            selection.SelectedReasoningEffort = SelectedReasoningEffort;
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

        var recentModels = GetCurrentSelection().RecentModels;
        recentModels.RemoveAll(existing => string.Equals(existing, model, StringComparison.OrdinalIgnoreCase));
        recentModels.Insert(0, model);

        if (recentModels.Count > 8)
        {
            recentModels.RemoveRange(8, recentModels.Count - 8);
        }
    }

    private async Task LoadProviderSelectionAsync(IProviderRuntime providerRuntime, CancellationToken cancellationToken)
    {
        var selection = GetSelection(providerRuntime.ProviderId);
        var configuredDefaults = await providerRuntime.ReadConfiguredDefaultsAsync(cancellationToken);
        var initialModel = SaveCurrentModelOnRestart
            ? FirstNonEmpty(
                selection.SelectedModel,
                selection.LastSuccessfulModel,
                configuredDefaults.SelectedModel,
                providerRuntime.ProviderId == ProviderIds.OpenAiCodex ? "gpt-5.4-mini" : "ollama/gemma3:4b")
            : FirstNonEmpty(
                configuredDefaults.SelectedModel,
                providerRuntime.ProviderId == ProviderIds.OpenAiCodex ? "gpt-5.4-mini" : "ollama/gemma3:4b");
        var initialReasoningEffort = FirstNonEmpty(
            selection.SelectedReasoningEffort,
            selection.LastSuccessfulReasoningEffort,
            configuredDefaults.SelectedReasoningEffort,
            providerRuntime.ProviderId == ProviderIds.OpenAiCodex ? "medium" : string.Empty);

        SelectedModel = initialModel;
        SelectedReasoningEffort = initialReasoningEffort;
        if (!string.IsNullOrWhiteSpace(initialModel))
        {
            AddRecentModel(initialModel);
        }
    }

    private void MigrateLegacyProviderSelections()
    {
        settings.ProviderSelections ??= new Dictionary<string, ProviderSelectionSettings>(StringComparer.OrdinalIgnoreCase);
        settings.RecentModels ??= [];

        var codexSelection = GetSelection(ProviderIds.OpenAiCodex);
        if (string.IsNullOrWhiteSpace(codexSelection.SelectedModel) && !string.IsNullOrWhiteSpace(settings.SelectedModel))
        {
            codexSelection.SelectedModel = settings.SelectedModel;
        }

        if (string.IsNullOrWhiteSpace(codexSelection.SelectedReasoningEffort) && !string.IsNullOrWhiteSpace(settings.SelectedReasoningEffort))
        {
            codexSelection.SelectedReasoningEffort = settings.SelectedReasoningEffort;
        }

        if (string.IsNullOrWhiteSpace(codexSelection.LastSuccessfulModel) && !string.IsNullOrWhiteSpace(settings.LastSuccessfulModel))
        {
            codexSelection.LastSuccessfulModel = settings.LastSuccessfulModel;
        }

        if (string.IsNullOrWhiteSpace(codexSelection.LastSuccessfulReasoningEffort) && !string.IsNullOrWhiteSpace(settings.LastSuccessfulReasoningEffort))
        {
            codexSelection.LastSuccessfulReasoningEffort = settings.LastSuccessfulReasoningEffort;
        }

        if (codexSelection.RecentModels.Count == 0 && settings.RecentModels.Count > 0)
        {
            codexSelection.RecentModels.AddRange(settings.RecentModels);
        }

        _ = GetSelection(ProviderIds.OpenCode);
    }

    private ProviderSelectionSettings GetCurrentSelection() => GetSelection(SelectedProviderId);

    private ProviderSelectionSettings GetSelection(string providerId)
    {
        var normalizedProviderId = ProviderIds.Normalize(providerId);
        if (!settings.ProviderSelections.TryGetValue(normalizedProviderId, out var selection) || selection is null)
        {
            selection = new ProviderSelectionSettings();
            settings.ProviderSelections[normalizedProviderId] = selection;
        }

        selection.RecentModels ??= [];
        return selection;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
