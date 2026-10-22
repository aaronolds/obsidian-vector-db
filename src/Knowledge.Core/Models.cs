using System.Security.Cryptography;
using System.Text;

namespace Knowledge.Core;

public sealed record VaultDescriptor(string VaultId, string RootPath);

public sealed record WikiLink(string Target, string? DisplayText);

public interface ITokenCounter
{
    int Count(string text, bool includeSpecialTokens = true);
}

public interface IEmbeddingService
{
    Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public interface IQueryEmbeddingService
{
    void ValidateQuery(string query);
    Task<EmbeddingResult> EmbedQueryAsync(string query, CancellationToken cancellationToken = default);
}

public interface IKnowledgeRetriever
{
    Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record KnowledgeSearchRequest(
    string Query,
    int Limit = 5,
    string? PathPrefix = null,
    IReadOnlyList<string>? Tags = null);

public sealed record KnowledgeSearchResult(
    string DocumentId,
    string ChunkId,
    string Path,
    string Title,
    IReadOnlyList<string> HeadingPath,
    string ContentMarkdown,
    double Score);

public static class KnowledgeSearchRequestValidator
{
    public static KnowledgeSearchRequest Normalize(KnowledgeSearchRequest request, int maxLimit)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Search query must not be blank.", nameof(request));
        if (maxLimit < 1 || request.Limit < 1 || request.Limit > maxLimit)
            throw new ArgumentOutOfRangeException(nameof(request), $"Search limit must be between 1 and {maxLimit}.");

        var pathPrefix = request.PathPrefix is null ? null : NormalizePathPrefix(request.PathPrefix);
        var tags = (request.Tags ?? []).Select(tag => tag?.Trim() ?? "").ToArray();
        if (tags.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Search tags must not be blank.", nameof(request));
        return new KnowledgeSearchRequest(request.Query.Trim(), request.Limit, pathPrefix,
            tags.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string? NormalizePathPrefix(string pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
            throw new ArgumentException("Search path prefix must not be blank.", nameof(pathPrefix));

        var prefix = pathPrefix.Trim();
        if (prefix.Contains('\0') || prefix.StartsWith('/') || prefix.StartsWith('\\') ||
            Path.IsPathRooted(prefix) || (prefix.Length >= 2 && char.IsAsciiLetter(prefix[0]) && prefix[1] == ':'))
            throw new ArgumentException("Search path prefix must be vault-relative.", nameof(pathPrefix));

        prefix = prefix.Replace('\\', '/');
        var segments = prefix.Split('/');
        if (segments.Contains("..", StringComparer.Ordinal))
            throw new ArgumentException("Search path prefix must stay inside the vault.", nameof(pathPrefix));
        var normalized = string.Join('/', segments.Where(segment => segment.Length > 0 && segment != "."));
        if (normalized.Length == 0) return null;
        if (prefix.EndsWith('/') && !normalized.EndsWith('/')) normalized += "/";
        return normalized;
    }
}

public static class KnowledgeIndexSchema
{
    public const int SchemaVersion = 1;
    public const int EmbeddingSchemaVersion = 2;
    public const string QueryFormatting = "bge-en-passage-retrieval-instruction-v1";
    public const string EmbeddingProvider = "onnx";
    public const string EmbeddingModel = "BAAI/bge-small-en-v1.5";
    public const int EmbeddingDimensions = 384;
}

public sealed record EmbeddingResult(
    ReadOnlyMemory<float> Vector,
    string Provider,
    string Model,
    int Dimensions);

public sealed record IndexedChunkMetadata(
    string PointId,
    string VaultId,
    string DocumentId,
    string ChunkId,
    string RelativePath,
    string FileHash,
    int ChunkOrdinal,
    int SchemaVersion,
    string EmbeddingProvider,
    string EmbeddingModel,
    int EmbeddingDimensions,
    int EmbeddingSchemaVersion,
    int DocumentChunkCount,
    string IndexProfile);

public sealed record KnowledgeIndexPoint(
    KnowledgeChunk Chunk,
    IReadOnlyList<string> Aliases,
    EmbeddingResult Embedding,
    Guid PointId,
    int DocumentChunkCount,
    string IndexProfile);

public interface IKnowledgeIndexStore
{
    Task EnsureCollectionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IndexedChunkMetadata>> GetIndexedChunksAsync(string vaultId, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(string vaultId, string documentId, CancellationToken cancellationToken = default);
    Task UpsertAsync(IReadOnlyList<KnowledgeIndexPoint> points, CancellationToken cancellationToken = default);
}

public static class EmbeddingTextFormatter
{
    public static string Format(string title, IReadOnlyList<string> headingPath,
        IReadOnlyList<string> tags, string content)
    {
        var builder = new StringBuilder($"Title: {title}\n");
        if (headingPath.Count > 0)
            builder.Append("Section: ").Append(string.Join(" > ", headingPath)).Append('\n');
        if (tags.Count > 0)
            builder.Append("Tags: ").Append(string.Join(", ", tags)).Append('\n');
        return builder.Append('\n').Append(content).ToString();
    }
}

public sealed record KnowledgeDocument(
    string VaultId,
    string DocumentId,
    string RelativePath,
    string Title,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<WikiLink> Links,
    string FileHash,
    DateTimeOffset ModifiedUtc);

public sealed record KnowledgeChunk(
    string VaultId,
    string DocumentId,
    string ChunkId,
    string RelativePath,
    string Title,
    IReadOnlyList<string> HeadingPath,
    IReadOnlyList<string> Tags,
    IReadOnlyList<WikiLink> Links,
    int ChunkOrdinal,
    string ContentMarkdown,
    string ContentNormalized,
    string FileHash,
    string ChunkHash,
    DateTimeOffset ModifiedUtc);

public static class KnowledgeIdentity
{
    public static string DocumentId(string vaultId, string relativePath) =>
        Hash($"{vaultId}\n{relativePath}");

    public static string ChunkId(string documentId, IReadOnlyList<string> headingPath, int ordinal) =>
        Hash($"{documentId}\n{string.Join(" > ", headingPath)}\n{ordinal}");

    public static string FileHash(ReadOnlySpan<byte> bytes) => Hash(bytes);

    public static string ChunkHash(string contentMarkdown, string title, IReadOnlyList<string> headingPath, IReadOnlyList<string> tags) =>
        Hash($"{contentMarkdown}\nTitle: {title}\nSection: {string.Join(" > ", headingPath)}\nTags: {string.Join(", ", tags)}");

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
