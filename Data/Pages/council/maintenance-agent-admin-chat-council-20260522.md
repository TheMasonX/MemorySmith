# Council Review: Maintenance Agent Admin Chat And Transcripts

## Decision

Add a first admin-only maintenance-agent conversation surface as a non-mutating chat with durable transcript entries, using the existing maintenance-agent provider/model configuration until model-profile assignments are implemented.

## Evidence Reviewed

- `Data/Pages/plans/admin-models-maintenance-agent-plan-20260522.md`: tracks remaining maintenance task/log visibility, admin transcript-style logs, and optional admin conversation.
- `Data/Pages/council/proposal-review-agent-execution-council-20260522.md`: established that proposal review can use the existing maintenance-agent provider now, while model-profile assignment and detailed transcripts remain deferred.
- `Data/Memories/Core/project-wiki-maintenance-proposals-current.json`: current state for `/proposals`, recent task activity, proposal review execution, and approval-gated proposal writes.
- `MemorySmith.App/Services/MaintenanceAgentServices.cs`: current run/review paths, `MaintenanceRunActivity`, provider invocation through `IChatProvider`, and existing storage paths.
- `MemorySmith.App/Components/Pages/Chat.razor`: existing general chat has browser-local sessions and should not be copied wholesale for admin maintenance logs without a server-side transcript decision.
- `MemorySmith.App/Components/Pages/Admin.razor`: admin-only surface already has RBAC and is the natural home for a maintenance conversation panel.
- `MemorySmith.Tests/MaintenanceAgentWorkflowTests.cs`: focused service tests for maintenance execution and proposal review.

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | Persist admin maintenance conversations in a dedicated transcript log instead of browser storage, because these turns explain operational/admin decisions. | 0.84 | Do not store transcript facts in Core memories until the code exists and the behavior is validated. |
| Data Model Architect | Start with JSONL transcript entries plus a configurable path, mirroring activity logs; defer richer schema/indexing until transcript fields stabilize. | 0.80 | Transcript entries can grow quickly and may need retention controls later. |
| Retrieval Specialist | Keep first chat non-mutating and include recent activity/proposal context only as prompt context; do not let this surface write memories/pages directly. | 0.78 | Fresh retrieval/tool use is tempting but would blur the proposal governance boundary. |
| Human Learning Advocate | Put the first surface in `/admin` so admins can ask what happened and see the latest transcript without hunting through files. | 0.82 | It should be clearly separate from general `/chat` so admins understand this is operational. |
| Skeptical Reviewer | Require Admin policy, record disabled/provider-missing outcomes, and make all write-producing ideas go through future proposal workflow work. | 0.77 | Provider output may include unsafe instructions or claim actions were taken; UI copy must avoid implying writes occurred. |
| Synthesizer | Build a narrow, reversible slice: path setting, service method, durable transcript list, and Admin panel. | 0.83 | Model-profile assignment and tool-enabled maintenance conversations remain deferred. |

## Synthesis

Change now:

- Add `MemorySmith:MaintenanceAgent:Storage:TranscriptLogPath` for compact JSONL transcript entries.
- Add `MaintenanceAgentService.SendAdminMessageAsync` and `ListRecentTranscriptsAsync`.
- Store user prompt, assistant response, provider/model, timestamps, and warnings.
- Add an Admin Maintenance panel with recent transcript entries and a prompt box.
- Keep the chat non-mutating: no memory/page writes, no proposal approvals, no direct tool execution.

Defer:

- Model-profile assignment for maintenance/admin chat.
- Streaming responses.
- Retrieval/tool use inside the maintenance admin chat.
- Transcript search, retention pruning, redaction policy, and proposal-id drilldown.
- Routing general chat-agent edits through the proposal workflow.

## Dissent

The Human Learning Advocate would prefer a full chat-like experience immediately, including session history and streaming. The Skeptical Reviewer objects because a full chat surface can look like it has operational authority before write/proposal governance is complete. The compromise is a durable, non-mutating admin conversation panel that can answer questions and preserve transcripts without executing changes.

## Acceptance Criteria

- Admin maintenance chat uses the existing maintenance-agent provider/model path and returns a warning when LLM review is disabled or no provider is configured.
- Sending an admin maintenance message appends a transcript entry to `TranscriptLogPath` and recent transcripts can be read back newest-first.
- Transcript entries include user prompt, assistant response, provider, model, timestamps, and warnings.
- `/admin` exposes a Maintenance panel with a prompt box and recent transcript entries.
- The first implementation does not write memories/pages, approve proposals, or apply file changes from admin chat output.
- Focused service tests, Admin markup tests, and project wiki fixture tests pass before PR.

## Open Questions

- Should maintenance admin chat eventually share model-profile assignments with proposal review, or have a separate profile assignment?
- What transcript retention limit should be enforced once logs are durable and searchable?
- Should transcript entries link to maintenance activity run ids once activity records grow a stable id field?
- Should the admin conversation eventually be its own route instead of an Admin tab?