# OpenTelemetry Local-First V1 Plan (MemorySmith)

## Summary

This plan delivers a local-first OpenTelemetry implementation that complements existing Serilog + Windows Event Log observability. It focuses on safe-by-default telemetry with explicit controls for performance and data exposure.

## Goals

1. Add OpenTelemetry tracing and metrics with optional OTLP export for local collector use.
2. Keep overhead bounded via sampling, path filters, and low-cardinality tags.
3. Avoid sensitive data capture (no raw prompts, no attachment content, no API secrets, no full payload logs in OTel attributes).
4. Keep all telemetry controls configurable in existing admin settings.
5. Preserve current diagnostics and health workflows while adding OTel readiness visibility.

## Non-Goals (V1)

1. Full custom dashboard builder engine.
2. External SaaS telemetry dependency.
3. High-cardinality per-user/per-record telemetry dimensions.

## Architecture (V1)

1. App instrumentation:
- ASP.NET Core request instrumentation
- HttpClient instrumentation
- Runtime metrics instrumentation
- Custom ActivitySource + Meter for MemorySmith domain operations

2. Export path:
- Default: local-only instrumentation with exporter disabled
- Optional: OTLP exporter enabled to local collector endpoint

3. Existing observability:
- Keep Serilog sinks and diagnostics log APIs as-is
- Add telemetry config/health surfacing for operator visibility

## Guardrails

1. Performance:
- Parent-based sampling with configurable percentage (default low)
- Exclude low-value noisy endpoints (health, diagnostics, static assets)
- Exporter disabled by default

2. Privacy:
- Do not record query text, attachment content, auth headers, tokens, or API keys as attributes
- Use operation-level tags only (operation name/category, success, slow-path)
- Keep dimensions bounded

3. Operability:
- Admin-configurable toggles and endpoints/protocol settings
- Clear diagnostics visibility of effective telemetry config

## Deliverables

1. Config model and appsettings defaults for telemetry.
2. Admin settings descriptors for telemetry controls.
3. OpenTelemetry package wiring and startup registration.
4. MemoryApplicationService low-cardinality instrumentation.
5. Validation and council-review remediation pass.

## Acceptance Criteria

1. Build/tests pass with telemetry enabled defaults.
2. Telemetry exporter can be toggled without code changes.
3. No raw sensitive request content is added to telemetry attributes.
4. Request and core memory-operation latency/error telemetry is observable via OTel.
5. Path filtering and sampling controls are effective and admin-editable.

## Risks and Mitigations

1. Risk: Cardinality explosion.
- Mitigation: fixed operation tags, no freeform text attributes.

2. Risk: Extra CPU/network overhead.
- Mitigation: low default sampling, exporter off by default, filtered paths.

3. Risk: accidental sensitive data capture.
- Mitigation: no payload attributes, no query text tags, no body capture.

## Confidence

88%

## Open Questions

1. Should V1 include a direct Prometheus scrape endpoint, or stay OTLP-to-collector only?
2. Should chat provider/model be included as a bounded dimension for selected operation metrics?
