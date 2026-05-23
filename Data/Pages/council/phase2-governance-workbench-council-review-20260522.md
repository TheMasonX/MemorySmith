# Council Review: Phase 2 Governance Workbench

## Decision

PR #15 implements warning-first governance UI and policy save behavior without schema or ranking changes. The branch is approved for merge because tag diagnostics, policy editing, and approval-only suggestions are now visible to humans before later Agent write governance expands.

## Evidence Reviewed

- `Data/Pages/council/application-next-steps-council-review-20260521.md` Phase 2 acceptance criteria.
- `Data/Pages/workbench/tag-governance-workbench-20260522.md` human tag authoring and warning guidance.
- `MemorySmith.App/Services/TagGovernanceService.cs` snapshot, suggestion, completion, and draft diagnostic service.
- `MemorySmith.App/Services/MemoryGovernanceServices.cs` tag diagnostics and observe/warn/blockUnknown policy mode behavior.
- `MemorySmith.App/Services/MemoryApplicationService.cs` save-time blocking only for `Error` diagnostics.
- `MemorySmith.App/Components/Pages/MemoryViewer.razor` tag chips, autocomplete, and draft diagnostics.
- `MemorySmith.App/Components/Pages/TagManager.razor` and `.razor.cs` admin policy editor, usage table, and read-only suggestions.
- `MemorySmith.App/Controllers/GovernanceController.cs` admin/editor governance APIs.
- `MemorySmith.Tests/TagGovernanceTests.cs` focused tests.
- Validation: focused tests 7/7, app build passed, solution build passed, full suite 241/241, benchmark smoke passed.

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | Accept. The change matches the Phase 2 roadmap and stays convention-first. | 91% | None. |
| Data Model Architect | Accept. Tags remain string lists; policy remains JSON config; no schema migration is introduced. | 88% | Future auto-rewrite work must reopen approval-first review. |
| Retrieval Specialist | Accept. Ranking and recall behavior are unchanged; benchmark smoke passed. | 89% | Phase 3 should watch diagnostics envelope size. |
| Human Learning Advocate | Accept with follow-up. Chips, autocomplete, draft diagnostics, and `/tags` make governance visible. | 82% | Future blockUnknown remediation could offer an allowlist quick-add; not a blocker. |
| Skeptical Reviewer | Accept with caution. Tests and authorization are solid; suggestion heuristics are intentionally static and approval-only. | 79% | Reopen review if later phases attempt automatic suggestion application. |
| Synthesizer | Accept and merge. Acceptance criteria are met without schema, ranking, or Agent write behavior changes. | 87% | None. |

## Synthesis

What changes now: memory tag chips/autocomplete, pre-save draft diagnostics, admin Tag Manager, governance APIs, policy mode enforcement, and authoring docs.

What is deferred: automatic tag rewrites, retrieval-warning expansion, native chat tool calls, Agent write governance, page chunking/embeddings, and schema promotion.

## Dissent

No material dissent. The main cautions are future-facing: keep suggestions approval-only, measure diagnostics output size in Phase 3, and consider a future remediation affordance for blockUnknown save errors.

## Acceptance Criteria

- Existing records remain editable.
- Invalid policy input warns instead of crashing.
- Unknown tags can be observed, warned, or blocked by policy mode.
- Component-level coverage verifies chips, diagnostics, policy editing, and suggestion surfaces.
- Docs explain how humans should author tags and interpret warnings.
- Full local validation and CI pass before merge.

## Open Questions

- Should a later UI affordance add blocked tags directly to allowlist after explicit approval?
- What diagnostics envelope size should Phase 3 consider safe for context packs?
- Should future policy versions include explicit migrations beyond `SchemaVersion = 1`?
- Should suggestion history remain stateless, or should repeated unresolved suggestions become durable governance signals?