using Knowledge.Indexer.Configuration;
using Knowledge.Qdrant;
using Knowledge.Markdown;
using Knowledge.Embeddings;
using Knowledge.Core;
using System.Text.Json;
using System.Globalization;
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
    private const string Usage = "Usage: knowledge status [--config <path>] | knowledge chunks [--config <path>] | knowledge embeddings [--config <path>] | knowledge index [--config <path>] | knowledge search <query> [--limit <n>] [--path <prefix>] [--tag <tag>]... [--config <path>]";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default,
        Func<KnowledgeOptions, IKnowledgeRetriever>? retrieverFactory = null)
    {
        if (args is ["--help"] or ["-h"] or ["status", "--help"] or ["chunks", "--help"] or ["embeddings", "--help"] or ["index", "--help"] or ["search", "--help"])
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
        if (command == "search")
            return await RunSearchAsync(args[1..], output, error, cancellationToken, retrieverFactory);

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

    private static async Task<int> RunSearchAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, Func<KnowledgeOptions, IKnowledgeRetriever>? retrieverFactory)
    {
        SearchArguments parsed;
        try { parsed = SearchArguments.Parse(args); }
        catch (ArgumentException)
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        try
        {
            using var configuration = KnowledgeConfiguration.Load(parsed.ConfigPath ?? "knowledge.yaml");
            var services = new ServiceCollection();
            services.AddKnowledgeOptions(configuration, parsed.ConfigPath ?? "knowledge.yaml");
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
            var limit = parsed.Limit ?? options.Retrieval.DefaultLimit;
            if (limit < 1 || limit > options.Retrieval.MaxLimit)
                throw new ArgumentOutOfRangeException(nameof(parsed.Limit), $"Search limit must be between 1 and {options.Retrieval.MaxLimit}.");
            var request = KnowledgeSearchRequestValidator.Normalize(
                new KnowledgeSearchRequest(parsed.Query, limit, parsed.PathPrefix, parsed.Tags),
                options.Retrieval.MaxLimit);
            cancellationToken.ThrowIfCancellationRequested();

            IKnowledgeRetriever retriever;
            if (retrieverFactory is not null)
            {
                retriever = retrieverFactory(options);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(options.Embedding.ModelPath) || string.IsNullOrWhiteSpace(options.Embedding.TokenizerPath))
                    throw new EmbeddingAssetException("knowledge search requires embedding.modelPath and embedding.tokenizerPath for the pinned local BGE model.");
                var tokenizer = new BertWordPieceTokenizer(options.Embedding.TokenizerPath);
                _ = BgeQueryEmbeddingService.FormatQuery(request.Query, tokenizer);
                var embeddings = new OnnxEmbeddingService(options.Embedding.ModelPath, options.Embedding.TokenizerPath);
                using (embeddings)
                using (var client = new QdrantClient(options.Qdrant.Host, options.Qdrant.Port, grpcTimeout: TimeSpan.FromSeconds(10)))
                {
                    retriever = CreateRetriever(options, client,
                        new BgeQueryEmbeddingService(embeddings, tokenizer));
                    await RenderSearchAsync(retriever, request, output, error, cancellationToken);
                }
                return 0;
            }

            await RenderSearchAsync(retriever, request, output, error, cancellationToken);
            return 0;
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
        catch (KnowledgeSearchCompatibilityException ex)
        {
            await error.WriteLineAsync($"Search compatibility error: {ex.Message}");
            return 2;
        }
        catch (EmbeddingAssetException ex)
        {
            await error.WriteLineAsync($"Embedding asset error: {ex.Message}");
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Search cancelled.");
            return 130;
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync($"Search request error: {ex.Message}");
            return 2;
        }
        catch (InvalidOperationException)
        {
            await error.WriteLineAsync("Search configuration or result data is invalid.");
            return 2;
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync($"Qdrant error [{ex.StatusCode}]: search did not complete.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("Configuration or local embedding assets could not be read.");
            return 2;
        }
        catch (Exception ex) when (ex is InvalidDataException or OnnxRuntimeException)
        {
            await error.WriteLineAsync("Search inference or returned data was invalid.");
            return 1;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Search cancelled.");
            return 130;
        }
    }

    private static IKnowledgeRetriever CreateRetriever(KnowledgeOptions options, QdrantClient client,
        IQueryEmbeddingService queryEmbeddings)
    {
        var ingestion = new MarkdownIngestionOptions(options.Vault.Id, options.Vault.Path,
            options.Index.Include, options.Index.Exclude, options.Index.FrontmatterOptOutField,
            options.Chunking.TargetTokens, options.Chunking.SoftMaxTokens, options.Chunking.OverlapTokens);
        var profile = IndexProfile.Create(ingestion, options.Embedding);
        return new QdrantKnowledgeRetriever(client, options.Qdrant.Collection, options.Vault.Id,
            profile, queryEmbeddings, options.Retrieval.MaxLimit);
    }

    private static async Task RenderSearchAsync(IKnowledgeRetriever retriever, KnowledgeSearchRequest request,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var results = await retriever.SearchAsync(request, cancellationToken);
        if (results.Count > request.Limit || results.Any(result =>
                string.IsNullOrWhiteSpace(result.DocumentId) || string.IsNullOrWhiteSpace(result.ChunkId) ||
                string.IsNullOrWhiteSpace(result.Path) || string.IsNullOrWhiteSpace(result.Title) ||
                result.HeadingPath is null || result.ContentMarkdown is null || !double.IsFinite(result.Score)))
            throw new InvalidDataException("Retriever returned malformed or excessive search results.");
        if (results.Count == 0)
        {
            await output.WriteLineAsync("No matching notes found.");
            await error.WriteLineAsync("Search completed: 0 results.");
            return;
        }

        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            await output.WriteLineAsync($"{index + 1}. {result.Path} — {result.Title}");
            await output.WriteLineAsync($"   Section: {(result.HeadingPath.Count == 0 ? "(note)" : string.Join(" > ", result.HeadingPath))}");
            await output.WriteLineAsync($"   Score: {result.Score.ToString("F6", CultureInfo.InvariantCulture)}");
            await output.WriteLineAsync($"   Document ID: {result.DocumentId}");
            await output.WriteLineAsync($"   Chunk ID: {result.ChunkId}");
            await output.WriteLineAsync("   Content:");
            await output.WriteLineAsync(result.ContentMarkdown);
        }
        await error.WriteLineAsync($"Search completed: {results.Count} result(s).");
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

    private sealed record SearchArguments(string Query, int? Limit, string? PathPrefix,
        IReadOnlyList<string> Tags, string? ConfigPath)
    {
        public static SearchArguments Parse(string[] args)
        {
            string? query = null;
            int? limit = null;
            string? path = null;
            string? config = null;
            var tags = new List<string>();
            var seenLimit = false;
            var seenPath = false;
            var seenConfig = false;

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument)
                {
                    case "--limit":
                        if (seenLimit) throw new ArgumentException("Duplicate --limit.");
                        seenLimit = true;
                        limit = int.TryParse(ReadValue(args, ref index), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit)
                            ? parsedLimit : throw new ArgumentException("Invalid --limit.");
                        break;
                    case "--path":
                        if (seenPath) throw new ArgumentException("Duplicate --path.");
                        seenPath = true;
                        path = ReadValue(args, ref index);
                        break;
                    case "--tag":
                        tags.Add(ReadValue(args, ref index));
                        break;
                    case "--config":
                        if (seenConfig) throw new ArgumentException("Duplicate --config.");
                        seenConfig = true;
                        config = ReadValue(args, ref index);
                        break;
                    default:
                        if (argument.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException("Unknown search option.");
                        if (query is not null) throw new ArgumentException("Search accepts one query.");
                        query = argument;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Search query is required.");
            return new SearchArguments(query, limit, path, tags, config);
        }

        private static string ReadValue(string[] args, ref int index)
        {
            if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException("Search option requires a value.");
            return args[++index];
        }
    }
}
