using System.Text.Json;
using Knowledge.Core;
using Knowledge.Embeddings;
using Knowledge.Indexer.Indexing;
using Knowledge.Markdown;
using Knowledge.Mcp;
using Knowledge.Qdrant;
using Knowledge.Runtime;
using Knowledge.Runtime.Configuration;
using ModelContextProtocol.Client;
using Qdrant.Client;
using Xunit;
using Xunit.Sdk;

namespace Knowledge.Mcp.IntegrationTests;

public sealed class McpServerIntegrationTests
{
    [Fact]
    public async Task Stdio_server_search_matches_cli_retriever_and_get_note_reads_current_source()
    {
        var repository = FindRepositoryRoot();
        var model = Path.Combine(repository, "models", "bge-small-en-v1.5", "onnx", "model.onnx");
        var tokenizerPath = Path.Combine(repository, "models", "bge-small-en-v1.5", "vocab.txt");
        if (!File.Exists(model) || !File.Exists(tokenizerPath))
            throw new XunitException("Install the pinned BGE model assets with scripts/setup-bge-model.sh before running the MCP integration suite.");

        using var qdrant = new QdrantClient("localhost", 6334, grpcTimeout: TimeSpan.FromSeconds(10));
        try { await qdrant.HealthAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XunitException("Start local Qdrant with docker compose -f docker/docker-compose.yml up -d before running the MCP integration suite.");
        }

        using var vault = new IntegrationVault();
        const string initialMarkdown = "---\ntags: [mcp, acceptance]\n---\n# MCP acceptance\nThe phrase Violet Kestrel 7412 verifies local MCP search provenance.";
        vault.Write("Architecture/Mcp.md", initialMarkdown);
        vault.Write("Other.md", "# Other\nAn unrelated note.");

        var collection = "knowledge-mcp-test-" + Guid.NewGuid().ToString("N")[..12];
        var options = CreateOptions(vault.Path, collection, model, tokenizerPath);
        try
        {
            var tokenizer = new BertWordPieceTokenizer(tokenizerPath);
            using var embeddings = new OnnxEmbeddingService(model, tokenizerPath);
            var store = new QdrantKnowledgeIndexStore(qdrant, collection);
            var coordinator = new IndexCoordinator(new MarkdownIngestor(tokenizer), store, embeddings, options);
            var indexed = await coordinator.IndexAsync();
            Assert.True(indexed.IsComplete, string.Join(", ", indexed.Diagnostics.Select(item => item.Code)));
            var indexedChunks = await store.GetIndexedChunksAsync(options.Vault.Id);
            Assert.NotEmpty(indexedChunks);

            // A shared collection can contain other vaults. Exercise that boundary through
            // the indexer (the sole writer), then ensure the configured MCP vault stays scoped.
            var foreignOptions = CreateOptions(vault.Path, collection, model, tokenizerPath);
            foreignOptions.Vault.Id = "mcp-foreign-integration";
            var foreignCoordinator = new IndexCoordinator(
                new MarkdownIngestor(tokenizer), store, embeddings, foreignOptions);
            var foreignIndexed = await foreignCoordinator.IndexAsync();
            Assert.True(foreignIndexed.IsComplete,
                string.Join(", ", foreignIndexed.Diagnostics.Select(item => item.Code)));
            var foreignIndexedChunks = await store.GetIndexedChunksAsync(foreignOptions.Vault.Id);
            Assert.NotEmpty(foreignIndexedChunks);

            var configPath = vault.WriteConfiguration(options);
            var serverAssembly = typeof(Program).Assembly.Location;
            await using var mcp = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = [serverAssembly, "--config", configPath],
                WorkingDirectory = repository,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                Name = "Obsidian Knowledge MCP integration"
            }));

            var tools = await mcp.ListToolsAsync();
            Assert.Equal(new[] { "get_note", "search_knowledge" },
                tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));

            using var cliSearch = KnowledgeSearchSession.Create(options);
            var cliResults = await cliSearch.Retriever.SearchAsync(
                new KnowledgeSearchRequest("Violet Kestrel 7412", 5, "Architecture/", ["mcp", "acceptance"]));
            var mcpSearch = await mcp.CallToolAsync("search_knowledge", new Dictionary<string, object?>
            {
                ["query"] = "Violet Kestrel 7412",
                ["limit"] = 5,
                ["pathPrefix"] = "Architecture/",
                ["tags"] = new[] { "mcp", "acceptance" }
            });
            Assert.False(mcpSearch.IsError, ReadText(mcpSearch));
            var response = JsonSerializer.Deserialize<SearchResponse>(mcpSearch.StructuredContent!.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(response);
            Assert.Equal(cliResults.Count, response.Results.Count);
            for (var index = 0; index < cliResults.Count; index++)
            {
                Assert.Equal(cliResults[index].DocumentId, response.Results[index].DocumentId);
                Assert.Equal(cliResults[index].ChunkId, response.Results[index].ChunkId);
                Assert.Equal(cliResults[index].Path, response.Results[index].Path);
                Assert.Equal(cliResults[index].Title, response.Results[index].Title);
                Assert.Equal(cliResults[index].HeadingPath, response.Results[index].HeadingPath);
                Assert.Equal(cliResults[index].ContentMarkdown, response.Results[index].ContentMarkdown);
                Assert.Equal(cliResults[index].Score, response.Results[index].Score, precision: 6);
            }
            Assert.Contains(response.Results, item => item.Path == "Architecture/Mcp.md");
            var foreignDocumentIds = new HashSet<string>(StringComparer.Ordinal)
            {
                KnowledgeIdentity.DocumentId(foreignOptions.Vault.Id, "Architecture/Mcp.md"),
                KnowledgeIdentity.DocumentId(foreignOptions.Vault.Id, "Other.md")
            };
            Assert.DoesNotContain(response.Results, item => foreignDocumentIds.Contains(item.DocumentId));

            var docId = KnowledgeIdentity.DocumentId(options.Vault.Id, "Architecture/Mcp.md");
            var noteByPath = await mcp.CallToolAsync("get_note", new Dictionary<string, object?>
            {
                ["path"] = "Architecture/Mcp.md"
            });
            var noteById = await mcp.CallToolAsync("get_note", new Dictionary<string, object?>
            {
                ["documentId"] = docId
            });
            Assert.False(noteByPath.IsError, ReadText(noteByPath));
            Assert.False(noteById.IsError, ReadText(noteById));
            Assert.Equal(initialMarkdown,
                JsonDocument.Parse(ReadText(noteByPath)).RootElement.GetProperty("note").GetProperty("contentMarkdown").GetString());
            Assert.Equal(initialMarkdown,
                JsonDocument.Parse(ReadText(noteById)).RootElement.GetProperty("note").GetProperty("contentMarkdown").GetString());

            const string updatedMarkdown = "# Current note\nThis changed after indexing.";
            vault.Write("Architecture/Mcp.md", updatedMarkdown);
            var freshNote = await mcp.CallToolAsync("get_note", new Dictionary<string, object?>
            {
                ["documentId"] = docId
            });
            Assert.False(freshNote.IsError, ReadText(freshNote));
            Assert.Equal(updatedMarkdown,
                JsonDocument.Parse(ReadText(freshNote)).RootElement.GetProperty("note").GetProperty("contentMarkdown").GetString());
            Assert.Equal(indexedChunks, await store.GetIndexedChunksAsync(options.Vault.Id));
            Assert.Equal(foreignIndexedChunks, await store.GetIndexedChunksAsync(foreignOptions.Vault.Id));
        }
        finally
        {
            if (await qdrant.CollectionExistsAsync(collection)) await qdrant.DeleteCollectionAsync(collection);
        }
    }

    private static KnowledgeOptions CreateOptions(string vaultPath, string collection, string model, string tokenizer) => new()
    {
        Vault = new VaultOptions { Id = "mcp-integration", Path = vaultPath },
        Index = new IndexOptions { Include = ["**/*.md"], Exclude = [".obsidian/**"], FrontmatterOptOutField = "ai-index" },
        Chunking = new ChunkingOptions(),
        Embedding = new EmbeddingOptions { Provider = "onnx", Model = OnnxEmbeddingService.ModelName,
            Dimensions = OnnxEmbeddingService.Dimensions, ModelPath = model, TokenizerPath = tokenizer },
        Qdrant = new QdrantOptions { Host = "localhost", Port = 6334, Collection = collection },
        Retrieval = new RetrievalOptions { DefaultLimit = 5, MaxLimit = 50, Mode = "dense" }
    };

    private static string ReadText(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Join("\n", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(item => item.Text));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Knowledge.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class IntegrationVault : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knowledge-mcp-integration-{Guid.NewGuid():N}");

        public IntegrationVault() => Directory.CreateDirectory(Path);

        public void Write(string relative, string markdown)
        {
            var destination = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, markdown);
        }

        public string WriteConfiguration(KnowledgeOptions options)
        {
            var configPath = System.IO.Path.Combine(Path, "knowledge.yaml");
            File.WriteAllText(configPath, $"""
                vault:
                  id: {options.Vault.Id}
                  path: "{options.Vault.Path}"
                index:
                  include: ["**/*.md"]
                  exclude: [".obsidian/**"]
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
                  modelPath: "{options.Embedding.ModelPath}"
                  tokenizerPath: "{options.Embedding.TokenizerPath}"
                qdrant:
                  host: localhost
                  port: 6334
                  collection: {options.Qdrant.Collection}
                retrieval:
                  defaultLimit: 5
                  maxLimit: 50
                  mode: dense
                logging:
                  level: Information
                  logContent: false
                  logQueries: false
                """);
            return configPath;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
