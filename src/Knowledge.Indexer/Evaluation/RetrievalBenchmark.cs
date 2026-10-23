using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Knowledge.Core;
using Knowledge.Embeddings;
using Knowledge.Indexer.Configuration;
using Knowledge.Markdown;
using YamlDotNet.Core;

namespace Knowledge.Indexer.Evaluation;

public sealed record RetrievalBenchmarkEntry(
    string Id,
    string Query,
    IReadOnlyList<string> ExpectedPaths,
    string Category,
    IReadOnlyList<string>? ExpectedTerms = null);

public sealed record RetrievalBenchmark(string Sha256, IReadOnlyList<RetrievalBenchmarkEntry> Entries);

public static partial class RetrievalBenchmarkLoader
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow };

    public static RetrievalBenchmark Load(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RetrievalBenchmarkException("Benchmark file could not be read.");
        }

        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, DocumentOptions); }
        catch (JsonException) { throw new RetrievalBenchmarkException("Benchmark file is not valid JSON."); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new RetrievalBenchmarkException("Benchmark must be a JSON array.");
            var entries = new List<RetrievalBenchmarkEntry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                index++;
                entries.Add(ParseEntry(item, index, ids));
            }
            if (entries.Count < 20)
                throw new RetrievalBenchmarkException("Benchmark must contain at least 20 entries.");
            if (entries.Count > 50)
                throw new RetrievalBenchmarkException("Benchmark must contain no more than 50 entries.");
            return new RetrievalBenchmark(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), entries);
        }
    }

    private static RetrievalBenchmarkEntry ParseEntry(JsonElement item, int index, HashSet<string> ids)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw EntryError(index, "must be an object");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name is not ("id" or "query" or "expectedPaths" or "category" or "expectedTerms") ||
                !fields.TryAdd(property.Name, property.Value))
                throw EntryError(index, "contains an unsupported or duplicate field");
        }

        var id = ReadString(fields, "id", index);
        if (id.Length > 64 || !SafeIdRegex().IsMatch(id) || !ids.Add(id))
            throw EntryError(index, "has an invalid or duplicate id");
        var query = ReadString(fields, "query", index);
        if (query.Length == 0 || HasControl(query)) throw EntryError(index, "has an invalid query");
        var category = ReadString(fields, "category", index);
        if (category.Length > 48 || !CategoryRegex().IsMatch(category))
            throw EntryError(index, "has an invalid category");
        var expectedPaths = ReadStringArray(fields, "expectedPaths", index, required: true);
        if (expectedPaths.Count == 0 || expectedPaths.Any(path => !IsCanonicalRelativeMarkdownPath(path)) ||
            expectedPaths.Distinct(StringComparer.Ordinal).Count() != expectedPaths.Count)
            throw EntryError(index, "has invalid or duplicate expected paths");
        var expectedTerms = ReadStringArray(fields, "expectedTerms", index, required: false);
        if (expectedTerms.Any(term => term.Length == 0 || HasControl(term)) ||
            expectedTerms.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedTerms.Count)
            throw EntryError(index, "has invalid expected terms");
        return new RetrievalBenchmarkEntry(id, query, expectedPaths, category, expectedTerms);
    }

    private static string ReadString(Dictionary<string, JsonElement> fields, string field, int index)
    {
        if (!fields.TryGetValue(field, out var value) || value.ValueKind != JsonValueKind.String)
            throw EntryError(index, $"is missing a valid {field} field");
        var result = value.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(result) || result != result.Trim())
            throw EntryError(index, $"has a blank or noncanonical {field} field");
        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(Dictionary<string, JsonElement> fields, string field, int index, bool required)
    {
        if (!fields.TryGetValue(field, out var value))
        {
            if (!required) return [];
            throw EntryError(index, $"is missing {field}");
        }
        if (value.ValueKind != JsonValueKind.Array) throw EntryError(index, $"has an invalid {field} field");
        var result = new List<string>();
        foreach (var child in value.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.String) throw EntryError(index, $"has an invalid {field} item");
            var item = child.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(item) || item != item.Trim()) throw EntryError(index, $"has a blank or noncanonical {field} item");
            result.Add(item);
        }
        return result;
    }

    public static bool IsCanonicalRelativeMarkdownPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.Contains('\0') ||
            Path.IsPathRooted(path) || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = path.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not ("." or "..") && !HasControl(segment));
    }

    private static bool HasControl(string value) => value.Any(char.IsControl);
    private static RetrievalBenchmarkException EntryError(int index, string detail) => new($"Benchmark entry {index} {detail}.");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdRegex();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryRegex();
}

public sealed class RetrievalBenchmarkException(string message) : Exception(message);

public static class RetrievalBenchmarkSourceValidator
{
    public static IReadOnlyList<VaultFile> ValidateSources(RetrievalBenchmark benchmark,
        MarkdownIngestor ingestor, MarkdownIngestionOptions ingestion, CancellationToken cancellationToken)
    {
        var scan = ingestor.Discover(ingestion, cancellationToken);
        if (!scan.IsComplete) throw new RetrievalBenchmarkException("Configured vault could not be completely discovered.");
        if (scan.Files.Count == 0) throw new RetrievalBenchmarkException("Configured vault contains no eligible Markdown sources.");

        var byPath = scan.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var expected = benchmark.Entries.SelectMany(entry => entry.ExpectedPaths).Distinct(StringComparer.Ordinal);
        foreach (var path in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byPath.TryGetValue(path, out var file))
                throw new RetrievalBenchmarkException("An expected source path is missing or excluded from the configured vault.");
            try
            {
                var snapshot = ingestor.ReadSnapshot(file);
                var document = ingestor.IngestDocument(snapshot, ingestion);
                if (document.Status != DocumentIngestionStatus.Indexed)
                    throw new RetrievalBenchmarkException("An expected source is opted out, empty, or not indexable.");
            }
            catch (RetrievalBenchmarkException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or DecoderFallbackException or YamlException)
            {
                throw new RetrievalBenchmarkException("An expected source could not be read or parsed.");
            }
        }
        return scan.Files;
    }

    public static string CreateSnapshotHash(MarkdownIngestor ingestor, IReadOnlyList<VaultFile> files,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = ingestor.ReadSnapshot(file);
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(snapshot.FileHash));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static MarkdownIngestionOptions CreateOptions(KnowledgeOptions options) => new(
        options.Vault.Id, options.Vault.Path, options.Index.Include, options.Index.Exclude,
        options.Index.FrontmatterOptOutField, options.Chunking.TargetTokens, options.Chunking.SoftMaxTokens,
        options.Chunking.OverlapTokens);
}
