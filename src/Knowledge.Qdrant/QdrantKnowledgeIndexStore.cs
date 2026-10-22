using Knowledge.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Knowledge.Qdrant;

/// <summary>Qdrant persistence for the indexer. Only callers in the Indexer should write.</summary>
public sealed class QdrantKnowledgeIndexStore(QdrantClient client, string collection) : IKnowledgeIndexStore
{
    private const int Dimensions = 384;
    private const int ScrollPageSize = 256;
    private const int UpsertBatchPoints = 24;
    private const int UpsertBatchBytes = 3 * 1024 * 1024;

    public async Task EnsureCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (!await client.CollectionExistsAsync(collection, cancellationToken))
        {
            await client.CreateCollectionAsync(collection,
                new VectorParams { Size = Dimensions, Distance = Distance.Cosine },
                cancellationToken: cancellationToken);
        }

        var info = await client.GetCollectionInfoAsync(collection, cancellationToken);
        var collectionParams = info.Config?.Params;
        var vectors = collectionParams?.VectorsConfig;
        if (collectionParams is null || vectors is null || vectors.ConfigCase != VectorsConfig.ConfigOneofCase.Params || vectors.Params is null ||
            vectors.Params.Size != Dimensions || vectors.Params.Distance != Distance.Cosine ||
            (collectionParams.SparseVectorsConfig?.Map.Count ?? 0) != 0)
            throw new IndexCollectionException("Qdrant collection has an incompatible vector layout; select a fresh collection.");

