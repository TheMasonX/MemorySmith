# External project audit External Project Audit — Delta 02

**Description:** Second independent source review identifying additional defects and architecture recommendations not included in the first audit. 
**Timestamp:** 2026-07-27 13:35 CDT 
**Author:** External audit synthesis
**Source metadata:** [redacted]
**Source metadata:** [redacted]
**Review Status:** Peer Review Ready 
**Confidence:** 91%

## Executive Summary

This pass found **three additional source-backed bugs**, **two strongly inferred reliability defects**, and **six architecture/knowledge system recommendations**. The highest-risk new findings are:

1. A status-changing file-store save deletes the old record before the replacement has been serialized and committed, contradicting the method's atomicity contract and creating a proven data-loss window.
2. The administration API can remove the final active administrator role without checking or transactionally preserving the last-admin invariant.
3. Memory ID sanitization is lossy and mutates caller-owned records, allowing distinct logical IDs to collapse to the same persisted identity.

No prior findings are repeated here except where needed to explain shared root causes.

## New Findings Overview

| ID | Finding | Classification | Priority | Confidence |
|---|---|---|---:|---:|
| BUG-005 | Status transition deletes the old record before replacement commit | Proven | P0 | 99% |
| BUG-006 | Final active administrator role can be removed | Proven | P0 | 98% |
| BUG-007 | Lossy ID sanitization permits persistent identity collisions | Proven | P1 | 97% |
| INF-002 | Append-only event log is not safe across processes/instances | Strongly Inferred | P1 | 85% |
| INF-003 | In-place ID mutation can desynchronize caller/index/reference state | Strongly Inferred | P2 | 84% |

## Source-Backed Bugs

### BUG-005: Status transition deletes the old record before replacement commit

**Area:** File-backed persistence 
**Classification:** Proven 
**Category:** Data Integrity, Reliability, Atomicity 
**Priority:** P0 
**Impact:** 10/10 
**Probability:** 6/10 
**Severity:** 60/100 
**Confidence:** 99%

#### Claim

When a record changes status, `FileMemoryStore.Save` deletes its existing file before serializing and atomically moving the replacement. If serialization, directory creation, temporary-file writing, or final move fails, the previous valid record has already been destroyed.

#### Actual Behavior

The method:

1. Finds the existing record.
2. If the status folder differs, calls `File.Delete(existing)`.
3. Creates the destination path.
4. Serializes the record.
5. Writes a temporary file.
6. Moves the temporary file to the final destination.

The method documentation explicitly states that writes preserve the original file if the operation fails, but that is false for a cross-status save.

#### Expected Behavior

A status transition must preserve either the complete prior record or the complete replacement. It must never leave no authoritative record because a later operation failed.

#### Evidence

The reviewed persistence path deletes the prior status record before the replacement has been fully serialized and committed, so a later write failure can remove the only valid copy.

### BUG-006: Final active administrator role can be removed

The administration API validates the role name but does not preserve the last-admin invariant. Removing the final enabled administrator must be rejected inside the same transaction as the role deletion.

#### Actual Behavior

`AdminController.RemoveRole` validates only that the role name is recognized, then calls `RemoveRoleAsync` and records success. `RemoveRoleAsync` executes an unconditional delete for the requested user and normalized role. Neither path checks whether the removal targets the last enabled administrator.

The database already provides `HasAnyAdminAsync`, proving that active-admin existence is a recognized domain concept, but it is not used to protect role removal.

#### Expected Behavior

The system must reject any mutation that would leave zero enabled administrators. The check and deletion must be atomic to avoid two concurrent removals each observing another administrator.

#### Evidence

- ` [redacted source path]` performs unconditional removal after role-name validation.
- ` [redacted source path]` executes an unconditional `DELETE FROM UserRoles`.

### BUG-007: Lossy ID sanitization permits persistent identity collisions

The file-backed store assigns `record.Id = SanitizeId(record.Id)`. Character replacement is many-to-one, so distinct logical IDs can map to one persisted identity and overwrite each other.

#### Recommendation

Reject invalid IDs before mutation or persistence. For legacy IDs, use a reversible collision-resistant encoding and require explicit conflict resolution for ambiguous normalized IDs.

#### Expected Behavior

A logical identifier must be canonical before entering the store and must have one-to-one persistence mapping. Invalid IDs should be rejected, not silently rewritten into an existing identity.

#### Evidence

- ` [redacted source path] [redacted source reference] MemoryId` value object.
- Reject invalid IDs before mutation or persistence.
- For legacy IDs, use a reversible collision-resistant encoding rather than character replacement.
- Detect duplicate normalized IDs during migration and require explicit conflict resolution.

#### Validation Criteria

- Invalid IDs are rejected with no filesystem mutation.
- Distinct accepted IDs always map to distinct storage paths.
- Legacy collision scanning reports every ambiguous pair.
- Save never overwrites a different logical record due to normalization.

## Strongly Inferred Bugs

### INF-002: Append-only event log is not safe across processes or instances

**Area:** Event persistence 
**Classification:** Strongly Inferred 
**Priority:** P1 
**Confidence:** 85%

`FileEventStore.AppendEvent` protects `File.AppendAllText` with an instance-local lock, which does not coordinate multiple processes or service instances.

### INF-003: In-place ID mutation can desynchronize caller and reference state

`FileMemoryStore.Save` mutates the caller-owned record while normalizing its ID. That can leave indexes or references holding the original identifier while persistence uses the rewritten value.

**Missing proof:** A complete caller trace proving a current stale-index manifestation was not completed in this pass.

**Recommendation:** Make IDs immutable and validated at construction. Persistence must not rewrite domain identity.

## Architecture Recommendations

### ARCH-008: Separate identity validation from path encoding

The storage layer currently uses sanitization to solve both domain validation and filesystem safety. These are distinct concerns:

- Domain layer: Is this a valid, unique `MemoryId`? Storage layer: Can its encoding be reversible and collision-free?
- Define explicit outcomes such as `Removed`, `NotAssigned`, `RejectedLastAdmin`, and `UserNotFound`.
- State and test guarantees such as `atomic`, `never`, `always`, `thread-safe`, `secure`, and `transactional` only where they are actually enforced.
- Add a caller/index trace for `FileMemoryStore.Save` to promote INF-003 if stale-key behavior is reachable.
- Filesystem support matrix and durability requirements for supported deployments.
- Existing migration tooling for IDs that have already been sanitized.

## Final Recommendation

**Decision:** Changes Requested 
**Confidence:** 94%

BUG-005 and BUG-006 are release-blocking for any deployment relying on file-backed status transitions or administrative role management. BUG-007 should be fixed before accepting tool- or user-generated IDs at scale. The event-store and caller-mutation concerns require completion of the deployment/caller traces but deserve targeted tests immediately.
