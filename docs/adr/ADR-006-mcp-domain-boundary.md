# ADR-006: Expose Knowledge Through MCP Domain Operations

## Status

Accepted

## Context

AI clients need retrieval but should not depend on Qdrant or embedding mechanics.

## Decision

Expose domain-oriented MCP tools such as:

- `search_knowledge`
- `get_note`

Do not expose:

- `query_qdrant`
- `generate_embedding`
- `search_vector`
- vector dimensions
- ranking internals

## Consequences

The retrieval implementation can evolve from dense to hybrid, reranked, or graph-assisted search without changing client behavior.
