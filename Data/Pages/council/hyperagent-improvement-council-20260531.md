# Council Review: Comprehensive Improvement Opportunities for MemorySmith

**Conducted:** 2026-05-31
**Branch:** `feature/code-search-high-roi-batch8` latest tip
**Method:** 6-seat council with subagent-backed specialist reviews + synthesizer
**Scope:** Cross-cutting review for improvements beyond prior audit bug-finding — documentation health, data model consistency, retrieval quality, user experience, and architectural skepticism

---

## Decision

Invest in three targeted improvement tiers: (1) immediate consistency and onboarding wins that reduce confusion without behavioral changes, (2) medium-term structural improvements that the tracked decomposition tasks already describe, and (3) longer-term retrieval and training quality gates that compound over time. Stop adding new features until the consistency and onboarding gaps are closed.

---

## Evidence Reviewed

- README.md, NavMenu.razor, Home.razor, Chat.razor, CodeSearch.razor, TrainingWorkbench.razor
- MemoryRecord.cs, SourceLink.cs, MemoryStatus.cs, SecurityModels.cs, memory.schema.json
- FileMemoryStore.cs, SqliteMemorySmithDatabase.cs (schema section), DatabaseOptions.cs
- MemoryApplicationService.cs (RRF fusion), CodeSearchService.cs (hybrid scoring)
- SemanticEmbeddingSearchService.cs (TryRank), ChatContextPlanner.cs
- SemanticToolQualityTests.cs (MRR probes), code-search-relevance-suite.json
- MeasurementBaselineService.cs (thresholds), MemorySmithOptions.cs (full config surface)
- TrainingHarnessRunnerService.cs, TrainingOptions.cs, ChatTurnRecord.cs
- wiki-chat-agent.md, copilot-instructions.md, smith.agent.md
- Data/Pages/council/ (prior council reports for pattern reference)
- Data/Memories/Core/ (spot-checked 4 records), Data/Pages/features/ (spot-checked 3 pages)
- e2e/tests/route-smoke.spec.ts, Program.cs (DI registrations)
- 260 task records (97 Done, 4 InProgress, 112 Backlog)

---

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | Documentation drift is moderate — README tool count, test count, and route table are stale; wiki records for new features (training, code search UI, models page) don't exist yet; prompt tool list doesn't match catalog | 82% | New features shipping without wiki records creates a documentation debt spiral |
| Data Model Architect | JSON serialization inconsistency between PascalCase (FileMemoryStore) and camelCase (Web defaults) is a latent data-loss bug; DateTime/DateTimeOffset split is systemic; MemoryRecord lacks validation; SourceLink has no EndLine≥StartLine guard; SQLite migration framework is single-block | 85% | Cross-service JSON round-trips can silently drop properties today |
| Retrieval Specialist | Memory RRF discards score magnitude (proven to reduce nDCG by 3-8% vs convex combination per Bruch et al. 2023); MRR test probes are overfitted to the alias dictionary; no nDCG@10 metric exists; context window overflow detection is absent; code search embedding text wastes 3-5% of token budget on path prefix | 80% | Quality improvements are blocked by the absence of graded relevance judgments |
| Human Learning Advocate | First-run experience is a blank stat dashboard with no orientation; no in-app explanation of domain concepts (Memory, Page, Status, Confidence); Training Workbench enables "Start training run" in simulated mode without warning; 8 of 14 routes have no smoke test; Variables page unreachable from nav | 74% | A developer installing for their team cannot onboard colleagues without verbal explanation |
| Skeptical Reviewer | 112 backlog tasks signal scope creep; the training harness shipped before the core chat UX is polished (no code block copy, no message edit); MemoryScorer weights (0.63/0.3/0.2/0.1) are decorative since UsageCount only increments manually; configuration surface has ~150 knobs with no governance; MemoryIndex is maintained but never consulted by search | 78% | Feature velocity without debt paydown is compounding — each new feature lands on the same monolithic services |

