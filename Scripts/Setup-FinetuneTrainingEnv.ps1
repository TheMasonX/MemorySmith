[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$PythonVersion = "3.12",
    [string]$FallbackPythonVersion = "3.11",
    [string]$ScratchRoot,
    [string]$VenvPath,
    [string]$OverridePath = "artifacts/MemorySmith.App/appsettings.LocalOverrides.json",
    [ValidateSet("cu128", "cpu")]
    [string]$TorchFlavor = "cu128",
    [switch]$InstallPythonIfMissing,
    [switch]$PersistUserEnvironment,
    [switch]$RecreateVenv,
    [switch]$SkipDependencyInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
$requirementsPath = Join-Path $repoRoot "Scripts\training\requirements-training.txt"
$preflightScript = Join-Path $repoRoot "Scripts\Test-FinetuneHarnessPrereqs.ps1"

function Test-IsWindowsPlatform {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
}

function ConvertTo-HashtableCompat {
    param([Parameter(Mandatory = $false)]$InputObject)

    if ($null -eq $InputObject) {
        return @{}
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        $dictionary = @{}
        foreach ($key in $InputObject.Keys) {
            $dictionary[$key] = ConvertTo-HashtableCompat $InputObject[$key]
        }

        return $dictionary
    }

    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $items = @()
        foreach ($item in $InputObject) {
            $items += ,(ConvertTo-HashtableCompat $item)
        }

        return $items
    }

    if ($InputObject -is [psobject]) {
        $dictionary = @{}
        foreach ($property in $InputObject.PSObject.Properties) {
            $dictionary[$property.Name] = ConvertTo-HashtableCompat $property.Value
        }

        return $dictionary
    }

    return $InputObject
}

$isWindowsPlatform = Test-IsWindowsPlatform

function Resolve-WorkflowPath {
    param([Parameter(Mandatory = $true)][string]$PathValue)

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

function Resolve-PythonLauncher {
    param([Parameter(Mandatory = $true)][string[]]$Versions)

    if (Get-Command py -ErrorAction SilentlyContinue) {
        foreach ($version in $Versions) {
            try {
                & py "-$version" -c "import sys; print(sys.executable)" *> $null
                if ($LASTEXITCODE -eq 0) {
                    return @{ Exe = "py"; Args = @("-$version"); Version = $version }
                }
            }
            catch {
            }
        }
    }

    if (Get-Command python -ErrorAction SilentlyContinue) {
        $versionOutput = & python -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
        if ($versionOutput -in $Versions) {
            return @{ Exe = "python"; Args = @(); Version = $versionOutput }
        }
    }

    return $null
}

function Install-PythonRuntime {
    param([Parameter(Mandatory = $true)][string]$Version)

    if (-not $isWindowsPlatform) {
        throw "Automatic Python installation is only implemented in the PowerShell bootstrap for Windows. Install Python $Version manually and rerun."
    }

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "Python $Version is not installed and winget is unavailable. Install Python $Version manually and rerun."
    }

    $wingetId = "Python.Python.$Version"
    Write-Host "Installing Python $Version via winget ($wingetId)..."
    & winget install --id $wingetId --exact --accept-package-agreements --accept-source-agreements --scope user
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install Python $Version"
    }
}

function Resolve-TorchIndexUrl {
    param([Parameter(Mandatory = $true)][string]$Flavor)

    switch ($Flavor) {
        "cpu" { return "https://download.pytorch.org/whl/cpu" }
        default { return "https://download.pytorch.org/whl/cu128" }
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

function Write-LocalOverrideFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$TrainingVenvPath,
        [Parameter(Mandatory = $true)][string]$RunsDirectory
    )

    $existing = @{}
    if (Test-Path $Path) {
        try {
            $raw = Get-Content $Path -Raw
            if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey("AsHashtable")) {
                $existing = $raw | ConvertFrom-Json -AsHashtable
            }
            else {
                $existing = ConvertTo-HashtableCompat ($raw | ConvertFrom-Json)
            }
        }
        catch {
            $existing = @{}
        }
    }

    if (-not $existing.ContainsKey("MemorySmith")) {
        $existing["MemorySmith"] = @{}
    }

    if (-not $existing["MemorySmith"].ContainsKey("Training")) {
        $existing["MemorySmith"]["Training"] = @{}
    }

    $existing["MemorySmith"]["Training"]["PythonVenvPath"] = $TrainingVenvPath
    $existing["MemorySmith"]["Training"]["RunsDirectory"] = $RunsDirectory
    $existing["MemorySmith"]["Training"]["PythonHarnessScript"] = "MemorySmith.Training/harness.py"
    $existing["MemorySmith"]["Training"]["TrainingDataExportPath"] = "../Data/Training/exports"
    $existing["MemorySmith"]["Training"]["TranscriptDirectory"] = "../Data/Events/chat-transcripts"

    $directory = Split-Path $Path -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $json = $existing | ConvertTo-Json -Depth 10
    Write-Utf8NoBomFile -Path $Path -Content $json
}

if ([string]::IsNullOrWhiteSpace($ScratchRoot)) {
    $ScratchRoot = Get-DefaultScratchRoot
}

