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

# Create Ollama model with --experimental flag (in older Ollama versions)
result = subprocess.run(
    ["ollama", "create", OLLAMA_MODEL, "-f", modelfile_path, "--experimental"],
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
    print(f"Created {OLLAMA_MODEL} and tagged as {OLLAMA_TAG}")

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
