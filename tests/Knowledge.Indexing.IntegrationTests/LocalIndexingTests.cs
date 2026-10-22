using Knowledge.Core;
using Knowledge.Embeddings;
using Knowledge.Indexer.Configuration;
using Knowledge.Indexer.Indexing;
using Knowledge.Markdown;
using Knowledge.Qdrant;
using Qdrant.Client;
using Xunit.Sdk;

namespace Knowledge.Indexing.IntegrationTests;

public sealed class LocalIndexingTests
{
    [Fact]
    public async Task Pinned_onnx_indexes_to_local_qdrant_and_unchanged_run_skips_inference()
    {
        var root = FindRepositoryRoot();
        var model = Path.Combine(root, "models", "bge-small-en-v1.5", "onnx", "model.onnx");
        var vocabulary = Path.Combine(root, "models", "bge-small-en-v1.5", "vocab.txt");
        if (!File.Exists(model) || !File.Exists(vocabulary))
            throw new XunitException("Install pinned model assets with scripts/setup-bge-model.sh before running the indexing integration suite.");

        using var vault = new IntegrationVault();
        vault.Write("Architecture.md", "---\ntags: [architecture]\naliases: [System design]\n---\n# Local Indexing\nThe indexer stores deterministic vector points in Qdrant and links to [[Storage|the storage layer]].");
        vault.Write("OptedOut.md", "---\nai-index: false\n---\nPrivate excluded text.");

        using var embeddings = new CountingEmbeddingService(new OnnxEmbeddingService(model, vocabulary));
        using var client = new QdrantClient("localhost", 6334, grpcTimeout: TimeSpan.FromSeconds(10));
        try { await client.HealthAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XunitException("Start local Qdrant with docker compose -f docker/docker-compose.yml up -d before running the indexing integration suite.");
        }

        var collection = "knowledge-indexing-test-" + Guid.NewGuid().ToString("N")[..12];
        var options = new KnowledgeOptions
        {
            Vault = new VaultOptions { Id = "integration", Path = vault.Path },
            Index = new IndexOptions { Include = ["**/*.md"], Exclude = [".obsidian/**"], FrontmatterOptOutField = "ai-index" },
            Chunking = new ChunkingOptions(),
            Embedding = new EmbeddingOptions { Provider = "onnx", Model = OnnxEmbeddingService.ModelName, Dimensions = OnnxEmbeddingService.Dimensions,
                ModelPath = model, TokenizerPath = vocabulary },
            Qdrant = new QdrantOptions { Host = "localhost", Port = 6334, Collection = collection }
        };

        try
        {
            var store = new QdrantKnowledgeIndexStore(client, collection);
            var coordinator = new IndexCoordinator(new MarkdownIngestor(new BertWordPieceTokenizer(vocabulary)), store, embeddings, options);
            var first = await coordinator.IndexAsync();
            Assert.True(first.IsComplete, string.Join("; ", first.Diagnostics.Select(diagnostic => diagnostic.Code)));
            Assert.Equal(1, first.Indexed);
            Assert.Equal(1, first.OptedOut);
            Assert.Equal(1, first.EmbeddingCalls);
            var metadata = await store.GetIndexedChunksAsync("integration");
            var point = Assert.Single(metadata);
            Assert.Equal("Architecture.md", point.RelativePath);
            var stored = Assert.Single(await client.RetrieveAsync(collection, Guid.Parse(point.PointId), withPayload: true, withVectors: false));
            Assert.Equal("Architecture.md", stored.Payload["path"].StringValue);
            Assert.Equal("The indexer stores deterministic vector points in Qdrant and links to [[Storage|the storage layer]].", stored.Payload["contentMarkdown"].StringValue);
            Assert.Contains("System design", stored.Payload["aliases"].ListValue.Values.Select(value => value.StringValue));
            Assert.Equal("the storage layer", stored.Payload["links"].ListValue.Values.Single().StructValue.Fields["displayText"].StringValue);

            var otherVaultPointId = Guid.NewGuid();
            var foreignVector = new float[OnnxEmbeddingService.Dimensions];
            foreignVector[0] = 1f;
            var otherVaultPoint = new global::Qdrant.Client.Grpc.PointStruct
            {
                Id = otherVaultPointId,
                Vectors = foreignVector
            };
            otherVaultPoint.Payload["vaultId"] = "another-vault";
            otherVaultPoint.Payload["documentId"] = "foreign-document";
            await client.UpsertAsync(collection, [otherVaultPoint], wait: true);

            var second = await coordinator.IndexAsync();
            Assert.True(second.IsComplete);
            Assert.Equal(1, second.Unchanged);
            Assert.Equal(0, second.EmbeddingCalls);
            Assert.Equal(1, embeddings.Calls);

            var queryEmbeddings = new BgeQueryEmbeddingService(embeddings, new BertWordPieceTokenizer(vocabulary));
            var profile = IndexProfile.Create(new MarkdownIngestionOptions(options.Vault.Id, options.Vault.Path,
                options.Index.Include, options.Index.Exclude, options.Index.FrontmatterOptOutField,
                options.Chunking.TargetTokens, options.Chunking.SoftMaxTokens, options.Chunking.OverlapTokens), options.Embedding);
            var retriever = new QdrantKnowledgeRetriever(client, collection, "integration", profile, queryEmbeddings);
            var beforeSearch = await store.GetIndexedChunksAsync("integration");
            var results = await retriever.SearchAsync(new KnowledgeSearchRequest(
                "How are vector points saved by the indexer?", Limit: 3, PathPrefix: "Arch", Tags: ["architecture"]));
            var result = Assert.Single(results);
            Assert.Equal("Architecture.md", result.Path);
            Assert.Equal("Local Indexing", result.Title);
            Assert.Contains("deterministic vector points", result.ContentMarkdown);
            Assert.Equal(2, embeddings.Calls);
            Assert.Equal(beforeSearch, await store.GetIndexedChunksAsync("integration"));
            Assert.Single(await client.RetrieveAsync(collection, otherVaultPointId, withPayload: true, withVectors: false));

            File.Delete(Path.Combine(vault.Path, "Architecture.md"));
            var afterDelete = await coordinator.IndexAsync();
            Assert.True(afterDelete.IsComplete);
            Assert.Equal(1, afterDelete.StaleDeleted);
            Assert.Empty(await store.GetIndexedChunksAsync("integration"));
            Assert.Single(await client.RetrieveAsync(collection, otherVaultPointId, withPayload: true, withVectors: false));
        }
        finally
        {
            await client.DeleteCollectionAsync(collection);
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Knowledge.sln"))) return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new XunitException("Run this integration suite from a repository checkout.");
    }

    private sealed class CountingEmbeddingService(IEmbeddingService inner) : IEmbeddingService, IDisposable
    {
        public int Calls { get; private set; }
        public async Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls++;
            return await inner.EmbedAsync(text, cancellationToken);
        }
        public void Dispose()
        {
            if (inner is IDisposable disposable) disposable.Dispose();
        }
    }

    private sealed class IntegrationVault : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "knowledge-index-" + Guid.NewGuid().ToString("N"));
        public IntegrationVault() => Directory.CreateDirectory(Path);
        public void Write(string path, string content) => File.WriteAllText(System.IO.Path.Combine(Path, path), content);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
