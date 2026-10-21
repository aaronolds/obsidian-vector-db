using System.Text.RegularExpressions;
using Knowledge.Core;

namespace Knowledge.Embeddings;

/// <summary>
/// Counts BERT WordPiece tokens from a local vocabulary. The vocabulary must be
/// the tokenizer asset shipped for the selected embedding model.
/// </summary>
public sealed class BertWordPieceTokenCounter : ITokenCounter
{
    private static readonly Regex BasicTokens = new(@"[\p{L}\p{N}]+|[^\s\p{L}\p{N}]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly HashSet<string> _vocabulary;
    private readonly bool _lowerCase;

    public BertWordPieceTokenCounter(string vocabularyPath, bool lowerCase = true)
    {
        if (!File.Exists(vocabularyPath)) throw new FileNotFoundException("Tokenizer vocabulary was not found.", vocabularyPath);
        _vocabulary = File.ReadLines(vocabularyPath).ToHashSet(StringComparer.Ordinal);
        if (_vocabulary.Count == 0) throw new InvalidDataException("Tokenizer vocabulary is empty.");
        _lowerCase = lowerCase;
    }

    public int Count(string text, bool includeSpecialTokens = true)
    {
        var count = 0;
        foreach (Match match in BasicTokens.Matches(text))
        {
            var token = _lowerCase ? match.Value.ToLowerInvariant() : match.Value;
            count += CountWordPiece(token);
        }
        return count + (includeSpecialTokens ? 2 : 0);
    }

    private int CountWordPiece(string token)
    {
        if (_vocabulary.Contains(token)) return 1;
        var start = 0;
        var pieces = 0;
        while (start < token.Length)
        {
            var end = token.Length;
            string? found = null;
            while (end > start)
            {
                var candidate = (start == 0 ? "" : "##") + token[start..end];
                if (_vocabulary.Contains(candidate)) { found = candidate; break; }
                end--;
            }
            if (found is null) return 1;
            pieces++;
            start = end;
        }
        return pieces;
    }
}
