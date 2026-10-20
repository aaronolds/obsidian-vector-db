# ADR-009: Reindex Whole Documents When They Change

## Status

Accepted

## Context

Chunk-level diffing could minimize embedding work but complicates synchronization and failure handling.

Obsidian-scale documents are expected to be small enough that document-level reindexing is practical.

## Decision

When a document content hash changes:

1. delete existing indexed chunks for the document
2. parse and rechunk the current document
3. regenerate embeddings for all current chunks
4. upsert the replacement points

Unchanged documents are skipped.

## Consequences

- Simpler and safer synchronization.
- Some redundant embedding work after small edits.
- Chunk-level diffing remains a future optimization.
