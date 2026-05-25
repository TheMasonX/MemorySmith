# MemorySmith

[![CI](https://github.com/TheMasonX/MemorySmith/workflows/CI/badge.svg)](https://github.com/TheMasonX/MemorySmith/actions/workflows/ci.yml)
[![Docs Pages](https://github.com/TheMasonX/MemorySmith/workflows/Docs%20Pages/badge.svg)](https://github.com/TheMasonX/MemorySmith/actions/workflows/docs-pages.yml)

MemorySmith is a single-host ASP.NET Core app for local structured memory management. It hosts a Blazor workbench UI, markdown pages, REST API, MCP endpoint, file-backed content storage, SQLite-backed security/audit metadata, local chat/agent workflows, and background maintenance in one process. The `/memories` page is the primary structured memory workbench. The repository ships with a live project wiki inside `Data/Memories`, and the app uses its own memory store as a testbed.

## Quick Start

```powershell
dotnet run --project MemorySmith.App --launch-profile http
```

Opens on `http://localhost:5089` by default. Pages:

| Route | Purpose |
| --- | --- |
| `/memories` | Browse, search, create, edit, delete memory records |
| `/pages` | Create, search, edit, preview, and render markdown-backed pages from `Data/Pages` |
| `/chat` | Memory-enhanced chat and agent mode with provider/model selection, streaming responses, context usage, attachments, and local chat history |
| `/tasks` | Task tracking workspace with comments, status transitions, links, and attachments |
| `/tags` | Tag governance and diagnostics management workspace |
| `/maintenance` | Maintenance run controls and active-run status |
| `/proposals` | Maintenance-agent proposal dashboard with diff previews, evidence, comments, risk indicators, and topic map visualization |
| `/login`, `/profile`, `/admin/setup`, `/admin` | Local sign-in/profile management, first-admin bootstrap, and searchable RBAC/audit/history/settings administration |
| `/health` | Scrollable stat cards, activity charts (queries/day, changes/day), maintenance telemetry |
| `/variables` | Manage `%VarName%` path variables used in source link URIs |
| `/about` | MemorySmith and third-party license information |
| `/api/memories` | REST CRUD for automation |
| `/api/pages`, `/api/search`, `/api/chat` | Page CRUD/search/rendering, combined memory/page search, and chat/agent/config API |
| `/api/tasks`, `/api/governance` | Task CRUD/history/link APIs and tag-governance policy/suggestions/diagnostics APIs |
| `/api/maintenance-agent/*`, `/api/source-links/open` | Maintenance runs/proposals/topic-map APIs and guarded source-link open API |
| `/api/auth/*`, `/api/admin/*` | Current-user, login/logout, setup, user, provider, audit, and history metadata APIs |
| `/api/stats`, `/api/health/*`, `/api/diagnostics` | Stats, readiness, and redacted operational diagnostics |
| `/page-assets/*` | Static files from `Data/Pages/assets` for images, video, and audio embedded in pages |
| `/mcp` | MCP JSON-RPC endpoint for AI agent tool use |

## The Project Wiki

`Data/Memories/` is the structured live wiki for this project. `Data/Pages/` is the markdown live wiki for longer-form user and agent-authored notes. The app defaults `MemorySmith:DataPath` to `../Data/Memories` and `MemorySmith:PagesPath` to `../Data/Pages`, so local runs read and write those records directly.

User-created markdown files under `Data/Pages/` are valid project wiki content and should be committed unless a specific page is intentionally private or temporary.

**Using the wiki as a testbed:** before starting research or making architectural decisions, search the wiki first. The wiki records are also used as integration test fixtures — tests copy `Data/Memories/` to a temp directory so the source stays stable while being exercised through the same code paths.

### Current wiki records

| ID | What it documents |
| --- | --- |
| `project-wiki-active-architecture` | Single-host layout, project structure |
| `project-wiki-data-folder-policy` | `Data/` folder conventions and test fixture policy |
| `project-wiki-storage-rules` | `FileMemoryStore` behavior, atomic writes, ID sanitization |
| `project-wiki-event-store` | `FileEventStore` JSONL audit log, activity buckets |
| `project-wiki-source-links-feature` | `SourceLink` model, `%VarName%` expansion, `source_bundle`/`find_by_source` tools |
| `project-wiki-ui-architecture` | Blazor Server pages, MudBlazor 9.4, CSS conventions |
| `project-wiki-semantic-ui-current` | `/memories` workbench feature set |
| `project-wiki-mcp-integration` | MCP endpoint setup, VS Code `mcp.json` config |
| `project-wiki-mcp-search-tools-current` | All seven MCP tool signatures and usage notes |
| `project-wiki-mcp-context-pack` | `memorysmith_context_pack` deep-dive |
| `project-wiki-onnx-semantic-embeddings` | Optional ONNX Runtime embedding ranker and exact cosine semantic fallback path |
| `project-wiki-hybrid-search-rrf` | Lucene.NET + semantic RRF fusion |
| `project-wiki-semantic-search-gap` | Remaining semantic-search limitations and future vector-index gap |
| `project-wiki-search-roadmap` | Planned search improvements |
| `project-wiki-validation-command` | How to build and test |
| `project-wiki-test-architecture` | NUnit fixture strategy, benchmark suite |
| `project-wiki-windows-service-operations` | Windows Service install/uninstall flags |
| `project-wiki-markdown-pages` | Markdown page storage, rendering, and page assets |
| `project-wiki-memory-status-classification-current` | Status meanings and how MemorySmith treats Working, Core, Deprecated, and Unconsolidated records |
| `project-wiki-chat-agent-provider` | Chat provider/agent abstractions, Ollama streaming, and GitHub Copilot provider workflow |
| `project-wiki-chat-configuration-current` | Chat settings, model profile fallback, context/tool limits, and agent-write gating |
| `project-wiki-chat-image-attachments` | Image attachment pipeline, trusted temp storage, and vision payload routing |
| `project-wiki-chat-local-storage-persistence` | Browser-local chat history, draft retention, and provider/model selection persistence |
| `project-wiki-chat-streaming-thinking` | Streaming response chunks, thinking-block extraction, and elapsed timers |
| `project-wiki-agent-instructions-source-of-truth` | Copilot instruction files and current agent-facing source-of-truth map |
| `project-wiki-admin-configuration-surface` | `/admin` settings and Models tabs, write-only secrets, and settings reload behavior |
| `project-wiki-configuration-settings-current` | The active `MemorySmith:*` configuration surface and which settings stay file-managed |
| `project-wiki-ui-layout-source-link-polish` | UI layout, source-link open behavior, and navigation polish |
| `project-wiki-scope-boundaries` | What is and isn't in scope for the current implementation |
| `project-wiki-generalization-friction` | Known gaps for broader adoption |
| `ai-memory-suite-implementation-plan-20260520` | Current implementation status snapshot for the AI Memory Suite planning work |
| `project-wiki-benchmarkdotnet-suite` | BenchmarkDotNet project: smoke validation and full benchmark commands |
| `project-wiki-semantic-tool-quality-suite` | Search relevance probes, aggregate MRR, and MCP tool output quality assertions |
| `project-wiki-current-validation-baseline` | Stable current validation anchor for test inventory and KB-health checks |
| `project-wiki-current-validation-146-tests` | Historical alias retained so older references can resolve to the stable validation record |
| `project-wiki-wiki-validation-current` | Current validation coverage for pages, tasks, and the remaining live-memory validator gap |
| `project-wiki-github-actions-artifacts` | CI Cobertura coverage artifacts and Doxygen GitHub Pages export |
| `project-wiki-logging-telemetry-current` | Logging and OpenTelemetry settings, runtime endpoints, and local-first defaults |
| `project-wiki-maintenance-observability-refinements` | Startup triage/index scheduling and stats activity bucket API |
| `project-wiki-maintenance-proposals-current` | Current proposal dashboard, admin maintenance chat, and review workflow behavior |
| `project-wiki-operational-diagnostics-dashboard` | `/health` dashboard and `/api/diagnostics` operational snapshot |
| `project-wiki-request-guard-hardening` | Request guard middleware, `AllowRemoteApi` and `ApiKey` enforcement |
| `project-wiki-admin-auth-hardening` | Admin-policy hardening and editable settings current state |
| `project-wiki-source-link-configuration-current` | Source-link settings exposed through admin configuration and their runtime effects |
| `project-wiki-source-link-security-boundaries` | Source bundle read boundaries and allowed root variable rules |
| `project-wiki-tag-governance-current` | Tag policy management, diagnostics, suggestions, and `/tags` behavior |
| `project-wiki-test-fixture-overview` | Overview of the five integration-test fixture records |
| `project-wiki-test-fixture-context-root` | Context pack root fixture (context pack traversal tests) |
| `project-wiki-test-fixture-reference-child` | Reference child fixture |
| `project-wiki-test-fixture-backlink-source` | Backlink source fixture |
| `project-wiki-test-fixture-conflict-note` | Conflict fixture |

Retrieve any record via the MCP tool `memorysmith_get` with its ID, or search the `/memories` page.

Maintenance agent configuration lives under standard `MemorySmith:MaintenanceAgent` application settings. Operational switches such as task enablement, scheduling, and review behavior can be managed through the admin settings workflow, which requires an authenticated Admin user. The config supports read/write directory allowlists, proposal-only default writes, task switches, local Ollama model settings, weekly scheduling, busy-session skip probes, generated review proposals, and file-backed proposal/topic-map storage. The proposal dashboard can run all tasks or one selected task, and `/api/maintenance-agent/topic-map/mermaid` exports the cached topic map for PR notes and diagrams.

## Authentication, Audit, And History

MemorySmith keeps memory/page content file-backed and stores security metadata in SQLite at `Data/memorysmith.db` by default. Cookie authentication and RBAC policies protect the UI, REST APIs, and MCP tools. Built-in roles are `Viewer`, `Editor`, and `Admin`; the default local policy allows anonymous Viewer access, while mutation, diagnostics, admin, audit, settings, and restore workflows require stronger roles.

On a fresh local install, `Auth:OpenLocalEditorCompatibility` grants loopback requests non-admin local write compatibility until the first Admin user exists. Admin, user-management, settings, audit, diagnostics, and restore workflows always require a signed-in Admin user. Create that first account at `/admin/setup`, then sign in at `/login`. Local password auth is implemented; external provider rows for GitHub, Google, and Microsoft are seeded for administration and include OAuth developer setup links in the admin providers table.

Audit metadata is written to SQLite and mirrored to weekly JSONL files under `Data/Events`. Memory and page writes also create version-history artifacts under `Data/.history`; these artifacts are metadata/history records, not replacements for `Data/Memories` or `Data/Pages` as the source of truth. The admin UI provides text and facet filters for audit entries and recent history artifacts, while editable settings are searchable and grouped by category expanders.

## Memory Records

Each record is a JSON file in `Data/Memories/{Status}/`. Fields:

| Field | Type | Description |
| --- | --- | --- |
| `Id` | string | Unique, kebab-case. Must match the filename. |
| `Title` | string | Short human name. |
| `Content` | string | Body text. Max 20 000 chars. Rendered as safe Markdown in the `/memories` detail pane. |
| `Status` | int | `0` Unconsolidated · `1` Working · `2` Core · `3` Deprecated |
| `Confidence` | double | `0.0`–`1.0` |
| `Tags` | string[] | Comma-separated labels for filtering. |
| `References` | string[] | IDs of related records (used for context pack traversal). |
| `Conflicts` | string[] | IDs of conflicting records. |
| `SourceLinks` | array | File references — see below. |
| `UsageCount` | int | Incremented by explicit usage API/UI calls. Read-only MCP tools do not mutate it. |
| `LastUpdated` | datetime | ISO 8601 UTC. |

### Source Links

Each `SourceLink` in the array has:

```json
{ "Label": "Program.cs", "Uri": "%MemorySmithRepo%MemorySmith.App/Program.cs", "StartLine": 1, "EndLine": 50 }
```

- **`Uri`** may contain `%VarName%` tokens. Manage variable bindings at `/variables`; they are stored in `Data/vars.json`.
- **`StartLine`** / **`EndLine`** are optional 1-based line numbers. When only `StartLine` is set the default window is 50 lines.
- The MCP tool `memorysmith_source_bundle` resolves URIs and returns actual file content slices alongside the memory data.
- The MCP tool `memorysmith_find_by_source` scans all records for source links matching a URI substring pattern.

Add or edit source links in the `/memories` workbench using the format `Label | URI [| StartLine[-EndLine]]`, one per line.

Local file source-link chips copy the resolved path on click. Ctrl+Click opens the resolved file with the operating system default app when `SourceLinks:AllowOpenWithDefaultApp` is enabled and the path is under an allowed source root.

## Markdown Pages

`Data/Pages/` stores user and agent-editable markdown files. The `/pages` UI and `/api/pages` API keep page search and page navigation separate from structured memory search. `/api/search` returns a combined memory/page result set when broader discovery is useful. Page assets live under `Data/Pages/assets` and are served at `/page-assets`; markdown links such as `![diagram](assets/diagram.png)` are rewritten to that route when rendered. Assets referenced only by locked pages follow the same visibility gate. Pages can be locked to a minimum visibility level: `Anonymous`, `Authenticated` (shown as Signed in), or `Admin`. Editors can choose Anonymous or Signed in; only Admin users can set Admin-only pages.

The page editor has a markdown toolbar for common inserts, an image upload/embed tool that writes to `Data/Pages/assets`, a toggleable live preview, a manual preview refresh button, and an unsaved-change prompt for internal and external navigation. Pages are rendered with the shared Markdig pipeline, including Mermaid fenced blocks (` ```mermaid `) and Prism-compatible fenced code classes such as `language-csharp` or `language-json`. Raw HTML is disabled by default for rendered pages; trusted local deployments can enable `MemorySmith:Pages:AllowRawHtml` when raw HTML media tags are intentionally needed. The static docs-site generator also sanitizes rendered page HTML and emits a restrictive Content Security Policy before publishing wiki pages.

## Tasks Domain

`/tasks` and `/api/tasks` provide a first-party task workflow integrated with the project wiki. The Tasks workbench supports search and filtering, quick task creation, status transitions (`Backlog`, `Ready`, `InProgress`, `Blocked`, `Done`, `Archived`), comments, page links, external links, attachments, and task activity history.

Task APIs support list/get/create/update/delete plus dedicated mutation endpoints for status updates, comments, link management, and attachments. The page and task domains are intentionally linked (for example page-slug references), so execution and planning notes can stay close to implementation artifacts.

MCP agents can use task tools for task list/get/create/update/status/comment/attachment workflows. Read task tools require view permission; write task tools require edit permission and reuse the same validation and activity-history service path as `/api/tasks`.

### Evidence-Backed Task Standard

Use `/tasks` as the primary planning surface for audits and implementation, not as a mirror of external notes.

- Every implementation task should include explicit acceptance criteria and at least one validation note.
- Every audit finding should map to a task (`Backlog`, `Ready`, or `Archived` with reason) rather than remaining only in markdown reports.
- Use task comments for evidence snapshots: file paths, command outputs, test names, screenshots, or page links.
- When a task is blocked, capture blocker details, last verified state, and the next proposed action in the task comment stream.
- Keep report pages in `Data/Pages/research` and durable memory records in `Data/Memories`, then link both from the corresponding `/tasks` item.

## Tag Governance

`/tags` and `/api/governance` provide admin-governed tag policy management. The Tag manager supports policy mode and plain-tag mode controls, allow/block lists, alias rules, namespace constraints, policy diagnostics feedback, observed-tag usage analytics, and suggestion triage (approve/reject).

Governance endpoints include:

- `GET/PUT /api/governance/tag-policy`
- `GET /api/governance/tag-suggestions`
- `POST /api/governance/memory-diagnostics`

Use this flow to keep memory tagging consistent while still allowing practical iteration as the wiki grows.

## Maintenance And Proposals

`/maintenance`, `/proposals`, and `/api/maintenance-agent/*` deliver the maintenance-agent lifecycle: run-on-demand task execution, findings/proposal generation, admin transcript review, proposal action history, and topic-map visualization (`/api/maintenance-agent/topic-map/mermaid`).

The maintenance workbench is non-mutating by default and warning-first; proposal approval remains the explicit control point for applying file changes.

## Chat and Agent Mode

`/chat` uses the `IChatProvider` and `IChatAgent` abstractions. The registered providers are `OllamaChatProvider`, which calls a local Ollama HTTP service, and `GitHubCopilotChatProvider`, which uses the GitHub Copilot SDK with GitHub CLI authentication or a configured token environment variable. `MemoryChatAgent` now uses intent-aware preloading: exact-reply/simple prompts and write-only Agent commands skip local wiki pre-context, while explicit MemorySmith/wiki/codebase prompts receive a small bounded hybrid memory plus page preload. When preloaded context is absent or not enough, the shared prompt lets the model request an app-intercepted, MCP-compatible read-only wiki tool call by returning JSON such as `{"toolCalls":[{"name":"memorysmith_unified_search","arguments":{"query":"search text","memoryLimit":5,"pageLimit":5}}]}`. The chat allowlist now matches the read-only MCP surface: `memorysmith_search`, `memorysmith_semantic_search`, `memorysmith_hybrid_search`, `memorysmith_context_pack`, `memorysmith_get`, `memorysmith_page_search`, `memorysmith_page_get`, and `memorysmith_unified_search`. A deterministic intent interceptor (`ChatIntentInterceptor`) also pre-runs an obvious tool call when the user message starts with phrases like "search the wiki for ...", "open page &lt;slug&gt;", "get memory &lt;id&gt;", "semantic/hybrid search ...", or "context pack ...", so reliable retrieval does not depend on the model emitting the JSON tool-call protocol correctly. Retrieved tool output and preloaded context are wrapped in an "Untrusted retrieved data" preamble before being added to the model context so any embedded instructions are treated as data, not commands.

The chat UI queries the selected provider for available models, supports provider/model selection, persists the last used provider/model, Mermaid diagram theme mode, and active chat, keeps the top model bar and bottom composer stable when switching the shared sidebar between History and Trace, places the sidebar toggle at the right edge beside the shared sidebar, streams live response chunks with an elapsed timer, renders message bodies as safe Markdown through Markdig with raw HTML disabled, supports Mermaid diagrams and Prism-compatible fenced code highlighting, lets users choose Auto/Light/Dark Mermaid theme mode, wraps rendered diagrams in a matching readable light or dark surface, defers Mermaid conversion while a response is actively streaming so unfinished fences remain visible as code, shows the provider/model used on assistant turns, shows per-response durations, displays bottom-right context usage with context-window percentage when known and provider quota/rate text when reported, deletes chats from history with confirmation, supports icon Stop for immediate cancellation plus icon Finish Step for a softer stop after the current provider/tool step, supports text and image file attachments, supports pasted clipboard images, Enter-to-send with Shift+Enter newlines, autoscroll, clickable memory/page resource chips behind a collapsed per-turn References drawer, pending-response feedback, compact browser-local chat history, and collapsible thinking blocks when the provider returns reasoning content. History and Trace share one right sidebar with tabs; Trace shows the selected turn's execution graph, turn selector, reasoning, tool requests/results, write approvals, token estimates, and tool latency with filters, collapsible trace headers, and editable tool rerun. The transcript no longer renders per-turn Trace buttons. Neutral resource chips are preloaded context, blue resource chips are mid-turn tool/intercept resources, and green write chips are Agent-created pages. Text attachments are bounded and supplied as context. Image attachments are saved to trusted temp files for persistence and supplied as native image payloads when the selected provider supports them; Ctrl+V handles copied image files, Clipboard API image blobs, copied HTML image references, and data:image URLs. Draft text and queued attachments are retained per chat session when switching chats, and navigation warns before leaving with unsent content.

Chat mode answers questions and the shared prompt asks providers to format normal answers as GitHub-flavored Markdown. The prompt also gives all chat agents explicit guidance for when to use preloaded wiki context, when to request a single app-intercepted read-only `toolCalls` JSON object, and how to produce complete Mermaid fenced diagrams only when a diagram clarifies the answer. Each turn includes a runtime capability message derived from the active mode, `Chat:*` limits, and the current user's roles, so models are told whether tools are read-only, whether Agent writes are configured, and whether the current user can approve writes. Chat mode cannot create or update memories/pages, but it can use the read-only search and retrieval tools for local wiki evidence. Agent mode asks the provider for structured actions and can write memories and pages only when `Chat:AgentWritesEnabled` is explicitly set to true and the current user has an Editor or Admin role; the default is false. The chat UI still requires explicit per-action approval before applying proposed Agent writes, and pending proposals are described as pending rather than created. Tool-call execution is read-only and bounded by `Chat:MaxToolIterations`, `Chat:MaxToolCallsPerTurn`, and `Chat:MaxToolResultCharacters`; write actions still require Agent mode structured output, opt-in write setting, RBAC, and approval. The shared system prompt is stored in `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.md` and copied into the app output for service/publish runs; `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.modelfile` carries the matching Athena/Ollama system prompt.

The provider interface is intentionally narrow so OpenAI, Anthropic, or other APIs can be added without changing the UI or agent workflow.

### Agent Prompt Sources Of Truth

- `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.md`: the canonical chat/agent system prompt used by the app.
- `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.modelfile`: the aligned Athena/Ollama system prompt for local model packaging.
- `MemorySmith.Core/Docs/Prompts/maintenance-agent-task.md`: maintenance task analysis prompt.
- `MemorySmith.Core/Docs/Prompts/maintenance-proposal-generation.md`: proposal generation contract prompt.
- `MemorySmith.Core/Docs/Prompts/maintenance-revision-cycle.md`: reviewer feedback revision-cycle prompt.
- `.github/agents/smith.agent.md`: canonical repository-workflow prompt for Agent Smith (task tracking, evidence standards, and memory/wiki maintenance).
- `.github/copilot-instructions.md`: repository-wide Copilot behavior and project map guidance.

When updating agent behavior, keep these prompt files aligned with runtime capabilities in `MemorySmith.App/Services` and `MemorySmith.App/Controllers`.
Treat `.github/agents/smith.agent.md` as the primary workflow contract for this repository, and backfill supporting instructions/docs after Smith prompt updates to avoid guidance drift.

## Search

Three search modes are available in the UI (`/memories` search bar) and the REST API:

| Mode | Endpoint | Behavior |
| --- | --- | --- |
| Lexical | `POST /api/memories/search` | Lucene.NET `StandardAnalyzer` tokenization and weighted title/tag/reference/content scoring. Empty queries retain deterministic `LastUpdated` ordering. |
| Semantic | `POST /api/memories/search/semantic` | ONNX embedding cosine ranking when `SemanticSearch` model and vocabulary files are available; otherwise local token/tag/title/reference/alias scoring with match explanations. |
| Hybrid | `POST /api/memories/search/hybrid` | Lucene.NET lexical analysis + the active semantic ranker, fused with Reciprocal Rank Fusion (RRF). Best for discovery. |

All three accept `query`, `tags` (comma-separated, filter by any match), `status`, and `limit`.

The embedding path uses ONNX Runtime, a local WordPiece vocabulary, E5-style `query:`/`passage:` prefixes, and an exact in-memory cosine scan over the filtered memory set. It intentionally falls back to the existing token scorer when model assets are missing or unusable, so fresh clones still work without redistributing model binaries.

## MCP Tools

The MCP endpoint is at `http://localhost:5089/mcp`. VS Code config lives in `.vscode/mcp.json`. Up to nineteen tools are exposed by default (eight read-only chat-allowlisted, seven task tools, two page write tools requiring edit permission, plus two source-aware tools available only over MCP):

| Tool | Key args | Returns | Permission |
| --- | --- | --- | --- |
| `memorysmith_search` | `query`, `tags`, `status`, `limit` | Lexical results | View |
| `memorysmith_semantic_search` | `query`, `tags`, `status`, `limit` | Scored results with match reasons | View |
| `memorysmith_hybrid_search` | `query`, `tags`, `status`, `limit` | RRF-ranked results | View |
| `memorysmith_context_pack` | `query` or `ids`, `tags`, `referenceDepth`, `includeBacklinks`, `maxRecords`, `maxContentChars`, `format` | Search results + linked references/conflicts in one response | View |
| `memorysmith_get` | `id` | Single read-only record by ID | View |
| `memorysmith_page_search` | `query`, `limit` | Markdown page summaries from `Data/Pages` | View |
| `memorysmith_page_get` | `slug`, `maxCharacters` | One markdown page body, bounded for safe context inclusion | View |
| `memorysmith_unified_search` | `query`, `memoryLimit`, `pageLimit`, `tags`, `status` | One call across memories + pages, returning separate memory and page result sections | View |
| `memorysmith_task_list` | `query`, `status`, `assignee`, `limit` | Task summaries from `Data/Tasks` | View |
| `memorysmith_task_get` | `idOrKey` | One full task record by id or key | View |
| `memorysmith_task_create` | `title`, `description`, `priority`, `labels`, `slug` | Creates a task and records task activity | **Edit** |
| `memorysmith_task_update` | `idOrKey`, editable task fields such as `title`, `description`, `priority`, `labels` | Updates task fields and records task activity | **Edit** |
| `memorysmith_task_set_status` | `idOrKey`, `status`, `note` | Changes status and records status history | **Edit** |
| `memorysmith_task_add_comment` | `idOrKey`, `body` | Adds a task comment | **Edit** |
| `memorysmith_task_add_attachment` | `idOrKey`, `name`, `kind`, `uri` | Adds an absolute http/https task attachment URI | **Edit** |
| `memorysmith_page_save` | `markdown`, `slug` (opt), `title` (opt) | Creates or updates a wiki page; returns slug, title, and updated timestamp | **Edit** |
| `memorysmith_page_delete` | `slug` | Deletes a wiki page; returns success or not-found | **Edit** |
| `memorysmith_source_bundle` | `ids` or `query`/`tags`/`limit`, `maxFileBytes`, `format` | Records + resolved file content slices for every source link (MCP only) | Source bundle |
| `memorysmith_find_by_source` | `pattern` | Records whose source link URIs match the substring (MCP only) | Source bundle |

**`memorysmith_context_pack` tips:**

- Use `query` for open-ended discovery; use `ids` for anchoring to known records.
- `referenceDepth=1` follows one hop of `References` and `Conflicts` from root records.
- `includeBacklinks=true` adds records that reference the roots.
- `format=json` returns structured JSON for agent parsing; the default is Markdown prose.
- The tool reports warnings for missing roots, missing links, or records omitted after hitting `maxRecords`.

**Authorization notes:**

- Memory and page read tools require the normal view policy for the caller.
- Page tools also respect each page's minimum visibility role, so `memorysmith_page_search`, `memorysmith_page_get`, and `memorysmith_unified_search` omit pages the caller cannot view.
- Task read tools require view permission. Task write tools require edit permission and share validation with `/api/tasks`, including task status, assignee, page-link, and attachment URI safety rules.
- `memorysmith_page_save` and `memorysmith_page_delete` require edit permission and still apply page visibility rules, including Admin-only minimum-role restrictions.
- `memorysmith_source_bundle` and `memorysmith_find_by_source` are MCP-only `SensitiveRead` tools. They require the source-bundle policy because they can resolve local source-link file slices, and they are intentionally not available as chat-requested model tools. The source-bundle policy is granted to Editor and Admin callers, configured API-key requests, and auth-disabled local installs; Viewer callers, including the default anonymous Viewer role, can list and read normal memory/page content but cannot call source-bundle tools.
- `MemorySmith:Mcp:DisabledTools` hides individual tools from `tools/list` and rejects `tools/call` for those names. `MemorySmith:Mcp:EnabledTools` explicitly opts in descriptor-level default-off tool names; `DisabledTools` wins if a tool is listed in both places. Existing tools default on unless disabled, preserving the current MCP surface.

**`memorysmith_source_bundle` tips:**

- Combine with `memorysmith_context_pack` results: get the context pack first, then bundle the source for the most relevant records.
- `format=jsonl` returns one JSON object per line (streaming-friendly); `format=json` returns a single object with `memoryCount`, `sourceCount`, and `entries`.

## Configuration

All settings live under `MemorySmith` in `appsettings.json`:

```json
{
  "MemorySmith": {
    "DataPath": "../Data/Memories",
    "PagesPath": "../Data/Pages",
    "EventLogPath": "../Data/Events/audit.log",
    "VarsPath": "../Data/vars.json",
    "ApiKey": null,
    "AllowRemoteApi": false,
    "DataProtectionKeysPath": "../Data/Keys",
    "SettingsOverridePath": null,
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
      "OpenLocalEditorCompatibility": true
    },
    "Audit": {
      "JsonlEnabled": true,
      "JsonlPath": "../Data/Events/audit-{yyyy}-W{week}.jsonl"
    },
    "History": {
      "RootPath": "../Data/.history",
      "PageMode": "Snapshot",
      "MemoryMode": "JsonPatchWithCheckpoints"
    },
    "Pages": {
      "DefaultMinimumRole": "Anonymous",
      "AllowRawHtml": false
    },
    "SemanticSearch": {
      "EmbeddingsEnabled": true,
      "ModelPath": "Models/embedding-model.onnx",
      "VocabularyPath": "Models/vocab.txt",
      "MaxInputTokens": 512,
      "MaxIndexedTextCharacters": 6000,
      "QueryPrefix": "query: ",
      "DocumentPrefix": "passage: "
    },
    "Maintenance": {
      "Enabled": true,
      "TriageMinutes": 5,
      "IndexingMinutes": 60,
      "ConsolidationHours": 24,
      "StartupGraceSeconds": 30
    },
    "SourceLinks": {
      "MaxReadBytes": 65536,
      "AllowOpenWithDefaultApp": true,
      "AllowedFileRootVariables": [ "MemorySmithRepo" ],
      "AllowedFileRoots": []
    },
    "Mcp": {
      "EnabledTools": [],
      "DisabledTools": []
    },
    "Chat": {
      "Provider": "Ollama",
      "OllamaEndpoint": "http://localhost:11434",
      "OllamaModel": "gemma4:e4b",
      "OllamaContextWindowTokens": null,
      "GitHubModel": "gpt-4.1",
      "GitHubTokenEnvironmentVariable": "GITHUB_TOKEN",
      "GitHubModels": [
        { "Name": "gpt-4.1", "ChatMultiplier": 0, "IsPreferred": true, "Description": "Free/standard Copilot GPT option when available" },
        { "Name": "gpt-4.1-mini", "ChatMultiplier": 0, "IsPreferred": true, "Description": "Free/low-cost GPT mini option when available" },
        { "Name": "gpt-4o-mini", "ChatMultiplier": 0, "IsPreferred": true, "Description": "Free/low-cost GPT-4o mini option when available" },
        { "Name": "claude-3.5-haiku", "IsPreferred": true, "Description": "Lower-cost Claude Haiku option before Sonnet" },
        { "Name": "gpt-5.1-mini", "Description": "GPT-5.1 mini option when available" },
        { "Name": "gpt-4o", "Description": "GPT-4o option when available" },
        { "Name": "gpt-5", "Description": "GPT-5 option when available" },
        { "Name": "claude-sonnet-4.5", "Description": "Claude Sonnet option when available after cheaper candidates" }
      ],
      "SystemPromptPath": "Prompts/wiki-chat-agent.md",
      "RequestTimeoutSeconds": 600,
      "MaxContextRecords": 5,
      "MaxContextPages": 5,
      "MaxContextItemCharacters": 4000,
      "MaxHistoryMessages": 16,
      "MaxAttachmentCharacters": 120000,
      "MaxAttachmentBytes": 8388608,
      "AttachmentTempFileRetentionHours": 24,
      "ToolCallsEnabled": true,
      "MaxToolIterations": 2,
      "MaxToolCallsPerTurn": 3,
      "MaxToolResultCharacters": 12000,
      "AgentWritesEnabled": false
    }
  }
}
```

Override via `appsettings.LocalOverrides.json`, a custom `SettingsOverridePath`, or environment variables (`MemorySmith__DataPath`, etc.).

For an operator-facing map of the active settings, see [`Data/Pages/guides/configuration-reference.md`](Data/Pages/guides/configuration-reference.md). For chat model profile routing and maintenance-agent assignments, see [`Data/Pages/guides/agent-configuration.md`](Data/Pages/guides/agent-configuration.md).

- **`ApiKey`** — if set, all API and MCP requests must include `X-Api-Key: &lt;value&gt;`. Leave `null` for local use. The shared API key can satisfy non-admin API/MCP policies; it does not grant admin, user-management, settings, audit, diagnostics, or restore access.
- **`AllowRemoteApi`** — set `true` to allow non-localhost callers. Off by default.
- **`DataProtectionKeysPath`** — stores ASP.NET Core cookie/data-protection keys outside build output so local sign-in cookies survive app restarts.
- **`Database:*`** — controls the SQLite metadata database used for users, roles, provider links, login history, audit metadata, version metadata, token metadata, admin settings, and semantic-index metadata. Content files remain in `Data/Memories` and `Data/Pages`.
- **`SettingsOverridePath`** — optional path for admin-edited local settings. Defaults to `appsettings.LocalOverrides.json` beside the running app.
- **`Blazor:MaximumReceiveMessageSizeBytes`** — maximum SignalR payload size for interactive server circuits. The Admin settings UI exposes this, but changing it typically requires reconnecting or restarting the app to affect existing circuits.
- **`Auth:*`** — controls cookie/RBAC behavior. `AnonymousAccess=Viewer` keeps local browsing open by default; config-derived anonymous/default roles are clamped below Admin, and `OpenLocalEditorCompatibility=true` preserves pre-setup loopback write compatibility only for non-admin operations.
- **`Audit:*`** — controls the weekly JSONL audit mirror. SQLite remains the queryable metadata store.
- **`History:*`** — controls version-history artifact storage for memory and page mutations.
- **`Pages:DefaultMinimumRole`** — default minimum visibility for newly saved pages. Use `Anonymous`, `Authenticated`, or `Admin`; the admin settings UI exposes this as default page visibility.
- **`Pages:AllowRawHtml`** — enables trusted raw HTML rendering in markdown pages. Off by default; leave disabled for agent-written or unreviewed pages.
- **`SemanticSearch:*`** — controls optional ONNX embedding ranking. Relative model and vocabulary paths resolve from the configured data deployment root: the folder that contains `Memories`, `Events`, `Graph`, `Models`, and `Pages`. The default model path is `Models/embedding-model.onnx`; ONNX/model artifacts are ignored by Git, and a matching WordPiece `vocab.txt` is required before embeddings activate. Legacy `../Data/Models/...` values are also interpreted relative to that data root.
- **`Mcp:*`** — controls per-tool MCP exposure. `DisabledTools` hides named tools from `tools/list` and rejects direct `tools/call`; `EnabledTools` opts in descriptor-level default-off tools. Existing MCP tools default on unless disabled.
- **`DataPath`** — root of the memory store. Subdirectories (`Unconsolidated/`, `Working/`, `Core/`, `Deprecated/`) are created automatically.
- **`PagesPath`** — root of the markdown page store. `assets/` under this directory is served at `/page-assets` with page visibility checks for referenced assets.
- **`VarsPath`** — path to the flat JSON dict used for `%VarName%` source link expansion.
- **`SourceLinks:MaxReadBytes`** — maximum local file content returned per source-link entry by MCP source bundle reads.
- **`SourceLinks:AllowUnrestrictedSourceReads`** — opt-in broad read mode for local source-linked files when you want reads outside the configured allowlist.
- **`SourceLinks:ReadContextLinesBefore` / `SourceLinks:ReadContextLinesAfter`** — line padding added around requested source line ranges so source-grounded reads can include nearby context.
- **`SourceLinks:AllowOpenWithDefaultApp`** — allows Ctrl+Click source-link opening after variable resolution and allowed-root checks.
- **`SourceLinks:AllowedFileRootVariables`** — variable names whose resolved values are trusted roots for local source-link file reads. Defaults to `MemorySmithRepo`.
- **`SourceLinks:AllowedFileRoots`** — optional explicit local roots, useful when source links need access outside the repo wiki root.
- **`SourceLinks:DeniedFileRootVariables` / `SourceLinks:DeniedFileRoots`** — explicit deny roots that always block source-link reads and opening, even when broad reads are enabled.
- **`Chat:*`** — provider, Ollama endpoint/model, GitHub Copilot model/token environment settings, prompt path, timeout, context/history/attachment limits, read-only intercepted wiki tool-call limits, and whether agent-mode writes are enabled. `AgentWritesEnabled` is false by default; enabling it allows Agent mode to propose structured memory/page writes, but applying those writes still requires an authenticated Editor or Admin and explicit approval. `PreloadContextEnabled`, `MaxPreloadedContextRecords`, and `MaxPreloadedContextPages` control the small automatic pre-context used only for explicit local-knowledge prompts. `MaxContextItemCharacters` bounds each memory/page item sent to the chat provider, `MaxAttachmentCharacters` bounds text attachments, `MaxAttachmentBytes` bounds uploaded/pasted files, and `AttachmentTempFileRetentionHours` controls stale image attachment temp-file cleanup from the Chat route. `ToolCallsEnabled` allows models to request app-executed MemorySmith search/context/get calls inside the same user turn, while `MaxToolIterations`, `MaxToolCallsPerTurn`, and `MaxToolResultCharacters` bound cost and result size. `OllamaContextWindowTokens` is optional metadata for the UI usage meter when a local model's context window is known. Set `OllamaModel` to a model returned by `ollama list`; set `GitHubModel` to a Copilot model available to the authenticated GitHub account. The configured GitHub fallback order prefers free/low-cost GPT models first, then Claude Haiku, then Sonnet. The UI can query providers directly for available models and stores the last selected provider/model in browser storage.

## Windows Service

Easy local redeploy from an elevated PowerShell session:

```powershell
.\Scripts\Redeploy-MemorySmithService.ps1
```

Optional LAN HTTPS path with a certificate file:

```powershell
.\Scripts\Redeploy-MemorySmithService.ps1 -UseHttps -HttpsPort 7090 -HttpsCertificatePath .\artifacts\certs\memorysmith.home.arpa-7090.pfx -HttpsCertificatePassword (Get-Content .\artifacts\certs\memorysmith.home.arpa-7090-password.txt -Raw)
```

Current LAN certificate example for this repo:

- Host name: `memorysmith.home.arpa`
- LAN IP: `192.168.1.8`
- HTTPS port: `7090`
- Trust anchor for other devices: `artifacts/certs/MemorySmith-LAN-Root-CA.cer`

If clients should browse by name, make `memorysmith.home.arpa` resolve to `192.168.1.8` on the LAN.

Publish the app, then from an elevated PowerShell session:

```powershell
# Install
.\MemorySmith.App.exe install --service-name MemorySmith --service-display-name "MemorySmith" --memory-directory C:\MemorySmith\Memories --port 5089

# Uninstall
.\MemorySmith.App.exe uninstall --service-name MemorySmith

# Help
.\MemorySmith.App.exe --help
```

Install flags:

| Flag | Purpose |
| --- | --- |
| `install`, `--install-service` | Create the Windows Service |
| `uninstall`, `--uninstall-service` | Stop and delete the Windows Service |
| `--service-name` | Service name. Default: `MemorySmith` |
| `--service-display-name` | Display name in Services UI |
| `--service-description` | Windows Service description |
| `--service-start-type` | `auto`, `demand`, or `disabled` |
| `--memory-directory` | Target `MemorySmith:DataPath`; adjacent `Pages`, `Events/audit.log`, and `vars.json` are derived from its parent folder |
| `--port` | Local HTTP port. Default install port: `5089` |

Arguments after `--` are still passed as runtime args to the service process for advanced ASP.NET Core settings. Use either `--port` or a custom runtime `--urls`, not both.

`Redeploy-MemorySmithService.ps1` keeps the current HTTP path on `5089` and can optionally add HTTPS with `-UseHttps`. The current repo example uses a certificate whose SAN matches both `memorysmith.home.arpa` and `192.168.1.8`, with HTTPS served on port `7090`.

For this repository's live project wiki, the target memory directory is `<repo>\Data\Memories`. A local service install on port 5089 would be:

```powershell
.\MemorySmith.App.exe install --memory-directory C:\Path\To\MemorySmith\Data\Memories --port 5089
```

After installation, start the service from `services.msc` or PowerShell, then open `http://localhost:5089/health` for runtime configuration, storage diagnostics, activity, and maintenance telemetry.

## Validate

Use one entrypoint for local validation:

```powershell
.\Scripts\Validate-Repo.ps1
```

Common optional variants:

```powershell
.\Scripts\Validate-Repo.ps1 -IncludeCoverage
.\Scripts\Validate-Repo.ps1 -IncludeE2E
.\Scripts\Validate-Repo.ps1 -IncludeDocs
```

The script runs build/test plus live-wiki integrity checks by default, then adds optional coverage, browser regression, and docs-site validation when requested.

Default local validation includes task-record integrity, live memory-record validation, markdown page-link checks, and markdown path-literal checks. The task-record check verifies JSON parseability, required identity fields, filename/id consistency, recognized statuses, and unique task ids/keys. The memory-record check validates the live `Data/Memories` corpus against filename/id alignment, status-folder alignment, and the application validation rules used by `MemoryApplicationService`.

The CI workflow has three stable validation jobs:

- `build-and-test`: restore, task-record validation, live memory-record validation, page validators, build, NUnit tests, and Cobertura coverage artifacts.
- `browser-route-smoke`: Playwright route smoke coverage for `/memories`, `/pages`, `/chat`, `/tasks`, and `/health`, plus uploaded screenshots/manifest from `artifacts/browser-validation/route-smoke`.
- `browser-navigation-freeze`: Playwright navigation-freeze regression with failure screenshots, video, traces, and HTML report artifacts.

Underlying commands (also available individually):

```powershell
dotnet build MemorySmith.slnx -v minimal
dotnet test MemorySmith.slnx -v minimal
```

Collect local Cobertura coverage with the same collector used by CI:

```powershell
dotnet test MemorySmith.slnx --configuration Release --collect:"XPlat Code Coverage" --results-directory artifacts/TestResults -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura
```

Run the browser route-smoke suite (Playwright) with per-route screenshots and manifest output:

```powershell
Set-Location e2e
npm ci
npx playwright install chromium
npm run test:route-smoke
```

Artifacts are written to `artifacts/browser-validation/route-smoke/` with one screenshot per route plus `manifest.json`.

Run the browser route-hop regression suite (Playwright) for navigation freeze protection:

```powershell
Set-Location e2e
npm ci
npx playwright install chromium
npm run test:nav-freeze
```

Generate the Doxygen wiki locally when Doxygen and Graphviz are installed:

```powershell
doxygen docs/Doxyfile
```

Rebuild the GitHub Pages wiki site locally, open the generated site, or trigger a deployment with the helper script:

```powershell
.\Scripts\Publish-WikiSite.ps1
.\Scripts\Publish-WikiSite.ps1 -OpenSite
.\Scripts\Publish-WikiSite.ps1 -Deploy
```

Validate task records for parseability and unique ids/keys:

```powershell
.\Scripts\Test-TaskRecords.ps1
```

Validate live memory records for filename/id alignment, status-folder alignment, and application contract invariants:

```powershell
.\Scripts\Test-MemoryRecords.ps1
```

Validate local markdown page links (relative .md links and existing targets under `Data/Pages`):

```powershell
.\Scripts\Test-PageLinks.ps1
```

Validate plain-text `Data/Pages/...` markdown path literals (for example in architecture notes and council pages) so moved pages do not leave stale references behind:

```powershell
.\Scripts\Test-PagePathLiterals.ps1
```

Enable the same check on every commit with the repo-managed pre-commit hook:

```powershell
git config core.hooksPath .githooks
```

After setting `core.hooksPath`, `git commit` runs the page-link validator automatically.
After setting `core.hooksPath`, `git commit` runs the task-record and page validators automatically.

The script creates or reuses a local Python virtual environment under `artifacts/tools/docs-site-venv`, installs the `markdown` package used by CI, rebuilds `docs/output/wiki`, and with `-Deploy` dispatches `.github/workflows/docs-pages.yml` through GitHub CLI. `-Deploy` requires `gh auth login` and only runs from `main` or `master`.

Run BenchmarkDotNet search benchmarks:

```powershell
dotnet run -c Release --project MemorySmith.Benchmarks -- --smoke
dotnet run -c Release --project MemorySmith.Benchmarks -- --filter *SearchBenchmarks*
```

The solution builds `MemorySmith.App` as the single deployable host. `MemorySmith.Tests` contains an actively growing NUnit suite spanning unit tests, integration tests (via `WebApplicationFactory`), SQLite metadata coverage, auth/audit/history coverage, Markdown rendering coverage, task/governance flows, and a `[Category("Benchmark")]` suite of search quality probes with latency thresholds. GitHub Actions collects Cobertura coverage in CI and publishes a Doxygen HTML wiki through the Pages workflow.
