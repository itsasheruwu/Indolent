using System.Text.Json;
using System.Text.Json.Serialization;

namespace Indolent.Services;

public sealed class CodexModelCatalogService : ICodexModelCatalogService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "models_cache.json");

    public async Task<IReadOnlyList<CodexModelOption>> LoadAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(cachePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(cachePath);
        var cache = await JsonSerializer.DeserializeAsync<ModelsCacheDto>(stream, SerializerOptions, cancellationToken);

        return cache?.Models?
            .Where(model =>
                string.Equals(model.ShellType, "shell_command", StringComparison.OrdinalIgnoreCase)
                && string.Equals(model.Visibility, "list", StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.Priority)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(model => new CodexModelOption
            {
                Slug = model.Slug ?? string.Empty,
                DisplayName = model.DisplayName ?? model.Slug ?? string.Empty,
                Description = model.Description ?? string.Empty,
                Visibility = model.Visibility ?? string.Empty,
                Priority = model.Priority,
                DefaultReasoningLevel = model.DefaultReasoningLevel ?? string.Empty,
                SupportedReasoningLevels = (model.SupportedReasoningLevels ?? [])
                    .Select(level => new ReasoningLevelOption
                    {
                        Effort = level.Effort ?? string.Empty,
                        Description = level.Description ?? string.Empty
                    })
                    .Where(level => !string.IsNullOrWhiteSpace(level.Effort))
                    .ToArray()
            })
            .Where(model => !string.IsNullOrWhiteSpace(model.Slug))
            .ToArray()
            ?? [];
    }

    private sealed class ModelsCacheDto
    {
        [JsonPropertyName("models")]
        public List<ModelDto>? Models { get; init; }
    }

    private sealed class ModelDto
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; init; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("visibility")]
        public string? Visibility { get; init; }

        [JsonPropertyName("priority")]
        public int Priority { get; init; }

        [JsonPropertyName("shell_type")]
        public string? ShellType { get; init; }

        [JsonPropertyName("default_reasoning_level")]
        public string? DefaultReasoningLevel { get; init; }

        [JsonPropertyName("supported_reasoning_levels")]
        public List<ReasoningLevelDto>? SupportedReasoningLevels { get; init; }
    }

    private sealed class ReasoningLevelDto
    {
        [JsonPropertyName("effort")]
        public string? Effort { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }
}
