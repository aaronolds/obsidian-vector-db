namespace Knowledge.Indexer.Configuration;

public sealed class KnowledgeOptions
{
    public VaultOptions Vault { get; set; } = new();
    public IndexOptions Index { get; set; } = new();
    public ChunkingOptions Chunking { get; set; } = new();
    public EmbeddingOptions Embedding { get; set; } = new();
    public QdrantOptions Qdrant { get; set; } = new();
    public RetrievalOptions Retrieval { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
}

public sealed class VaultOptions
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class IndexOptions
{
    public string[] Include { get; set; } = [];
    public string[] Exclude { get; set; } = [];
    public string FrontmatterOptOutField { get; set; } = "ai-index";
}

public sealed class ChunkingOptions
{
    public int TargetTokens { get; set; } = 300;
    public int SoftMaxTokens { get; set; } = 400;
    public int OverlapTokens { get; set; } = 40;
    public bool OverlapOnlyWhenSplit { get; set; } = true;
}

public sealed class EmbeddingOptions
{
    public string Provider { get; set; } = "onnx";
    public string Model { get; set; } = "BAAI/bge-small-en-v1.5";
    public int Dimensions { get; set; } = 384;
    public string ModelPath { get; set; } = "";
    public string TokenizerPath { get; set; } = "";
}

public sealed class QdrantOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6334;
    public string Collection { get; set; } = "obsidian-knowledge";
}

public sealed class RetrievalOptions
{
    public int DefaultLimit { get; set; } = 5;
    public int MaxLimit { get; set; } = 50;
    public string Mode { get; set; } = "dense";
}

public sealed class LoggingOptions
{
    public string Level { get; set; } = "Information";
    public bool LogContent { get; set; }
    public bool LogQueries { get; set; }
}