        foreach (var field in new[] { "vaultId", "documentId" })
        {
            if (info.PayloadSchema.TryGetValue(field, out var schema))
            {
                if (schema.DataType != PayloadSchemaType.Keyword)
                    throw new IndexCollectionException($"Qdrant payload index for {field} is incompatible; select a fresh collection.");
                continue;
            }
            await client.CreatePayloadIndexAsync(collection, field, PayloadSchemaType.Keyword,
                wait: true, cancellationToken: cancellationToken);
        }
    }

    public async Task<IReadOnlyList<IndexedChunkMetadata>> GetIndexedChunksAsync(string vaultId, CancellationToken cancellationToken = default)
    {
        var entries = new List<IndexedChunkMetadata>();
        PointId? offset = null;
        var filter = new Filter
        {
            Must = { new Condition { Field = new FieldCondition { Key = "vaultId", Match = new Match { Keyword = vaultId } } } }
        };
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WithPayloadSelector payloadSelector = new WithPayloadSelector
            {
                Include = new PayloadIncludeSelector
                {
                    Fields = { "vaultId", "documentId", "chunkId", "path", "fileHash", "chunkOrdinal", "schemaVersion", "embeddingProvider", "embeddingModel", "embeddingDimensions", "embeddingSchemaVersion", "documentChunkCount", "indexProfile" }
                }
            };
            var page = await client.ScrollAsync(collection, filter, limit: ScrollPageSize, offset: offset,
                payloadSelector: payloadSelector, vectorsSelector: false, cancellationToken: cancellationToken);

        foreach (var point in page.Result)
            {
                var payload = point.Payload;
                entries.Add(new IndexedChunkMetadata(
                    Guid.TryParse(point.Id.Uuid, out var pointId) ? pointId.ToString("D") : point.Id.Uuid,
                    String(payload, "vaultId"),
                    String(payload, "documentId"),
                    String(payload, "chunkId"),
                    String(payload, "path"),
                    String(payload, "fileHash"),
                    Integer(payload, "chunkOrdinal"),
                    Integer(payload, "schemaVersion"),
                    String(payload, "embeddingProvider"),
                    String(payload, "embeddingModel"),
                    Integer(payload, "embeddingDimensions"),
                    Integer(payload, "embeddingSchemaVersion"),
                    Integer(payload, "documentChunkCount"),
                    String(payload, "indexProfile")));
            }

            if (page.NextPageOffset is null) break;
            offset = page.NextPageOffset;
        }
        return entries;
    }

    public async Task DeleteDocumentAsync(string vaultId, string documentId, CancellationToken cancellationToken = default)
    {
        var filter = new Filter
        {
            Must =
            {
                new Condition { Field = new FieldCondition { Key = "vaultId", Match = new Match { Keyword = vaultId } } },
                new Condition { Field = new FieldCondition { Key = "documentId", Match = new Match { Keyword = documentId } } }
            }
        };
        await client.DeleteAsync(collection, filter, wait: true, cancellationToken: cancellationToken);
    }

    public async Task UpsertAsync(IReadOnlyList<KnowledgeIndexPoint> points, CancellationToken cancellationToken = default)
    {
        var pointIds = points.Select(point => (PointId)point.PointId).Distinct().ToArray();
        if (pointIds.Length != points.Count)
            throw new IndexCollectionException("Two chunks map to the same Qdrant point ID.");
        foreach (var existing in await client.RetrieveAsync(collection, pointIds, withPayload: true,
                     withVectors: false, cancellationToken: cancellationToken))
        {
            var actual = String(existing.Payload, "chunkId");
            var expected = points.Single(point => point.PointId == Guid.Parse(existing.Id.Uuid)).Chunk.ChunkId;
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new IndexCollectionException("A Qdrant point ID maps to a different canonical chunk ID; no points were overwritten.");
        }

        var batch = new List<PointStruct>();
        var batchBytes = 0;
        foreach (var item in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(item);
            var point = Map(item);
            var estimatedBytes = point.CalculateSize() + 64;
            if (batch.Count > 0 && (batch.Count >= UpsertBatchPoints || batchBytes + estimatedBytes > UpsertBatchBytes))
            {
                await client.UpsertAsync(collection, batch, wait: true, cancellationToken: cancellationToken);
                batch.Clear();
                batchBytes = 0;
            }
            batch.Add(point);
            batchBytes += estimatedBytes;
        }
        if (batch.Count > 0)
            await client.UpsertAsync(collection, batch, wait: true, cancellationToken: cancellationToken);
    }

    public static Guid ToPointId(string canonicalChunkId)
    {
        const string prefix = "sha256:";
        if (!canonicalChunkId.StartsWith(prefix, StringComparison.Ordinal) || canonicalChunkId.Length != prefix.Length + 64)
            throw new ArgumentException("Chunk ID is not a valid canonical SHA-256 identifier.", nameof(canonicalChunkId));
        var fullDigest = canonicalChunkId.AsSpan(prefix.Length, 64);
        var digest = fullDigest[..32];
        for (var i = 0; i < fullDigest.Length; i++)
            if (!Uri.IsHexDigit(fullDigest[i])) throw new ArgumentException("Chunk ID is not a valid canonical SHA-256 identifier.", nameof(canonicalChunkId));
        var text = $"{digest[..8]}-{digest.Slice(8, 4)}-{digest.Slice(12, 4)}-{digest.Slice(16, 4)}-{digest.Slice(20, 12)}";
        return Guid.ParseExact(text, "D");
    }

    private static PointStruct Map(KnowledgeIndexPoint item)
    {
        var chunk = item.Chunk;
        var payload = new Dictionary<string, Value>
        {
            ["schemaVersion"] = 1,
            ["vaultId"] = chunk.VaultId,
            ["documentId"] = chunk.DocumentId,
            ["chunkId"] = chunk.ChunkId,
            ["path"] = chunk.RelativePath,
            ["title"] = chunk.Title,
            ["headingPath"] = chunk.HeadingPath.ToArray(),
            ["tags"] = chunk.Tags.ToArray(),
            ["aliases"] = item.Aliases.ToArray(),
            ["links"] = chunk.Links.Select(link => (Value)new Dictionary<string, Value>
            {
                ["target"] = link.Target,
                ["displayText"] = link.DisplayText is null
                    ? new Value { NullValue = NullValue.NullValue }
                    : new Value { StringValue = link.DisplayText }
            }).ToArray(),
            ["chunkOrdinal"] = chunk.ChunkOrdinal,
            ["contentMarkdown"] = chunk.ContentMarkdown,
            ["contentNormalized"] = chunk.ContentNormalized,
            ["fileHash"] = chunk.FileHash,
            ["chunkHash"] = chunk.ChunkHash,
            ["modifiedUtc"] = chunk.ModifiedUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["embeddingProvider"] = item.Embedding.Provider,
            ["embeddingModel"] = item.Embedding.Model,
            ["embeddingDimensions"] = item.Embedding.Dimensions,
            ["embeddingSchemaVersion"] = 1,
            ["documentChunkCount"] = item.DocumentChunkCount,
            ["indexProfile"] = item.IndexProfile
        };
        var point = new PointStruct
        {
            Id = item.PointId,
            Vectors = item.Embedding.Vector.ToArray()
        };
        foreach (var pair in payload) point.Payload.Add(pair.Key, pair.Value);
        return point;
    }

    private static void Validate(KnowledgeIndexPoint item)
    {
        var vector = item.Embedding.Vector.Span;
        if (vector.Length != Dimensions || item.Embedding.Dimensions != Dimensions ||
            vector.ToArray().Any(value => !float.IsFinite(value)) ||
            string.IsNullOrWhiteSpace(item.Embedding.Provider) || string.IsNullOrWhiteSpace(item.Embedding.Model))
            throw new IndexCollectionException("Embedding output does not match the configured finite 384-dimensional vector contract.");
        if (item.Chunk.ChunkId != KnowledgeIdentity.ChunkId(item.Chunk.DocumentId, item.Chunk.HeadingPath, item.Chunk.ChunkOrdinal) ||
            item.PointId != ToPointId(item.Chunk.ChunkId) || item.DocumentChunkCount < 1 || string.IsNullOrWhiteSpace(item.IndexProfile))
            throw new IndexCollectionException("Chunk identity or indexing metadata is inconsistent.");
    }

    private static string String(IDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.HasStringValue ? value.StringValue : "";

    private static int Integer(IDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.HasIntegerValue && value.IntegerValue is >= int.MinValue and <= int.MaxValue
            ? (int)value.IntegerValue
            : -1;
}

public sealed class IndexCollectionException(string message) : Exception(message);
