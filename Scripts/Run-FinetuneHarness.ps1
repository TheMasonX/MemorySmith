[CmdletBinding()]
param(
    [string]$RunId = (Get-Date -Format "yyyyMMdd-HHmmss"),
    [string]$WorkRoot,
    [string]$ExportPath = "Data/Training/exports",
    [string]$TranscriptDirectory = "Data/Events/chat-transcripts",
    [string]$PythonVenvPath,
    [string]$ScratchRoot,
    [ValidateSet("auto", "simulated", "lora", "infer")]
    [string]$TrainMode = "auto",
    [string]$ModelId = "Qwen/Qwen3.5-4B",
    [string]$AdapterPath,
    [switch]$RequireTrainingDependencies,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

function Test-IsWindowsPlatform {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
}

$isWindowsPlatform = Test-IsWindowsPlatform

function Resolve-WorkflowPath {
    param([Parameter(Mandatory = $true)][string]$PathValue)

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        throw "Path value must not be empty."
    }

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PathValue))
}

function Get-DefaultScratchRoot {
    if ($isWindowsPlatform -and (Test-Path "D:\temp")) {
        return "D:\temp\memorysmith-training"
    }

    return (Join-Path $repoRoot "artifacts\training-scratch")
}

function Get-DefaultTrainingVenvPath {
    $preferredRoots = @()
    if ($isWindowsPlatform -and (Test-Path "D:\temp")) {
        $preferredRoots += "D:\temp\memorysmith-training\.venv"
    }

    $preferredRoots += @(
        (Join-Path $repoRoot ".venv-training"),
        (Join-Path $repoRoot ".venv")
    )

    foreach ($candidate in $preferredRoots) {
        $windowsPython = Join-Path $candidate "Scripts\python.exe"
        $unixPython = Join-Path $candidate "bin/python"
        if ((Test-Path $windowsPython) -or (Test-Path $unixPython)) {
            return $candidate
        }
    }

    if ($isWindowsPlatform -and (Test-Path "D:\temp")) {
        return "D:\temp\memorysmith-training\.venv"
    }

    return (Join-Path $repoRoot ".venv")
}

function Resolve-PythonExecutable {
    param([Parameter(Mandatory = $true)][string]$VenvRoot)

    $windowsPython = Join-Path $VenvRoot "Scripts\python.exe"
    if ((Test-Path $windowsPython) -or $isWindowsPlatform) {
        return $windowsPython
    }

    return (Join-Path $VenvRoot "bin/python")
}

