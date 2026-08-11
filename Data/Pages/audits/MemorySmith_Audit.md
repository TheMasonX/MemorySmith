# External project audit External Project Bug and Architecture Audit

**Description:** Evidence-driven audit of the supplied knowledge system source corpus, focused on correctness, security, reliability, architecture, and reusable lessons for the external reviewer knowledge system. 
**Timestamp:** 2026-07-27 13:35 CDT 
**Author:** External audit synthesis
**Source metadata:** [redacted]
**Source metadata:** [redacted]
**Review Status:** Peer Review Ready 
**Confidence:** 86%

## Executive Summary

The supplied corpus contains sufficient primary source to verify four material defects and one strongly inferred workflow defect. The highest-risk findings are in agent-session creation: the concurrent-session cap is enforced through a non-atomic count-then-save sequence, and the source explicitly documents that raw model/provider overrides bypass model-profile role authorization. The maintenance-agent JSON extraction logic is brittle against realistic LLM output. File-backed memory reads also collapse absence, corruption, and operational I/O failure into the same `null` result, making a missing record indistinguishable from a damaged or unavailable store.
- ` [redacted source path]` later constructs and saves the session.
- ` [redacted source path]` saves independently.
- ` [redacted source path]` counts independently.
- The SQLite store likewise exposes count and save as independent operations.

#### Reasoning

For cap `N` and current count `N-1`, request A and request B can both read `N-1`, both pass, and both save. A thread-safe dictionary does not make a multi-operation cardinality invariant atomic.

#### Counterarguments

An upstream layer could serialize requests, but the service contract does not require or enforce that behavior. A unique session ID constraint also does not constrain active sessions per principal.

#### Recommendation

Add a store-level atomic operation such as `TryCreateUnderPrincipalCapAsync(session, cap, ct)`:

- In memory: principal-scoped lock or atomic principal state.
- SQLite: transactional count and insert under a write reservation.

#### Validation Criteria

Synchronize at least `2 × cap` create calls for one principal. Exactly `cap` creations must succeed; excess attempts must fail without creating additional active sessions.

## Model/provider override authorization

The caller-supplied provider and model path must resolve through the profile authorization rules rather than bypassing `ChatModelProfileService` role checks. The decision and any accepted overrides must be persisted with the created session.

#### Impact

A caller may bypass intended restrictions around provider or model selection, with possible cost, privacy, licensing, or data-handling consequences depending on deployment configuration.

#### Counterarguments

Upstream callers may be privileged, but this service already receives a `ClaimsPrincipal` and performs other authorization checks. The source itself states that this path violates the intended profile contract.

#### Recommendation

Accept only `modelProfileId` for ordinary callers, resolve it through `ChatModelProfileService`, and enforce `AllowedRoles` before persisting any provider or model override.

## Fragile JSON extraction

`ExtractJsonObjectPayload` returns everything from the first `{` through the final `}` in the complete response. It does not parse one JSON value and therefore fails for realistic responses containing trailing brace-bearing prose, multiple objects, or malformed suffixes.

#### Evidence

- ` [redacted source path]` uses `IndexOf('{')` and `LastIndexOf('}')` slicing.
- [redacted source reference] sends the result to `JsonSerializer.Deserialize` and catches `JsonException`.

A response containing a valid object followed by `Note: use {placeholder}` produces a combined invalid payload.

#### Counterarguments

The prompt may require strict JSON, but the code already strips code fences and provides a fallback, which demonstrates that non-strict output is expected.

#### Recommendation

Use `Utf8JsonReader` or `JsonDocument.ParseValue` to parse exactly one complete JSON value. Return explicit outcomes such as `Success`, `NoJson`, `MalformedJson`, and `SchemaInvalid`.

## File-store error classification

`FileMemoryStore.Load` returns `null` both when no record exists and when reading or deserializing an existing record throws.

#### Evidence

- ` [redacted source path]` returns `null` for an absent file.
- The store catches operational and deserialization failures and also returns `null`, preventing callers from distinguishing `Found`, `NotFound`, `Corrupt`, and `StorageError` outcomes.

`ParseProposalReview` catches `JsonException` and returns an envelope with a parse-failure or manual-review-required outcome; automated progression should be blocked.

## Architecture Audit

### 1. Invariants are spread across orchestration and weak store primitives

The session service owns cap policy, while stores expose only count and save. This API cannot preserve the invariant atomically. The same pattern can recur for quotas, status transitions, cleanup, reservations, and nesting limits.

**Recommendation:** expose invariant-preserving operations such as `TryCreate`, `TryTransition`, `ExpireIfIdle`, and `TryReserveCapacity`. Derive `AuthorizedAgentSessionConfiguration` from the caller, profile, tool scope, and requested settings, and persist its decision provenance.

### 3. LLM output needs a centralized trust boundary

Model output is untrusted external input. Parsing, schema validation, repair policy, telemetry, and fail-closed behavior should live in one structured-output gateway—not ad hoc brace extraction in workflows.

### 4. Several active files are monolithic change magnets

The manifest shows large active files including [redacted source reference], [redacted source reference], [redacted source reference], [redacted source reference], [redacted source reference], [redacted source reference], [redacted source reference], [redacted source reference], and [redacted source reference].

Size alone is not a defect. These files nevertheless span orchestration, parsing, policy, persistence, UI state, and operations, increasing review cost and semantic-duplication risk.

**Recommendation:** decompose around stable business capabilities and invariants, not arbitrary line counts or partial-class fragments.

### 5. Authoritative and non-authoritative artifacts share a repository surface

The exported tree contains source and tests alongside dated audits, council reports, historical plans, generated embeddings, training exports, benchmark outputs, and archived task material. If co-indexed without authority metadata, RAG can treat stale proposals and AI summaries like current implementation evidence.

### 6. Public retrieval contracts overlap

The project exposes lexical, semantic, hybrid, unified, page, code, task, direct-get, and context-pack concepts. The specialization is useful, but overlapping public names and argument conventions increase routing entropy.

**Recommendation:** consider a smaller stable public contract:

- `resolve(resourceRef)` for typed direct lookup.
- `search(query, scopes, mode, filters, page)` for discovery.
- `contextPack(roots, traversalPolicy, budget)` with an explicit review outcome such as `MaintenanceProposalReviewEnvelope.Recommendation`.
- Deployment evidence showing whether file-store diagnostics are always enabled.
- Build and test results for the exact supplied revision.

## Final Recommendation

**Decision:** Changes Requested for the agent-session and maintenance-review paths. 
**Confidence:** 90%

BUG-001 and BUG-002 should be treated as required fixes before the agent-session surface is considered hardened. BUG-003 and INF-001 should fail closed before automated proposal review is trusted. BUG-004 should be corrected before file-backed retrieval is treated as authoritative without an external health guard.
