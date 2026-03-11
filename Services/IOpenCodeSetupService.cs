namespace Indolent.Services;

public interface IOpenCodeSetupService
{
    Task<OpenCodeSetupResult> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
