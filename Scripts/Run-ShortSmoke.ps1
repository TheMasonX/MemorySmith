<#
Runs a short 30-optimizer-step smoke run against the canonical-only export.
Adjust `$SyntheticDataPath` or other params as needed.
#>
param(
    [string]$RunId = (Get-Date -Format "yyyyMMdd-HHmmss"),
    [string]$SyntheticDataPath = "Data/Training/exports/canonical-only-20260601.sft.jsonl",
    [int]$Epochs = 1,
    [int]$GradientAccumulationSteps = 4,
    [int]$MaxTrainSteps = 30,
    [switch]$TrustRemoteCode
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
$launcher = Join-Path $repoRoot "Scripts\Run-FinetuneHarness.ps1"

if (-not (Test-Path $launcher)) {
    throw "Launcher not found: $launcher"
}

Write-Host "Starting short smoke run: $RunId (MaxTrainSteps=$MaxTrainSteps)"

# Build launcher argument array so switch parameters are passed correctly
$params = @(
    "-RunId", $RunId,
    "-SyntheticDataPath", $SyntheticDataPath,
    "-Epochs", $Epochs.ToString(),
    "-GradientAccumulationSteps", $GradientAccumulationSteps.ToString(),
    "-MaxTrainSteps", $MaxTrainSteps.ToString()
)
if ($TrustRemoteCode) { $params += "-TrustRemoteCode" }

& $launcher @params

Write-Host "Short smoke run requested. Check runs directory for artifacts."