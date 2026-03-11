using System.Drawing;

namespace Indolent.Models;

public sealed class OcrLayoutResult
{
    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<OcrTextRegion> Lines { get; init; } = [];

    public IReadOnlyList<OcrTextRegion> Words { get; init; } = [];
}

public sealed class OcrTextRegion
{
    public string Text { get; init; } = string.Empty;

    public Rectangle Bounds { get; init; }
}
