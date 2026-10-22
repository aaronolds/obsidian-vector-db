using Knowledge.Core;
using Knowledge.Embeddings;

namespace Knowledge.Embeddings.Tests;

public sealed class QueryEmbeddingTests
{
    [Fact]
    public async Task Adds_the_query_instruction_once_and_trims_only_outer_whitespace()
    {
        var embeddings = new RecordingEmbeddingService();
        var counter = new RecordingTokenCounter(10);
        var service = new BgeQueryEmbeddingService(embeddings, counter);

        var result = await service.EmbedQueryAsync("  how does OBO work?  ");

        Assert.Equal("Represent this sentence for searching relevant passages: how does OBO work?", embeddings.Text);
        Assert.Equal(embeddings.Text, counter.Text);
        Assert.Equal("onnx", result.Provider);
        Assert.Equal("BAAI/bge-small-en-v1.5", result.Model);
        Assert.Equal(384, result.Dimensions);
    }

    [Fact]
    public async Task Accepts_512_tokens_and_rejects_513_before_embedding()
    {
        var embeddings = new RecordingEmbeddingService();
        var counter = new RecordingTokenCounter(512);
        var service = new BgeQueryEmbeddingService(embeddings, counter);
        await service.EmbedQueryAsync("query");
        Assert.Equal(1, embeddings.Calls);

        counter.TokenCount = 513;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.EmbedQueryAsync("sensitive query"));
        Assert.Equal(1, embeddings.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \t")]
    public async Task Rejects_blank_query_without_embedding(string query)
    {
        var embeddings = new RecordingEmbeddingService();
        var service = new BgeQueryEmbeddingService(embeddings, new RecordingTokenCounter(1));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EmbedQueryAsync(query));
        Assert.Equal(0, embeddings.Calls);
    }

    private sealed class RecordingEmbeddingService : IEmbeddingService
    {
        public int Calls { get; private set; }
        public string? Text { get; private set; }
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls++;
            Text = text;
            return Task.FromResult(new EmbeddingResult(new float[384], "onnx", "BAAI/bge-small-en-v1.5", 384));
        }
    }

    private sealed class RecordingTokenCounter(int tokenCount) : ITokenCounter
    {
        public int TokenCount { get; set; } = tokenCount;
        public string? Text { get; private set; }
        public int Count(string text, bool includeSpecialTokens = true)
        {
            Text = text;
            return TokenCount;
        }
    }
}
