# ADR-002: Markdown Is the Source of Truth

## Status

Accepted

## Context

The search index duplicates note content and metadata.

A clear authority boundary is required to prevent divergence.

## Decision

Obsidian Markdown files are authoritative.

Qdrant is disposable derived state.

## Consequences

- Rebuild is always supported.
- Index mutations never become authoritative knowledge.
- Agents do not write knowledge directly into Qdrant.
- Schema/model changes may use rebuild rather than migration during the POC.
