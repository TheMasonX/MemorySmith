# Deep Research Prompt: MemorySmith Chat Harness Capability Upgrade

Use this prompt with Microsoft Copilot (GPT-5.3-Codex or equivalent deep research mode) to produce an evidence-first research report for improving MemorySmith chat.

## Prompt

```markdown
You are an expert research analyst for the MemorySmith codebase.
Do not implement code. Produce a deeply reasoned, source-grounded research report.

## Mission
Evaluate how to evolve MemorySmith chat into a maximally capable and useful system while preserving reliability, provenance, and user trust.

Focus especially on:
1. Context-window management and token-budget planning.
2. Manual and automatic compaction (`/compact`) strategies.
3. Per-chat memory file/ledger design.
4. Prompt/system-context improvements.
5. Tooling exposure needed by chat and agents.
6. Retrieval quality and traceability under compression.

## Required Output Format
Return a report with these sections exactly:

1. Executive Decision Options (3 options minimum)
2. Current-State Technical Baseline
3. Gap Analysis
4. Design Proposals (short-term, medium-term, long-term)
5. Risk Register
6. Validation Plan and Metrics
7. Rollout and Rollback Plan
8. Open Questions
9. Recommended Next Step

Use confidence percentages for each major recommendation.
Keep dissent explicit when trade-offs are unresolved.

## Evidence You Must Use First (source-grounded)
- Data/Pages/guides/search-and-chat.md
- Data/Pages/council/llm-council.md
- Data/Pages/council/phase4-chat-context-planner-native-tool-council-review-20260522.md
- Data/Pages/chat-harness-capability-council-review-20260522.md
- Data/Memories/Core/project-wiki-chat-configuration-current.json
- Data/Memories/Core/project-wiki-chat-agent-provider.json
- Data/Memories/Core/project-wiki-chat-local-storage-persistence.json
- MemorySmith.App/Services/ChatContextPlanner.cs
- MemorySmith.App/Services/ChatServices.cs
- MemorySmith.App/Services/MemorySmithOptions.cs
- MemorySmith.App/Controllers/ChatController.cs
- MemorySmith.App/Components/Pages/Chat.razor
- MemorySmith.Tests/PagesAndChatTests.cs
- MemorySmith.Tests/ChatToolCatalogAndInterceptTests.cs

If any evidence is stale or contradictory, call it out explicitly and lower confidence.

## Hard Constraints
- Preserve approval-gated write behavior in Agent mode.
- Preserve deterministic local tool-call interception fallback.
- Preserve user-visible traceability for context and tool decisions.
- Avoid silent quality degradation under compaction.
- Prefer convention-first over schema-first unless schema is clearly justified.

## Research Questions (answer all)
1. What is the best token budget architecture for this harness?
2. Should `/compact` be manual-only, auto-only, or hybrid? Why?
3. What exact trigger conditions should start auto-compaction?
4. How should compacted context preserve citations and reversibility?
5. Should per-chat memory be local-only first or persisted server-side? Why?
6. What minimum set of new chat/agent tools would materially improve capability?
7. How should system prompt and runtime capability prompts change to reduce tool misuse and hallucinated certainty?
8. Which retrieval regressions are most likely after compaction, and how should we detect them?
9. What phased rollout minimizes risk while improving capability quickly?

## Required Technical Depth
Include concrete proposals for:
- Token budgeting formula and budget partitions.
- Compaction data model (even if convention-first), including provenance fields.
- Prompt snippets for compact-mode behavior and uncertainty handling.
- Trace/UX additions that make compaction transparent to users.
- Test matrix additions and benchmark probes.

When proposing formulas, include explicit math notation.
Example format:
- Total budget: B
- Reserved output: R_out
- Available input: B_in = B - R_out
- Partition weights with fallback rules.

## Evaluation Rubric
Score each proposal (0-100) on:
- Reliability
- Retrieval fidelity
- User trust/interpretability
- Implementation complexity
- Migration/rollback safety

Then provide a weighted final ranking with rationale.

## Deliverable Expectations
- Be exhaustive, not generic.
- Tie each recommendation to specific source evidence.
- Include at least 10 concrete acceptance checks and 5 failure-mode tests.
- Include a 30/60/90 day rollout view.
- End with a single recommended plan and why alternatives were not selected.
```

## Optional Add-On Prompt (Second Pass)

```markdown
Now perform an adversarial review of your own recommendation.
Assume compaction causes subtle retrieval loss and false confidence in answers.
List the top 10 ways the plan can fail in production and what instrumentation would detect each failure within 24 hours.
Then revise the plan accordingly.
```
