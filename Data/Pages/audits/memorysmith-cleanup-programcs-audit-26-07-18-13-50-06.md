# MemorySmith Audit — Cleanup/Maintainability Focus: Program.cs Decomposition Verification
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-18
**Method:** per this pass's specific framing (find cleanup/tech-debt/bloat/maintainability opportunities), verified **TSK-0282** — the highest-priority, largest-blast-radius refactor this codebase's own audit history records ("Audit #9... top recommendation") — actually delivered what it claimed, rather than trusting its Done status. Read `Program.cs` (now 89 lines, down from a documented 862) in full, confirmed all 9 extracted `Hosting/*.cs` modules exist as separate, reasonably-sized files, and did a full read of the largest/most security-relevant one (`MemorySmithContentEndpoints.cs`, 156 lines) rather than stopping at "the file exists."

---

## Executive Summary

| # | Finding | Confidence | Severity | Relationship to existing tasks |
|---|---|---|---|---|
| — | **TSK-0282 genuinely succeeded** — 862 lines → 89-line composition root + 9 cleanly-separated, sensibly-sized modules (33-156 lines each), each with a one-line annotated purpose. The specific incident this task was written to prevent (silently dropping inline blocks during a manual reconstruction) is now structurally much harder to reproduce, exactly as intended | 95% | N/A (positive verification) | Confirms TSK-0282's Done status is accurate, not just claimed |
| — | The regression test guarding the OAuth-callback extraction (`TagGovernanceTests.ExternalAuthCallbacks_RecordDurableSuccessAndFailureEvidence`) was correctly updated to point at the new file locations, **and** its own comment explains it was deliberately narrowed to a wiring-pin now that real behavioral coverage exists elsewhere (`GitHubOAuthCallbackHandlerTests`) — a textbook-correct way to evolve a source-inspection test after a refactor | 90% | N/A (positive verification) | — |
| F50 | `MemorySmithContentEndpoints.ResolvePageAssetPath` (new code, part of the TSK-0282 extraction itself) contains a **third** independent hand-written implementation of the "is this path under this allowed root" containment check — the same pattern already flagged as duplicated in F19 (`VarResolver`/`MaintenanceWritePermissionService`) and already recommended for consolidation into a shared `PathSecurity.IsUnderRoot` helper in this engagement's remediation plan (W1.1) | 95% | Medium (concrete evidence the duplication is still actively recurring, including inside a brand-new, otherwise well-executed cleanup effort) | **Strengthens and re-prioritizes W1.1** from the consolidated remediation plan — no new task needed, but the "why this matters" case just got stronger |
| F51 | The same file's doc comment explicitly states its three `internal static` helpers exist specifically *so the traversal/encoding rules are directly unit-testable* — confirmed `InternalsVisibleTo("MemorySmith.Tests")` is correctly wired for this — but **zero tests currently exist** for any of the three helpers (`ResolvePageAssetPath`, `NormalizePageAssetRequestPath`, `HasValidPercentEncoding`) | 90% | Medium (a deliberately-created testability seam sitting unused, for exactly the kind of security-relevant string logic — path traversal, percent-encoding validation — most worth testing) | **New** |

---

## Verified: TSK-0282 is a genuine success story

Worth stating in detail, not just as a summary line, because this engagement has surfaced a lot of "Done tasks that didn't fully close the gap" (TSK-0289/F36, TSK-0383/F40, TSK-0234/F27) — a clean counterexample matters as much as the problems for calibrating how this codebase's process actually performs.

- `Program.cs` is now 89 lines with a header comment explicitly naming the original incident (*"the June 4 reconstruction silently lost ~16 inline blocks; this shape makes that failure class structurally impossible"*) and stating the design invariant that makes that true (*"every concern lives in a named module... dropping any of them is a one-line diff"*).
- All 9 referenced modules (`MemorySmithConfigurationSetup`, `MemorySmithSecuritySetup`, `MemorySmithStorageSetup`, `MemorySmithCoreSetup`, `MemorySmithTelemetrySetup`, `MemorySmithChatSetup`, `MemorySmithMaintenanceSetup`, `MemorySmithPipelineSetup`, `MemorySmithContentEndpoints`) exist as real, separate files under `MemorySmith.App/Hosting/`, ranging 33-156 lines — a sensible split by concern, not a relocation of the same god-file problem into one giant `Hosting.cs`.
- The 185-line inline OAuth callback lambda specifically called out in TSK-0282's problem statement is now `GitHubOAuthCallbackHandler.cs` (previously read in full in an earlier report in this engagement, where it was found to be well-structured — that earlier read is now retroactively confirmed as reviewing the *correct*, already-fixed version of this code, not a stale pre-refactor copy).
- The regression test that used to pin the *inline lambda's presence in Program.cs as raw text* now correctly checks the new file locations, and its comment explicitly documents *why* it was narrowed in scope (real behavioral tests now exist elsewhere) rather than just silently changing what it asserts — this is the kind of self-documenting test evolution that makes a refactor's safety net legible to the next person, not just functional.

---

## F50 — A third hand-written path-containment check, found inside the TSK-0282 extraction itself (Medium, 95%)

