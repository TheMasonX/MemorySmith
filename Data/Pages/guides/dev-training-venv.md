---
id: dev-training-venv
title: Training Tools Virtual Environment
status: Working
confidence: 0.95
tags: [training, python, venv, setup]
---

# Training Tools Virtual Environment

## Location

Configure the venv path via the `RunsDirectory` / `PythonVenvPath` settings in
`MemorySmith:Training` (see `appsettings.LocalOverrides.json`):

```json
"Training": {
  "PythonVenvPath": "<your-venv-root>\\.venv",
  "RunsDirectory": "<your-venv-root>\\runs"
}
```

A common convention is to keep both under a single training workspace root, e.g.
`C:\Users\<you>\memorysmith-training\` or any drive with sufficient space (~20 GB).

**Quick setup example:**
```powershell
# Create the venv once in your chosen location
python -m venv "<your-venv-root>\.venv"
$venvPy = "<your-venv-root>\.venv\Scripts\python.exe"
& $venvPy -m pip install -r MemorySmith.Training/requirements.txt
```

## Why

All Python-based training tools (GGUF conversion, LoRA merging, harness) must use the
dedicated training venv to avoid polluting the main Python installation with
training-specific dependencies (torch, gguf, safetensors, transformers, etc.).

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
# Invoke with the venv Python directly
$venvPy = (Get-Content artifacts/MemorySmith.App/appsettings.LocalOverrides.json |
    ConvertFrom-Json).MemorySmith.Training.PythonVenvPath + "\Scripts\python.exe"
& $venvPy -m convert_hf_to_gguf ...

# The Run-FinetuneHarness.ps1 and merge_and_deploy_adapter.py scripts
# read PythonVenvPath from appsettings automatically.
```

## Notes

- Install new packages with: `& $venvPy -m pip install <package>`
- The `Run-FinetuneHarness.ps1` script already uses the configured venv path internally
- `merge_and_deploy_adapter.py` should be run with the venv Python
