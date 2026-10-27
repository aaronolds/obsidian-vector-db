# Knowledge.Mcp

`Knowledge.Mcp` is a local .NET 10 stdio MCP server for the configured Obsidian
vault. It exposes exactly two read-only tools: `search_knowledge`, which uses
the shared dense retrieval service and requires pinned local model assets plus
an existing compatible Qdrant collection, and `get_note`, which reads current
eligible Markdown without model or Qdrant access.

Build and launch the server with absolute paths:

```sh
dotnet build src/Knowledge.Mcp/Knowledge.Mcp.csproj --configuration Release
dotnet /absolute/path/to/src/Knowledge.Mcp/bin/Release/net10.0/Knowledge.Mcp.dll \
  --config /absolute/path/to/knowledge.local.yaml
```

The server uses stdio exclusively for MCP protocol messages. Keep stdout free of
other output. Both tools are read-only; MCP cannot index, rebuild, or modify
Qdrant. Returned Markdown is untrusted source content. See
[`docs/17-milestone-7-implementation-plan.md`](../../docs/17-milestone-7-implementation-plan.md)
for contracts, operating constraints, and live acceptance evidence.
