# Obsidian Knowledge Platform

A local-first .NET 10 knowledge platform with authoritative Markdown and a
rebuildable Qdrant index. The architecture in `docs/` and decisions in `docs/adr/`
govern implementation.

**Implemented: Milestone 0 only.** The executable loads and validates configuration
and reports vault existence and Qdrant connectivity. Ingestion, inference,
collection creation, indexing, retrieval, watching, and MCP hosting are deferred.

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

The tests cover YAML loading, every configuration section, validation, environment
precedence, relative paths, and CLI errors. They require no Docker or model assets.
The live integration check is starting Compose and running status as shown above.

## Project boundaries

- `Knowledge.Core`: dependency-free domain boundary; no unused contracts added.
- `Knowledge.Markdown`: library shell for later ingestion and chunking.
- `Knowledge.Embeddings`: library shell for later local ONNX inference.
- `Knowledge.Qdrant`: read-only gRPC health probe; references Core.
- `Knowledge.Indexer`: executable named `knowledge`; owns configuration and CLI.
- `Knowledge.Mcp`: library shell; no MCP host or SDK in this milestone.
- `tests/Knowledge.Indexer.Tests`: configuration and CLI tests.

Infrastructure libraries reference Core, which references none of them. No Core
abstractions were needed in Milestone 0, so no placeholder domain tests were added.

See `docs/09-milestone-0-implementation-notes.md` for scope-specific decisions and
validation deferred to the milestones that use Markdown, model assets, and vectors.
