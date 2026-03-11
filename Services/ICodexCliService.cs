namespace Indolent.Services;

public interface ICodexCliService
{
    string TerminalTranscript { get; }

    string LogsDirectoryPath { get; }

    event EventHandler? TerminalTranscriptChanged;

    Task<CodexPreflightResult> RunPreflightAsync(CancellationToken cancellationToken = default);

    Task<string?> ReadConfiguredModelAsync(CancellationToken cancellationToken = default);

    Task<string?> ReadConfiguredReasoningEffortAsync(CancellationToken cancellationToken = default);

    Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default);

    Task<TerminalCommandResult> RunTerminalCommandAsync(string arguments, CancellationToken cancellationToken = default);

    void ClearTerminalTranscript();
}
