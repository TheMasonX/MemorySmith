# Comprehensive Codebase Audit Report - Untracked Task Gap Pass - 2026-05-26

## Scope

- Included: current codebase gaps that are still untracked in `Data/Tasks`, plus existing tasks that remain materially under-specified after today's earlier benchmark and auth follow-up work.
- Excluded: repeating already-tracked service/security/browser backlog unless needed for sequencing or dependency notes.
- Timebox: one targeted deep-dive pass after reconciling current backlog, recent sprint pages, and 2026-05-26 task creation activity.

## Acceptance Criteria

- Findings are evidence-backed and limited to still-unowned or under-specified work.
- Existing 2026-05-26 tasks are incorporated instead of duplicated.
- New work is translated into `/tasks` records with implementation-ready descriptions.
- High-impact prioritization is reviewed through a simulated council with explicit dissent.

## Evidence Reviewed

- Planning surfaces: `README.md`, `Data/Tasks/*.json`, `Data/Pages/Tasks/Sprints/*.md`, `Data/Pages/research/ultra-codebase-audit-20260524.md`, `Data/Pages/audits/claude-mcp-benchmark-followup-20260526.md`.
- Code hotspot scan (2026-05-26): `MemorySmith.App/Components/Pages/Chat.razor` 2511 lines, `MemorySmith.App/Components/Pages/Admin.razor` 1663, `MemorySmith.App/Components/Pages/Tasks.razor` 1300, `MemorySmith.App/Components/Pages/Pages.razor` 1046, `MemorySmith.App/Services/MemoryApplicationService.cs` 1168, `MemorySmith.App/Services/ChatToolCatalog.cs` 1161.
- Route ownership evidence: `Chat.razor` `@code` begins at line 392 and owns transcript, sidebar, storage, trace, attachments, and send orchestration; `Admin.razor` `@code` begins at line 590 and owns Users, OAuth, Models, Configuration, Variables, Audit, and History behaviors in one file.
- Core service evidence: `MemoryApplicationService.cs` exposes list/search/context-pack/get/find-by-source/create/update/delete/usage/stats/telemetry operations in one class.
- Tool-surface evidence: `ChatToolCatalog.cs` currently yields 19 `ChatToolDescriptor` instances and also owns search/context-pack/source/task schema builders and formatting helpers.
- Backlog coverage checks: searched `Data/Tasks` for `Chat.razor`, `Admin.razor`, `Pages.razor`, `Tasks.razor`, `MemoryApplicationService`, and `ChatToolCatalog`; no first-class decomposition tasks exist for those exact surfaces.
- Task hygiene evidence: `TSK-0053` remains open while `TSK-0177` still uses PascalCase root properties and `TSK-0026` through `TSK-0028` retain imported `*-new-task` ids even after later title/status normalization.
- Same-day tracked work already in progress or completed: `TSK-0179`, `TSK-0180`, `TSK-0183`, `TSK-0184`, `TSK-0185`, `TSK-0186`.

## Findings

