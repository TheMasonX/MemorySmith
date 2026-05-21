# Implementation Plan: AI Memory Suite

Status: Council-reviewed implementation plan, 2026-05-20

Scope: Planning only. No code implementation is implied by this page.

Related: [Core Memory System Improvements RFC](temp-plan.md), [Council Workflow](llm-council.md), [Search and Chat](search-and-chat.md), [Deep Research Intake Notes](deep-research-intake-20260520.md)

## Decision

Build MemorySmith into a governed AI memory suite by adding a custom per-wiki tag policy, warning-first staleness and maintenance behavior, source-backed Agent write proposals, structured tool outputs, and evidence-gated schema/page retrieval upgrades. The plan rejects both extremes: unvalidated free-form conventions and immediate broad schema expansion.

Overall confidence: 86%. The main reason this confidence is not higher is that MemorySmith already has automatic maintenance paths that can move records to Deprecated based on score, which conflicts with the warning-first design goal unless it is audited and changed deliberately.

## Implementation Status

2026-05-20 first feature slice: warning-first governance foundations are implemented on the feature branch, while later ranking, schema, chunking, and Agent-write behavior changes remain gated.

Implemented now:

- `MemorySmith:Maintenance:AutomaticDeprecationEnabled` defaults to `false`; low-score records generate `DeprecationRecommended` events instead of being silently moved to Deprecated.
- `Data/Policies/tag-policy.json` defines the first per-wiki tag policy with canonical namespaces, plain-tag blocklist, and aliases.
- `MemoryDiagnosticsService` emits tag, source-link, relationship, staleness, and maintenance diagnostics without mutating memory records.
- `/memories` shows diagnostic chips in result rows and a diagnostics panel for the selected memory.
- Context-pack JSON keeps the existing `records` and `warnings` shape while adding `schemaVersion: memorysmith.context-pack.v1` and per-record diagnostics.
- NUnit coverage now locks warning-first maintenance, tag/source/relation/staleness diagnostics, and additive context-pack metadata.

Still gated:

- search ranking, RRF, or temporal decay changes;
- persisted `MemoryRecord` schema promotion;
- page frontmatter/chunking/embedding work;
- default MCP envelope changes beyond additive context-pack metadata;
- stricter Agent write approval behavior beyond the existing proposal workflow.

## User Direction Incorporated

- There are no existing clients outside this application itself, so internal contracts can move when long-term usefulness justifies it.
- Local app clients still count: `/memories`, `/pages`, `/chat`, `/mcp`, tests, fixtures, chat prompts, and project wiki files need coordinated migration.
- The tag schema should be custom per project/wiki instance, not hard-coded globally.
- The UI should allow tag allowlists and blocklists.
- Lexical analysis should help suggest blocklist entries, aliases, and merges, but it should not automatically delete or rewrite tags without human approval.
- The desired endpoint is a capable AI memory suite for agent memory, search, human-facing wiki docs, and chat-assisted learning.

## Evidence Reviewed

