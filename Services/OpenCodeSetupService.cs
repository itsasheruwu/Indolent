using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Indolent.Services;

public sealed class OpenCodeSetupService(ILogger<OpenCodeSetupService> logger) : IOpenCodeSetupService
{
    private const string OpenCodeNpmPackage = "opencode-ai";
    private const string OpenCodeModelName = "gemma3:4b";
    private const string OllamaBaseUrl = "http://localhost:11434";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string WorkingDirectory = ResolveWorkingDirectory();

    public async Task<OpenCodeSetupResult> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var detail = new StringBuilder();

        try
        {
            progress?.Report("Checking Open Code...");
            detail.AppendLine("Checking Open Code...");
            if (!await IsCommandAvailableAsync("opencode", KnownOpenCodePaths(), cancellationToken))
            {
                progress?.Report("Installing Open Code...");
                detail.AppendLine("Installing Open Code with npm...");
                var installOpenCode = await RunCommandAsync(
                    await ResolveCommandAsync("npm", KnownNpmPaths(), cancellationToken),
                    $"install -g {OpenCodeNpmPackage}",
                    timeoutSeconds: 180,
                    cancellationToken);

                AppendProcessDetail(detail, installOpenCode);

                if (installOpenCode.ExitCode != 0 || !await IsCommandAvailableAsync("opencode", KnownOpenCodePaths(), cancellationToken))
                {
                    return Fail(
                        "Open Code could not be installed automatically.",
                        detail,
                        "Indolent tried `npm install -g opencode-ai`. Install Node.js/npm or finish the Open Code install manually.");
                }
            }

            progress?.Report("Checking Ollama...");
            detail.AppendLine("Checking Ollama...");
            var ollamaReachable = await IsOllamaReachableAsync(cancellationToken);
            var ollamaCommand = await ResolveCommandAsync("ollama", KnownOllamaPaths(), cancellationToken);

            if (!ollamaReachable && ollamaCommand is null)
            {
                progress?.Report("Installing Ollama...");
                detail.AppendLine("Installing Ollama...");
                var installOllama = await RunCommandAsync(
                    ResolvedCommand.ForPowerShell(),
                    "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://ollama.com/install.ps1 | iex\"",
                    timeoutSeconds: 300,
                    cancellationToken);

                AppendProcessDetail(detail, installOllama);

                ollamaCommand = await ResolveCommandAsync("ollama", KnownOllamaPaths(), cancellationToken);
                if (ollamaCommand is null)
                {
                    return Fail(
                        "Ollama could not be installed automatically.",
                        detail,
                        "Indolent ran the official Ollama Windows installer script, but `ollama` is still not available.");
                }
            }

            if (!await IsOllamaReachableAsync(cancellationToken))
            {
                ollamaCommand ??= await ResolveCommandAsync("ollama", KnownOllamaPaths(), cancellationToken);
                if (ollamaCommand is not null)
                {
                    progress?.Report("Starting Ollama...");
                    detail.AppendLine("Starting Ollama...");
                    RunBackgroundProcess(ollamaCommand, "serve");
                    if (!await WaitForOllamaAsync(cancellationToken))
                    {
                        return Fail(
                            "Ollama is installed, but Indolent could not start the local API automatically.",
                            detail,
                            "Try starting Ollama once from the Start menu, then re-run setup.");
                    }
                }
            }

            if (!await IsOllamaReachableAsync(cancellationToken))
            {
                return Fail(
                    "Ollama is not reachable.",
                    detail,
                    "The local API at `http://localhost:11434` never became available.");
            }

            progress?.Report("Checking Gemma 3...");
            detail.AppendLine("Checking Gemma 3...");
            if (!await HasModelAsync(OpenCodeModelName, cancellationToken))
            {
                progress?.Report("Downloading Gemma 3...");
                detail.AppendLine("Pulling gemma3:4b...");
                var pullResponse = await HttpClient.PostAsJsonAsync(
                    $"{OllamaBaseUrl}/api/pull",
                    new { model = OpenCodeModelName, stream = false },
                    SerializerOptions,
                    cancellationToken);
                var pullBody = await pullResponse.Content.ReadAsStringAsync(cancellationToken);
                detail.AppendLine(pullBody.Trim());

                if (!pullResponse.IsSuccessStatusCode || !await HasModelAsync(OpenCodeModelName, cancellationToken))
                {
                    return Fail(
                        "Gemma 3 could not be downloaded automatically.",
                        detail,
                        "Ollama responded, but `gemma3:4b` is still not installed.");
                }
            }

            progress?.Report("Open Code setup is ready.");
            detail.AppendLine("Open Code setup completed successfully.");
            return new OpenCodeSetupResult
            {
                IsSuccess = true,
                Summary = "Open Code, Ollama, and Gemma 3 are ready.",
                Detail = detail.ToString().Trim()
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Open Code guided setup failed.");
            detail.AppendLine(ex.ToString());
            return new OpenCodeSetupResult
            {
                IsSuccess = false,
                Summary = "Open Code setup failed.",
                Detail = detail.ToString().Trim()
            };
        }
    }

