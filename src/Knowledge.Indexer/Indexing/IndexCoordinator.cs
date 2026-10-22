using System.Text;
using Knowledge.Core;
using Knowledge.Indexer.Configuration;
using Knowledge.Markdown;
using Knowledge.Qdrant;
using Grpc.Core;

namespace Knowledge.Indexer.Indexing;

public sealed record IndexingSummary(
    int Scanned,
    int Indexed,
    int Unchanged,
    int OptedOut,
    int Empty,
    int StaleDeleted,
    int Failed,
    int ChunksWritten,
    int ChunksDeleted,
    int EmbeddingCalls,
    TimeSpan EmbeddingDuration,
    TimeSpan QdrantDuration,
    IReadOnlyList<IngestionDiagnostic> Diagnostics)
{
    public bool IsComplete => Failed == 0;
}

public sealed class IndexCoordinator(
    MarkdownIngestor ingestor,
    IKnowledgeIndexStore store,
    IEmbeddingService embeddings,
    KnowledgeOptions options)
{
    public async Task<IndexingSummary> IndexAsync(CancellationToken cancellationToken = default)
    {
        var settings = new MarkdownIngestionOptions(options.Vault.Id, options.Vault.Path,
            options.Index.Include, options.Index.Exclude, options.Index.FrontmatterOptOutField,
            options.Chunking.TargetTokens, options.Chunking.SoftMaxTokens, options.Chunking.OverlapTokens);
        var scan = ingestor.Discover(settings, cancellationToken);
        var diagnostics = scan.Diagnostics.ToList();
        if (!scan.IsComplete)
            return Summary(scan.Files.Count, 0, 0, 0, 0, 0, 1, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, diagnostics);

        var qdrantDuration = TimeSpan.Zero;
        var qdrantWatch = System.Diagnostics.Stopwatch.StartNew();
        await store.EnsureCollectionAsync(cancellationToken);
        var existing = await store.GetIndexedChunksAsync(options.Vault.Id, cancellationToken);
        qdrantWatch.Stop();
        qdrantDuration += qdrantWatch.Elapsed;
        var profile = IndexProfile.Create(settings, options.Embedding);
        if (!IsCompatible(existing, profile, out var reason))
            throw new IndexCollectionException(reason);

        var byDocument = existing.GroupBy(entry => entry.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var discoveredPaths = scan.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        var indexed = 0;
        var unchanged = 0;
        var optedOut = 0;
        var empty = 0;
        var staleDeleted = 0;
        var failed = 0;
        var chunksWritten = 0;
        var chunksDeleted = 0;
        var embeddingCalls = 0;
        var embeddingDuration = TimeSpan.Zero;
        var qdrantFailed = false;

        foreach (var file in scan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceSnapshot snapshot;
            try { snapshot = ingestor.ReadSnapshot(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                diagnostics.Add(new IngestionDiagnostic(file.RelativePath, "read-failed", "The note could not be read; its indexed data was retained."));
                continue;
            }

            var documentId = KnowledgeIdentity.DocumentId(options.Vault.Id, file.RelativePath);
            var previous = byDocument.GetValueOrDefault(documentId, []);
            if (IsCompleteDocument(previous, snapshot.FileHash, profile))
            {
                unchanged++;
                continue;
            }

            qdrantWatch.Restart();
            await store.DeleteDocumentAsync(options.Vault.Id, documentId, cancellationToken);
            qdrantWatch.Stop();
            qdrantDuration += qdrantWatch.Elapsed;
            chunksDeleted += previous.Length;

            DocumentIngestionResult document;
            try { document = ingestor.IngestDocument(snapshot, settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or FormatException or YamlDotNet.Core.YamlException)
            {
                failed++;
                diagnostics.Add(new IngestionDiagnostic(file.RelativePath, "parse-failed", "The changed note could not be parsed; its previous chunks were removed."));
                continue;
            }

            if (document.Status == DocumentIngestionStatus.OptedOut)
            {
                optedOut++;
                continue;
            }
            if (document.Status == DocumentIngestionStatus.Empty)
            {
                empty++;
                continue;
            }

            var points = new List<KnowledgeIndexPoint>(document.Chunks.Count);
            try
            {
                foreach (var chunk in document.Chunks.OrderBy(item => item.ChunkOrdinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = EmbeddingTextFormatter.Format(chunk.Title, chunk.HeadingPath, chunk.Tags, chunk.ContentNormalized);
                    embeddingCalls++;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    EmbeddingResult embedding;
                    try { embedding = await embeddings.EmbedAsync(text, cancellationToken); }
                    finally { timer.Stop(); embeddingDuration += timer.Elapsed; }
                    ValidateEmbedding(embedding, chunk, options.Embedding);
                    points.Add(new KnowledgeIndexPoint(chunk, document.Document.Aliases, embedding,
                        Knowledge.Qdrant.QdrantKnowledgeIndexStore.ToPointId(chunk.ChunkId), document.Chunks.Count, profile));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                failed++;
                diagnostics.Add(new IngestionDiagnostic(file.RelativePath, "embedding-failed", "The changed note could not be fully embedded; its indexed data was removed."));
                continue;
            }

            try
            {
                qdrantWatch.Restart();
                await store.UpsertAsync(points, cancellationToken);
                var afterWrite = await store.GetIndexedChunksAsync(options.Vault.Id, cancellationToken);
                qdrantWatch.Stop();
                qdrantDuration += qdrantWatch.Elapsed;
                var actual = afterWrite.Where(entry => entry.DocumentId == documentId).ToArray();
                if (!IsCompleteDocument(actual, snapshot.FileHash, profile) || actual.Length != points.Count)
                    throw new IndexCollectionException("Qdrant did not retain a complete document write.");
                indexed++;
                chunksWritten += points.Count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (RpcException ex)
            {
                qdrantWatch.Stop();
                qdrantDuration += qdrantWatch.Elapsed;
                failed++;
                qdrantFailed = true;
                diagnostics.Add(new IngestionDiagnostic(file.RelativePath, $"qdrant-{ex.StatusCode.ToString().ToLowerInvariant()}", "Qdrant did not complete the document write; the remaining index was left untouched."));
                break;
            }
            catch (Exception)
            {
                qdrantWatch.Stop();
                qdrantDuration += qdrantWatch.Elapsed;
                failed++;
                diagnostics.Add(new IngestionDiagnostic(file.RelativePath, "qdrant-write-failed", "The document write could not be confirmed and will be retried on the next run."));
            }
        }

        // Candidate discovery was complete. Keep path identities whose reads or parses failed,
        // and remove only documents whose source path is now absent or excluded.
        if (!qdrantFailed)
        {
            foreach (var group in byDocument.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = group[0];
                if (discoveredPaths.Contains(entry.RelativePath)) continue;
                qdrantWatch.Restart();
                await store.DeleteDocumentAsync(options.Vault.Id, entry.DocumentId, cancellationToken);
                qdrantWatch.Stop();
                qdrantDuration += qdrantWatch.Elapsed;
                staleDeleted++;
                chunksDeleted += group.Length;
            }
        }

        return Summary(scan.Files.Count, indexed, unchanged, optedOut, empty, staleDeleted,
            failed, chunksWritten, chunksDeleted, embeddingCalls, embeddingDuration, qdrantDuration, diagnostics);
    }

    private bool IsCompatible(IReadOnlyList<IndexedChunkMetadata> chunks, string profile, out string reason)
    {
        foreach (var chunk in chunks)
        {
            if (chunk.SchemaVersion != KnowledgeIndexSchema.SchemaVersion ||
                chunk.EmbeddingSchemaVersion != KnowledgeIndexSchema.EmbeddingSchemaVersion ||
                chunk.EmbeddingProvider != KnowledgeIndexSchema.EmbeddingProvider ||
                chunk.EmbeddingModel != KnowledgeIndexSchema.EmbeddingModel ||
                chunk.EmbeddingDimensions != options.Embedding.Dimensions)
            {
                reason = "Indexed payload uses an incompatible schema or embedding model; select a fresh collection.";
                return false;
            }
            if (chunk.IndexProfile != profile)
            {
                reason = "Indexed payload uses an incompatible indexing profile; select a fresh collection.";
                return false;
            }
            if (chunk.VaultId != options.Vault.Id || string.IsNullOrWhiteSpace(chunk.DocumentId) ||
                string.IsNullOrWhiteSpace(chunk.RelativePath) || string.IsNullOrWhiteSpace(chunk.ChunkId) ||
                chunk.DocumentId != KnowledgeIdentity.DocumentId(chunk.VaultId, chunk.RelativePath))
            {
                reason = "Indexed payload is missing required identity fields; select a fresh collection.";
                return false;
            }
        }
        reason = "";
        return true;
    }

    public static bool IsCompleteDocument(IReadOnlyList<IndexedChunkMetadata> chunks, string fileHash, string profile)
    {
        if (chunks.Count == 0) return false;
        var expectedCount = chunks[0].DocumentChunkCount;
        if (expectedCount < 1 || chunks.Count != expectedCount) return false;
        if (chunks.Any(item => item.FileHash != fileHash || item.IndexProfile != profile || item.DocumentChunkCount != expectedCount)) return false;
        var ordinals = chunks.Select(item => item.ChunkOrdinal).Order().ToArray();
        if (!ordinals.SequenceEqual(Enumerable.Range(0, expectedCount)) ||
            chunks.Select(item => item.ChunkId).Distinct(StringComparer.Ordinal).Count() != expectedCount ||
            chunks.Select(item => item.PointId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedCount ||
            chunks.Select(item => item.DocumentId).Distinct(StringComparer.Ordinal).Count() != 1 ||
            chunks.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() != 1)
            return false;
        try
        {
            return chunks.All(item => Guid.TryParse(item.PointId, out var pointId) &&
                                      pointId == Knowledge.Qdrant.QdrantKnowledgeIndexStore.ToPointId(item.ChunkId));
        }
        catch (ArgumentException) { return false; }
    }

    private static void ValidateEmbedding(EmbeddingResult embedding, KnowledgeChunk chunk, EmbeddingOptions options)
    {
        if (embedding.Provider != options.Provider || embedding.Model != options.Model ||
            embedding.Dimensions != options.Dimensions || embedding.Vector.Length != options.Dimensions ||
            embedding.Vector.Span.ToArray().Any(value => !float.IsFinite(value)))
            throw new IndexCollectionException($"Embedding output for {chunk.ChunkId} does not match the configured model contract.");
    }

    private static IndexingSummary Summary(int scanned, int indexed, int unchanged, int optedOut, int empty,
        int staleDeleted, int failed, int written, int deleted, int calls, TimeSpan embedding, TimeSpan qdrant,
        IReadOnlyList<IngestionDiagnostic> diagnostics) =>
        new(scanned, indexed, unchanged, optedOut, empty, staleDeleted, failed, written, deleted, calls, embedding, qdrant, diagnostics);
}
