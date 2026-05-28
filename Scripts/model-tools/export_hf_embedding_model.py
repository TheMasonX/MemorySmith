from __future__ import annotations

import argparse
import json
import shutil
from datetime import datetime, timezone
from pathlib import Path

from huggingface_hub import snapshot_download


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Download a Hugging Face embedding model and export/copy ONNX assets into Data/Models."
    )
    parser.add_argument("--model-id", required=True, help="Hugging Face model id (for example: nomic-ai/nomic-embed-code)")
    parser.add_argument("--models-dir", required=True, help="Target models directory (for example: Data/Models)")
    parser.add_argument("--cache-dir", default=".cache/hf-model-export", help="Local snapshot cache root")
    parser.add_argument("--output-name", default="", help="ONNX output filename inside models-dir; defaults to <repo-name>.onnx")
    parser.add_argument("--download-only", action="store_true", help="Only download snapshot files; skip ONNX export")
    parser.add_argument("--force-redownload", action="store_true", help="Force refreshing the local Hugging Face snapshot")
    parser.add_argument("--trust-remote-code", action="store_true", help="Allow remote model code during optimum export")
    parser.add_argument("--task", default="feature-extraction", help="Optimum export task")
    parser.add_argument("--opset", type=int, default=17, help="ONNX opset for export")
    return parser.parse_args()


def safe_name(model_id: str) -> str:
    return model_id.replace("/", "-").replace("\\", "-")


def find_prebuilt_onnx(snapshot_dir: Path) -> Path | None:
    candidates = [
        snapshot_dir / "onnx" / "model.onnx",
        snapshot_dir / "onnx" / "model_fp16.onnx",
        snapshot_dir / "onnx" / "model_int8.onnx",
        snapshot_dir / "onnx" / "model_quantized.onnx",
        snapshot_dir / "model.onnx",
    ]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return None


def find_exported_onnx(export_dir: Path) -> Path:
    direct = export_dir / "model.onnx"
    if direct.exists():
        return direct

    nested = export_dir / "onnx" / "model.onnx"
    if nested.exists():
        return nested

    matches = sorted(export_dir.rglob("*.onnx"))
    if not matches:
        raise FileNotFoundError(f"No ONNX file produced under {export_dir}")
    return matches[0]


def copy_tokenizer_assets(snapshot_dir: Path, tokenizer_output_dir: Path) -> dict:
    tokenizer_output_dir.mkdir(parents=True, exist_ok=True)
    asset_names = [
        "tokenizer.json",
        "tokenizer_config.json",
        "special_tokens_map.json",
        "vocab.txt",
        "vocab.json",
        "merges.txt",
        "added_tokens.json",
        "config.json",
        "config_sentence_transformers.json",
        "modules.json",
        "sentence_bert_config.json",
        "1_Pooling/config.json",
    ]

    copied: list[str] = []
    for name in asset_names:
        source = snapshot_dir / name
        if source.exists():
            target = tokenizer_output_dir / Path(name)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            copied.append(name)

    return {
        "copiedTokenizerAssets": copied,
        "hasWordPieceVocab": "vocab.txt" in copied,
        "tokenizerDirectory": str(tokenizer_output_dir),
    }


def export_with_optimum(model_source_dir: Path, export_dir: Path, task: str, trust_remote_code: bool, opset: int) -> Path:
    from optimum.exporters.onnx import main_export

    export_dir.mkdir(parents=True, exist_ok=True)
    # Some sentence-transformers repos fail auto library detection in Optimum.
    # Try transformers first, then fall back to Optimum's default auto detection.
    export_errors: list[str] = []
    for library_name in ("transformers", None):
        try:
            main_export(
                model_name_or_path=str(model_source_dir),
                output=str(export_dir),
                task=task,
                trust_remote_code=trust_remote_code,
                opset=opset,
                library_name=library_name,
            )
            break
        except Exception as exc:  # pragma: no cover - exercised in environment-specific export failures.
            export_errors.append(f"library={library_name or 'auto'}: {exc}")
    else:
        raise RuntimeError("Optimum ONNX export failed. " + " | ".join(export_errors))

    return find_exported_onnx(export_dir)


def main() -> int:
    args = parse_args()
    models_dir = Path(args.models_dir).resolve()
    cache_root = Path(args.cache_dir).resolve()
    model_slug = safe_name(args.model_id)
    output_name = args.output_name.strip() or f"{model_slug}.onnx"

    models_dir.mkdir(parents=True, exist_ok=True)
    cache_root.mkdir(parents=True, exist_ok=True)

    snapshot_dir = cache_root / model_slug
    snapshot_download(
        repo_id=args.model_id,
        local_dir=str(snapshot_dir),
        local_dir_use_symlinks=False,
        force_download=args.force_redownload,
        resume_download=not args.force_redownload,
    )

    manifest: dict[str, object] = {
        "modelId": args.model_id,
        "snapshotDirectory": str(snapshot_dir),
        "modelsDirectory": str(models_dir),
        "requestedOutputName": output_name,
        "downloadOnly": bool(args.download_only),
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
    }

    tokenizer_info = copy_tokenizer_assets(snapshot_dir, models_dir / f"{model_slug}-tokenizer")
    manifest.update(tokenizer_info)

    prebuilt = find_prebuilt_onnx(snapshot_dir)
    output_path = models_dir / output_name

    if prebuilt is not None:
        shutil.copy2(prebuilt, output_path)
        manifest["onnxSource"] = "prebuilt"
        manifest["onnxInputPath"] = str(prebuilt)
        manifest["onnxOutputPath"] = str(output_path)
    elif args.download_only:
        manifest["onnxSource"] = "none"
        manifest["warning"] = "No prebuilt ONNX was found in the model repo and --download-only was used."
    else:
        export_dir = cache_root / f"{model_slug}-onnx-export"
        exported = export_with_optimum(
            model_source_dir=snapshot_dir,
            export_dir=export_dir,
            task=args.task,
            trust_remote_code=bool(args.trust_remote_code),
            opset=args.opset,
        )
        shutil.copy2(exported, output_path)
        manifest["onnxSource"] = "exported"
        manifest["onnxInputPath"] = str(exported)
        manifest["onnxOutputPath"] = str(output_path)
        manifest["exportTask"] = args.task
        manifest["exportOpset"] = args.opset

    if not manifest.get("hasWordPieceVocab", False):
        manifest["compatibilityNote"] = (
            "This model snapshot does not include vocab.txt. MemorySmith's current WordPiece tokenizer path "
            "expects vocab.txt, so additional runtime tokenizer support may be required before using this model in-app."
        )

    manifest_path = models_dir / f"{model_slug}.manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    print(json.dumps(manifest, indent=2))
    print(f"Manifest written to: {manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
