**File name:**

`memorysmith_audit_deltas_2026-07-05_v11.md`

---

# MemorySmith Audit Deltas Addendum (v11)

**Scope:** This report contains **only new findings and corrections** beyond the previous audit passes. Previously reported issues are intentionally omitted.

---

# Executive Summary

### High-priority new findings

| ID    | Area                                     | Severity | Confidence |
| ----- | ---------------------------------------- | -------- | ---------- |
| D-001 | Agent tool filtering                     | High     | 97%        |
| D-002 | Code search cache/provider coupling      | High     | 92%        |
| D-003 | Task lifecycle invariants                | Medium   | 96%        |
| D-004 | Task creation validation                 | Medium   | 91%        |
| D-005 | Duplicate availability rules             | Medium   | 95%        |
| D-006 | Domain invariants spread across services | Medium   | 89%        |

---

# D-001 Agent-only tools are vulnerable to capability drift

## Finding

The project now distinguishes between:

* chat-visible tools
* agent-visible tools

However, the filtering logic appears to still be centered around chat visibility in at least one execution path.

This creates a structural problem:

```
Tool Definition
        ↓
 AvailableInChat
 AvailableInAgent
        ↓
Session filtering
        ↓
Agent execution
```

If the filtering layer is checking only one availability flag, any future agent-only tool silently disappears.

This is especially concerning because recent work has added dedicated multi-turn agent capabilities.

---

## Why this matters

This is not a current correctness failure for every tool.

It is a **future maintenance trap**.

The project has already introduced two separate concepts:

* available in chat
* available in agent

The filtering logic should not duplicate availability rules.

Duplicated policy almost always drifts.

---

## Recommendation

Create a single policy function.

Example:

```
ToolVisibility.CanUse(
    tool,
    executionMode,
    session)
```

Everything should go through that.

Never duplicate:

```
AvailableInChat &&
...
```

or

```
AvailableInAgent &&
...
```

throughout the codebase.

---

Confidence: **97%**

---

# D-002 Search cache is coupled to queries instead of search capabilities

## Finding

Current cache behavior appears to primarily consider:

* query
* search scope
* limits

However search capability itself can change:

* embeddings unavailable
* embeddings restored
* lexical fallback
* semantic search

The cache does not appear to explicitly incorporate provider state.

---

## Why this matters

Consider:

```
Embeddings fail
↓

fallback search

↓

results cached

↓

embeddings recover

↓

cached lexical results continue
```

The user receives stale-quality answers despite the system recovering.

---

## Recommendation

Cache keys should include search capability generation.

For example:

```
IndexGeneration

EmbeddingGeneration

ProviderGeneration
```

instead of just query text.

---

Confidence: **92%**

---

# D-003 Task lifecycle invariants are distributed

## Finding

Task lifecycle currently appears to update several related values independently.

Examples include:

* Status
* Completed timestamp
* Archived state

These should behave as one state machine.

Instead, different services appear responsible for different pieces.

---

## Why this matters

Distributed lifecycle logic creates impossible states.

Examples:

```
Completed
CompletedAt == null
```

or

```
In Progress

CompletedAt != null
```

or

```
Archived

IsArchived == false
```

Even if only one of these currently exists, the architecture encourages more.

---

## Recommendation

Move lifecycle transitions behind a single API.

Example:

```
TaskLifecycleTransition.Apply(...)
```

All derived fields update together.

---

Confidence: **96%**

---

# D-004 Domain validation is inconsistent at creation time

## Finding

The task domain has canonical concepts:

* Status
* Priority
* Type

Creation logic appears more permissive than update logic.

This allows arbitrary values to enter the domain.

---

## Why this matters

Once invalid values exist:

* filtering breaks
* reporting breaks
* maintenance becomes harder

Greenfield systems should reject invalid state immediately.

---

## Recommendation

Every public mutation path should validate against one shared domain model.

Avoid:

```
Normalize()

Default()

Best effort
```

Prefer:

```
Validate()

Reject

Explain why
```

---

Confidence: **91%**

---

# D-005 Tool availability policy is duplicated

## Finding

Multiple layers now participate in deciding tool availability.

Examples include:

* catalog
* session
* execution

This duplicates policy.

---

## Why this matters

Policy duplication almost always diverges.

Eventually one layer says:

```
allowed
```

while another says

```
not allowed
```

These bugs are difficult to diagnose because neither component is individually incorrect.

---

## Recommendation

One policy object.

Every caller asks it.

No exceptions.

---

Confidence: **95%**

---

# D-006 Domain invariants are spread across services

## Finding

Several domain concepts are enforced in application services instead of the domain itself.

Examples include:

* task lifecycle
* archive behavior
* completion timestamps
* visibility rules

The domain becomes "whatever the caller remembered to update."

---

## Recommendation

Move invariant enforcement into domain objects.

Example:

```
Task

Page

MemoryRecord

AgentSession
```

should own their own invariants.

Application services should orchestrate only.

---

Confidence: **89%**

---

# Consolidation Opportunities

## 1. Unified Tool Visibility Engine

Instead of

```
AvailableInChat

AvailableInAgent

session filters

execution filters
```

have

```
ToolVisibilityPolicy
```

One implementation.

One source of truth.

---

## 2. Unified Lifecycle Engine

Instead of:

```
Create

Update

Archive

Complete

Restore
```

each manipulating different fields,

have

```
TaskLifecycle.ApplyTransition()
```

---

## 3. Search Capability Versioning

Instead of

```
Cache(query)
```

use

```
Cache(
    query,
    indexGeneration,
    embeddingGeneration,
    providerGeneration)
```

This eliminates an entire class of stale-result bugs.

---

## 4. Replace Normalization with Validation

A recurring pattern throughout the repository is:

```
Normalize

Guess

Repair

Continue
```

This is convenient during early development, but it accumulates technical debt by allowing invalid state to enter the system.

For a greenfield project, prefer:

```
Validate

Reject

Repair explicitly
```

over silently synthesizing defaults or correcting malformed input.

---

# Architectural Theme

A consistent pattern across the repository is the presence of **implicit contracts**:

* duplicated policy decisions
* silent normalization
* inferred lifecycle transitions
* fallback behavior that hides invalid state

None of these are catastrophic individually, but together they increase maintenance cost and make future refactoring more brittle.

The strongest architectural direction remains to:

1. Centralize policy.
2. Centralize invariants.
3. Eliminate silent repair paths.
4. Replace implicit behavior with explicit validation.
5. Prefer a single authoritative implementation over duplicated logic.

These changes align well with the project's stated goal of avoiding legacy systems and technical debt while the codebase is still young.
