using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

namespace Indolent.Services;

public sealed partial class CodexCliService(ILogger<CodexCliService> logger) : ICodexCliService
{
    private const int DefaultTimeoutSeconds = 60;
    private static readonly Regex ModelRegex = ConfigModelRegex();
    private static readonly Regex ReasoningEffortRegex = ConfigReasoningEffortRegex();
    private static ResolvedCommand? cachedCommand;
    private static readonly string workingDirectory = ResolveWorkingDirectory();

    public async Task<CodexPreflightResult> RunPreflightAsync(CancellationToken cancellationToken = default)
    {
        var command = await ResolveCommandAsync(cancellationToken);

        if (command is null)
        {
            return new CodexPreflightResult
            {
                IsInstalled = false,
                BlockingMessage = "Install Codex CLI first. Indolent could not locate a runnable Codex command."
            };
        }

        var versionResult = await RunProcessAsync(command, "-V", 10, null, cancellationToken);

        if (versionResult.ExitCode != 0)
        {
            logger.LogWarning("Codex CLI not detected. stderr: {Error}", versionResult.StandardError);
            return new CodexPreflightResult
            {
                IsInstalled = false,
                BlockingMessage = "Install Codex CLI first. Indolent blocks answering until `codex` is available on PATH."
            };
        }

        return new CodexPreflightResult
        {
            IsInstalled = true,
            Version = versionResult.StandardOutput.Trim(),
            IsLoggedIn = true,
            BlockingMessage = string.Empty
        };
    }

    public async Task<string?> ReadConfiguredModelAsync(CancellationToken cancellationToken = default)
    {
        var content = await ReadConfigAsync(cancellationToken);
        if (content is null)
        {
            return null;
        }

        return ModelRegex.Match(content) is { Success: true } match
            ? match.Groups["model"].Value
            : null;
    }

    public async Task<string?> ReadConfiguredReasoningEffortAsync(CancellationToken cancellationToken = default)
    {
        var content = await ReadConfigAsync(cancellationToken);
        if (content is null)
        {
            return null;
        }

        return ReasoningEffortRegex.Match(content) is { Success: true } match
            ? match.Groups["effort"].Value
            : null;
    }

