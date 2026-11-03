using System.Text.Json;
using Knowledge.Core;
using Knowledge.Embeddings;
using Knowledge.Indexer;
using Knowledge.Indexer.Indexing;
using Knowledge.Markdown;
using Knowledge.Qdrant;
using Knowledge.Runtime.Configuration;
using Knowledge.Runtime.Indexing;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Knowledge.Indexing.IntegrationTests;

public sealed partial class LocalIndexingTests
{
    [Theory]
    [InlineData(80, 120)]
    [InlineData(300, 400)]
    public async Task Repeated_rebuilds_and_cli_restore_preserve_complete_payloads_vectors_and_sources(int target, int maximum)
    {
        AssertNoEnvironmentOverrides();
        var assets = Path.Combine(FindRepositoryRoot(), "models", "bge-small-en-v1.5");
        var model = Path.Combine(assets, "onnx", "model.onnx");
        var vocabulary = Path.Combine(assets, "vocab.txt");
        using var vault = new IntegrationVault();
        void Write(string path, string text)
        {
            var full = Path.Combine(vault.Path, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            File.SetLastWriteTimeUtc(full, new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc));
        }
        Write("Notes/Storage.md", "---\ntags: [architecture]\naliases: [Durable storage]\n---\n# Storage\n## Durability\nQdrant is rebuildable derived state. Markdown is the authoritative source. See [[Operations|recovery procedures]].");
        Write("Notes/Long.md", "# Research\n## Details\n" + string.Join("\n\n", Enumerable.Range(0, 12)
            .Select(index => $"Observation {index}: " + string.Join(' ', Enumerable.Repeat("Coherent semantic passages retain context across bounded chunks.", 12)))));
        Write("Notes/Structures.md", "# Examples\n## Syntax\nUnicode: café 日本語.\n\n- preserve lists\n- preserve `identifiers`\n\n> quoted context\n\n| Key | Value |\n| --- | --- |\n| local | true |\n\n```yaml\n---\nmode: local\n```\n");
        Write("Empty.md", "---\ntags: []\n---\n");
        Write("Private.md", "---\nai-index: false\n---\nPRIVATE_NOTE_SENTINEL");
        Write("Templates/Excluded.md", "PRIVATE_NOTE_SENTINEL");
        var sourceHashes = SourceHashes(vault.Path);
        var collection = "knowledge-hardening-test-" + Guid.NewGuid().ToString("N")[..12];
        var options = new KnowledgeOptions
        {
            Vault = new VaultOptions { Id = "integration", Path = vault.Path },
            Index = new IndexOptions { Include = ["**/*.md"], Exclude = ["Templates/**"], FrontmatterOptOutField = "ai-index" },
            Chunking = new ChunkingOptions { TargetTokens = target, SoftMaxTokens = maximum, OverlapTokens = 20 },
            Embedding = new EmbeddingOptions { ModelPath = model, TokenizerPath = vocabulary },
            Qdrant = new QdrantOptions { Collection = collection }
        };
        using var config = new TemporaryFile(".yaml");
        await File.WriteAllTextAsync(config.Path, $"""
            vault:
              id: integration
              path: {JsonSerializer.Serialize(vault.Path)}
            index:
              include: ["**/*.md"]
              exclude: ["Templates/**"]
            chunking:
              targetTokens: {target}
              softMaxTokens: {maximum}
              overlapTokens: 20
            embedding:
              modelPath: {JsonSerializer.Serialize(model)}
              tokenizerPath: {JsonSerializer.Serialize(vocabulary)}
            qdrant:
              collection: {collection}
            logging:
              level: Trace
            """);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        using var client = new QdrantClient("localhost", 6334, grpcTimeout: TimeSpan.FromSeconds(10));
        await client.HealthAsync(cancellationToken: token);
        using var embeddings = new CountingEmbeddingService(new OnnxEmbeddingService(model, vocabulary));
        var store = new QdrantKnowledgeIndexStore(client, collection);
        var coordinator = new IndexCoordinator(new MarkdownIngestor(new BertWordPieceTokenizer(vocabulary)), store, embeddings, options);
        Exception? primaryFailure = null;
        try
        {
            var initial = await coordinator.IndexAsync(token);
            Assert.True(initial.IsComplete);
            Assert.Equal(3, initial.Indexed);
            Assert.Equal(1, initial.Empty);
            Assert.Equal(1, initial.OptedOut);
            var baseline = await SnapshotAsync(client, collection, token);
            Assert.True(baseline.Count > 3);
            var calls = embeddings.Calls;
            var noop = await coordinator.IndexAsync(token);
            Assert.Equal(3, noop.Unchanged);
            Assert.Equal(0, noop.EmbeddingCalls);
            Assert.Equal(0, noop.ChunksWritten);
            Assert.Equal(calls, embeddings.Calls);
            AssertState(baseline, await SnapshotAsync(client, collection, token), tolerance: 0);

            using (var cancelled = new CancellationTokenSource())
            using (var benchmark = new TemporaryFile(".json"))
            using (var report = new TemporaryFile(".json"))
            {
                await File.WriteAllTextAsync(benchmark.Path, JsonSerializer.Serialize(Enumerable.Range(0, 20)
                    .Select(index => new { id = $"q-{index}", query = "PRIVATE_QUERY_SENTINEL", category = "semantic", expectedPaths = new[] { "Notes/Storage.md" } })));
                cancelled.Cancel();
                foreach (var command in new[] { "status", "chunks", "embeddings", "index", "rebuild", "search", "evaluate" })
                {
                    string[] args = command switch
                    {
                        "search" => [command, "PRIVATE_QUERY_SENTINEL", "--config", config.Path],
                        "evaluate" => [command, "--benchmark", benchmark.Path, "--output", report.Path, "--config", config.Path],
                        _ => [command, "--config", config.Path]
                    };
                    using var cancelledOutput = new StringWriter();
                    using var cancelledError = new StringWriter();
                    Assert.Equal(130, await KnowledgeCli.RunAsync(args, cancelledOutput, cancelledError, cancelled.Token));
                    Assert.DoesNotContain("PRIVATE_", cancelledOutput.ToString() + cancelledError.ToString());
                    AssertState(baseline, await SnapshotAsync(client, collection, token), tolerance: 0);
                }
            }

            // Bad pinned assets must fail before index/rebuild can mutate an existing collection.
            using (var badModel = new TemporaryFile(".onnx"))
            using (var badConfig = new TemporaryFile(".yaml"))
            {
                await File.WriteAllTextAsync(badModel.Path, "PRIVATE_ASSET_SENTINEL");
                await File.WriteAllTextAsync(badConfig.Path, (await File.ReadAllTextAsync(config.Path))
                    .Replace(JsonSerializer.Serialize(model), JsonSerializer.Serialize(badModel.Path), StringComparison.Ordinal));
                foreach (var command in new[] { "index", "rebuild" })
                {
                    using var badOutput = new StringWriter();
                    using var badError = new StringWriter();
                    Assert.Equal(2, await KnowledgeCli.RunAsync([command, "--config", badConfig.Path], badOutput, badError, token));
                    Assert.DoesNotContain("PRIVATE_", badOutput.ToString() + badError.ToString());
                    AssertState(baseline, await SnapshotAsync(client, collection, token), tolerance: 0);
                }
            }

            var settings = new MarkdownIngestionOptions("integration", vault.Path, options.Index.Include,
                options.Index.Exclude, "ai-index", target, maximum, 20);
            var retriever = new QdrantKnowledgeRetriever(client, collection, "integration",
                IndexProfile.Create(settings, options.Embedding),
                new BgeQueryEmbeddingService(embeddings, new BertWordPieceTokenizer(vocabulary)));
            var request = new KnowledgeSearchRequest("What is the authoritative source when rebuilding storage?",
                Limit: 5, PathPrefix: "Notes/Storage.md", Tags: ["architecture"]);
            var originalSearch = Assert.Single(await retriever.SearchAsync(request, token));
            Assert.Equal("Notes/Storage.md", originalSearch.Path);
            Assert.Equal("Storage", originalSearch.Title);
            Assert.Equal(new[] { "Storage", "Durability" }, originalSearch.HeadingPath);
            Assert.Contains("[[Operations|recovery procedures]]", originalSearch.ContentMarkdown);

            for (var iteration = 0; iteration < 2; iteration++)
            {
                var rebuild = await coordinator.RebuildAsync(token);
                Assert.True(rebuild.IsComplete);
                Assert.Equal(3, rebuild.Indexed);
                Assert.Equal(baseline.Count, rebuild.EmbeddingCalls);
                AssertState(baseline, await SnapshotAsync(client, collection, token));
            }
            await client.DeleteCollectionAsync(collection, cancellationToken: token);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, await KnowledgeCli.RunAsync(["rebuild", "--config", config.Path], output, error, token));
            Assert.DoesNotContain("PRIVATE_NOTE_SENTINEL", output.ToString() + error.ToString());
            AssertState(baseline, await SnapshotAsync(client, collection, token));
            var restored = Assert.Single(await retriever.SearchAsync(request, token));
            Assert.Equal(JsonSerializer.Serialize(originalSearch with { Score = restored.Score }), JsonSerializer.Serialize(restored));
            Assert.InRange(Math.Abs(originalSearch.Score - restored.Score), 0, 1e-5);
            Assert.Equal(sourceHashes, SourceHashes(vault.Path));

            // A failed preflight retains every point, even when discovery succeeds.
            File.CreateSymbolicLink(Path.Combine(vault.Path, "Broken.md"), Path.Combine(vault.Path, "missing.md"));
            Assert.False((await coordinator.RebuildAsync(token)).IsComplete);
            AssertState(baseline, await SnapshotAsync(client, collection, token), tolerance: 0);
            File.Delete(Path.Combine(vault.Path, "Broken.md"));

            Write("Notes/Storage.md", "# Storage\nChanged durability policy.");
            var changed = await coordinator.IndexAsync(token);
            Assert.Equal(1, changed.Indexed);
            Assert.Equal(2, changed.Unchanged);
            var afterChange = await SnapshotAsync(client, collection, token);
            AssertState(baseline.Where(point => point.Payload["path"].StringValue != "Notes/Storage.md").ToArray(),
                afterChange.Where(point => point.Payload["path"].StringValue != "Notes/Storage.md").ToArray(), tolerance: 0);

            File.Move(Path.Combine(vault.Path, "Notes/Storage.md"), Path.Combine(vault.Path, "Notes/Renamed.md"));
            var renamed = await coordinator.IndexAsync(token);
            Assert.Equal(1, renamed.Indexed);
            Assert.Equal(1, renamed.StaleDeleted);
            Assert.Empty(await retriever.SearchAsync(request, token));
            File.Delete(Path.Combine(vault.Path, "Notes/Long.md"));
            Write("Notes/Structures.md", "---\nai-index: false\n---\nNow opted out.");
            Directory.CreateDirectory(Path.Combine(vault.Path, "Templates"));
            File.Move(Path.Combine(vault.Path, "Notes/Renamed.md"), Path.Combine(vault.Path, "Templates/Renamed.md"));
            var removed = await coordinator.IndexAsync(token);
            Assert.True(removed.IsComplete);
            Assert.Equal(2, removed.StaleDeleted);
            Assert.Empty(await SnapshotAsync(client, collection, token));
            Assert.Empty(await retriever.SearchAsync(new KnowledgeSearchRequest("storage"), token));
            var converged = await coordinator.IndexAsync(token);
            Assert.True(converged.IsComplete);
            Assert.Equal(0, converged.EmbeddingCalls);
            Assert.Equal(0, converged.ChunksWritten);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            // Never cancel cleanup with the acceptance timeout; verify only our collection.
            try
            {
                if (await client.CollectionExistsAsync(collection)) await client.DeleteCollectionAsync(collection);
                Assert.False(await client.CollectionExistsAsync(collection));
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            {
                primaryFailure.Data["CleanupFailure"] = cleanupFailure.GetType().Name;
            }
        }
    }

