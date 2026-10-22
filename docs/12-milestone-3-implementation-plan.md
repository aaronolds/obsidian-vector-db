# Milestone 3 implementation plan

Implementation status: code, offline validation, and live Qdrant/ONNX integration
complete. The documented sample-vault CLI run passed twice. The selected-vault
check indexed valid notes and confirmed a zero-embedding second pass, but its
final status remains open because one note has malformed frontmatter and the
indexer correctly returned an incomplete-run exit code. No note files were
changed; the temporary Qdrant collection and config were removed after the run.

This plan expands Milestone 3 in [05-implementation-plan.md](05-implementation-plan.md).
The architecture, specification, canonical contracts, and accepted ADRs remain
authoritative. The implementation must preserve local inference, Markdown as the
source of truth, Qdrant as the only derived persistent store, and the indexer as
its sole writer. Record any infeasible requirement and supporting evidence before
proposing an architectural change. No ADR changes are currently required.

## Outcome and scope

Given a configured vault, installed pinned BGE assets, and local Qdrant:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  index --config knowledge.local.yaml
```

The first run creates a missing collection and indexes eligible chunks. A second
run with unchanged source, configuration, and index profile performs **zero
embedding calls**. Later runs replace changed documents and remove deleted,
excluded, and opted-out documents.

Included:

- Collection creation and compatibility validation: 384 dense dimensions, cosine.
- Complete canonical chunk payloads and deterministic Qdrant point IDs.
- Raw-byte file hashing and document-level incremental indexing.
- Recovery from incomplete document writes and safe stale-document deletion.
- The `index` command, operational metrics, unit tests, and local integration tests.

Deferred: CLI search and retrieval interfaces (Milestone 4), `rebuild` and expanded
`status` reporting (Milestone 5), benchmark evaluation, MCP, hybrid retrieval,
watchers, chunk-level diffs, schema migrations, concurrent indexing, and performance
tuning. Preserve the existing `status`, `chunks`, and `embeddings` commands.

## Starting point and implementation gaps

Milestones 1 and 2 already provide:

- `KnowledgeDocument`, `KnowledgeChunk`, `KnowledgeIdentity`, and the canonical
  embedding-text formatter in Core.
- Raw-byte SHA-256 hashes and newline-delimited deterministic document/chunk IDs.
- Markdown parsing, normalization, chunking, and exact token counting.
- Local ONNX inference through `IEmbeddingService`, with pinned asset validation.
- Configuration and CLI composition in Indexer.
- `Qdrant.Client` 1.19.0 and a read-only health probe in `Knowledge.Qdrant`.
- Docker Compose pinned to Qdrant 1.19.2, bound to loopback on gRPC port 6334.

Focused changes are needed:

- `ReadVault` currently scans, reads, parses, and chunks everything in one call.
  Its result exposes chunks and counters, but not document records, aliases,
  individual source snapshots, or explicit per-document outcomes. Split those
  stages so hashing and unchanged detection precede parsing/chunking, and aliases
  reach the Qdrant mapper without reconstructing them from chunks.
- Discovery must distinguish a complete scan from an unreadable or missing vault.
  Absence from a failed scan must never authorize deletion.
- The current containment check resolves a final file symlink but does not prove
  containment through linked parent directories or prevent traversal cycles.
  Correct the scanner behavior needed for trustworthy indexing and deletion;
  record unrelated ingestion issues separately.
- There is no collection adapter, payload mapper, incremental coordinator, or
  persisted evidence that every chunk of a document was written successfully.

## Implementation sequence

### 1. Define storage mapping and incremental-state rules

Keep canonical document IDs, chunk IDs, file hashes, and chunk hashes unchanged.
Extend tests around the existing helpers instead of adding competing formulas.
Renames, including case-only changes reported by the scanner, remain delete + add.

Qdrant accepts integer or UUID point IDs, so the canonical `sha256:...` chunk ID
needs a storage-only mapping. Use the first 32 hexadecimal digits of the chunk
digest, formatted as `8-4-4-4-12` UUID text in that order. Define this textual
mapping explicitly to avoid `Guid` byte-order differences. Keep the complete
canonical chunk ID in payload and application output. Test fixed examples,
repeatability, heading/ordinal changes, and mapping consistency. Reject a detected
point-ID collision with a different stored chunk ID. This adapter leaves domain
identity intact. See Qdrant's [point-ID documentation](https://qdrant.tech/documentation/concepts/points/).

Map every field in the recommended payload from
[03-domain-and-data-contracts.md](03-domain-and-data-contracts.md), including
document aliases, links with nullable display text, both content forms, hashes,
UTC modification time, and embedding provenance. Start with `schemaVersion = 1`
and `embeddingSchemaVersion = 1`; do not change the canonical records to carry
Qdrant DTOs. Validate vector length, finite values, and embedding identity before
upsert, even when the provider is substituted in tests.

Add two derived payload fields to each chunk:

| Field | Purpose |
|---|---|
| `documentChunkCount` | Expected total chunks in this document version; detects interrupted multi-batch writes |
| `indexProfile` | Deterministic fingerprint of the effective ingestion and embedding profile |

Define a stable, versioned serialization for the fingerprint. Include parser,
normalizer, chunker, formatter and point-mapping versions; effective chunking
settings and frontmatter opt-out field; model revision/digests, tokenizer profile,
pooling, normalization, dimensions, and schema versions. Reserve the BGE query
formatting rule in the embedding profile as documented in Milestone 2; query
embedding remains deferred. Exclude asset locations, vault absolute path,
timestamps, logging, and include/exclude globs. Discovery rules are applied anew
on every run, so changing exclusions can remove existing documents immediately.

Load indexed metadata by paginated, vault-filtered scrolling with vectors and
content fields omitted. Aggregate all chunks of each document, rather than trusting
one representative payload. An unchanged document can be skipped only when:

- Every stored chunk has the current file hash and compatible profile/provenance.
- All chunks agree on a positive `documentChunkCount`, equal to the stored count.
- Ordinals are unique and contiguous from zero, with consistent document/path
  identity and point-ID mapping.

Missing points, inconsistent hashes/counts, or otherwise incomplete state must
trigger whole-document replacement. Unknown/incompatible schema, model, or profile
must fail preflight with a rebuild-required diagnostic before writes. Do not
automatically recreate an incompatible collection. Until Milestone 5 supplies
`rebuild`, document selecting a fresh collection as the Milestone 3 recovery path.

Empty eligible notes produce no points. They may be reread on subsequent runs but
perform no embedding work. Do not add fake vector points, a document catalog,
SQLite, or local state files just to persist zero-chunk documents.

Acceptance: mapping and state tests prove complete payloads, stable identities,
profile compatibility, and detection of partial documents without model weights
or Qdrant.

### 2. Expose discovery and per-document ingestion stages

- Extract shared discovery, source snapshot reading, and document ingestion APIs
  from `MarkdownIngestor`. Retain `ReadVault` as the composition used by existing
  diagnostic commands; avoid parallel implementations of parsing or chunking.
- Discover the full candidate path set before mutations. Apply include/exclude
  rules, normalize relative paths with observed casing, and order paths ordinally.
  Return explicit scan completeness and safe diagnostics. Abort before mutations
  when the root is missing, traversal fails, or enumeration is incomplete.
- Enforce containment for files and every traversed directory, including symlink
  ancestors. Permit internal links, reject external targets, and detect cycles.
  Preserve logical relative paths for identity and deterministic ordering.
- Read a candidate once into a per-document snapshot of bytes and modification
  time. Hash those exact bytes and later parse the same snapshot. Do not hash one
  read and embed another, or use modification time as a hash substitute. A file
  edited after the snapshot is picked up on the next explicit run.
- Return distinct eligible, opted-out, empty, and failed outcomes. Eligible
  outcomes include the `KnowledgeDocument` and its chunks so aliases and
  document-level metadata remain available even when there are no chunks.
- Track discovered-but-unreadable paths as present and failed. Such files must
  not be swept as deleted. Continue with other documents when safe and return an
  incomplete-run result.

Acceptance: unchanged files need no full parse/chunking; existing `chunks` and
`embeddings` output stays compatible. Scanner tests cover empty and missing roots,
inaccessible paths, glob changes, symlink ancestors/cycles, and scan failures.

### 3. Implement the Qdrant indexing adapter

- Add only the storage boundary needed by the coordinator: ensure/validate the
  collection, enumerate indexed document metadata, delete a document, and upsert
  its chunk points. Put Qdrant client types and mapping in `Knowledge.Qdrant`.
  Keep application options in Indexer and Core dependency-free. Introduce a small
  Core interface and domain-shaped metadata/write records only where consumed.
- Reuse the pinned gRPC client and endpoint. Create a missing collection with one
  unnamed 384-dimensional dense vector and cosine distance; validate an existing
  collection's vector layout before any point mutation. See Qdrant's
  [collection documentation](https://qdrant.tech/documentation/manage-data/collections/).
- Create keyword payload indexes on `vaultId` and `documentId` for synchronization
  filters. Defer retrieval-specific tuning and sparse vectors.
- Scope every metadata read and document deletion to the configured `vaultId`;
  deletion also requires `documentId`. Never use an unfiltered delete-all operation.
- Follow scroll cursors until exhausted. Bound upsert requests by both point
  count and estimated serialized size so large documents do not exceed gRPC
  message limits. Do not mix documents in a replacement batch.
- Request `wait = true` for deletes and upserts, and verify completed application
  rather than counting an acknowledged write as success. Pass cancellation and
  bounded deadlines through each RPC. See the official
  [upsert](https://api.qdrant.tech/api-reference/points/upsert-points) and
  [delete](https://api.qdrant.tech/api-reference/points/delete-points) contracts.
- Dispose clients through the composition root. Report safe operation/error codes
  without server response bodies, payloads, vectors, or arbitrary exception text.
  Confirm the required methods against the pinned SDK and Qdrant 1.19.2; current
  online API documentation may describe a newer server. Do not upgrade dependencies
  as an incidental part of this milestone.

Acceptance: live tests prove collection creation/validation, full payload
round-trip, pagination, awaited writes, document filtering, and isolation from
points belonging to another vault.

### 4. Implement the document-level indexing coordinator

Implement orchestration in Indexer against the storage boundary and
`IEmbeddingService`, using the existing formatter and exact token counter. The
algorithm is sequential for the POC; only one index process may operate on the
collection at a time. Document that operating constraint.

1. Validate configuration and pinned local assets. Complete discovery, validate
   collection compatibility, and read all indexed metadata. Perform profile/schema
   checks before deleting or upserting any points. Verify the ONNX runtime can
   initialize before destructive replacement begins.
2. For each discovered candidate, read its snapshot and compute the raw-byte hash.
   If indexed state is complete and matches the hash/profile, count it as unchanged
   and continue without parsing, chunking, or calling `EmbedAsync`.
3. For each new, changed, or incomplete document, delete all its existing points
   and await completion. Then parse and rechunk the snapshot, following ADR-009's
   explicit delete-before-parse replacement order.
4. If opted out or empty, leave the document with no points. Otherwise construct
   canonical input and generate embeddings for every chunk. Hold one document's
   validated results before beginning its upserts so an embedding failure cannot
   write a subset of that document. Set the expected chunk count on every point.
5. Upsert bounded batches and verify document completeness after the writes.
   Count a document as indexed only after all its chunks are confirmed. A failed
   or interrupted write must never become an unchanged-document shortcut later.
6. After processing candidates, sweep previously indexed documents absent from
   the complete candidate path set. This removes deleted and path-excluded notes.
   Opt-out removal already occurs during replacement. Protect discovered paths
   whose reads/parsing failed; a failed read is not evidence of source deletion.

Preserve ADR-009's failure tradeoff explicitly: replacement is not transactional.
A failure after deletion can temporarily leave a document absent, and a failure
during upsert can leave partial points. Do not promise uninterrupted retrieval,
rollback, collection aliases, or generation switching. The next run repairs the
document from Markdown because missing or incomplete state cannot be skipped.

Continue after a document-specific read, parse, or embedding failure when safe;
return nonzero. Stop on Qdrant failure or unusable global runtime state, skip the
remaining stale sweep, and return nonzero. Cancellation stops processing and the
sweep. Never modify Markdown. An empty, successfully scanned vault may legitimately
remove all points for the configured vault; a missing/unreadable vault may not.

Acceptance: instrumented tests prove exactly zero embedding calls on the unchanged
second run, only changed documents are reembedded, stale chunks disappear when a
document shrinks, and every interruption point can be retried safely.

### 5. Add the CLI vertical slice and operating documentation

- Add `index [--config <path>]` and `index --help`. Help must require neither
  configuration nor model assets nor Qdrant. Use the existing options and local
  asset paths; no new persistence settings or cloud fallback are needed.
- Compose and dispose the scanner, exact tokenizer, embedding service, adapter,
  and coordinator in Indexer. Keep inference behind `IEmbeddingService` and provide
  a narrow test seam for counting calls and injecting failures.
- Emit a concise stdout summary and metadata-only diagnostics/structured logs to
  stderr. Include scanned, indexed, unchanged, opted-out/empty, stale-deleted, and
  failed document counts; chunks written/deleted; embedding-call count; embedding
  and Qdrant durations. Define counters so exclusions and empty documents do not
  inflate unchanged/indexed counts. Do not emit payloads or vectors.
- Return `0` only for a complete run, including a valid empty vault; `1` for
  operational failure or incomplete indexing; `2` for usage, configuration, asset
  setup, or incompatible collection/profile; `130` for cancellation. Identify
  configuration reads, source reads, inference, and Qdrant failures accurately.
- Remove `index` from the existing future-command rejection test. Keep `rebuild`,
  `search`, and `watch` unsupported. Preserve existing diagnostic-command behavior.
- Add a synthetic indexing sample configuration and README procedure using an
  isolated collection. Explain incremental behavior, profile compatibility,
  nontransactional replacement, and one-process operation. Update project-boundary
  and configuration documentation after implementation passes.

Acceptance: two CLI runs against local Qdrant succeed with no model/network
downloads, and the second reports zero embedding calls and no document writes.

### 6. Verify the milestone with real services

Add offline mapping/coordinator tests and an explicitly invoked Qdrant integration
project. Keep normal solution tests independent of Docker and ONNX weights. The
integration project must fail clearly when prerequisites are missing; skipped
service/model tests do not establish completion. Use unique disposable test
collections and synthetic temporary vaults, and clean up only those collections.

| Area | Required evidence |
|---|---|
| Initial index | Missing collection created with 384/cosine; all eligible sample chunks and complete payloads persisted |
| No-op | Same vault/config/profile on second run: zero `EmbedAsync` calls, unchanged IDs/payloads, zero document mutations |
| Content change | Only edited document reembedded; all its old points removed, including obsolete ordinals after shrinking |
| Hash semantics | Timestamp-only change skipped; raw-byte change detected even with unchanged modification time |
| Removal | Deleted, newly excluded, and newly opted-out notes have no remaining points; rename is delete + add |
| Empty content | Empty/frontmatter-only notes create no points; populated-to-empty replacement removes old chunks |
| Recovery | Fail before/after deletion, during embedding, and between upsert batches; next run repairs missing/partial state |
| Compatibility | Wrong distance/dimensions/vector layout, unknown schema, and changed profile fail before mutations |
| Discovery safety | Missing/unreadable root and incomplete enumeration cause no mutations; unreadable present files are not swept |
| Scope and scale | Other vault points survive; scroll and upsert cross multiple pages/batches; deterministic mapping survives a fresh collection |
| Privacy/CLI | Safe diagnostics, documented exit codes, cancellation, and no note text/vector sentinels in operational output |
| End-to-end | Actual pinned ONNX embeddings stored in local Qdrant; real-vault run followed by unchanged run meets the exit criterion |

Reproduction commands when the pinned model assets and Docker service are available:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test Knowledge.sln --no-build --no-restore
docker compose -f docker/docker-compose.yml up -d
./scripts/setup-bge-model.sh
dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- \
  index --config knowledge.milestone3.yaml
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- \
  index --config knowledge.milestone3.yaml
```

