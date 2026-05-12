# Detailed Review: MCP Search And Wiki Testbase

Date: 2026-05-12

## Scope

This review covers the current MemorySmith search/MCP surface, the repository wiki as a testbase, and near-term improvements that make the tool more accurate and easier for agents to use on larger knowledge bases.

## Evidence-Based Findings

| Finding | Evidence | Confidence |
|---|---|---:|
| The active app is now centered on `MemorySmith.App` as the single host for UI, REST, MCP, storage, and maintenance. | `README.md`, `MemorySmith.App/Program.cs`, `MemorySmith.App/Controllers/McpController.cs` | 0.96 |
| `memorysmith_context_pack` is the most useful MCP workflow for agents because it combines discovery roots, explicit ids, graph expansion, warnings, output budgets, and optional structured JSON. | `MemorySmith.App/Controllers/McpController.cs`, `MemorySmith.App/Services/MemoryApplicationService.cs`, `MemorySmith.Tests/McpAndSemanticSearchTests.cs` | 0.95 |
| The project wiki is a real testbase, but before this review it mostly contained product documentation records rather than deterministic graph/search fixtures. Tests could pass or fail based on wording drift in docs. | `Data/Memories/Core/*.json`, `MemorySmith.Tests/ProjectWikiTestbaseTests.cs` | 0.93 |
| The local semantic path is still token/tag/title/reference/alias scoring and not embeddings. Hybrid search currently improves practical retrieval by fusing that signal with Lucene.NET lexical rank through RRF. | `MemorySmith.App/Services/MemoryApplicationService.cs`, `Data/Memories/Core/project-wiki-semantic-search-gap.json`, `Data/Memories/Core/project-wiki-hybrid-search-rrf.json` | 0.98 |
| `Schemas/memory.schema.json` had drifted from the actual file-backed JSON shape: live records use PascalCase properties and numeric `MemoryStatus` enum values. | `Schemas/memory.schema.json`, `Data/Memories/Core/*.json`, `MemorySmith.Core/Models/MemoryRecord.cs`, `MemorySmith.Core/Models/MemoryStatus.cs` | 0.94 |

## Changes Made From This Review

- Added five deterministic wiki records under `Data/Memories/Core` with the `test-fixture` tag:
  - `project-wiki-test-fixture-overview`
  - `project-wiki-test-fixture-context-root`
  - `project-wiki-test-fixture-reference-child`
  - `project-wiki-test-fixture-backlink-source`
  - `project-wiki-test-fixture-conflict-note`
- Added service-level testbase coverage in `MemorySmith.Tests/ProjectWikiTestbaseTests.cs` for deterministic hybrid search plus reference, conflict, and backlink relationships.
- Added MCP JSON coverage in `MemorySmith.Tests/McpAndSemanticSearchTests.cs` for packing the fixture graph through `format=json`.
- Updated `Schemas/memory.schema.json` to match the current PascalCase record shape and allow both numeric and named `MemoryStatus` values.
- Updated README and wiki memories so future agents can find the fixture set.

## Improvement Backlog

1. Add a dedicated graph-integrity MCP tool, such as `memorysmith_validate_graph`, that reports missing references, dangling conflicts, orphaned memories, and high-fanout records.
   Confidence: 0.88. This would turn warnings from individual context packs into a whole-KB maintenance workflow.

2. Add first-class JSON output to search tools, not only context packs.
   Confidence: 0.86. `memorysmith_get` already returns JSON and context packs now support `format=json`; structured search results would remove another parsing step for agents.

3. Add schema validation tests for `Data/Memories`.
   Confidence: 0.84. The schema drift found in this review should be prevented by tests once a lightweight JSON schema validator is chosen.

4. Add stable fixture records for lifecycle/status filtering, long-content truncation, and multi-tag intersection.
   Confidence: 0.82. The new graph fixture covers search and relationships; status/tag/content-boundary fixtures would make API tests more precise.

5. Make semantic scoring pluggable behind the current hybrid API shape.
   Confidence: 0.78. This preserves the practical MCP surface while allowing embeddings or another vector-capable index later.

## Assumptions

- The repository `Data/Memories` folder should remain both live project documentation and realistic test data.
- Purpose-built fixture records are acceptable when clearly tagged as `test-fixture` and documented as part of the project wiki.
- Numeric `MemoryStatus` values are the current persisted format because the file store uses default `System.Text.Json` enum serialization.

## Open Questions

- Should MemorySmith migrate persisted records to named status strings for readability, or keep numeric enum values for compatibility?
- Should all MCP tools support `format=json`, or only context-heavy tools where structured parsing has the highest value?
- Should graph integrity warnings be part of every search/context response, or kept in a separate validation tool to avoid noise?