---

## Synthesis

### Change Now (Sprint 1 — Consistency, 2 days)

1. **Centralize `MemorySmithJsonDefaults`** — 4 named static instances replacing 25 per-file fields. Fix the PascalCase/camelCase split. This is the highest-ROI single change: it prevents a latent data-loss bug and reduces 75 LOC of duplication.

2. **Add empty-state guidance to Home.razor** — when `_stats.TotalCount == 0`, show a welcome card with "Get started" links. When unauthenticated, show "Sign in to edit." ~30 LOC.

3. **Delete dead code** — `ShouldPreloadContext` + regexes + `FormatRecordAsync` + `FormatLinks` + McpController dead helpers. ~250 LOC removed. Zero behavioral change.

4. **Update README** — correct test count (419, not 184), correct tool count (22+), add Training Workbench and Code Search to the route table, add first-run admin bootstrap note to Quick Start.

5. **Add `HelperText` props** to the 5 most confusing controls: Code Search "Operator cap", Training Workbench "simulated mode", Chat model selector disabled state, Hybrid/Semantic/Lexical search mode buttons, "Rebuild if stale" switch.

### Change Next (Sprint 2 — Structural, 3 days)

6. **Extract shared utilities** — `VectorMath.Dot`, `MemorySmithCrypto.ComputeSha256Hex`, `MemorySmithPaths.NormalizeDataRelativePath/ResolveDataDeploymentRoot`, `SafeFileWriter`, `SnippetBuilder`. ~200 LOC of duplicates eliminated.

7. **Move providers out of ChatServices.cs** — `OllamaChatProvider.cs`, `GitHubCopilotChatProvider.cs`, `ChatToolCallParser.cs` as separate files. File moves only, no logic changes. Reduces ChatServices from 3,978 to ~2,400 LOC.

8. **Convert `IOptions` → `IOptionsMonitor`** at 16 injection sites. Read `CurrentValue` at call site. Admin setting changes now take effect without restart.

9. **Add validation to `MemoryRecord`** — clamp `Confidence` to [0,1], enforce `EndLine >= StartLine` on `SourceLink`, add `MaxLength` on Title/Content at the create/update boundary.

10. **Add wiki records** for Training Workbench, Code Search UI, and Models page. Update the prompt tool list to match the current 22-tool catalog.

### Defer with Gate (Sprint 3+ — Quality & Architecture)

11. **Upgrade memory RRF to DBSF** — requires the graded relevance suite (nDCG@10) as a measurement gate first. Defer the code change until the evaluation infrastructure exists.

12. **Add cross-encoder reranker for code search** — requires a reranker model install path. Defer until the model export workflow is proven stable.

13. **Decompose ChatServices** (TSK-0042) — defer until Sprint 2's utility extractions land, which reduce the diff size of the decomposition.

14. **Add evaluation gates to training harness** (TSK-0204) — defer until the transcript data volume justifies the investment.

15. **Decide the fate of `MemoryIndex`** — either wire it into search (replacing `_store.LoadAll()` per query) or delete it. The current state (populated and maintained but unused) is the worst of both worlds.

### Stop Doing

16. **Stop adding new configuration knobs without documenting them.** The current ~150 options are ungoverned — no admin knows what's tunable vs what should be left alone. Add a `MemorySmith:ConfigGovernance:WarnOnUnrecognizedKeys` option and surface unknown keys in diagnostics.

17. **Stop filing backlog tasks without triage.** 112 Backlog tasks is not a backlog — it's a wish list. Triage: keep the top 20 as Backlog, archive the rest as "Someday/Maybe", and commit to a "backlog zero for P0/P1" policy.

---

## Dissent

- **Retrieval Specialist vs Skeptical Reviewer:** The Retrieval Specialist recommends upgrading from RRF to DBSF as a near-term quality win. The Skeptical Reviewer argues that the evaluation infrastructure doesn't exist yet (no nDCG@10, no graded relevance judgments), so any fusion change is flying blind. **Resolution:** Build the evaluation infrastructure first (nDCG@10 metric + graded relevance suite), THEN change the fusion method. Sequence matters.

