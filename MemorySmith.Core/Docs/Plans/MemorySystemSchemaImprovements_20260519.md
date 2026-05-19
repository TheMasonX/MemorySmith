# Plan: Core Memory System & Schema Improvements for AI Agents

## 1. Executive Summary
The current `MemoryRecord` schema provides a solid foundational semantic and file-backed wiki system. However, from the perspective of an AI agent consumer, the schema lacks explicit relationship semantics (why do records relate?), granularity in content (hard rules vs. context), and temporal decay mechanics. This plan details schema and structural improvements to optimize the MemorySmith core memory system for AI autonomous agent reasoning.

## 2. Current State Analysis
**Current Schema Strengths:**
*   Clear lifecycle (`Status`: Unconsolidated, Working, Core, Deprecated).
*   Solid attribution (`SourceLinks` with variable expansion).
*   Basic graph associations (`References`, `Conflicts`).
*   Confidence tracking.

**Limitations for AI Agents:**
*   **Untyped Relationships:** `References` and `Conflicts` are simple string arrays of IDs. An agent doesn't know *why* they relate (e.g., "Supersedes", "DependsOn", "ProvidesContextFor").
*   **Opaque Content:** The `Content` field is a single markdown string. Agents struggle to separate strict constraints ("NEVER do X") from general background ("Y is a popular library").
*   **Static Lifecycle:** Memories exist until manually deprecated. There is no automated staleness or temporal validity for transient architecture facts.
*   **Search Limitations:** Keyword and simple semantic search fetch the whole 100-300 token chunk without allowing the agent to target specific subsections.

## 3. Proposed Schema Improvements

### A. Semantic Relationship Typing (Graph Enrichment)
Instead of flat string arrays for `References` and `Conflicts`, use a structured relationship model:
```json
"Relations": {
  "type": "array",
  "items": {
    "type": "object",
    "properties": {
      "TargetId": { "type": "string" },
         "RelationType": {
            "type": "string",
            "enum": ["DependsOn", "Supersedes", "IsContextFor", "ConflictsWith", "ResolvedBy"]
      },
      "Description": { "type": "string" }
    }
  }
}
```
**AI Benefit:** Agents can walk the knowledge graph intelligently (e.g., if a memory is `Superseded`, automatically follow the chain to the active `TargetId`).

### B. Tiered/Structured Content Blocks
Break down `Content` into strict agent-parsable categories.
```json
"Intent": {
   "type": "string",
   "enum": ["Rule", "Context", "Procedure", "Definition"]
},
"Constraints": {
   "type": "array",
   "description": "Hard invariants the agent MUST follow",
   "items": { "type": "string" }
},
"DetailedContent": {
   "type": "string",
   "description": "The contextual prose"
}
```
**AI Benefit:** During MCP context injection, hard `Constraints` can be heavily weighted in the system prompt, while `DetailedContent` can be left for standard RAG context, preventing rule dilution.

### C. Temporal and Priority Mechanics
Introduce memory expiration and agent priority hints.
```json
"Priority": {
   "type": "integer",
   "enum": [0, 1, 2, 3], /* e.g., 0=Background, 3=Critical System invariant */
},
"ValidUntil": {
   "type": "string",
   "format": "date-time",
   "description": "Optional decay date for transient state info"
}
```
**AI Benefit:** Prompts can auto-prune stale records, reducing context window clutter and hallucination risks from outdated environment states.

## 4. Architectural Implementation Plan

1.  **Phase 1: Schema Updates (Week 1)**
    *   Update `Schemas/memory.schema.json` with backward compatibility (keep `Content` as fallback, make `Constraints`/`DetailedContent` optional).
    *   Update `MemorySmith.Core/Models/MemoryRecord.cs` to reflect the JSON schema changes.
    *   Add migration logic in `FileMemoryStore.cs` to handle legacy records on load.

2.  **Phase 2: RAG / Context Pack Overhaul (Week 2)**
    *   Modify `mcp_memorysmithwi_memorysmith_context_pack` to parse `Priority` and strict `Constraints`.
    *   Build the context pack output to distinctly isolate rules from context so the downstream LLM sees: `<Rules> ... </Rules> <Context> ... </Context>`.

3.  **Phase 3: Agent Prompts & Skills (Week 3)**
    *   Update the core `copilot-instructions.md` and specific `.agent.md` files to instruct the AI on how to interpret the new `RelationType` fields when exploring the codebase.
    *   Update the frontend Memory Creation UI to support structured constraint entry.

## 5. Assumptions & Open Questions

*   **Assumption:** The AI context window can comfortably fit the slightly larger JSON metadata payload. (Confidence: 95%).
*   **Assumption:** Legacy memories can be accurately mapped by the AI in a batch migration task (putting text into `Constraints` vs `DetailedContent`).
*   **Open Question:** Will adding semantic relationship types over-complicate manual wiki edits by human users? Should relationship typing be heavily automated?
*   **Open Question:** How do we compute and update the `ValidUntil` date automatically for things that shouldn't expire natively but are likely to change? (e.g., via periodic agent review tasks).

*Confidence Level for Plan Viability:* 85%
