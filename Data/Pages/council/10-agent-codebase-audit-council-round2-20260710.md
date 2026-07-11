# Council Review: 10-Agent Codebase Audit Round 2 — Sprint 60

**Date:** 2026-07-10 (Round 2)
**Type:** 6-seat heterogeneous swarm (Branch B)
**Meeting:** Post-audit peer review of `Data/Pages/Audits/codebase-audit-20260710-10-agent-swarm.md`
**Method:** Parallel subagent swarm — each seat independently investigated findings against source code
**Relation to Round 1:** This is a fresh audit sweep covering both MemorySmith.Agent and MemorySmith repos, distinct from the earlier MemorySmith-only audit.

---

## Decision

The audit report is accepted with **moderate confidence (72%)** after significant recalibration. 2 of 16 P1 findings were factually wrong; 5 were over-severity. The 3 remaining P1 findings (PlaceBlock coords, SignalR drift, task schema violations) plus 1 P0 upgrade (MemoryScorer) and 3 P2→P1 upgrades (runtime decomposition, PageSummary Score, ChatServices monolith) define the remediation priorities for Sprint 60 and beyond.

---

## Evidence Reviewed

- **Audit report:** `Data/Pages/Audits/codebase-audit-20260710-10-agent-swarm.md` — 144 raw findings
- **Roadmap:** `MemorySmith.Agent/Data/Pages/roadmap.md` — Sprint 60 (current): "Architectural Stability & Legacy Cleanup"
- **Source code spot-checks:** All 10 partition areas verified by Source-Grounded Archivist; P1 findings individually verified against actual file contents
- **Council skill:** `.github/skills/council/SKILL.md`
- **Subagent-swarm skill:** `.github/skills/subagent-swarm/SKILL.md`
- **Codebase audit skill:** `.github/skills/codebase-audit/SKILL.md`

---

## Findings

