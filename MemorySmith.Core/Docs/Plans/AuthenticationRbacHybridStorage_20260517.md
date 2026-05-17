# MemorySmith Authentication, RBAC, Audit, History, and Hybrid Storage Architecture Plan

Date: 2026-05-17  
Status: Architecture-first implementation plan for review  
Branch: `plan/auth-rbac-hybrid-storage-architecture-20260517`  
Primary decision level: high confidence on target shape, medium confidence on exact rollout defaults  
Overall confidence: 0.84

## 1. Executive Decision

MemorySmith should add authentication, identity linking, RBAC, queryable audit metadata, transparent JSONL audit mirrors, version history, and an admin panel without undoing the current local-first single-host architecture.

The recommended target is a hybrid model:

- `MemorySmith.App` remains the only deployable ASP.NET Core host.
- SQLite becomes the primary store for identity, provider links, roles, login history, admin settings, audit metadata, version metadata, API/service tokens, and semantic index metadata.
- File storage remains the source of truth for memory JSON, markdown pages, page assets, ONNX model artifacts, Lucene/vector indexes, and human-readable history artifacts.
- JSONL audit files remain as transparent append-only mirrors, but SQLite becomes the query path for the admin audit viewer.
- Authorization is enforced at the ASP.NET Core policy layer and again at application-service write boundaries, not only by hiding UI buttons.
- The admin panel should be built as new Blazor Server/MudBlazor pages in the existing app, not Razor Pages or a separate SPA.

This plan deliberately does not add auth middleware, schema files, or package references yet. Those are safe only after review because they change default access, API behavior, service deployment, and data migration posture.

## 2. Critical Corrections To The Draft Prompt

The original prompt is directionally good, but several details need tightening against the current repository:

1. SQLite should not be described as primary for memory/page content. In current MemorySmith, `Data/Memories` and `Data/Pages` are live wiki content and test fixtures. Replacing them with rows would fight the project wiki policy. SQLite should be primary for security/admin metadata and indexes.
2. MemorySmith does not currently have auth. There is only `MemorySmithRequestGuardMiddleware`, which blocks non-loopback callers by default and optionally requires `X-Api-Key` for `/api` and `/mcp`.
3. The current audit log is not a complete security audit system. `FileEventStore` writes `MemoryEvent` JSONL with `Timestamp`, `MemoryId`, `Action`, and `Details`; it has no actor, provider, request, IP, user agent, before/after hash, diff reference, or outcome.
4. Page writes bypass the memory application service. `PagesController`, `Pages.razor`, and chat page writes call `IPageService`/`FilePageService` directly, so page audit/history needs a new application-service boundary or a decorator.
5. MCP tools are currently read-only, but the endpoint is identityless. Future audit attribution for MCP should move from shared API key to scoped service tokens mapped to a user or service principal.
6. The previous broad refactor plan explicitly marked multi-user auth, roles, and account management out of scope. This plan supersedes that out-of-scope boundary only if accepted by the human reviewer.
7. Git as internal version control is attractive for human diffability, but should not be the core write path for MVP because service accounts, Git availability, locking, commits, ignore rules, and rollback semantics introduce operational complexity.

Confidence: 0.91.

## 3. Current Codebase Ground Truth

### 3.1 Solution and Host

Active solution shape from `MemorySmith.slnx`:

- `MemorySmith.Core`
- `MemorySmith.Storage`
- `MemorySmith.App`
- `MemorySmith.Tests`
- `MemorySmith.Benchmarks`

`MemorySmith.App/Program.cs` is the single host. It registers Razor components, MudBlazor, controllers, storage, page service, event store, memory index, semantic embeddings, maintenance, diagnostics, chat providers, and MCP/controllers in one ASP.NET Core process.

There is no `AddAuthentication`, `UseAuthentication`, `AddAuthorization`, `UseAuthorization`, `AuthorizeRouteView`, or `[Authorize]` usage in the active app.

Confidence: 0.99.

### 3.2 UI Routes

Current routable Blazor pages:

| Route | Component | Current access |
|---|---|---|
| `/` | `Home.razor` | Unauthenticated |
| `/memories` | `MemoryViewer.razor` | Unauthenticated read/write/delete |
| `/pages` and `/pages/{*Slug}` | `Pages.razor` | Unauthenticated read/write/delete |
| `/chat` | `Chat.razor` | Unauthenticated chat/agent UI |
| `/variables` | `Variables.razor` | Unauthenticated variable management |
| `/health` | `HealthStats.razor` | Unauthenticated health/diagnostics UI |
| `/about` | `About.razor` | Unauthenticated |

`Routes.razor` uses `RouteView`, not `AuthorizeRouteView`. `NavMenu.razor` has no admin link and no auth-sensitive navigation.

Confidence: 0.98.

### 3.3 REST and MCP Surface

Current controllers:

| Route | Controller | Notes for RBAC |
|---|---|---|
| `/api/memories` | `MemoriesController` | CRUD, search, semantic search, hybrid search, usage increment |
| `/api/pages` | `PagesController` | Page list/search/get/html/save/update/delete |
| `/api/search` | `SearchController` | Combined memory/page search |
| `/api/chat` | `ChatController` | Chat config and send |
| `/api/stats` | `StatsController` | Stats, telemetry, activity buckets |
| `/api/health` | `HealthController` | live/ready |
| `/api/diagnostics` | `DiagnosticsController` | Redacted-ish but still path-heavy operational diagnostics |
| `/api/source-links` | `SourceLinksController` | Source-link operations |
| `/mcp` | `McpController` | HTTP JSON-RPC MCP endpoint |

MCP tools exposed today:

- `memorysmith_search`
- `memorysmith_semantic_search`
- `memorysmith_hybrid_search`
- `memorysmith_context_pack`
- `memorysmith_get`
- `memorysmith_source_bundle`
- `memorysmith_find_by_source`

All current MCP tools are read-only, but they can expose memory/page/source content. They should require `CanView` at minimum and a stronger `CanReadSourceBundle` policy if source bundles are considered more sensitive than wiki reads.

Confidence: 0.96.

### 3.4 Current Storage

Current storage abstractions:

- `IMemoryStore`: `Load`, `Save`, `Delete`, `LoadAll`.
- `IEventStore`: `AppendEvent`, `GetEvents`.
- `IVarStore`: variable file persistence.
- `IPageService`: page list/search/get/save/asset/delete/render.

Current file-backed implementations:

- `FileMemoryStore` stores `MemoryRecord` JSON files under `Data/Memories/{Status}`.
- `FilePageService` stores markdown under `Data/Pages`, assets under `Data/Pages/assets`, and serves assets through `/page-assets`.
- `FileEventStore` appends JSONL to `Data/Events/audit.log` by default.
- `FileVarStore` stores `Data/vars.json`.

Important properties:

- Memory IDs are sanitized and validated.
- Memory writes use a temp-file-then-move pattern.
- Page slugs are normalized and checked under the pages root.
- Search, stats, context packs, and source lookup currently use full-store scans.
- Current tests rely on copying `Data/Memories` to temp storage before mutation.

Confidence: 0.95.

### 3.5 Current Search and Retrieval

Memory search is implemented in `MemoryApplicationService`:

- Lexical search uses Lucene.NET `StandardAnalyzer` tokenization and local weighted scoring.
- Semantic search uses optional ONNX Runtime embeddings when model/vocab are available; otherwise it falls back to token/tag/title/reference/alias scoring.
- Hybrid search fuses lexical and semantic ranks with Reciprocal Rank Fusion.
- Context packs use hybrid search plus references/conflicts/backlinks.

Page search is implemented separately in `FilePageService` by enumerating markdown files and scoring title/markdown text.

Current semantic search is exact in-memory ranking over filtered memories. There is no durable vector index yet. The plan should integrate semantic index metadata, not assume a vector index already exists.

Confidence: 0.94.

### 3.6 Current Security Boundary

`MemorySmithRequestGuardMiddleware` currently does two things:

- Rejects non-loopback callers when `MemorySmith:AllowRemoteApi` is false.
- Requires `X-Api-Key` for paths under `/api` and `/mcp` when `MemorySmith:ApiKey` is configured.

This is local deployment hardening, not user authentication. A shared API key cannot support identity linking, RBAC, role changes, user audit attribution, provider tracking, or per-user login history.

Confidence: 0.99.

## 4. Requirements Interpreted For This Repository

| Area | Requirement | Repository-specific interpretation |
|---|---|---|
| Authentication | GitHub, Google, Microsoft OAuth plus local password fallback | Use ASP.NET Core authentication handlers and local cookie auth. Use provider subject IDs, not email, as stable external identity keys. |
| Identity linking | One MemorySmith user can link multiple providers | Store external links separately from users. Enforce one provider subject maps to one user. Self-linking allowed after sign-in. |
| RBAC | Admin, Editor, Viewer; anonymous users are Viewer | Represent roles in SQLite. Policy handler can grant anonymous Viewer when configured. |
| Default role behavior | Auto-Editor or explicit promotion | Default should be explicit promotion. Auto-Editor should be an admin setting. |
| Audit logging | Page edits, memory edits, settings, roles, model/index reloads, admin actions | SQLite audit metadata plus weekly JSONL mirrors. Add before/after hashes and history artifact references. |
| Version history | Pages and memories in `.history` folders | Page snapshots by default; memory JSON Patch diffs plus periodic checkpoints. Metadata in SQLite. |
| Admin panel | User/provider/role/audit/history/model/index/health | New Blazor Server pages under `/admin` using existing MudBlazor style. |
| Hybrid storage | SQLite primary DB, file content storage | SQLite primary for auth/admin/audit/history metadata. Files remain source of truth for pages/memories/history artifacts/models/indexes. |
| Abstraction layer | Interfaces and factory for DB providers | Add store interfaces plus `IDatabaseProviderFactory`; implement only SQLite initially. Future providers are explicit future work. |

