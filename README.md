# MemorySmith

MemorySmith is a single-host ASP.NET Core app for local structured memory management. It hosts a Blazor workbench UI, REST API, MCP endpoint, file-backed storage, and background maintenance in one process. The repository ships with a live project wiki inside `Data/Memories` — the app uses its own memory store as a testbed.

## Quick Start

```powershell
dotnet run --project MemorySmith.App --launch-profile http
```

Opens on `http://localhost:5089` by default. Pages:

| Route | Purpose |
|---|---|
| `/memories` | Browse, search, create, edit, delete memory records |
| `/health` | Stat cards, activity charts (queries/day, changes/day), maintenance telemetry |
| `/variables` | Manage `%VarName%` path variables used in source link URIs |
| `/api/memories` | REST CRUD for automation |
| `/api/stats`, `/api/health/*` | Stats and readiness |
| `/mcp` | MCP JSON-RPC endpoint for AI agent tool use |

## The Project Wiki

`Data/Memories/` is the live wiki for this project. The app defaults `MemorySmith:DataPath` to `../Data/Memories`, so local runs read and write those records directly.

**Using the wiki as a testbed:** before starting research or making architectural decisions, search the wiki first. The wiki records are also used as integration test fixtures — tests copy `Data/Memories/` to a temp directory so the source stays stable while being exercised through the same code paths.

### Current wiki records

| ID | What it documents |
|---|---|
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
| `project-wiki-hybrid-search-rrf` | Lucene.NET + semantic RRF fusion |
| `project-wiki-semantic-search-gap` | Local scoring approach and future embedding gap |
| `project-wiki-search-roadmap` | Planned search improvements |
| `project-wiki-validation-command` | How to build and test |
| `project-wiki-test-architecture` | NUnit fixture strategy, benchmark suite |
| `project-wiki-windows-service-operations` | Windows Service install/uninstall flags |
| `project-wiki-scope-boundaries` | What is and isn't in scope for the current implementation |
| `project-wiki-generalization-friction` | Known gaps for broader adoption |

Retrieve any record via the MCP tool `memorysmith_get` with its ID, or search the `/memories` page.

## Memory Records

Each record is a JSON file in `Data/Memories/{Status}/`. Fields:

| Field | Type | Description |
|---|---|---|
| `Id` | string | Unique, kebab-case. Must match the filename. |
| `Title` | string | Short human name. |
| `Content` | string | Body text. Max 20 000 chars. |
| `Status` | int | `0` Unconsolidated · `1` Working · `2` Core · `3` Deprecated |
| `Confidence` | double | `0.0`–`1.0` |
| `Tags` | string[] | Comma-separated labels for filtering. |
| `References` | string[] | IDs of related records (used for context pack traversal). |
| `Conflicts` | string[] | IDs of conflicting records. |
| `SourceLinks` | array | File references — see below. |
| `UsageCount` | int | Incremented by `memorysmith_get` and usage API calls. |
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

## Search

Three search modes are available in the UI (`/memories` search bar) and the REST API:

| Mode | Endpoint | Behavior |
|---|---|---|
| Keyword | `POST /api/memories/search` | Verbatim `Contains` over title, content, tags. Deterministic ordering by `LastUpdated`. |
| Semantic | `POST /api/memories/search/semantic` | Local token/tag/title/reference/alias overlap scoring with match explanations. |
| Hybrid | `POST /api/memories/search/hybrid` | Lucene.NET lexical analysis + semantic scoring, fused with Reciprocal Rank Fusion (RRF). Best for discovery. |

All three accept `query`, `tags` (comma-separated, filter by any match), `status`, and `limit`.

> The semantic side is intentionally a local token scorer, not an embedding/vector index. See `project-wiki-semantic-search-gap` for the known gap and roadmap.

## MCP Tools

The MCP endpoint is at `http://localhost:5089/mcp`. VS Code config lives in `.vscode/mcp.json`. Seven tools are exposed:

