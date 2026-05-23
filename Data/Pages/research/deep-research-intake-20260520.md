# Deep Research Intake Notes (2026-05-20)

Source inputs:

- Copilot shared task response: <https://copilot.microsoft.com/shares/tasks/ajvthvsd2irkDvch8TAQV>
- Raw captured page: [temp-deep-research-response](temp-deep-research-response.md)

Purpose:

- Preserve the main external findings in a decision-ready format.
- Separate generally strong patterns from weaker or blog-only evidence.
- Feed the next MemorySmith council review with concrete questions and acceptance gates.

## What This Resolves

The newly captured research largely supports the current RFC direction:

- Convention-first remains reasonable for local-first systems.
- UI validation/autocomplete is usually needed before metadata conventions scale.
- Staleness should be surfaced before applying hard suppression/decay.
- Hybrid output shape is preferred: JSON for tool interfaces, Markdown for human docs.
- Relationship typing should be promoted incrementally, not front-loaded.

This does not by itself decide MemorySmith-local defaults (roles, exact thresholds, compatibility choices). Those stay local decisions.

## High-Value Findings (Most Actionable)

1. Tag governance should not remain free-form at scale.

- Practical pattern: keep conventions initially, then add UI assistance and validation before promoting to schema.
- Immediate implication for MemorySmith: prioritize tag chips/autocomplete/validation before ranking behavior depends on namespaced tags.

1. Staleness handling should be warning-first.

- Practical pattern: explicit freshness/review metadata and visible stale markers outperform silent drops.
- Immediate implication: add stale/superseded/expired warnings in search and context-pack output before any aggressive decay.

1. Page chunking should be evidence-triggered, not assumed.

- Practical pattern: start with whole pages for small corpora; chunk when page length and retrieval misses justify it.
- Immediate implication: define local trigger metrics (page length distribution, miss rate, recall@k regressions) before implementing chunking.

1. JSON default for agent/tool output is widely favored.

- Practical pattern: machine interfaces should return strict JSON; render Markdown summaries in UI.
- Immediate implication: preserve Markdown for pages/chat readability but bias MCP tool responses toward structured JSON for agent consumers.

1. Governance and approval need traceable evidence.

- Practical pattern: treat AI writes as proposals with provenance, confidence, and reviewable diffs.
- Immediate implication: for critical memory writes, require source-backed rationale and make approval rationale visible in trace/review records.

## Evidence-Quality Caveat

The captured research blends source types:

- Stronger: platform docs, standards-style guidance, mainstream vendor docs.
- Moderate: engineering handbooks, architecture writeups, operational reports.
- Weaker: personal blogs, opinion-heavy posts, unreplicated claims.

Use this intake as directional evidence, not final proof. For each implementation decision, require at least one strong source or local benchmark.

## Local Decision Questions Still Open

These remain MemorySmith-internal and should be settled by council review + local measurement:

- Exact tag canonical forms and which ones are mandatory.
- Exact stale behavior policy (warn vs demote vs filter).
- Exact page-chunking trigger threshold.
- Whether `memorysmith_context_pack` should switch defaults while preserving existing consumers.
- Exact RBAC requirements for Agent writes into Core memories or strict-rule content.

## Recommended Next Council Packet

Use this packet when running the next council review:

- [Core Memory System Improvements RFC](../plans/temp-plan.md)
- [AI Memory Suite Implementation Plan](../plans/ai-memory-suite-implementation-plan.md)
- [Council Workflow](../council/llm-council.md)
- [Deep Research Prompt](memory-system-deep-research-prompt.md)
- [temp-deep-research-response](temp-deep-research-response.md)
- This intake note

Decision target for the next review:

- Use the implementation plan's Phase 0 maintenance audit as the first gate.
- Approve a Phase 1 implementation envelope limited to policy, diagnostics, validation, and warning surfaces.
- Defer ranking decay, schema expansion, and chunking until local metrics prove need.

## Suggested Acceptance Gates (Pre-Implementation)

1. Tag validators and UI cues exist before namespaced tags affect ranking.
1. Stale/superseded warnings appear in search/context outputs and chat references.
1. A benchmark/probe suite exists for strict-rule retrieval and stale-context safety.
1. A compatibility note exists for any JSON-default output changes.
1. Critical Agent write approvals include explicit evidence/provenance in review surfaces.
