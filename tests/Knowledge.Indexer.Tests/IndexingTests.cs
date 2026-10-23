using Knowledge.Core;
using Knowledge.Embeddings;
using Knowledge.Runtime.Configuration;
using Knowledge.Indexer.Indexing;
using Knowledge.Runtime.Indexing;
using Knowledge.Markdown;
using Knowledge.Qdrant;

namespace Knowledge.Indexer.Tests;

public sealed class IndexingTests
{
    [Fact]
    public async Task Unchanged_second_run_does_not_generate_embeddings_or_write_points()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nSemantic content.");
        var (coordinator, embeddings, store) = Create(vault);

        var first = await coordinator.IndexAsync();
        var second = await coordinator.IndexAsync();

        Assert.Equal(1, first.Indexed);
        Assert.Equal(1, first.EmbeddingCalls);
        Assert.Equal(0, second.Indexed);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, second.EmbeddingCalls);
        Assert.Equal(0, embeddings.Calls - first.EmbeddingCalls);
        Assert.Equal(1, store.UpsertCalls);
    }

    [Fact]
    public async Task Changed_note_replaces_its_points_and_stale_notes_are_removed()
    {
        using var vault = new TemporaryVault();
        vault.Write("A.md", "# A\nOriginal text.");
        vault.Write("B.md", "# B\nOther text.");
        var (coordinator, embeddings, store) = Create(vault);
        await coordinator.IndexAsync();
        var oldA = store.Points.Values.Single(point => point.RelativePath == "A.md").ChunkId;

        vault.Write("A.md", "# A\nChanged text.");
        File.Delete(Path.Combine(vault.Path, "B.md"));
        var result = await coordinator.IndexAsync();

        Assert.Equal(1, result.Indexed);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(1, result.StaleDeleted);
        Assert.Equal(1, result.EmbeddingCalls);
        Assert.Equal(3, embeddings.Calls);
        Assert.DoesNotContain(store.Points.Values, point => point.ChunkId == oldA && point.RelativePath == "B.md");
        Assert.All(store.Points.Values, point => Assert.Equal("A.md", point.RelativePath));
    }

    [Fact]
    public async Task Incomplete_document_is_reembedded_on_the_next_run()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nSemantic content.");
        var (coordinator, embeddings, store) = Create(vault);
        await coordinator.IndexAsync();
        store.Points.Clear();

        var result = await coordinator.IndexAsync();

        Assert.Equal(1, result.Indexed);
        Assert.Equal(1, result.EmbeddingCalls);
        Assert.Equal(2, embeddings.Calls);
    }

    [Fact]
    public async Task Timestamp_only_change_is_unchanged_but_same_timestamp_byte_change_is_detected()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nOriginal content.");
        var (coordinator, embeddings, _) = Create(vault);
        await coordinator.IndexAsync();
        var path = Path.Combine(vault.Path, "Note.md");
        var originalTime = File.GetLastWriteTimeUtc(path);

        File.SetLastWriteTimeUtc(path, originalTime.AddMinutes(2));
        var timestampOnly = await coordinator.IndexAsync();
        Assert.Equal(1, timestampOnly.Unchanged);
        Assert.Equal(0, timestampOnly.EmbeddingCalls);

        File.WriteAllText(path, "# Note\nChanged content.");
        File.SetLastWriteTimeUtc(path, originalTime);
        var changedBytes = await coordinator.IndexAsync();
        Assert.Equal(1, changedBytes.Indexed);
        Assert.Equal(1, changedBytes.EmbeddingCalls);
        Assert.Equal(2, embeddings.Calls);
    }

    [Fact]
    public async Task Populated_document_changed_to_empty_removes_old_points()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nContent to index.");
        var (coordinator, embeddings, store) = Create(vault);
        await coordinator.IndexAsync();
        Assert.Single(store.Points);

        vault.Write("Note.md", "---\ntags: []\n---\n");
        var result = await coordinator.IndexAsync();

        Assert.Equal(1, result.Empty);
        Assert.Equal(0, result.EmbeddingCalls);
        Assert.Empty(store.Points);
        Assert.Equal(1, embeddings.Calls);
    }

    [Fact]
    public async Task Incompatible_profile_fails_before_document_mutations()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nContent.");
        var (coordinator, _, store) = Create(vault);
        await coordinator.IndexAsync();
        var upserts = store.UpsertCalls;
        var point = Assert.Single(store.Points);
        store.Points[point.Key] = point.Value with { IndexProfile = "sha256:unknown" };

        await Assert.ThrowsAsync<IndexCollectionException>(() => coordinator.IndexAsync());
        Assert.Equal(upserts, store.UpsertCalls);
        Assert.Single(store.Points);
    }

    [Fact]
    public async Task Milestone_3_embedding_schema_requires_a_fresh_collection()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nContent.");
        var (coordinator, _, store) = Create(vault);
        await coordinator.IndexAsync();
        var upserts = store.UpsertCalls;
        var point = Assert.Single(store.Points);
        store.Points[point.Key] = point.Value with { EmbeddingSchemaVersion = 1 };

        var error = await Assert.ThrowsAsync<IndexCollectionException>(() => coordinator.IndexAsync());

        Assert.Contains("fresh collection", error.Message);
        Assert.Equal(upserts, store.UpsertCalls);
        Assert.Single(store.Points);
    }

    [Fact]
    public async Task Failed_document_embedding_is_retried_without_a_false_noop()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nOriginal.");
        var (coordinator, embeddings, store) = Create(vault);
        await coordinator.IndexAsync();
        vault.Write("Note.md", "# Note\nChanged.");
        embeddings.FailNext = true;

        var failed = await coordinator.IndexAsync();
        Assert.Equal(1, failed.Failed);
        Assert.Empty(store.Points);

        var retried = await coordinator.IndexAsync();
        Assert.Equal(1, retried.Indexed);
        Assert.Equal(1, retried.EmbeddingCalls);
    }

    [Fact]
    public async Task Missing_vault_does_not_delete_existing_points()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nSemantic content.");
        var (coordinator, _, store) = Create(vault);
        await coordinator.IndexAsync();
        var existing = store.Points.Count;
        Directory.Delete(vault.Path, true);

        var result = await coordinator.IndexAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(existing, store.Points.Count);
    }

    [Fact]
    public async Task Rebuild_recreates_collection_and_reembeds_unchanged_sources()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nSemantic content.");
        var (coordinator, embeddings, store) = Create(vault);
        await coordinator.IndexAsync();
        var existingPointIds = store.Points.Keys.ToArray();

        var result = await coordinator.RebuildAsync();

        Assert.True(result.IsComplete);
        Assert.Equal(1, store.RecreateCalls);
        Assert.Equal(1, result.Indexed);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(1, result.EmbeddingCalls);
        Assert.Equal(2, embeddings.Calls);
        Assert.Equal(existingPointIds, store.Points.Keys);
    }

    [Fact]
    public async Task Rebuild_with_missing_vault_preserves_existing_collection()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nSemantic content.");
        var (coordinator, _, store) = Create(vault);
        await coordinator.IndexAsync();
        var existingCount = store.Points.Count;
        Directory.Delete(vault.Path, true);

        var result = await coordinator.RebuildAsync();

        Assert.False(result.IsComplete);
        Assert.Equal(0, store.RecreateCalls);
        Assert.Equal(existingCount, store.Points.Count);
    }

    [Fact]
    public async Task Rebuild_of_empty_vault_leaves_an_empty_recreated_collection()
    {
        using var vault = new TemporaryVault();
        var (coordinator, _, store) = Create(vault);

        var result = await coordinator.RebuildAsync();

        Assert.True(result.IsComplete);
        Assert.Equal(1, store.RecreateCalls);
        Assert.Empty(store.Points);
    }

    [Fact]
    public async Task Cancelled_rebuild_does_not_recreate_collection()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nSemantic content.");
        var (coordinator, _, store) = Create(vault);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RebuildAsync(cancellation.Token));

        Assert.Equal(0, store.RecreateCalls);
    }

    [Fact]
    public async Task Incomplete_discovery_from_broken_markdown_link_preserves_collection()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nSemantic content.");
        var (coordinator, _, store) = Create(vault);
        await coordinator.IndexAsync();
        var existingCount = store.Points.Count;
        File.CreateSymbolicLink(Path.Combine(vault.Path, "Unreadable.md"), Path.Combine(vault.Path, "missing-target.md"));

        var result = await coordinator.RebuildAsync();

        Assert.False(result.IsComplete);
        Assert.Equal(0, store.RecreateCalls);
        Assert.Equal(existingCount, store.Points.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "read-failed");
    }

    [Fact]
    public async Task Rebuild_recovers_an_incompatible_profile_and_failed_rebuild_is_repaired_by_index()
    {
        using var vault = new TemporaryVault();
        vault.Write("Note.md", "# Note\nOriginal.");
        var (coordinator, embeddings, store) = Create(vault);
        await coordinator.IndexAsync();
        var indexed = store.Points.Single();
        store.Points[indexed.Key] = indexed.Value with { IndexProfile = "sha256:incompatible" };

        embeddings.FailNext = true;
        var partial = await coordinator.RebuildAsync();
        Assert.False(partial.IsComplete);
        Assert.Empty(store.Points);

        var recovered = await coordinator.IndexAsync();
        Assert.True(recovered.IsComplete);
        Assert.Equal(1, recovered.Indexed);
        Assert.Single(store.Points);
    }

    [Fact]
    public void Profile_is_deterministic_and_changes_with_chunking_rules()
    {
        using var vault = new TemporaryVault();
        var options = Options(vault);
        var input = IngestionOptions(options);

        Assert.Equal(IndexProfile.Create(input, options.Embedding), IndexProfile.Create(input, options.Embedding));
        var changed = input with { TargetTokens = input.TargetTokens + 1 };
        Assert.NotEqual(IndexProfile.Create(input, options.Embedding), IndexProfile.Create(changed, options.Embedding));
        Assert.Equal(2, KnowledgeIndexSchema.EmbeddingSchemaVersion);
        Assert.Equal("bge-en-passage-retrieval-instruction-v1", KnowledgeIndexSchema.QueryFormatting);
    }

    [Fact]
    public void Point_id_uses_the_canonical_digest_prefix_without_platform_byte_order()
    {
        var id = QdrantKnowledgeIndexStore.ToPointId("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        Assert.Equal(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), id);
        Assert.Throws<ArgumentException>(() => QdrantKnowledgeIndexStore.ToPointId("sha256:not-a-digest"));
    }

    private static (IndexCoordinator Coordinator, CountingEmbeddingService Embeddings, MemoryIndexStore Store) Create(TemporaryVault vault)
    {
        var options = Options(vault);
        var embeddings = new CountingEmbeddingService();
        var store = new MemoryIndexStore();
        return (new IndexCoordinator(new MarkdownIngestor(new ApproximateBertTokenCounter()), store, embeddings, options), embeddings, store);
    }

    private static KnowledgeOptions Options(TemporaryVault vault) => new()
    {
        Vault = new VaultOptions { Id = "primary", Path = vault.Path },
        Index = new IndexOptions { Include = ["**/*.md"], Exclude = ["Templates/**"], FrontmatterOptOutField = "ai-index" },
        Chunking = new ChunkingOptions { TargetTokens = 300, SoftMaxTokens = 400, OverlapTokens = 40 },
        Embedding = new EmbeddingOptions { Provider = "onnx", Model = "BAAI/bge-small-en-v1.5", Dimensions = 384 }
    };

    private static MarkdownIngestionOptions IngestionOptions(KnowledgeOptions options) => new(
        options.Vault.Id, options.Vault.Path, options.Index.Include, options.Index.Exclude,
        options.Index.FrontmatterOptOutField, options.Chunking.TargetTokens,
        options.Chunking.SoftMaxTokens, options.Chunking.OverlapTokens);

    private sealed class CountingEmbeddingService : IEmbeddingService
    {
        public int Calls { get; private set; }
        public bool FailNext { get; set; }
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Synthetic embedding failure.");
            }
            var vector = new float[384];
            vector[0] = 1;
            return Task.FromResult(new EmbeddingResult(vector, "onnx", "BAAI/bge-small-en-v1.5", 384));
        }
    }

    private sealed class MemoryIndexStore : IKnowledgeIndexStore, IKnowledgeCollectionLifecycle
    {
        public Dictionary<Guid, IndexedChunkMetadata> Points { get; } = [];
        public int UpsertCalls { get; private set; }
        public int RecreateCalls { get; private set; }
        public Task EnsureCollectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecreateCollectionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecreateCalls++;
            Points.Clear();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<IndexedChunkMetadata>> GetIndexedChunksAsync(string vaultId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IndexedChunkMetadata>>(Points.Values.Where(point => point.VaultId == vaultId).ToArray());

        public Task DeleteDocumentAsync(string vaultId, string documentId, CancellationToken cancellationToken = default)
        {
            foreach (var key in Points.Where(pair => pair.Value.VaultId == vaultId && pair.Value.DocumentId == documentId).Select(pair => pair.Key).ToArray())
                Points.Remove(key);
            return Task.CompletedTask;
        }

        public Task UpsertAsync(IReadOnlyList<KnowledgeIndexPoint> points, CancellationToken cancellationToken = default)
        {
            UpsertCalls++;
            foreach (var item in points)
                Points[item.PointId] = new IndexedChunkMetadata(item.PointId.ToString(), item.Chunk.VaultId,
                    item.Chunk.DocumentId, item.Chunk.ChunkId, item.Chunk.RelativePath, item.Chunk.FileHash,
                    item.Chunk.ChunkOrdinal, KnowledgeIndexSchema.SchemaVersion, item.Embedding.Provider, item.Embedding.Model,
                    item.Embedding.Dimensions, KnowledgeIndexSchema.EmbeddingSchemaVersion, item.DocumentChunkCount, item.IndexProfile);
            return Task.CompletedTask;
        }
    }
}
