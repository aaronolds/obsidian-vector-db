# Milestone 7: MCP server

Implementation status: implemented on 2026-10-23; acceptance completed on
2026-10-25. Asset-free tests, live Qdrant/ONNX search equivalence, and a Codex CLI
stdio-client smoke test passed. Section 9.6 records commands and evidence.
Planning and implementation date: 2026-10-23.

## 1. Outcome and scope

Implement Milestone 7 from [the delivery plan](05-implementation-plan.md): expose
`search_knowledge` and `get_note` through a local MCP server, with search using
the same `IKnowledgeRetriever` implementation as the CLI. Demonstrate POC
FR-022–024 and AC-009, following the architecture, canonical contracts, security
requirements, and ADR-002, ADR-005, ADR-006, ADR-007, and ADR-008.

Preserve local BGE-small-en-v1.5, dense retrieval, existing schemas and index
profiles, configured vault isolation, and the indexer as sole writer. No
architectural decision changes are proposed. Record any infeasibility and its
evidence before proposing a design departure.

Included: shared configuration/retrieval composition, a stdio host, the two
tools, safe current-note access, protocol and behavioral tests, live CLI/MCP
equivalence checks, and operating documentation. Path containment and privacy
checks needed to expose these tools safely belong in this milestone, even
though broader hardening is Milestone 8.

Excluded: HTTP/remote hosting, authentication, write tools, indexing from MCP,
watching, hybrid search, reranking, graph expansion, model/chunking changes,
arbitrary filesystem access, extra MCP resources/prompts, and Milestone 8's
general hardening backlog.

Milestone 6 acceptance was completed on 2026-10-24 with an owner-reviewed
22/24 Hit@5 dense baseline. Milestone 7 closure uses the existing retrieval
service and does not authorize retrieval tuning or repeat that benchmark.

## 2. Starting point and project ownership

Historical starting state at revision `a456098` on 2026-10-23:

- `Knowledge.Mcp` is a `net10.0` library shell referencing Core. There is no MCP
  SDK, executable host, tool registration, or MCP test project.
- Core already owns `IKnowledgeRetriever`, search records, request validation,
  identity functions, and schema constants. Keep it dependency-free.
- `QdrantKnowledgeRetriever` already validates collection/schema/profile/runtime
  compatibility and filters by configured vault ID. Reuse it without changing
  ranking, filtering, or compatibility semantics.
- `KnowledgeCli.RunSearchAsync` and `RunEvaluateAsync` compose the tokenizer,
  ONNX service, query embedding service, Qdrant client, and retriever. Their
  options/YAML loader/validator and `IndexProfile.Create` currently live in
  `Knowledge.Indexer`, which the MCP host should not reference as an executable.
- `MarkdownIngestor` already owns discovery, include/exclude rules, symlink
  containment, snapshot reads, parsing, and opt-out handling. Reuse these rules;
  expose a metadata/read path without chunking rather than adding a second
  frontmatter parser or scanner in MCP.

Add one small shared infrastructure project, `Knowledge.Runtime`, for the
existing configuration code, unchanged index-profile construction, and common
read-only retrieval composition. Both entry points reference it. This is an
implementation packaging choice to share existing behavior, not a new domain
layer or a change to an accepted ADR. Index orchestration, CLI rendering, and
evaluation remain in Indexer; Markdown, embeddings, and Qdrant retain their
current responsibilities. MCP tool handlers depend on domain services, with
infrastructure construction confined to host composition.

Add an infrastructure-free `IKnowledgeNoteReader` and note request/result
records to Core. Implement the reader in Markdown. Keep MCP protocol DTOs,
serialization, SDK attributes, and tool error mapping in `Knowledge.Mcp`.

## 3. Tool contracts

### `search_knowledge`

