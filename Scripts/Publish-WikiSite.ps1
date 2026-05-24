<#
.SYNOPSIS
Builds the GitHub Pages wiki site and optionally dispatches the Pages workflow.
#>
[CmdletBinding()]
param(
    [switch]$Deploy,
    [switch]$OpenSite,
    [switch]$ForceRecreateEnvironment,
    [switch]$ExportMermaidSvg
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$buildScript = Join-Path $repoRoot 'docs\build_pages_site.py'
$outputDir = Join-Path $repoRoot 'docs\output\wiki'
$outputIndex = Join-Path $outputDir 'index.html'
$environmentRoot = Join-Path $repoRoot 'artifacts\tools\docs-site-venv'
$environmentPython = Join-Path $environmentRoot 'Scripts\python.exe'
$workflowFile = 'docs-pages.yml'
$allowedDeployBranches = @('main', 'master')

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$Arguments = @(),

        [Parameter(Mandatory = $true)]
        [string]$Step
    )

    Write-Host "==> $Step"
    & $FilePath @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

function Get-PythonBootstrapCommand {
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($null -ne $py) {
        return [pscustomobject]@{
            FilePath = $py.Source
            Arguments = @('-3')
        }
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -ne $python) {
        return [pscustomobject]@{
            FilePath = $python.Source
            Arguments = @()
        }
    }

    throw "Python 3 was not found. Install Python or the Windows 'py' launcher before building the wiki site."
}

function Ensure-DocsEnvironment {
    if ($ForceRecreateEnvironment -and (Test-Path $environmentRoot)) {
        Write-Host '==> Remove docs-site virtual environment'
        Remove-Item $environmentRoot -Recurse -Force
    }

    if (-not (Test-Path $environmentPython)) {
        $bootstrap = Get-PythonBootstrapCommand
        Invoke-CheckedCommand -FilePath $bootstrap.FilePath -Arguments ($bootstrap.Arguments + @('-m', 'venv', $environmentRoot)) -Step 'Create docs-site virtual environment'
    }

    Invoke-CheckedCommand -FilePath $environmentPython -Arguments @('-m', 'pip', 'install', '--upgrade', 'pip', 'markdown') -Step 'Install docs-site Python dependencies'
}

function Get-CurrentGitBranch {
    $branch = & git -C $repoRoot rev-parse --abbrev-ref HEAD
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to determine the current git branch.'
    }

    return $branch.Trim()
}

function Get-GitHubCliPath {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -eq $gh) {
        throw "GitHub CLI ('gh') is required for -Deploy. Install it from https://cli.github.com/ and run 'gh auth login'."
    }

    & $gh.Source auth status --hostname github.com | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI is not authenticated. Run 'gh auth login' before using -Deploy."
    }

    return $gh.Source
}

if (-not (Test-Path $buildScript)) {
    throw "Missing site generator: $buildScript"
}

Push-Location $repoRoot
try {
    Ensure-DocsEnvironment

    $buildArguments = @($buildScript)
    if ($ExportMermaidSvg) {
        $buildArguments += '--export-mermaid-svg'
    }

    Invoke-CheckedCommand -FilePath $environmentPython -Arguments $buildArguments -Step 'Build GitHub Pages wiki site'

    if (-not (Test-Path $outputIndex)) {
        throw "Site build finished, but the expected output file was not created: $outputIndex"
    }

    $resolvedIndex = (Resolve-Path $outputIndex).Path
    Write-Host ''
    Write-Host "Site rebuilt successfully: $resolvedIndex"

    if ($OpenSite) {
        Start-Process $resolvedIndex
    }

    if ($Deploy) {
        $branch = Get-CurrentGitBranch
        if ($branch -notin $allowedDeployBranches) {
            throw "Refusing to deploy from branch '$branch'. Switch to main or master, or update the guard intentionally if the deployment policy changes."
        }

        $gh = Get-GitHubCliPath
        Invoke-CheckedCommand -FilePath $gh -Arguments @('workflow', 'run', $workflowFile, '--ref', $branch) -Step 'Trigger GitHub Pages deployment'

        Write-Host ''
        Write-Host "Deployment requested from branch '$branch'."
        Write-Host "Monitor it with: gh run list --workflow $workflowFile --limit 1"
    }
}
finally {
    Pop-Location
}