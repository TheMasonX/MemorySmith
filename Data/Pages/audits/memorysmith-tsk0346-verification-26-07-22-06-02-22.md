# MemorySmith Audit — TSK-0346 Checklist Verification: Which Silent Catches Are Still Live
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-22
**Method:** followed through on the prior report's own recommendation — verified P1-007's specific silent-catch citations against current code rather than leaving them as an unverified checklist. Confirmed all target files still exist, then read each cited location directly. This turns `TSK-0346`'s "known locations" list (itself unchanged since it was written against 2026-07-10's code) into an up-to-date, severity-triaged checklist rather than a new finding — this is a verification/correction pass on an existing High-priority ticket, not a duplicate report.

---

## Executive Summary

| Location (from TSK-0346) | Status | Severity if still open | Notes |
|---|---|---|---|
| `SafeJsInterop.cs` (~line 27) | **Already fixed** — no longer a bare catch | N/A | Now correctly catches `JSException`/`JSDisconnectedException` specifically; this line item should be struck from the ticket |
| `FileEventStore.cs` (2×, now ~lines 82, 89) | **Still live**, exactly as described | Medium — silently discards malformed log lines and silently returns an empty result if the whole file is unreadable, with no way to distinguish "genuinely empty" from "couldn't read" | Confirmed both bare `catch { }` blocks still present |
| `CodeSearch.razor` (~line 326, now ~326) | **Still live** | Low-Medium — has an explanatory comment ("Background indexing failure is non-critical"), so at least the *intent* is documented even without logging | Confirmed present |
| `HealthStats.razor` (~line 300, now ~300) | **Still live** | Low — sets a fallback UI readiness string, no comment at all | Confirmed present |
| `MemorySmith.Bridge/Program.cs` (~line 640, now ~line 498) | **Still live**, location has drifted with file growth | Low — has an explanatory comment ("Fall through to the documented default") | Confirmed present; ticket's line number is stale but the finding is accurate |
| `FileMemoryStore.cs` (~line 100 per ticket, actual ~119) | **Present, but likely a different/lower-severity catch than the ticket implies** | Very Low | This is a temp-file cleanup-on-failure catch inside a `finally` block, explicitly commented `/* ignore cleanup errors */` — reasonable, low-risk as written. The ticket's phrasing ("silent exception propagation on corrupt files") suggests a *read/load* failure path, which may be a different, still-unlocated catch in this file, or may simply be an imprecise description of this one |
| `Chat.razor` (2×, ~749/~2873); `TrainingWorkbench.razor` (4×, ~494/527/569/607) | **Not verified in this pass** | Unknown | Both files confirmed to still exist and are large (`Chat.razor` now 3232 lines, up from the ~3100 the ticket cites — confirms it's still growing, not shrinking); the specific cited lines were not individually checked this round |

---

## Recommendation for `TSK-0346`

Update the ticket's "Known locations" list to reflect this verification rather than leaving it as a static snapshot from 2026-07-10:
1. **Remove** `SafeJsInterop.cs` — already resolved.
2. **Keep and prioritize** `FileEventStore.cs` — the only location in this batch with a real data-visibility consequence (silently losing track of corrupted or unreadable event-log data, which matters for anything relying on this store for history/audit purposes).
3. **Keep, lower priority** `CodeSearch.razor`, `HealthStats.razor`, `Bridge/Program.cs` — all confirmed still bare, but all three already have explanatory comments (except `HealthStats.razor`) and all three are UI/CLI-adjacent fallback paths with low real consequence beyond "an operator can't tell why something silently degraded." Fixing these is good hygiene, not urgent risk reduction.
4. **Re-verify or reword** the `FileMemoryStore.cs` entry — either confirm the temp-cleanup catch found here is genuinely what was meant (in which case, consider marking it as acceptable-as-is given the `finally`-block cleanup context, similar to this engagement's own earlier finding that not every silent catch is a smell — some are correctly-scoped, deliberate suppressions of a truly inconsequential failure), or locate a different, more consequential catch this file may also contain that better matches "silent exception propagation on corrupt files."
5. **Still needs verification**: `Chat.razor`'s 2 citations and `TrainingWorkbench.razor`'s 4 citations — recommend this be the next specific check, given both files are large enough that "still exists, still that size" doesn't confirm the exact lines are unchanged.

**Effort to close out the remaining verification:** under an hour — this is exactly the kind of quick, mechanical check that turns a vague, aging "15+ locations across 6+ files" ticket into a small, precisely-scoped, easy-to-execute set of fixes, most of which (per this pass's findings) are genuinely low-risk, quick wins once someone sits down to do them.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Did not verify `Chat.razor`'s or `TrainingWorkbench.razor`'s specific cited lines in this pass — flagged explicitly above rather than assumed to still be accurate.
- The `FileMemoryStore.cs` ambiguity (temp-cleanup catch vs. a possible different corrupt-file-read catch) is noted as genuinely unresolved rather than guessed at — recommend a direct follow-up read of the file's full read/load path before updating that specific line item in the ticket.
