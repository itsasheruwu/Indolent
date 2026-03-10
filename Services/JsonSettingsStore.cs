using System.Text.Json;

namespace Indolent.Services;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Indolent",
        "settings.json");

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(settingsPath))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(settingsPath);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions) ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(settingsPath)!;
        Directory.CreateDirectory(directory);

        await using var stream = File.Create(settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions);
    }
}