Confidence: 0.89.

## 5. Non-Goals For The First Reviewed Implementation

These are intentionally out of the first implementation slice unless the reviewer changes scope:

- Public internet multi-tenant SaaS hardening.
- Per-memory or per-page ACLs beyond global role permissions.
- 2FA enforcement.
- Email verification enforcement.
- Storing OAuth access/refresh tokens for provider APIs.
- Replacing memory/page JSON/markdown with database content rows.
- Full Identity UI scaffolding or a separate SPA.
- PostgreSQL, SQL Server, or LiteDB implementation in the first slice.
- Git-backed commits as the primary version history mechanism.
- Durable vector index implementation unless folded into the semantic search roadmap separately.

Confidence: 0.87.

## 6. Assumptions

1. MemorySmith remains local-first and can keep anonymous read access by default.
2. Anonymous read access maps to Viewer only, not Editor.
3. Existing memory/page content remains readable without account creation.
4. Write actions should require an authenticated Editor or Admin once RBAC enforcement is enabled.
5. First admin bootstrap can be loopback-only because the app is normally local.
6. OAuth client secrets are supplied through user-secrets, environment variables, or a future secret provider, not stored in plain SQLite settings.
7. Local password fallback can be disabled through configuration.
8. Local passwords use framework password hashing and lockout/rate limiting rather than custom cryptography.
9. The admin panel uses existing Blazor Server plus MudBlazor conventions.
10. `Data/Memories` remains stable as live project wiki and integration-test fixture source.
11. Audit metadata is queryable in SQLite, but transparent JSONL mirrors remain useful for inspection and export.
12. The app may run as a Windows Service, so bootstrapping, backups, and secrets cannot depend only on interactive console flows.
13. Future database providers are valuable as an abstraction boundary, but only SQLite should be implemented until the auth/admin model settles.
14. Remote/shared deployments require stricter diagnostics redaction and must not rely on the existing shared API key as user identity.

Confidence: 0.86.

## 7. Open Decisions And Recommendations

| ID | Question | Recommendation | Confidence | Needs review? |
|---|---|---|---:|---|
| O1 | Should users self-link providers? | Yes, from an account/security page after signing in. Prevent unlinking the last usable login method. | 0.93 | No, reviewer answered yes. |
| O2 | Should admins disable providers globally? | Yes. Store enabled/disabled in DB, keep secrets in configuration. | 0.91 | No, reviewer answered yes. |
| O3 | Should anonymous access be allowed? | Yes, configurable, default `Viewer`. | 0.91 | No, reviewer answered configurable/default allowed. |
| O4 | Audit rotation cadence? | Weekly by default. Add `Audit.JsonlRotation=Weekly`; allow Daily/Weekly/Monthly. | 0.88 | Minor config naming review. |
| O5 | Compress rotated logs? | Yes, configurable. Default true for rotated logs older than current period. | 0.82 | Minor. |
| O6 | Page history snapshots or diffs? | Full markdown snapshots. They are human-readable, diff-friendly, and simpler to restore. Generate diffs on demand in UI. | 0.78 | Yes, reviewer left choice open. |
| O7 | Memory history full JSON or diffs? | JSON Patch-style diffs plus periodic full checkpoints every N versions or days. | 0.74 | Yes, reviewer requested diffs and mentioned Git. |
| O8 | Use Git internally for versioning? | Not as core MVP. Optional future export/backup mode only. | 0.76 | Yes. |
| O9 | Admin panel technology? | Existing Blazor Server/MudBlazor page set under `/admin`. | 0.95 | No, reviewer requested existing tech. |
| O10 | Require 2FA? | No for MVP. Leave schema room for future passkeys/2FA. | 0.90 | No, reviewer answered no. |
| O11 | Local password rate limiting? | Yes. Combine account lockout and IP/user partitioned rate limits. | 0.92 | Defaults need review. |
| O12 | OAuth email verification? | Do not enforce yet. Store provider email verification claims when available. | 0.84 | No, reviewer said future. |
| O13 | JSON backup of users? | No. Use SQLite online backup plus archive manifest. Keep one canonical user format: SQLite. | 0.86 | Yes, reviewer asked for better archive solution. |
| O14 | Backward compatibility write behavior? | Prefer setup-gated writes. Optionally provide one temporary `OpenLocalEditorCompatibility` flag for existing local workflows. | 0.63 | Yes. |
| O15 | API key future? | Replace shared API key with scoped API/service tokens stored hashed in SQLite; keep existing `MemorySmith:ApiKey` only as migration bridge. | 0.85 | Yes. |

## 8. Evidence-Based Claims

| Claim | Evidence | Score |
|---|---|---:|
| MemorySmith currently has no user authentication or RBAC. | No `AddAuthentication`, `UseAuthentication`, `AddAuthorization`, `UseAuthorization`, or `[Authorize]` in `MemorySmith.App`; only request guard middleware exists. | 1.00 |
| The current event log is insufficient for security audit trails. | `MemoryEvent` has only timestamp, memory ID, action, and details; `FileEventStore` writes one JSON object per line. OWASP logging guidance calls for when/where/who/what, result status, interaction ID, user identity, and higher-risk admin/security events. | 0.97 |
| Blazor Server auth should be integrated with cascading authentication state and route authorization. | Microsoft Blazor auth docs describe `AuthenticationStateProvider`, `AddCascadingAuthenticationState`, `AuthorizeRouteView`, and `AuthorizeView`. Current `Routes.razor` uses `RouteView`. | 0.94 |
| ASP.NET Core policy/role authorization is the right enforcement layer for controllers and components. | Microsoft role authorization docs describe `[Authorize(Roles=...)]`, `AuthorizeView Roles`, and policy builders. MemorySmith has controllers and Razor components matching those integration points. | 0.95 |
| Multiple OAuth providers are supported by ASP.NET Core, but GitHub needs either a third-party provider or custom handler. | Microsoft social auth docs show chained external providers for Google/Microsoft/etc.; aspnet-contrib provider docs include `AddGitHub`; GitHub docs describe OAuth authorization code flow. | 0.88 |
| Provider subject IDs must be treated as stable identity keys; email is display/secondary data. | OAuth/OIDC providers expose subject/user IDs; GitHub docs require revalidating user identity after every sign-in because the signed-in account can change. | 0.86 |
| Local password fallback requires throttling/lockout and generic failure behavior. | OWASP Authentication Cheat Sheet recommends login throttling, account lockout considerations, generic auth failure messages, password strength controls, and logging auth successes/failures. | 0.92 |
| SQLite is a good local metadata store but has concurrency/migration constraints. | SQLite WAL docs state readers and writers can proceed concurrently but only one writer exists; EF Core SQLite docs list schema/migration limitations. | 0.84 |
| JSONL plus SQLite is justified for audit because MemorySmith needs both transparent local files and queryable admin views. | OWASP logging guidance accepts file system or database logs and emphasizes purpose, event attributes, protection, and verification. Current `FileEventStore` already gives a local JSONL pattern. | 0.82 |
| Version history should sit at application-service boundaries, not only in storage. | Memory writes flow through `MemoryApplicationService`, but page writes currently call `IPageService` directly from API/UI/chat. A history layer needs actor/context and before/after hashes, which storage alone does not know. | 0.91 |
| Keeping memory/page files as source of truth preserves core MemorySmith behavior. | README and project wiki document `Data/Memories` and `Data/Pages` as live wiki content and test fixture source. Tests copy `Data/Memories` before mutation. | 0.96 |
| Admin settings and role/provider changes must themselves be audited. | OWASP Logging Cheat Sheet lists configuration changes, user administration, privilege changes, and administrator actions as higher-risk functionality to log. | 0.95 |

External source references are listed at the end of this document.

## 9. Target Architecture

### 9.1 High-Level Runtime Shape

```text
Browser / REST clients / MCP clients
    -> ASP.NET Core authentication schemes
        -> Cookie auth for browser/Blazor
        -> External OAuth/OIDC challenge handlers
        -> Local password sign-in endpoints
        -> Scoped API token scheme for automation/MCP
    -> Authorization policies
        -> CanViewMemorySmith
        -> CanEditMemorySmith
        -> CanAdminMemorySmith
        -> CanReadSourceBundle
    -> MemorySmith.App
        -> Blazor UI and controllers
        -> Admin services
        -> MemoryApplicationService
        -> PageApplicationService or audited page-service decorator
        -> Tool executor for MCP/chat retrieval
    -> MemorySmith.Storage
        -> File stores for content
        -> SQLite stores for identity/admin/audit/history metadata
        -> Database provider factory
    -> Data/
        -> memorysmith.db
        -> Memories/
        -> Pages/
        -> Events/
        -> .history/
        -> Models/
        -> Graph/ or Indexes/
```

### 9.2 Project Placement

Recommended placement:

| Project | New responsibilities |
|---|---|
| `MemorySmith.Core` | Domain models for users, roles, provider links, audit metadata, version metadata, semantic index metadata; no ASP.NET dependencies. |
| `MemorySmith.Storage` | Persistence interfaces, SQLite provider, migration runner, provider factory, database-backed stores, file history store. |
| `MemorySmith.App` | Auth scheme setup, cookie/external login endpoints, Blazor auth state, policies, admin pages, admin services, current-user context, authorization handlers. |
| `MemorySmith.Tests` | NUnit tests for schema migration, stores, policies, authorization behavior, audit/history, admin services, API contracts. |

