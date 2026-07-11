# Sprint Plan - Codebase Health Refactor (2026-05-23)

## Sprint Objective

Reduce codebase complexity and maintenance cost by decomposing oversized services/tests and trimming dependency surface with behavior-preserving, low-risk slices.

## Capacity Assumptions

- Team size: 1 maintainer with agent-assisted delivery.
- Effective days per sprint: 4.
- Risk buffer: 30% for regression validation and extraction sequencing.

## Sprint 1 - Hotspot Decomposition

### Objective

Lower immediate complexity risk in the highest-impact files while keeping behavior stable.

### Committed Items

- TSK-0042 Decompose ChatServices into bounded modules.
- TSK-0043 Decompose MaintenanceAgentServices into bounded modules.
- TSK-0044 Split PagesAndChatTests into focused fixtures and shared builders.

### Stretch Items

- TSK-0045 Split TaskDomainService into orchestration/storage/contract layers.

### Exit Criteria

- ChatServices and MaintenanceAgentServices are materially reduced in file size and concern count.
- PagesAndChatTests is replaced by focused fixtures with equivalent behavior coverage.
- No critical regression in chat, pages, tasks, or search flows.

### Demo Targets

- Before/after file topology and responsibility mapping.
- Green targeted test suites for refactored domains.
- Representative PR slices showing reduced diff blast radius.

## Sprint 2 - Dependency and Guardrail Consolidation

### Objective

Complete remaining decomposition and lock in sustainable complexity controls.

### Committed Items

- TSK-0045 Split TaskDomainService into orchestration/storage/contract layers.
- TSK-0046 Run dependency hygiene pass and remove/justify stale package references.
- TSK-0047 Add refactor guardrails for complexity/architecture drift detection.

### Stretch Items

- Follow-up optimization backlog derived from new guardrail telemetry.

### Exit Criteria

- Candidate stale dependencies are either removed or explicitly justified.
- Guardrails catch complexity drift early in CI/local validation.
- Task and service boundaries are documented and aligned to the simplification plan.

### Demo Targets

- Dependency report before/after and validation results.
- Guardrail checks integrated into routine validation.
- Updated architecture notes mapping new service boundaries.

## Task Links

- TSK-0042: Addresses F-001.
- TSK-0043: Addresses F-002.
- TSK-0044: Addresses F-003.
- TSK-0045: Addresses F-004.
- TSK-0046: Addresses F-005.
- TSK-0047: Addresses F-006.

## Assumptions

- Behavior preservation takes precedence over maximal restructuring speed.
- Refactor slices can be validated incrementally with focused tests.
- Dependency trimming should not remove optional capabilities without explicit decision.

## Confidence

- Sprint sequencing confidence: 89%
- Sprint 1 delivery confidence: 81%
- Sprint 2 delivery confidence: 77%
