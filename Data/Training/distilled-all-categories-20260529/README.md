# Distilled Dataset: 2026-05-29

Source file: `distilled-all-categories-20260529.sft.jsonl`

## Supplemental Description (Hyperagent)

- 55 validated JSONL examples across 9 categories
- 29 unique real memory/page IDs from the live knowledge base
- Tool call envelope verified: `{"toolCalls":[{"name":"...","arguments":{...}}]}`

Category totals:

- Single-tool retrieval: 15
- Context pack + multi-reference: 8
- Direct get by ID/slug: 5
- Task browsing: 5
- Code search: 5
- Multi-turn: 6
- Agent-mode writes: 4
- Graceful failure: 4
- Citation-focused: 3

Validation report:

- 55/55 valid JSON
- 0 confabulated IDs
- Correct tool-call envelope on all 53 tool-call examples
- All `memory:` and `page:` references from verified inventory

Quality note:

Hyperagent initially generated an incorrect envelope (`{"tool":"..."}`) before correction. The current file uses the exact envelope expected by the C# `ReadToolCalls` parser, consistent with TRAIN-001 audit guidance.
