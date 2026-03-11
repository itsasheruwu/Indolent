namespace Indolent.Services;

public interface IProviderRuntimeRegistry
{
    IReadOnlyList<ProviderOption> Providers { get; }

    bool IsKnownProvider(string? providerId);

    IProviderRuntime GetProvider(string? providerId);
}
