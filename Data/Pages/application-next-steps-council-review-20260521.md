# Council Review: MemorySmith Application Next Steps

Status: Begun after PR #13 merge on 2026-05-21  
Scope: Application roadmap, search/retrieval quality, governance UX, chat/agent behavior, schema gates  
Decision level: high impact; implementation is gated by acceptance criteria below

## Decision

After PR #13, MemorySmith should prioritize measurement-backed governance UX and retrieval safety before changing ranking formulas, persisted memory schema, page chunking, or Agent write approval semantics.

## Evidence Reviewed

- [README](../../README.md) for the current single-host app, route map, search modes, chat behavior, MCP tools, and configuration.
- [AI Memory Suite Implementation Plan](ai-memory-suite-implementation-plan.md) for the phased governance roadmap and explicit still-gated work.
- [Core Memory System Improvements RFC](temp-plan.md) for convention-first, validation-first schema discipline.
- [Search and Chat](search-and-chat.md) for current search modes, chat tool behavior, retrieval habits, and council triggers.
- [Council Workflow](llm-council.md) and `.github/skills/llm-council-review/SKILL.md` for council method and completion checks.
- [PR 13 Search Diagnostics Follow-Up](pr13-search-diagnostics-council-review-20260521.md) for the just-merged diagnostics, hot-path hardening, and no-ranking-change constraint.
- `MemorySmith.Core/Docs/Plans/SemantingSearch.md` for ONNX/vector-search aspirations and current exact in-memory cosine implementation notes.
- `MemorySmith.Core/Docs/Plans/ChatCapabilityImprovements_20260518.md` for the chat/tool-call gap analysis and context-planner direction.
- `MemorySmith.Core/Docs/Plans/MemorySmith_FinalRefactorDesign_20260507.md` for the active single-host simplification architecture.
- `MemorySmith.Core/Docs/Plans/MemorySystemSchemaImprovements_20260519.md` as a useful but now-constrained schema-forward proposal.
- GitHub PR #13 final state: rebase-merged, review threads 13 resolved / 0 unresolved, CI `build-and-test` success, latest merged commit on master `700b968`.

## Round 1: Independent Seat Findings

| Seat | Recommendation | Confidence | Blocking concern |
| --- | --- | ---: | --- |
| Source-Grounded Archivist | Treat the merged governance diagnostics as the new baseline, then reconcile older schema/vector/chat plans against the newer convention-first roadmap before implementing more architecture. | 0.88 | Several docs are intentionally historical or aspirational; implementing them literally would contradict newer gates. |
| Data Model Architect | Defer persisted schema fields until policy diagnostics, UI assistance, and usage metrics prove tags cannot carry the concept safely. | 0.84 | Schema promotion without migration tests and legacy-read compatibility would create avoidable churn in the live wiki fixture. |
| Retrieval Specialist | Build a measured search quality baseline next: curated memory/page queries, MRR or Recall@5, stale-warning rates, and ONNX-vs-fallback metadata. | 0.90 | "Best possible search" cannot be judged from local code shape alone; it needs repeatable probes over the project wiki. |
| Human Learning Advocate | Make governance visible in the workbench before adding more hidden rules: tag chips, policy editing, diagnostics before save, and clear warning surfaces in chat references. | 0.86 | Warning-first governance fails if users only discover warnings after a tool or agent has already used bad context. |
| Skeptical Reviewer | Resist adding vector indexes, page chunking, native provider tools, or strict Agent approval rules in one large branch. Spike and measure the riskier surfaces separately. | 0.82 | The product is now broad enough that a sweeping "AI suite" branch could break trust through token bloat, stale citations, or UX friction. |
| Synthesizer | Start with measurement plus governance UX, then complete retrieval-warning propagation, then harden chat/tool behavior, then advance Agent write governance and schema/page promotions only through council gates. | 0.87 | Each phase needs a narrow validation gate and rollback note before implementation starts. |

## Round 2: Adversarial Review And Dissent

### Retrieval Versus Data Model

The Retrieval Specialist wants faster progress on page chunking and richer vector search because page content and long-form docs are clearly part of the knowledge base. The Data Model Architect dissents: page frontmatter, heading chunks, and embeddings introduce durable data contracts. The synthesis is to measure page length, heading density, page-query misses, and chat truncation first. Chunking becomes justified only when the corpus or query probes prove whole-page search is insufficient.

### Chat Native Tools Versus Simplicity

