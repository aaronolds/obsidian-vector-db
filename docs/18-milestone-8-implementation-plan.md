# Milestone 8: Hardening and POC acceptance

## 1. Status and objective

Status: in progress on 2026-10-29; the asset-free baseline and one
failure-safety fix are complete. Live Qdrant/ONNX acceptance is pending.
The new managed worktree was created before this plan, from revision
`c142c50c1dd06fcf6c0abd282990b5b598893ee3`, on branch
`codex/milestone-8-hardening`.

Complete Milestone 8 from [the roadmap](05-implementation-plan.md): error
handling, path traversal protection, exclusion rules, privacy-safe logging,
configuration validation, graceful Qdrant unavailability, malformed note
handling, and deterministic rebuild validation. Close the POC only when all
acceptance criteria have demonstrated evidence.

The architecture in [01](01-solution-architecture.md), the specification in
[02](02-poc-specification.md), the contracts in
[03](03-domain-and-data-contracts.md), the security requirements in
[06](06-security-and-privacy.md), configuration rules in
[07](07-configuration-reference.md), and ADR-001 through ADR-010 govern this
work. The detailed Milestone 1–7 plans define implemented behavior where older
illustrative examples differ, including embedding schema version 2.

## 2. Scope and constraints

Keep the current .NET 10 projects, CLI commands, dense retrieval behavior,
configuration shape, and two read-only MCP tools. Fix demonstrated defects in
the owning component and add focused regression coverage. A requirement already
proven by a meaningful test needs an evidence reference, not a duplicate test.

Preserve these invariants:

- Markdown is authoritative; Qdrant is rebuildable derived state.
- Core remains independent of infrastructure. The indexer is the sole writer.
- Discovery must complete, and collection layout, payload schema, index profile,
  and runtime compatibility must pass before document point mutations.
- Missing or unreadable vaults must not cause stale-document deletion. A failed
  individual read retains that document's existing data; changed-document parse
  or embedding failures follow ADR-009's delete-before-parse behavior.
- Rebuild validates local runtime assets, completes discovery, and checks every
  candidate source is readable before deleting the entire configured collection.
- Replacement and rebuild remain nontransactional. Partial derived state is
  repaired by a later explicit run; no atomicity or rollback is promised.
- Inference stays local with pinned assets. Application logs exclude note text,
  queries, embedding inputs, vectors, and result content. `logContent` and
  `logQueries` remain false.
- MCP stays local stdio and read-only; returned note content remains untrusted
  data with provenance.

Out of scope: watcher/service mode, hybrid or sparse search, ranking tuning,
deduplication, graph traversal, rerankers, new stores, cloud services, remote MCP,
authentication, attachment indexing, agent write-back, and general refactoring.
Retain the owner-reviewed 22/24 dense baseline. Do not create a new benchmark or
repeat owner relevance review unless a necessary fix changes retrieval inputs
or results. If an accepted architecture decision proves infeasible, record the
issue and its consequences before proposing a decision change.

## 3. Planning baseline

Source and test inspection established this starting coverage:

| Area | Existing evidence | Milestone 8 audit focus |
|---|---|---|
| Discovery and ingestion | `IngestionTests`, shared `Discover`/`ReadSnapshot`, note-reader containment tests | Indexing and rebuild use the same containment guarantees; external links, ancestor links, cycles, aliases, unreadable roots/subtrees, exclusion precedence |
| Incremental safety | `IndexingTests`: unchanged runs, changed/deleted notes, embedding retry, incomplete documents, incompatible profiles, missing vault | Actual mutation order and zero-mutation assertions at safety gates; isolated document failures and interrupted writes |
| Rebuild | Unit preflight/recovery tests; live `Pinned_onnx_indexes_to_local_qdrant_and_unchanged_run_skips_inference` | Existing live rebuild comparison checks metadata and selected search IDs; extend to complete canonical payload/content and vector equivalence across a representative multi-note corpus |
| Configuration and CLI | `ConfigurationTests`, `CliTests`, `SearchTests` | Invalid configuration rejected before inference/network/mutations; operational errors, cancellation, and exit-code consistency across commands |
| MCP | `MarkdownNoteReaderTests`, `KnowledgeMcpToolsTests`, live `McpServerIntegrationTests` | Preserve Milestone 7 parity, eligibility, privacy, read-only snapshots, and recovery; add tests only for uncovered cases |
| Privacy and local boundary | Sanitized CLI/MCP tests, loopback-only validator, checked-in Compose bindings | Command diagnostics, parser/inference/RPC exception paths, log levels, stdout/stderr boundaries, metadata-only evaluation reports |

