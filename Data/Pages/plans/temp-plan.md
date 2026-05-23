# RFC: Core Memory System Improvements for AI Agents

Status: Draft RFC, council-reviewed on 2026-05-20

Scope: Wiki and memory-system planning only; no implementation is implied by this page

Related: [Search and Chat](../guides/search-and-chat.md), [Council Workflow](../council/llm-council.md), [Deep Research Prompt](../research/memory-system-deep-research-prompt.md), [AI Memory Suite Implementation Plan](ai-memory-suite-implementation-plan.md), `MemorySmith.Core/Docs/Plans/MemorySystemSchemaImprovements_20260519.md`

## 1. Executive Summary

MemorySmith should improve agent memory, search, human-facing wiki docs, and chat-assisted learning without assuming that either extreme is correct:

- Pure schema expansion gives agents precise fields, but it creates migrations, UI burden, and larger JSON records.
- Pure convention-only design keeps storage lean, but it can create hidden complexity in tag parsing, stale context, and ambiguous relationships.

The recommended direction is **convention-first, validated, and evidence-gated**. Use the current `MemoryRecord` shape first: `Content`, `Tags`, `References`, `Conflicts`, `SourceLinks`, `Status`, `Confidence`, `UsageCount`, and `LastUpdated`. Add lightweight conventions that humans can read and agents can retrieve. Then promote a convention into schema only after the wiki, search probes, and chat traces prove that the concept is durable and repeatedly useful.

This plan replaces the earlier “zero schema changes” framing. The better rule is: **avoid schema churn, but do not avoid schema changes when they are the simplest long-term representation of a real capability**.

Confidence in this revised direction: 82%. The biggest risks are manual convention drift, stale-ranking mistakes, and premature schema expansion.

## 2. Intended Use Cases

This plan optimizes for three audiences at once.

| Audience | Need | Design implication |
| --- | --- | --- |
| AI coding agents | Compact, source-grounded truth that ranks well in MCP/search/context packs | Keep structured memories atomic, tagged, sourced, and linked. Prefer JSON output for tools when an agent will parse it. |
| Human wiki readers | Browseable explanations, decisions, runbooks, and learning paths | Keep longer narrative in markdown pages. Link pages to structured memories when a fact needs source links or lifecycle metadata. |
| Chat users | Ask questions, learn concepts, inspect evidence, and optionally approve Agent writes | Chat should retrieve both memories and pages, show references/trace evidence, and explain when an answer depends on strict rules or stale records. |

## 3. Current Ground Truth

Verified during review:

- MemorySmith is a single-host `MemorySmith.App` system with Blazor UI, REST API, MCP endpoint, markdown pages, chat, storage, and maintenance in one process.
- Structured memories live under `Data/Memories`; markdown pages live under `Data/Pages`.
- The current memory schema has flat `Tags`, `References`, and `Conflicts`; it has no `Relations`, `Constraints`, `Priority`, or `ValidUntil` fields.
- Search currently supports lexical, semantic, and hybrid modes. Semantic ranking uses optional ONNX embeddings with local token-scoring fallback. Hybrid search uses RRF.
- MCP tools include memory search, semantic search, hybrid search, context pack, exact get, page search/get, unified search, source bundle, and find-by-source in the app design. In this review session, the exposed MCP write surface was unavailable, so MCP was used for search, get, context, and source-backed review rather than page writes.
- Chat has conservative preloading, read-only tool calls, page/unified retrieval, trace events, references, and explicit approval for Agent writes when writes are enabled.

## 4. Design Principle: Conventions First, Schema When Proven

Conventions are useful when they are visible in the UI, searchable, and easy to correct. Schema is useful when a concept needs type safety, validation, query behavior, or UI controls.

Use conventions when:

- The concept is still experimental.
- Humans can understand it in plain markdown.
- Search can use it as a hint without changing behavior.
- Mistakes are easy to detect and repair.

Promote to schema when:

- A convention appears across many Core records.
- Agents or chat need reliable machine parsing.
- UI controls are needed to prevent malformed input.
- Tests show search quality or write approval depends on the field.
- A convention becomes impossible to validate cleanly as a tag or markdown section.

## 5. Recommended Memory Authoring Conventions

These are planning conventions, not yet enforcement rules. Do not assume code behavior exists until tests and implementation records prove it.

### 5.1 Tags

Keep normal flat tags for broad topics, such as `project-wiki`, `chat`, `search`, and `current-state`.

Use namespaced tags only for behavior-relevant hints. Prefer lowercase, colon-delimited tags without a leading `#` because current MemorySmith tags are stored as plain strings:

