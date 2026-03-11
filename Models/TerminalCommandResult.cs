namespace Indolent.Models;

public sealed class TerminalCommandResult
{
    public int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public bool TimedOut { get; init; }

    public bool IsSuccess => !TimedOut && ExitCode == 0;
}