The Chat Capability plan makes a strong case that prompt-mediated JSON tool calls are brittle. The Skeptical Reviewer agrees, but rejects a full provider-tool rewrite as the immediate next branch. The synthesis is a provider-capability spike with tests: define the shared registry and capability metadata, prove one provider path or document why it cannot yet be made native, and keep the app-intercept fallback.

### Agent Governance Versus Human Workbench

Agent write governance is strategically important, but the Human Learning Advocate argues it should not outrun the UI that teaches humans the same policy. The synthesis is to implement policy visibility first: if tag/source/relation/staleness diagnostics are not understandable in `/memories`, they will not be trustworthy inside Agent approval flows.

### Schema Ambition Versus Current Plan

`MemorySystemSchemaImprovements_20260519.md` remains valuable as a catalog of eventual schema candidates: typed relations, constraints, priority, validity. It is not the controlling plan. The controlling path is convention-first with diagnostics and promotion gates from `temp-plan.md` and `ai-memory-suite-implementation-plan.md`.

## Round 3: Recommended Roadmap

### Phase 0: Close The PR 13 Baseline

Status: complete.

- PR #13 merged by rebase.
- Copilot review threads are resolved.
- CI is green.
- Warning-first maintenance, tag policy diagnostics, context-pack diagnostics, duplicate-tolerant hot paths, and benchmark smoke coverage are now the baseline.

### Phase 1: Measurement Baseline Branch

Goal: make future search, governance, and page decisions measurable.

Recommended branch: `feature/search-governance-measurement-baseline`.

Implementation path: add a read-only `MeasurementBaselineService` and expose it from the admin diagnostics surface at `/api/diagnostics/measurement-baseline`. This keeps Phase 1 out of ranking, schema, page chunking, and Agent write behavior while still giving future branches concrete data.

Falsifiable implementation hypothesis: the existing search, page, diagnostics, tag policy, source-link, and semantic-provider services already expose enough information to compute a useful baseline without mutating `Data/Memories` or `Data/Pages`.

Cheapest discriminating validation: focused NUnit tests should run the baseline against copied project wiki fixtures and prove the live wiki snapshots are unchanged after measurement.

Tasks:

- Add a repeatable search-quality fixture over current project wiki queries.
- Report MRR, Recall@5, top-hit correctness, and stale/diagnostic warning rates for lexical, semantic, hybrid, unified, and context-pack paths where practical.
- Record ONNX active versus token fallback in semantic result metadata or diagnostic output.
- Add page corpus metrics: page count, size distribution, heading count, and longest pages.
- Add tag policy health metrics: unknown tags, blocked tags, alias candidates, duplicate namespace warnings, and broad/low-value tag counts.
- Add source-link health metrics: unresolved variables, missing files, invalid ranges, disallowed roots.

Acceptance gates:

- Metrics run locally without mutating `Data/Memories` or `Data/Pages`.
- Tests copy live wiki fixtures before mutation.
- Full NUnit suite passes.
- Benchmark smoke still passes.
- Metrics are documented in the wiki with explicit thresholds for later promotion decisions.

Phase 1 baseline thresholds now carried by the measurement contract:

| Threshold | Value | Use |
| --- | ---: | --- |
| Search promotion minimum MRR | 0.75 | Minimum mean reciprocal rank before search behavior is treated as healthy enough for promotion decisions. |
| Search promotion minimum Recall@5 | 0.80 | Minimum top-five recall before ranking/chunking changes are considered successful. |
| Maximum diagnostic warning rate | 0.20 | Search/context output warning rate above this needs review before expanding warning surfaces. |
| Maximum broken source-link rate | 0.05 | Source-link health above this failure rate blocks stronger source-backed Agent/write behavior. |
| Page chunking token threshold | 1500 estimated tokens | Pages above this size count toward the chunking pressure metric. |
| Page chunking long-page ratio threshold | 0.20 | If more than 20% of pages exceed the token threshold, page chunking gets a fresh council review. |

### Phase 2: Governance Workbench And Tag Manager

Goal: make warning-first governance usable by humans.

Recommended branch: `feature/governance-workbench-tag-manager`.

Tasks:

- Replace comma-only tag editing with keyboard-accessible chips and autocomplete.
- Show tag/source/relation/staleness diagnostics before save.
- Add a Tag Manager surface for namespaces, allowlist, blocklist, aliases, usage counts, policy mode, and suggested merges.
- Add lexical suggestions for casing variants, near duplicates, low-value tags, broad tags, and namespace mistakes.
- Keep suggestions approval-based; no automatic tag rewrites.

Acceptance gates:

- Existing records remain editable.
- Invalid policy input warns instead of crashing.
- Unknown tags can be observed, warned, or blocked by policy mode.
- UI tests or component-level coverage verify chips, diagnostics, and policy edits.
- Docs explain how humans should author tags and interpret warnings.

