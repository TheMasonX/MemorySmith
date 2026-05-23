<#
.SYNOPSIS
Stops, rebuilds, republishes, registers if needed, and starts the local MemorySmith Windows Service.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'MemorySmith',
    [string]$ServiceDisplayName,
    [string]$Configuration = 'Release',
    [string]$MemoryDirectory,
    [string]$PublishDirectory,
    [int]$Port = 5089,
    [int]$ServiceTimeoutSeconds = 60,
    [int]$ReadyTimeoutSeconds = 60,
    [switch]$SkipReadyCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$appProject = Join-Path $repoRoot 'MemorySmith.App\MemorySmith.App.csproj'
$MemoryDirectory = if ([string]::IsNullOrWhiteSpace($MemoryDirectory)) { Join-Path $repoRoot 'Data\Memories' } else { [System.IO.Path]::GetFullPath($MemoryDirectory) }
$PublishDirectory = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) { Join-Path $repoRoot 'artifacts\MemorySmith.App' } else { [System.IO.Path]::GetFullPath($PublishDirectory) }
$publishExe = Join-Path $PublishDirectory 'MemorySmith.App.exe'
$ServiceDisplayName = if ([string]::IsNullOrWhiteSpace($ServiceDisplayName)) { $ServiceName } else { $ServiceDisplayName }

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

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

function Get-ServiceController {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    return Get-Service -Name $Name -ErrorAction SilentlyContinue
}

function Stop-ServiceIfPresent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $service = Get-ServiceController -Name $Name
    if ($null -eq $service) {
        Write-Host "==> Stop service (skipped: '$Name' is not registered)"
        return
    }

    if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "==> Stop service (skipped: '$Name' is already stopped)"
        return
    }

    Write-Host "==> Stop service '$Name'"
    Stop-Service -Name $Name -ErrorAction Stop
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds($TimeoutSeconds))
}

function Register-ServiceIfMissing {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName,

        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [Parameter(Mandatory = $true)]
        [string]$DataPath,

        [Parameter(Mandatory = $true)]
        [int]$HttpPort
    )

    if (Get-ServiceController -Name $Name) {
        Write-Host "==> Register service (skipped: '$Name' already exists)"
        return
    }

    Invoke-CheckedCommand -FilePath $ExecutablePath -Arguments @(
        'install',
        '--service-name', $Name,
        '--service-display-name', $DisplayName,
        '--memory-directory', $DataPath,
        '--port', $HttpPort.ToString()
    ) -Step "Register Windows service '$Name'"
}

function Start-ServiceAndWait {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    Write-Host "==> Start service '$Name'"
    Start-Service -Name $Name -ErrorAction Stop
    $service = Get-Service -Name $Name -ErrorAction Stop
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds($TimeoutSeconds))
}

function Wait-ReadyEndpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    Write-Host "==> Verify ready endpoint"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -Method Get -SkipHttpErrorCheck -TimeoutSec 5
            if ([int]$response.StatusCode -eq 200) {
                $payload = $null
                if (-not [string]::IsNullOrWhiteSpace($response.Content)) {
                    try {
                        $payload = $response.Content | ConvertFrom-Json
                    }
                    catch {
                        $payload = $null
                    }
                }

                if ($null -ne $payload -and $payload.status -eq 'Ready') {
                    return
                }
            }
        }
        catch {
        }

        Start-Sleep -Seconds 1
    }

    throw "Ready check failed for $Url within $TimeoutSeconds seconds."
}

if (-not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

if (-not (Test-Path $appProject)) {
    throw "Missing project file: $appProject"
}

Push-Location $repoRoot
try {
    Stop-ServiceIfPresent -Name $ServiceName -TimeoutSeconds $ServiceTimeoutSeconds
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @('build', $appProject, '-c', $Configuration, '-v', 'minimal') -Step 'Build MemorySmith.App'
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @('publish', $appProject, '-c', $Configuration, '-o', $PublishDirectory, '--no-build', '-v', 'minimal') -Step 'Publish MemorySmith.App'

    if (-not (Test-Path $publishExe)) {
        throw "Publish completed, but the expected executable was not found: $publishExe"
    }

    Register-ServiceIfMissing -Name $ServiceName -DisplayName $ServiceDisplayName -ExecutablePath $publishExe -DataPath $MemoryDirectory -HttpPort $Port
    Start-ServiceAndWait -Name $ServiceName -TimeoutSeconds $ServiceTimeoutSeconds

    if (-not $SkipReadyCheck) {
        Wait-ReadyEndpoint -Url "http://localhost:$Port/api/health/ready" -TimeoutSeconds $ReadyTimeoutSeconds
    }

    Write-Host ''
    Write-Host "MemorySmith service '$ServiceName' is deployed and running."
    Write-Host "Publish directory: $PublishDirectory"
    Write-Host "Ready endpoint: http://localhost:$Port/api/health/ready"
}
finally {
    Pop-Location
}