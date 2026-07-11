---
name: codebase-audit
description: 'Sweep the MemorySmith codebase for bugs, inconsistencies, gaps, weak guards, error handling gaps, observability/logging issues, overcoupling, and architectural fixes. Produces a unified audit report with swarm-partitioned parallel exploration, recommended quick-fix follow-ups, MCP task records for remaining findings, and an optional council review phase. Use when users request a systematic audit, quality sweep, bug hunt, pre-sprint review, or codebase health check.'
argument-hint: 'Optional focus areas or layers to target (default: all layers)'
user-invocable: true
---

# Codebase Audit — MemorySmith Edition

Runs a **parallel subagent swarm** to exhaustively audit the MemorySmith codebase, synthesizes findings into a structured P0–P3 report, recommends quick fixes for minor issues (does not auto-apply), creates MCP task records for remaining findings, and proposes a council review as a logical follow-up phase.

## Outcome

- `Data/Pages/Audits/codebase-audit-{YYYYMMDD}.md` — Unified report with items table, P0–P3 findings, architecture notes, open questions, and next steps
- Recommended quick-fix batch — presented to the user for approval; applied in a few focused tool rounds if approved
- New MCP task records for findings without existing coverage
- Validated task records via `Test-TaskRecords.ps1`
- Council review proposal as a recommended follow-up

## Use When

The user asks for:
- "Sweep the codebase for bugs / issues / gaps / smells"
- "Do a pre-sprint quality review"
- "Find weak guards or error handling problems"
- "Audit the architecture for overcoupling"
- "Create an audit report and peer-review it"
- "Review observability and logging"
- "Run a codebase health check before release"

