# MemorySmith - Audit #5 And General Vector Report Validation Notes

This page records a direct validation pass against the live repository for the still-relevant claims in `audits/vector-deepdive-5` and the paired general vector-search report.

## Summary

The two reports are still useful as prompts for review, but the majority of the original security/configuration findings are now stale. `TSK-0205` already closed clipboard external-fetch hardening, Mermaid restriction controls, CSP baseline emission, and the markdown sanitization follow-up. The main live gap that survived this pass is narrower and lower-risk than the reports suggested: the admin-only diagnostics surfaces still serialize more exact runtime configuration than their redaction contract implies.

## Confirmed From Code

- `MemorySmith.App/Services/OperationalDiagnosticsService.cs` still returns full `ChatOptions` and `TelemetryOptions` objects inside `EffectiveMemorySmithConfiguration`.
- `MemorySmith.App/Controllers/DiagnosticsController.cs` protects `/api/diagnostics` with `MemorySmithPolicies.CanAdminMemorySmith`, so the exposure is currently admin-only rather than broadly public.
- `MemorySmith.App/Components/Pages/HealthStats.razor` still formats telemetry export with the exact `OtlpEndpoint` string.
- `MemorySmith.App/Services/ChatServices.cs` remains a large multi-concern service, so the broader maintainability complaint from the audit family still stands even though the earlier security findings were partially stale.

## Corrected Or Stale Audit Claims

- Clipboard external image fetch is already default-off and admin-configurable through `MemorySmith.App/appsettings.json`, `MemorySmith.App/Services/MemorySmithOptions.cs`, `MemorySmith.App/Services/AdminSettingsService.cs`, and the gated fetch path in `MemorySmith.App/wwwroot/memorysmith.js`.
- Content-Security-Policy baseline settings and middleware are already present in `MemorySmith.App/appsettings.json`, `MemorySmith.App/Services/MemorySmithOptions.cs`, `MemorySmith.App/Services/AdminSettingsService.cs`, and `MemorySmith.App/Program.cs`.
- The markdown sanitization follow-up is no longer missing as originally claimed. `MemorySmith.Tests/PagesAndChatTests.cs` now covers unsafe markdown links, single-quoted raw HTML attributes, unquoted trusted-mode attributes, and `srcset` sanitization in `ChatMarkdownRenderer`.
- The current prompt contract in `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.md` already documents the intercepted read-only local tool protocol, including code-search tools, so no broad prompt/tool-list drift defect was confirmed from this pass.

## Task Mapping

- Existing completed owner: `TSK-0205` for clipboard, Mermaid, CSP, and markdown hardening.
- Existing backlog owner: `TSK-0042` for the broader `ChatServices` decomposition and readability problem.
- Existing backlog owner: `TSK-0212` for the narrower chat tool-call parse and buffer fallback robustness defects confirmed during Audit #6 validation.
- New backlog owner: `TSK-0213` for operational diagnostics redaction boundaries.

## Notes

- I did not create new tasks for claims that no longer survive validation. This pass is meant to reduce backlog noise as much as it adds new work.
- The tracker for this validation run is `logs/agent-smith-20260529-vector-audit-family-validation.md`.