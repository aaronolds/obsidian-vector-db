using Knowledge.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Knowledge.Qdrant;

/// <summary>Read-only dense retrieval over an already indexed Qdrant collection.</summary>
public sealed class QdrantKnowledgeRetriever(
    QdrantClient client,
    string collection,
    string vaultId,
    string expectedIndexProfile,
    IQueryEmbeddingService queryEmbeddings,
    int maxLimit = 50) : IKnowledgeRetriever
{
    private const int ScrollPageSize = 256;
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = KnowledgeSearchRequestValidator.Normalize(request, maxLimit);
        cancellationToken.ThrowIfCancellationRequested();
        queryEmbeddings.ValidateQuery(normalized.Query);

        if (!await client.CollectionExistsAsync(collection, cancellationToken))
            throw new KnowledgeSearchCompatibilityException("Search collection is missing; run knowledge index with this configuration first.");

        var info = await client.GetCollectionInfoAsync(collection, cancellationToken);
        ValidateCollection(info);
        var metadata = await ReadCompatibilityMetadataAsync(cancellationToken);
        ValidateMetadata(metadata);
        if (metadata.Count == 0)
            return [];

        var embedding = await queryEmbeddings.EmbedQueryAsync(normalized.Query, cancellationToken);
        ValidateEmbedding(embedding);

        var filter = BuildFilter(normalized, vaultId);
        var payloadSelector = new WithPayloadSelector
        {
            Include = new PayloadIncludeSelector
            {
                Fields = { "vaultId", "documentId", "chunkId", "path", "title", "headingPath", "chunkOrdinal", "contentMarkdown", "schemaVersion", "embeddingSchemaVersion", "embeddingProvider", "embeddingModel", "embeddingDimensions", "indexProfile" }
            }
        };
        var response = await client.QueryAsync(collection, embedding.Vector.ToArray(), filter: filter,
            limit: (ulong)normalized.Limit, payloadSelector: payloadSelector, vectorsSelector: false,
            timeout: RpcTimeout, cancellationToken: cancellationToken);

        var results = response.Select(MapResult).ToArray();
        Array.Sort(results, static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : StringComparer.Ordinal.Compare(left.ChunkId, right.ChunkId);
        });
        return results;
    }

    private async Task<List<CompatibilityMetadata>> ReadCompatibilityMetadataAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CompatibilityMetadata>();
        PointId? offset = null;
        var filter = new Filter
        {
            Must = { new Condition { Field = new FieldCondition { Key = "vaultId", Match = new Match { Keyword = vaultId } } } }
        };
        var payloadSelector = new WithPayloadSelector
        {
            Include = new PayloadIncludeSelector
            {
                Fields = { "vaultId", "schemaVersion", "embeddingSchemaVersion", "embeddingProvider", "embeddingModel", "embeddingDimensions", "indexProfile" }
            }
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await client.ScrollAsync(collection, filter, limit: ScrollPageSize, offset: offset,
                payloadSelector: payloadSelector, vectorsSelector: false,
                cancellationToken: cancellationToken);
            foreach (var point in page.Result)
            {
                var payload = point.Payload;
                entries.Add(new CompatibilityMetadata(
                    ReadString(payload, "vaultId"), ReadInteger(payload, "schemaVersion"),
                    ReadInteger(payload, "embeddingSchemaVersion"), ReadString(payload, "embeddingProvider"),
                    ReadString(payload, "embeddingModel"), ReadInteger(payload, "embeddingDimensions"),
                    ReadString(payload, "indexProfile")));
            }

            if (page.NextPageOffset is null) break;
            offset = page.NextPageOffset;
        }
        return entries;
    }

    private void ValidateMetadata(IEnumerable<CompatibilityMetadata> metadata)
    {
        foreach (var item in metadata)
        {
            if (item.VaultId != vaultId || item.SchemaVersion != KnowledgeIndexSchema.SchemaVersion ||
                item.EmbeddingSchemaVersion != KnowledgeIndexSchema.EmbeddingSchemaVersion ||
                item.EmbeddingProvider != KnowledgeIndexSchema.EmbeddingProvider ||
                item.EmbeddingModel != KnowledgeIndexSchema.EmbeddingModel ||
                item.EmbeddingDimensions != KnowledgeIndexSchema.EmbeddingDimensions ||
                item.IndexProfile != expectedIndexProfile)
                throw new KnowledgeSearchCompatibilityException("Search index uses an incompatible schema or embedding profile; select a fresh collection and run knowledge index.");
        }
    }

    private static void ValidateCollection(CollectionInfo info)
    {
        if (!QdrantCollectionLayout.IsCompatible(info))
            throw new KnowledgeSearchCompatibilityException("Search collection has an incompatible vector layout; select a compatible collection.");
    }

    private static Filter BuildFilter(KnowledgeSearchRequest request, string vaultId)
    {
        var filter = new Filter();
        filter.Must.Add(new Condition { Field = new FieldCondition { Key = "vaultId", Match = new Match { Keyword = vaultId } } });
        if (request.PathPrefix is not null)
            filter.Must.Add(new Condition { Field = new FieldCondition { Key = "path", Match = new Match { Prefix = request.PathPrefix } } });
        foreach (var tag in request.Tags ?? [])
            filter.Must.Add(new Condition { Field = new FieldCondition { Key = "tags", Match = new Match { Keyword = tag } } });
        return filter;
    }

    public static Filter CreateFilter(KnowledgeSearchRequest request, string vaultId)
    {
        if (string.IsNullOrWhiteSpace(vaultId)) throw new ArgumentException("Vault ID is required.", nameof(vaultId));
        return BuildFilter(KnowledgeSearchRequestValidator.Normalize(request, 50), vaultId);
    }

    private static void ValidateEmbedding(EmbeddingResult embedding)
    {
        var squaredNorm = 0d;
        foreach (var value in embedding.Vector.Span)
        {
            if (!float.IsFinite(value))
                throw new InvalidDataException("Query embedding does not match the configured finite 384-dimensional model contract.");
            squaredNorm += (double)value * value;
        }
        if (embedding.Provider != KnowledgeIndexSchema.EmbeddingProvider ||
            embedding.Model != KnowledgeIndexSchema.EmbeddingModel ||
            embedding.Dimensions != KnowledgeIndexSchema.EmbeddingDimensions ||
            embedding.Vector.Length != KnowledgeIndexSchema.EmbeddingDimensions ||
            !double.IsFinite(squaredNorm) || squaredNorm <= 0)
            throw new InvalidDataException("Query embedding does not match the configured finite 384-dimensional model contract.");
    }

    private KnowledgeSearchResult MapResult(ScoredPoint point)
    {
        var payload = point.Payload;
        var vault = ReadString(payload, "vaultId");
        var documentId = ReadString(payload, "documentId");
        var chunkId = ReadString(payload, "chunkId");
        var path = ReadString(payload, "path");
        var title = ReadString(payload, "title");
        var hasContent = TryReadString(payload, "contentMarkdown", out var content);
        var headings = ReadStringList(payload, "headingPath");
        var ordinal = ReadInteger(payload, "chunkOrdinal");
        if (vault != vaultId || string.IsNullOrWhiteSpace(documentId) ||
            string.IsNullOrWhiteSpace(chunkId) || string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(title) || !hasContent || headings is null || ordinal < 0 ||
            documentId != KnowledgeIdentity.DocumentId(vault, path) ||
            chunkId != KnowledgeIdentity.ChunkId(documentId, headings, ordinal) ||
            !HasCanonicalPointId(point.Id, chunkId) ||
            !double.IsFinite(point.Score) || ReadInteger(payload, "schemaVersion") != KnowledgeIndexSchema.SchemaVersion ||
            ReadInteger(payload, "embeddingSchemaVersion") != KnowledgeIndexSchema.EmbeddingSchemaVersion ||
            ReadString(payload, "embeddingProvider") != KnowledgeIndexSchema.EmbeddingProvider ||
            ReadString(payload, "embeddingModel") != KnowledgeIndexSchema.EmbeddingModel ||
            ReadInteger(payload, "embeddingDimensions") != KnowledgeIndexSchema.EmbeddingDimensions ||
            ReadString(payload, "indexProfile") != expectedIndexProfile)
            throw new InvalidDataException("Qdrant returned a search point with invalid provenance or score data.");
        return new KnowledgeSearchResult(documentId, chunkId, path, title, headings, content, point.Score);
    }

    private static bool HasCanonicalPointId(PointId pointId, string chunkId)
    {
        try
        {
            return Guid.TryParse(pointId.Uuid, out var actual) &&
                   actual == QdrantKnowledgeIndexStore.ToPointId(chunkId);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ReadString(IDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.HasStringValue ? value.StringValue : "";

    private static bool TryReadString(IDictionary<string, Value> payload, string key, out string value)
    {
        if (payload.TryGetValue(key, out var payloadValue) && payloadValue.HasStringValue)
        {
            value = payloadValue.StringValue;
            return true;
        }
        value = "";
        return false;
    }

    private static int ReadInteger(IDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.HasIntegerValue &&
        value.IntegerValue is >= int.MinValue and <= int.MaxValue ? (int)value.IntegerValue : -1;

    private static IReadOnlyList<string>? ReadStringList(IDictionary<string, Value> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value.KindCase != Value.KindOneofCase.ListValue) return null;
        var list = value.ListValue;
        var result = new List<string>(list.Values.Count);
        foreach (var item in list.Values)
        {
            if (!item.HasStringValue) return null;
            result.Add(item.StringValue);
        }
        return result;
    }

    private sealed record CompatibilityMetadata(string VaultId, int SchemaVersion, int EmbeddingSchemaVersion,
        string EmbeddingProvider, string EmbeddingModel, int EmbeddingDimensions, string IndexProfile);
}

public sealed class KnowledgeSearchCompatibilityException(string message) : Exception(message);