Do NOT use for: single-file reviews, ad hoc Q&A about code, performance profiling (use `web-perf` or Chrome DevTools), dependency analysis, or the MemorySmith.Agent repository (use that project's own codebase-audit skill instead).

## Procedure

### Phase 0: Preparation

1. **Activate MCP tool groups.** Before assuming tools are missing, call `activate_memorysmith_task_management` to register `memorysmith_task_create`/`get`/`list`/`set_status`/`update`/`add_comment`. Similarly call any other `activate_*` tools for expected tool categories.
2. **Check for existing audit reports** in `Data/Pages/Audits/` — note the latest to avoid duplication.
3. **Check repo memory** (`/memories/repo/`) for known issues, recent fixes, and project conventions.
4. **Check the task list** via `mcp_memorysmithw2_memorysmith_task_list` — get the highest existing `tsk-XXXX` key for new task creation.
5. **Read key context files**: `README.md`, `Data/Memories/Core/` records for architecture and conventions, any `copilot-instructions.md`, and the app configuration flow.

### Phase 1: Design the Swarm Partition

1. **Choose N** (typically 5) subagents. Match to number of independent codebase layers.
2. **Standard MemorySmith partition** (for a full audit):

| Agent | Layer | What to Cover |
|-------|-------|--------------|
| 1 | **Core** | `MemorySmith.Core/` — Models, Indexing, StateMachine, Docs, `.csproj` |
| 2 | **API & Services** | `MemorySmith.App/Controllers/`, `Services/`, `Hosting/` |
| 3 | **Storage & Infrastructure** | `MemorySmith.Storage/`, `MemorySmith.Bridge/`, `Schemas/`, `Scripts/`, `Data/vars.json`, `LICENSE.txt`, `MemorySmith.slnx` |
| 4 | **Tests & Quality** | `MemorySmith.Tests/`, `MemorySmith.Benchmarks/`, `e2e/` |
| 5 | **UI & App Shell** | `MemorySmith.App/Components/`, `Program.cs`, `appsettings.json`, `wwwroot/` |

3. **Verify independence** — each partition must be independently auditable. If a feature spans layers (e.g., a filter that flows Controller → Service → Storage → Test), each layer covers its own slice; the synthesis phase connects the dots.

### Phase 2: Launch the Swarm

Create **N self-contained subagent prompts** following the swarm template:

```text
You are swarm agent {i} of {N}.
Your partition: {description — files and directories}.
Research objective: Comprehensive codebase audit of MemorySmith at {repo_path}. Find bugs, inconsistencies, gaps, weak guards, error handling gaps, observability/logging issues, overcoupling, and architectural fixes.
Your specific task: Audit all files in your partition thoroughly.

For each file, check for:
| Category | What to Check |
|----------|--------------|
| **Bugs** | Logic errors, race conditions, null refs, incorrect state transitions |
| **Inconsistencies** | Divergent patterns, mismatched naming, config vs code contract violations |
| **Gaps** | Missing handlers in switch statements, unhandled edge cases |
| **Weak guards** | Missing null checks, CancellationToken.None, fire-and-forget with no error reporting |
| **Error handling** | Silent catch blocks, swallowed exceptions, fire-and-forget that drops failures |
| **Observability** | Logging at wrong level, missing correlation IDs, silent failure paths |
| **Overcoupling** | One class owning too many concerns, dead code that looks alive |
| **Architecture** | Interfaces defined but never wired, stub implementations that always return defaults |

Return findings in this structured format — a markdown table with columns:

| ID | File | Line(s) | Category | Description | Severity (P0-P3) | Impact | Confidence % | Recommendation |
|----|------|---------|----------|-------------|-------------------|--------|--------------|----------------|

Then after the table, add brief notes under these headings:
- **Architecture Notes**: Structural observations across the partition
- **Open Questions**: Things that need further investigation
- **Out of Scope**: Issues found that fall outside the audit but should be tracked

Do not implement any code changes — research only. Be thorough but concise. Include file paths and line numbers where applicable. Read actual file contents to verify findings — do not assume.
```

Launch all subagents simultaneously via `runSubagent`.

### Phase 3: Synthesize Results

Collect all N subagent outputs and produce the unified report:

1. **Merge** all structured findings into a single dataset.
2. **Deduplicate** overlapping findings across partitions (e.g., a service-file finding and its test might overlap).
3. **Resolve conflicts** — if subagents disagree, document both positions with evidence.
4. **Assign severity** — P0 = Critical (crash, data loss, security bypass); P1 = High (silent failure, broken workflow); P2 = Medium (observability, testability, minor bugs); P3 = Low (cosmetic, convention, cleanup).
5. **Identify cross-cutting themes** that span multiple partitions.

Report structure (write to `Data/Pages/Audits/codebase-audit-{YYYYMMDD}.md`):

```markdown
# Codebase Audit — {date}

**Task Description:** {what was audited}
**Author:** Agent Smith (swarm synthesis)
**Timestamp:** {date}

## Executive Summary
- Brief paragraph of top findings
- Severity distribution table

## P0 — Critical
### P0-001: {Title}
**File:** `{path}` (line ~{N})
**The bug:** {description}
**Impact:** {consequences}
**Recommendation:** {fix}
**Confidence:** {X}%

## P1 — High
... (same format, consolidated)

## P2 — Medium
Consolidated table by theme where appropriate.

## P3 — Low / Observability / Cleanup
Consolidated by theme (Doc typos, Convention violations, Minor refactors, Test quality, UI polish).

## Architecture Notes
Cross-cutting structural observations.

## Supplemental Data
- Methodology (partition strategy, agent count)
- Category distribution

## Out Of Scope
Items identified but not audited.

## Assumptions
Key assumptions made during the audit.

## Open Questions
Questions that need follow-up.

## Next Steps
Prioritized action items.
```

### Phase 4: Recommend Quick Fixes

For **low-risk, P3/cosmetic fixes** identified in the audit, collect them into a **recommended quick-fix batch** and present to the user:

| # | Fix | File | Rationale |
|---|-----|------|-----------|
| 1 | Rename `SomeTypo.md` → `SomeFile.md` | `path/to/file.md` | Typo in filename |
| 2 | Fix heading `H#` → `#` | `path/to/doc.md` | Broken markdown |
| ... | ... | ... | ... |

**Criteria for quick-fix recommendations:**
- No behavior change, no API contract change, no risk of regression.
- Cosmetic only: filename typos, heading formatting, whitespace, convention fixes (`sealed class` → `sealed record`), non-existent folder refs, placeholder text.
- If uncertain about impact, add to the task list instead — never auto-apply.

**Do NOT auto-apply quick fixes.** Present them as a batch the user can approve. If approved, apply them in a focused tool round (multi-replace + build verify), then update the report to mark them as `(DONE)`.

### Phase 5: Create MCP Task Records

For each P0/P1/P2 finding **without existing task coverage**:

1. Check against existing tasks in `Data/Tasks/` — several overlap areas may already be tracked (e.g., Chat decomposition → tsk-0189, Admin decomposition → tsk-0190, SqliteStorage split → tsk-0157, E2E nav-freeze → tsk-0067, historical doc cleanup → tsk-0152).
2. Use the `memorysmith_task_create` MCP tool to create task records. The tool is registered in the Agent Smith toolset via the `memorysmithwiki/*` pattern. Use the `slug` parameter to set a desired ID suffix:
   - Required fields: `title`, `description`, `type`, `status`, `priority`
   - Use `assigneeMode: Custom`, `assigneeCustomText: "Copilot"`
   - Pass labels as comma-separated string
3. Set description to include the finding summary, file reference, and recommendation from the audit.
4. Do NOT create tasks for P3 findings — they are too numerous and should stay as cleanup notes in the report.

### Phase 6: Validate

1. Run `pwsh ./Scripts/Test-TaskRecords.ps1` to validate all task records.
2. If any quick fixes were applied (approved by user), run `dotnet build MemorySmith.slnx --no-restore` to verify they compile.
3. If any task or fix fails validation, correct before proceeding.

### Phase 7: Council Review (Recommended Follow-Up)

After the audit report and tasks are complete, **propose a council review** as a next step. A council review adds:

- **4–6 independent reviewer seats**, each with a distinct perspective (e.g., Source-Grounded Archivist, Data Model Architect, Retrieval Specialist, Skeptical Reviewer)
- **Independent verification** of each P0/P1 finding against source code
- **Severity recalibration** based on multi-seat consensus
- **Task quality review** — do the created tasks capture the right scope?
- **Discovery of missed findings** that the swarm may have overlooked

To invoke the council review, reference the [`../council/SKILL.md`](../council/SKILL.md) skill. The council skill can delegate its parallel seat reviews to the [`../subagent-swarm/SKILL.md`](../subagent-swarm/SKILL.md) skill when the user has authorized subagent usage.

**Proposal template to the user:**

> "The audit found {N} findings (P0: {X}, P1: {Y}, P2: {Z}). I recommend a **council review** next to verify the critical findings independently, calibrate severity, and catch anything the swarm missed. This would use {M} independent seats with distinct perspectives. Shall I proceed?"

### Phase 8: Document

Record the audit session:
- **Session memory** (`/memories/session/`): objective, partition strategy, key findings, any fixes applied, tasks created, council review recommendation
- **Tracker notes**: update or create a tracker in `logs/` with audit summary

## Decision Branches

### Branch A: Full Sweep (default)
5-agent homogeneous swarm covering all layers. Use for pre-sprint, pre-release, or periodic quality reviews.

### Branch B: Targeted Layer
If the user specifies a focus area (e.g., "audit just the storage layer" or "review only security-related code"), reduce N to match and only partition the relevant directories. Use 2–3 agents for depth.

### Branch C: Validation-First
If an audit already exists and the user wants to verify fixes, skip the swarm. Go directly to Phase 4 (check existing fixes) and Phase 6 (validate that tasks marked as fixed actually resolved).

## Completion Checks

A codebase audit session is complete only when all are true:
- Swarm launched with N ≥ 2 independent subagents
- All N subagents completed with structured findings
- Findings synthesized into a unified report with P0–P3 severity
- Cross-cutting themes identified and documented
- Quick fixes recommended to user (applied only with explicit approval)
- New task records created (if any) for findings without coverage
- Task records pass `Test-TaskRecords.ps1` validation
- Council review proposed as a logical follow-up
- Session recorded in `/memories/session/`

## Anti-patterns

- **Skipping the synthesis phase**: Aggregating raw subagent output without deduplication or severity assignment produces an unusable report.
- **Over-partitioning**: N > 5 rarely adds proportional value. 5 is the sweet spot for a full MemorySmith audit.
- **Auto-applying quick fixes**: Never apply quick fixes without user approval. Present them as a batch for explicit sign-off. "Quick fix" means the user can say yes and it's done in 1–2 tool rounds — not that the agent decides unilaterally.
- **Applying risky quick fixes**: Line-for-line cosmetic changes only. Never fix logic bugs during the quick-fix phase — those go to tasks.
- **Re-creating existing tasks**: Always check `Data/Tasks/` for existing coverage before creating new ones. Especially check tsk-0157 (SqliteStorage), tsk-0189/0190 (Chat/Admin decomposition), tsk-0067 (E2E freeze), tsk-0152 (doc curation).
- **Skipping the council review proposal**: After the audit, always recommend a council review. The swarm finds surface issues; the council catches depth issues, calibrates severity, and catches misses.
- **P3 task bloat**: Creating individual tasks for 72 P3 items is counterproductive. Consolidate P3 findings as cleanup notes in the report.

## References

- [`../subagent-swarm/SKILL.md`](../subagent-swarm/SKILL.md) — swarm skill used for parallel exploration (delegated internally)
- [`../council/SKILL.md`](../council/SKILL.md) — council review skill recommended as follow-up
- `mcp_memorysmithw2_memorysmith_task_create` — MCP tool for creating task records
- `mcp_memorysmithw2_memorysmith_task_list` — MCP tool for querying existing tasks
- `mcp_memorysmithw2_memorysmith_task_add_comment` — MCP tool for adding evidence comments
- `Data/Tasks/` — task record store for created tasks
- `Scripts/Test-TaskRecords.ps1` — task JSON validation script
- `Data/Pages/Audits/` — report output directory
- `AGENTS.md` — task schema contract for task JSON records
- `.github/copilot-instructions.md` — project context and conventions
