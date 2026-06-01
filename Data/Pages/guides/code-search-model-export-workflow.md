# Code Search Model Export Workflow

Use this workflow to download and prepare Hugging Face embedding models for MemorySmith into `Data/Models` with a repo-local Python virtual environment.

## One-command install and export

From the repo root:

```powershell
./Scripts/Install-CodeSearchModel.ps1
```

Default behavior:
- Creates or reuses `.venv-model-export`
- Installs `Scripts/model-tools/requirements-model-export.txt`
- Downloads `nomic-ai/nomic-embed-code`
- Exports or copies ONNX artifacts into `Data/Models`
- Copies tokenizer assets and writes a manifest json

## Common examples

Use a model that already publishes ONNX files:

```powershell
./Scripts/Install-CodeSearchModel.ps1 -ModelId nomic-ai/nomic-embed-text-v1.5 -OutputName nomic-embed-text-v1.5.onnx
```

Force a clean environment and redownload:

```powershell
./Scripts/Install-CodeSearchModel.ps1 -RecreateVenv -ForceRedownload
```

Dry run:

```powershell
./Scripts/Install-CodeSearchModel.ps1 -WhatIf
```

## Notes on runtime compatibility

- The export workflow can convert models to ONNX, but runtime compatibility still depends on MemorySmith tokenizer support.
- Current defaults are WordPiece-oriented (`vocab.txt` expected).
- If a model snapshot does not include `vocab.txt` (for example some Nomic code-model snapshots), the generated manifest includes a compatibility note.

## Output files

Artifacts are written under `Data/Models`, including:
- `<model-slug>.onnx`
- `<model-slug>.manifest.json`
- `<model-slug>-tokenizer/` tokenizer/config assets

## Related tools

- `Scripts/Install-CodeSearchModel.ps1`
- `Scripts/model-tools/export_hf_embedding_model.py`
- `Scripts/model-tools/requirements-model-export.txt`
