# MemorySmith Audit — Delta Report 3 (Branch Moved: New Closure Commit)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1`
**Note on commit drift:** the URLs in this request point to `6281037` (audited in the two prior reports). The branch has since advanced to **`e8a3065`** ("Sprint 1 closure: task records, memory updates, docs, TSK-0364 unconsolidated guard", 2026-07-11T08:42:24Z), confirmed via the branch's Atom commit feed. Per your instruction to audit "the latest commit," this pass covers the actual current HEAD, not the older pinned SHA. Everything in Delta Reports 1–2 still stands against the code they examined; this report covers only what changed in `e8a3065` and is not otherwise a re-scan of the whole tree.
**Report generated:** 2026-07-11

---

## Executive Summary

| # | Finding | Confidence | Severity | Notes |
|---|---|---|---|---|
| F17 | TSK-0364's "Unconsolidated guard" fix works, but only by accident of statement ordering — it's a plain `if` block that *overrides* an incorrect assignment made two branches earlier, rather than fixing the guard condition that caused the incorrect assignment | 85% | Medium (fragile, not incorrect) | **New** — verified correct today, but one refactor away from silently regressing |
| F18 | A semantic-search relevance regression test (`SemanticToolQualityTests.HybridProbes`) blocked a planned wiki-documentation update this same commit — the team's own commit message admits the doc edit was abandoned, not fixed, because it broke the test | 80% | Medium (process/architecture smell — docs silently drift from code) | **New** — directly observed happening in this commit, not hypothetical |

---

## F17 — TSK-0364 fix is correct but implemented as a fragile "assign-then-override" pattern (Medium, 85%)

**File:** `MemorySmith.Core/StateMachine/MemoryStateMachine.cs`, current full method:

```csharp
if (allowDeprecation && score < DeprecationThreshold && original != MemoryStatus.Deprecated)
{
    newStatus = MemoryStatus.Deprecated;
}
else if (original == MemoryStatus.Unconsolidated && score >= WorkingThreshold)
{
    newStatus = MemoryStatus.Working;
}
else if (original == MemoryStatus.Working && score >= CoreThreshold)
{
    newStatus = MemoryStatus.Core;
}
if (original == MemoryStatus.Unconsolidated && allowDeprecation && score < DeprecationThreshold)   // ← plain `if`, NOT `else if`
{
    newStatus = MemoryStatus.Unconsolidated;
}
else if (original == MemoryStatus.Core && score < CoreThreshold)
{
    newStatus = MemoryStatus.Working;
}
else if (original == MemoryStatus.Deprecated && score >= WorkingThreshold)
{
    newStatus = MemoryStatus.Working;
}
```

**Traced by hand for the target bug scenario** (fresh Unconsolidated record, score ≈ 0.1, `allowDeprecation = true`):
1. First `if` (line 17): `score < DeprecationThreshold` is true, `original != Deprecated` is true → **fires**, sets `newStatus = Deprecated`. This is the exact bug TSK-0364 was opened to fix — at this point in execution, the bug still happens.
2. The two `else if` branches immediately after (lines 21, 25) are skipped (chain already resolved).
3. The **new**, syntactically-separate `if` (line 33) re-evaluates almost the same condition (`original == Unconsolidated && allowDeprecation && score < DeprecationThreshold`) against `original` — which was never mutated, so it's still `Unconsolidated` — and this condition is also true, so it fires and **overwrites `newStatus` back to `Unconsolidated`**, undoing step 1.
4. Net result: `newStatus == Unconsolidated == original`, no event fires. **The test (`UnconsolidatedRecord_WithLowScore_DoesNotDeprecate`) passes, and the fix genuinely works** for every input I traced (also checked: mid-band scores, at-threshold scores, `allowDeprecation=false`, and interaction with the Core/Deprecated branches below it — no case found where this produces a wrong result).

**Why this is still worth flagging even though it's correct today:** the fix works by exploiting an implementation detail (that `original` is read-only through the whole method and that C# evaluates a second top-level `if` unconditionally after the first chain resolves) rather than by fixing the actual defect, which is that the *first* branch's guard condition is simply missing an exclusion for `Unconsolidated`. The"self-cancelling" nature of this pattern means:
- A future engineer skimming this method has to mentally simulate the interaction between two separate if-chains to understand that Unconsolidated records can't be deprecated — the code doesn't say that directly anywhere, it's an emergent property of the ordering.
- If anyone ever converts this method to a C# `switch` expression on `original` (an extremely natural refactor for exactly this kind of status-dispatch logic, and the kind of cleanup this project's own philosophy — "eliminate legacy/compat paths, consolidate duplication" — explicitly favors), the override trick breaks silently: a `switch` picks one arm per input, so there's no "fire wrong branch, then fire a correcting second branch" mechanism available. The bug TSK-0364 fixed would return with no compiler warning and no obviously-failing test until someone re-derives the exact score-band scenario.
- The duplicated sub-condition (`allowDeprecation && score < DeprecationThreshold`, now written out twice with slightly different surrounding clauses) is exactly the kind of small duplication this project's audits keep finding and asking to consolidate elsewhere (e.g. `FixedTimeEquals`, backlink scans in prior reports).