| Tool | Key args | Returns |
|---|---|---|
| `memorysmith_search` | `query`, `tags`, `status`, `limit` | Keyword results |
| `memorysmith_semantic_search` | `query`, `tags`, `status`, `limit` | Scored results with match reasons |
| `memorysmith_hybrid_search` | `query`, `tags`, `status`, `limit` | RRF-ranked results |
| `memorysmith_context_pack` | `query` or `ids`, `tags`, `referenceDepth`, `includeBacklinks`, `maxRecords`, `maxContentChars`, `format` | Search results + linked references/conflicts in one response |
| `memorysmith_get` | `id` | Single record by ID; increments usage count |
| `memorysmith_source_bundle` | `ids` or `query`/`tags`/`limit`, `maxFileBytes`, `format` | Records + resolved file content slices for every source link |
| `memorysmith_find_by_source` | `pattern` | Records whose source link URIs match the substring |

**`memorysmith_context_pack` tips:**
- Use `query` for open-ended discovery; use `ids` for anchoring to known records.
- `referenceDepth=1` follows one hop of `References` and `Conflicts` from root records.
- `includeBacklinks=true` adds records that reference the roots.
- `format=json` returns structured JSON for agent parsing; the default is Markdown prose.
- The tool reports warnings for missing roots, missing links, or records omitted after hitting `maxRecords`.

**`memorysmith_source_bundle` tips:**
- Combine with `memorysmith_context_pack` results: get the context pack first, then bundle the source for the most relevant records.
- `format=jsonl` returns one JSON object per line (streaming-friendly); `format=json` returns a single object with `memoryCount`, `sourceCount`, and `entries`.

## Configuration

All settings live under `MemorySmith` in `appsettings.json`:

```json
{
  "MemorySmith": {
    "DataPath": "../Data/Memories",
    "EventLogPath": "../Data/Events/audit.log",
    "VarsPath": "../Data/vars.json",
    "ApiKey": null,
    "AllowRemoteApi": false,
    "Maintenance": {
      "Enabled": true,
      "TriageMinutes": 5,
      "IndexingMinutes": 60,
      "ConsolidationHours": 24,
      "StartupGraceSeconds": 30
    },
    "SourceLinks": {
      "MaxReadBytes": 65536,
      "AllowedFileRootVariables": [ "MemorySmithRepo" ],
      "AllowedFileRoots": []
    }
  }
}
```

Override via `appsettings.Development.json` or environment variables (`MemorySmith__DataPath`, etc.).

- **`ApiKey`** — if set, all API and MCP requests must include `X-Api-Key: <value>`. Leave `null` for local use.
- **`AllowRemoteApi`** — set `true` to allow non-localhost callers. Off by default.
- **`DataPath`** — root of the memory store. Subdirectories (`Unconsolidated/`, `Working/`, `Core/`, `Deprecated/`) are created automatically.
- **`VarsPath`** — path to the flat JSON dict used for `%VarName%` source link expansion.
- **`SourceLinks:MaxReadBytes`** — maximum local file content returned per source-link entry by MCP source bundle reads.
- **`SourceLinks:AllowedFileRootVariables`** — variable names whose resolved values are trusted roots for local source-link file reads. Defaults to `MemorySmithRepo`.
- **`SourceLinks:AllowedFileRoots`** — optional explicit local roots, useful when source links need access outside the repo wiki root.

## Windows Service

Publish the app, then from an elevated PowerShell session:

```powershell
# Install
.\MemorySmith.App.exe --install-service --service-name MemorySmith --service-display-name "MemorySmith" -- --urls http://localhost:5089

# Uninstall
.\MemorySmith.App.exe --uninstall-service --service-name MemorySmith
```

Optional install flags: `--service-description`, `--service-start-type` (`auto`, `demand`, `disabled`). Arguments after `--` are passed as runtime args to the service process.

## Validate

```powershell
dotnet build MemorySmith.slnx -v minimal
dotnet test MemorySmith.slnx -v minimal
```

Run BenchmarkDotNet search benchmarks:

```powershell
dotnet run -c Release --project MemorySmith.Benchmarks -- --smoke
dotnet run -c Release --project MemorySmith.Benchmarks -- --filter *SearchBenchmarks*
```

The solution builds `MemorySmith.App` as the single deployable host. `MemorySmith.Tests` includes unit tests, integration tests (via `WebApplicationFactory`), and a `[Category("Benchmark")]` suite of 23 search quality probes with latency thresholds. Older `Worker` and `Dashboard` projects are retained as migration history and are not in the active solution.