Avoid putting database or ASP.NET authentication dependencies in `MemorySmith.Core`.

Confidence: 0.88.

### 9.3 Configuration Shape

Proposed top-level appsettings shape:

```json
{
  "MemorySmith": {
    "Database": {
      "Provider": "SQLite",
      "ConnectionString": "Data Source=../Data/memorysmith.db",
      "ApplyMigrationsOnStartup": true,
      "UseWal": true,
      "BusyTimeoutSeconds": 30
    },
    "Auth": {
      "Enabled": true,
      "AnonymousAccess": "Viewer",
      "AuthenticatedDefaultRole": "Viewer",
      "AutoEditorForAuthenticatedUsers": false,
      "LocalPasswordEnabled": true,
      "RequireHttpsForRemoteAuth": true,
      "Setup": {
        "AllowLoopbackBootstrap": true
      },
      "RateLimits": {
        "LoginPermitLimit": 5,
        "LoginWindowMinutes": 15,
        "LockoutMinutes": 15,
        "MaxProgressiveLockoutMinutes": 60
      },
      "Providers": {
        "GitHub": { "Enabled": false },
        "Google": { "Enabled": false },
        "Microsoft": { "Enabled": false }
      }
    },
    "Audit": {
      "JsonlEnabled": true,
      "JsonlPath": "../Data/Events/audit-{yyyy}-W{week}.jsonl",
      "JsonlRotation": "Weekly",
      "CompressRotatedLogs": true,
      "HashChainEnabled": true
    },
    "History": {
      "RootPath": "../Data/.history",
      "PageMode": "Snapshot",
      "MemoryMode": "JsonPatchWithCheckpoints",
      "MemoryCheckpointEveryVersions": 20
    }
  },
  "Authentication": {
    "GitHub": { "ClientId": null, "ClientSecret": null },
    "Google": { "ClientId": null, "ClientSecret": null },
    "Microsoft": { "ClientId": null, "ClientSecret": null }
  }
}
```

Secrets should be provided through user-secrets, environment variables, Windows service configuration, or a future secret provider. They should not be committed into `appsettings.json`.

Confidence: 0.82.

## 10. Authentication Design

### 10.1 Recommended Implementation Style

Use ASP.NET Core authentication primitives rather than hand-rolling protocol/session behavior:

- Cookie authentication for browser sessions.
- External provider handlers for OAuth/OIDC sign-in.
- ASP.NET Core Data Protection for cookie/auth state protection and key rotation.
- Framework password hashing, ideally `PasswordHasher<TUser>` or ASP.NET Core Identity pieces, for local passwords.
- ASP.NET Core rate limiting for login endpoints and API/token endpoints.
- Custom MemorySmith persistence stores so identity data follows the planned database-provider factory.

Two viable implementation approaches:

| Option | Description | Pros | Cons | Recommendation |
|---|---|---|---|---|
| A | Use ASP.NET Core Identity Core with custom stores backed by MemorySmith interfaces | Mature password hashing, lockout, security stamps, external login linking | More interfaces and Identity concepts; table schema must be mapped carefully | Preferred if team accepts complexity |
| B | Use cookie auth plus small custom auth services using `PasswordHasher<TUser>` and OAuth handlers | Smaller surface, closer to requested schema | More security responsibility on MemorySmith code | Acceptable only if kept narrow and heavily tested |

Recommended: Option A if the implementation team is comfortable with Identity store interfaces; otherwise Option B with explicit review of every auth decision. Do not implement custom password hashing.

Confidence: 0.79.

### 10.2 Providers

Provider behavior:

- GitHub: use GitHub OAuth authorization code flow. Prefer a maintained provider such as aspnet-contrib `AddGitHub` or OpenIddict client web providers. Store GitHub user ID as `ProviderSubject`.
- Google: use Google external login/OIDC support. Store `sub` as `ProviderSubject`; store email and `email_verified` as metadata.
- Microsoft: use Microsoft Account or Microsoft identity platform handler depending on target audience. Store stable object/sub claim as `ProviderSubject`.
- Local: not a provider link in the OAuth sense, but can be represented in provider history as provider `LocalPassword` for login history and audit context.

Provider disabling:

- Admin can disable a provider globally.
- Disabling prevents new sign-ins and new links.
- Existing sessions may either remain valid until expiry or be invalidated by a security-stamp sweep. Recommendation: invalidate active sessions for disabled providers unless this creates too much friction.

Identity linking rules:

1. Signed-in users can link additional providers from account settings.
2. A provider subject can link to only one MemorySmith user.
3. Users cannot unlink their last working sign-in method.
4. Admins can unlink a provider, but not if it would lock out the last Admin unless a new local password or other provider is confirmed.
5. Email address collision is not enough to auto-link accounts. If two providers report the same email, require signed-in self-linking or admin merge.
6. Store email verification claims when available, but do not enforce verification in MVP.

Confidence: 0.87.

### 10.3 Local Password Fallback

Defaults:

- `Auth.LocalPasswordEnabled=true` for local/offline fallback.
- Allow disabling local passwords entirely for deployments that require OAuth only.
- Minimum password length: 15 characters when no 2FA is enforced.
- Maximum password length: at least 128 characters.
- Allow spaces and all printable characters.
- Do not require composition rules like uppercase/lowercase/symbols.
- Use generic login failure messages.
- Log successes, failures, and lockouts.
- Never log passwords or password hashes.

Suggested lockout/rate-limit defaults:

| Control | Default |
|---|---|
| Account failed login threshold | 5 failures within 15 minutes |
| Soft lockout duration | 15 minutes |
| Progressive lockout max | 60 minutes |
| IP login limiter | 20 login attempts per 15 minutes per IP hash |
| Username/account limiter | 5 attempts per 15 minutes per normalized username |
| API token auth failure limiter | 60 failures per hour per IP/token prefix |
| Queueing | Disabled for login; return 429 with `Retry-After` |

These defaults balance brute-force resistance with local-first usability. The account lockout design must avoid letting an attacker permanently deny service to another user. Admin reset and local break-glass recovery need review.

Confidence: 0.81.

### 10.4 Bootstrap

The first-admin flow is the highest-risk UX/security transition.

Recommended bootstrap:

1. On startup, if the Users table does not exist, run migrations.
2. If no Admin user exists, enable `/admin/setup` only for loopback callers.
3. Require either a one-time setup token generated at startup or a configured `MemorySmith:Auth:Setup:BootstrapTokenHash`.
4. The setup page creates the first Admin with local password and optional provider link.
5. After first Admin exists, `/admin/setup` returns 404 or redirects to login.
6. Log bootstrap completion in audit metadata and JSONL mirror.

Windows Service concern: if console logs are unavailable, the setup token must be discoverable through a secure local-only method, such as a one-time file under `Data/Setup` with restrictive ACLs, or provided up front through configuration.

Open review item: whether loopback-only setup without a token is acceptable for a local app. Recommendation: still use token.

Confidence: 0.76.

### 10.5 API and MCP Automation Tokens

The existing `MemorySmith:ApiKey` is a shared bearer secret. It should not be the long-term identity mechanism.

Recommended replacement:

- Add scoped API/service tokens created by Admins.
- Store only a hash of each token.
- Show token once on creation.
- Assign token owner: either a user or service principal.
- Assign scopes and/or roles: `memory:read`, `page:read`, `source:read`, `memory:write`, `page:write`, `admin:read`, `admin:write`.
- Default MCP token scopes: read-only memory/page/context/source, no write.
- Audit token prefix, owner, provider `ApiToken`, scopes, and request ID.

Migration bridge:

- Keep `MemorySmith:ApiKey` for one release as a legacy local automation bridge.
- Treat legacy key as a service principal with configured role, default Viewer.
- Emit diagnostics warning when legacy API key is used.

Confidence: 0.86.

## 11. RBAC Design

### 11.1 Roles

| Role | Description | Default capabilities |
|---|---|---|
| Anonymous | Not a stored role. Unauthenticated caller. | Viewer capabilities only when `AnonymousAccess=Viewer`. |
| Viewer | Read-only user. | Browse/search/get memories and pages; use chat read context; view limited health; call read-only MCP tools. |
| Editor | Content author. | Viewer plus create/update/delete memories/pages; upload page assets; approve agent writes; manage variables if approved. |
| Admin | System operator. | Editor plus user/provider/role management, settings, audit viewer, history restore, model/index controls, diagnostics, API tokens. |

Potential future roles:

- Auditor: view audit/history without admin mutation rights.
- Operator: model/index/health controls without user management.

Do not add future roles until needed. The policy system can leave room for them.

Confidence: 0.90.

### 11.2 Policies

Recommended ASP.NET Core policies:

| Policy | Grants |
|---|---|
| `CanViewMemorySmith` | Anonymous if enabled, Viewer, Editor, Admin |
| `CanEditMemorySmith` | Editor, Admin |
| `CanAdminMemorySmith` | Admin |
| `CanManageUsers` | Admin |
| `CanManageSettings` | Admin |
| `CanViewAudit` | Admin, future Auditor |
| `CanRestoreHistory` | Admin, optionally Editor for content restore after review |
| `CanReadSourceBundle` | Viewer by default for local-only; review for remote deployments |
| `CanUseChat` | Viewer, Editor, Admin; anonymous if Viewer enabled |
| `CanApproveAgentWrites` | Editor, Admin and `Chat.AgentWritesEnabled` plus approval flow |