**File:** `MemorySmith.App/Hosting/MemorySmithContentEndpoints.cs`, `ResolvePageAssetPath`, lines 83-94:
```csharp
internal static string? ResolvePageAssetPath(string pageAssetsPath, string assetPath)
{
    var normalizedAssetPath = NormalizePageAssetRequestPath(assetPath);
    if (string.IsNullOrWhiteSpace(normalizedAssetPath) || normalizedAssetPath.Split('/').Any(segment => segment is ".." or "."))
    {
        return null;
    }

    var resolvedPath = Path.GetFullPath(Path.Combine(pageAssetsPath, normalizedAssetPath.Replace('/', Path.DirectorySeparatorChar)));
    var normalizedRoot = pageAssetsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    return resolvedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ? resolvedPath : null;
}
```
The final two lines (`TrimEnd` + append separator + `StartsWith` with `OrdinalIgnoreCase`) are, algorithm-for-algorithm, the same shape as `VarResolver.IsUnderRoot` and `MaintenanceWritePermissionService.IsUnderPath` — already identified as duplicated in an earlier report (F19) and already carrying a concrete extraction recommendation in this engagement's consolidated remediation plan (workstream W1, task W1.1: extract a single `PathSecurity.IsUnderRoot` helper into `MemorySmith.Core/Security/`).

**Why this specific instance matters more than "one more duplicate":** this code is *new this sprint*, written as part of the very refactor whose entire stated purpose was reducing structural risk and improving maintainability (TSK-0282). That's direct, dated evidence that the underlying habit — reach for a hand-rolled containment check rather than a shared one — is still live in how this codebase gets written today, not just a historical artifact from before anyone noticed the pattern. If W1.1's extraction had already landed before this sprint, this file would have had one line to write (`PathSecurity.IsUnderRoot(resolvedPath, pageAssetsPath)`) instead of reproducing the whole algorithm a third time. This is the strongest evidence yet for prioritizing that specific piece of the remediation plan sooner rather than later — every sprint it's deferred is another chance for a fourth (and fifth) copy to appear.
**Recommendation:** no new recommendation needed — this simply confirms and strengthens W1.1's existing scope. When that extraction happens, add this file's call site as the third (not second) one to migrate, and this file's own existing test-shaped gap (F51, below) becomes the natural place to add the shared helper's boundary-condition tests too, rather than writing a fourth copy of that test matrix as well.
**Effort:** already estimated in the remediation plan (0.5 day for the extraction itself); this finding doesn't change that estimate, only its priority.

---

## F51 — Deliberately-testable helpers with zero tests written against them (Medium, 90%)

Same file, doc comment (lines 9-14): *"the helpers are `internal static` so the traversal/encoding rules are directly unit-testable."* This is a clear, explicit statement of intent — someone made a specific design choice (visibility modifier + presumably relying on the existing `InternalsVisibleTo("MemorySmith.Tests")` in `MemorySmith.App/Properties/AssemblyInfo.cs`, confirmed present) to make `ResolvePageAssetPath`, `NormalizePageAssetRequestPath`, and `HasValidPercentEncoding` directly testable without needing a full HTTP pipeline. Grepped `MemorySmith.Tests/` for any reference to all three method names — **zero results**.

**Why this is worth flagging rather than a routine "add more tests" note:** these three helpers are exactly the category of logic where a small, cheap unit test carries disproportionate value — percent-encoding validation and path-segment traversal checks are precisely the kind of string-processing logic that's easy to get subtly wrong at an edge case (double-encoding, mixed-case hex digits, a trailing `%` with fewer than two characters remaining, a segment that's literally empty after a `//`) and where a unit test is far cheaper to write and far more precise at pinning exact behavior than an integration test hitting the real `/page-assets/{**assetPath}` route would be. The capability to test this cheaply was deliberately built and is sitting completely unused.
**Recommendation:** write a focused test class (e.g. `MemorySmithContentEndpointsTests.cs`) covering: `HasValidPercentEncoding` against valid encoding, a truncated `%` at the end of the string, and invalid hex digits; `NormalizePageAssetRequestPath` against a `?query`/`#fragment` suffix, backslash-vs-forward-slash input, and a genuinely malformed percent-sequence (confirming the `UriFormatException` catch path returns `null` rather than throwing); `ResolvePageAssetPath` against a `../`-containing path, a path that resolves outside `pageAssetsPath` via a more indirect route (e.g. a sibling directory sharing a name prefix, which is exactly the kind of edge case the `TrimEnd` + separator-append pattern exists to guard against and is worth confirming still holds here), and a legitimate nested-subdirectory asset path that should succeed. **Effort:** half a day, and this is exactly the sort of test suite that becomes the natural home for the shared `PathSecurity.IsUnderRoot` helper's own boundary tests once F50's consolidation happens, rather than needing to be written twice.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Did not individually full-read all 9 extracted `Hosting/*.cs` modules in this pass — `MemorySmithContentEndpoints.cs` was chosen as the highest-value single file to verify in depth (largest of the group, handles authenticated file-serving, most likely to contain the kind of security-relevant logic worth double-checking) rather than spreading equal depth across all 9; the other 8 remain open scope for a subsequent pass if a complete line-by-line review of the whole `Hosting/` folder is wanted.
- F50's severity framing (Medium, not elevated beyond the existing W1.1 priority) reflects that this is additional *evidence* for an already-identified and already-prioritized issue, not a newly-discovered independent risk — the practical fix and its priority were already captured in the consolidated remediation plan; this pass's contribution is strengthening the case for doing it soon rather than changing what "it" is.
