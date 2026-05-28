# OAuth Provider Setup and Deployment Checklist

Date: 2026-05-17
Status: Historical rollout checklist. Verify against current runtime support before treating this as current-state guidance.

Purpose

- This document describes the rollout steps an operator or developer can use when enabling OAuth/OIDC applications for GitHub, Google, and Microsoft.
- Current runtime note as of 2026-05-26: GitHub is the only registered external sign-in handler in the shipping app. Google and Microsoft entries can be preconfigured for future rollout, but the UI should treat them as unsupported until matching auth handlers are registered.

Assumptions

- The current MemorySmith branch includes the metadata schema and admin scaffolding for providers (seeded provider rows for `GitHub`, `Google`, and `Microsoft`).
- The current app reads provider secrets from `MemorySmith:Auth:Providers:{Provider}:ClientId` and `MemorySmith:Auth:Providers:{Provider}:ClientSecret`.
- The app is run locally at `http://localhost:5089` for development and behind `https://yourdomain` in production. All production callback URLs must use HTTPS.
- Secrets must NOT be committed to source control. Use `dotnet user-secrets` for development and environment variables or a secrets store for production.

Quick summary (high level)

1. Register an OAuth app in each provider console (GitHub, Google, Azure). Record the Client ID and Client Secret.
2. Configure each app's Authorized Redirect URI to point at the intended MemorySmith callback endpoints. Active today: GitHub -> `https://<host>/signin-github` (for local dev: `http://localhost:5089/signin-github`). Future rollout only unless handlers are added: Google -> `https://<host>/signin-google`, Microsoft -> `https://<host>/signin-microsoft`.
3. Store Client ID/Secret securely (user-secrets or environment variables). Do not add secrets to `appsettings.json`.
4. If you are rolling out a provider that is not already registered at startup, add the auth wiring in `Program.cs`, then publish and restart the service.
5. Verify by visiting `/login` and exercising only the provider flows that are actually registered in the running build; confirm a provider link or user row is created in the DB and audit entries appear.

Detailed per-provider registration steps

GitHub (OAuth App)

- Console: <https://github.com/settings/developers> -> OAuth Apps -> New OAuth App
- Required fields:
  - Application name: MemorySmith (or your preferred name)
  - Homepage URL: `https://<your-host>` (for local dev use `http://localhost:5089`)
  - Authorization callback URL: `https://<your-host>/signin-github` (local dev: `http://localhost:5089/signin-github`)
- After creation copy the **Client ID** and **Client Secret**.
- Recommended scopes for sign-in: `read:user` and `user:email` (add additional scopes only if you need them).
- Notes: GitHub's OAuth flow is an authorization-code flow; the app should use a maintained provider such as `AspNet.Security.OAuth.GitHub` (NuGet: `AspNet.Security.OAuth.GitHub`) or an equivalent implementation.

Google (OpenID Connect)

- Console: <https://console.cloud.google.com/apis/credentials>
- Steps:
  1. Configure OAuth consent screen (required). Choose `External` for testing; add yourself as a test user.
  2. Create Credentials -> OAuth client ID -> Web application.
  3. Authorized redirect URIs: `https://<your-host>/signin-google` (local dev: `http://localhost:5089/signin-google`).
  4. Copy **Client ID** and **Client Secret**.
- Recommended scopes: `openid profile email`.
- Notes: Google often insists on HTTPS for production redirect URIs; localhost URIs are allowed for development.

Microsoft / Azure AD (Microsoft Account / Work or School)

- Portal: <https://portal.azure.com> -> Azure Active Directory -> App registrations -> New registration
- Steps:
  1. Name: `MemorySmith` (or your choice)
  2. Supported account types: choose according to your needs (Accounts in any organizational directory and personal Microsoft accounts is the broadest)
  3. Redirect URI (platform = Web): `https://<your-host>/signin-microsoft` (local dev: `http://localhost:5089/signin-microsoft`)
  4. After register -> Certificates & secrets -> New client secret -> copy value
  5. API permissions: add `User.Read` (Microsoft Graph) if you plan to read profile/email.
- Notes: For production, grant admin consent where appropriate.

Where to put ClientId/ClientSecret (recommended)

- Development (local, simple): use `dotnet user-secrets` inside the `MemorySmith.App` project directory:

```powershell
cd MemorySmith.App
dotnet user-secrets init
dotnet user-secrets set "MemorySmith:Auth:Providers:GitHub:ClientId" "<your-client-id>"
dotnet user-secrets set "MemorySmith:Auth:Providers:GitHub:ClientSecret" "<your-client-secret>"
dotnet user-secrets set "MemorySmith:Auth:Providers:Google:ClientId" "<your-client-id>"
dotnet user-secrets set "MemorySmith:Auth:Providers:Google:ClientSecret" "<your-client-secret>"
dotnet user-secrets set "MemorySmith:Auth:Providers:Microsoft:ClientId" "<your-client-id>"
dotnet user-secrets set "MemorySmith:Auth:Providers:Microsoft:ClientSecret" "<your-client-secret>"
```

- Production (Windows Service, container, cloud): use environment variables (double-underscore maps to nested configuration keys) or your host's secret store.

Examples (Windows PowerShell, machine-level):

