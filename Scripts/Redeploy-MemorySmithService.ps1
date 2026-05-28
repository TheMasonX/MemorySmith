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
    [string]$SettingsOverridePath,
    [ValidateSet('Cpu', 'Cuda', 'OpenVino')]
    [string]$OnnxRuntimeFlavor = 'Cpu',
    [ValidateSet('Cpu', 'Cuda', 'OpenVino')]
    [string]$SemanticExecutionProvider,
    [bool]$CpuFallbackEnabled = $true,
    [int]$CudaDeviceId = 0,
    [string]$OpenVinoDeviceId = '',
    [int]$Port = 5089,
    [string]$BindAddress = '0.0.0.0',
    [switch]$UseHttps,
    [switch]$HttpOnly,
    [int]$HttpsPort = 7090,
    [string]$HttpsBindAddress,
    [string]$HttpsCertificatePath,
    [string]$HttpsCertificatePassword,
    [string]$HttpsCertificatePasswordFile,
    [bool]$AllowRemoteApi = $true,
    [switch]$EnsurePrivateFirewallRule = $true,
    [int]$ServiceTimeoutSeconds = 60,
    [int]$ReadyTimeoutSeconds = 180,
    [switch]$SkipReadyCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$appProject = Join-Path $repoRoot 'MemorySmith.App\MemorySmith.App.csproj'
$MemoryDirectory = if ([string]::IsNullOrWhiteSpace($MemoryDirectory)) { Join-Path $repoRoot 'Data\Memories' } else { [System.IO.Path]::GetFullPath($MemoryDirectory) }
$PublishDirectory = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) { Join-Path $repoRoot 'artifacts\MemorySmith.App' } else { [System.IO.Path]::GetFullPath($PublishDirectory) }
$SettingsOverridePath = if ([string]::IsNullOrWhiteSpace($SettingsOverridePath)) { Join-Path $PublishDirectory 'appsettings.LocalOverrides.json' } else { [System.IO.Path]::GetFullPath($SettingsOverridePath) }
$publishExe = Join-Path $PublishDirectory 'MemorySmith.App.exe'
$ServiceDisplayName = if ([string]::IsNullOrWhiteSpace($ServiceDisplayName)) { $ServiceName } else { $ServiceDisplayName }
$SemanticExecutionProvider = if ([string]::IsNullOrWhiteSpace($SemanticExecutionProvider)) { $OnnxRuntimeFlavor } else { $SemanticExecutionProvider }

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

function Register-OrUpdateService {
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
        [string]$ListenUrl,

        [Parameter(Mandatory = $true)]
        [bool]$RemoteApiEnabled,

        [string[]]$AdditionalRuntimeArgs = @()
    )

    if (Get-ServiceController -Name $Name) {
        Invoke-CheckedCommand -FilePath $ExecutablePath -Arguments @(
            'uninstall',
            '--service-name', $Name
        ) -Step "Unregister Windows service '$Name'"
    }

    $runtimeArguments = @(
        'install',
        '--service-name', $Name,
        '--service-display-name', $DisplayName,
        '--memory-directory', $DataPath,
        '--',
        '--urls', $ListenUrl,
        '--MemorySmith:AllowRemoteApi', $RemoteApiEnabled.ToString().ToLowerInvariant()
    ) + $AdditionalRuntimeArgs

    Invoke-CheckedCommand -FilePath $ExecutablePath -Arguments $runtimeArguments -Step "Register Windows service '$Name'"
}

function Ensure-FirewallRuleForPort {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuleName,

        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $rule = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
    if ($null -eq $rule) {
        Write-Host "==> Create firewall rule '$RuleName'"
        New-NetFirewallRule `
            -DisplayName $RuleName `
            -Direction Inbound `
            -Action Allow `
            -Profile Private `
            -Protocol TCP `
            -LocalPort $Port | Out-Null
    }
    else {
        Write-Host "==> Firewall rule (skipped: '$RuleName' already exists)"
    }
}

function Get-PrimaryLanIp {
    $defaultRoute = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' } |
        Sort-Object RouteMetric, InterfaceMetric |
        Select-Object -First 1

    if ($defaultRoute) {
        $routeIp = Get-NetIPAddress -InterfaceIndex $defaultRoute.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object {
                $_.IPAddress -notlike '127.*' -and
                $_.IPAddress -notlike '169.254.*'
            } |
            Select-Object -ExpandProperty IPAddress -First 1

        if ($routeIp) {
            return $routeIp
        }
    }

    return (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.IPAddress -like '192.168.*' -or
            $_.IPAddress -like '10.*' -or
            $_.IPAddress -match '^172\.(1[6-9]|2[0-9]|3[0-1])\.'
        } |
        Select-Object -ExpandProperty IPAddress -First 1)
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

function Resolve-DefaultHttpsCertificatePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $certDirectory = Join-Path $RepositoryRoot 'artifacts\certs'
    if (-not (Test-Path $certDirectory)) {
        return $null
    }

    $portMatchedCert = Get-ChildItem -Path $certDirectory -Filter "*-$Port.pfx" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($portMatchedCert) {
        return $portMatchedCert.FullName
    }

    $anyCert = Get-ChildItem -Path $certDirectory -Filter '*.pfx' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    return $anyCert?.FullName
}

function Resolve-DefaultHttpsPasswordFilePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CertificatePath
    )

    $certificateDirectory = Split-Path $CertificatePath -Parent
    $certificateFileNameWithoutExtension = [System.IO.Path]::GetFileNameWithoutExtension($CertificatePath)
    $defaultPasswordFile = Join-Path $certificateDirectory "$certificateFileNameWithoutExtension-password.txt"

    if (Test-Path $defaultPasswordFile) {
        return $defaultPasswordFile
    }

    return $null
}

function Write-SemanticSettingsOverride {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExecutionProvider,

        [Parameter(Mandatory = $true)]
        [bool]$CpuFallbackEnabled,

        [Parameter(Mandatory = $true)]
        [int]$CudaDeviceId,

        [AllowEmptyString()]
        [string]$OpenVinoDeviceId = ''
    )

    $root = @{}
    if (Test-Path $Path) {
        $raw = Get-Content -Path $Path -Raw
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            $parsed = ConvertFrom-Json -InputObject $raw -AsHashtable
            if ($parsed -is [System.Collections.IDictionary]) {
                $root = @{}
                foreach ($entry in $parsed.GetEnumerator()) {
                    $root[$entry.Key] = $entry.Value
                }
            }
        }
    }

    if (-not $root.ContainsKey('MemorySmith') -or $root['MemorySmith'] -isnot [System.Collections.IDictionary]) {
        $root['MemorySmith'] = @{}
    }

    $memorySmith = $root['MemorySmith']
    if (-not $memorySmith.ContainsKey('SemanticSearch') -or $memorySmith['SemanticSearch'] -isnot [System.Collections.IDictionary]) {
        $memorySmith['SemanticSearch'] = @{}
    }

    $semantic = $memorySmith['SemanticSearch']
    $semantic['EmbeddingsEnabled'] = $true
    $semantic['ExecutionProvider'] = $ExecutionProvider
    $semantic['CpuFallbackEnabled'] = $CpuFallbackEnabled
    $semantic['CudaDeviceId'] = $CudaDeviceId
    $semantic['OpenVinoDeviceId'] = $OpenVinoDeviceId

    $directory = Split-Path $Path -Parent
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $json = $root | ConvertTo-Json -Depth 20
    Set-Content -Path $Path -Value $json -Encoding UTF8
}