- [Core Memory System Improvements RFC](temp-plan.md) - convention-first and evidence-gated direction.
- [Deep Research Intake Notes](deep-research-intake-20260520.md) and [temp-deep-research-response](temp-deep-research-response.md) - external research synthesis on tag governance, staleness, JSON tool outputs, chunking, and council workflows.
- [Search and Chat](search-and-chat.md) - current human/agent retrieval guidance.
- [Council Workflow](llm-council.md) and `.github/skills/llm-council-review/SKILL.md` - required council method.
- [Schemas/memory.schema.json](../../Schemas/memory.schema.json) and [MemoryRecord.cs](../../MemorySmith.Core/Models/MemoryRecord.cs) - current flat record model.
- [MemoryApplicationService.cs](../../MemorySmith.App/Services/MemoryApplicationService.cs) - current search, validation, context pack, and CRUD behavior.
- [MemoryMaintenanceTasks.cs](../../MemorySmith.App/Services/MemoryMaintenanceTasks.cs), [MemoryMaintenanceService.cs](../../MemorySmith.App/Services/MemoryMaintenanceService.cs), and [MemoryStateMachine.cs](../../MemorySmith.Core/StateMachine/MemoryStateMachine.cs) - current automatic status transitions and deprecation behavior.
- [MemoryScorer.cs](../../MemorySmith.Core/StateMachine/MemoryScorer.cs) - current scoring used by maintenance.
- [MemoryViewer.razor](../../MemorySmith.App/Components/Pages/MemoryViewer.razor) - current free-text tag editing and memory workbench.
- [PageService.cs](../../MemorySmith.App/Services/PageService.cs) - current whole-page search with no chunking/frontmatter metadata.
- [McpController.cs](../../MemorySmith.App/Controllers/McpController.cs) - current MCP tool surface and mixed text/JSON formatting.
- [ChatServices.cs](../../MemorySmith.App/Services/ChatServices.cs) - current Agent memory proposal shape.
- [SemanticToolQualityTests.cs](../../MemorySmith.Tests/SemanticToolQualityTests.cs) and [SearchBenchmarkTests.cs](../../MemorySmith.Tests/SearchBenchmarkTests.cs) - current search quality and tool probes.
- MCP context pack for `memory-system-rfc-council-review-20260520`, search roadmap, context pack, chat provider, markdown pages, source links, UI state, and validation baseline memories.

## Council Findings

| Seat | Recommendation | Confidence | Blocking Concern |
| --- | --- | ---: | --- |
| Source-Grounded Archivist | Accept convention-first only if every convention is backed by explicit docs, validators, and source-linked evidence. | 76% | Current plan must list all internal clients and output contracts before changing tool shapes. |
| Data Model Architect | Use a hybrid path: convention first, then schema promotion after thresholds prove machine parsing is needed. | 82% | Promotion gates must be quantified before Phase 1 ends. |
| Retrieval Specialist | Prioritize staleness warnings, tool metadata, and search probes before ranking or chunking changes. | 80% | Do not alter ranking or RRF without local measurements and rollback tests. |
| Human Learning Advocate | Make tag rules, staleness, source evidence, and Agent proposals visible in the UI before relying on them. | 86% | Free-text tag entry will undermine conventions unless chips/autocomplete/diagnostics are added. |
| Skeptical Reviewer | Conditional approval only after resolving silent deprecation, tag drift, source-link validation, and ranking-rule mismatches. | 71% | Current maintenance can silently move records to Deprecated by score, contradicting warning-first policy. |
| Synthesizer | Proceed with a phased AI memory suite plan: governance first, behavior changes later. | 84% | Phase gates must be enforced, not treated as aspirational prose. |

## Ten Deliberation Rounds

### Round 1: Scope And Freedom To Move

Because MemorySmith has no external clients outside the app, the plan can change internal APIs, JSON shapes, schemas, prompts, and UI workflows when doing so improves long-term usefulness. That freedom should not become casual churn. The active app, tests, Data/Memories fixture, Data/Pages wiki, chat prompt, MCP endpoint, and Blazor UI are all internal consumers that need migration as a unit.

Decision: allow broad internal changes, but version or gate every behavior that affects search ranking, status transitions, tool output shape, or Agent writes.

### Round 2: Current Maintenance Contradiction

The most important finding is that warning-first staleness is not just future policy. Current maintenance can already mutate memory status automatically:

- `MemoryMaintenanceService` runs triage soon after startup and then on an interval when maintenance is enabled.
- `MemoryStateMachine` moves records below the deprecation threshold to Deprecated.
- `MemoryMaintenanceTasks.RunConsolidationAsync` also calls `DeprecateObsoleteRecords`, which changes status to Deprecated when `MemoryScorer.Score(record) < 0.2`.
- `MemoryScorer` combines usage, confidence, references, and recency. It does not understand `kind:rule`, `expires`, `review-after`, source links, or human review status.

Decision: Phase 0 must audit and redesign maintenance before building staleness semantics. The suite should not silently bury important old Core knowledge.