```powershell
[Environment]::SetEnvironmentVariable("MemorySmith__Auth__Providers__GitHub__ClientId","<id>", "Machine")
[Environment]::SetEnvironmentVariable("MemorySmith__Auth__Providers__GitHub__ClientSecret","<secret>", "Machine")
[Environment]::SetEnvironmentVariable("MemorySmith__Auth__Providers__Google__ClientId","<id>", "Machine")
[Environment]::SetEnvironmentVariable("MemorySmith__Auth__Providers__Google__ClientSecret","<secret>", "Machine")
[Environment]::SetEnvironmentVariable("MemorySmith__Auth__Providers__Microsoft__ClientId","<id>", "Machine")
[Environment]::SetEnvironmentVariable("MemorySmith__Auth__Providers__Microsoft__ClientSecret","<secret>", "Machine")
```

- After setting machine-level environment variables, restart the service so the process inherits them.

Sample `appsettings` fragment (do NOT commit secrets)

```json
"MemorySmith": {
  "Auth": {
    "Providers": {
      "GitHub": { "ClientId": null, "ClientSecret": null },
      "Google": { "ClientId": null, "ClientSecret": null },
      "Microsoft": { "ClientId": null, "ClientSecret": null }
    }
  }
}
```

Provider wiring in the app (developer note)

- The app needs ASP.NET Core external auth handlers registered in `Program.cs` for provider sign-ins to work. The running app already includes a custom GitHub OAuth registration; the snippets below are rollout references for additional providers or future refactoring.

GitHub (requires `AspNet.Security.OAuth.GitHub`):

```csharp
builder.Services.AddAuthentication()
  .AddGitHub(options =>
  {
      options.ClientId = configuration["MemorySmith:Auth:Providers:GitHub:ClientId"];
      options.ClientSecret = configuration["MemorySmith:Auth:Providers:GitHub:ClientSecret"];
      options.Scope.Add("user:email");
      options.SaveTokens = true;
  });
```

Google (built-in):

```csharp
builder.Services.AddAuthentication()
  .AddGoogle(options =>
  {
      options.ClientId = configuration["MemorySmith:Auth:Providers:Google:ClientId"];
      options.ClientSecret = configuration["MemorySmith:Auth:Providers:Google:ClientSecret"];
  });
```

Microsoft (built-in):

```csharp
builder.Services.AddAuthentication()
  .AddMicrosoftAccount(options =>
  {
      options.ClientId = configuration["MemorySmith:Auth:Providers:Microsoft:ClientId"];
      options.ClientSecret = configuration["MemorySmith:Auth:Providers:Microsoft:ClientSecret"];
  });
```

Notes for operators: if the current app branch does not yet register these handlers, the provider rows (seeded in the DB) are only metadata; you must either merge the wiring above or ask the dev to enable the external handlers in the running build.

Local dev HTTPS and certificates

- For Google/Azure in production you must use HTTPS. For local dev you can use `http://localhost:5089` redirect URIs but some providers prefer/require HTTPS for non-localhost domains.
- Ensure ASP.NET Core dev certificate is trusted for local development (see `dotnet dev-certs https --trust`).

Testing and verification

1. Ensure secrets are set (user-secrets or environment variables).
2. If you updated machine env vars, restart the MemorySmith service:

  ```powershell
  Stop-Service -Name MemorySmith -ErrorAction SilentlyContinue
  Start-Service -Name MemorySmith
  # Check status
  Get-Service -Name MemorySmith | Select-Object Name, Status, DisplayName
  # Quick web check
  Invoke-WebRequest -UseBasicParsing http://localhost:5089/health
  ```

1. Open `https://<host>/login` (or `http://localhost:5089/login`) and click the provider button.
2. On success you should see an authenticated session; verify `/api/auth/me` returns `IsAuthenticated:true` and roles.
3. Check admin audit/history: `/admin` -> Audit and History tabs to confirm provider link and audit entries.

Rollback and safety

- If a provider misbehaves, remove or unset the corresponding environment variables and restart the service.
- Do not commit secrets to the repo.

Open questions & notes for the dev team (things you might ask them to confirm)

- Are the provider handlers already registered in `Program.cs` on this branch? If not, prefer the built-in Google and Microsoft handlers plus `AspNet.Security.OAuth.GitHub` for GitHub.
- Should we add UI buttons on the `/login` page for each enabled provider? (The codebase has a local password form already.)
- For production deployments, confirm where the service reads environment variables when running as a Windows Service (machine vs service-specific environment).

Support commands (summary)

Set secrets for local dev (in `MemorySmith.App` folder):

```powershell
cd MemorySmith.App
dotnet user-secrets init
dotnet user-secrets set "MemorySmith:Auth:Providers:GitHub:ClientId" "<id>"
dotnet user-secrets set "MemorySmith:Auth:Providers:GitHub:ClientSecret" "<secret>"
```

Set machine env vars (Windows):

```powershell
[Environment]::SetEnvironmentVariable("MemorySmith__Auth__Providers__GitHub__ClientId","<id>", "Machine")
[Environment]::SetEnvironmentVariable("MemorySmith__Auth__Providers__GitHub__ClientSecret","<secret>", "Machine")
# Repeat for Google and Microsoft
```

Publish and restart service (operator step after secrets/configured code):

```powershell
dotnet publish MemorySmith.App/MemorySmith.App.csproj -c Release -o artifacts/MemorySmith.App
Stop-Service -Name MemorySmith -ErrorAction SilentlyContinue
Start-Service -Name MemorySmith
```

If you want me to (a) wire provider handlers into `Program.cs` and (b) push that change and re-publish, say so and I will implement the code changes and run the redeploy steps.

---
Document created by the operator assistant. Ask for more detail for any specific provider or deployment target.
