namespace Indolent.Models;

public sealed class OpenCodeSetupResult
{
    public bool IsSuccess { get; init; }

    public string Summary { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;
}
