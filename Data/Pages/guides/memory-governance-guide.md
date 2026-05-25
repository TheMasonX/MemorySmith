# Memory Governance Guide

Status: First implementation slice, 2026-05-20

Related: [AI Memory Suite Implementation Plan](../plans/ai-memory-suite-implementation-plan.md), [Search and Chat](search-and-chat.md), [Council Workflow](../council/llm-council.md)

## Purpose

MemorySmith now treats governance as visible diagnostics before behavioral enforcement. The goal is to help people and agents notice malformed tags, stale records, broken source links, dangling relationships, and low-score deprecation recommendations without silently hiding records or changing search ranking.

## Tag Policy

The active tag policy is file-backed at `Data/Policies/tag-policy.json` by default. The first policy is warning-oriented and local to this wiki instance.

If the policy file is missing or malformed, MemorySmith falls back to its built-in default policy so local startup and diagnostics can continue. Treat that fallback as a resilience path, not as proof that the configured policy is healthy. `TSK-0150` tracks making fallback use visible in diagnostics and the `/tags` workflow.

Supported namespaces:

| Namespace | Form | Notes |
| --- | --- | --- |
| `kind` | `kind:rule`, `kind:procedure`, `kind:decision`, etc. | Single canonical kind per memory. |
| `priority` | `priority:critical`, `priority:high`, `priority:normal`, `priority:low` | Warning/review hint only; not a ranking rule. |
| `audience` | `audience:agent`, `audience:human`, `audience:chat`, etc. | Multiple allowed. |
| `scope` | `scope:<local-topic>` | Free-form local project scope. |
| `review-after` | `review-after:YYYY-MM` | Warns when the month is current or past. |
| `expires` | `expires:YYYY-MM` | Warns when the claim should be reverified. |
| `stale-risk` | `stale-risk:YYYY-MM` | Warns that the topic tends to drift. |
| `supersedes` | `supersedes:<memory-id>` | Validates the target exists. |
| `superseded-by` | `superseded-by:<memory-id>` | Warns that newer guidance should be preferred. |

Plain tags should use lowercase kebab-case. Blocklisted tags such as `misc`, `general`, `important`, `todo`, status names, and similar low-signal labels produce warnings instead of blocking saves.

## Diagnostics

Diagnostics are additive metadata. They do not mutate memory records and do not change ranking.

Current diagnostic categories:

| Category | Examples |
| --- | --- |
| `tag` | leading `#`, malformed plain tag, unknown namespace, invalid date, duplicate single-cardinality namespace, alias suggestion, blocklisted plain tag |
| `source` | missing `%VarName%`, unresolved local path, invalid line range, line range beyond file length |
| `relationship` | missing references/conflicts, self-conflicts, supersession tags pointing at unknown records |
| `staleness` | due `review-after`, reached `expires`, due `stale-risk`, Deprecated status, `superseded-by` marker |
| `maintenance` | low score below the deprecation threshold while automatic deprecation is disabled |

The `/memories` workbench shows compact diagnostic chips in result rows and a detail diagnostics panel for the selected memory.

## Warning-First Maintenance

Automatic score-based deprecation is disabled by default with `MemorySmith:Maintenance:AutomaticDeprecationEnabled=false`. Triage and consolidation still promote eligible records, rebuild the index, and merge duplicates, but low-score records remain visible and generate `DeprecationRecommended` events instead of being silently moved to Deprecated.

Automatic deprecation can be explicitly enabled for controlled testing or future deployments, but that should remain a conscious decision because `MemoryScorer` still does not understand strict rules, staleness tags, source evidence, or human review state.

## Tool Output

Context-pack JSON now carries `schemaVersion: memorysmith.context-pack.v1` and keeps the existing `records` and `warnings` properties. Each context-pack record can include `diagnostics`; top-level warnings include diagnostic summaries so agents can see stale, broken, or risky context without parsing prose.

Markdown context-pack output and chat tool output also include a compact `Diagnostics` section when a record has warnings.

## Gates Still Closed

These remain deferred until separate council review, probes, tests, and rollback notes exist:

- search ranking or RRF changes;
- temporal decay;
- schema promotion for `ReviewAfter`, `ValidUntil`, `Kind`, `Priority`, or typed relations;
- page frontmatter, chunking, or page embeddings;
- default MCP envelope changes beyond additive context-pack metadata;
- stricter Agent write approval behavior beyond the existing proposal workflow.
