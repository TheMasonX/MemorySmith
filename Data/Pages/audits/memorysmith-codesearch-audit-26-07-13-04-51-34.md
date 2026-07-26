# MemorySmith Audit — CodeSearchService.cs Deep Dive (New Findings)
**File reviewed in full:** `MemorySmith.App/Services/CodeSearchService.cs`, 3,116 lines, commit `e8a3065` (unchanged from Delta Report 4 — re-verified branch HEAD before this pass).
**Report generated:** 2026-07-11
**Coverage:** every method in this file read in full (`SearchAsync`, `GetStatusAsync`, `EnsureIndexedAsync`/`BuildIndexCoreAsync` and its ~950-line build loop, the resumable-build log helpers, `MergeShardAsync` and its shard-load/insert/update helpers, `LoadVectorCandidatesAsync` and the SQL it constructs, `EnsureDatabaseAsync`/`EnsureColumnAsync`/`HasColumnAsync`), plus the file's cache/invalidation fields traced end-to-end and its one call into `ChatToolCatalog.cs` cross-referenced to check a caller-side security claim made in an inline comment.
**Notable context discovered mid-review:** a comment at line 1704-1707 references "Audit #7 finding 2.4 / CS-01/CS-02" — this codebase has an existing internal audit numbering scheme separate from this engagement's reports, and at least one of its findings (shard-path validation) is independently confirmed fixed at the tool layer (TSK-0234, Done). This report's findings are new relative to both that prior audit history and this engagement's Reports 1–4/Deltas 1–4.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F23 | `QuerySynonyms` hardcodes five literal hand-tool words (`screwdriver`, `hammer`, `wrench`, `pliers`, plus `tool`/`tools`/`tooling`/`utility`/`utilities`) with no doc comment, no config surface, and no evident connection to this project's actual domain (a C#/.NET knowledge-base and code-search tool, not anything hardware- or tool-shop-related) | 75% | Low (arbitrary/unexplained, not incorrect) | **New** |
| F24 | Two incompatible schema-evolution philosophies coexist in the same codebase: `SqliteMemorySmithDatabase` uses a tracked, recorded migration list; `CodeSearchService` uses untracked, ad-hoc `HasColumnAsync`/`EnsureColumnAsync` checks with no version record at all | 90% | Medium (consolidation opportunity, "legacy pattern" duplication) | **New** |
| F25 | The vector-candidate scoring lambda (~12 lines: dot product, matched-token count, lexical score, target weight, hybrid score, coverage weight) is duplicated verbatim across the primary vector path and the sparse-prefilter-fallback path in `SearchAsync`; the `ScoredChunk`→`CodeSearchResult` projection is duplicated a third time for the lexical-only path | 90% | Low-Medium (DRY violation in the hottest, most safety-critical scoring code in the file) | **New** |
| F26 | `WarmMetadataReuseEnabled` (default `true`, undocumented) lets the indexer skip re-reading/re-hashing a file based on size+mtime alone; this is a well-known class of false-negative risk (unchanged size + coarse/rounded mtime on some filesystems can mask a real content change), and it's on by default with zero doc-comment explaining the tradeoff | 65% | Low (documented risk class, not a demonstrated live bug) | **New**, minor |
| F27 | `CodeSearchService.MergeShardAsync` is a public method with no root-containment check of its own — the code's own comment admits the path-allowlist safety property is enforced only by one caller (`ChatToolCatalog`'s tool dispatch), not by the service method itself, so any other/future direct caller inherits an unguarded arbitrary-file-merge surface | 85% | Medium (shallow-module / defense-in-depth gap, already partially mitigated but by caller discipline, not by the method's own contract) | **Extension to the already-Done TSK-0234** — recommends closing the gap TSK-0234 explicitly left open, not re-opening it |

---

## F23 — Unexplained hand-tool synonyms in the query-expansion table (Low, 75%)

**File:** `CodeSearchService.cs`, lines 136-147:
```csharp
private static readonly Dictionary<string, string[]> QuerySynonyms = new(StringComparer.OrdinalIgnoreCase)
{
    ["tool"] = ["tools", "tooling", "utility", "utilities", "harness", "script", "scripts", "cli"],
    ...
    ["screwdriver"] = ["tool", "tools", "tooling", "utility", "driver", "drivers"],
    ["hammer"] = ["tool", "tools", "tooling", "utility"],
    ["wrench"] = ["tool", "tools", "tooling", "utility"],
    ["pliers"] = ["tool", "tools", "tooling", "utility"]
};
```
The `tool`/`tools`/`tooling`/`utility`/`utilities` entries make obvious sense for a search index over a software project (matching "CLI tool," "harness script," etc.). The four physical hand-tool entries (`screwdriver`, `hammer`, `wrench`, `pliers`) don't fit that pattern — this is a knowledge-base/code-search tool for a C#/.NET project, not a hardware, IoT, or tool-shop domain, and no `.md`/wiki content or code comment in the repository explains why a search for "hammer" should be treated as synonymous with "utility." Grepped the rest of the codebase for a connection (e.g., a demo/test fixture about hand tools, or a metaphor used elsewhere in the docs) and found none.

**Most likely explanations, in order of likelihood:** (a) a leftover from an LLM-assisted synonym-generation pass that included plausible-sounding-but-irrelevant entries and nobody pruned them; (b) a deliberate but undocumented choice to help match queries against a very specific historical document that used a tool-shop metaphor (if so, this needs a comment saying so); (c) copy-paste residue from an unrelated project. Given this project's sibling repo (`MemorySmith.Agent`) has an actual Minecraft crafting-tool domain (per this audit engagement's earlier findings on `CraftAliases`, `"pickaxe"`/`"axe"` resolution), there's a plausible but unconfirmed chance this table was drafted with that sibling project's domain in mind and pasted into the wrong repo.

