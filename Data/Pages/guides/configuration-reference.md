# Configuration Reference

MemorySmith configuration is rooted under `MemorySmith` in appsettings and is split across three operator surfaces:

1. Base app settings in `MemorySmith.App/appsettings.json`.
2. Admin-edited overrides in `appsettings.LocalOverrides.json` or the file referenced by `MemorySmith:SettingsOverridePath`.
3. Structured model profile management in `/admin` on the Models tab.

This page is the operator and agent-oriented map of the configuration groups, where they are edited, and how to verify the running result. It is intentionally grouped for readability rather than an exhaustive per-key table; `TSK-0158` tracks a generated inventory/coverage check for every editable admin setting key.

## Editing Rules

| Surface | Use it for | Notes |
| --- | --- | --- |
| `/admin` Configuration tab | Allowlisted scalar and list settings | Uses `AdminSettingsService`; reloads configuration after save. Sensitive values remain write-only and configured secrets can be removed only through the explicit `Clear secret` action. |
| `/admin` Models tab | `MemorySmith:Chat:ModelProfiles`, `DefaultModelProfileId`, maintenance-agent model assignments | Managed separately because profiles are a structured collection with workflow/default semantics. |
| App settings files | Bootstrap defaults, non-editable complex settings, deployment-specific paths | `SettingsOverridePath` itself and complex `GitHubModels` entries remain file-managed. |
| `/health` and `/api/diagnostics` | Runtime verification | Confirms effective paths, warnings, embeddings state, and telemetry-related config visibility. |

## Configuration Storage Model

- Base defaults live in `MemorySmith.App/appsettings.json`.
- The admin UI writes edited settings into `appsettings.LocalOverrides.json` beside the running app unless `MemorySmith:SettingsOverridePath` points somewhere else.
- Sensitive values are write-only in the admin UI. The app reports `Configured` or `Not configured` instead of echoing secrets, and configured secrets are cleared through the explicit `Clear secret` action rather than a blank replacement field.
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
| Training harness | `Training:*` | harness scripts, run artifacts under `runs/<runId>/`, `/admin` Configuration | Fine-tuning data export/run behavior drifts or silently falls back to simulated mode. |
| Maintenance agent | `MaintenanceAgent:*` | `/maintenance`, `/proposals`, admin maintenance chat | Review paths, write roots, or transcript handling drift. |
| Logging and telemetry | `Logging:*`, `Telemetry:*` | `/api/diagnostics`, `/api/diagnostics/logs*`, structured logs | Observability is too noisy, too sparse, or exporter behavior surprises operators. |

