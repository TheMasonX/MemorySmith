<#
.SYNOPSIS
Creates a repo-local .venv, installs model export dependencies, downloads a Hugging Face model snapshot, and exports/copies ONNX assets into Data/Models.

.EXAMPLE
./Scripts/Install-CodeSearchModel.ps1

.EXAMPLE
./Scripts/Install-CodeSearchModel.ps1 -ModelId nomic-ai/nomic-embed-text-v1.5 -OutputName nomic-embed-text-v1.5.onnx
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ModelId = "nomic-ai/nomic-embed-code",
    [string]$VenvPath = ".venv-model-export",
    [string]$ModelsDir = "Data/Models",
    [string]$CacheDir = ".cache/hf-model-export",
    [string]$OutputName = "",
    [switch]$DownloadOnly,
    [switch]$ForceRedownload,
    [switch]$TrustRemoteCode,
    [string]$Task = "feature-extraction",
    [int]$Opset = 17,
    [switch]$RecreateVenv
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent

function Resolve-WorkflowPath {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $PathValue))
}

$resolvedVenvPath = Resolve-WorkflowPath -PathValue $VenvPath -BasePath $repoRoot
$resolvedModelsDir = Resolve-WorkflowPath -PathValue $ModelsDir -BasePath $repoRoot
$resolvedCacheDir = Resolve-WorkflowPath -PathValue $CacheDir -BasePath $repoRoot
$requirementsPath = Join-Path $repoRoot "Scripts\model-tools\requirements-model-export.txt"
$exportScriptPath = Join-Path $repoRoot "Scripts\model-tools\export_hf_embedding_model.py"

if ([string]::IsNullOrWhiteSpace($OutputName) -and $ModelId -eq "nomic-ai/nomic-embed-code") {
    $OutputName = "nomic-embed-code.onnx"
}

function Resolve-PythonLauncher {
    if (Get-Command py -ErrorAction SilentlyContinue) {
        try {
            & py -3.11 -c "import sys; print(sys.executable)" *> $null
            if ($LASTEXITCODE -eq 0) {
                return @{ Exe = "py"; Args = @("-3.11") }
            }
        }
        catch {
        }
    }

    if (Get-Command python -ErrorAction SilentlyContinue) {
        return @{ Exe = "python"; Args = @() }
    }

    throw "No Python launcher found. Install Python 3.11+ and retry."
}

if (-not (Test-Path $requirementsPath)) {
    throw "Requirements file not found: $requirementsPath"
}

if (-not (Test-Path $exportScriptPath)) {
    throw "Export script not found: $exportScriptPath"
}

if ($RecreateVenv -and (Test-Path $resolvedVenvPath)) {
    if ($PSCmdlet.ShouldProcess($resolvedVenvPath, "Remove existing virtual environment")) {
        Write-Host "Removing existing virtual environment: $resolvedVenvPath"
        Remove-Item -Recurse -Force $resolvedVenvPath
    }
}

$launcher = Resolve-PythonLauncher
$launcherExe = [string]$launcher.Exe
$launcherArgs = [string[]]$launcher.Args

if (-not (Test-Path $resolvedVenvPath)) {
    if ($PSCmdlet.ShouldProcess($resolvedVenvPath, "Create virtual environment")) {
        Write-Host "Creating virtual environment at: $resolvedVenvPath"
        & $launcherExe @launcherArgs -m venv $resolvedVenvPath
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create virtual environment at $resolvedVenvPath"
        }
    }
}

$venvPython = Join-Path $resolvedVenvPath "Scripts\python.exe"
if (-not (Test-Path $venvPython)) {
    if ($WhatIfPreference) {
        Write-Host "WhatIf mode: skipping pip/install/export steps because virtual environment is not materialized."
        return
    }

    throw "Virtual environment python was not found: $venvPython"
}

Write-Host "Upgrading pip/setuptools/wheel in venv"
if ($PSCmdlet.ShouldProcess($venvPython, "Upgrade pip toolchain in virtual environment")) {
    & $venvPython -m pip install --upgrade pip setuptools wheel
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to upgrade pip toolchain"
    }
}

Write-Host "Installing model export requirements from $requirementsPath"
if ($PSCmdlet.ShouldProcess($requirementsPath, "Install model export requirements")) {
    & $venvPython -m pip install -r $requirementsPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install export requirements"
    }
}

if (-not (Test-Path $resolvedModelsDir)) {
    New-Item -ItemType Directory -Path $resolvedModelsDir -Force | Out-Null
}

$exportArgs = @(
    $exportScriptPath,
    "--model-id", $ModelId,
    "--models-dir", $resolvedModelsDir,
    "--cache-dir", $resolvedCacheDir,
    "--task", $Task,
    "--opset", $Opset.ToString()
)

if (-not [string]::IsNullOrWhiteSpace($OutputName)) {
    $exportArgs += @("--output-name", $OutputName)
}

if ($DownloadOnly) {
    $exportArgs += "--download-only"
}

if ($ForceRedownload) {
    $exportArgs += "--force-redownload"
}

if ($TrustRemoteCode) {
    $exportArgs += "--trust-remote-code"
}

Write-Host "Running model download/export workflow"
if ($PSCmdlet.ShouldProcess($resolvedModelsDir, "Download/export model artifacts into Data/Models")) {
    & $venvPython @exportArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Model export script failed"
    }
}

Write-Host "Model workflow completed. Artifacts are under: $resolvedModelsDir"
