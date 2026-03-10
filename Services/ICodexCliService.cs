namespace Indolent.Services;

public interface ICodexCliService
{
    Task<CodexPreflightResult> RunPreflightAsync(CancellationToken cancellationToken = default);

    Task<string?> ReadConfiguredModelAsync(CancellationToken cancellationToken = default);

    Task<string?> ReadConfiguredReasoningEffortAsync(CancellationToken cancellationToken = default);

    Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default);
}
