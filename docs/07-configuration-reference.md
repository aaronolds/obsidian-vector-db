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

`tokenizerPath` is an optional local path to the vocabulary file shipped with the
selected model. When configured, Milestone 1 uses it for WordPiece token counts;
the path must exist and is resolved relative to the configuration file. Model
assets are never downloaded by the indexer.

Changing them requires rebuild.

### Qdrant

The collection is derived state.

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
