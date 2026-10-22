using System.Globalization;
using System.Text;
using Knowledge.Core;

namespace Knowledge.Embeddings;

/// <summary>Local BERT BasicTokenizer and greedy WordPiece encoder for the pinned BGE vocabulary.</summary>
public sealed class BertWordPieceTokenizer : ITokenCounter
{
    private static readonly string[] SpecialTokens = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]"];
    private readonly string[] _vocabulary;
    private readonly Dictionary<string, int> _tokenIds;

    public BertWordPieceTokenizer(string vocabularyPath)
    {
        if (!File.Exists(vocabularyPath))
            throw new FileNotFoundException("Tokenizer vocabulary was not found.", vocabularyPath);

        _vocabulary = File.ReadAllLines(vocabularyPath);
        if (_vocabulary.Length != BgeAssetManifest.VocabularySize)
            throw new InvalidDataException("Tokenizer vocabulary size does not match the pinned BGE model.");
        _tokenIds = new Dictionary<string, int>(_vocabulary.Length, StringComparer.Ordinal);
        for (var i = 0; i < _vocabulary.Length; i++)
            if (!_tokenIds.TryAdd(_vocabulary[i], i))
                throw new InvalidDataException("Tokenizer vocabulary contains duplicate entries.");

        foreach (var special in SpecialTokens)
            if (!_tokenIds.ContainsKey(special))
                throw new InvalidDataException("Tokenizer vocabulary is missing required BERT special tokens.");
    }

    public int Count(string text, bool includeSpecialTokens = true) => Encode(text, includeSpecialTokens).Length;

    public int[] Encode(string text, bool addSpecialTokens = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<int> output = [];
        if (addSpecialTokens) output.Add(Id("[CLS]"));

        var cursor = 0;
        while (cursor < text.Length)
        {
            var (index, token) = FindNextSpecialToken(text, cursor);
            var end = index < 0 ? text.Length : index;
            EncodeBasicText(text.AsSpan(cursor, end - cursor), output);
            if (index < 0) break;
            output.Add(Id(token!));
            cursor = index + token!.Length;
        }

        if (addSpecialTokens) output.Add(Id("[SEP]"));
        return output.ToArray();
    }

    public int PaddingTokenId => Id("[PAD]");

    private int Id(string token) => _tokenIds[token];

    private (int Index, string? Token) FindNextSpecialToken(string text, int start)
    {
        var bestIndex = -1;
        string? bestToken = null;
        foreach (var token in SpecialTokens)
        {
            var index = text.IndexOf(token, start, StringComparison.Ordinal);
            if (index >= 0 && (bestIndex < 0 || index < bestIndex || index == bestIndex && token.Length > bestToken!.Length))
            {
                bestIndex = index;
                bestToken = token;
            }
        }
        return (bestIndex, bestToken);
    }

    private void EncodeBasicText(ReadOnlySpan<char> text, List<int> output)
    {
        List<string> words = [];
        StringBuilder word = new();
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsInvalidOrControl(rune)) continue;
            if (IsWhitespace(rune))
            {
                FlushWord();
                continue;
            }
            if (IsChinese(rune))
            {
                FlushWord();
                AddPunctuationToken(rune);
                continue;
            }

            var normalized = rune.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            foreach (var normalizedRune in normalized.EnumerateRunes())
            {
                if (Rune.GetUnicodeCategory(normalizedRune) == UnicodeCategory.NonSpacingMark) continue;
                if (IsPunctuation(normalizedRune))
                {
                    FlushWord();
                    AddPunctuationToken(normalizedRune);
                }
                else
                {
                    word.Append(normalizedRune.ToString());
                }
            }
        }
        FlushWord();
        foreach (var token in words) EncodeWordPiece(token, output);

        void FlushWord()
        {
            if (word.Length == 0) return;
            words.Add(word.ToString());
            word.Clear();
        }

        void AddPunctuationToken(Rune rune) => words.Add(rune.ToString());
    }

    private void EncodeWordPiece(string word, List<int> output)
    {
        var runes = word.EnumerateRunes().ToArray();
        if (runes.Length > 100)
        {
            output.Add(Id("[UNK]"));
            return;
        }

        var start = 0;
        List<int> pieces = [];
        while (start < runes.Length)
        {
            var foundEnd = -1;
            var foundId = -1;
            for (var end = runes.Length; end > start; end--)
            {
                var candidate = string.Concat(runes.AsSpan(start, end - start).ToArray().Select(x => x.ToString()));
                if (start > 0) candidate = "##" + candidate;
                if (!_tokenIds.TryGetValue(candidate, out foundId)) continue;
                foundEnd = end;
                break;
            }

            if (foundEnd < 0)
            {
                output.Add(Id("[UNK]"));
                return;
            }
            pieces.Add(foundId);
            start = foundEnd;
        }
        output.AddRange(pieces);
    }

    private static bool IsInvalidOrControl(Rune rune)
    {
        var value = rune.Value;
        if (value is 0 or 0xFFFD) return true;
        if (value is '\t' or '\n' or '\r') return false;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format;
    }

    private static bool IsWhitespace(Rune rune) =>
        rune.Value is '\t' or '\n' or '\r' || Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator;

    private static bool IsPunctuation(Rune rune)
    {
        if (rune.Value is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~') return true;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    private static bool IsChinese(Rune rune) => rune.Value is >= 0x4E00 and <= 0x9FFF
        or >= 0x3400 and <= 0x4DBF
        or >= 0xF900 and <= 0xFAFF
        or >= 0x20000 and <= 0x2A6DF
        or >= 0x2A700 and <= 0x2B73F
        or >= 0x2B740 and <= 0x2B81F
        or >= 0x2B820 and <= 0x2CEAF
        or >= 0x2F800 and <= 0x2FA1F;
}
