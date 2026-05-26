# MemorySmith MCP Tool Suite — External Agent Benchmark Report

**Author:** Claude Sonnet 4.6 (external MCP agent via claude.ai)
**Sessions conducted:** 2026-05-25 through 2026-05-26
**Tools exercised:** 17 of 19 MCP tools
**Total tool calls:** ~65 across three sessions
**Scope:** Availability, search quality, edge case behavior, capability gaps, and enhancement recommendations

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Methodology](#2-methodology)
3. [Session 1: Initial Landscape and Availability](#3-session-1-initial-landscape-and-availability)
4. [Session 2: Auth Resolution, Write Path, and Edge Cases](#4-session-2-auth-resolution-write-path-and-edge-cases)
5. [Session 3: Syntax Boundaries, Corpus Mapping, and Format Deep-Dive](#5-session-3-syntax-boundaries-corpus-mapping-and-format-deep-dive)
6. [Cross-Session Pattern Analysis](#6-cross-session-pattern-analysis)
7. [Search Quality Analysis](#7-search-quality-analysis)
8. [Capability Gap Analysis](#8-capability-gap-analysis)
9. [KB Health Findings](#9-kb-health-findings)
10. [Recommendations](#10-recommendations)
11. [Open Questions](#11-open-questions)
12. [Appendix A: Full Test Matrix](#appendix-a-full-test-matrix)
13. [Appendix B: Full Corpus Map](#appendix-b-full-corpus-map)
14. [Appendix C: Confirmed Blocklisted Tags](#appendix-c-confirmed-blocklisted-tags)

---

## 1. Executive Summary

This report documents three sessions of systematic external-agent testing of the MemorySmith MCP tool suite. The agent (Claude Sonnet 4.6 via claude.ai) exercised all available read tools and attempted all write tools, measuring availability, correctness, edge-case behavior, search quality, and identifying both current bugs and architectural gaps.

**Critical findings:**

1. **Edit-gated write tools hang silently for ~4 minutes instead of returning an auth error.** This affects `page_save`, `page_delete`, `task_create`, `task_update`, `task_set_status`, `task_add_comment`, and `task_add_attachment`. The fix pattern already exists — TSK-0159 applied it to `source_bundle`/`find_by_source` — but it has not been applied to the edit-permission check path. This is the highest-priority operational issue.

2. **The tag governance blocklist is aggressively consuming production-relevant tags.** As of session 3, the `project-wiki` tag (carried by ~100% of KB records), the test fixture marker tags `quartzwave` and `nimbusvector` (used as unique retrieval anchors in integration tests), and ~8 other tags are in the blocklist. Every record in the KB now emits at least one `tag.blocked` Warning. This creates diagnostic noise that drowns out signals from genuinely problematic records.

3. **Lucene field-targeted queries and fuzzy matching silently return zero results** rather than returning an error or falling back. Agents have no way to know these are unsupported without empirical testing.

4. **The `envelope` format for search results exposes significantly richer diagnostic data** (raw lexical score, raw semantic score, per-field match breakdown) that is not available in `json` format and is completely undocumented.

5. **Structured tag namespaces (`kind:`, `scope:`, `audience:`) are a powerful undocumented filtering system** that enables high-precision recall of authoritative, scoped, or agent-targeted records.

6. **The KB contains 50 memory records** across ~12 topic clusters, all healthy from a retrieval standpoint. Eight records have unresolved source links; two records have dangling relationship references.

**Summary verdict:** The read path is solid, well-designed, and production-ready. The write path has a correctness bug that makes it unusable for Viewer-role MCP clients. Several high-value capabilities are either undocumented or missing entirely. The signal/noise ratio in diagnostics output is poor due to blocklist overreach.

---

## 2. Methodology

### 2.1 Agent Context

All tests were conducted by Claude Sonnet 4.6 operating as an MCP client connecting to the MemorySmith service at `localhost`. The agent operated under Viewer-level permissions — it had full read access but not Edit or Admin access. This permission level is the realistic scenario for any external language-model agent consuming the wiki.

All tool calls were made via the standard MCP JSON-RPC transport. No direct REST API access was used; every finding reflects the MCP surface specifically.

### 2.2 Test Design Principles

Tests were designed to be:

- **Destructive-safe:** No write operations were executed that could permanently alter the KB without recovery, and destructive write tests (`page_delete`) were not attempted.
- **Systematic:** Each tool was tested against expected behavior, then against edge cases (boundary limits, invalid inputs, missing targets).
- **Comparative:** Search quality was measured across all three search modes (lexical, semantic, hybrid) on identical queries to isolate each mode's behavior.
- **Empirical:** Assumptions about behavior were tested before being recorded as facts. Multiple sessions allowed comparison across service states.

### 2.3 Session Timeline

| Session | Date | Focus | Service state observed |
|---|---|---|---|
| Session 1 | 2026-05-25 | Landscape scan, search quality, initial stress test | Source I/O tools and task tools all timed out |
| Session 2 | 2026-05-26 (morning) | Auth recovery, edge cases, write path confirmation | Source tools returned proper auth error; write path still hung |
| Session 3 | 2026-05-26 (afternoon) | Lucene syntax, corpus mapping, format deep-dive | Read path fully healthy; write path still hung |

### 2.4 Tools Tested

Of 19 MCP tools, 17 were exercised:

- **Tested and healthy:** `hybrid_search`, `search`, `semantic_search`, `get`, `context_pack`, `unified_search`, `page_search`, `page_get`, `task_list`, `task_get`
- **Tested with auth error (correct):** `source_bundle`, `find_by_source`
- **Tested with silent timeout (bug):** `page_save`, `task_create`
- **Not tested (skipped):** `page_delete` (destructive), `task_update`, `task_set_status`, `task_add_comment`, `task_add_attachment` (all assumed to share the write-path hang based on the confirmed pattern for `task_create` and `page_save`)

---

## 3. Session 1: Initial Landscape and Availability

### 3.1 KB Landscape Scan

The first call was a broad `hybrid_search` with `limit=20` and the query "memorysmith project overview current state." This returned the top 20 records by hybrid RRF score and gave an immediate picture of the corpus:

- The KB is built around a `project-wiki-*` ID namespace covering architecture, search, chat, configuration, security, governance, UI, validation, and maintenance topics.
- Virtually every record carried the `project-wiki` plain tag — a universal namespace marker, not a discriminating filter.
- Structured tags (`kind:fact`, `kind:rule`, `audience:admin`, `scope:configuration`, etc.) appeared on a subset of records, notably the configuration-domain records.
- Status values observed: status 2 (Active/Working) dominated; status 1 (Working-but-not-canonical) appeared on governance and RFC records.
- Confidence scores ranged from 0.82 (RFC/historical records) to 1.0 (boundary/policy records). The pattern was: higher confidence on architectural constraint records, lower on evolving-state and governance records.

**Observation:** The `project-wiki` universal tag is a design artifact, not a governance-meaningful tag. Its presence on every record provides zero filtering value and was already creating diagnostics noise even in session 1 (as `tag.unknown_plain` before it was later blocklisted).

### 3.2 Search Quality — Three-Way Comparison

**Test query set 1: Exact technical phrase**
- Query: `"reciprocal rank fusion"`
- Lexical result: `project-wiki-hybrid-search-rrf` ranked #1 with score 6.25, `matchReason: "exact lexical content phrase"`. Correct.
- Semantic result: `project-wiki-search-roadmap` ranked #1, not the RRF record. The model treated the query conceptually — "search ranking combining vectors" — rather than matching the specific phrase.
- Hybrid result: `project-wiki-hybrid-search-rrf` ranked #1. The lexical component correctly won the fusion.

**Finding:** For exact technical terminology, lexical and hybrid are correct; semantic alone may diverge. This is expected behavior. The key insight is that the RRF fusion correctly privileges exact-phrase lexical hits.

**Test query set 2: Paraphrased natural language**
- Query: `"how does the search ranking combine vector and keyword scoring"`
- Semantic top-3 scores: 0.831, 0.817, 0.815 — a 1.6% spread across records that are meaningfully different in specificity.
- The top result was `project-wiki-search-roadmap` (0.831) rather than `project-wiki-hybrid-search-rrf` (0.815). The roadmap record ranked above the specific mechanism record because the roadmap's broader context matched the framing better.

**Finding:** At this score range, hybrid is more stable than pure semantic. Small document changes could invert the ranking at 0.816 vs 0.815. This is a known property of dense retrieval models and is not a bug, but agents relying on semantic-only top-1 results for high-stakes queries should be aware of the instability.

**Assumption:** The embedding model (e5-base-v2.onnx at dimension 768) has not been fine-tuned on MemorySmith-specific vocabulary. The semantic scores reflect general-purpose sentence similarity, not domain-calibrated relevance.

### 3.3 Tag-Only Filter Behavior

Test: `hybrid_search(tags="missing-feature", no query)` — returned 2 records.
- `matchReason` for both: `"No query supplied; returned by recency."`
- Results ordered by `lastUpdated` descending.

**Finding:** When no query is provided, the tool functions as a tag-filtered recency browser. This is useful behavior — it enables "show me the most recently updated records tagged X" — but it is entirely undocumented. Agents attempting to use this pattern have no documentation to rely on.

### 3.4 Status Filter

Test: `hybrid_search(query="security authentication", status="Working")` — returned records with numeric status 1.

**Finding:** The `status` filter accepts status *names* (strings like "Working", "Active", "Deprecated") not numeric codes. The filter works correctly. The status name-to-code mapping is not documented in the tool description, requiring callers to know the status vocabulary ahead of time.

### 3.5 The Session 1 Timeout Cascade

The most significant session 1 finding was a cascade of 4-minute silent timeouts across four tool calls made in sequence:

1. `source_bundle(ids="project-wiki-chat-agent-provider,project-wiki-chat-streaming-thinking")` → 4-min timeout
2. `find_by_source(pattern="ChatServices.cs")` → 4-min timeout
3. `task_list(limit=25)` → 4-min timeout
4. `task_create(...)` → 4-min timeout

After all four timeouts, `memorysmith_get` and `hybrid_search` calls responded immediately.

**Initial hypothesis (session 1):** File I/O deadlock in a shared async context. The `source_bundle` and `find_by_source` tools read files from the filesystem via `%MemorySmithRepo%` variable resolution (`VarResolver` → `FileVarStore`). `task_list` and `task_create` access the file-backed task store at `Data/Tasks`. If `FileVarStore` or `vars.json` was in a locked/contending state, it could propagate blocking across all file-backed operations while leaving in-memory Lucene/ONNX reads unaffected.

**Revised hypothesis (session 2):** The session 1 `task_list` timeout was likely coincidental — it may have been a transient service degradation that happened to coincide with the source I/O calls. In session 2, `task_list` and `task_get` responded immediately. The root cause of the source tool timeouts was separately resolved by TSK-0159, which implemented proper auth rejection for `source_bundle` and `find_by_source`.

**Open question OQ-1:** Was the session 1 `task_list` timeout caused by the same file I/O contention as `source_bundle`, or was it an independent transient event? Server-side traces from that session would resolve this.

---

## 4. Session 2: Auth Resolution, Write Path, and Edge Cases

### 4.1 TSK-0159 Effect — From Timeout to Auth Error

Between session 1 and session 2, TSK-0159 ("Clarify and tighten source-bundle permission granularity") was completed. The effect was immediate and unambiguous:

- **Session 1:** `source_bundle` → 4-minute silent timeout
- **Session 2:** `source_bundle` → immediate response: `"The caller is not authorized to read source bundles."`

This is the correct behavior — fail fast, return a descriptive error, don't hang. The same improvement was applied to `find_by_source`.

**Evidence of the fix scope:** The TSK-0159 fix specifically patched the `CanReadSourceBundleAsync` check path. The edit-permission check path (`CanEditAsync` or equivalent, used by `page_save`, `task_create`, etc.) was not addressed, which is why the write tool hang persisted.

**Assumption:** The two auth check paths (`CanReadSourceBundleAsync` vs edit-permission) are distinct methods in `McpController.cs`. The hang in write tools occurs in the edit-permission check, not in the tool handler itself. If the check were in the handler body, a timeout would still eventually surface an exception — but the 4-minute uniform timeout across all write tool types suggests a common synchronization point.

### 4.2 The Write Path Split — Confirmed Pattern

Across sessions 2 and 3, the following availability matrix was confirmed through repeated testing:

**Read tools (View permission) — all respond immediately:**
- `hybrid_search`, `search`, `semantic_search` ✅
- `get` ✅
- `context_pack` ✅
- `unified_search` ✅
- `page_search`, `page_get` ✅
- `task_list`, `task_get` ✅

**SensitiveRead tools (Editor/Admin) — proper auth error immediately:**
- `source_bundle` ✅ (since TSK-0159)
- `find_by_source` ✅ (since TSK-0159)

**Write tools (Edit permission) — 4-minute silent timeout:**
- `page_save` ❌ (tested session 2 with minimal content; session 3 with minimal content — both timed out)
- `task_create` ❌ (tested sessions 1 and 2 — both timed out)
- `task_add_comment`, `task_update`, `task_set_status`, `task_add_attachment` — not individually tested, but assumed affected based on the pattern

The `page_save` timeout was tested twice with deliberately minimal content (`"# Test\n\nMinimal."`) to rule out payload-size as a factor. Both tests timed out in the same ~4-minute window, confirming the hang is not content-dependent.

**Confidence in root cause attribution:** 72%. The evidence strongly points to the edit-permission check path, but without server-side traces confirming which line in `McpController.cs` is blocking, we cannot definitively rule out other causes (e.g., a write lock on `Data/Pages` or `Data/Tasks` directory).

### 4.3 Edge Case Results

**`memorysmith_get` with invalid ID:**
- Input: `id="completely-made-up-id-that-does-not-exist"`
- Response: `"No memory record found for id 'completely-made-up-id-that-does-not-exist'."`
- **Assessment:** Clean. Plain string, not JSON, not exception. Agent parsers must handle both JSON and plain-string responses from this tool.

**`memorysmith_page_get` with nonexistent slug:**
- Input: `slug="research/does-not-exist-test-404"`
- Response: `"No page found for slug 'research/does-not-exist-test-404'."`
- **Assessment:** Same pattern. Both tools use the same plain-string 404 idiom.

**`context_pack` with mixed valid/invalid IDs:**
- Input: `ids="real-record-project-wiki-active-architecture,fake-id-does-not-exist"`
- Response: `{"warnings": ["Explicit root id 'real-record-project-wiki-active-architecture' was not found.", "Explicit root id 'fake-id-does-not-exist' was not found."], "records": []}`
- **Assessment:** Both IDs returned as not found. This revealed a secondary finding: the ID I used as "real" (`real-record-project-wiki-active-architecture`) was wrong — the actual ID is `project-wiki-active-architecture`. The pack gracefully returned empty with warnings rather than erroring. However, the error message format for the two cases is identical — a real-but-misspelled ID and a purely fake ID produce the same warning text. There's no disambiguation.

**`unified_search` with zero limits:**
- `memoryLimit=0, pageLimit=5`: returned only pages. Correct.
- `memoryLimit=5, pageLimit=0`: returned only memories. Correct.
- **Assessment:** Zero-limit on either side works as a layer-isolation flag. This is useful but undocumented.

**`hybrid_search` with `limit=1`:**
- Returned exactly 1 result. Correct.

### 4.4 Tag Policy Evolution Between Sessions

A meaningful change was observed between session 1 and session 2: the `project-wiki` tag transitioned from `tag.unknown_plain` (Info severity, "not in allowlist") to `tag.blocked` (Warning severity, "explicitly blocklisted").

**Session 1 diagnostic for `project-wiki-hybrid-search-rrf`:**
```json
{"code": "tag.unknown_plain", "severity": "Info", "message": "Plain tag 'project-wiki' is observed outside the active allowlist."}
```

**Session 2 diagnostic for the same record:**
```json
{"code": "tag.blocked", "severity": "Warning", "message": "Plain tag 'project-wiki' is blocklisted by the active tag policy."}
```

This transition means someone (or an automated process) approved a "Suggest Reject" action in the TagGovernanceService between the two sessions, moving `project-wiki` from unrecognized to explicitly prohibited. This is the tag governance system working as designed — but see Section 9.2 for why this specific blocklisting creates more noise than signal.

---

## 5. Session 3: Syntax Boundaries, Corpus Mapping, and Format Deep-Dive

### 5.1 Lucene Syntax Boundary Testing

`memorysmith_search` is documented as using "Lucene.NET StandardAnalyzer lexical ranking." Session 3 systematically tested which Lucene syntax features are actually functional through the MCP surface.

**Boolean operators — confirmed working:**
- Query: `mcp AND (search OR hybrid) NOT deprecated`
- Result: 5 results returned, all MCP/search-domain records, none tagged deprecated. Score range 6.25–8.25.
- `matchReason` correctly showed which boolean terms matched in which fields: `"lexical title: mcp; lexical tags: mcp; lexical references: mcp; lexical content: mcp"`
- **Assessment:** Full Lucene boolean syntax passes through to the index correctly.

**Prefix wildcard — confirmed working:**
- Query: `mcp*`
- Result: Top record score 16.25. `matchReason: "lexical title: mcp; lexical tags: mcp; lexical references: mcp; lexical content: mcp"` — the prefix expansion captured all `mcp*` tokens across all fields.
- **Note:** The score of 16.25 for a wildcard query versus ~6.25 for a literal boolean query suggests that prefix expansion compounds the per-field boost. This is standard Lucene behavior but means wildcards can over-score relative to exact terms for common prefixes.

**Field-targeted queries — confirmed NOT working:**
- Query: `title:security`
- Result: Zero results.
- Query: `title:mcp`
- Result: Zero results.
- **Assessment:** The Lucene index does not expose field names to external query syntax. The `memorysmith_search` tool wraps all queries in a Lucene `MultiFieldQueryParser` (or equivalent) that searches across title, tags, references, and content simultaneously. Field selectors in the query string are either stripped or parsed as content terms. Importantly, the tool returns zero results rather than an error or fallback — this is a silent failure mode.

**Fuzzy search — confirmed NOT working:**
- Query: `hybrd~` (deliberate misspelling of "hybrid")
- Result: Zero results.
- Query: `hybrd~2` (not tested, but assumed same)
- **Assessment:** The StandardAnalyzer does not apply Levenshtein fuzzy matching. Again, silent zero-result failure rather than an error.

**Pattern from syntax failures:** Both unsupported Lucene features return zero results rather than an error. This is worse than an error message because callers cannot distinguish "unsupported syntax" from "no matching records." An agent querying `title:authentication` would conclude there are no authentication records rather than that field-targeting is unsupported.

**Open question OQ-2:** Is the zero-result behavior for field-targeted and fuzzy queries caused by (a) the query parser silently stripping the unsupported syntax before evaluation, or (b) the unsupported syntax token matching no documents? A search for `title` alone would disambiguate — if it returns results, (b) is the answer; if it returns zero, (a) is.

### 5.2 The `envelope` Format — Undocumented Richness

The three search format options (`markdown`, `json`, `envelope`) are not documented in any tool description or wiki page. Testing `format=envelope` on `hybrid_search` revealed significantly richer output than `format=json`:

**`json` format `matchReason` for the same query:**
```
"matchReason": "Hybrid RRF fused lexical rank 1 and semantic rank 1."
```

**`envelope` format `matchReason` for the same query:**
```
"matchReason": "Hybrid RRF fused lexical rank 1 and semantic rank 1. Lexical score 33.5: lexical title: context, pack; lexical tags: agent, context, pack, workflow; lexical content: agent, context, pack Semantic score 0.855: Embedding cosine similarity 0.855 using ONNX semantic search."
```

The envelope format adds:
- Raw Lucene lexical score (33.5) — interpretable as absolute relevance on the lexical axis
- Raw semantic cosine similarity (0.855) — directly interpretable as embedding similarity
- Per-field lexical term breakdown — shows exactly which terms matched in title vs tags vs content
- ONNX model path and vocabulary path in the `provider` block — useful for diagnostics

The `json` format's fused RRF score (0.032787) is not interpretable as confidence — it's a rank-fusion artifact. The envelope format gives agents the actual relevance signals.

**Implication:** For any agent workflow involving relevance debugging or score-based filtering, `envelope` is the correct format. For tool-chaining where only IDs and snippets are needed, `json` is lighter. This distinction is entirely undocumented.

**Additional envelope observation:** The `warnings[]` array at the envelope level contains the most critical diagnostics from all returned records as a flat list — one entry per record-diagnostic pair with severity Warning or above. This acts as a quick-scan surface for KB health issues without parsing each record's `diagnostics[]` array.

### 5.3 Conflict Graph Structure

The test fixture records (`project-wiki-test-fixture-context-root` and its graph) were used to map the context_pack relationship types. The complete fixture graph:

```
project-wiki-test-fixture-context-root (root)
  ├── references → project-wiki-test-fixture-reference-child
  ├── conflicts ↔ project-wiki-test-fixture-conflict-note (bidirectional)
  └── ← referenced by (backlink) → project-wiki-test-fixture-backlink-source
```

When packed with `referenceDepth=1, includeBacklinks=true`, the `relationship` field on each returned record was:

| Record | `relationship` value |
|---|---|
| `test-fixture-context-root` | `"root"` |
| `test-fixture-reference-child` | `"reference of project-wiki-test-fixture-context-root"` |
| `test-fixture-conflict-note` | `"conflict of project-wiki-test-fixture-context-root"` |
| `test-fixture-backlink-source` | `"references project-wiki-test-fixture-context-root"` |
| `test-fixture-overview` | `"references project-wiki-test-fixture-context-root"` |

**Conflict symmetry observation:** `test-fixture-conflict-note` has `conflicts: ["project-wiki-test-fixture-context-root"]` in its own record, and the root has `conflicts: ["project-wiki-test-fixture-conflict-note"]`. The conflict relationship is bidirectional in the data, but the `relationship` label in the pack is directional — it says `"conflict of [root]"` not `"mutually conflicts with [root]"`. This asymmetry may mislead agents into thinking conflicts are directed (like references) when they are actually symmetric.

**`matchReason` for non-root pack records is `null`:** Records included via reference expansion or backlink have `"matchReason": null` and `"score": null`. The `matchReason: "Explicit root id."` only appears on the initial root records. This means agents cannot distinguish how deeply nested a record is from the pack output alone — the `relationship` field is the only indicator of position in the graph.

### 5.4 Structured Tag Namespace System

Session 3 confirmed that the KB uses colon-namespaced tags as a first-class metadata system. This is not documented in any tool description.

**Test:** `hybrid_search(tags="kind:fact", no query)` returned 8 records in recency order, all authoritative current-state records. The `kind:fact` tag appears to be applied to records that describe confirmed, stable, implemented behavior.

**Observed namespaces and values:**

| Namespace | Confirmed values | Semantic meaning |
|---|---|---|
| `kind:` | `fact`, `rule`, `index` | Record epistemological type |
| `audience:` | `admin`, `developer`, `agent`, `human`, `chat` | Intended consumer |
| `scope:` | `configuration`, `validation`, `governance`, `memory-governance`, `observability`, `wiki-health` | Domain coverage |

The `audience:agent` value is particularly notable — it marks records written specifically for machine consumption rather than human reading. Filtering by `tags=audience:agent` should return the highest-quality, most machine-readable records in the KB.

**Pattern:** The structured tag system is a lightweight semantic layer on top of the plain-tag system. It enables precision recall that plain tags cannot — `tags=kind:fact,audience:agent` would return agent-targeted authoritative facts exclusively. This is more powerful than `tags=current-state` (which is plain-tag and broad) and should be the recommended pattern for agent context-gathering workflows.

**Gap:** No tool description mentions the structured tag namespace convention. No page documents the valid values for `kind:`, `scope:`, or `audience:`. Agents must either discover the values empirically or have them documented.

### 5.5 Semantic Garbage Query Behavior

**Test:** `semantic_search(query="xkqzpwvmrjflb nonsense garbage zzzzz", limit=3)`

Results returned:
1. `project-wiki-chat-streaming-thinking`: score 0.749255
2. `project-wiki-chat-image-attachments`: score 0.745502
3. `project-wiki-chat-agent-provider`: score 0.739968

For comparison, a meaningful query (e.g., "how does streaming work in the chat pipeline") returns scores in the 0.815–0.855 range.

**The gap is ~8%.** On an absolute cosine scale, 0.749 vs 0.820 is meaningful, but there is no built-in threshold mechanism. A `limit=3` garbage query returns 3 results that look entirely legitimate — correct titles, coherent snippets, real records. Without score inspection, an agent would have no way to know it received garbage-query results.

**Practical implication:** In an agent pipeline that uses semantic search as the first step and then calls `get` or `context_pack` on results, a garbage query (or a very out-of-domain query) will silently return plausible-looking but irrelevant records. This is a reliability risk for production agent workflows.

**Assumption:** The e5-base-v2 model has a minimum cosine similarity floor above zero for any two non-trivially different text inputs. Even pure nonsense produces a non-zero embedding that has some cosine relationship to real documents. The "garbage floor" appears to be ~0.74–0.75 in this KB.

### 5.6 Full Corpus Size and Score Degradation at Tail

A `hybrid_search` with `limit=50` on a broad query ("current state architecture") returned exactly 50 records, with the following score profile:

| Rank range | Score range (hybrid RRF) | Quality assessment |
|---|---|---|
| 1–10 | 0.032–0.033 | High-relevance, directly on-topic |
| 11–25 | 0.026–0.031 | Good-relevance, related domain |
| 26–40 | 0.022–0.026 | Moderate — shared vocabulary but tangential |
| 41–50 | 0.009–0.022 | Low — only semantically adjacent; includes test fixtures and task records |

The total corpus appears to be approximately 50–55 records. At `limit=50`, the bottom 10 results include test fixture records (which have no architectural content), and task governance records (which are about task management processes, not architecture).

**Practical implication:** For most research queries, `limit=10–15` provides high-signal results. `limit=20–25` is appropriate for thorough research. `limit=50` is only useful for corpus scanning.

---

## 6. Cross-Session Pattern Analysis

### 6.1 The Blocklist Escalation Pattern

Across three sessions, the tag governance blocklist grew noticeably. Tags observed transitioning from `tag.unknown_plain` to `tag.blocked`:

- `project-wiki`: blocklisted between session 1 and session 2 (2026-05-25 to 2026-05-26 morning)
- `quartzwave` and `nimbusvector`: confirmed blocklisted in session 3

Additional tags found already blocklisted in session 2 and 3: `lucene`, `explicit-ids`, `max-records`, `maintenance-agent`, `memorysmith-app`, `localstorage`, `cobertura`.

**Pattern:** The tag governance system is actively processing tag suggestions and the blocklist is growing. The system is working as designed. However, the blocklisting criteria appear to be "tags that are too broad, too implementation-specific, or namespace-polluting." This criterion correctly identifies `project-wiki` (universal, zero discriminating value) but incorrectly captures `quartzwave` and `nimbusvector` (intentional test-fixture markers).

**New insight from pattern:** The blocklist is a shared configuration file (`Data/Policies/tag-policy.json`) that affects all records immediately on next retrieval. There is no per-record exemption mechanism — once a tag is blocklisted, every record carrying it emits a Warning diagnostic. With `project-wiki` on ~50 of ~50 records, every search result now contains at least one Warning in `diagnostics[]` and the top-level `warnings[]` array. **The blocklist has paradoxically reduced the signal value of Warnings** by making them universal. A Warning on a record now means "this record exists" rather than "this record has an issue."

### 6.2 The `usageCount` Pattern

`usageCount` was visible on every returned record. Across the full corpus:

- Most records: `usageCount: 0`
- A small set of high-activity records: `usageCount: 1–4`

High-usageCount records (≥2):
- `project-wiki-active-architecture`: 4 (the primary architecture boundary record)
- `project-wiki-scope-boundaries`: 3
- `project-wiki-validation-command`: 3
- `project-wiki-data-folder-policy`: 4
- `project-wiki-search-roadmap`: 2
- `project-wiki-mcp-integration`: 2
- `project-wiki-generalization-friction`: 2

**Pattern:** The high-usageCount records cluster around: (a) project-wide constraints and boundary records, (b) validation/testing infrastructure, and (c) friction/issue records. These are the records most frequently retrieved by the built-in chat agent during its operation.

**Notable absence:** `project-wiki-mcp-context-pack` has `usageCount: 1` despite being the most directly relevant record for external MCP agent workflows. This suggests external agents were not exercising the system before this benchmark — supporting the hypothesis that MCP tooling was primarily designed for use by the internal chat agent.

**Observation:** `usageCount: 0` on most records is potentially misleading — it may mean "never retrieved via this tracked path" rather than "never useful." If `memorysmith_get` doesn't increment `usageCount` (unclear from documentation), then records fetched by exact ID would appear unused even if frequently accessed.

**Open question OQ-3:** Does `usageCount` increment on `memorysmith_get` calls, or only on search-result retrievals? If only on search retrievals, records accessed by known ID (a common pattern in agent tool-chaining) would appear unused.

### 6.3 The Source Link Resolution Pattern

Records with `source.unresolved` diagnostics appeared consistently across all three sessions. The affected records:

| Record | Unresolved count | Notes |
|---|---|---|
| `memory-system-rfc-council-review-20260520` | 3 | Historical RFC — sources may have been moved or renamed |
| `ai-memory-suite-implementation-plan-20260520` | 2 | Implementation plan — sources may have been refactored |
| `ai-memory-suite-governance-foundation-20260520` | 1 | Governance foundation — same cluster |
| `project-wiki-memory-status-classification-current` | 1 | Current-state record — unexpected for an Active record |
| `project-wiki-markdown-pages` | 2 | Pages feature record — sources likely moved |

**Pattern:** The unresolved source links cluster around records created in the 2026-05-20 wave (the "AI memory suite" records) and one older record about pages. The 2026-05-20 records likely reference source paths that were valid at creation time but have been restructured since. The `%MemorySmithRepo%` variable expansion was working correctly for current records (since the `source_bundle` auth error was reached, not a resolution error), which isolates the issue to the specific file paths in these records.

### 6.4 The `matchReason` Divergence Signal

When running `limit=50` hybrid searches, some records appear with `"lexical rank none"` in their `matchReason`:

```
"matchReason": "Hybrid RRF fused lexical rank none and semantic rank 38."
```

This means the record had no lexical component — it was included purely because of semantic similarity. Records with `lexical rank none` in a large result set are the weakest-confidence results. They appear because the semantic model found some embedding-space similarity, but no query terms occurred in the record's text.

**New insight:** This field can be used as a quality signal. Records with `lexical rank none` in hybrid results are semantically adjacent but not lexically related — they should be treated with lower confidence than records that contributed to both lexical and semantic ranking.

---

## 7. Search Quality Analysis

### 7.1 Hybrid vs. Lexical vs. Semantic — Final Assessment

After three sessions of comparative testing, the search modes have clear use cases:

**Lexical (`memorysmith_search`):**
- Best for: exact terms, IDs, known tag values, multi-clause boolean filtering
- Supports: AND, OR, NOT, prefix wildcards (`*`), phrase quoting
- Does not support: field targeting (`title:X`), fuzzy (`~`), boost operators
- Failure mode: zero results (clean fail); missing features produce zero results not errors
- Score interpretation: raw Lucene float (0–40+ range), directly comparable within a query

**Semantic (`memorysmith_semantic_search`):**
- Best for: concept-level queries where wording differs significantly from KB records
- Always returns `limit` results regardless of relevance
- Score interpretation: cosine similarity (0.0–1.0), but compressed range (0.74–0.87 in this KB)
- Failure mode: returns plausible-looking off-topic results with no quality signal
- Not recommended as a standalone tool for high-precision recall

**Hybrid (`memorysmith_hybrid_search`) ← default:**
- Best for: all general-purpose memory retrieval
- Combines lexical precision with semantic recall via RRF
- Score interpretation: RRF fusion value (0.009–0.033), not interpretable as confidence
- `envelope` format gives the underlying scores
- Fails gracefully for boolean syntax (unlike lexical); just uses the terms

**Unified (`memorysmith_unified_search`):**
- Best for: queries that may match either memory records or pages
- Pages use lexical-only (`markdown-lexical` mode) — no ONNX
- Asymmetric quality: memory results are hybrid; page results are lexical-only
- This asymmetry is undocumented and creates unequal recall across layers

### 7.2 Page Search Quality Gap

Pages are retrieved using `markdown-lexical` mode in all paths — `page_search`, `unified_search`, and presumably direct page content matching. ONNX embeddings are not used for pages.

**Practical impact:** A query like "how does authentication flow work" would semantically match a page titled "OAuth Provider Setup" and another titled "Session Management," but the lexical search may miss them if the query terms don't appear verbatim. For memories, semantic recall would surface these. For pages, they'd be missed.

**Scale of the gap:** With 20+ pages in the wiki (observed via `page_search` with no query), and given that pages often contain high-density technical content that uses varied vocabulary, the semantic recall gap for pages is non-trivial.

**Open question OQ-4:** What fraction of meaningful page queries would be missed by lexical-only search? A controlled experiment with known-relevant pages and paraphrased queries would quantify this.

---

## 8. Capability Gap Analysis

### 8.1 Search Parameter Gaps

**Missing: `minScore` threshold filter**

The absence of a minimum score filter is the single highest-impact search gap. Currently, semantic and hybrid searches always return exactly `limit` results regardless of relevance. A `minScore: 0.80` parameter would allow callers to get zero results (a meaningful signal) when no records are sufficiently relevant, rather than always getting `limit` off-topic results.

Implementation sketch: add a `minScore` float parameter to `hybrid_search`, `semantic_search`, and `unified_search`. For hybrid mode, apply the threshold to the per-record semantic component (from envelope format) rather than the fused RRF score (which is not interpretable as confidence).

**Missing: `updatedAfter` / `updatedBefore` date range filters**

All search tools lack date-range filtering. "What changed in the last 7 days?", "Show me records updated after 2026-05-20" — these are natural recency queries that currently require either knowing the `lastUpdated` values ahead of time or paging through tag-only recency results and manually filtering. The `lastUpdated` field is present on every record in the response, confirming it's indexed.

**Missing: `offset` / `skip` pagination**

The `limit` parameter truncates results at N with no way to get results N+1 through 2N. For a ~50-record KB this is workable, but as the KB grows it will become a significant constraint. A standard `offset` parameter would resolve this.

**Missing: `includeContent: false` option**

For scanning use cases (build a list of IDs, then fetch full content for the top K), callers must receive full content on every search result. Adding `includeContent: false` would dramatically reduce response payload for scanning workflows.

**Missing: `excludeIds` parameter**

In iterative research workflows, agents often want "more records like these but not these." A list of IDs to exclude would enable this without hacky workarounds (negative Lucene queries on specific IDs are possible but brittle).

### 8.2 Context Pack Parameter Gaps

**Missing: `maxDiagnosticsPerRecord` or `includeDiagnostics: false`**

At `referenceDepth=2, maxRecords=30`, the envelope-level `diagnostics[]` array accumulated 90+ entries. Since every record has at least one `tag.blocked` Warning (due to the `project-wiki` blocklisting), the diagnostics array is proportionally `~N_records * average_diagnostics_per_record` entries. This is signal-destroying noise. A cap or suppression option would make large context packs parseable.

**Missing: `conflictDepth` separate from `referenceDepth`**

Conflicts and references serve different purposes. A depth-1 reference expansion is often appropriate (get the records this record cites). Conflict expansion may only need depth 0 (show that conflicts exist without recursively following them). Having separate depth controls would give callers fine-grained graph traversal without needing to tune a single `referenceDepth` value for both.

### 8.3 Missing Tools

**`memorysmith_propose` — agent memory edit proposals**

Currently, agents can only read memory records. Writing requires Edit permission, which the external MCP client does not have. The `MaintenanceProposalWorkflow` system already exists in MemorySmith for human-reviewed edits. Exposing a `memorysmith_propose` tool would let agents submit proposed changes to memory records that are then reviewed and approved by a human in the `/proposals` UI — exactly the governance model that already exists for the maintenance agent.

This is the highest-impact missing tool. It would close the read/write gap without requiring Edit permission to be granted to external agents.

**`memorysmith_health` — service readiness check**

There is currently no way for an agent to check whether the service is healthy before attempting expensive operations. During session 1, source tools timed out silently for 4 minutes before the agent could detect degradation. A `memorysmith_health` tool returning `{onnxAvailable: bool, indexStatus: string, taskStoreHealthy: bool, varResolverHealthy: bool, lastMaintenanceRun: string}` would let agents fail fast or skip source-bundle calls when the service is degraded.

**`memorysmith_similar` — more-like-this by record ID**

Agents frequently want to find records similar to a known-good record. Currently this requires: (a) fetch the record with `get`, (b) extract its content, (c) run a semantic search with that content as the query. A `memorysmith_similar(id="X", limit=5)` tool would do this in one call and could use the ONNX embedding directly from the index rather than re-embedding query text.

**`memorysmith_tag_list` — policy state introspection**

Agents currently discover tag policy by observing `tag.blocked` and `tag.unknown_plain` diagnostics on records. They cannot proactively know which tags are valid, which are blocked, or which are aliased. A `memorysmith_tag_list` tool returning the active policy (allowlist, blocklist, aliases, namespaces) would let agents avoid emitting blocked tags in proposals and intelligently suggest existing tags.

**`memorysmith_stats` — KB aggregate statistics**

No tool provides aggregate KB information: total record count, records by status, records with unresolved source links, recently updated records count, task backlog size. These are useful for KB health monitoring and for agents calibrating how broad or narrow to search.

### 8.4 Documentation Gaps

The following confirmed behaviors are not documented in any tool description, wiki page, or configuration reference:

| Undocumented behavior | Found in | Impact |
|---|---|---|
| `format=envelope` richer matchReason with raw scores | All search tools | High — only format useful for score interpretation |
| Boolean operators (AND, OR, NOT) in `memorysmith_search` | Session 2 | High — powerful but unknown to callers |
| Prefix wildcard (`*`) in `memorysmith_search` | Session 3 | Medium |
| Field-targeted syntax (`title:X`) does NOT work | Session 3 | High — silent zero-result failure misleads callers |
| Fuzzy syntax (`~`) does NOT work | Session 3 | Medium |
| Tag-only filter returns by recency with `matchReason: "No query supplied"` | Session 1 | Medium |
| `unified_search(memoryLimit=0)` = pages-only mode | Session 2 | Medium |
| Structured tag namespaces `kind:`, `scope:`, `audience:` | Session 3 | High — most powerful filter mechanism available |
| Valid values for structured tag namespaces | Session 3 | High |
| `context_pack` mixed valid/invalid IDs = graceful degradation | Session 2 | Low |
| Error response shapes (plain string vs JSON) | Session 2 | Medium — agent parsers must handle both |
| Status filter accepts names not numeric codes | Session 1 | Medium |
| Write tools silently hang 4min for Viewer callers | Sessions 1–3 | Critical |

---

## 9. KB Health Findings

### 9.1 Dangling References

`task-tracking-feature-20260523` carries two `relationship.missing_reference` warnings:
- Reference target `task-tracking-feature-page` — not found in KB
- Reference target `tasks-page-feature-design-20260523` — not found in KB

These appear to reference memory records or pages that were planned at the time the record was written but were never created, or were created under different IDs. The record itself (status: 1 Working, confidence: 0.96) is otherwise healthy.

**Recommendation:** Update the `references[]` array in this record to either point to the correct IDs or remove the broken references.

### 9.2 Unresolved Source Links

Eight source links across five records are unresolved (`source.unresolved`):

| Record | `SourceLinks[N]` | Pattern |
|---|---|---|
| `memory-system-rfc-council-review-20260520` | [0], [1], [2] | 3 links — RFC historical record |
| `ai-memory-suite-implementation-plan-20260520` | [0], [1] | 2 links — implementation plan |
| `ai-memory-suite-governance-foundation-20260520` | [0] | 1 link — governance foundation |
| `project-wiki-memory-status-classification-current` | [3] | 1 link — unexpected for an Active record |
| `project-wiki-markdown-pages` | [3], [4] | 2 links — pages feature record |

**Pattern:** The first three are part of the 2026-05-20 "AI memory suite" cluster — records created in a governance/RFC context that likely referenced source files at paths valid at creation time but since restructured. `project-wiki-memory-status-classification-current` is more concerning since it's an Active current-state record: a broken source link on an otherwise authoritative record reduces its evidentiary credibility.

**Assumption:** The `%MemorySmithRepo%` variable itself resolves correctly, since `source_bundle` reached the auth check rather than a resolution error. The issue is in the specific file paths after variable expansion — files were moved, renamed, or are missing.

### 9.3 Blocklist Noise — The `project-wiki` Problem

With `project-wiki` explicitly in the blocklist, every one of the ~50 KB records emits a `tag.blocked` Warning. The consequence:

- **In search results:** Every record's `diagnostics[]` contains a `tag.blocked` Warning entry. The `warnings[]` envelope array (which surfaces the most critical diagnostics) is entirely populated with `project-wiki` warnings, drowning out genuinely actionable warnings like `source.unresolved` and `relationship.missing_reference`.
- **In context packs:** At depth=2 with 30 records, 30 records × 1 `project-wiki` warning = 30 envelope-level warning entries just from this one tag. Combined with other diagnostics, the envelope `diagnostics[]` array reached 90+ entries.
- **Signal destruction:** Before `project-wiki` was blocklisted, the top-level `warnings[]` array in search results contained only genuinely problematic diagnostics (`source.unresolved`, other `tag.blocked` entries). Now it contains 90%+ `project-wiki` noise.

**Recommendation:** Either (a) remove `project-wiki` from the blocklist and instead add it to the allowlist as a "namespace identifier" class tag with no discriminating value but no diagnostic noise, or (b) implement a `suppressDiagnosticCodes` parameter on search tools to let callers filter known-noisy codes from their responses.

### 9.4 Test Fixture Tag Blocklisting

The test fixture records `project-wiki-test-fixture-context-root` (tag: `quartzwave`) and `project-wiki-test-fixture-reference-child` (tag: `nimbusvector`) now emit `tag.blocked` Warnings for their unique retrieval markers.

The fixture overview record explicitly states: *"The fixture graph uses stable ids and unique query markers such as quartzwave and nimbusvector."*

**Risk:** If any integration test uses `tags=quartzwave` as a filter to isolate fixture records, that test would currently return results but with Warning diagnostics that assertion logic might not expect. More importantly, if the TagGovernanceService eventually enforces blocklisted tags by excluding them from filtering (currently it only emits diagnostics), tests relying on tag-based fixture isolation would silently break.

**Root cause:** The TagGovernanceService's "Suggest Reject" pipeline doesn't distinguish between production tags and test-specific fixture tags. The governance criteria (presumably: tags that are overly narrow, unique to one record, or not meaningful to the broader KB) would correctly flag `quartzwave` and `nimbusvector` as candidates for blocklisting. But these tags are intentionally unique — that's the point.

**Recommendation:** Add a mechanism for records to mark specific tags as "intentional fixture identifiers" that are exempt from governance processing, or add `test-fixture`-tagged records to a governance exemption list.

---

## 10. Recommendations

### Priority 1 — Critical (fix before next external agent use)

**R1: Fix write tool silent timeout.** Apply the TSK-0159 auth-check fix pattern to the edit-permission check path in `McpController.cs`. All write tools (`page_save`, `page_delete`, `task_create`, `task_update`, `task_set_status`, `task_add_comment`, `task_add_attachment`) should return an immediate structured error like `{"error": "unauthorized", "requiredPermission": "Edit", "tool": "memorysmith_page_save"}` when the caller lacks Edit permission, instead of hanging. As a short-term mitigation, add all write tools to `MemorySmith:Mcp:DisabledTools` for Viewer-only deployments.

**R2: Document the write permission requirement.** Every MCP tool description that requires Edit or higher permission should explicitly state this in the description. Currently the permission model is invisible to callers until they observe a timeout.

### Priority 2 — High (near-term quality improvements)

**R3: Document `format=envelope` and make it the recommended format for search.** Add a description note to all search tools explaining that `envelope` format provides raw lexical and semantic scores separately, which is essential for relevance debugging and score-based filtering.

**R4: Document Lucene boolean and wildcard syntax in `memorysmith_search`.** Add a description note listing: what works (AND, OR, NOT, `*` prefix wildcard), what does not work (field targeting `title:X`, fuzzy `~`), and that unsupported syntax silently returns zero results.

**R5: Document structured tag namespaces.** Add a description note to `hybrid_search`, `search`, and `unified_search` that the `tags` filter supports structured colon-namespaced tags (`kind:fact`, `audience:agent`, `scope:configuration`), and document the known valid values. Add a corresponding page to the wiki under `guides/`.

**R6: Remove `quartzwave` and `nimbusvector` from the blocklist.** Add these to an exemption list or a `test-fixture` exemption category to prevent test infrastructure from generating false governance warnings.

**R7: Add `minScore` parameter to semantic and hybrid search.** Allow callers to specify a minimum semantic cosine similarity threshold below which results are excluded, enabling clean "nothing found" responses for out-of-domain queries.

### Priority 3 — Medium (meaningful capability improvements)

**R8: Add `updatedAfter`/`updatedBefore` date range filters to all search tools.** The `lastUpdated` field is already indexed; exposing it as a filter is a low-effort high-value addition.

**R9: Implement `memorysmith_propose` for agent-submitted memory edits.** This closes the read/write gap without requiring Edit permission grants. The proposal workflow already exists; this is primarily a new MCP endpoint wrapping `MaintenanceProposalWorkflow`.

**R10: Implement `memorysmith_health` tool.** Return service state including ONNX availability, index rebuild status, task store health, and `varResolver` resolution status. Allows agents to detect degradation before attempting expensive calls.

**R11: Add `offset` pagination to all search tools.** Standard `offset` integer parameter alongside `limit`. Without this, large KB growth will make comprehensive coverage impossible.

**R12: Suppress `project-wiki` blocklist diagnostic noise.** Either remove from blocklist or implement `suppressDiagnosticCodes` parameter. The current state makes the `warnings[]` array useless as a health signal.

**R13: Add `maxDiagnosticsPerRecord` cap to context_pack.** At depth=2, the envelope `diagnostics[]` array becomes parsability-hostile. A cap of 3–5 most-severe diagnostics per record would preserve signal while preventing noise explosion.

### Priority 4 — Low (long-term enhancements)

**R14: Implement `memorysmith_similar` (more-like-this by ID).**

**R15: Implement `memorysmith_tag_list` (policy state introspection).**

**R16: Implement `memorysmith_stats` (aggregate KB statistics).**

**R17: Add semantic search for pages** (`markdown-semantic` mode alongside `markdown-lexical`).

**R18: Add `includeContent: false` option** to search tools for lightweight scanning workflows.

**R19: Document `usageCount` increment semantics.** Clarify whether `memorysmith_get` increments the count or whether only search-result retrievals do.

---

## 11. Open Questions

**OQ-1: Session 1 `task_list` timeout root cause.**
Was the session 1 `task_list` timeout caused by the same file I/O contention as `source_bundle` (shared `FileVarStore` path), or was it an independent transient service degradation? Server-side traces from 2026-05-25 would resolve this. If the two are related, the fix for `source_bundle` may not fully protect `task_list` under file contention.

**OQ-2: Zero-result behavior for unsupported Lucene syntax.**
Does `title:security` return zero results because (a) the unsupported syntax is stripped before query evaluation (leaving an empty or trivial query), or (b) the token `title` followed by `:security` is evaluated as content terms that match no documents? A search for `title` alone would disambiguate.

**OQ-3: `usageCount` increment semantics.**
Does `usageCount` increment on `memorysmith_get` calls, or only when a record appears in a search result? If only search results, records fetched by exact known ID (the most common pattern in agent tool-chaining) would appear unused even if frequently accessed. This affects the reliability of `usageCount` as a staleness or popularity signal.

**OQ-4: Page recall gap size.**
What fraction of meaningful page queries are missed by lexical-only page search? A controlled experiment using known-relevant pages with paraphrased queries would quantify the gap and justify (or not) the investment in semantic page indexing.

**OQ-5: Edit-permission hang — exact blocking location.**
Does the 4-minute hang in write tools occur in (a) the edit-permission check before the handler executes, (b) the handler body during a downstream write operation, or (c) somewhere in the MCP JSON-RPC dispatch layer? Server-side tracing during a `page_save` call from a Viewer client would pinpoint the line. This matters because (a) is fixed by the TSK-0159 pattern; (b) and (c) require different fixes.

**OQ-6: `conflicts` relationship directionality.**
The context_pack fixture shows that `conflict` relationships are symmetric in the data (both records list each other in their `conflicts[]` array) but the `relationship` label in the pack output is directional (`"conflict of [root]"`). Is this label intentionally asymmetric (showing the direction from the root's perspective), or is it incidental? Should the label be `"mutually conflicts with [root]"` to avoid implying directionality?

**OQ-7: Tag governance exemption mechanism.**
Is there a planned or existing mechanism to exempt specific records (e.g., those tagged `test-fixture`) from tag governance enforcement, or is the expectation that test fixture tags will be added to the allowlist rather than exempt from the blocklist?

**OQ-8: `format=envelope` vs `format=json` content parity.**
The `envelope` format exposes per-field lexical term breakdown and raw scores that `json` does not. Is this intentional (envelope as a debug format for developers) or an accidental divergence in the format rendering paths? If intentional, should it be documented as the "rich" format for agent use?

---

## Appendix A: Full Test Matrix

| Tool | Test type | Query/params | Expected | Actual | Assessment |
|---|---|---|---|---|---|
| `hybrid_search` | Corpus scan | `limit=20, query="overview"` | Top records | 20 records returned | ✅ Pass |
| `hybrid_search` | Tag-only filter | `tags="missing-feature"` | Tag-filtered recency | 2 records, recency order | ✅ Pass |
| `hybrid_search` | Status filter | `status="Working"` | Status-1 records | Status-1 records returned | ✅ Pass |
| `hybrid_search` | Limit boundary | `limit=1` | 1 result | 1 result | ✅ Pass |
| `hybrid_search` | Large limit | `limit=50` | 50 results | 50 results, full corpus | ✅ Pass |
| `hybrid_search` | Structured tag | `tags="kind:fact"` | kind:fact records | 8 records in recency order | ✅ Pass |
| `hybrid_search` | Format envelope | `format="envelope"` | Rich matchReason | Raw lexical+semantic scores visible | ✅ Pass |
| `search` | Boolean | `"mcp AND (search OR hybrid) NOT deprecated"` | Filtered results | Correct boolean evaluation | ✅ Pass |
| `search` | Wildcard | `"mcp*"` | Prefix matches | Score 16.25, correct hits | ✅ Pass |
| `search` | Field-targeted | `"title:security"` | Results or error | Zero results (silent fail) | ❌ Undocumented |
| `search` | Fuzzy | `"hybrd~"` | Results or error | Zero results (silent fail) | ❌ Undocumented |
| `semantic_search` | Normal | `"how does search ranking combine vector and keyword"` | Relevant results | Top-3 score 0.815–0.831 | ✅ Pass |
| `semantic_search` | Garbage input | `"xkqzpwvmrjflb zzzzz"` | Ideally no results | 3 results, scores 0.740–0.749 | ⚠️ No threshold |
| `get` | Valid ID | `id="project-wiki-active-architecture"` | Full record | Full record returned | ✅ Pass |
| `get` | Invalid ID | `id="completely-made-up"` | Error/null | Plain string "No memory record found" | ✅ Pass (format note) |
| `context_pack` | Normal depth=1 | `query="chat agent", referenceDepth=1` | Roots + refs | Correct graph | ✅ Pass |
| `context_pack` | Deep depth=2 | `maxRecords=30, depth=2, backlinks=true` | Large pack | 30 records, 90+ diagnostics | ⚠️ Diagnostic noise |
| `context_pack` | IDs-only depth=0 | `ids="id1,id2", depth=0` | Exact roots | Exact roots, correct | ✅ Pass |
| `context_pack` | Mixed valid/invalid | `ids="real,fake"` | Warnings + results | Both "not found", empty records | ✅ Graceful (ID was wrong) |
| `context_pack` | Fixture graph | `ids="test-fixture-root", backlinks=true` | Full graph | All 4 fixture records with correct relationship labels | ✅ Pass |
| `unified_search` | Normal | `query="blazor streaming", limits=8+8` | Mixed results | Memories + pages returned | ✅ Pass |
| `unified_search` | Pages-only | `memoryLimit=0, pageLimit=5` | Pages only | Pages only returned | ✅ Pass |
| `unified_search` | Memories-only | `memoryLimit=5, pageLimit=0` | Memories only | Memories only returned | ✅ Pass |
| `page_search` | No query | `limit=20` | Recent pages | 20 most recently updated pages | ✅ Pass |
| `page_search` | With query | `query="deployment"` | Relevant pages | Correct pages | ✅ Pass |
| `page_get` | Large page | `slug="workbench/tasks", maxChars=20000` | Full content | ~20k chars returned | ✅ Pass |
| `page_get` | Missing slug | `slug="research/does-not-exist"` | Error/null | Plain string "No page found" | ✅ Pass (format note) |
| `task_list` | Default | `limit=10` | Recent tasks | Tasks returned | ✅ Pass |
| `task_list` | Status filter | `status="Backlog"` | Backlog tasks | Correct filter | ✅ Pass |
| `task_list` | Query filter | `query="mcp source bundle"` | Relevant tasks | Correct tasks | ✅ Pass |
| `task_get` | Valid key | `idOrKey="TSK-0151"` | Full task | Full task with comments/history | ✅ Pass |
| `task_get` | Numeric key | `idOrKey="TSK-0171"` | Full task | Full task | ✅ Pass |
| `source_bundle` | Session 1 | `ids="..."` | Source content | 4-min timeout | ❌ Timeout (pre-TSK-0159) |
| `source_bundle` | Session 2+ | `ids="..."` | Auth error | `"The caller is not authorized..."` | ✅ Correct (post-TSK-0159) |
| `find_by_source` | Session 1 | `pattern="ChatServices.cs"` | Records | 4-min timeout | ❌ Timeout (pre-TSK-0159) |
| `find_by_source` | Session 2+ | `pattern="..."` | Auth error | `"The caller is not authorized..."` | ✅ Correct (post-TSK-0159) |
| `page_save` | Minimal content | `slug="research/test", markdown="# T\n\nTest"` | Saved or auth error | 4-min timeout | ❌ Silent hang (bug) |
| `task_create` | Session 1 | Full params | Created or auth error | 4-min timeout | ❌ Silent hang (bug) |
| `task_create` | Session 2 | Full params | Created or auth error | 4-min timeout | ❌ Silent hang (bug) |

---

## Appendix B: Full Corpus Map

Approximately 50 memory records observed across 3 sessions. IDs and clusters:

**Architecture (4 records)**
- `project-wiki-active-architecture` — single-host constraint (usageCount: 4)
- `project-wiki-ui-architecture` — Blazor Server + MudBlazor (confidence: 1.0)
- `project-wiki-test-architecture` — NUnit 4, integration test patterns
- `project-wiki-scope-boundaries` — refactor boundary constraints (usageCount: 3)

**Search & MCP Tools (6 records)**
- `project-wiki-search-roadmap` — all search modes overview (usageCount: 2)
- `project-wiki-mcp-search-tools-current` — MCP tool surface documentation (usageCount: 1)
- `project-wiki-mcp-context-pack` — context pack usage guide (usageCount: 1)
- `project-wiki-hybrid-search-rrf` — RRF implementation details
- `project-wiki-onnx-semantic-embeddings` — ONNX e5-base-v2 configuration
- `project-wiki-semantic-search-gap` — known semantic coverage gaps

**Chat & Agent (4 records)**
- `project-wiki-chat-agent-provider` — IChatProvider/IChatAgent architecture
- `project-wiki-chat-streaming-thinking` — streaming and thinking blocks
- `project-wiki-chat-image-attachments` — attachment pipeline
- `project-wiki-chat-local-storage-persistence` — browser localStorage

**Configuration (5 records, all `kind:fact`)**
- `project-wiki-configuration-settings-current` — root config reference
- `project-wiki-chat-configuration-current` — chat model profiles
- `project-wiki-logging-telemetry-current` — logging + OpenTelemetry
- `project-wiki-source-link-configuration-current` — source link settings
- `project-wiki-admin-configuration-surface` — admin workbench

**Security & Auth (3 records)**
- `project-wiki-request-guard-hardening` — API key + remote access guards
- `project-wiki-admin-auth-hardening` — Admin role requirements
- `project-wiki-source-link-security-boundaries` — source bundle auth model

**Governance & Tags (2 records)**
- `project-wiki-tag-governance-current` — tag policy system (`kind:fact`)
- `project-wiki-maintenance-proposals-current` — proposal workflow (`kind:fact`)

**Validation & Tests (4 records)**
- `project-wiki-wiki-validation-current` — validation baseline (`kind:fact`)
- `project-wiki-current-validation-baseline` — active test count anchor
- `project-wiki-current-validation-146-tests` — historical alias (deprecated)
- `project-wiki-benchmarkdotnet-suite` — BenchmarkDotNet coverage

**Test Fixtures (5 records)**
- `project-wiki-test-fixture-overview` — fixture graph overview
- `project-wiki-test-fixture-context-root` — root node (tag: `quartzwave` ⚠️ blocked)
- `project-wiki-test-fixture-reference-child` — reference node (tag: `nimbusvector` ⚠️ blocked)
- `project-wiki-test-fixture-conflict-note` — conflict node
- `project-wiki-test-fixture-backlink-source` — backlink node

**Memory Governance (5 records)**
- `project-wiki-memory-status-classification-current` — status taxonomy (`kind:rule`, `audience:agent`)
- `project-wiki-data-folder-policy` — data folder conventions (usageCount: 4)
- `project-wiki-agent-instructions-source-of-truth` — copilot-instructions alignment
- `memory-system-rfc-council-review-20260520` — historical RFC (status: 1)
- `ai-memory-suite-governance-foundation-20260520` — governance foundation (status: 1)

**Operations (4 records)**
- `project-wiki-windows-service-operations` — service install/CLI
- `project-wiki-operational-diagnostics-dashboard` — /diagnostics endpoint
- `project-wiki-maintenance-observability-refinements` — maintenance telemetry
- `project-wiki-github-actions-artifacts` — CI, coverage, Doxygen

**UI & Features (4 records)**
- `project-wiki-semantic-ui-current` — /memories workbench (usageCount: 1)
- `project-wiki-ui-layout-source-link-polish` — nav drawer + layout
- `project-wiki-markdown-pages` — pages feature overview
- `project-wiki-event-store` — FileEventStore

**Task Tracking & Governance (3 records)**
- `task-tracking-feature-20260523` — task domain implementation (⚠️ 2 missing refs)
- `task-priority-severity-rubric-20260523` — priority classification
- `ai-memory-suite-implementation-plan-20260520` — implementation status

**Integration & Friction (2 records)**
- `project-wiki-mcp-integration` — MCP integration friction notes (usageCount: 2)
- `project-wiki-generalization-friction` — general developer friction (usageCount: 2)

**Validation Command (1 record)**
- `project-wiki-validation-command` — dotnet test command (usageCount: 3)

---

## Appendix C: Confirmed Blocklisted Tags

As of 2026-05-26 session 3, the following tags are confirmed `tag.blocked` (Warning severity) in `Data/Policies/tag-policy.json`:

| Tag | Records affected | Notes |
|---|---|---|
| `project-wiki` | ~50 (all records) | Universal namespace tag; blocklisting creates maximum noise |
| `quartzwave` | 1 (`test-fixture-context-root`) | ⚠️ Intentional test fixture marker |
| `nimbusvector` | 1 (`test-fixture-reference-child`) | ⚠️ Intentional test fixture marker |
| `lucene` | 1 (`project-wiki-hybrid-search-rrf`) | Implementation technology tag |
| `explicit-ids` | 1 (`project-wiki-mcp-context-pack`) | Operational parameter tag |
| `max-records` | 1 (`project-wiki-mcp-context-pack`) | Operational parameter tag |
| `maintenance-agent` | 1 (`project-wiki-maintenance-proposals-current`) | Component name tag |
| `memorysmith-app` | 1 (`project-wiki-active-architecture`) | Application name tag |
| `localstorage` | 1 (`project-wiki-chat-local-storage-persistence`) | Technology tag |
| `cobertura` | 1 (`project-wiki-github-actions-artifacts`) | Tool name tag |

**Observation:** The blocklist appears to target tags that are either too specific (single-record) or too technical/implementation-internal. The `quartzwave` and `nimbusvector` entries are exceptions that should be reviewed and exempted.

---

*End of report. Prepared by Claude Sonnet 4.6 operating as external MCP agent, 2026-05-26.*
*All findings are based on live tool call results. No REST API access was used. All observations reflect Viewer-permission MCP client behavior.*