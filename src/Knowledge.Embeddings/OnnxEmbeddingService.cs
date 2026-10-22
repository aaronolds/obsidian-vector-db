using System.Security.Cryptography;
using Knowledge.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Knowledge.Embeddings;

public sealed class EmbeddingAssetException(string message) : Exception(message);

/// <summary>Local BGE-small ONNX feature extraction with CLS pooling and L2 normalization.</summary>
public sealed class OnnxEmbeddingService : IEmbeddingService, ITokenCounter, IDisposable
{
    public const string ProviderName = "onnx";
    public const string ModelName = "BAAI/bge-small-en-v1.5";
    public const int Dimensions = 384;
    public const int MaximumTokenCount = 512;

    private readonly BertWordPieceTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly object _runLock = new();
    private bool _disposed;

    public OnnxEmbeddingService(string modelPath, string vocabularyPath)
    {
        ValidateAsset(modelPath, BgeAssetManifest.ModelPath, BgeAssetManifest.ModelSha256, "ONNX model");
        var assetRoot = Directory.GetParent(Path.GetFullPath(modelPath))?.Parent?.FullName
            ?? throw new EmbeddingAssetException("The configured model path does not use the pinned BGE asset layout.");
        ValidateAsset(vocabularyPath, BgeAssetManifest.VocabularyPath, BgeAssetManifest.VocabularySha256, "Tokenizer vocabulary");
        ValidateAsset(Path.Combine(assetRoot, "tokenizer.json"), "tokenizer.json", BgeAssetManifest.TokenizerJsonSha256, "Tokenizer configuration");
        ValidateAsset(Path.Combine(assetRoot, "tokenizer_config.json"), "tokenizer_config.json", BgeAssetManifest.TokenizerConfigSha256, "Tokenizer options");
        ValidateAsset(Path.Combine(assetRoot, "special_tokens_map.json"), "special_tokens_map.json", BgeAssetManifest.SpecialTokensMapSha256, "Special-token configuration");
        ValidateAsset(Path.Combine(assetRoot, "config.json"), "config.json", BgeAssetManifest.ModelConfigSha256, "Model configuration");
        ValidateAsset(Path.Combine(assetRoot, "1_Pooling", "config.json"), "1_Pooling/config.json", BgeAssetManifest.PoolingConfigSha256, "Pooling configuration");

        _tokenizer = new BertWordPieceTokenizer(vocabularyPath);
        try
        {
            OrtEnv.Instance().DisableTelemetryEvents();
            var session = new InferenceSession(modelPath, new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = (OrtLoggingLevel)3
            });
            try { ValidateGraph(session); }
            catch { session.Dispose(); throw; }
            _session = session;
        }
        catch (Exception ex) when (ex is not EmbeddingAssetException)
        {
            throw new EmbeddingAssetException("The configured ONNX model could not be loaded or does not match the supported BGE graph.");
        }
    }

    public int CountTokens(string text) => _tokenizer.Count(text);
    public int Count(string text, bool includeSpecialTokens = true) => _tokenizer.Count(text, includeSpecialTokens);

    public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Embedding input must not be blank.", nameof(text));

        var ids = _tokenizer.Encode(text);
        if (ids.Length > MaximumTokenCount)
            throw new ArgumentOutOfRangeException(nameof(text), "Formatted embedding input exceeds the 512-token model limit.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] vector;
            lock (_runLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                vector = Run(ids, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EmbeddingResult(vector, ProviderName, ModelName, Dimensions));
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private float[] Run(int[] ids, CancellationToken cancellationToken)
    {
        var longIds = Array.ConvertAll(ids, static id => (long)id);
        var shape = new[] { 1, ids.Length };
        var attention = Enumerable.Repeat(1L, ids.Length).ToArray();
        var tokenTypes = new long[ids.Length];
        var idsValue = NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(longIds, shape));
        var attentionValue = NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attention, shape));
        var tokenTypesValue = NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypes, shape));
        var inputs = new[] { idsValue, attentionValue, tokenTypesValue };

        using var runOptions = new RunOptions();
        using var cancellation = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);
        using var outputs = _session.Run(inputs, _session.OutputNames, runOptions);
        var output = outputs.FirstOrDefault(x => x.Name == "last_hidden_state") ?? outputs.FirstOrDefault()
            ?? throw new InvalidDataException("The ONNX graph returned no hidden-state output.");
        var tensor = output.AsTensor<float>();
        var dimensions = tensor.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[0] != 1 || dimensions[1] != ids.Length || dimensions[2] != Dimensions)
            throw new InvalidDataException("The ONNX graph returned an unsupported hidden-state shape.");

        var hidden = tensor.ToArray();
        var vector = hidden.AsSpan(0, Dimensions).ToArray(); // CLS is token position zero.
        double squaredNorm = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) throw new InvalidDataException("The ONNX graph returned a non-finite embedding value.");
            squaredNorm += (double)value * value;
        }
        if (!double.IsFinite(squaredNorm) || squaredNorm <= 0)
            throw new InvalidDataException("The ONNX graph returned an invalid zero-length embedding.");

        var norm = Math.Sqrt(squaredNorm);
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        if (vector.Length != Dimensions || vector.Any(static value => !float.IsFinite(value)))
            throw new InvalidDataException("The ONNX graph returned an invalid embedding vector.");
        return vector;
    }

    private static void ValidateGraph(InferenceSession session)
    {
        var required = new[] { "input_ids", "attention_mask", "token_type_ids" };
        if (required.Any(name => !session.InputMetadata.ContainsKey(name)))
            throw new EmbeddingAssetException("The ONNX graph is missing a required BERT input.");
        if (session.InputMetadata.Count != required.Length)
            throw new EmbeddingAssetException("The ONNX graph exposes an unsupported input contract.");
        if (!session.OutputMetadata.Values.Any(x => x.ElementType == typeof(float) && x.Dimensions.Length == 3))
            throw new EmbeddingAssetException("The ONNX graph has no rank-three float hidden-state output.");

        foreach (var name in required)
        {
            var metadata = session.InputMetadata[name];
            if (metadata.ElementType != typeof(long) || metadata.Dimensions.Length != 2)
                throw new EmbeddingAssetException("The ONNX graph has an unsupported BERT input type or rank.");
        }
    }

    private static void ValidateAsset(string path, string expectedRelativePath, string expectedSha256, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new EmbeddingAssetException($"{label} asset is missing. Run scripts/setup-bge-model.sh and configure its local path.");
        using var stream = File.OpenRead(path);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new EmbeddingAssetException($"{label} asset digest does not match the pinned {expectedRelativePath} artifact.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_runLock)
        {
            if (_disposed) return;
            _session.Dispose();
            _disposed = true;
        }
    }
}
