namespace Indolent.Services;

public interface IAgentClickService
{
    Task<AgentClickResult> TryClickAnswerAsync(
        string answerText,
        ScreenCaptureResult capture,
        OcrLayoutResult ocrLayout,
        string model,
        string reasoningEffort,
        CancellationToken cancellationToken = default);
}
