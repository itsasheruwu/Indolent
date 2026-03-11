using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Indolent.Services;

public sealed class OpenCodeProviderRuntime(ILogger<OpenCodeProviderRuntime> logger) : IProviderRuntime
{
    private const int DefaultTimeoutSeconds = 60;
    private const int TerminalTimeoutSeconds = 120;
    private const string OllamaBaseUrl = "http://localhost:11434";
    private const string OpenCodeModelSlug = "ollama/gemma3:4b";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string WorkingDirectory = ResolveWorkingDirectory();
    private static readonly string LogsDirectoryPathValue = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Indolent",
        "logs",
        "opencode");
    private static readonly string ScreenshotStagingDirectory = Path.Combine(
        Path.GetTempPath(),
        "Indolent",
        "opencode-screenshots");
    private static ResolvedCommand? cachedCommand;
    private readonly Lock transcriptGate = new();
    private readonly string sessionLogPath = Path.Combine(LogsDirectoryPathValue, $"opencode-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
    private string terminalTranscript = "Shared Open Code transcript.\r\nThis view shows the actual Open Code commands the app runs.\r\n\r\n";

    public string ProviderId => ProviderIds.OpenCode;

    public string DisplayName => "Open Code";

    public string LogsDirectoryPath => LogsDirectoryPathValue;

    public string TerminalTranscript
    {
        get
        {
            lock (transcriptGate)
            {
                return terminalTranscript;
            }
        }
    }

    public event EventHandler? TerminalTranscriptChanged;

    public async Task<ProviderPreflightResult> RunPreflightAsync(CancellationToken cancellationToken = default)
    {
        ClearStaleStagedScreenshots();
        var command = await ResolveCommandAsync(cancellationToken);
        if (command is null)
        {
            return new ProviderPreflightResult
            {
                IsInstalled = false,
                BlockingMessage = "Install Open Code first. Indolent could not locate a runnable `opencode` command."
            };
        }

        var versionResult = await RunProcessAsync(command, "--version", 10, null, "preflight", cancellationToken);
        if (versionResult.ExitCode != 0)
        {
            logger.LogWarning("Open Code CLI not detected. stderr: {Error}", versionResult.StandardError);
            return new ProviderPreflightResult
            {
                IsInstalled = false,
                BlockingMessage = "Install Open Code first. Indolent blocks answering until `opencode` is available on PATH."
            };
        }

        var ollamaState = await ProbeOllamaAsync(cancellationToken);
        if (!ollamaState.IsReachable)
        {
            return new ProviderPreflightResult
            {
                IsInstalled = true,
                Version = versionResult.StandardOutput.Trim(),
                IsLoggedIn = false,
                BlockingMessage = "Open Code is installed, but Ollama is not reachable at `http://localhost:11434`."
            };
        }

        if (!ollamaState.HasGemma)
        {
            return new ProviderPreflightResult
            {
                IsInstalled = true,
                Version = versionResult.StandardOutput.Trim(),
                IsLoggedIn = false,
                BlockingMessage = "Ollama is reachable, but model `gemma3:4b` is not available."
            };
        }

        return new ProviderPreflightResult
        {
            IsInstalled = true,
            Version = versionResult.StandardOutput.Trim(),
            IsLoggedIn = true,
            BlockingMessage = string.Empty
        };
    }

    public Task<ProviderDefaults> ReadConfiguredDefaultsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderDefaults
        {
            SelectedModel = OpenCodeModelSlug,
            SelectedReasoningEffort = string.Empty
        });

    public Task<IReadOnlyList<ProviderModelOption>> LoadModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ProviderModelOption>>(
        [
            new ProviderModelOption
            {
                Slug = OpenCodeModelSlug,
                DisplayName = "Gemma 3",
                Description = "Ollama (local)",
                Visibility = "list",
                Priority = 0,
                DefaultReasoningLevel = string.Empty,
                SupportedReasoningLevels = []
            }
        ]);

    public async Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.StartNew();
        var command = await ResolveCommandAsync(cancellationToken);
        if (command is null)
        {
            return new AnswerResult
            {
                Status = AnswerStatus.Failed,
                ErrorMessage = "Open Code CLI is not available from the desktop app process.",
                Duration = startedAt.Elapsed
            };
        }

        string? stagedScreenshotPath = null;

        try
        {
            stagedScreenshotPath = StageScreenshot(request.ScreenshotPath);
            var arguments = new StringBuilder()
                .Append("run --format default ")
                .Append("--dir ").Append(Quote(WorkingDirectory)).Append(' ')
                .Append("--agent summary ")
                .Append("-m ").Append(Quote(request.Model)).Append(' ');

            if (!string.IsNullOrWhiteSpace(stagedScreenshotPath))
            {
                arguments.Append("-f ").Append(Quote(stagedScreenshotPath)).Append(' ');
            }

            arguments.Append("-- ").Append(Quote(BuildPrompt(request)));

            var result = await RunProcessAsync(
                command,
                arguments.ToString(),
                DefaultTimeoutSeconds,
                null,
                "answer",
                cancellationToken);

            if (result.TimedOut)
            {
                return new AnswerResult
                {
                    Status = AnswerStatus.Timeout,
                    ErrorMessage = "Open Code timed out before returning an answer.",
                    Duration = startedAt.Elapsed
                };
            }

            if (result.ExitCode != 0)
            {
                return new AnswerResult
                {
                    Status = AnswerStatus.Failed,
                    ErrorMessage = $"Open Code failed: {FirstNonEmpty(result.StandardError, result.StandardOutput, "Unknown CLI failure.")}",
                    Duration = startedAt.Elapsed
                };
            }

            var text = result.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new AnswerResult
                {
                    Status = AnswerStatus.Empty,
                    ErrorMessage = "Open Code returned an empty answer.",
                    Duration = startedAt.Elapsed
                };
            }

            AppendAssistantResponse(text);
            return new AnswerResult
            {
                Status = AnswerStatus.Success,
                Text = text,
                Duration = startedAt.Elapsed
            };
        }
        finally
        {
            TryDelete(stagedScreenshotPath);
        }
    }

    public async Task<TerminalCommandResult> RunTerminalCommandAsync(string arguments, CancellationToken cancellationToken = default)
    {
        var command = await ResolveCommandAsync(cancellationToken);
        if (command is null)
        {
            return new TerminalCommandResult
            {
                ExitCode = -1,
                StandardError = "Open Code CLI is not available from the desktop app process."
            };
        }

        var resolvedArguments = string.IsNullOrWhiteSpace(arguments) ? "--help" : arguments.Trim();
        var result = await RunProcessAsync(command, resolvedArguments, TerminalTimeoutSeconds, null, "terminal", cancellationToken);
        return new TerminalCommandResult
        {
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            TimedOut = result.TimedOut
        };
    }

    public void ClearTerminalTranscript()
    {
        lock (transcriptGate)
        {
            terminalTranscript = string.Empty;
        }

        TerminalTranscriptChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<ProcessResult> RunProcessAsync(
        ResolvedCommand command,
        string arguments,
        int timeoutSeconds,
        string? standardInput,
        string transcriptLabel,
        CancellationToken cancellationToken)
    {
        AppendTranscriptEntry(transcriptLabel, command, arguments, standardInput);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.BuildArguments(arguments),
                WorkingDirectory = WorkingDirectory,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.Environment["OPENCODE_CONFIG_CONTENT"] = BuildRuntimeConfig();

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            AppendProcessResult(string.Empty, ex.Message, -1, false);
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
            var standardOutput = await outputTask;
            var standardError = await errorTask;
            AppendProcessResult(standardOutput, standardError, process.ExitCode, false);
            return new ProcessResult(process.ExitCode, standardOutput, standardError, false);
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
            var standardOutput = await outputTask;
            var standardError = await errorTask;
            AppendProcessResult(standardOutput, standardError, -1, true);
            return new ProcessResult(-1, standardOutput, standardError, true);
        }
    }

    private static async Task WriteStandardInputAsync(Process process, string standardInput)
    {
        try
        {
            await using var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false), 1024, leaveOpen: true);
            await writer.WriteAsync(standardInput);
            await writer.FlushAsync();
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

    private void AppendTranscriptEntry(string transcriptLabel, ResolvedCommand command, string arguments, string? standardInput)
    {
        var builder = new StringBuilder()
            .Append('[')
            .Append(DateTimeOffset.Now.ToString("HH:mm:ss"))
            .Append("] ")
            .Append(transcriptLabel)
            .Append(": ")
            .Append(command.FileName);

        var resolvedArguments = command.BuildArguments(arguments);
        if (!string.IsNullOrWhiteSpace(resolvedArguments))
        {
            builder.Append(' ').Append(resolvedArguments);
        }

        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(standardInput))
        {
            builder.AppendLine("[stdin]");
            builder.AppendLine(standardInput.TrimEnd());
        }

        AppendTranscript(builder.ToString());
    }

    private void AppendProcessResult(string standardOutput, string standardError, int exitCode, bool timedOut)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            builder.AppendLine("[stdout]");
            builder.AppendLine(standardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            builder.AppendLine("[stderr]");
            builder.AppendLine(standardError.TrimEnd());
        }

        builder.Append('[')
            .Append(timedOut ? "timed out" : $"exit {exitCode}")
            .AppendLine("]")
            .AppendLine();

        AppendTranscript(builder.ToString());
    }

    private void AppendAssistantResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var builder = new StringBuilder()
            .AppendLine("[assistant]")
            .AppendLine(text.TrimEnd())
            .AppendLine();

        AppendTranscript(builder.ToString());
    }

    private void AppendTranscript(string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk))
        {
            return;
        }

        lock (transcriptGate)
        {
            terminalTranscript += chunk;
        }

        TryAppendLogChunk(chunk);
        TerminalTranscriptChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TryAppendLogChunk(string chunk)
    {
        try
        {
            Directory.CreateDirectory(LogsDirectoryPathValue);
            File.AppendAllText(sessionLogPath, chunk);
        }
        catch
        {
            // ignore best-effort log persistence failures
        }
    }

    private static string? StageScreenshot(string screenshotPath)
    {
        if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
        {
            return null;
        }

        Directory.CreateDirectory(ScreenshotStagingDirectory);
        var stagedPath = Path.Combine(ScreenshotStagingDirectory, $"indolent-opencode-{Guid.NewGuid():N}{Path.GetExtension(screenshotPath)}");
        File.Copy(screenshotPath, stagedPath, overwrite: false);
        return stagedPath;
    }

    private static void ClearStaleStagedScreenshots()
    {
        try
        {
            if (!Directory.Exists(ScreenshotStagingDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(ScreenshotStagingDirectory))
            {
                TryDelete(file);
            }
        }
        catch
        {
            // ignore temp cleanup failures
        }
    }

    private static async Task<OllamaProbeResult> ProbeOllamaAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync($"{OllamaBaseUrl}/api/tags", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new OllamaProbeResult(false, false);
            }

            var payload = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(SerializerOptions, cancellationToken);
            var hasGemma = payload?.Models?.Any(model => string.Equals(model.Name, "gemma3:4b", StringComparison.OrdinalIgnoreCase)) == true;
            return new OllamaProbeResult(true, hasGemma);
        }
        catch
        {
            return new OllamaProbeResult(false, false);
        }
    }

    private static string BuildRuntimeConfig()
        => """
{
  "$schema": "https://opencode.ai/config.json",
  "provider": {
    "ollama": {
      "name": "Ollama (local)",
      "npm": "@ai-sdk/openai-compatible",
      "models": {
        "gemma3:4b": {
          "name": "Gemma 3"
        }
      },
      "options": {
        "baseURL": "http://localhost:11434/v1"
      }
    }
  }
}
""";

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore temp cleanup failures
        }
    }

    private static async Task<ResolvedCommand?> ResolveCommandAsync(CancellationToken cancellationToken)
    {
        if (cachedCommand is not null && cachedCommand.Exists())
        {
            return cachedCommand;
        }

        var appDataCommand = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "opencode.cmd");

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
                Arguments = "opencode",
                WorkingDirectory = WorkingDirectory,
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
            return output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch
        {
            return [];
        }
    }

    private static string ResolveWorkingDirectory()
    {
        var currentDirectory = Environment.CurrentDirectory;
        return string.IsNullOrWhiteSpace(currentDirectory) ? AppContext.BaseDirectory : currentDirectory;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

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
                ".ps1" => new ResolvedCommand("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" {{0}}", path),
                _ when File.Exists(path) => ForCmdFile(path),
                _ => null
            };
        }
    }

    private sealed record OllamaProbeResult(bool IsReachable, bool HasGemma);

    private sealed class OllamaTagsResponse
    {
        public List<OllamaTagModel>? Models { get; init; }
    }

    private sealed class OllamaTagModel
    {
        public string? Name { get; init; }
    }
}

