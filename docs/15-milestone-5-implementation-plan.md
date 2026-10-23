# Milestone 5: Rebuild and operational commands

Implementation status: implemented on 2026-10-22. Verification evidence and
remaining environment limits are recorded in section 8. Planning and implementation
date: 2026-10-22.

## 1. Scope and authoritative requirements

Implement only Milestone 5 from [the delivery plan](05-implementation-plan.md):

- `knowledge rebuild [--config <path>]` recreates the configured collection and
  indexes all currently eligible Markdown using the existing ingestion pipeline.
- `knowledge status [--config <path>]` reports the configured vault root and ID,
  Qdrant endpoint, collection existence, embedding model, indexed document count,
  and indexed chunk count, while retaining useful existing status fields.
- Demonstrate that deleting the collection and rebuilding restores usable dense
  search with equivalent identities, content, and provenance.

Governing requirements: architecture sections 8–9; POC FR-019, FR-020, NFR-003,
NFR-004, and NFR-005; data contracts; security/privacy and configuration references;
ADR-001, ADR-002, ADR-005, ADR-008, ADR-009, and ADR-010. Preserve the current
Milestone 4 embedding schema version 2 and index profile. No architectural change
is currently necessary. Document any discovered infeasibility before changing a
decision.

Excluded: benchmark authoring/evaluation (Milestone 6), MCP hosting (Milestone 7),
watching, hybrid search, graph expansion, migrations, transactional replacement,
collection aliases, background jobs, cloud services, and new persistent stores.

## 2. Current implementation and ownership

- `KnowledgeCli` already dispatches status/index/search and owns configuration,
  local runtime construction, output, and exit codes. Rebuild is rejected today.
- `IndexCoordinator.IndexAsync` performs complete discovery, validates collection
  and stored profile metadata, then does document-level synchronization. Reuse
  its ingestion, embedding, write verification, diagnostics, and summary logic.
- `IKnowledgeIndexStore` and dependency-free records live in `Knowledge.Core`.
  Add only the collection lifecycle/status contracts needed by this milestone;
  keep Qdrant SDK types in `Knowledge.Qdrant`.
- `QdrantKnowledgeIndexStore` owns collection creation/layout checks, payload
  indexes, paginated metadata reads, and document mutations. Extend it for
  explicit recreation and read-only status. A small separate status interface
  is acceptable if it avoids giving read-only consumers mutation capabilities.
- `QdrantHealthCheck` supplies reachability. Status currently reports connectivity
  and configuration but does not inspect the configured collection or counts.
- Existing fake stores and CLI factory seams support asset-free tests. Extend
  those narrowly rather than introducing a general dependency framework.

## 3. Rebuild behavior and safety gates

Rebuild is an explicit, destructive operation on **the configured collection**.
It follows the architecture's collection recreation semantics, including removal
of any other vault's points stored in that collection. V1 has one active vault;
operators must use a dedicated collection. Document this clearly in command help
and the operating instructions. Ordinary `index` remains vault-scoped and must
never recreate an incompatible collection automatically. No extra interactive
confirmation or new force flag is needed for the explicitly named command.

Implement the following sequence:

1. Parse arguments and fully validate configuration before network access.
2. Require pinned local model/tokenizer assets and construct the ONNX service,
   validating digests and supported graph before deleting any collection.
3. Complete vault discovery with the existing include/exclude and symlink rules.
   Abort without collection mutations for a missing root, incomplete scan, or
   unreadable included source. Check candidate readability using `ReadSnapshot`
   before recreation; avoid retaining an entire vault in memory. Honor
   cancellation throughout preflight and immediately before recreation.
4. Explicitly recreate only `qdrant.collection`: delete if present, create if
   absent, then create/validate the current unnamed 384-dimensional cosine
   collection and required keyword payload indexes. Do not validate the old
   schema/profile as a prerequisite: rebuild is the recovery path for old,
   incompatible, or malformed derived state. Await acknowledged operations;
   propagate cancellation and safe error codes.
5. Run the shared indexing pipeline against the new collection. All eligible
   non-empty, non-opted-out documents must be embedded and written, even when
   identical to the old index. Reuse preflight discovery where practical without
   exposing an unsafe public way to bypass ordinary index preflight. Preserve
   ADR-009 replacement order for normal indexing and subsequent recovery runs.
