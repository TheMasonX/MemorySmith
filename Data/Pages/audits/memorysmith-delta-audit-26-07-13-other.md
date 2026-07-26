# MemorySmith delta audit — duplication and smell pass

## Executive summary

This pass found a second layer of structural duplication beyond the earlier auth/source-link issues. The most important new pattern is that the core memory model is still split into several near-overlapping shapes, and the file-backed stores repeat the same atomic-write / cleanup / corrupt-file policy in three places. Those two areas are where consolidation will pay down the most debt fastest.

The repo also still carries primitive-obsession and naming drift in the event models, plus a small but real repeated-status-counting smell in the stats factory. None of these are catastrophic alone, but together they create a pattern where one conceptual change fans out across many files and is easy to implement inconsistently.

Overall confidence in these new deltas: **88%**. I did not execute external linters in this environment; these findings come from direct cross-file code inspection and duplication pattern comparison on the current commit.

## New findings

| ID    | Severity | Confidence | Finding                                                                                                                                                                                          | Why it matters                                                                                                                         | Evidence |
| ----- | -------: | ---------: | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------- | -------- |
| D-007 |   Medium |        91% | `MemoryRecord` and `MemoryMetadata` are a clear data-clump pair with repeated scalar fields and collection shapes.                                                                               | Any future rename or semantic tweak to shared memory-list fields will need parallel edits and is likely to drift.                      |          |
| D-008 |     High |        95% | `FileMemoryStore`, `FileVarStore`, and `FileEventStore` repeat the same file-persistence shape: path bootstrapping, temp-write/move behavior, catch-all cleanup, and silent corruption fallback. | This is duplicated policy, not just duplicated syntax. A fix in one store is not guaranteed to reach the others.                       |          |
| D-009 |   Medium |        86% | `MemoryEvent` and `MemoryUpdateEvent` are primitive-obsessed and semantically inconsistent: both use raw `Action` strings, while identifiers differ (`MemoryId` vs `Id`).                        | The same concept is encoded differently across two event types, making downstream handling brittle and encouraging ad hoc conversions. |          |
| D-010 |      Low |        84% | `StatsSnapshotFactory.Build()` repeats one count expression per `MemoryStatus` instead of deriving the counts generically.                                                                       | This is small now, but it hard-codes status knowledge into the factory and invites repeated edits when status handling expands.        |          |
| D-011 |   Medium |        83% | `MemoryRecord.Tags`, `References`, `Conflicts`, and `MemoryMetadata.Tags` keep domain meaning in raw `List<string>` fields.                                                                      | That is primitive obsession in the storage model; it makes validation and meaning travel separately from the data.                     |          |

## Detailed findings

### D-007 — Memory model data clumps

`MemoryRecord` and `MemoryMetadata` share the same conceptual core: `Id`, `Title`, `Status`, `Confidence`, `Tags`, `UsageCount`, and `LastUpdated` are repeated across both types. `MemoryRecord` adds content and relationships, while `MemoryMetadata` trims the payload for list views, but the overlap is still large enough that these are a classic data-clump pair.

**Fix:** Introduce a shared value object for the common shape, or a canonical domain type with a lightweight projection for list views. Keep the list-view type as a projection, but make it derive from one shared source of truth rather than re-declaring the same field set.
**Confidence:** 91%

### D-008 — Repeated file-store persistence skeleton

`FileMemoryStore` writes JSON by sanitizing IDs, creating directories, writing to a temp file, moving it into place, and swallowing cleanup errors. `FileVarStore` does essentially the same write/move/cleanup pattern for `vars.json`, and `FileEventStore` repeats the “open file, serialize, append, catch-all read fallback, skip malformed input” policy in a third variant. That is more than shared style; it is duplicated storage policy.

**Fix:** Extract a small file-store utility layer that owns:

1. atomic write,
2. temp-file cleanup,
3. corrupt-file reporting,
4. read/deserialize-with-diagnostics behavior.

Then let each store supply only its path, serializer shape, and record-specific mapping.
**Confidence:** 95%

### D-009 — Event model naming drift and primitive obsession

`MemoryEvent` uses `MemoryId`, `Action`, and `Details`. `MemoryUpdateEvent` uses `Id`, `Action`, and `Timestamp`. The same “what happened to which memory?” concept is encoded in two different shapes, and both rely on raw strings for the action kind. That is a naming and type-design smell at the same time.

**Fix:** Normalize the event contract around one identifier name and replace `Action` with a small enum or discriminated value object. If the two event types are intentionally different, make the distinction explicit in names, not just in fields.
**Confidence:** 86%

### D-010 — Repeated status counting in stats snapshot

`StatsSnapshotFactory.Build()` hard-codes one `.Count(...)` call for each `MemoryStatus` value. That is straightforward, but it means the factory has to know every status and gets edited whenever status handling changes. It is a small repeated-switch shape, just expressed as multiple count calls rather than a `switch`.

**Fix:** Derive the counts via grouping or a dictionary keyed by status. That removes the repeated count shape and makes the snapshot resilient to future status additions.
**Confidence:** 84%

### D-011 — Primitive obsession in domain collections

The model keeps `Tags`, `References`, and `Conflicts` as raw `List<string>` collections. The data clearly represents domain concepts, but the type system does not help distinguish a tag from a relationship reference or a conflict marker. `MemoryMetadata` repeats the same `Tags` shape.

**Fix:** Move toward small value types or dedicated collection wrappers for the concepts that matter semantically. At minimum, introduce a single `MemoryTag` / `MemoryReference` / `MemoryConflict` vocabulary layer so validation and meaning are not scattered.
**Confidence:** 83%

## Task mapping and backlog fit

I did not find an existing `TSK-###` item that cleanly covers the model-shape consolidation or the cross-store file utility extraction. `TSK-0157` is about splitting the SQLite adapter, which is adjacent in spirit but not the same problem; it should stay focused on SQLite decomposition rather than absorbing file-store policy duplication.

Likewise, `TSK-0042` is about `ChatServices` decomposition and is unrelated to these storage/model smells. `TSK-0023` is already closed and covers remote-access guardrails, so it should not be reopened for these new findings.

**Recommended task split:**

* a new task for shared file-store persistence utilities,
* a new task for core memory model consolidation,
* a small follow-on task for event-contract normalization and stats derivation.

## Implementation guidance

The safest order is:

1. Extract a shared file-store helper first, because it will reduce duplicate error policy immediately.
2. Consolidate the core memory shape next, so downstream projections are derived from one model.
3. Normalize event naming and action typing after that.
4. Simplify `StatsSnapshotFactory` last; it is low-risk and easy to verify.

That sequence reduces the blast radius of future model changes and removes several places where one logical change currently requires multiple edits.

## Assumptions and open questions

* Assumption: the file-store classes are intended to remain separate responsibilities, but their shared I/O policy should not remain duplicated.
* Assumption: `MemoryRecord` and `MemoryMetadata` are not meant to diverge semantically as much as their current declarations suggest.
* Open question: should `Action` become an enum now, or should the team first stabilize the event taxonomy before tightening the type?
* Open question: should `MemoryMetadata` be a projection only, or should it become a shared base or record-like core object?

## Confidence notes

* D-007: 91% — the repeated field set is obvious and substantial.
* D-008: 95% — the file-store duplication is highly similar across three implementations.
* D-009: 86% — naming drift is clear, though the intended taxonomy may still be evolving.
* D-010: 84% — the repeated count shape is direct, but low severity.
* D-011: 83% — raw string collections are a real smell, though some may remain as compatibility bridges.
