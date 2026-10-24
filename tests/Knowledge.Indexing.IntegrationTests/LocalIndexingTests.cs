using System.Text.Json;
using Knowledge.Core;
using Knowledge.Embeddings;
using Knowledge.Runtime.Configuration;
using Knowledge.Runtime.Indexing;
using Knowledge.Indexer.Indexing;
using Knowledge.Indexer.Evaluation;
using Knowledge.Markdown;
using Knowledge.Qdrant;
using Qdrant.Client;
using Xunit.Sdk;

namespace Knowledge.Indexing.IntegrationTests;

public sealed class LocalIndexingTests
{
    [Fact]
    public async Task Knowledge_cli_evaluate_runs_read_only_against_local_qdrant_and_pinned_onnx()
    {
        var root = FindRepositoryRoot();
        var model = Path.Combine(root, "models", "bge-small-en-v1.5", "onnx", "model.onnx");
        var vocabulary = Path.Combine(root, "models", "bge-small-en-v1.5", "vocab.txt");
        if (!File.Exists(model) || !File.Exists(vocabulary))
            throw new XunitException("Install pinned model assets with scripts/setup-bge-model.sh before running the indexing integration suite.");

        using var vault = new IntegrationVault();
        vault.Write("Architecture.md", "# Local indexing\n\nPRIVATE_NOTE_SENTINEL: The local indexer stores markdown passages as vector points.");
        var collection = "knowledge-evaluate-test-" + Guid.NewGuid().ToString("N")[..12];
        using var client = new QdrantClient("localhost", 6334, grpcTimeout: TimeSpan.FromSeconds(10));
        try { await client.HealthAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XunitException("Start local Qdrant with docker compose -f docker/docker-compose.yml up -d before running the indexing integration suite.");
        }

        using var config = new TemporaryFile(".yaml");
        using var benchmark = new TemporaryFile(".json");
        using var report = new TemporaryFile(".json");
        const string querySentinel = "PRIVATE_QUERY_SENTINEL";
        await File.WriteAllTextAsync(config.Path,
            $"""
             vault:
               id: integration
               path: "{vault.Path}"
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
               modelPath: "{model}"
               tokenizerPath: "{vocabulary}"
             qdrant:
               host: localhost
               port: 6334
               collection: {collection}
             retrieval:
               defaultLimit: 2
               maxLimit: 50
               mode: dense
             logging:
               level: Warning
               logContent: false
               logQueries: false
             """);
        var entries = Enumerable.Range(0, 20).Select(index => new RetrievalBenchmarkEntry(
            $"integration-{index}", $"{querySentinel} How does local indexing store markdown passages?",
            ["Architecture.md"], "semantic")).ToArray();
        var benchmarkJson = entries.Select(entry => new
        {
            id = entry.Id,
            query = entry.Query,
            expectedPaths = entry.ExpectedPaths,
            category = entry.Category
        });
        await File.WriteAllTextAsync(benchmark.Path, JsonSerializer.Serialize(benchmarkJson));
        try
        {
            using var indexOutput = new StringWriter();
            using var indexError = new StringWriter();
            var indexCode = await Knowledge.Indexer.KnowledgeCli.RunAsync(["index", "--config", config.Path], indexOutput, indexError);
            Assert.True(indexCode == 0, $"Index CLI failed with exit code {indexCode}: {indexError}");

            var statusStore = new QdrantKnowledgeIndexStore(client, collection);
            var statusBefore = await statusStore.GetStatusAsync("integration");
            var metadataBefore = await statusStore.GetIndexedChunksAsync("integration");
            Assert.Equal(1, statusBefore.IndexedDocuments);
            Assert.NotEmpty(metadataBefore);

            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await Knowledge.Indexer.KnowledgeCli.RunAsync([
                "evaluate", "--benchmark", benchmark.Path, "--output", report.Path, "--config", config.Path
            ], output, error);

            Assert.True(code == 0, $"Evaluate CLI failed with exit code {code}: {error}");
            Assert.DoesNotContain(querySentinel, output.ToString() + error.ToString());
            Assert.DoesNotContain("PRIVATE_NOTE_SENTINEL", output.ToString() + error.ToString());
            using (var reportJson = JsonDocument.Parse(await File.ReadAllTextAsync(report.Path)))
            {
                var reportRoot = reportJson.RootElement;
                Assert.Equal(20, reportRoot.GetProperty("queryCount").GetInt32());
                Assert.Equal(5, reportRoot.GetProperty("k").GetInt32());
                Assert.Equal("dense", reportRoot.GetProperty("retrievalMode").GetString());
                Assert.Equal("Architecture.md",
                    reportRoot.GetProperty("results")[0].GetProperty("results")[0].GetProperty("path").GetString());
                Assert.DoesNotContain(querySentinel, reportRoot.ToString());
                Assert.DoesNotContain("PRIVATE_NOTE_SENTINEL", reportRoot.ToString());
            }

            Assert.Equal(statusBefore, await statusStore.GetStatusAsync("integration"));
            Assert.Equal(metadataBefore, await statusStore.GetIndexedChunksAsync("integration"));

            var previousReport = await File.ReadAllBytesAsync(report.Path);
            using var invalidBenchmark = new TemporaryFile(".json");
            await File.WriteAllTextAsync(invalidBenchmark.Path, "[invalid");
            using var failureOutput = new StringWriter();
            using var failureError = new StringWriter();
            var failureCode = await Knowledge.Indexer.KnowledgeCli.RunAsync([
                "evaluate", "--benchmark", invalidBenchmark.Path, "--output", report.Path, "--config", config.Path
            ], failureOutput, failureError);
            Assert.Equal(2, failureCode);
            Assert.Equal(previousReport, await File.ReadAllBytesAsync(report.Path));
            Assert.Equal(statusBefore, await statusStore.GetStatusAsync("integration"));
            Assert.Equal(metadataBefore, await statusStore.GetIndexedChunksAsync("integration"));
        }
        finally
        {
            if (await client.CollectionExistsAsync(collection)) await client.DeleteCollectionAsync(collection);
        }
    }

