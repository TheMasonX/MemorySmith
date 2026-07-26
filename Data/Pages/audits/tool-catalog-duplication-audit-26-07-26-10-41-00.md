# MemorySmith Delta Audit

## Tool Catalog Duplication & Input Coercion

**Report ID:** `MS-AUDIT-9F5C8E2B8D4A7C31`
**Repository:** MemorySmith
**Branch:** `dev/sprint-1`
**Commit Audited:** `62810376c17af1f7a782092d8d666bcc2148cc70`
**Report ID:** `MS-AUDIT-9F5C8E2B8D4A7C31`

---

# Executive Summary

This audit slice focused exclusively on `ChatToolCatalog` and its surrounding tool infrastructure, looking for:

* semantic (Type 4) duplication
* primitive obsession
* brittle parsing
* hidden assumptions
* schema duplication
* maintainability issues
* opportunities to reduce long-term technical debt

No critical correctness bugs were identified.

However, this section of the codebase still contains several examples of duplicated behavior that will become increasingly expensive as the tool surface grows.

Overall code quality remains high, but these findings should be addressed before significantly expanding the MCP/tool ecosystem.

---

# New Findings

| ID    | Severity | Confidence | Category              |
| ----- | -------- | ---------- | --------------------- |
| D-050 | Medium   | 91%        | Type 4 Duplication    |
| D-051 | Medium   | 89%        | Schema Duplication    |
| D-052 | Low      | 82%        | Hidden Input Coercion |

---

# D-050 — Memory Search Query Readers Are Functionally Duplicated

**Severity:** Medium

**Confidence:** 91%

## Evidence

The following methods all perform nearly identical work:

* `ReadLexicalQuery`
* `ReadSemanticQuery`
* `ReadHybridQuery`

Each:

* extracts identical fields
* validates identical properties
* builds nearly identical objects

The only meaningful variation is the destination query type.

This is classic semantic duplication (Type 4 clone).

---

## Why This Matters

Any future change to:

* query validation
* default limits
* tag handling
* status parsing

requires three independent edits.

Eventually these implementations will drift.

---

## Recommendation

Replace with one shared helper.

For example:

```
SearchArguments ReadSearchArguments(...)
```

or

```
ReadQuery<T>()
```

using a factory delegate.

One implementation should own parsing.

Individual query types should only own construction.

---

# D-051 — Task JSON Schemas Repeat the Same Domain Vocabulary

**Severity:** Medium

**Confidence:** 89%

---

The task tools currently construct multiple JSON schemas independently:

* Create
* Update
* Status
* Comment
* Attachment

Although each operation differs slightly, nearly all repeat the same:

* property names
* descriptions
* JSON object construction
* required field handling

---

## Why This Matters

Every task model evolution becomes shotgun surgery.

Example:

Adding

```
Priority
```

could require edits in several schema builders.

Missing one creates inconsistent MCP contracts.

---

## Recommendation

Introduce reusable schema fragments.

Example:

```
TaskSchemaBuilder
```

or

```
TaskFieldDefinitions
```

Operations then compose shared fragments instead of rebuilding them.

---

# D-052 — Hidden String Coercion

**Severity:** Low

**Confidence:** 82%

---

`ReadString()` accepts actual strings.

However, for non-string JSON values it falls back to:

```
node.ToString()
```

meaning:

Objects

↓

become JSON text

Arrays

↓

become JSON text

Numbers

↓

become strings

Booleans

↓

become strings

---

## Why This Matters

Malformed input is silently accepted.

The resulting data may still "work" while no longer representing what the caller intended.

This masks upstream bugs.

Silent recovery is often worse than explicit failure because contract drift becomes invisible.

---

## Recommendation

Split responsibilities:

```
ReadStrictString(...)
```

returns only strings.

```
CoerceToString(...)
```

performs explicit conversion.

Call sites must intentionally choose one.

This documents behavior and prevents accidental coercion.

---

# Architectural Observations

## Tool Catalog Growth

The tool catalog is gradually evolving from:

> registry

into

> registry + parser + schema builder + validation layer

Those responsibilities are beginning to mix.

Although still manageable, this file is becoming a hotspot.

Future work should continue moving toward:

* schema modules
* parser modules
* registration modules

instead of one monolithic catalog.

---

## Semantic Duplication Trend

This audit continues a recurring pattern identified in previous reports.

The project generally avoids copy/paste.

Instead, duplication appears as repeated behavioral patterns:

* schema builders
* parser helpers
* provider adapters
* argument normalization

These are more dangerous than literal duplication because they often escape clone detectors.

---

# Existing Task Relationship

These findings appear to extend—not duplicate—existing work.

Most relevant:

* **TSK-0192**

  * Expand scope to include parser extraction and shared schema fragments.

Potential follow-on work:

* Shared schema composition library
* Shared search argument parser
* Shared tool input normalization layer

---

# Suggested Implementation Order

1. Extract shared search argument reader.
2. Extract reusable task schema fragments.
3. Split strict vs coercive string parsing.
4. Continue decomposing `ChatToolCatalog` into smaller focused modules.

---

# Open Questions

* Should malformed tool arguments fail fast or continue favoring resilience?

* Should schema fragments become first-class reusable objects?

* Is the long-term vision one centralized tool contract library shared by MCP, providers, and chat services?

Clarifying these decisions now will reduce future architectural drift.

---

# Overall Assessment

No blocking defects were identified in this slice.

The primary opportunity is reducing semantic duplication before the tool catalog expands further.

Addressing these issues now will:

* reduce maintenance cost
* improve contract consistency
* simplify future MCP expansion
* reduce the chance of subtle behavioral drift

---

# Confidence Summary

| Finding | Confidence |
| ------- | ---------- |
| D-050   | 91%        |
| D-051   | 89%        |
| D-052   | 82%        |