Milestone 7's recorded verification on 2026-10-25 passed 164 unit tests and
6 embedding, 3 indexing, and 1 MCP integration tests, with zero build warnings
or errors. These are historical counts; record actual new results separately.
Milestone 6's owner-reviewed real-vault baseline is 22/24 Hit@5 (91.7%), with
both misses reviewed. Earlier real-vault two-pass indexing demonstrated zero
embedding calls and zero chunk writes on the unchanged pass.

The worktree was built and tested on 2026-10-29 with .NET SDK `10.0.402`:
`dotnet build Knowledge.sln --no-restore` passed with zero warnings and errors;
the solution suite passed 165/165 (Indexer 133, Embeddings 8, MCP 24). The
explicit live suites passed Embeddings 6/6, Indexing 3/3, and MCP 1/1.

The model weights were resolved from the primary checkout through an ignored
worktree symlink; no weights were copied or committed. Docker Compose started
the repository's loopback-only Qdrant service for the live runs. The MCP suite's
first run exposed stale fixture assumptions: generated configuration hard-coded
`defaultLimit: 5` despite test options using 2, then a recovery assertion expected
a specific note in the default top two despite several near-identical fixtures.
The fixture now serializes its configured limit and explicitly requests 50 for
that recovery assertion. The rerun passed. The ignored local config and private
evaluation files remain absent. Asset digests have not yet been checked for this
milestone.

### 3.1 Implemented finding

`IndexCoordinator` previously treated a non-RPC exception during upsert or its
post-write verification as a per-document failure, then continued to delete
stale documents. Since the Qdrant write state was unconfirmed, this could cause
additional destructive mutations after an uncertain storage failure. The
coordinator now marks that state unsafe and stops before stale cleanup. The
focused regression injects a generic upsert exception, confirms the deleted
document's old points remain, and confirms the changed document is not falsely
recorded. The next run can retry the changed document and perform stale cleanup
after storage writes succeed.

### 3.2 Verification on 2026-10-29

| Command | Result |
|---|---|
| `dotnet build Knowledge.sln --no-restore` | Passed; 0 warnings, 0 errors |
| `dotnet test Knowledge.sln --no-build --no-restore` | Passed; 165 total (133 Indexer, 8 Embeddings, 24 MCP) |
| `dotnet test tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj` | Passed; 6/6 with pinned local ONNX assets |
| `dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj` | Passed; 3/3 with local Qdrant and ONNX |
| `dotnet test tests/Knowledge.Mcp.IntegrationTests/Knowledge.Mcp.IntegrationTests.csproj --no-restore` | Passed; 1/1 with local Qdrant and ONNX |

The live suites use their disposable collections and cleanup. They exercise
real indexing/rebuild, no-op retry, deletion, MCP stdio parity, full indexed
state snapshots, current note reads, retry after a collection appears, and note
availability when Qdrant is unavailable. The broader Milestone 8 deterministic
multi-corpus rebuild matrix, filesystem/fault-injection audit, and final Release
acceptance run remain open; these passes alone do not close Milestone 8 or the
POC.

## 4. Implementation sequence

### Phase A: establish coverage and a reproducible baseline

1. Restore, build, and run the asset-free solution suites in this worktree.
   Record revision, SDK/runtime, commands, exit codes, warnings, and test counts.
2. Map every FR, NFR, and AC in the POC specification, plus all eight Milestone 8
   objectives, to actual assertions and recorded acceptance evidence. Separate
   executed evidence from planned coverage and historical implementation notes.
3. Read the detailed Milestone 3, 5, 6, and 7 evidence before identifying a gap.
   Record each uncovered case with its owning component and expected behavior.
4. Prepare synthetic temporary vaults and unique disposable
   `knowledge-hardening-test-<id>` collections for live verification. Inspect
   relevant `KNOWLEDGE_` override names and neutralize conflicting values in the
   test process without printing private settings.
5. Reuse the primary checkout's pinned local assets through an ignored local
   link or copy, and validate them using the existing asset-validation path.
   Do not download weights or copy real-vault configuration into fixtures.
6. Check local Qdrant health. Start checked-in Compose only if needed; preserve
   a healthy shared service and its bindings. Missing prerequisites leave live
   gates pending and do not prevent asset-free hardening work.

Deliverable: a requirements/evidence matrix and a concrete gap list in this
document. Do not predeclare any untested scenario a production defect.

### Phase B: filesystem eligibility and mutation safety

