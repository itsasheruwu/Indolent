namespace Indolent.Services;

public sealed class ProviderRuntimeRegistry(IEnumerable<IProviderRuntime> runtimes) : IProviderRuntimeRegistry
{
    private readonly Dictionary<string, IProviderRuntime> runtimesById = runtimes.ToDictionary(
        runtime => runtime.ProviderId,
        runtime => runtime,
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ProviderOption> Providers { get; } = runtimes
        .Select(runtime => new ProviderOption
        {
            Id = runtime.ProviderId,
            DisplayName = runtime.DisplayName
        })
        .ToArray();

    public bool IsKnownProvider(string? providerId)
        => !string.IsNullOrWhiteSpace(providerId) && runtimesById.ContainsKey(providerId);

    public IProviderRuntime GetProvider(string? providerId)
    {
        var normalized = ProviderIds.Normalize(providerId);
        return runtimesById.TryGetValue(normalized, out var runtime)
            ? runtime
            : runtimesById[ProviderIds.OpenAiCodex];
    }
}
