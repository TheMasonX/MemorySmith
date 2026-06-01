---
name: runtime-parity-audit
description: "Audit and reconcile drift between agent/prompt docs and actual runtime capabilities in MemorySmith services/controllers."
argument-hint: "Prompt scope and runtime surfaces to verify"
user-invocable: true
disable-model-invocation: false
---

# Runtime Parity Audit

Inherits from `task-core-loop`.

## Added Context
Use this to validate that prompt contracts match implemented behavior.

## Focus Surfaces
- `MemorySmith.Core/Docs/Prompts/*`
- `.github/agents/smith.agent.md`
- `.github/copilot-instructions.md`
- Runtime implementations in `MemorySmith.App/Services` and `MemorySmith.App/Controllers`

## Additional Procedure
1. Extract behavioral claims from prompts/docs.
2. Map each claim to source code evidence.
3. Classify each claim as: aligned, drifted, stale, or unverifiable.
4. Patch prompt/docs or code based on source-of-truth intent.
5. Record explicit drift findings and follow-up tasks.

## Output
- Parity matrix (claim -> code evidence -> status).
- Required updates with risk and confidence.