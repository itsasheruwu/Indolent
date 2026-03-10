namespace Indolent.Services;

public interface ICodexModelCatalogService
{
    Task<IReadOnlyList<CodexModelOption>> LoadAvailableModelsAsync(CancellationToken cancellationToken = default);
}
