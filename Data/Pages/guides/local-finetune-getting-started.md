# Local Fine-Tune Getting Started Guide

This guide walks through setting up and running MemorySmith's local fine-tuning pipeline from scratch.

## Prerequisites

- MemorySmith running locally with admin access
- Python 3.11+ installed
- An NVIDIA GPU with 8+ GB VRAM (optional — simulated mode works without GPU)

## Step 1: Set Up the Python Environment

```powershell
# From the repo root
python -m venv .venv
.venv/Scripts/Activate.ps1  # Windows
# or: source .venv/bin/activate  # Linux/macOS

pip install torch transformers datasets trl peft
# Optional for faster training:
pip install unsloth
```

Or use the automated script:
```powershell
./Scripts/Test-FinetuneHarnessPrereqs.ps1
```

## Step 2: Enable Transcript Capture

In `/admin` settings (or `appsettings.LocalOverrides.json`):

```json
{
  "MemorySmith": {
    "Training": {
      "ChatTranscriptEnabled": true,
      "StoreChatContent": true,
      "FeedbackEnabled": true,
      "TranscriptRedactionEnabled": true
    }
  }
}
```

## Step 3: Generate Training Data

Use the chat normally. Every conversation turn generates:
- **Metadata** (`{date}.jsonl`) — provider, model, timing, tool calls, content hash
- **Content** (`{date}.content.jsonl`) — full user/assistant messages (if `StoreChatContent=true`)

Rate good responses with thumbs up, bad ones with thumbs down.

## Step 4: Run a Dry Run

Visit `/training-workbench` and click **Start dry run**. This validates:
- Python environment and dependencies
- CUDA availability
- Training data format
- Export pipeline

Review the output in the run history section.

## Step 5: Run Real Training

Click **Start training run**. The harness will:
1. Export training data from transcripts
2. Load the base model (default: Qwen/Qwen3.5-4B)
3. Apply QLoRA with 4-bit quantization
4. Train for up to 200 steps with cosine LR schedule
5. Save the LoRA adapter to the work directory

Expected time: 15-25 minutes for ~375 examples on an 8GB GPU.

## Step 6: Use the Fine-Tuned Model

After training, the LoRA adapter is saved in `runs/{run-id}/`. To use it with Ollama:

1. Create a Modelfile pointing to the adapter
2. Build the Ollama model: `ollama create athena -f Modelfile`
3. Set the model in MemorySmith admin settings

## Troubleshooting

| Issue | Solution |
|---|---|
| "Simulated mode" warning | Install CUDA-compatible PyTorch and ensure GPU is detected |
| "Missing core deps" | Install: `pip install torch transformers datasets trl peft` |
| Training timeout | Increase `Training:MaxRunMinutes` (default 360) |
| Low quality results | Add more training data, increase epochs, adjust learning rate |
| OOM during training | Reduce batch size or enable gradient checkpointing (default on) |