Authorization must be enforced in three places:

1. Controllers and routeable components through policies.
2. Application services before mutations and sensitive reads.
3. MCP/tool executor before tool execution.

UI-only hiding is not enforcement.

Confidence: 0.94.

### 11.3 Endpoint Authorization Matrix

| Surface | Action | Policy |
|---|---|---|
| `/memories` UI | View/search | `CanViewMemorySmith` |
| `/memories` UI | Create/update/delete/usage | `CanEditMemorySmith` |
| `/pages` UI | View/search/render | `CanViewMemorySmith` |
| `/pages` UI | Save/delete/upload asset | `CanEditMemorySmith` |
| `/chat` UI/API | Chat with read context | `CanUseChat` |
| `/chat` agent writes | Approve/apply writes | `CanApproveAgentWrites` |
| `/variables` UI/API | View variables | `CanViewMemorySmith` or `CanEditMemorySmith` after review |
| `/variables` UI/API | Modify variables | `CanEditMemorySmith` or Admin after review |
| `/health` UI | Basic health | Viewer |
| `/api/diagnostics` | Full paths/config | Admin; redact for non-admin if any limited endpoint remains |
| `/api/health/live` | Liveness | Allow anonymous |
| `/api/health/ready` | Readiness | Viewer or anonymous local-only; review for remote |
| `/mcp` read tools | Search/get/context | `CanViewMemorySmith` |
| `/mcp` source bundle | Read source slices | `CanReadSourceBundle` |
| `/admin/*` | Admin panel | `CanAdminMemorySmith` unless audit-only role added |
| `/api/admin/*` | Admin automation | `CanAdminMemorySmith` |

Confidence: 0.85.

### 11.4 Application-Service Enforcement

Add a current-user abstraction in `MemorySmith.App`, for example:

```csharp
public interface ICurrentUserContext
{
    string? UserId { get; }
    string DisplayName { get; }
    string AuthScheme { get; }
    string? Provider { get; }
    bool IsAuthenticated { get; }
    IReadOnlyList<string> Roles { get; }
    string ActorKind { get; }
}
```

Then app services can accept authorization decisions without referencing `HttpContext` directly everywhere. Controllers/components continue using ASP.NET Core policies; app services protect non-HTTP callers such as maintenance, chat agent, or tests.

Recommended patterns:

- Use `IAuthorizationService` for user-facing paths.
- Use explicit system actor context for maintenance/index/model operations.
- Use service principal context for API tokens/MCP clients.
- Unit test app-service methods with Viewer/Editor/Admin/Anonymous contexts.

Confidence: 0.86.

## 12. Audit Logging Design

### 12.1 Two-Layer Audit

Use both layers, with different purposes:

| Layer | Purpose | Source of truth? |
|---|---|---|
| SQLite `AuditMetadata` | Query, filter, admin UI, integrity chain, relationship to version metadata | Yes for audit query |
| JSONL rotated files | Transparent local inspection, export, append-only mirror, disaster recovery | Mirror/export |

SQLite should be used for admin filtering by actor, action, target, outcome, time, provider, and correlation ID.

JSONL should contain enough information to inspect the same event without SQLite tooling.

Confidence: 0.89.

### 12.2 Event Types

Audit these events at minimum:

- Authentication success/failure/logout/lockout.
- Provider link/unlink.
- Provider enable/disable.
- User create/disable/delete/merge.
- Role assign/remove/default-role setting changes.
- Page create/update/delete/asset upload/history restore.
- Memory create/update/delete/usage increment/history restore.
- Settings changes.
- API/service token create/revoke/use failure.
- MCP tool calls, at least for source bundle and large context exports; optionally aggregate routine searches.
- Chat agent write proposals and approvals/rejections.
- Model reloads, ONNX model path changes, semantic index rebuilds.
- Maintenance tasks that mutate records or indexes.
- Diagnostics/admin data exports.
- Audit log read/export operations.
- Application startup/shutdown and migration application.

For routine search queries, the current `Query:{kind}` events are useful operational telemetry but can be noisy. Recommendation: keep query events in activity telemetry, but let audit detail be configurable for privacy and log volume.

Confidence: 0.86.

### 12.3 Audit Fields

Recommended audit metadata fields:

| Field | Notes |
|---|---|
| `AuditId` | GUID/ULID string. |
| `Sequence` | Monotonic SQLite integer for local ordering. |
| `OccurredAtUtc` | UTC event time. |
| `RecordedAtUtc` | UTC persistence time if different. |
| `ActorUserId` | Nullable for anonymous/system. |
| `ActorDisplay` | Denormalized display string at event time. |
| `ActorKind` | Anonymous/User/ServiceToken/System. |
| `AuthScheme` | Cookie/OAuth/API token/System. |
| `Provider` | GitHub/Google/Microsoft/LocalPassword/ApiToken/System. |
| `RoleSnapshotJson` | Roles/capabilities at time of action. |
| `Action` | Stable action ID, e.g. `memory.updated`. |
| `TargetKind` | Memory/Page/User/Role/Provider/Setting/Model/Index/Audit. |
| `TargetId` | Record ID, slug, user ID, setting key, etc. |
| `Outcome` | Success/Failure/Denied/Pending. |
| `Reason` | Failure/denial reason, sanitized. |
| `BeforeHash` | SHA-256 of canonical before content/metadata. |
| `AfterHash` | SHA-256 of canonical after content/metadata. |
| `DiffRef` | Relative path to history diff/snapshot if available. |
| `RequestId` | `HttpContext.TraceIdentifier` or generated background ID. |
| `CorrelationId` | Cross-event operation ID. |
| `IpHash` | HMAC or salted hash, not raw IP by default. |
| `UserAgentHash` | Hash plus optional short family string. |
| `DetailsJson` | Sanitized structured details. |
| `PreviousAuditHash` | Hash-chain previous event hash. |
| `AuditHash` | Hash over canonical event fields. |

Do not log passwords, tokens, OAuth access tokens, client secrets, raw auth cookies, or full request bodies by default.

Confidence: 0.92.

### 12.4 JSONL Rotation

Reviewer preference: weekly rotation, compression configurable.

Recommended file layout:

```text
Data/Events/
  audit-2026-W20.jsonl
  audit-2026-W19.jsonl.gz
  audit-2026-W18.jsonl.gz
```

Settings:

- `Audit.JsonlRotation`: `Daily`, `Weekly`, `Monthly`.
- `Audit.CompressRotatedLogs`: true/false.
- `Audit.RetentionDays`: optional future setting, default no automatic deletion.
- `Audit.HashChainEnabled`: true by default.

Rotation should be deterministic UTC-based unless reviewer wants local time. Recommendation: UTC.

Confidence: 0.83.

### 12.5 Dual-Write and Recovery

There is no true atomic transaction across SQLite and the file system. Design for recovery:

1. Generate operation/correlation ID.
2. Read before state and compute `BeforeHash`.
3. Write history artifact to temp path.
4. Start SQLite transaction and insert pending version/audit metadata if needed.
5. Save current file through existing file store.
6. Move history artifact to final path.
7. Commit SQLite transaction as success.
8. Append JSONL mirror.
9. If JSONL append fails, record health warning and retry/export later; do not roll back successful content mutation.
10. On startup, run a recovery scan for pending metadata or orphan history temp files.

If SQLite audit metadata cannot be written for a user-visible mutation, fail closed for Admin/settings/user/role/security actions. For memory/page content writes, reviewer must choose fail-closed versus allow-with-health-warning. Recommendation: fail closed once auth is enforced; during migration, allow with health warning may reduce upgrade risk.

Confidence: 0.68.

## 13. Version History Design

### 13.1 History Layout

Recommended layout under the deployment data root:

```text
Data/.history/
  pages/
    architecture/
      000001.md
      000002.md
      metadata.jsonl
  memories/
    project-wiki-active-architecture/
      000001.snapshot.json
      000002.patch.json
      000020.snapshot.json
      metadata.jsonl
```

SQLite stores queryable metadata and points to these files. The files remain human-readable where practical.

Confidence: 0.79.

### 13.2 Page History

Recommendation: full Markdown snapshots.

Rationale:

- Markdown is already a diff-friendly text format.
- Page snapshots are easy to inspect and restore without custom tooling.
- The page editor can generate diffs on demand using a diff library or server-side line comparison.
- Page assets need separate handling; history stores markdown references and asset metadata, not duplicate large image/video content by default.

Open question: whether page asset uploads should be versioned. Recommendation: record asset upload metadata and hash, but do not duplicate asset binaries unless asset replacement/deletion is added.

Confidence: 0.78.

### 13.3 Memory History

Recommendation: JSON Patch-style structured diffs plus periodic snapshots.

Rationale:

- Reviewer prefers diffs for file size constraints.
- Memory records can contain up to 20,000 characters today; future records may grow.
- Pure diff chains can become expensive to restore, so periodic snapshots bound restore cost.

Patch shape should be canonical and stable. If adopting RFC 6902 JSON Patch, add tests for arrays (`Tags`, `References`, `Conflicts`, `SourceLinks`) because array diffs can be noisy. A simpler domain patch may be more readable:

```json
{
  "format": "memorysmith.memory-diff.v1",
  "beforeHash": "...",
  "afterHash": "...",
  "changes": [
    { "path": "/Title", "kind": "replace", "before": "Old", "after": "New" },
    { "path": "/Tags", "kind": "replace", "beforeHash": "...", "afterHash": "..." },
    { "path": "/Content", "kind": "text-diff", "patch": "..." }
  ]
}
```

