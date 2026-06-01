# Agent Workflow Guide

This guide captures operational principles for AI agents, automated tools, and contributors
working within MemorySmith. The principles below were proposed for review based on observations
from a peer knowledge-base system (LLMWikiServer) during a cross-system parity review in June
2026. They have been adapted and are maintained by the MemorySmith project team.

## 1. Validator Discipline

MemorySmith is strict about ID / Path alignment. Before proposing or committing any memory
record:

- Verify the `Id` field (kebab-case) **exactly** matches the filename, including the date
  suffix (e.g., `my-feature-20260601` → `my-feature-20260601.json`).
- Confirm the `Status` integer matches the correct subfolder:
  - `0` → `Unconsolidated/`
  - `1` → `Working/`
  - `2` → `Core/`
  - `3` → `Deprecated/`
- Never write a record whose `Id` does not derive from the file path. Drift between the two
  breaks search indexing, context-pack resolution, and the `memorysmith_source_bundle` tool.

## 2. Use Context Packs Before Search

When investigating a codebase topic, do **not** reach for `memorysmith_search` alone. Use
`memorysmith_context_pack` first to follow the `References` and `Conflicts` keys.

This technique lets you trace the "ripple effect" of any proposed change 1–2 hops out across
documentation **before** you write a single line of code or a single memory record. A search
shows you what exists; a context pack shows you what will break.

```
memorysmith_context_pack(ids=["my-record-id"])
→ returns records/warnings + per-record diagnostics
→ check every References entry for transitive impact
```

## 3. Ground Tasks in Comments

When working on a Task, treat the `comments` array as a terminal log — paste specific file
paths, test output, and command results immediately as you encounter them.

High-quality tasks contain enough evidence for a **human to approve a status transition
without opening the IDE**. Vague "working on it" comments carry zero value. Concrete entries
like:

> `MemorySmith.App/Services/MemoryDiagnosticsService.cs:218 — NullReferenceException on
> empty References list; fixed by null-coalescing in GetDiagnosticsAsync`

…give reviewers everything they need to accept, reject, or escalate.

## 4. Respect the Status Tier Ladder

Memory `Status` is a trust ladder, not a filing category. It controls whether Athena treats
content as exploratory noise or hardened fact.

| Status | Subfolder | Meaning | Agent behavior |
|---|---|---|---|
| 0 – Unconsolidated | `Unconsolidated/` | Raw notes, scraps, first-pass capture | May create freely |
| 1 – Working | `Working/` | Active research, in-progress features | Default for all agent writes |
| 2 – Core | `Core/` | Hardened, human-reviewed architecture facts | **NEVER promote without human review** |
| 3 – Deprecated | `Deprecated/` | Stale info kept for audit history | Read-only; do not edit |

> **Agent Rule:** Never upgrade a record's status to `Core` (`2`) without explicit human
> review or confidence ≥ 0.95 **and** explicit user approval in the current conversation.
> Incorrect Core records corrupt every future agent run that loads them as ground truth.

## 5. Use `%VarNameX%` in Source Links

Never hardcode an absolute file-system path in a `SourceLinks[].Uri` value. Use the
variable tokens defined in `Data/vars.json` to keep records portable across developer
machines.

```json
// ✅ Correct — portable across machines
{ "Label": "Service file", "Uri": "%MemorySmithRepo%MemorySmith.App/Services/MyService.cs" }

// ❌ Wrong — hardcodes developer-local path
{ "Label": "Service file", "Uri": "C:\\Users\\norrt\\source\\repos\\MemorySmith\\MemorySmith.App\\Services\\MyService.cs" }
```

Current variables (`Data/vars.json`):

| Token | Resolves to |
|---|---|
| `%MemorySmithRepo%` | Root of the MemorySmith repository on the local machine |

New variables must be added via `/variables` before use; do not invent undeclared tokens.

---

## Peer Review Observations (June 2026)

The following capabilities were noted by a peer AI knowledge-base system (LLMWikiServer)
during a cross-system parity review as design patterns they considered adopting. They are
not formal audits — treat this as one data point among several:

| Capability | MemorySmith surface |
|---|---|
| **Unified Agent Workspace** | Tasks, Pages, and Memories form a single cross-linked graph |
| **Context Pack pattern** | `memorysmith_context_pack` exposes `References` / `Conflicts` for ripple-effect tracing |
| **Tag Governance** | `/tags` workspace with allow/block lists, aliases, and hygiene diagnostics |
| **Source Link Variables** | `%VarNameX%` tokens make JSON records portable across developer machines |
| **Audit JSONL Mirroring** | SQLite audit log mirrored to `Data/Events/audit-yyyy-MMM.jsonl` for repo-backed evidence durability |
| **Human-Readable Task Seed** | `Data/Pages/workbench/tasks.md` provides the human-oriented backlog while the Tasks system manages stateful JSON |
| **Validator-Driven Git Hooks** | `.githooks/` runs `Test-PageLinks.ps1` and `Test-MemoryRecords.ps1` before commits |
| **Source Bundle Pattern** | `memorysmith_source_bundle` fetches all source-link content slices for a set of memory IDs in one call |

These strengths were identified as candidates for adoption in peer systems — they represent the
current "gold standard" patterns in this repository.

---

## Three Open Gaps (Feature Backlog, June 2026)

The same parity review identified three workflow gaps not yet addressed in MemorySmith:

1. **Diagnostics API** (TSK-0267) — `/api/diagnostics` should report the *effective* runtime
   config (embedding model loaded, provider connection status, indexing state) rather than just
   a binary `/health` check. Note: any such endpoint must be gated at appropriate privilege
   level — see TSK-0267 acceptance criteria for security scope.

2. **Reverse Reference Generator** (TSK-0268) — Users need a "What points to this?" view for
   memories and pages to close the navigation gap when following cross-links backwards. This
   is also a prerequisite for the F5 Knowledge Graph visualization feature.

3. **Assignee-Aware Task Filtering** (TSK-0269) — The `/tasks` board currently lacks filtering
   by assignee, making large backlogs harder to triage by owner.

---

## Related Pages

- [Configuration Reference](configuration-reference.md)
- [Memory Governance Guide](memory-governance-guide.md)
- [Search and Chat](search-and-chat.md)
- [Architecture](architecture.md)