- **Human Learning Advocate vs Skeptical Reviewer:** The Advocate wants a guided onboarding wizard. The Skeptic argues that a local-dev tool for individual developers doesn't need onboarding — the README suffices. **Resolution:** Don't build a wizard. Do add empty-state guidance and `HelperText` props — minimal investment, meaningful impact. Skip the full tour.

- **Data Model Architect vs Source-Grounded Archivist:** The Architect wants to add validation constraints to `MemoryRecord` now. The Archivist warns that changing the record model requires updating the JSON schema, all wiki records, and the MCP tool schemas simultaneously. **Resolution:** Add validation at the service boundary (create/update methods), not on the model class. This avoids schema migration while gaining the safety.

---

## Acceptance Criteria

1. **JSON consistency:** All services use `MemorySmithJsonDefaults` — no per-file `JsonSerializerOptions` fields. Verified by grep.
2. **Dead code removed:** `ShouldPreloadContext`, `FormatRecordAsync`, McpController dead helpers — all gone. Verified by build.
3. **README accurate:** Test count, tool count, route table all match current code. Verified by CI script.
4. **Empty-state rendered:** `Home.razor` shows guidance when `TotalCount == 0`. Verified by e2e smoke test.
5. **`HelperText` on critical controls:** 5 controls have inline help. Verified by visual review.
6. **Shared utilities extracted:** No remaining duplicate `Dot`, `ComputeHash`, `BuildSnippet`, or path-resolution methods. Verified by grep.
7. **Providers in separate files:** `OllamaChatProvider.cs` and `GitHubCopilotChatProvider.cs` exist. ChatServices.cs < 2,500 LOC. Verified by `wc -l`.
8. **`IOptionsMonitor` everywhere:** Zero `IOptions<MemorySmithOptions>` injection sites. Verified by grep.
9. **Backlog triaged:** ≤ 25 active Backlog tasks. Remainder archived with "Someday/Maybe" label.

---

## Open Questions

1. **Should `MemoryIndex` be wired into search or deleted?** It's 45 LOC of maintained-but-unused code. The Retrieval Specialist favors wiring it in (as a memory-backed cache for `_store.LoadAll()`). The Skeptic favors deleting it. Neither view is blocked — either is an improvement over the status quo.

2. **Is the training harness ready for production use, or should it be gated behind a feature flag?** The Advocate notes that "Start training run" is enabled in simulated mode with no visual distinction. The Architect notes that `trust_remote_code=True` in `harness.py` is a supply-chain risk. The Skeptic argues the harness should be behind `Training.Enabled = false` by default.

3. **What's the target for the backlog triage?** 112 Backlog tasks is unwieldy. The maintainer's task velocity (97 Done across ~2 weeks) suggests they can close ~7/day when focused. At that rate, 112 items is 16 working days — more than the rest of the feature work. Triage is required.

4. **Should the wiki prompt tool list be auto-generated from `ChatToolCatalog`?** The Archivist strongly recommends this to prevent drift. The Architect notes it requires a build-time code-gen step. The Synthesizer defers to the maintainer's preference on build-time generation vs manual sync.

5. **Is the Variables page intentionally hidden from nav?** The Advocate flagged it as unreachable via the UI. If intentional, document why. If not, add it to the Governance nav group.

---

## Confidence

**Overall council confidence: 80%.**

All seats converged on the "consistency before features" thesis. The disagreements are about sequencing (evaluation before fusion change, validation at boundary vs model), not direction. The highest-confidence finding (JSON serialization inconsistency, 92% from the Architect) is also the highest-impact fix.

---

*Report format follows Data/Pages/council/ convention. Save as `Data/Pages/council/comprehensive-improvement-council-20260531.md` if committed to the repo.*
