using System.Text.Json;
using Knowledge.Core;
using Knowledge.Indexer;
using Knowledge.Indexer.Evaluation;
using Knowledge.Markdown;

namespace Knowledge.Indexer.Tests;

public sealed class RetrievalBenchmarkTests
{
    [Fact]
    public void Loader_requires_twenty_unique_and_well_formed_entries()
    {
        using var file = new TemporaryJson(BenchmarkJson(19));
        var exception = Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(file.Path));
        Assert.Contains("at least 20", exception.Message);

        using var duplicate = new TemporaryJson(BenchmarkJson(20, duplicateIds: true));
        Assert.Contains("duplicate id", Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(duplicate.Path)).Message);

        using var oversized = new TemporaryJson(BenchmarkJson(51));
        Assert.Contains("no more than 50", Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(oversized.Path)).Message);
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("/outside.md")]
    [InlineData("Notes\\Case.md")]
    [InlineData("Notes//Case.md")]
    [InlineData("Notes/./Case.md")]
    [InlineData("Notes/Case.MD")]
    public void Loader_rejects_noncanonical_paths(string path)
    {
        using var file = new TemporaryJson(BenchmarkJson(20, expectedPath: path));
        Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(file.Path));
    }

    [Fact]
    public async Task Evaluator_scores_only_first_five_chunks_and_reports_aggregates_without_content()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\n\nKnowledge for entry {i}.");
        var entries = Enumerable.Range(0, 20).Select(i => new RetrievalBenchmarkEntry(
            $"query-{i}", $"query text {i}", [$"Note{i}.md"], i < 10 ? "semantic" : "exact-term",
            ["TERM_SENTINEL"])).ToArray();
        var benchmark = new RetrievalBenchmark("hash", entries);
        var options = new Knowledge.Indexer.Configuration.KnowledgeOptions
        {
            Vault = new() { Id = "test-vault", Path = vault.Path },
            Index = new() { Include = ["**/*.md"] }
        };
        var ingestion = RetrievalBenchmarkSourceValidator.CreateOptions(options);
        var ingestor = new MarkdownIngestor();
        var sources = RetrievalBenchmarkSourceValidator.ValidateSources(benchmark, ingestor, ingestion, CancellationToken.None);
        var snapshot = RetrievalBenchmarkSourceValidator.CreateSnapshotHash(ingestor, sources, CancellationToken.None);
        var retriever = new RecordingRetriever(i =>
        [
            Result("Other.md", 0, "TERM_SENTINEL"), Result("Other.md", 1, "TERM_SENTINEL"),
            Result("Other.md", 2, "TERM_SENTINEL"), Result("Other.md", 3, "TERM_SENTINEL"),
            Result(i == 0 ? "Note0.md" : "Other.md", 4, "TERM_SENTINEL"),
            Result($"Note{i}.md", 5, "TERM_SENTINEL")
        ]);

        var report = await RetrievalBenchmarkEvaluator.EvaluateAsync(benchmark, retriever, options, ingestor,
            ingestion, snapshot, "profile", CancellationToken.None);

        Assert.Equal(1, report.Passed);
        Assert.Equal(20, report.Total);
        Assert.Equal(0.05, report.HitAt5);
        Assert.False(report.TargetMet);
        Assert.Equal(5, report.Results[0].FirstMatchingRank);
        Assert.Null(report.Results[1].FirstMatchingRank); // The expected note was at rank 6.
        Assert.Equal(2, report.Categories.Count);
        Assert.All(retriever.Requests, request =>
        {
            Assert.Equal(5, request.Limit);
            Assert.Null(request.PathPrefix);
            Assert.Null(request.Tags);
        });

        using var output = new TemporaryJson("existing report");
        await RetrievalBenchmarkEvaluator.WriteReportAtomicallyAsync(output.Path, report);
        var json = await File.ReadAllTextAsync(output.Path);
        Assert.DoesNotContain("TERM_SENTINEL", json);
    }

    [Fact]
    public async Task Cli_evaluate_uses_limit_five_and_report_omits_query_and_note_content()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\n\nSource content {i}.");
        var vocab = Path.Combine(FindRepositoryRoot(), "models", "bge-small-en-v1.5", "vocab.txt");
        using var configuration = new TemporaryConfiguration(
            $"vault: {{id: primary, path: '{vault.Path}'}}\nindex: {{include: ['**/*.md']}}\nembedding: {{tokenizerPath: '{vocab}'}}\nretrieval: {{defaultLimit: 2}}\n");
        using var benchmarkFile = new TemporaryJson(BenchmarkJson(20));
        using var reportFile = new TemporaryJson("");
        File.Delete(reportFile.Path);
        var retriever = new RecordingRetriever(_ => [Result("Note0.md", 0, "PRIVATE_NOTE_SENTINEL")]);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = await KnowledgeCli.RunAsync(["evaluate", "--benchmark", benchmarkFile.Path, "--output", reportFile.Path,
            "--config", configuration.Path], output, error, retrieverFactory: _ => retriever);

        Assert.Equal(0, code);
        Assert.All(retriever.Requests, request => Assert.Equal(5, request.Limit));
        var report = await File.ReadAllTextAsync(reportFile.Path);
        Assert.DoesNotContain("query text", report);
        Assert.DoesNotContain("PRIVATE_NOTE_SENTINEL", report);
        Assert.DoesNotContain("query text", error.ToString());
        Assert.Contains("Dense Hit@5:", error.ToString());
    }

    [Fact]
    public async Task Cli_rejects_ineligible_ground_truth_before_retrieval()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", i == 0 ? "---\nai-index: false\n---\n# Opted out\nHidden." : $"# Note {i}\nBody.");
        var vocab = Path.Combine(FindRepositoryRoot(), "models", "bge-small-en-v1.5", "vocab.txt");
        using var configuration = new TemporaryConfiguration(
            $"vault: {{id: primary, path: '{vault.Path}'}}\nindex: {{include: ['**/*.md']}}\nembedding: {{tokenizerPath: '{vocab}'}}\n");
        using var benchmarkFile = new TemporaryJson(BenchmarkJson(20));
        using var reportFile = new TemporaryJson("");
        File.Delete(reportFile.Path);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var calls = 0;

        var code = await KnowledgeCli.RunAsync(["evaluate", "--benchmark", benchmarkFile.Path, "--output", reportFile.Path,
            "--config", configuration.Path], output, error, retrieverFactory: _ => { calls++; return new RecordingRetriever(_ => []); });

        Assert.Equal(2, code);
        Assert.Equal(0, calls);
        Assert.False(File.Exists(reportFile.Path));
        Assert.DoesNotContain("Hidden", output.ToString() + error.ToString());
    }

    [Fact]
    public async Task Help_and_invalid_syntax_do_not_require_assets_or_retriever()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await KnowledgeCli.RunAsync(["evaluate", "--help"], output, error));
        Assert.Contains("--benchmark", output.ToString());
        Assert.Equal(2, await KnowledgeCli.RunAsync(["evaluate", "--benchmark", "a", "--benchmark", "b", "--output", "c"], output, error));
    }

    [Theory]
    [InlineData(17, true)]
    [InlineData(16, false)]
    public async Task HitAt5_target_uses_unrounded_ratio(int passing, bool expectedTarget)
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        var options = new Knowledge.Indexer.Configuration.KnowledgeOptions
        {
            Vault = new() { Id = "test", Path = vault.Path }, Index = new() { Include = ["**/*.md"] }
        };
        var benchmark = new RetrievalBenchmark("hash", Enumerable.Range(0, 20).Select(i =>
            new RetrievalBenchmarkEntry($"id-{i}", $"query {i}", [$"Note{i}.md"], "semantic")).ToArray());
        var ingestion = RetrievalBenchmarkSourceValidator.CreateOptions(options);
        var ingestor = new MarkdownIngestor();
        var snapshot = RetrievalBenchmarkSourceValidator.CreateSnapshotHash(ingestor,
            RetrievalBenchmarkSourceValidator.ValidateSources(benchmark, ingestor, ingestion, CancellationToken.None), CancellationToken.None);
        var retriever = new RecordingRetriever(index => [Result(index < passing ? $"Note{index}.md" : "Other.md", 0, "")]);

        var report = await RetrievalBenchmarkEvaluator.EvaluateAsync(benchmark, retriever, options, ingestor,
            ingestion, snapshot, "profile");

        Assert.Equal(passing, report.Passed);
        Assert.Equal(expectedTarget, report.TargetMet);
    }

    [Fact]
    public async Task Cancelled_report_write_preserves_previous_complete_report()
    {
        using var file = new TemporaryJson("previous complete report");
        // Serialization is never reached after cancellation, and the destination remains intact.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RetrievalBenchmarkEvaluator.WriteReportAtomicallyAsync(
            file.Path, null!, cancellation.Token));
        Assert.Equal("previous complete report", await File.ReadAllTextAsync(file.Path));
    }

    private static KnowledgeSearchResult Result(string path, int ordinal, string content) =>
        new("document-" + path, $"chunk-{path}-{ordinal}", path, path, [], content, 1d - ordinal / 10d);

    private static string BenchmarkJson(int count, string expectedPath = "Note0.md", bool duplicateIds = false) =>
        JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => new
        {
            id = duplicateIds ? "same-id" : $"query-{i}", query = $"query text {i}", expectedPaths = new[] { expectedPath }, category = "semantic"
        }));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Knowledge.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class RecordingRetriever(Func<int, IReadOnlyList<KnowledgeSearchResult>> results) : IKnowledgeRetriever
    {
        public List<KnowledgeSearchRequest> Requests { get; } = [];
        public Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(results(Requests.Count - 1));
        }
    }

    private sealed class TemporaryJson : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knowledge-benchmark-{Guid.NewGuid():N}.json");
        public TemporaryJson(string content) => File.WriteAllText(Path, content);
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }

    private sealed class TemporaryVault : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knowledge-vault-{Guid.NewGuid():N}");
        public TemporaryVault() => Directory.CreateDirectory(Path);
        public void Write(string path, string content) => File.WriteAllText(System.IO.Path.Combine(Path, path), content);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
