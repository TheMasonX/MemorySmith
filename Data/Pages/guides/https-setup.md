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

## 2a. LAN Certificate Example For This Repository

For the current local-network setup, the concrete LAN certificate target is:

- Host name: `memorysmith.home.arpa`
- LAN IP: `192.168.1.8`
- HTTPS port: `7090`

Windows-native certificate artifacts have been generated under `artifacts/certs`:

- Root CA to trust on LAN clients: `artifacts/certs/MemorySmith-LAN-Root-CA.cer`
- Server PFX for Kestrel/service binding: `artifacts/certs/memorysmith.home.arpa-7090.pfx`
- Server CER for inspection/distribution: `artifacts/certs/memorysmith.home.arpa-7090.cer`
- Generated PFX password file: `artifacts/certs/memorysmith.home.arpa-7090-password.txt`

If clients should use the host name instead of the raw IP, make sure `memorysmith.home.arpa` resolves to `192.168.1.8` on those devices through router DNS or a hosts file entry.

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

If prompted by the browser, verify certificate details and confirm it is the local ASP.NET Core development certificate.

For other devices on the LAN, the built-in ASP.NET Core development certificate is usually only valid for `localhost`. That means `https-lan` will bind the HTTPS listener, but a phone or another PC will still see a certificate mismatch unless you use a certificate whose SAN matches the host name or IP that device connects to.

For a stronger LAN HTTPS path, use the Windows service redeploy script with an explicit certificate:

```powershell
.\Scripts\Redeploy-MemorySmithService.ps1 -UseHttps -HttpsPort 7090 -HttpsCertificatePath .\artifacts\certs\memorysmith.home.arpa-7090.pfx -HttpsCertificatePassword (Get-Content .\artifacts\certs\memorysmith.home.arpa-7090-password.txt -Raw)
```

That command keeps HTTP on `5089` and adds HTTPS on `7090` using the LAN cert whose SAN covers both `memorysmith.home.arpa` and `192.168.1.8`.

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

## Screenshot Backlog Template

- [ ] HTTPS-SETUP-01 dev certificate check success
- [ ] HTTPS-SETUP-02 launchSettings HTTPS profile snippet
- [ ] HTTPS-SETUP-03 app running on HTTPS with lock icon
- [ ] HTTPS-SETUP-04 `/health` over HTTPS
- [ ] HTTPS-SETUP-05 `/api/health/readiness` over HTTPS
- [ ] HTTPS-SETUP-06 certificate warning example (optional troubleshooting)