For large content diffs, store hashes and a text patch rather than duplicating entire content every time. Every N versions, store a full snapshot.

Confidence: 0.74.

### 13.4 Restore

Restore is an Admin operation for MVP.

Restore workflow:

1. Admin opens history viewer.
2. UI shows version timeline, actor, action, before/after hash, and rendered diff.
3. Admin previews selected restore.
4. Restore creates a new current version; it never rewrites history.
5. Restore itself is audited with target version and new version ID.

Editors restoring their own content can be considered later.

Confidence: 0.82.

### 13.5 Git As Internal Version Control

Recommendation: do not use Git as the MVP history engine.

Reasons:

- Git may not be installed or on PATH for Windows Service deployments.
- Service account identity and repository ownership can be awkward.
- Commits for every save can generate noisy repository history.
- `.gitignore` already excludes some artifacts like models; history needs explicit treatment.
- Atomic coordination with SQLite audit metadata is still unresolved.

Future option:

- Add an admin backup/export action that can initialize or update a Git repository under `Data/.archive/git` for human inspection.
- Treat Git as an archive mirror, not the source of truth.

Confidence: 0.76.

## 14. SQLite Schema

The schema below is conceptual. Exact SQL should be reviewed before implementation.

### 14.1 Core System Tables

```sql
CREATE TABLE SchemaMigrations (
    MigrationId TEXT PRIMARY KEY,
    AppliedAtUtc TEXT NOT NULL,
    ProductVersion TEXT NOT NULL
);

CREATE TABLE Settings (
    Key TEXT PRIMARY KEY,
    ValueJson TEXT NOT NULL,
    ValueHash TEXT NOT NULL,
    UpdatedByUserId TEXT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
```

### 14.2 Users, Providers, Roles

```sql
CREATE TABLE Users (
    UserId TEXT PRIMARY KEY,
    DisplayName TEXT NOT NULL,
    NormalizedDisplayName TEXT NOT NULL,
    Email TEXT NULL,
    NormalizedEmail TEXT NULL,
    IsDisabled INTEGER NOT NULL DEFAULT 0,
    LocalPasswordEnabled INTEGER NOT NULL DEFAULT 0,
    PasswordHash TEXT NULL,
    PasswordHashVersion INTEGER NOT NULL DEFAULT 1,
    SecurityStamp TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    LastLoginAtUtc TEXT NULL
);

CREATE INDEX IX_Users_NormalizedEmail ON Users(NormalizedEmail);

CREATE TABLE Providers (
    ProviderName TEXT PRIMARY KEY,
    DisplayName TEXT NOT NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 0,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    UpdatedAtUtc TEXT NOT NULL,
    UpdatedByUserId TEXT NULL
);

CREATE TABLE UserProviderLinks (
    LinkId TEXT PRIMARY KEY,
    UserId TEXT NOT NULL REFERENCES Users(UserId) ON DELETE CASCADE,
    ProviderName TEXT NOT NULL REFERENCES Providers(ProviderName),
    ProviderSubject TEXT NOT NULL,
    ProviderDisplayName TEXT NULL,
    ProviderEmail TEXT NULL,
    ProviderEmailVerified INTEGER NULL,
    LinkedAtUtc TEXT NOT NULL,
    LastUsedAtUtc TEXT NULL,
    UNIQUE(ProviderName, ProviderSubject)
);

CREATE INDEX IX_UserProviderLinks_UserId ON UserProviderLinks(UserId);

CREATE TABLE Roles (
    RoleId TEXT PRIMARY KEY,
    Name TEXT NOT NULL UNIQUE,
    NormalizedName TEXT NOT NULL UNIQUE,
    Description TEXT NULL,
    IsSystem INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE UserRoles (
    UserId TEXT NOT NULL REFERENCES Users(UserId) ON DELETE CASCADE,
    RoleId TEXT NOT NULL REFERENCES Roles(RoleId) ON DELETE CASCADE,
    AssignedAtUtc TEXT NOT NULL,
    AssignedByUserId TEXT NULL,
    PRIMARY KEY(UserId, RoleId)
);
```

Seed roles: `Viewer`, `Editor`, `Admin`.

Seed providers: `GitHub`, `Google`, `Microsoft`, `LocalPassword`, `ApiToken`, `System`.

### 14.3 Login History and API Tokens

```sql
CREATE TABLE LoginHistory (
    LoginId TEXT PRIMARY KEY,
    UserId TEXT NULL REFERENCES Users(UserId) ON DELETE SET NULL,
    ProviderName TEXT NOT NULL,
    ProviderSubject TEXT NULL,
    OccurredAtUtc TEXT NOT NULL,
    Succeeded INTEGER NOT NULL,
    FailureCode TEXT NULL,
    IpHash TEXT NULL,
    UserAgentHash TEXT NULL,
    RequestId TEXT NULL
);

CREATE INDEX IX_LoginHistory_User_Time ON LoginHistory(UserId, OccurredAtUtc);
CREATE INDEX IX_LoginHistory_Provider_Time ON LoginHistory(ProviderName, OccurredAtUtc);

CREATE TABLE ApiTokens (
    TokenId TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    TokenHash TEXT NOT NULL UNIQUE,
    TokenPrefix TEXT NOT NULL,
    OwnerUserId TEXT NULL REFERENCES Users(UserId) ON DELETE CASCADE,
    ServicePrincipalName TEXT NULL,
    ScopesJson TEXT NOT NULL,
    IsDisabled INTEGER NOT NULL DEFAULT 0,
    CreatedAtUtc TEXT NOT NULL,
    CreatedByUserId TEXT NULL,
    LastUsedAtUtc TEXT NULL,
    ExpiresAtUtc TEXT NULL
);
```

### 14.4 Audit Metadata

```sql
CREATE TABLE AuditMetadata (
    AuditId TEXT PRIMARY KEY,
    Sequence INTEGER NOT NULL UNIQUE,
    OccurredAtUtc TEXT NOT NULL,
    RecordedAtUtc TEXT NOT NULL,
    ActorUserId TEXT NULL REFERENCES Users(UserId) ON DELETE SET NULL,
    ActorDisplay TEXT NULL,
    ActorKind TEXT NOT NULL,
    AuthScheme TEXT NULL,
    ProviderName TEXT NULL,
    RoleSnapshotJson TEXT NULL,
    Action TEXT NOT NULL,
    TargetKind TEXT NOT NULL,
    TargetId TEXT NULL,
    Outcome TEXT NOT NULL,
    Reason TEXT NULL,
    BeforeHash TEXT NULL,
    AfterHash TEXT NULL,
    DiffRef TEXT NULL,
    RequestId TEXT NULL,
    CorrelationId TEXT NULL,
    IpHash TEXT NULL,
    UserAgentHash TEXT NULL,
    DetailsJson TEXT NULL,
    PreviousAuditHash TEXT NULL,
    AuditHash TEXT NOT NULL
);

CREATE INDEX IX_Audit_Time ON AuditMetadata(OccurredAtUtc);
CREATE INDEX IX_Audit_Actor_Time ON AuditMetadata(ActorUserId, OccurredAtUtc);
CREATE INDEX IX_Audit_Target ON AuditMetadata(TargetKind, TargetId, OccurredAtUtc);
CREATE INDEX IX_Audit_Action_Time ON AuditMetadata(Action, OccurredAtUtc);
CREATE INDEX IX_Audit_Correlation ON AuditMetadata(CorrelationId);
```

### 14.5 Version History Metadata

```sql
CREATE TABLE VersionHistory (
    VersionId TEXT PRIMARY KEY,
    TargetKind TEXT NOT NULL,
    TargetId TEXT NOT NULL,
    VersionNumber INTEGER NOT NULL,
    ParentVersionId TEXT NULL REFERENCES VersionHistory(VersionId),
    Format TEXT NOT NULL,
    HistoryPath TEXT NOT NULL,
    BeforeHash TEXT NULL,
    AfterHash TEXT NOT NULL,
    ByteSize INTEGER NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    CreatedByUserId TEXT NULL REFERENCES Users(UserId) ON DELETE SET NULL,
    AuditId TEXT NULL REFERENCES AuditMetadata(AuditId),
    RestoreSupported INTEGER NOT NULL DEFAULT 1,
    UNIQUE(TargetKind, TargetId, VersionNumber)
);

CREATE INDEX IX_VersionHistory_Target ON VersionHistory(TargetKind, TargetId, VersionNumber);
CREATE INDEX IX_VersionHistory_Time ON VersionHistory(CreatedAtUtc);
```

### 14.6 Semantic Index Metadata

```sql
CREATE TABLE SemanticIndexMetadata (
    MetadataId TEXT PRIMARY KEY,
    CorpusKind TEXT NOT NULL,
    SourceId TEXT NOT NULL,
    ChunkId TEXT NOT NULL,
    SourceContentHash TEXT NOT NULL,
    EmbeddingModelId TEXT NOT NULL,
    TokenizerId TEXT NOT NULL,
    VectorDimensions INTEGER NOT NULL,
    IndexPath TEXT NULL,
    IndexedAtUtc TEXT NOT NULL,
    LastBuildId TEXT NULL,
    Status TEXT NOT NULL,
    UNIQUE(CorpusKind, SourceId, ChunkId, EmbeddingModelId, TokenizerId)
);

CREATE INDEX IX_SemanticIndex_Source ON SemanticIndexMetadata(CorpusKind, SourceId);
CREATE INDEX IX_SemanticIndex_Status ON SemanticIndexMetadata(Status, IndexedAtUtc);

CREATE TABLE IndexBuilds (
    BuildId TEXT PRIMARY KEY,
    StartedAtUtc TEXT NOT NULL,
    CompletedAtUtc TEXT NULL,
    RequestedByUserId TEXT NULL REFERENCES Users(UserId) ON DELETE SET NULL,
    Kind TEXT NOT NULL,
    Status TEXT NOT NULL,
    DetailsJson TEXT NULL,
    AuditId TEXT NULL REFERENCES AuditMetadata(AuditId)
);
```

