# Request: Model Profile and Ollama Comparator Parity (2026-05-30)

## Request Summary

Before self-review execution loops, ensure the local runtime has a reliable model-comparison baseline:

- a trained-model profile intended to remain the default in chat
- an identical stock `qwen3.5:4b` 24k comparator profile
- Ollama tag parity so both profiles are executable

## Why This Request Exists

Repeated training and A/B sessions were paying setup tax because model profile defaults and local Ollama tags drift across environments (repo run vs published artifacts run). The comparison objective needs deterministic profile wiring and deterministic local tags.

## Evidence

- `MemorySmith.App/Services/ChatModelProfileService.cs` persists model profiles and default profile id to resolved settings override.
- `MemorySmith.App/Services/MemorySmithConfigurationPaths.cs` resolves override path across repo and artifacts surfaces.
- Local Ollama inspection on 2026-05-30 showed stock `qwen3.5:4b` only; no local `memorysmith-athena:latest` tag was present.
- Created local stock comparator tag `qwen3.5:4b-24k-stock` and updated override profile wiring in:
  - `MemorySmith.App/appsettings.LocalOverrides.json`
  - `artifacts/MemorySmith.App/appsettings.LocalOverrides.json`

## Recommendations

### 1. Add automated bootstrap for model profile parity

- Classification: `Now`
- Impact: high
- Effort: medium
- Confidence: 92%
- Recommendation: add a script hook that resolves active settings path, verifies required Ollama tags, creates stock 24k comparator tag when missing, and writes consistent profile/default ids.

### 2. Gate trained-default promotion on local tag presence

- Classification: `Now`
- Impact: high
- Effort: low
- Confidence: 91%
- Recommendation: if trained tag is missing, warn and keep default on an executable stock profile instead of setting a broken default.

### 3. Add one-command verification snapshot

- Classification: `Next`
- Impact: medium
- Effort: low
- Confidence: 89%
- Recommendation: add a script output with `ollama list` summary, resolved settings path, profile ids, default id, and context windows for fast audit evidence.

### 4. Fold parity checks into periodic self-review cadence

- Classification: `Later`
- Impact: medium
- Effort: low
- Confidence: 84%
- Recommendation: include this verification in monthly self-review to avoid recurrence.

## Status

`InProgress`

## Tracking

- Implementation task: `TSK-0253`
- Related request pages:
  - `self-review-and-skill-governance-20260529.md`
  - `skill-small-improvements-batch-20260529.md`
