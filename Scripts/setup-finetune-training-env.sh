#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

if [[ -d /d/temp ]]; then
  DEFAULT_SCRATCH_ROOT="/d/temp/memorysmith-training"
else
  DEFAULT_SCRATCH_ROOT="$REPO_ROOT/artifacts/training-scratch"
fi

SCRATCH_ROOT="${1:-$DEFAULT_SCRATCH_ROOT}"
VENV_PATH="${VENV_PATH:-$SCRATCH_ROOT/.venv}"
OVERRIDE_PATH="${OVERRIDE_PATH:-$REPO_ROOT/artifacts/MemorySmith.App/appsettings.LocalOverrides.json}"
REQUIREMENTS_PATH="$REPO_ROOT/Scripts/training/requirements-training.txt"

mkdir -p "$SCRATCH_ROOT" "$SCRATCH_ROOT/runs" "$SCRATCH_ROOT/hf-home/hub" "$SCRATCH_ROOT/hf-home/datasets" "$SCRATCH_ROOT/torch-home" "$SCRATCH_ROOT/temp"

export HF_HOME="$SCRATCH_ROOT/hf-home"
export HF_HUB_CACHE="$SCRATCH_ROOT/hf-home/hub"
export TRANSFORMERS_CACHE="$SCRATCH_ROOT/hf-home/hub"
export HF_DATASETS_CACHE="$SCRATCH_ROOT/hf-home/datasets"
export TORCH_HOME="$SCRATCH_ROOT/torch-home"
export TMPDIR="$SCRATCH_ROOT/temp"

if command -v python3.12 >/dev/null 2>&1; then
  PYTHON_BIN="$(command -v python3.12)"
elif command -v python3.11 >/dev/null 2>&1; then
  PYTHON_BIN="$(command -v python3.11)"
else
  echo "Python 3.12 or 3.11 is required for the fine-tune environment bootstrap." >&2
  exit 1
fi

if [[ ! -d "$VENV_PATH" ]]; then
  "$PYTHON_BIN" -m venv "$VENV_PATH"
fi

VENV_PYTHON="$VENV_PATH/bin/python"
"$VENV_PYTHON" -m pip install --upgrade pip setuptools wheel
"$VENV_PYTHON" -m pip install torch torchvision --index-url https://download.pytorch.org/whl/cu128
"$VENV_PYTHON" -m pip install -r "$REQUIREMENTS_PATH"

mkdir -p "$(dirname "$OVERRIDE_PATH")"
cat > "$OVERRIDE_PATH" <<JSON
{
  "MemorySmith": {
    "Training": {
      "PythonVenvPath": "$VENV_PATH",
      "RunsDirectory": "$SCRATCH_ROOT/runs",
      "PythonHarnessScript": "MemorySmith.Training/harness.py",
      "TrainingDataExportPath": "../Data/Training/exports",
      "TranscriptDirectory": "../Data/Events/chat-transcripts"
    }
  }
}
JSON

pwsh "$REPO_ROOT/Scripts/Test-FinetuneHarnessPrereqs.ps1" -PythonVenvPath "$VENV_PATH"

echo "Training environment ready."
echo "Scratch root:        $SCRATCH_ROOT"
echo "Training venv:       $VENV_PATH"
echo "Local override file: $OVERRIDE_PATH"