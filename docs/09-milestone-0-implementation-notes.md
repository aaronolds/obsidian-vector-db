# Milestone 0 implementation notes

The accepted ADRs remain unchanged. These choices resolve foundation-level
details without implementing later milestones.

## Status versus startup validation

The configuration reference calls for startup failure on a missing/unreadable
vault, unavailable model assets, and collection dimension mismatches. The
Milestone 0 request explicitly requires status to report whether the vault exists
and defers collection creation and embedding inference.

For status, syntactically invalid configuration fails before network access.
A missing vault is reported as `Vault exists: false`, and the command returns
exit code 1 after reporting all status fields. An unavailable Qdrant service is
also reported and returns 1. Existence is a directory check, not a claim that
all vault files are readable. Readability checks belong to ingestion; asset and
collection compatibility checks belong to embedding/indexing startup. Status
does not load a model or query collection metadata.

## Configuration ownership and defaults

Options and YAML loading belong to the Indexer application composition root.
They are not domain models. Core remains free of infrastructure dependencies,
and no unused embedding, indexing, or retrieval interfaces were introduced.
Markdown, Embeddings, and MCP are library shells referencing Core.

The checked-in YAML uses `primary` and `./docs` as a portable demonstration vault.
Users set `KNOWLEDGE_VAULT__PATH` or edit a local YAML file for their actual vault.
Relative paths resolve against the config directory. The file is required;
environment variables override individual settings after YAML loading. Empty
exclude lists are permitted, while an include list is required.

The POC validator enforces ONNX, BAAI/bge-small-en-v1.5, 384 dimensions, dense
retrieval configuration, loopback Qdrant, and metadata-only logging. Invalid
settings fail instead of silently selecting another implementation. No cloud
fallback, remote service, or secrets configuration is added.

Chunk settings are checked for positive sizes, target/soft-max ordering, overlap
less than the target, overlap only when split, and soft maximum below the model's
512-token input window. This is preliminary validation; only the actual tokenizer
can guarantee that constructed text including title, heading, tags, and special
tokens fits. That enforcement remains part of Milestone 2.

## Qdrant protocol and lifecycle

The reference port 6334 is gRPC. The official Qdrant .NET client performs a
read-only health RPC with a three-second deadline, rather than probing an HTTP
route on the gRPC port or merely checking that a TCP socket accepts connections.
Failure logs contain a gRPC status code, never arbitrary server response bodies.

Compose publishes only IPv4 loopback gRPC, uses a named storage volume, disables
telemetry, and pins the server image. The default `localhost` resolves to loopback;
`127.0.0.1` is available as an override if workstation IPv6 resolution differs.
No Docker startup hook creates a collection. The Indexer will own mutations in
the indexing milestone (ADR-005).

## CLI and diagnostics

The console assembly is named `knowledge`. Commands can be run through `dotnet
run` or the generated apphost. Status has no future command stubs. JSON logs go
to stderr and human-readable status goes to stdout. Validation and parser errors
do not echo YAML source or supplied values. Logging of content and queries is
explicitly rejected in this milestone.

Exit codes are 0 for an existing vault and reachable Qdrant, 1 for missing vault
or unavailable Qdrant, 2 for command/configuration errors, and 130 for cancellation.

## Milestone 1 tokenizer sequencing note

Milestone 1 now performs token-budgeted chunking without loading an embedding
model. The exact BGE WordPiece vocabulary is an external model asset and is not
committed to this repository. `embedding.tokenizerPath` accepts a local `vocab.txt`
when available and uses the `BertWordPieceTokenCounter`; the `chunks` inspection
command otherwise uses a deterministic conservative counter so the checked-in
sample vault remains runnable without model downloads. Milestone 2 must validate
that the selected vocabulary and ONNX runtime reproduce the reference BGE
tokenization and inference behavior before embedding-backed indexing is enabled.