function Wait-ReadyEndpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds,

        [switch]$SkipCertificateCheck
    )

    Write-Host "==> Verify ready endpoint"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

    $readyUri = [System.Uri]::new($Url)
    $liveUrl = [System.Uri]::new($readyUri, '../live').AbsoluteUri

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $requestArguments = @{
                Uri = $Url
                Method = 'Get'
                SkipHttpErrorCheck = $true
                TimeoutSec = 5
            }

            if ($SkipCertificateCheck) {
                $requestArguments['SkipCertificateCheck'] = $true
            }

            $response = Invoke-WebRequest @requestArguments
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

            # If ready does not explicitly return { status: "Ready" }, fall back to liveness.
            # This covers auth redirects/HTML and environments where /ready requires credentials.
            $liveArguments = @{
                Uri = $liveUrl
                Method = 'Get'
                SkipHttpErrorCheck = $true
                TimeoutSec = 5
            }

            if ($SkipCertificateCheck) {
                $liveArguments['SkipCertificateCheck'] = $true
            }

            $liveResponse = Invoke-WebRequest @liveArguments
            if ([int]$liveResponse.StatusCode -eq 200) {
                $livePayload = $null
                if (-not [string]::IsNullOrWhiteSpace($liveResponse.Content)) {
                    try {
                        $livePayload = $liveResponse.Content | ConvertFrom-Json
                    }
                    catch {
                        $livePayload = $null
                    }
                }

                if ($null -ne $livePayload -and $livePayload.status -eq 'Healthy') {
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

if ($HttpOnly -and $UseHttps) {
    throw 'Use either -HttpOnly or -UseHttps, not both.'
}

if ($SemanticExecutionProvider -ne 'Cpu' -and $SemanticExecutionProvider -ne $OnnxRuntimeFlavor) {
    Write-Warning "Semantic execution provider '$SemanticExecutionProvider' does not match ONNX runtime flavor '$OnnxRuntimeFlavor'. MemorySmith can still fall back to CPU, but the requested hardware provider is unlikely to initialize unless the published runtime flavor also matches."
}

if (-not [string]::IsNullOrWhiteSpace($HttpsCertificatePassword) -and -not [string]::IsNullOrWhiteSpace($HttpsCertificatePasswordFile)) {
    throw 'Use either -HttpsCertificatePassword or -HttpsCertificatePasswordFile, not both.'
}

Push-Location $repoRoot
try {
    $preferHttps = -not $HttpOnly
    if ($UseHttps) {
        $preferHttps = $true
    }

    $listenUrls = @("http://$BindAddress`:$Port")
    $additionalRuntimeArgs = @()
    $readyUrl = "http://localhost:$Port/api/health/ready"
    $readySkipCertificateCheck = $false
    $resolvedHttpsCertificatePath = $null
    $resolvedHttpsCertificatePassword = $null
    $httpsEnabled = $false

    if ($preferHttps) {
        $resolvedHttpsCertificatePath = if ([string]::IsNullOrWhiteSpace($HttpsCertificatePath)) {
            Resolve-DefaultHttpsCertificatePath -RepositoryRoot $repoRoot -Port $HttpsPort
        }
        else {
            [System.IO.Path]::GetFullPath($HttpsCertificatePath)
        }

        if (-not [string]::IsNullOrWhiteSpace($resolvedHttpsCertificatePath) -and -not (Test-Path $resolvedHttpsCertificatePath)) {
            throw "HTTPS certificate file not found: $resolvedHttpsCertificatePath"
        }

        if ([string]::IsNullOrWhiteSpace($resolvedHttpsCertificatePath)) {
            Write-Warning "No HTTPS certificate found under artifacts/certs. Continuing with HTTP only. Use -HttpOnly to suppress HTTPS auto-discovery."
        }
        else {
            if ([string]::IsNullOrWhiteSpace($HttpsCertificatePassword) -and [string]::IsNullOrWhiteSpace($HttpsCertificatePasswordFile)) {
                $HttpsCertificatePasswordFile = Resolve-DefaultHttpsPasswordFilePath -CertificatePath $resolvedHttpsCertificatePath
            }

            if (-not [string]::IsNullOrWhiteSpace($HttpsCertificatePasswordFile)) {
                $resolvedHttpsCertificatePasswordFile = [System.IO.Path]::GetFullPath($HttpsCertificatePasswordFile)
                if (-not (Test-Path $resolvedHttpsCertificatePasswordFile)) {
                    throw "HTTPS certificate password file not found: $resolvedHttpsCertificatePasswordFile"
                }

                $resolvedHttpsCertificatePassword = (Get-Content -Path $resolvedHttpsCertificatePasswordFile -Raw).Trim()
                if ([string]::IsNullOrWhiteSpace($resolvedHttpsCertificatePassword)) {
                    throw "HTTPS certificate password file is empty: $resolvedHttpsCertificatePasswordFile"
                }
            }
            elseif (-not [string]::IsNullOrWhiteSpace($HttpsCertificatePassword)) {
                $resolvedHttpsCertificatePassword = $HttpsCertificatePassword.Trim()
                if ([string]::IsNullOrWhiteSpace($resolvedHttpsCertificatePassword)) {
                    throw 'HTTPS certificate password resolves to empty after trimming.'
                }
            }

            $resolvedHttpsBindAddress = if ([string]::IsNullOrWhiteSpace($HttpsBindAddress)) { $BindAddress } else { $HttpsBindAddress }
            $listenUrls += "https://$resolvedHttpsBindAddress`:$HttpsPort"
            $additionalRuntimeArgs += @('--Kestrel:Certificates:Default:Path', $resolvedHttpsCertificatePath)
            if (-not [string]::IsNullOrWhiteSpace($resolvedHttpsCertificatePassword)) {
                $additionalRuntimeArgs += @('--Kestrel:Certificates:Default:Password', $resolvedHttpsCertificatePassword)
            }

            $readyUrl = "https://localhost:$HttpsPort/api/health/ready"
            $readySkipCertificateCheck = $true
            $httpsEnabled = $true

            Write-Host "==> HTTPS enabled using certificate: $resolvedHttpsCertificatePath"
        }
    }

    $listenUrl = [string]::Join(';', $listenUrls)
    $additionalRuntimeArgs += @('--MemorySmith:SettingsOverridePath', $SettingsOverridePath)

    Stop-ServiceIfPresent -Name $ServiceName -TimeoutSeconds $ServiceTimeoutSeconds
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @('build', $appProject, '-c', $Configuration, '-v', 'minimal', "-p:MemorySmithOnnxRuntimeFlavor=$OnnxRuntimeFlavor") -Step 'Build MemorySmith.App'
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @('publish', $appProject, '-c', $Configuration, '-o', $PublishDirectory, '--no-build', '-v', 'minimal', "-p:MemorySmithOnnxRuntimeFlavor=$OnnxRuntimeFlavor") -Step 'Publish MemorySmith.App'

    if (-not (Test-Path $publishExe)) {
        throw "Publish completed, but the expected executable was not found: $publishExe"
    }

    Write-Host "==> Write semantic settings override '$SettingsOverridePath'"
    Write-SemanticSettingsOverride -Path $SettingsOverridePath -ExecutionProvider $SemanticExecutionProvider -CpuFallbackEnabled $CpuFallbackEnabled -CudaDeviceId $CudaDeviceId -OpenVinoDeviceId $OpenVinoDeviceId

    Register-OrUpdateService -Name $ServiceName -DisplayName $ServiceDisplayName -ExecutablePath $publishExe -DataPath $MemoryDirectory -ListenUrl $listenUrl -RemoteApiEnabled $AllowRemoteApi -AdditionalRuntimeArgs $additionalRuntimeArgs

    if ($EnsurePrivateFirewallRule) {
        Ensure-FirewallRuleForPort -RuleName "MemorySmith HTTP $Port" -Port $Port
        if ($httpsEnabled) {
            Ensure-FirewallRuleForPort -RuleName "MemorySmith HTTPS $HttpsPort" -Port $HttpsPort
        }
    }

    Start-ServiceAndWait -Name $ServiceName -TimeoutSeconds $ServiceTimeoutSeconds

    if (-not $SkipReadyCheck) {
        Wait-ReadyEndpoint -Url $readyUrl -TimeoutSeconds $ReadyTimeoutSeconds -SkipCertificateCheck:$readySkipCertificateCheck
    }

    Write-Host ''
    Write-Host "MemorySmith service '$ServiceName' is deployed and running."
    Write-Host "Publish directory: $PublishDirectory"
    Write-Host "Listen URL: $listenUrl"
    Write-Host "MemorySmith:AllowRemoteApi: $AllowRemoteApi"
    Write-Host "MemorySmithOnnxRuntimeFlavor: $OnnxRuntimeFlavor"
    Write-Host "Semantic execution provider: $SemanticExecutionProvider"
    Write-Host "CPU fallback enabled: $CpuFallbackEnabled"
    Write-Host "CUDA device id: $CudaDeviceId"
    if (-not [string]::IsNullOrWhiteSpace($OpenVinoDeviceId)) {
        Write-Host "OpenVINO device id: $OpenVinoDeviceId"
    }
    Write-Host "Settings override: $SettingsOverridePath"
    if ($httpsEnabled -and $resolvedHttpsCertificatePath) {
        Write-Host "HTTPS certificate: $resolvedHttpsCertificatePath"
    }
    $lanIp = Get-PrimaryLanIp
    if ($lanIp) {
        Write-Host "LAN URL: http://${lanIp}:$Port"
        if ($httpsEnabled) {
            Write-Host "LAN HTTPS URL: https://${lanIp}:$HttpsPort"
        }
    }
    Write-Host "Ready endpoint: $readyUrl"
}
finally {
    Pop-Location
}