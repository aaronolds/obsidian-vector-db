using Knowledge.Core;
using Knowledge.Indexer;
using Knowledge.Runtime.Configuration;
using Knowledge.Qdrant;

namespace Knowledge.Indexer.Tests;

public sealed class SearchTests
{
    [Fact]
    public async Task Cli_applies_filters_and_prints_complete_provenance_without_echoing_query()
    {
        using var configuration = new TemporaryConfiguration("vault: {id: primary, path: .}\nindex: {include: ['**/*.md']}\n");
        var retriever = new FakeRetriever
        {
            Results = [new KnowledgeSearchResult("doc", "chunk", "Notes/A.md", "A", ["Overview", "Design"],
                "Original **Markdown**\n\n[[Target]]", 0.81234567)]
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = await KnowledgeCli.RunAsync(
            ["search", "private sentinel", "--tag", "engineering", "--path", "Notes/", "--limit", "2", "--tag", "local", "--config", configuration.Path],
            output, error, retrieverFactory: _ => retriever);

        Assert.Equal(0, code);
        Assert.Equal("private sentinel", retriever.Request?.Query);
        Assert.Equal(2, retriever.Request?.Limit);
        Assert.Equal("Notes/", retriever.Request?.PathPrefix);
        Assert.Equal(["engineering", "local"], retriever.Request?.Tags);
        Assert.Contains("Notes/A.md", output.ToString());
        Assert.Contains("Overview > Design", output.ToString());
        Assert.Contains("0.812346", output.ToString());
        Assert.Contains("Original **Markdown**\n\n[[Target]]", output.ToString());
        Assert.DoesNotContain("private sentinel", output.ToString());
        Assert.DoesNotContain("private sentinel", error.ToString());
    }

    [Fact]
    public async Task Cli_uses_configured_default_limit_and_handles_empty_results()
    {
        using var configuration = new TemporaryConfiguration("vault: {id: primary, path: .}\nindex: {include: ['**/*.md']}\n");
        var retriever = new FakeRetriever();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = await KnowledgeCli.RunAsync(["search", "question", "--config", configuration.Path],
            output, error, retrieverFactory: _ => retriever);

        Assert.Equal(0, code);
        Assert.Equal(5, retriever.Request?.Limit);
        Assert.Contains("No matching notes found.", output.ToString());
        Assert.Contains("0 results", error.ToString());
    }

    [Theory]
    [InlineData("search")]
    [InlineData("search|question|--limit")]
    [InlineData("search|question|--limit|2|--limit|3")]
    [InlineData("search|question|--unknown|x")]
    [InlineData("search|one|two")]
    public async Task Invalid_search_syntax_returns_usage_without_running_retriever(string arguments)
    {
        var args = arguments.Split('|');
        using var output = new StringWriter();
        using var error = new StringWriter();
        var factoryCalls = 0;

        var code = await KnowledgeCli.RunAsync(args, output, error, retrieverFactory: _ =>
        {
            factoryCalls++;
            return new FakeRetriever();
        });

        Assert.Equal(2, code);
        Assert.Contains("Usage:", error.ToString());
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Search_failure_does_not_echo_query_or_exception_text()
    {
        using var configuration = new TemporaryConfiguration("vault: {id: primary, path: .}\nindex: {include: ['**/*.md']}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = await KnowledgeCli.RunAsync(["search", "PRIVATE_QUERY_SENTINEL", "--config", configuration.Path],
            output, error, retrieverFactory: _ => new FakeRetriever { Failure = new InvalidDataException("PRIVATE_QUERY_SENTINEL") });

        Assert.Equal(1, code);
        Assert.DoesNotContain("PRIVATE_QUERY_SENTINEL", error.ToString());
        Assert.DoesNotContain("PRIVATE_QUERY_SENTINEL", output.ToString());
    }

    [Fact]
    public async Task Invalid_path_filter_fails_before_model_or_retriever_composition()
    {
        using var configuration = new TemporaryConfiguration("vault: {id: primary, path: .}\nindex: {include: ['**/*.md']}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var factoryCalls = 0;

        var code = await KnowledgeCli.RunAsync(["search", "question", "--path", "../outside", "--config", configuration.Path],
            output, error, retrieverFactory: _ =>
            {
                factoryCalls++;
                return new FakeRetriever();
            });

        Assert.Equal(2, code);
        Assert.Equal(0, factoryCalls);
        Assert.DoesNotContain("outside", error.ToString());
    }

    private sealed class FakeRetriever : IKnowledgeRetriever
    {
        public KnowledgeSearchRequest? Request { get; private set; }
        public IReadOnlyList<KnowledgeSearchResult> Results { get; init; } = [];
        public Exception? Failure { get; init; }
        public Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Results);
        }
    }
}
