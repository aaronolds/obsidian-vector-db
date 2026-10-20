# ADR-004: Use Structure-Aware Markdown Chunking

## Status

Accepted

## Context

Blind fixed-token chunking can split logical sections and remove context.

Obsidian Markdown already contains semantic structure.

## Decision

Chunk primarily by heading hierarchy and semantic Markdown blocks.

Target approximately 250 to 350 model tokens with a soft maximum around 400 tokens, while always respecting the model hard limit.

Embedding text includes:

- note title
- full heading hierarchy
- tags
- normalized chunk content

Use no overlap by default.

Use small overlap only when oversized prose must be split.

## Consequences

- Chunker is more complex than fixed-size slicing.
- Retrieved chunks should be more coherent.
- Chunking must understand code blocks, lists, tables, and blockquotes.
