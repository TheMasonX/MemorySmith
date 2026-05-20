---
name: llm-council-review
description: 'Run a MemorySmith LLM council review for high-impact decisions. Use when evaluating schema changes, retrieval/search behavior, chat or agent write governance, wiki conventions, or long-term architecture tradeoffs with explicit dissent, risks, confidence, and acceptance criteria.'
argument-hint: 'Decision topic and scope to review'
user-invocable: true
disable-model-invocation: false
---

# LLM Council Review for MemorySmith

Use this skill to run a structured, evidence-first, multi-perspective decision review.

## Outcome

Produce a council report that includes:
- A one-sentence decision statement
- Seat-by-seat recommendations with confidence percentages
- Explicit disagreement and dissent
- Risks, assumptions, open questions
- Acceptance criteria and validation gates before implementation

## Use When

Use this workflow when decisions affect:
- Memory record shape or schema fields
- Retrieval quality, ranking behavior, staleness handling, vector/semantic behavior
- Chat or agent write behavior and evidence traceability
- User-facing wiki conventions that impact both people and agents
- Long-lived architecture or migration cost

Do not use this workflow for quick lookups, one-off formatting, or exact ID fetches.

## Inputs

Collect these inputs before running the workflow:
- Decision topic and one-sentence decision question
- Scope of impact: pages only, schema, retrieval/search, chat/agent writes, or mixed
- Primary evidence pages and memory records
- Any known stale docs, assumptions, or unresolved constraints

## Procedure

1. Classify the decision.
If this is low impact (cleanup, formatting, direct retrieval), skip council and do a normal edit or lookup.
If this is high impact, continue.

2. Build a bounded evidence pack.
Prefer MemorySmith tools and local project evidence first.
Minimum pack should include:
- Relevant pages in Data/Pages
- Relevant core memories in Data/Memories/Core
- Context pack or linked references/conflicts/backlinks when records are known
- Source-linked code evidence when claims depend on implementation
- Tests and benchmarks when behavior is affected (gold standard)
- If tests or benchmarks are not possible in exceptional circumstances, document why, add alternative evidence, and define a follow-up validation gate

3. Select council seats.
Use at least 3 seats (default) and expand toward all 6 for major architecture decisions:
- Source-Grounded Archivist
- Data Model Architect
- Retrieval Specialist
- Human Learning Advocate
- Skeptical Reviewer
- Synthesizer

4. Run independent seat reviews.
Give each seat the same evidence and require:
- Findings
- Risks
- Recommendations
- Assumptions
- Open questions
- Confidence percentage

5. Branch on disagreement.
If seats materially disagree, do not flatten to consensus.
Record the disagreement and identify missing evidence that would change the outcome.

6. Synthesize a decision.
The Synthesizer must separate:
- What changes now
- What is deferred
- What evidence gates must be passed before implementation

7. Record the result in the wiki.
Write or update the decision report with dissent visible, confidence values, and validation criteria.

8. Gate implementation.
Only proceed to code changes or broad migrations after acceptance criteria are explicit and verifiable.

## Decision Branches

- Branch A: Convention-first vs schema-first
If the concept is immature or primarily documentation/process, prefer convention-first plus validation probes.
Promote to schema only after patterns are stable and machine parsing or enforcement is clearly needed.

- Branch B: Retrieval/search sensitive changes
If ranking or recall quality may change, prefer measurable retrieval checks (tests/benchmarks) and explicit rollback notes.
If measurements are not feasible in an exceptional case, require documented rationale plus a time-boxed follow-up measurement plan.

- Branch C: Chat/agent write behavior
If trust or safety boundaries are affected, require evidence traceability and approval-gated write behavior.

- Branch D: Evidence weakness
If evidence is thin or stale, defer implementation and define what evidence must be gathered.

## Completion Checks

A council review is complete only when all are true:
- Decision statement is explicit
- Evidence links are listed and source-grounded
- Each seat includes confidence and blocking concerns
- Dissent is visible, not merged away
- Acceptance criteria are testable or reviewable
- If tests/benchmarks are omitted, exception rationale and follow-up validation gates are explicit
- Open questions are documented with next-step owners or gates

## Report Template

Use this structure in the final output:

```markdown
# Council Review: <Decision>

## Decision
<one sentence>

## Evidence Reviewed
- <pages/memories/source links>
- <tests/validation commands>

## Findings
| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | ... | 0.85 | ... |
| Data Model Architect | ... | 0.80 | ... |
| Retrieval Specialist | ... | 0.78 | ... |
| Human Learning Advocate | ... | 0.82 | ... |
| Skeptical Reviewer | ... | 0.74 | ... |

## Synthesis
<what changes now vs later>

## Dissent
<unresolved disagreement>

## Acceptance Criteria
- <gate 1>
- <gate 2>

## Open Questions
- <question 1>
- <question 2>
```

## Prompt Pattern

Use this prompt form for each seat:

```text
You are the <seat name> council seat for MemorySmith.
Review <topic> using the supplied wiki and code evidence.
Do not implement code.
Return findings, risks, recommendations, assumptions, open questions, and confidence percent.
Prefer MemorySmith source-linked project wiki evidence over generic advice.
```

## References

- Data/Pages/llm-council.md
- Data/Pages/temp-plan.md
- Data/Pages/search-and-chat.md
- Data/Memories/Core (relevant records)
