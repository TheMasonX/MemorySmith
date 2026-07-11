# MemorySmith code audit — latest commit `db04b23a25e3930b424f3ef9eb0a0af3efcb9c27`

## Scope note

`db04b23a...` is a task-state commit only; the code changes live in the parent implementation commit `f448503...`, and `db04` only marks TSK-0288 through TSK-0292 as Done. The task workbench itself says to verify task status, owner, comments, and acceptance criteria against `Data/Tasks` before planning implementation, so I checked the task records and the current workbench view before auditing the code paths. fileciteturn17file0 fileciteturn58file0

## Executive summary

The five Phase 1 audit-synthesis fixes landed, but the repo still carries a few high-risk implicit contracts that matter in a greenfield system: configuration discovery still falls back through multiple filesystem locations, task files are parsed with a permissive fallback model, and the task domain accepts more free-form state than the UI suggests. The most important remaining problems are not “missing features”; they are places where the system can silently accept bad input, preserve stale state, or leak legacy artifacts after deletion. fileciteturn10file0 fileciteturn54file0 fileciteturn61file0

## Highest-priority findings

### 1) Settings overrides still rely on brittle fallback discovery and silent failure paths
**Severity:** High  
**Confidence:** 92%

`ResolveSettingsOverridePath` searches multiple locations (`AppContext.BaseDirectory`, `MemorySmith.App/appsettings.LocalOverrides.json`, and `artifacts/MemorySmith.App/appsettings.LocalOverrides.json`) and returns the first match. `MemorySmithLocalDevelopmentPostConfigure.LoadOverrideKeys()` then swallows JSON, I/O, and permission errors and behaves as though no override file exists. That means a malformed or missing local override can quietly re-enable defaults instead of surfacing a configuration problem. In a greenfield project, this is exactly the kind of “legacy fallback” that later becomes accidental behavior. fileciteturn10file0 fileciteturn54file0

**Why it matters:** security-sensitive defaults and admin settings can drift without an obvious failure signal.

**Implementation guidance:** collapse config sourcing to one authoritative settings file for each environment, and make malformed override files fail loudly in non-test environments. Keep the discovery path only if there is a clearly documented migration window.

### 2) Task status is under-validated, and completion state can become stale
**Severity:** High  
**Confidence:** 88%

The task UI exposes a fixed status dropdown and a fixed severity dropdown, but the backend does not enforce those finite values. `CreateAsync`, `UpdateAsync`, and `SetStatusAsync` all accept arbitrary strings after minimal trimming/defaulting, and `SetStatusAsync` keeps `CompletedAtUtc` when a task moves away from `Done`. That creates two correctness problems: invalid status/priority values can persist forever, and reopened tasks can still look “completed” in downstream stats or reports. fileciteturn64file0 fileciteturn63file0

**Why it matters:** the UI implies a constrained state machine, but the storage layer behaves like free text.

**Implementation guidance:** validate `Status`, `Priority`, and `Type` against explicit allowlists in the service layer, and clear `CompletedAtUtc` whenever a task transitions out of `Done` unless you intentionally want “ever completed” semantics.

### 3) Hard delete leaves task attachments behind
**Severity:** High  
**Confidence:** 90%

Task attachment files are written into a separate artifact tree under `/artifacts/task-attachments/...`, but `DeleteAsync(true)` only deletes the task JSON file. It does not remove attachment files or any other file-backed side artifacts. That means a “hard delete” is not actually hard, and storage / public URI cleanup will drift over time. fileciteturn33file0 fileciteturn49file0

**Why it matters:** orphaned files accumulate, and delete semantics become misleading.

**Implementation guidance:** make hard delete a transaction-like operation across the JSON file, attachment files, and any related activity/history records. If that is too large for now, rename the operation to “delete task record” until the cleanup path exists.

### 4) Rate limiting and loopback trust are deployment-sensitive and currently assume direct client IPs
**Severity:** Medium  
**Confidence:** 74%

