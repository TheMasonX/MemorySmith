# Council Decision: Audit #9 Implementation Track — Ship Review & Next-Task Ordering

**Date:** 2026-06-12
**Decision:** Ship TSK-0042 step 1 (unified chat tool loop) as staged; the proposed next-task ordering (TSK-0042 step 2 → TSK-0189 → TSK-0283 → TSK-0284, TSK-0280 opportunistic) is correct given the confirmed step-2 split scope.

**Scope of impact:** long-lived architecture + chat/agent execution path (the tool loop is the write-path's engine).
**Seats:** Source-Grounded Archivist, Skeptical Reviewer, Synthesizer (3 of 6 — architecture refactor, no schema/retrieval-ranking change).

## Evidence Reviewed

- `MemorySmith.App/Services/MemoryChatAgent.ToolLoop.cs` (new — `RunToolLoopAsync`, the single loop driver)
- `MemorySmith.App/Services/ChatServices.cs` (StreamAsync now a thin event consumer; old inline loop and `CompleteWithToolCallsAsync` body removed; file 3,996 → 3,744 lines)
- `MemorySmith.Tests/ChatToolLoopParityTests.cs` (5 tests)
- `MemorySmith.App/Services/AgentSessions/AgentSessionService.cs` (timeout/cancellation interaction)
- `Data/Pages/audits/hyperagent-audit-9-architecture-deepening-20260611.md` (candidate 3)
- `Data/Tasks/tsk-0042-*.json`, `tsk-0248-*.json`
- Local suite: 513 passed / 0 failed / 10 skipped before corrections; TSK-0282 already CI-green on master.

## Findings

| Seat | Recommendation | Confidence | Blocking Concern |
|------|---------------|------------|------------------|
| Source-Grounded Archivist | Ship as-is. Single-loop claim, streaming-surface preservation (prefix buffering, traces with context merge, run-control strings, iteration-limit message), message-append order, usage-aggregation order, and log event ids all verified against source. Stale items: audit §3 line citations; TSK-0042 status. | 94% | None |
| Skeptical Reviewer | Ship; no blocker. Exception/cancellation paths verified safe through the iterator into AgentSessionService's TimeoutPhase guard; no source-inspection test reads ChatServices.cs. MEDIUM: parity-test gaps (run-control non-effect on SendAsync, multi-iteration usage parity, prefix-buffer path unexercised). LOW: ordering question — confirm step-2 split keeps provider-acquisition arms in ToolLoop.cs. | 72% | None |
| Synthesizer | Ship now; close coverage gaps no later than step-2 PR; gate step 2 on the acquisition-arms check. | 89% | None |

## Synthesis

**Shipped now (corrections applied same session, ahead of the synthesizer's deferral allowance):**
- The three coverage gaps were closed immediately: `SendAsync_IgnoresRunControlStop_ByDesign` (pins the documented asymmetry), a two-iteration reply-parity test, and `Stream_BuffersToolCallEnvelope_AndFlushesPlainAnswerDeltas` (multi-chunk `ChunkedScriptProvider` exercising the `IsPotentialToolCallPrefix` path).
- TSK-0042 → InProgress (step 1 done); audit §3 citation note added.

**Material finding from the corrections:** writing the usage-parity assertion exposed a **pre-existing divergence** — on multi-iteration turns with no provider-reported usage, the streaming path constructs each iteration's response with `finalChunk?.Usage ?? currentUsage`, feeding the prior aggregate back into the estimator, while SendAsync estimates from scratch. Measured on an identical two-tool-call script: stream 17,756 input / 100 output tokens vs send 13,992 / 59. The unification faithfully preserves both old behaviors; **fixing the estimator is TSK-0248's scope** (evidence recorded there). A behavior-preserving refactor must not change it silently — this is the Branch B/D pattern: measurable divergence documented, fix deferred to the owning task with the measurement attached.

**Deferred:**
- TSK-0042 step 2 (file split: protocol records → ChatProtocol.cs; each provider → own file). Gate below.
- TSK-0189 (chat turn engine) after step 2; TSK-0283 (provider seam) after step 2; TSK-0284 thereafter; TSK-0280 anytime.
- TSK-0248 now carries the usage-divergence evidence and owns the estimator fix.

**Evidence gates:**
- Step-2 PR review MUST confirm the provider-acquisition arms (`provider.StreamAsync` accumulation and `provider.CompleteAsync` branches) remain in `MemoryChatAgent.ToolLoop.cs`. If a split design would move them, do TSK-0283 first (provider code then moves once).
- TSK-0283 does not begin before step 2 is merged and CI-green.

## Dissent

Archivist (94%) vs Skeptic (72%) — **not resolved by consensus.** The gap reflects risk tolerance around test-coverage gaps rather than any identified defect. The Skeptic's position is recorded: the parity suite as first written could not catch drift in run-control asymmetry, multi-iteration usage, or prefix buffering. The same-session corrections closed those gaps, and the usage assertion immediately found a real divergence — vindicating the dissent's premise that the gaps were load-bearing. The estimator divergence itself remains open (deferred to TSK-0248, not flattened into this ship decision).

## Acceptance Criteria

- [x] Single loop driver: no iteration/parse/execute logic outside `RunToolLoopAsync` (archivist-verified).
- [x] Streaming surface byte-preserved: status strings, trace events with context merge, iteration-limit message (archivist-verified).
- [x] Run-control stop checks gated to the streaming entry point; pinned by `SendAsync_IgnoresRunControlStop_ByDesign`.
- [x] Prefix-buffer path exercised by a multi-chunk streaming test.
- [x] Full local suite green after corrections (516 passed / 0 failed / 10 skipped expected — 3 net new tests).
- [x] Usage divergence measured and recorded on TSK-0248; not silently changed.
- [ ] CI green on the pushed commit (verified post-push).
- [ ] Step-2 PR confirms acquisition arms unmoved (gate, next session).

## Open Questions

- Should the estimator unify on the send-path semantics (estimate-from-scratch per iteration) or carry provider-reported partials? (owner: TSK-0248; gate: decide before changing either path.)
- Does TSK-0189's turn-engine extraction want the loop's event vocabulary (`ToolLoopEvent`) promoted from private to internal for reuse? (owner: TSK-0189 grilling; gate: design discussion, not now.)
