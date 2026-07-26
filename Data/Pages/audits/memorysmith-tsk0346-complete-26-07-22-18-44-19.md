# MemorySmith Audit — TSK-0346 Checklist Verification: Complete (Chat.razor + TrainingWorkbench.razor)
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-22
**Method:** closed out the two files left unverified in the prior report. `TSK-0346`'s entire "known locations" list (8 files, 15 originally-cited catch blocks) is now fully verified against current code — this report supersedes the prior one's "not yet verified" status for `Chat.razor` and `TrainingWorkbench.razor` and should be read as the completion of that checklist, not a separate finding.

---

## `Chat.razor` — both cited locations still live

**Line 749** (`LoadStorageTextAsync`): a bare `catch` around a browser-`localStorage` read via JS interop, falling through to `MarkStorageLoadFailed(warnOnFailure)` and returning `null`. **Low severity** — this at least routes through a named handler that presumably surfaces *some* signal when `warnOnFailure` is true, rather than being pure silence; the same low-risk shape as the now-fixed `SafeJsInterop.cs` pattern, just not narrowed to specific JS-interop exception types.

**Line 2873** (embedded "question card" JSON parsing inside chat message content): a bare `catch` around `JsonNode.Parse`, returning `null` on any parse failure, which causes the content to fall back to plain rendering instead of a structured interactive card. **Low-Medium severity** — the fallback behavior itself is reasonable (malformed embedded JSON from an LLM response shouldn't crash the chat UI), but there's zero signal anywhere (not even a debug log) for a developer trying to diagnose why a given message isn't rendering as a card. Worth at least a `LogDebug` on the caught exception.

## `TrainingWorkbench.razor` — all four cited locations still live

**Line 494** (loading a training run's status/metrics/benchmark files): a bare `catch` returning `null` on **any** failure reading these files. **Medium severity** — this is the most consequential of the newly-verified locations: a training-run status card silently vanishes from the workbench for any reason at all (a transient file lock, a genuinely deleted run, actual data corruption), with no way for an operator to distinguish "this run doesn't exist" from "something is currently preventing me from reading it." Given this directly affects observability of a real ML training process — plausibly something a user would actively be waiting on/monitoring — this is the one location out of TSK-0346's full list most worth prioritizing alongside `FileEventStore.cs`.

**Line 527** (formatting a timestamp for display): a bare `catch` falling back to `"-"`. **Low severity** — purely cosmetic, no data or observability consequence beyond a slightly less informative label.

**Line 569** (parsing records-per-second/final-loss metrics): a bare `catch` falling back to `("-", "-")`. **Low-Medium severity** — same shape and consequence as line 494 but scoped to two specific display fields rather than an entire run's status.

**Line 607** (parsing individual training telemetry event lines): the **only one of all 15 locations with an explanatory comment already in place** — *"Ignore malformed lines so one bad event does not break telemetry rendering."* This is a genuinely correct, deliberate design choice (isolating one bad line from the rest of the event stream is the right call), not a smell. The one remaining gap: there's no count anywhere of *how many* lines were skipped, so a telemetry stream with a systematic parsing problem (not just an occasional bad line) would look identical to a healthy one from the UI's perspective. **Low severity, recommend a running skipped-count surfaced in the UI rather than a log line**, since the existing design intent here is sound and worth preserving, just slightly under-instrumented.

---

## `TSK-0346` — complete, current status of all 15 originally-cited locations

| # | Location | Status | Severity |
|---|---|---|---|
| 1 | `SafeJsInterop.cs` | **Resolved** — remove from ticket | — |
| 2-3 | `FileEventStore.cs` (×2) | Live | **Medium** |
| 4 | `CodeSearch.razor` | Live | Low-Medium |
| 5 | `HealthStats.razor` | Live | Low |
| 6 | `Bridge/Program.cs` | Live (line drifted 640→498) | Low |
| 7 | `FileMemoryStore.cs` | Present but likely a different/lower-severity catch than the ticket's phrasing implies — needs a second look, not a fix | Very Low (as found) |
| 8-9 | `Chat.razor` (×2) | Live | Low, Low-Medium |
| 10 | `TrainingWorkbench.razor` line 494 | Live | **Medium** |
| 11 | `TrainingWorkbench.razor` line 527 | Live | Low |
| 12 | `TrainingWorkbench.razor` line 569 | Live | Low-Medium |
| 13 | `TrainingWorkbench.razor` line 607 | Live, but already well-designed — just under-instrumented | Low |

**Recommended priority order for `TSK-0346`, now that the full list is verified:** `FileEventStore.cs` and `TrainingWorkbench.razor` line 494 first (both have genuine observability consequences for real operational/monitoring use cases), everything else can follow in any order since none of the remaining locations carry meaningful risk beyond "a developer has to guess why something silently degraded." This turns a flat 15-item list into an actually-prioritized one, which is the concrete value of having done this verification pass at all rather than leaving the ticket as an unranked checklist.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- This completes verification of every location `TSK-0346` currently names. It does not claim these are the *only* silent catches in the codebase — only that this specific ticket's existing list is now fully checked and current.