### Round 3: Tag Schema

Tags should remain human-readable, but they need a policy layer. The proper schema is not one global taxonomy. It is a per-wiki tag policy with a small reserved core plus custom topic vocabularies.

Recommended starting schema:

| Category | Form | Values Or Rule | Purpose |
| --- | --- | --- | --- |
| Plain topic tags | `lower-kebab-case` | Custom per wiki, allowlisted or suggestion-only | Domain discovery: `search`, `chat`, `mcp`, `pages`, `ui`, `storage`, `tests`. |
| Kind | `kind:<value>` | `fact`, `rule`, `procedure`, `decision`, `plan`, `research`, `guide`, `concept`, `issue`, `example`, `index` | Tells agents and humans what kind of knowledge this is. |
| Priority | `priority:<value>` | `critical`, `high`, `normal`, `low` | Review and retrieval hint, not ranking behavior until tested. |
| Audience | `audience:<value>` | `agent`, `human`, `chat`, `developer`, `admin` | Helps format context and docs. |
| Scope | `scope:<value>` | Custom per wiki | Local project boundary: app, storage, tests, docs, security, data, etc. |
| Review | `review-after:YYYY-MM` | Valid month only | Warn that the record needs review. |
| Expiration | `expires:YYYY-MM` | Valid month only | Warn that the record may be invalid after the date. |
| Stale risk | `stale-risk:YYYY-MM` | Valid month only | Warn that the topic tends to drift after the date. |
| Supersession | `supersedes:<memory-id>` | Valid memory ID | Temporary convention until typed relations are justified. |
| Superseded by | `superseded-by:<memory-id>` | Valid memory ID | Temporary convention until typed relations are justified. |

Rules:

- Only one `kind:` tag should be canonical per memory.
- Only one `priority:` tag should be canonical per memory.
- Plain topic tags should not encode dates, IDs, or status.
- Status names such as `core`, `working`, `deprecated`, and `unconsolidated` should be blocked as plain tags unless the project explicitly overrides this.
- `current-state` can remain as an existing local convention, but future lifecycle semantics should move toward review/expiration diagnostics or schema fields.
- Tags never become hidden ranking rules until validators, UI, and probes exist.

### Round 4: Tag Policy, Allowlist, Blocklist, And Lexical Analysis

The tag policy should be a file-backed, per-wiki configuration managed by the app. Proposed storage:

```json
{
  "schemaVersion": 1,
  "mode": "warn",
  "namespaces": [
    {
      "name": "kind",
      "cardinality": "single",
      "allowedValues": ["fact", "rule", "procedure", "decision", "plan", "research", "guide", "concept", "issue", "example", "index"]
    }
  ],
  "plainTags": {
    "mode": "allowWithSuggestions",
    "allowlist": ["project-wiki", "search", "chat", "mcp", "pages", "ui", "storage", "tests"],
    "blocklist": ["misc", "general", "important", "stuff"],
    "aliases": {
      "retrieval": "search",
      "semantic-searching": "semantic-search"
    }
  }
}
```

The exact path should be chosen during implementation. Prefer a data-root path such as `Data/Policies/tag-policy.json` so the policy travels with the wiki instance and can be copied in tests. Do not store canonical tag policy only in user settings or appsettings; that would separate the rules from the knowledge base.

Lexical analysis should propose governance actions, not apply them automatically. Suggested analyses:

- Casing variants: `MCP`, `mcp`, `model-context-protocol`.
- Near duplicates by edit distance and token overlap: `serach` vs `search`, `sematic` vs `semantic`.
- Singular/plural or hyphen variants: `embedding`, `embeddings`, `semantic-embedding`.
- Overbroad tags used on too many records: a tag on more than 60-70% of records stops discriminating unless it is intentionally structural.
- Low-value tags: stopwords, `misc`, `general`, `notes`, `todo`, `important`, `new`, `old`.
- Malformed namespaces: `kind: rule`, `kind-rule`, `review_after:2026-05`, `expires:2026-13`.
- ID/date clutter in plain tags, which should move into relationships or review fields.
- Query-derived suggestions: frequent search terms that return good records but lack a canonical tag.