| Seat | Recommendation | Confidence | Blocking Concern |
|------|---------------|:----------:|-----------------|
| **Source-Grounded Archivist** | 12/16 P1 confirmed; 1 partially confirmed; 3 overclaimed. BlueprintRepository TaskCanceledException handling is actually correct — only ItemRegistry vulnerable. | 95% | ChatServices file path in report is wrong (it's MemorySmith.App/Services/, not MemorySmith.Core) |
| **Data Model Architect** | 3 upgrades (P2→P1): Runtime decomposition, PageSummary Score, ChatServices monolith. MemoryScorer upgraded to P0 — root cause is dimensionally incompatible scoring factors, not just weight sum. | 95% | MemoryScorer scoring is unbounded — thresholds are meaningless for any record with usage or references |
| **Retrieval Specialist** | 1 upgrade (P2→P1): PageSummary Score gap. 1 downgrade (P2→P3): search-fallback gate. Critical finding: RRF scores are so small (~0.016 at rank 1) that LocalKnowledgeResolver gateway search path is effectively dead weight. | 85% | Unified search ranking is broken for any mixed memory+page query |
| **Human Learning Advocate** | ChatServices monolith and task schema violations are highest developer-frustration items. No severity changes, but notes 7 findings share the same root cause pattern (deferred decomposition). | 90% | AGENTS.md has Minecraft-specific content irrelevant to MemorySmith developers (P2) |
| **Skeptical Reviewer** | 2 P1 findings WRONG (chatFilter.js intentionally deleted; duplicate route is valid ASP.NET pattern). 4 P1 findings over-severity → P2. Overall confidence in report: 72%. | 72% | Report methodology is thorough but fact-checking against source was insufficient for some P1 claims |
| **Synthesizer** | Wave C must be split into C1 (Agent-side) and C2 (MemorySmith-side) to avoid scope creep. Accept 9 confirmed P1s + 1 P0 for Sprint 60 allocation. | 85% | Defines 6 new task requirements below |

---

## Synthesis

### What Changes NOW (Sprint 60)

#### P0 — Immediate

| Finding | Task | Wave Assignment |
|---------|:----:|:---------------:|
| MemoryScorer scoring model — dimensionally incompatible factors, unbounded output, broken thresholds | **New task** | Wave C2 (MemorySmith.Core) |

#### P1 — This Sprint

| Finding | Task | Wave Assignment |
|---------|:----:|:---------------:|
| PlaceBlockGoalDecomposer — all blocks at same coordinates | **New task** | Wave C1 (Agent-side) |
| SignalR event name drift — hardcoded strings vs DashboardHubEvents constants | **New task** | Wave C1 (Agent-side) |
| PageSummary has no Score property — unified search ranking broken | **New task** | Wave C2 (MemorySmith.Core) |
| Runtime decomposition — 6 interfaces with zero implementations (7 findings linked) | New task OR extend TSK-0293 | Wave A (Legacy Fallback Removal) |
| ChatServices.cs monolith (3,279 lines) | **New task** | Wave C2 (MemorySmith.Core) |
| Task schema violations — PascalCase records, orphan .md files | New task OR extend Wave A | Wave A (Legacy Fallback Removal) |

#### Downgraded to P2 (Defer to Sprint 61+)

| Original Finding | Recalibrated To | Rationale |
|-----------------|:---------------:|-----------|
| CreatePageTool throws | P2 | Compensated by ToolDispatcher outer catch |
| GetPageAsync no try-catch | P2 | All existing callers wrap it |
| TaskCanceledException swallowed | P2 | Only ItemRegistry vulnerable; 3 sites not 5 |
| craft/smelt stop guard | P2 | Compensated by pathfinder cancellation + timeout |
| DisableAsync no transaction | P2 | SQLite serialization prevents corruption |
| FileEventStore silent catches | P2 | Inner catch is legitimate; outer catch needs logging |
| FileMemoryStore silent catch | P3 | Standard temp-cleanup pattern |

#### Removed

| Original Finding | Reason |
|-----------------|--------|
| chatFilter.js missing | File was intentionally deleted in Sprint 56 (TSK-0279). Compensating control: inline SYSTEM_MESSAGE_PATTERNS filtering |
| Duplicate HttpPost("setup") route | ASP.NET Core supports [Consumes] disambiguation natively |

---

## Sprint 60 Wave Allocation (Post-Council)

| Wave | Original Scope | Added | Risk |
|:-----|:--------------|:------|:----:|
| **Wave A** (Legacy Fallback Removal) | TSK-0293/0284/0082/0118 | + Task schema migration (3 P1-DATA) + Runtime decomposition scoping | 🟢 Low — mechanical cleanup |
| **Wave B** (Core Pipeline Hardening) | TSK-0322/0345/0348 | + No new P1 findings (all error-handling findings downgraded to P2) | 🟢 Low |
| **Wave C1** (Audit Synthesis — Agent) | TSK-0346/0347/0349/0350 | + PlaceBlock coords fix + SignalR drift fix | 🟡 Medium — 2 new P1s |
| **Wave C2** (Audit Synthesis — MemorySmith) | *(new)* | + MemoryScorer P0 fix + PageSummary Score + ChatServices decomposition + Storage error handling | 🔴 High — MemorySmith.Core scope |
| **Wave D** (Inventory SSOT) | TSK-0302/0245 | No change | 🟢 Low |
| **Wave E** (Backlog Cleanup) | TSK-0144/0145/0134/0133/0132/0271 | No change | 🟢 Low |

---

## Dissent

1. **chatFilter.js finding:** All 6 seats except Skeptical Reviewer accepted the original claim. The Skeptical Reviewer identified that the file was intentionally deleted in Sprint 56 (TSK-0279). **Resolution:** Skeptical Reviewer's evidence is definitive — the file was deleted by design. Finding removed entirely.

2. **Duplicate route finding:** Data Model Architect and Source-Grounded Archivist confirmed the initial P1. Skeptical Reviewer demonstrated ASP.NET Core [Consumes] disambiguation is standard practice. **Resolution:** Skeptical Reviewer is correct. Finding removed.

3. **MemoryScorer severity:** Data Model Architect argued for P0 (root cause deeper than weight sum), while Retrieval Specialist and Source-Grounded Archivist accepted P1. **Resolution:** Upgraded to P0 — the unbounded scoring model and dimensionally incompatible factors make the thresholds meaningless, which is a fundamental design flaw, not just a weight miscalculation.

---

## Acceptance Criteria

1. **P0 fix:** MemoryScorer must use bounded, normalized factors with weights summing to 1.0. Thresholds must be recalibrated against actual score distribution on real data. *Verification:* Unit test asserts `Score()` output is always in [0, maxExpected] + config validation.

2. **P1 fix (PlaceBlock):** PlaceBlockGoalDecomposer must advance coordinates per iteration. *Verification:* Test with Count=5 produces 5 distinct coordinate sets.

3. **P1 fix (SignalR):** DashboardHubEvents constants must be used at all SendAsync call sites. JS dashboard legacy listener removed only after C# constants are confirmed deployed. *Verification:* Grep shows zero hardcoded SendAsync strings.

4. **P1 fix (PageSummary):** PageSummary must have a `Score` property populated with lexical match density. *Verification:* Unified search test shows pages interleaved by relevance, not always at bottom.

5. **P1 fix (Task schema):** All PascalCase task records migrated to camelCase. All orphan `.md` files removed. *Verification:* `Test-TaskRecords.ps1` passes with zero violations.

---

## Open Questions

| # | Question | Owner | Due Gate |
|---|----------|-------|----------|
| Q-1 | Should MemoryScorer extract individual factors into a `ScoringFactors` record for testability, or is inline normalization sufficient? | Data Model Architect | Before P0 implementation |
| Q-2 | Should the RRF score scaling be a MemorySmith API change (multiply by 100) or an Agent-side normalization in LocalKnowledgeResolver? | Retrieval Specialist | Before PageSummary Score implementation |
| Q-3 | Should Runtime decomposition (6 interfaces) be extracted one-at-a-time via Strangler Fig, or is a dedicated refactor sprint needed? | Synthesizer / Project Lead | Sprint 60 Wave A completion |
| Q-4 | Is the ChatServices.cs refactor scoped to Sprint 60 Wave C2, or does it need its own sprint? Depends on breaking up ~3,279 lines into 4-5 services. | Project Lead | Sprint 60 planning |
| Q-5 | Should the 6 new task records be created immediately or held until Wave assignments are confirmed? | Synthesizer | Before Sprint 60 execution |

---

## Related Documents

- Audit report: `Data/Pages/Audits/codebase-audit-20260710-10-agent-swarm.md`
- Roadmap: `MemorySmith.Agent/Data/Pages/roadmap.md` (Sprint 60)
- Previous council (Round 1): `Data/Pages/council/10-agent-codebase-audit-council-20260710.md`
- Previous audit: `Data/Pages/Audits/codebase-audit-20260710-agent10-swarm.md`
- Council skill: `.github/skills/council/SKILL.md`
- Subagent-swarm skill: `.github/skills/subagent-swarm/SKILL.md`
