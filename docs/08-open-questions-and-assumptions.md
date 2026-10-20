# Open Questions, Ambiguities, and Recommended Defaults

This document records items that were not explicitly decided in discussion.

None of these should block the POC. A recommended default is provided for each.

## 1. Project name

### Ambiguity

No formal product name was specified.

### Recommended default

Use:

> **Obsidian Knowledge Platform**

as the working name.

This can be renamed without architectural impact.

---

## 2. Configuration filename

### Ambiguity

No filename was selected.

### Recommended default

Use:

```text
knowledge.yaml
```

---

## 3. Exact ONNX model artifact

### Ambiguity

The embedding model was selected, but not the exact ONNX export artifact or quantization.

### Recommended default

Start with a standard non-quantized or well-supported FP32/FP16 ONNX export that reproduces Sentence Transformers behavior correctly.

Do not optimize quantization until retrieval correctness is established.

Before implementation is considered complete, validate embeddings against a trusted reference implementation for several known strings.

---

## 4. Tokenizer implementation

### Ambiguity

No tokenizer library was selected.

### Recommended default

Use the tokenizer artifacts published with the selected model and a .NET-compatible tokenizer implementation capable of matching the model's WordPiece behavior.

Correct tokenization is more important than avoiding a small dependency.

---

## 5. Path casing

### Ambiguity

macOS and Windows may use case-insensitive filesystems while Linux commonly uses case-sensitive semantics.

### Recommended default

Use the path exactly as discovered after canonical vault-relative normalization. Do not forcibly lowercase paths.

A rename is delete + add.

---

## 6. Symlinks

### Ambiguity

Obsidian vaults may contain symlinks.

### Recommended default

Do not index files whose resolved path escapes the configured vault root.

Allow internal symlinks only if they resolve inside the vault.

---

## 7. Heading-less notes

### Ambiguity

Some notes may not contain headings.

### Recommended default

Use the note title as context and chunk the body by paragraphs and semantic block boundaries.

---

## 8. Note title resolution

### Ambiguity

Should title come from filename, first H1, or frontmatter?

### Recommended default

Resolution order:

1. frontmatter `title` when present
2. first H1
3. filename without `.md`

Store the relative path separately so title changes do not affect document identity.

---

## 9. Frontmatter tags vs inline tags

### Ambiguity

Obsidian supports frontmatter tags and inline `#tags`.

### Recommended default

V1 must support frontmatter tags.

Inline tag extraction is recommended if the Markdown parser can implement it reliably without mistaking headings or code for tags.

If inline tags add complexity, defer them without changing the payload schema.

---

## 10. Aliases

### Ambiguity

Aliases were not discussed in detail.

### Recommended default

Parse frontmatter aliases and include them in embedding context only when they materially name the note.

Store them in payload.

---

## 11. Large code blocks

### Ambiguity

The preferred rule says to keep code blocks intact, but an individual code block could exceed the model hard token limit.

### Recommended default

Preserve code blocks intact whenever possible.

If a single code block exceeds the hard model limit, split it by line ranges with small contextual continuation markers. Hard model limits always win.

---

## 12. Large tables

### Ambiguity

A table may exceed the hard model limit.

### Recommended default

Repeat the table header and split rows into multiple chunks.

---

## 13. Query prefix for BGE

### Ambiguity

BGE family models can use retrieval-specific query instructions depending on version and reference implementation.

### Recommended default

Match the official `BAAI/bge-small-en-v1.5` retrieval guidance used by the selected runtime/export.

Treat the query formatting rule as part of `EmbeddingSchemaVersion`.

Do not hard-code model-specific query strings in core retrieval logic.

---

## 14. Embedding normalization

### Ambiguity

The exact runtime implementation may require explicit L2 normalization.

### Recommended default

Match the official/reference Sentence Transformers behavior for the selected model and validate cosine results against it.

Embedding normalization is part of the embedding implementation, not the caller.

---

## 15. Qdrant collection migration

### Ambiguity

No schema migration strategy was defined.

### Recommended default

For the POC, use rebuild instead of migration.

Any incompatible schema or embedding change increments collection/index schema version and recreates the derived index.

---

## 16. Qdrant persistence location

### Ambiguity

Docker persistence path was not specified.

### Recommended default

Use a named Docker volume in the POC.

Do not place Qdrant database files inside the Obsidian vault.

---

## 17. Docker vs native Qdrant

### Decision

Local Docker was selected.

### Recommended default

Use Docker Compose to make the local dependency explicit and repeatable.

---

## 18. Multiple vaults

### Decision

Schema supports `vaultId`; V1 exposes one configured vault.

### Recommended default

Do not implement cross-vault ranking or multiple configured roots until required.

---

## 19. Search result deduplication

### Ambiguity

Adjacent chunks from the same note may dominate top results.

### Recommended default

Do not add complex diversity logic initially.

Record this behavior during evaluation. Add per-document diversification only if benchmark/human review identifies it as a problem.

---

## 20. Minimum relevance threshold

### Ambiguity

No similarity cutoff was selected.

### Recommended default

Do not impose a hard score threshold in the first implementation.

Return top-K and evaluate score distributions. Add thresholds only with benchmark evidence.

---

## 21. Dense search candidate count

### Ambiguity

No internal candidate count was selected.

### Recommended default

For dense-only POC, request the exact requested result count unless Qdrant filtering or future reranking requires a larger candidate set.

---

## 22. File watcher

### Decision

Deferred.

### Recommended default

Explicit indexing commands only.

---

## 23. Hybrid search

### Decision

Architecturally planned, not part of the initial dense POC.

### Recommended default

Introduce after a dense benchmark is recorded.

---

## 24. SQLite

### Decision

No SQLite in V1.

Qdrant is the sole derived persistent store.

Add a secondary catalog only if future graph, workflow, sync, or relational requirements justify it.

---

## 25. Benchmark threshold

### Ambiguity

No target was explicitly approved.

### Recommended default

Use 85% Hit@5 as a practical POC target.

The purpose is to establish a baseline, not create a contractual SLA.

---

## 26. Licensing and model distribution

### Ambiguity

How model artifacts are acquired/distributed was not specified.

### Recommended default

Do not commit large third-party model binaries to the source repository unless repository policy explicitly permits it.

Provide a documented setup/download step or artifact cache strategy and retain license/attribution information as required.

---

## 27. MCP transport

### Ambiguity

Specific MCP transport was not selected.

### Recommended default

Use the simplest local transport supported by the chosen .NET MCP SDK and target client, preferably stdio for the POC when practical.

Transport must not change the knowledge-domain tool contract.

---

## 28. Document content returned by search

### Ambiguity

Return normalized text or original Markdown?

### Recommended default

Return original Markdown plus provenance.

Use normalized text internally for embedding.

---

## 29. `get_note` data source

### Ambiguity

Read from Qdrant or Markdown?

### Recommended default

Read current authoritative Markdown from the vault.

Qdrant is for search, not authoritative note reconstruction.

---

## 30. Deferred decisions

Explicitly defer:

- graph database
- graph traversal ranking
- reranker model
- cloud hosting
- remote MCP authentication
- cloud embedding provider
- multi-user access
- attachment indexing
- OCR
- automatic note summarization
- agent write-back
- background watcher/service mode
