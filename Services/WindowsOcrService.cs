using Microsoft.Extensions.Logging;

using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace Indolent.Services;

public sealed class WindowsOcrService(ILogger<WindowsOcrService> logger) : IOcrService
{
    public async Task<OcrLayoutResult> ExtractLayoutAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            throw new InvalidOperationException("Windows OCR is unavailable for the current user profile languages.");
        }

        var file = await StorageFile.GetFileFromPathAsync(imagePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        cancellationToken.ThrowIfCancellationRequested();

        var result = await engine.RecognizeAsync(bitmap);
        var text = result.Text?.Trim() ?? string.Empty;
        var lines = result.Lines?
            .Select(line => new OcrTextRegion
            {
                Text = line.Text?.Trim() ?? string.Empty,
                Bounds = ToRectangle(line.Words.Select(word => word.BoundingRect).ToArray())
            })
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray()
            ?? [];
        var words = result.Lines?
            .SelectMany(line => line.Words)
            .Select(word => new OcrTextRegion
            {
                Text = word.Text?.Trim() ?? string.Empty,
                Bounds = ToRectangle(word.BoundingRect)
            })
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .ToArray()
            ?? [];

        logger.LogInformation("OCR extracted {CharacterCount} characters and {LineCount} lines from {Path}", text.Length, lines.Length, imagePath);
        return new OcrLayoutResult
        {
            Text = text,
            Lines = lines,
            Words = words
        };
    }

    public async Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken = default)
        => (await ExtractLayoutAsync(imagePath, cancellationToken)).Text;

    private static System.Drawing.Rectangle ToRectangle(Windows.Foundation.Rect rect)
        => new(
            (int)Math.Round(rect.X),
            (int)Math.Round(rect.Y),
            Math.Max(1, (int)Math.Round(rect.Width)),
            Math.Max(1, (int)Math.Round(rect.Height)));

    private static System.Drawing.Rectangle ToRectangle(IReadOnlyList<Windows.Foundation.Rect> rects)
    {
        if (rects.Count == 0)
        {
            return System.Drawing.Rectangle.Empty;
        }

        var left = rects.Min(rect => rect.X);
        var top = rects.Min(rect => rect.Y);
        var right = rects.Max(rect => rect.X + rect.Width);
        var bottom = rects.Max(rect => rect.Y + rect.Height);

        return new System.Drawing.Rectangle(
            (int)Math.Round(left),
            (int)Math.Round(top),
            Math.Max(1, (int)Math.Round(right - left)),
            Math.Max(1, (int)Math.Round(bottom - top)));
    }
}
