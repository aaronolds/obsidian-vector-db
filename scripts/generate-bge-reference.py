#!/usr/bin/env python3
"""Generate a synthetic CPU reference fixture from the pinned BGE PyTorch checkpoint."""
import argparse
import hashlib
import json
import os
import shutil
import tempfile
from pathlib import Path

import torch
import transformers
from transformers import AutoModel, AutoTokenizer

REVISION = "5c38ec7c405ec4b44b94cc5a9bb96e735b38267a"
WEIGHTS_SHA256 = "3c9f31665447c8911517620762200d2245a2518d6e7208acc78cd9db317e21ad"
TEXTS = [
    "Title: Synthetic note\nSection: Test\n\nLocal model inference stays on this workstation.",
    "café 東京 X-Azure-FDID",
    "Code: vector = await EmbedAsync(text);",
]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets", type=Path, default=Path("models/bge-small-en-v1.5"))
    parser.add_argument("--weights", type=Path, required=True,
                        help="Pinned upstream model.safetensors (not committed).")
    parser.add_argument("--output", type=Path,
                        default=Path("tests/Knowledge.Embeddings.IntegrationTests/TestData/bge-reference.json"))
    args = parser.parse_args()

    digest = hashlib.sha256(args.weights.read_bytes()).hexdigest()
    if digest != WEIGHTS_SHA256:
        raise SystemExit("PyTorch checkpoint digest does not match the pinned model revision.")

    with tempfile.TemporaryDirectory(prefix="bge-reference-") as temporary:
        model_dir = Path(temporary)
        for name in ("config.json", "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json"):
            shutil.copy2(args.assets / name, model_dir / name)
        os.symlink(args.weights.resolve(), model_dir / "model.safetensors")

        tokenizer = AutoTokenizer.from_pretrained(model_dir, local_files_only=True)
        model = AutoModel.from_pretrained(model_dir, local_files_only=True).eval()
        fixture = {
            "model": "BAAI/bge-small-en-v1.5",
            "revision": REVISION,
            "reference": {
                "transformers": transformers.__version__,
                "torch": torch.__version__,
                "tokenizers": __import__("tokenizers").__version__,
                "device": "cpu",
                "pooling": "last_hidden_state[:, 0]",
                "normalization": "torch.nn.functional.normalize(p=2, dim=1)",
                "weightsSha256": digest,
            },
            "cases": [],
        }
        for text in TEXTS:
            encoded = tokenizer(text, return_tensors="pt", truncation=False)
            with torch.inference_mode():
                hidden = model(**encoded).last_hidden_state
                vector = torch.nn.functional.normalize(hidden[:, 0], p=2, dim=1)[0]
            fixture["cases"].append({
                "text": text,
                "inputIds": encoded["input_ids"][0].tolist(),
                "vector": vector.tolist(),
            })
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(fixture, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
