#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(dirname "$script_dir")
asset_dir="$repo_root/models/bge-small-en-v1.5"
model_path="$asset_dir/onnx/model.onnx"
revision=5c38ec7c405ec4b44b94cc5a9bb96e735b38267a
expected=828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35

if [ ! -f "$model_path" ]; then
  mkdir -p "$(dirname "$model_path")"
  curl --fail --location --retry 3 \
    "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/$revision/onnx/model.onnx" \
    --output "$model_path.tmp"
  mv "$model_path.tmp" "$model_path"
fi

actual=$(shasum -a 256 "$model_path" | awk '{print $1}')
if [ "$actual" != "$expected" ]; then
  rm -f "$model_path"
  echo "BGE model digest mismatch; expected pinned revision $revision." >&2
  exit 1
fi

echo "Verified BAAI/bge-small-en-v1.5 at revision $revision."
