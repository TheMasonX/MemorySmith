# Configuration Reference

MemorySmith configuration is rooted under `MemorySmith` in appsettings and is split across three operator surfaces:

1. Base app settings in `MemorySmith.App/appsettings.json`.
2. Admin-edited overrides in `appsettings.LocalDevelopment.json` or the file referenced by `MemorySmith:SettingsOverridePath`.
3. Structured model profile management in `/admin` on the Models tab.

This page is the operator and agent-oriented map of the configuration groups, where they are edited, and how to verify the running result. It is intentionally grouped for readability rather than an exhaustive per-key table; `TSK-0158` tracks a generated inventory/coverage check for every editable admin setting key.

## Editing Rules

| Surface | Use it for | Notes |
| --- | --- | --- |
| `/admin` Configuration tab | Allowlisted scalar and list settings | Uses `AdminSettingsService`; reloads configuration after save. |
| `/admin` Models tab | `MemorySmith:Chat:ModelProfiles`, `DefaultModelProfileId`, maintenance-agent model assignments | Managed separately because profiles are a structured collection with workflow/default semantics. |
| App settings files | Bootstrap defaults, non-editable complex settings, deployment-specific paths | `SettingsOverridePath` itself and complex `GitHubModels` entries remain file-managed. |
| `/health` and `/api/diagnostics` | Runtime verification | Confirms effective paths, warnings, embeddings state, and telemetry-related config visibility. |

## Configuration Storage Model

- Base defaults live in `MemorySmith.App/appsettings.json`.
- The admin UI writes edited settings into `appsettings.LocalDevelopment.json` beside the running app unless `MemorySmith:SettingsOverridePath` points somewhere else.
- Sensitive values are write-only in the admin UI. The app reports `Configured` or `Not configured` instead of echoing secrets.
- List settings are edited one value per line.
- Nullable integers such as `MemorySmith:Chat:OllamaContextWindowTokens` accept a blank value to clear the override.

## Group Map

| Group | Root keys | Main verification surface | Main risk if misconfigured |
| --- | --- | --- | --- |
| Core paths | `DataPath`, `PagesPath`, `EventLogPath`, `VarsPath`, `DataProtectionKeysPath` | `/health`, `/api/diagnostics` | App points at the wrong wiki or loses auth key continuity. |
| Remote/API security | `ApiKey`, `AllowRemoteApi` | `/api/diagnostics` warnings | Remote callers can reach the API without the intended boundary. |
| Database | `Database:*` | startup logs, `/health` | SQLite metadata store points at the wrong file or applies the wrong startup policy. |
| Auth and providers | `Auth:*` | `/login`, `/admin`, `/admin/setup` | Incorrect roles, bootstrap drift, or broken OAuth sign-in. |
| Audit and history | `Audit:*`, `History:*` | `/admin`, filesystem | Audit/history evidence goes to an unexpected path or cadence. |
| Pages and search | `Pages:*`, `SemanticSearch:*`, `TaskSearch:*`, `Governance:*` | `/pages`, `/memories`, `/tasks`, `/tags`, `/health` | Search quality or wiki rendering drifts from operator expectations. |
| Maintenance | `Maintenance:*` | `/maintenance`, `/health` | Background work runs too often, too rarely, or not at all. |
| Source links | `SourceLinks:*` | `/variables`, source-link actions, diagnostics | Agents can read too much or too little local source. |
| MCP tools | `Mcp:*` | `/mcp`, `/admin` Configuration | A deployment exposes an unwanted tool or hides an expected one. |
| Chat | `Chat:*` | `/chat`, `/api/chat/config` | Wrong provider/model defaults, context bloat, or disabled tool flow. |
| Maintenance agent | `MaintenanceAgent:*` | `/maintenance`, `/proposals`, admin maintenance chat | Review paths, write roots, or transcript handling drift. |
| Logging and telemetry | `Logging:*`, `Telemetry:*` | `/api/diagnostics`, `/api/diagnostics/logs*`, structured logs | Observability is too noisy, too sparse, or exporter behavior surprises operators. |