Audit `Knowledge.Markdown/Ingestion.cs`, `MarkdownNoteReader.cs`, and
`Knowledge.Indexer/Indexing/IndexCoordinator.cs` together.

- Verify traversal, absolute/drive/UNC selectors, separator/casing behavior,
  external file and directory links, internal links, cycles, broken links, and
  an ancestor redirected outside the vault between discovery and reading.
  Check the actual filesystem's casing behavior; do not infer it solely from
  the OS. Avoid claiming atomic protection against concurrent filesystem swaps.
- Verify include rules, exclude-wins behavior, default excluded folders, and
  `ai-index: false` for ingestion and note reads. Verify transition from indexed
  to excluded/opted-out removes only the intended document on the next index.
  Inspect aliases so alternate link paths cannot bypass eligibility policy.
- Assert incomplete discovery causes no document writes or deletions and no
  collection recreation. Assert unreadable candidate files preserve the old
  collection during rebuild preflight. Distinguish a legitimate empty vault
  from a failed scan.
- Use controlled filesystem fixtures or minimal existing test seams for
  permission/read failures; chmod-based tests alone may pass under privileged
  accounts. Add abstractions only if a specific required case cannot otherwise
  be tested reliably.

Deliverable: focused regressions and minimal fixes for demonstrated gaps.

### Phase C: failures, cancellation, and privacy

Audit the coordinator, CLI boundaries, shared runtime, Qdrant adapters, and MCP
error mapping. Extend existing fakes rather than building a new framework.

- Cover malformed frontmatter, unclosed fences, invalid UTF-8, unreadable notes,
  and valid neighboring notes. Failed paths receive safe diagnostics and a
  nonzero incomplete-run result. Valid documents continue when safe.
- Assert ADR-009 ordering for changed documents: delete before parse/embed.
  A failed replacement must not be reported unchanged; a repaired source must
  reindex on the next run. Other documents retain correct content.
- Inject embedding failure, partial upsert, failed write verification, and RPC
  failure during compatibility reads, deletion, writes, and stale cleanup.
  Verify the operation stops when storage state is unsafe or unavailable,
  prevents subsequent unsafe cleanup, and can recover on a later run.
- Check cancellation before mutation and after deletion or partial writes;
  preserve documented CLI exit code 130 and recoverability. Keep existing
  operational/usage distinctions: 1 for incomplete operational work, 2 for
  invalid usage/configuration/assets/compatibility. MCP uses tool errors and
  retains usable note reads after retrieval failures.
- Verify invalid configuration, incompatible collections/profiles, and bad
  pinned assets fail at the documented gates. Assert mutation counters/state,
  rather than relying only on the exception type or remaining point count.
- Exercise unavailable Qdrant through bounded unreachable test endpoints or
  injected failures. Do not stop the user's shared service. Check safe errors,
  bounded completion, disposal, cancellation, and subsequent usable operations.
- Use synthetic sentinel note/query/exception content to test stderr, structured
  logs, MCP notifications, and error responses at supported log levels. Confirm
  configuration/parser/ONNX/RPC errors cannot echo sensitive supplied values.
  Explicit `search`, `get_note`, `chunks`, and `embeddings` result output remains
  intentional; test diagnostic channels separately. Keep evaluation reports
  metadata-only and MCP stdout protocol-only.

Deliverable: transparent failure behavior, bounded recovery checks, and privacy
regressions for the paths uncovered by Phase A.

### Phase D: deterministic rebuild and end-to-end acceptance

Extend `Knowledge.Indexing.IntegrationTests/LocalIndexingTests.cs` with a small,
representative synthetic corpus containing multi-chunk prose, nested headings,
tags, aliases, wikilinks, code, tables, Unicode, empty and opted-out notes, and
configured exclusions. Keep source bytes and modification times fixed during
determinism comparisons.

1. Run initial indexing with real pinned ONNX and local Qdrant. Capture source
   hashes and all point IDs, complete payloads, and vectors in memory.
2. Run unchanged indexing; assert zero embedding calls, zero chunk writes, and
   unchanged complete point state.
3. Run rebuild twice from the same corpus/configuration/runtime. Compare sorted
   document/chunk/point identities, original and normalized content, hashes,
   metadata, document chunk counts, schema, and profile exactly. Compare vectors
   component-wise within `1e-5` for the same runtime, matching existing repeated
   inference tolerance; assert finite 384-dimensional values. Canonicalize map
   ordering and compare semantic values, not serialized property order.
