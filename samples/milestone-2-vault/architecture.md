---
title: Local Embedding Flow
tags: [architecture, embeddings]
aliases: [Vector generation]
---
# Inference

The local indexer converts a note section into a normalized vector on the workstation.
The vector preserves semantic meaning while the source Markdown remains authoritative.

## Model input

The tokenizer adds [CLS] and [SEP] around text. Context, including tags, consumes the same 512-token model window as the body.