The Tag Manager should show suggested actions:

- add to allowlist;
- add to blocklist;
- mark as alias of canonical tag;
- bulk rename with preview;
- ignore suggestion for this wiki;
- require approval for future use.

### Round 5: Staleness, Expiration, And Supersession

Staleness is a warning and review workflow first, not a ranking formula. A memory can be old and still authoritative. A newly written record can be wrong. The memory suite needs explicit signals:

- `LastUpdated` means the file changed, not that content was reviewed.
- `review-after` means the record needs human recheck.
- `expires` means the claim may be invalid after the date and should not be cited without warning.
- `stale-risk` means the topic drifts quickly.
- `superseded-by` means use newer guidance first, but keep the old record visible for provenance.

Phase 0 should stop unreviewed automatic deprecation from hiding records. Phase 1 should show stale/expired/superseded diagnostics in memory detail, search results, context packs, and chat references. Phase 2 should measure whether warnings are enough. Only Phase 3 may add filters or ranking changes, and only with tests and a clear override.

### Round 6: Schema Evolution And Relations

The current `MemoryRecord` model is simple enough to inspect and edit. Keep that advantage while adding governance around it. Schema promotion should happen only when a convention needs reliable machine parsing.

Promotion gates:

- The convention is used in at least 30% of new Core records or 20+ records, whichever is smaller for the current wiki size.
- It has been used consistently for at least 6 weeks or two substantial planning cycles.
- Validator warnings for that convention are below 5% after UI assistance.
- Search/chat/tool behavior needs parsing that tags cannot provide safely.
- Migration from tags to fields has tests and a rollback plan.

Likely promotion order if gates pass:

1. `ReviewAfter` and `ValidUntil` date fields, because stale safety is central and dates are fragile as tags.
2. `Kind` enum, if `kind:` tags become common and UI/search behavior depends on them.
3. `Priority` enum, only after retrieval probes prove it helps.
4. `Relations` typed edge list, if supersession/dependency/conflict workflows become common.

Candidate typed relation model:

```json
{
  "type": "Supersedes",
  "targetId": "project-wiki-old-record",
  "note": "Replaces the old deployment guidance after single-host consolidation."
}
```

Keep `References` and `Conflicts` until typed relations prove they can replace or derive those arrays without making records harder to read.

### Round 7: Retrieval, Pages, And Chunking

Search is already a core strength: lexical, semantic, hybrid, context packs, source bundles, and tests exist. The next step is safer retrieval output, not cleverer ranking.

Near-term retrieval changes:

- Add diagnostics to result metadata: stale, expired, superseded, tag warnings, broken source links, truncated content.
- Expose whether semantic search used ONNX or token fallback.
- Preserve context-pack warnings and relationship provenance in JSON.
- Keep full-record retrieval available when context packs truncate content.

Page retrieval should become first-class but measured:

- Collect page length, heading count, and search miss metrics.
- Add optional page frontmatter only after deciding the metadata contract.
- Chunk pages by Markdown headings, not blind fixed windows, when metrics prove need.
- Preserve page slug, heading path, source line range if possible, and adjacent heading context in each chunk.

Chunking trigger proposal:

- More than 20% of pages exceed 1500 tokens, or
- page search miss rate exceeds 10% on a curated query set, or
- long-page Recall@5 / MRR falls below agreed thresholds, or
- chat context regularly truncates pages that contain the needed answer.

### Round 8: Tool Outputs, MCP, And Chat

Agent-facing tools should return strict structured output with human-readable summaries. Markdown-only results are readable, but they force agents to parse prose.

Recommended tool envelope:

```json
{
  "schemaVersion": "memorysmith.tool.v1",
  "query": "source links file references",
  "items": [],
  "warnings": [],
  "diagnostics": [],
  "summary": "Found 3 relevant memory records."
}
```

Plan:

- Keep `format=markdown` for human/debug views.
- Make JSON the preferred path for chat internals and MCP agent workflows.
- Add `schemaVersion` before changing default shapes.
- Update tests that parse MCP outputs before flipping defaults.
- Render structured warnings in chat trace and reference drawers.
- Provide raw JSON disclosure in Trace for power users.

Because no external clients need preserving, the app can eventually make JSON the default for MemorySmith's own agent path. Do that only after local chat, tests, and docs are updated.

### Round 9: Agent Write Governance

Current Agent memory proposals are too thin for a governed memory suite: they include ID, title, content, tags, status, and confidence, but not source links, page citations, rationale, alternatives, validation diagnostics, or risk level. Agent writes should be reviewable proposals, not trusted writes.

Proposed proposal model additions:

- `SourceLinks` and `PageCitations`.
- `Rationale` explaining why the memory/page should exist.
- `EvidenceIds` listing memories/pages/tool calls used.
- `SuggestedRelations` such as supersedes/conflicts/depends-on.
- `ValidationDiagnostics` from tag, source-link, staleness, and relationship validators.
- `RiskLevel`: low, medium, high, critical.
- `RequiresRole`: Editor or Admin.
- `Diff` for edits.
- `AlternativeViews` or dissent when confidence is low or a conflict exists.

Approval policy:

- Low-risk Working or Unconsolidated proposals can be approved by an Editor.
- Core, `kind:rule`, `priority:critical`, expired, supersession, or source-link-changing proposals require Admin review.
- Proposals with broken source links, malformed tags, or missing rationale should not be fast-approved.
- Batch approvals can exist later, but only after single-write quality is measured.

### Round 10: Validation, Rollout, And Governance

Implementation should proceed by gates, not enthusiasm. Each phase should have tests, doc updates, rollback notes, and a council trigger for high-impact changes.

Council triggers:

- Memory schema change.
- Search ranking change.
- Automatic status transition change.
- MCP/tool output default shape change.
- Agent write approval rule change.
- Page chunking or embedding pipeline change.

Non-council changes:

- Copy edits.
- UI polish with no behavioral change.
- Single-memory/page fixes.
- Tests that encode already-approved behavior.

## Final Architecture Target

MemorySmith should become a local-first memory suite with these layers:

1. **Record Layer** - JSON memory records remain inspectable and source-linked.
2. **Policy Layer** - per-wiki tag policy, validation mode, aliases, allowlist, blocklist, and governance settings.
3. **Diagnostics Layer** - tag, source-link, relation, staleness, maintenance, and chunking diagnostics.
4. **Retrieval Layer** - lexical, semantic, hybrid, page, chunk, context-pack, and source-bundle retrieval with structured warnings.
5. **Human Workbench Layer** - `/memories`, `/pages`, `/chat`, `/health`, `/variables`, and future `/admin/tags` surfaces for review and learning.
6. **Agent Governance Layer** - source-backed proposals, trace evidence, approval gates, and council workflows for high-impact changes.
7. **Measurement Layer** - search quality probes, page corpus stats, stale-result metrics, tag drift metrics, and Agent write review outcomes.

## Implementation Phases

### Phase 0: Freeze Risky Assumptions And Measure Baseline

Goal: prevent current automation from undermining the plan.

Tasks:

1. Audit maintenance status transitions in `MemoryMaintenanceTasks`, `MemoryMaintenanceService`, `MemoryStateMachine`, and `MemoryScorer`.
2. Decide whether automatic deprecation remains allowed. Recommended default: recommendation-only until staleness and review diagnostics exist.
3. Add a planning note to docs explaining current maintenance behavior and the intended warning-first replacement.
4. Inventory existing tags across `Data/Memories`.
5. Inventory SourceLinks and unresolved `%VarName%` references.
6. Capture current search benchmark results and top known probes.
7. Capture page corpus statistics: count, size distribution, heading distribution.

Acceptance gates:

- Maintenance behavior is documented and no longer contradicts the warning-first plan.
- Baseline tag inventory exists.
- Baseline search/page/source-link metrics exist.
- No ranking, schema, or chunking changes have been made.

