# Deep Research Prompt: Agent Memory, Search, and Wiki Architecture

Use this prompt in Microsoft Copilot, ChatGPT Deep Research, or a comparable research mode. The goal is to answer externally researchable questions from the [Core Memory System Improvements RFC](temp-plan.md). Do not use this prompt to decide local-only MemorySmith policy by fiat; use it to gather evidence, patterns, trade-offs, and examples that can inform a later council review.

## Prompt

```text
You are performing deep technical research for MemorySmith, a local-first ASP.NET Core/Blazor app for structured project memory, markdown wiki pages, MCP tools, and memory-enhanced chat.

Context:
- MemorySmith stores structured memories as JSON records with fields like Id, Content, Status, Confidence, Tags, References, Conflicts, SourceLinks, UsageCount, and LastUpdated.
- Longer human-readable documentation lives in markdown pages.
- Search currently includes lexical, semantic, and hybrid retrieval. Semantic retrieval may use local ONNX embeddings or fallback token scoring. Hybrid uses rank fusion.
- MCP/chat tools can return memory search results, page results, context packs, and source-linked evidence.
- The current design question is whether to improve agent usefulness through lightweight conventions first, through explicit schema fields, or through a staged path from conventions to schema when evidence justifies it.
- Important local constraints: local-first operation, inspectable file-backed storage, source-grounded memory records, human-readable wiki pages, and no unnecessary service/database complexity unless evidence shows it is needed.

Research objective:
Produce an evidence-backed report that helps MemorySmith decide how to evolve agent memory, retrieval, wiki pages, and chat traces for long-term usefulness. Focus on practices supported by reputable sources, implementations, papers, product docs, or widely used open-source systems. Distinguish strong evidence from opinion.

Questions to research:

1. Namespaced tags and UI-assisted metadata
- In knowledge-base, note-taking, personal knowledge management, issue-tracking, or AI memory systems, when do tag conventions remain manageable, and when do they need UI-assisted chips, controlled vocabularies, validation, or schema fields?
- What conventions are commonly used for namespaced tags such as kind:rule, priority:critical, review-after:YYYY-MM, expires:YYYY-MM, supersedes:<id>, or superseded-by:<id>?
- What failure modes are documented for free-form tags in long-lived knowledge bases?
- What UI patterns best prevent typo drift while keeping editing fast?

2. Expiration, review dates, and staleness handling
- How do mature systems handle expiring knowledge, review-after dates, deprecated docs, superseded docs, and stale search results?
- Is it better to warn, filter, demote, archive, or automatically move records to a deprecated state?
- What evidence exists about risks of time-decay ranking in knowledge retrieval, especially burying old but still-important rules?
- What ranking or warning patterns are recommended for LLM retrieval-augmented generation when records may be stale?

3. Page chunking and page embeddings thresholds
- What practical thresholds indicate that whole-page search is no longer enough and page chunking should be added?
- Recommended chunk sizes, overlap, section-aware chunking, markdown-heading preservation, provenance fields, and citation practices for RAG over documentation.
- When should markdown pages be embedded alongside structured records?
- What evaluation metrics should be used before and after page chunking: MRR, recall@k, nDCG, answer faithfulness, citation accuracy, latency, or human usefulness ratings?

4. JSON vs Markdown output for agent tools
- For agent-facing tools, what evidence or patterns support JSON as the default output instead of Markdown prose?
- How do MCP tools, LLM tool-calling APIs, agent frameworks, or structured-output systems recommend returning records, warnings, relationship metadata, and errors?
- What hybrid design works best when humans may inspect the same output: JSON default for tools, Markdown summaries for UI, or both?

5. Relationship typing: schema, graph store, or convention notes
- In file-backed knowledge systems or AI memory systems, when should relationships be represented as typed schema fields, graph edges, tags, or prose notes?
- Compare approaches for DependsOn, Supersedes, ConflictsWith, ResolvedBy, IsContextFor, and related edge types.
- What are practical migration strategies from flat References/Conflicts arrays to typed relationships?
- When is a lightweight graph store justified, and when is schema enough?

6. Agent write approval and governance
- What approval models are used for AI-generated durable knowledge, documentation changes, or memory writes?
- What role or permission model is typical for high-trust changes such as strict rules, Core records, deprecations, or schema-affecting updates?
- What evidence should an AI write proposal include before approval: source links, citations, confidence, dissent, trace, tests, related records, rollback notes?
- What UX patterns help humans review AI-proposed knowledge updates without turning approval into rubber-stamping?

7. Strict rules in markdown and extraction reliability
- Are GitHub Flavored Markdown alert blocks, admonitions, frontmatter, or structured sections commonly used to distinguish strict rules from ordinary context?
- What parsers or markdown AST approaches are recommended over regular expressions for robust extraction?
- What prompt or context-pack formatting helps LLMs respect retrieved rules while treating retrieved content as untrusted data?

8. Council-style multi-perspective review
- What evidence exists for multi-agent, multi-perspective, red-team, debate, or council workflows improving technical decisions or reducing blind spots?
- What are the costs and failure modes: groupthink, verbosity, false consensus, repeated assumptions, model collusion, or overconfidence?
- What minimal council workflow is practical for a small local-first developer tool?

Deliverables:
- Executive summary with the top 10 findings.
- A table mapping each research question to recommendations for MemorySmith.
- Evidence levels: strong evidence, moderate evidence, weak evidence/opinion.
- Concrete examples from real systems, docs, papers, or open-source projects, with links.
- Risks and anti-patterns to avoid.
- Recommended decision gates for when to stay with conventions, when to add validation/UI, and when to promote a convention into schema.
- Suggested evaluation metrics and test probes for retrieval quality and stale-context safety.
- A short answer to this key decision: for a local-first AI memory/wiki/chat system, what should be convention-first, what should be schema-first, and what should remain page prose?

Constraints for your answer:
- Do not assume a cloud database, SaaS tenancy, or distributed architecture is required.
- Prefer local-first and file-backed-friendly designs unless there is strong evidence that complexity is worth it.
- Distinguish external best practices from decisions that require local MemorySmith product judgment.
- Cite sources with URLs and explain why each source is relevant.
- If evidence is thin, say so directly.
```

## Questions Excluded From External Research

These require local product judgment or code review more than external research:

- The exact role required to approve Agent writes in MemorySmith. External systems can inform the model, but final roles must fit MemorySmith's RBAC and local deployment assumptions.
- The exact threshold for MemorySmith page chunking. External RAG thresholds can guide this, but final thresholds should be based on MemorySmith corpus size, latency, and search-quality probes.
- Whether `memorysmith_context_pack` should change its default output. External tool-output patterns can inform this, but compatibility with current MCP/chat consumers must be checked locally.

## How To Use The Results

After research returns, run a [Council Workflow](llm-council.md) review before changing code or schema. The council should separate:

- findings that update the RFC immediately;
- findings that justify a prototype;
- findings that require local benchmarks or user testing;
- findings that should be rejected because they conflict with MemorySmith's local-first goals.