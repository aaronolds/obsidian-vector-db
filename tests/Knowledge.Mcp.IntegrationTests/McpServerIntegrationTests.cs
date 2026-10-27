using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Google.Protobuf;
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
            var indexedState = await SnapshotIndexedStateAsync(qdrant, collection, indexedChunks, foreignIndexedChunks);
            var initialSourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(vault.FilePath("Architecture/Mcp.md"))));
            var otherSourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(vault.FilePath("Other.md"))));

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
            using var cliOutput = new StringWriter();
            using var cliError = new StringWriter();
            var cliCode = await Knowledge.Indexer.KnowledgeCli.RunAsync([
                "search", "Violet Kestrel 7412", "--limit", "5", "--path", "Architecture/",
                "--tag", "mcp", "--tag", "acceptance", "--config", configPath
            ], cliOutput, cliError);
            Assert.Equal(0, cliCode);
            Assert.DoesNotContain("Violet Kestrel 7412", cliError.ToString());
            Assert.Equal(cliResults.Select(item => item.ChunkId), ExtractCliChunkIds(cliOutput.ToString()));
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

            var concurrentResults = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                mcp.CallToolAsync("search_knowledge", new Dictionary<string, object?>
                {
                    ["query"] = "Violet Kestrel 7412",
                    ["limit"] = 5,
                    ["pathPrefix"] = "Architecture/",
                    ["tags"] = new[] { "mcp", "acceptance" }
                }).AsTask()));
            Assert.All(concurrentResults, result => Assert.False(result.IsError, ReadText(result)));
            foreach (var concurrentResult in concurrentResults)
            {
                var concurrentResponse = JsonSerializer.Deserialize<SearchResponse>(concurrentResult.StructuredContent!.Value,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.NotNull(concurrentResponse);
                Assert.Equal(response.Results.Select(item => item.ChunkId),
                    concurrentResponse.Results.Select(item => item.ChunkId));
            }

            using var defaultCliOutput = new StringWriter();
            using var defaultCliError = new StringWriter();
            var defaultCliCode = await Knowledge.Indexer.KnowledgeCli.RunAsync([
                "search", "Violet Kestrel 7412", "--config", configPath
            ], defaultCliOutput, defaultCliError);
            Assert.Equal(0, defaultCliCode);
            var defaultMcpSearch = await mcp.CallToolAsync("search_knowledge", new Dictionary<string, object?>
            {
                ["query"] = "Violet Kestrel 7412"
            });
            Assert.False(defaultMcpSearch.IsError, ReadText(defaultMcpSearch));
            var defaultResponse = JsonSerializer.Deserialize<SearchResponse>(defaultMcpSearch.StructuredContent!.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(defaultResponse);
            Assert.Equal(Math.Min(options.Retrieval.DefaultLimit, indexedChunks.Count), defaultResponse.Results.Count);
            Assert.Equal(ExtractCliChunkIds(defaultCliOutput.ToString()), defaultResponse.Results.Select(item => item.ChunkId));

            var emptySearch = await mcp.CallToolAsync("search_knowledge", new Dictionary<string, object?>
            {
                ["query"] = "Violet Kestrel 7412",
                ["tags"] = new[] { "no-such-acceptance-tag" }
            });
            Assert.False(emptySearch.IsError, ReadText(emptySearch));
            Assert.Empty(JsonSerializer.Deserialize<SearchResponse>(emptySearch.StructuredContent!.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Results);
            using var emptyCliOutput = new StringWriter();
            using var emptyCliError = new StringWriter();
            var emptyCliCode = await Knowledge.Indexer.KnowledgeCli.RunAsync([
                "search", "Violet Kestrel 7412", "--tag", "no-such-acceptance-tag", "--config", configPath
            ], emptyCliOutput, emptyCliError);
            Assert.Equal(0, emptyCliCode);
            Assert.Contains("No matching notes found.", emptyCliOutput.ToString());
            Assert.Empty(ExtractCliChunkIds(emptyCliOutput.ToString()));

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
            Assert.Equal(initialSourceHash,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(vault.FilePath("Architecture/Mcp.md")))));

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
            Assert.Equal(otherSourceHash,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(vault.FilePath("Other.md")))));
            Assert.Equal(indexedState,
                await SnapshotIndexedStateAsync(qdrant, collection, indexedChunks, foreignIndexedChunks));

            await VerifyFailedSearchCanRetryAfterCollectionAppearsAsync(qdrant, vault, model, tokenizerPath);
            await VerifyUnavailableQdrantLeavesNoteReadsAvailableAsync(vault, model, tokenizerPath);
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

    private static string[] ExtractCliChunkIds(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith("   Chunk ID: ", StringComparison.Ordinal))
        .Select(line => line["   Chunk ID: ".Length..].TrimEnd('\r'))
        .ToArray();

    private static async Task<string> SnapshotIndexedStateAsync(QdrantClient client, string collection,
        params IReadOnlyList<IndexedChunkMetadata>[] vaultChunks)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var chunk in vaultChunks.SelectMany(items => items).OrderBy(item => item.PointId, StringComparer.Ordinal))
        {
            var point = Assert.Single(await client.RetrieveAsync(collection, Guid.Parse(chunk.PointId),
                withPayload: true, withVectors: true));
            using var json = JsonDocument.Parse(JsonFormatter.Default.Format(point));
            hash.AppendData(Encoding.UTF8.GetBytes(Canonicalize(json.RootElement)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Canonicalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + Canonicalize(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonicalize)) + "]",
        _ => value.GetRawText()
    };

    private static async Task VerifyFailedSearchCanRetryAfterCollectionAppearsAsync(
        QdrantClient qdrant, IntegrationVault vault, string model, string tokenizerPath)
    {
        var collection = "knowledge-mcp-retry-" + Guid.NewGuid().ToString("N")[..12];
        var options = CreateOptions(vault.Path, collection, model, tokenizerPath);
        var configPath = vault.WriteConfiguration(options, $"knowledge-{collection}.yaml");
        var serverAssembly = typeof(Program).Assembly.Location;
        try
        {
            await using var retryClient = await ModelContextProtocol.Client.McpClient.CreateAsync(
                new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = "dotnet",
                    Arguments = [serverAssembly, "--config", configPath],
                    WorkingDirectory = FindRepositoryRoot(),
                    ShutdownTimeout = TimeSpan.FromSeconds(5),
                    Name = "Obsidian Knowledge MCP retry integration"
                }));

            var firstSearch = await retryClient.CallToolAsync("search_knowledge", new Dictionary<string, object?>
            {
                ["query"] = "Violet Kestrel 7412"
            });
            Assert.True(firstSearch.IsError);
            Assert.Contains("index_incompatible", ReadText(firstSearch));

            var note = await retryClient.CallToolAsync("get_note", new Dictionary<string, object?>
            {
                ["path"] = "Other.md"
            });
            Assert.False(note.IsError, ReadText(note));

            var tokenizer = new BertWordPieceTokenizer(tokenizerPath);
            using var embeddings = new OnnxEmbeddingService(model, tokenizerPath);
            var store = new QdrantKnowledgeIndexStore(qdrant, collection);
            var coordinator = new IndexCoordinator(new MarkdownIngestor(tokenizer), store, embeddings, options);
            var indexed = await coordinator.IndexAsync();
            Assert.True(indexed.IsComplete, string.Join(", ", indexed.Diagnostics.Select(item => item.Code)));

            var retrySearch = await retryClient.CallToolAsync("search_knowledge", new Dictionary<string, object?>
            {
                ["query"] = "Violet Kestrel 7412"
            });
            Assert.False(retrySearch.IsError, ReadText(retrySearch));
            Assert.Contains(JsonSerializer.Deserialize<SearchResponse>(retrySearch.StructuredContent!.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Results,
                item => item.Path == "Architecture/Mcp.md");

            var afterInvalid = await retryClient.CallToolAsync("search_knowledge", new Dictionary<string, object?>
            {
                ["query"] = "   "
            });
            Assert.True(afterInvalid.IsError);
            Assert.Contains("invalid_request", ReadText(afterInvalid));
            var afterInvalidValid = await retryClient.CallToolAsync("get_note", new Dictionary<string, object?>
            {
                ["path"] = "Other.md"
            });
            Assert.False(afterInvalidValid.IsError, ReadText(afterInvalidValid));
        }
        finally
        {
            if (await qdrant.CollectionExistsAsync(collection)) await qdrant.DeleteCollectionAsync(collection);
        }
    }

    private static async Task VerifyUnavailableQdrantLeavesNoteReadsAvailableAsync(
        IntegrationVault vault, string model, string tokenizerPath)
    {
        var options = CreateOptions(vault.Path, "knowledge-mcp-unavailable-" + Guid.NewGuid().ToString("N")[..12], model, tokenizerPath);
        options.Qdrant.Port = 1;
        var configPath = vault.WriteConfiguration(options, $"knowledge-{options.Qdrant.Collection}.yaml");
        await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = [typeof(Program).Assembly.Location, "--config", configPath],
                WorkingDirectory = FindRepositoryRoot(),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                Name = "Obsidian Knowledge MCP unavailable Qdrant integration"
            }));

        var search = await client.CallToolAsync("search_knowledge", new Dictionary<string, object?>
        {
            ["query"] = "PRIVATE_QUERY_SENTINEL"
        });
        Assert.True(search.IsError);
        Assert.Contains("retrieval_unavailable", ReadText(search));
        Assert.DoesNotContain("PRIVATE_QUERY_SENTINEL", ReadText(search));

        var note = await client.CallToolAsync("get_note", new Dictionary<string, object?>
        {
            ["path"] = "Other.md"
        });
        Assert.False(note.IsError, ReadText(note));
    }

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

        public string FilePath(string relative) =>
            System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Write(string relative, string markdown)
        {
            var destination = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, markdown);
        }

        public string WriteConfiguration(KnowledgeOptions options, string fileName = "knowledge.yaml")
        {
            var configPath = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(configPath, $"""
                vault:
                  id: {options.Vault.Id}
                  path: '{options.Vault.Path.Replace("'", "''")}'
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
                  modelPath: '{options.Embedding.ModelPath.Replace("'", "''")}'
                  tokenizerPath: '{options.Embedding.TokenizerPath.Replace("'", "''")}'
                qdrant:
                  host: {options.Qdrant.Host}
                  port: {options.Qdrant.Port}
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