function Initialize-TrainingScratchEnvironment {
    param([Parameter(Mandatory = $true)][string]$Root)

    $resolvedRoot = Resolve-WorkflowPath $Root
    $hfHome = Join-Path $resolvedRoot "hf-home"
    $hfHubCache = Join-Path $hfHome "hub"
    $datasetsCache = Join-Path $hfHome "datasets"
    $torchHome = Join-Path $resolvedRoot "torch-home"
    $tempDirectory = Join-Path $resolvedRoot "temp"

    foreach ($directory in @($resolvedRoot, $hfHome, $hfHubCache, $datasetsCache, $torchHome, $tempDirectory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $env:HF_HOME = $hfHome
    $env:HF_HUB_CACHE = $hfHubCache
    $env:TRANSFORMERS_CACHE = $hfHubCache
    $env:HF_DATASETS_CACHE = $datasetsCache
    $env:TORCH_HOME = $torchHome
    $env:TMP = $tempDirectory
    $env:TEMP = $tempDirectory
    # Prefer plain HTTP transfers for large model shards on local Windows hosts.
    if ([string]::IsNullOrWhiteSpace($env:HF_HUB_DISABLE_XET)) {
        $env:HF_HUB_DISABLE_XET = "1"
    }
    if ([string]::IsNullOrWhiteSpace($env:HF_HUB_ENABLE_HF_TRANSFER)) {
        $env:HF_HUB_ENABLE_HF_TRANSFER = "0"
    }
}

function Write-Utf8NoBomFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $directory = Split-Path $Path -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

if ([string]::IsNullOrWhiteSpace($ScratchRoot)) {
    $ScratchRoot = Get-DefaultScratchRoot
}

if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path $ScratchRoot "runs"
}

if ([string]::IsNullOrWhiteSpace($PythonVenvPath)) {
    $PythonVenvPath = Get-DefaultTrainingVenvPath
}

Initialize-TrainingScratchEnvironment -Root $ScratchRoot

$resolvedWorkRoot = Resolve-WorkflowPath $WorkRoot
$workDir = Join-Path $resolvedWorkRoot $RunId
$requestPath = Join-Path $workDir "request.json"
$resolvedVenvPath = Resolve-WorkflowPath $PythonVenvPath
$pythonExe = Resolve-PythonExecutable $resolvedVenvPath
$harnessPath = Join-Path $repoRoot "MemorySmith.Training/harness.py"
$preflightScript = Join-Path $repoRoot "Scripts/Test-FinetuneHarnessPrereqs.ps1"

if (-not (Test-Path $pythonExe)) {
    throw "Python executable not found at $pythonExe"
}
if (-not (Test-Path $harnessPath)) {
    throw "Harness script not found at $harnessPath"
}
if (-not (Test-Path $preflightScript)) {
    throw "Preflight script not found at $preflightScript"
}

$preflightJson = & $preflightScript -PythonVenvPath $resolvedVenvPath -AsJson
if ($LASTEXITCODE -ne 0) {
    throw "Training dependency preflight execution failed"
}
$preflight = $preflightJson | ConvertFrom-Json
$enforceDependencyReadiness = $RequireTrainingDependencies -and -not [string]::Equals($TrainMode, "simulated", [System.StringComparison]::OrdinalIgnoreCase)
if ($enforceDependencyReadiness -and -not $preflight.ready) {
    $missingText = if ($preflight.missing.Count -gt 0) { $preflight.missing -join ', ' } else { 'none' }
    throw "Training dependency preflight not ready for TrainMode '$TrainMode'. Missing: $missingText. Accelerator: $($preflight.accelerator)."
}
if ($RequireTrainingDependencies -and -not $enforceDependencyReadiness -and -not $preflight.ready) {
    Write-Warning "RequireTrainingDependencies was set but TrainMode '$TrainMode' allows simulated execution; continuing despite preflight readiness=false."
}
if (-not $preflight.ready) {
    $missingText = if ($preflight.missing.Count -gt 0) { $preflight.missing -join ', ' } else { 'none' }
    $optionalText = if ($preflight.optionalMissing.Count -gt 0) { $preflight.optionalMissing -join ', ' } else { 'none' }
    Write-Warning "Training preflight not ready for TrainMode '$TrainMode'. Harness run will execute in simulated mode. Missing: $missingText. Optional missing: $optionalText. Accelerator: $($preflight.accelerator)."
}

New-Item -ItemType Directory -Path $workDir -Force | Out-Null
$resolvedExportPath = Resolve-WorkflowPath $ExportPath
New-Item -ItemType Directory -Path $resolvedExportPath -Force | Out-Null

$request = [ordered]@{
    runId = $RunId
    trainMode = $TrainMode
    modelId = $ModelId
    exportPath = $resolvedExportPath
    transcriptDirectory = (Resolve-WorkflowPath $TranscriptDirectory)
    format = "FilteredSft"
    dependencyProbe = [ordered]@{
        python = $preflight.python
        ready = $preflight.ready
        missing = @($preflight.missing)
        optionalMissing = @($preflight.optionalMissing)
        acceleratorReady = $preflight.cudaAvailable
        accelerator = $preflight.accelerator
        error = $preflight.torchError
    }
    hyperparameters = [ordered]@{
        epochs = 1
        learningRate = 0.0002
        sequenceLength = 512
    }
}

# Add adapterPath if provided (for inference mode)
if (-not [string]::IsNullOrWhiteSpace($AdapterPath)) {
    $request["adapterPath"] = (Resolve-WorkflowPath $AdapterPath)
}

$requestJson = $request | ConvertTo-Json -Depth 8
Write-Utf8NoBomFile -Path $requestPath -Content $requestJson

$processArguments = @(
    $harnessPath,
    "--run-id", $RunId,
    "--request", $requestPath,
    "--workdir", $workDir
)
if ($DryRun) {
    $processArguments += "--dry-run"
}

Write-Host "Starting harness run $RunId"
& $pythonExe @processArguments
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    throw "Harness failed with exit code $exitCode"
}

$statusPath = Join-Path $workDir "status.json"
$benchmarkPath = Join-Path $workDir "benchmark.json"
$eventsPath = Join-Path $workDir "events.jsonl"

Write-Host "Run completed."
Write-Host "Status:    $statusPath"
Write-Host "Events:    $eventsPath"
Write-Host "Benchmark: $benchmarkPath"
Write-Host "Scratch:   $(Resolve-WorkflowPath $ScratchRoot)"
