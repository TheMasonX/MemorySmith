# MemorySmith Audit — Corpus Sweep, Round 2: A Recurring "Half-Fixed" Pattern
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-21
**Method:** continued the corpus sweep, this time reading `Data/Pages/audits/codebase-audit-20260710-swarm-synthesis.md` in full — a 2026-07-10 document, one day older than the one processed last report, and (confirmed below) the actual **origin document** for two claims this engagement had already independently investigated from the newer document's side. Verified every P0 and P1 item in this document against current code and the task backlog rather than treating the older document as superseded just because a newer one exists.

---

## Executive Summary

| # | Finding | Confidence | Status |
|---|---|---|---|
| — | **This is the origin document.** The "1.23 sum" and "MemoryIndex unsynchronized" claims the 2026-07-11 synthesis's skeptical review addressed both originate here, dated one day earlier, with full original detail | — | Context, not a finding |
| — | **P0-001 (scoring weights) was only half-fixed.** The weight *coefficients* were corrected from summing to 1.23 down to exactly 1.0 — but the same finding's other half, "normalize `References.Count` (log or cap)," was never done. This is exactly this engagement's own F10, now dated and historically grounded: the bug was real, partially fixed, and the unaddressed half is still live today | 95% | **Confirms F10 with historical grounding** — not new, but now precisely dated |
| — | **P0-002 (MemoryIndex race) was only half-fixed.** At the time of this document, `MemoryIndex` had *zero* synchronization at all. A `ReaderWriterLockSlim` was added at some point since — but only around writes, never reads. This is exactly this engagement's own F2 | 95% | **Confirms F2 with historical grounding** — same conclusion, now dated |
| — | **P1-001 (API key env-var mismatch) is the same bug as F59**, corroborated by a second, independent, one-day-older source document | 95% | Confirms F59; still live, still untracked (per prior report) |
| — | **P1-005 (loopback callers blocked by API key check) is correctly tracked** as `TSK-0345` (Backlog, High) — but the task's own description text is now slightly stale: one of its two described sub-problems (`/api/admin/setup/status` exemption) was already fixed by a broader path-prefix change, while the core problem (loopback callers still required to supply an API key) remains genuinely open exactly as described | 90% | **Tracked correctly; description needs a small update, not a new ticket** |
| — | **P1-007 lists specific silent-catch locations this engagement hasn't verified**, including files never yet read (`Chat.razor`, `TrainingWorkbench.razor`, `CodeSearch.razor`, `HealthStats.razor`, `SafeJsInterop.cs`, `FileEventStore.cs`, `MemorySmith.Bridge/Program.cs`) | — | **Concrete, actionable checklist for a follow-up pass** — not yet verified in this report |

---

## The headline pattern: fixes that resolve the letter of a finding but not its substance

Three items in this one document — independently traceable to three different subsystems — show the same shape of incomplete remediation:

1. **Scoring weights**: the *symptom* named in the original finding ("weights sum to 1.23, not 1.0") was fixed precisely — the coefficients now sum to exactly 1.0 (`0.50+0.25+0.15+0.10`). But the finding's stated *root cause and full recommendation* ("an unnormalized `References.Count` term dominates... normalize weights to sum to 1.0, normalize `References.Count`... and re-calibrate thresholds") was only followed for the first clause. The unnormalized `References.Count` term this engagement's own F10 flagged is still exactly as unbounded as this document originally described — the fix made the formula's coefficients look tidy without addressing the actual behavioral problem the coefficients were only a symptom of.
2. **MemoryIndex locking**: the original finding ("zero synchronization... use `ConcurrentDictionary` or `ReaderWriterLockSlim`, make dictionaries private, expose query methods") got a `ReaderWriterLockSlim` added — satisfying the letter of the recommendation's first half — but not the second half ("make dictionaries private, expose query methods"), which is exactly what would have forced the read-side locking gap to be addressed as a natural consequence. The properties are still public, mutable, and unprotected for reads, for the same reason this engagement's F2 already diagnosed: nothing forces a caller to go through a lock-aware accessor.
3. **Request-guard exempt paths**: the original finding's two sub-problems (loopback-requires-key; setup-status not exempt) got asymmetric attention — the narrower, more mechanical one (add a path to an exempt list) was fixed, while the broader, more architecturally-involved one (loopback callers shouldn't need a shared remote-access secret at all) is still open, tracked, and unstarted.

**Why this is worth naming as a pattern rather than three coincidences:** in all three cases, the *easier* half of a two-part fix landed, and the *harder* half — the one requiring an actual design decision (how should `References.Count` be bounded? should the index expose accessor methods instead of raw dictionaries? should the loopback/API-key interaction be restructured?) — didn't. That's a legible, specific signal about where this project's remediation process tends to stop short, and it's more useful to whoever prioritizes future work than treating each of these as an isolated bug: **when closing a finding that has multiple named sub-recommendations, verify all of them landed, not just the first or the easiest.**

---

## P1-005 (`TSK-0345`): tracked correctly, description needs a small correction

Confirmed via direct code read (`MemorySmithRequestGuardMiddleware.cs`, lines 45-50) that the core problem TSK-0345 describes is genuinely still present: the API-key check at line 45 has no `isLoopback` condition anywhere in it, so a loopback caller is blocked exactly as the task says, whenever `settings.ApiKey` happens to be configured — regardless of `AllowRemoteApi`'s value. This part of the task's description is accurate and the work is genuinely still needed.

The task's *other* named sub-problem — *"`/api/admin/setup/status` is not in the ApiKeyExemptUiPaths list"* — is now stale. `ApiKeyExemptUiPaths` contains `/api/admin/setup` (not the narrower `/api/admin/setup/status`), and `IsApiKeyExemptUiPath` uses `PathString.StartsWithSegments`, which matches on path-segment boundaries — `/api/admin/setup/status` does start with the segments `/api/admin/setup`, so it's already covered. This isn't a new bug or a reason to close the ticket; it's a one-line correction to keep the ticket's description accurate for whoever picks it up next, so they don't spend time re-verifying something already resolved.
**Recommendation:** update `TSK-0345`'s description to drop the now-resolved setup-status clause and keep the still-open loopback-API-key clause as the ticket's sole remaining scope. Trivial edit, no code change needed for this specific correction.

---

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Did not verify P1-007's specific file:line citations in this pass (`Chat.razor` ~749/~2873, `TrainingWorkbench.razor` 494/527/569/607, `CodeSearch.razor` ~326, `HealthStats.razor` ~300, `SafeJsInterop.cs` ~27, `FileEventStore.cs` 65-82/88-91, `MemorySmith.Bridge/Program.cs` ~640) — these are concrete, checkable claims from a document old enough (2026-07-10) that some may already be fixed the same way P0-001/P0-002/P1-005 were partially fixed; recommend this be the next pass's starting checklist rather than reading these files cold.
- This report covers one more document out of the corpus; roughly 60 remain unexamined for the same kind of gap. The two documents processed so far (2026-07-10 and 2026-07-11 swarm syntheses) both turned out to be unusually information-dense and high-signal — there's no guarantee the remaining ~60 have the same yield, and a sampling strategy (checking a few more recent, high-severity-density documents before assuming diminishing returns) is more efficient than committing to read all of them exhaustively.
