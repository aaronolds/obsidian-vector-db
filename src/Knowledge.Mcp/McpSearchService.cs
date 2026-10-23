using Knowledge.Core;
using Knowledge.Runtime;
using Knowledge.Runtime.Configuration;

namespace Knowledge.Mcp;

/// <summary>Creates and retains the read-only retrieval runtime after the first valid search.</summary>
public sealed class McpSearchService(KnowledgeOptions options) : IDisposable
{
    private readonly object _gate = new();
    private KnowledgeSearchSession? _session;
    private bool _disposed;

    public int DefaultLimit => options.Retrieval.DefaultLimit;
    public int MaxLimit => options.Retrieval.MaxLimit;

    public Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(KnowledgeSearchRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = KnowledgeSearchRequestValidator.Normalize(request, options.Retrieval.MaxLimit);
        cancellationToken.ThrowIfCancellationRequested();
        var retriever = GetRetriever();
        return retriever.SearchAsync(normalized, cancellationToken);
    }

    private IKnowledgeRetriever GetRetriever()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _session ??= KnowledgeSearchSession.Create(options);
            return _session.Retriever;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
        }
    }
}