## Core Paths

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:DataPath` | Root for structured memories under `Data/Memories/*` | `/health` path cards, `/memories` results |
| `MemorySmith:PagesPath` | Root for markdown wiki pages and page assets | `/pages`, `/page-assets`, `/api/diagnostics` |
| `MemorySmith:EventLogPath` | Legacy file-backed event log path | `/health` and filesystem |
| `MemorySmith:VarsPath` | Variable map used by `%VarName%` source links | `/variables`, source-link actions |
| `MemorySmith:DataProtectionKeysPath` | ASP.NET Core data protection keys directory | sign-in persistence across restarts |
| `MemorySmith:SettingsOverridePath` | Optional override file location | file-managed; not edited from `/admin` |

Agent note: if behavior looks wrong across multiple routes, check the effective path set first. Path drift is the fastest way to end up testing the wrong wiki.

## Security And Remote API

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:AllowRemoteApi` | Allows non-loopback API and MCP traffic | `/api/diagnostics` warning list |
| `MemorySmith:ApiKey` | Shared API/MCP key via `X-Api-Key` | configured state in `/admin`, guarded API requests |

Safe default: keep `AllowRemoteApi=false` unless the instance is intentionally exposed and an API key plus transport/auth posture are already in place.

## Database

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:Database:Provider` | Active metadata backend | currently `SQLite` only |
| `ConnectionString` | SQLite path and DB settings | startup behavior, auth/audit/history metadata |
| `ApplyMigrationsOnStartup` | Runs DB migrations at app start | startup logs |
| `UseWal` | Enables SQLite write-ahead logging | concurrency behavior |
| `BusyTimeoutSeconds` | SQLite lock wait timeout | lock error rate and startup/runtime stability |

## Authentication And Providers

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:Auth:Enabled` | Enables interactive auth | `/login`, admin-protected routes |
| `AnonymousAccess` | Signed-out role baseline | read-only route behavior |
| `AuthenticatedDefaultRole` | Default role for signed-in users | newly signed-in user behavior |
| `AutoEditorForAuthenticatedUsers` | Grants normal edit capability to authenticated users | page/memory editing without Admin powers |
| `LocalPasswordEnabled` | Enables local username/password login | `/login` local auth form |
| `RequireHttpsForRemoteAuth` | Blocks remote auth over HTTP | remote login posture |
| `OpenLocalEditorCompatibility` | Bootstrap compatibility valve for local editing | pre-first-admin local behavior |
| `Setup:AllowLoopbackBootstrap` | Allows anonymous first-admin setup from loopback | `/admin/setup` |
| `Setup:BootstrapTokenHash` | Token-gated setup alternative | write-only secret state |
| `RateLimits:*` | Local auth throttling and lockout | local login error behavior |
| `Providers:{GitHub|Google|Microsoft}:*` | OAuth provider enablement and credentials | provider rows in `/admin`, sign-in flow |

Clamp rule: Admin access is not granted by broad anonymous/default-role settings. Admin routes still require an authenticated Admin claim.

## Audit, History, And Pages

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:Audit:JsonlEnabled` | Enables JSONL audit mirror | `Data/Events`, admin audit view |
| `Audit:JsonlPath` | Rotated JSONL audit path pattern | filesystem and retention expectations |
| `Audit:JsonlRotation` | Rotation cadence label | audit file naming |
| `Audit:CompressRotatedLogs` | Compresses rotated audit files | audit archive shape |
| `Audit:HashChainEnabled` | Enables tamper-evident chaining | audit integrity posture |
| `History:RootPath` | Root for version history artifacts | `Data/.history` |
| `History:PageMode` | Page history storage mode | restore/history semantics |
| `History:MemoryMode` | Memory history storage mode | restore/history semantics |
| `History:MemoryCheckpointEveryVersions` | Checkpoint frequency | restore chain length |
| `Pages:DefaultMinimumRole` | Default page visibility | page save defaults |
| `Pages:AllowRawHtml` | Allows raw HTML in rendered pages | only for trusted content |

## Search, Governance, And Maintenance

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:SemanticSearch:*` | ONNX model paths, prefixes, and size limits | `/health` semantic provider state, semantic/hybrid search |
| `MemorySmith:TaskSearch:HybridSemanticEnabled` | Hybrid lexical/semantic task ranking | `/tasks` query relevance |
| `MemorySmith:Governance:TagPolicyPath` | Tag policy JSON path | `/tags`, policy diagnostics |
| `MemorySmith:Maintenance:*` | Background triage/index/consolidation cadence | `/maintenance`, `/health` |

Current semantic behavior: embeddings are optional. If model assets are missing or unusable, the app falls back to local token scoring and reports provider status.

## Source Links

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:SourceLinks:MaxReadBytes` | Caps returned file content size | MCP/source-link reads |
| `AllowUnrestrictedSourceReads` | Broad read mode | diagnostics and source access behavior |
| `ReadContextLinesBefore` / `After` | Adds line padding around requested ranges | source-bundle output |
| `AllowOpenWithDefaultApp` | Enables Ctrl+Click open behavior | UI source-link actions |
| `AllowedFileRootVariables` / `AllowedFileRoots` | Allowed source roots | diagnostics and read success |
| `DeniedFileRootVariables` / `DeniedFileRoots` | Explicit deny roots | blocked read/open behavior |

Deny roots take precedence over allow roots and unrestricted mode.

## MCP Tools

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:Mcp:EnabledTools` | Explicitly enables descriptor-level default-off MCP tools | `/mcp` `tools/list` |
| `MemorySmith:Mcp:DisabledTools` | Hides named MCP tools and rejects direct calls | `/mcp` `tools/list` and `tools/call` |

Existing MCP tools default on unless they are listed in `DisabledTools`; `DisabledTools` takes precedence if a tool appears in both lists.

## Chat

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:Chat:Provider` | Default provider fallback | `/chat` provider selection |
| `OllamaEndpoint`, `OllamaModel`, `OllamaContextWindowTokens` | Local Ollama transport and usage metadata | `/chat`, provider model discovery |
| `GitHubModel`, `GitHubCliPath`, `GitHubCliUrl`, `GitHubTokenEnvironmentVariable` | GitHub provider defaults and auth guidance | `/chat`, provider errors |
| `SystemPromptPath` | Prompt file path for chat/agent runtime | runtime behavior and prompt packaging |
| `RequestTimeoutSeconds` | Provider request timeout | long chat turns |
| `MaxContextRecords`, `MaxContextPages`, `PreloadContextEnabled`, `MaxPreloadedContextRecords`, `MaxPreloadedContextPages`, `MaxContextItemCharacters`, `MaxHistoryMessages` | Context planner and history budget | `/chat` context behavior |
| `MaxAttachmentCharacters`, `MaxAttachmentBytes` | Attachment input limits | attachment uploads |
| `ToolCallsEnabled`, `MaxToolIterations`, `MaxToolCallsPerTurn`, `MaxToolResultCharacters` | Bounded read-only tool loop | tool traces and retrieval behavior |
| `AgentWritesEnabled` | Enables approval-gated Agent write proposals | `/chat` Agent mode and `/proposals` |

Important exception: `Chat:ModelProfiles`, `DefaultModelProfileId`, and maintenance-agent model assignment IDs are edited from the Models tab, not the generic settings table.

## Maintenance Agent

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:MaintenanceAgent:Read` / `Write` | Allowed read and write roots | `/maintenance`, proposal behavior |
| `DirectWrite` | Allows direct writes instead of proposal-first workflow | should remain off for normal governance |
| `UseLlm`, `Provider`, `OllamaEndpoint`, `Model` | Legacy provider/model path | maintenance runs when model profiles are not assigned |
| `ModelProfileId`, `ProposalReviewModelProfileId`, `AdminChatModelProfileId` | Structured model routing | Models tab and maintenance/chat behavior |
| `AgentVersion` | Prompt contract version label | maintenance result metadata |
| `MaxFindingsPerTask` | Output bound per maintenance task | review workload |
| `Tasks:*` | Enables individual maintenance task types | `/maintenance` task list |
| `Schedule:*` | Weekly scheduler behavior | run cadence |
| `ResourceProbe:*` | Busy-machine skip behavior | local workstation friendliness |
| `Storage:*` | Proposal, topic-map, run-state, activity log, and transcript paths plus transcript retention/redaction | `/proposals`, maintenance chat transcripts |

## Logging And Telemetry

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:Logging:*` | Console, structured file, Windows Event Log, request logging, slow thresholds, metrics windows, diagnostics result caps | `/api/diagnostics/logs`, `/api/diagnostics/logs/metrics`, local log sinks |
| `MemorySmith:Telemetry:*` | OpenTelemetry traces/metrics, exporter, service name, path exclusions, sampling | `/api/diagnostics`, `/health`, collector behavior |

Telemetry defaults are local-first: telemetry is enabled, exporter is off, sampling is low, and noisy request paths are excluded.

## Verification Checklist

1. Check `/health` for path state, semantic provider status, maintenance state, and runtime configuration clues.
2. Check `/api/diagnostics` for effective paths, source-link roots, telemetry config visibility, configured URLs, and warnings.
3. Check `/chat`, `/maintenance`, `/proposals`, `/tags`, `/tasks`, and `/variables` when the suspected drift is feature-specific.
4. Inspect `appsettings.LocalDevelopment.json` or the configured override file when UI behavior and expected values disagree.

## Agent Assistance Notes

- Start with runtime verification, not only file inspection. MemorySmith reloads config after admin saves, so `/api/diagnostics` is often the fastest truth surface.
- Treat `AllowRemoteApi=true` with no API key as a configuration smell immediately.
- Remember that model profiles and generic settings are intentionally split.
- When a setting looks missing from `/admin`, check whether it is intentionally file-managed rather than assuming the docs are stale.

## Related Pages

- [System Setup Guide](system-setup.md)
- [Search and Chat](search-and-chat.md)
- [Operations](../ops/operations.md)
- [Agent and Model Configuration](agent-configuration.md)
- [Wiki Health and Validation](../ops/wiki-health-and-validation.md)