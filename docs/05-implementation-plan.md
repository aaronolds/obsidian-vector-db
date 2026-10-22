# Implementation Plan

## 1. Delivery strategy

Build vertical slices that prove behavior end-to-end.

Avoid implementing every abstraction before any searchable result exists.

## 2. Milestone 0: Repository and local infrastructure

### Objectives

- Create .NET 10 solution.
- Establish project boundaries.
- Start Qdrant locally.
- Add configuration loading.
- Add structured logging.

### Deliverables

```text
src/
tests/
eval/
docs/
docker/docker-compose.yml
knowledge.yaml
```

### Suggested projects

```text
Knowledge.Core
Knowledge.Markdown
Knowledge.Embeddings
Knowledge.Qdrant
Knowledge.Indexer
Knowledge.Mcp
```

### Exit criteria

- Solution builds.
- Qdrant runs locally.
- `knowledge status` can validate Qdrant connectivity.

---

## 3. Milestone 1: Markdown ingestion and chunking

### Objectives

- Scan the vault.
- Parse Markdown.
- Parse YAML frontmatter.
- Extract heading hierarchy.
- Extract tags, aliases, and wikilinks.
- Normalize content.
- Create chunks.

### Tests

- heading hierarchy
- small section
- large section
- code block
- list
- table
- blockquote
- wikilink with alias
- frontmatter tags
- `ai-index: false`
- malformed Markdown
- empty note

### Exit criteria

Given a sample vault, the CLI can print deterministic chunk objects without generating embeddings.

---

## 4. Milestone 2: Local embedding generation

### Objectives

- Implement `IEmbeddingService`.
- Run `BAAI/bge-small-en-v1.5` locally through ONNX Runtime.
- Load tokenizer assets.
- Implement correct input tokenization.
- Validate vector dimensions.
- Normalize embeddings as required by the selected model/runtime implementation.

### Validation

- vector dimension is exactly 384
- repeated identical text produces equivalent output within expected floating-point tolerance
- tokenizer limit is enforced before inference
- embedding implementation is covered by an integration test

### Exit criteria

The CLI can generate a valid embedding for each sample chunk.

---

## 5. Milestone 3: Qdrant indexing

### Objectives

- Create collection.
- Configure cosine distance.
- Upsert chunks.
- Store payload.
- Implement deterministic IDs.
- Implement file hashing.
- Skip unchanged files.
- Replace changed documents.
- Delete stale documents.

### Exit criteria

```bash
knowledge index
```

can index a real vault and a second unchanged run performs no embedding work.

---

## 6. Milestone 4: Dense CLI retrieval

Implementation status: complete on 2026-10-22; see the detailed
[Milestone 4 implementation plan](13-milestone-4-implementation-plan.md) for
verification evidence and the real-vault smoke-test limitation.

Detailed plan: [13-milestone-4-implementation-plan.md](13-milestone-4-implementation-plan.md).

### Objectives

- Implement `IKnowledgeRetriever`.
- Generate query embeddings.
- Search Qdrant.
- Return provenance.
- Add optional filters.

### Exit criteria

```bash
knowledge search "How does OBO work?"
```

returns ranked note sections with path, heading, and content.

This milestone should happen before MCP so retrieval can be debugged independently.

---

## 7. Milestone 5: Rebuild and operational commands

### Commands

```bash
knowledge rebuild
knowledge status
```

### `rebuild`

- recreate collection
- index all eligible content

### `status`

Report:

- vault root
- configured vault ID
- Qdrant endpoint
- collection
- embedding model
- document count
- chunk count

### Exit criteria

Deleting the collection and rebuilding restores usable search.

---

## 8. Milestone 6: Retrieval benchmark

### Objectives

- Create at least 20 benchmark queries.
- Add automated Hit@5 evaluation.
- Categorize queries.
- Record dense baseline.

### Exit criteria

Dense baseline results are captured and reviewed.

Recommended target:

```text
Hit@5 >= 85%
```

If the baseline misses the target, analyze failures before changing architecture.

---

## 9. Milestone 7: MCP server

### Objectives

Expose:

```text
search_knowledge
get_note
```

The MCP server uses the same retrieval service as the CLI.

### Exit criteria

An MCP client can search the vault and receive provenance-equivalent results to the CLI.

---

## 10. Milestone 8: Hardening

### Objectives

- error handling
- path traversal protection
- exclusion rules
- privacy-safe logging
- configuration validation
- graceful Qdrant unavailability
- malformed note handling
- deterministic rebuild validation

### Exit criteria

All POC acceptance criteria are demonstrated.

---

## 11. Post-POC Milestone 9: Hybrid retrieval

### Trigger

Proceed when the dense benchmark reveals exact-term or acronym weaknesses, or when hybrid is otherwise justified by measured quality.

### Objectives

- add sparse representation
- store/query sparse vectors
- execute dense and sparse retrieval
- combine rankings with RRF
- rerun benchmark

### Success criterion

Hybrid must materially improve benchmark quality without unacceptable complexity or latency.

---

## 12. Post-POC Milestone 10: Obsidian graph-assisted retrieval

Potential work:

- link resolution
- backlink lookup
- one-hop expansion
- candidate reranking

Do not add a graph database unless the relational structure becomes complex enough to justify it.

---

## 13. Suggested backlog

### Epic: Foundation

- Create .NET 10 solution
- Add Docker Compose for Qdrant
- Add configuration model
- Add structured logging

### Epic: Markdown ingestion

- Discover Markdown files
- Parse YAML
- Parse heading hierarchy
- Extract wikilinks
- Normalize Markdown
- Implement chunker
- Add chunker unit tests

### Epic: Embeddings

- Add model assets
- Add tokenizer
- Implement ONNX embedding provider
- Enforce model token limits
- Verify 384-dimensional output

### Epic: Indexing

- Create Qdrant collection
- Implement Qdrant point mapping
- Implement document IDs
- Implement chunk IDs
- Implement file hashes
- Skip unchanged notes
- Replace changed notes
- Remove stale notes
- Implement rebuild

### Epic: Retrieval

- Implement query embedding
- Implement dense search
- Implement filters
- Return provenance
- Add CLI search

### Epic: Evaluation

- Author benchmark
- Implement evaluator
- Record baseline
- Review failures

### Epic: MCP

- Create MCP host
- Implement `search_knowledge`
- Implement `get_note`
- Add integration tests

### Epic: Hardening

- Validate paths
- Validate configuration
- Harden local network defaults
- Review logs for content leakage
- Document operating procedure

## 14. Recommended implementation order

```text
Infrastructure
    ↓
Markdown parser
    ↓
Chunking
    ↓
Embeddings
    ↓
Qdrant indexing
    ↓
CLI retrieval
    ↓
Evaluation
    ↓
MCP
    ↓
Hardening
    ↓
Hybrid retrieval
```

The architecture deliberately puts evaluation before retrieval sophistication.
