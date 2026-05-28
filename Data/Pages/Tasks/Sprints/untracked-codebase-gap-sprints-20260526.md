# Sprint Plan - Untracked Codebase Gap Sequence - 2026-05-26

## Planning Assumptions
- Team size: one primary implementer plus review support.
- Sprint duration: one week each.
- Effective capacity: about 18 to 20 focused engineering hours per sprint after review, validation, and docs overhead.
- Risk buffer: 25% reserved for route smoke checks, regression repair, and task/wiki updates.
- Primary execution surface: `Data/Tasks` and `/tasks`.
- Narrative references: `research/codebase-untracked-task-audit-20260526` and `council/untracked-task-prioritization-council-20260526`.

## Sprint 1 - MCP Contract And Tool Surface Control

## Sprint Objective
Finish the external-agent contract cleanup while preventing `ChatToolCatalog` from becoming the next unbounded monolith.

## Committed Items
- `TSK-0192` Modularize ChatToolCatalog into domain registries and shared schema helpers. Estimate M. Enabler for the rest of this sprint.
- `TSK-0183` Characterize and regression-test MCP write-tool denial path. Estimate S.
- `TSK-0185` Add MCP health and stats introspection tools. Estimate M. Depends on `TSK-0192` boundaries.
- `TSK-0186` Expand MCP search filters and pagination for agent workflows. Estimate M. Depends on `TSK-0192` boundaries and current search-contract docs.

## Stretch Items
- `TSK-0177` Add `memorysmith_read_file` MCP+chat tool for surgical source reads by path.
- `TSK-0053` Task JSON canonicalization guardrails, if `TSK-0192` leaves capacity for tooling follow-up.

## Exit Criteria
- MCP tool additions or behavior changes no longer require editing one monolithic registry file.
- Unauthorized MCP write behavior is characterized under the right auth profile and locked with regression coverage.
- External agents can query a bounded health/stats surface and use clearer search ergonomics without scraping human docs.

## Demo Targets
- A tool-registry diff shows domain-owned descriptor files or equivalent bounded structure.
- MCP clients can request health/stats and receive deterministic bounded output.
- An unauthorized write-tool call returns a fast, documented denial under test.

## Sprint 2 - Route Component Simplification

## Sprint Objective
Reduce UI review blast radius in the two largest workbench routes without changing user-facing behavior.

## Committed Items
- `TSK-0189` Decompose Chat workbench route into child components and state helpers. Estimate L.
- `TSK-0190` Decompose Admin workbench route into tab-owned components and editor helpers. Estimate L.

## Stretch Items
- `TSK-0047` Complexity guardrails updated to report route-component hotspots after the first two extractions.

## Exit Criteria
- `Chat.razor` and `Admin.razor` are materially reduced and no longer centralize their largest subviews plus local state helpers in one file.
- Focused route tests and smoke checks still cover the extracted behavior.
- Future chat/admin UX tasks can target bounded child components instead of the top-level page file.

## Demo Targets
- Chat transcript/sidebar/composer and Admin tab sections are visibly owned by separate component files.
- Browser and focused component tests still pass for the main user flows.

## Sprint 3 - Memory Core And Backlog Contract Cleanup

## Sprint Objective
Reduce central domain-service risk while making the task store contract more predictable for future tooling.

## Committed Items
- `TSK-0191` Split MemoryApplicationService into retrieval, mutation, and diagnostics modules. Estimate L.
- `TSK-0053` Add task JSON contract canonicalization and compatibility guardrails. Estimate M.

## Stretch Items
- Review whether `Tasks.razor` and `Pages.razor` should become new decomposition tasks based on post-Sprint-2 guardrail output.

## Exit Criteria
- Memory-domain changes no longer require editing one class for search, context pack, CRUD, diagnostics, and telemetry together.
- Task contract policy is explicit enough that future generated/imported tasks do not keep adding new persisted shapes silently.
- Validation commands exist for both the memory-domain extraction and task-contract guardrails.

## Demo Targets
- Memory retrieval/mutation boundaries are inspectable in focused files or services.
- Task-record validation distinguishes canonical from legacy-compatible records clearly.

## Task Links
- Audit: `research/codebase-untracked-task-audit-20260526`
- Council: `council/untracked-task-prioritization-council-20260526`
- New tasks: `TSK-0189`, `TSK-0190`, `TSK-0191`, `TSK-0192`