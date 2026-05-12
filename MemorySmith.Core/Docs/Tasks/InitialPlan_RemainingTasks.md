# InitialPlan Remaining Tasks

Date: 2026-04-23
Source: Gap analysis from `Docs/Plans/InitialPlan.md` vs current implementation.

## P0 - Architecture-Blocking

- [ ] Add gRPC API surface in `MemorySmith.Worker` to mirror core REST capabilities.
- [ ] Introduce vector search abstraction (`IVectorIndex`) and baseline implementation (brute-force cosine similarity).
- [ ] Implement semantic search endpoint behavior in `/api/memories/search` using vector index (retain lexical fallback if needed).
- [ ] Implement event audit persistence for lifecycle transitions (`MemoryEvent` sink and wiring from triage pipeline).

## P1 - Migration Path Enablement

- [ ] Add `MemorySmith.Storage.Postgres` project scaffold.
- [ ] Add Postgres store implementation behind `IMemoryStore` (or adapter interface if phased).
- [ ] Add SQL schema/migration files for records, tags, references, conflicts, embeddings.
- [ ] Add worker config switch for storage provider selection (File/Postgres).
- [ ] Add migration/import utility from file store to Postgres.

## P1 - Lifecycle and Worker Completeness

- [ ] Implement `ConsolidationService` behavior (dedupe, merge, rewrite, promotion policy).
- [ ] Define and implement validation/stability criteria for `Working -> Core` beyond score-only threshold.
- [ ] Add graph persistence implementation (`edges.json` or abstraction-backed store).
- [ ] Add embedding persistence implementation under `Data/Graph/embeddings` and lifecycle update policy.

## P2 - Testing and Quality

- [ ] Add integration tests for worker API endpoints (hosted test server / WebApplicationFactory).
- [ ] Add end-to-end tests for hosted services (triage/indexing/consolidation behavior over test data).
- [ ] Add unit tests for `MemoryIndex` add/remove/rebuild behavior.
- [ ] Add CI optional steps: formatting and security/dependency scanning.

## P3 - Scope Clarification and Documentation Hygiene

- [ ] Clarify whether `MemorySmith.Tools` remains in scope; if yes, scaffold project and initial commands.
- [ ] Align plan wording with actual doc location (`MemorySmith.Core/Docs` vs repo-root `docs/`).
- [ ] Add implementation status table to `InitialPlan.md` (`Done`/`Partial`/`Planned`) to prevent ambiguity.
- [ ] Fix markdown heading typo in `Docs/Tasks/Task1.md` (currently starts with `H#`).

## Definition of Done for Plan Closure

- [ ] All P0 items complete.
- [ ] At least first pass of all P1 items complete.
- [ ] Tests updated to cover new behaviors.
- [ ] Plan and progress docs updated with evidence links.
