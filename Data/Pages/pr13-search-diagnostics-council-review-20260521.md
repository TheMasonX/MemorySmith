# Council Review: PR 13 Search Diagnostics Follow-Up

## Decision

Proceed with a narrow PR 13 follow-up that preserves the no-ranking-change design constraint while centralizing repeated context-pack formatting and ensuring search diagnostics are enriched only for returned results, not every candidate.

## Evidence Reviewed

- PR #13 comment from TheMasonX: search is critical; optimize speed, accuracy, signal-to-noise ratio, token bloat, tests, benchmarking, and centralization.
- PR #13 Copilot review comments on lexical diagnostics N+1, semantic linear scans, context-pack warning noise, maintenance event spam, duplicated deprecation thresholds, status-like blocked tags, and Markdown formatter/doc drift.
- Follow-up commit `52476a4 Centralize search diagnostics enrichment`.
- [AI Memory Suite Implementation Plan](ai-memory-suite-implementation-plan.md), especially Phase 1 and Phase 3 retrieval output safety gates.
- [Memory Governance Guide](memory-governance-guide.md), especially diagnostics-as-metadata and no-ranking-change constraints.
- MemorySmith context pack records: `project-wiki-search-roadmap`, `project-wiki-mcp-context-pack`, `project-wiki-mcp-search-tools-current`, and `ai-memory-suite-governance-foundation-20260520`.
- Source evidence: [MemoryApplicationService.cs](../../MemorySmith.App/Services/MemoryApplicationService.cs), [MemoryDiagnosticFormatting.cs](../../MemorySmith.App/Services/MemoryDiagnosticFormatting.cs), [ChatToolCatalog.cs](../../MemorySmith.App/Services/ChatToolCatalog.cs), [McpController.cs](../../MemorySmith.App/Controllers/McpController.cs), [SearchBenchmarkTests.cs](../../MemorySmith.Tests/SearchBenchmarkTests.cs), [SearchBenchmarks.cs](../../MemorySmith.Benchmarks/SearchBenchmarks.cs).
- Validation evidence before this follow-up: `dotnet build MemorySmith.slnx -v minimal` passed; PR #13 earlier validation reached 220/220 tests after the page-asset repair.

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
| --- | --- | ---: | --- |
| Source-Grounded Archivist | Accept the follow-up only if it remains source-grounded in current PR comments and does not broaden into ranking changes. | 0.88 | Any claim that search quality improved must be backed by tests/benchmarks, not prose. |
| Data Model Architect | Keep diagnostics as DTO metadata and do not promote new schema fields in this PR. | 0.86 | Schema promotion before Phase 5 would violate the plan and create migration cost. |
| Retrieval Specialist | Patch the remaining hot path: hybrid search should sort candidates first, apply the requested limit, then attach diagnostics only to returned results. | 0.90 | Enriching diagnostics for every hybrid candidate wastes time and can touch source files unnecessarily. |
| Human Learning Advocate | Keep warnings visible but concise; warning/error-only summaries and capped Markdown diagnostics are the right token budget tradeoff. | 0.84 | Too many diagnostics in context packs will reduce usefulness even when technically correct. |
| Skeptical Reviewer | Do not mark the PR comment fully satisfied until duplicated context-pack formatters are centralized or deliberately documented as separate surfaces. | 0.82 | Repeated formatter drift already caused contract problems and contradicts the user's centralization request. |
| Synthesizer | Make two changes now: limited diagnostics enrichment and shared context-pack formatting. Defer ranking/page embeddings/vector-index changes to measured phases. | 0.87 | CI, search probes, and benchmark smoke must pass before pushing. |

## Synthesis

Changes now:

- Preserve current lexical, semantic, hybrid, and context-pack ranking semantics.
- Attach diagnostics after search limits are applied where possible, especially hybrid search.
- Reuse one context-pack formatter across direct MCP and chat tool catalog paths, allowing direct MCP to resolve source-link URIs while sharing projection and Markdown structure.
- Keep warning summaries warning/error-only and capped.

Deferred:

- Ranking weight changes, RRF changes, temporal decay, page chunking, page embeddings, durable vector indexes, schema promotion, and broader MCP envelope changes.
- UI redesign for diagnostics beyond the current chips/panel.

## Dissent

The Retrieval Specialist would prefer broader ranking quality probes immediately because search quality is the product's core value. The Synthesizer defers ranking changes because the active design explicitly says ranking remains unchanged until probes prove a change is helpful. The compromise is to improve search infrastructure and benchmark coverage now without changing relevance math.

## Acceptance Criteria

- Direct `/mcp` context-pack output and chat catalog context-pack output use a shared formatter implementation.
- Direct `/mcp` JSON context-pack output still resolves source-link URIs through `VarResolver`; chat catalog output preserves raw source-link URIs.
- Hybrid search applies the requested limit before diagnostics enrichment.
- Existing search relevance probes still pass.
- Full `MemorySmith.Tests` suite passes.
- Benchmark smoke command passes: `dotnet run -c Release --project MemorySmith.Benchmarks -- --smoke`.
- No ranking formula or persisted memory schema changes are introduced.

## Post-Implementation Checkpoints

Stage 1, search and formatter plumbing: implemented in `591f2bb Tighten search diagnostics follow-up`. The follow-up added a shared `MemoryContextPackFormatter`, routed direct MCP and chat context-pack output through it, and applied hybrid search limits before diagnostics enrichment. Validation passed with `dotnet build MemorySmith.slnx -v minimal`, affected tests at 80/80, full tests at 225/225, and benchmark smoke returning results for lexical metadata diagnostics, semantic, hybrid, chat-context, and context-pack paths. Council conclusion: acceptance criteria met without ranking or schema changes. Confidence: 0.91.

Stage 2, PR review closure: current source evidence shows lexical N+1 diagnostics, semantic linear-scan enrichment, warning bloat, deprecation event spam, duplicated deprecation thresholds, and Markdown formatter drift have been addressed. The only remaining source-grounded cleanup was removing blocklisted status-like `working` tags from memory records because `Status` already carries that state. This is a policy-conformance edit, not a new retrieval or schema decision, so a fresh full council was not required. Validation gate: no `"working"` tags remain under `Data/Memories/**/*.json`, and focused governance/search tests pass. Confidence: 0.89.

## Open Questions

- Should future search quality gates compute MRR/NDCG over the project wiki rather than only top-hit and must-contain probes?
- Should context-pack warning caps become configurable after larger wiki measurements?
- Should `SearchMetadataAsync` replace lexical `SearchAsync` for API/UI callers in a later contract cleanup?
