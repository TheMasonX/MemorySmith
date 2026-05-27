# HTTPS Setup Guide

This guide explains how to run MemorySmith over HTTPS for local development and how to verify certificate trust on Windows.

## 1. Prerequisites

- .NET 9 SDK installed
- Local repository clone of MemorySmith
- PowerShell 7+

## 2. Create And Trust The ASP.NET Core Development Certificate

Run these commands once per machine/user profile:

```powershell
dotnet dev-certs https --clean
dotnet dev-certs https --trust
dotnet dev-certs https --check
```

Expected result: check returns success for a valid trusted certificate.

> [!NOTE]
> Screenshot placeholder [HTTPS-SETUP-01]: terminal showing successful `dotnet dev-certs https --check`.

## 2a. Repository Alias And LAN Certificate Example

This repository standardizes on the private-network host name `memorysmith.home.arpa` for the HTTPS MCP endpoint. The checked-in `.vscode/mcp.json` expects `https://memorysmith.home.arpa:7090/mcp`.

That alias is intentional, but it only works broadly when contributors reproduce three pieces locally:

1. `memorysmith.home.arpa` resolves to the machine running MemorySmith, using router DNS or a hosts-file entry.
2. The HTTPS certificate served on port `7090` includes `memorysmith.home.arpa` in the SAN.
3. Any client that will browse to MemorySmith or call the MCP endpoint trusts the issuing root CA.

If your LAN IP is different from the example below, keep the `memorysmith.home.arpa` alias and regenerate the certificate with your own IP in the SAN. The durable repo contract is the alias plus port, not one specific private IP.

For the current local-network setup, the concrete certificate target is:

- Host name: `memorysmith.home.arpa`
- LAN IP: `192.168.1.8`
- HTTPS port: `7090`

Windows-native certificate artifacts have been generated under `artifacts/certs`:

- Root CA to trust on LAN clients: `artifacts/certs/MemorySmith-LAN-Root-CA.cer`
- Server PFX for Kestrel/service binding: `artifacts/certs/memorysmith.home.arpa-7090.pfx`
- Server CER for inspection/distribution: `artifacts/certs/memorysmith.home.arpa-7090.cer`
- Generated PFX password file: `artifacts/certs/memorysmith.home.arpa-7090-password.txt`

If clients should use the host name instead of the raw IP, make sure `memorysmith.home.arpa` resolves to `192.168.1.8` on those devices through router DNS or a hosts file entry.

Windows hosts-file example when local DNS is not available:

```text
192.168.1.8 memorysmith.home.arpa
```

Edit `C:\Windows\System32\drivers\etc\hosts` as Administrator. Replace `192.168.1.8` with the actual address of the machine hosting MemorySmith.

### Generate Matching Windows Certificates For `memorysmith.home.arpa`

If you are setting up a new machine and do not already have the certificate artifacts under `artifacts/certs`, this Windows-native example creates a private root CA plus a server certificate whose SAN covers `memorysmith.home.arpa` and one LAN IP.

Update `$lanIp` before running it:

```powershell
$certDir = Join-Path (Resolve-Path .) 'artifacts/certs'
New-Item -ItemType Directory -Force -Path $certDir | Out-Null

$lanIp = '192.168.1.8'

$root = New-SelfSignedCertificate `
	-Type Custom `
	-Subject 'CN=MemorySmith LAN Root CA' `
	-KeyAlgorithm RSA `
	-KeyLength 4096 `
	-HashAlgorithm sha256 `
	-CertStoreLocation 'Cert:\CurrentUser\My' `
	-KeyExportPolicy Exportable `
	-KeyUsage CertSign,CRLSign,DigitalSignature `
	-TextExtension @('2.5.29.19={text}CA=true&pathlength=1') `
	-NotAfter (Get-Date).AddYears(10)

$server = New-SelfSignedCertificate `
	-Type Custom `
	-Subject 'CN=memorysmith.home.arpa' `
	-DnsName 'memorysmith.home.arpa' `
	-Signer $root `
	-KeyAlgorithm RSA `
	-KeyLength 2048 `
	-HashAlgorithm sha256 `
	-CertStoreLocation 'Cert:\CurrentUser\My' `
	-KeyExportPolicy Exportable `
	-TextExtension @(
		'2.5.29.37={text}1.3.6.1.5.5.7.3.1',
		"2.5.29.17={text}DNS=memorysmith.home.arpa&IPAddress=$lanIp"
	) `
	-NotAfter (Get-Date).AddYears(2)

Export-Certificate -Cert $root -FilePath (Join-Path $certDir 'MemorySmith-LAN-Root-CA.cer') | Out-Null
Export-Certificate -Cert $server -FilePath (Join-Path $certDir 'memorysmith.home.arpa-7090.cer') | Out-Null

$password = [guid]::NewGuid().ToString('N')
$password | Set-Content (Join-Path $certDir 'memorysmith.home.arpa-7090-password.txt')
$securePassword = ConvertTo-SecureString $password -AsPlainText -Force

