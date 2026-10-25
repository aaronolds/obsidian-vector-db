# Obsidian Knowledge Platform

A local-first .NET 10 knowledge platform with authoritative Markdown and a
rebuildable Qdrant index. The architecture in `docs/` and decisions in `docs/adr/`
govern implementation.

**Milestones 6 and 7 implemented; Milestone 6 acceptance complete with a
reviewed 22/24 real-vault Hit@5 baseline. Milestone 7 live Qdrant/ONNX acceptance
remains pending.**
The executable loads and validates configuration, reports vault and vault-scoped index status,
scans and chunks Markdown, generates local BGE-small embeddings through ONNX
Runtime, incrementally indexes documents, rebuilds the configured Qdrant
collection explicitly, supports dense CLI search, and offers local dense Hit@5
evaluation. A local stdio MCP server exposes read-only `search_knowledge` and
`get_note` tools. Watching remains deferred. The offline test suites pass.
Milestone 3's live Qdrant/ONNX acceptance checks and two-pass sample-vault CLI
check pass. Milestone 6 measured 22/24 real-vault Hit@5; see the [baseline]
(eval/dense-baseline.md) and [acceptance evidence]
(docs/16-milestone-6-implementation-plan.md#9-milestone-6-acceptance-closure-plan).
After fixing a parser bug involving fenced YAML examples, the real-vault check passed both runs with
parser profile v2: 408 notes scanned, 402 indexed, 6 empty, and 3,692 chunks
stored. The second pass left all 402 documents unchanged, wrote no chunks, and
made zero embedding calls. Both runs returned exit code 0 with no failures.
The temporary acceptance collection was removed and no notes were changed.

## Prerequisites

- Stable .NET 10 SDK (`global.json` accepts .NET 10 feature-band updates).
- Docker with Docker Compose and a running Docker daemon.

Run the following commands from the repository root.

## Restore and build

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
```

## Start Qdrant

```sh
docker compose -f docker/docker-compose.yml up -d
docker compose -f docker/docker-compose.yml ps
```

Compose pins Qdrant to `v1.19.2`, stores data in the named
`knowledge-local_qdrant-storage` volume, and publishes only `127.0.0.1:6334`
(gRPC). REST, the dashboard, and cluster ports are not published. Telemetry is
disabled. There are no credentials or initialization scripts. The `index` command
creates and validates its collection. Startup can take a few seconds; rerun status
if the service is not ready yet.

Stop the service while retaining its data:

```sh
docker compose -f docker/docker-compose.yml down
```

## Run knowledge status

The checked-in `knowledge.yaml` uses `./docs` as an existing demonstration vault.
It reads no Markdown content and creates no collections. Run:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- status
```

Or use the executable produced by the build (macOS/Linux):

```sh
./src/Knowledge.Indexer/bin/Debug/net10.0/knowledge status
```

For an actual vault, override the path (macOS/Linux):

```sh
KNOWLEDGE_VAULT__PATH="/absolute/path/to/Obsidian" \
  dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- status
```

PowerShell equivalent:

```powershell
$env:KNOWLEDGE_VAULT__PATH = 'C:\Users\you\Documents\Obsidian'
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- status
```

Select a different configuration file explicitly:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- status --config knowledge.local.yaml
```

Without `--config`, the file is `knowledge.yaml` in the current directory. Relative
vault paths resolve against the selected configuration file's directory. Tilde
and environment-variable expansion inside YAML paths are not performed; use an
absolute path or an environment override.

Status prints the application version, vault ID/path/existence, Qdrant gRPC
endpoint/reachability, collection name/existence, embedding model, vector
dimensions, and the indexed document and chunk counts for the configured vault.
Counts are read from identity payload fields only and status never loads model
assets or changes Qdrant. The command can report operational state without model
weights. A missing collection reports zero counts and returns `1`; unavailable or
malformed count reads report counts as unavailable with a safe diagnostic.

Exit codes: `0` means the vault exists, Qdrant responds, and the configured
collection was inspected successfully; `1` means an operational problem such as
a missing vault/collection, unavailable Qdrant, or failed count read; `2` means
invalid configuration or command usage; `130` means cancellation.

## Print Markdown chunks

Milestone 1 exposes a local inspection command that does not start Qdrant, load
ONNX weights, or make network requests:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- \
  chunks --config knowledge.yaml > chunks.ndjson
```

The command recursively discovers eligible Markdown files, parses frontmatter and
Obsidian wikilinks, preserves original Markdown, produces normalized content, and
prints one deterministic camel-case `KnowledgeChunk` JSON object per line. Errors
are written to stderr and return exit code `1`; configuration and tokenizer setup
errors return `2`. Frontmatter `ai-index: false` and configured excludes prevent a
note from producing chunks. Set `embedding.tokenizerPath` to the selected model's
local `vocab.txt` for exact WordPiece sizing; without it, the command uses an
approximate counter that can underestimate token counts.

## Generate local embeddings

The pinned tokenizer assets are checked in under `models/bge-small-en-v1.5/`;
the 133 MB ONNX weights stay outside Git. Install the weight file and verify its
digest with:

```sh
./scripts/setup-bge-model.sh
```

Then run the synthetic-vault demonstration:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  embeddings --config knowledge.milestone2.yaml > embeddings.ndjson
```

Each NDJSON line contains chunk IDs and provenance, exact token count, model
identity, dimensions, and vector. This command explicitly writes note-derived
vectors to stdout; summaries and safe diagnostics go to stderr. It does not start
Qdrant or make network requests. `embeddings` requires the pinned local
`embedding.modelPath` and `embedding.tokenizerPath` assets and checks their
digests before inference.

The tokenizer also supplies chunk token counts during embedding generation.
Without tokenizer assets, `chunks` remains available for inspection, but its
approximate counter cannot guarantee the model input limit. The sample vault
covers long prose splitting, code, tables, Unicode, opt-out, and empty notes.

## Index the vault

`knowledge index` requires the pinned local ONNX model and tokenizer assets, a
readable vault, and Qdrant running locally. Install the ONNX weights with
`./scripts/setup-bge-model.sh`, start Qdrant as above, and run:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  index --config knowledge.milestone3.yaml
```

The sample config uses a dedicated `obsidian-knowledge-milestone3` collection.
For your own vault, set `KNOWLEDGE_VAULT__PATH` and choose a collection in a local
config file. The command creates the collection with one unnamed 384-dimensional
cosine vector and indexes eligible Markdown chunks. It hashes note bytes, uses
deterministic chunk IDs, and stores canonical chunk/document metadata in Qdrant.
Matching documents are skipped only when every indexed chunk is present and
consistent. A second unchanged run therefore reports zero embedding calls.
Changes replace the whole document; deletions, exclusions, and opt-outs remove
its points. Empty notes create no points.

Replacement deletes a changed document's prior points before parsing and writing
its replacement, as specified by ADR-009. Qdrant has no transaction across those
operations, so an interrupted replacement can temporarily leave a note absent or
partially indexed. The next `index` run detects the incomplete chunk count and
retries it. The indexer does not change Markdown. Run one `index` process at a time
against a collection.

The index profile is stored in each chunk payload. If the model, schema, tokenizer,
chunker, or embedding-affecting settings change, indexing stops before point
mutations and requests a fresh collection. Use the explicit `rebuild` command to
recreate the configured collection and index all eligible notes.
Discovery must complete before point changes; a missing or unreadable vault cannot
trigger stale-point deletion. Operational output contains metadata only, never
note text or vectors.

Exit code `0` means the run completed, `1` means indexing was incomplete, `2` means
usage/configuration/assets/profile were invalid, and `130` means the run was
cancelled.

## Search indexed notes

Milestone 4 adds local dense retrieval over an existing, compatible Qdrant index.
Install the pinned local model assets, start Qdrant, and index the configured
vault before searching:

```sh
./scripts/setup-bge-model.sh
docker compose -f docker/docker-compose.yml up -d
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  index --config knowledge.milestone4.yaml
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  search "How does OBO work?" --config knowledge.milestone4.yaml
```

The Milestone 4 sample uses its own `obsidian-knowledge-milestone4` collection.
Retrieval adds the pinned BGE English query instruction, embeds locally, then
asks Qdrant for dense cosine matches. Search never creates collections or payload
indexes. If the collection is missing, index it first. The finalized query profile
uses embedding schema version 2, so a Milestone 3 collection requires a fresh
collection and a new `index` run before it can be searched; no migration runs
automatically.

Optional arguments set the result count and exact filters:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  search "Front Door restrictions" --limit 10 --path Architecture/ --tag azure
```

The default result count is 5 and the configured maximum is 50. Path matching is
a case-sensitive, literal vault-relative prefix. Tags are exact and case-sensitive;
repeated `--tag` values require all listed tags. Filters apply inside Qdrant before
the result limit and every request remains scoped to the configured vault.

Search prints the requested original Markdown and provenance to stdout. Application
diagnostics go to stderr and contain operational metadata only; queries and note
content are never logged or repeated in errors. Search reports the last indexed
state, so run `index` after notes are changed, deleted, excluded, or opted out.

## Use the MCP server

Build the server and configure your MCP client to start its DLL with an absolute
configuration path. For example:

```sh
dotnet build src/Knowledge.Mcp/Knowledge.Mcp.csproj --configuration Release
```

```json
{
  "mcpServers": {
    "obsidian-knowledge": {
      "command": "dotnet",
      "args": [
        "/absolute/path/to/src/Knowledge.Mcp/bin/Release/net10.0/Knowledge.Mcp.dll",
        "--config",
        "/absolute/path/to/knowledge.local.yaml"
      ]
    }
  }
}
```

The server uses local stdio and writes only MCP protocol messages to stdout.
`search_knowledge` accepts a query and the same optional limit, path prefix, and
exact tags as CLI search. It returns ranked Markdown chunks with document/chunk
IDs, path, title, heading path, and score. It requires the pinned local model,
tokenizer, and an existing compatible Qdrant collection. It never indexes or
repairs the collection.

`get_note` accepts exactly one canonical vault-relative Markdown path or
document ID. It reads current eligible Markdown from the vault, including
frontmatter, and enforces configured include/exclude rules, `ai-index: false`,
and vault containment for symbolic links. It does not require Qdrant or model
assets. Both tools are read-only; returned note text is untrusted data. Tool
errors and application diagnostics omit queries and note contents.

The server uses the same retrieval service and index-profile fingerprint as the
CLI. The live search-equivalence acceptance check requires pinned model assets
and local Qdrant; see the [Milestone 7 plan](docs/17-milestone-7-implementation-plan.md).

## Evaluate dense retrieval

The `evaluate` command checks a local benchmark against the existing index. The
benchmark must contain at least 20 verified questions and expected vault-relative
Markdown paths. It scores exact path matches among the first five ranked chunks,
reports Hit@5 overall and by category, and writes metadata without query text or
note bodies. Evaluation never indexes or modifies Qdrant.

Keep real-vault benchmark and report files under the ignored `eval/local/`
directory because their paths and identifiers may be sensitive:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  evaluate --benchmark eval/local/retrieval-benchmark.json \
  --output eval/local/dense-baseline.json --config knowledge.local.yaml
```

The 85% Hit@5 goal is advisory. Review misses and representative successes
manually and record the dense baseline before considering later retrieval changes.
See [eval/README.md](eval/README.md) for dataset and privacy guidance. The
checked-in configuration uses repository documentation as a sample vault; it
does not supply a verified real-vault benchmark.

## Rebuild the configured collection

`knowledge rebuild` requires the pinned local model and tokenizer assets, a fully
discoverable vault, and local Qdrant. It validates the runtime and reads every
eligible source file before deleting the configured collection. It then creates
the current 384-dimensional cosine collection and indexes all eligible notes,
even when their content matches the previous index. A fully discovered empty
vault produces an empty collection. Malformed notes or failures after recreation
can leave a partial collection; rerun `index` to repair incomplete documents or
rerun `rebuild` to start over.

Rebuild deletes every point in the configured collection, including points for
other vault IDs. Use a dedicated collection and run only one index/rebuild process
against it at a time. Rebuild is not transactional: cancellation or a failure
after collection deletion can leave missing or partial derived state. The source
Markdown is never changed.

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  rebuild --config knowledge.milestone4.yaml
```

The `Knowledge.Indexer.Tests` suite exercises synchronization and retry behavior
without Docker or model assets. The explicitly invoked
`tests/Knowledge.Indexing.IntegrationTests` project exercises real ONNX and Qdrant
together; it requires both services and the pinned model assets.

```sh
dotnet test tests/Knowledge.Embeddings.Tests/Knowledge.Embeddings.Tests.csproj
dotnet test tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj
```

The real-model integration project is separate from the asset-free solution test
suite. It needs the installed ONNX file and checks token IDs and vectors against a
synthetic Transformers reference fixture. The
`scripts/generate-bge-reference.py` script records how to reproduce the fixture
from the pinned PyTorch checkpoint. On Python 3.12, install the pinned reference
dependencies and regenerate it with:

```sh
python3.12 -m venv /tmp/bge-reference
/tmp/bge-reference/bin/pip install -r scripts/requirements-bge-reference.txt
/tmp/bge-reference/bin/hf download BAAI/bge-small-en-v1.5 model.safetensors \
  --revision 5c38ec7c405ec4b44b94cc5a9bb96e735b38267a \
  --local-dir /tmp/bge-reference-assets
/tmp/bge-reference/bin/python scripts/generate-bge-reference.py \
  --weights /tmp/bge-reference-assets/model.safetensors
```

Reference dependencies and checkpoint are only needed to regenerate the fixture;
they are not needed at runtime.

## Configuration and logging

`knowledge.yaml` follows `docs/07-configuration-reference.md`. All sections bind to
strongly typed .NET options; validation runs before network access. Vault ID,
path, and at least one include pattern are required. Unknown keys, malformed
values, invalid ranges, incompatible token settings, non-local Qdrant hosts, cloud
providers, and non-POC model/retrieval settings fail clearly. Supported .NET log
levels are `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, and
`None` (case-insensitive).

`KNOWLEDGE_` environment variables take precedence over YAML. Double underscores
separate nested names, for example:

```sh
export KNOWLEDGE_VAULT__ID=primary
export KNOWLEDGE_QDRANT__HOST=127.0.0.1
export KNOWLEDGE_QDRANT__PORT=6334
export KNOWLEDGE_LOGGING__LEVEL=Warning
```

Array elements can be overridden by index, such as
`KNOWLEDGE_INDEX__INCLUDE__0='Notes/**/*.md'`. Element overrides retain other YAML
array entries; edit the YAML to replace or empty a whole list. Globs use forward
slashes and vault-relative paths. Pattern evaluation is deferred to ingestion.

JSON structured logs go to stderr and status output goes to stdout. Logs contain
only operational metadata. `logContent` and `logQueries` must remain false; this
milestone never logs Markdown, embedding inputs, vectors, retrieved content, or
queries. No secrets or cloud embedding service are required.

## Tests

```sh
dotnet test Knowledge.sln --no-build --no-restore
```

`Knowledge.Indexer.Tests` and `Knowledge.Embeddings.Tests` require no model assets.
`Knowledge.Mcp.Tests` also runs without model assets or Qdrant.
The real-model integration suite requires the pinned ONNX file installed with the
setup script; no Docker service is required for embeddings.
The MCP live acceptance suite is excluded from the solution and also requires
running local Qdrant:

```sh
dotnet test tests/Knowledge.Mcp.IntegrationTests/Knowledge.Mcp.IntegrationTests.csproj
```

## GitHub automation and contributing

The checked-in GitHub workflows define Linux .NET 10 Release build/unit-test CI
and direct/transitive NuGet vulnerability audits. Dependabot configuration
requests weekly NuGet, Actions, Docker Compose, and Python reference-tool updates.
Normal PR checks do not download models or run the real-model/Qdrant integration
suites.

See [CONTRIBUTING.md](CONTRIBUTING.md) for local verification and data-safety
guidance. [GitHub automation setup](docs/14-github-automation.md) covers workflow
activation, required checks, and native Copilot automatic review. Copilot
instructions are included, but repository rules must be enabled separately;
adding the files alone does not enable automatic reviews or branch protection.

## Project boundaries

- `Knowledge.Core`: dependency-free domain boundary; no unused contracts added.
- `Knowledge.Markdown`: vault discovery, frontmatter/Markdown parsing, normalization,
  wikilink extraction, and structure-aware deterministic chunking.
- `Knowledge.Embeddings`: pinned BERT WordPiece tokenizer and local ONNX inference.
- `Knowledge.Qdrant`: gRPC health probe and index storage adapter; references Core.
- `Knowledge.Runtime`: shared configuration, index profile, and read-only search composition.
- `Knowledge.Indexer`: executable named `knowledge`; owns indexing orchestration and CLI.
- `Knowledge.Mcp`: local stdio MCP executable; owns the `search_knowledge` and `get_note` tool adapters.
- `tests/Knowledge.Indexer.Tests`: configuration and CLI tests.
- `tests/Knowledge.Mcp.Tests`: asset-free MCP and current-note tests.

Infrastructure libraries reference Core, which remains independent of tokenizer,
ONNX, Markdown parser, and Qdrant types.

See `docs/09-milestone-0-implementation-notes.md` for foundation decisions,
`docs/10-milestone-1-implementation-plan.md` for the ingestion contract, and
`docs/11-milestone-2-implementation-plan.md` for embedding behavior and
`docs/12-milestone-3-implementation-plan.md` for incremental indexing and its
verification evidence.
