namespace Indolent.Services;

public interface IScreenCaptureService
{
    Task<ScreenCaptureResult> CaptureDisplayUnderCursorAsync(CancellationToken cancellationToken = default);
}
