# Configuration Reference

## 1. Recommended configuration file

Working default:

```text
knowledge.yaml
```

The exact filename is not architecturally significant.

Environment variables may override machine-specific settings.

## 2. Proposed configuration

```yaml
vault:
  id: primary
  path: /Users/aaron/Documents/Obsidian

index:
  include:
    - "**/*.md"

  exclude:
    - ".obsidian/**"
    - ".trash/**"
    - "Templates/**"

  frontmatterOptOutField: ai-index

chunking:
  targetTokens: 300
  softMaxTokens: 400
  overlapTokens: 40
  overlapOnlyWhenSplit: true

embedding:
  provider: onnx
  model: BAAI/bge-small-en-v1.5
  dimensions: 384
  modelPath: ./models/bge-small-en-v1.5/onnx/model.onnx
  tokenizerPath: ./models/bge-small-en-v1.5/vocab.txt

qdrant:
  host: localhost
  port: 6334
  collection: obsidian-knowledge

retrieval:
  defaultLimit: 5
  maxLimit: 50
  mode: dense

logging:
  level: Information
  logContent: false
  logQueries: false
```

## 3. Configuration principles

### Vault

- `id` is stable logical identity.
- `path` may differ by workstation.
- V1 supports one active vault.

### Index rules

- include rules are evaluated before excludes
- excludes win
- `ai-index: false` wins over path inclusion

### Chunking

`targetTokens` is a preference, not a hard boundary.

`softMaxTokens` should be respected unless preserving an indivisible semantic unit would still remain within the embedding model hard limit.

`overlapTokens` applies only when a large prose section must be split.

### Embedding

The following settings are index-defining:

- model
- dimensions
- normalization behavior
- embedding schema version

`modelPath` and `tokenizerPath` are paths to pinned local model and vocabulary
assets. Both resolve relative to the configuration file and may be overridden by
`KNOWLEDGE_EMBEDDING__MODELPATH` and `KNOWLEDGE_EMBEDDING__TOKENIZERPATH`.
`knowledge embeddings` and `knowledge index` require both paths and verify all
expected asset digests before inference. `knowledge status` does not load the
assets. `knowledge chunks` may run without them, but its approximate token count
does not guarantee the model input limit. Model assets are never downloaded at runtime; use
`scripts/setup-bge-model.sh` to install the pinned ONNX weight file.

Changing index-defining settings requires a fresh collection in Milestone 3;
Milestone 5 provides `knowledge rebuild` to explicitly recreate the configured
collection and repopulate it from eligible Markdown. Ordinary `index` stores an
index-profile fingerprint in each chunk payload and stops before point writes if
an existing index has a different profile. `status` can inspect configuration and
Qdrant state even when configured model asset paths are absent or missing.

### Qdrant

The collection is derived state. `knowledge index` creates a missing collection
using a single unnamed 384-dimensional cosine vector and checks existing vector
layout before writing. It does not recreate an incompatible collection. The
explicit `rebuild` command deletes and recreates the whole configured collection,
including points for other vault IDs, so use a dedicated collection. Before the
deletion it validates local inference assets, completes vault discovery, and
checks every candidate source can be read. A failure after deletion can leave a
partial collection; a later `index` or `rebuild` repairs derived state.

Changing the collection name does not migrate data automatically.

### Retrieval

V1:

```yaml
mode: dense
```

Future:

```yaml
mode: hybrid
```

MCP tool contracts should remain stable.

Milestone 4 uses `retrieval.defaultLimit` when `--limit` is omitted and rejects
explicit limits outside `1..retrieval.maxLimit` (recommended maximum 50). The
`search` command accepts an optional literal, case-sensitive vault-relative
`--path` prefix and repeated exact `--tag` values. Repeated tags are combined
with AND semantics. The configured `vaultId` is always applied as a filter.

The BGE passage-retrieval instruction is applied to queries only. The query
formatting identifier is part of the index profile and embedding schema version
2; an older Milestone 3 collection must be reindexed into a fresh collection
before search. Search is read-only and does not create a missing collection.

`knowledge evaluate --benchmark <path> --output <path> [--config <path>]`
requires dense mode, `retrieval.maxLimit` of at least 5, the pinned local model
and tokenizer assets, and an existing compatible collection. It uses an exact
top-five chunk cutoff regardless of `retrieval.defaultLimit`, applies no search
filters, and is read-only. Benchmark and report paths are relative to the
working directory; keep private real-vault files under ignored `eval/local/`.

## 4. Environment overrides

Recommended convention:

```text
KNOWLEDGE_VAULT__PATH
KNOWLEDGE_QDRANT__HOST
KNOWLEDGE_QDRANT__PORT
KNOWLEDGE_LOGGING__LEVEL
```

Secrets are not currently needed.

## 5. Validation

Application startup should fail clearly when:

- vault path does not exist
- vault path is not readable
- target model assets are unavailable
- Qdrant configuration is invalid
- token settings exceed model capability
- vector dimensions do not match collection configuration