| Tag | Meaning | Example |
| --- | --- | --- |
| `kind:rule` | A strict rule or invariant appears in the content | `kind:rule` |
| `kind:procedure` | The memory describes a repeatable workflow | `kind:procedure` |
| `priority:critical` | Agents should treat this as high priority when relevant | `priority:critical` |
| `review-after:YYYY-MM` | The record should be reviewed after a month | `review-after:2026-07` |
| `expires:YYYY-MM` | The record is invalid after a month unless renewed | `expires:2026-08` |
| `stale-risk:YYYY-MM` | The record may become stale but should not auto-expire | `stale-risk:2026-09` |
| `supersedes:<memory-id>` | This record replaces a known older record | `supersedes:project-wiki-old-search-plan` |
| `superseded-by:<memory-id>` | This record should defer to a newer record | `superseded-by:project-wiki-search-roadmap` |

Guardrails:

- Do not mix `#expires`, `expires`, and `expires:` forms. Pick one canonical form before enforcement.
- Do not make tags carry long explanations. Put explanations in `Content`.
- Do not rely on namespaced tags for ranking until validators and tests exist.
- If a tag references another memory ID, the target should exist or the context pack should report a warning.

### 5.2 Strict Rules in Markdown Content

Use GFM alert blocks for constraints that humans and agents should notice:

```markdown
> [!IMPORTANT]
> Keep `Data/Memories` stable. Tests that mutate wiki records must copy it to temp storage first.
```

Recommended content structure for durable memories:

```markdown
## Rule
> [!IMPORTANT]
> One or two hard constraints, if any.

## Context
Short explanation of why the rule exists.

## Evidence
- Source link or related memory reference.

## Review Notes
- Review after: 2026-07
- Supersedes: old-memory-id, if applicable.
```

Do not assume GFM alert extraction is implemented. The implementation plan should use a Markdown-aware parser or heavily tested extraction, not an untested regular expression over arbitrary markdown.

### 5.3 Relationships

Today, `References` and `Conflicts` are plain memory ID arrays. Treat them as graph edges with simple meaning:

- `References`: related records that add context, source evidence, or adjacent decisions.
- `Conflicts`: records that disagree, are obsolete, or require reconciliation.

For now, typed relationship details should be written in `Content` under a `Relationship Notes` section and optionally mirrored in tags such as `supersedes:<id>`. A future schema may add a first-class `Relations` array if convention-based notes are too fragile.

Do not infer automatic conflict resolution merely because one record is newer or has higher confidence. That behavior must be explicit, tested, and visible in search/context-pack output before agents rely on it.

## 6. Search and Retrieval Improvements

### 6.1 Baseline Strategy

- Keep lexical search honest for exact terms, IDs, tags, and source words.
- Use semantic search for conceptual recall, but expose whether the result came from ONNX embeddings or local fallback scoring.
- Use hybrid search as the default discovery mode, while preserving match reasons that let an agent see lexical vs semantic disagreement.
- Use context packs when the agent needs root records plus references, conflicts, and backlinks.
- Use source bundles only after narrowing the record set; source reads are higher-sensitivity and should stay bounded.

### 6.2 Pages as First-Class Knowledge

Pages are not just notes; they are the human-readable half of the wiki. The search roadmap should eventually treat pages as first-class retrieval units:

- Keep whole-page search for small corpora.
- Add page chunking when pages become too long for useful snippets.
- Add page embeddings after memory embeddings are stable and measured.
- Preserve page slugs and section provenance so chat can cite the exact page context.
- Keep combined or unified search available so agents discover both narrative pages and atomic memory records.

### 6.3 Staleness Before Decay

The old plan proposed a temporal decay formula. The safer order is:

1. Add explicit staleness metadata as tags and visible warnings.
2. Show staleness warnings in search/context-pack/chat trace output.
3. Measure whether stale records are harming answers.
4. Only then apply ranking changes.

Initial behavior should warn, not hide. Never silently bury Core rules, unresolved tasks, or high-confidence architecture decisions because they are old.

If ranking decay is later implemented, it should be bounded, reversible, and tested against the live project wiki. A draft scoring rule:

```text
finalScore = baseSearchScore * confidenceMultiplier * freshnessHint * usageHint
```

Where:

- `freshnessHint` never drops below a configured floor for Core records.
- `kind:rule` and `priority:critical` records do not decay unless they also have an expired `expires:YYYY-MM` tag.
- expired records are flagged first; exclusion should be opt-in and visible.
- usage should be logarithmic or capped so popular stale records do not dominate forever.

## 7. Chat and Human Learning Improvements

Chat should be a learning surface, not only a text box over search.

Target behavior:

- When strict rules are retrieved, chat should distinguish “hard wiki rule” from general context.
- When results are stale, deprecated, or superseded, chat should say so and prefer newer evidence.
- Trace should show why a tool was called, which memory/page results were used, and which results were ignored.
- Agent write proposals should show the conventions they used, their evidence, and why the proposed status/confidence/tags are appropriate.
- Human-facing pages should explain the same conventions that agents use, so the UI and chat do not become a private language for models.

Good chat prompts for humans:

- “Search the wiki for strict rules about Data/Memories and explain which ones are current.”
- “Use a context pack for search roadmap records, then explain the trade-offs between page chunking and memory embeddings.”
- “Review this proposed memory as a council: data model, retrieval, human docs, and skeptical risk.”

## 8. Governance for Agent Writes

