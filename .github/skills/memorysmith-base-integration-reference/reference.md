# MemorySmith.Agent — Base Platform Integration & Project Wiki Reference

> **Date:** 2026-07-10 | **Author:** Agent 10 (coordinated memory audit sweep) | **Confidence:** 95%

---

## Table of Contents

1. [RestMemoryGateway — Agent-Side Memory Interface](#1-restmemorygateway)
2. [MemorySmithBlueprintRepository — Blueprint Page Backend](#2-memorysmithblueprintrepository)
3. [MemorySmithItemRegistry — Item Spec Page Backend](#3-memorysmithitemregistry)
4. [Page Search Score=0.0 — Root Cause](#4-page-search-score00)
5. [World KB TestWorld Setup](#5-world-kb-testworld-setup)
6. [Project Wiki Structure](#6-project-wiki-structure)
7. [Data Folder Policies](#7-data-folder-policies)
8. [MC Version Support Range](#8-mc-version-support-range)
9. [Cross-Repo CI Validation Differences](#9-cross-repo-ci-validation-differences)
10. [Appendices](#10-appendices)

---

## 1. RestMemoryGateway

**File:** `D:\@Repos\MemorySmith.Agent\Agent.Memory\RestMemoryGateway.cs`

The `RestMemoryGateway` class implements `IMemoryGateway` by calling MemorySmith's REST API via `HttpClient`. It provides **5 methods**:

| Method | HTTP Verb | Endpoint | Purpose |
|--------|-----------|----------|---------|
| `SearchAsync(query, ct)` | `GET` | `/api/search?query={q}&limit=20` | Unified memory+page search |
| `GetPageAsync(pageId, ct)` | `GET` | `/api/pages/{slug}` | Fetch page body by slug |
| `CreatePageAsync(title, content, type, ct)` | `POST` | `/api/pages` | Create a new wiki page |
| `UpdatePageAsync(pageId, content, title?, ct)` | `PUT` | `/api/pages/{slug}` | Update existing page |
| *(implicit via HttpClient)* | — | — | HTTP layer handles connection pooling |

### HTTP Client Configuration

- Configured via `RestMemoryGatewayOptions` (`D:\@Repos\MemorySmith.Agent\Agent.Memory\RestMemoryGatewayOptions.cs`)
- Default `BaseUrl`: `http://localhost:5000` (agent KB)
- Default `TimeoutSeconds`: 30
- Optional `X-Api-Key` header via `ApiKey` property
- Default page role for created pages: `"Anonymous"`
- Separate `WorldKbUrl`, `WorldApiKey`, `WorldTimeoutSeconds` for dual-gateway setup

### Retry Policy Status

- **No automatic retry** — transient HTTP failures return empty results rather than crashing the tool loop (Sprint 53, TSK-0193)
- Specifically catches: `HttpRequestException`, `TaskCanceledException` (timeout), `JsonException`
- `OperationCanceledException` is propagated for cooperative cancellation
- Each catch logs a `LogWarning` with context

### Cache TTLs

Caching is handled in `MemorySmithItemRegistry`, **not** in `RestMemoryGateway` itself:

| Cache | TTL | Location | Notes |
|-------|-----|----------|-------|
| Item spec cache | `ItemCacheTtlSeconds` (default 60s) | `MemorySmithItemRegistry` | `ConcurrentDictionary` in-memory |
| Null entry cache | `NullCacheTtlSeconds` (default 5s) | `MemorySmithItemRegistry` | Shorter TTL so transient outages don't permanently mask items (TSK-0092) |
| Disable caching | Set `ItemCacheTtlSeconds = 0` | — | Disables all caching, including null entries |

### Key Design Details

- **Search:** `SearchHit.Score` is `double?` — pages always get `Score=null` from MemorySmith's search API (see §4). Agent side converts: `h.Score ?? 0.0`
- **UpdatePageAsync:** Sprint 51 (TSK-0138) added explicit-title path to avoid GET-then-PUT race condition
- **CreatePage slug generation:** `ToSlug()` helper converts title to lowercase, spaces→hyphens, strips dots, slashes→hyphens

---

## 2. MemorySmithBlueprintRepository

**File:** `D:\@Repos\MemorySmith.Agent\Agent.Memory\MemorySmithBlueprintRepository.cs`

Implements `IBlueprintRepository` backed by MemorySmith wiki pages under `blueprints/{slug}`.

### Methods

| Method | Behavior |
|--------|----------|
| `GetAsync(blueprintId, ct)` | Three-stage lookup: (1) direct page slug `blueprints/{slug}`, (2) local file fallback, (3) search fallback |
| `SearchAsync(query, ct)` | Searches via gateway, filters for pages with `"blueprints"` in PageId, supplements with local files |
| `SaveAsync(blueprint, ct)` | **Not implemented** (`NotImplementedException`). Blueprints are authored manually as wiki pages. |

### Lookup Strategy (GetAsync)

1. **Direct slug lookup** — `memory.GetPageAsync("blueprints/{slug}")` — deterministic, preferred per D-003
2. **Local file fallback** — checks `{localPagesRoot}/Data/Pages/blueprints/{slug}.md` for offline/dev runs
3. **Search fallback** — `memory.SearchAsync("blueprints/{blueprintId}")` for IDs not matching normalization convention

### Page Prefix

Pages must be at `blueprints/{id}` — e.g. `blueprints/my-house` for blueprint `my_house`.

### Local Fallback Discovery

`FindLocalPagesRoot()` walks up from `AppContext.BaseDirectory` looking for `Data/Pages/blueprints/` directory. Returns first matching parent directory.

### Validation

- Sprint 45 (TSK-0094): rejects blueprints with empty `Id` after parsing
- Preserves `RawMarkdown` for later re-parsing by `GoalFactory`

---

## 3. MemorySmithItemRegistry

**File:** `D:\@Repos\MemorySmith.Agent\Agent.Memory\MemorySmithItemRegistry.cs`

Implements `IItemRegistry` backed by MemorySmith wiki pages under `item-registry/{slug}`.

### Methods

| Method | Behavior |
|--------|----------|
| `GetAsync(itemId, ct)` | Normalizes ID→slug, checks cache, then three-stage fetch (gateway → local → search) |
| `SearchAsync` | Not directly exposed; item search is done via `IKnowledgeResolver` |

### Lookup Strategy (GetAsync)

1. **Normalize** — underscores→hyphens, lowercase (`oak_log` → `oak-log`)
2. **Cache check** — TTL-gated `ConcurrentDictionary` (default 60s)
3. **Direct page lookup** — `memory.GetPageAsync("item-registry/{slug}")`
4. **Local file fallback** — `Data/Pages/item-registry/{slug}.md`
5. **Search fallback** — `memory.SearchAsync("item-registry/{itemId}")`

### Cache Details

- Key: normalized slug (lowercase, hyphens)
- Entry: `(ItemSpec? Spec, DateTimeOffset Expires)`
- Null entries cached with shorter TTL (5s default) via `NullCacheTtlSeconds`
- Disabled when `ItemCacheTtlSeconds = 0`

### Page Format

Wiki pages use front-matter format. Example (`D:\@Repos\MemorySmith.Agent\Data\Pages\item-registry\`):

```markdown
# oak-log

item_id: oak_log
display_name: Oak Log
source_blocks: oak_log, birch_log, spruce_log
requires_smelting: false
min_harvest_level: 0
```

Required fields: `item_id`, `display_name` (or `# heading` as fallback for `item_id`)

### Parsing

- `ParseItemSpec()` is `public static` for direct unit testing (`ItemSpecParserTests`)
- Lines parsed as `key: value` pairs, case-insensitive
- Headings (`# text`) used as item_id fallback
- Invalid field keys (non-alphanumeric/underscore characters) silently skipped

---

## 4. Page Search Score=0.0

**Root cause documented in:** `/memories/repo/page-search-score-zero-facts.md`

### Full Chain

```
MemorySmith.SearchController.cs:53
  → pageResults.Select(...new UnifiedSearchResult(..., Score=null))
  → PageSummary has NO Score property (PageService.cs:216)
  → Agent.RestMemoryGateway.SearchAsync: h.Score ?? 0.0
  → SearchResult.Score is non-nullable double, always 0.0 for pages
  → SearchMemoryTool.ExecuteAsync ignores Score field
```

### Root Cause

MemorySmith's `PageSummary` model (`MemorySmith.Core`) has **no `Score` property**. When the search controller constructs `UnifiedSearchResult` for page hits, it passes `Score=null`. This contrasts with memory records which have a computed BM25/semantic score.

### Impact

- Pages always sort to the bottom of search results (`.OrderByDescending(result => result.Score ?? 0)`)
- The `SearchMemory` tool in the agent never sees page results as top hits
- Page content is only accessible via direct `GetPageAsync` calls with known slugs

### Fix Options

| Approach | Effort | Scope |
|----------|--------|-------|
| Agent-side quick fix: set minimum page score to 0.05 in `RestMemoryGateway` | Small | Agent only |
| Cross-repo proper fix: Add `double? Score` to `PageSummary`, add BM25/semantic scoring for pages | Large | MemorySmith + Agent |

**Current status:** Root cause and fix options preserved in `/memories/repo/master-knowledge-base.md`. Cross-repo fix pending.

---

## 5. World KB TestWorld Setup

**Source memory:** `/memories/repo/world-kb-testworld-setup.md`

### Location

`D:\Minecraft\MemorySmith\TestWorld`

### Port

**6869** (convention per `world-kb-deployment.md`)

### Directory Structure

```
TestWorld/
├── Memories/Core/        # 8 mc-*.json memory records seeded
├── Memories/Working/     # empty (gitkeep)
├── Memories/Unconsolidated/
├── Memories/Deprecated/
├── Pages/                # 8 .md pages seeded
├── Events/
├── Keys/
├── Models/
├── .history/
├── vars.json
├── appsettings.WorldKb.json
└── README.md
```

### Memory Records (8, prefix: `mc-`)

| File | Content |
|------|---------|
| `mc-home.json` | Root KB overview |
| `mc-blocks-reference.json` | Blocks, ores, mining tools |
| `mc-items-reference.json` | Tools, weapons, food, resources |
| `mc-biomes-reference.json` | All Overworld/Nether/End biomes |
| `mc-game-mechanics.json` | Day/night, health, enchanting, redstone, villagers, status effects |
| `mc-commands-reference.json` | Server commands |
| `mc-materials-groups.json` | Tool effectiveness, block hardness, material categories |
| `mc-mobs-reference.json` | Passive/neutral/hostile/boss mobs with HP, damage, drops |

### Wiki Pages (8)

| File | Content |
|------|---------|
| `home.md` | Landing page with quick links and world info table |
| `blocks/overview.md` | Block categories and properties |
| `items/overview.md` | Tools, weapons, food, components |
| `recipes/overview.md` | Crafting and smelting recipes |
| `biomes/overview.md` | Biome types organized by climate |
| `guides/getting-started.md` | First-day survival guide |
| `guides/mining.md` | Ore distribution, mining strategies, safety |
| `structures/README.md` | Template for logging discovered structures |

### Config

- `appsettings.WorldKb.json` overrides all MemorySmith paths to `TestWorld`
- Auth: Anonymous access = Editor, `OpenLocalEditorCompatibility = true`
- Database: SQLite at `TestWorld/memorysmith.db` (auto-created on first run)

### How to Run (Development)

```powershell
$env:MemorySmith__DataPath="D:\Minecraft\MemorySmith\TestWorld\Memories"
$env:MemorySmith__PagesPath="D:\Minecraft\MemorySmith\TestWorld\Pages"
dotnet run --project D:\@Repos\MemorySmith\MemorySmith.App -- --urls "http://localhost:6869"
```

### Deployment Scripts

In `D:\@Repos\MemorySmith.Agent\Scripts\`:
- `Deploy-WorldWiki.ps1` — Builds and installs as Windows service "MemorySmith - World Wiki (TestWorld)" on port 6869/6969
- `Get-WorldWikiStatus.ps1` — Check service status
- `Stop-WorldWikiService.ps1` — Stop the service
- `Uninstall-WorldWikiService.ps1` — Stop and unregister

---

## 6. Project Wiki Structure

### MemorySmith Repo (`D:\@Repos\MemorySmith\Data\Memories\`)

```
Memories/
├── Core/           # 57 .json files — active structured project wiki records
├── Working/        # 5 files — in-progress structured memories
├── Unconsolidated/ # 1 file (.gitkeep only) — raw memory inbox
└── Deprecated/     # 1 file (.gitkeep only) — superseded records
```

#### Core Memory Records Count & Distribution

**Total: 57 files** (56 `.json` + 1 `.md` + 1 `.gitkeep`)

Categories by prefix/topic:

| Prefix / Topic | Count | Examples |
|----------------|-------|---------|
| `project-wiki-*` (product wiki) | 52 | architecture, storage, MCP, search, UI, chat, tasks, validation, etc. |
| `agent-*` (agent-related) | 0 | (agent memories live in Agent repo) |
| `memory-system-rfc-*` | 1 | Council review |
| `ai-memory-suite-*` | 2 | Governance, implementation plan |
| `external-parity-*` | 1 | External parity observations |
| `agent-collaboration-*` | 1 | Collaboration principles |
| `task-priority-*` | 1 | Priority/severity rubric |
| `planning-goals-reference.md` | 1 | Markdown reference (not JSON) |

Key records:
- `project-wiki-active-architecture.json` — Single-host layout, app pipeline
- `project-wiki-data-folder-policy.json` — Data folder conventions, test fixture policy
- `project-wiki-storage-rules.json` — FileMemoryStore behavior
- `project-wiki-event-store.json` — FileEventStore JSONL audit log
- `project-wiki-source-links-feature.json` — SourceLink model, `%VarName%` expansion
- `project-wiki-ui-architecture.json` — Blazor Server, MudBlazor 9.4
- `project-wiki-mcp-integration.json` — MCP endpoint, 22-tool catalog
- `project-wiki-hybrid-search-rrf.json` — Lucene.NET + semantic RRF fusion
- `project-wiki-onnx-semantic-embeddings.json` — ONNX Runtime embedding ranker
- `project-wiki-semantic-search-gap.json` — Remaining semantic-search limitations
- `project-wiki-validation-command.json` — Build and test instructions
- `project-wiki-test-architecture.json` — NUnit fixture strategy (~419 test methods)
- `project-wiki-chat-*` (6) — Chat provider, attachments, local storage, streaming, configuration, agent instructions
- `project-wiki-code-search-*` (2) — Relevance suite, UI
- `project-wiki-configuration-settings-current.json` — Current configuration surface
- 5 test fixture records: `overview`, `context-root`, `reference-child`, `conflict-note`, `backlink-source`

#### Working Memory Records (5 files)

Located at `D:\@Repos\MemorySmith.Agent\Data\Memories\Working\`? No — the Working directory under `D:\@Repos\MemorySmith\Data\Memories\Working\` contains:
- `.gitkeep`
- `adding-openai-compatible-chat-provider.json`
- `dev-training-venv.md`
- `task-tracking-feature-20260523.json`
- `tool-system-reference.md`
- `user-defined-requirements-20260531.json`

#### Unconsolidated & Deprecated

Both directories contain only `.gitkeep` — no active records.

### Wiki Pages (`D:\@Repos\MemorySmith\Data\Pages\`)

```
Pages/
├── architecture.md           # Single-host architecture overview
├── search-and-chat.md        # Search and chat guide
├── operations.md             # Operations guide
├── home.md                   # Landing page
├── council/                  # 24 council review reports (md files)
├── features/                 # 18 feature .md pages + 3 .page.json metadata files
├── plans/                    # 6 implementation plan documents
├── guides/                   # 13 developer guides
├── research/                 # ~22 research documents (various topics)
├── assets/                   # Static files for page rendering
├── audits/                   # Audit reports
├── chat/                     # Chat-related pages
├── ops/                      # Operations pages
├── requests/                 # Feature requests
├── requirements/             # Requirements documents
├── Tasks/                    # Task-related pages
├── workbench/                # Workbench pages
└── Training/                 # Training-related pages
```

### Agent Repo Wiki Pages (`D:\@Repos\MemorySmith.Agent\Data\Pages\`)

```
Pages/
├── home.md                   # Agent landing page
├── architecture.md           # Bounded contexts, runtime flow
├── planner.md                # HTN planner, decomposers
├── tool-registry.md          # ToolDispatcher, MCP catalog
├── memory.md                 # IMemoryGateway, dual KB, WorldFacts
├── chat-system.md            # In-game chat interpretation
├── vision.md                 # Vision subsystem
├── blueprints.md             # Blueprint schema
├── roadmap.md                # Sprint history
├── agent-profile.md          # Agent identity
├── decisions.md              # ADR log
├── events-world-state-reference.md
├── user-requirements.md
├── Features/                 # 10 feature deep-dive markdown pages
├── guides/                   # 12+ developer guides
├── item-registry/            # Item spec pages (MC items)
├── blueprints/               # Blueprint markdown pages
├── council/                  # Council review documents
├── Tasks/                    # Sprint handoff documents
├── Handoffs/                 # Sprint handoff files
├── policies/                 # Policy documents (package-vetting.md)
├── policies/                 # Policy documents
├── Analysis/                 # Analysis documents
├── Audits/                   # Audit reports
├── Proposals/                # Proposals
├── Preferences/              # Preferences
├── memories/                 # Memory references (codebase-facts.md)
├── assets/                   # Static assets
└── .obsidian/                # Obsidian vault config
```

### Wiki Data Path

The app defaults `MemorySmith:DataPath` to `../Data/Memories` and `MemorySmith:PagesPath` to `../Data/Pages`.

---

## 7. Data Folder Policies

### MemorySmith Repo

**Source:** `D:\@Repos\MemorySmith\Data\Memories\Core\project-wiki-data-folder-policy.json`

Policies:
1. `Data/Memories/` is the **structured project memory wiki** — describes actual current state, durable facts, rules, and implemented behavior
2. `Data/Pages/` is the **markdown wiki** for longer notes, design docs, roadmaps, future tasks, and council reviews
3. **Future plans belong in pages, not memory records**
4. Tests must **copy** `Data/Memories/` to a temp directory; never mutate the live wiki
5. Purpose-built `test-fixture` tagged records provide deterministic test cases

### MemorySmith.Agent Repo

**Source:** `D:\@Repos\MemorySmith.Agent\Data\Pages\policies\package-vetting.md`

Five policies (P-1 through P-5):
- **P-1:** Documented justification required for every new package
- **P-1a:** License whitelist — MIT, Apache-2.0, BSD-2/3-Clause only
- **P-2:** Every dependency listed in `WebUI.Blazor/wwwroot/about.html`
- **P-3:** Zero vulnerable packages (`dotnet list package --vulnerable` must return zero)
- **P-4:** No deprecated packages
- **P-5:** Direct pinning of transitive deps requires justification and removal plan

**Additional agent-side policy** (from `D:\@Repos\MemorySmith\Data\Policies\tag-policy.json`):
- Tag governance for memory classifications

---

## 8. MC Version Support Range

**Source:** `/memories/repo/mc-version-range.md`

| Property | Value |
|----------|-------|
| Minimum supported | Minecraft **1.16.5** |
| Maximum supported | Minecraft **1.21.6+** |
| Last verified | 2026-06-28 (both 1.16.5 and 1.21.x) |
| Mineflayer version | `^4.23.0` (caret range allows 1.21.x support) |

### Key Facts

- `bot.chat()` works for **both** 1.16.5 and 1.21.x — DO NOT replace with raw `bot._client.write()` packets
- Mineflayer 4.23.0 → 4.37.1 jump from `npm install` did NOT break chat — the raw-packet "fix" was the actual cause of kick
- Use `bot.version` to log MC version on spawn for diagnostics
- `bot.majorVersion` returns protocol version string like `"1.16"` or `"1.21"` — NOT a single digit

### Entity Observation (Sprint 55)

- Entity scan enabled via `bot.on('physicsTick', scanNearbyEntities)` — commit `06e7798`
- `ENTITY_SCAN_RADIUS = 32` blocks
- `ENTITY_SCAN_COOLDOWN_MS = 3000`
- 28 hostile mob types detected
- Verified working on **both 1.16.5 and 1.21.x** before enabling

---

## 9. Cross-Repo CI Validation Differences

### MemorySmith (`D:\@Repos\MemorySmith\.github\workflows\ci.yml`)

```yaml
- run: dotnet build --no-restore --configuration Release
- run: dotnet test MemorySmith.slnx
```

- Builds the **solution** (`.slnx`)
- Tests the solution directly
- No additional flags beyond standard dotnet commands

### MemorySmith.Agent (`D:\@Repos\MemorySmith.Agent\.github\workflows\ci.yml`)

```yaml
- run: dotnet build MemorySmith.Agent.slnx --no-restore --configuration Release -p:CopilotSkipCliDownload=true
- run: dotnet test MemorySmith.Agent.slnx
```

- Builds the **solution explicitly** (`MemorySmith.Agent.slnx`)
- Uses `-p:CopilotSkipCliDownload=true` — avoids downloading Copilot CLI during CI builds
- Same `--no-restore --configuration Release` pattern

### Key Differences

| Aspect | MemorySmith | MemorySmith.Agent |
|--------|-------------|-------------------|
| Build command | `dotnet build --no-restore --configuration Release` | `dotnet build MemorySmith.Agent.slnx --no-restore --configuration Release -p:CopilotSkipCliDownload=true` |
| Test command | `dotnet test MemorySmith.slnx` | `dotnet test MemorySmith.Agent.slnx` |
| Special flags | None | `-p:CopilotSkipCliDownload=true` |
| Local validation | `Scripts/Validate-Repo.ps1` | Individual scripts in `Scripts/` |

### Local Validation

- MemorySmith repo: `Scripts/Validate-Repo.ps1` with optional `-IncludeCoverage`, `-IncludeE2E`, `-IncludeDocs`
- MemorySmith.Agent repo: Task record validation via `Scripts/Test-TaskRecords.ps1`, plus per-sprint CI tasks

---

## 10. Appendices

### A. Dual Gateway DI Registration

From `D:\@Repos\MemorySmith.Agent\Data\Pages\memory.md` and `Data\Pages\guides\world-kb-deployment.md`:

```csharp
// Agent KB — default key
builder.Services.AddRestMemoryGateway(agentOptions);

// World KB — keyed service "world"
builder.Services.AddKeyedSingleton<IMemoryGateway>("world", worldGateway);
```

When `WorldKbUrl` is null/empty, a startup `LogWarning` is emitted and world KB tools gracefully degrade (return empty results).

### B. Tool Routing (Sprint 23+)

| Tool | Routes to | Purpose |
|------|-----------|---------|
| `SearchMemory` | World KB | Search block locations, world events |
| `CreatePage` | World KB | Save world observations |
| `GetPage` | Agent KB | Retrieve codebase guides, architecture |

### C. WorldState & StructuredFacts

- `StructuredFacts` is a capped dictionary (1000 max) of `Fact` records
- `FactSource`: `Observed`, `Inferred`, `Durable`
- Fact confidence: 0.70 if <60s old, 0.50 if older
- `LocalKnowledgeResolver` scans facts for normalized query matches

### D. Entity Observation Config (Sprint 55)

From `/memories/repo/entity-observation-enabled-sprint55.md`:
- `ENTITY_SCAN_RADIUS = 32` blocks
- `ENTITY_SCAN_COOLDOWN_MS = 3000ms`
- 28 hostile mob types detected
- Verified on both 1.16.5 and 1.21.x

### E. Data Enrichment Status

From `/memories/repo/data-enrichment-complete.md`:
- Historical expansion from 13 to 28 core memories (MemorySmith.Agent repo)
- Now superseded by `/memories/repo/master-knowledge-base.md`
- 10 feature wiki pages created in `Data/Pages/Features/`

### F. Key Agent.Memory File Paths

| File | Path |
|------|------|
| RestMemoryGateway.cs | `D:\@Repos\MemorySmith.Agent\Agent.Memory\RestMemoryGateway.cs` |
| RestMemoryGatewayOptions.cs | `D:\@Repos\MemorySmith.Agent\Agent.Memory\RestMemoryGatewayOptions.cs` |
| MemorySmithBlueprintRepository.cs | `D:\@Repos\MemorySmith.Agent\Agent.Memory\MemorySmithBlueprintRepository.cs` |
| MemorySmithItemRegistry.cs | `D:\@Repos\MemorySmith.Agent\Agent.Memory\MemorySmithItemRegistry.cs` |
| IKnowledgeResolver.cs | `D:\@Repos\MemorySmith.Agent\Agent.Memory\IKnowledgeResolver.cs` |
| LocalKnowledgeResolver.cs | `D:\@Repos\MemorySmith.Agent\Agent.Memory\LocalKnowledgeResolver.cs` |

---

## Tags

`integration-reference`, `memory-gateway`, `rest-memory-gateway`, `blueprint-repository`, `item-registry`, `project-wiki`, `world-kb`, `testworld`, `mc-version-support`, `page-search-score-zero`, `ci-validation`, `data-policy`, `dual-gateway`
