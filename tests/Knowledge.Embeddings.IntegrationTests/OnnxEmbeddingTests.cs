using Knowledge.Embeddings;
using Knowledge.Indexer.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Knowledge.Embeddings.IntegrationTests;

public sealed class OnnxEmbeddingTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string Assets = Path.Combine(Root, "models", "bge-small-en-v1.5");

    [Fact]
    public async Task Real_model_returns_repeatable_normalized_384_dimensional_vector()
    {
        using var service = CreateService();
        const string input = "Title: Synthetic note\nSection: Test\n\nLocal model inference stays on this workstation.";
        var first = await service.EmbedAsync(input);
        var second = await service.EmbedAsync(input);

        Assert.Equal(384, first.Dimensions);
        Assert.Equal("onnx", first.Provider);
        Assert.Equal("BAAI/bge-small-en-v1.5", first.Model);
        Assert.Equal(384, first.Vector.Length);
        Assert.All(first.Vector.ToArray(), x => Assert.True(float.IsFinite(x)));
        Assert.InRange(Math.Abs(Math.Sqrt(first.Vector.ToArray().Sum(x => (double)x * x)) - 1), 0, 1e-5);
        Assert.Equal(first.Vector.Length, second.Vector.Length);
        for (var i = 0; i < first.Vector.Length; i++) Assert.InRange(Math.Abs(first.Vector.Span[i] - second.Vector.Span[i]), 0, 1e-5);
    }

    [Fact]
    public async Task Enforces_the_full_512_token_window_without_truncation()
    {
        using var service = CreateService();
        var maximum = string.Join(' ', Enumerable.Repeat("a", 510)); // 510 body pieces plus CLS and SEP.
        var result = await service.EmbedAsync(maximum);
        Assert.Equal(384, result.Vector.Length);

        var tooLong = string.Join(' ', Enumerable.Repeat("a", 511));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.EmbedAsync(tooLong));
    }

    [Fact]
    public async Task Cancellation_is_reported_and_the_session_remains_usable()
    {
        using var service = CreateService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EmbedAsync("synthetic", cancellation.Token));
        Assert.Equal(384, (await service.EmbedAsync("synthetic")).Vector.Length);
    }

    [Fact]
    public async Task Embeds_every_sample_vault_chunk_and_respects_its_formatted_token_budget()
    {
        using var service = CreateService();
        var config = Path.Combine(Root, "knowledge.milestone2.yaml");
        using var loaded = Knowledge.Indexer.Configuration.KnowledgeConfiguration.Load(config);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddKnowledgeOptions(loaded, config);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Knowledge.Indexer.Configuration.KnowledgeOptions>>().Value;
        var ingestor = new Knowledge.Markdown.MarkdownIngestor(service);
        var chunks = ingestor.ReadVault(new Knowledge.Markdown.MarkdownIngestionOptions(options.Vault.Id, options.Vault.Path,
            options.Index.Include, options.Index.Exclude, options.Index.FrontmatterOptOutField,
            options.Chunking.TargetTokens, options.Chunking.SoftMaxTokens, options.Chunking.OverlapTokens));

        Assert.Empty(chunks.Diagnostics);
        Assert.Equal(5, chunks.DocumentsProcessed);
        Assert.Equal(1, chunks.DocumentsSkipped);
        Assert.Equal(10, chunks.Chunks.Count);
        foreach (var chunk in chunks.Chunks)
        {
            var text = Knowledge.Core.EmbeddingTextFormatter.Format(chunk.Title, chunk.HeadingPath, chunk.Tags, chunk.ContentNormalized);
            Assert.InRange(service.CountTokens(text), 1, 512);
            Assert.Equal(384, (await service.EmbedAsync(text)).Vector.Length);
        }
        Assert.DoesNotContain(chunks.Chunks, x => x.RelativePath == "opted-out.md");
    }

    [Fact]
    public async Task Matches_the_pinned_transformers_reference_fixture()
    {
        using var service = CreateService();
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "bge-reference.json")));
        var tokenizer = new BertWordPieceTokenizer(Path.Combine(Assets, "vocab.txt"));
        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(3, cases.Length);
        foreach (var testCase in cases)
        {
            var text = testCase.GetProperty("text").GetString()!;
            var expectedIds = testCase.GetProperty("inputIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(expectedIds, tokenizer.Encode(text));

            var expectedVector = testCase.GetProperty("vector").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            var actual = (await service.EmbedAsync(text)).Vector.ToArray();
            Assert.Equal(expectedVector.Length, actual.Length);
            var dot = 0d;
            var expectedNorm = 0d;
            var actualNorm = 0d;
            for (var i = 0; i < actual.Length; i++)
            {
                Assert.InRange(Math.Abs(actual[i] - expectedVector[i]), 0, 1e-4);
                dot += (double)actual[i] * expectedVector[i];
                expectedNorm += (double)expectedVector[i] * expectedVector[i];
                actualNorm += (double)actual[i] * actual[i];
            }
            Assert.True(dot / Math.Sqrt(expectedNorm * actualNorm) >= 0.99999);
        }
    }

    private static OnnxEmbeddingService CreateService()
    {
        var model = Path.Combine(Assets, "onnx", "model.onnx");
        if (!File.Exists(model)) throw new FileNotFoundException("Run scripts/setup-bge-model.sh to install the pinned model.", model);
        return new OnnxEmbeddingService(model, Path.Combine(Assets, "vocab.txt"));
    }

    private static string FindRepositoryRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "Knowledge.sln"))) return path.FullName;
        throw new DirectoryNotFoundException("Cannot locate repository root for model integration tests.");
    }
}
