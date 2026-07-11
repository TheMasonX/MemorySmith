# MemorySmith Dependency-Ordered Refactor Plan

## Goal

Reduce brittle fallback behavior, tighten implicit contracts, and make the codebase easier to evolve without reintroducing legacy patterns.

This plan is ordered so each step removes blockers for the next one.

## Ordering principles

1. Stabilize shared contracts first.
2. Separate load/parse/validate concerns before changing behavior.
3. Remove silent fallback paths before tightening invariants.
4. Add concurrency and atomicity protections before expanding features.
5. Split large orchestration classes only after their boundaries are clear.

---

## Phase 0 — Safety rails and test coverage

### 0.1 Add regression tests for current brittle paths

**Why first:** every later refactor depends on having a way to prove behavior has not drifted.

Cover at minimum:
- unreadable task files vs malformed JSON
- malformed task deletion/quarantine behavior
- attachment filename collision under concurrent writes
- settings override load vs update symmetry
- OAuth callback state lifecycle
- migration partial-failure recovery

**Acceptance:** each brittle path has a failing test before the fix and a passing test after.

### 0.2 Add a small shared test helper layer

**Why first:** these flows currently span filesystem, auth, and JSON handling. A thin helper layer keeps the later refactors from turning into test duplication.

Suggested helpers:
- temp filesystem root builder
- synthetic task file builder
- settings override file builder
- authenticated HTTP context factory
- migration database fixture

**Acceptance:** new tests avoid reimplementing the same setup code.

---

## Phase 1 — Make configuration behavior authoritative

### 1.1 Collapse settings override discovery to one explicit path policy

**Depends on:** Phase 0 tests

Current issue: settings override discovery is helpful but brittle, with silent fallback when files are missing or unreadable.

Target shape:
- one primary override path per environment
- explicit migration path for old locations if needed
- loud failure for invalid settings in non-test environments

**Implementation notes:**
- keep path resolution in one helper
- keep "where do we read from?" separate from "how do we parse?"
- stop silently accepting unreadable override files outside local/dev

**Acceptance:** configuration load either succeeds deterministically or fails with a clear error.

### 1.2 Make settings load and settings write share the same error policy

**Depends on:** 1.1

Current issue: load is forgiving, write is brittle.

Target shape:
- same validation rules on read and write
- same file atomicity guarantees on write
- same error surface for invalid/unavailable files

**Acceptance:** a corrupted settings file cannot be accepted on load while still causing unhandled failures on update.

---

## Phase 2 — Separate task data concerns

### 2.1 Split task file reading from task parsing

**Depends on:** Phase 0 tests

Current issue: unreadable files can still crash the task loader before the parse fallback logic runs.

Target shape:
- file read errors become explicit load failures
- JSON parse errors become malformed-record fallbacks
- parse fallback is not used as a catch-all for every file problem

**Acceptance:** one unreadable task file does not abort the whole task list.

### 2.2 Add a quarantine/delete path for malformed tasks

**Depends on:** 2.1

Current issue: malformed tasks become read-only and effectively undeletable through the UI.

Target shape:
- malformed tasks can still be removed or quarantined
- normal edit rules remain strict for healthy records
- malformed-file handling does not trap data forever

**Acceptance:** a broken task file can be removed from the app without manual filesystem intervention.

### 2.3 Extract a task loader/quarantine component from `TaskDomainService`

**Depends on:** 2.1 and 2.2

Current issue: one large class owns loading, normalization, mutation, scoring, attachment policy, and quarantine behavior.

Target shape:
- loader component
- mutation component
- attachment policy component
- search/query projection component

**Acceptance:** each concern can be tested independently and the main service becomes orchestration only.

---

## Phase 3 — Tighten task contracts

### 3.1 Enforce explicit allowlists for task fields

**Depends on:** Phase 2

Current issue: the UI presents fixed options, but the service layer still accepts arbitrary status/priority/type values.

Target shape:
- service validates status, priority, and type against defined sets
- invalid values fail early
- no silent coercion unless it is explicitly intended

**Acceptance:** task state stored on disk always matches the allowed contract.

### 3.2 Define completion semantics for reopen/update flows

**Depends on:** 3.1

Current issue: `CompletedAtUtc` can remain stale after a task leaves `Done`.

Target shape:
- either "ever completed" semantics are documented and tested
- or `CompletedAtUtc` is cleared on exit from `Done`

**Acceptance:** reporting and UI behavior cannot disagree about whether a task is currently complete.

### 3.3 Normalize task identifiers and paths through one canonical helper

**Depends on:** 2.3

Current issue: path and id normalization is spread across the task service and attachment helpers.

Target shape:
- one canonical path resolver
- one canonical task-id normalizer
- one canonical slug sanitizer

**Acceptance:** the same logical task cannot appear under multiple path interpretations.

---

## Phase 4 — Fix file-backed attachment semantics

### 4.1 Make attachment filename generation atomic

**Depends on:** Phase 0 tests and 2.3

Current issue: uniqueness is checked by probing, then writing, which is race-prone.

