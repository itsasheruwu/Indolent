namespace Indolent.Models;

public static class ProviderIds
{
    public const string OpenAiCodex = "openai-codex";
    public const string OpenCode = "open-code";

    public static IReadOnlyList<string> All { get; } = [OpenAiCodex, OpenCode];

    public static string Normalize(string? providerId)
        => All.Contains(providerId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            ? All.First(id => string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase))
            : OpenAiCodex;
}
