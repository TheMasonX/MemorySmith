"""Merge LoRA adapter weights into base model and deploy to Ollama."""
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

RUN_ID = "v6-toolselection-v3-consolidated"
BASE_MODEL = "Qwen/Qwen3.5-4B"
ADAPTER_PATH = f"D:/temp/memorysmith-training/runs/{RUN_ID}/adapter"
OUTPUT_PATH = f"D:/temp/memorysmith-training/runs/{RUN_ID}/merged"
OLLAMA_MODEL = "memorysmith-athena:latest"
# Also keep a versioned tag
OLLAMA_TAG = f"memorysmith-athena:{RUN_ID}"

print(f"Step 1: Merging LoRA adapter from {ADAPTER_PATH} into {BASE_MODEL}")
print(f"Output: {OUTPUT_PATH}")

# Create output directory
os.makedirs(OUTPUT_PATH, exist_ok=True)

# Step 1: Merge adapter into base model
import torch
from transformers import AutoModelForCausalLM, AutoTokenizer
from peft import PeftModel

print("Loading base model...")
base = AutoModelForCausalLM.from_pretrained(
    BASE_MODEL,
    torch_dtype=torch.bfloat16,
    device_map="cpu",
    trust_remote_code=True,
)

print("Loading adapter...")
model = PeftModel.from_pretrained(base, ADAPTER_PATH)

print("Merging weights...")
merged = model.merge_and_unload()

print("Saving merged model...")
merged.save_pretrained(OUTPUT_PATH, safe_serialization=True)

# Save tokenizer
tokenizer = AutoTokenizer.from_pretrained(BASE_MODEL, trust_remote_code=True)
tokenizer.save_pretrained(OUTPUT_PATH)

# Also save config
config = merged.config
with open(os.path.join(OUTPUT_PATH, "config.json"), "w") as f:
    json.dump(config.to_dict(), f, indent=2)

print(f"Merged model saved to {OUTPUT_PATH}")
print(f"Total files: {len(os.listdir(OUTPUT_PATH))}")


# Step 2: Create Ollama model from merged weights
print("\nStep 2: Creating Ollama model...")

# Create Modelfile for the merged model
# Load the system prompt from the canonical prompt file
prompt_file = "MemorySmith.Core/Docs/Prompts/wiki-chat-agent.modelfile"
with open(prompt_file, "r", encoding="utf-8") as f:
    original_modelfile = f.read()

# Replace FROM line to point at merged model directory
merged_modelfile = original_modelfile.replace("FROM qwen3.5", f"FROM {OUTPUT_PATH}")

# Write modified modelfile
modelfile_path = os.path.join(OUTPUT_PATH, "Modelfile")
with open(modelfile_path, "w", encoding="utf-8") as f:
    f.write(merged_modelfile)

print(f"Modelfile written to {modelfile_path}")
print(f"FROM {OUTPUT_PATH}")
print(f"Total lines: {len(merged_modelfile.splitlines())}")

# Quantization level: q4_K_M (good quality, ~4.8 GB for 4B model)
# Alternatives: q5_K_M (~5.5 GB, higher quality), q8_0 (~8 GB, near lossless)
QUANTIZE_LEVEL = os.environ.get("OLLAMA_QUANTIZE", "q4_K_M")

# Create Ollama model with quantization
# Without -q, the model stays in FP16/BF16 (~8.4 GB for 4B) which may not fit in VRAM
result = subprocess.run(
    ["ollama", "create", OLLAMA_MODEL, "-f", modelfile_path, "--experimental", "-q", QUANTIZE_LEVEL],
    capture_output=True, text=True, cwd=OUTPUT_PATH
)
print(f"Ollama create stdout: {result.stdout}")
print(f"Ollama create stderr: {result.stderr}")
print(f"Ollama create exit: {result.returncode}")

if result.returncode == 0:
    # Also create a versioned tag
    subprocess.run(
        ["ollama", "cp", OLLAMA_MODEL, OLLAMA_TAG],
        capture_output=True
    )
    print(f"Created {OLLAMA_MODEL} (quantized {QUANTIZE_LEVEL}) and tagged as {OLLAMA_TAG}")

    # Also tag with quantization level for clarity
    quant_tag = f"{OLLAMA_TAG}-{QUANTIZE_LEVEL}"
    subprocess.run(
        ["ollama", "cp", OLLAMA_MODEL, quant_tag],
        capture_output=True
    )
    print(f"Also tagged as {quant_tag}")

    # Patch config blob to add renderer/parser for tool support
    # (Ollama create from local GGUF doesn't auto-detect these)
    import hashlib, json
    manifest_path = os.path.expanduser(
        f"~/.ollama/models/manifests/registry.ollama.ai/library/{OLLAMA_MODEL.replace(':', '/').replace('@', '/')}"
    )
    # Try alternate path format
    alt_manifest_path = manifest_path.replace("registry.ollama.ai", "registry.ollama.ai/library")
    if os.path.exists(manifest_path):
        mp = manifest_path
    elif os.path.exists(alt_manifest_path):
        mp = alt_manifest_path
    else:
        mp = None
        print("Warning: could not find manifest to patch tool support")

    if mp:
        with open(mp) as f:
            manifest = json.load(f)
        config_digest = manifest["config"]["digest"].replace("sha256:", "")
        blob_dir = os.path.expanduser("~/.ollama/models/blobs")
        config_path = os.path.join(blob_dir, f"sha256-{config_digest}")

        with open(config_path) as f:
            config = json.load(f)

        config["renderer"] = "qwen3.5"
        config["parser"] = "qwen3.5"

        new_content = json.dumps(config, separators=(",", ":"))
        new_digest = hashlib.sha256(new_content.encode()).hexdigest()
        new_blob_path = os.path.join(blob_dir, f"sha256-{new_digest}")

        with open(new_blob_path, "w") as f:
            f.write(new_content)

        manifest["config"]["digest"] = f"sha256:{new_digest}"
        with open(mp, "w") as f:
            json.dump(manifest, f, indent=2)

        print(f"Patched config blob {config_digest[:12]} -> {new_digest[:12]} (added renderer/parser)")

    # Verify
    result = subprocess.run(
        ["ollama", "list"],
        capture_output=True, text=True
    )
    print("\nOllama models:")
    for line in result.stdout.splitlines():
        if "memorysmith" in line:
            print(f"  {line}")

print("\nDone!")
