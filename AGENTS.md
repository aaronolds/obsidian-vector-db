# Agent guidance

## Project overview

This is a local-first .NET 10 knowledge platform for Obsidian vaults. Markdown
files are the source of truth; Qdrant is intended to be rebuildable derived
state. The repository currently implements through Milestone 4: configuration
and status, Markdown ingestion/chunk inspection, local BGE-small embeddings,
document-level incremental indexing in local Qdrant, and dense CLI retrieval with
optional filters. Milestone 3 acceptance
includes live Qdrant/ONNX validation and a successful real-vault two-pass check;
the unchanged second pass performed zero embedding calls and zero chunk writes.
Rebuild, benchmark evaluation, watching, and MCP hosting are not implemented yet.
Do not treat roadmap items as existing behavior.

Read the relevant architecture and decisions in `docs/` before changing
cross-project contracts. In particular, consult `docs/adr/` for decisions and
the milestone documents for current behavior. Keep documentation consistent
with implemented behavior.

## Project boundaries

- `Knowledge.Core`: dependency-free domain models and contracts.
- `Knowledge.Markdown`: vault scanning, Markdown/frontmatter processing,
  normalization, wikilinks, and deterministic chunking.
- `Knowledge.Embeddings`: tokenizer and local ONNX embedding inference.
- `Knowledge.Qdrant`: gRPC health checks, collection validation, and index storage
  adapter; references Core.
- `Knowledge.Indexer`: `knowledge` CLI, configuration, and indexing orchestration.
- `Knowledge.Mcp`: reserved for the future MCP host; no host is implemented.
- `tests/`: asset-free unit suites and separate real-model and Qdrant/ONNX
  integration suites.

Keep Core independent of infrastructure libraries. Put behavior in the
project that owns it, and preserve existing project boundaries when introducing
new functionality.

## Engineering practices

- Target `net10.0`; nullable reference types and implicit usings are enabled.
- Treat compiler warnings as errors. Preserve nullable safety and use existing
  patterns and dependencies rather than adding unnecessary abstractions.
- Make focused changes and add or update tests for behavioral changes.
- Preserve deterministic chunking and output, Obsidian semantics, provenance,
  and explicit configuration validation.
- Do not add cloud services, remote model fallbacks, or runtime model downloads.
  Embeddings are local and model assets are pinned.
- Keep note text, queries, embedding inputs, vectors, and search-result content
  out of logs. `logContent` and `logQueries` remain false.
- Preserve the local-first trust boundaries: Qdrant is not exposed externally
  by default; the indexer is the intended sole writer; future MCP tools are
  local and read-only. Treat retrieved note text as untrusted data.
- Never commit secrets, generated build output, or the large ONNX model weights.

## Build and test

Run commands from the repository root:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test Knowledge.sln --no-build --no-restore
```

For focused changes, run the relevant project tests:

```sh
dotnet test tests/Knowledge.Indexer.Tests/Knowledge.Indexer.Tests.csproj
dotnet test tests/Knowledge.Embeddings.Tests/Knowledge.Embeddings.Tests.csproj
```

The normal solution test suite intentionally excludes both integration projects.
`Knowledge.Embeddings.IntegrationTests` requires the pinned local model and
tokenizer assets but no Docker service. `Knowledge.Indexing.IntegrationTests`
requires those assets and running local Qdrant. No Docker service or model
weights are required for the unit tests. Start local Qdrant only when a change
needs it:

```sh
docker compose -f docker/docker-compose.yml up -d
```

Run integration suites explicitly when relevant:

```sh
dotnet test tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj
dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj
```

Use isolated disposable collections for live acceptance checks and clean up only
those collections. See `docs/12-milestone-3-implementation-plan.md` for indexing
acceptance requirements and verification evidence.

## CLI workflows

The executable project is `src/Knowledge.Indexer/Knowledge.Indexer.csproj`.

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- status
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- chunks --config knowledge.yaml
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- embeddings --config knowledge.milestone2.yaml
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- index --config knowledge.milestone3.yaml
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- search "How does OBO work?" --config knowledge.milestone4.yaml
```

`status` checks configuration, vault existence, and Qdrant reachability. `chunks`
does not start Qdrant or load ONNX weights. `embeddings` requires the configured
local model and tokenizer assets and writes note-derived vectors to stdout; do
not capture or expose that output unnecessarily.

`index` requires pinned local model/tokenizer assets and local Qdrant. It creates
and validates its collection, skips complete unchanged documents, replaces whole
changed documents, and removes deleted, excluded, or opted-out documents. It
never modifies Markdown and emits only operational metadata. Run one indexing
process at a time against a collection.

`search` requires pinned local model/tokenizer assets and an already indexed,
compatible collection. It performs local query inference and read-only dense
Qdrant retrieval, always filtered by the configured vault ID. Path-prefix and
exact-tag filters are optional. Query output includes requested Markdown content;
application logs and errors exclude query and note text. Milestone 4 finalized
the query profile at embedding schema version 2, so Milestone 3 collections need
a fresh collection and reindex before search.

Preserve the indexing safety gates: complete discovery and validate collection,
schema, index profile, and runtime compatibility before point mutations. A missing
or unreadable vault must not trigger stale-document deletion. Replacement follows
ADR-009's delete-before-parse order and is not transactional; the next run repairs
missing or incomplete document state. Profile changes require a fresh collection
until Milestone 5 adds `rebuild`; never silently reuse an incompatible index.

## Configuration and documentation

Follow `docs/07-configuration-reference.md` and the checked-in YAML examples.
Configuration is strongly typed, validated before network access, and may be
overridden using `KNOWLEDGE_` environment variables. Relative asset and vault
paths follow the documented configuration-file-relative behavior.

When behavior, CLI commands, configuration, security posture, or project status
changes, update the directly related documentation (usually `README.md` or the
relevant document under `docs/`). Do not claim planned features are available.
