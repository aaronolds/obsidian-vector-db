# Obsidian Knowledge Platform

A local-first .NET 10 knowledge platform with authoritative Markdown and a
rebuildable Qdrant index. The architecture in `docs/` and decisions in `docs/adr/`
govern implementation.

**Implemented: Milestone 2.** The executable loads and validates configuration,
reports vault existence and Qdrant connectivity, scans and chunks Markdown, and
generates local BGE-small embeddings through ONNX Runtime. Collection creation,
indexing, retrieval, watching, and MCP hosting remain deferred.

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
disabled. There are no credentials or initialization scripts; collection creation
will belong to the Indexer in Milestone 3. Startup can take a few seconds; rerun
status if the service is not ready yet.

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
endpoint/reachability, collection name, embedding model, and vector dimensions.
Connectivity uses Qdrant's gRPC health RPC with a three-second deadline. It does
not check collection existence or model assets.

Exit codes: `0` means the vault exists and Qdrant responds; `1` means the vault is
missing or Qdrant is unavailable; `2` means invalid configuration or command usage;
`130` means cancellation.

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
The real-model integration suite requires the pinned ONNX file installed with the
setup script; no Docker service is required for embeddings.

## Project boundaries

- `Knowledge.Core`: dependency-free domain boundary; no unused contracts added.
- `Knowledge.Markdown`: vault discovery, frontmatter/Markdown parsing, normalization,
  wikilink extraction, and structure-aware deterministic chunking.
- `Knowledge.Embeddings`: pinned BERT WordPiece tokenizer and local ONNX inference.
- `Knowledge.Qdrant`: read-only gRPC health probe; references Core.
- `Knowledge.Indexer`: executable named `knowledge`; owns configuration and CLI.
- `Knowledge.Mcp`: library shell; no MCP host or SDK in this milestone.
- `tests/Knowledge.Indexer.Tests`: configuration and CLI tests.

Infrastructure libraries reference Core, which remains independent of tokenizer,
ONNX, Markdown parser, and Qdrant types.

See `docs/09-milestone-0-implementation-notes.md` for foundation decisions,
`docs/10-milestone-1-implementation-plan.md` for the ingestion contract, and
`docs/11-milestone-2-implementation-plan.md` for embedding behavior and
verification evidence.
