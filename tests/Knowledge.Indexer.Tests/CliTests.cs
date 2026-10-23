using Knowledge.Indexer;

namespace Knowledge.Indexer.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData("watch")]
    public async Task Future_commands_are_not_implemented(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await KnowledgeCli.RunAsync([command], output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public async Task Rebuild_help_explains_that_the_configured_collection_is_recreated()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await KnowledgeCli.RunAsync(["rebuild", "--help"], output, error));
        Assert.Contains("knowledge rebuild", output.ToString());
        Assert.Contains("dedicated collection", output.ToString());
    }

    [Fact]
    public async Task Rebuild_unknown_configuration_key_is_safe_and_returns_usage_error()
    {
        using var file = new TemporaryConfiguration("vault: {id: primary, path: .}\nunknown: PRIVATE_SENTINEL\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(2, await KnowledgeCli.RunAsync(["rebuild", "--config", file.Path], output, error));
        Assert.Contains("Configuration error:", error.ToString());
        Assert.DoesNotContain("PRIVATE_SENTINEL", error.ToString());
        Assert.DoesNotContain(" at ", error.ToString());
    }

    [Fact]
    public async Task Embedding_help_is_available_without_model_assets_or_qdrant()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await KnowledgeCli.RunAsync(["embeddings", "--help"], output, error));
        Assert.Contains("knowledge embeddings", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Index_help_is_available_without_model_assets_or_qdrant()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await KnowledgeCli.RunAsync(["index", "--help"], output, error));
        Assert.Contains("knowledge index", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Search_help_is_available_without_model_assets_or_qdrant()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await KnowledgeCli.RunAsync(["search", "--help"], output, error));
        Assert.Contains("knowledge search <query>", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Help_does_not_require_configuration_or_qdrant()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await KnowledgeCli.RunAsync(["--help"], output, error));
        Assert.Contains("knowledge status", output.ToString());
        Assert.Contains("knowledge search", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Missing_configuration_is_a_clear_error()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await KnowledgeCli.RunAsync(["status", "--config", $"{Guid.NewGuid():N}.yaml"], output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("cannot read the configuration file", error.ToString());
    }

    [Fact]
    public async Task Status_does_not_require_configured_model_assets_and_reports_unavailable_counts_safely()
    {
        using var file = new TemporaryConfiguration("vault: {id: primary, path: .}\nindex: {include: ['**/*.md']}\nembedding: {modelPath: missing-model.onnx, tokenizerPath: missing-vocab.txt}\nqdrant: {host: localhost, port: 1, collection: status-test}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await KnowledgeCli.RunAsync(["status", "--config", file.Path], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Embedding model:", output.ToString());
        Assert.Contains("Collection exists: unavailable", output.ToString());
        Assert.Contains("Indexed chunks: unavailable", output.ToString());
        Assert.DoesNotContain("missing-model.onnx", output.ToString() + error.ToString());
        Assert.DoesNotContain("missing-vocab.txt", output.ToString() + error.ToString());
        Assert.DoesNotContain("Configuration error:", error.ToString());
    }

    [Fact]
    public async Task Malformed_yaml_errors_do_not_echo_source_text()
    {
        using var file = new TemporaryConfiguration("vault: [PRIVATE_CONTENT_SENTINEL");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await KnowledgeCli.RunAsync(["status", "--config", file.Path], output, error));
        Assert.Contains("Invalid YAML", error.ToString());
        Assert.DoesNotContain("PRIVATE_CONTENT_SENTINEL", error.ToString());
    }
}