    [Fact]
    public async Task Qdrant_status_is_read_only_vault_scoped_paginated_and_fails_closed_on_bad_identity()
    {
        using var client = new QdrantClient("localhost", 6334, grpcTimeout: TimeSpan.FromSeconds(10));
        try { await client.HealthAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XunitException("Start local Qdrant with docker compose -f docker/docker-compose.yml up -d before running the indexing integration suite.");
        }

        var collection = "knowledge-status-test-" + Guid.NewGuid().ToString("N")[..12];
        var store = new QdrantKnowledgeIndexStore(client, collection);
        try
        {
            Assert.Equal(new KnowledgeIndexStatus(false, 0, 0), await store.GetStatusAsync("vault-a"));
            await store.EnsureCollectionAsync();
            Assert.Equal(new KnowledgeIndexStatus(true, 0, 0), await store.GetStatusAsync("vault-a"));

            var vector = new float[KnowledgeIndexSchema.EmbeddingDimensions];
            vector[0] = 1;
            var points = Enumerable.Range(0, 260).Select(index =>
            {
                var point = new global::Qdrant.Client.Grpc.PointStruct { Id = Guid.NewGuid(), Vectors = vector };
                point.Payload["vaultId"] = "vault-a";
                point.Payload["documentId"] = $"document-{index / 2}";
                return point;
            }).ToArray();
            var foreign = new global::Qdrant.Client.Grpc.PointStruct { Id = Guid.NewGuid(), Vectors = vector };
            foreign.Payload["vaultId"] = "vault-b";
            foreign.Payload["documentId"] = "foreign-document";
            await client.UpsertAsync(collection, points, wait: true);
            await client.UpsertAsync(collection, [foreign], wait: true);

            var populated = await store.GetStatusAsync("vault-a");
            Assert.Equal(130, populated.IndexedDocuments);
            Assert.Equal(260, populated.IndexedChunks);
            Assert.Equal(new KnowledgeIndexStatus(true, 1, 1), await store.GetStatusAsync("vault-b"));

            var malformed = new global::Qdrant.Client.Grpc.PointStruct { Id = Guid.NewGuid(), Vectors = vector };
            malformed.Payload["vaultId"] = "vault-a";
            await client.UpsertAsync(collection, [malformed], wait: true);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetStatusAsync("vault-a"));

            await client.DeleteCollectionAsync(collection);
            await client.CreateCollectionAsync(collection,
                new global::Qdrant.Client.Grpc.VectorParams { Size = 2, Distance = global::Qdrant.Client.Grpc.Distance.Cosine });
            await store.RecreateCollectionAsync();
            Assert.True(global::Knowledge.Qdrant.QdrantCollectionLayout.IsCompatible(await client.GetCollectionInfoAsync(collection)));
            Assert.Equal(new KnowledgeIndexStatus(true, 0, 0), await store.GetStatusAsync("vault-a"));
        }
        finally
        {
            if (await client.CollectionExistsAsync(collection)) await client.DeleteCollectionAsync(collection);
        }
    }

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
            var status = await store.GetStatusAsync("integration");
            Assert.True(status.CollectionExists);
            Assert.Equal(1, status.IndexedDocuments);
            Assert.Equal(1, status.IndexedChunks);
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
            status = await store.GetStatusAsync("integration");
            Assert.Equal(1, status.IndexedDocuments);
            Assert.Equal(1, status.IndexedChunks);

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

            var evalIngestion = RetrievalBenchmarkSourceValidator.CreateOptions(options);
            var evalIngestor = new MarkdownIngestor(new BertWordPieceTokenizer(vocabulary));
            var benchmark = new RetrievalBenchmark("synthetic-integration", Enumerable.Range(0, 20).Select(index =>
                new RetrievalBenchmarkEntry($"integration-{index}", "How does the indexer save vector points?",
                    ["Architecture.md"], "semantic")).ToArray());
            var evalFiles = RetrievalBenchmarkSourceValidator.ValidateSources(benchmark, evalIngestor, evalIngestion, CancellationToken.None);
            var sourceSnapshot = RetrievalBenchmarkSourceValidator.CreateSnapshotHash(evalIngestor, evalFiles, CancellationToken.None);
            var evalReport = await RetrievalBenchmarkEvaluator.EvaluateAsync(benchmark, retriever, options,
                evalIngestor, evalIngestion, sourceSnapshot, profile, CancellationToken.None,
                new RetrievalBenchmarkEnvironment(QdrantVersion: "local-test"));
            Assert.Equal(20, evalReport.Total);
            Assert.Equal(20, evalReport.Results.Count);
            Assert.Equal("Architecture.md", Assert.Single(evalReport.Results[0].Results).Path);
            Assert.Equal(beforeSearch, await store.GetIndexedChunksAsync("integration"));

            var beforeRebuild = Assert.Single(await store.GetIndexedChunksAsync("integration"));
            var rebuild = await coordinator.RebuildAsync();
            Assert.True(rebuild.IsComplete, string.Join("; ", rebuild.Diagnostics.Select(diagnostic => diagnostic.Code)));
            Assert.Equal(1, rebuild.Indexed);
            Assert.Equal(1, rebuild.EmbeddingCalls);
            var afterRebuild = Assert.Single(await store.GetIndexedChunksAsync("integration"));
            Assert.Equal(beforeRebuild with { }, afterRebuild);
            Assert.Empty(await client.RetrieveAsync(collection, otherVaultPointId, withPayload: true, withVectors: false));
            var rebuiltSearch = Assert.Single(await retriever.SearchAsync(new KnowledgeSearchRequest("How does the indexer store vector points?")));
            Assert.Equal(result.DocumentId, rebuiltSearch.DocumentId);
            Assert.Equal(result.ChunkId, rebuiltSearch.ChunkId);

            await client.DeleteCollectionAsync(collection);
            var externallyDeletedRebuild = await coordinator.RebuildAsync();
            Assert.True(externallyDeletedRebuild.IsComplete);
            Assert.Equal(1, externallyDeletedRebuild.Indexed);
            Assert.Equal(1, externallyDeletedRebuild.EmbeddingCalls);
            Assert.True((await store.GetStatusAsync("integration")).CollectionExists);
            Assert.Single(await retriever.SearchAsync(new KnowledgeSearchRequest("How does the indexer store vector points?")));

            var noOp = await coordinator.IndexAsync();
            Assert.True(noOp.IsComplete);
            Assert.Equal(1, noOp.Unchanged);
            Assert.Equal(0, noOp.EmbeddingCalls);

            File.Delete(Path.Combine(vault.Path, "Architecture.md"));
            var afterDelete = await coordinator.IndexAsync();
            Assert.True(afterDelete.IsComplete);
            Assert.Equal(1, afterDelete.StaleDeleted);
            Assert.Empty(await store.GetIndexedChunksAsync("integration"));
            Assert.Empty(await client.RetrieveAsync(collection, otherVaultPointId, withPayload: true, withVectors: false));
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

    private sealed class TemporaryFile(string extension) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"knowledge-evaluate-{Guid.NewGuid():N}{extension}");
        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
