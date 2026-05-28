# Council Review: Security Safe-by-Default Guardrails

## Decision

Adopt a safe-by-default security baseline that preserves localhost functionality, and require explicit opt-in for high-risk behaviors through profile/config guardrails and targeted compatibility switches.

## Evidence Reviewed

- Data/Pages/research/security-codebase-audit-20260523.md
- MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs
- MemorySmith.App/Program.cs
- MemorySmith.App/Services/SecurityServices.cs
- MemorySmith.App/Controllers/AuthController.cs
- MemorySmith.App/Controllers/AdminController.cs
- MemorySmith.App/appsettings.json
- MemorySmith.App/Services/MemorySmithLocalDevelopmentPostConfigure.cs
- MemorySmith.App/Properties/launchSettings.json
- MemorySmith.Tests/SecurityAndSourceLinkTests.cs
- Data/Tasks/tsk-0023-add-startup-admin-guardrails-for-secure-remote-mode-when-allowremoteapi-true-require-an-api-key-and-enforce-https-auth-hardening-settings.json

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | Prioritize remote hardening and transport controls first; evidence already shows warning-only behavior on key remote guardrail. | 94% | If behavior remains warning-only, operator error can become externally reachable exposure. |
| Data Model Architect | Introduce explicit security profile metadata (safe local, secure remote, custom) and persist profile-derived settings as clear config state. | 81% | Overloading many boolean flags without profile semantics will keep drift risk high. |
| Retrieval Specialist | Keep read-path functionality unaffected; hardening should target auth, transport, and write approvals without reducing retrieval/search usability. | 86% | Over-broad restrictions may degrade legitimate wiki retrieval and tool workflows. |
| Skeptical Reviewer | Require proxy-trust and anti-forgery decisions to be test-backed before broad rollout; avoid assumptions based only on local-host behavior. | 78% | Reverse-proxy and browser threat-model gaps can invalidate local-only assumptions. |
| Synthesizer | Sequence into two sprints: enforce remote+transport baseline first, then close proxy/CSRF/testing/documentation gaps with configurable opt-ins. | 89% | Must preserve existing localhost workflows and avoid breaking current runbooks. |

## Synthesis

- Change now:
  - Enforce hardened remote baseline with explicit opt-in compatibility switches.
  - Add transport/cookie hardening options and default-safe remote behavior.
  - Add profile-driven guidance in admin UX and diagnostics.
- Defer with gate:
  - Full anti-forgery expansion and proxy-trust model finalization pending focused compatibility tests.

## Dissent

- Skeptical Reviewer dissents on enabling anti-forgery broadly in Sprint 1 without proven compatibility for current form/API flows.
- Retrieval Specialist cautions against any policy that implicitly weakens memory/page retrieval workflows or tool-call reliability.

## Acceptance Criteria

- Remote mode cannot silently run with insecure high-risk combinations unless an explicit compatibility override is configured.
- Secure transport controls are configurable and default-safe for non-loopback scenarios.
- Proxy-trust behavior is explicitly configurable and covered by tests.
- At least one end-to-end validation path confirms localhost workflows remain functional.

## Open Questions

- Which exact compatibility switches are mandatory to preserve current scripted workflows?
- Should profile enforcement occur at startup only, runtime diagnostics only, or both?
- What proxy trust defaults are safe without overcomplicating single-host setups?