    private static void AppendProcessDetail(StringBuilder detail, ProcessResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            detail.AppendLine(result.StandardOutput.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            detail.AppendLine(result.StandardError.Trim());
        }
    }

    private static async Task<bool> IsCommandAvailableAsync(string commandName, IEnumerable<string> knownPaths, CancellationToken cancellationToken)
        => await ResolveCommandAsync(commandName, knownPaths, cancellationToken) is not null;

    private static async Task<ResolvedCommand?> ResolveCommandAsync(string commandName, IEnumerable<string> knownPaths, CancellationToken cancellationToken)
    {
        foreach (var knownPath in knownPaths)
        {
            if (File.Exists(knownPath))
            {
                return ResolvedCommand.FromPath(knownPath);
            }
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = commandName,
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
            var path = output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(path) ? null : ResolvedCommand.FromPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static Task<bool> WaitForOllamaAsync(CancellationToken cancellationToken)
        => PollAsync(() => IsOllamaReachableAsync(cancellationToken), TimeSpan.FromSeconds(1), 20, cancellationToken);

    private static async Task<bool> PollAsync(Func<Task<bool>> operation, TimeSpan delay, int attempts, CancellationToken cancellationToken)
    {
        for (var index = 0; index < attempts; index++)
        {
            if (await operation())
            {
                return true;
            }

            await Task.Delay(delay, cancellationToken);
        }

        return false;
    }

    private static async Task<bool> IsOllamaReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync($"{OllamaBaseUrl}/api/tags", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HasModelAsync(string modelName, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync($"{OllamaBaseUrl}/api/tags", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(SerializerOptions, cancellationToken);
            return payload?.Models?.Any(model => string.Equals(model.Name, modelName, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    private static void RunBackgroundProcess(ResolvedCommand command, string arguments)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command.FileName,
                    Arguments = command.BuildArguments(arguments),
                    WorkingDirectory = WorkingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
        }
        catch
        {
            // best effort only
        }
    }

    private static async Task<ProcessResult> RunCommandAsync(
        ResolvedCommand? command,
        string arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (command is null)
        {
            return new ProcessResult(-1, string.Empty, "Required command is not available.", false);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.BuildArguments(arguments),
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
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
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

            return new ProcessResult(-1, await outputTask, await errorTask, true);
        }
    }

    private static OpenCodeSetupResult Fail(string summary, StringBuilder detail, string fallbackLine)
    {
        detail.AppendLine(fallbackLine);
        return new OpenCodeSetupResult
        {
            IsSuccess = false,
            Summary = summary,
            Detail = detail.ToString().Trim()
        };
    }

    private static string ResolveWorkingDirectory()
    {
        var currentDirectory = Environment.CurrentDirectory;
        return string.IsNullOrWhiteSpace(currentDirectory) ? AppContext.BaseDirectory : currentDirectory;
    }

    private static IEnumerable<string> KnownOpenCodePaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "opencode.cmd");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "opencode");
    }

    private static IEnumerable<string> KnownNpmPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "npm.cmd");
    }

    private static IEnumerable<string> KnownOllamaPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

    private sealed record ResolvedCommand(string FileName, string ArgumentsPrefix, string? ProbePath = null)
    {
        public string BuildArguments(string arguments)
            => string.IsNullOrWhiteSpace(ArgumentsPrefix) ? arguments : string.Format(ArgumentsPrefix, arguments);

        public static ResolvedCommand? FromPath(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.ToLowerInvariant() switch
            {
                ".cmd" or ".bat" => new ResolvedCommand("cmd.exe", $"/d /s /c \"\"{path}\" {{0}}\"", path),
                ".exe" => new ResolvedCommand(path, string.Empty, path),
                ".ps1" => new ResolvedCommand("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" {{0}}", path),
                _ when File.Exists(path) => new ResolvedCommand("cmd.exe", $"/d /s /c \"\"{path}\" {{0}}\"", path),
                _ => null
            };
        }

        public static ResolvedCommand ForPowerShell()
            => new("powershell.exe", "{0}");
    }

    private sealed class OllamaTagsResponse
    {
        public List<OllamaTagModel>? Models { get; init; }
    }

    private sealed class OllamaTagModel
    {
        public string? Name { get; init; }
    }
}