Implementation result, 2026-05-22:

- Added `TagGovernanceService` and `/api/governance/*` endpoints for tag policy snapshots, suggestions, draft diagnostics, and admin policy saves.
- Replaced memory editor comma-only tags with keyboard-addable chips, autocomplete suggestions, and a pre-save draft diagnostics panel for tag/source/relation/staleness checks.
- Added `/tags` Tag Manager for namespace, allowlist, blocklist, alias, usage, policy mode, and read-only suggestion review.
- Preserved approval-only cleanup: suggestions do not rewrite memory records or tag policy automatically.
- Added authoring and warning guidance in `Data/Pages/tag-governance-workbench-20260522.md`.
- Local validation passed: `TagGovernanceTests` 7/7, `dotnet build MemorySmith.App/MemorySmith.App.csproj -v minimal`, `dotnet build MemorySmith.slnx -v minimal`, full suite 241/241, and benchmark smoke across lexical metadata diagnostics, semantic, hybrid, chat-context, and context-pack paths.
- Council review approved merge in `Data/Pages/phase2-governance-workbench-council-review-20260522.md`, with non-blocking follow-ups for blockUnknown remediation and Phase 3 diagnostics envelope size.

### Phase 3: Retrieval Warning Propagation

Goal: carry diagnostics and provenance through every retrieval surface without changing ranking.

Tasks:

- Add diagnostics to lexical, semantic, hybrid, unified, page, and context-pack outputs consistently.
- Add explicit semantic provider metadata: ONNX embedding ranker, token fallback, model/vocabulary availability.
- Render stale/source/tag/relation warnings in chat trace and reference drawers.
- Add versioned structured tool envelopes behind an opt-in or format flag before changing defaults.
- Keep Markdown output for humans and JSON output for agents.

Acceptance gates:

- A stale or broken-source record remains retrievable but visibly warned.
- Existing clients and tests continue to parse current tool results unless they opt into the new envelope.
- Search relevance probes do not regress.
- No ranking, RRF, temporal decay, or schema behavior changes occur in this phase.

### Phase 4: Chat Context Planner And Native Tool Spike

Goal: reduce context bloat and make tool use more reliable.

Tasks:

- Extract a shared tool registry from the chat catalog and MCP controller if duplication remains after PR #13.
- Add provider capability metadata for native tools, structured responses, images, context-window reporting, and streaming behavior.
- Prototype native tool-call registration for the GitHub Copilot provider or document the SDK blocker.
- Keep deterministic intent intercepts and JSON-text tool calls as fallback.
- Add a context planner that preloads less by default and chooses memory/page/tool context based on intent and budget.

Acceptance gates:

- Smoke prompt with no context still receives no accidental wiki preload.
- Retrieval prompt reliably triggers tool/intercept context.
- Trace shows why context was loaded or skipped.
- Tool loops remain bounded by configured limits.
- Provider-specific native tool support has tests or documented non-support.

### Phase 5: Agent Write Governance

Goal: make Agent proposals auditable before expanding write power.

Tasks:

- Expand proposal models with evidence IDs, page citations, source links, rationale, diagnostics, risk level, suggested relations, and diff previews.
- Prevalidate proposed writes using tag/source/relation/staleness diagnostics.
- Add approval checklist UI and reviewer notes.
- Apply stricter approval rules for Core, strict-rule, priority-critical, supersession, expired, or source-link-changing proposals.
- Log proposal outcomes for later quality metrics.

Acceptance gates:

- Proposal without evidence cannot be fast-approved into Core.
- Broken source links and malformed tags block or warn according to policy.
- Audit/history records show who approved what and why.
- Tests cover low-risk and high-risk proposal paths.

### Phase 6: Page Metadata, Chunking, And Embeddings

Goal: make long-form pages first-class retrieval sources only when the corpus justifies it.

Tasks:

- Add optional YAML frontmatter only after choosing and testing a parser.
- Add page diagnostics parallel to memory diagnostics.
- Implement heading-based chunks behind a feature flag.
- Preserve slug, heading path, section ID, and source line range where feasible.
- Add page embeddings only after memory/page query metrics justify the complexity.

Acceptance gates:

- Chunking improves or preserves Recall@5 and MRR on page queries.
- Small pages do not become harder to find.
- Chat citations show page section provenance.
- Rebuilds are deterministic and fast enough for local use.

### Phase 7: Schema Promotion

Goal: promote only proven conventions.

Candidate order:

