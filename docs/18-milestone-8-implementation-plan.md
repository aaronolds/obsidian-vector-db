# Milestone 8: Hardening and POC acceptance

## 1. Status and objective

Status: **complete; POC formally closed on 2026-10-30.** The final requirements
mapping, executed checks, accepted limitations, and closure decision are in
[section 7](#7-final-verification-and-poc-closure). Sections 2–6 retain the
original audit plan and dated intermediate evidence.

This closure was completed in a new managed worktree at base revision
`7245ccd` (including the earlier hardening PR #19). The original plan began in
`codex/milestone-8-hardening` from `c142c50`; those are historical identifiers,
not this final acceptance tree.

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
evaluation files remain absent. Asset digests had not yet been checked at that historical checkpoint;
all seven were verified during final acceptance.

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
availability when Qdrant is unavailable. At that checkpoint, the broader deterministic rebuild matrix,
filesystem/fault-injection audit, and final Release acceptance remained open.
Section 7 records their subsequent closure; these historical passes alone did
not close the POC.

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

## 7. Final verification and POC closure

**Decision: Milestone 8 is complete and the POC is formally closed on
2026-10-30.** All required POC acceptance criteria are demonstrated. There are
no outstanding required POC implementation or acceptance gates. The next work
is the designed, unexecuted [Milestone 9 answer-quality/agent evaluation](19-answer-quality-agent-evaluation-plan.md).
Hybrid retrieval is conditional on that evidence.

### 7.1 Acceptance environment and reproducibility

Executed from base `7245ccd` plus this closure's source/test changes, on macOS
27.2 arm64, .NET SDK `10.0.402`, runtime `10.0.13`, ONNX Runtime package
`1.31.1`, Qdrant server/client `1.20.0`, and MCP SDK `2.3.0`. No `KNOWLEDGE_`
overrides were present. The healthy shared Compose service remained bound only
to `127.0.0.1:6334`; it was neither stopped nor reconfigured.

The acceptance source/test fingerprint is
`c0760f714d2c036b85f48acd7b292297ee2871a19d96c207dac7d96d3304d498`.
Reproduce by sorting the tracked `src/` and `tests/` paths plus the new
`tests/Knowledge.Indexing.IntegrationTests/HardeningAcceptanceTests.cs`, retaining
`.cs`, `.csproj`, and `.json` files, then hashing each UTF-8 relative path,
NUL, raw file bytes, NUL in order with SHA-256. Documentation is excluded from
this fingerprint. The base revision alone does not identify the acceptance tree.

All seven asset digests matched `models/bge-small-en-v1.5/asset-manifest.json`.
The ONNX weights were reused through an ignored worktree symlink to the primary
checkout; no model was downloaded, copied into Git, or sent to a remote service.
The ignored link remains a local test prerequisite. Tests used synthetic
temporary vaults and unique disposable collections; their cleanup passed. The
new hardening test explicitly verified its collection was absent after cleanup.
The temporary baseline-audit utility was removed. No real vault notes or
existing user collections were mutated.

| Final command | Exit/result on 2026-10-30 |
|---|---|
| `dotnet restore Knowledge.sln` | 0 |
| `dotnet build Knowledge.sln --configuration Release --no-restore` | 0; zero warnings/errors |
| `dotnet test Knowledge.sln --configuration Release --no-build --no-restore` | 0; 194/194: Indexer 161, Embeddings 8, MCP 25; zero skipped |
| `dotnet test tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj --configuration Release --no-restore` | 0; 6/6; zero skipped |
| `dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj --configuration Release --no-restore` | 0; 5/5; zero skipped |
| `dotnet test tests/Knowledge.Mcp.IntegrationTests/Knowledge.Mcp.IntegrationTests.csproj --configuration Release` | 0; 1/1; zero skipped |

The explicit integration projects were restored on their first invocation.
Sandboxed .NET execution could not create required local IPC sockets, so final
builds/tests ran with approved host access. This was an execution-environment
constraint, not a passing test or product defect. No audit checks were disabled.

### 7.2 Audit findings and resolved gaps

| Gap/finding | Resolution and executed evidence |
|---|---|
| Uncertain non-RPC writes allowed subsequent stale cleanup | Prior PR #19 fix retained; `Unconfirmed_document_write_prevents_stale_document_cleanup`, partial-write and verification failure regressions pass |
| File/directory aliases bypassed target exclusions; an early excluded directory alias hid canonical eligible files | Apply both logical and physical include/exclude policy and track active directory ancestry; `Internal_aliases_cannot_bypass_target_eligibility_or_hide_canonical_notes` and reader alias path/ID regression pass |
| A file link's target introduced another linked ancestor, allowing containment/exclusion bypass | Walk all target path components after each link, bound resolution to 64 links, and repeat at read time; `File_link_targets_with_linked_ancestors_cannot_bypass_containment_or_exclusions` passes for excluded and external targets |
| Containment assumed macOS/Windows always ignore path case | Use conservative ordinal containment; probe confirmed this workstation's temporary filesystem is case-insensitive. Canonical reader case tests pass; other filesystem/platform execution is not claimed |
| Incompatible scalar settings escaped as raw exceptions from `chunks`, `embeddings`, and `index` | Safe CLI configuration catches return 2; `Incompatible_configuration_values_fail_safely_before_runtime_composition` passes for all seven commands with private-value/query sentinels |
| Rebuild evidence compared only selected metadata/results | New real ONNX/Qdrant test compares every point ID, complete semantic payload, and every vector component for two chunking configurations, two repeated rebuilds, and actual CLI restore after external test-collection deletion |
| Failure/mutation ordering and retry coverage incomplete | RPC-stage matrix, partial writes, cancelled partial writes, malformed YAML/fences/UTF-8, read-time redirection, discovery failure, and zero-storage-call preflight checks now pass |
| Unreadable root/subtree behavior not executed in earlier milestones | Disposable Unix permission smoke established actual permission denial before invoking Release `chunks`; root and nested-directory scans both returned 1 with safe `scan-failed` diagnostics. Shared discovery failure tests separately assert zero storage calls |

No embedding input formatter, model, tokenizer, normalization, chunking, index
profile, or schema changed. The eligibility fix affects only aliases that did
not satisfy the intended policy. A read-only audit using the current
`RetrievalBenchmarkSourceValidator.ValidateSources` validated all 24 approved
benchmark questions against 424 currently eligible candidate sources and found
zero descendant symlinks in the configured real vault. A separate read-only
comparison against the base revision's actual discovery implementation returned
the identical ordered set of 424 candidate paths. Therefore these link fixes
do not change that vault's retrieval inputs. This was an eligibility
audit, not a new retrieval run: the reviewed 22/24 result remains the historical
2026-10-24 baseline on its recorded versions and snapshot.

### 7.3 Evidence catalog

All current test references below passed in the final 2026-10-30 Release run
identified in 7.1. Historical evidence has its own recorded date/revision.

- **E1 — ingestion:** `IngestionTests`, particularly frontmatter/heading/link
  extraction, structured blocks, deterministic IDs, long prose, exclusions,
  malformed fences, external links/cycles, alias policy, and linked ancestors.
- **E2 — indexing and failure safety:** `IndexingTests`, including no-op/hash
  checks, profile/schema mutation gates, malformed replacements, stage-specific
  RPC failures, uncertain/partial writes, cancellation, read-time containment,
  missing vault, failed discovery, preflight retention, and later repair.
- **E3 — configuration and CLI:** `ConfigurationTests`, `CliTests`,
  `SearchTests`, `QdrantSearchTests`, and `RetrievalBenchmarkTests`. These assert
  safe invalid configuration, local-only settings, filters, provenance, safe
  failure channels at all supported log levels, metadata-only reports, and
  cancellation/report preservation.
- **E4 — embeddings:** tokenizer/formatter/query unit tests and all six real
  `OnnxEmbeddingTests`: pinned assets, token limits, reference token IDs/vectors,
  query instruction, repeatability, normalization, sample-vault inputs, and
  cancellation/session reuse.
- **E5 — live storage/rebuild:** all five `LocalIndexingTests`, including
  `Repeated_rebuilds_and_cli_restore_preserve_complete_payloads_vectors_and_sources`
  in `HardeningAcceptanceTests.cs` (two cases). Full snapshots scroll every
  point, compare map values independent of ordering, and assert finite
  384-dimensional vectors within `1e-5`; unchanged state is exact. Source bytes
  and mtimes are fixed for rebuild comparisons. A single-result filtered query
  avoids cutoff ties; score tolerance is `1e-5`. The test also covers changed
  neighbors, rename delete/add, deletion/exclusion/opt-out, no-op convergence,
  retained state on failed preflight/bad assets, and exit 130 for every CLI
  command with unchanged full point snapshots.
- **E6 — MCP:** `MarkdownNoteReaderTests`, `KnowledgeMcpToolsTests`, and the live
  `McpServerIntegrationTests`: canonical selectors, current note reads,
  eligibility, path/ID parity, only two read-only tools, protocol-only stdout,
  safe errors, CLI equivalence, filters/vault isolation, full payload/vector
  and source read-only snapshots, and independent note availability/retry.
- **E7 — real-vault retrieval and client acceptance:** retained
  [Milestone 3](12-milestone-3-implementation-plan.md) real-vault two-pass
  indexing (402 unchanged documents, zero embedding calls/chunk writes),
  [Milestone 6 baseline](../eval/dense-baseline.md) at revision `a456098` plus its
  recorded patch on 2026-10-24 (24 questions, 22 hits, owner review), and
  [Milestone 7 section 9.6](17-milestone-7-implementation-plan.md#96-verification-and-acceptance-closure)
  on 2026-10-25 (Codex CLI stdio client acceptance). No launch/protocol changes
  required repeating that manual client smoke.
- **E8 — local boundary/manual checks:** all seven manifest digests, loopback
  Compose inspection, temporary-filesystem casing probe, confirmed unreadable
  root/subtree smoke, and read-only real-vault benchmark eligibility audit above.
  These are executed local observations, not portable automated assertions.

### 7.4 Functional and nonfunctional requirement audit

Grouped rows name every requirement explicitly. No roadmap feature substitutes
for demonstrated implemented behavior.

| Requirement | Passing evidence and assertion |
|---|---|
| FR-001 discovery | E1 recursive fixtures; E2 complete-discovery gate; E8 permission failures |
| FR-002 include/exclude | E1 exclude-wins and alias-target fixtures; E5 excluded transition |
| FR-003 opt-out | E1 frontmatter; E5 indexed-to-opted-out removal; E6 current note eligibility |
| FR-004 frontmatter | E1 tags/aliases/control extraction; E5 stored aliases |
| FR-005 Markdown structure | E1 nested headings, prose, lists, quotes, tables, code, inline identifiers; E5 representative corpus |
| FR-006 wikilinks | E1 target/display extraction; E5 original and normalized payloads |
| FR-007 chunking | E1 long prose/structure; E5 two multi-chunk configurations |
| FR-008 token limit | E4 exact tokenizer and 512/513-token checks before inference |
| FR-009 embedding context | E4 canonical title/heading/tag/content formatter assertion |
| FR-010 local embeddings | E4 real pinned ONNX inference; E8 asset digests |
| FR-011 Qdrant vectors | E5 real collection layout, 384 finite cosine vectors, and incompatible-layout recreation checks |
| FR-012 provenance metadata | E5 complete payload equality and search fields; E6 full point snapshots |
| FR-013 document identity | E1/E2 deterministic IDs; E5 repeated rebuild and rename identities |
| FR-014 chunk identity | E1/E2 deterministic chunks/point mapping; E5 all point/chunk IDs |
| FR-015 file hashes | E2 timestamp-only no-op and same-timestamp byte-change detection; E5 source hashes |
| FR-016 skip unchanged | E2 no embedding/upsert; E5 exact full-state no-op; E7 real-vault second pass |
| FR-017 replace changed | E2 delete-before-parse failure/retry; E5 changed source with neighbor equality |
| FR-018 remove stale | E2 stale deletion; E5 deleted/excluded/opted-out paths absent from points and search |
| FR-019 rebuild | E5 two repeated rebuilds and actual CLI restore after deletion |
| FR-020 status | E3 safe unavailable state without model assets; E5 vault-scoped paginated counts and malformed identity |
| FR-021 CLI search | E3 complete CLI provenance; E5 successful restored search; E7 real benchmark |
| FR-022 MCP search | E6 stdio tool discovery/invocation and live search |
| FR-023 MCP note retrieval | E6 current path/ID reads, unavailable assets/service independence |
| FR-024 filters | E3 AND tags/vault/path/limits; E6 actual CLI/MCP explicit/default parity |
| FR-025 search provenance | E3 path/title/heading/Markdown/score output; E5/E6 live assertions |
| NFR-001 local-first | E4/E5 local ONNX/Qdrant; E6 local stdio; E8 loopback service |
| NFR-002 no embedding egress | Local-only provider validation in E3; E4 pinned inference; no remote fallback/download path |
| NFR-003 rebuildability | E5 deleted collection restored from unchanged source hashes |
| NFR-004 determinism | E5 complete IDs/content/payload equality, vectors within `1e-5`, fixed source mtimes |
| NFR-005 private logging | E2/E3 synthetic parser/configuration/exception/query sentinels; E6 MCP errors/protocol; reports omit bodies/queries |
| NFR-006 failure transparency | E2 failed path and safe diagnostic codes, non-complete results; E3/E5 CLI exits and retries |
| NFR-007 configurability | E3 typed YAML/environment precedence/ranges; E5 two chunk configurations |
| NFR-008 embedding abstraction | Core's `IEmbeddingService` has no infrastructure dependency; E2 fakes and E4 ONNX implement the same contract |
| NFR-009 retrieval abstraction | MCP composes shared retrieval through `IKnowledgeRetriever`; E3 injected retriever and E6 live parity |

### 7.5 POC acceptance and hardening objective audit

| Criterion | Accepted evidence |
|---|---|
| AC-001 initial indexing | E5 creates/indexes representative eligible notes; E7 real-vault indexing |
| AC-002 incremental no-op | E5 zero calls/writes and exact state; E7 real-vault no-op |
| AC-003 change handling | E2 replacement/retry and E5 unchanged neighbor equality |
| AC-004 deletion handling | E5 deletion, exclusion, opt-out, and empty search after removal |
| AC-005 rebuild | E5 actual `KnowledgeCli.RunAsync` rebuild after disposable collection deletion restores full state/search |
| AC-006 semantic search | E7 reviewed conceptual 4/4, paraphrased 2/3; E5 synthetic paraphrased search |
| AC-007 technical terms | E7 reviewed exact identifiers 4/4 and acronyms 3/3; no lexical-superiority claim |
| AC-008 provenance | E3/E5/E6 full path, title, heading, IDs, original Markdown and score |
| AC-009 MCP equivalence | E6 current live actual CLI/shared retriever/MCP parity; E7 intended client |
| AC-010 benchmark | E7 24 verified questions, owner-reviewed 22/24 Hit@5 (91.7%) |

| Milestone 8 objective | Accepted evidence |
|---|---|
| Error handling | E2 fault stages, incomplete writes, retry; E3 safe CLI exit mapping |
| Path traversal protection | E1/E6 canonical selectors and direct/indirect external link rejection |
| Exclusion rules | E1/E5/E6 logical and physical policy, opt-out, removed-result transitions |
| Privacy-safe logging | E2/E3/E6 sentinels and safe operational channels; E8 local boundary |
| Configuration validation | E3 all seven CLI invalid-scalar paths, typed validation before runtime; E5 bad assets retain full state |
| Qdrant unavailability | E2 injected RPC stages; E3 bounded unreachable status; E6 live unreachable endpoint, surviving note reads and retry |
| Malformed notes | E1/E2 YAML, unclosed fences, invalid UTF-8, safe neighbor continuation, repair |
| Deterministic rebuild | E5 two configurations, complete state comparisons, CLI restore, source preservation |

### 7.6 Accepted limits and operating handoff

This closes the local single-owner dense-retrieval POC, not a production service
or an agent answer-quality claim. Platform execution here is macOS arm64 on a
case-insensitive filesystem; no Windows, Linux, or case-sensitive-volume live
execution is claimed by this closure. Containment is conservative but not atomic
under concurrent filesystem swaps. Eligible internal aliases can still produce
multiple logical identities; deduplication remains deferred.

Document replacement and whole-collection rebuild remain nontransactional as
ADR-009 specifies. After an interruption, rerun `index` to repair incomplete
documents, or explicitly `rebuild` a dedicated collection. Rebuild removes every
vault ID in the selected collection. Missing assets, unavailable Qdrant, invalid
profiles, refresh after edits, and alias-policy upgrades are covered in the
[README operating procedure](../README.md#recovery-and-refresh).

The retained relevance baseline is a small, reviewed retrieval sample, not an
answer-quality score, broad quality guarantee, or SLA. Watching, hybrid/reranking,
graph expansion, remote/multi-user deployment, authentication, attachment
indexing, and write-back remain outside implemented POC scope. Milestone 9 will
measure answers/tool use before a next-improvement decision.
