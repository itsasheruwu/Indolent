using Microsoft.Extensions.Logging;

using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace Indolent.Services;

public sealed class WindowsOcrService(ILogger<WindowsOcrService> logger) : IOcrService
{
    public async Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken = default)
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

        logger.LogInformation("OCR extracted {CharacterCount} characters from {Path}", text.Length, imagePath);
        return text;
    }
}