Agent writes should remain opt-in and approval-gated. For memory quality, approval should consider more than whether the JSON is valid.

Suggested approval checklist:

- Is the proposed record better as a structured memory rather than a markdown page?
- Does it cite source links or related memories when making durable claims?
- Is `Status` appropriate? New claims should usually start as Working unless they are already verified project truth.
- Is `Confidence` realistic?
- Are namespaced tags canonical and necessary?
- Does the record contain a strict rule? If so, is the rule sourced and reviewed carefully?
- Does it supersede or conflict with an existing record? If so, are both sides linked?

Future UI/trace improvements may surface these checklist items directly in the Agent write approval panel.

## 9. Phased Plan

### Phase 0: Decision Cleanup

- Treat this page and `MemorySystemSchemaImprovements_20260519.md` as competing RFCs, not both accepted plans.
- Use the [Council Workflow](../council/llm-council.md) for any final decision that changes schema, search ranking, or chat write behavior.
- Record the decision and dissent in a page or structured memory before implementation.

### Phase 1: Documentation and Convention Pilot

- Update a small number of high-value Core records to use markdown alert blocks and canonical namespaced tags.
- Do not bulk-edit the entire wiki yet.
- Add examples to human-facing pages so users can learn the conventions.
- Create search probes for strict-rule discovery, stale-warning discovery, and page-vs-memory retrieval.

### Phase 2: Validation Without Behavior Change

- Add validators or diagnostics for malformed namespaced tags.
- Add graph diagnostics for missing references, missing conflict targets, and supersession tags pointing at unknown IDs.
- Add context-pack warnings for stale, expired, superseded, or conflicting records.
- Keep ranking behavior unchanged until the warnings prove useful.

### Phase 3: Retrieval Output Quality

- Make structured JSON the preferred agent-facing context-pack/search format while keeping Markdown for humans.
- Include search-mode metadata, fallback mode, relationship warnings, and staleness warnings.
- Add page chunking and page embeddings only after corpus size or chat failures justify the added machinery.

### Phase 4: Schema Promotion Decision

Promote conventions into schema only if Phase 1-3 evidence shows repeated need. Candidate fields:

- `Relations` if tags and relationship notes cannot support reliable graph traversal.
- `Constraints` or `Intent` if markdown alert extraction is too fragile.
- `ValidUntil` or `ReviewAfter` if date tags become common and need UI controls.
- `Priority` if search/chat behavior depends on stable priority semantics.

Each promoted field needs migration, UI support, tests, and fallback behavior for legacy records.

### Phase 5: Long-Term Capability Expansion

If maximizing usefulness requires broader changes, they should be considered openly rather than blocked by the lean premise:

- Durable vector index under `Data/Graph/embeddings` when exact in-memory scans are too slow.
- Page chunk embeddings when narrative pages outgrow whole-page search.
- Richer graph validation or a lightweight graph store if relationship traversal becomes a core workflow.
- Stronger governance telemetry for Agent writes and trace-backed decisions.

These should remain evidence-driven additions, not automatic scope.

## 10. Acceptance Criteria Before Implementation

Do not implement ranking, schema, or write-behavior changes from this RFC until these are true:

- A council review has compared convention-first and schema-first options.
- The chosen option has explicit trade-offs, dissent, and rollback notes.
- Search probes cover strict-rule retrieval, stale/superseded records, and page/memory balance.
- Existing Core memories still rank for their known probe queries.
- Chat trace output can show the evidence used for an answer or Agent write proposal.
- Human-facing docs explain the conventions without requiring code knowledge.

## 11. Open Questions

- Should namespaced tags become UI-assisted chips before any search behavior uses them?
- Should `expires:YYYY-MM` move a record to Deprecated, merely warn, or only affect context packs?
- What threshold proves that page chunking is worth adding?
- Should `memorysmith_context_pack` default to JSON for MCP agents while `/pages` and UI keep Markdown-first display?
- Should relationship typing live in schema, a graph store, or convention-backed content notes?
- What role should be required to approve Agent writes that create Core memories or strict rules?

Externally researchable parts of these questions have been converted into a reusable [Deep Research Prompt](../research/memory-system-deep-research-prompt.md). Use the research results as evidence for a later council review; do not treat external recommendations as automatic MemorySmith decisions.

## 12. Council Review Summary

Four review lenses were applied: architecture/data model, search/retrieval/MCP, human learning/chat UX, and skeptical risk.

Shared conclusions:

- Lean storage is good, but convention-only behavior is not enough.
- Tag namespaces need canonical forms, validation, UI help, and tests before they influence ranking or agent behavior.
- GFM alert blocks are a good human/agent convention, but extraction must be tested and should not rely on a naive regex.
- Automatic temporal decay is risky. Start with warnings and measurement.
- The markdown page wiki and structured memory wiki should work together; pages need a clearer long-term retrieval path.
- The council workflow should be a repeatable MemorySmith method, not a generic list of external links.

Overall confidence after review: 82% for the revised convention-first, evidence-gated approach; 55% for the original convention-only plan as written.