### Phase 1: Policy And Diagnostics Foundation

Goal: create the governance model without changing ranking.

Tasks:

1. Define `TagPolicy` and `TagDiagnostic` models.
2. Choose file-backed policy storage under the wiki data root.
3. Implement canonical tag validation: namespaces, dates, cardinality, aliases, blocked tags, and malformed tags.
4. Implement source-link diagnostics: missing variables, missing files, invalid line ranges, disallowed roots, and oversized reads.
5. Implement relationship diagnostics for `References`, `Conflicts`, `supersedes`, and `superseded-by` targets.
6. Implement staleness diagnostics for `review-after`, `expires`, `stale-risk`, `LastUpdated`, and `Deprecated` status.
7. Add diagnostics to memory save/update paths as warnings first.
8. Add docs: memory writing guide, tag policy guide, source-link guide, and examples.

Acceptance gates:

- Malformed tags generate actionable diagnostics.
- Broken source links are detected without blocking unrelated edits.
- Stale/expired/superseded records produce warnings.
- Existing records can still load and save.
- Tests cover valid and invalid tags, date tags, source links, and relationship references.

### Phase 2: Human Workbench And Tag Manager

Goal: make governance visible and easy to use.

Tasks:

1. Replace comma-only tag editing in `/memories` with tag chips and autocomplete.
2. Show tag validation warnings inline before save.
3. Add a Tag Manager surface for allowlist, blocklist, aliases, namespace values, usage counts, and suggested merges.
4. Add lexical-analysis suggestions for blocklist/alias candidates.
5. Add staleness and supersession badges to memory list/detail/search results.
6. Add source-link health indicators in the memory editor.
7. Add a diagnostics panel per memory with tag/source/relation/stale issues.
8. Add admin controls for tag policy mode: observe, warn, block invalid namespace values, block all unknown tags.

Acceptance gates:

- Users can define allowlisted and blocklisted tags in the UI.
- Lexical analysis suggests blacklist/alias/merge candidates with preview, not automatic mutation.
- Tag editing is keyboard-accessible and does not require memorizing the policy.
- Unknown tags can be allowed, warned, or blocked based on policy mode.
- Existing free-form tags remain visible during migration.

### Phase 3: Retrieval Output Safety

Goal: teach search, MCP, and chat to carry warnings and provenance.

Tasks:

1. Add diagnostics to lexical, semantic, hybrid, unified, and page result DTOs.
2. Add structured warnings to `memorysmith_context_pack` JSON output.
3. Add `schemaVersion` and structured envelope support to MCP tool results.
4. Expose ONNX vs token fallback in semantic match reasons or metadata.
5. Add chat trace rendering for stale/source/tag/relation warnings.
6. Add reference drawer chips for strict rules, stale records, expired records, and superseded records.
7. Keep ranking unchanged until probes prove a change is helpful.

Acceptance gates:

- A stale record is still returned but visibly marked.
- A broken source-linked record is returned with a warning, not silently trusted.
- Context packs include warnings in JSON and chat trace.
- Existing relevance tests still pass.
- MCP output shape changes are versioned and tested.

### Phase 4: Agent Write Governance

Goal: make Agent writes auditable and safe enough for a knowledge base.

Tasks:

1. Expand Agent proposal models with evidence, citations, rationale, diagnostics, risk, and diff fields.
2. Add proposal prevalidation before approval.
3. Add approval checklist UI with tag/source/status/confidence/relation checks.
4. Add RBAC rules: stricter approval for Core, strict-rule, critical, supersession, expired, or source-link-changing proposals.
5. Add rejection reasons and reviewer notes.
6. Log proposal outcomes for quality metrics.
7. Update chat prompt and tool instructions so agents prefer source-backed proposals.

Acceptance gates:

- A proposal without evidence cannot be fast-approved into Core.
- A proposal with malformed tags shows diagnostics before approval.
- Approval trace records who approved what and why.
- Tests cover low-risk and high-risk proposal paths.

