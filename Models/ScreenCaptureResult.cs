using System.Drawing;

namespace Indolent.Models;

public sealed class ScreenCaptureResult
{
    public required string ImagePath { get; init; }

    public required Rectangle Bounds { get; init; }
}
