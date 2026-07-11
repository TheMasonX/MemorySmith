# Making the MemorySmith Knowledge Graph Searchable, Reviewable, and RAG-Ready

**Task Description:** Independent deep-dive research on evolving MemorySmith's memory graph into a more capable retrieval/reference system (schema, weighting, traversal, reviewability)
**Author:** Claude (independent analysis; a second audit was supplied as context and is evaluated, not assumed, below)
**Timestamp:** 2026-07-10
**Branch:** master (as of the audited commit)
**Commit:** `db04b23a25e3930b424f3ef9eb0a0af3efcb9c27`

---

## Executive Summary

The supplied audit's direction is broadly right — typed edges, better traversal, reviewable authoring, cluster views — but it is written as if the codebase has none of this yet. It does. Direct code inspection at `db04b23` found:

- A full graph-analytics pipeline already exists (`MaintenanceTopicMapService`): nodes, typed edges, clusters, "supersession chains," dependency-cycle detection, and a staleness heatmap. It just isn't wired into retrieval, isn't incremental, and has real bugs in the parts that matter most to a reviewer (chains don't actually chain; clustering is tag-string co-occurrence, not community detection).
- A relation-typing scheme **already exists**, smuggled inside the `Tags` list as colon-prefixed strings (`supersedes:tsk-0123`, `depends-on:xyz`). This is exactly the "arbitrary/unclear choice" the user asked me to look for: relation edges are living in the tag namespace, unvalidated against real record IDs, and subject to whatever the tag-policy governance system enforces for unrelated reasons.
- **TSK-0268 (reverse references) is ~60% done despite sitting in `Backlog`.** The context-pack `reverseReferences` field and the workbench's "Incoming References" panel are shipped and working. Only the pages-backlinks view and the dedicated API endpoint are missing. The task tracker is stale, and the pasted audit's "TSK-0268 is the right current foothold" framing undersells how much of it already exists.
- **`MemoryIndex` (`ById`, `ByTag`, `ByReference`) is maintained on every write and read by nothing.** Zero call sites anywhere in the App, Core, or Storage projects query it. Meanwhile, both live reverse-reference implementations independently do a full O(n) scan over every memory record on every request — one of them duplicating the other. This is the single cheapest, highest-leverage fix available: wire the two duplicate O(n) scans to the already-paid-for O(1) index, and the "traversal is slow" problem the pasted audit worries about mostly goes away for the reference case before any new schema work happens.
- Hybrid search scoring (`ReciprocalRankScore(lexicalRank) + ReciprocalRankScore(semanticRank)`, RRF k=60) has **zero graph signal** — reference count, cluster membership, and recency-of-supersession never touch the ranking function today. Graph-aware reranking is a real, well-evidenced opportunity, but recent 2026 benchmarking work (below) shows graph structure's payoff is concentrated in multi-hop reasoning queries and carries real offline-maintenance cost — this is a case for *targeted* graph signals in reranking, not a wholesale GraphRAG rebuild, for a single-maintainer greenfield KB of this size.

**Recommended sequencing, in order of leverage-per-effort (detailed in Phases below):** (1) fix the dormant index + de-duplicate the two reverse-reference code paths + close TSK-0268's two remaining criteria — cheap, immediately useful, no schema change; (2) migrate the tag-smuggled relation types into a first-class typed-edge model that subsumes `References`/`Conflicts`, with existence validation; (3) make `MaintenanceTopicMapService` incremental and fix its cycle/chain/cluster algorithms; (4) add bounded traversal APIs (k-hop, shortest-path, blast-radius) on top of the now-correct typed graph; (5) only then consider feeding graph signals into retrieval ranking, informed by the current literature's caveats about cost/benefit.

---

## Goals

- Make "what points to this, and how" answerable in O(1)/O(hops), not O(corpus).
- Give relation types a real, validated schema instead of a tag-string convention.
- Make the existing cluster/cycle/chain analysis correct and incremental instead of full-rebuild-and-often-wrong.
- Give human reviewers a structured, diffable way to see and correct graph edges — not a 32-node capped SVG.
- Decide, with evidence rather than assumption, whether/where graph signal should touch retrieval ranking.

### Non-Goals

- Full GraphRAG re-architecture (entity extraction pipelines, hierarchical community summarization, agentic multi-hop planners). The evidence below suggests this is not proportionate to a single-operator KB's current scale and query patterns.
- Replacing the existing lexical/semantic/RRF hybrid search. Graph should augment it, not replace it.
- A general-purpose graph database migration (Neo4j, etc.). Nothing found in this audit indicates the SQLite-backed model is the bottleneck — the bottleneck is that the useful indexes/analytics that exist aren't connected to anything.

---

## Requirements

- R1: Reverse-reference lookups must not re-scan the full corpus per request.
- R2: Relation types must be a validated, first-class schema element, not a tag-string convention, while remaining backward-compatible with existing `References`/`Conflicts` data during migration.
- R3: Cluster/cycle/chain analysis must produce results a reviewer can trust (real multi-hop chains, deduplicated cycles, meaningful clusters) and must not require a full corpus rebuild to stay current.
- R4: Any new traversal API must be bounded (hop limit, result cap) — this codebase's own council notes already flag "do not let an LLM freewheel through arbitrary hops" as a known risk class elsewhere; the same discipline applies here.
- R5: Reviewer-facing surfaces (workbench, pages viewer, topic map) must reflect the same underlying data model, not three independently-computed views of "what links to what."

### Constraints

- C1: Existing `MemoryRecord.References`/`Conflicts` fields are read by the RRF `ScoreSemanticMatch` path today (`referenceTokens` contributes to semantic scoring) — any schema migration must not silently break that scoring contribution.
- C2: `MaintenanceTopicMapService.BuildAsync` already has a defined trigger surface (weekly scheduled maintenance run + on-demand `?refresh=true` endpoint) — incremental-update work should fit that existing trigger model rather than inventing a new one.
- C3: Single-maintainer project — prefer fixes with a small, reviewable diff over big-bang schema rewrites, consistent with the project's own stated preference (per session memory) for evidence-based, incremental, zero-tech-debt-tolerant changes.

---

## Phases

### Phase 1: Stop paying for infrastructure that isn't wired up

#### Goal
Make the reference graph that already exists fast and non-duplicated before adding anything new.

#### Deliverables
- `GetReverseReferencesAsync` and the inline context-pack reverse-reference builder both read from `MemoryIndex.ByReference` (and a new `ByConflict`, see G-2 below) instead of scanning `_store.LoadAll()`.
- One canonical reverse-reference computation, called from both the workbench panel and the context-pack formatter.
- TSK-0268 finished: `/pages` "Linked from" backlinks, and the `GET /api/memories/{id}/reverse-references` endpoint (already-shipped criteria left alone).

#### Success Criteria
- Reverse-reference lookup cost is O(hops), not O(corpus size).
- Single code path owns "who references this ID," covered by one test suite instead of two untested-independently paths.
- TSK-0268 status reflects reality (3/5 → 5/5 criteria, or the task is split into a "done" part and a small follow-up).

### Phase 2: Give relation types a real schema

#### Goal
Replace the tag-smuggled relation convention with validated, typed edges, without a breaking migration.

#### Deliverables
- New edge concept: `sourceId`, `targetId`, `relationType` (`references`, `supersedes`, `depends_on`, `conflicts_with`, `same_as`, `mentions` — extending, not reinventing, the type vocabulary `MaintenanceTopicMapService` already uses internally), `origin` (`manual`/`inferred`/`imported`), `createdAtUtc`.
- Migration path: `ExtractTagRelationships`'s colon-prefixed tags become the *import source* for one-time backfill into the new edge store, then stop being parsed as relations (they remain visible as literal tags only if a human wants them to).
- Validation: every edge target must resolve to a real record/page ID at write time — closing the silent-dangling-edge gap in current `ExtractTagRelationships`.

#### Success Criteria
- Zero relation semantics left encoded only in tag strings.
- No possible edge to a nonexistent target (verified by a test that tries to create one).
- Tag Manager / tag-policy governance no longer has to reason about "real" vs. "pseudo-relation" tags.

### Phase 3: Make graph analytics correct and incremental

#### Goal
Fix the specific algorithmic gaps found in `MaintenanceTopicMapService`, and stop requiring a full rebuild to reflect a single edit.

#### Deliverables
- `BuildSupersessionChains` actually follows multi-hop chains (A supersedes B supersedes C → one 3-element chain), not one 2-element pair per edge.
- `FindDependencyCycles` deduplicates cycles found from different start nodes and uses a single shared visited-set instead of re-deriving one per top-level node (correctness stays the same; avoids the combinatorial blow-up on dense dependency graphs).
- `BuildClusters` gets a documented ceiling (e.g., exclude tags above N members, or weight by tag rarity) so one generic tag can't produce a useless giant "cluster," pending a decision on whether real community detection (Louvain/label-propagation over the new typed edges) is worth the added complexity.
- Incremental update: a single record edit updates the affected slice of the cached `TopicMapDocument` rather than triggering (or waiting up to a week for) a full `BuildAsync` rebuild.

#### Success Criteria
- A reviewer looking at "supersession chains" sees an actual chain, not a flat edge dump relabeled.
- Cycle list has no duplicate/rotated entries.
- Editing one record updates its neighborhood in the cached topic map without a full corpus rebuild.

### Phase 4: Bounded traversal API, then (only if justified) retrieval integration

#### Goal
Give reviewers and agents real traversal answers; decide deliberately, with evidence, whether to feed graph signal into ranking.

#### Deliverables
- k-hop neighborhood, shortest-path, and "blast radius" (reverse k-hop) endpoints over the Phase 2 typed-edge store, each hop-bounded and result-capped per R4.
- An explicit, written decision (not a silent default) on whether/how a graph term (e.g., reference count, cluster co-membership, supersession recency) enters the RRF scoring function, backed by a small before/after eval on this KB's own query logs rather than assumed from external benchmarks.

#### Success Criteria
- "What's the blast radius of changing this record?" is answerable via API, bounded, in the workbench.
- Retrieval-integration decision is written down with the evidence used to make it (see Supplemental Data), not implemented speculatively.

---

## Sprint Plan

| Sprint | Description | Tasks |
| ------ | ----------- | ----- |
| 1 | Wire dormant index, de-duplicate reverse-reference logic | G-1, G-2 |
| 2 | Close remaining TSK-0268 criteria | G-3 |
| 3 | Typed-edge schema + migration off tag-smuggled relations | G-4, G-5 |
| 4 | Fix supersession-chain / cycle-dedup / cluster-ceiling bugs | G-6, G-7, G-8 |
| 5 | Incremental topic-map updates | G-9 |
| 6 | Bounded traversal APIs | G-10 |
| 7 | Retrieval-integration evaluation (decision gate, not a guaranteed build) | G-11 |

---

## Task Table

| ID | Sprint | Name | Description | Confidence |
| -- | ------ | ---- | ----------- | ---------- |
| G-1 | 1 | Wire `MemoryIndex.ByReference`/`ByConflict` into live reverse-reference lookups | Replace both O(n) scans with O(1) index reads | 92% |
| G-2 | 1 | De-duplicate reverse-reference computation | One implementation, used by workbench + context-pack | 90% |
| G-3 | 2 | Close TSK-0268 remaining criteria | Pages "Linked from" backlinks + dedicated API endpoint; correct task status | 88% |
| G-4 | 3 | Design typed-edge schema | `relationType`/`origin`/`provenance` model, additive alongside `References`/`Conflicts` | 75% |
| G-5 | 3 | Migrate tag-smuggled relations to typed edges | One-time backfill from `ExtractTagRelationships`'s parsing convention; add target-existence validation | 80% |
| G-6 | 4 | Fix `BuildSupersessionChains` to follow multi-hop chains | Currently emits one 2-element pair per edge, not an actual chain | 90% |
| G-7 | 4 | Fix `FindDependencyCycles` duplication/complexity | Shared visited-set, dedupe rotated cycle representations | 82% |
| G-8 | 4 | Add a ceiling/weighting to `BuildClusters` | Prevent one generic tag from becoming a useless giant cluster | 70% |
| G-9 | 5 | Make topic-map builds incremental | Update affected subgraph on record edit instead of full weekly/on-demand rebuild | 60% |
| G-10 | 6 | Bounded k-hop / shortest-path / blast-radius traversal API | Built on the Phase 2 typed-edge store | 65% |
| G-11 | 7 | Evaluate graph-aware reranking with an actual before/after test | Decision gate informed by 2026 GraphRAG-vs-agentic-search benchmarking, not assumed | 55% |

---

## Task List

### G-1: Wire the dormant `MemoryIndex` into reverse-reference lookups

#### Description
`MemoryIndex.ById`, `.ByTag`, and `.ByReference` are populated on every `Add`/`Remove`/`Rebuild` call (`MemoryApplicationService.cs`, `MemoryMaintenanceTasks.cs`) but have **zero read call sites** anywhere in `MemorySmith.App`, `MemorySmith.Core`, or `MemorySmith.Storage` — confirmed by grep across all non-test `.cs` files in all three projects. Meanwhile both real reverse-reference features (`GetReverseReferencesAsync` in `MemoryApplicationService.cs`, and the inline `reverseRefMap` builder used for context-pack assembly) independently call `_store.LoadAll()` and linearly scan every record's `References`/`Conflicts` list on every single request.

#### Detailed Steps
1. Add `ByConflict` to `MemoryIndex` (currently only `ByReference` exists; `Conflicts` isn't indexed at all, an asymmetry worth closing in the same pass).
2. Replace `GetReverseReferencesAsync`'s scan with `_index.ByReference.GetValueOrDefault(id)` (+ `ByConflict`), falling back to the existing scan only if the index is ever found to be stale (shouldn't be, given it's updated transactionally with the store — verify this invariant as part of the change).
3. Replace the inline `reverseRefMap` construction in the context-pack path with a call to the same method from step 2, for a batch of IDs.
4. Add a test that edits a record's `References`, then immediately queries reverse-references for the target, with no intervening rebuild — proves the index stays live-consistent.

#### Test Plan
- Unit test: index-based lookup returns identical results to the old O(n) scan on a fixture with 50+ interlinked records (regression-proof the migration).
- Perf smoke test: reverse-reference lookup time is flat as corpus size grows in a synthetic 10k-record fixture (proves O(1) vs the old O(n)).

#### Sources
- `MemorySmith.Core/Indexing/MemoryIndex.cs` (full file, 45 lines) — confirms `ByReference` dictionary exists and is populated.
- `MemorySmith.App/Services/MemoryApplicationService.cs:489-500` (doc comment explicitly ties this method to TSK-0268) and `:350-380` (inline duplicate).
- Confirmed via `grep -rn "\.ByReference\|\.ByTag\|\.ById\[" ` across all `MemorySmith.App/*.cs`, `MemorySmith.Core/*.cs`, `MemorySmith.Storage/*.cs` (non-test) — zero read sites found.

---

### G-2: De-duplicate reverse-reference computation

#### Description
Two independent implementations of "what points to this record" exist: a single-ID method (`GetReverseReferencesAsync`, used by `MemoryViewer.razor`'s "Incoming References" panel) and a batch inline builder (used only inside context-pack assembly in `MemoryApplicationService.cs`). They already agree on semantics (both scan `References` + `Conflicts`), so this is a pure consolidation, best done in the same change as G-1 since both need the same underlying index-backed primitive anyway.

#### Detailed Steps
1. Extract a single `GetReverseReferencesAsync(IReadOnlyCollection<string> ids)` batch-capable method backed by the G-1 index.
2. Have the single-ID workbench call site pass a one-element collection.
3. Delete the inline `reverseRefMap` construction in the context-pack path in favor of this method.

#### Test Plan
- Existing context-pack tests and `MemoryViewer` tests (if any) continue to pass unmodified — proves behavior parity, not just code consolidation.

#### Sources
- Same as G-1.

---

### G-3: Close TSK-0268's remaining criteria and correct its tracked status

#### Description
TSK-0268 (status: `Backlog`) lists five acceptance criteria. Verified against code at `db04b23`:

| Criterion | Status | Evidence |
|---|---|---|
| context-pack `reverse_references` field | **Done** | `MemoryContextPackFormatter.cs:42`, `MemoryQueries.cs:54` |
| `/memories` workbench "Incoming References" panel | **Done** | `MemoryViewer.razor:382-386, 582` |
| `/pages` viewer "Linked from" backlinks | **Not done** | zero matches for "linked"/"backlink" in `Pages.razor` |
| `GET /api/memories/{id}/reverse-references` endpoint | **Not done** | zero matches for "reverse" in `MemoriesController.cs` |
| Built on load, no separate build step | **Done** (for the two implemented surfaces) | both current implementations compute on-demand |

The task tracker showing this as untouched `Backlog` work is itself worth fixing — both for accurate planning (this is a much smaller remaining task than its description implies) and because the "other audit" supplied by the user treated this as a from-scratch foothold, which would lead to redundant work if taken at face value.

#### Detailed Steps
1. Add the `/pages` "Linked from" section using the existing `ExtractPageLinks`/link-scanning logic already written for `MaintenanceTopicMapService` (don't reinvent — that regex-based markdown-link scanner already does exactly this for the topic map; reuse it as the live backlink source rather than writing a third implementation).
2. Add `GET /api/memories/{id}/reverse-references` to `MemoriesController`, backed by the G-1/G-2 consolidated method.
3. Update TSK-0268's status/description to reflect actual completion, splitting the remaining 2 criteria into their own tracked scope if that's the project's preferred granularity.

#### Test Plan
- Test: page with a markdown link to another page shows up in that target page's "Linked from" list.
- Test: new API endpoint returns identical results to the workbench panel for the same ID.

#### Sources
- `Data/Tasks/tsk-0268-add-reverse-reference-view-for-memories-and-pages.json` (full description).
- `MemorySmith.App/Components/Pages/MemoryViewer.razor`, `Pages.razor` (both fetched and grepped in full).
- `MemorySmith.App/Controllers/MemoriesController.cs` (grepped for "reverse", zero hits).
- `MaintenanceAgentServices.cs:1140-1150` (`ExtractPageLinks`, the reusable link-scanning logic).

---

### G-4 / G-5: Typed-edge schema + migration off tag-smuggled relations

#### Description
`MaintenanceTopicMapService.ExtractTagRelationships` already parses relation semantics out of `Tags` today: a tag of the shape `supersedes:tsk-0123`, `depends-on:xyz`, `conflicts-with:abc`, or `superseded-by:xyz` is interpreted as a typed graph edge (`Supersedes`/`DependsOn`/`ConflictsWith`/`SupersededBy`), entirely separately from the `References`/`Conflicts` arrays. This is undocumented outside this one method, is not validated against real record IDs (unlike the `Mentions` edge type in the same file, which does check `recordIds.Contains(...)` before creating an edge), and means the same string field (`Tags`) is simultaneously subject to tag-policy governance (case normalization, allowed-tag validation — see prior audit sessions' findings on `MemoryGovernanceServices`) and silently double-duty as relationship storage.

This is the schema change the supplied audit correctly identifies as needed — but it is a **migration**, not a green-field design, and the migration source is this existing (if fragile) convention.

#### Detailed Steps
1. Define the new edge shape: `sourceId`, `targetId`, `relationType` (closed vocabulary matching what `MaintenanceTopicMapService` already recognizes, extendable later), `origin`, `createdAtUtc`. Keep `References`/`Conflicts` as-is for backward compatibility (per constraint C1 — the RRF scorer reads `record.References` directly for `referenceTokens`).
2. Add existence validation at write time: reject/flag any edge whose target isn't a real record or page ID (closing the gap `ExtractTagRelationships` currently has).
3. One-time backfill: run `ExtractTagRelationships`'s parsing logic once over the live corpus to seed the new edge store, then stop parsing colon-prefixed tags as relations going forward (they become inert unless a human wants a literal tag with a colon in it).
4. Update the Tag Manager / tag-policy validation to no longer need to reason about which tags are "real" vs. relation-encoded, since the encoding goes away.

#### Test Plan
- Migration test: run the backfill against a fixture with several `supersedes:`/`depends-on:` tags, assert the resulting edge set matches what `ExtractTagRelationships` would have produced today (proves no semantic loss during migration).
- Validation test: attempt to create an edge to a nonexistent ID, assert rejection (this is new behavior — today's `ExtractTagRelationships` allows dangling edges silently).

#### Sources
- `MaintenanceAgentServices.cs:1105-1130` (`ExtractTagRelationships`) — full method read; confirms the tag-string convention and its lack of target validation, contrasted directly against `ExtractPageLinks`'s `Mentions` edges 20 lines earlier, which do validate.
- `MemorySmith.Core/Models/MemoryRecord.cs` — confirms `References`/`Conflicts` are raw `List<string>` today.
- `MemoryApplicationService.cs:1089` — confirms `record.References` feeds `referenceTokens` in RRF semantic scoring (C1).

---

### G-6: Fix `BuildSupersessionChains` to actually build chains

#### Description
Despite its name, `BuildSupersessionChains` does not chain anything — it converts each individual `Supersedes`/`SupersededBy` edge into its own 2-element `[source, target]` list. A record chain `A supersedes B supersedes C` produces two disconnected pairs `[A,B]` and `[B,C]` today, never `[A,B,C]`. This matters specifically for the reviewer use case the supplied audit is optimizing for: "this record was superseded, and by what, transitively" is exactly the question a human reviewer asks when cleaning up stale records, and today's output doesn't answer it.

#### Detailed Steps
1. Build a directed graph from `Supersedes` edges only (normalize `SupersededBy` to the same direction first).
2. Walk chains from each root (a node with no incoming `Supersedes` edge) to its terminal leaf, emitting one ordered list per root-to-leaf path.
3. Guard against cycles in supersession data itself (a record shouldn't supersede its own ancestor) — if found, surface as a data-quality warning rather than infinite-looping.

#### Test Plan
- Test: 3-hop chain `A→B→C` produces one 3-element chain, not two 2-element pairs.
- Test: a supersession cycle (data error) is detected and reported, not looped forever.

#### Sources
- `MaintenanceAgentServices.cs:1195-1201` (`BuildSupersessionChains`, full method read).

---

### G-7: Fix `FindDependencyCycles` duplication and complexity

#### Description
The DFS in `FindDependencyCycles` restarts a fresh `seen` `HashSet<string>` (cloned, not shared) at every recursive call and every top-level starting node, rather than sharing one global visited-set with proper recursion-stack tracking. This is correct but needlessly expensive on dense `depends-on:` graphs (no cross-run memoization of "this subtree is provably cycle-free"), and — separately — a single cycle `A→B→C→A` gets reported multiple times, once per rotation, depending which node the outer loop started from.

#### Detailed Steps
1. Rewrite using a single shared "on current recursion stack" set plus a "fully explored, no cycle" memo set, standard cycle-detection DFS — O(V+E) instead of the current per-root re-exploration.
2. Canonicalize found cycles (e.g., rotate to start at the lexicographically smallest node ID) before adding to the results list, and dedupe.

#### Test Plan
- Test: a single 4-node cycle reachable from 3 different starting tags in `graph.Keys` produces exactly one entry in the output, not three rotated copies.
- Perf test: a synthetic dense dependency graph (500 nodes, average out-degree 5) completes in bounded time, guarding against the current algorithm's worst-case blowup.

#### Sources
- `MaintenanceAgentServices.cs:1203-1230` (`FindDependencyCycles`, full method read, including the `Visit` local function's `HashSet` cloning pattern).

---

### G-8: Add a ceiling/weighting to `BuildClusters`

#### Description
`BuildClusters` groups nodes by exact-match, non-colon tags shared by more than one node, with no weighting or size cap. A broadly-applied tag (e.g., a sprint label, or a generic category tag used across dozens of records) becomes a "cluster" as large as, or larger than, any semantically meaningful grouping, and every node can belong to arbitrarily many overlapping "clusters" since there's no partitioning step. This directly affects whether the "search by cluster" UX the supplied audit proposes would actually be useful, or just surface one giant unhelpful bucket per common tag.

#### Detailed Steps
1. As a cheap first pass: exclude tags above a configurable member-count ceiling from cluster formation, and/or weight cluster membership by inverse tag frequency (rare shared tags are more informative than common ones — same intuition as TF-IDF).
2. As a follow-up (flagged low-confidence, see Task Table): evaluate whether real community detection (e.g., label propagation over the Phase 2 typed-edge graph, not just tag co-occurrence) is worth the added implementation cost, given this KB's likely corpus size.

#### Test Plan
- Test: a tag shared by 80% of a fixture corpus does not produce a "cluster" once the ceiling is applied; a tag shared by 3 semantically-related records does.

#### Sources
- `MaintenanceAgentServices.cs:1187-1194` (`BuildClusters`, full method read).

---

### G-9: Make topic-map builds incremental

#### Description
`MaintenanceTopicMapService.BuildAsync` is a full-corpus rebuild (`_memoryStore.LoadAll()` plus every page, re-scanned in full) triggered either by the weekly `run_maintenance_weekly` schedule or an on-demand `?refresh=true` call to `MaintenanceAgentController.TopicMap`. Between rebuilds, cached clusters/cycles/chains/staleness data can be up to a week stale relative to live edits — directly relevant to the "incremental updates for growing corpora" theme the supplied audit raises abstractly; this grounds it in the actual trigger model (C2) rather than inventing a new one.

#### Detailed Steps
1. On each memory/page write (`MemoryApplicationService`'s existing `_index.Add`/`Remove` call sites are natural hook points), recompute only the affected node's edges and invalidate/patch the cached `TopicMapDocument` incrementally rather than requiring a full rebuild.
2. Keep the existing full-rebuild path as a periodic consistency-repair job (still useful for correctness, just no longer the only way to stay current).

#### Test Plan
- Test: editing one record's `Tags` updates that record's node/edges in the cached topic map without a full `BuildAsync` call, and without disturbing unrelated nodes' cached state.

#### Sources
- `MaintenanceAgentServices.cs:1493` (`RunAsync` calling `_topicMap.BuildAsync`), `MaintenanceAgentController.cs:72-88` (`TopicMap`/`TopicMapMermaid` endpoints with `refresh` parameter), `MaintenanceTopicMapService.LoadCachedAsync`/`SaveCacheAsync` (full-document cache read/write, no partial-update path today).

---

### G-10: Bounded k-hop / shortest-path / blast-radius traversal API

#### Description
Today's traversal surface is limited to one-hop reverse-reference lookup (Phase 1) and a capped, non-interactive 32-node/120-edge static visualization (`TopicMapVisualization.razor`). Neither answers "what's the shortest path between these two records" or "what's the blast radius if I change this node," both explicitly called out as reviewer needs in the supplied audit and both consistent with this codebase's own existing preference for explicit hop/result bounds over unbounded LLM-driven traversal (matching the "plan/verify/execute" caution the supplied audit's citations recommend, e.g., GraphRunner-style bounded planning rather than freewheeling multi-hop).

#### Detailed Steps
1. Build on the Phase 2 typed-edge store (not the raw `Tags`-parsed edges) for correctness.
2. Implement k-hop neighborhood (bounded depth + result cap), shortest-path (bounded max depth, fail fast beyond it), and blast-radius (reverse k-hop from a given node) as API endpoints, mirroring the bounded-tool-call discipline already used elsewhere in this codebase's agent/tool-loop design.
3. Surface these in the workbench UI as an actual interactive graph (filterable by relation type/node kind/confidence, click-to-expand), replacing or supplementing the current fixed 32/120-cap circular SVG.

#### Test Plan
- Test: shortest-path between two records with no connecting path returns "no path found within N hops," not an unbounded search.
- Test: blast-radius query on a highly-connected node respects the result cap rather than returning the whole graph.

#### Sources
- `TopicMapVisualization.razor` (full component, confirms the 32-node/120-edge/fixed-layout cap with no filtering or interactivity).
- GraphRunner-style plan/verify/execute traversal, as cited (with independent verification below) in Supplemental Data.

---

### G-11: Evaluate graph-aware reranking — decision gate, not a default build

#### Description
Current hybrid scoring (`ReciprocalRankScore(lexicalRank) + ReciprocalRankScore(semanticRank)`, RRF k=60, `MemoryApplicationService.cs:993`) has no graph term. The supplied audit's citations (PathRAG, Youtu-GraphRAG, GraphRAG) are real, independently verified papers describing genuine techniques for exactly this kind of reranking/expansion. But a 2026 benchmarking paper directly on point — *"Do We Still Need GraphRAG? Benchmarking RAG and GraphRAG for Agentic Search Systems"* (arXiv:2604.09666) — found that **agentic multi-round retrieval substantially narrows the gap to GraphRAG for general QA, and GraphRAG's advantage concentrates specifically in complex multi-hop reasoning, while carrying real offline preprocessing/index-maintenance cost.** For a single-operator KB of MemorySmith's likely scale, that's a genuine reason to gate this decision on actual evidence from this system's own query patterns rather than assume graph reranking is worth building by default.

#### Detailed Steps
1. Instrument current query logs (if not already captured) to classify queries as single-hop/lookup vs. multi-hop/relational (e.g., "what superseded X" vs "explain Y").
2. Prototype one graph term (e.g., +boost for records reachable within k hops of other top-ranked results) behind a flag, evaluate on the multi-hop subset specifically, not the whole query mix.
3. Only fold into default scoring if the multi-hop subset shows a measurable improvement that isn't noise, given this is a low-traffic single-user system where statistical power will be limited — say so explicitly in the writeup rather than overclaiming significance.

#### Test Plan
- A/B-style before/after comparison on a hand-curated set of multi-hop queries (e.g., "what depends on the tag policy loader," "what superseded the original CSRF fix") vs. single-hop queries, comparing ranking quality with/without the graph term.

#### Sources
- `MemoryApplicationService.cs:21, 993, 1081-1082` (RRF implementation, k=60, no graph term today).
- PathRAG: Chen et al., "PathRAG: Pruning Graph-based Retrieval Augmented Generation with Relational Paths," arXiv:2502.14902 (verified via search, Feb 2025).
- Youtu-GraphRAG: Dong et al., arXiv:2508.19855 (verified via citation in a third paper's reference list).
- "Do We Still Need GraphRAG? Benchmarking RAG and GraphRAG for Agentic Search Systems," arXiv:2604.09666 (April 2026) — directly on point for the cost/benefit caveat above.
- Original GraphRAG: Edge et al., "From local to global: A graph rag approach to query-focused summarization," arXiv:2404.16130 (2024).

---

## Supplemental Data

### Current architecture, as verified in code (not assumed)

**Two disconnected "graph" subsystems exist today**, and neither talks to the other:

1. **Live per-request reverse-reference lookup** — `MemoryApplicationService.GetReverseReferencesAsync` / inline context-pack builder. Real-time, correct, but duplicated and O(n) (see G-1/G-2).
2. **Periodic full-corpus `TopicMapDocument`** — `MaintenanceTopicMapService`. Computes nodes, typed edges (from `References`/`Conflicts`/tag-smuggled relations/page links), clusters, "chains," cycles, staleness — but is rebuilt in full weekly or on `?refresh=true`, is not used by retrieval ranking at all, and feeds only a 32-node capped static SVG plus a Mermaid export.

Retrieval itself (`SemanticEmbeddingSearchService` + `MemoryApplicationService`'s hybrid RRF path) never touches either of these — `record.References` contributes tokens to *semantic* scoring only (bag-of-words on the reference strings), not as a structural/graph signal.

### Relation-type vocabulary already in informal use (from `ExtractTagRelationships`)
`Supersedes`, `SupersededBy`, `DependsOn`, `ConflictsWith` — plus `References`, `ConflictsWith` (from the raw arrays), `Mentions` and `LinksTo` (from page-scanning). This is a reasonable starting vocabulary for the Phase 2 typed-edge schema; no need to invent a new one from scratch as the supplied audit's proposed list does — align with what's already implicitly in production use.

### External grounding (independently verified this session, not taken from the supplied audit at face value)

| Claim | Verification |
|---|---|
| PathRAG (path-pruning graph retrieval) is a real, published technique | Confirmed, arXiv:2502.14902, Feb 2025 |
| Youtu-GraphRAG (hierarchical community detection + agentic retrieval) is real | Confirmed via cross-citation in a 2026 benchmarking paper's reference list |
| Graph structure's benefit is concentrated in multi-hop reasoning, with real offline cost, vs. general QA where dense/hybrid retrieval is competitive | Confirmed, arXiv:2604.09666 (Apr 2026) — this is the key critical counterweight the supplied audit didn't include |
| Query-aware traversal (dynamic edge weighting by query relevance) is an active, current research direction | Confirmed, QAFD-RAG, OpenReview 2025-2026 submission |

This grounds Phase 4's "decision gate" framing: the literature itself says don't assume graph reranking pays for itself — check.

---

## Out of Scope

- Full entity-extraction/knowledge-graph-construction pipeline (LLM-based triple extraction from unstructured content) — not evaluated; would be a much larger, separate proposal if the Phase 4 evaluation shows it's warranted.
- Graph database migration (Neo4j/similar) — nothing found suggests SQLite + in-process traversal is the bottleneck at this project's scale.
- UI visual redesign details for the interactive graph beyond "replace the 32-node cap with something filterable/interactive" — left to implementation-time design work.

## Assumptions

1. `MemoryIndex`'s three dictionaries are correctly kept in sync with the store on every write today (Add/Remove/Rebuild are called at every mutation site found) — I verified the call sites exist but did not load-test consistency under concurrent writes; worth a quick check before relying on it in G-1.
2. `MaintenanceTopicMapService.BuildAsync`'s weekly trigger cadence is configurable (not hardcoded) — inferred from `MaintenanceAgentConfigService` patterns seen elsewhere in the codebase during prior audit sessions, not re-verified line-by-line this session.
3. This KB's actual query volume/pattern (single operator, likely bursty rather than high-throughput) is assumed based on the project's stated nature (personal knowledge management, solo-maintained) — G-11's "limited statistical power" caveat depends on this; if usage patterns differ, revisit.

## Open Questions

1. Is there a reason `MemoryIndex.ByReference`/`ByTag`/`ById` were built and then never wired to a consumer — a leftover from a planned feature, or an intentional abstraction for a future storage backend? Worth checking git history/task archive before deleting vs. reusing it (G-1 assumes "reuse," but "it's from an abandoned feature and safe to delete instead, then build the real index fresh" is also a valid outcome if history shows the former).
2. Does the tag-policy governance system currently validate the colon-prefixed relation tags at all today, or do they slip through as arbitrary free text? This affects how risky the Phase 2 migration is (if governance already validates `supersedes:` tag values against real IDs, some of G-5's validation work may already be half-done elsewhere).
3. What's the actual current corpus size (record count, page count)? This materially affects whether G-9's incremental-update investment and G-11's reranking evaluation are worth prioritizing now vs. later — the report above assumes "small enough that O(n) scans work today but won't scale indefinitely," which should be confirmed against real numbers.

## Requested Data

- Current record/page counts and growth rate, to size the urgency of G-1 (index) and G-9 (incremental updates).
- Any existing query logs or usage analytics for MemorySmith's search/chat surface, needed to make G-11's decision gate evidence-based rather than speculative.
- Git history / original task discussion (if any) for why `MemoryIndex` was built, to resolve Open Question 1.

## Next Steps

1. Confirm Open Questions 1–3 (cheap, unblocks prioritization).
2. Implement Sprint 1 (G-1, G-2) — highest confidence, smallest diff, immediately useful regardless of what happens with Phases 2–4.
3. Re-scope TSK-0268 (G-3) to reflect actual completion before doing anything else task-tracker-facing, so subsequent planning isn't working from a stale picture.
4. Treat Phases 2–4 as sequential, gated proposals — each phase's own success criteria should be met and reviewed before starting the next, consistent with this project's stated zero-tech-debt-tolerance preference.
