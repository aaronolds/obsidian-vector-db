# Milestone 2 implementation plan

Status: implemented and verified on 2026-10-20.

This plan expands Milestone 2 in [05-implementation-plan.md](05-implementation-plan.md).
The architecture, specification, canonical contracts, and accepted ADRs remain
authoritative. Implementation choices below resolve the artifact, tokenizer, and
normalization details left open in [08-open-questions-and-assumptions.md](08-open-questions-and-assumptions.md).
If a requirement proves infeasible, record the issue and evidence before proposing
an architecture change. No ADR changes are currently required.

## Outcome and scope

Given a configured synthetic sample vault and explicitly installed local model
assets, the CLI generates a valid embedding for every eligible chunk:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  embeddings --config knowledge.local.yaml
```

`embeddings` is a proposed diagnostic command for this milestone. It exercises
discovery, existing ingestion, canonical embedding-text construction, exact
tokenization, and local ONNX inference without requiring Qdrant.

Included:

- The architecture's `IEmbeddingService` and `EmbeddingResult` contracts.
- Reproducible installation of the selected model and tokenizer assets.
- Model-exact token IDs, masks, special tokens, and input-length enforcement.
- CPU ONNX inference, CLS pooling, L2 normalization, and vector validation.
- Shared tokenization for ingestion budgets and inference.
- Offline unit tests, an actual-model integration suite, and the CLI demonstration.

Deferred: Qdrant collection creation/writes, incremental indexing, search, query
formatting integration, retrieval benchmarks, MCP, batching, GPU acceleration,
quantization, watchers, and cloud providers. Preserve existing `chunks` and
`status` behavior; neither command should load an ONNX session.

## Starting point and implementation gaps

Milestone 1 provides canonical document/chunk records, `ITokenCounter`, ingestion,
and the `chunks` command. Configuration already restricts the POC to local ONNX,
`BAAI/bge-small-en-v1.5`, and 384 dimensions.

The existing code requires these focused changes:

- `BertWordPieceTokenCounter` stores vocabulary entries in a set and returns
  counts, not vocabulary-index token IDs. Its regex preprocessing does not prove
  equivalence to BERT normalization, special-token handling, or Unicode behavior.
- `ApproximateBertTokenCounter` counts words and punctuation. Despite its current
  description as conservative, it can underestimate WordPiece counts. It is only
  suitable for asset-free chunk inspection, never inference input validation.
- The canonical formatter is private to `MarkdownIngestor`. Counting and inference
  must use the same formatted text rather than separately maintained copies.
- Embedding configuration has a vocabulary path but no ONNX model path. No model
  artifacts, trusted token/vector fixtures, embedding test project, or committed
  synthetic sample vault currently exist.

Limit ingestion changes to those needed to share formatting/tokenization and
guarantee the model limit. Record unrelated Milestone 1 findings separately.

## Implementation sequence

### 1. Pin the local artifact and reference behavior

- Start with the model publisher's non-quantized `onnx/model.onnx` and tokenizer
  files from the same immutable Hugging Face repository revision. Inspect the
  graph before committing to its input/output mapping; do not infer pooling from
  an output tensor's name alone.
- Commit a small asset manifest recording the full repository commit, upstream
  file paths, SHA-256 digests, license/attribution, model identity, maximum input
  length, input/output names and types, pooling, and normalization. Include any
  external tensor-data files required by the graph. Pin compatible stable .NET
  runtime/tokenizer dependencies when implementing, without floating versions.
- Acquire `vocab.txt`, `tokenizer.json`, `tokenizer_config.json`,
  `special_tokens_map.json`, and relevant model/pooling configuration. Record
  which artifacts the runtime consumes and which establish reference behavior.
- Provide an explicit setup script or documented setup command that downloads
  only pinned public assets and verifies digests. Keep large weights out of Git
  through targeted ignore rules; retain the manifest and attribution.
- Generate committed reference fixtures using pinned Transformers/PyTorch or
  Sentence Transformers with the same revision, evaluation mode, CPU execution,
  and no truncation. Record package versions and a reproducible fixture generator.
  Fixtures contain synthetic text only and include complete token IDs and vectors.

Reference behavior is a 512-token total input window, 384 dimensions, the first
token's final hidden state (CLS pooling), and L2 normalization. Document chunks
receive no retrieval-query instruction. Query instructions remain a model-specific
formatting concern for Milestone 4; do not add a prefix to every `EmbedAsync` input.
These choices follow the publisher's [model guidance](https://huggingface.co/BAAI/bge-small-en-v1.5)
and [pooling configuration](https://huggingface.co/BAAI/bge-small-en-v1.5/blob/main/1_Pooling/config.json).

Acceptance: an immutable asset manifest and reference fixtures exist; the selected
ONNX graph can load locally and its tensor contract is documented. If its export
cannot reproduce reference behavior, document the discrepancy before selecting a
different export of the same approved model. Do not substitute another model.

### 2. Add contracts, shared formatting, and configuration

- Add the exact architecture contracts to `Knowledge.Core`:

  ```csharp
  public interface IEmbeddingService
  {
      Task<EmbeddingResult> EmbedAsync(
          string text,
          CancellationToken cancellationToken = default);
  }

  public sealed record EmbeddingResult(
      ReadOnlyMemory<float> Vector,
      string Provider,
      string Model,
      int Dimensions);
  ```

- Extract a pure canonical embedding-text formatter into Core. Reuse it in the
  chunker's budget calculations and the new CLI pipeline. Preserve the existing
  title/optional-section/optional-tags/blank-line/content layout and chunk contracts.
- Keep ONNX tensors, tokenizer types, and application options outside Core. Pass
  focused constructor settings into the embedding library; Indexer owns binding,
  dependency registration, and service disposal.
- Add `embedding.modelPath`, resolved relative to the selected configuration file,
  with `KNOWLEDGE_EMBEDDING__MODELPATH` support. Retain `tokenizerPath` as the local
  vocabulary path and document the companion assets required by the pinned profile.
- Require compatible, readable model/tokenizer assets for `embeddings`. Preserve
  asset-free `status` and approximate `chunks` inspection when no vocabulary is
  configured. A configured vocabulary must use the exact tokenizer or fail clearly.
- Validate configured identity/dimensions and asset digests against the pinned
  profile before inference. Do not expose alternate pooling or normalization modes
  that would silently create an incompatible index.
- Record the effective model revision, tokenizer, document formatter, pooling,
  normalization, and eventual query-formatting rule as the embedding schema profile.
  Keep `EmbeddingResult` unchanged; persistence of `embeddingSchemaVersion` belongs
  to Milestone 3. Incompatible profile changes require a rebuild.

Proposed local configuration additions:

```yaml
embedding:
  provider: onnx
  model: BAAI/bge-small-en-v1.5
  dimensions: 384
  modelPath: ./models/bge-small-en-v1.5/onnx/model.onnx
  tokenizerPath: ./models/bge-small-en-v1.5/vocab.txt
