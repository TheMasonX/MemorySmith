---
name: wiki-hygiene-audit
description: "Run a repeatable hygiene audit over Data/Memories and Data/Pages for drift, stale facts, broken source links, and cleanup candidates."
argument-hint: "Scope (core/working/pages) and cleanup strictness"
user-invocable: true
disable-model-invocation: false
---

# Wiki Hygiene Audit

Inherits from `task-core-loop`.

## Added Context
Use this for dogfooding and knowledge quality maintenance.

## Audit Targets
- Stale or contradictory memory records.
- Source links that no longer resolve or point to moved files.
- Duplicate pages or overlapping records that should be consolidated.
- Memories incorrectly storing future plans that belong in pages.

## Additional Procedure
1. Start from `Data/Memories/Core` then expand to `Working` and `Unconsolidated`.
2. Cross-check linked `Data/Pages` content for drift.
3. Identify candidates for merge, deprecation, or rewrite.
4. Apply minimal fixes and capture evidence for each change.
5. Create follow-up task records for unresolved hygiene debt.

## Output
- Hygiene findings by severity.
- Proposed consolidations/deprecations.
- Residual backlog items linked to `/tasks`.