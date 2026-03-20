using System.Collections.ObjectModel;

using Indolent.Services;

namespace Indolent.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly AppState appState;
    private readonly IOpenCodeSetupService openCodeSetupService;
    private readonly IProviderRuntimeRegistry providerRegistry;
    private readonly ISettingsStore settingsStore;
    private readonly SynchronizationContext? synchronizationContext;

    private bool isRefreshing;
    private bool isRunningTerminalCommand;
    private bool isRunningGuidedSetup;
    private bool isTerminalViewOpen;
    private string selectedProviderId;
    private string selectedModel;
    private string selectedReasoningEffort;
    private string setupStatusText = string.Empty;
    private string terminalCommandText = "--help";
    private ProviderModelOption? selectedModelOption;
    private int accountVisibleModelCount;

    public MainWindowViewModel(
        AppState appState,
        IOpenCodeSetupService openCodeSetupService,
        IProviderRuntimeRegistry providerRegistry,
        ISettingsStore settingsStore)
    {
        this.appState = appState;
        this.openCodeSetupService = openCodeSetupService;
        this.providerRegistry = providerRegistry;
        this.settingsStore = settingsStore;
        synchronizationContext = SynchronizationContext.Current;
        selectedProviderId = appState.SelectedProviderId;
        selectedModel = appState.SelectedModel;
        selectedReasoningEffort = appState.SelectedReasoningEffort;

        AvailableProviders = new ObservableCollection<ProviderOption>(providerRegistry.Providers);
        AvailableModels = [];
        ReasoningOptions = [];

        foreach (var provider in providerRegistry.Providers)
        {
            providerRegistry.GetProvider(provider.Id).TerminalTranscriptChanged += (_, _) =>
            {
                if (string.Equals(appState.SelectedProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
                {
                    RaisePropertyChangedOnUiThread(nameof(TerminalTranscript));
                }
            };
        }

        appState.PropertyChanged += (_, _) => SyncFromState();
        UpdateSelectedModelState();
    }

    public ObservableCollection<ProviderOption> AvailableProviders { get; }

    public ObservableCollection<ProviderModelOption> AvailableModels { get; }

    public ObservableCollection<ReasoningLevelOption> ReasoningOptions { get; }

    public string SelectedProviderId
    {
        get => selectedProviderId;
        set
        {
            if (SetProperty(ref selectedProviderId, value))
            {
                UpdateProviderTextState();
            }
        }
    }

    public string SelectedModel
    {
        get => selectedModel;
        set
        {
            if (SetProperty(ref selectedModel, value))
            {
                UpdateSelectedModelState();
            }
        }
    }

    public string SelectedReasoningEffort
    {
        get => selectedReasoningEffort;
        set
        {
            if (SetProperty(ref selectedReasoningEffort, value))
            {
                OnPropertyChanged(nameof(SelectedReasoningDescription));
            }
        }
    }

    public bool IsReady => appState.Preflight.IsInstalled;

    public bool IsRefreshing
    {
        get => isRefreshing;
        private set
        {
            if (SetProperty(ref isRefreshing, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
            }
        }
    }

    public bool CanRefresh => !IsRefreshing;

    public bool IsTerminalViewOpen
    {
        get => isTerminalViewOpen;
        private set
        {
            if (SetProperty(ref isTerminalViewOpen, value))
            {
                OnPropertyChanged(nameof(ShowSettingsViewVisibility));
                OnPropertyChanged(nameof(ShowTerminalViewVisibility));
                OnPropertyChanged(nameof(TerminalToggleText));
            }
        }
    }

    public Visibility ShowSettingsViewVisibility => IsTerminalViewOpen ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ShowTerminalViewVisibility => IsTerminalViewOpen ? Visibility.Visible : Visibility.Collapsed;

    public string TerminalToggleText => IsTerminalViewOpen ? "Back to Settings" : "Open Terminal";

    public string TerminalCommandText
    {
        get => terminalCommandText;
        set => SetProperty(ref terminalCommandText, value);
    }

    public string TerminalTranscript => CurrentProviderRuntime.TerminalTranscript;

    public bool IsRunningTerminalCommand
    {
        get => isRunningTerminalCommand;
        private set
        {
            if (SetProperty(ref isRunningTerminalCommand, value))
            {
                OnPropertyChanged(nameof(CanRunTerminalCommand));
            }
        }
    }

    public bool CanRunTerminalCommand => !IsRunningTerminalCommand && appState.Preflight.IsInstalled;

    public bool ShowInstallBlocker => !appState.Preflight.IsInstalled || !appState.Preflight.IsLoggedIn;

    public Visibility ShowInstallBlockerVisibility => ShowInstallBlocker ? Visibility.Visible : Visibility.Collapsed;

    public string AppDescriptionText => CurrentProviderId == ProviderIds.OpenCode
        ? "Resident Open Code widget for short OCR-based answers."
        : "Resident OpenAI Codex widget for short OCR-based answers.";

    public string StatusCardTitle => $"{CurrentProviderDisplayName} status";

    public string CliLabelText => CurrentProviderId == ProviderIds.OpenCode ? "Open Code CLI" : "Codex CLI";

    public string VersionText => appState.Preflight.IsInstalled
        ? $"Installed ({appState.Preflight.Version})"
        : "Not detected";

    public string LoginStatusText => CurrentProviderId == ProviderIds.OpenCode
        ? "Handled by Open Code + local Ollama at runtime"
        : "Handled by Codex CLI at runtime";

    public string BlockingTitle => CurrentProviderId == ProviderIds.OpenCode ? "Open Code setup required" : "OpenAI Codex required";

    public string BlockingMessage => string.IsNullOrWhiteSpace(appState.Preflight.BlockingMessage)
        ? CurrentProviderId == ProviderIds.OpenCode
            ? "Indolent needs Open Code plus a reachable local Ollama `gemma3:4b` model before the widget can answer."
            : "Indolent needs a working Codex CLI install before the widget can answer."
        : appState.Preflight.BlockingMessage;

    public string LastAnswerSummary => appState.LastAnswerSummary;

    public string ProviderSectionTitle => "Provider and model";

    public string AvailableModelsSummary => CurrentProviderId == ProviderIds.OpenCode
        ? "Showing the single Open Code model configured for Ollama."
        : accountVisibleModelCount > 0
            ? $"Showing {accountVisibleModelCount} Codex models visible to this install."
            : "No account-visible Codex models were found in the local Codex cache.";

    public string SelectedModelDescription => selectedModelOption?.Description switch
    {
        { Length: > 0 } description => description,
        _ when !string.IsNullOrWhiteSpace(SelectedModel) && CurrentProviderId == ProviderIds.OpenCode => "Open Code uses the local Ollama model configured by Indolent.",
        _ when !string.IsNullOrWhiteSpace(SelectedModel) => "Current model is not in the visible Codex model list for this install.",
        _ when CurrentProviderId == ProviderIds.OpenCode => "Open Code is pinned to the local Ollama Gemma 3 model.",
        _ => "Choose a model from the Codex models visible to this install."
    };

    public string SelectedModelSupportText => selectedModelOption is null
        ? string.Empty
        : selectedModelOption.SupportsReasoningSelection
            ? $"{selectedModelOption.SupportedReasoningLevels.Count} reasoning modes available."
            : CurrentProviderId == ProviderIds.OpenCode
                ? "Open Code does not expose separate reasoning modes here."
                : "This model does not expose multiple reasoning modes.";

    public bool ShowReasoningSelector => ReasoningOptions.Count > 1;

    public Visibility ShowReasoningSelectorVisibility => ShowReasoningSelector ? Visibility.Visible : Visibility.Collapsed;

    public string SelectedReasoningDescription
        => ReasoningOptions.FirstOrDefault(option => string.Equals(option.Effort, SelectedReasoningEffort, StringComparison.OrdinalIgnoreCase))?.Description
            ?? "Reasoning effort is not configurable for the current model.";

    public string AgentModeDescription => CurrentProviderId == ProviderIds.OpenCode
        ? "When enabled, Indolent will try to click the matching answer on screen. It uses local OCR first and only asks Open Code again if the click target is ambiguous."
        : "When enabled, Indolent will try to click the matching answer on screen. It uses local OCR first and only asks Codex again if the click target is ambiguous.";

    public string ProviderBehaviorText => CurrentProviderId == ProviderIds.OpenCode
        ? "Indolent manages a runtime Open Code config for Ollama, stages screenshots in a temp folder, and attaches those files when it shells out to `opencode`."
        : "Indolent does not manage authentication. It reuses whatever Codex CLI session is already available and only shells out to Codex when needed.";

    public string TerminalTitle => CurrentProviderId == ProviderIds.OpenCode ? "Open Code terminal" : "Codex terminal";

    public string TerminalDescription => CurrentProviderId == ProviderIds.OpenCode
        ? "This is the shared Open Code transcript the app uses for widget answers and terminal commands."
        : "This is the shared Codex CLI transcript the app uses for widget answers and terminal commands.";

    public string TerminalArgumentsLabel => CurrentProviderId == ProviderIds.OpenCode ? "Open Code arguments" : "Codex arguments";

    public string InstallGuideUrl => CurrentProviderId == ProviderIds.OpenCode
        ? "https://opencode.ai/docs"
        : "https://help.openai.com/en/articles/11096431-openai-codex-cli-getting-started";

    public string LogsDirectoryPath => CurrentProviderRuntime.LogsDirectoryPath;

    public bool IsRunningGuidedSetup
    {
        get => isRunningGuidedSetup;
        private set
        {
            if (SetProperty(ref isRunningGuidedSetup, value))
            {
                OnPropertyChanged(nameof(CanRunGuidedSetup));
            }
        }
    }

    public bool CanRunGuidedSetup => !IsRunningGuidedSetup && CurrentProviderId == ProviderIds.OpenCode;

    public bool ShowGuidedSetupButton => CurrentProviderId == ProviderIds.OpenCode;

    public Visibility ShowGuidedSetupVisibility => ShowGuidedSetupButton ? Visibility.Visible : Visibility.Collapsed;

    public string GuidedSetupDescription => "Indolent can install Open Code, install or start Ollama, and download Gemma 3 without leaving the app unless a manual installer step is required.";

    public string SetupStatusText
    {
        get => setupStatusText;
        private set
        {
            if (SetProperty(ref setupStatusText, value))
            {
                OnPropertyChanged(nameof(ShowSetupStatus));
                OnPropertyChanged(nameof(ShowSetupStatusVisibility));
            }
        }
    }

    public bool ShowSetupStatus => !string.IsNullOrWhiteSpace(SetupStatusText);

    public Visibility ShowSetupStatusVisibility => ShowSetupStatus ? Visibility.Visible : Visibility.Collapsed;

    public bool StartWithWidget
    {
        get => appState.StartWithWidget;
        set
        {
            if (appState.StartWithWidget == value)
            {
                return;
            }

            appState.StartWithWidget = value;
            OnPropertyChanged();
            _ = appState.PersistAsync(settingsStore);
        }
    }

    public bool AgentModeEnabled
    {
        get => appState.AgentModeEnabled;
        set
        {
            if (appState.AgentModeEnabled == value)
            {
                return;
            }

            appState.AgentModeEnabled = value;
            if (!value && appState.AgentLoopEnabled)
            {
                appState.AgentLoopEnabled = false;
                OnPropertyChanged(nameof(AgentLoopEnabled));
                OnPropertyChanged(nameof(ShowAgentLoopVisibility));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowAgentLoopVisibility));
            _ = appState.PersistAsync(settingsStore);
        }
    }

    public bool AgentLoopEnabled
    {
        get => appState.AgentLoopEnabled;
        set
        {
            if (appState.AgentLoopEnabled == value)
            {
                return;
            }

            appState.AgentLoopEnabled = value;
            OnPropertyChanged();
            _ = appState.PersistAsync(settingsStore);
        }
    }

    public bool SaveCurrentModelOnRestart
    {
        get => appState.SaveCurrentModelOnRestart;
        set
        {
            if (appState.SaveCurrentModelOnRestart == value)
            {
                return;
            }

            appState.SaveCurrentModelOnRestart = value;
            OnPropertyChanged();
            _ = appState.PersistAsync(settingsStore);
        }
    }

    public Visibility ShowAgentLoopVisibility => AgentModeEnabled ? Visibility.Visible : Visibility.Collapsed;

    private string CurrentProviderId => ProviderIds.Normalize(SelectedProviderId);

    private string CurrentProviderDisplayName => providerRegistry.GetProvider(CurrentProviderId).DisplayName;

    private IProviderRuntime CurrentProviderRuntime => providerRegistry.GetProvider(CurrentProviderId);

    public async Task RefreshPreflightAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;

        try
        {
            var providerRuntime = CurrentProviderRuntime;
            var preflightTask = providerRuntime.RunPreflightAsync();
            var modelsTask = providerRuntime.LoadModelsAsync();
            await Task.WhenAll(preflightTask, modelsTask);

            appState.UpdatePreflight(await preflightTask);
            LoadAvailableModels(await modelsTask);
            await appState.PersistAsync(settingsStore);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task CommitSelectedProviderAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedProviderId))
        {
            return;
        }

        await appState.SetSelectedProviderAsync(SelectedProviderId, providerRegistry);
        await RefreshPreflightAsync();
        await appState.PersistAsync(settingsStore);
    }

    public async Task CommitSelectedModelAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedModel))
        {
            return;
        }

        appState.SetSelectedModel(SelectedModel);
        appState.SetSelectedReasoningEffort(SelectedReasoningEffort);
        await appState.PersistAsync(settingsStore);
    }

    public async Task CommitSelectedReasoningAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedReasoningEffort))
        {
            return;
        }

        appState.SetSelectedReasoningEffort(SelectedReasoningEffort);
        await appState.PersistAsync(settingsStore);
    }

    public async Task RunGuidedSetupAsync()
    {
        if (!CanRunGuidedSetup)
        {
            return;
        }

        IsRunningGuidedSetup = true;
        SetupStatusText = "Starting guided Open Code setup...";
        var progress = new Progress<string>(message => SetupStatusText = message);

        try
        {
            var result = await openCodeSetupService.EnsureReadyAsync(progress);
            SetupStatusText = string.IsNullOrWhiteSpace(result.Detail)
                ? result.Summary
                : $"{result.Summary}\r\n\r\n{result.Detail}";
            await RefreshPreflightAsync();
        }
        finally
        {
            IsRunningGuidedSetup = false;
        }
    }

    public void ToggleTerminalView()
        => IsTerminalViewOpen = !IsTerminalViewOpen;

    public void ClearTerminalTranscript()
    {
        CurrentProviderRuntime.ClearTerminalTranscript();
        OnPropertyChanged(nameof(TerminalTranscript));
    }

    public async Task RunTerminalCommandAsync()
    {
        if (IsRunningTerminalCommand)
        {
            return;
        }

        var trimmedArguments = string.IsNullOrWhiteSpace(TerminalCommandText) ? "--help" : TerminalCommandText.Trim();
        IsRunningTerminalCommand = true;

        try
        {
            await CurrentProviderRuntime.RunTerminalCommandAsync(trimmedArguments);
            OnPropertyChanged(nameof(TerminalTranscript));
        }
        finally
        {
            IsRunningTerminalCommand = false;
        }
    }

    private void SyncFromState()
    {
        selectedProviderId = appState.SelectedProviderId;
        selectedModel = appState.SelectedModel;
        selectedReasoningEffort = appState.SelectedReasoningEffort;
        UpdateSelectedModelState();

        OnPropertyChanged(nameof(SelectedProviderId));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedReasoningEffort));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanRunTerminalCommand));
        OnPropertyChanged(nameof(ShowInstallBlocker));
        OnPropertyChanged(nameof(ShowInstallBlockerVisibility));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(LoginStatusText));
        OnPropertyChanged(nameof(BlockingTitle));
        OnPropertyChanged(nameof(BlockingMessage));
        OnPropertyChanged(nameof(LastAnswerSummary));
        OnPropertyChanged(nameof(StartWithWidget));
        OnPropertyChanged(nameof(AgentModeEnabled));
        OnPropertyChanged(nameof(AgentLoopEnabled));
        OnPropertyChanged(nameof(SaveCurrentModelOnRestart));
        OnPropertyChanged(nameof(ShowAgentLoopVisibility));
        OnPropertyChanged(nameof(TerminalTranscript));
        OnPropertyChanged(nameof(CanRunGuidedSetup));
        OnPropertyChanged(nameof(ShowGuidedSetupButton));
        OnPropertyChanged(nameof(ShowGuidedSetupVisibility));
        UpdateProviderTextState();
    }

    private void LoadAvailableModels(IReadOnlyList<ProviderModelOption> models)
    {
        accountVisibleModelCount = models.Count;
        var resolvedModels = models.ToList();

        if (!string.IsNullOrWhiteSpace(SelectedModel)
            && resolvedModels.All(model => !string.Equals(model.Slug, SelectedModel, StringComparison.OrdinalIgnoreCase)))
        {
            resolvedModels.Insert(0, new ProviderModelOption
            {
                Slug = SelectedModel,
                DisplayName = SelectedModel,
                Description = CurrentProviderId == ProviderIds.OpenCode
                    ? "Current model is outside the app-managed Open Code configuration."
                    : "Current model is not in the visible Codex model list for this install.",
                Visibility = "custom",
                Priority = int.MinValue,
                DefaultReasoningLevel = appState.SelectedReasoningEffort,
                SupportedReasoningLevels = string.IsNullOrWhiteSpace(appState.SelectedReasoningEffort)
                    ? []
                    : [new ReasoningLevelOption
                    {
                        Effort = appState.SelectedReasoningEffort,
                        Description = "Reusing the saved reasoning effort for this model."
                    }]
            });
        }

        SyncCollection(AvailableModels, resolvedModels);
        UpdateSelectedModelState();
        OnPropertyChanged(nameof(AvailableModelsSummary));
    }

    private void UpdateSelectedModelState()
    {
        selectedModelOption = AvailableModels.FirstOrDefault(model =>
            string.Equals(model.Slug, SelectedModel, StringComparison.OrdinalIgnoreCase));

        var supportedReasoningLevels = selectedModelOption?.SupportedReasoningLevels ?? [];
        SyncCollection(ReasoningOptions, supportedReasoningLevels);

        var resolvedReasoningEffort = ResolveReasoningEffort();
        if (!string.Equals(selectedReasoningEffort, resolvedReasoningEffort, StringComparison.OrdinalIgnoreCase))
        {
            selectedReasoningEffort = resolvedReasoningEffort;
            OnPropertyChanged(nameof(SelectedReasoningEffort));
        }

        OnPropertyChanged(nameof(SelectedModelDescription));
        OnPropertyChanged(nameof(SelectedModelSupportText));
        OnPropertyChanged(nameof(ShowReasoningSelector));
        OnPropertyChanged(nameof(ShowReasoningSelectorVisibility));
        OnPropertyChanged(nameof(SelectedReasoningDescription));
    }

    private string ResolveReasoningEffort()
    {
        if (ReasoningOptions.Count == 0)
        {
            return selectedReasoningEffort;
        }

        if (ReasoningOptions.Any(option => string.Equals(option.Effort, selectedReasoningEffort, StringComparison.OrdinalIgnoreCase)))
        {
            return selectedReasoningEffort;
        }

        if (ReasoningOptions.Any(option => string.Equals(option.Effort, appState.SelectedReasoningEffort, StringComparison.OrdinalIgnoreCase)))
        {
            return appState.SelectedReasoningEffort;
        }

        if (!string.IsNullOrWhiteSpace(selectedModelOption?.DefaultReasoningLevel)
            && ReasoningOptions.Any(option => string.Equals(option.Effort, selectedModelOption.DefaultReasoningLevel, StringComparison.OrdinalIgnoreCase)))
        {
            return selectedModelOption.DefaultReasoningLevel;
        }

        return ReasoningOptions[0].Effort;
    }

    private void UpdateProviderTextState()
    {
        OnPropertyChanged(nameof(AppDescriptionText));
        OnPropertyChanged(nameof(StatusCardTitle));
        OnPropertyChanged(nameof(CliLabelText));
        OnPropertyChanged(nameof(ProviderSectionTitle));
        OnPropertyChanged(nameof(AvailableModelsSummary));
        OnPropertyChanged(nameof(SelectedModelDescription));
        OnPropertyChanged(nameof(SelectedModelSupportText));
        OnPropertyChanged(nameof(AgentModeDescription));
        OnPropertyChanged(nameof(ProviderBehaviorText));
        OnPropertyChanged(nameof(TerminalTitle));
        OnPropertyChanged(nameof(TerminalDescription));
        OnPropertyChanged(nameof(TerminalArgumentsLabel));
        OnPropertyChanged(nameof(InstallGuideUrl));
        OnPropertyChanged(nameof(LogsDirectoryPath));
        OnPropertyChanged(nameof(CanRunGuidedSetup));
        OnPropertyChanged(nameof(ShowGuidedSetupButton));
        OnPropertyChanged(nameof(ShowGuidedSetupVisibility));
        OnPropertyChanged(nameof(GuidedSetupDescription));
    }

    private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (var index = 0; index < source.Count; index++)
        {
            if (index >= target.Count)
            {
                target.Add(source[index]);
                continue;
            }

            if (EqualityComparer<T>.Default.Equals(target[index], source[index]))
            {
                continue;
            }

            target[index] = source[index];
        }
    }

    private void RaisePropertyChangedOnUiThread(string propertyName)
    {
        if (synchronizationContext is null)
        {
            OnPropertyChanged(propertyName);
            return;
        }

        synchronizationContext.Post(_ => OnPropertyChanged(propertyName), null);
    }
}
