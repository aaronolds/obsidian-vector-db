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
    public void Loader_rejects_noncanonical_paths(string path)
    {
        using var file = new TemporaryJson(BenchmarkJson(20, expectedPath: path));
        Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(file.Path));
    }

    [Fact]
    public void Loader_accepts_uppercase_markdown_extension()
    {
        using var file = new TemporaryJson(BenchmarkJson(20, expectedPath: "Notes/Case.MD"));

        var benchmark = RetrievalBenchmarkLoader.Load(file.Path);

        Assert.All(benchmark.Entries, entry => Assert.Equal("Notes/Case.MD", Assert.Single(entry.ExpectedPaths)));
    }

    [Fact]
    public void Loader_rejects_unknown_duplicate_and_malformed_fields_without_echoing_values()
    {
        var entries = Enumerable.Range(0, 20).Select(i => new Dictionary<string, object?>
        {
            ["id"] = $"query-{i}",
            ["query"] = $"query text {i}",
            ["expectedPaths"] = new[] { "Note0.md" },
            ["category"] = "semantic"
        }).ToArray();

        entries[19]["private-marker"] = "QUERY_SENTINEL";
        using var unknown = new TemporaryJson(JsonSerializer.Serialize(entries));
        var exception = Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(unknown.Path));
        Assert.Contains("entry 20", exception.Message);
        Assert.DoesNotContain("QUERY_SENTINEL", exception.Message);

        entries[19].Remove("private-marker");
        using var duplicateProperty = new TemporaryJson(BenchmarkJson(20)
            .Replace("{\"id\":\"query-0\",", "{\"id\":\"query-0\",\"id\":\"query-0\",", StringComparison.Ordinal));
        Assert.Contains("duplicate field",
            Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(duplicateProperty.Path)).Message);

        entries[19]["query"] = 47;
        using var invalidType = new TemporaryJson(JsonSerializer.Serialize(entries));
        Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(invalidType.Path));

        using var invalidJson = new TemporaryJson("[{ invalid json }");
        Assert.Equal("Benchmark file is not valid JSON.",
            Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(invalidJson.Path)).Message);
    }

    [Fact]
    public void Loader_rejects_null_entries_and_missing_null_or_blank_fields()
    {
        var entries = Enumerable.Range(0, 20).Select(i => new Dictionary<string, object?>
        {
            ["id"] = $"query-{i}", ["query"] = $"query text {i}",
            ["expectedPaths"] = new[] { "Note0.md" }, ["category"] = "semantic"
        }).ToArray();
        entries[0].Remove("category");
        using var missing = new TemporaryJson(JsonSerializer.Serialize(entries));
        Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(missing.Path));

        entries[0]["category"] = null;
        entries[1]["query"] = " ";
        entries[2]["expectedPaths"] = new[] { "" };
        using var nullOrBlank = new TemporaryJson(JsonSerializer.Serialize(entries));
        Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(nullOrBlank.Path));

        object?[] withNull = Enumerable.Range(0, 20).Select(i => (object?)new
        {
            id = $"query-{i}", query = $"query text {i}", expectedPaths = new[] { "Note0.md" }, category = "semantic"
        }).ToArray();
        withNull[19] = null;
        using var nullEntry = new TemporaryJson(JsonSerializer.Serialize(withNull));
        Assert.Throws<RetrievalBenchmarkException>(() => RetrievalBenchmarkLoader.Load(nullEntry.Path));
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
        var options = new Knowledge.Runtime.Configuration.KnowledgeOptions
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
        Assert.All(report.Categories, category => Assert.Equal(10, category.Total));
        Assert.All(report.Results, result => Assert.Equal(1, result.MatchedTerms));
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
    public async Task Evaluator_handles_alternative_paths_short_and_empty_results_case_and_folder_exactness()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        vault.Write("Alt0.md", "# Alternative\nBody.");
        var entries = Enumerable.Range(0, 20).Select(i => new RetrievalBenchmarkEntry(
            $"query-{i}", $"query text {i}", i == 0 ? ["Note0.md", "Alt0.md"] : [$"Note{i}.md"],
            i < 10 ? "semantic" : "exact", ["MATCH_SENTINEL"])).ToArray();
        var benchmark = new RetrievalBenchmark("hash", entries);
        var options = new Knowledge.Runtime.Configuration.KnowledgeOptions
        {
            Vault = new() { Id = "test-vault", Path = vault.Path },
            Index = new() { Include = ["**/*.md"] }
        };
        var ingestor = new MarkdownIngestor();
        var ingestion = RetrievalBenchmarkSourceValidator.CreateOptions(options);
        var sources = RetrievalBenchmarkSourceValidator.ValidateSources(benchmark, ingestor, ingestion, CancellationToken.None);
        var snapshot = RetrievalBenchmarkSourceValidator.CreateSnapshotHash(ingestor, sources, CancellationToken.None);
        var retriever = new RecordingRetriever(i => i switch
        {
            0 => [Result("Alt0.md", 0, "MATCH_SENTINEL")],
            1 => [Result("note1.md", 0, "MATCH_SENTINEL")],
            2 => [Result("Elsewhere/Note2.md", 0, "MATCH_SENTINEL")],
            3 => [],
            4 => [Result("Elsewhere.md", 0, ""), Result("Note4.md", 1, "")],
            _ => [Result($"Note{i}.md", 0, "")]
        });

        var report = await RetrievalBenchmarkEvaluator.EvaluateAsync(benchmark, retriever, options, ingestor,
            ingestion, snapshot, "profile");

        Assert.Equal(17, report.Passed);
        Assert.True(report.TargetMet);
        Assert.Equal(1, report.Results[0].FirstMatchingRank);
        Assert.False(report.Results[1].Hit); // Case mismatch is not an exact path match.
        Assert.False(report.Results[2].Hit); // Same filename in another folder is not a match.
        Assert.False(report.Results[3].Hit); // Empty results are a valid miss.
        Assert.Equal(2, report.Results[4].FirstMatchingRank); // Short results use available ranks.
        Assert.Equal(1, report.Results[0].MatchedTerms);
        Assert.Equal(0, report.Results[4].MatchedTerms);
        Assert.Equal(("exact", 10, 10), (report.Categories[0].Category,
            report.Categories[0].Passed, report.Categories[0].Total));
        Assert.Equal(("semantic", 7, 10), (report.Categories[1].Category,
            report.Categories[1].Passed, report.Categories[1].Total));
    }

    [Fact]
    public async Task Evaluator_rejects_malformed_ranked_result_metadata()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        var benchmark = new RetrievalBenchmark("hash", Enumerable.Range(0, 20).Select(i =>
            new RetrievalBenchmarkEntry($"query-{i}", $"query {i}", [$"Note{i}.md"], "semantic")).ToArray());
        var options = new Knowledge.Runtime.Configuration.KnowledgeOptions
        {
            Vault = new() { Id = "test-vault", Path = vault.Path },
            Index = new() { Include = ["**/*.md"] }
        };
        var ingestor = new MarkdownIngestor();
        var ingestion = RetrievalBenchmarkSourceValidator.CreateOptions(options);
        var sources = RetrievalBenchmarkSourceValidator.ValidateSources(benchmark, ingestor, ingestion, CancellationToken.None);
        var snapshot = RetrievalBenchmarkSourceValidator.CreateSnapshotHash(ingestor, sources, CancellationToken.None);
        var retriever = new RecordingRetriever(_ => [Result("invalid//path.md", 0, "")]);

        await Assert.ThrowsAsync<InvalidDataException>(() => RetrievalBenchmarkEvaluator.EvaluateAsync(benchmark,
            retriever, options, ingestor, ingestion, snapshot, "profile"));
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
    public async Task Cli_rejects_invalid_late_query_before_retrieval()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        var vocab = Path.Combine(FindRepositoryRoot(), "models", "bge-small-en-v1.5", "vocab.txt");
        using var configuration = new TemporaryConfiguration(
            $"vault: {{id: primary, path: '{vault.Path}'}}\nindex: {{include: ['**/*.md']}}\nembedding: {{tokenizerPath: '{vocab}'}}\n");
        var entries = Enumerable.Range(0, 20).Select(i => new
        {
            id = $"query-{i}",
            query = i == 19 ? string.Join(' ', Enumerable.Repeat("oversized", 600)) : $"query text {i}",
            expectedPaths = new[] { "Note0.md" },
            category = "semantic"
        });
        using var benchmarkFile = new TemporaryJson(JsonSerializer.Serialize(entries));
        using var reportFile = new TemporaryJson("");
        File.Delete(reportFile.Path);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var factoryCalls = 0;

        var code = await KnowledgeCli.RunAsync(["evaluate", "--benchmark", benchmarkFile.Path, "--output", reportFile.Path,
            "--config", configuration.Path], output, error,
            retrieverFactory: _ => { factoryCalls++; return new RecordingRetriever(_ => []); });

        Assert.Equal(2, code);
        Assert.Equal(0, factoryCalls);
        Assert.False(File.Exists(reportFile.Path));
        Assert.DoesNotContain("oversized", output.ToString() + error.ToString());
    }

    [Fact]
    public async Task Cli_source_change_during_evaluation_preserves_existing_report()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        var vocab = Path.Combine(FindRepositoryRoot(), "models", "bge-small-en-v1.5", "vocab.txt");
        using var configuration = new TemporaryConfiguration(
            $"vault: {{id: primary, path: '{vault.Path}'}}\nindex: {{include: ['**/*.md']}}\nembedding: {{tokenizerPath: '{vocab}'}}\n");
        using var benchmarkFile = new TemporaryJson(BenchmarkJson(20));
        using var reportFile = new TemporaryJson("previous-complete-report");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var retriever = new RecordingRetriever(index =>
        {
            if (index == 19) vault.Write("Note0.md", "# Changed while evaluating\nBody.");
            return [Result("Note0.md", 0, "private result body")];
        });

        var code = await KnowledgeCli.RunAsync(["evaluate", "--benchmark", benchmarkFile.Path, "--output", reportFile.Path,
            "--config", configuration.Path], output, error, retrieverFactory: _ => retriever);

        Assert.Equal(1, code);
        Assert.Equal("previous-complete-report", await File.ReadAllTextAsync(reportFile.Path));
        Assert.DoesNotContain("private result body", output.ToString() + error.ToString());
    }

    [Fact]
    public async Task Cli_cancellation_during_evaluation_preserves_existing_report()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        var vocab = Path.Combine(FindRepositoryRoot(), "models", "bge-small-en-v1.5", "vocab.txt");
        using var configuration = new TemporaryConfiguration(
            $"vault: {{id: primary, path: '{vault.Path}'}}\nindex: {{include: ['**/*.md']}}\nembedding: {{tokenizerPath: '{vocab}'}}\n");
        using var benchmarkFile = new TemporaryJson(BenchmarkJson(20));
        using var reportFile = new TemporaryJson("previous-complete-report");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var retriever = new RecordingRetriever(index =>
        {
            if (index == 9) cancellation.Cancel();
            return [Result("Note0.md", 0, "private result body")];
        });

        var code = await KnowledgeCli.RunAsync(["evaluate", "--benchmark", benchmarkFile.Path, "--output", reportFile.Path,
            "--config", configuration.Path], output, error, cancellation.Token, _ => retriever);

        Assert.Equal(130, code);
        Assert.Equal(10, retriever.Requests.Count);
        Assert.Equal("previous-complete-report", await File.ReadAllTextAsync(reportFile.Path));
        Assert.DoesNotContain("private result body", output.ToString() + error.ToString());
    }

    [Fact]
    public async Task Cli_retrieval_failure_preserves_existing_report_and_suppresses_exception_text()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        var vocab = Path.Combine(FindRepositoryRoot(), "models", "bge-small-en-v1.5", "vocab.txt");
        using var configuration = new TemporaryConfiguration(
            $"vault: {{id: primary, path: '{vault.Path}'}}\nindex: {{include: ['**/*.md']}}\nembedding: {{tokenizerPath: '{vocab}'}}\n");
        using var benchmarkFile = new TemporaryJson(BenchmarkJson(20));
        using var reportFile = new TemporaryJson("previous-complete-report");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var retriever = new RecordingRetriever(_ => throw new InvalidDataException("SERVER_ERROR_SENTINEL"));

        var code = await KnowledgeCli.RunAsync(["evaluate", "--benchmark", benchmarkFile.Path,
            "--output", reportFile.Path, "--config", configuration.Path], output, error,
            retrieverFactory: _ => retriever);

        Assert.Equal(1, code);
        Assert.Equal("previous-complete-report", await File.ReadAllTextAsync(reportFile.Path));
        Assert.DoesNotContain("SERVER_ERROR_SENTINEL", output.ToString() + error.ToString());
    }

    [Fact]
    public async Task Cli_rejects_report_destination_inside_vault()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        var vocab = Path.Combine(FindRepositoryRoot(), "models", "bge-small-en-v1.5", "vocab.txt");
        using var configuration = new TemporaryConfiguration(
            $"vault: {{id: primary, path: '{vault.Path}'}}\nindex: {{include: ['**/*.md']}}\nembedding: {{tokenizerPath: '{vocab}'}}\n");
        using var benchmarkFile = new TemporaryJson(BenchmarkJson(20));
        var reportPath = Path.Combine(vault.Path, "report.json");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var factoryCalls = 0;

        var code = await KnowledgeCli.RunAsync(["evaluate", "--benchmark", benchmarkFile.Path, "--output", reportPath,
            "--config", configuration.Path], output, error,
            retrieverFactory: _ => { factoryCalls++; return new RecordingRetriever(_ => []); });

        Assert.Equal(2, code);
        Assert.Equal(0, factoryCalls);
        Assert.False(File.Exists(reportPath));
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

    [Fact]
    public async Task Cli_rejects_max_limit_below_five_and_report_overwrite_before_retrieval()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        var vocab = Path.Combine(FindRepositoryRoot(), "models", "bge-small-en-v1.5", "vocab.txt");
        using var configuration = new TemporaryConfiguration(
            $"vault: {{id: primary, path: '{vault.Path}'}}\nindex: {{include: ['**/*.md']}}\nembedding: {{tokenizerPath: '{vocab}'}}\nretrieval: {{maxLimit: 4}}\n");
        using var benchmarkFile = new TemporaryJson(BenchmarkJson(20));
        using var reportFile = new TemporaryJson("");
        File.Delete(reportFile.Path);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var factoryCalls = 0;
        Func<Knowledge.Runtime.Configuration.KnowledgeOptions, IKnowledgeRetriever> factory = _ =>
        {
            factoryCalls++;
            return new RecordingRetriever(_ => []);
        };

        var invalidLimit = await KnowledgeCli.RunAsync(["evaluate", "--benchmark", benchmarkFile.Path,
            "--output", reportFile.Path, "--config", configuration.Path], output, error, retrieverFactory: factory);
        Assert.Equal(2, invalidLimit);
        Assert.Equal(0, factoryCalls);
        Assert.False(File.Exists(reportFile.Path));

        var overwrite = await KnowledgeCli.RunAsync(["evaluate", "--benchmark", benchmarkFile.Path,
            "--output", benchmarkFile.Path, "--config", configuration.Path], output, error, retrieverFactory: factory);
        Assert.Equal(2, overwrite);
        Assert.Equal(0, factoryCalls);
        Assert.True(File.Exists(benchmarkFile.Path));
    }

    [Fact]
    public async Task Cli_reports_missing_tokenizer_as_setup_error_without_retrieval()
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        using var configuration = new TemporaryConfiguration(
            $"vault: {{id: primary, path: '{vault.Path}'}}\nindex: {{include: ['**/*.md']}}\nembedding: {{tokenizerPath: '/missing/tokenizer.json'}}\n");
        using var benchmarkFile = new TemporaryJson(BenchmarkJson(20));
        using var reportFile = new TemporaryJson("");
        File.Delete(reportFile.Path);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var factoryCalls = 0;

        var code = await KnowledgeCli.RunAsync(["evaluate", "--benchmark", benchmarkFile.Path,
            "--output", reportFile.Path, "--config", configuration.Path], output, error,
            retrieverFactory: _ => { factoryCalls++; return new RecordingRetriever(_ => []); });

        Assert.Equal(2, code);
        Assert.Equal(0, factoryCalls);
        Assert.False(File.Exists(reportFile.Path));
        Assert.DoesNotContain("/missing/tokenizer.json", output.ToString() + error.ToString());
    }

    [Theory]
    [InlineData(17, true)]
    [InlineData(16, false)]
    public async Task HitAt5_target_uses_unrounded_ratio(int passing, bool expectedTarget)
    {
        using var vault = new TemporaryVault();
        for (var i = 0; i < 20; i++) vault.Write($"Note{i}.md", $"# Note {i}\nBody.");
        var options = new Knowledge.Runtime.Configuration.KnowledgeOptions
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

    [Fact]
    public async Task Report_write_failure_preserves_existing_destination()
    {
        using var destination = new TemporaryDirectory();
        var existing = Path.Combine(destination.Path, "report.json");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "sentinel.txt"), "previous complete report");
        var report = new RetrievalBenchmarkReport(1, DateTimeOffset.UtcNow, "test", null, "test", "test", null,
            "dense", "hash", 20, "vault", "collection", null, null, "snapshot", "model", "revision", "model-hash",
            "tokenizer-hash", "tokenizer-json-hash", "tokenizer-config-hash", "special-tokens-hash", "model-config-hash",
            "pooling-hash", 384, "cosine", 1, 2, "format", "profile", 300, 400, 40, [], [], "opt-out", 5,
            0.85, 0, 20, 0, false, [], []);

        await Assert.ThrowsAnyAsync<IOException>(() => RetrievalBenchmarkEvaluator.WriteReportAtomicallyAsync(existing, report));

        Assert.Equal("previous complete report", await File.ReadAllTextAsync(Path.Combine(existing, "sentinel.txt")));
        Assert.Empty(Directory.EnumerateFiles(destination.Path, $".{Path.GetFileName(existing)}.*.tmp"));
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knowledge-benchmark-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
