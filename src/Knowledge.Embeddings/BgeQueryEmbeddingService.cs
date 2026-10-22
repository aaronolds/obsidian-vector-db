using Knowledge.Core;

namespace Knowledge.Embeddings;

/// <summary>Applies the BGE English passage-retrieval instruction to queries only.</summary>
public sealed class BgeQueryEmbeddingService(IEmbeddingService embeddings, ITokenCounter tokenCounter)
    : IQueryEmbeddingService
{
    public const string InstructionPrefix = "Represent this sentence for searching relevant passages: ";

    public void ValidateQuery(string query) => _ = FormatQuery(query, tokenCounter);

    public async Task<EmbeddingResult> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        var formattedQuery = FormatQuery(query, tokenCounter);
        cancellationToken.ThrowIfCancellationRequested();
        return await embeddings.EmbedAsync(formattedQuery, cancellationToken).ConfigureAwait(false);
    }

    public static string FormatQuery(string query, ITokenCounter tokenCounter)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search query must not be blank.", nameof(query));
        var formattedQuery = InstructionPrefix + query.Trim();
        if (tokenCounter.Count(formattedQuery) > OnnxEmbeddingService.MaximumTokenCount)
            throw new ArgumentOutOfRangeException(nameof(query), "Formatted search query exceeds the 512-token model limit.");
        return formattedQuery;
    }
}
