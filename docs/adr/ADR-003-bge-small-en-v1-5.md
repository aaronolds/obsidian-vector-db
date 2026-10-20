# ADR-003: Use BAAI/bge-small-en-v1.5 for Initial Embeddings

## Status

Accepted

## Context

The POC needs a local embedding model suitable for semantic retrieval, small enough for workstation inference, and practical from .NET.

Alternatives discussed:

- sentence-transformers/all-MiniLM-L6-v2
- BAAI/bge-small-en-v1.5
- intfloat/e5-small-v2
- nomic-embed-text-v1.5

## Decision

Use:

```text
BAAI/bge-small-en-v1.5
```

Initial settings:

- 384 dimensions
- cosine similarity
- local inference
- ONNX Runtime behind `IEmbeddingService`

## Rationale

- Retrieval-oriented.
- Small enough for local use.
- 384-dimensional output keeps storage compact.
- Larger usable input window than MiniLM.
- Replaceable behind an abstraction.

## Consequences

- Embedding-model changes require index rebuild.
- Chunking must respect the model tokenizer limit.
- Runtime implementation must reproduce reference tokenization, pooling, and normalization behavior.
