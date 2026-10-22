# Knowledge.Core

Dependency-free domain boundary for vault, document, chunk, embedding, indexing,
and search contracts. Retrieval requests/results and the retriever/query-embedding
interfaces live here so the CLI and future MCP host can share one contract.
Application configuration belongs to the Indexer composition root; infrastructure
types remain in their owning projects.
