# MemorySmith Audit — `MaintenanceDiffService`: Unbounded O(n·m) Table + Uncatchable Recursive Crash Risk
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-19
**Method:** continued the deep read of `MaintenanceAgentServices.cs`. `MaintenanceDiffService` (lines 409-466) is a small, self-contained class — exactly the kind of thing easy to skim past as "just a diff helper." Reading it fully, then tracing its one real call site to confirm the inputs are genuinely unbounded, is what turned it into this report's finding.

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| F56 | `MaintenanceDiffService.BuildUnifiedDiff` computes a full classic LCS dynamic-programming table (`O(before.Length × after.Length)` time **and** memory) with no size cap, then backtracks it with an **unbounded recursive** function whose depth scales with the diff's edit-script length — and this runs on every proposal save, over whatever full file content a maintenance proposal targets, which nothing upstream caps. A large enough diff doesn't just run slowly — the recursive backtrack risks a `StackOverflowException`, which in .NET is **uncatchable and terminates the entire process**, not just the current request | 85% | High (a single large, plausible maintenance proposal — e.g. a full wiki-page rewrite — can crash the whole application process, not just fail gracefully) | **New** — no existing task covers this; TSK-0043 (general decomposition of this file) doesn't mention it |

---

## F56 — Diff computation has no size guard and a fatal recursion risk (High, 85%)

