namespace Indolent.Services;

public interface IOcrService
{
    Task<OcrLayoutResult> ExtractLayoutAsync(string imagePath, CancellationToken cancellationToken = default);

    Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken = default);
}
