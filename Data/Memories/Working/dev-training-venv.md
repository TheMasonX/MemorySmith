---
id: dev-training-venv
title: Training Tools Virtual Environment
status: Working
confidence: 0.95
tags: [training, python, venv, setup]
---

# Training Tools Virtual Environment

## Location
- **Venv path:** `D:\temp\memorysmith-training\.venv`
- **Python:** `D:\temp\memorysmith-training\.venv\Scripts\python.exe`
- **Scripts alias:** `$venvPy = "D:\temp\memorysmith-training\.venv\Scripts\python.exe"`

## Why
All Python-based training tools (GGUF conversion, LoRA merging, harness) must use the dedicated training venv to avoid polluting the main Python installation with training-specific dependencies (torch, gguf, safetensors, transformers, etc.).

## Available Tools (in venv)
| Tool | Entry point |
|------|-------------|
| HF → GGUF conversion | `python -m convert_hf_to_gguf` |
| LoRA → GGUF | `python -m convert_lora_to_gguf` |
| GGUF metadata inspection | `python -c "from gguf import GGUFReader; ..."` |
| Model merge | `Scripts/merge_and_deploy_adapter.py` (uses venv torch) |

## Key Packages (pip install in venv)
- `gguf` — GGUF format read/write/quantize
- `torch` — model loading for conversion
- `safetensors` — weight file format
- `transformers` — HF model handling
- `llama-cpp-scripts` — convert_hf_to_gguf and related tools
- `protobuf`, `sentencepiece` — tokenizer support

## Usage
```powershell
# Quick invocation
& "D:\temp\memorysmith-training\.venv\Scripts\python.exe" -m convert_hf_to_gguf ...

# Or with alias
$venvPy = "D:\temp\memorysmith-training\.venv\Scripts\python.exe"
& $venvPy -c "from gguf import GGUFReader; ..."
```

## Notes
- Install new packages with: `& $venvPy -m pip install <package>`
- The `Run-FinetuneHarness.ps1` script already uses `$venvPy` internally
- `merge_and_deploy_adapter.py` should be run with the venv Python
