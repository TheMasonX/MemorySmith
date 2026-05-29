---
name: training-contract-evolution
description: "Evolve training request/status/benchmark/event contracts with compatibility checks and evidence-driven validation."
argument-hint: "Field additions/changes and expected producers/consumers"
user-invocable: false
disable-model-invocation: false
---

# Training Contract Evolution

Inherits from `task-core-loop`.

Internal workflow for safe contract changes across training surfaces.

## Scope
- Request payload fields.
- Status metrics (especially done-phase).
- Benchmark payload metrics.
- Event payload consistency.

## Procedure Additions
1. Apply `task-core-loop` steps first.
2. Define contract change and affected surfaces.
3. Update all producers.
4. Verify consumer assumptions and UI render paths.
5. Validate via focused tests and one strict run artifact check.
6. Confirm non-secret policy for persisted fields.

## Compatibility Rules
- Additive fields preferred.
- Avoid breaking existing field names without migration.
- Keep security-sensitive values out of artifacts.

## Output
- Contract delta summary.
- Evidence from status/benchmark/events.
- Backward-compat risk assessment.