$resolvedScratchRoot = Resolve-WorkflowPath $ScratchRoot
if ([string]::IsNullOrWhiteSpace($VenvPath)) {
    $VenvPath = Join-Path $resolvedScratchRoot ".venv"
}

$resolvedVenvPath = Resolve-WorkflowPath $VenvPath
$runsDirectory = Join-Path $resolvedScratchRoot "runs"
$hfHome = Join-Path $resolvedScratchRoot "hf-home"
$hfHubCache = Join-Path $hfHome "hub"
$hfDatasetsCache = Join-Path $hfHome "datasets"
$torchHome = Join-Path $resolvedScratchRoot "torch-home"
$tempDirectory = Join-Path $resolvedScratchRoot "temp"
$resolvedOverridePath = Resolve-WorkflowPath $OverridePath

foreach ($directory in @($resolvedScratchRoot, $runsDirectory, $hfHome, $hfHubCache, $hfDatasetsCache, $torchHome, $tempDirectory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$versionsToTry = @($PythonVersion, $FallbackPythonVersion)
$launcher = Resolve-PythonLauncher -Versions $versionsToTry
if ($null -eq $launcher) {
    if (-not $InstallPythonIfMissing) {
        throw "Supported Python runtime not found. Re-run with -InstallPythonIfMissing or install Python $PythonVersion/$FallbackPythonVersion manually."
    }

    Install-PythonRuntime -Version $PythonVersion
    $launcher = Resolve-PythonLauncher -Versions $versionsToTry
    if ($null -eq $launcher) {
        throw "Python installation completed but no supported launcher was detected afterward."
    }
}

if ($RecreateVenv -and (Test-Path $resolvedVenvPath)) {
    if ($PSCmdlet.ShouldProcess($resolvedVenvPath, "Remove existing training virtual environment")) {
        Remove-Item -Recurse -Force $resolvedVenvPath
    }
}

if (-not (Test-Path $resolvedVenvPath)) {
    if ($PSCmdlet.ShouldProcess($resolvedVenvPath, "Create training virtual environment")) {
        & $launcher.Exe @($launcher.Args + @("-m", "venv", $resolvedVenvPath))
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create training virtual environment at $resolvedVenvPath"
        }
    }
}

$venvPython = if ($isWindowsPlatform) {
    Join-Path $resolvedVenvPath "Scripts\python.exe"
}
else {
    Join-Path $resolvedVenvPath "bin/python"
}

if (-not (Test-Path $venvPython)) {
    throw "Training virtual environment python executable not found at $venvPython"
}

$sessionEnv = @{
    HF_HOME = $hfHome
    HF_HUB_CACHE = $hfHubCache
    TRANSFORMERS_CACHE = $hfHubCache
    HF_DATASETS_CACHE = $hfDatasetsCache
    TORCH_HOME = $torchHome
    TMP = $tempDirectory
    TEMP = $tempDirectory
}

foreach ($entry in $sessionEnv.GetEnumerator()) {
    Set-Item -Path ("Env:" + $entry.Key) -Value $entry.Value
}

if ($PersistUserEnvironment) {
    foreach ($entry in $sessionEnv.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "User")
    }

    [Environment]::SetEnvironmentVariable("MemorySmith__SettingsOverridePath", $resolvedOverridePath, "User")
}

if (-not (Test-Path $requirementsPath)) {
    throw "Training requirements file not found: $requirementsPath"
}

Write-Host "Using Python $($launcher.Version) with venv at $resolvedVenvPath"

if (-not $SkipDependencyInstall) {
    & $venvPython -m pip install --upgrade pip setuptools wheel
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to upgrade pip toolchain in training virtual environment"
    }

    $torchIndexUrl = Resolve-TorchIndexUrl -Flavor $TorchFlavor
    & $venvPython -m pip install torch torchvision --index-url $torchIndexUrl
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install torch/torchvision from $torchIndexUrl"
    }

    & $venvPython -m pip install -r $requirementsPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install training requirements from $requirementsPath"
    }
}

Write-LocalOverrideFile -Path $resolvedOverridePath -TrainingVenvPath $resolvedVenvPath -RunsDirectory $runsDirectory

Write-Host "Running training dependency preflight..."
& $preflightScript -PythonVenvPath $resolvedVenvPath
if ($LASTEXITCODE -ne 0) {
    throw "Training dependency preflight failed after environment setup"
}

Write-Host "Training environment ready."
Write-Host "Scratch root:           $resolvedScratchRoot"
Write-Host "Training venv:          $resolvedVenvPath"
Write-Host "Runs directory:         $runsDirectory"
Write-Host "Local override file:    $resolvedOverridePath"
Write-Host "Persisted env vars:     $PersistUserEnvironment"
Write-Host "Suggested run command:  ./Scripts/Run-FinetuneHarness.ps1 -PythonVenvPath '$resolvedVenvPath' -WorkRoot '$runsDirectory' -ScratchRoot '$resolvedScratchRoot' -RequireTrainingDependencies -RunId ft-smoke-$(Get-Date -Format yyyyMMdd-HHmmss)"