```

Acceptance: Core remains dependency-free; formatter output remains identical;
configuration tests cover relative paths, environment overrides, incompatible
assets, and commands that do not require weights.

### 3. Implement one exact tokenizer for counts and inference

- Evaluate `Microsoft.ML.Tokenizers.BertTokenizer` against the pinned reference
  fixtures. Use it only after parity is demonstrated; record any library mismatch
  and resolve it inside `Knowledge.Embeddings` without changing the domain boundary.
- Replace or delegate the existing counter to the same tokenizer implementation
  used by inference. Preserve vocabulary line order for token IDs and validate
  required special tokens and compatibility with the model embedding table.
- Match reference lowercasing, accent handling, punctuation, whitespace/control
  cleanup, Chinese character handling, unknown tokens, and WordPiece rules.
  Test literal special-token text, Unicode combining characters, emoji, long
  identifiers, code, and technical punctuation as well as ordinary English.
- Produce `input_ids`, `attention_mask`, and `token_type_ids` where the pinned
  graph requires them. Include `[CLS]` and `[SEP]` exactly once; use `[PAD]` and
  correct attention masks if the selected graph requires padding. Single-sequence
  token-type IDs are zero. Use the graph's declared tensor element types.
- Disable silent truncation. Reject any final input above 512 tokens before
  inference; the budget includes metadata and special tokens. Test inputs of
  exactly 512 and 513 tokens and a body that fits until context is added.
- Feed the exact counter into `MarkdownIngestor` for `embeddings`. Add a final check
  of every emitted chunk's canonical input and correct only token-budget defects
  exposed by exact tokenization. Oversized metadata must fail visibly rather than
  dropping context or truncating content.

Acceptance: token-ID sequences match the reference exactly, count equals encoded
length under the same special-token policy, all sample chunks fit, and oversized
inputs are rejected without calling the inference runner.

### 4. Implement the local ONNX embedding service

- Add the CPU `Microsoft.ML.OnnxRuntime` dependency only to `Knowledge.Embeddings`.
  Validate native runtime loading on the development workstation and record its
  OS/architecture; do not claim untested platform support.
- Implement `OnnxEmbeddingService : IEmbeddingService` using one reusable inference
  session per service lifetime. Start with sequential, batch-size-one execution;
  batching and throughput tuning are outside this milestone.
- At startup validate the pinned graph's required input names, ranks/types, output
  contract, and hidden dimension. Check dynamic dimensions at execution time.
  Reject an unsupported graph instead of guessing an alternate tensor mapping.
- For the selected feature-extraction graph, take the CLS vector from final hidden
  states with expected shape `[1, sequenceLength, 384]`. Do not substitute mean
  pooling or the BERT pooler output. If the graph embeds postprocessing, validate
  its semantics explicitly so pooling/normalization is applied correctly once.
- L2-normalize in the provider. Reject incorrect lengths, NaN/infinite values, and
  zero/invalid norms. Return `Provider = "onnx"`, the approved model identity,
  `Dimensions = 384`, and an owned float buffer independent of disposed outputs.
- Dispose per-call input/output values, run options, and cancellation registrations;
  dispose the session when its owner shuts down. Honor cancellation before
  tokenization and inference and use the runtime's termination mechanism where
  supported. Verify the service remains usable after a cancelled request.
- Reject blank input with a safe validation error. Convert model/runtime failures
  to stable diagnostics without embedding text or arbitrary exception payloads.
  Keep native verbose/profiling output disabled by default. No runtime downloads,
  cloud fallback, or network requests are permitted.

Acceptance: the real model produces finite, unit-length 384-dimensional vectors
matching the reference fixtures, repeated calls meet the documented tolerance,
and results remain valid after call resources are disposed.

### 5. Connect the CLI vertical slice

- Add `embeddings [--config <path>]` and help text. Compose the exact tokenizer,
  ingestion pipeline, shared formatter, and `IEmbeddingService` without creating
  a Qdrant client or health probe.
- Process chunks in ordinal relative-path order, then chunk ordinal. Emit one
  camel-case NDJSON diagnostic envelope per chunk with `documentId`, `chunkId`,
  `relativePath`, `chunkOrdinal`, `tokenCount`, `provider`, `model`, `dimensions`,
  and `vector`. This envelope is diagnostic output, not a new canonical chunk or
  Qdrant payload contract. Serialize the vector as a JSON number array.
- Vector output is explicitly requested by this command; operational logs remain
  metadata-only. Do not echo note bodies or constructed input text. Send summaries,
  counts, timings, safe error codes, and affected identifiers/paths to stderr.
- Complete embeddings for a document before emitting its envelopes so a failed
  document cannot appear partially successful. Continue other documents after a
  document-specific failure when safe; stop on unusable global model/runtime state.
- Return `0` for a complete run, including an empty eligible vault with valid
  assets; `1` for incomplete ingestion/inference; `2` for usage/configuration/asset
  setup failures; `130` for cancellation. Distinguish model-read errors from
  configuration-file errors. Never report partial work as successful.
- Preserve existing `chunks` NDJSON and `status` output. Add no `index`, `search`,
  `rebuild`, or MCP implementations in this milestone.

Acceptance: a synthetic vault produces one valid vector per eligible chunk with
Qdrant stopped and networking disabled. Excluded and opted-out files produce no
envelopes. Failure and cancellation behavior is covered by CLI tests.

### 6. Complete validation and operating documentation

- Add `Knowledge.Embeddings.Tests` to the solution for tokenizer, input-limit,
  pooling, normalization, ownership, and error tests. Keep formatter tests small;
  extend existing ingestion/CLI/configuration tests for this integration.
- Commit the small pinned vocabulary/configuration assets needed by tokenizer
  tests with attribution and matching manifest digests, so exact-tokenizer unit
  tests run offline without installing model weights.
- Put real-model checks in a separate `Knowledge.Embeddings.IntegrationTests`
  project so its execution is explicit. Normal tests need no weights or network;
  running the integration project requires installed pinned assets and fails with
  a setup diagnostic if they are absent. A skipped model run is not milestone
  completion. Keep test fixtures and reference outputs small and synthetic.
- Cover the matrix below, including actual inference on every sample chunk.
  Use a narrow inference test seam only where necessary to prove oversized input
  never reaches ONNX or to exercise failures the valid model cannot produce.
- Provide a small committed sample vault/configuration covering prose, headings,
  tags, code, a table, Unicode, oversized prose splitting, opt-out, and an empty note.
  Compare emitted IDs with the exact-tokenizer `chunks` run using the same config.
- Update README, embedding-library README, configuration reference, sample YAML,
  and setup instructions after implementation passes. Correct claims that the
  approximate counter guarantees model fit, and document explicit vector output.
  Mark Milestone 2 implemented only after the real-model checks succeed.

## Verification matrix and completion gates

| Area | Required evidence |
|---|---|
| Tokenization | Exact reference token IDs, masks/types, special tokens, Unicode and technical-text fixtures; count equals encoding length |
| Hard limit | 512 tokens accepted; 513 rejected before inference; context and special tokens included; no truncation |
| Output validity | Exactly 384 finite floats, correct provider/model/dimensions, nonzero norm, L2 norm within `1e-5` of 1 |
| Repeatability | Repeated identical input on the pinned CPU runtime differs by at most `1e-5` per component |
| Reference parity | Complete vectors against trusted fixtures: initial target maximum component error `1e-4` and cosine similarity at least `0.99999` |
| Ingestion integration | Every actual sample chunk's formatted input fits and embeds; title/section/tags use the same formatter during counting and inference |
| Lifecycle and failure | Cancellation, invalid graph/assets, invalid output, safe diagnostics, owned result buffers, disposal, and successful subsequent calls |
| CLI compatibility | One envelope per successful chunk, stable ordering, correct exit codes, no partial document output, existing commands preserved |
| Local execution | Setup is explicit; tests/runtime have no automatic downloads or cloud fallback; embedding command works without Qdrant |

The floating-point thresholds are proposed FP32 CPU acceptance values. Record
observed errors with the pinned reference/runtime and platform. Investigate a
failure before changing tolerances; document evidence for any justified adjustment.
Equivalent floating-point vectors are required, not byte-identical NDJSON floats
across platforms. Reference parity establishes implementation correctness, not
retrieval quality or the Milestone 6 benchmark.

Proposed implementation validation commands (after adding projects and assets):

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test tests/Knowledge.Indexer.Tests/Knowledge.Indexer.Tests.csproj --no-build --no-restore
dotnet test tests/Knowledge.Embeddings.Tests/Knowledge.Embeddings.Tests.csproj --no-build --no-restore
dotnet restore tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj
dotnet test tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj --no-build --no-restore
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- \
  chunks --config knowledge.local.yaml > chunks.ndjson
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- \
  embeddings --config knowledge.local.yaml > embeddings.ndjson
```