**Recommendation:** replace the two-block pattern with a single corrected guard on the original branch:
```csharp
if (allowDeprecation && score < DeprecationThreshold
    && original is not (MemoryStatus.Deprecated or MemoryStatus.Unconsolidated))
{
    newStatus = MemoryStatus.Deprecated;
}
```
and delete the override block entirely (lines 29-37 in the current file, including the explanatory comment, which becomes unnecessary once the real guard states the exclusion directly). This is a small, low-risk, same-day cleanup — recommend filing it as a follow-up to TSK-0364 (or amending TSK-0364 itself, since its current "Done"/"InProgress" state per the commit message reflects a working fix, not a final one) rather than leaving the accepted fix as the fragile version. Add one more test explicitly asserting the *reason* — e.g. a test that would fail if someone reordered the branches or converted to `switch` without preserving the exclusion — the current test only proves the observable behavior, not the invariant.

---

## F18 — A content-coupled relevance test blocked a documentation update this commit (Medium, 80%)

**What happened, per the commit's own message** (`e8a3065`):
> `project-wiki-source-link-security-boundaries: Skipped (causes test failure, needs investigation)`

**Root cause, traced:** `MemorySmith.Tests/SemanticToolQualityTests.cs` defines a fixed table of query→expected-memory-ID→expected-max-rank probes:
```csharp
private static readonly SearchQualityProbe[] HybridProbes =
[
    ...
    new("source links allowed roots max read bytes var resolver", "project-wiki-source-link-security-boundaries", 2),
    ...
];
```
This asserts that searching for that exact query string must return the `project-wiki-source-link-security-boundaries` memory record within the top 2 hybrid-search results. Editing the record's `Content` field (as the commit was attempting to do, presumably to add the newly-shipped auth self-lockout guardrail material, matching what *did* successfully happen to the sibling `project-wiki-admin-auth-hardening` record in this same commit) changes its embedding vector, which can shift its rank against this fixed query below the asserted threshold of 2 — the test fails, and rather than fix the test's expectation (or investigate whether the rank shift indicates a real relevance regression worth caring about vs. an expected consequence of legitimately improving the content), the content edit was simply dropped.

**Why this matters architecturally:** this is a real, observed instance of a **golden-value regression test acting as an unintended pin on production content**. Seven other probes in the same `HybridProbes` array create the identical structural risk for their respective memory records (`project-wiki-hybrid-search-rrf`, `project-wiki-mcp-context-pack`, `project-wiki-active-architecture` ×2, `project-wiki-chat-image-attachments`, `project-wiki-mcp-search-tools-current`, `project-wiki-source-links-feature`) — any future edit to any of those eight wiki pages carries the same risk of being silently abandoned rather than the test being re-evaluated. The commit message's own "needs investigation" is the right instinct, but it wasn't converted into a tracked task, which means the *next* time someone hits this same wall (very likely, since wiki content editing is a routine, ongoing activity in this project per the `Data/Pages/` corpus size), the investigation starts from zero again, or — more likely, based on the pattern just observed — gets skipped again.

**This is not the same issue as the general "semantic search relevance" test suite existing** — regression tests for search quality are good and worth keeping. The problem is specifically that the test's *design* (hard top-K rank assertions against literal content, with no tolerance band, no re-baselining process, and no distinction between "the search algorithm got worse" vs. "the content legitimately changed") makes routine content maintenance and test correctness indistinguishable from each other, so the safe default becomes "don't touch the content," which is the opposite of what a living knowledge base needs.

**Recommendation:**
1. File a task (none currently exists for this, confirmed via search) to loosen `SemanticToolQualityTests`'s probes from a fixed rank ceiling to a tolerance-banded or top-K-set assertion (e.g., "in top 5" instead of "in top 2," or assert presence in top-K without strict ordering), and/or add a documented re-baselining procedure so a deliberate content edit can be accompanied by a deliberate, reviewed test update in the same PR instead of being abandoned.
2. Separately, get someone to actually complete the abandoned `project-wiki-source-link-security-boundaries` content update — right now it's dropped with no tracking, and the underlying source-link security-boundary documentation may already be stale relative to the code (I did not independently re-verify the accuracy of that memory record's content against current `VarResolver.cs`/`MemorySmithOptions.cs` behavior in this pass — that's a separate, worthwhile check but outside this delta's scope).

---

## Assumptions

- Verified branch HEAD via the Atom feed at request time; if the branch advances again between this report and your reading it, the same caveat applies as before.
- F17's "traced by hand" claim of correctness covers the score bands and flag combinations I could enumerate by inspection (deprecation-boundary, working-boundary, core-boundary, both `allowDeprecation` states); it is not an exhaustive property-based proof. Recommend the property-based/invariant test suggested above as the actual verification, not this trace.
- F18 assumes the "Skipped... needs investigation" note in the commit message accurately reflects what happened (i.e., a genuine test failure blocked a genuine intended edit) rather than being a placeholder note for something else — I did not have a pre-edit diff of the intended (and reverted/abandoned) memory content change to confirm the causal link beyond the commit message's own account and the test file's structure, which together make the mechanism clear even without seeing the exact abandoned edit.