4. Delete only the disposable collection and run the actual CLI rebuild entry
   point. Verify restored counts, equivalent state, and successful search with
   full provenance. Search fixtures must avoid tied cutoff scores; compare
   scores with a documented numeric tolerance, not byte equality.
5. Demonstrate a changed note leaves an unchanged neighbor untouched, a rename
   behaves as delete/add, and deletion/exclusion/opt-out removes the intended
   points. Repeat indexing to confirm convergence to a no-op.
6. Verify preflight failure retains existing derived state and injected failure
   after deletion leaves a reported incomplete run that a later `index` repairs.
   Use deterministic fault injection for outage timing, not races against the
   shared Docker container.
7. Reuse the existing live MCP suite for CLI/MCP parity, read-only full-state
   comparisons, current note reads, and failure independence. Do not repeat the
   manual client smoke test unless MCP launch/protocol behavior changes.

Keep full content and vectors inside assertions. Commit only synthetic fixtures
and metadata-level evidence. Bound requests/processes, dispose clients before
cleanup, clean only test-owned collections and files, and verify cleanup without
masking a primary test failure. Verify source hashes remain unchanged around
operations intended to be read-only.

### Phase E: regression and documentation closure

Run from the worktree root after fixes:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test Knowledge.sln --no-build --no-restore
dotnet test tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj
dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj
dotnet test tests/Knowledge.Mcp.IntegrationTests/Knowledge.Mcp.IntegrationTests.csproj
```

Run focused affected tests during development. Run the final solution in Release
as CI does, with matching configuration on build and test. Asset-free tests must
remain runnable without Docker/model weights; live suites stay explicitly invoked.
Record platform-dependent filesystem coverage and any actual unverified platform
rather than claiming cross-platform execution from one workstation.

Update README, `AGENTS.md`, the documentation index, roadmap, directly affected
configuration/security/contracts guidance, and this document to reflect verified
behavior. Reconcile stale pending-language where later acceptance already closed
it while preserving dated historical evidence. Document operating procedures for
explicit refresh, missing assets/service, invalid profiles, interrupted indexing,
and whole-collection rebuild consequences. No automatic repair is implied.

## 5. POC acceptance matrix

The following is the execution checklist, not a declaration of completion.

| Criterion | Evidence to retain or complete |
|---|---|
| AC-001 Initial indexing | Existing Milestone 3 live evidence plus the representative synthetic indexing run |
| AC-002 Incremental no-op | Historical real-vault two-pass evidence plus zero-call/zero-write full-state comparison |
| AC-003 Change handling | Changed document rebuilt, unchanged neighbor untouched, failure retry demonstrated |
| AC-004 Deletion handling | Deleted, excluded, and opted-out transitions remove only intended points/results |
| AC-005 Rebuild | Actual CLI rebuild after deleting a disposable collection restores equivalent searchable content |
| AC-006 Semantic search | Retain reviewed Milestone 6 paraphrase/concept evidence and run synthetic search regression |
| AC-007 Exact technical-term baseline | Retain recorded exact-identifier 4/4 and acronym 3/3 results; no lexical superiority claim |
| AC-008 Provenance | Exact path, title, heading, IDs, and original Markdown assertions in CLI/MCP/rebuild checks |
| AC-009 MCP equivalence | Milestone 7 acceptance plus passing current live MCP regression |
| AC-010 Benchmark | Retain 24-question owner-reviewed 22/24 Hit@5 baseline and private artifact references |

Each final evidence row must identify its test or historical artifact, execution
date/revision, result, and any limitation. If a fix changes embedding-affecting
behavior, assess profile/schema consequences and revalidate the affected quality
evidence before closure; never silently reuse an incompatible index.

## 6. Completion gates

Milestone 8 and the POC may be marked complete only when:

- All Milestone 8 objectives and specification requirements have an audited
  evidence mapping, with no unresolved required behavior or acceptance gap.
- Focused regressions, full solution tests, and explicit live suites pass with
  zero build warnings/errors and actual counts recorded.
- Repeated rebuilds demonstrate equivalent complete content/identity and local
  vectors within the stated tolerance; post-failure retry converges correctly.
- Filesystem safety, eligibility, preflight, mutation gates, failure transparency,
  privacy, and local read-only boundaries have demonstrated evidence.
- All AC-001 through AC-010 are demonstrated, the reviewed baseline is retained,
  disposable resources are cleaned, and documentation matches implemented behavior.

Unavailable assets, Qdrant, or missing required evidence leave the corresponding
gate pending. Unit tests alone do not close live acceptance. Record any remaining
implementation issue explicitly rather than expanding into a later milestone.
