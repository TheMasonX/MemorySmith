# MemorySmith - Audit #6 Validation Notes

This page records a direct validation pass against the live repository for the claims in `audits/vector-search-audit-6`.

## Summary

The audit is useful, but it mixes live defects with several stale statements. The current repo still has real code-search storage and chat tool-call robustness gaps, but at least one of the report's headline medium findings has already been fixed: hybrid-ranking weights are operator-configurable and admin-exposed now, not hardcoded-only constants.

## Confirmed From Code

- `MemorySmith.App/Services/CodeSearchService.cs` still persists chunk embeddings in `EmbeddingJson TEXT`, deserializes JSON into fresh `float[]` instances, and keeps the code-search SQLite connection string on `Pooling = false`.
- `MemorySmith.App/Services/CodeSearchService.cs` still creates the code-search schema without applying WAL or other reviewed performance PRAGMAs in the same path.
- `MemorySmith.App/Services/CodeSearchService.cs` still uses the manual scalar `Dot` loop in the hot scoring path.
- `MemorySmith.App/Services/CodeSearchService.cs` still contains the misindented lexical-tail `CacheResults` / `return CompleteSearch("lexical", ...)` block.
- `MemorySmith.App/Services/CodeSearchService.cs` still recomputes the `DocumentPath + "\n" + SearchText` haystack separately in `ScoreLexical` and `CountMatchedTokens`.
- `MemorySmith.App/Services/CodeSearchService.cs` still drops one-character tokens in `AddTokenVariants`, which is a real code-search tradeoff for generic type parameters and loop variables.
- `MemorySmith.App/Services/CodeSearchService.cs` still writes query-result cache entries without an expiry policy.
- `MemorySmith.App/Services/ChatServices.cs` still buffers overly broad potential tool-call prefixes, swallows tool-call parse failures, strips fenced JSON with a brittle last-fence heuristic, and retains dead helper methods that no longer appear to own active behavior.
- `MemorySmith.App/Components/Pages/Chat.razor` still persists the full chat session state every two seconds during active streaming.

## Corrected Or Stale Audit Claims

- Hybrid code-search ranking weights are already configurable through `MemorySmith.App/Services/MemorySmithOptions.cs`, `MemorySmith.App/appsettings.json`, and `MemorySmith.App/Services/AdminSettingsService.cs`.
- The ranking knobs are already admin-editable and guarded by validation that rejects invalid weight combinations.
- The current code-search test suite already contains the named relevance and diversification tests cited by the audit, so any follow-up tuning task should start from those current guards instead of assuming the values are unowned constants.
- `MemorySmith.Tests/CodeSearchServiceTests.cs` currently includes `SearchAsync_DefaultExcludePatternsSkipProjectDocsNoise`; that test needs interpretation care, but it is not evidence by itself that the repo forgot to test docs noise at all.

## Task Mapping

- Existing: `TSK-0198` for candidate-prefilter work.
- Existing: `TSK-0199` for semantic-search ANN planning.
- New: `TSK-0211` for validated code-search SQLite/storage/hot-path hardening.
- New: `TSK-0212` for validated chat tool-call parse/buffer fallback hardening.

## Notes

- I did not promote every audit suggestion into a new task. Items like a streaming HTTP chat endpoint or score-gap-aware result diversification may still be worthwhile, but they were treated as follow-up product choices rather than immediate validated defects from this pass.
- The tracker for this validation run is `logs/agent-smith-20260528-vector-search-audit-6-validation.md`.