Use the camel-case contract in
[the canonical contracts](03-domain-and-data-contracts.md#11-mcp-tool-search_knowledge):

| Input | Behavior |
|---|---|
| `query` | Required nonblank string; existing trimming, BGE query formatting, and token-limit validation |
| `limit` | Optional integer; omission uses `retrieval.defaultLimit`; explicit values must be in `1..retrieval.maxLimit` |
| `pathPrefix` | Optional vault-relative literal prefix; existing normalization and case-sensitive matching |
| `tags` | Optional string array; existing exact, case-sensitive AND semantics and duplicate normalization |

Return `{ "results": [...] }` containing the existing result fields:
`documentId`, `chunkId`, `path`, `title`, `headingPath`, `contentMarkdown`, and
`score`. Preserve service order, Markdown, identity, and numeric score precision.
An empty result set is a successful response. Do not overfetch, deduplicate,
introduce a threshold, or reconstruct chunks from current files.

Use the shared request validator and BGE query service; do not parse CLI output
or reproduce retrieval logic in the tool. Vault ID, collection, and model come
only from configuration. Clients cannot select another root/collection/vault or
supply vectors, Qdrant filters, or retrieval strategy. Retain the documented
score field without exposing additional ranking internals or diagnostics.

### `get_note`

Accept exactly one nonblank `path` or `documentId`. Reject neither/both, wrong
types, and malformed identifiers. `documentId` must use the existing canonical
`sha256:` identity format; do not invent a new ID algorithm.

- `path` is a canonical vault-relative Markdown path using `/` and discovered
  casing. Reject absolute/drive/UNC paths, backslashes, control characters,
  empty/dot/parent segments, and URI forms. Do not URL-decode requests, lowercase
  paths, resolve by basename/title, or treat a path as a glob.
- Reuse fresh discovery and configured include/exclude rules. Match a path
  ordinally, or compute `KnowledgeIdentity.DocumentId(configuredVaultId,
  relativePath)` for discovered candidates to resolve an ID. Use a per-call
  in-memory lookup; no catalog, Qdrant dependency, or persistent cache is needed.
  Incomplete discovery is an operational failure, not a successful not-found.
- Check resolved root, all ancestor links, and the final target before reading.
  Allow discovered internal links only when the resolved target remains inside
  the vault; reject escapes, broken links, and cycles. Reuse/factor the Markdown
  containment helpers, covering any missing ancestor checks with regression
  tests. Do not rely on a lexical prefix test alone.
- Read one source snapshot, decode using existing UTF-8 behavior, and use shared
  parsing for title and the configured opt-out field. Excluded and opted-out
  notes are unavailable through either selector. Do not chunk or embed the note.
- Return current complete Markdown as `contentMarkdown`, including frontmatter
  and original line endings, plus `documentId`, canonical `path`, `title`, `fileHash`, and
  `modifiedUtc`. Use `{ "found": true, "note": { ... } }`; an empty eligible
  note is valid. Use `{ "found": false, "note": null }` for missing, deleted,
  renamed, excluded, or opted-out notes, without exposing their contents.
- Unreadable files, malformed frontmatter/encoding, and ambiguous identity
  resolution fail safely. Never substitute stale Qdrant content. Current note
  text may differ from indexed search text; the returned hash identifies the
  snapshot read. Do not claim transactional consistency with concurrent edits.

Factor metadata extraction out of ingestion so the reader and indexer use the
same parser and opt-out rules without tokenizing/chunking for `get_note`. The
refactor must preserve existing metadata, chunk IDs, chunk boundaries, hashes,
and index-profile bytes. Document the concrete note response shape in the
canonical contracts during implementation.

## 4. Host, transport, and lifecycle

Convert `Knowledge.Mcp` to a separate .NET 10 executable. Select and centrally
pin a compatible stable version of the official `ModelContextProtocol` package
in `Directory.Packages.props`; validate .NET 10, tool schema generation, and
the intended local client's protocol support before committing the pin. Do not
add ASP.NET/HTTP packages for the stdio host.

Use local stdio, the recommended POC default in
[the open questions](08-open-questions-and-assumptions.md#27-mcp-transport).
The official SDK provides
[stdio hosting](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md)
and [tool/schema support](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/tools/tools.md).
Verify behavior against the pinned release rather than assuming current `main`
APIs. Explicitly register only the two named tools, mark them read-only and
non-destructive, and describe returned note text as untrusted source data.

Proposed invocation after implementation:

```sh
dotnet run --no-build --project src/Knowledge.Mcp/Knowledge.Mcp.csproj -- \
  --config knowledge.local.yaml
```

Support only `--config <path>` and standalone `--help`/`-h`; default to
`knowledge.yaml`. Reject unknown, missing, and repeated options. Preserve
`KNOWLEDGE_` overrides, configuration-file-relative vault/asset resolution, and
existing typed validation before network access. Avoid allowing generic host
argument binding to bypass the YAML/override rules. Help requires no assets,
vault, or Qdrant and exits without starting a protocol session.

Keep stdout exclusively for MCP messages while serving. Send help, usage, and
sanitized diagnostics to stderr; no banners, rendered search text, build output,
or generic-host startup messages may enter the protocol stream. Document a
client launch using `dotnet` plus an absolute built DLL path and absolute config
path, so runtime startup does not restore/build or depend on the client's cwd.

Load/validate configuration once. Initialize search resources lazily on the
first valid search, then reuse one owned tokenizer/ONNX session/Qdrant client per
host. Missing assets or unavailable Qdrant must not prevent tool discovery or
`get_note`; do not cache a failed search initialization permanently. Keep CLI
validation behavior intact when extracting configuration. Preserve existing
ONNX inference locking and test overlapping calls, first-use initialization,
cancellation, and disposal without introducing a new scheduling framework.

Propagate request cancellation to retrieval and filesystem work at available
boundaries. On stdin closure, client disconnect, or process cancellation, stop
accepting requests, cancel/drain active operations, and dispose resources once
they are no longer in use. Preserve the existing Qdrant timeout behavior.
No implicit indexing, collection creation, payload changes, or repairs.

## 5. Errors, privacy, and trust boundary

Use protocol errors for malformed protocol requests or unknown tools. Translate
well-formed tool calls with invalid domain arguments, incompatible/missing
collections, asset errors, vault read failures, or unavailable retrieval into
sanitized MCP tool errors (`isError: true`). Do not report failures as empty
searches or ordinary missing notes. Preserve SDK cancellation semantics rather
than swallowing cancellation as a retrieval miss. A failed call must leave the
host usable for subsequent requests.

Use stable error codes such as `invalid_request`, `index_incompatible`,
`assets_unavailable`, `retrieval_unavailable`, and `note_unavailable`, with short
actionable messages. Do not return exception strings, stacks, filesystem/server
error bodies, query text, or note text in errors. Startup uses existing process
conventions: usage/configuration failure `2`, operational failure `1`, normal
shutdown `0`, and explicit user cancellation `130` where observable.

Keep `logContent` and `logQueries` false. Configure host/SDK logging so enabling
an allowed level cannot dump request arguments, response bodies, embeddings, or
transport frames to stderr, files, telemetry, or client log notifications. Test
SDK binding failures and unexpected exceptions as well as application errors.
Only successful requested tool results carry note content. Provenance stays
attached to that untrusted data, never presented as system instructions.

## 6. Implementation sequence

1. **Extract shared setup.** Add `Knowledge.Runtime`; move configuration and
   index-profile construction without semantic changes; share retrieval creation
   and ownership between CLI/evaluator/MCP. Preserve asset-free factory seams.
   Prove identical profile fingerprints and existing CLI behavior before tools.
2. **Add authoritative note reads.** Define Core contracts, factor Markdown
   metadata parsing/containment helpers, and implement path/ID resolution with
   current inclusion and opt-out checks. Cover it without model assets/Qdrant.
3. **Add the stdio host.** Pin the SDK, add argument parsing and safe logging,
   register explicit tools and lazy search resources, and implement lifecycle.
4. **Implement tool adapters.** Map validated requests to shared services,
   serialize stable camel-case results, and translate safe errors. Assert actual
   advertised schemas and serialized output, including optional/null behavior;
   provide structured results and matching JSON text content for clients that
   consume text. Keep SDK types outside Core and Markdown.
5. **Verify through an MCP client.** Add offline protocol coverage and explicit
   live Qdrant/ONNX equivalence tests, then demonstrate the built executable in
   an intended local MCP client. Record versions, outcomes, and limitations.
6. **Document implemented behavior.** Update README, configuration and domain
   contracts, security operating notes, project map, agent guidance, roadmap,
   and this plan's evidence/status. Preserve Milestone 6's separate acceptance
   evidence and current completed status.

## 7. Validation and acceptance

Add `tests/Knowledge.Mcp.Tests` to the normal solution for asset-free tool,
host, and SDK protocol tests using injected fakes and temporary vaults. Add
`tests/Knowledge.Mcp.IntegrationTests` as a separate explicit suite, excluded
from normal solution tests like the existing integration projects. The latter
launches the production executable over stdio with real pinned assets/Qdrant.

| Area | Required coverage |
|---|---|
| Shared setup | Same YAML/env/path rules and validation; unchanged profile fingerprints; existing status/chunks/search/evaluate/index/rebuild tests pass |
| Search | Default/explicit limits; blank/oversized query; prefix normalization; exact AND tags; ordering, scores, IDs, headings, and Markdown preserved; empty results; no index writes |
| Note reads | Path and ID lookup; neither/both selectors; malformed ID; live edits/deletion/rename; excluded/opted-out/custom opt-out; empty notes; original frontmatter/line endings; UTF-8/parser/read failures |
| Containment | Traversal in both separator forms; absolute/drive/UNC/URI paths; casing; sibling-prefix escape; internal/external file and ancestor links; broken/cyclic links; link retarget before read; ID lookup cannot bypass eligibility |
| Protocol | SDK connection/discovery; exactly two tools; input/output schemas and camel casing; successful calls and tool errors; invalid argument types; unknown tools; stdout has only protocol frames |
| Lifecycle/privacy | Missing assets/Qdrant still permit discovery/note reads; initialization retry and overlapping calls; cancellation/disconnect/EOF; clean disposal; query/note/server-error sentinels absent from all logs and error responses |

Run the required solution checks from the repository root during implementation:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test Knowledge.sln --no-build --no-restore
```

For live tests, install the existing pinned assets explicitly and start local
Qdrant only when needed:

```sh
docker compose -f docker/docker-compose.yml up -d
dotnet test tests/Knowledge.Mcp.IntegrationTests/Knowledge.Mcp.IntegrationTests.csproj
```

Use a synthetic temporary vault and unique disposable collection indexed by
the existing indexer. Against the same configuration and unchanged corpus,
compare CLI/shared-service and MCP search results field-for-field, allowing
numeric tolerance only for independently executed inference. Cover filters,
empty results, incompatible/missing collections, and foreign-vault points.
Use fixtures without tied cutoff scores; acknowledge existing tie-cutoff
limitations rather than changing ranking. Confirm note reads reflect source
edits even before reindexing and work without Qdrant/model availability.

Capture collection metadata/point payloads and source hashes before/after calls
to establish read-only behavior, and inspect RPC activity for mutations where
the test harness permits. Clean up only test-created collections and files.
Rerun the existing indexing integration suite because shared composition and
Markdown reading change; model inference behavior itself remains unchanged.

Milestone 7 is complete when a real MCP client discovers and calls both tools,
search returns provenance-equivalent ranked results to the CLI (AC-009), current
note retrieval enforces eligibility/containment, and tests demonstrate read-only
operation and privacy-safe errors/logging. Record the actual client/SDK versions
and verification commands. Missing assets, Qdrant, or client verification leaves
the corresponding live acceptance pending; unit tests alone cannot close it.

## 8. Implementation and verification evidence

Implemented:

- Added `Knowledge.Runtime` and moved the existing configuration/YAML loader,
  typed options/validator, and index-profile creation there. The CLI and
  evaluator now use shared local search composition. The profile algorithm and
  serialized fields were not changed; the existing indexer/profile tests pass.
- Added Core note-read contracts and a Markdown reader that applies configured
  discovery rules, resolves path or canonical document ID, checks each path
  component for vault containment, rechecks every ancestor link before reading,
  uses shared
  frontmatter metadata parsing for title/opt-out, and returns current original
  Markdown with hash and modification time. Note reads do not tokenize, chunk,
  load model assets, or connect to Qdrant.
- Converted `Knowledge.Mcp` to a .NET 10 stdio executable using the official
  `ModelContextProtocol` SDK 2.3.0. It advertises exactly the two read-only tools,
  emits structured and JSON text results, performs lazy reusable local search
  initialization, and sends no console log output to the protocol stream.
- Added `Knowledge.Mcp.Tests` to the solution. It exercises request/error
  mapping, schemas and read-only tool annotations, asset-free in-process MCP
  calls, and a child-process stdio handshake proving that `get_note` works
  without model weights or Qdrant. Added the separate explicit live suite
  `Knowledge.Mcp.IntegrationTests` for synthetic-vault indexing, foreign-vault
  isolation, and CLI/MCP result parity.
- Updated README, architecture index, contracts, configuration/security
  guidance, roadmap, and `AGENTS.md` to match the implemented behavior.

Verification on 2026-10-23:

- `dotnet restore Knowledge.sln` succeeded.
- `dotnet build Knowledge.sln --no-restore -m:1 -p:UseSharedCompilation=false`
  succeeded with zero warnings or errors.
- `dotnet test Knowledge.sln --no-build --no-restore` passed: Indexer 120/120,
  Embeddings 8/8, and MCP 21/21 (149 tests total).
- `dotnet build tests/Knowledge.Mcp.IntegrationTests/Knowledge.Mcp.IntegrationTests.csproj
  --no-restore -m:1 -p:UseSharedCompilation=false` compiled successfully with
  zero warnings or errors.
- Live MCP search/CLI equivalence was not run. The pinned `model.onnx` weights
  are absent, and Docker could not access its local daemon socket in this
  environment. The integration test is ready for a workstation with those
  prerequisites; live acceptance remains pending.
- Milestone 6's real-vault benchmark, baseline, and manual review were still
  pending at this historical verification point; they closed on 2026-10-24.

At the 2026-10-23 implementation handoff, Milestone 7's live acceptance was
still open. That verification is historical; Milestone 6 subsequently closed on
2026-10-24 and Milestone 7 closed on 2026-10-25. The broader POC still requires
its remaining acceptance work.

## 9. Milestone 7 acceptance closure plan

Plan and execution date: 2026-10-25. Status: complete; evidence follows in
section 9.6.
Work starts from revision `911d15d` on branch `codex/milestone-7-closure` in a
dedicated managed worktree.

### 9.1 Planning baseline before execution

The following notes record the environment and coverage audit before acceptance
work began. They explain the test setup and are superseded by section 9.6.

- .NET SDK `10.0.402` is available. Local Compose reports Qdrant `v1.19.2`
  running, with only `127.0.0.1:6334` published. Docker socket access requires
  the execution environment's additional permission; it is not evidence of a
  missing service.
- Pinned model weights and the private local configuration exist in the primary
  checkout. Ignored files were not copied into the new worktree: its tokenizer
  exists, but its ONNX file and `knowledge.local.yaml` do not. Presence is not
  digest verification or proof that a configuration is suitable for acceptance.
- The live MCP suite currently contains one unexecuted acceptance test. It
  indexes a synthetic vault and a foreign vault into a unique collection,
  compares filtered MCP results with the shared CLI retriever field-for-field,
  reads a note by path and ID, checks a source edit, and compares indexed chunk
  metadata afterward. It does not launch the CLI entry point, compare full
  stored payloads/vectors, or demonstrate an intended desktop MCP client.
- Existing offline tests provide useful coverage, but section 7 is the required
  coverage matrix, not proof every case is already tested. Audit actual tests
  before adding coverage or claiming closure.
- `src/Knowledge.Mcp/README.md` still describes a library shell. The roadmap
  also carries the historical missing-assets explanation. Correct those during
  documentation closure.

### 9.2 Phase A: prepare an isolated, reproducible environment

1. Verify the existing local ONNX and tokenizer digests using the pinned runtime
   asset validation. Reuse those assets locally, without downloading or
   committing weights. The live suite currently resolves the model relative to
   its repository root; place an ignored local copy or symlink at that expected
   path and verify resolution from this worktree. Prefer this over adding new
   production configuration solely for acceptance.
2. Inspect only necessary configuration fields. Use temporary synthetic vaults
   and unique `knowledge-mcp-test-<id>` collections for automated checks. Use a
   separate ignored acceptance configuration with absolute paths for the client
   smoke test. Never point it at the deleted Milestone 6 collection or rebuild
   an existing user collection.
3. Confirm Qdrant health over local gRPC. Start the checked-in Compose service
   only if needed; do not recreate a healthy service or change its bindings.
   Check relevant `KNOWLEDGE_` overrides so inherited settings cannot redirect
   test/client activity into another collection or vault.
4. Restore and build the solution and separately restore the integration
   projects. Record revision, SDK/runtime, pinned assets, SDK package version,
   Qdrant version, command exit codes, and any actual blocker.

Deliverable: verified local prerequisites and a disposable test environment.
No modification to real Markdown or existing collections is required.

### 9.3 Phase B: audit and close acceptance coverage gaps

Map each section 7 requirement to an actual assertion in the unit or live
suite. Add focused tests where required behavior lacks evidence; fix only
failures within the existing MCP contracts and project boundaries.

| Gate | Required execution evidence |
|---|---|
| Search parity | Default and explicit limits, path prefix, exact AND tags, empty results, and foreign-vault isolation; compare rank, IDs, path, title, heading, Markdown, and score with numeric tolerance only for independent inference |
| CLI boundary | Compare at least one MCP request against the actual CLI search entry point under the same configuration/corpus; retain the shared-retriever comparison for precise field assertions |
| Safe failure and recovery | Missing/incompatible collection, unavailable Qdrant, missing assets, invalid/binding arguments, and subsequent usable calls; discovery and note reads remain available without retrieval prerequisites |
| Current note access | Path/ID equivalence, edits before reindexing, deletion/rename, opt-out/exclusion, empty notes, encoding/parser failures, and containment/link checks; offline coverage may establish filesystem cases |
| Protocol and lifecycle | Exactly two advertised tools, actual input/output schemas, matching structured/text results, unknown tools, cancellation, concurrent/first-use calls, initialization retry, EOF/disconnect, and resource disposal |
| Read-only and privacy | Unchanged source bytes and complete indexed state during read-only calls; protocol-only stdout, sanitized errors, and no query/note/server-error sentinels in stderr or log notifications |

For index stability, snapshot all points belonging to both fixture vault IDs,
including IDs, complete payloads, and vectors, before and after tool calls.
Compare in memory or hash canonical sorted state; report only metadata/digests.
Existing indexed-chunk metadata equality alone is weaker than full state
equality. Inspect mutation RPCs if the harness supports it. Keep intentional
fixture edits outside unchanged-source comparison windows, and assert the index
stays unchanged across those edits and subsequent note reads.

Use deterministic synthetic content without tied cutoff scores. Failure tests
must target isolated fixtures or an unreachable test endpoint; do not stop the
shared local Qdrant service. Bound process/request timeouts, dispose clients
before cleanup, and delete only collections created by that test, including
after failures. Preserve primary failures when reporting cleanup problems.

Deliverable: coverage mapping, focused fixes/tests if needed, and live evidence
for AC-009, privacy, and read-only operation. Avoid expanding into Milestone 8.

### 9.4 Phase C: run verification and demonstrate the intended client

Run from the new worktree after environment preparation and any focused fixes:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test Knowledge.sln --no-build --no-restore
dotnet test tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj
dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj
dotnet test tests/Knowledge.Mcp.IntegrationTests/Knowledge.Mcp.IntegrationTests.csproj
dotnet build src/Knowledge.Mcp/Knowledge.Mcp.csproj --configuration Release
```

The three integration suites are separate from normal solution tests. Record
actual test totals and outcomes; a successful build cannot substitute for a live
run. Resolve restore/environment failures explicitly rather than silently
omitting checks. Repeat affected checks after fixes.

Then identify the intended installed local MCP client and verify that it can
launch this stdio server. Use a dedicated temporary client entry with the built
Release DLL and absolute configuration path, launched from outside the repo.
Do not overwrite unrelated client settings. Demonstrate:

1. Tool discovery returns exactly `search_knowledge` and `get_note`.
2. Search of the indexed synthetic fixture returns the expected provenance and
   agrees with the CLI under the same configuration and source/index snapshot.
3. `get_note` succeeds by path and returned document ID, then reflects an
   intentional synthetic-source edit before reindexing.
4. Invalid input produces a sanitized error and a subsequent valid call works.
5. Disconnect/reconnect shuts down and restarts cleanly without protocol noise.

Record the client name/version, MCP SDK version, supported handshake/protocol
details, launch shape, and outcomes. Keep request/response content out of
committed evidence. If client access or compatibility prevents this check,
retain that gate as pending and describe the exact missing step; automated SDK
client tests alone do not satisfy the intended-client demonstration.

Deliverable: passing automated results plus an actual intended-client smoke
test. Remove only temporary client entries and test-created collections/files
after the clients and server are stopped; verify collection deletion.

### 9.5 Phase D: record evidence and close the milestone

Update this document with dated commands, versions, test counts, parity and
read-only/privacy evidence, client demonstration, and verified cleanup. Keep
private logs/configuration under ignored local paths and commit no source text,
queries, vectors, model weights, or build output.

Once every gate passes, reconcile `README.md`, `AGENTS.md`, the Milestone 7
roadmap entry, and `src/Knowledge.Mcp/README.md`. Review the related domain,
configuration, and security docs and change them only if actual behavior or
operating guidance requires it. Preserve the historical verification evidence
and Milestone 6's reviewed baseline. Do not claim Milestone 8 or the full POC is
complete. Review the final diff for unrelated changes and private artifacts.

| Closure gate | Completion condition |
|---|---|
| Prerequisites | Pinned assets validated; local Qdrant and isolated configuration verified |
| Regression | Solution and explicit live suites pass with recorded counts/exit codes |
| Search equivalence | CLI and MCP produce equivalent ranked content/provenance with required filters and vault isolation |
| Note access | Current eligible Markdown reads work with validated containment and independent retrieval availability |
| Privacy/read-only/lifecycle | Coverage audit closed and execution evidence recorded; source/index snapshots match where expected |
| Intended client | Built Release server discovered and both tools successfully exercised; client/version recorded |
| Cleanup/documentation | Only disposable resources removed, removal verified, and status documents agree |

No new benchmark, retrieval tuning, watcher, remote transport, or general
hardening work was part of this closure.

### 9.6 Verification and acceptance closure

Executed on 2026-10-25 in this worktree. Pinned asset digests matched the
checked-in manifest: ONNX model
`828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35` and
tokenizer vocabulary
`07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3`. The
.NET SDK was `10.0.402`; .NET runtime `10.0.13`; MCP SDK package
`ModelContextProtocol` `2.3.0`; Qdrant `1.19.2`; Codex CLI `0.162.1`.

The local Qdrant Compose service remained healthy with its gRPC port bound to
`127.0.0.1:6334`. No `KNOWLEDGE_` overrides were set. The worktree model link,
synthetic vault/configuration, client event log, and cleanup utility were local
test aids and have been removed. No real vault notes or existing collections
were changed.

Final verification completed successfully:

```text
dotnet restore Knowledge.sln                                          exit 0
dotnet restore tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj -p:NuGetAudit=false
                                                                      exit 0
dotnet restore tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj -p:NuGetAudit=false
                                                                      exit 0
dotnet restore tests/Knowledge.Mcp.IntegrationTests/Knowledge.Mcp.IntegrationTests.csproj -p:RestoreIgnoreFailedSources=true
                                                                      exit 0
dotnet build Knowledge.sln --no-restore                               exit 0; 0 warnings, 0 errors
dotnet test Knowledge.sln --no-build --no-restore                     exit 0; 164/164
dotnet test tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj --no-restore
                                                                      exit 0; 6/6
dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj --no-restore
                                                                      exit 0; 3/3
dotnet test tests/Knowledge.Mcp.IntegrationTests/Knowledge.Mcp.IntegrationTests.csproj --no-restore
                                                                      exit 0; 1/1
dotnet build src/Knowledge.Mcp/Knowledge.Mcp.csproj --configuration Release --no-restore
                                                                      exit 0; 0 warnings, 0 errors
```

The MCP integration test now compares explicit and default search result order
against the actual `KnowledgeCli.RunAsync` search command and the shared
retriever. It covers path-prefix and exact AND-tag filtering, empty results,
foreign-vault isolation, concurrent search calls, note reads by path and ID,
current source edits, a missing collection followed by successful search after
indexing, unavailable Qdrant, wrong-typed arguments, and unknown tools. Valid
note reads succeed after protocol and retrieval errors. Asset-free reader tests
cover empty notes, canonical path casing, internal/external symlinks, invalid
UTF-8, exclusions, opt-out, and deletion. Missing model assets return a
sanitized `assets_unavailable` tool error while note reads and later calls remain
available. For both fixture vaults the live test compares hashes of canonical
full Qdrant point data, including complete payloads and vectors, before and after
tool calls. It also checks source hashes around read-only operations.

For the intended local client check, Codex CLI `0.162.1` ran the Release MCP DLL
over stdio with temporary command-line configuration and `--ignore-user-config`;
no persistent Codex settings changed. The client discovered exactly
`get_note` and `search_knowledge`, searched the synthetic marker, read the note
by path and canonical document ID, received a sanitized `invalid_request` for a
blank query, and successfully read again afterward. The final client summary
reported every check passing. The Codex command exited `0`.

All test-owned collections were removed in test cleanup. The separate client
smoke-test collection was explicitly deleted and verified absent. Full point
and source comparisons passed for the intended read-only windows; the fixture
note edit was intentionally visible to `get_note` without changing indexed
state. Search equivalence, note eligibility/containment, privacy-safe failures,
read-only behavior, lifecycle recovery, and client launch are demonstrated.

| Closure gate | Result |
|---|---|
| Pinned assets, local Qdrant, isolated configuration | Passed; asset hashes verified and Qdrant remained loopback-only |
| Solution and explicit integration regression suites | Passed: 164 unit, 6 embedding, 3 indexing, and 1 MCP tests |
| CLI/MCP search parity and filters | Passed for explicit and default limits, path prefix, exact AND tags, empty results, and vault isolation |
| Current note reads and failure independence | Passed by path and ID, source edit, missing model, missing collection, and unavailable Qdrant cases |
| Read-only, privacy, and lifecycle | Passed with unchanged point payload/vector snapshots, sanitized errors, concurrent calls, and successful retries |
| Intended local client | Passed with Codex CLI 0.162.1 using the built Release DLL over stdio |
| Cleanup and documentation | Passed; disposable collection verified absent and status documents reconciled |

Milestone 7 acceptance is complete. Milestone 6's owner-reviewed 22/24 baseline
remains unchanged; Milestone 8 and the broader POC remain separate work.
