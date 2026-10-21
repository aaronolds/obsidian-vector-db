using Knowledge.Indexer.Configuration;
using Knowledge.Qdrant;
using Knowledge.Markdown;
using Knowledge.Embeddings;
using Knowledge.Core;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knowledge.Indexer;

public static class KnowledgeCli
{
    private const string Usage = "Usage: knowledge status [--config <path>] | knowledge chunks [--config <path>]";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        if (args is ["--help"] or ["-h"] or ["status", "--help"] or ["chunks", "--help"])
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }
        var command = args.FirstOrDefault();
        if (command == "chunks")
            return await RunChunksAsync(args[1..], output, error, cancellationToken);

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
                : new BertWordPieceTokenCounter(options.Embedding.TokenizerPath);
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
}
