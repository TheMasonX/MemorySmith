# Fine-Tuning Critical Review And Tasked Plan (2026-05-28)

## Scope
Reviewed artifacts:
- audits/vector-deepdive-5
- audits/local-finetuning
- audits/local-finetuning-ux-notes
- artifacts/hyperagent-local-llm-finetuning.gz (ingested and reviewed)

## Verification Snapshot
- Tests are passing.
- Command: dotnet test MemorySmith.Tests --no-build -- NUnit.DefaultTimeout=60000
- Result: total 414, failed 0, passed 412, skipped 2 (CUDA-unavailable skips expected on this host).

## Critical Findings (Pre-Implementation)

### High severity
1. Clipboard external URL auto-fetch on paste requires an explicit toggle and confirm prompt to prevent unexpected network side effects.
2. Chat anchor filtering rewrites href but does not sanitize all anchor attributes (for example onclick), leaving residual script-surface risk if generic attributes are accepted.
3. Mermaid SVG insertion path requires hardening controls (version pin + sanitize) before trust elevation.
4. CSP baseline is absent and should be added for defense in depth.

### Medium severity
1. Transcript secret redaction patterns are narrow and may miss real-world credentials.
2. Diagnostics snapshot should support stricter masking in hardened profile.
3. Chat local history retention needs explicit storage-budget eviction contract.

### Fine-tuning architecture findings
1. local-finetuning.md provides a strong staged harness for SFT + preference tuning, eval gates, and promotion discipline.
2. local-finetuning-ux-notes.md identifies high-ROI operator UX for context-window control, VRAM estimates, and in-app training visibility.
3. Current chat transcript + feedback persistence is insufficient as a training-data foundation and should be solved first.
4. The provided hyperagent scaffold is implementation-oriented and materially supports the plan with concrete integration contracts: `MemoryType` enum model (`MemorySmith.Core/Models/MemoryType.cs`), transcript/feedback service contracts, Ollama `num_ctx` patch guidance, migration files, and training harness scripts.

## Artifact Status
- `artifacts/hyperagent-local-llm-finetuning.gz` was provided, decompressed, and reviewed.
- Payload structure verified under `artifacts/hyperagent-local-llm-finetuning/scaffold`.
- Former blocker task `TSK-0207` is now resolved.

## Tasked Plan
Created task records:
- TSK-0201: Add chat transcript and feedback data plane for fine-tuning
- TSK-0202: Send num_ctx and add context-window governance for Ollama chat
- TSK-0203: Build Python training harness and .NET bridge contract
- TSK-0204: Add fine-tuning eval gates and promotion rollback workflow
- TSK-0205: Harden chat and markdown security toggles before training rollout
- TSK-0206: Add admin training workbench with live run telemetry
- TSK-0207: Ingest and review hyperagent fine-tuning artifact (completed)

## Assumptions
- Primary deployment remains local-first single-user workstation.
- CUDA may be unavailable on some hosts; training/eval flows must degrade safely.
- Existing tool-call envelope remains authoritative until a versioned migration is approved.

## Open Questions
1. Should hardened profile disable Mermaid by default or keep with sanitizer-only hardening?
2. Should diagnostics split into operator and public-safe payload variants?

## Decision Log
- Decision 2026-05-28: Memory taxonomy will ship enum-first (`MemoryType`) with optional `SubType` string for extensibility. Rationale: strong type safety, clearer eval targets, and alignment with the ingested hyperagent scaffold contracts.

## Immediate Priority
- Start implementation with TSK-0201 and TSK-0202, then gate model promotion on TSK-0204.
