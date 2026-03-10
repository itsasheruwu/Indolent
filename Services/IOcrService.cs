namespace Indolent.Services;

public interface IOcrService
{
    Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken = default);
}
