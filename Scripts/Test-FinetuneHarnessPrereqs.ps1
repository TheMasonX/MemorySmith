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
required_modules = ['torch', 'transformers', 'datasets', 'trl', 'peft']
optional_modules = ['unsloth']
missing_required = [name for name in required_modules if importlib.util.find_spec(name) is None]
missing_optional = [name for name in optional_modules if importlib.util.find_spec(name) is None]
cuda_available = False
cuda_version = None
device_name = None
accelerator = 'cpu-only'
torch_error = None
try:
    import torch
    cuda_available = bool(torch.cuda.is_available())
    cuda_version = getattr(torch.version, 'cuda', None)
    device_name = torch.cuda.get_device_name(0) if cuda_available and torch.cuda.device_count() > 0 else None
    accelerator = device_name or (f'cuda {cuda_version}' if cuda_version else 'cpu-only')
except Exception as ex:
    if 'torch' not in missing_required:
        torch_error = str(ex)

ready = len(missing_required) == 0 and cuda_available and not torch_error
payload = {
    'python': platform.python_version(),
    'missing': missing_required,
    'optionalMissing': missing_optional,
    'cudaAvailable': cuda_available,
    'cudaVersion': cuda_version,
    'deviceName': device_name,
    'accelerator': accelerator,
    'torchError': torch_error,
    'ready': ready
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
        Write-Host "Training dependencies: ready ($($payload.accelerator))"
    }
    else {
        if ($payload.missing.Count -gt 0) {
            Write-Host "Training dependencies missing: $($payload.missing -join ', ')"
        }
        else {
            Write-Host "Training accelerator unavailable: $($payload.accelerator)"
        }

        if ($payload.optionalMissing.Count -gt 0) {
            Write-Host "Optional training extras missing: $($payload.optionalMissing -join ', ')"
        }

        if (-not [string]::IsNullOrWhiteSpace($payload.torchError)) {
            Write-Host "Torch probe warning: $($payload.torchError)"
        }
    }
}

if ($FailOnMissing -and -not $payload.ready) {
    throw "Training dependency preflight failed"
}
