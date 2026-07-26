# MemorySmith delta audit — GitHub provider + argument-shape slice

**Report ID:** `ms-c11573019e3d960d`  
**Snapshot:** `fb9f8311b72a9c20354f6eb17580d582331eeef8`

## Executive summary

This slice adds two new findings around the GitHub Copilot provider implementation. First, the GitHub provider now contains a third copy of the same permissive tool-argument normalization pattern already present in the chat catalog and Ollama parser paths. Second, the provider capability metadata says native tool calls are supported, but the implementation is still relying on reflection-based best-effort attachment plus envelope normalization fallbacks, which is a weaker contract than the capability label suggests. citeturn238file0turn231file0turn227file0turn234file0

The current seam is therefore not just “provider adapter code.” It is a cluster of repeated parsing and compatibility patterns that still need a single source of truth.

## Findings

| ID | Severity | Confidence | Issue | Why it matters | Evidence |
|---|---:|---:|---|---|---|
| D-047 | Medium | 88% | **Type-4 duplication in tool-argument normalization** — `ReadGitHubToolArguments()` repeats the same JSON-object / stringified-JSON / fallback-to-`input` shape already used in `ParseOllamaArguments()` and `ReadArguments()`. | Three separate implementations now interpret malformed tool payloads in the same broad way. That is semantic duplication and a future drift risk if one path changes its fallback semantics. | citeturn238file0turn231file0turn217file0 |
| D-048 | Medium | 86% | **Capability overstatement / speculative generality** — `GitHubCopilotChatProvider.Capabilities` advertises `SupportsNativeToolCalls: true` even though native tool attachment is a reflection-based best-effort shim and tool-call handling still normalizes into fallback envelopes. | The capability label is stronger than the actual contract. Downstream selection logic may assume stable native tool support where the implementation is still compatibility-driven. | citeturn236file0turn238file0 |
| D-049 | Low | 80% | **Implicit prompt-role normalization contract** — `NormalizeGitHubPromptRole()` simply lowercases whatever role string is present, defaulting blanks to `user`, rather than validating a known role vocabulary. | This is okay if the GitHub SDK is lenient, but it is another place where unknown/novel roles can leak through as-is. That makes prompt semantics depend on upstream tolerance rather than an explicit local contract. | citeturn238file0 |

## Detailed notes

### D-047 — Another copy of the permissive argument parser
`ReadGitHubToolArguments()` accepts `Arguments`, `ToolArguments`, `Parameters`, or `Input`; if the value is a string, it attempts to parse JSON and falls back to `{ "input": rawText }`; if it is some other object shape, it serializes and re-parses before finally falling back to stringifying the raw value. This is the same “best effort argument rehydration” pattern already seen in the Ollama parser and the chat catalog parser. citeturn238file0turn231file0turn217file0

**Fix:** move the normalization into one shared helper and make the allowed fallbacks explicit per transport.  
**Confidence:** 88%

### D-048 — The capability metadata says more than the implementation can guarantee
`Capabilities` advertises native tool-call support, but the implementation still has a reflection-based `TryAttachGitHubNativeTools()` shim and a fallback path that normalizes native tool call events into MemorySmith envelopes. That means “native tool calls supported” is true only in a loose compatibility sense, not as a stable contract. citeturn236file0turn238file0

**Fix:** either narrow the capability label to reflect the best-effort nature of the integration, or move the fallback/compatibility behavior behind a more explicit adapter boundary.  
**Confidence:** 86%

### D-049 — Prompt-role normalization is permissive rather than validating
`NormalizeGitHubPromptRole()` lowercases arbitrary role strings and uses `user` only when blank. If the upstream SDK or any future provider starts using a more constrained vocabulary, this helper will not catch unexpected roles; it will just pass them through in lower-case form. That is acceptable as a convenience path, but it is still an implicit contract. citeturn238file0

**Fix:** validate against a known set of prompt roles or document that the provider intentionally preserves unknown roles.  
**Confidence:** 80%

## Task mapping and backlog fit

`TSK-0283` remains the main provider-contract task, and D-048 should be folded into it as a scope correction: the capability metadata needs to match the actual contract shape. citeturn210file0turn236file0turn238file0

`TSK-0192` still owns the broader tool-surface modularization, and D-047 strengthens the case that schema/argument normalization helpers should be extracted into shared utilities rather than repeated in provider-specific code. citeturn235file0turn227file0turn231file0turn238file0

## Implementation guidance

1. Extract a shared argument-normalization helper for tool-call payloads.
2. Reword the GitHub provider capability metadata to match the best-effort native-tool behavior, or strengthen the adapter until the label is fully true.
3. Validate or explicitly document allowed prompt roles. citeturn238file0turn236file0turn231file0turn217file0

## Assumptions and open questions

- Assumption: the GitHub provider’s best-effort native tool support is intended as compatibility glue, not a long-term contract. citeturn236file0turn238file0
- Assumption: the permissive argument parsing is meant to improve resilience against model/provider variance. citeturn231file0turn217file0turn238file0
- Open question: should capability metadata describe the implementation as “native-tool aware” or “native-tool supported”? The current label implies more than the shim can guarantee. citeturn236file0turn238file0
- Open question: should prompt roles be validated locally, or is the provider expected to tolerate arbitrary lower-cased roles? citeturn238file0

## Confidence notes

- D-047: 88%
- D-048: 86%
- D-049: 80% citeturn238file0turn231file0turn217file0turn236file0turn227file0turn234file0

**Report ID for follow-up references:** `ms-c11573019e3d960d`
