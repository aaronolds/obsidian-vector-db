using System.Text.Json;
using Knowledge.Core;
using Knowledge.Embeddings;
using Knowledge.Indexer.Configuration;
using Knowledge.Indexer.Indexing;
using Knowledge.Markdown;

namespace Knowledge.Indexer.Evaluation;

public sealed record RetrievalBenchmarkResult(
    string Id,
    string Category,
    bool Hit,
    int? FirstMatchingRank,
    IReadOnlyList<RetrievalBenchmarkHit> Results,
    int? MatchedTerms,
    int? TotalTerms);

public sealed record RetrievalBenchmarkHit(string DocumentId, string ChunkId, string Path, int Rank, double Score);

public sealed record RetrievalBenchmarkCategory(string Category, int Passed, int Total, double HitAt5);

public sealed record RetrievalBenchmarkReport(
    int ReportSchemaVersion,
    DateTimeOffset RunUtc,
    string ApplicationVersion,
    string? GitRevision,
    string DotnetRuntime,
    string OnnxRuntime,
    string? QdrantVersion,
    string RetrievalMode,
    string BenchmarkSha256,
    int QueryCount,
    string VaultId,
    string Collection,
    long? IndexedDocuments,
    long? IndexedChunks,
    string SourceSnapshotSha256,
    string Model,
    string ModelRevision,
    string ModelSha256,
    string TokenizerSha256,
    string TokenizerJsonSha256,
    string TokenizerConfigSha256,
    string SpecialTokensMapSha256,
    string ModelConfigSha256,
    string PoolingConfigSha256,
    int Dimensions,
    string Distance,
    int PayloadSchemaVersion,
    int EmbeddingSchemaVersion,
    string QueryFormatting,
    string IndexProfile,
    int TargetTokens,
    int SoftMaxTokens,
    int OverlapTokens,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    string FrontmatterOptOutField,
    int K,
    double HitAt5Target,
    int Passed,
    int Total,
    double HitAt5,
    bool TargetMet,
    IReadOnlyList<RetrievalBenchmarkCategory> Categories,
    IReadOnlyList<RetrievalBenchmarkResult> Results);

public sealed record RetrievalBenchmarkEnvironment(string? GitRevision = null, string? QdrantVersion = null,
    long? IndexedDocuments = null, long? IndexedChunks = null);

public static class RetrievalBenchmarkEvaluator
{
    public const int K = 5;
    public const double Target = 0.85;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<RetrievalBenchmarkReport> EvaluateAsync(RetrievalBenchmark benchmark,
        IKnowledgeRetriever retriever, KnowledgeOptions options, MarkdownIngestor ingestor,
        MarkdownIngestionOptions ingestion,
        string sourceSnapshotSha256, string indexProfile, CancellationToken cancellationToken = default,
        RetrievalBenchmarkEnvironment? environment = null)
    {
        var results = new List<RetrievalBenchmarkResult>(benchmark.Entries.Count);
        foreach (var entry in benchmark.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = new HashSet<string>(entry.ExpectedPaths, StringComparer.Ordinal);
            var ranked = await retriever.SearchAsync(new KnowledgeSearchRequest(entry.Query, K), cancellationToken).ConfigureAwait(false);
            var top = new List<RetrievalBenchmarkHit>(Math.Min(K, ranked.Count));
            int? firstMatchingRank = null;
            var matchedTerms = 0;
            var maxRank = Math.Min(K, ranked.Count);
            for (var i = 0; i < maxRank; i++)
            {
                var result = ranked[i];
                if (string.IsNullOrWhiteSpace(result.DocumentId) || string.IsNullOrWhiteSpace(result.ChunkId) ||
                    !RetrievalBenchmarkLoader.IsCanonicalRelativeMarkdownPath(result.Path) || result.ContentMarkdown is null ||
                    !double.IsFinite(result.Score))
                    throw new InvalidDataException("Retriever returned malformed benchmark result metadata.");
                var rank = i + 1;
                top.Add(new RetrievalBenchmarkHit(result.DocumentId, result.ChunkId, result.Path, rank, result.Score));
                if (matches.Contains(result.Path)) firstMatchingRank ??= rank;
            }
            foreach (var term in entry.ExpectedTerms ?? [])
                if (top.Any(result => ranked[result.Rank - 1].ContentMarkdown.Contains(term, StringComparison.OrdinalIgnoreCase))) matchedTerms++;
            var totalTerms = entry.ExpectedTerms?.Count ?? 0;
            results.Add(new RetrievalBenchmarkResult(entry.Id, entry.Category, firstMatchingRank.HasValue,
                firstMatchingRank, top, totalTerms == 0 ? null : matchedTerms, totalTerms == 0 ? null : totalTerms));
        }

        var passed = results.Count(result => result.Hit);
        var ratio = (double)passed / results.Count;
        var categories = results.GroupBy(result => result.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new RetrievalBenchmarkCategory(group.Key, group.Count(result => result.Hit), group.Count(),
                (double)group.Count(result => result.Hit) / group.Count())).ToArray();
        var assets = BgeAssetManifest.ModelSha256;
        var report = new RetrievalBenchmarkReport(1, DateTimeOffset.UtcNow,
            typeof(KnowledgeCli).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            environment?.GitRevision, Environment.Version.ToString(),
            typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version?.ToString() ?? "unknown",
            environment?.QdrantVersion, "dense", benchmark.Sha256, benchmark.Entries.Count, options.Vault.Id,
            options.Qdrant.Collection, environment?.IndexedDocuments, environment?.IndexedChunks, sourceSnapshotSha256,
            options.Embedding.Model, BgeAssetManifest.Revision, assets, BgeAssetManifest.VocabularySha256,
            BgeAssetManifest.TokenizerJsonSha256, BgeAssetManifest.TokenizerConfigSha256, BgeAssetManifest.SpecialTokensMapSha256,
            BgeAssetManifest.ModelConfigSha256, BgeAssetManifest.PoolingConfigSha256,
            options.Embedding.Dimensions, "cosine", KnowledgeIndexSchema.SchemaVersion,
            KnowledgeIndexSchema.EmbeddingSchemaVersion, KnowledgeIndexSchema.QueryFormatting, indexProfile,
            options.Chunking.TargetTokens, options.Chunking.SoftMaxTokens, options.Chunking.OverlapTokens,
            options.Index.Include, options.Index.Exclude, options.Index.FrontmatterOptOutField, K, Target,
            passed, results.Count, ratio, ratio >= Target, categories, results);
        return report;
    }

    public static async Task WriteReportAtomicallyAsync(string path, RetrievalBenchmarkReport report,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination) ?? throw new IOException("Report directory is invalid.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string? ReadGitRevision(string workingDirectory)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo("git", "rev-parse --verify HEAD")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 && output.Length == 40 && output.All(Uri.IsHexDigit) ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