    private static void AssertNoEnvironmentOverrides()
    {
        Assert.False(Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Any(name => name.StartsWith("KNOWLEDGE_", StringComparison.OrdinalIgnoreCase)),
            "Unset KNOWLEDGE_ overrides before running the indexing integration suite.");
    }

    private static string[] SourceHashes(string root) => Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" +
            KnowledgeIdentity.FileHash(File.ReadAllBytes(path))).ToArray();

    private static async Task<IReadOnlyList<RetrievedPoint>> SnapshotAsync(QdrantClient client, string collection, CancellationToken token)
    {
        var points = new List<RetrievedPoint>();
        PointId? offset = null;
        do
        {
            var page = await client.ScrollAsync(collection, limit: 16, offset: offset,
                payloadSelector: true, vectorsSelector: true, cancellationToken: token);
            points.AddRange(page.Result);
            offset = page.NextPageOffset;
        } while (offset is not null);
        return points.OrderBy(point => point.Id.Uuid, StringComparer.Ordinal).ToArray();
    }

    private static void AssertState(IReadOnlyList<RetrievedPoint> expected, IReadOnlyList<RetrievedPoint> actual, double tolerance = 1e-5)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Id, actual[index].Id);
            // Protobuf map equality compares semantic values independently of map ordering.
            Assert.True(expected[index].Payload.Equals(actual[index].Payload), "Complete point payload differs.");
            var expectedVector = expected[index].Vectors.Vector.GetDenseVector();
            var actualVector = actual[index].Vectors.Vector.GetDenseVector();
            Assert.NotNull(expectedVector);
            Assert.NotNull(actualVector);
            var left = expectedVector.Data;
            var right = actualVector.Data;
            Assert.Equal(384, left.Count);
            Assert.Equal(384, right.Count);
            for (var component = 0; component < left.Count; component++)
            {
                Assert.True(float.IsFinite(left[component]) && float.IsFinite(right[component]));
                Assert.InRange(Math.Abs(left[component] - right[component]), 0, tolerance);
            }
        }
    }
}
