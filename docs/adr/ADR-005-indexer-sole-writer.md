# ADR-005: The Indexer Is the Sole Writer to Qdrant

## Status

Accepted

## Context

Allowing multiple services or agents to mutate Qdrant would permit the derived index to diverge from Markdown.

## Decision

Only the indexer may create, update, or delete knowledge points.

The MCP server is read-only.

## Consequences

- Synchronization logic is centralized.
- Index state remains a deterministic projection of the vault.
- Agent write-back, if ever required, must target the authoritative knowledge source through a separate design.
