using Knowledge.Core;
using Knowledge.Embeddings;
using Knowledge.Qdrant;
using Qdrant.Client;

namespace Knowledge.Indexer.Tests;

public sealed class QdrantSearchTests
{
    [Fact]
    public void Builds_vault_path_and_exact_tag_filters_as_an_and_query()
    {
        var filter = QdrantKnowledgeRetriever.CreateFilter(
            new KnowledgeSearchRequest("question", 5, "./Notes\\Architecture//", [" engineering ", "engineering", "Local"]),
            "primary");

        Assert.Equal(4, filter.Must.Count);
        Assert.Equal("vaultId", filter.Must[0].Field.Key);
        Assert.Equal("primary", filter.Must[0].Field.Match.Keyword);
        Assert.Equal("path", filter.Must[1].Field.Key);
        Assert.Equal("Notes/Architecture/", filter.Must[1].Field.Match.Prefix);
        Assert.Equal("engineering", filter.Must[2].Field.Match.Keyword);
        Assert.Equal("Local", filter.Must[3].Field.Match.Keyword);
    }

    [Fact]
    public void No_filters_still_requires_the_configured_vault()
    {
        var filter = QdrantKnowledgeRetriever.CreateFilter(new KnowledgeSearchRequest("question"), "primary");
        var condition = Assert.Single(filter.Must);
        Assert.Equal("vaultId", condition.Field.Key);
        Assert.Equal("primary", condition.Field.Match.Keyword);
    }

    [Theory]
    [InlineData("/outside")]
    [InlineData("C:\\outside")]
    [InlineData("\\\\server\\share")]
    [InlineData("Notes/../private")]
    [InlineData(" ")]
    public void Rejects_blank_or_outside_vault_path_prefixes(string prefix)
    {
        Assert.Throws<ArgumentException>(() => QdrantKnowledgeRetriever.CreateFilter(
            new KnowledgeSearchRequest("question", PathPrefix: prefix), "primary"));
    }

    [Fact]
    public void Canonicalizes_a_root_only_prefix_to_no_path_filter()
    {
        var filter = QdrantKnowledgeRetriever.CreateFilter(
            new KnowledgeSearchRequest("question", PathPrefix: "./"), "primary");
        Assert.Single(filter.Must);
    }

    [Fact]
    public async Task Rejects_oversized_formatted_query_before_contacting_qdrant()
    {
        using var client = new QdrantClient("127.0.0.1", 6334);
        var embeddings = new BgeQueryEmbeddingService(new UnusedEmbeddingService(), new FixedTokenCounter(513));
        var retriever = new QdrantKnowledgeRetriever(client, "missing", "primary", "profile", embeddings);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            retriever.SearchAsync(new KnowledgeSearchRequest("query")));
    }

    private sealed class UnusedEmbeddingService : IEmbeddingService
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Embedding must not run for this test.");
    }

    private sealed class FixedTokenCounter(int count) : ITokenCounter
    {
        public int Count(string text, bool includeSpecialTokens = true) => count;
    }
}