The local config must select the committed sample vault and installed pinned
assets. Run the embedding command twice and compare identifiers, counts, and
vectors within the repeatability tolerance. Record dependency versions, artifact
revision/digests, platform, commands, and results in the implementation record.
Completion requires all four Milestone 2 validations and its CLI exit criterion;
passing mocked tests or merely producing 384 numbers is insufficient.

## Dependency references

These sources guide implementation; repository architecture remains authoritative.
The asset manifest must replace moving model links with immutable revisions.

- [Model publisher's ONNX artifacts](https://huggingface.co/BAAI/bge-small-en-v1.5/tree/main/onnx).
- [Model tokenizer settings](https://huggingface.co/BAAI/bge-small-en-v1.5/blob/main/tokenizer_config.json).
- [ONNX Runtime C# usage and resource management](https://onnxruntime.ai/docs/get-started/with-csharp.html).
- [Microsoft tokenizer implementation](https://github.com/dotnet/machinelearning/tree/main/src/Microsoft.ML.Tokenizers).

## Implementation record

- Runtime: .NET SDK 10.0.401, macOS arm64; `Microsoft.ML.OnnxRuntime` 1.29.0.
- Model: upstream revision `5c38ec7c405ec4b44b94cc5a9bb96e735b38267a`; the
  checked-in asset manifest records all digests. The 133 MB ONNX file is installed
  locally with `scripts/setup-bge-model.sh` and excluded from Git.
- Reference fixture: Transformers 4.57.1, PyTorch 2.8.0, Tokenizers 0.22.2 on
  CPU; regenerated with `scripts/generate-bge-reference.py` and the pinned
  `model.safetensors` checkpoint. The real-model test passes exact token-ID parity,
  maximum component error `<= 1e-4`, and cosine similarity `>= 0.99999`.
- `dotnet build Knowledge.sln --no-restore -m:1` succeeds with no warnings.
- The asset-free solution suite passes 68 tests. The explicitly invoked real-model
  integration project passes 5 tests, for 73 total across all three test projects;
  it covers all 10 sample chunks, the 512/513-token boundary, repeated vectors,
  cancellation, and reference parity.
- The `chunks` sample emits 10 chunks from 5 eligible documents and skips one
  opted-out note. The `embeddings` sample emits one 384-value vector per chunk
  with no Qdrant service. Vectors are finite, L2-normalized, and within the 512
  token window.

Reference checks found that `Microsoft.ML.Tokenizers` 2.0.1 dropped newlines and
some ASCII symbols such as `=` for the selected BERT settings. Those differences
changed token IDs and vectors, so the package was not retained. `Knowledge.Embeddings`
now implements the BERT basic tokenizer and greedy WordPiece directly against the
pinned vocabulary; the three synthetic English, Unicode, and technical-text
fixtures pass exact token-ID parity and the vector tolerances above. This is a
documented implementation-level library choice; the approved model, ONNX runtime,
service contract, and architecture boundaries are unchanged.
