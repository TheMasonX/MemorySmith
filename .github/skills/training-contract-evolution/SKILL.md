---
name: training-contract-evolution
description: "Evolve training request/status/benchmark/event contracts with compatibility checks and evidence-driven validation."
argument-hint: "Field additions/changes and expected producers/consumers"
user-invocable: false
disable-model-invocation: false
---

# Training Contract Evolution

Internal workflow for safe contract changes across training surfaces.

## Scope
- Request payload fields.
- Status metrics (especially done-phase).
- Benchmark payload metrics.
- Event payload consistency.

## Procedure
1. Define contract change and affected surfaces.
2. Update all producers.
3. Verify consumer assumptions and UI render paths.
4. Validate via focused tests and one strict run artifact check.
5. Confirm non-secret policy for persisted fields.

## Compatibility Rules
- Additive fields preferred.
- Avoid breaking existing field names without migration.
- Keep security-sensitive values out of artifacts.

## Output
- Contract delta summary.
- Evidence from status/benchmark/events.
- Backward-compat risk assessment.
