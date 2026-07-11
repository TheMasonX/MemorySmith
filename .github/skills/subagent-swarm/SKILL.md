---
name: subagent-swarm
description: 'Launch N subagents in parallel to research large datasets and synthesize results. Use when investigating multiple files, code paths, or data sources simultaneously; for large-scale codebase exploration; for parallel validation sweeps; or when the council skill needs parallel seat research.'
argument-hint: 'Number of subagents, partition strategy, and research objective'
user-invocable: true
disable-model-invocation: false
---

# Subagent Swarm

Launch `<N>` subagents in parallel within a single coordinated tool call to research, explore, or analyze large sets of data simultaneously, then synthesize individual findings into a unified result.

## When to Use

- **Large codebase exploration**: Multiple independent areas need investigation (e.g., "check all controllers for a pattern")
- **Data partition analysis**: A large dataset can be split into chunks and analyzed in parallel (e.g., "audit all 30+ memory records for stale facts")
- **Multi-perspective review**: Different viewpoints or expertise areas need independent analysis before synthesis (e.g., council seat reviews)
- **Parallel validation**: Multiple independent checks that can run simultaneously (e.g., "verify all endpoints handle error X")
- **Breadth-first investigation**: When you need to cover many files quickly before deep-diving into specific ones

Do NOT use for: sequential tasks with dependencies, single-file analysis, simple lookups, or tasks where one subagent's output is needed for the next.

## Inputs

- **Research objective**: What question(s) to answer. Must be clear, bounded, and partitionable.
- **Partition strategy**: How to split the work across subagents (by file, by directory, by concern, by perspective).
- **Subagent count (N)**: Number of parallel agents. Match to partition granularity.
- **Subagent prompt template**: What each agent is instructed to do. Must be self-contained (no cross-agent dependency).
- **Synthesis instructions**: How to combine individual findings into the final result.

## Procedure

### 1. Define the Swarm

1. **State the research objective** — what must be discovered or verified.
2. **Partition the work** — split into N independent chunks. Common strategies:
   - **By file/directory**: `src/controllers/` chunk 1, `src/services/` chunk 2, etc.
   - **By concern**: "UI layer" vs "data layer" vs "API layer"
   - **By perspective**: "security review" vs "performance review" vs "maintainability review" (council-like)
   - **By data subset**: Records 1–10, records 11–20, records 21–30
   - **By question**: Each subagent answers a different sub-question of the overall research question
3. **Choose N** — match to partition count. Typical: 2–5. N > 5 is rarely proportional to value gained and burns significant context.
4. **Verify independence** — confirm no subagent depends on another's output. If they do, it is not a swarm — it is a pipeline.

### 2. Write Subagent Prompts

Each subagent must receive a self-contained prompt that includes:

```
{research objective, with subset identified}
{instructions — what to explore, what to look for}
{evidence locations — files, directories, URLs}
{output format — structured so results can be mechanically merged}
{synthesis-ready constraints}
```

Key rules:
- **Self-contained**: Each subagent must complete its task without communicating with other agents.
- **Structured output format**: Define a format so results merge mechanically (e.g., "Return a markdown table with columns: File | Finding | Severity | Evidence"). Without a structured output contract, synthesis is ad-hoc and unreliable.
- **No cross-agent references**: Never reference other subagents or their expected outputs in an individual prompt.
- **Research only**: Subagents must not implement code changes.

### 3. Launch the Swarm

Launch all subagents simultaneously via the `runSubagent` tool. Issue one call per subagent.

Prompt template for each subagent:

```text
You are swarm agent {i} of {N}.
Your partition: {description of this agent's subset}.
Research objective: {overall objective}.
Your specific task: {exactly what to do}.
Explore: {specific files, directories, or data to investigate}.
Return findings in this format:
{structured output format — table, list, schema}
Do not implement any code changes — research only.
Be thorough but concise. Include file paths and line numbers where applicable.
```

### 4. Await Results

Wait for all subagents to complete. Collect their individual outputs.

### 5. Synthesize

Combine individual subagent outputs into a unified result:

1. **Merge**: Collect all structured findings into a single dataset or document.
2. **Deduplicate**: Remove overlapping or redundant findings across partitions.
3. **Resolve conflicts**: If subagents disagree on a finding, flag as a conflict and note the evidence on each side.
4. **Summarize**: Produce a unified report with:
   - Executive summary of key findings
   - Structured lists or tables of all findings
   - Confidence levels per finding
   - Cross-cutting patterns or themes
   - Open questions or gaps
   - Recommended next actions

### 6. Document

Record the swarm session in the task tracker, session memory, or project log:
- Research objective and partition strategy
- Number of subagents used
- Key findings and synthesis output
- What was learned about the partition approach (for future improvement)

## Decision Branches

### Branch A: Homogeneous Swarm
All subagents perform the same task on different partitions. Best for codebase sweeps, data audits, and exhaustive checks where uniformity of analysis is important.

### Branch B: Heterogeneous Swarm
Each subagent has a different task or perspective. Best for council-like reviews, multi-faceted analysis, or when different expertise areas are needed. Each subagent may explore different evidence sets.

### Branch C: Discovery Swarm
Subagents explore with loose guidance to surface unknown issues. Best for exploratory audits. Requires careful triage afterward since results may be broad and unprioritized.

## Completion Checks

A swarm session is complete only when all are true:
- Research objective is clearly stated at the start
- Partition strategy is explicit and justified
- Each subagent prompt is self-contained with structured output format
- All N subagents completed
- Results are synthesized into a unified output
- Conflicts or disagreements between subagents are documented
- Confidence levels or quality assessments are included
- Recommendations or next steps are provided

## Relation to Council Skill

The [`council`](../council/SKILL.md) skill should use this swarm internally when the user has given explicit permission for subagent usage. The council skill's step 4 (seat reviews) maps naturally to a **heterogeneous swarm (Branch B)**, where each seat runs as a parallel subagent with its own perspective.

When the council skill delegates to this swarm:
- Use N = number of council seats (3–6)
- Partition by seat perspective (Source-Grounded Archivist, Data Model Architect, etc.)
- Use a structured output format per seat that feeds directly into the council skill's step 6 (synthesis)
- The swarm's synthesis step produces the raw seat data; the council skill's Synthesizer seat then produces the final decision

## Anti-patterns

- **Sequential disguised as parallel**: If subagent B needs subagent A's output, do not use a swarm — use a pipeline.
- **Too many subagents**: N > 5 rarely adds proportional value. Prefer quality over quantity.
- **Vague prompts**: "Explore the codebase" without boundaries produces bloated, overlapping results. Partition explicitly.
- **Missing output format**: Without a structured output contract, synthesis is ad-hoc and unreliable.
- **No synthesis step**: Launching subagents without a plan to combine results wastes the parallel effort.
- **Code changes in swarm**: Subagents should research only. Code implementation after synthesis is a separate step.
- **Ignoring conflicts**: When subagents disagree, document both positions with evidence rather than flattening to consensus.

## References

- [`../council/SKILL.md`](../council/SKILL.md) — council skill that delegates to this swarm for parallel seat reviews
- `runSubagent` tool — the tool used to launch each swarm agent