Use a counting decorator around the real embedding service in an integration test
to prove the no-op; timing alone is insufficient. Verify payloads through the
storage adapter rather than implementing Milestone 4 search for this milestone.
Repeat the CLI demonstration with an explicitly selected real vault and isolated
collection; retain only metadata counts/results in the implementation record.

Implementation validation record (2026-10-22, macOS, .NET 10.0.13):

- `dotnet build Knowledge.sln --no-restore -m:1 -v:minimal` — passed, zero
  warnings and errors.
- `dotnet test Knowledge.sln --no-restore -m:1 -v:minimal` — passed, 75 Indexer
  tests and 4 Embeddings tests.
- `dotnet build tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj --no-restore -m:1 -v:minimal` — passed, zero warnings and errors.
- `knowledge index --help` — passed without configuration, model assets, or Qdrant.
- `dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj --no-restore -m:1 -v:minimal` — passed against local Qdrant `v1.19.2` and the pinned ONNX model. It verified stored payload fields, one embedding on the first indexing pass, zero embeddings on the second pass, stale-document deletion, and preservation of another vault's point.
- `./scripts/setup-bge-model.sh` — passed; installed ONNX model SHA-256 is `828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35`.
- `knowledge index --config knowledge.milestone3.yaml` — first sample-vault run: scanned 6, indexed 4, opted out 1, empty 1, wrote 10 chunks using 10 embedding calls, no failures.
- Same CLI command on the unchanged sample vault: scanned 6, unchanged 4, opted out 1, empty 1, wrote 0 chunks, made 0 embedding calls, no failures.
- Selected-vault CLI run — first pass scanned 407 notes, indexed 400, found 6
  empty notes, and reported 1 malformed-frontmatter failure; it wrote 3,695
  chunks with 3,695 embedding calls. The second pass left 400 documents unchanged,
  made 0 embedding calls, and reported the same malformed note. The run returned
  exit code 1 both times, as required for an incomplete index.
- The selected-vault acceptance collection and temporary config were deleted
  after the check. Notes were never modified, and no personal note content was
  emitted by the CLI.

Mark Milestone 3 complete only after AC-001 through AC-004 and its real-vault
second-run criterion pass. The remaining vault-specific action is to repair or
exclude the malformed note according to the vault owner's preference, then rerun
both CLI passes and confirm exit code 0 with zero embedding calls on the second.
Rebuilding, semantic ranking, and MCP acceptance remain later milestone gates.
