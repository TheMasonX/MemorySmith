# Sprint Plan - Security Hardening Safe-by-Default (2026-05-23)

## Sprint Objective

Strengthen security guardrails with safe-by-default settings while preserving local-first functionality through explicit, configurable opt-in compatibility controls.

## Capacity Assumptions

- Team size: 1 maintainer with agent-assisted implementation.
- Effective days per sprint: 4.
- Risk buffer: 25% for compatibility validation and policy tuning.

## Sprint 1 - Guardrails First

### Objective

Close highest-risk remote/transport gaps without breaking localhost workflows.

### Committed Items

- TSK-0023 Harden remote API startup/runtime guardrails and require explicit compatibility override for insecure combinations.
- TSK-0037 Add configurable transport hardening baseline (secure cookie policy, HSTS option, remote-safe defaults).
- TSK-0038 Add trusted proxy/forwarded-header configuration for loopback and HTTPS-sensitive checks.

### Stretch Items

- TSK-0039 Add targeted anti-forgery and bootstrap hardening for setup/login form flows.

### Exit Criteria

- Remote insecure combinations are explicitly blocked or require acknowledged compatibility override.
- Transport hardening settings are documented and enabled by default for remote-safe profile.
- Proxy trust behavior is configurable and validated by tests.

### Demo Targets

- Startup/readiness response under secure and insecure remote combinations.
- Auth cookie and transport settings visible in diagnostics/admin guidance.
- Proxy-mode test proving trusted forwarding path and safe fallback.

## Sprint 2 - Assurance and Governance

### Objective

Expand verification coverage and operational guidance for sustained secure operations.

### Committed Items

- TSK-0039 Add targeted anti-forgery and bootstrap hardening for setup/login form flows.
- TSK-0040 Add security regression matrix tests for profile/remote/proxy/auth combinations.
- TSK-0041 Publish operator security posture guidance and profile-runbook page linked to admin diagnostics.

### Stretch Items

- TSK-0024 Add profile preset workflow improvements in admin UX for one-click secure mode.

### Exit Criteria

- Security regression tests cover remote guardrail, transport, proxy, and setup/login hardening expectations.
- Operator documentation clearly states safe-by-default stance and opt-in exceptions.
- No critical/localhost workflow regressions in smoke validation.

### Demo Targets

- Passing security regression matrix run.
- Admin workflow for selecting/understanding secure profile posture.
- Documentation walkthrough showing stance, overrides, and rollout checklist.

## Task Links

- TSK-0023: Existing high-priority guardrail item aligned to F-001.
- TSK-0037: Addresses F-002 transport/cookie posture.
- TSK-0038: Addresses F-003 proxy trust boundary.
- TSK-0039: Addresses F-004 setup/auth CSRF resilience.
- TSK-0040: Addresses validation debt across F-001 to F-004.
- TSK-0041: Addresses operational governance and safe-by-default literature.

## Assumptions

- Safe-by-default applies to non-loopback and shared-environment usage.
- Localhost workflows remain usable without complex setup.
- Backward-compatibility switches are explicit and auditable.

## Confidence

- Sprint sequencing confidence: 86%
- Delivery confidence Sprint 1: 82%
- Delivery confidence Sprint 2: 78%