**File:** `MemorySmith.App/Services/MaintenanceAgentServices.cs`, `MaintenanceDiffService`, lines 409-466 (full class, quoted in relevant part):
```csharp
private static int[,] BuildCommonSubsequenceTable(IReadOnlyList<string> left, IReadOnlyList<string> right)
{
    var table = new int[left.Count + 1, right.Count + 1];   // ← O(n·m) memory, no cap
    for (var i = 1; i <= left.Count; i++)
        for (var j = 1; j <= right.Count; j++)
            table[i, j] = ...;   // O(n·m) time
    return table;
}

private static void AppendDiff(IReadOnlyList<string> before, IReadOnlyList<string> after, int[,] table, int i, int j, List<string> diff)
{
    if (i > 0 && j > 0 && string.Equals(before[i - 1], after[j - 1], StringComparison.Ordinal))
    {
        AppendDiff(before, after, table, i - 1, j - 1, diff);   // ← recursive, one call frame per line of the resulting diff, no depth cap
        diff.Add(" " + before[i - 1]);
    }
    else if (...) { AppendDiff(...); diff.Add("+" + ...); }
    else if (...) { AppendDiff(...); diff.Add("-" + ...); }
}
```
This is a textbook Longest-Common-Subsequence unified-diff implementation — algorithmically correct, and fine for small inputs. Two compounding problems as input size grows:
1. **The DP table itself is `O(n·m)` memory** with no cap — `left.Count` and `right.Count` are simply the line counts of `before`/`after`. For two ~1,000-line texts, that's a ~4MB `int[1001,1001]` array — manageable. For two ~10,000-line texts (a large wiki page or a substantial memory-record rewrite, not an exotic input), that's a `int[10001,10001]` array — **~400MB for one diff computation**, and per F55 (this engagement's immediately preceding finding), two maintenance runs can currently execute concurrently with no guard, so this can multiply.
2. **The backtrack is recursive with no depth limit.** `AppendDiff`'s recursion depth is proportional to the length of the edit script it's reconstructing — roughly `left.Count + right.Count` in the worst case (a diff with many changed lines, e.g. replacing most of a large file's content, which is exactly the shape of change a maintenance-agent-authored proposal is likely to produce). .NET's default thread stack is ~1MB; each `AppendDiff` frame is small, but at a few thousand lines of net-new recursion depth, this is a realistic way to exhaust it. **`StackOverflowException` in .NET cannot be caught by any `try`/`catch` block — it unconditionally terminates the process.** This isn't "the current HTTP request fails with a 500"; it's "the entire ASP.NET Core host process dies," taking down every other in-flight request and any other user's session with it.

**Confirmed this is genuinely reachable, not a contrived worst case:** traced the one real call site, `FileMaintenanceProposalStore.SaveAsync` (line 598): `Changes = proposal.Changes.Select(_diff.WithDiff).ToList()` — this runs **unconditionally, on every single proposal save** (submit, approve, reject, respond-for-revision, agent-revision — every `MaintenanceProposalWorkflow` method examined in the prior report ends up calling `SaveAsync`), for every change in the proposal whose `Diff` field is still empty. `Change.Before`/`Change.After` are full file-content strings for whatever the maintenance agent (or a human, or the chat agent per `IsChatAgentProposal`) is proposing to change — nothing found anywhere upstream in this engagement's reads of `MaintenanceWritePermissionService`, `MaintenanceAgentConfigService`, or the proposal model itself caps the size of `Before`/`After`. A maintenance-agent-proposed rewrite of a large existing wiki page, or a bulk content-cleanup proposal touching a big memory-record file, is exactly the kind of legitimate, intended use of this feature that would produce a large diff — this isn't an adversarial-input scenario, it's a plausible outcome of the feature working as designed on a large-enough target file.

**Recommendation:**
1. **Immediate, low-risk mitigation:** add a size guard before attempting the LCS diff — if `before.Length`/`after.Length` (or line count) exceeds a configurable threshold, skip the detailed line-by-line diff and fall back to a cheap summary (`"<N> lines changed (diff omitted: file too large for detailed rendering)"`), or store a byte-level unified-diff computed by a bounded/iterative algorithm instead. The proposal's `Diff` field is presumably for human-readability during review — a summary is a reasonable degradation for oversized inputs, and far better than an outright process crash.
2. **Fix the recursion regardless of the size guard**, since even a moderately large diff (well below whatever size threshold is chosen for mitigation #1) can still accumulate meaningful recursion depth if the two texts are highly dissimilar (maximizing edit-script length relative to total size) — convert `AppendDiff` to an iterative loop with an explicit stack (a `Stack<(int i, int j)>` or simply walking the table backwards in a `while` loop and reversing the resulting list at the end) rather than a recursive function. This removes the uncatchable-crash risk entirely, independent of any size cap, and is a mechanical, low-risk rewrite of an already-correct algorithm — the DP table and the backtrack logic don't change, only the control-flow shape of the second half.
**Effort:** half a day including both fixes and a test that constructs a deliberately large (e.g., 50,000-line) before/after pair and asserts `BuildUnifiedDiff` completes without a stack overflow and within a reasonable time/memory bound — this is exactly the kind of test that's cheap to write and would have caught this immediately, since a stack-overflow crash is otherwise very hard to observe in a unit test (it kills the test runner process too, which is itself a diagnostic signal worth specifically calling out to whoever picks this up).
**Confidence (85%):** the algorithmic claims (O(n·m) table, unbounded recursion depth, uncatchable `StackOverflowException` in .NET) are well-established facts about the .NET runtime and the code's structure, not speculative. The reachability claim (proposal content is genuinely unbounded) is confirmed by tracing the real call site and not finding a cap anywhere in this engagement's reads so far — but this engagement has not yet read 100% of every file that constructs a `MaintenanceProposalChange`, so there's a residual chance an upstream cap exists somewhere unexamined. The 15% held back reflects that gap plus the standing caveat that this sandbox can't run the code to empirically trigger the crash and confirm the exact input size threshold.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Did not exhaustively verify every possible source of `MaintenanceProposalChange.Before`/`.After` content across the whole codebase for a hidden upstream size cap — checked the classes read so far in this engagement (`MaintenanceWritePermissionService`, the proposal workflow methods) and found none, but this is not a claim that no cap exists anywhere in the system.
- This continues, rather than completes, the read of `MaintenanceAgentServices.cs` — `MaintenanceAgentConfigService`, `MaintenanceResourceProbe`, `MaintenanceTopicMapService`'s full body, and the remainder of `MaintenanceAgentService`'s own methods remain open scope for a further pass.
