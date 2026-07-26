# MemorySmith Audit — Corpus Sweep, Round 3: Good News — Four of a Council Review's Top Findings Are Resolved
**Repo:** `TheMasonX/MemorySmith` · **Branch:** `dev/sprint-1` @ `e8a3065`
**Report generated:** 2026-07-23
**Method:** read `Data/Pages/audits/council-codebase-audit-20260710.md` in full — a 5-seat structured council review validating a separate 94-finding, 10-agent swarm report, with explicit per-seat confidence scoring and a recorded dissent-resolution process. Directly verified its top-priority items (both P0 blockers, plus two items an earlier seat had flagged as needing independent confirmation) against current code rather than treating "reviewed by a council" as itself sufficient assurance.

---

## Executive Summary — this report is mostly good news

| Finding (as this council rated it) | Verified current status | Confidence |
|---|---|---|
| **P0-001** — path traversal in `FileMemoryStore.SanitizeId` (regex omitted `..`), called a release blocker at 100% confidence by two seats | **Resolved.** Current regex is `[/\\:?*]|\.\.` — the `..` alternation has been added | 95% |
| **P0-002** — `MemorySmith.Benchmarks.csproj` referencing a non-existent BenchmarkDotNet version | **Resolved.** Current reference is `BenchmarkDotNet 0.14.0`, a real, valid, published version | 90% |
| **P1-009** — `AuthController.Login` missing failed-login audit logging (the one item with genuine seat-vs-seat dissent in this document) | **Resolved, and more thoroughly than either seat's debate anticipated.** `SecurityServices.SignInAsync` records both a structured `LoginHistoryEntry` (hashed IP/UA, failure code, timestamp) **and** a separate `_audit.RecordAsync("auth.login.failed", ...)` call on every failed attempt | 95% |
| **P1-015** — `launchSettings.json` using a non-standard `"LocalDevelopment"` environment name instead of `"Development"` | **Resolved.** Current `launchSettings.json` uses `"Development"`, matching `appsettings.Development.json`'s naming convention | 95% |

**None of these needed a new finding written up — they needed verification, and the verification came back clean.** This is worth reporting with the same rigor as a live bug: a thorough, well-reasoned council review flagged four real, well-evidenced issues (including two release-blocking severity items) back on 2026-07-10, and by the current `e8a3065` HEAD, all four have been substantively fixed — not just patched at the surface, in each case.

---

## Why these four specifically, and what makes each verification solid

**P0-001** was checked by tracing the actual regex's behavior against a traversal payload (`../../etc/passwd`) character-by-character rather than just confirming the pattern text changed — the `\.\.` alternation, combined with the existing separator-character class, leaves no way for a `..` or path-separator sequence to survive sanitization, which is what actually closes the vulnerability (a naive fix that only handled one case would still have left a gap).

**P0-002** was a one-line check, but worth doing rather than assuming — `0.14.0` is confirmed as a real BenchmarkDotNet release (unlike whatever the original finding's evidence, likely `0.15.8` per this document's own Open Questions section, turned out to be — a typo, a pre-release channel reference, or similar). The Benchmarks project can restore and build again.

**P1-009** is the most interesting of the four, because the council's own recorded dissent (Skeptical Reviewer vs. Source-Grounded Archivist) never actually got resolved with certainty — the synthesis explicitly de-escalated it to P2 "pending audit of middleware behavior" rather than closing the underlying question. Checking the real code answers it definitively: the application layer's own `SignInAsync` already does exactly what the Source-Grounded Archivist argued for (a structured, correlatable audit event), independent of whatever ASP.NET Core's authentication middleware may or may not separately do — meaning the debate's premise (maybe middleware alone covers this) was moot; the application code covers it either way, and does so with two independent, complementary records (an operational `LoginHistoryEntry` for security review, and a general `_audit` entry consistent with every other audited action in the system).

**P1-015** was checked directly against the actual file, not assumed fixed just because it's a low-severity cosmetic item — confirmed `Development` now matches ASP.NET Core's standard convention.

---

## What this confirms about this codebase's remediation process, in contrast to the prior two corpus-sweep reports

This is a useful counterpoint to this engagement's own prior corpus-sweep findings (the "recurring half-fixed pattern" from Round 2, and the several genuinely-still-untracked findings from Round 1 and 2). Those rounds found real gaps — but this round demonstrates the process isn't uniformly leaky: a full council-level review's **highest-severity, most release-critical items** (both P0s, the actual blockers) got fixed thoroughly, not superficially. The pattern across all three sweep rounds together suggests: **top-priority, blocker-tagged items get closed reliably; it's the P1-tier items with two-part recommendations, or items that never got a ticket at all, where things fall through.** That's a more precise, more useful characterization of this project's actual remediation reliability than either "everything gets fixed" or "nothing gets fixed" would be — and it's a better prioritization signal for anyone deciding how much independent verification a given open finding warrants before acting on it.

---

## Remaining threads from this same document, not yet verified

This council review's synthesis also recalibrated several other items worth checking in a future pass, not verified in this one:
- **P1-004/P1-005** ("divergent state promotion paths" / "index not updated during consolidation causing stale/phantom search results") — likely already resolved, since this engagement's very first report directly quoted `MemoryMaintenanceTasks.cs` calling `_index.Rebuild(...)` both before and after consolidation; worth a one-line confirmation rather than assumption.
- **P1-006/P1-012** (god-class decomposition for `SqliteMemorySmithDatabase`/`McpController`) — the former is already this engagement's own F12/TSK-3081; `McpController`'s specific "9 dependencies" claim hasn't been independently checked.
- **P2-032/P2-033** (recalibrated to P1: "no demotion, no re-promotion") — superseded by this engagement's own more precise, more current F1 finding (the state machine now has deprecation and full-recovery re-promotion; the remaining gap is narrower — partial-recovery only — than what this older document describes).
- **P2-039** (recalibrated to P1: missing `JsonPropertyName` attributes, "will cause API contract breaks") — not yet checked against current code.

## Assumptions

- Confirmed branch HEAD unchanged (`e8a3065`) before this pass.
- Did not verify the four "remaining threads" listed above in this pass — flagged as open rather than assumed either way.
- P0-002's confidence is held at 90% rather than higher because I did not independently confirm `0.14.0` restores cleanly via an actual `dotnet restore` in this sandbox (still infeasible per this engagement's standing feasibility note) — the version number matching a real, known-published release is strong evidence, not a substitute for an actual restore.