### Phase 5: Measurement And Promotion Gates

Goal: decide whether to promote tags into schema or alter retrieval behavior.

Tasks:

1. Measure tag drift: unknown tags, alias suggestions, blocked attempts, duplicate clusters.
2. Measure stale-result impact: top-k stale rates, expired citations, user overrides, answer quality probes.
3. Measure Agent proposal quality: rejection rate, missing-source rate, approval time.
4. Measure page retrieval: page length distribution, miss rate, Recall@5/MRR on page queries.
5. Decide whether specific conventions should become schema fields.
6. Run a focused council review before each schema/ranking/chunking promotion.

Acceptance gates:

- Promotion thresholds are met with local data.
- A rollback plan exists for each promoted field or behavior.
- Tests are written before migration.
- The wiki docs explain the new behavior before it ships.

### Phase 6: Optional Schema Promotion

Goal: move proven conventions into stable model fields.

Candidate tasks:

1. Promote `ReviewAfter` and `ValidUntil` to optional date fields if date tags prove fragile or widely used.
2. Promote `Kind` to enum if `kind:` becomes central to retrieval/UI.
3. Promote `Priority` only if quality probes prove it improves ranking or review workflow.
4. Promote typed `Relations` only if supersession/dependency/conflict queries become common.
5. Provide migration from tag conventions to fields, with compatibility reading old tags during transition.
6. Add UI controls for new fields before enforcing them.

Acceptance gates:

- Migration runs on a copied `Data/Memories` fixture.
- Old-form and new-form records both behave during the transition.
- Search/chat/tool outputs document whether they read tag, field, or both.
- Schema changes are reflected in `Schemas/memory.schema.json`, model tests, UI tests, and docs.

### Phase 7: Page Metadata, Chunking, And Page Embeddings

Goal: make long-form docs first-class retrieval sources when the corpus needs it.

Tasks:

1. Add optional page frontmatter for tags, audience, related memories, review-after, and source links.
2. Add page diagnostics parallel to memory diagnostics.
3. Implement heading-based chunking behind a feature flag.
4. Preserve slug, heading path, section ID, and source line range where feasible.
5. Add page chunk search and optional embeddings only after page metrics trigger the need.
6. Render cited page sections in chat references.

Acceptance gates:

- Chunking improves or preserves page query Recall@5/MRR.
- Chunking does not make small pages harder to find.
- Chat citations show the page section, not only the page title.
- Rebuilding chunks is deterministic and fast enough for local use.

### Phase 8: Advanced Council And Learning Flows

Goal: use the memory suite to help humans learn and make better decisions, not just store facts.

Tasks:

1. Add a guided council workflow in chat for high-impact decisions.
2. Let chat assemble evidence packs from memories, pages, source bundles, and search benchmarks.
3. Keep council seats independent and preserve dissent in generated reports.
4. Add templates for decision records, implementation plans, and research intake notes.
5. Measure whether councils improve outcomes: fewer missed risks, fewer stale citations, better acceptance-gate clarity.

Acceptance gates:

- Council flow is opt-in for high-impact decisions only.
- Dissent is preserved in the final report.
- Council reports cite evidence and state confidence.
- If council flow adds verbosity without better decisions, scale it back.

### Phase 9: Sustained Operations

Goal: keep the memory suite healthy after features ship.

Tasks:

1. Quarterly tag-policy review.
2. Quarterly stale/expired memory review.
3. Monthly broken source-link report.
4. Search quality probe updates when new major wiki topics appear.
5. Agent proposal quality report.
6. Wiki docs cleanup: remove obsolete planning pages or mark supersession clearly.

Acceptance gates:

- Health surfaces show tag drift, stale records, broken links, and search quality warnings.
- Users can fix issues from the UI or navigate directly to the affected record/page.
- Project wiki truth remains source-grounded and reviewable.

## Test And Benchmark Plan

Use NUnit, consistent with project preference.

Recommended test additions:

