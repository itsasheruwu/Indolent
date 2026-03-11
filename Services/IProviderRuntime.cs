namespace Indolent.Services;

public interface IProviderRuntime
{
    string ProviderId { get; }

    string DisplayName { get; }

    string LogsDirectoryPath { get; }

    string TerminalTranscript { get; }

    event EventHandler? TerminalTranscriptChanged;

    Task<ProviderPreflightResult> RunPreflightAsync(CancellationToken cancellationToken = default);

    Task<ProviderDefaults> ReadConfiguredDefaultsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderModelOption>> LoadModelsAsync(CancellationToken cancellationToken = default);

    Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default);

    Task<TerminalCommandResult> RunTerminalCommandAsync(string arguments, CancellationToken cancellationToken = default);

    void ClearTerminalTranscript();
}
