namespace Indolent.Services;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync();

    Task SaveAsync(AppSettings settings);
}