Export-PfxCertificate -Cert $server -FilePath (Join-Path $certDir 'memorysmith.home.arpa-7090.pfx') -Password $securePassword | Out-Null
Import-Certificate -FilePath (Join-Path $certDir 'MemorySmith-LAN-Root-CA.cer') -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
```

This produces the same file names referenced elsewhere in this guide and by the HTTPS-first service redeploy workflow. If you prefer another PKI tool such as `mkcert` or an internal CA, that is fine too; the important part is that the exported server certificate covers `memorysmith.home.arpa` and the client trusts the issuing root.

## 2b. Trust The LAN Root CA On Windows

Use these exact steps on another Windows machine that should trust the local MemorySmith certificate:

1. Copy `artifacts/certs/MemorySmith-LAN-Root-CA.cer` to the target Windows device.
2. Double-click the `.cer` file.
3. Click `Install Certificate...`.
4. Choose `Current User` if you only need trust for the current signed-in user, or `Local Machine` if you want trust for all users and have administrator rights.
5. Select `Place all certificates in the following store`.
6. Click `Browse...`.
7. Choose `Trusted Root Certification Authorities`.
8. Click `Next`.
9. Click `Finish`.
10. Accept the Windows security warning about importing a root certificate.
11. Verify that the target device can resolve `memorysmith.home.arpa` to `192.168.1.8`, or browse by IP instead.
12. Open `https://memorysmith.home.arpa:7090` or `https://192.168.1.8:7090`.

PowerShell alternative on Windows:

```powershell
Import-Certificate -FilePath .\MemorySmith-LAN-Root-CA.cer -CertStoreLocation Cert:\CurrentUser\Root
```

If you use the PowerShell path, run it in the same user context that will browse to MemorySmith.

## 2c. Trust The LAN Root CA On Android

Use these exact steps on an Android device that should trust the local MemorySmith certificate:

1. Copy `artifacts/certs/MemorySmith-LAN-Root-CA.cer` to the Android device, usually into `Downloads`.
2. Open `Settings`.
3. Open `Security and privacy`.
4. Open `More security settings`.
5. Open `Encryption and credentials` or `Credential storage`.
6. Tap `Install a certificate`.
7. Choose `CA certificate`.
8. Accept the Android warning about user-installed certificate authorities.
9. Select `MemorySmith-LAN-Root-CA.cer` from storage.
10. If Android asks for a name, use `MemorySmith LAN Root CA`.
11. Make sure the device can resolve `memorysmith.home.arpa` to `192.168.1.8`, or use `https://192.168.1.8:7090` directly.
12. Open the MemorySmith URL in Chrome or another normal browser.

Notes for Android:

- Settings labels vary slightly by vendor and Android version, but the path always ends at installing a `CA certificate` from storage.
- Some work-managed devices block user-installed root CAs.
- Normal browsers typically trust the user CA for web browsing, but some apps ignore user-installed CAs unless they were built to allow them.

## 3. Use The Built-In HTTPS Launch Profiles

`MemorySmith.App/Properties/launchSettings.json` now includes two HTTPS paths:

- `https` for `https://localhost:7090;http://localhost:5089`
- `https-lan` for `https://0.0.0.0:7090;http://0.0.0.0:5089`

Both profiles run under `LocalDevelopment`, which preserves static web assets and the local-development post-configuration defaults without relying on a separate `appsettings.LocalDevelopment.json` file.

> [!NOTE]
> Screenshot placeholder [HTTPS-SETUP-02]: `launchSettings.json` with the `https` and `https-lan` profiles.

## 4. Run The App With HTTPS

From repo root:

```powershell
dotnet run --project MemorySmith.App --launch-profile https
```

For LAN binding from the repo run path:

```powershell
dotnet run --project MemorySmith.App --launch-profile https-lan
```

Open:

- `https://localhost:7090`
- `https://memorysmith.home.arpa:7090`
- `https://192.168.1.8:7090`

Once the alias resolves and the certificate is trusted, the checked-in `.vscode/mcp.json` should also be able to reach `https://memorysmith.home.arpa:7090/mcp` without a local edit.

If prompted by the browser, verify certificate details and confirm it is the local ASP.NET Core development certificate.

For other devices on the LAN, the built-in ASP.NET Core development certificate is usually only valid for `localhost`. That means `https-lan` will bind the HTTPS listener, but a phone or another PC will still see a certificate mismatch unless you use a certificate whose SAN matches the host name or IP that device connects to.

For a stronger LAN HTTPS path, use the Windows service redeploy script with an explicit certificate:

```powershell
.\Scripts\Redeploy-MemorySmithService.ps1 -UseHttps -HttpsPort 7090 -HttpsCertificatePath .\artifacts\certs\memorysmith.home.arpa-7090.pfx -HttpsCertificatePasswordFile .\artifacts\certs\memorysmith.home.arpa-7090-password.txt
```

That command keeps HTTP on `5089` and adds HTTPS on `7090` using the LAN cert whose SAN covers both `memorysmith.home.arpa` and `192.168.1.8`.

