# Knowledge.Embeddings

Milestone 2 provides `BertWordPieceTokenizer` and `OnnxEmbeddingService` for the
pinned local `BAAI/bge-small-en-v1.5` model. The service uses CPU ONNX Runtime,
CLS pooling, and L2 normalization to return finite 384-dimensional vectors. It
validates the model and tokenizer digests before loading inference and rejects
inputs beyond the model's 512-token window.

The vocabulary, tokenizer configuration, model configuration, and pooling
configuration are pinned and committed under `models/bge-small-en-v1.5/`. The
133 MB ONNX weight file is excluded from Git. Install it explicitly with
`scripts/setup-bge-model.sh`; no runtime download or cloud fallback exists.

`ApproximateBertTokenCounter` remains available to the asset-free `chunks`
inspection command. It is an estimate and cannot guarantee model-window fit.

Milestone 4 adds `BgeQueryEmbeddingService`. It applies the pinned English
passage-retrieval instruction only to queries, checks the fully formatted input
against the 512-token limit, and delegates inference to `IEmbeddingService`.
Document embedding text and `OnnxEmbeddingService.EmbedAsync` remain unchanged.
