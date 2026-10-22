# Milestone 4 implementation plan

Implementation status: implemented. Offline and live synthetic-vault verification
passed on 2026-10-22. A separate real-vault retrieval smoke check was not run
because no real vault path is configured in this checkout.

This plan expands Milestone 4 in [05-implementation-plan.md](05-implementation-plan.md).
The architecture, specification, canonical contracts, and accepted ADRs remain
authoritative. No architectural changes are proposed. If implementation proves a
requirement infeasible, record the issue and evidence before changing the design.

## Outcome and scope

Given an indexed collection, pinned local BGE assets, and local Qdrant:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  search "How does OBO work?" --config knowledge.local.yaml
```

Return ranked note sections with canonical document/chunk IDs, vault-relative
path, title, full heading path, original Markdown, and cosine score.

Included:

- `IKnowledgeRetriever` and canonical search request/result records.
- Model-specific query formatting and local query embeddings.
- Read-only dense Qdrant search scoped to the configured vault.
- Optional limit, path-prefix, and exact-tag filters.
- CLI search, safe diagnostics, unit tests, and explicit live acceptance checks.

Deferred: rebuild and expanded status (Milestone 5), the 20-query benchmark and
Hit@5 target (Milestone 6), MCP and `get_note` (Milestone 7), hybrid/sparse search,
reranking, diversification, graph traversal, watching, cloud services, migrations,
and concurrent-index consistency guarantees. Preserve existing commands and all
Milestone 3 indexing safety gates.

## Starting point

- Core already defines `IEmbeddingService`, canonical chunks, identities, and
  document embedding text. Search contracts are documented but not implemented.
- `OnnxEmbeddingService` validates pinned assets, enforces 512 total tokens,
  returns finite 384-dimensional vectors, and applies CLS pooling/L2 normalization.
  It currently embeds exactly the supplied text; retain that behavior.
- Milestone 3 stores all search-result fields in payload, plus schema/model
  provenance, `documentChunkCount`, and `indexProfile`.
- The current store has mutation methods, including `EnsureCollectionAsync`,
  which can create a collection and payload indexes. Search must not call it.
- `IndexProfile.Create` currently records query formatting as
  `deferred-to-milestone-4`; payload and profile use embedding schema version 1.
- Configuration already provides dense mode, default limit 5, and maximum limit
  50. `KnowledgeCli` has no search parser or retrieval composition.
- Qdrant.Client 1.19.0 and Qdrant 1.19.2 are pinned. The installed SDK exposes
  `Conditions.MatchPrefix`; no dependency upgrade is needed for path filtering.

## Implementation sequence

### 1. Define domain contracts and filter semantics

Add the records from [03-domain-and-data-contracts.md](03-domain-and-data-contracts.md)
to Core without Qdrant, ONNX, configuration, logging, or CLI dependencies:

```csharp
public sealed record KnowledgeSearchRequest(
    string Query,
    int Limit = 5,
    string? PathPrefix = null,
    IReadOnlyList<string>? Tags = null);

public sealed record KnowledgeSearchResult(
    string DocumentId,
    string ChunkId,
    string Path,
    string Title,
    IReadOnlyList<string> HeadingPath,
    string ContentMarkdown,
    double Score);

public interface IKnowledgeRetriever
{
    Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken cancellationToken = default);
}
```

Use these semantics consistently in CLI and retriever validation:

| Input | Milestone 4 behavior |
|---|---|
| Query | Reject null, empty, or whitespace; trim outer whitespace only |
| Limit | CLI omission uses `retrieval.defaultLimit`; explicit value must be 1 through configured `retrieval.maxLimit`; do not clamp |
| Path prefix | Optional literal prefix of canonical vault-relative `path`; ordinal/case-sensitive, with no wildcard interpretation |
| Folder prefix | `Architecture/` includes descendants and excludes `ArchitectureOther/`; preserve a supplied trailing slash |
| Invalid paths | Reject rooted/drive/UNC paths, `..` segments, and NUL; normalize separators to `/` and remove redundant `.` segments |
| Tags | Trim outer whitespace, reject blank entries, remove duplicate exact values, and match stored values exactly and case-sensitively |
| Multiple tags | AND: every requested tag must be present; path and tag predicates are also ANDed |
| No filters | Still constrain every search to configured `vaultId` |

An omitted or empty tag list applies no tag restriction. Reject a blank supplied
path prefix rather than silently broadening the search. Canonicalize a supplied
root-only relative prefix such as `./` to no path restriction. Validate Windows
absolute paths even when running on macOS/Linux.

Existing ingestion trims tag whitespace and preserves spelling; do not introduce
lowercasing, `#` removal, hierarchical expansion, or new tag extraction rules.
These filter details resolve unspecified behavior without changing an ADR.

