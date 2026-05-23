# Council Workflow for MemorySmith Decisions

Status: Draft operating method  
Use with: [Core Memory System Improvements RFC](../plans/temp-plan.md), [Search and Chat](../guides/search-and-chat.md)

## Purpose

The council workflow is a structured way to review decisions that are too important for a single quick answer. It asks several independent perspectives to inspect the same evidence, disagree openly, and then produce a synthesis with confidence, risks, and next steps.

In MemorySmith, the council is not an external plugin requirement. It is a local practice for using agents, MCP search, wiki pages, chat trace evidence, and human review together.

Use the council when a decision affects long-term memory quality, search behavior, schema shape, Agent writes, security boundaries, or user-facing learning docs.

Do not use it for quick lookups, simple edits, exact ID fetches, or routine formatting.

## When To Use It

| Decision type | Use council? | Why |
|---|---:|---|
| Schema or memory-record shape changes | Yes | Migration and agent-parsing costs are long-lived. |
| Search ranking, staleness, or vector-index behavior | Yes | Small ranking changes can hide important memories. |
| Chat/Agent write behavior | Yes | Human trust depends on evidence, traceability, and approval. |
| New user-facing wiki conventions | Usually | Humans and agents both need to understand them. |
| One-off page cleanup | No | A normal edit is enough. |
| Exact memory/page retrieval | No | Use search, get, page get, or context pack directly. |

## Council Seats

Use the seats that match the decision. A small review can use three; a major architecture decision should use all six.

| Seat | Responsibility | Default question |
|---|---|---|
| Source-Grounded Archivist | Checks evidence, source links, and wiki truth | What is actually verified, and where is it documented? |
| Data Model Architect | Checks schema, migration, validation, and storage costs | Is this concept better as schema, convention, page prose, or nothing? |
| Retrieval Specialist | Checks lexical, semantic, hybrid, MCP, context-pack, and page retrieval effects | Will agents find the right thing later? |
| Human Learning Advocate | Checks page structure, examples, chat teaching flow, and user comprehension | Would a human understand and safely use this? |
| Skeptical Reviewer | Looks for overclaims, stale assumptions, hidden complexity, and rollback needs | What will we regret in six months? |
| Synthesizer | Combines evidence into a decision and records dissent | What should change now, what waits, and what evidence would change the answer? |

## Evidence Pack

Before asking for council opinions, gather a bounded evidence pack. Prefer MemorySmith tools before general searching.

Minimum evidence for a MemorySmith decision:

- Relevant pages from `Data/Pages`.
- Relevant Core memories from `Data/Memories/Core`.
- `memorysmith_context_pack` for linked references, conflicts, and backlinks when records are known.
- Source bundles for records whose claims depend on code.
- Current tests or validation commands if the decision touches behavior.
- Open assumptions and known stale documents.

For page-only planning, a page search plus a few direct page reads may be enough. For schema/search/chat plans, use a context pack and source-linked evidence.

## Process

1. Define the decision in one sentence.
2. Gather the evidence pack.
3. Give every council seat the same evidence and ask for independent findings.
4. Require each seat to return risks, recommendations, assumptions, open questions, and a confidence percentage.
5. Compare disagreements explicitly instead of smoothing them away.
6. Synthesize a decision or RFC update.
7. Record the decision, dissent, confidence, and follow-up validation in the wiki.
8. Only then implement code or broad data migrations.

## Output Template

Use this template for final council reports.

```markdown
# Council Review: <Decision>

## Decision
One sentence describing what is being accepted, rejected, or deferred.

## Evidence Reviewed
- Page or memory links
- Source links
- Tests or validation commands

## Findings
| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | ... | 0.85 | ... |
| Data Model Architect | ... | 0.80 | ... |
| Retrieval Specialist | ... | 0.78 | ... |
| Human Learning Advocate | ... | 0.82 | ... |
| Skeptical Reviewer | ... | 0.74 | ... |

## Synthesis
What changes now, what remains undecided, and why.

## Dissent
Concise record of unresolved disagreement.

## Acceptance Criteria
- Tests, docs, or review gates required before implementation.

## Open Questions
- Questions that should block or shape future work.
```

## Prompt Pattern

For a subagent or chat turn, use a role-specific prompt like this:

```text
You are the <seat name> council seat for MemorySmith.
Review <topic> using the supplied wiki/code evidence.
Do not implement code.
Return findings, risks, recommendations, assumptions, open questions, and confidence percent.
Prefer MemorySmith's current architecture and source-linked project wiki over generic advice.
```

For a single chat session, ask for separate sections instead of separate agents:

```text
Use a MemorySmith council review. Give me separate Source-Grounded Archivist, Data Model Architect, Retrieval Specialist, Human Learning Advocate, Skeptical Reviewer, and Synthesizer sections. Keep dissent visible.
```

## How It Fits MemorySmith Chat

MemorySmith chat already has pieces that make council review useful:

- It can retrieve memories and pages through preloaded context and read-only tool calls.
- It can show references and trace events for tool calls and reasoning.
- Agent writes remain approval-gated when enabled.
- The Trace sidebar can be used to inspect what evidence influenced a response.

The desired long-term behavior is for chat to make council review easier:

- A user asks for a council review of a proposed page, memory, or architecture decision.
- Chat retrieves a bounded evidence pack.
- Each seat produces a compact, separately labeled finding.
- The Synthesizer proposes a wiki update or decision record.
- If Agent writes are enabled, the proposed write appears in Trace with approval controls.

This page documents the method; it does not require the app to implement multi-agent orchestration before the method is useful.

## Example: Memory System RFC Review

Decision: Should MemorySmith improve AI-agent memory through convention-only tags and markdown alerts, or through explicit schema fields?

Expected council disagreement:

- Data Model Architect will prefer schema when behavior depends on type safety.
- Retrieval Specialist will prefer whatever improves ranking, warnings, and context-pack structure with measurable tests.
- Human Learning Advocate will resist conventions that are invisible in the UI.
- Skeptical Reviewer will reject overconfident claims about LLM parsing and temporal decay.
- Synthesizer should likely choose convention-first with validation and schema-promotion gates.

That is exactly the kind of decision where council review is worth the extra work.

## Quality Bar

A council review is useful only if it changes the decision quality. Good reviews have:

- Evidence from the local wiki or source tree.
- Specific risks, not vague caution.
- Confidence values that can go down when evidence is weak.
- Dissent that remains visible in the final record.
- Acceptance criteria before implementation.
- A clear distinction between “document now,” “prototype,” and “ship.”

Weak reviews should be rejected when they:

- Repeat generic web-search summaries.
- Cite unverified external projects as authority.
- Hide disagreement behind a false consensus.
- Recommend implementation without migration, tests, or rollback notes.
- Ignore the difference between structured memories, markdown pages, and chat behavior.

## Relationship to External LLM Council Ideas

The broader LLM-council idea is simple: multiple model perspectives can expose blind spots. MemorySmith's version is intentionally local and evidence-centered. It does not depend on a specific vendor, model, plugin, or external repository. External tools may inspire the workflow, but local wiki evidence and human review decide what MemorySmith records as truth.