- `TagPolicyValidationTests`: canonical tags, unknown namespaces, blocklist, aliases, dates, cardinality.
- `TagLexicalAnalysisTests`: near duplicates, stopword/noise tags, casing variants, merge suggestions.
- `MemoryMaintenanceWarningFirstTests`: maintenance does not silently deprecate protected/strict records; recommendation-only mode works.
- `StalenessDiagnosticsTests`: review-after, expires, stale-risk, superseded-by, Deprecated status warnings.
- `SourceLinkValidationTests`: missing variables, missing files, invalid line ranges, disallowed roots.
- `RelationshipDiagnosticsTests`: dangling references/conflicts/supersession tags.
- `ContextPackDiagnosticsTests`: context pack JSON includes warnings and preserves record IDs.
- `McpToolSchemaTests`: structured envelopes include `schemaVersion`, `items`, `warnings`, and `diagnostics`.
- `AgentWriteProposalGovernanceTests`: low-risk vs high-risk proposals, missing evidence, malformed tags, approval roles.
- `PageMetadataAndChunkingTests`: frontmatter parse, heading chunks, section citations, deterministic rebuild.
- `SearchQualityStaleSafetyTests`: stale records are visible and warned; ranking changes require measurable improvement.

Recommended metrics:

- MRR and Recall@5 for memory search probes.
- nDCG for mixed memory/page queries.
- Percent of top-10 results with stale/expired warnings.
- Tag drift rate and unknown tag count.
- Source-link health rate.
- Agent proposal approval/rejection rate and missing-evidence rate.
- Page query miss rate and page length distribution.

## Risks And Mitigations

| Risk | Mitigation |
| --- | --- |
| Automatic maintenance hides authoritative records. | Phase 0 audit; switch to warning/recommendation mode before staleness logic depends on age. |
| Tag policy becomes too rigid. | Start in observe/warn mode; allow per-wiki custom allowlists and explicit overrides. |
| Tag policy stays too loose. | Use lexical analysis and metrics; move namespaces to blocking mode after drift proves harmful. |
| Lexical analysis creates noisy suggestions. | Require human approval and support ignore rules per wiki. |
| Schema promotion creates migration churn. | Promote one field at a time only after thresholds; keep old tags readable during transition. |
| JSON tool output breaks local chat/tests. | Add versioned envelope first, update internal consumers, then change defaults. |
| Page chunking adds complexity before need. | Gate on page metrics and retrieval probes. |
| Agent approvals become burdensome. | Risk-tier proposals; fast path only for low-risk Working/Unconsolidated writes. |
| Council workflow becomes bureaucracy. | Require council only for schema, ranking, maintenance, MCP default, Agent governance, or chunking changes. |

## Open Questions

- Should the tag policy live under `Data/Policies`, `Data/Graph`, or another data-root folder?
- Should initial policy mode be `observe` or `warn`? Recommendation: `warn` for namespaces, `observe` for plain topic tags.
- Should `expires` ever auto-move a record to Deprecated? Recommendation: not until Phase 2 metrics show warnings fail.
- Should Admin approval be required for all Core writes or only Core writes with strict-rule/critical/stale/source-link risk? Recommendation: all Core writes require Admin until proposal quality is measured.
- Should page frontmatter use YAML? Recommendation: yes if a robust parser is added; otherwise defer frontmatter.
- Should current `current-state` tags remain long-term? Recommendation: allow during transition, then decide after staleness fields exist.
- What is the exact threshold for schema promotion in small wikis? Recommendation: combine percentage and absolute count, then require council approval.

## Immediate Next Planning Actions

1. Decide and document maintenance behavior: apply status automatically, recommend only, or require human approval.
2. Draft the first `TagPolicy` schema and UI mock for Tag Manager.
3. Create the memory writing/tagging guide with examples.
4. Create baseline tag/source/page/search reports.
5. Add Phase 0 and Phase 1 tests before any ranking or schema work.
6. Run a focused council review before implementing Phase 6 schema promotion or Phase 7 chunking.
