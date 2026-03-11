using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

using Indolent.Helpers;

using Microsoft.Extensions.Logging;

namespace Indolent.Services;

public sealed class ScreenCaptureService(ILogger<ScreenCaptureService> logger) : IScreenCaptureService
{
    public Task<ScreenCaptureResult> CaptureDisplayUnderCursorAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        NativeMethods.GetCursorPos(out var cursor);
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new InvalidOperationException("Unable to determine the monitor under the cursor.");
        }

        var bounds = new Rectangle(
            monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Top,
            monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top);
        var imagePath = Path.Combine(Path.GetTempPath(), $"indolent-capture-{Guid.NewGuid():N}.png");

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bitmap.Save(imagePath, ImageFormat.Png);

        logger.LogInformation("Captured screen bounds {Bounds} to {Path}", bounds, imagePath);
        return Task.FromResult(new ScreenCaptureResult
        {
            ImagePath = imagePath,
            Bounds = bounds
        });
    }
}