Target shape:
- generate a unique storage filename before writing
- preserve human-friendly display name separately
- avoid relying on `File.Exists` as a lock

**Acceptance:** concurrent uploads cannot collide on the same attachment storage name.

### 4.2 Add attachment cleanup to hard delete

**Depends on:** 4.1

Current issue: hard delete removes the task JSON but leaves attachment artifacts behind.

Target shape:
- hard delete removes task data, attachment files, and related side artifacts
- soft delete remains explicit and reversible

**Acceptance:** hard delete actually means the task’s file-backed data is gone.

### 4.3 Separate public URI generation from filesystem layout

**Depends on:** 4.1

Current issue: public artifact paths and storage paths are too tightly coupled.

Target shape:
- storage path is internal
- public URI is derived, not authoritative

**Acceptance:** storage can move without changing the app’s public contract.

---

## Phase 5 — Harden auth and request boundaries

### 5.1 Add explicit OAuth state lifecycle handling

**Depends on:** Phase 0 auth tests

Current issue: callback handling needs stricter one-time validation semantics.

Target shape:
- state created at challenge
- state consumed at callback
- state invalidated after use

**Acceptance:** replayed callback state is rejected.

### 5.2 Make rate limiting and loopback logic proxy-aware or explicitly direct-only

**Depends on:** 5.1

Current issue: IP-based trust can become wrong behind a proxy.

Target shape:
- either direct-to-app only, documented and enforced
- or forwarded-header-aware with strict validation

**Acceptance:** the limiter and loopback checks use the intended client identity.

### 5.3 Narrow antiforgery opt-outs

**Depends on:** 5.1

Current issue: controller-wide antiforgery exclusion is broader than needed.

Target shape:
- keep global antiforgery by default
- opt out only the specific endpoints that need it

**Acceptance:** browser POST endpoints follow the narrowest safe CSRF policy.

---

## Phase 6 — Database and migration reliability

### 6.1 Make SQLite migrations explicitly atomic where possible

**Depends on:** Phase 0 persistence tests

Current issue: partial migration application can leave the database in an ambiguous state.

Target shape:
- transaction per migration where supported
- migration marked applied only after successful commit

**Acceptance:** a failed migration cannot be mistaken for a successful one.

### 6.2 Add dirty-migration detection or checksum validation

**Depends on:** 6.1

Current issue: partial failure recovery still needs a guard for already-mutated schemas.

Target shape:
- detect incomplete/partially applied migration state
- fail loudly instead of attempting undefined re-apply behavior

**Acceptance:** startup can distinguish clean schema, pending schema, and dirty schema.

### 6.3 Isolate migration ownership from business logic

**Depends on:** 6.1 and 6.2

Current issue: migration knowledge is still close to the runtime store implementation.

Target shape:
- dedicated migration manager
- store implementation only consumes the current schema contract

**Acceptance:** storage code no longer needs to reason about migration sequencing in multiple places.

---

## Phase 7 — Consolidate large orchestration surfaces

### 7.1 Split `TaskDomainService` into focused collaborators

**Depends on:** Phases 2–4

Suggested collaborators:
- loader/quarantine service
- mutation service
- attachment service
- search/query service
- task identity / normalization helpers

**Acceptance:** the main service becomes a small composition layer.

### 7.2 Reduce duplicated validation logic across UI and service layers

**Depends on:** 7.1

Current issue: the UI often mirrors rules that the backend also needs, but the rules are not yet centralized.

Target shape:
- single authoritative validation source
- UI only reflects rules, not defines them

**Acceptance:** changes to task rules require one code path, not two.

---

## Phase 8 — Suggested task ledger updates

These are corrections or expansions, not new code changes by themselves.

- `TSK-0264` → broaden from malformed tasks to malformed + unreadable tasks, and add quarantine/delete escape hatch.
- `TSK-0181` → include load/write symmetry and I/O / permission handling for settings overrides.
- `TSK-0148` or `TSK-0194` → add attachment filename collision and concurrent upload coverage.
- `TSK-0045` → expand to explicit loader/quarantine split and DTO boundary extraction.
- `TSK-0041` → add OAuth state lifecycle / replay protection.
- new task if needed → migration atomicity and dirty migration detection.

---

## Suggested execution order summary

1. Add tests.
2. Fix config behavior.
3. Split task load from parse.
4. Add malformed-task quarantine/delete.
5. Enforce task field allowlists and completion semantics.
6. Fix attachment atomicity and cleanup.
7. Harden OAuth callback lifecycle and proxy trust.
8. Harden migration atomicity.
9. Split the large orchestration services.

---

## Exit criteria for the refactor pass

- No silent config fallback remains for operationally important paths.
- One unreadable task file cannot take down the workbench.
- Broken tasks are removable without manual filesystem surgery.
- Attachment uploads are collision-safe and deletions are complete.
- OAuth callbacks cannot be replayed by stale state.
- Migrations either succeed cleanly or fail with a detectable dirty state.
- Large orchestration classes are reduced to thin coordinators.