    public async Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"indolent-{Guid.NewGuid():N}.txt");
        var startedAt = Stopwatch.StartNew();
        var command = await ResolveCommandAsync(cancellationToken);

        try
        {
            if (command is null)
            {
                return new AnswerResult
                {
                    Status = AnswerStatus.Failed,
                    ErrorMessage = "Codex CLI is not available from the desktop app process.",
                    Duration = startedAt.Elapsed
                };
            }

            var arguments = new StringBuilder()
                .Append("-C ").Append(Quote(workingDirectory)).Append(' ')
                .Append("exec --skip-git-repo-check --ephemeral --color never ")
                .Append("--output-last-message ").Append(Quote(outputPath)).Append(' ')
                .Append("-m ").Append(Quote(request.Model)).Append(' ')
                .Append(FormatImageArguments(request.ScreenshotPath))
                .Append(FormatConfigOverride("model_reasoning_effort", request.ReasoningEffort))
                .Append('-')
                .ToString();

            var result = await RunProcessAsync(
                command,
                arguments,
                DefaultTimeoutSeconds,
                BuildPrompt(request),
                cancellationToken);

            if (result.TimedOut)
            {
                return new AnswerResult
                {
                    Status = AnswerStatus.Timeout,
                    ErrorMessage = "Codex timed out before returning an answer.",
                    Duration = startedAt.Elapsed
                };
            }

            if (result.ExitCode != 0)
            {
                return new AnswerResult
                {
                    Status = AnswerStatus.Failed,
                    ErrorMessage = $"Codex failed: {FirstNonEmpty(result.StandardError, result.StandardOutput, "Unknown CLI failure.")}",
                    Duration = startedAt.Elapsed
                };
            }

            if (!File.Exists(outputPath))
            {
                return new AnswerResult
                {
                    Status = AnswerStatus.Failed,
                    ErrorMessage = "Codex did not return an output message file.",
                    Duration = startedAt.Elapsed
                };
            }

            var text = (await File.ReadAllTextAsync(outputPath, cancellationToken)).Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                return new AnswerResult
                {
                    Status = AnswerStatus.Empty,
                    ErrorMessage = "Codex returned an empty answer.",
                    Duration = startedAt.Elapsed
                };
            }

            return new AnswerResult
            {
                Status = AnswerStatus.Success,
                Text = text,
                Duration = startedAt.Elapsed
            };
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ResolvedCommand command,
        string arguments,
        int timeoutSeconds,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.BuildArguments(arguments),
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false);
        }

        var inputTask = standardInput is null
            ? Task.CompletedTask
            : WriteStandardInputAsync(process, standardInput);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await inputTask;
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore best-effort cleanup
            }

            await inputTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return new ProcessResult(-1, await outputTask, await errorTask, true);
        }
    }

    private static string BuildPrompt(AnswerRequest request)
    {
        var builder = new StringBuilder(request.Prompt.Length + request.ScreenText.Length + 96)
            .AppendLine(request.Prompt);

        if (!string.IsNullOrWhiteSpace(request.ScreenText))
        {
            builder
                .AppendLine()
                .AppendLine("OCR text:")
                .AppendLine(request.ScreenText);
        }

        return builder.ToString();
    }

    private static async Task WriteStandardInputAsync(Process process, string standardInput)
    {
        try
        {
            await process.StandardInput.WriteAsync(standardInput);
            await process.StandardInput.FlushAsync();
        }
        catch
        {
            // ignore stdin failures and let process exit handling surface the real error
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // ignore best-effort cleanup
            }
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string FormatConfigOverride(string key, string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : $"-c {Quote($"{key}=\"{value}\"")} ";

    private static string FormatImageArguments(string screenshotPath)
        => string.IsNullOrWhiteSpace(screenshotPath)
            ? string.Empty
            : $"--image {Quote(screenshotPath)} ";

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static async Task<string?> ReadConfigAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "config.toml");

        if (!File.Exists(configPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(configPath, cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore temp cleanup failures
        }
    }

    [GeneratedRegex("^model\\s*=\\s*\"(?<model>[^\"]+)\"", RegexOptions.Multiline)]
    private static partial Regex ConfigModelRegex();

    [GeneratedRegex("^model_reasoning_effort\\s*=\\s*\"(?<effort>[^\"]+)\"", RegexOptions.Multiline)]
    private static partial Regex ConfigReasoningEffortRegex();

    private static async Task<ResolvedCommand?> ResolveCommandAsync(CancellationToken cancellationToken)
    {
        if (cachedCommand is not null && cachedCommand.Exists())
        {
            return cachedCommand;
        }

        var appDataCommand = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "codex.cmd");

        if (File.Exists(appDataCommand))
        {
            cachedCommand = ResolvedCommand.ForCmdFile(appDataCommand);
            return cachedCommand;
        }

        var whereResult = await RunWhereAsync(cancellationToken);
        foreach (var candidate in whereResult)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var resolved = ResolvedCommand.FromPath(candidate.Trim());
            if (resolved is not null)
            {
                cachedCommand = resolved;
                return cachedCommand;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> RunWhereAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "codex",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch
        {
            return [];
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

    private static string ResolveWorkingDirectory()
    {
        var currentDirectory = Environment.CurrentDirectory;
        return string.IsNullOrWhiteSpace(currentDirectory) ? AppContext.BaseDirectory : currentDirectory;
    }

    private sealed record ResolvedCommand(string FileName, string ArgumentsPrefix, string? ProbePath = null)
    {
        public string BuildArguments(string arguments)
            => string.IsNullOrWhiteSpace(ArgumentsPrefix) ? arguments : string.Format(ArgumentsPrefix, arguments);

        public bool Exists() => ProbePath is null || File.Exists(ProbePath);

        public static ResolvedCommand ForCmdFile(string path)
            => new("cmd.exe", $"/d /s /c \"\"{path}\" {{0}}\"", path);

        public static ResolvedCommand? FromPath(string path)
        {
            var extension = Path.GetExtension(path);

            return extension.ToLowerInvariant() switch
            {
                ".cmd" or ".bat" => ForCmdFile(path),
                ".exe" => new ResolvedCommand(path, string.Empty, path),
                ".ps1" => new ResolvedCommand(
                    "powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" {{0}}",
                    path),
                _ when File.Exists(path) => ForCmdFile(path),
                _ => null
            };
        }
    }
}
