# Local Fine-Tune Harness

MemorySmith includes a local fine-tuning pipeline for training custom Ollama models using chat transcripts and feedback data.

## Overview

The harness enables a data flywheel: chat interactions generate training data, feedback ratings identify high-quality examples, and the fine-tune pipeline produces a specialized model that improves future chat quality.

## Components

### Data Plane (C#)

- **ChatTranscriptWriter** — captures turn metadata (provider, model, timing, tool calls) and optionally content to daily JSONL files. Supports configurable secret redaction (Bearer tokens, API keys, passwords).
- **SqliteChatFeedbackStore** — stores thumbs up/down ratings per turn in a SQLite database. Supports upsert and enumeration by date range.

### Orchestration (C#)

- **TrainingHarnessRunnerService** — manages the Python subprocess lifecycle:
  - Probes dependencies (torch, transformers, trl, peft, optionally unsloth)
  - Detects CUDA availability and device capabilities
  - Launches `harness.py` with request JSON, timeout management, and environment variable injection (HF_TOKEN)
  - Reports live status via the active-run singleton

### Execution (Python)

- **harness.py** — the training engine:
  - **Simulated mode** — runs when CUDA/dependencies are missing; emits synthetic training events and metrics
  - **LoRA/QLoRA mode** — real training with configurable hyperparameters:
    - Batch size (default 4), gradient accumulation (4), max steps (200)
    - 4-bit quantization via BitsAndBytesConfig
    - Gradient checkpointing for memory efficiency
    - Cosine LR schedule with warmup (10 steps)
    - LoRA rank 16, alpha 32, dropout 0.05
  - Loads data from chat transcripts or synthetic starter examples
  - Writes events, status, and benchmarks to the work directory

## Configuration

Settings under `MemorySmith:Training`:

| Setting | Default | Description |
|---|---|---|
| `ChatTranscriptEnabled` | `false` | Enable chat transcript capture |
| `StoreChatContent` | `false` | Store full message content (not just metadata) |
| `TranscriptRedactionEnabled` | `true` | Redact secrets in stored transcripts |
| `TranscriptRetentionDays` | `90` | Auto-delete transcripts older than N days |
| `FeedbackEnabled` | `false` | Enable thumbs up/down feedback UI |
| `MaxRunMinutes` | `360` | Training run timeout |
| `PreferenceFormat` | `FilteredSft` | Export format (FilteredSft, Dpo, Orpo) |

## Getting Started

1. Enable transcript capture: set `Training:ChatTranscriptEnabled=true` and `Training:StoreChatContent=true` in admin settings
2. Use the chat normally — transcripts accumulate in `Data/Events/chat-transcripts/`
3. Rate responses with thumbs up/down to build a feedback signal
4. When ready, visit `/training-workbench` (requires Admin role)
5. Click "Start training run" (or "Start dry run" for a simulated test)

The harness automatically detects whether real training is possible (CUDA + dependencies) and falls back to simulated mode otherwise.

## Requirements for Real Training

- Python 3.11+ with a virtual environment at `.venv` (or configured path)
- PyTorch with CUDA support
- transformers, trl, peft, datasets
- An NVIDIA GPU with 8+ GB VRAM (QLoRA with 4-bit quantization)
- Optional: unsloth for faster training

Use `Scripts/Test-FinetuneHarnessPrereqs.ps1` to check readiness.
