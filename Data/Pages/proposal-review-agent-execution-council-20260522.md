# Council Review: Proposal Review Agent Execution

## Decision

Implement proposal review execution through the existing maintenance-agent provider/model configuration, persisting agent feedback in proposal history/comments and accepting review-generated revised proposals only when they pass the existing proposal store validation.

## Evidence Reviewed

- `Data/Pages/admin-models-maintenance-agent-plan-20260522.md`: records the Request Agent Review baseline and remaining review-agent goals.
- `Data/Memories/Core/project-wiki-maintenance-proposals-current.json`: current proposal workflow behavior and the durable review request marker.
- `MemorySmith.App/Services/MaintenanceAgentServices.cs`: `MaintenanceProposalWorkflow`, proposal storage/apply validation, and existing maintenance LLM provider hook.
- `MemorySmith.App/Components/Pages/Proposals.razor`: user-facing proposal action surface.
- `MemorySmith.App/Services/ChatModelProfileService.cs`: model profiles exist for chat, but maintenance/review-agent profile assignment remains deferred.
- `MemorySmith.Tests/MaintenanceAgentWorkflowTests.cs`: current proposal workflow regression surface.

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | Keep the implemented request marker and add separate `agent_review_completed` / `agent_revision_proposed` history actions so future readers can distinguish request, review, and revision. | 0.86 | Do not update Core memories with review execution until the code exists. |
| Data Model Architect | Avoid new proposal schema fields for the first execution slice; use existing comments, history, metadata.supersedes, and metadata.superseded_by. | 0.80 | A future schema may still be needed for first-class review verdicts. |
| Retrieval Specialist | Include proposal JSON, evidence, and related ids in the review prompt; do not let review execution approve or apply writes. | 0.78 | Fresh context retrieval should be a later improvement, not part of the first execution path. |
| Human Learning Advocate | Keep the UI action simple: one button requests/runs review and the Comments tab shows the outcome. | 0.82 | Long-running review feedback should eventually expose active task state. |
| Skeptical Reviewer | Gate revisions through existing proposal validation and keep the original proposal actionable so humans can reject the agent's advice. | 0.76 | Provider output may be malformed or overconfident; parsing must degrade to a comment instead of failing the workflow. |

## Synthesis

Change now:

- `MaintenanceAgentService` gets a proposal-review execution method that records the request, calls the configured maintenance-agent provider when `UseLlm` is enabled, parses structured review output, records agent feedback, and optionally saves a revised proposal.
- `MaintenanceProposalWorkflow` gets agent-specific comment/revision helpers that preserve the original proposal status and enforce existing actionable/closed guards.
- `/proposals` calls the service rather than only the workflow marker.

Defer:

- Admin-selected review-agent model profile assignment.
- Rich review verdict schema and UI filters.
- Fresh wiki context retrieval beyond the proposal's existing evidence bundle.
- Durable task logs/transcript for long-running reviews.

## Dissent

The Data Model Architect would prefer a typed review-result schema before storing agent outcomes, while the Skeptical Reviewer argues that provider JSON quality is not yet proven enough for a schema migration. The compromise is to parse a small structured envelope for execution but persist only through existing proposal comments/history plus optional validated revised proposals.

## Acceptance Criteria

- Requesting agent review through `MaintenanceAgentService` preserves the original proposal status.
- If LLM review is disabled or unavailable, the request remains recorded and the UI reports a warning instead of failing the proposal workflow.
- If the provider returns review comments, the proposal history includes `agent_review_completed` and the Comments tab can show the feedback.
- If the provider returns a valid revised proposal, the original proposal records `agent_revision_proposed`, the revised proposal records `metadata.supersedes`, and both proposals remain reviewable.
- Malformed provider output degrades to a saved agent comment with no applied writes.
- Focused workflow/markup tests and project wiki fixture tests pass before PR.

## Open Questions

- Which chat model profile should become the default proposal review profile once model assignments exist?
- Should review verdicts become filterable proposal metadata?
- Should review agents fetch fresh wiki context, or only inspect proposal evidence by default?