**Recommendation:** low priority, but cheap to resolve — either add a comment explaining the intent, or remove the four hand-tool entries if (as suspected) they don't correspond to anything actually searched for in this codebase. If keeping any hardcoded synonym table long-term, consider moving it to a config file (`_options.CodeSearch` already has many tunables) so it's editable without a recompile — the current all-or-nothing hardcoded table means adding or removing a domain-specific synonym requires a code change and deploy.

---

## F24 — Two incompatible schema-evolution philosophies in the same codebase (Medium, 90%)

**Contrast:**
- `SqliteMemorySmithDatabase.cs` (examined in Delta Report 2, F13): an explicit `MigrationsLazy` list of `(MigrationId, SchemaSql, SeedSql)` tuples, applied via `ApplyPendingMigrationsAsync`, tracked in a `SchemaMigrations` table so the system always knows exactly which migrations have run against a given database file.
- `CodeSearchService.cs`, `EnsureDatabaseAsync` (lines 2111-2156): a single `CREATE TABLE IF NOT EXISTS` block for the base schema, followed by two hardcoded calls —
  ```csharp
  await EnsureColumnAsync(connection, "SourceLengthBytes", "INTEGER NULL", cancellationToken);
  await EnsureColumnAsync(connection, "SourceLastWriteUtc", "TEXT NULL", cancellationToken);
  ```
  — where `EnsureColumnAsync` (lines 2896-2906) checks `PRAGMA table_info` for the column's existence and runs a bare `ALTER TABLE ... ADD COLUMN` if missing. **There is no tracking table, no migration ID, and no ordering guarantee beyond "whichever `EnsureColumnAsync` calls appear first in the C# source."**

**Why this matters beyond style:** the tracked-migration approach gives you, for free, (a) a queryable record of exactly what schema state any given database file is in, (b) a natural hook for a data-backfill step (`SeedSql`) alongside a structural change, and (c) protection against accidentally re-running a destructive one-time migration twice. The ad-hoc `EnsureColumnAsync` approach has none of these — it happens to be safe *today* because both existing calls only add nullable columns (which is inherently idempotent and needs no backfill), but the pattern has no way to express "add this column AND backfill it from existing data" the way the other database's `SeedSql` mechanism can. A future engineer adding a code-search schema change that *does* need a backfill (e.g., a new non-nullable column, or a computed column derived from existing rows) has no established pattern to reach for in this file and would likely either invent a third approach or force a `NULL`-then-fixup pattern that the tracked-migrations system already solves cleanly.

