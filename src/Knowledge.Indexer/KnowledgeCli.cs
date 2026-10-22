using Knowledge.Indexer.Configuration;
using Knowledge.Qdrant;
using Knowledge.Markdown;
using Knowledge.Embeddings;
using Knowledge.Core;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Grpc.Core;
using Qdrant.Client;
using Knowledge.Indexer.Indexing;

namespace Knowledge.Indexer;

public static class KnowledgeCli
{
    private const string Usage = "Usage: knowledge status [--config <path>] | knowledge chunks [--config <path>] | knowledge embeddings [--config <path>] | knowledge index [--config <path>]";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        if (args is ["--help"] or ["-h"] or ["status", "--help"] or ["chunks", "--help"] or ["embeddings", "--help"] or ["index", "--help"])
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }
        var command = args.FirstOrDefault();
        if (command == "chunks")
            return await RunChunksAsync(args[1..], output, error, cancellationToken);
        if (command == "embeddings")
            return await RunEmbeddingsAsync(args[1..], output, error, cancellationToken);
        if (command == "index")
            return await RunIndexAsync(args[1..], output, error, cancellationToken);

        var configPath = args switch
        {
            ["status"] => "knowledge.yaml",
            ["status", "--config", var path] when !string.IsNullOrWhiteSpace(path) => path,
            _ => null
        };
        if (configPath is null)
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        try
        {
            using var configuration = KnowledgeConfiguration.Load(configPath);
            var services = new ServiceCollection();
            services.AddKnowledgeOptions(configuration, configPath);
            using var provider = services.BuildServiceProvider();
            // Force options validation before any network I/O.
            var options = provider.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
            using var loggerFactory = LoggerFactory.Create(logging => logging
                .SetMinimumLevel(Enum.Parse<LogLevel>(options.Logging.Level, true))
                .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace)
                .AddJsonConsole(console =>
                {
                    console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
                    console.UseUtcTimestamp = true;
                }));
            var logger = loggerFactory.CreateLogger("Knowledge.Indexer.Status");
            using var probe = new QdrantHealthCheck(options.Qdrant.Host, options.Qdrant.Port);
            var health = await probe.CheckAsync(cancellationToken);
            var vaultExists = Directory.Exists(options.Vault.Path);
            var endpoint = new UriBuilder("http", options.Qdrant.Host, options.Qdrant.Port).Uri.GetLeftPart(UriPartial.Authority);
            var version = typeof(KnowledgeCli).Assembly.GetName().Version?.ToString(3) ?? "unknown";

            await output.WriteLineAsync($"Application version: {version}");
            await output.WriteLineAsync($"Vault ID: {options.Vault.Id}");
            await output.WriteLineAsync($"Vault path: {options.Vault.Path}");
            await output.WriteLineAsync($"Vault exists: {vaultExists.ToString().ToLowerInvariant()}");
            await output.WriteLineAsync($"Qdrant endpoint: {endpoint} (gRPC)");
            await output.WriteLineAsync($"Qdrant reachable: {health.IsReachable.ToString().ToLowerInvariant()}");
            await output.WriteLineAsync($"Collection: {options.Qdrant.Collection}");
            await output.WriteLineAsync($"Embedding model: {options.Embedding.Model}");
            await output.WriteLineAsync($"Embedding dimensions: {options.Embedding.Dimensions}");

            logger.LogInformation("Status checked: VaultId={VaultId}, VaultExists={VaultExists}, QdrantReachable={QdrantReachable}",
                options.Vault.Id, vaultExists, health.IsReachable);
            if (!health.IsReachable)
                logger.LogWarning("Qdrant health check failed: Endpoint={Endpoint}, FailureCode={FailureCode}", endpoint, health.FailureCode);
            return vaultExists && health.IsReachable ? 0 : 1;
        }
        catch (OptionsValidationException ex)
        {
            foreach (var failure in ex.Failures) await error.WriteLineAsync($"Configuration error: {failure}");
            return 2;
        }
        catch (KnowledgeConfigurationException ex)
        {
            await error.WriteLineAsync($"Configuration error: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("Configuration error: cannot read the configuration file. Run from the repository root or use --config <path>.");
            return 2;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync("Configuration error: unknown key, incompatible value, or invalid path. Check knowledge.yaml and KNOWLEDGE_ overrides.");
            return 2;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Status cancelled.");
            return 130;
        }
    }

    private static async Task<int> RunIndexAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var configPath = args switch
        {
            [] => "knowledge.yaml",
            ["--config", var path] when !string.IsNullOrWhiteSpace(path) => path,
            _ => null
        };
        if (configPath is null)
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        try
        {
            using var configuration = KnowledgeConfiguration.Load(configPath);
            var services = new ServiceCollection();
            services.AddKnowledgeOptions(configuration, configPath);
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.Embedding.ModelPath) || string.IsNullOrWhiteSpace(options.Embedding.TokenizerPath))
                throw new EmbeddingAssetException("knowledge index requires embedding.modelPath and embedding.tokenizerPath for the pinned local BGE model.");

            using var embeddings = new OnnxEmbeddingService(options.Embedding.ModelPath, options.Embedding.TokenizerPath);
            using var client = new QdrantClient(options.Qdrant.Host, options.Qdrant.Port, grpcTimeout: TimeSpan.FromSeconds(10));
            var store = new QdrantKnowledgeIndexStore(client, options.Qdrant.Collection);
            var coordinator = new IndexCoordinator(new MarkdownIngestor(embeddings), store, embeddings, options);
            var result = await coordinator.IndexAsync(cancellationToken);
            foreach (var diagnostic in result.Diagnostics)
                await error.WriteLineAsync($"Index error [{diagnostic.Code}] {diagnostic.RelativePath}: {diagnostic.Message}");

            await output.WriteLineAsync($"Scanned: {result.Scanned}; indexed: {result.Indexed}; unchanged: {result.Unchanged}; opted out: {result.OptedOut}; empty: {result.Empty}; stale deleted: {result.StaleDeleted}; failed: {result.Failed}");
            await output.WriteLineAsync($"Chunks written: {result.ChunksWritten}; chunks deleted: {result.ChunksDeleted}; embedding calls: {result.EmbeddingCalls}; embedding time: {result.EmbeddingDuration.TotalMilliseconds:F0} ms; Qdrant time: {result.QdrantDuration.TotalMilliseconds:F0} ms");
            return result.IsComplete ? 0 : 1;
        }
        catch (OptionsValidationException ex)
        {
            foreach (var failure in ex.Failures) await error.WriteLineAsync($"Configuration error: {failure}");
            return 2;
        }
        catch (KnowledgeConfigurationException ex)
        {
            await error.WriteLineAsync($"Configuration error: {ex.Message}");
            return 2;
        }
        catch (IndexCollectionException ex)
        {
            await error.WriteLineAsync($"Index compatibility error: {ex.Message}");
            return 2;
        }
        catch (EmbeddingAssetException ex)
        {
            await error.WriteLineAsync($"Embedding asset error: {ex.Message}");
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Indexing cancelled; run index again to repair any incomplete document.");
            return 130;
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync($"Qdrant error [{ex.StatusCode}]: indexing did not complete; run index again to retry.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("Configuration or local embedding assets could not be read.");
            return 2;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Indexing cancelled; run index again to repair any incomplete document.");
            return 130;
        }
    }

    private static async Task<int> RunChunksAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var configPath = args switch
        {
            [] => "knowledge.yaml",
            ["--config", var path] when !string.IsNullOrWhiteSpace(path) => path,
            _ => null
        };
        if (configPath is null)
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }
        try
        {
            using var configuration = KnowledgeConfiguration.Load(configPath);
            var services = new ServiceCollection();
            services.AddKnowledgeOptions(configuration, configPath);
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
            ITokenCounter counter = string.IsNullOrWhiteSpace(options.Embedding.TokenizerPath)
                ? new ApproximateBertTokenCounter()
                : new BertWordPieceTokenizer(options.Embedding.TokenizerPath);
            var ingestion = new MarkdownIngestor(counter);
            var result = ingestion.ReadVault(new MarkdownIngestionOptions(options.Vault.Id, options.Vault.Path,
                options.Index.Include, options.Index.Exclude, options.Index.FrontmatterOptOutField,
                options.Chunking.TargetTokens, options.Chunking.SoftMaxTokens, options.Chunking.OverlapTokens), cancellationToken);
            foreach (var chunk in result.Chunks)
                await output.WriteLineAsync(JsonSerializer.Serialize(chunk, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            foreach (var diagnostic in result.Diagnostics)
                await error.WriteLineAsync($"Ingestion error [{diagnostic.Code}] {diagnostic.RelativePath}: {diagnostic.Message}");
            await error.WriteLineAsync($"Chunks: {result.Chunks.Count}; documents: {result.DocumentsProcessed}; skipped: {result.DocumentsSkipped}; errors: {result.Diagnostics.Count}");
            return result.Diagnostics.Count == 0 ? 0 : 1;
        }
        catch (OptionsValidationException ex)
        {
            foreach (var failure in ex.Failures) await error.WriteLineAsync($"Configuration error: {failure}");
            return 2;
        }
        catch (KnowledgeConfigurationException ex)
        {
            await error.WriteLineAsync($"Configuration error: {ex.Message}");
            return 2;
        }
        catch (InvalidDataException ex)
        {
            await error.WriteLineAsync($"Tokenizer error: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("Configuration error: cannot read the configuration file. Run from the repository root or use --config <path>.");
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Chunking cancelled.");
            return 130;
        }
    }

    private static async Task<int> RunEmbeddingsAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var configPath = args switch
        {
            [] => "knowledge.yaml",
            ["--config", var path] when !string.IsNullOrWhiteSpace(path) => path,
            _ => null
        };
        if (configPath is null)
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        try
        {
            using var configuration = KnowledgeConfiguration.Load(configPath);
            var services = new ServiceCollection();
            services.AddKnowledgeOptions(configuration, configPath);
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.Embedding.ModelPath) || string.IsNullOrWhiteSpace(options.Embedding.TokenizerPath))
                throw new EmbeddingAssetException("embedding.modelPath and embedding.tokenizerPath must point to pinned local BGE assets.");

            using var embeddings = new OnnxEmbeddingService(options.Embedding.ModelPath, options.Embedding.TokenizerPath);
            var ingestion = new MarkdownIngestor(embeddings);
            var result = ingestion.ReadVault(new MarkdownIngestionOptions(options.Vault.Id, options.Vault.Path,
                options.Index.Include, options.Index.Exclude, options.Index.FrontmatterOptOutField,
                options.Chunking.TargetTokens, options.Chunking.SoftMaxTokens, options.Chunking.OverlapTokens), cancellationToken);

            var failures = result.Diagnostics.Count;
            var emitted = 0;
            foreach (var diagnostic in result.Diagnostics)
                await error.WriteLineAsync($"Ingestion error [{diagnostic.Code}] {diagnostic.RelativePath}: {diagnostic.Message}");

            foreach (var document in result.Chunks.GroupBy(chunk => chunk.DocumentId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pending = new List<(KnowledgeChunk Chunk, EmbeddingResult Result, int TokenCount)>();
                try
                {
                    foreach (var chunk in document.OrderBy(x => x.ChunkOrdinal))
                    {
                        var text = EmbeddingTextFormatter.Format(chunk.Title, chunk.HeadingPath, chunk.Tags, chunk.ContentNormalized);
                        pending.Add((chunk, await embeddings.EmbedAsync(text, cancellationToken), embeddings.CountTokens(text)));
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OnnxRuntimeException)
                {
                    failures++;
                    var failedPath = document.First().RelativePath;
                    await error.WriteLineAsync($"Embedding error [inference-failed] {failedPath}: The document could not be embedded.");
                    continue;
                }

                foreach (var item in pending)
                {
                    var envelope = new
                    {
                        documentId = item.Chunk.DocumentId,
                        chunkId = item.Chunk.ChunkId,
                        relativePath = item.Chunk.RelativePath,
                        chunkOrdinal = item.Chunk.ChunkOrdinal,
                        tokenCount = item.TokenCount,
                        provider = item.Result.Provider,
                        model = item.Result.Model,
                        dimensions = item.Result.Dimensions,
                        vector = item.Result.Vector.ToArray()
                    };
                    await output.WriteLineAsync(JsonSerializer.Serialize(envelope, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                    emitted++;
                }
            }

            await error.WriteLineAsync($"Embeddings: {emitted}; documents: {result.DocumentsProcessed}; skipped: {result.DocumentsSkipped}; errors: {failures}");
            return failures == 0 ? 0 : 1;
        }
        catch (OptionsValidationException ex)
        {
            foreach (var failure in ex.Failures) await error.WriteLineAsync($"Configuration error: {failure}");
            return 2;
        }
        catch (KnowledgeConfigurationException ex)
        {
            await error.WriteLineAsync($"Configuration error: {ex.Message}");
            return 2;
        }
        catch (EmbeddingAssetException ex)
        {
            await error.WriteLineAsync($"Embedding asset error: {ex.Message}");
            return 2;
        }
        catch (InvalidDataException ex)
        {
            await error.WriteLineAsync($"Tokenizer error: {ex.Message}");
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Embedding generation cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("Embedding asset error: a configured local model asset cannot be read.");
            return 2;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Embedding generation cancelled.");
            return 130;
        }
    }
}