### 14.7 Backup Metadata

Optional but recommended once admin backup exists:

```sql
CREATE TABLE BackupRuns (
    BackupId TEXT PRIMARY KEY,
    StartedAtUtc TEXT NOT NULL,
    CompletedAtUtc TEXT NULL,
    RequestedByUserId TEXT NULL REFERENCES Users(UserId) ON DELETE SET NULL,
    ArchivePath TEXT NULL,
    ManifestHash TEXT NULL,
    Status TEXT NOT NULL,
    DetailsJson TEXT NULL,
    AuditId TEXT NULL REFERENCES AuditMetadata(AuditId)
);
```

Confidence: 0.78.

## 15. Persistence Interfaces

The requested interface names are shown below. If ASP.NET Core Identity is used, avoid confusion with `Microsoft.AspNetCore.Identity.IUserStore<TUser>` by placing these in a clear namespace or renaming to `IMemorySmithUserStore`. The plan keeps the requested names for traceability.

### 15.1 User and Role Stores

```csharp
public interface IUserStore
{
    Task<UserAccount?> GetByIdAsync(string userId, CancellationToken cancellationToken);
    Task<UserAccount?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserAccount>> ListAsync(UserQuery query, CancellationToken cancellationToken);
    Task CreateAsync(UserAccount user, CancellationToken cancellationToken);
    Task UpdateAsync(UserAccount user, CancellationToken cancellationToken);
    Task DisableAsync(string userId, string disabledByUserId, CancellationToken cancellationToken);
    Task<bool> HasAnyAdminAsync(CancellationToken cancellationToken);
}

public interface IRoleStore
{
    Task<IReadOnlyList<RoleRecord>> ListRolesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RoleRecord>> GetRolesForUserAsync(string userId, CancellationToken cancellationToken);
    Task AssignRoleAsync(string userId, string roleName, string assignedByUserId, CancellationToken cancellationToken);
    Task RemoveRoleAsync(string userId, string roleName, string removedByUserId, CancellationToken cancellationToken);
}
```

### 15.2 Provider Links and Login History

```csharp
public interface IProviderLinkStore
{
    Task<IReadOnlyList<ProviderLink>> GetLinksForUserAsync(string userId, CancellationToken cancellationToken);
    Task<ProviderLink?> GetByProviderSubjectAsync(string providerName, string providerSubject, CancellationToken cancellationToken);
    Task LinkAsync(ProviderLink link, CancellationToken cancellationToken);
    Task UnlinkAsync(string linkId, CancellationToken cancellationToken);
    Task SetProviderEnabledAsync(string providerName, bool enabled, string updatedByUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuthProviderRecord>> ListProvidersAsync(CancellationToken cancellationToken);
}

public interface ILoginHistoryStore
{
    Task RecordAsync(LoginHistoryEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<LoginHistoryEntry>> QueryAsync(LoginHistoryQuery query, CancellationToken cancellationToken);
}
```

### 15.3 Audit, Settings, History, Index Metadata

```csharp
public interface IAuditLogStore
{
    Task AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken);
    Task<PagedResult<AuditLogEntry>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken);
    Task<AuditLogEntry?> GetAsync(string auditId, CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<AdminSetting?> GetAsync(string key, CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminSetting>> ListAsync(CancellationToken cancellationToken);
    Task SetAsync(AdminSetting setting, CancellationToken cancellationToken);
}

public interface IVersionHistoryStore
{
    Task<VersionHistoryEntry> CreateVersionAsync(VersionCreateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<VersionHistoryEntry>> GetHistoryAsync(string targetKind, string targetId, CancellationToken cancellationToken);
    Task<VersionHistoryEntry?> GetVersionAsync(string versionId, CancellationToken cancellationToken);
    Task<Stream> OpenArtifactAsync(string versionId, CancellationToken cancellationToken);
}

public interface ISemanticIndexMetadataStore
{
    Task UpsertChunkAsync(SemanticIndexMetadata metadata, CancellationToken cancellationToken);
    Task<IReadOnlyList<SemanticIndexMetadata>> GetBySourceAsync(string corpusKind, string sourceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SemanticIndexMetadata>> GetStaleAsync(string modelId, string tokenizerId, CancellationToken cancellationToken);
    Task RecordBuildAsync(IndexBuildRecord build, CancellationToken cancellationToken);
}
```

### 15.4 API Tokens and Database Provider Factory

```csharp
public interface IApiTokenStore
{
    Task<ApiTokenRecord?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);
    Task<IReadOnlyList<ApiTokenRecord>> ListAsync(ApiTokenQuery query, CancellationToken cancellationToken);
    Task CreateAsync(ApiTokenRecord token, CancellationToken cancellationToken);
    Task RevokeAsync(string tokenId, string revokedByUserId, CancellationToken cancellationToken);
    Task RecordUseAsync(string tokenId, DateTime usedAtUtc, CancellationToken cancellationToken);
}

public interface IDatabaseProviderFactory
{
    IDatabaseProvider Create(DatabaseOptions options);
}

public interface IDatabaseProvider
{
    string Name { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IDbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
    IUserStore Users { get; }
    IRoleStore Roles { get; }
    IProviderLinkStore ProviderLinks { get; }
    ILoginHistoryStore LoginHistory { get; }
    IAuditLogStore AuditLogs { get; }
    ISettingsStore Settings { get; }
    IVersionHistoryStore VersionHistory { get; }
    ISemanticIndexMetadataStore SemanticIndexMetadata { get; }
    IApiTokenStore ApiTokens { get; }
}
```

Implementation note: a provider object that exposes all stores is convenient, but DI may be cleaner if each store is registered separately from a common connection factory. Choose the DI shape during implementation.

Confidence: 0.75.

## 16. Database Provider Factory

### 16.1 Provider Selection

First implementation:

- `SQLite`: implemented.

Recognized but not implemented until needed:

- `PostgreSQL`
- `SqlServer`
- `LiteDB`

Configuration should fail fast for non-implemented providers with a clear message. Do not silently fall back to SQLite.

Confidence: 0.91.

### 16.2 SQLite Provider

SQLite implementation responsibilities:

- Resolve connection string relative to deployment/data root.
- Create parent directory.
- Apply migrations at startup if enabled.
- Enable foreign keys.
- Optionally enable WAL mode.
- Configure busy timeout.
- Provide online backup support for admin backup/export.
- Provide migration lock/recovery behavior if EF Core is used, or a simple migration transaction if using hand-written SQL.

Implementation options:

| Option | Pros | Cons |
|---|---|---|
| `Microsoft.Data.Sqlite` plus hand-written migrations | Small, explicit, provider factory friendly | More SQL code to maintain |
| EF Core SQLite | Faster model iteration, migrations | SQLite migration limitations and provider-specific behavior; larger dependency |
| Dapper | Lightweight mapping | Adds package and still needs migrations |

Recommendation: use `Microsoft.Data.Sqlite` and explicit migrations for the security/admin schema unless the team prefers EF Core Identity integration. If Identity Core custom stores are chosen, EF Core may become more attractive but should still be reviewed against SQLite migration limitations.

Confidence: 0.72.

### 16.3 Future Providers

Future relational providers should keep the same store interfaces but use provider-specific SQL/migrations. Do not promise provider parity until tests exist.

LiteDB is not relational SQL and should be treated as a separate document-provider implementation, not as a drop-in SQL provider.

Confidence: 0.80.

## 17. Admin Panel Design

### 17.1 Technology

Use Blazor Server components under `MemorySmith.App/Components/Pages/Admin`. Continue MudBlazor and current app layout conventions.

Do not create a separate SPA or new host.

Confidence: 0.95.

### 17.2 Routes

| Route | Purpose | Policy |
|---|---|---|
| `/admin` | Overview dashboard | Admin |
| `/admin/users` | User management | Admin |
| `/admin/providers` | Provider enable/disable and configuration status | Admin |
| `/admin/roles` | Role assignments and default role behavior | Admin |
| `/admin/audit` | Audit log viewer | Admin, future Auditor |
| `/admin/history` | Search history by target | Admin |
| `/admin/history/memory/{id}` | Memory history viewer/restore | Admin |
| `/admin/history/page/{*slug}` | Page history viewer/restore | Admin |
| `/admin/settings` | Admin settings | Admin |
| `/admin/search-indexes` | Semantic/index rebuild status and controls | Admin |
| `/admin/models` | ONNX model status/reload controls | Admin |
| `/admin/system` | Health/diagnostics/backups | Admin |

Navigation should show the Admin link only when authorized, but route policies must enforce access.

Confidence: 0.86.

### 17.3 UI Structure

Use dense operational UI, consistent with current app:

- Left list/table for users/audit/history entries.
- Right details pane for selected item.
- Toolbars with icon buttons and concise text commands.
- Filters for date range, actor, action, target kind, provider, outcome.
- Confirmation dialogs for destructive or security-sensitive changes.
- Diff viewer for history restore.
- Provider status cards should show configured/missing secrets without revealing secret values.
- Model/index controls should show current model path, vocab path, status, last rebuild, stale chunk count, and rebuild button.

