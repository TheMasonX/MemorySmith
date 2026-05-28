[CmdletBinding()]
param(
    [string]$PythonVenvPath = ".venv",
    [switch]$AsJson,
    [switch]$FailOnMissing
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$pythonExe = Join-Path $repoRoot (Join-Path $PythonVenvPath "Scripts/python.exe")
if (-not (Test-Path $pythonExe)) {
    throw "Python executable not found at $pythonExe"
}

$probe = @"
import importlib.util
import json
import platform
modules = ["torch", "transformers", "datasets", "trl", "peft", "unsloth"]
missing = [name for name in modules if importlib.util.find_spec(name) is None]
payload = {
  "python": platform.python_version(),
  "missing": missing,
  "ready": len(missing) == 0
}
print(json.dumps(payload))
"@

$result = & $pythonExe -c $probe
if ($LASTEXITCODE -ne 0) {
    throw "Failed to probe Python dependencies"
}

$payload = $result | ConvertFrom-Json

if ($AsJson) {
    $payload | ConvertTo-Json -Depth 6
}
else {
    Write-Host "Python version: $($payload.python)"
    if ($payload.ready) {
        Write-Host "Training dependencies: ready"
    }
    else {
        Write-Host "Training dependencies missing: $($payload.missing -join ', ')"
    }
}

if ($FailOnMissing -and -not $payload.ready) {
    throw "Training dependency preflight failed"
}
