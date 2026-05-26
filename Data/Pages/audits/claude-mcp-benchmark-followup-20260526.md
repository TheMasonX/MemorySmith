# Claude MCP Benchmark Follow-up - 2026-05-26

This page reviews [Claude MCP Benchmark Report - 2026-05-25](claude-mcp-benchmark-report-5-25-26.md) and [Claude MCP Benchmark Report - 2026-05-26](claude-mcp-benchmark-report-5-26-26.md) against the current repository state as of 2026-05-26.

## Summary

The reports were useful and specific, especially where they named exact memory IDs and stale source-link paths. Several findings were still actionable and have been converted into task records. Several others were already stale by the time of this review because the repo, docs, and tag policy had already moved forward.

I also fixed the concrete KB-health drift that the report identified where the repair was obvious and low risk.

## Disposition

| Claude finding | Disposition | Evidence | Follow-up |
| --- | --- | --- | --- |
| Edit-gated MCP write tools appear to hang for Viewer callers | Accepted for investigation | `MemorySmith.App/Controllers/McpController.cs` currently looks fail-fast for `ChatToolRisk.Write`, but there is no focused unauthorized denial-path regression coverage and loopback bootstrap compatibility can mask behavior differences. | `TSK-0183` |
| Public docs do not explain MCP write permissions | Closed as stale | `README.md` and `Data/Pages/features/api-and-mcp.md` already document View, Edit, and Source bundle boundaries for the current tool set. | No new task |
| Per-tool MCP disable/enable controls are missing | Closed as stale | `MemorySmith:EnabledTools` and `DisabledTools` are already exposed in admin settings and documented. | No new task |
| `format=envelope` and advanced search contract are undocumented for public users | Accepted | Runtime supports `envelope`, but current public docs still emphasize basic tool purpose and do not explain the richer structured format or practical lexical-query boundaries. | `TSK-0184` |
| `project-wiki` and fixture-tag governance noise should be exempted or suppressed | Closed as stale | Current `Data/Policies/tag-policy.json` allowlists `project-wiki` and does not blocklist the fixture tags called out in the report. | No new task |
| External agents need a first-class MCP health/stats surface | Accepted | Runtime already has `/health`, `/api/stats`, and `/api/diagnostics`, but no equivalent MCP tool exists. | `TSK-0185` |
| External agents need better server-side search ergonomics such as recency filters, thresholds, and paging | Accepted | Current search surfaces expose only the core filters and bounded limits; the ergonomics gap is real and unowned. | `TSK-0186` |
| Broken memory references and stale source-link paths in structured wiki records | Fixed directly | The named records still had stale page paths and one working memory still referenced missing IDs. | Repaired during this audit |

## KB Fixes Applied Directly

These were concrete wiki-data repairs, not code backlog items:

- Removed broken `References` entries from `task-tracking-feature-20260523`.
- Updated moved page-path source links in `project-wiki-memory-status-classification-current`.
- Updated moved page-path source links in `project-wiki-markdown-pages`.
- Updated moved page-path source links in `ai-memory-suite-implementation-plan-20260520`.
- Updated moved page-path source links in `ai-memory-suite-governance-foundation-20260520`.
- Updated moved page-path source links in `memory-system-rfc-council-review-20260520`.

Focused validation after those repairs: the edited memory JSON files parsed cleanly and each updated `%MemorySmithRepo%...` source-link target resolved to an existing file.

## Notes Back To Claude

If the write-tool timeout is still reproducible on a current build, the most helpful follow-up evidence would be:

1. The exact JSON-RPC request and the raw response or timeout behavior.
2. Whether the target instance had an Admin account provisioned yet, because loopback bootstrap compatibility changes the effective auth path before first-admin setup.
3. Whether the caller was using plain MCP HTTP, VS Code MCP, or another bridge/proxy layer.

Two other observations:

1. The exact memory IDs and stale URIs in the KB-health section were the strongest part of the report because they were immediately testable and repairable.
2. The permission-doc and tag-policy complaints were already stale by the time of this review, so future benchmark passes should note the repo snapshot or commit if possible.

## Deferred For Later Review

I did not create new tasks yet for every longer-horizon recommendation in the report. In particular, semantic page search, `memorysmith_propose`, and larger diagnostics-envelope shaping changes still need product-scoping rather than straight backlog cloning from an external audit.