## Core Paths

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:DataPath` | Root for structured memories under `Data/Memories/*` | `/health` path cards, `/memories` results |
| `MemorySmith:PagesPath` | Root for markdown wiki pages and page assets | `/pages`, `/page-assets`, `/api/diagnostics` |
| `MemorySmith:EventLogPath` | Legacy file-backed event log path | `/health` and filesystem |
| `MemorySmith:VarsPath` | Variable map used by `%VarName%` source links | `/variables`, source-link actions |
| `MemorySmith:DataProtectionKeysPath` | ASP.NET Core data protection keys directory plus the local HMAC key used for hashed audit/login request metadata | sign-in persistence across restarts; audit/login metadata contains hashes, not raw IP or user-agent values |
| `MemorySmith:SettingsOverridePath` | Optional override file location | file-managed; not edited from `/admin` |

Agent note: if behavior looks wrong across multiple routes, check the effective path set first. Path drift is the fastest way to end up testing the wrong wiki.

## Security And Remote API

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:SecurityProfile` | Optional preset: `local-dev`, `secure-local`, or `remote-hardened` | Admin Configuration and `/api/diagnostics` |
| `MemorySmith:AllowRemoteApi` | Allows guarded non-loopback API and MCP traffic after an API key is configured | `/api/diagnostics` warning list |
| `MemorySmith:ApiKey` | Shared API/MCP key via `X-Api-Key`; required for guarded non-loopback API/MCP when remote API is enabled | configured state in `/admin` (replace or use `Clear secret` to remove), guarded API requests |

Recommended dogfood default: leave explicit settings in their secure-local posture, or set `MemorySmith:SecurityProfile=secure-local` when you want the preset recorded in configuration.

Safe default: keep `AllowRemoteApi=false` unless the instance is intentionally exposed and an API key plus transport/auth posture are already in place. With `AllowRemoteApi=true` and no API key, guarded non-loopback `/api` and `/mcp` requests are blocked until the key is configured. Browser-facing auth/setup routes remain exempt so LAN UI sign-in and first-admin bootstrap can still reach their existing auth/bootstrap checks.

Runtime note: `MemorySmith:SecurityProfile=local-dev` and `ASPNETCORE_ENVIRONMENT=LocalDevelopment` are related but not identical. The security profile applies a small preset under any environment; the `LocalDevelopment` environment also runs `MemorySmithLocalDevelopmentPostConfigure`, which applies additional dogfood-friendly defaults only when those keys are not already overridden in `appsettings.LocalOverrides.json` or the configured `SettingsOverridePath` file.

Representative `LocalDevelopment` post-configuration defaults when keys are missing:

| Key | LocalDevelopment default | Why it matters |
| --- | --- | --- |
| `MemorySmith:AllowRemoteApi` | `true` | Makes non-loopback API/MCP exposure possible once an API key is configured. |
| `MemorySmith:Auth:RequireHttpsForRemoteAuth` | `false` | Permits local HTTP auth flows for dogfood/debug use. |
| `MemorySmith:Auth:OpenLocalEditorCompatibility` | `false` | Prefers the stricter local-editor compatibility posture unless explicitly overridden. |
| `MemorySmith:Pages:AllowRawHtml` | `true` | Trusted local pages can render raw HTML. |
| `MemorySmith:Chat:AgentWritesEnabled` | `true` | Agent mode can create approval-gated proposals by default. |
| `MemorySmith:Chat:*` limits | larger timeout, context, attachment, and tool-loop caps | Local dogfood runs allow bigger chat/tool payloads than the secure-local baseline. |
| `MemorySmith:Limits:*` and `MemorySmith:SourceLinks:MaxReadBytes` | higher content/search/source-link ceilings | Large local wiki and source-bundle workflows work without immediate tuning. |

Operator check: when environment behavior does not match the intended profile, verify both the active ASP.NET Core environment and whether the key is explicitly present in the override file. `TSK-0181` tracks the current malformed-override caveat in this path.

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
| `Providers:{GitHub|Google|Microsoft}:*` | OAuth provider enablement and credentials | provider rows in `/admin`; only runtime-registered providers are advertised as active sign-in methods |

Clamp rule: Admin access is not granted by broad anonymous/default-role settings. Admin routes still require an authenticated Admin claim.

Current runtime note: startup currently registers GitHub OAuth at `/signin-github`. Google and Microsoft credentials can still be stored for future rollout, but `/admin` marks those providers as unsupported and `/login` plus `/profile` do not treat them as active sign-in methods until matching auth handlers and callback routes are registered.

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
| `MemorySmith:SemanticSearch:*` | ONNX model paths, tokenizer/pooling convention, prefixes, and size limits | `/health` semantic provider state, semantic/hybrid search |
| `MemorySmith:TaskSearch:HybridSemanticEnabled` | Hybrid lexical/semantic task ranking | `/tasks` query relevance |
| `MemorySmith:TaskAttachments:*` | Task file upload storage path and per-file byte limit | `/tasks` attachment upload and `/artifacts/task-attachments/...` serving |
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
| `ClipboardFetchExternalImagesEnabled` | Allows clipboard paste flow to fetch HTTP/HTTPS image URLs as attachments | clipboard image paste behavior and network posture |
| `ToolCallsEnabled`, `MaxToolIterations`, `MaxToolCallsPerTurn`, `MaxToolResultCharacters` | Bounded read-only tool loop | tool traces and retrieval behavior |
| `AgentWritesEnabled` | Enables approval-gated Agent write proposals | `/chat` Agent mode and `/proposals` |
| `AgentWriteRoots` | Paths approved chat-agent memory/page proposals may target | `/chat` approvals and `/proposals`; separate from `MaintenanceAgent:Write` |

Important exception: `Chat:ModelProfiles`, `DefaultModelProfileId`, and maintenance-agent model assignment IDs are edited from the Models tab, not the generic settings table.

## Training Harness

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:Training:ChatTranscriptEnabled` | Enables assistant-turn transcript JSONL capture for training export | transcript files under `Data/Events/chat-transcripts` |
| `StoreChatContent` | Enables companion content JSONL capture with literal request/response text | `*.content.jsonl` files and privacy posture |
| `TranscriptRetentionDays` | Deletes stale transcript files older than configured age | transcript directory contents over time |
| `TranscriptRedactionEnabled` | Redacts common token/secret patterns before content write | sampled transcript content and redaction behavior |
| `FeedbackEnabled` | Enables thumbs feedback persistence path | `/chat` thumbs controls and feedback store rows |
| `TrainingDataExportPath` | Target directory for exported SFT/DPO/ORPO JSONL | `Data/Training/exports/*` outputs |
| `RunsDirectory` | Root directory for per-run contract artifacts | `runs/<runId>/status.json`, `events.jsonl`, `benchmark.json` |
| `PythonVenvPath` | Python environment path used by harness bridge script | `Scripts/Test-FinetuneHarnessPrereqs.ps1` output |
| `PythonHarnessScript` | Python harness entrypoint path | `Scripts/Run-FinetuneHarness.ps1` invocation |
| `PreferenceFormat` | Default export format selection (`FilteredSft`, `Dpo`, `Orpo`) | request payloads and export naming conventions |

Operational scripts:

- `Scripts/Test-FinetuneHarnessPrereqs.ps1` validates whether training dependencies are installed in the configured venv.
- `Scripts/Run-FinetuneHarness.ps1` runs the harness bridge and writes request/status/events/benchmark artifacts.

## Maintenance Agent

| Key | Purpose | Verify |
| --- | --- | --- |
| `MemorySmith:MaintenanceAgent:Read` / `Write` | Allowed read and write roots | `/maintenance`, proposal behavior |
| `DirectWrite` | Allows direct writes instead of proposal-first workflow | should remain off for normal governance |
| `ActionUx:ShowAccept`, `ShowRespond`, `ShowReject`, `DefaultAction`, `RevisionRequired` | Proposal action visibility, primary-action emphasis, and revision gate policy | `/proposals`, Admin Configuration |
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
4. Inspect `appsettings.LocalOverrides.json` or the configured override file when UI behavior and expected values disagree.
5. If `LocalDevelopment` behavior looks unexpectedly permissive, validate that the override file parses cleanly. Current known risk `TSK-0181` tracks malformed or unreadable override files being treated like missing overrides during LocalDevelopment post-configuration.

## Agent Assistance Notes

- Start with runtime verification, not only file inspection. MemorySmith reloads config after admin saves, so `/api/diagnostics` is often the fastest truth surface.
- Treat `AllowRemoteApi=true` with no API key as a blocked remote-readiness state, not just a warning.
- Remember that model profiles and generic settings are intentionally split.
- When a setting looks missing from `/admin`, check whether it is intentionally file-managed rather than assuming the docs are stale.

## Related Pages

- [System Setup Guide](system-setup.md)
- [Search and Chat](search-and-chat.md)
- [Operations](../ops/operations.md)
- [Agent and Model Configuration](agent-configuration.md)
- [Wiki Health and Validation](../ops/wiki-health-and-validation.md)