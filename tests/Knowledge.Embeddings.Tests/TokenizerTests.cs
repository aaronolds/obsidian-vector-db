using Knowledge.Core;
using Knowledge.Embeddings;

namespace Knowledge.Embeddings.Tests;

public sealed class TokenizerTests
{
    private static readonly string Vocabulary = Path.Combine(AppContext.BaseDirectory, "vocab.txt");

    [Fact]
    public void Encodes_bert_reference_ids_and_special_tokens()
    {
        var tokenizer = new BertWordPieceTokenizer(Vocabulary);
        Assert.Equal([101, 7592, 1010, 2088, 999, 102], tokenizer.Encode("Hello, world!"));
        Assert.Equal(6, tokenizer.Count("Hello, world!"));
    }

    [Fact]
    public void Normalizes_accents_and_splits_unicode_and_punctuation()
    {
        var tokenizer = new BertWordPieceTokenizer(Vocabulary);
        Assert.Equal(tokenizer.Encode("cafe"), tokenizer.Encode("café"));
        Assert.Equal(4, tokenizer.Encode("東京").Length);
        Assert.True(tokenizer.Encode("X-Azure-FDID").Length > 2);
    }

    [Fact]
    public void Embedding_formatter_uses_the_canonical_context_layout()
    {
        Assert.Equal("Title: Title\nSection: A > B\nTags: one, two\n\nBody",
            EmbeddingTextFormatter.Format("Title", ["A", "B"], ["one", "two"], "Body"));
        Assert.Equal("Title: Title\n\nBody", EmbeddingTextFormatter.Format("Title", [], [], "Body"));
    }

    [Fact]
    public void Rejects_missing_or_unpinned_assets_before_inference()
    {
        var error = Assert.Throws<EmbeddingAssetException>(() =>
            new OnnxEmbeddingService(Path.Combine(AppContext.BaseDirectory, "missing.onnx"), Vocabulary));
        Assert.Contains("asset is missing", error.Message);
    }
}
