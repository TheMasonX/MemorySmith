# MemorySmith Audit — Duplication Deep Dive (jscpd + Manual Semantic Verification)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` · **Commit:** `e8a3065` (unchanged from Delta Reports 3-6)
**Report generated:** 2026-07-13

## Methodology

**Tool used:** [jscpd](https://github.com/kucherenko/jscpd) (the Rust-rewritten CLI, v-current via `npm install -g jscpd`), an industry-standard token-based copy/paste detector, widely used in CI pipelines (SonarQube integrates it, GitLab's built-in duplication widget is jscpd-based). Ran against the full repository:
```
jscpd . --pattern "**/*.cs" --ignore "**/bin/**,**/obj/**,**/*.g.cs,**/*.Designer.cs,**/Migrations/**" \
  --min-lines 5 --min-tokens 40 --format csharp --no-gitignore --reporters json,console
```
**Result:** 163 files analyzed, 53,271 lines, 342,907 tokens, **479 clones found, 8.30% duplicated lines**. Filtered the JSON report to exclude `MemorySmith.Tests/` and `e2e/` (test-code repetition is a different risk profile than production duplication and was reviewed separately — see note at the end) — **156 production-code clone pairs remained**, sorted by size and manually triaged.

**On "Type 4" (semantic) duplication specifically:** jscpd, like all mainstream CPD tools (PMD/CPD, Simian, SonarQube's duplication engine), does token-sequence matching — it reliably finds Type-1 (exact), Type-2 (renamed identifiers/literals), and some Type-3 (near-miss, small edits) clones, but it cannot find Type-4 clones (functionally equivalent code with materially different structure/tokens) by design — that class of detection requires PDG-based tools like NiCad or Deckard, neither of which is installable in this sandbox (no Maven Central/Apache mirror access). **Workaround used:** every one of jscpd's reported pairs was manually read and compared side-by-side rather than trusted at face value — this both (a) caught and discarded at least one false positive (see note under F32) where token-similarity didn't reflect true semantic overlap, and (b) is the only realistic route to genuine Type-4 findings in this environment: reading the actual duplicated *concepts* jscpd's near-misses pointed at (e.g., "two different methods that both resolve a data-relative path") rather than only trusting exact line ranges. This report's findings are the result of that combined process, not raw tool output.

---

## Executive Summary

| # | Finding | Confidence | Severity | Instances | Relationship to existing tasks |
|---|---|---|---|---|---|
| F30 | 7-8 mutation methods in `TaskDomainService.cs` (`UpdateAsync`, `SetStatusAsync`, `AddCommentAsync`, `AddLinkedPageAsync`, `AddExternalLinkAsync`, `AddAttachmentAsync`, `RemoveAttachmentAsync`, ...) repeat an identical `ThrowIfCancellationRequested → lock(_gate) → FindByIdOrKey → null-check` preamble | 100% | Medium (Type-1 clone inside thread-safety-critical code) | 7-8x | **Complements TSK-0045** (TaskDomainService layering split, Backlog/High) — tactical, low-risk step that makes that refactor easier, doesn't duplicate its scope |
| F31 | 8 action methods in `TasksController.cs` repeat an identical `try { ... return updated is null ? NotFound() : Ok(updated); } catch (ArgumentException ex) { return BadRequest(ex.Message); }` wrapper | 100% | Low-Medium (Type-1 clone, textbook exception-filter candidate) | 8x | **New** |
| F32 | `ResolveDataDeploymentRoot`/`NormalizeDataRelativePath` (locates the app's `Data/` root and strips `Data/`-relative path prefixes) is implemented **verbatim in three separate classes across two files** — `SemanticEmbeddingSearchService`, `OnnxTextEmbeddingProvider` (same file, different class), and `CodeSearchService` (different file) | 100% | Medium (same architectural anti-pattern as F19: path-resolution logic reinvented per-class) | 3x | **New** — a shared `MemorySmithConfigurationPaths.cs` already exists as the natural home and doesn't yet have this logic |
| F33 | The batch chunk-insert SQL parameter-binding boilerplate (14 parameters, bind-and-loop) is duplicated between `CodeSearchService`'s main index-build path and its shard-merge path — **but the two copies have one real behavioral difference** (`INSERT` vs `INSERT OR IGNORE`) that a careless "just delete the duplicate" fix would silently erase | 95% | Low-Medium (duplication finding with a built-in caution) | 2x | **New** |

**Also checked and ruled out this pass:** `MemoryApplicationService.cs:1419-1443` was flagged by jscpd as a near-clone of `SemanticEmbeddingSearchService.cs:499-523` — read both directly; they're unrelated (`NormalizeSearchToken`/`BuildSnippet` string-processing helpers vs. embedding-provider code) that happened to token-match closely enough to cross jscpd's threshold. Noted here explicitly as a demonstrated false positive, both to be transparent about tool limitations and because it's a useful illustration of why every finding in this report was hand-verified rather than reported from raw tool output.

---

## F30 — `TaskDomainService.cs`: repeated lock+lookup preamble across 7-8 mutation methods

**Pattern, byte-identical across all instances** (`TaskDomainService.cs`, confirmed at lines 449-460 `UpdateAsync`, 495-506 `SetStatusAsync`, 525-536 `AddCommentAsync`, 558-569 `AddLinkedPageAsync`, 603-614 `AddExternalLinkAsync`, 642-653 `AddAttachmentAsync`, 679-690 `RemoveAttachmentAsync`, plus one more per the raw pattern-match count of 9 total hits against 8 method declarations found):
```csharp
public Task<TaskItem?> XAsync(string idOrKey, ..., CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    lock (_gate)
    {
        var item = FindByIdOrKey(idOrKey, cancellationToken);
        if (item is null)
        {
            return Task.FromResult<TaskItem?>(null);
        }
        // ...method-specific mutation logic...
    }
}
```
**Why this matters beyond line count:** this preamble is inside a `lock` block — it's the thread-safety-critical part of every one of these methods, not incidental boilerplate. A future change to the locking strategy (e.g., switching to a `ReaderWriterLockSlim` for read/write separation, adding a lock-acquisition timeout, or fixing a subtle ordering issue if `_gate` is ever taken alongside another lock elsewhere) requires touching 7-8 near-identical blocks correctly, every time, with no compiler help catching a missed one.

**Recommendation:**
```csharp
private Task<TaskItem?> WithLockedTask(string idOrKey, CancellationToken cancellationToken, Func<TaskItem, TaskItem?> mutate)
{
    cancellationToken.ThrowIfCancellationRequested();
    lock (_gate)
    {
        var item = FindByIdOrKey(idOrKey, cancellationToken);
        return Task.FromResult(item is null ? null : mutate(item));
    }
}
```
Each call site becomes e.g. `return WithLockedTask(idOrKey, cancellationToken, item => { /* method-specific logic; Save(item); AppendActivity(...); */ return item; });`. This is a pure refactor — no behavior change — and every existing `TaskDomainService`-targeted test should pass unmodified; treat any test failure post-refactor as a sign the extraction accidentally changed something, not an acceptable side effect.
**Effort:** 3-4 hours including running the full `MemorySmith.Tests` suite before/after to confirm zero behavior drift. **Sequencing note:** doing this before TSK-0045's larger layering split makes that later work strictly easier (one call site to relocate instead of eight), so it's worth landing first even though TSK-0045 is the higher-priority item on paper.

---

## F31 — `TasksController.cs`: repeated try/catch/NotFound-or-Ok wrapper across 8 actions

**Pattern** (`TasksController.cs`, lines 70-208, confirmed identical in `Update`, `SetStatus`, `AddComment`, `AddLinkedPage`, `AddExternalLink`, `AddAttachment`, `RemoveAttachment`, and — with extra file-handling logic inserted in the middle but the same wrapper shape — `AddFileAttachment`):
```csharp
[HttpX("...")]
[Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
public async Task<ActionResult<TaskItem>> X(...)
{
    try
    {
        var updated = await _tasks.XAsync(...);
        return updated is null ? NotFound() : Ok(updated);
    }
    catch (ArgumentException ex)
    {
        return BadRequest(ex.Message);
    }
}
```
This is a textbook case for an ASP.NET Core exception filter rather than a shared method — the `ArgumentException → 400 BadRequest` mapping is generic HTTP-layer policy, not task-specific logic, so it belongs at the framework boundary:
```csharp
public sealed class ArgumentExceptionToBadRequestFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is ArgumentException ex)
        {
            context.Result = new BadRequestObjectResult(ex.Message);
            context.ExceptionHandled = true;
        }
    }
}
```
applied via `[ServiceFilter(typeof(ArgumentExceptionToBadRequestFilter))]` on the controller (or registered globally if other controllers have the same pattern — worth a quick check of `AdminController`/`PageController` for the same shape before deciding controller-level vs. global). Once the filter is in place, each action collapses to its two meaningful lines:
```csharp
var updated = await _tasks.XAsync(...);
return updated is null ? NotFound() : Ok(updated);
```
**Effort:** 2-3 hours including verifying the filter doesn't change response shape/status code for any existing integration test asserting a 400 body format. **Risk:** low, but this is an HTTP-contract-adjacent change (error response construction) — worth a specific test confirming the error body text is unchanged before/after, since `ex.Message` piped through a filter vs. inline could theoretically differ in wrapping/serialization if not done carefully.

---

## F32 — Data-root path resolution duplicated three times across two files

**All three copies, confirmed via direct side-by-side read (not just jscpd's flagged ranges):**
1. `SemanticEmbeddingSearchService.cs` lines 433-460, inside class `SemanticEmbeddingSearchService` (declared line 183).
2. `SemanticEmbeddingSearchService.cs` lines 844-871, inside class `OnnxTextEmbeddingProvider` (declared line 533, a **second, separate class in the same file**).
3. `CodeSearchService.cs` lines 2806-2844, inside class `CodeSearchService`.

All three declare byte-identical private static `ResolveDataDeploymentRoot(string dataPath)` (walks up out of a `Memories` folder if the configured data path points inside one) and `NormalizeDataRelativePath(string path)` (strips a `../Data/`, `./Data/`, or `Data/` prefix and leading `./` segments, normalizing slashes). The three call sites that use these (`ResolvePath` in the two `SemanticEmbeddingSearchService.cs` classes, `ResolveRepositoryRoot` in `CodeSearchService.cs`) are themselves near-identical too.

**This is the same architectural pattern as F19** (`VarResolver.IsUnderRoot` vs. `MaintenanceWritePermissionService.IsUnderPath`, reported two passes ago) — path/location-resolution logic reinvented per-class rather than shared — now confirmed as a *third* occurrence, which upgrades this from "maybe a one-off" to "a recurring habit in this codebase worth a general fix, not three individual ones." A shared home for this kind of logic already exists and is already used for exactly this category of concern: `MemorySmith.App/Services/MemorySmithConfigurationPaths.cs` (previously examined in Delta Report 4, F22, as the correct place `SettingsOverridePath` resolution lives) — confirmed via grep it does **not** currently contain `ResolveDataDeploymentRoot`/`NormalizeDataRelativePath`, so this is a genuine gap, not a "should have been found there" miss.

**Recommendation:** move both methods into `MemorySmithConfigurationPaths.cs` as public statics (e.g. `MemorySmithConfigurationPaths.ResolveDataDeploymentRoot(dataPath)` / `.NormalizeDataRelativePath(path)`), delete all three private copies, and update the three call sites. Zero behavior change if done as a pure move — this is as close to a risk-free refactor as this codebase offers. **Effort:** 2 hours including confirming both `SemanticEmbeddingSearchService.cs` classes and `CodeSearchService.cs` compile cleanly against the shared static and their respective test suites (`SemanticEmbeddingPathTests.cs`, `SqliteMetadataStoreTests.cs`, and any `CodeSearchService`-specific tests) pass unchanged.

---

## F33 — Chunk-insert SQL boilerplate duplicated with one real behavioral difference (caution flagged)

**Two occurrences in `CodeSearchService.cs`:** lines 1541-1585 (main index-build path, plain `INSERT INTO CodeSearchChunks`) and lines 1867-1908 (shard-merge path, `INSERT OR IGNORE INTO CodeSearchChunks`) — both declare the identical 14 `SqliteParameter` bindings and an identical per-chunk bind-and-execute loop.

**The catch, worth stating plainly since it's the point of this finding:** these are *not* safe to blindly collapse. The main build path uses a plain `INSERT` (a primary-key conflict there is unexpected and should surface as an exception — likely indicating a real bug, like the delete-before-insert step upstream not having run). The shard-merge path deliberately uses `INSERT OR IGNORE` (a conflict there is *expected and fine* — two independently-built shards can legitimately produce the same chunk key, and the merge should just keep whichever copy landed first rather than fail the whole merge). A refactor that extracts "the shared insert helper" without preserving this distinction would either (a) make shard-merges brittle by making conflicts throw, or (b) make the main build silently swallow a real bug by ignoring conflicts it should surface.

**Recommendation:**
```csharp
private static async Task InsertChunksAsync(
    SqliteConnection connection, SqliteTransaction transaction,
    IEnumerable<IndexedChunk> chunks, bool ignoreDuplicates, CancellationToken cancellationToken)
{
    await using var insert = connection.CreateCommand();
    insert.Transaction = transaction;
    insert.CommandText = $@"
INSERT {(ignoreDuplicates ? "OR IGNORE " : "")}INTO CodeSearchChunks (...) VALUES (...);";
    // ...same 14 parameter declarations and bind-loop, once...
}
```
called as `InsertChunksAsync(connection, transaction, chunks, ignoreDuplicates: false, ct)` from the build path and `ignoreDuplicates: true` from the merge path — the conflict-handling difference becomes an explicit, visible parameter instead of an easy-to-lose distinction between two copies of similar-looking SQL text.
**Effort:** 2-3 hours including a test that specifically exercises both flag values against a deliberately-conflicting chunk key, asserting the build path throws/surfaces the conflict and the merge path silently keeps the existing row — this is the test that actually proves the consolidation preserved the distinction, not just that the code compiles.

---

## Note on test-code duplication (not reported as findings)

The filtered-out `MemorySmith.Tests/` clones (the majority of the 479 total — `SqliteMetadataStoreTests.cs`, `TagGovernanceTests.cs`, `SecurityAndSourceLinkTests.cs`, `SemanticEmbeddingPathTests.cs`, `WindowsServiceCommandsTests.cs`, `StateTransitionTests.cs`, and others all showed repeated arrange/act/assert shapes) are a different risk category from production duplication — test repetition is a common, often-acceptable tradeoff for test readability (each test staying self-contained and easy to read in isolation, rather than sharing abstracted setup that makes an individual test's intent harder to see at a glance), and this engagement's stated focus has consistently been production code. Flagging this only so it's clear the 8.30%/479-clone headline number is not being silently under-reported — the bulk of it is test scaffolding repetition that a team may reasonably choose to leave as-is, not undiscovered production risk.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- jscpd's `--min-lines 5 --min-tokens 40` thresholds were chosen to surface meaningful blocks while filtering out trivial noise (e.g., single-line getter repetition); a lower threshold would surface more (mostly low-value) pairs, a higher one might hide smaller-but-real duplicates. Did not re-run at multiple threshold levels to check sensitivity — if a more exhaustive pass is wanted later, re-running at `--min-lines 3 --min-tokens 20` would be the next step, expect significantly more noise to triage.
- F30's "7-8 instances" count is approximate — confirmed 8 method declarations matching the general shape and 9 raw occurrences of the exact `FindByIdOrKey` line; did not individually re-verify all 9 have the *entire* preamble byte-identical (verified 3 of them directly: `UpdateAsync`, `SetStatusAsync`, `RemoveAttachmentAsync`), extrapolated the rest from the consistent method signatures and jscpd's pairwise matches. Recommend the implementing engineer do a final visual pass across all 8-9 before assuming the extracted helper covers every case identically.
- This report does not re-scan `MemorySmith.Agent` (explicitly out of scope per the original request) or the `Data/` wiki-content corpus (not code).