The login limiter is now per-IP, which is better than the previous global bucket, but it still keys directly on `HttpContext.Connection.RemoteIpAddress`. The pipeline I reviewed does not include forwarded-header processing, so if the app ever sits behind a reverse proxy or terminator, the limiter and the loopback checks will operate on the proxy hop instead of the real client. `IsLoopback()` also treats a null remote IP as loopback, which is fine for some local/test cases but is an implicit trust decision. fileciteturn21file0 fileciteturn53file0 fileciteturn60file0

**Why it matters:** a deployment change can silently collapse or bypass the intended security boundary.

**Implementation guidance:** decide whether the app is strictly direct-to-Kestrel or proxy-aware. If proxy-aware, add forwarded-header handling and rate-limit on the validated client identity, not the raw transport hop.

### 5) Corrupt task files are intentionally downgraded to warnings, which can hide real storage problems
**Severity:** Medium  
**Confidence:** 81%

`LoadAll()` catches any exception while parsing task files, logs a warning, and synthesizes a fallback “malformed task” record. That preserves visibility for broken JSON, but it also means I/O failures, partial writes, and other unexpected file problems are absorbed into a fake task object instead of failing the surface that depends on the data. This is resilient, but it is also a silent-fallback pattern that can mask underlying storage health issues. fileciteturn49file0 fileciteturn61file0

**Why it matters:** users see “a task” instead of a storage failure, and the root cause can linger.

**Implementation guidance:** keep the fallback only for parse corruption if that UX is essential; separate parse errors from I/O/access errors and surface the latter as operational failures.

### 6) Blanket antiforgery opt-out is broader than necessary
**Severity:** Low to Medium  
**Confidence:** 67%

The global antiforgery filter is good in principle, but `AuthController` is opted out wholesale. That includes `Logout`, which is a POST that now bypasses antiforgery entirely. If logout CSRF is acceptable for this app, this is fine; if not, the exemption is wider than needed. fileciteturn72file0

**Why it matters:** it weakens the blanket CSRF protection model in the one controller that handles browser form posts.

**Implementation guidance:** keep the global filter, but opt out only the endpoints that truly need it.

## Refactoring / consolidation opportunities

1. Centralize task state validation in the service layer, not the UI layer. The UI already behaves like a finite state machine; make the storage contract match it. fileciteturn64file0 fileciteturn63file0

2. Separate “authoritative config” from “convenience discovery.” `ResolveSettingsOverridePath()` and `LoadOverrideKeys()` should not both be trying to be helpful if the long-term goal is to remove legacy fallback behavior. fileciteturn10file0 fileciteturn54file0

3. Make delete semantics explicit for tasks, attachments, and task activity history. Right now the contract is split across multiple storage locations. fileciteturn33file0 fileciteturn49file0

4. Decide whether task storage roots are derived from `DataPath` and `EventLogPath` as hidden anchors or should become first-class configurable paths. Right now that coupling is implicit. fileciteturn54file0 fileciteturn61file0

## Assumptions and open questions

- I assumed the intended deployment model may include direct local hosting; if not, the IP-based limiter and loopback checks need proxy-aware handling.  
- I assumed task statuses/severities are meant to be bounded enums because the UI presents them as dropdowns.  
- I did not find evidence in the reviewed files of a second authoritative settings store, so I treated the override file as the single mutable local config surface.  
- The workbench already tracks a related backlog item about `Vars.json` being a loose file, which is adjacent to the config-fallback issue and suggests the team is already aware of loose-file drift. fileciteturn58file0

## Bottom line

The Phase 1 security hardening is moving in the right direction, but the codebase still tolerates too many “helpful” fallback behaviors for a project that wants to stay greenfield-clean. The best next move is to tighten contracts: one config source, explicit task enums, explicit delete semantics, and deployment-aware IP handling. That will remove a lot of future audit noise and make the remaining behavior easier to reason about.