Avoid marketing/landing-page patterns. This is an admin workbench.

Confidence: 0.88.

### 17.4 Admin Services

Add application services rather than making components talk directly to stores:

- `UserAdminService`
- `ProviderAdminService`
- `RoleAdminService`
- `AuditQueryService`
- `HistoryQueryService`
- `SettingsAdminService`
- `IndexAdminService`
- `BackupAdminService`

Every admin service method should take current user context, enforce policy where appropriate, and emit audit events.

Confidence: 0.87.

## 18. Integration Points By Existing File

### 18.1 `Program.cs`

Add, after options/configuration:

- Database provider registration.
- Auth services.
- Authorization policies.
- Cascading authentication state.
- Current user context.
- Audit/history/admin services.
- Rate limiter.
- Data Protection key persistence under `Data/Keys` or another configured root.

Middleware order should be reviewed carefully. Proposed order:

1. Forwarded headers if explicitly configured for reverse proxy.
2. Serilog/request logging/correlation ID if added.
3. Existing local/remote request guard, adjusted for auth endpoints and remote policy.
4. HTTPS redirection.
5. Static files and page assets.
6. Authentication.
7. Authorization.
8. Antiforgery.
9. Controllers and Razor components.

Important review point: external OAuth callback paths must not be blocked accidentally by API-key checks. They are not under `/api` or `/mcp`, but non-loopback request guard behavior matters for shared deployments.

Confidence: 0.73.

### 18.2 `Routes.razor` and `_Imports.razor`

Changes:

- Add `@using Microsoft.AspNetCore.Components.Authorization`.
- Replace `RouteView` with `AuthorizeRouteView`.
- Provide NotAuthorized content that links to login when auth is enabled.
- Keep anonymous Viewer pages accessible through policy rather than global hard block.

Confidence: 0.91.

### 18.3 `MemoryViewer.razor`

Changes:

- Hide New/Edit/Delete/Usage controls unless `CanEditMemorySmith`.
- Keep read/search controls for Viewers.
- Display sign-in prompt only when anonymous read is disabled or write attempted.
- Do not rely on UI hiding for enforcement.

Confidence: 0.90.

### 18.4 `Pages.razor`

Changes:

- Hide New/Edit/Delete/upload/save controls unless `CanEditMemorySmith`.
- Use `PageApplicationService` instead of direct `IPageService` if introduced.
- Add history link for Admin.

Confidence: 0.89.

### 18.5 `MemoriesController`

Changes:

- Read/search endpoints use `CanViewMemorySmith`.
- Create/update/delete/usage endpoints use `CanEditMemorySmith`.
- Add current user context to service calls or let service resolve it.
- Return 401/403 through ASP.NET Core auth, not custom ad hoc responses.

Confidence: 0.93.

### 18.6 `PagesController`

Changes:

- Read/search/html endpoints use `CanViewMemorySmith`.
- Save/update/delete endpoints use `CanEditMemorySmith`.
- Use page app service so page writes get audit/history.

Confidence: 0.93.

### 18.7 `McpController`

Changes:

- Authenticate through API token or cookie if a browser calls it.
- Authorize each tool through a shared tool executor.
- Include caller context in audit entries.
- Keep tool output bounded.
- Consider `CanReadSourceBundle` for `memorysmith_source_bundle`.

Confidence: 0.88.

### 18.8 `ChatServices.cs`

Changes:

- Chat can remain Viewer-accessible.
- Agent write proposals require Editor/Admin and explicit approval.
- When writes are approved, pass actor/context into memory/page app services.
- Audit provider/model, prompt operation ID, proposed targets, approval/rejection, and final writes.
- Extract duplicated MCP/chat tool execution into shared `MemorySmithToolService` if doing auth work here.

Confidence: 0.86.

### 18.9 `MemoryApplicationService`

Changes:

- Add authorization-aware mutation methods or enforce auth in calling layer plus app-service guard.
- Read before state for update/delete/usage where history/audit needs hashes.
- Write version metadata/history before or during mutation.
- Replace `AuditAndPublishAsync` with a richer audit service.
- Keep search query telemetry but separate noisy query events from high-value audit events if needed.

Confidence: 0.87.

### 18.10 `FileEventStore`

Changes:

- Keep for JSONL mirror or replace with `JsonlAuditMirrorStore`.
- Do not stretch `MemoryEvent` to represent all audit concepts if it creates migration pain.
- Add rotation/compression in new audit mirror service.

Confidence: 0.84.

### 18.11 `OperationalDiagnosticsService`

Changes:

- Include database provider/status/migration state.
- Include auth enabled, anonymous access mode, provider enabled/configured status.
- Redact full paths and auth details for non-admin or remote callers.
- Warn when remote access is enabled without strong auth.
- Warn when JSONL audit mirror fails.

Confidence: 0.86.

## 19. Search, Context Pack, and MCP Integration

### 19.1 Authorization-Aware Retrieval

MVP does not need per-record ACLs. All Viewers can read all memories/pages.

Still, design search APIs so a future permission filter can be added:

- Add an optional `AccessContext` or current user context to search calls.
- Keep context pack root/reference/backlink traversal behind the same read policy.
- If future per-record ACLs are introduced, filter candidate records before ranking and filter linked records before adding to context packs.

Confidence: 0.82.

### 19.2 Semantic Metadata

`SemanticIndexMetadata` should track what has been indexed, not force a vector index implementation.

Use cases:

- Detect stale chunks after memory/page changes.
- Show index freshness in admin panel.
- Audit rebuild/reload actions.
- Keep a path to future HNSW/Lucene vector index metadata.

Confidence: 0.84.

### 19.3 Tool-Call Audit

Audit all high-impact tool calls:

- `memorysmith_source_bundle`: always audit because it reads local source slices.
- `memorysmith_context_pack`: audit if result count/content budget exceeds a threshold or always for admin-configured strict audit mode.
- Search/get tools: aggregate telemetry by default; detailed audit configurable.

Include:

- Tool name.
- Caller kind/user/token.
- Arguments after redaction/truncation.
- Result count and byte count, not full result body.
- Target IDs where available.

Confidence: 0.79.

## 20. Migration Plan

### 20.1 Migration Principles

- Do not move or rewrite existing memory/page files.
- Do not require external OAuth setup to keep using local read access.
- Do not break tests that copy `Data/Memories` fixtures.
- Make the SQLite DB additive and rebuildable for metadata where possible.
- Make auth enforcement a deliberate phase, not an accidental side effect of adding tables.

Confidence: 0.90.

### 20.2 Rollout Phases

#### Phase 0: Plan Review

Deliverables:

- This document.
- Human decisions on open questions O6, O7, O8, O13, O14, O15.

No runtime behavior changes.

#### Phase 1: SQLite Foundation, No Auth Enforcement

Deliverables:

- `DatabaseOptions`.
- SQLite provider and migrations.
- `SchemaMigrations`, `Settings`, seed roles/providers.
- Health/diagnostics surface for DB status.
- NUnit tests for migration idempotency and provider factory failure modes.

No changes to memory/page access yet.

#### Phase 2: Audit and Version Metadata Foundation

Deliverables:

- `AuditMetadata` and `VersionHistory` stores.
- JSONL mirror writer with weekly rotation and optional compression.
- Memory write audit/history through `MemoryApplicationService`.
- Page write audit/history through `PageApplicationService` or decorator.
- Startup recovery scan for pending/orphan history artifacts.

Access can still be open during this phase to reduce risk.

#### Phase 3: Identity Bootstrap and Local Password

Deliverables:

- First-admin setup.
- Local password sign-in/out.
- Cookie auth and Data Protection key persistence.
- Login history.
- Rate limiting and lockout.
- Basic account page.

Auth can be enabled but RBAC enforcement may remain in compatibility mode until tests pass.

#### Phase 4: RBAC Enforcement

Deliverables:

- Authorization policies.
- Controller/component enforcement.
- App-service mutation guards.
- Viewer/Editor/Admin UI behavior.
- Anonymous Viewer config.
- Compatibility flag decision applied.

This is the behavior-changing phase.

#### Phase 5: External Providers and Identity Linking

Deliverables:

- GitHub provider.
- Google provider.
- Microsoft provider.
- Provider enable/disable settings.
- Self-link/unlink page.
- Admin link/unlink tools.
- Login history provider attribution.

#### Phase 6: Admin Panel

Deliverables:

- `/admin` overview.
- Users/roles/providers/settings.
- Audit viewer.
- History viewer/restore.
- Model/index controls.
- Backup/export UI.

Some admin pages can ship earlier if needed, but avoid partial pages that can mutate security state without complete audit.

#### Phase 7: Scoped API Tokens and MCP Attribution

Deliverables:

- API token table and token auth scheme.
- Admin token creation/revocation.
- MCP token docs.
- Legacy API key deprecation warning.

#### Phase 8: Provider Abstraction Expansion

Deliverables:

- Provider-specific SQL tests for PostgreSQL/SQL Server if accepted.
- LiteDB feasibility study if still desired.

Do not start this until SQLite auth/admin model is stable.

### 20.3 Existing Data Migration

Existing content:

- Memories stay under `Data/Memories`.
- Pages stay under `Data/Pages`.
- Existing `Data/Events/audit.log` remains historical operational telemetry.

New DB:

- Created at `Data/memorysmith.db` by default.
- Seeds providers/roles/settings.
- Does not import old `MemoryEvent` rows into `AuditMetadata` by default because old rows lack actor/outcome/hash fields.

Optional import:

