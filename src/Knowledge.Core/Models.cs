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
