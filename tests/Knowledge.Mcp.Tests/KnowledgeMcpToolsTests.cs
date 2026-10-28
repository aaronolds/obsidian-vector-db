using System.Reflection;
using System.Text.Json;
using Knowledge.Core;
using Knowledge.Mcp;
using Knowledge.Markdown;
using Knowledge.Runtime.Configuration;
using ModelContextProtocol.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knowledge.Mcp.Tests;

public sealed class KnowledgeMcpToolsTests
{
    [Fact]
    public async Task Search_arguments_fail_as_sanitized_tool_errors_before_loading_assets()
    {
        using var search = new McpSearchService(new KnowledgeOptions());

        var response = await KnowledgeMcpTools.SearchKnowledge("PRIVATE_QUERY_SENTINEL", search,
            CancellationToken.None, limit: 0);

        Assert.True(response.IsError);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(response.Content)).Text;
        Assert.Contains("invalid_request", text);
        Assert.DoesNotContain("PRIVATE_QUERY_SENTINEL", text);
    }

    [Fact]
    public async Task Get_note_returns_structured_and_text_json_and_safe_not_found()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "---\ntitle: Note\n---\nCurrent body.");
        var reader = vault.Reader();

        var found = await KnowledgeMcpTools.GetNote(reader, CancellationToken.None, path: "Note.md");
        Assert.False(found.IsError);
        Assert.NotNull(found.StructuredContent);
        Assert.Equal("true", found.StructuredContent.Value.GetProperty("found").GetRawText());
        var content = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(found.Content)).Text;
        Assert.Contains("Current body.", content);

        var missing = await KnowledgeMcpTools.GetNote(reader, CancellationToken.None, path: "Missing.md");
        Assert.False(missing.IsError);
        Assert.Equal("false", missing.StructuredContent!.Value.GetProperty("found").GetRawText());
        Assert.Equal(JsonValueKind.Null, missing.StructuredContent.Value.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task Invalid_note_request_is_a_sanitized_error()
    {
        using var vault = new TemporaryVault();
        var response = await KnowledgeMcpTools.GetNote(vault.Reader(), CancellationToken.None,
            path: "../PRIVATE_NOTE_SENTINEL.md");

        Assert.True(response.IsError);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(response.Content)).Text;
        Assert.Contains("invalid_request", text);
        Assert.DoesNotContain("PRIVATE_NOTE_SENTINEL", text);
    }

    [Fact]
    public void Only_two_tools_are_advertised_and_both_are_marked_read_only()
    {
        var tools = typeof(KnowledgeMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(item => item.Attribute is not null).ToArray();

        Assert.Equal(2, tools.Length);
        Assert.Equal(new[] { "get_note", "search_knowledge" },
            tools.Select(item => item.Attribute!.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, item =>
        {
            Assert.True(item.Attribute!.ReadOnly);
            Assert.False(item.Attribute.Destructive);
            Assert.True(item.Attribute.UseStructuredContent);
            Assert.NotNull(item.Attribute.OutputSchemaType);
        });
    }

    [Fact]
    public async Task Help_does_not_load_configuration_or_write_protocol_stdout()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = await Program.RunAsync(["--help"], output, error);

        Assert.Equal(0, code);
        Assert.Empty(output.ToString());
        Assert.Contains("knowledge-mcp", error.ToString());
    }

    [Fact]
    public async Task In_process_mcp_protocol_advertises_and_invokes_only_the_two_tools()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nProtocol body.");
        var options = new KnowledgeOptions();
        using var services = new ServiceCollection()
            .AddSingleton<IKnowledgeNoteReader>(vault.Reader())
            .AddSingleton(new McpSearchService(options))
            .BuildServiceProvider();
        var c2s = new Pipe();
        var s2c = new Pipe();
        var createOptions = new McpServerToolCreateOptions { Services = services };
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in typeof(KnowledgeMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(method => McpServerTool.Create(method, new KnowledgeMcpTools(), createOptions)))
            tools.Add(method);
        await using var server = McpServer.Create(
            new StreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream()),
            new McpServerOptions { ScopeRequests = false, ToolCollection = tools },
            serviceProvider: services);
        using var serverCancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(serverCancellation.Token);
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()));

        var discovered = await client.ListToolsAsync();
        Assert.Equal(new[] { "get_note", "search_knowledge" },
            discovered.Select(tool => tool.Name).Order(StringComparer.Ordinal));

        var note = await client.CallToolAsync("get_note", new Dictionary<string, object?> { ["path"] = "Note.md" });
        Assert.False(note.IsError);
        Assert.NotNull(note.StructuredContent);
        Assert.Contains("Protocol body.", note.Content.OfType<TextContentBlock>().Single().Text);

        CallToolResult? wrongTypeResult = null;
        Exception? wrongTypeException = null;
        try
        {
            wrongTypeResult = await client.CallToolAsync("search_knowledge",
                new Dictionary<string, object?> { ["query"] = 123 });
        }
        catch (Exception exception)
        {
            wrongTypeException = exception;
        }
        Assert.Null(wrongTypeException);
        Assert.NotNull(wrongTypeResult);
        Assert.True(wrongTypeResult.IsError);
        Assert.Equal("An error occurred invoking 'search_knowledge'.",
            wrongTypeResult.Content.OfType<TextContentBlock>().Single().Text);

        CallToolResult? unknownToolResult = null;
        Exception? unknownToolException = null;
        try
        {
            unknownToolResult = await client.CallToolAsync("unknown_knowledge_tool",
                new Dictionary<string, object?>());
        }
        catch (Exception exception)
        {
            unknownToolException = exception;
        }
        Assert.True(unknownToolException is not null || unknownToolResult?.IsError == true);

        var noteAfterProtocolErrors = await client.CallToolAsync("get_note",
            new Dictionary<string, object?> { ["path"] = "Note.md" });
        Assert.False(noteAfterProtocolErrors.IsError);

        var invalidSearch = await client.CallToolAsync("search_knowledge", new Dictionary<string, object?>
        {
            ["query"] = "PRIVATE_QUERY_SENTINEL",
            ["limit"] = 0
        });
        Assert.True(invalidSearch.IsError);
        Assert.Contains("invalid_request", invalidSearch.Content.OfType<TextContentBlock>().Single().Text);
        Assert.DoesNotContain("PRIVATE_QUERY_SENTINEL", invalidSearch.Content.OfType<TextContentBlock>().Single().Text);
        serverCancellation.Cancel();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Stdio_executable_starts_without_assets_or_qdrant_for_note_reads()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Local note\nNo Qdrant or model is needed for this read.");
        var configPath = vault.WriteConfigurationWithoutModelAssets();
        var serverPath = typeof(Program).Assembly.Location;

        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [serverPath, "--config", configPath],
            WorkingDirectory = Environment.CurrentDirectory,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            Name = "Obsidian Knowledge MCP unit integration"
        }));

        var tools = await client.ListToolsAsync();
        Assert.Equal(new[] { "get_note", "search_knowledge" },
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        var note = await client.CallToolAsync("get_note", new Dictionary<string, object?> { ["path"] = "Note.md" });
        Assert.False(note.IsError);
        Assert.Contains("No Qdrant or model", note.Content.OfType<TextContentBlock>().Single().Text);

        var search = await client.CallToolAsync("search_knowledge", new Dictionary<string, object?>
        {
            ["query"] = "PRIVATE_QUERY_SENTINEL"
        });
        Assert.True(search.IsError);
        Assert.Contains("assets_unavailable", search.Content.OfType<TextContentBlock>().Single().Text);
        Assert.DoesNotContain("PRIVATE_QUERY_SENTINEL", search.Content.OfType<TextContentBlock>().Single().Text);

        var noteAfterFailure = await client.CallToolAsync("get_note",
            new Dictionary<string, object?> { ["path"] = "Note.md" });
        Assert.False(noteAfterFailure.IsError);
        Assert.Contains("No Qdrant or model", noteAfterFailure.Content.OfType<TextContentBlock>().Single().Text);
    }

    private sealed class TemporaryVault : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knowledge-mcp-tool-{Guid.NewGuid():N}");

        public TemporaryVault() => Directory.CreateDirectory(Path);

        public void Write(string relativePath, string content) => File.WriteAllText(System.IO.Path.Combine(Path, relativePath), content);

        public string WriteConfigurationWithoutModelAssets()
        {
            var configPath = System.IO.Path.Combine(Path, "knowledge.yaml");
            File.WriteAllText(configPath, $"""
                vault:
                  id: test-vault
                  path: '{Path.Replace("'", "''")}'
                index:
                  include: ["**/*.md"]
                  exclude: []
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
                  modelPath: ""
                  tokenizerPath: ""
                qdrant:
                  host: localhost
                  port: 6334
                  collection: test-collection
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

        public IKnowledgeNoteReader Reader() => new MarkdownNoteReader(new MarkdownIngestor(),
            new MarkdownIngestionOptions("test-vault", Path, ["**/*.md"], [], "ai-index", 300, 400, 40));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