**Recommendation:** don't rush to unify these into one shared component under schedule pressure — that's a bigger, riskier change than either database currently needs. Instead: (a) as a near-term step, add a one-paragraph comment above `EnsureDatabaseAsync` explaining this is a deliberately lighter-weight pattern for this specific database (append-only, no backfill needs so far) and pointing at `SqliteMemorySmithDatabase`'s approach as the pattern to switch to if a future change needs backfill semantics; (b) if/when `SqliteMemorySmithDatabase`'s planned decomposition (TSK-3081, already in this remediation plan's W5) extracts shared connection/command helpers into their own class, consider whether a shared, lightweight `IMigrationRunner` abstraction belongs there too, usable by both databases — but only take that step if a second code-search schema change actually comes up needing backfill, rather than speculatively generalizing now.

---

## F25 — Duplicated scoring/projection logic in the hottest code path (Low-Medium, 90%)

**File:** `CodeSearchService.cs`, `SearchAsync`, lines 296-386. The exact same 10-line scoring lambda appears twice:
```csharp
.Select(chunk =>
{
    var rawScore = Dot(queryEmbedding, chunk.Embedding);
    var matchedTokenCount = CountMatchedTokens(chunk, queryTokens);
    var lexicalScore = ScoreLexical(chunk, expandedQueryTokens);
    var targetWeight = GetTargetWeight(chunk.Target, chunk.DocumentPath, queryTokens);
    var hybridScore = ScoreHybrid(rawScore, lexicalScore);
    var coverageWeight = ScoreTokenCoverageWeight(matchedTokenCount, queryTokens.Count);
    return new ScoredChunk(chunk, rawScore, lexicalScore, targetWeight, coverageWeight, hybridScore * targetWeight * coverageWeight, matchedTokenCount);
})
```
— once at lines 300-309 (primary vector path over `vectorCandidates.Chunks`), and identically at lines 342-351 (sparse-prefilter-fallback path over the full `chunks` load). The subsequent projection from `ScoredChunk` into `CodeSearchResult` (rounding the score, building a snippet, building a match-reason string) is written out a third time for the lexical-only path (lines 394-407), with only the match-reason builder function differing (`BuildVectorMatchReason` vs `BuildLexicalMatchReason`).

**Why this matters more than typical duplication:** this is the actual ranking math that determines what search results a user sees — F10 in an earlier report already found one bug in this class of code (`MemoryScorer`'s unbounded reference-count term), and the general lesson from that finding applies here too: formula code that's copy-pasted is formula code that can silently drift out of sync. If someone fixes a bug in the vector-path scoring lambda (say, clamping `rawScore` for numerical stability) and forgets the fallback-path copy exists, the fallback path keeps the bug. There's no compiler warning for "these two blocks used to be identical and now aren't."

**Recommendation:**
```csharp
private List<ScoredChunk> ScoreChunks(IEnumerable<IndexedChunk> chunks, float[]? queryEmbedding, List<string> queryTokens, List<string> expandedQueryTokens, bool lexicalOnly)
{
    return chunks
        .Where(chunk => lexicalOnly || chunk.Embedding.Length == queryEmbedding!.Length)
        .Select(chunk =>
        {
            var rawScore = lexicalOnly ? 0.0 : Dot(queryEmbedding!, chunk.Embedding);
            var lexicalScore = ScoreLexical(chunk, expandedQueryTokens);
            var matchedTokenCount = CountMatchedTokens(chunk, queryTokens);
            var targetWeight = GetTargetWeight(chunk.Target, chunk.DocumentPath, queryTokens);
            var coverageWeight = ScoreTokenCoverageWeight(matchedTokenCount, queryTokens.Count);
            var hybridScore = lexicalOnly ? lexicalScore : ScoreHybrid(rawScore, lexicalScore);
            return new ScoredChunk(chunk, rawScore, lexicalScore, targetWeight, coverageWeight, hybridScore * targetWeight * coverageWeight, matchedTokenCount);
        })
        .Where(entry => entry.WeightedScore > 0)
        .OrderByDescending(entry => entry.WeightedScore)
        .ThenBy(entry => entry.Chunk.DocumentPath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Chunk.StartLine)
        .ToList();
}
```
and a second small helper for the `ScoredChunk → CodeSearchResult` projection taking the match-reason-builder as a `Func<ScoredChunk, string>`. This collapses three call sites into one scoring function and one projection function, called three times with different inputs/flags instead of hand-copied three times. **Test strategy:** the existing search-quality tests (`SemanticToolQualityTests.cs`, and any `CodeSearchService`-specific test file if one exists — not independently confirmed in this pass) should pass unchanged if this is a pure refactor; treat any score/ranking difference after the change as a real bug surfaced by the refactor, not an acceptable side effect, and diff the two original lambdas character-by-character before deleting either one to make sure they really were identical (I traced them by eye as identical in this pass, but a mechanical diff is cheap insurance before deleting security/ranking-adjacent code).

**Effort estimate:** 3-4 hours including the diff-verification step above and updating/adding tests. Low risk if the pre-refactor diff check passes.

---

## F26 — `WarmMetadataReuseEnabled`'s staleness heuristic is undocumented and on by default (Low, 65%)

**File:** `CodeSearchService.cs`, `CanWarmReuseByMetadata` (lines 1488-1505), gated by `MemorySmithOptions.CodeSearch.WarmMetadataReuseEnabled` (`MemorySmithOptions.cs` line 245, default `true`, **no doc comment**).

This is a legitimate two-tier reuse strategy, not a bug — `CanWarmReuseByMetadata` is a cheap pre-check (file size + mtime match) that avoids reading and hashing a file at all, distinct from the slower-but-robust `CanReuseDocument` (actual content-hash comparison) that runs when the fast check doesn't apply. The concern is narrower: size+mtime staleness detection is a well-known heuristic with a real, if narrow, false-negative failure mode — a file rewritten with the exact same byte count within the same filesystem timestamp-resolution window (common on some network filesystems, some container overlay filesystems, or after certain git/deploy operations that don't preserve sub-second mtime precision) can be silently treated as unchanged, meaning the index would keep serving stale content for that file until the next `ForceRebuild` or a change that alters the file's size. This is turned **on by default** with no comment anywhere near the option's declaration explaining the tradeoff to whoever might toggle it.

**Recommendation:** add a doc comment on `WarmMetadataReuseEnabled` stating the tradeoff plainly (fast, but can miss same-size/same-mtime-window content changes on some filesystems; disable if running the index over a filesystem with coarse mtime resolution or if absolute freshness matters more than incremental-build speed). This is a documentation-only fix — I'm not recommending a behavior change, since disabling this by default would meaningfully slow down incremental rebuilds for the common case where it's safe, and the risk window is genuinely narrow. Confidence is 65%, not higher, specifically because I have not confirmed this has caused an actual observed staleness incident in this project — this is a known-risk-class flag, not a demonstrated bug.

---

## F27 — `MergeShardAsync`'s path safety depends entirely on caller discipline (Medium, 85%)

**File:** `CodeSearchService.cs`, lines 1702-1710, with the method's own comment stating the constraint explicitly:
```csharp
// Service-layer path guard: validate before any filesystem I/O.
// The tool layer (ChatToolCatalog.MergeShardAllowedExtensions + IsPathWithinAnyRoot)
// enforces root-allowlist checks for the MCP/chat code path, but direct callers of
// this service method have no such protection. Audit finding: CS-01/CS-02 (Audit #7).
```

**Verified both halves of this claim directly:**
1. `ChatToolCatalog.cs` line 496 does call `IsPathWithinAnyRoot(canonicalShardPath, allowedRoots)` before invoking `ctx.CodeSearch.MergeShardAsync(...)` at line 502 — the tool-dispatch path genuinely is guarded, and `TSK-0234` ("validate merge shard tool paths against allowed roots and sqlite extensions," status **Done**) correctly reflects that this got fixed.
2. `CodeSearchService.MergeShardAsync` itself only validates (a) the path isn't empty, (b) the extension is `.db`/`.sqlite`/`.sqlite3`, (c) the file exists (lines 1708-1727) — **no root-containment check appears anywhere in the method**, confirming the comment's own admission.

**Why this is worth re-raising even though TSK-0234 is closed:** TSK-0234's description ("Vetted from Audit #7 finding 2.4... Default-off gating exists, but shardPath validation remains missing when enabled") scoped the fix to "merge-shard inputs" generically and the team correctly implemented it — at the tool layer, which was almost certainly the fastest and lowest-risk place to land it given the timeline. But the method's own comment already flags this as an incomplete fix from a defense-in-depth standpoint, not a closed loop: `MergeShardAsync` is a `public` method on a service with no other access modifier restricting it, and its signature — `MergeShardAsync(string shardDatabasePath, bool preferNewer, CancellationToken)` — gives no compile-time or runtime signal that the caller must have already validated the path against an allowlist. This is a textbook **shallow module**: the interface's apparent contract ("merge this shard file") is narrower than its actual safety contract ("merge this shard file, which you must have already checked is under an allowed root, using a check that lives in a different class"), and nothing enforces the gap between the two. Any future caller — a new admin endpoint, a scheduled maintenance job, a different MCP tool, a test harness that accidentally gets exposed — inherits an arbitrary-file-read-into-index primitive with zero built-in protection, and would only discover the missing constraint by reading this specific comment, if they read it at all.

**Recommendation:** thread the same `allowedRoots` concept the tool layer already computes into the service method itself, so the safety property holds by construction rather than by caller diligence:
```csharp
public async Task<CodeSearchShardMergeResult> MergeShardAsync(
    string shardDatabasePath,
    bool preferNewer,
    IReadOnlyList<string>? allowedRoots,   // null = no restriction (preserve today's behavior for existing internal callers if truly needed), non-null = enforce
    CancellationToken cancellationToken)
```
or, more in keeping with this project's `VarResolver`-style pattern from the earlier reports, resolve a `CodeSearch.AllowedShardMergeRoots` config list inside `CodeSearchService` itself (mirroring `SourceLinks.AllowedFileRoots`) and enforce it unconditionally inside `MergeShardAsync`, with `ChatToolCatalog`'s existing check becoming a legitimate (if now redundant) belt-and-suspenders layer rather than the *only* layer. The second option is preferable — it doesn't require every call site to remember to pass the right roots — and is a small, additive change given `VarResolver`'s root-resolution logic already exists as a pattern to follow (don't duplicate it — extract the shared `PathSecurity.IsUnderRoot` helper from this engagement's remediation plan (W1.1) first, and have this new check call that).
**Effort estimate:** 3-4 hours (config option + enforcement + a test that a shard path outside the configured roots is rejected even when called directly, bypassing `ChatToolCatalog` entirely — that test is the actual proof this closes the gap).

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- F23's "most likely explanation" for the hand-tool synonyms is explicitly speculative (labeled as such) — I did not find definitive evidence for why those four entries exist, only ruled out the explanations I could check (no matching domain content elsewhere in the repo).
- F25's recommended refactor assumes the two duplicated lambdas are behaviorally identical today — I traced them by eye as identical, but flagged in the recommendation itself that a mechanical diff should precede deletion, since eye-tracing two 10-line blocks isn't the same rigor as this engagement gave the shorter, more consequential snippets in earlier reports (e.g., the `MemoryStateMachine` trace in F17).
- F27 assumes there are no other internal callers of `MergeShardAsync` beyond `ChatToolCatalog` today (confirmed via repo-wide grep) — if a new caller has been added since this pass, re-verify it also performs the root check before relying on this report's risk assessment.
- Did not review `TreeSitterChunkingService.cs` (the chunking dependency injected into this class) or `ParsedChunk.cs` in this pass — `CodeSearchService.cs` itself is now fully covered, but its immediate collaborators are not, consistent with this engagement's stated scope-per-pass approach.
