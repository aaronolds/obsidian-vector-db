# ADR-008: Local-First Security and No Content Egress by Default

## Status

Accepted

## Context

The vault may contain personal, technical, or company-sensitive information.

## Decision

The POC runs:

- vault access locally
- indexing locally
- embedding generation locally
- Qdrant locally
- MCP locally

No cloud embedding fallback is permitted by default.

## Consequences

- Better privacy boundary.
- Simpler POC security posture.
- Remote/shared deployment requires a separate security architecture.
