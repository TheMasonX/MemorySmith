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

## 3. Add An HTTPS Launch Profile

The current project launch settings only include an `http` profile. Add an `https` profile in `MemorySmith.App/Properties/launchSettings.json`.

Use this profile block under `profiles`:

```json
"https": {
  "commandName": "Project",
  "dotnetRunMessages": true,
  "launchBrowser": true,
  "applicationUrl": "https://localhost:7090;http://localhost:5089",
  "environmentVariables": {
    "ASPNETCORE_ENVIRONMENT": "Development"
  }
}
```

This keeps HTTP available while enabling HTTPS as the primary local entrypoint.

> [!NOTE]
> Screenshot placeholder [HTTPS-SETUP-02]: `launchSettings.json` with the new `https` profile.

## 4. Run The App With HTTPS

From repo root:

```powershell
dotnet run --project MemorySmith.App --launch-profile https
```

Open:

- `https://localhost:7090`

If prompted by the browser, verify certificate details and confirm it is the local ASP.NET Core development certificate.

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
