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

## Additional Procedure
1. Review active skills for duplication and missing shared abstractions.
2. Identify loops that should move from chat polling to script-driven waits.
3. Propose prompt updates that reduce ambiguity and token use.
4. Update requests pages under `Data/Pages/requests` with:
   - a master outstanding list
   - grouped changes for small items
   - dedicated pages for significant changes
5. Mark each recommendation as now, next, or later with confidence.

## Output
- Improvement shortlist with impact and effort.
- Proposed edits/scripts with evidence.
- Updated requests pages reflecting outstanding work.
