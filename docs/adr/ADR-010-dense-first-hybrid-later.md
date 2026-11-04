# ADR-010: Establish a Dense Retrieval Baseline Before Hybrid Search

## Status

Accepted

## Context

Technical notes contain exact identifiers that can benefit from lexical/sparse retrieval, but introducing hybrid search immediately increases complexity before baseline quality is known.

## Decision

POC retrieval uses dense semantic search only.

The architecture must preserve a path to:

- sparse retrieval
- dense + sparse search
- Reciprocal Rank Fusion (RRF)

Hybrid search is evaluated after the dense benchmark is recorded.

Sequencing clarification on 2026-10-30: the recorded 22/24 retrieval baseline
precedes a 20-question answer-quality/agent evaluation using the existing MCP
tools. Hybrid remains conditional on diagnosed retrieval failures and an
explicit comparison with competing improvements; see
[Milestone 9](../19-answer-quality-agent-evaluation-plan.md). The dense-first
decision and stable MCP boundary remain unchanged.

## Consequences

- Faster POC implementation.
- Clear baseline.
- Exact identifiers may underperform initially.
- Hybrid search can be justified with evidence rather than assumption.
