using System.Collections.ObjectModel;

using Indolent.Services;

namespace Indolent.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly AppState appState;
    private readonly ICodexCliService codexCliService;
    private readonly ICodexModelCatalogService modelCatalogService;
    private readonly ISettingsStore settingsStore;
    private readonly SynchronizationContext? synchronizationContext;

    private bool isRefreshing;
    private bool isRunningTerminalCommand;
    private bool isTerminalViewOpen;
    private string selectedModel;
    private string selectedReasoningEffort;
    private string terminalCommandText = "--help";
    private CodexModelOption? selectedModelOption;
    private int accountVisibleModelCount;

    public MainWindowViewModel(
        AppState appState,
        ICodexCliService codexCliService,
        ICodexModelCatalogService modelCatalogService,
        ISettingsStore settingsStore)
    {
        this.appState = appState;
        this.codexCliService = codexCliService;
        this.modelCatalogService = modelCatalogService;
        this.settingsStore = settingsStore;
        synchronizationContext = SynchronizationContext.Current;
        selectedModel = appState.SelectedModel;
        selectedReasoningEffort = appState.SelectedReasoningEffort;
        AvailableModels = [];
        ReasoningOptions = [];
        appState.PropertyChanged += (_, _) => SyncFromState();
        codexCliService.TerminalTranscriptChanged += (_, _) => RaisePropertyChangedOnUiThread(nameof(TerminalTranscript));
        UpdateSelectedModelState();
    }

    public ObservableCollection<CodexModelOption> AvailableModels { get; }

    public ObservableCollection<ReasoningLevelOption> ReasoningOptions { get; }

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
        private set => SetProperty(ref isRefreshing, value);
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

    public string TerminalTranscript
    {
        get => codexCliService.TerminalTranscript;
    }

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

    public bool ShowInstallBlocker => !appState.Preflight.IsInstalled;

    public Visibility ShowInstallBlockerVisibility => ShowInstallBlocker ? Visibility.Visible : Visibility.Collapsed;

    public string CodexVersionText => appState.Preflight.IsInstalled
        ? $"Installed ({appState.Preflight.Version})"
        : "Not detected";

    public string LoginStatusText => "Handled by Codex CLI at runtime";

    public string BlockingTitle => "Codex CLI required";

    public string BlockingMessage => string.IsNullOrWhiteSpace(appState.Preflight.BlockingMessage)
        ? "Indolent needs a working Codex CLI install before the widget can answer."
        : appState.Preflight.BlockingMessage;

    public string LastAnswerSummary => appState.LastAnswerSummary;

    public string AvailableModelsSummary => accountVisibleModelCount > 0
        ? $"Showing {accountVisibleModelCount} Codex models visible to this install."
        : "No account-visible Codex models were found in the local Codex cache.";

    public string SelectedModelDescription => selectedModelOption?.Description switch
    {
        { Length: > 0 } description => description,
        _ when !string.IsNullOrWhiteSpace(SelectedModel) => "Current model is not in the visible Codex model list for this install.",
        _ => "Choose a model from the Codex models visible to this install."
    };

    public string SelectedModelSupportText => selectedModelOption is null
        ? string.Empty
        : selectedModelOption.SupportsReasoningSelection
            ? $"{selectedModelOption.SupportedReasoningLevels.Count} reasoning modes available."
            : "This model does not expose multiple reasoning modes.";

    public bool ShowReasoningSelector => ReasoningOptions.Count > 1;

    public Visibility ShowReasoningSelectorVisibility => ShowReasoningSelector ? Visibility.Visible : Visibility.Collapsed;

    public string SelectedReasoningDescription
        => ReasoningOptions.FirstOrDefault(option => string.Equals(option.Effort, SelectedReasoningEffort, StringComparison.OrdinalIgnoreCase))?.Description
            ?? "Reasoning effort is not configurable for the current model.";

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

    public Visibility ShowAgentLoopVisibility => AgentModeEnabled ? Visibility.Visible : Visibility.Collapsed;

    public async Task RefreshPreflightAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;

        try
        {
            var preflightTask = codexCliService.RunPreflightAsync();
            var modelsTask = modelCatalogService.LoadAvailableModelsAsync();
            await Task.WhenAll(preflightTask, modelsTask);

            var result = await preflightTask;
            appState.UpdatePreflight(result);
            LoadAvailableModels(await modelsTask);
            await appState.PersistAsync(settingsStore);
        }
        finally
        {
            IsRefreshing = false;
        }
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

    public void ToggleTerminalView()
        => IsTerminalViewOpen = !IsTerminalViewOpen;

    public void ClearTerminalTranscript()
    {
        codexCliService.ClearTerminalTranscript();
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
            await codexCliService.RunTerminalCommandAsync(trimmedArguments);
            OnPropertyChanged(nameof(TerminalTranscript));
        }
        finally
        {
            IsRunningTerminalCommand = false;
        }
    }

    private void SyncFromState()
    {
        selectedModel = appState.SelectedModel;
        selectedReasoningEffort = appState.SelectedReasoningEffort;
        UpdateSelectedModelState();
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedReasoningEffort));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanRunTerminalCommand));
        OnPropertyChanged(nameof(ShowInstallBlocker));
        OnPropertyChanged(nameof(ShowInstallBlockerVisibility));
        OnPropertyChanged(nameof(CodexVersionText));
        OnPropertyChanged(nameof(LoginStatusText));
        OnPropertyChanged(nameof(BlockingTitle));
        OnPropertyChanged(nameof(BlockingMessage));
        OnPropertyChanged(nameof(LastAnswerSummary));
        OnPropertyChanged(nameof(StartWithWidget));
        OnPropertyChanged(nameof(AgentModeEnabled));
        OnPropertyChanged(nameof(AgentLoopEnabled));
        OnPropertyChanged(nameof(ShowAgentLoopVisibility));
        OnPropertyChanged(nameof(TerminalTranscript));
    }

    private void LoadAvailableModels(IReadOnlyList<CodexModelOption> models)
    {
        accountVisibleModelCount = models.Count;
        var resolvedModels = models.ToList();

        if (!string.IsNullOrWhiteSpace(SelectedModel)
            && resolvedModels.All(model => !string.Equals(model.Slug, SelectedModel, StringComparison.OrdinalIgnoreCase)))
        {
            resolvedModels.Insert(0, new CodexModelOption
            {
                Slug = SelectedModel,
                DisplayName = SelectedModel,
                Description = "Current model is not in the visible Codex model list for this install.",
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
