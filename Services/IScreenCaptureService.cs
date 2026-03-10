namespace Indolent.Services;

public interface IScreenCaptureService
{
    Task<string> CaptureDisplayUnderCursorAsync(CancellationToken cancellationToken = default);
}
