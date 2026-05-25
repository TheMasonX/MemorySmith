# Council Review: Source-Governance Sprint

## Decision
The source-governance slice is merge-ready for the implemented scope: configurable source-read expansion, deny-first source-root policy, and shared MCP source-bridge tools are consistent with the existing safety model, with one residual risk that unrestricted reads must stay opt-in and documented clearly.

## Evidence Reviewed
- [MemorySmith.App/Services/MemorySmithOptions.cs](../../../../MemorySmith.App/Services/MemorySmithOptions.cs)
- [MemorySmith.App/Services/VarResolver.cs](../../../../MemorySmith.App/Services/VarResolver.cs)
- [MemorySmith.App/Services/ChatToolCatalog.cs](../../../../MemorySmith.App/Services/ChatToolCatalog.cs)
- [MemorySmith.App/Controllers/McpController.cs](../../../../MemorySmith.App/Controllers/McpController.cs)
- [MemorySmith.App/Services/AdminSettingsService.cs](../../../../MemorySmith.App/Services/AdminSettingsService.cs)
- [MemorySmith.App/Services/OperationalDiagnosticsService.cs](../../../../MemorySmith.App/Services/OperationalDiagnosticsService.cs)
- [MemorySmith.Tests/SecurityAndSourceLinkTests.cs](../../../../MemorySmith.Tests/SecurityAndSourceLinkTests.cs)
- [MemorySmith.Tests/ChatToolCatalogAndInterceptTests.cs](../../../../MemorySmith.Tests/ChatToolCatalogAndInterceptTests.cs)
- [README.md](../../../../README.md)
- Validation: `dotnet test .\MemorySmith.Tests\MemorySmith.Tests.csproj --filter "FullyQualifiedName~SecurityAndSourceLinkTests|FullyQualifiedName~ChatToolCatalogAndInterceptTests"`

## Findings
| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | Approve the config and resolver changes; they make source-grounded answers more useful without widening writes. | 0.90 | Broad-read mode must remain opt-in and discoverable. |
| Retrieval Specialist | Approve the shared catalog move; treating source-bridge tools as `SensitiveRead` is the right central gate. | 0.88 | If future source tools are added, they must inherit the same risk classification. |
| Skeptical Reviewer | Approve with caution; the only meaningful risk is accidental overexposure if unrestricted reads are enabled in production. | 0.84 | Operators could misread the new knob as a default behavior change. |
| Synthesizer | Merge the current slice and defer any separate write-root split to a follow-up sprint. | 0.89 | No evidence that the current changes require a larger schema or routing redesign. |

## Dissent
There was no material dissent on the implemented scope. The only concern was operational: unrestricted reads are powerful and should stay off by default, with explicit documentation and admin intent.

## Synthesis
What changes now:
- Source-linked reads can expand around requested line ranges within configured context bounds.
- Deny roots override allow roots and unrestricted-read opt-in.
- MCP source tools are centralized in the shared catalog.

What is deferred:
- Separate chat-agent write roots from maintenance-agent write roots.
- Any broader write-policy redesign beyond explicit approval gates.

## Acceptance Criteria
- Focused tests cover allowed source reads, denied source reads, and broad-read opt-in behavior.
- Shared source-bridge tools appear through the MCP catalog as `SensitiveRead`.
- Docs explain the new source-read knobs and the operational risk of broad reads.
- Write approval remains explicit and unchanged.

## Open Questions
- Should the broad-read opt-in be surfaced in admin UI copy as a dangerous/advanced setting?
- Should the write-root split be promoted into the next sprint or remain explicitly separate?
