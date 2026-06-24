# Cross-Repo Request: Fix bare catch blocks in ChatServices.cs

**Date:** 2026-06-24
**Source Audit:** MemorySmith.Agent audit validation council
**Requested by:** SteveBot (Agent repo maintenance agent)
**Reviewer:** @TheMasonX

## Summary

`MemorySmith.App/Services/ChatServices.cs` contains 20+ bare `catch { }` blocks across streaming, SSE, and tool dispatch paths. These silently swallow exceptions, making streaming failures, SSE errors, and tool dispatch bugs invisible until they manifest as strange user-facing behavior.

## Severity Assessment

**HIGH** — These catch blocks can mask:
- Streaming response failures (user sees empty or partial response)
- SSE connection errors (agent appears connected but isn't receiving events)
- Tool dispatch exceptions (tool "fails silently" with no error message)
- JSON serialization errors in chat context

## Evidence

The following bare `catch { }` blocks were identified (line numbers from the current `sprint-35-llm-first` branch state — verify against latest):

| Line | Context | Risk |
|------|---------|------|
| ~348 | Chat streaming | Stream failure → user sees no response |
| ~369 | Chat streaming | Same |
| ~394 | Chat streaming | Same |
| ~423 | SSE connection | Connection lost silently |
| ~439 | SSE connection | Connection lost silently |
| ~811 | Tool dispatch | Tool crash → no error returned |
| ~1426 | ChatServices core | General exception swallowed |
| ~1442 | ChatServices core | General exception swallowed |
| ~1562 | ChatServices core | General exception swallowed |
| ~1580 | ChatServices core | General exception swallowed |
| ~1598 | ChatServices core | General exception swallowed |
| ~1616 | ChatServices core | General exception swallowed |
| ~2372 | Chat context | Context build failure → wrong response |
| ~2468 | Chat context | Same |
| ~3467 | Chat completion | Completion failure → empty response |

## Recommended Fix Pattern

Replace each bare `catch { }` with:

```csharp
catch (Exception ex) when (!IsFatal(ex))
{
    logger?.LogWarning(ex, "Non-fatal error in {Context}: {Message}", contextName, ex.Message);
    // Continue — preserve existing fallthrough behavior
}
```

Where `{Context}` uniquely identifies the location (e.g., "ChatServices.StreamResponse", "ChatServices.SseDispatch").

## Relationship to Agent Repo Fixes

The MemorySmith.Agent repo is fixing the same pattern across 7 locations in Sprint 46 (TSK-0101: "Fix all catch→null blocks with structured logging"). This request extends the same hardening to the base repo.

## Acceptance Criteria

- [ ] All bare `catch { }` blocks replaced with `catch (Exception ex) { logger?.LogWarning(...) }`
- [ ] No behavioral changes — existing fallthrough behavior preserved
- [ ] `dotnet test` passes with zero failures
- [ ] `dotnet build` passes with zero warnings

## Reference

- Council report: `Data/Pages/council/audit-validation-sprint46-plan-council-2026-06-24.md`
- Agent task: TSK-0102
