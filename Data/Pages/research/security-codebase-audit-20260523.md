# Comprehensive Codebase Audit Report - Security Focus (2026-05-23)

> Historical note: this page captures the 2026-05-23 security posture snapshot, not guaranteed current state. Some findings were later addressed by follow-up work such as `TSK-0023` (remote hardening) and `TSK-0055` (task read authorization). Verify current behavior against current code, README, and canonical task records before planning from this audit.

## Safety Stance

MemorySmith should preserve functionality while enforcing a safe-by-default posture, with explicit opt-in switches for elevated risk capabilities. Security controls should fail closed where practical and expose clear operator guidance where strict blocking would disrupt expected local workflows.

## Scope

- Included:
  - Auth, request guard, and role policies in MemorySmith.App.
  - Chat and agent write governance controls.
  - Source-link local file boundary and open-with-default-app controls.
  - Page rendering safety controls and local development overrides.
  - Current security-relevant test coverage.
- Excluded:
  - Third-party dependency CVE scanning and SCA inventory (not executed in this pass).
  - Network perimeter hardening external to the app host.
- Timebox:
  - Medium-depth targeted audit.

## Evidence Reviewed

- README.md
- MemorySmith.App/Program.cs
- MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs
- MemorySmith.App/Services/SecurityServices.cs
- MemorySmith.App/Services/VarResolver.cs
- MemorySmith.App/Services/AdminSettingsService.cs
- MemorySmith.App/Controllers/AuthController.cs
- MemorySmith.App/Controllers/AdminController.cs
- MemorySmith.App/Controllers/PagesController.cs
- MemorySmith.App/Controllers/SourceLinksController.cs
- MemorySmith.App/appsettings.json
- MemorySmith.App/Services/MemorySmithLocalDevelopmentPostConfigure.cs
- MemorySmith.App/Properties/launchSettings.json
- MemorySmith.Tests/SecurityAndSourceLinkTests.cs
- MemorySmith.Tests/AppApiContractTests.cs
- Data/Tasks/tsk-0023-add-startup-admin-guardrails-for-secure-remote-mode-when-allowremoteapi-true-require-an-api-key-and-enforce-https-auth-hardening-settings.json

## Findings

| ID | Domain | Severity | Confidence | Summary | Evidence |
|---|---|---|---:|---|---|
| F-001 | Remote API guardrails | High | 93% | Remote API enablement is warning-first rather than strict-safe-by-default: when AllowRemoteApi is true and ApiKey is empty, diagnostics warn but request guard does not block. | MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs, MemorySmith.App/Services/OperationalDiagnosticsService.cs, MemorySmith.Tests/SecurityAndSourceLinkTests.cs |
| F-002 | Transport/cookie security | High | 87% | Cookie auth sets SameSite and sliding expiration, but no explicit secure cookie policy or HSTS policy is configured. In HTTP deployments this can weaken transport guarantees. | MemorySmith.App/Program.cs |
| F-003 | Proxy/trust boundary | Medium | 82% | Loopback and HTTPS-sensitive decisions rely on Connection.RemoteIpAddress and Request.IsHttps without explicit forwarded-header trust configuration, creating risk in reverse-proxy deployments. | MemorySmith.App/Services/MemorySmithRequestGuardMiddleware.cs, MemorySmith.App/Services/SecurityServices.cs, MemorySmith.App/Controllers/AuthController.cs |
| F-004 | Setup/auth CSRF resilience | Medium | 76% | Setup and login form endpoints are anonymous form posts and currently rely on ambient browser protections and deployment context rather than explicit anti-forgery enforcement at those endpoints. | MemorySmith.App/Controllers/AdminController.cs, MemorySmith.App/Controllers/AuthController.cs, MemorySmith.App/Program.cs |
| F-005 | Risky-local profile drift | Medium | 90% | LocalDevelopment post-configuration intentionally relaxes multiple controls (remote API, raw HTML pages, larger limits, agent writes), but no first-class profile gate prevents accidental carryover into less-trusted environments. | MemorySmith.App/Services/MemorySmithLocalDevelopmentPostConfigure.cs, MemorySmith.App/Properties/launchSettings.json, MemorySmith.App/Services/AdminSettingsService.cs |
| F-006 | Positive control: source-link file boundaries | Low | 95% | Source-link access is constrained to configured roots and uses path normalization; open-with-default-app is gated by setting and root checks. | MemorySmith.App/Services/VarResolver.cs, MemorySmith.Tests/SecurityAndSourceLinkTests.cs |
| F-007 | Positive control: bootstrap token and strong password floor | Low | 94% | First-admin setup enforces bootstrap token path for non-loopback setup and uses strong password minimum, with token hash checked in fixed time compare. | MemorySmith.App/Services/SecurityServices.cs |

## Risk Register

- R-001: Insecure remote exposure due to warning-only posture when remote API is enabled without key.
  - Impact: High
  - Likelihood: Medium
  - Mitigation: Make hardened remote profile enforceable and default-safe, with explicit opt-in bypass and clear diagnostics.
- R-002: Weak transport guarantees if deployed over HTTP with cookie auth.
  - Impact: High
  - Likelihood: Medium
  - Mitigation: Add configurable secure-cookie and HSTS controls, defaulting to safer remote behavior while preserving localhost development.
- R-003: Misclassified client identity behind proxies can undercut loopback/HTTPS assumptions.
  - Impact: Medium
  - Likelihood: Medium
  - Mitigation: Add trusted forwarded-header configuration and tests for proxied request classification.
- R-004: Anonymous setup/login form posts are exposed to browser-mediated cross-site request scenarios.
  - Impact: Medium
  - Likelihood: Low/Medium
  - Mitigation: Add targeted anti-forgery protections and bootstrap hardening options that remain opt-in where needed.

## Open Questions

- Q-001: Should remote hardened mode be strict-blocking by default when AllowRemoteApi is true and ApiKey is empty, or should startup permit with explicit danger acknowledgement?
  - Decision owner: Admin/security governance
  - Due gate: Sprint 1 design sign-off
- Q-002: Should secure-cookie and HSTS enforcement be tied to a single security profile switch or remain separate advanced settings?
  - Decision owner: Platform maintainers
  - Due gate: Sprint 1 implementation kickoff
- Q-003: Should setup/login anti-forgery be enabled always, or profile-gated for compatibility with automation scripts?
  - Decision owner: Auth maintainers
  - Due gate: Sprint 1 planning review

## Assumptions

- The product remains local-first, but may be optionally exposed in controlled remote scenarios.
- Existing behavior that enables local workflows (setup, local editing, local API use) should continue to function by default on localhost.
- Functional regression risk is unacceptable; hardening must be configurable and staged.

## Confidence

- Overall audit confidence: 88%
- Highest confidence: F-001 and F-006
- Lowest confidence: F-004 (depends on browser/request-context threat model and deployment style)
