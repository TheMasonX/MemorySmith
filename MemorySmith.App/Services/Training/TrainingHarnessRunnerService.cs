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

public sealed record TrainingDependencyProbeResult(
    bool Ready,
    string PythonVersion,
    IReadOnlyList<string> MissingModules,
    IReadOnlyList<string> OptionalMissingModules,
    bool AcceleratorReady,
    string? Accelerator,
    string? Error)
{
    public string Summary => !string.IsNullOrWhiteSpace(Error)
        ? Error
        : !AcceleratorReady
            ? $"Simulated mode: accelerator unavailable ({PythonVersion}{FormatAcceleratorSuffix()})"
            : MissingModules.Count > 0
                ? $"Simulated mode: missing {string.Join(", ", MissingModules)} ({PythonVersion}{FormatAcceleratorSuffix()})"
                : OptionalMissingModules.Count > 0
                    ? $"Ready without optional {string.Join(", ", OptionalMissingModules)} ({PythonVersion}{FormatAcceleratorSuffix()})"
                    : $"Ready ({PythonVersion}{FormatAcceleratorSuffix()})";

    private string FormatAcceleratorSuffix() => string.IsNullOrWhiteSpace(Accelerator)
        ? string.Empty
        : $"; {Accelerator}";
}

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

    public async Task<TrainingDependencyProbeResult> ProbeDependenciesAsync(CancellationToken cancellationToken)
    {
        var pythonExecutable = ResolvePythonExecutable(_options.CurrentValue.Training.PythonVenvPath);
        if (!File.Exists(pythonExecutable))
        {
            return new TrainingDependencyProbeResult(false, "-", ["python"], [], false, null, $"Python executable not found: {pythonExecutable}");
        }

        const string probeScript = "import importlib.util, json, platform; required=['torch','transformers','datasets','trl','peft']; optional=['unsloth']; missing_required=[name for name in required if importlib.util.find_spec(name) is None]; missing_optional=[name for name in optional if importlib.util.find_spec(name) is None]; cuda_available=False; cuda_version=None; device_name=None; accelerator='cpu-only'; torch_error=None; exec(\"try:\\n import torch\\n cuda_available = bool(torch.cuda.is_available())\\n cuda_version = getattr(torch.version, 'cuda', None)\\n device_name = torch.cuda.get_device_name(0) if cuda_available and torch.cuda.device_count() > 0 else None\\n accelerator = device_name or ('cuda ' + str(cuda_version) if cuda_version else 'cpu-only')\\nexcept Exception as ex:\\n torch_error = str(ex)\\n\", globals(), locals()); ready=(len(missing_required)==0 and cuda_available and torch_error is None); print(json.dumps({'python': platform.python_version(), 'missing': missing_required, 'optionalMissing': missing_optional, 'cudaAvailable': cuda_available, 'cudaVersion': cuda_version, 'deviceName': device_name, 'accelerator': accelerator, 'torchError': torch_error, 'ready': ready}))";
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(probeScript);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                return new TrainingDependencyProbeResult(false, "-", [], [], false, null, string.IsNullOrWhiteSpace(stderr) ? "Training dependency probe failed." : stderr.Trim());
            }

            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var pythonVersion = root.TryGetProperty("python", out var pythonElement) && pythonElement.ValueKind == JsonValueKind.String
                ? pythonElement.GetString() ?? "-"
                : "-";
            var ready = root.TryGetProperty("ready", out var readyElement) && readyElement.ValueKind == JsonValueKind.True;
            var missing = root.TryGetProperty("missing", out var missingElement) && missingElement.ValueKind == JsonValueKind.Array
                ? missingElement.EnumerateArray().Select(element => element.GetString() ?? string.Empty).Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
                : [];
            var optionalMissing = root.TryGetProperty("optionalMissing", out var optionalMissingElement) && optionalMissingElement.ValueKind == JsonValueKind.Array
                ? optionalMissingElement.EnumerateArray().Select(element => element.GetString() ?? string.Empty).Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
                : [];
            var acceleratorReady = root.TryGetProperty("cudaAvailable", out var acceleratorElement) && acceleratorElement.ValueKind == JsonValueKind.True;
            var accelerator = root.TryGetProperty("accelerator", out var acceleratorElement2) && acceleratorElement2.ValueKind == JsonValueKind.String
                ? acceleratorElement2.GetString()
                : null;
            var torchError = root.TryGetProperty("torchError", out var torchErrorElement) && torchErrorElement.ValueKind == JsonValueKind.String
                ? torchErrorElement.GetString()
                : null;

            return new TrainingDependencyProbeResult(ready, pythonVersion, missing, optionalMissing, acceleratorReady, accelerator, string.IsNullOrWhiteSpace(torchError) ? null : torchError);
        }
        catch (Exception ex)
        {
            return new TrainingDependencyProbeResult(false, "-", [], [], false, null, ex.Message);
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

        var pythonExecutable = ResolvePythonExecutable(appOptions.Training.PythonVenvPath);
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

        var dependencyProbe = await ProbeDependenciesAsync(cancellationToken);

        var requestPath = Path.Combine(workDirectory, "request.json");
        var huggingFaceToken = ResolveEnvironmentSecret(appOptions.Training.HuggingFaceTokenEnvironmentVariable);
        var request = new
        {
            runId,
            trainMode = "auto",
            exportPath = exportDirectory,
            transcriptDirectory = ResolvePath(appOptions.Training.TranscriptDirectory),
            format = appOptions.Training.PreferenceFormat.ToString(),
            activeModelTag = appOptions.Training.ActiveModelTag,
            fallbackModelTag = appOptions.Training.FallbackModelTag,
            maxRunMinutes = appOptions.Training.MaxRunMinutes,
            dependencyProbe = new
            {
                python = dependencyProbe.PythonVersion,
                ready = dependencyProbe.Ready,
                missing = dependencyProbe.MissingModules,
                optionalMissing = dependencyProbe.OptionalMissingModules,
                acceleratorReady = dependencyProbe.AcceleratorReady,
                accelerator = dependencyProbe.Accelerator,
                error = dependencyProbe.Error
            }
        };

        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), cancellationToken);

        var active = new TrainingHarnessActiveRun(runId, dryRun, DateTime.UtcNow, workDirectory, null, null, true);
        lock (_gate)
        {
            _activeRun = active;
        }

        _ = Task.Run(() => RunHarnessAsync(active, pythonExecutable, harnessScript, requestPath, appOptions.Training.MaxRunMinutes, huggingFaceToken), CancellationToken.None);
        var modeSuffix = dependencyProbe.Ready ? string.Empty : $" {dependencyProbe.Summary}";
        return new TrainingHarnessLaunchResult(true, runId, $"Started run {runId}.{modeSuffix}");
    }

    private async Task RunHarnessAsync(TrainingHarnessActiveRun run, string pythonExecutable, string harnessScript, string requestPath, int maxRunMinutes, string? huggingFaceToken)
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
        if (!startInfo.Environment.ContainsKey("HF_HUB_DISABLE_XET"))
        {
            startInfo.Environment["HF_HUB_DISABLE_XET"] = "1";
        }

        if (!startInfo.Environment.ContainsKey("HF_HUB_ENABLE_HF_TRANSFER"))
        {
            startInfo.Environment["HF_HUB_ENABLE_HF_TRANSFER"] = "0";
        }

        if (!string.IsNullOrWhiteSpace(huggingFaceToken))
        {
            startInfo.Environment["HF_TOKEN"] = huggingFaceToken;
            startInfo.Environment["HUGGING_FACE_HUB_TOKEN"] = huggingFaceToken;
        }

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

    private static string? ResolveEnvironmentSecret(string environmentVariableName)
    {
        if (string.IsNullOrWhiteSpace(environmentVariableName))
        {
            return null;
        }

        var value = Environment.GetEnvironmentVariable(environmentVariableName.Trim());
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private string ResolvePythonExecutable(string configuredVenvPath)
    {
        var venvRoot = ResolvePath(configuredVenvPath);
        var windowsPython = Path.Combine(venvRoot, "Scripts", "python.exe");
        if (File.Exists(windowsPython) || OperatingSystem.IsWindows())
        {
            return windowsPython;
        }

        return Path.Combine(venvRoot, "bin", "python");
    }

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
