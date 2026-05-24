# Sprint Plan - CI/CD Testing Regression Guardrails (2026-05-24)

## Sprint Objective
Establish enforceable browser-regression quality gates in CI so navigation lockups and route-level breakages are caught before merge.

## Capacity Assumptions
- Team size: 1 maintainer with agent-assisted delivery.
- Sprint length: 1 week.
- Effective days per sprint: 4.
- Risk buffer: 30% for flaky-test hardening and CI tuning.

## Sprint 1 - Blocking Browser Gate Foundation

### Objective
Convert existing local browser regression coverage into required pull-request gates with actionable artifacts.

### Committed Items
- TSK-0067 Add required PR Playwright navigation-freeze gate. (M, depends on stable app boot in CI)
- TSK-0068 Add browser smoke matrix and artifact publication for critical routes. (M, depends on TSK-0067)
- TSK-0069 Split CI test topology and document branch-protection required checks. (S, depends on TSK-0067)

### Stretch Items
- Initial flaky-test quarantine policy draft and annotation conventions.

### Exit Criteria
- Pull requests run and enforce at least one deterministic browser regression gate.
- Browser failures emit traces/screenshots/videos as artifacts.
- CI check names are stable and ready for required-status protection.

### Demo Targets
- Failing/green PR examples for navigation-freeze guard.
- Artifact bundle walkthrough for one induced UI navigation failure.
- Updated CI topology diagram and required checks list.

## Sprint 2 - Coverage Expansion and Governance

### Objective
Broaden browser resilience coverage and align documentation/governance so quality gates stay trustworthy.

### Committed Items
- TSK-0070 Expand route interaction regressions and freeze diagnostics across core workbench pages. (M, depends on TSK-0068)
- TSK-0071 Align validation docs and runbooks with enforced CI checks and triage flow. (S, depends on TSK-0069)

### Stretch Items
- Add optional nightly broader browser suite for non-blocking exploratory coverage.

### Exit Criteria
- Core route smoke coverage is implemented with per-route failure attribution.
- Documentation and runtime quality gates are aligned and auditable.
- Regression-triage workflow (including flaky-test handling) is explicit.

### Demo Targets
- Route matrix run summary with pass/fail attribution.
- Updated docs showing exact mandatory validation paths.
- Example incident triage from artifact to root cause.

## Task Links
- TSK-0067: Addresses F-001, F-002.
- TSK-0068: Addresses F-003, F-004.
- TSK-0069: Addresses F-001, R-002.
- TSK-0070: Addresses F-004.
- TSK-0071: Addresses F-005.

## Assumptions
- Browser tests can run on Ubuntu CI runners with deterministic startup.
- Existing local route-hop spec is a suitable seed for required gating.
- CI runtime increase remains acceptable after topology split.

## Risks
- New browser checks may be flaky during initial rollout.
- App startup timing in CI may require explicit readiness probes.
- Artifact retention policies may need tuning for storage costs.

## Confidence
- Sprint 1 delivery confidence: 80%
- Sprint 2 delivery confidence: 76%
