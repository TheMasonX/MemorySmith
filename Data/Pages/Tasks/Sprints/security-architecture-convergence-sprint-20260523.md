# Sprint Plan - Security + Architecture Convergence (2026-05-23)

## Sprint Objective

Reduce structural complexity and security risk together by decomposing high-risk modules while enforcing explicit trust-boundary and configuration safety invariants.

## Capacity Assumptions

- Team size: 1 maintainer with agent support.
- Effective days: 4.
- Risk buffer: 30% for behavior-preserving extraction and validation.

## Committed Items

- TSK-0048 Establish security invariants for architectural refactor seams.
- TSK-0049 Converge chat service decomposition with trust-boundary protections.
- TSK-0050 Converge maintenance decomposition with proposal/approval governance protections.
- TSK-0052 Add task-store parse fault isolation and malformed-record recovery flow.
- TSK-0054 Standardize task mutation error mapping for malformed-record safety.

## Stretch Items

- TSK-0051 Add combined architecture-security drift guardrails in CI/local checks.
- TSK-0053 Add task JSON contract canonicalization and compatibility guardrails.

## Exit Criteria

- Refactor seams include explicit security invariants and tests.
- No regressions in approval gating, request guard, or role-based write permissions.
- Architectural decomposition reduces hotspot concentration with traceable behavior parity.

## Demo Targets

- Before/after module map for chat and maintenance services.
- Green invariant tests covering key security trust boundaries.
- Convergence dashboard showing linked security and architecture task progression.

## Task Links

- TSK-0048: foundational invariant gates for converged delivery.
- TSK-0049: chat architecture + security convergence path.
- TSK-0050: maintenance architecture + governance convergence path.
- TSK-0051: sustained drift detection after refactor/hardening.
- TSK-0052: task-surface reliability and governance continuity under bad artifact input.
- TSK-0053: task contract canonicalization and migration-safe governance checks.
- TSK-0054: non-500 mutation behavior and consistent client-facing error contracts for malformed task records.

## Assumptions

- Security and architecture changes are delivered in the same PR slices where possible.
- Localhost development remains functional under documented compatibility profile.
- Refactor speed is secondary to trust-boundary correctness.

## Confidence

- Sprint confidence: 84%