1. `ReviewAfter` and `ValidUntil` date fields.
2. `Kind` enum.
3. `Priority` enum.
4. Typed `Relations`.

Acceptance gates:

- Convention usage crosses documented thresholds.
- Validator error rate is low after UI assistance.
- Migration runs against copied fixtures.
- Old-form and new-form records both read correctly during transition.
- Schema, model, UI, docs, MCP/chat output, and tests are updated together.

## Round 4: Implementation Order Recommendation

The next concrete branch should be Phase 1, not Phase 2, because measurement gives the project a way to decide whether later UI, search, and schema work actually improves the system.

Recommended immediate work package:

1. Add search/governance/page/source-link metric services or test-only probes.
2. Add a small report writer under diagnostics or benchmark smoke output.
3. Add wiki documentation explaining current baseline thresholds.
4. Run the existing full suite and benchmark smoke.

Only after that should the Tag Manager UI branch begin. This keeps the next user-facing surface grounded in known tag drift and source-link health rather than imagined examples.

## Phase 1 Implementation Result

Status: implemented on `feature/search-governance-measurement-baseline`.

Implemented now:

- `MeasurementBaselineService` computes a read-only snapshot with search quality, semantic provider mode, page corpus metrics, tag policy health, source-link health, and promotion thresholds.
- `/api/diagnostics/measurement-baseline` exposes the snapshot behind the same Admin policy as operational diagnostics.
- Search quality reports MRR, Recall@5, top-hit accuracy, diagnostic warning rate, average result count, and per-probe ranks for lexical, semantic, hybrid, unified, and context-pack paths.
- Page metrics report page count, character buckets, heading counts, longest pages, and pages above the chunking token threshold.
- Tag metrics report unknown plain tags, blocked tags, alias candidates, duplicate namespace warnings, broad tags, low-value tag use, and diagnostic counts by code.
- Source-link metrics report total links, records with links, missing variables, unresolved paths, missing files, disallowed roots, invalid ranges, out-of-range lines, warning count, and broken-link rate.

Still intentionally deferred:

- Ranking, RRF, temporal decay, or persisted schema changes.
- Page frontmatter, chunking, or page embeddings.
- Tag Manager UI and policy editing workflows.
- Native chat tool calls and Agent proposal governance changes.

Validation plan for PR readiness:

- Focused measurement tests and endpoint contract test.
- Full `MemorySmith.Tests` suite.
- `dotnet build MemorySmith.slnx -v minimal`.
- Benchmark smoke: `dotnet run -c Release --project MemorySmith.Benchmarks -- --smoke`.

Validation result:

- Focused measurement and endpoint tests: 3/3 passed.
- Full NUnit suite: 234/234 passed.
- Solution build: passed.
- Benchmark smoke: passed, returning 10 results/records for lexical, lexical metadata diagnostics, semantic, hybrid, chat-context, and context-pack paths.

## Risks And Mitigations

| Risk | Mitigation |
| --- | --- |
| Measurement becomes another dashboard with no decisions. | Define thresholds and use them as gates for chunking, schema, and ranking changes. |
| Tag Manager UI adds friction. | Start with observe/warn modes and fast keyboard editing; do not block ordinary edits until policy quality is proven. |
| Diagnostics create token bloat. | Keep warning/error caps and structured summaries; benchmark context-pack and chat prompts. |
| Native tool-call work becomes provider-specific sprawl. | Add capability metadata and keep fallback intercepts; isolate provider-specific adapters. |
| Agent governance becomes too strict for local use. | Use risk tiers: low-risk Working proposals stay fast; Core/rule/source-link changes get stronger review. |
| Schema promotion happens prematurely. | Require usage thresholds, tests, migration, rollback, and council review. |

## Open Questions

- What exact curated query set should become the canonical search-quality fixture?
- Should search-quality metrics live in `MemorySmith.Benchmarks`, `MemorySmith.Tests`, or a small diagnostics service surfaced through `/health`?
- Should Tag Manager policy edits require Admin only, or Editor plus Admin for block mode?
- Which provider should be the first native tool-call spike: GitHub Copilot SDK or Ollama tools?
- Should page frontmatter use YamlDotNet, and should frontmatter be accepted before chunking exists?
- What threshold should trigger page chunking in this repo: page size, query miss rate, chat truncation, or all three?

## Confidence

Overall confidence: 87%.

The roadmap is strongly supported by the latest project wiki and the just-merged PR #13 evidence. Confidence is not higher because several next phases need measurements that do not exist yet: curated search quality baselines, page miss rates, tag drift trends, and Agent proposal quality metrics.
