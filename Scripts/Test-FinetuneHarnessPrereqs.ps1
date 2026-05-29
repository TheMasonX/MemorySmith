[CmdletBinding()]
param(
    [string]$PythonVenvPath,
    [switch]$AsJson,
    [switch]$FailOnMissing
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

if ([string]::IsNullOrWhiteSpace($PythonVenvPath)) {
    $PythonVenvPath = Get-DefaultTrainingVenvPath
}

$resolvedVenvPath = Resolve-WorkflowPath $PythonVenvPath
$pythonExe = Resolve-PythonExecutable $resolvedVenvPath
if (-not (Test-Path $pythonExe)) {
    throw "Python executable not found at $pythonExe"
}

$probe = @'
import importlib.util
import json
import platform
modules = ['torch', 'transformers', 'datasets', 'trl', 'peft', 'unsloth']
missing = [name for name in modules if importlib.util.find_spec(name) is None]
payload = {
    'python': platform.python_version(),
    'missing': missing,
    'ready': len(missing) == 0
}
print(json.dumps(payload))
'@

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
