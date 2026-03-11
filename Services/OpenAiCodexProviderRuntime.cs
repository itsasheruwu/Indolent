namespace Indolent.Services;

public sealed class OpenAiCodexProviderRuntime(
    ICodexCliService codexCliService,
    ICodexModelCatalogService modelCatalogService) : IProviderRuntime
{
    public string ProviderId => ProviderIds.OpenAiCodex;

    public string DisplayName => "OpenAI Codex";

    public string LogsDirectoryPath => codexCliService.LogsDirectoryPath;

    public string TerminalTranscript => codexCliService.TerminalTranscript;

    public event EventHandler? TerminalTranscriptChanged
    {
        add => codexCliService.TerminalTranscriptChanged += value;
        remove => codexCliService.TerminalTranscriptChanged -= value;
    }

    public async Task<ProviderPreflightResult> RunPreflightAsync(CancellationToken cancellationToken = default)
    {
        var result = await codexCliService.RunPreflightAsync(cancellationToken);
        return new ProviderPreflightResult
        {
            IsInstalled = result.IsInstalled,
            Version = result.Version,
            IsLoggedIn = result.IsLoggedIn,
            BlockingMessage = result.BlockingMessage
        };
    }

    public async Task<ProviderDefaults> ReadConfiguredDefaultsAsync(CancellationToken cancellationToken = default)
        => new()
        {
            SelectedModel = await codexCliService.ReadConfiguredModelAsync(cancellationToken) ?? string.Empty,
            SelectedReasoningEffort = await codexCliService.ReadConfiguredReasoningEffortAsync(cancellationToken) ?? string.Empty
        };

    public async Task<IReadOnlyList<ProviderModelOption>> LoadModelsAsync(CancellationToken cancellationToken = default)
        => (await modelCatalogService.LoadAvailableModelsAsync(cancellationToken))
            .Select(model => new ProviderModelOption
            {
                Slug = model.Slug,
                DisplayName = model.DisplayName,
                Description = model.Description,
                Visibility = model.Visibility,
                Priority = model.Priority,
                DefaultReasoningLevel = model.DefaultReasoningLevel,
                SupportedReasoningLevels = model.SupportedReasoningLevels
            })
            .ToArray();

    public Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default)
        => codexCliService.AnswerAsync(request, cancellationToken);

    public Task<TerminalCommandResult> RunTerminalCommandAsync(string arguments, CancellationToken cancellationToken = default)
        => codexCliService.RunTerminalCommandAsync(arguments, cancellationToken);

    public void ClearTerminalTranscript() => codexCliService.ClearTerminalTranscript();
}
