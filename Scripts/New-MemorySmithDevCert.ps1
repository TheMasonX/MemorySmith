<#
.SYNOPSIS

Generates a self-signed root CA and a server certificate for memorysmith.home.arpa
for local HTTPS access. Installs the root CA into the CurrentUser trusted store.

.DESCRIPTION
Creates a 10-year root CA ("MemorySmith LAN Root CA") and a 2-year server
certificate whose SAN includes memorysmith.home.arpa and the specified LAN IP.
Outputs the following files under <repo-root>\artifacts\certs:

  - MemorySmith-LAN-Root-CA.cer            (root CA certificate, distribute to clients)
  - memorysmith.home.arpa-7090.pfx          (server PFX for Kestrel binding)
  - memorysmith.home.arpa-7090.cer          (server CER for inspection)
  - memorysmith.home.arpa-7090-password.txt (PFX password, plaintext for script use)

The PFX password is a random 64-character hex string. The script also imports
the root CA into Cert:\CurrentUser\Root so the local machine trusts it.

.PARAMETER LanIp
The LAN IP address to include in the server certificate SAN. Default: auto-detect.

.PARAMETER CertDir
Output directory for certificate artifacts. Default: <repo-root>\artifacts\certs.

.PARAMETER HttpsPort
HTTPS port used in the certificate file naming. Default: 7090.

.PARAMETER HostName
The DNS name for the server certificate. Default: memorysmith.home.arpa.

.PARAMETER SkipTrust
If specified, the root CA is exported but NOT imported into the local trusted store.

.EXAMPLE
.\Scripts\New-MemorySmithDevCert.ps1
Generates certs with auto-detected LAN IP and installs the root CA.

.EXAMPLE
.\Scripts\New-MemorySmithDevCert.ps1 -LanIp 192.168.1.100 -SkipTrust
Generates certs for 192.168.1.100 without installing the root CA.

.NOTES
Requires PowerShell 7+ and Windows (New-SelfSignedCertificate).
Must be run as Administrator to install the root CA.
#>

[CmdletBinding()]
param(
    [string]$LanIp,

    [string]$CertDir,

    [int]$HttpsPort = 7090,

    [string]$HostName = 'memorysmith.home.arpa',

    [switch]$SkipTrust
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Resolve paths ───────────────────────────────────────────────────────────────
$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($CertDir)) {
    $CertDir = Join-Path $repoRoot 'artifacts\certs'
}

# ── Resolve LAN IP ──────────────────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($LanIp)) {
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
            $LanIp = $routeIp
        }
    }

    if ([string]::IsNullOrWhiteSpace($LanIp)) {
        $LanIp = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object {
                $_.IPAddress -like '192.168.*' -or
                $_.IPAddress -like '10.*' -or
                $_.IPAddress -match '^172\.(1[6-9]|2[0-9]|3[0-1])\.'
            } |
            Select-Object -ExpandProperty IPAddress -First 1
    }
}

if ([string]::IsNullOrWhiteSpace($LanIp)) {
    Write-Warning 'Could not auto-detect a LAN IP. The certificate will be created with DNS only.'
    $ipSanPart = ''
}
else {
    $ipSanPart = "&IPAddress=$LanIp"
    Write-Host "LAN IP resolved: $LanIp"
}

# ── Create cert directory ───────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $CertDir | Out-Null

# ── Generate root CA ────────────────────────────────────────────────────────────
Write-Host "==> Generate root CA 'MemorySmith LAN Root CA' (10-year validity)"

$root = New-SelfSignedCertificate `
    -Type Custom `
    -Subject 'CN=MemorySmith LAN Root CA' `
    -KeyAlgorithm RSA `
    -KeyLength 4096 `
    -HashAlgorithm sha256 `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyExportPolicy Exportable `
    -KeyUsage CertSign, CRLSign, DigitalSignature `
    -TextExtension @('2.5.29.19={text}CA=true&pathlength=1') `
    -NotAfter (Get-Date).AddYears(10)

$rootCerPath = Join-Path $CertDir 'MemorySmith-LAN-Root-CA.cer'
Export-Certificate -Cert $root -FilePath $rootCerPath | Out-Null
Write-Host "  Exported root CA: $rootCerPath"

# ── Generate server certificate ─────────────────────────────────────────────────
Write-Host "==> Generate server certificate for '$HostName' (2-year validity)"

$sanExtension = "2.5.29.17={text}DNS=$HostName$ipSanPart"

$server = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=$HostName" `
    -Signer $root `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm sha256 `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyExportPolicy Exportable `
    -TextExtension @(
        '2.5.29.37={text}1.3.6.1.5.5.7.3.1',
        $sanExtension
    ) `
    -NotAfter (Get-Date).AddYears(2)

$serverCerPath = Join-Path $CertDir "$HostName-$HttpsPort.cer"
Export-Certificate -Cert $server -FilePath $serverCerPath | Out-Null
Write-Host "  Exported server CER: $serverCerPath"

# ── Export PFX with random password ─────────────────────────────────────────────
$pfxPassword = -join ((1..64) | ForEach-Object { '{0:x}' -f (Get-Random -Minimum 0 -Maximum 16) })

$passwordFilePath = Join-Path $CertDir "$HostName-$HttpsPort-password.txt"
$pfxPassword | Set-Content -Path $passwordFilePath -Encoding UTF8 -NoNewline
Write-Host "  Password file: $passwordFilePath"

$pfxPath = Join-Path $CertDir "$HostName-$HttpsPort.pfx"
$securePassword = ConvertTo-SecureString $pfxPassword -AsPlainText -Force
Export-PfxCertificate -Cert $server -FilePath $pfxPath -Password $securePassword | Out-Null
Write-Host "  Exported server PFX: $pfxPath"

# ── Trust the root CA unless skipped ────────────────────────────────────────────
if (-not $SkipTrust) {
    Write-Host "==> Install root CA into Cert:\CurrentUser\Root"
    Import-Certificate -FilePath $rootCerPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
    Write-Host "  Root CA installed. Cert:\CurrentUser\Root now trusts 'MemorySmith LAN Root CA'."
}
else {
    Write-Host "==> Skip root CA installation (-SkipTrust specified)"
}

# ── Summary ─────────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host 'Certificate generation complete.'
Write-Host "  Host name: $HostName"
if (-not [string]::IsNullOrWhiteSpace($LanIp)) {
    Write-Host "  LAN IP in SAN: $LanIp"
}
Write-Host "  HTTPS port: $HttpsPort"
Write-Host "  Cert directory: $CertDir"
Write-Host ''
Write-Host 'Next steps:'
Write-Host '  1. Ensure memorysmith.home.arpa resolves to this machine'
Write-Host '     (router DNS or hosts-file entry).'
Write-Host '  2. Deploy with:'
Write-Host "     .\Scripts\Redeploy-MemorySmithService.ps1 -HttpsCertificatePath '$pfxPath' -HttpsCertificatePasswordFile '$passwordFilePath'"
Write-Host '  3. Distribute MemorySmith-LAN-Root-CA.cer to LAN clients that'
Write-Host '     should trust this certificate.'
