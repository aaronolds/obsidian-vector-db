using Knowledge.Indexer.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Knowledge.Indexer.Tests;

public sealed class ConfigurationTests
{
    private static string ReferencePath => Path.Combine(AppContext.BaseDirectory, "knowledge.yaml");

    [Fact]
    public void Reference_configuration_binds_all_sections_and_resolves_relative_vault_path()
    {
        using var configuration = KnowledgeConfiguration.Load(ReferencePath);
        using var services = new ServiceCollection().AddKnowledgeOptions(configuration, ReferencePath).BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<KnowledgeOptions>>().Value;

        Assert.Equal("primary", options.Vault.Id);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "docs"), options.Vault.Path);
        Assert.Equal(["**/*.md"], options.Index.Include);
        Assert.Equal([".obsidian/**", ".trash/**", "Templates/**"], options.Index.Exclude);
        Assert.Equal("ai-index", options.Index.FrontmatterOptOutField);
        Assert.Equal(300, options.Chunking.TargetTokens);
        Assert.Equal(400, options.Chunking.SoftMaxTokens);
        Assert.Equal(40, options.Chunking.OverlapTokens);
        Assert.True(options.Chunking.OverlapOnlyWhenSplit);
        Assert.Equal("onnx", options.Embedding.Provider);
        Assert.Equal("BAAI/bge-small-en-v1.5", options.Embedding.Model);
        Assert.Equal(384, options.Embedding.Dimensions);
        Assert.Equal("localhost", options.Qdrant.Host);
        Assert.Equal(6334, options.Qdrant.Port);
        Assert.Equal("obsidian-knowledge", options.Qdrant.Collection);
        Assert.Equal(5, options.Retrieval.DefaultLimit);
        Assert.Equal(50, options.Retrieval.MaxLimit);
        Assert.Equal("dense", options.Retrieval.Mode);
        Assert.Equal("Information", options.Logging.Level);
        Assert.False(options.Logging.LogContent);
        Assert.False(options.Logging.LogQueries);
    }

    [Theory]
    [InlineData("vault:id", "", "vault.id")]
    [InlineData("vault:path", " ", "vault.path")]
    [InlineData("index:include:0", "", "index.include")]
    [InlineData("index:exclude:0", "../outside/**", "index.exclude")]
    [InlineData("index:include:0", "/absolute/**", "index.include")]
    [InlineData("index:frontmatterOptOutField", "", "index.frontmatterOptOutField")]
    [InlineData("chunking:targetTokens", "0", "chunking.targetTokens")]
    [InlineData("chunking:softMaxTokens", "200", "chunking.softMaxTokens")]
    [InlineData("chunking:softMaxTokens", "512", "512-token")]
    [InlineData("chunking:overlapTokens", "-1", "chunking.overlapTokens")]
    [InlineData("chunking:overlapTokens", "300", "chunking.overlapTokens")]
    [InlineData("chunking:overlapOnlyWhenSplit", "false", "chunking.overlapOnlyWhenSplit")]
    [InlineData("embedding:provider", "cloud", "embedding.provider")]
    [InlineData("embedding:model", "other", "embedding.model")]
    [InlineData("embedding:dimensions", "768", "embedding.dimensions")]
    [InlineData("qdrant:host", "", "qdrant.host")]
    [InlineData("qdrant:host", "example.org", "qdrant.host")]
    [InlineData("qdrant:host", "http://localhost", "qdrant.host")]
    [InlineData("qdrant:port", "0", "qdrant.port")]
    [InlineData("qdrant:port", "65536", "qdrant.port")]
    [InlineData("qdrant:collection", "", "qdrant.collection")]
    [InlineData("qdrant:collection", "bad/name", "qdrant.collection")]
    [InlineData("retrieval:defaultLimit", "0", "retrieval limits")]
    [InlineData("retrieval:maxLimit", "4", "retrieval limits")]
    [InlineData("retrieval:mode", "hybrid", "retrieval.mode")]
    [InlineData("logging:level", "Verbose", "logging.level")]
    [InlineData("logging:level", "99", "logging.level")]
    [InlineData("logging:logContent", "true", "logging.logContent")]
    [InlineData("logging:logQueries", "true", "logging.logQueries")]
    public void Invalid_settings_fail_options_validation_before_use(string key, string value, string expectedMessage)
    {
        using var configuration = KnowledgeConfiguration.Load(ReferencePath);
        configuration[key] = value;
        using var services = new ServiceCollection().AddKnowledgeOptions(configuration, ReferencePath).BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<KnowledgeOptions>>().Value);
        Assert.Contains(expectedMessage, error.Message);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Loopback_hosts_are_supported(string host)
    {
        using var configuration = KnowledgeConfiguration.Load(ReferencePath);
        configuration["qdrant:host"] = host;
        using var services = new ServiceCollection().AddKnowledgeOptions(configuration, ReferencePath).BuildServiceProvider();
        Assert.Equal(host, services.GetRequiredService<IOptions<KnowledgeOptions>>().Value.Qdrant.Host);
    }

    [Fact]
    public void Environment_overrides_yaml_for_nested_values_and_array_elements()
    {
        var overrides = new Dictionary<string, string>
        {
            ["KNOWLEDGE_VAULT__ID"] = "test-vault",
            ["KNOWLEDGE_VAULT__PATH"] = Path.GetTempPath(),
            ["KNOWLEDGE_QDRANT__HOST"] = "127.0.0.1",
            ["KNOWLEDGE_QDRANT__PORT"] = "16334",
            ["KNOWLEDGE_INDEX__INCLUDE__0"] = "Notes/**/*.md",
            ["KNOWLEDGE_LOGGING__LEVEL"] = "Warning"
        };
        var previous = overrides.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in overrides) Environment.SetEnvironmentVariable(key, value);
            using var configuration = KnowledgeConfiguration.Load(ReferencePath);
            using var services = new ServiceCollection().AddKnowledgeOptions(configuration, ReferencePath).BuildServiceProvider();
            var options = services.GetRequiredService<IOptions<KnowledgeOptions>>().Value;

            Assert.Equal("test-vault", options.Vault.Id);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()), options.Vault.Path);
            Assert.Equal("127.0.0.1", options.Qdrant.Host);
            Assert.Equal(16334, options.Qdrant.Port);
            Assert.Equal(["Notes/**/*.md"], options.Index.Include);
            Assert.Equal("Warning", options.Logging.Level);
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Theory]
    [InlineData("qdrant:port", "not-a-number")]
    [InlineData("logging:logContent", "not-a-boolean")]
    [InlineData("qdrant:poert", "6334")]
    public void Incompatible_values_and_unknown_keys_fail_binding(string key, string value)
    {
        using var configuration = KnowledgeConfiguration.Load(ReferencePath);
        configuration[key] = value;
        using var services = new ServiceCollection().AddKnowledgeOptions(configuration, ReferencePath).BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IOptions<KnowledgeOptions>>().Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("- not-a-root-mapping")]
    [InlineData("vault: [invalid")]
    [InlineData("vault: {}\n---\nvault: {}")]
    [InlineData("vault: {id: one, ID: two}")]
    public void Invalid_yaml_is_rejected(string yaml)
    {
        using var file = new TemporaryConfiguration(yaml);
        Assert.Throws<KnowledgeConfigurationException>(() => KnowledgeConfiguration.Load(file.Path));
    }

    [Fact]
    public void Validation_aggregates_errors_without_echoing_supplied_values()
    {
        var options = new KnowledgeOptions();
        options.Embedding.Model = "PRIVATE_CONTENT_SENTINEL";
        var result = new KnowledgeOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.True(result.Failures.Count() >= 3);
        Assert.DoesNotContain("PRIVATE_CONTENT_SENTINEL", result.FailureMessage);
    }

    [Fact]
    public void Empty_exclude_list_is_supported()
    {
        using var file = new TemporaryConfiguration("vault: {id: primary, path: .}\nindex: {include: ['**/*.md'], exclude: []}");
        using var configuration = KnowledgeConfiguration.Load(file.Path);
        using var services = new ServiceCollection().AddKnowledgeOptions(configuration, file.Path).BuildServiceProvider();
        Assert.Empty(services.GetRequiredService<IOptions<KnowledgeOptions>>().Value.Index.Exclude);
    }

    [Fact]
    public void Empty_include_list_fails_clearly()
    {
        using var file = new TemporaryConfiguration("vault: {id: primary, path: .}\nindex: {include: []}");
        using var configuration = KnowledgeConfiguration.Load(file.Path);
        using var services = new ServiceCollection().AddKnowledgeOptions(configuration, file.Path).BuildServiceProvider();
        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<KnowledgeOptions>>().Value);
        Assert.Contains("index.include", error.Message);
    }

    [Theory]
    [InlineData("vault: unexpected-scalar")]
    [InlineData("qdrant: []")]
    [InlineData("unknownSection: {}")]
    [InlineData("index: {include: '**/*.md'}")]
    [InlineData("vault: {id: {nested: invalid}}")]
    public void Incorrect_yaml_shapes_are_rejected(string yaml)
    {
        using var file = new TemporaryConfiguration(yaml);
        Assert.Throws<KnowledgeConfigurationException>(() => KnowledgeConfiguration.Load(file.Path));
    }
}

internal sealed class TemporaryConfiguration : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knowledge-test-{Guid.NewGuid():N}.yaml");
    public TemporaryConfiguration(string yaml) => File.WriteAllText(Path, yaml);
    public void Dispose() => File.Delete(Path);
}