- Admin command can import old `audit.log` as legacy events with `ActorKind=Unknown`, `Outcome=Unknown`, `Action` preserved, and `DetailsJson` marked legacy.

Confidence: 0.82.

### 20.4 Backup and Archive

Recommendation for user-store backup: keep one canonical user format, SQLite.

Backup artifact should be an archive with a manifest, not a second JSON representation of users:

```text
memorysmith-backup-20260517-153000.zip
  manifest.json
  database/memorysmith.db
  memories/**
  pages/**
  events/**
  history/**
  vars.json
```

Backup process:

- Use SQLite online backup API or a safe checkpoint/copy strategy.
- Include hashes for every file in manifest.
- Optionally exclude `Data/Models` by default because ONNX models can be large and redistributability may be a concern.
- Audit backup creation and export path.

Confidence: 0.86.

## 21. Testing Plan

Use NUnit, consistent with project instructions.

### 21.1 Unit Tests

- Role normalization and policy mapping.
- Provider link uniqueness and unlink-last-login prevention.
- Password lockout/rate-limit state transitions.
- Audit hash-chain canonicalization.
- Version diff/snapshot generation.
- History restore preview.
- Path canonicalization for history artifacts.

### 21.2 Storage Tests

- SQLite migrations are idempotent.
- Seed roles/providers exist.
- Foreign keys enforced.
- WAL/busy timeout configuration applied or safely skipped.
- Store CRUD for users, roles, providers, audit, settings, history, semantic metadata.
- Provider factory rejects unsupported providers.

### 21.3 App/API Tests

- Anonymous Viewer can read when enabled.
- Anonymous cannot write.
- Viewer cannot write.
- Editor can write memory/page and audit/history is created.
- Admin can manage user role.
- Last Admin cannot be disabled/demoted without replacement.
- Disabled provider cannot start new login.
- Login failures are generic and recorded.
- Lockout returns consistent response and audit event.
- `/api/diagnostics` redacts or denies for non-admin.
- MCP read tools require view policy and record caller attribution.

### 21.4 UI Tests

Existing test stack may not include browser automation. At minimum use component/service tests where practical:

- Admin nav appears only for Admin.
- Edit buttons hidden for Viewer.
- Unauthorized route displays login/denied state.
- History viewer renders diff metadata safely.

### 21.5 Regression Tests

- Existing `dotnet test MemorySmith.slnx -v minimal` stays green.
- Tests still copy `Data/Memories` fixture before mutation.
- Existing local no-auth tests either configure compatibility mode or assert new auth behavior explicitly.

Confidence: 0.88.

## 22. Risk Register

| Risk | Impact | Mitigation | Confidence |
|---|---|---|---:|
| Auth breaks current local write workflow | High | First-admin setup, clear migration docs, optional temporary compatibility flag | 0.72 |
| Hand-rolled auth mistakes | High | Use ASP.NET Core auth primitives, framework password hashing, external handlers, policy tests | 0.87 |
| SQLite/file dual-write inconsistency | Medium/High | Pending metadata, recovery scan, fail-closed for security actions | 0.68 |
| Shared API key remains identity substitute | Medium | Add scoped tokens and deprecate legacy key | 0.86 |
| Admin panel exposes secrets/paths | Medium | Redaction, policy enforcement, no secret values in DB/UI | 0.84 |
| JSONL logs grow indefinitely | Medium | Weekly rotation, compression, health warnings, retention review | 0.82 |
| Version diff format is hard to restore | Medium | Periodic snapshots, restore tests, avoid unbounded diff chains | 0.80 |
| OAuth provider email collision causes wrong account link | High | Provider subject as key; no auto-link by email alone | 0.90 |
| Lockout can be abused for DoS | Medium | Soft/progressive lockout, admin reset, IP and account partitions, avoid permanent lockout | 0.79 |
| Future provider factory over-engineers MVP | Medium | Implement only SQLite; fail fast for others | 0.86 |
| Git history path complicates service deployment | Medium | Keep Git as optional archive, not MVP write path | 0.76 |
| Diagnostics leak host paths in shared deployment | Medium | Admin-only diagnostics and remote redaction | 0.85 |

## 23. Implementation Backlog

### Review Gate 1: Architecture Approval

- Approve or change SQLite scope.
- Approve page snapshot vs diff choice.
- Approve memory diff/checkpoint approach.
- Decide Git archive future.
- Decide backward compatibility write behavior.
- Decide API key replacement/deprecation stance.

### Review Gate 2: Database Foundation

- Add database options.
- Add SQLite provider package and provider factory.
- Add migrations and seed data.
- Add DB diagnostics.
- Add NUnit storage tests.

### Review Gate 3: Audit/History Foundation

- Add audit model/store/service.
- Add JSONL mirror with rotation/compression.
- Add history artifact store.
- Add memory/page app-service write wrappers.
- Add tests.

### Review Gate 4: Identity/RBAC

- Add local password bootstrap.
- Add auth middleware and Blazor auth state.
- Add policies and endpoint/component enforcement.
- Add provider linking.
- Add tests for access matrix.

### Review Gate 5: Admin Panel

- Add `/admin` pages.
- Add user/role/provider/settings/audit/history/model/index services.
- Add backup/export.
- Add audit around all admin actions.

## 24. Recommended Safe First Steps

Safe before further review:

1. Keep this plan in `MemorySmith.Core/Docs/Plans`.
2. Optionally add a short project-wiki memory pointing to this plan after review.
3. Optionally add a non-runtime ADR summarizing the accepted architecture after review.

Not safe before review:

- Adding auth packages and middleware.
- Creating a database file or migrations.
- Changing default write access.
- Adding `[Authorize]` attributes.
- Changing MCP/API key behavior.
- Implementing history writes.

Confidence: 0.93.

## 25. Source References

### Repository Evidence

- `README.md`
- `MemorySmith.slnx`
- `MemorySmith.App/Program.cs`
- `MemorySmith.App/MemorySmith.App.csproj`
- `MemorySmith.App/appsettings.json`
- `MemorySmith.App/Components/Routes.razor`
- `MemorySmith.App/Components/Layout/NavMenu.razor`
- `MemorySmith.App/Components/Pages/MemoryViewer.razor`
- `MemorySmith.App/Components/Pages/Pages.razor`
- `MemorySmith.App/Controllers/MemoriesController.cs`
- `MemorySmith.App/Controllers/PagesController.cs`
- `MemorySmith.App/Controllers/SearchController.cs`
- `MemorySmith.App/Controllers/ChatController.cs`
- `MemorySmith.App/Controllers/McpController.cs`
- `MemorySmith.App/Controllers/StatsController.cs`
- `MemorySmith.App/Controllers/DiagnosticsController.cs`
- `MemorySmith.App/Controllers/HealthController.cs`
- `MemorySmith.App/Services/MemoryApplicationService.cs`
- `MemorySmith.App/Services/PageService.cs`
- `MemorySmith.App/Services/ChatServices.cs`
- `MemorySmith.App/Services/MemorySmithOptions.cs`
- `MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs`
- `MemorySmith.App/Services/SemanticEmbeddingSearchService.cs`
- `MemorySmith.App/Services/OperationalDiagnosticsService.cs`
- `MemorySmith.Storage/IMemoryStore.cs`
- `MemorySmith.Storage/FileMemoryStore.cs`
- `MemorySmith.Storage/IEventStore.cs`
- `MemorySmith.Storage/FileEventStore.cs`
- `MemorySmith.Core/Models/MemoryRecord.cs`
- `MemorySmith.Core/Models/MemoryEvent.cs`
- `Data/Memories/Core/project-wiki-active-architecture.json`
- `Data/Memories/Core/project-wiki-storage-rules.json`
- `Data/Memories/Core/project-wiki-event-store.json`
- `Data/Memories/Core/project-wiki-markdown-pages.json`
- `Data/Memories/Core/project-wiki-mcp-search-tools-current.json`
- `Data/Memories/Core/project-wiki-request-guard-hardening.json`
- `Data/Memories/Core/project-wiki-validation-command.json`
- `MemorySmith.Core/Docs/Plans/MemorySmith_FinalRefactorDesign_20260507.md`
- `MemorySmith.Core/Docs/Reviews/Audit_20260517_131014.md`

### External References

- ASP.NET Core authentication overview: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/
- ASP.NET Core role-based authorization: https://learn.microsoft.com/en-us/aspnet/core/security/authorization/roles
- ASP.NET Core Blazor authentication state: https://learn.microsoft.com/en-us/aspnet/core/blazor/security/authentication-state
- ASP.NET Core external provider authentication: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/
- ASP.NET Core Google external login setup: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/google-logins
- ASP.NET Core Microsoft Account external login setup: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/microsoft-logins
- ASP.NET Core Data Protection overview: https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/introduction
- ASP.NET Core rate limiting middleware: https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit
- GitHub OAuth app authorization flow: https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps
- AspNet.Security.OAuth.Providers GitHub provider: https://github.com/aspnet-contrib/AspNet.Security.OAuth.Providers
- SQLite WAL documentation: https://sqlite.org/wal.html
- EF Core SQLite provider limitations: https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations
- OWASP Logging Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html
- OWASP Authentication Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html

## 26. Final Recommendation

Proceed with review of this architecture before code changes. The highest-value implementation path is:

1. SQLite provider and schema foundation.
2. Audit/history foundation with no access behavior change.
3. Local identity/bootstrap and auth plumbing.
4. RBAC enforcement.
5. External providers and identity linking.
6. Admin panel and scoped API tokens.

This sequence keeps MemorySmith usable while converting the security model from local guard/API key to real users, roles, auditability, and recoverable content history.
