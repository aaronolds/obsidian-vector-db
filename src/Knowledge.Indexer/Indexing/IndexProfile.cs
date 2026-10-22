using System.Security.Cryptography;
using System.Text.Json;
using Knowledge.Core;
using Knowledge.Embeddings;
using Knowledge.Indexer.Configuration;
using Knowledge.Markdown;

namespace Knowledge.Indexer.Indexing;

public static class IndexProfile
{
    public static string Create(MarkdownIngestionOptions ingestion, EmbeddingOptions embedding)
    {
        var profile = new
        {
            profileVersion = 1,
            schemaVersion = 1,
            embeddingSchemaVersion = 1,
            parser = "knowledge-markdown-v1",
            normalizer = "knowledge-markdown-normalizer-v1",
            chunker = "knowledge-structure-chunker-v1",
            formatter = "knowledge-embedding-text-v1",
            pointMapping = "sha256-prefix-uuid-v1",
            ingestion.TargetTokens,
            ingestion.SoftMaxTokens,
            ingestion.OverlapTokens,
            ingestion.ModelTokenLimit,
            overlapOnlyWhenSplit = true,
            ingestion.FrontmatterOptOutField,
            provider = embedding.Provider,
            model = embedding.Model,
            dimensions = embedding.Dimensions,
            modelRevision = BgeAssetManifest.Revision,
            modelDigest = BgeAssetManifest.ModelSha256,
            tokenizerDigest = BgeAssetManifest.VocabularySha256,
            tokenizerConfigDigest = BgeAssetManifest.TokenizerConfigSha256,
            modelConfigDigest = BgeAssetManifest.ModelConfigSha256,
            poolingConfigDigest = BgeAssetManifest.PoolingConfigSha256,
            pooling = "cls",
            normalization = "l2",
            queryFormatting = "deferred-to-milestone-4"
        };
        return "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile))).ToLowerInvariant();
    }
}
