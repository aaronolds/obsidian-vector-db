using Knowledge.Markdown;
using Knowledge.Runtime;
using Knowledge.Runtime.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Knowledge.Mcp;

public static class Program
{
    private const string Usage = "Usage: knowledge-mcp [--config <path>] | --help";

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        return await RunAsync(args, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        string configPath;
        try
        {
            if (args is ["--help"] or ["-h"])
            {
                await error.WriteLineAsync(Usage).ConfigureAwait(false);
                return 0;
            }
            configPath = ParseConfigPath(args);
        }
        catch (ArgumentException)
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            Microsoft.Extensions.Configuration.ConfigurationManager configuration;
            try { configuration = KnowledgeConfiguration.Load(configPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync("Configuration file could not be read.").ConfigureAwait(false);
                return 2;
            }
            using (configuration)
            {
            var optionsServices = new ServiceCollection();
            optionsServices.AddKnowledgeOptions(configuration, configPath, validateEmbeddingAssets: false);
            using var optionsProvider = optionsServices.BuildServiceProvider();
            KnowledgeOptions options;
            try { options = optionsProvider.GetRequiredService<IOptions<KnowledgeOptions>>().Value; }
            catch (InvalidOperationException)
            {
                await error.WriteLineAsync("Configuration contains an incompatible value or unknown setting.").ConfigureAwait(false);
                return 2;
            }

            var builder = Host.CreateApplicationBuilder([]);
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<MarkdownIngestor>();
            builder.Services.AddSingleton<Knowledge.Core.IKnowledgeNoteReader>(services =>
                new MarkdownNoteReader(services.GetRequiredService<MarkdownIngestor>(),
                    KnowledgeSearchSession.CreateIngestionOptions(options)));
            builder.Services.AddSingleton<McpSearchService>();
            builder.Services.AddMcpServer()
                .WithStdioServerTransport()
                .WithTools<KnowledgeMcpTools>();

            using var host = builder.Build();
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
            }
        }
        catch (OptionsValidationException)
        {
            await error.WriteLineAsync("Configuration is invalid; check knowledge.yaml and local-only Qdrant/model settings.").ConfigureAwait(false);
            return 2;
        }
        catch (KnowledgeConfigurationException ex)
        {
            await error.WriteLineAsync($"Configuration error: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("MCP server could not start or stopped unexpectedly.").ConfigureAwait(false);
            return 1;
        }
    }

    private static string ParseConfigPath(string[] args) => args switch
    {
        [] => "knowledge.yaml",
        ["--config", var path] when !string.IsNullOrWhiteSpace(path) => path,
        _ => throw new ArgumentException("Invalid server arguments.")
    };
}
