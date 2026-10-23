using Knowledge.Core;
using Knowledge.Embeddings;
using Knowledge.Markdown;
using Knowledge.Qdrant;
using Knowledge.Runtime.Configuration;
using Knowledge.Runtime.Indexing;
using Qdrant.Client;

namespace Knowledge.Runtime;

/// <summary>Owns the local inference and Qdrant resources used by dense retrieval.</summary>
public sealed class KnowledgeSearchSession : IDisposable
{
    private readonly OnnxEmbeddingService _embeddings;
    private readonly QdrantClient _client;
    private bool _disposed;

    private KnowledgeSearchSession(OnnxEmbeddingService embeddings, QdrantClient client,
        IKnowledgeRetriever retriever)
    {
        _embeddings = embeddings;
        _client = client;
        Retriever = retriever;
    }

    public IKnowledgeRetriever Retriever { get; }
    public QdrantClient Client => _client;

    public static KnowledgeSearchSession Create(KnowledgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Embedding.ModelPath) ||
            string.IsNullOrWhiteSpace(options.Embedding.TokenizerPath))
            throw new EmbeddingAssetException("Search requires local model and tokenizer paths for the pinned BGE model.");

        OnnxEmbeddingService? embeddings = null;
        QdrantClient? client = null;
        try
        {
            var tokenizer = new BertWordPieceTokenizer(options.Embedding.TokenizerPath);
            embeddings = new OnnxEmbeddingService(options.Embedding.ModelPath, options.Embedding.TokenizerPath);
            client = new QdrantClient(options.Qdrant.Host, options.Qdrant.Port,
                grpcTimeout: TimeSpan.FromSeconds(10));
            var retriever = CreateRetriever(options, client, new BgeQueryEmbeddingService(embeddings, tokenizer));
            return new KnowledgeSearchSession(embeddings, client, retriever);
        }
        catch
        {
            client?.Dispose();
            embeddings?.Dispose();
            throw;
        }
    }

    public static IKnowledgeRetriever CreateRetriever(KnowledgeOptions options, QdrantClient client,
        IQueryEmbeddingService queryEmbeddings)
    {
        var ingestion = CreateIngestionOptions(options);
        var profile = IndexProfile.Create(ingestion, options.Embedding);
        return new QdrantKnowledgeRetriever(client, options.Qdrant.Collection, options.Vault.Id,
            profile, queryEmbeddings, options.Retrieval.MaxLimit);
    }

    public static MarkdownIngestionOptions CreateIngestionOptions(KnowledgeOptions options) => new(
        options.Vault.Id, options.Vault.Path, options.Index.Include, options.Index.Exclude,
        options.Index.FrontmatterOptOutField, options.Chunking.TargetTokens, options.Chunking.SoftMaxTokens,
        options.Chunking.OverlapTokens);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        _embeddings.Dispose();
    }
}
