using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services.Training;

public sealed record TrainingHarnessActiveRun(
    string RunId,
    bool DryRun,
    DateTime StartedAtUtc,
    string WorkDirectory,
    int? ExitCode,
    string? LastError,
    bool IsRunning);

public sealed record TrainingHarnessLaunchResult(bool Started, string RunId, string Message);

public sealed class TrainingHarnessRunnerService
{
    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<TrainingHarnessRunnerService> _logger;
    private readonly object _gate = new();
    private TrainingHarnessActiveRun? _activeRun;

    public TrainingHarnessRunnerService(
        IOptionsMonitor<MemorySmithOptions> options,
        IWebHostEnvironment environment,
        ILogger<TrainingHarnessRunnerService> logger)
    {
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    public TrainingHarnessActiveRun? GetActiveRun()
    {
        lock (_gate)
        {
            return _activeRun;
        }
    }

    public async Task<TrainingHarnessLaunchResult> StartRunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        TrainingHarnessActiveRun? current;
        lock (_gate)
        {
            current = _activeRun;
            if (current?.IsRunning == true)
            {
                return new TrainingHarnessLaunchResult(false, current.RunId, $"Run {current.RunId} is already in progress.");
            }
        }

        var appOptions = _options.CurrentValue;
        var runId = $"ui-ft-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var runsDirectory = ResolvePath(appOptions.Training.RunsDirectory);
        Directory.CreateDirectory(runsDirectory);
        var workDirectory = Path.Combine(runsDirectory, runId);
        Directory.CreateDirectory(workDirectory);

        var pythonExecutable = ResolvePath(Path.Combine(appOptions.Training.PythonVenvPath, "Scripts", "python.exe"));
        var harnessScript = ResolvePath(appOptions.Training.PythonHarnessScript);
        var exportDirectory = ResolvePath(appOptions.Training.TrainingDataExportPath);
        Directory.CreateDirectory(exportDirectory);

        if (!File.Exists(pythonExecutable))
        {
            return new TrainingHarnessLaunchResult(false, runId, $"Python executable not found: {pythonExecutable}");
        }

        if (!File.Exists(harnessScript))
        {
            return new TrainingHarnessLaunchResult(false, runId, $"Harness script not found: {harnessScript}");
        }

        var requestPath = Path.Combine(workDirectory, "request.json");
        var request = new
        {
            runId,
            exportPath = exportDirectory,
            transcriptDirectory = ResolvePath(appOptions.Training.TranscriptDirectory),
            format = appOptions.Training.PreferenceFormat.ToString(),
            activeModelTag = appOptions.Training.ActiveModelTag,
            fallbackModelTag = appOptions.Training.FallbackModelTag,
            maxRunMinutes = appOptions.Training.MaxRunMinutes
        };

        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), cancellationToken);

        var active = new TrainingHarnessActiveRun(runId, dryRun, DateTime.UtcNow, workDirectory, null, null, true);
        lock (_gate)
        {
            _activeRun = active;
        }

        _ = Task.Run(() => RunHarnessAsync(active, pythonExecutable, harnessScript, requestPath, appOptions.Training.MaxRunMinutes), CancellationToken.None);
        return new TrainingHarnessLaunchResult(true, runId, $"Started run {runId}.");
    }

    private async Task RunHarnessAsync(TrainingHarnessActiveRun run, string pythonExecutable, string harnessScript, string requestPath, int maxRunMinutes)
    {
        var timeout = TimeSpan.FromMinutes(Math.Clamp(maxRunMinutes, 1, 1440));
        using var timeoutCts = new CancellationTokenSource(timeout);

        var arguments = new List<string>
        {
            Quote(harnessScript),
            "--run-id", Quote(run.RunId),
            "--request", Quote(requestPath),
            "--workdir", Quote(run.WorkDirectory)
        };
        if (run.DryRun)
        {
            arguments.Add("--dry-run");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            Arguments = string.Join(" ", arguments),
            WorkingDirectory = ResolveRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            var exitCode = process.ExitCode;
            var stdErr = await stdErrTask;
            if (!string.IsNullOrWhiteSpace(stdErr))
            {
                _logger.LogWarning("Training harness stderr for run {RunId}: {StdErr}", run.RunId, stdErr);
            }

            SetCompletedRun(run, exitCode, exitCode == 0 ? null : $"Harness exited with code {exitCode}.");
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore process kill failures.
            }

            SetCompletedRun(run, null, $"Harness run exceeded timeout of {timeout.TotalMinutes:0} minute(s).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Training harness run {RunId} failed unexpectedly.", run.RunId);
            SetCompletedRun(run, null, ex.Message);
        }
        finally
        {
            _ = await stdOutTask;
            _ = await stdErrTask;
        }
    }

    private void SetCompletedRun(TrainingHarnessActiveRun run, int? exitCode, string? lastError)
    {
        lock (_gate)
        {
            if (_activeRun is null || !string.Equals(_activeRun.RunId, run.RunId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _activeRun = _activeRun with
            {
                ExitCode = exitCode,
                LastError = lastError,
                IsRunning = false
            };
        }
    }

    private string ResolveRepositoryRoot() =>
        Path.GetFullPath(Path.Combine(_environment.ContentRootPath, ".."));

    private string ResolvePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var repositoryRootCandidate = Path.GetFullPath(Path.Combine(ResolveRepositoryRoot(), configuredPath));
        if (File.Exists(repositoryRootCandidate) || Directory.Exists(repositoryRootCandidate) || configuredPath.StartsWith(".", StringComparison.Ordinal))
        {
            return repositoryRootCandidate;
        }

        return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredPath));
    }

    private static string Quote(string value) =>
        string.IsNullOrWhiteSpace(value) ? "\"\"" : $"\"{value.Replace("\"", "\\\"")}\"";
}
