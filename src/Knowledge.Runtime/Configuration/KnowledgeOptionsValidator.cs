using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knowledge.Runtime.Configuration;

public sealed class KnowledgeOptionsValidator(bool validateEmbeddingAssets = true) : IValidateOptions<KnowledgeOptions>
{
    // Total BGE input window; context and special tokens also consume tokens.
    // The tokenizer will enforce the final input length in Milestone 2.
    private const int ModelTokenLimit = 512;

    public ValidateOptionsResult Validate(string? name, KnowledgeOptions options)
    {
        List<string> errors = [];
        Require(!string.IsNullOrWhiteSpace(options.Vault.Id), "vault.id is required.");
        Require(!string.IsNullOrWhiteSpace(options.Vault.Path), "vault.path is required.");
        Require(options.Index.Include.Length > 0 && options.Index.Include.All(ValidPattern),
            "index.include must contain at least one non-empty relative glob.");
        Require(options.Index.Exclude.All(ValidPattern), "index.exclude must contain only non-empty relative globs.");
        Require(!string.IsNullOrWhiteSpace(options.Index.FrontmatterOptOutField), "index.frontmatterOptOutField is required.");
        Require(options.Chunking.TargetTokens > 0, "chunking.targetTokens must be positive.");
        Require(options.Chunking.SoftMaxTokens >= options.Chunking.TargetTokens,
            "chunking.softMaxTokens must be at least targetTokens.");
        Require(options.Chunking.SoftMaxTokens < ModelTokenLimit,
            "chunking.softMaxTokens must be below the model's 512-token input window to leave room for context and special tokens.");
        Require(options.Chunking.OverlapTokens >= 0 && options.Chunking.OverlapTokens < options.Chunking.TargetTokens,
            "chunking.overlapTokens must be non-negative and less than targetTokens.");
        Require(options.Chunking.OverlapOnlyWhenSplit, "chunking.overlapOnlyWhenSplit must be true for the POC (ADR-004).");
        Require(options.Embedding.Provider == "onnx", "embedding.provider must be onnx for local inference (ADR-003/008).");
        Require(options.Embedding.Model == "BAAI/bge-small-en-v1.5", "embedding.model must be BAAI/bge-small-en-v1.5 for the POC (ADR-003).");
        Require(options.Embedding.Dimensions == 384, "embedding.dimensions must be 384 for the POC model (ADR-003).");
        if (validateEmbeddingAssets)
        {
            Require(string.IsNullOrWhiteSpace(options.Embedding.TokenizerPath) || File.Exists(options.Embedding.TokenizerPath),
                "embedding.tokenizerPath must point to an existing local vocabulary file when configured.");
            Require(string.IsNullOrWhiteSpace(options.Embedding.ModelPath) || File.Exists(options.Embedding.ModelPath),
                "embedding.modelPath must point to an existing local ONNX file when configured.");
        }
        Require(IsLocalHost(options.Qdrant.Host), "qdrant.host must be localhost or a loopback IP address for the local POC (ADR-008).");
        Require(options.Qdrant.Port is > 0 and <= 65535, "qdrant.port must be between 1 and 65535 (gRPC, normally 6334).");
        Require(!string.IsNullOrWhiteSpace(options.Qdrant.Collection) && options.Qdrant.Collection.All(
            c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            "qdrant.collection must contain only ASCII letters, digits, hyphens, or underscores.");
        Require(options.Retrieval.DefaultLimit > 0 && options.Retrieval.MaxLimit >= options.Retrieval.DefaultLimit,
            "retrieval limits must be positive and maxLimit must be at least defaultLimit.");
        Require(options.Retrieval.Mode == "dense", "retrieval.mode must be dense for the POC (ADR-010).");
        Require(Enum.TryParse<LogLevel>(options.Logging.Level, true, out var level) &&
                Enum.IsDefined(level) && !int.TryParse(options.Logging.Level, out _),
            "logging.level must be a named .NET log level (Trace through None).");
        Require(!options.Logging.LogContent, "logging.logContent must be false; content logging is not supported.");
        Require(!options.Logging.LogQueries, "logging.logQueries must be false; query logging is not supported.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

        void Require(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }
    }

    private static bool IsLocalHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static bool ValidPattern(string pattern) =>
        !string.IsNullOrWhiteSpace(pattern) && !pattern.Contains('\\') &&
        !pattern.StartsWith('/') && !pattern.Contains(':') && !pattern.Split('/').Contains("..");
}