Current default behavior of `Redeploy-MemorySmithService.ps1` is HTTPS-first: running it with no HTTPS parameters attempts auto-discovery from `artifacts/certs` (prefers `*-[HttpsPort].pfx`, then newest `*.pfx`). If no certificate is found, deployment continues over HTTP-only.

To explicitly skip HTTPS and force HTTP-only:

```powershell
.\Scripts\Redeploy-MemorySmithService.ps1 -HttpOnly
```

> [!NOTE]
> Screenshot placeholder [HTTPS-SETUP-03]: browser on `https://localhost:7090` with lock icon visible.

## 5. Verify Secure Endpoints

Check these pages over HTTPS:

1. `/login`
2. `/admin/setup`
3. `/admin`
4. `/health`

Also verify API endpoint access over HTTPS:

- `https://localhost:7090/api/health/readiness`

> [!NOTE]
> Screenshot placeholder [HTTPS-SETUP-04]: `/health` loaded over HTTPS.
> [!NOTE]
> Screenshot placeholder [HTTPS-SETUP-05]: HTTPS API readiness response in browser or API tool.

## 6. Optional: Redirect HTTP To HTTPS In Development

If you want strict HTTPS-only behavior in local runs, keep the HTTPS endpoint in the profile and avoid using the HTTP URL directly.

MemorySmith and ASP.NET Core can still expose both endpoints for compatibility during transition.

## 7. Common Issues

### Certificate Not Trusted

- Re-run `dotnet dev-certs https --trust`
- Close and reopen all browser windows
- Retry `dotnet dev-certs https --check`

### Port Already In Use

- Change the HTTPS port in the launch profile, for example `https://localhost:7091`
- Re-run with the same launch profile name after updating `applicationUrl`

### Browser Still Warns About Certificate

- Confirm you are visiting `https://localhost:<port>` and not an IP address
- Verify the certificate subject and issuer indicate the ASP.NET Core development certificate
- For LAN access, prefer a certificate whose SAN matches the machine name or LAN IP rather than the default localhost development certificate
- For this repo's LAN example, trust `artifacts/certs/MemorySmith-LAN-Root-CA.cer` on the client device and browse to `https://memorysmith.home.arpa:7090` or `https://192.168.1.8:7090`
- Remove stale certificates and recreate:

```powershell
dotnet dev-certs https --clean
dotnet dev-certs https --trust
```

> [!NOTE]
> Screenshot placeholder [HTTPS-SETUP-06]: browser certificate warning example for troubleshooting docs.

## 8. Production TLS

For hosted environments (IIS/reverse proxy/Kestrel cert binding), use [HTTPS Production TLS Guide](../ops/https-production-tls.md).

## 9. Access From Outside Home

Yes, it is possible to reach MemorySmith when you are away from home, but the safe answer depends on how you expose it.

Recommended path right now:

1. Keep MemorySmith private.
2. Use a VPN or private mesh such as WireGuard or Tailscale to join your home network remotely.
3. Reach `https://memorysmith.home.arpa:7090` or `https://192.168.1.8:7090` only after the remote device can resolve the host name and trusts `MemorySmith-LAN-Root-CA.cer`.

Not recommended as a default right now:

- Direct router port forwarding of the current home-hosted MemorySmith instance to the public internet.
- Reusing `MemorySmith-LAN-Root-CA.cer` as an internet-facing trust anchor.

Why this is not a safe default yet:

- MemorySmith is still tracking remote-hardening work in the project wiki.
- The request guard blocks guarded remote API/MCP traffic when `MemorySmith:AllowRemoteApi=false`; if you enable remote API access and leave `MemorySmith:ApiKey` empty, guarded non-loopback `/api` and `/mcp` requests fail closed even though browser auth/setup routes remain reachable for LAN UI sign-in and bootstrap.
- Current security planning pages still call out transport hardening, proxy/header trust, and remote-safe defaults as active work rather than finished posture.

If you eventually want public internet access instead of VPN-only access, minimum expectations are:

1. Use a real public DNS name, not `memorysmith.home.arpa`.
2. Use a public CA certificate, typically via a reverse proxy or ACME/Let's Encrypt.
3. Keep HTTPS-only access.
4. Finish first-admin setup and use real authentication.
5. Disable compatibility settings that are only acceptable for local bootstrap scenarios.
6. If guarded `/api` or `/mcp` traffic must be remotely reachable, set a strong `MemorySmith:ApiKey` and treat that as mandatory, not optional. Browser auth/setup routes are intentionally exempt so remote UI login still works.

Until the remote-hardening tasks and validation matrix are closed, prefer VPN/private-mesh access over direct public exposure.

## Screenshot Backlog Template

- [ ] HTTPS-SETUP-01 dev certificate check success
- [ ] HTTPS-SETUP-02 launchSettings HTTPS profile snippet
- [ ] HTTPS-SETUP-03 app running on HTTPS with lock icon
- [ ] HTTPS-SETUP-04 `/health` over HTTPS
- [ ] HTTPS-SETUP-05 `/api/health/readiness` over HTTPS
- [ ] HTTPS-SETUP-06 certificate warning example (optional troubleshooting)
