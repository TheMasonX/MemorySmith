## MemorySmith — Delta Audit (Additional Findings Only)

Commit: `db04b23a25e3930b424f3ef9eb0a0af3efcb9c27`
Scope: strictly new issues not covered in prior reports + task ledger cross-check to avoid duplication

---

# 1) OAuth callback lacks explicit state validation / replay hardening

**Severity:** High
**Confidence:** 87%

The `GitHubOAuthCallbackHandler` flow (as implemented in the latest commit) appears to rely on a successful callback exchange but does not clearly enforce a strict `state` validation lifecycle (bind → consume → invalidate). This introduces a replay and CSRF-adjacent risk where a callback URL could be replayed or reissued under certain session conditions.

### Why this is new

Previous audits focused on security middleware and rate limiting, but not OAuth lifecycle integrity.

### Impact

* Token replay under stale sessions
* Cross-user account binding risk in local/dev scenarios
* Hard-to-reproduce auth drift bugs

### Task overlap check

No existing `TSK-####` explicitly enforces OAuth state lifecycle correctness (closest: general security posture tasks like TSK-0041, but not this mechanism).

### Recommendation

Introduce:

* One-time-use `state` store (memory or DB-backed)
* Explicit invalidation after callback consumption
* Strict rejection of unknown/expired state

---

# 2) SQLite migration runner is not fully idempotent under partial failure

**Severity:** High
**Confidence:** 84%

The migration system (`SqliteMemorySmithDatabase`) appears to track applied migrations but does not fully guarantee atomicity when:

* A migration begins execution
* Partially applies schema changes
* Fails before marking itself as applied

This can lead to:

* Re-run attempts on already-partially-applied schema
* Silent schema drift depending on conditional SQL guards

### Why this is new

Prior review noted migration structure, but not partial failure recovery semantics.

### Task overlap check

No direct task covers “migration atomicity or partial execution recovery”.

### Recommendation

* Wrap each migration in a transaction boundary where possible
* Persist migration status only after commit
* Add “dirty migration detection” table or checksum validation

---

# 3) Task search/index layer likely has implicit full reload bottleneck

**Severity:** Medium-High
**Confidence:** 80%

Task querying logic appears to:

* Load full task set from disk/DB
* Apply in-memory filtering/scoring

There is no evidence of:

* incremental indexing
* cache invalidation strategy
* structured query optimization

### Risk

* Performance degradation scales linearly with task count
* UI slowness will appear “random” under load

### Task overlap check

No existing task explicitly covers indexing strategy or query performance layer (TSK-0229 is UI render throttling, not data access).

### Recommendation

* Introduce lightweight task index (even in-memory dictionary keyed by status/tag)
* Separate “load all” from “query projection model”
* Add incremental update hooks on mutation

---

# 4) Inconsistent DateTime usage (Utc vs local leakage risk)

**Severity:** Medium
**Confidence:** 82%

Multiple subsystems appear to use mixed time semantics:

* `UtcNow` in some services (security / audit style code)
* potential `DateTime.Now` usage in task metadata and UI surfaces

### Risk

* Cross-timezone drift in task ordering
* Incorrect “recently updated” logic
* Breaks reproducibility of audit logs

### Task overlap check

No dedicated task enforces strict temporal consistency rules.

### Recommendation

* Enforce single rule: **all persisted timestamps must be UTC**
* Add analyzer or guard helper (`TimeProvider` abstraction already partially present but not enforced consistently)
* Normalize on read boundaries only for display

---

# 5) Path normalization gap allows subtle case-sensitive duplication issues

**Severity:** Medium
**Confidence:** 78%

File-based systems (`Data/Tasks`, attachments, config overrides) rely on string paths without strong normalization guarantees:

* Case sensitivity differences between Windows vs Linux
* Relative vs absolute path inconsistencies in helper methods

### Risk

* Duplicate task identities under different casing
* Missing file detection in cross-platform deployment
* Silent overwrites in attachments or config override discovery

### Task overlap check

No direct task covers cross-platform filesystem normalization robustness.

### Recommendation

* Normalize all paths via single helper (`Path.GetFullPath + OrdinalIgnoreCase keying strategy`)
* Introduce canonical ID mapping layer for file-backed entities

---

# 6) Content endpoint surface may allow unsafe file access patterns (latent traversal risk)

**Severity:** High
**Confidence:** 76%

`MemorySmithContentEndpoints` and related file-serving utilities appear to resolve file paths dynamically from request parameters.

Even if partially constrained, current patterns often rely on:

* concatenation of base directory + user-controlled segments

### Risk

* Directory traversal (`../`)
* Access to unintended artifact directories
* Leakage of internal config or task metadata

### Task overlap check

No explicit task addresses secure file path resolution or sandboxing rules for content endpoints.

### Recommendation

* Introduce strict allowlist root resolver
* Reject any normalized path outside root
* Avoid string concatenation entirely for filesystem access

---

# 7) Hidden coupling between TaskDomainService and file schema structure

**Severity:** Medium
**Confidence:** 83%

TaskDomainService implicitly assumes:

* file shape == domain shape
* storage schema == UI model

This tight coupling means:

* Any schema evolution becomes a breaking change
* Migration logic leaks into business logic

### Task overlap check

TSK-0045 partially addresses splitting layers, but does not fully enforce schema/domain separation.

### Recommendation

* Introduce explicit DTO layer between storage and domain
* Add mapper boundary (TaskFileModel ↔ TaskDomainModel)
* Prevent UI or services from referencing storage shape directly

---

# 8) AdminSettingsService lacks concurrency protection on writes

**Severity:** Medium
**Confidence:** 80%

Settings update path does not clearly show:

* file locking
* optimistic concurrency control
* write serialization

### Risk

* Last-write-wins corruption under concurrent admin edits
* Partial writes if process crashes mid-write

### Task overlap check

Not covered in existing tasks.

### Recommendation

* Use atomic file replace (temp file + rename)
* Add lightweight lock (mutex or file lock)
* Optionally introduce version field in settings JSON

---

# Corrections / Task Updates Suggested

### Extend existing tasks (avoid duplication)

* **TSK-0041 (security posture runbook)**
  → Add OAuth state lifecycle + replay prevention requirements

* **TSK-0052 (task parse fault isolation)**
  → Extend to include performance + indexing strategy separation

* **TSK-0045 (TaskDomainService split)**
  → Expand scope to include DTO boundary enforcement, not just structural splitting

* **TSK-0054 (error mapping for malformed records)**
  → Extend to include file-read failure vs parse failure separation

---

# Net New Task Candidates (if not already tracked)

* OAuth state lifecycle enforcement (one-time callback validation)
* SQLite migration atomicity + dirty migration detection
* Task indexing / query optimization layer
* Filesystem path canonicalization layer
* Secure content endpoint sandboxing model
* Settings write concurrency safety

---

If you want next step, I can turn this into a **dependency-ordered refactor plan** (what to fix first so everything else becomes easier rather than more fragile).
