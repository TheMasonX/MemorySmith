---
name: self-review
description: "Run an agent self-review for skill and prompt improvements, including script extraction opportunities and token-conscious workflow upgrades."
argument-hint: "Review scope, urgency, and output format"
user-invocable: true
disable-model-invocation: false
---

# Self Review

Inherits from `task-core-loop`.

## Added Context
Use this to periodically review and improve:
- skill design and overlap
- agent prompt clarity
- token-cost hotspots
- script-extraction opportunities for repeated terminal commands
- chat tool usage parity across prompt, runtime, MCP, and coding-agent surfaces

Prefer MCP tooling first for reads/writes where available; propose script hooks only for repeated operations that are not already covered by MCP tools.

## Additional Procedure
1. Review active skills for duplication and missing shared abstractions.
2. Identify loops that should move from chat polling to script-driven waits.
3. Propose prompt updates that reduce ambiguity and token use.
4. For repeated manual content generation, propose hook-script outputs that feed MCP write tools.
5. Treat chat tool usage parity as a first-class requirement: document any intentional mode differences and flag undocumented drift.
5. Update requests pages under `Data/Pages/requests` with:
   - a master outstanding list
   - grouped changes for small items
   - dedicated pages for significant changes
6. Update or create `/tasks` records when a recommendation is significant enough to merit execution tracking.
7. Mark each recommendation as now, next, or later with confidence.

## Output
- Improvement shortlist with impact and effort.
- Proposed edits/scripts with evidence.
- Updated requests pages reflecting outstanding work.