Acceptance: asset-free tests establish request validation, configured limits,
literal prefix behavior, case sensitivity, and repeated-tag semantics.

### 2. Implement BGE query formatting and finalize its schema

Add a small query embedding contract to Core, consumed by the retriever:

```csharp
public interface IQueryEmbeddingService
{
    Task<EmbeddingResult> EmbedQueryAsync(
        string query,
        CancellationToken cancellationToken = default);
}
```

Implement `BgeQueryEmbeddingService` in `Knowledge.Embeddings` as a wrapper around
the existing `IEmbeddingService`. Keep the model-specific instruction there:

```text
Represent this sentence for searching relevant passages: {query}
```

The instruction includes one space after the colon. Apply it exactly once at the
query formatting boundary. Do not pass queries through `EmbeddingTextFormatter`,
and do not add this instruction to document embeddings or generic `EmbedAsync`.
The [pinned BGE model card](https://huggingface.co/BAAI/bge-small-en-v1.5/blob/5c38ec7c405ec4b44b94cc5a9bb96e735b38267a/README.md)
recommends a query instruction for passage retrieval and unprefixed passages.

- Count the final formatted query with the pinned tokenizer, including special
  tokens. Accept 512 tokens and reject 513 before inference or search; never
  truncate. Error messages must not include query text or formatted input.
- Reuse existing CLS pooling, L2 normalization, digest checks, and cancellation.
- Validate returned provider/model, dimension count, finite vector values, and
  nonzero norm before sending a query vector to Qdrant, including with test fakes.
- Extend the synthetic reference fixture/generator with a formatted English query
  and verify real ONNX output against the same pinned reference workflow.

Finalize `queryFormatting` in `IndexProfile` with a stable versioned identifier,
and advance `embeddingSchemaVersion` to 2 wherever written or validated. Centralize
the version constants used by profile creation, payload writing, and validation
so indexing and retrieval cannot disagree. Keep document IDs, chunk IDs, document
formatter, chunking, pooling, model weights, and point-ID mapping unchanged.

This deliberately changes profile compatibility. Existing Milestone 3 collections
must fail clearly with a fresh-collection diagnostic, even though the document
vector calculation itself is unchanged. Do not silently accept the deferred
profile, rewrite existing payloads, delete collections, or implement migration.
Before Milestone 5, choose a new collection and run `index` to populate it. Explain
this prerequisite in the search documentation and sample configuration.

Acceptance: unit tests prove the exact instruction, unchanged document inputs,
token boundaries, invalid-vector rejection, and schema/profile mismatch handling.
The explicit real-model suite proves query token IDs and vector equivalence.

### 3. Implement reusable read-only Qdrant retrieval

Implement `QdrantKnowledgeRetriever : IKnowledgeRetriever` in `Knowledge.Qdrant`.
Compose it with `IQueryEmbeddingService`, the configured vault ID, expected
profile/schema/model metadata, limits, and the pinned Qdrant client. Keep
`Knowledge.Indexer` as the composition root. The retriever must be usable by a
future MCP host without referencing the executable or duplicating retrieval.
Do not add an MCP package or a speculative retrieval framework.

1. Validate the request and cancellation before inference or network access.
2. Read collection information and validate the existing single unnamed
   384-dimensional cosine layout, with no sparse vectors. A missing collection
   is an actionable setup error: run `index` with the selected configuration.
   Do not create it or conflate it with an existing empty collection.
3. Read only compatibility metadata for the configured vault, paginating to
   completion. Validate supported schema, embedding provenance, and expected
   profile before returning results. Extract shared read-only metadata/layout
   helpers from the current adapter as needed; do not duplicate validation or
   depend on the write-capable store interface. Missing/invalid metadata fails
   visibly rather than silently removing incompatible points from results.
4. An existing empty vault index returns no results successfully and needs no
   embedding call. Otherwise generate one query embedding for the request.
5. Execute one dense query through the pinned client's vector search/query API,
   requesting the effective limit, result payload, and no stored vectors.
6. Strictly map results to `KnowledgeSearchResult`, validating required payload
   types, canonical IDs/path consistency, expected vault/provenance, and finite
   scores. An empty heading list is valid. Malformed payloads fail with a safe
   diagnostic; do not manufacture blank provenance or emit partial output.

Build all predicates into the Qdrant query before top-K selection:

- A required exact `vaultId` match.
- A native `MatchPrefix` condition on `path` when supplied.
- One required exact keyword condition on the `tags` array for each requested tag.

The official [Qdrant prefix-filter documentation](https://qdrant.tech/documentation/search/filtering/#prefix-match)
dates native prefix matching to 1.19.0 and defines case-sensitive literal
matching. Confirm behavior against the pinned 1.19.2 server in integration tests.
Do not post-filter an unfiltered top-K list, use full-text matching for paths, or
introduce a derived prefix payload. Correct prefix filtering does not require a
new payload index under the repository's default local configuration. Defer
path/tag index acceleration; search must never create or repair indexes.

Preserve descending Qdrant cosine ranking and raw scores. Use ordinal chunk ID
only to stabilize ties among returned equal-score results; do not promise stable
tie selection at the top-K boundary. Apply no score threshold, oversampling,
document deduplication, reranking, or score-as-probability conversion.

Use cancellation and bounded RPC deadlines, following the existing ten-second
client timeout. Dispose resources in the composition root. Metadata preflight is
linear in indexed chunks for this initial CLI; keep bodies and vectors out of it.
Search reflects the last index run. It neither opens source notes nor reconciles
deletions/exclusions until `index` runs. Concurrent replacement may expose missing
or partial documents under ADR-009; do not promise a snapshot or add transactions.

Acceptance: adapter tests inspect generated requests and payload mapping; live
tests prove native filtering, ranked results, vault isolation, and zero mutations.

### 4. Add the CLI vertical slice and safe output

Support the existing specification's spelling:

```text
knowledge search <query> [--limit <n>] [--path <prefix>] [--tag <tag>]... [--config <path>]
knowledge search --help
```

- Require one positional query; normal shell quoting handles spaces. Accept
  options in any order after the query. Reject unknown flags, missing values,
  duplicate singleton options, extra positionals, and invalid integers. Only
  `--tag` repeats. Do not add a CLI parsing dependency solely for this command.
- Resolve configuration and asset paths with existing YAML/environment behavior.
  Validate configuration and request before loading ONNX or contacting Qdrant.
  Help requires no configuration, assets, or service.
- Compose the query wrapper and retriever against existing abstractions. Provide
  a focused injection seam for CLI tests using fakes rather than a real server.
- Print numbered results to stdout with invariant-culture scores, path, title,
  heading hierarchy, canonical IDs, and the complete `ContentMarkdown` string.
  Separate presentation labels from content; do not normalize Markdown again.
  Use an explicit note-level label for empty heading paths.
- Print a clear no-results message and exit successfully for valid empty searches.
  Buffer/validate the result list before output to avoid partial success on a
  malformed payload. Do not echo the query in result headers or diagnostics.
- Emit only operational metadata to stderr: counts, durations, and safe failure
  codes. Keep `logContent` and `logQueries` false. Never print raw RPC/ONNX error
  bodies or arbitrary exception text that could include queries or note content.
  Search stdout intentionally contains requested note content and must be
  documented separately from application logs.

| Exit code | Meaning |
|---|---|
| `0` | Successful search, including no results |
| `1` | Qdrant unavailable/timeout, inference failure, or invalid returned data |
| `2` | Usage, request, configuration, assets, missing collection, or compatibility error |
| `130` | Cancellation |

Update the future-command tests to make search available; retain rebuild/watch as
unimplemented. Add `knowledge.milestone4.yaml` using the synthetic sample vault,
pinned local assets, and its own collection. Add explicit `index` then `search`
instructions to README and the configuration reference only once implemented.
Keep all remaining milestone statuses accurate.

Acceptance: offline CLI tests cover help, parsing, defaults/overrides, rendering,
empty results, exit codes, cancellation, and privacy-safe failures.

### 5. Verify the complete slice and record evidence

Keep normal unit tests asset-free. Add retrieval/CLI tests to
`Knowledge.Indexer.Tests`, with mocked query embeddings and Qdrant transport or
client seams as appropriate. Do not require Docker to test predicate construction
or payload validation. Extend the existing explicit real-model and Qdrant/ONNX
integration projects rather than adding integration dependencies to the solution.

| Area | Required evidence |
|---|---|
| Query embedding | Exactly one formatted query embedding per nonempty search; 512/513 final-token boundary; reference-vector agreement |
| Dense retrieval | Semantically relevant synthetic passage is in top five; scores descend and provenance matches indexed chunks |
| Filters | Prefix, case, Unicode, spaces, nested paths, one/multiple tags, combined predicates, and no-match cases |
| Filter before top-K | Stronger off-prefix/off-tag distractors cannot crowd out eligible results at a small limit |
| Vault isolation | A stronger point from a different vault is never returned |
| Payload fidelity | Canonical IDs, original Markdown/wikilinks/code, title, path, and heading arrays survive round-trip |
| Compatibility | Missing collection, wrong vector layout, old profile/schema, and malformed metadata fail clearly |
| Read-only operation | Neither successful nor failed search calls collection creation, payload-index creation, upsert, or deletion |
| Index updates | After the next `index`, changed sections are searchable and deleted, excluded, or opted-out documents no longer appear |
| Privacy | Sentinel queries are never echoed; note content appears only in explicitly requested successful stdout, never in stderr/logs or failure output |
| Regression | Index twice still yields zero second-pass embedding calls/writes; existing status/chunks/embeddings tests pass |

Run from the repository root:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test Knowledge.sln --no-build --no-restore
dotnet test tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj
dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj
```

Start local Qdrant only for live validation and require preinstalled pinned model
assets. Use isolated disposable collections, including separate intentionally
incompatible fixtures; remove only collections created by the tests. Hash source
files and compare collection points/payloads before and after search to corroborate
that retrieval leaves Markdown and indexed state unchanged.

Perform a sample-vault CLI `index` followed by natural-language search, filtered
search, no-match search, and a second unchanged `index`. Add a real-vault smoke
check using a fresh disposable collection: confirm a known paraphrased query
returns its expected note section and validate a path/tag restriction. Keep real
queries and result bodies out of committed evidence. Record only commands with
synthetic placeholders, versions, pass/fail, counts, timings, exit codes, and
cleanup confirmation. Do not create the formal benchmark or claim 85% Hit@5.

## Definition of done

- The documented `knowledge search` command returns ranked original note sections
  with complete canonical provenance through `IKnowledgeRetriever`.
- Query formatting and embedding compatibility are explicit and tested; old
  collections receive a documented fresh-collection diagnostic.
- Optional filters execute in Qdrant before ranking/limit, always within the
  configured vault, with the semantics defined above.
- Retrieval performs no writes and preserves local-only inference and safe logs.
- Unit, reference-model, live Qdrant/ONNX, CLI, and indexing-regression checks pass,
  with verification evidence added to this document.
- README, configuration reference, command help, and project guidance reflect
  implemented Milestone 4 behavior and continue to defer Milestones 5 onward.

## Verification evidence

Completed on 2026-10-22:

- `dotnet build Knowledge.sln --no-restore -m:1` succeeded with zero warnings and
  zero errors.
- `dotnet test Knowledge.sln --no-build --no-restore -m:1` passed 106 tests:
  98 Indexer tests and 8 asset-free embedding tests, including preflight
  rejection of overlong queries before model loading or Qdrant access.
- `Knowledge.Embeddings.IntegrationTests` passed all 6 pinned ONNX tests,
  including formatted query token IDs and equality with inference over the exact
  formatted query string.
- `Knowledge.Indexing.IntegrationTests` passed against local Qdrant 1.19.2 and
  the pinned ONNX model. It verified query retrieval, path/tag filters, vault
  isolation, and unchanged indexed metadata after search.
- The synthetic-vault CLI indexed 4 documents into 10 chunks, returned the
  expected `architecture.md` section for a natural-language query, returned two
  filtered chunks for the exact path-prefix/tag query, and returned zero results
  for the case-mismatched `Architecture/` prefix. The second index pass reported
  4 unchanged documents, zero chunk writes, and zero embedding calls.
- The CLI sample collection `obsidian-knowledge-milestone4` was deleted after the
  smoke run; Qdrant confirmed it no longer exists. The integration test used and
  cleaned its own random collection.

The plan's real-vault retrieval smoke check remains unperformed because this
checkout has no configured real Obsidian vault root. The prior Milestone 3
real-vault indexing evidence remains unchanged. No real note content or query was
added to committed output.
