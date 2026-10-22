# Obsidian Knowledge Platform

## Purpose

The Obsidian Knowledge Platform is a local-first knowledge retrieval system that indexes an Obsidian Markdown vault and exposes semantic search to AI clients through MCP.

The initial objective is deliberately narrow:

> Given an Obsidian vault containing Markdown files, create a searchable semantic index and expose it through an MCP server so an AI client can retrieve relevant note sections using natural-language queries with provenance.

## Status

Architecture baseline: **Approved for POC implementation**

The documentation in this package reflects the decisions reached during architecture discussions and resolves remaining ambiguities with recommended defaults.

## Governing principles

1. **Markdown is the source of truth.**
2. **Qdrant is derived state and must be rebuildable.**
3. **The indexer is the only writer to Qdrant.**
4. **MCP exposes knowledge-domain operations, not database mechanics.**
5. **No note content leaves the workstation by default.**
6. **Retrieval quality is measured, not judged only by intuition.**
7. **Start simple, preserve extension points, and add retrieval sophistication only when evaluation proves value.**

## Document set

| Document | Purpose |
|---|---|
| `01-solution-architecture.md` | End-to-end system architecture and component responsibilities |
| `02-poc-specification.md` | POC scope, functional requirements, NFRs, and acceptance criteria |
| `03-domain-and-data-contracts.md` | Canonical document/chunk/query/result contracts |
| `04-retrieval-evaluation-plan.md` | Retrieval benchmark and evaluation approach |
| `05-implementation-plan.md` | Milestones, work breakdown, and delivery sequence |
| `06-security-and-privacy.md` | Local-first security boundary and privacy requirements |
| `07-configuration-reference.md` | Proposed configuration model |
| `08-open-questions-and-assumptions.md` | Ambiguities, defaults, and deferred decisions |
| `11-milestone-2-implementation-plan.md` | Local embedding implementation sequence and verification gates |
| `adr/` | Architecture Decision Records |

## Initial technology baseline

- .NET 10
- Qdrant, local Docker deployment
- `BAAI/bge-small-en-v1.5`
- ONNX Runtime for local embedding inference
- 384-dimensional dense vectors
- Cosine similarity
- Dense semantic search for the initial POC
- Hybrid dense + sparse retrieval planned after baseline evaluation
- MCP as the AI-facing retrieval boundary

## Proposed repository layout

```text
obsidian-knowledge-platform/
├── src/
│   ├── Knowledge.Core/
│   ├── Knowledge.Markdown/
│   ├── Knowledge.Embeddings/
│   ├── Knowledge.Qdrant/
│   ├── Knowledge.Indexer/
│   └── Knowledge.Mcp/
├── tests/
│   ├── Knowledge.Core.Tests/
│   ├── Knowledge.Markdown.Tests/
│   ├── Knowledge.Indexing.Tests/
│   └── Knowledge.Retrieval.Tests/
├── eval/
│   └── retrieval-benchmark.json
├── docs/
│   ├── adr/
│   └── ...
├── docker/
│   └── docker-compose.yml
└── knowledge.yaml
```

## POC definition of done

The POC is complete when:

1. A configured Obsidian vault can be indexed into Qdrant.
2. Re-running indexing skips unchanged files.
3. Changed files are safely reindexed.
4. Deleted files are removed from the index.
5. Natural-language queries return relevant chunks with provenance.
6. The same retrieval capability is exposed through MCP.
7. A benchmark set demonstrates that expected notes appear in the top five results at an agreed baseline rate.
8. The entire Qdrant collection can be deleted and rebuilt from Markdown without loss of authoritative knowledge.