| ID | Domain | Severity | Confidence | Summary | Evidence | Task Mapping |
| --- | --- | ---: | ---: | --- | --- | --- |
| F-001 | UI Architecture | High | 93% | The Blazor route backlog still does not own the two largest and most actively changed route components: `Chat.razor` and `Admin.razor`. | Hotspot scan plus direct route reads show `Chat.razor` at 2511 lines and `Admin.razor` at 1663 lines, each combining large markup and large local state/event surfaces; task search found no route-level decomposition owner. | New: `TSK-0189`, `TSK-0190` |
| F-002 | Core Memory Architecture | High | 90% | `MemoryApplicationService` has become a central monolith for retrieval, mutation, diagnostics, and telemetry without an owning refactor task. | `MemoryApplicationService.cs` at 1168 lines; public methods span list/search/search-metadata/lexical/semantic/hybrid/context pack/get/diagnostics/find-by-source/create/update/delete/usage/stats/telemetry. | New: `TSK-0191` |
| F-003 | MCP/Chat Tool Architecture | High | 92% | `ChatToolCatalog` has become its own architecture hotspot and will keep growing under the current MCP backlog unless modularized first. | `ChatToolCatalog.cs` at 1161 lines; 19 yielded tool descriptors; schema builders and format helpers are in the same file; active backlog items `TSK-0183`, `TSK-0185`, `TSK-0186`, and `TSK-0177` all touch this surface. | New: `TSK-0192` |
| F-004 | Task Contract Hygiene | Medium | 88% | The task-contract guardrail task is still correct, but it needs refreshed evidence and explicit linkage to today's audit because mixed persisted shapes and imported legacy ids still exist. | `TSK-0053` remains open; `TSK-0177` uses PascalCase roots; `TSK-0026` to `TSK-0028` retain `*-new-task` ids; runtime compatibility exists, but canonicalization is not complete. | Existing: `TSK-0053` updated |
| F-005 | Secondary Route Candidates | Medium | 74% | `Tasks.razor` and `Pages.razor` remain large, but current evidence is weaker for immediate extraction because recent UX/layout work is still settling. | Hotspot scan shows `Tasks.razor` 1300 lines and `Pages.razor` 1046; however, they also have recent completed UX tasks and fewer cross-cutting platform concerns than Chat/Admin. | Deferred under `TSK-0047` guardrail review |

## Backlog Cross-Reference

| Area | Existing task set | This audit's action |
| --- | --- | --- |
| MCP benchmark/auth follow-up | `TSK-0179`, `TSK-0180`, `TSK-0183`, `TSK-0184`, `TSK-0185`, `TSK-0186` | Reuse as active baseline; do not duplicate. |
| Service/test decomposition | `TSK-0042`, `TSK-0043`, `TSK-0044`, `TSK-0045`, `TSK-0047`, `TSK-0048`, `TSK-0049`, `TSK-0050`, `TSK-0051`, `TSK-0157` | Extend with the still-unowned UI route, memory-core, and tool-catalog architecture tasks. |
| Task provenance and contract drift | `TSK-0029`, `TSK-0053` | Keep `TSK-0029` closed; update `TSK-0053` with current evidence rather than clone a new hygiene task. |

## Risk Register

- R-001: Chat/Admin feature work will keep landing in oversized route files, making every new UI change broader and harder to review. Mitigation: `TSK-0189`, `TSK-0190`.
- R-002: MCP feature expansion will continue to bloat `ChatToolCatalog.cs`, increasing schema drift and authorization regression risk. Mitigation: `TSK-0192` before or alongside `TSK-0185` and `TSK-0186`.
- R-003: Memory-domain behavior changes remain concentrated in one service, which raises regression risk across search, CRUD, diagnostics, and telemetry. Mitigation: `TSK-0191` plus existing characterization coverage.
- R-004: Mixed task JSON shapes and legacy imported ids will keep weakening task-tooling predictability if canonicalization stays aspirational. Mitigation: `TSK-0053`.

## Open Questions

- Q-001: Should `TSK-0192` be treated as a prerequisite for `TSK-0185` and `TSK-0186`, or can those new MCP features land safely as one more vertical slice before modularization? Proposed gate: council review on this page's linked council record.
- Q-002: After Chat/Admin extraction, should `Tasks.razor` and `Pages.razor` become first-class decomposition tasks, or should `TSK-0047` measure them for one more cycle first?
- Q-003: Does `MemoryApplicationService` want extraction into multiple services, partials with explicit ownership, or a thinner orchestration facade over specialized collaborators?

## Confidence

- Finding confidence: 90%.
- Sprint sequencing confidence: 84%.
- Residual uncertainty: I did not execute live browser decomposition experiments in this pass, and the exact extraction boundaries for `MemoryApplicationService` should be locked by characterization tests before code changes begin.