6. Emit metadata-only summaries and return success only when indexing completes.

A fully discovered empty vault is valid and produces an empty recreated
collection. Excluded, opted-out, and empty notes must contribute no chunks.
Malformed notes and changes after preflight may still cause a partial rebuild;
identify failures and return nonzero. Rebuild is deliberately nontransactional:
after recreation the prior index is gone, cancellation or failures can leave a
missing/partial collection, and rerunning `index` or `rebuild` repairs derived
state. Do not claim rollback, stable snapshots, or concurrent-writer safety.
Only one index/rebuild process may write a collection at a time.

## 4. Status semantics

Status must be read-only: never call `EnsureCollectionAsync`, create payload
indexes, initialize ONNX, generate embeddings, or modify Markdown/Qdrant.

Retain application version, vault ID/path/existence, endpoint/reachability,
collection name, model identity, and dimensions. Add collection existence,
indexed document count, and indexed chunk count with stable output labels.

Define counts as follows:

- Counts describe indexed state for the configured `vaultId`, not files currently
  in the vault or collection-wide counts across vaults.
- Chunk count is the number of stored points matching that exact vault ID.
- Document count is the distinct nonblank `documentId` count among those points.
  Empty, opted-out, and excluded documents with no stored chunks are not counted.
- Paginate to completion using only identity payload fields and no vectors or
  note-content fields. Counts must not be capped at one page. An existing empty
  collection reports zero. A missing collection reports `exists: false` and zero
  indexed counts without creating anything.
- Unavailable Qdrant or failed/malformed count reads report unavailable counts
  and a safe diagnostic, never fabricated successful zeros. Status can inspect
  old profiles without requiring model initialization or a current index profile;
  it is an operational report, not a search-readiness guarantee. Do not count a
  malformed document identity as a valid document.
- Preserve config-file-relative paths and environment overrides. Status must
  work without model weights. Inspect existing shared asset-existence validation
  and make only the narrow command-aware adjustment needed if configured missing
  weights would otherwise block status. Retain strict checks for commands that
  require assets and all security/type/range validation before network access.

Exit codes: `0` for an existing vault, reachable Qdrant, and successful inspection
of an existing collection (including empty); `1` for an operational problem such
as missing vault/collection, unavailable Qdrant, or failed count retrieval; `2`
for usage/configuration or rebuild asset/compatibility setup errors; `130` for
cancellation. Catch transport failures without exposing server bodies or note
content. Keep status useful even when the collection is absent.

## 5. Implementation sequence

1. Add minimal Core collection recreation/status contracts and records. Extend
   all existing fake implementations without weakening current safety tests.
2. Implement Qdrant recreation and paginated, vault-filtered read-only counts.
   Keep existing vector/payload schema validation for ordinary indexing.
3. Add rebuild coordination with preflight before deletion and shared indexing
   behavior. Avoid copying the existing synchronization algorithm.
4. Wire rebuild dispatch/help/config/output and expanded status into the CLI.
   Preserve current commands, logging boundaries, cancellation, and exit codes.
5. Add focused unit tests and extend explicit Qdrant/ONNX integration acceptance.
6. Update README, configuration reference, milestone index/status, and repository
   agent guidance where current-status claims change. Add a Milestone 5 YAML
   example only if it improves the documented workflow; no new settings needed.
7. Record actual verification and limitations in this file; mark implemented
   behavior separately from any live acceptance blocked by local prerequisites.

## 6. Verification matrix

| Area | Required evidence |
|---|---|
| Rebuild | Existing state is recreated; unchanged source is embedded again; missing collection is created; old schema/profile/layout can be recovered |
| Preflight | Invalid config/assets/runtime, missing/unreadable vault, incomplete discovery, unreadable candidate, and cancellation before reset cause no destructive collection operation |
| Eligibility | Exclusions, opt-out, empty/malformed notes, empty vault, and source immutability follow existing semantics |
| Recovery | Post-reset write failure/cancellation returns nonzero; next index repairs missing/incomplete document state |
| Determinism | Repeated rebuilds preserve document/chunk/point IDs, chunk hashes, original Markdown, and provenance for unchanged source; ignore operational timestamps/timings |
| Status | Required fields, missing/empty/populated collection, multiple chunks per document, other-vault isolation, more than one scroll page, malformed identity, transport failure, and cancellation |
| Read-only | Status makes no collection/payload/point mutation and no inference; works with missing/omitted local model assets |
| CLI/privacy | Help and invalid argument cases; config/env/path handling; exit codes; sentinels in query/note/server error data never appear in operational output |
| Regression | Existing ingestion/embedding/index/search tests pass; unchanged index second pass still makes zero embedding calls and chunk writes |

