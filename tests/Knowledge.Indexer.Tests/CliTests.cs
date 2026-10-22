using Knowledge.Indexer;

namespace Knowledge.Indexer.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData("index")]
    [InlineData("rebuild")]
    [InlineData("search")]
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
    public async Task Embedding_help_is_available_without_model_assets_or_qdrant()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await KnowledgeCli.RunAsync(["embeddings", "--help"], output, error));
        Assert.Contains("knowledge embeddings", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Help_does_not_require_configuration_or_qdrant()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await KnowledgeCli.RunAsync(["--help"], output, error));
        Assert.Contains("knowledge status", output.ToString());
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
