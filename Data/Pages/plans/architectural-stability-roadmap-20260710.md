# Architectural Stability & Forward Roadmap (2026-07-10)

**Status:** Active roadmap. Supersedes the individual sprint plans from May 2026 (archived to `Tasks/Sprints/Archived/`).

## Strategic Theme

The project has accumulated significant forward velocity on features, chat, MCP, and search since late May. However, architectural debt — dead code, duplicated patterns, silent fallbacks, unenforced contracts, and dormant InProgress items — has grown unchecked. This roadmap rebalances toward **architectural stability and legacy cleanup** before the next major feature cycle.

## Current State (2026-07-10)

- **312 total tasks**: 133 Done (42.6%), 163 Backlog (52.2%), 9 Ready, 5 InProgress, 2 Archived
- **115 stale Backlog items** untouched since before June 10
- **5 InProgress tasks**: 1 active (TSK-0042), 4 dormant (TSK-0201, 0202, 0203, 0271)
- **CI/CD**: Browser smoke, navigation-freeze, and .NET test gates are operational
- **Security**: Immediate P0 findings from the July 5 audit have been patched (TSK-0289–0292 Done); 9 more audit-synthesis items are Ready

## Roadmap Phases

### Phase 1: Sprint — Architectural Stability & Legacy Cleanup (Current)

**Goal:** Sweep Ready audit tasks, resolve dormant InProgress items, archive stale backlog families.

Key deliverables:
- ~~All 9 audit-synthesis Ready tasks (TSK-0293–0301)~~ → 6 Done, 3 Ready remaining
- ChatServices decomposition continues (TSK-0042)
- TSK-0297: Dead code deleted from ChatServices (10 methods + 6 regex fields)
- TSK-0300: Auth self-lockout guardrail implemented
- TSK-0386: AdminController route disambiguation + rate limiting added
- TSK-0387/0364: MemoryStateMachine demotion/re-promotion + Unconsolidated guard
- TSK-0390: SourceLinksController audit logging + rate limiting added
- Dormant training/transcript tasks dispositioned (TSK-0201–0203, TSK-0271)
- Old sprint plans archived; roadmap published
- At least 3 stale Backlog families archived: screenshots, markdown expansion, chat polish

### Phase 2: Security & Reliability Hardening (Next)

**Goal:** Close the highest-priority 3000-series security and reliability gaps.

Key candidates:
- TSK-3001: Narrow antiforgery exemptions + regression tests
- TSK-3007: OAuth callback state lifecycle enforcement
- TSK-3012: Content endpoint sandboxing
- TSK-3009: Task field allowlist enforcement
- TSK-3014: GitHub Copilot streaming backpressure

Dependencies: Phase 1 must clear the Ready queue first.

### Phase 3: Architecture Decomposition & Contract Cleanup (Follow-on)

**Goal:** Complete the long-running decomposition work and close contract gaps.

Key candidates:
- TSK-0043: Decompose MaintenanceAgentServices
- TSK-0045: Split TaskDomainService into layers
- TSK-0191: Split MemoryApplicationService into retrieval/mutation/diagnostics
- TSK-0192: Modularize ChatToolCatalog into domain registries
- TSK-0053: Task JSON contract canonicalization
- TSK-0283: IChatProvider seam honesty (capabilities contract)
- TSK-0284: Code search seam (ICodeSearchService)

## Deprecated/Legacy Cleanup Targets

These patterns are prioritized for removal or refactoring in Phase 1–3:

| Pattern | Location(s) | Status |
|---------|-------------|--------|
| `MemoryIndex` dead code | `MemorySmith.Core` | TSK-0301 |
| 10 dead private methods | `ChatServices.cs` | TSK-0297 |
| `FixedTimeEquals` ×3 copies | Middleware + SecurityServices | TSK-0296 |
| TreeSitter C# key mismatch | `TreeSitterChunkingService.cs` | TSK-0293 |
| Dead search tool docs | README, wiki guides | TSK-0294 |
| `memorysmith_semantic_search` / `unified_search` | MCP + Chat tools | TSK-0271 |
| Training harness `or`-fallback anti-pattern | `harness.py` | TSK-0298 |
| Phantom `LockoutMinutes`/`MaxProgressiveLockoutMinutes` settings | Config surface | Done (TSK-0290) |

## Stale Backlog — Archive Candidates

The following families have been untouched for >6 weeks and are candidates for `Archived` status:

| Family | Tasks | Rationale |
|--------|-------|-----------|
| Screenshot capture | TSK-0057–0063, TSK-0117 | Superseded by CI browser smoke coverage |
| Markdown rendering expansion | TSK-0075–0090 | Deferred since ultra-audit (May 24); no stakeholder demand |
| Chat response quality polish | TSK-0091–0100 | Deferred until write-governance is stable |
| UI layout/responsive polish | TSK-0125, 0127, 0129, 0135, 0136 | Nice-to-have; no traction since creation |
| Model/profile expansion | TSK-0012, TSK-0178 | No provider demand beyond current GitHub+Ollama |
| Agent write-approval UX | TSK-0014–0025 (subset) | Write governance is functional; many sub-items are deferred enhancements |

## Risks

- **Backlog bloat:** 163 Backlog items is too many for effective prioritization. Archiving stale families is essential.
- **Dormant InProgress:** TSK-0201–0203 block downstream training/transcript work. Clear disposition is needed.
- **Scope creep:** The 3000-series tasks (14 new items) arrived this week. Must resist adding them all to one sprint.

## Confidence

- Overall roadmap confidence: 80%
- Phase 1 confidence: 82%
- Phase 2 confidence: 76%
- Phase 3 confidence: 72%
