# POC Specification

## 1. Purpose

This specification defines the initial proof of concept for the Obsidian Knowledge Platform.

## 2. POC objective

Demonstrate that an Obsidian Markdown vault can be indexed locally into Qdrant and queried semantically through both a CLI and MCP with useful provenance and measurable retrieval quality.

## 3. Scope

### Included

- One configured Obsidian vault.
- Recursive Markdown discovery.
- Configurable include/exclude patterns.
- YAML frontmatter parsing.
- Heading-aware chunking.
- Obsidian wikilink extraction.
- Local embeddings using `BAAI/bge-small-en-v1.5`.
- Qdrant local Docker deployment.
- Incremental document-level indexing.
- Dense semantic retrieval.
- CLI search.
- MCP search.
- Provenance.
- Retrieval benchmark.

### Excluded

- Sparse/hybrid retrieval.
- Reranking.
- Graph traversal.
- Binary attachments.
- OCR.
- Cloud embeddings.
- Cloud Qdrant.
- Multi-user access.
- Filesystem watcher.
- Agent write-back.

## 4. Functional requirements

### FR-001 Vault discovery

The system shall recursively discover Markdown files under the configured vault root.

### FR-002 Include/exclude rules

The system shall apply configurable include and exclude patterns before parsing files.

Default excludes:

- `.obsidian/**`
- `.trash/**`
- `Templates/**`

### FR-003 Index opt-out

The system shall exclude a document when frontmatter contains:

```yaml
ai-index: false
```

### FR-004 Frontmatter parsing

The system shall parse YAML frontmatter and extract at minimum:

- tags
- aliases
- `ai-index`

### FR-005 Markdown structure

The system shall identify:

- title
- heading hierarchy
- paragraphs
- lists
- tables
- blockquotes
- fenced code blocks
- inline code
- Obsidian wikilinks

### FR-006 Wikilinks

The system shall extract wikilink targets and display text.

### FR-007 Chunking

The system shall create semantically coherent chunks using Markdown structure.

### FR-008 Chunk model constraint

The system shall ensure the final constructed embedding input does not exceed the embedding model tokenizer limit.

### FR-009 Embedding context

The embedding input shall include:

- note title
- heading path
- tags when present
- normalized chunk content

### FR-010 Local embeddings

The system shall generate embeddings locally using `BAAI/bge-small-en-v1.5`.

### FR-011 Qdrant vectors

The Qdrant collection shall use 384-dimensional dense vectors with cosine similarity.

### FR-012 Provenance metadata

Every indexed chunk shall retain enough information to identify:

- vault
- note path
- title
- heading path
- chunk ordinal
- content hash
- original Markdown content

### FR-013 Deterministic document identity

Document IDs shall be generated deterministically from vault ID and normalized relative path.

### FR-014 Deterministic chunk identity

Chunk IDs shall be generated deterministically from document identity, heading context, and chunk ordinal.

### FR-015 File hashing

The indexer shall calculate a content hash for every included Markdown document.

### FR-016 Skip unchanged documents

If the current file hash matches the indexed hash, the indexer shall not regenerate that document's embeddings.

### FR-017 Reindex changed documents

When a document changes, the indexer shall replace all indexed chunks for that document.

### FR-018 Remove deleted documents

When a previously indexed document is no longer present or becomes excluded, the indexer shall delete its chunks.

### FR-019 Rebuild

The system shall support rebuilding the complete Qdrant collection from Markdown.

### FR-020 Status

The CLI shall report basic index status including:

- configured vault
- Qdrant connectivity
- collection existence
- indexed document count
- indexed chunk count
- embedding model identity

### FR-021 CLI search

The system shall allow semantic search from the CLI before MCP integration is considered complete.

### FR-022 MCP search

The MCP server shall expose `search_knowledge`.

### FR-023 MCP note retrieval

The MCP server shall expose `get_note`.

### FR-024 Search filters

`search_knowledge` shall support optional:

- result limit
- path prefix
- tags

### FR-025 Search provenance

Every search result shall return:

- note path
- title
- heading path
- Markdown content
- score

## 5. Nonfunctional requirements

### NFR-001 Local-first

Indexing, embedding generation, Qdrant storage, and MCP shall run locally for the POC.

### NFR-002 No content egress

No note content shall be transmitted to an external embedding API by default.

### NFR-003 Rebuildability

Deleting the Qdrant collection shall not cause authoritative knowledge loss.

### NFR-004 Determinism

Given the same vault content, configuration, embedding model, and software version, a rebuild should produce equivalent document/chunk identity and content.

### NFR-005 Privacy-aware logging

Application logs shall not contain note bodies, embedding text, vectors, or retrieved content by default.

### NFR-006 Failure transparency

Indexing failures shall identify the affected source path and shall not be silently ignored.

### NFR-007 Configurability

Vault path, Qdrant endpoint, chunk settings, include/exclude patterns, and result limits shall be configurable.

### NFR-008 Replaceable embeddings

Core indexing and retrieval code shall depend on `IEmbeddingService`, not directly on ONNX Runtime.

### NFR-009 Retrieval abstraction

MCP shall depend on a retrieval abstraction such as `IKnowledgeRetriever`, not directly on raw Qdrant search mechanics.

## 6. Proposed CLI

```bash
knowledge index
knowledge rebuild
knowledge status
knowledge search "How does OBO authentication work?"
```

Optional examples:

```bash
knowledge search "X-Azure-FDID" --limit 10
knowledge search "Front Door restrictions" --path Architecture/
knowledge search "MCP authentication" --tag architecture
```

## 7. Acceptance criteria

### AC-001 Initial indexing

Given an eligible Markdown vault, `knowledge index` creates a Qdrant collection and indexes all eligible notes without manual database intervention.

### AC-002 Incremental no-op

Running `knowledge index` twice without modifying the vault does not regenerate embeddings on the second run.

### AC-003 Change handling

Modifying a note causes only that document to be reindexed.

### AC-004 Deletion handling

Deleting or excluding a note removes it from search results after the next index run.

### AC-005 Rebuild

Deleting the collection and running `knowledge rebuild` restores searchable knowledge from the vault.

### AC-006 Semantic search

A natural-language query can retrieve a conceptually relevant passage even when query wording differs from note wording.

### AC-007 Exact technical-term baseline

Exact technical identifiers are evaluated and documented. Dense-only retrieval is not required to outperform lexical retrieval for identifiers in the POC.

### AC-008 Provenance

Every returned result identifies the note and section from which it came.

### AC-009 MCP equivalence

MCP search returns materially the same ranked knowledge as CLI search using the same retrieval service.

### AC-010 Benchmark

The retrieval benchmark contains at least 20 representative questions before the POC is declared complete.

Recommended baseline target:

> At least 85% of benchmark queries should place an expected document in the top five dense-search results.

This target is a recommended initial threshold, not a contractual production SLA. If the baseline is lower, the benchmark results become input to the hybrid-search milestone rather than invalidating the architecture.

## 8. Definition of done

The POC is done when all acceptance criteria are demonstrated and benchmark results are recorded.
