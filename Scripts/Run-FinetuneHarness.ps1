[CmdletBinding()]
param(
    [string]$RunId = (Get-Date -Format "yyyyMMdd-HHmmss"),
    [string]$WorkRoot = "runs",
    [string]$ExportPath = "Data/Training/exports",
    [string]$TranscriptDirectory = "Data/Events/chat-transcripts",
    [string]$PythonVenvPath = ".venv",
    [switch]$RequireTrainingDependencies,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$workDir = Join-Path $repoRoot (Join-Path $WorkRoot $RunId)
$requestPath = Join-Path $workDir "request.json"
$pythonExe = Join-Path $repoRoot (Join-Path $PythonVenvPath "Scripts/python.exe")
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

$preflightJson = & $preflightScript -PythonVenvPath $PythonVenvPath -AsJson
if ($LASTEXITCODE -ne 0) {
    throw "Training dependency preflight execution failed"
}
$preflight = $preflightJson | ConvertFrom-Json
if ($RequireTrainingDependencies -and -not $preflight.ready) {
    throw "Training dependencies missing: $($preflight.missing -join ', ')"
}
if (-not $preflight.ready) {
    Write-Warning "Training dependencies missing. Harness run will execute in simulated mode. Missing: $($preflight.missing -join ', ')"
}

New-Item -ItemType Directory -Path $workDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $repoRoot $ExportPath) -Force | Out-Null

$request = [ordered]@{
    runId = $RunId
    exportPath = (Resolve-Path (Join-Path $repoRoot $ExportPath)).Path
    transcriptDirectory = (Join-Path $repoRoot $TranscriptDirectory)
    format = "FilteredSft"
    dependencyProbe = [ordered]@{
        python = $preflight.python
        ready = $preflight.ready
        missing = @($preflight.missing)
    }
    hyperparameters = [ordered]@{
        epochs = 3
        learningRate = 0.0002
        sequenceLength = 4096
    }
}
$request | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $requestPath

$args = @(
    $harnessPath,
    "--run-id", $RunId,
    "--request", $requestPath,
    "--workdir", $workDir
)
if ($DryRun) {
    $args += "--dry-run"
}

Write-Host "Starting harness run $RunId"
& $pythonExe @args
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
