# Council Review: Phase 3 Retrieval Warning Propagation

## Decision

Ship Phase 3 as a warning/provenance propagation layer that adds diagnostics and provider metadata to retrieval outputs without changing ranking, RRF fusion, temporal decay, persisted schemas, or agent-write behavior.

## Evidence Reviewed

- `Data/Pages/council/application-next-steps-council-review-20260521.md` Phase 3 scope and acceptance gates.
- `MemorySmith.App/Services/MemoryApplicationService.cs` for lexical diagnostic search and retrieval envelope construction.
- `MemorySmith.App/Services/SemanticEmbeddingSearchService.cs` for semantic provider metadata.
- `MemorySmith.App/Services/RetrievalEnvelopes.cs` for versioned retrieval envelope records.
- `MemorySmith.App/Controllers/MemoriesController.cs`, `PagesController.cs`, `SearchController.cs`, and `McpController.cs` for API and MCP retrieval surfaces.
- `MemorySmith.App/Services/ChatToolCatalog.cs` and `MemorySmith.App/Components/Pages/Chat.razor` for chat/tool/reference warning propagation.
- `MemorySmith.Tests/MemoryApplicationServiceTests.cs`, `AppApiContractTests.cs`, `ChatToolCatalogAndInterceptTests.cs`, and `McpAndSemanticSearchTests.cs` for focused coverage.
- Validation: `dotnet build MemorySmith.slnx -v minimal`, full NUnit suite 245/245, focused MCP suite 13/13 after final MCP test cleanup, benchmark smoke across lexical, lexical diagnostics, semantic, hybrid, chat context, and context pack paths.

## Findings

| Seat | Recommendation | Confidence | Blocking Concern |
|---|---|---:|---|
| Source-Grounded Archivist | Approve with documentation because diagnostics now travel with retrieval records and structured envelopes preserve source evidence for agents. | 0.92 | None blocking; keep envelope schema version explicit. |
| Data Model Architect | Approve because the retrieval envelope is additive and not a persisted memory schema migration. | 0.90 | Default unified search response gains provider/diagnostic fields; this is additive but should stay documented. |
| Retrieval Specialist | Approve because rank calculation paths remain unchanged while metadata exposes lexical, ONNX, token fallback, and hybrid provenance. | 0.88 | Watch envelope size and duplicate diagnostic text in future phases. |
| Human Learning Advocate | Approve because chat reference warnings make stale/source/tag/relation problems visible at the point of use. | 0.87 | UI only surfaces compact chips; detailed remediation still depends on memory editor/governance pages. |
| Skeptical Reviewer | Approve after validation because MCP and chat tool structured JSON are opt-in and Markdown defaults remain compatible. | 0.84 | Future clients may rely on exact unified-search JSON shape; additive defaults need release-note visibility. |
| Synthesizer | Ship Phase 3 and move Phase 4 to context-planner/tool-registry work. | 0.89 | None blocking after local validation. |

## Synthesis

Phase 3 should merge as an additive retrieval transparency release. The implementation keeps current human-readable Markdown defaults for existing tool callers while allowing agents to opt into `memorysmith.retrieval-results.v1` envelopes. Memory and page APIs expose opt-in envelopes through `format=envelope`/`format=json-v2`, and MCP/chat tools expose structured JSON through `format=json`, `format=envelope`, or `format=json-v2`. Unified search now returns diagnostic and provider metadata directly because it is already a composite API surface.

Deferred to later phases: ranking quality changes, temporal scoring, RRF tuning, persisted schema changes, native provider tool-call registration, and agent write governance.

## Dissent

The Skeptical Reviewer would prefer all default API response additions to be behind format flags. The counterargument is that unified search already returns mixed result objects, so additive `diagnostics` and `provider` fields improve provenance without breaking existing JSON property readers. The risk is accepted with documentation and tests.

## Acceptance Criteria

- A warning-bearing record remains retrievable and the result includes diagnostics.
- Lexical, semantic, hybrid, unified, page, context-pack, chat-tool, and MCP retrieval surfaces either expose diagnostics directly or through an opt-in structured envelope.
- Existing Markdown tool output remains parseable for current clients unless a structured format is requested.
- Semantic provider metadata reports ONNX embedding versus token fallback status without requiring ONNX to be available.
- Search relevance and benchmark smoke paths continue to pass without ranking, RRF, temporal decay, or persisted schema changes.

## Open Questions

- Should Phase 4 extract the duplicated chat/MCP tool formatting into a single shared registry before adding native provider tools? Current confidence: 86% yes.
- Should structured envelope warning summaries be capped or paged for very large result sets? Current confidence: 78% yes, but Phase 3 did not need a cap beyond existing result limits.
- Should unified search gain an opt-in legacy response mode before external clients depend on exact field sets? Current confidence: 64%; no evidence of strict external clients yet.
