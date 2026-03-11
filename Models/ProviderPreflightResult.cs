namespace Indolent.Models;

public sealed class ProviderPreflightResult
{
    public bool IsInstalled { get; init; }

    public string Version { get; init; } = string.Empty;

    public bool IsLoggedIn { get; init; }

    public string BlockingMessage { get; init; } = string.Empty;

    public bool IsReady => IsInstalled && IsLoggedIn;
}
