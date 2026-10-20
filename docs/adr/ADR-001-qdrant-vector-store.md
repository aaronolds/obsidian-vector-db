# ADR-001: Use Qdrant as the Vector Store

## Status

Accepted

## Context

The system needs local vector search over Markdown chunks, metadata filtering, a path to hybrid retrieval, and a clean service boundary.

Alternatives considered:

- sqlite-vec
- Qdrant

## Decision

Use Qdrant as the vector/search database.

Run it locally in Docker for the POC.

## Rationale

- First-class vector database.
- Clean .NET application boundary.
- Supports metadata payloads and filtering.
- Provides a path to dense + sparse hybrid retrieval.
- Avoids coupling retrieval to a single SQLite file.
- Better aligned with future use by multiple tools or agents.

## Consequences

Positive:

- Strong retrieval capabilities.
- Future hybrid-search path.
- Clean separation between app and search store.

Negative:

- Requires a local service/container.
- More operational footprint than an embedded database.

## Additional decision

Do not add SQLite in V1.

Qdrant is the sole derived persistent store unless future requirements justify a separate relational/catalog database.
