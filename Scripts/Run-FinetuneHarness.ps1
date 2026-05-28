[CmdletBinding()]
param(
    [string]$RunId = (Get-Date -Format "yyyyMMdd-HHmmss"),
    [string]$WorkRoot = "runs",
    [string]$ExportPath = "Data/Training/exports",
    [string]$TranscriptDirectory = "Data/Events/chat-transcripts",
    [string]$PythonVenvPath = ".venv",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$workDir = Join-Path $repoRoot (Join-Path $WorkRoot $RunId)
$requestPath = Join-Path $workDir "request.json"
$pythonExe = Join-Path $repoRoot (Join-Path $PythonVenvPath "Scripts/python.exe")
$harnessPath = Join-Path $repoRoot "MemorySmith.Training/harness.py"

if (-not (Test-Path $pythonExe)) {
    throw "Python executable not found at $pythonExe"
}
if (-not (Test-Path $harnessPath)) {
    throw "Harness script not found at $harnessPath"
}

New-Item -ItemType Directory -Path $workDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $repoRoot $ExportPath) -Force | Out-Null

$request = [ordered]@{
    runId = $RunId
    exportPath = (Resolve-Path (Join-Path $repoRoot $ExportPath)).Path
    transcriptDirectory = (Join-Path $repoRoot $TranscriptDirectory)
    format = "FilteredSft"
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