Run from the repository root:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test Knowledge.sln --no-build --no-restore
```

For live acceptance use the pinned local model and local Qdrant, unique disposable
test collections, and a synthetic temporary vault. Do not reset a user's current
collection. Verify index/search/status, explicit external collection deletion,
rebuild/search/status, a second equivalent rebuild, and a subsequent no-op index.
Also exercise replacement of an incompatible collection. Hash source files before
and after; compare actual content/provenance in assertions without printing it.
Clean up only test-created collections.

```sh
docker compose -f docker/docker-compose.yml up -d
dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj
```

The pinned model weights were installed as an ignored local asset and validated
with the existing setup script for live acceptance. Do not add weights/build
output to Git or introduce runtime downloads. Record exact passed checks,
metadata-only counts, versions, and any environment limitations; never claim an
unrun live check passed.

## 7. Implementation handoff

After completing this plan, delegate its implementation in this shared worktree
to the user's requested `gpt-6-luna` model with `high` reasoning. The implementation
agent must reread AGENTS.md and authoritative references, perform the work and
verification, and report changes and limitations. The coordinating agent reviews
the resulting diff and acceptance evidence before reporting completion.

## 8. Implementation and verification evidence

Implemented behavior:

- `rebuild` validates configuration and pinned ONNX runtime setup, discovers the
  full vault and reads each included candidate before collection recreation, then
  recreates the selected collection and indexes all eligible content through the
  existing indexing pipeline. Missing vault, incomplete discovery, unreadable
  candidates, and cancellation before recreation preserve the old collection.
- `status` reports collection existence and configured-vault document/chunk
  counts, paginating identity-only payload reads. It does not load ONNX assets or
  mutate Qdrant. Missing collections return zero counts; malformed identity data
  and transport failures report unavailable counts with safe diagnostics.
- Collection recreation uses the current unnamed 384-dimensional cosine layout
  and required keyword payload indexes, recovering incompatible layouts. The
  embedding schema and profile were not changed.
- Updated command, operational, configuration, milestone, and agent guidance
  documents describe collection-wide deletion and nontransactional recovery.

Verification performed on 2026-10-22:

- `dotnet restore Knowledge.sln` succeeded.
- `dotnet build Knowledge.sln --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet test Knowledge.sln --no-build --no-restore` passed: Knowledge.Indexer
  106/106 and Knowledge.Embeddings 8/8.
- The existing setup script verified the pinned local model asset digest; local
  Qdrant v1.19.2 was available.
- `dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj --no-restore`
  passed 2/2. Unique disposable collections and a synthetic vault verified first
  indexing, vault-scoped status, search, unchanged no-op indexing, external
  collection deletion followed by rebuild and search, stable metadata identity,
  other-vault point removal during rebuild, and cleanup. Adapter checks verified
  an empty collection, foreign-vault isolation, 260 matching chunks across pages,
  malformed identity failure, and replacement of an incompatible vector layout.
- Unit coverage verifies deterministic rebuild identities, re-embedding unchanged
  source, preservation when preflight finds a missing vault, empty-vault rebuild,
  cancellation before reset, and repair by ordinary indexing after a failed
  post-reset embedding. CLI tests cover destructive help, status without model
  assets, and safe unavailable counts when Qdrant is unreachable.

The coordinating agent also completed a compiled-CLI smoke on 2026-10-22 with 13
command invocations: missing/populated status, status with configured missing
weights, invalid-assets rebuild returning 2 without changing the collection,
external collection deletion and rebuild restoring search with the same document
and chunk IDs/content, repeated rebuild, no-op index with zero embeddings, and
missing-vault rebuild returning 1 while preserving points. Source SHA-256 hashes
were unchanged and the synthetic vault, config, and disposable collection were
removed. A broken in-vault Markdown symlink is discovered as an unreadable
candidate and verifies that source-read preflight leaves the existing collection
untouched. Incomplete directory enumeration was not simulated because filesystem
permission behavior varies by environment.
