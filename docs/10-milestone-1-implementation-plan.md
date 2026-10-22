# Milestone 1 implementation plan

Status: implemented in the working tree; this document remains the implementation
and verification record for Milestone 1.

This plan expands Milestone 1 in [05-implementation-plan.md](05-implementation-plan.md).
The architecture, specification, canonical contracts, and accepted ADRs remain
authoritative. The choices below fill implementation gaps without changing those
decisions. If implementation exposes an infeasible requirement, record the issue
and its evidence before proposing an architecture change.

## Outcome and scope

Given a configured sample vault, the CLI prints deterministic chunk objects
without generating embeddings or contacting Qdrant:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- chunks --config knowledge.yaml
```

`chunks` is a proposed inspection command for this milestone. `index`, `rebuild`,
`search`, and MCP remain deferred. Existing `status` behavior remains supported.

Included:

- Recursive Markdown discovery, include/exclude rules, and frontmatter opt-out.
- Markdown structure, YAML metadata, title resolution, and wikilink extraction.
- Original Markdown preservation, semantic normalization, and embedding-text formatting.
- Heading-aware, token-budgeted chunking with deterministic provenance.
- Automated fixtures and a CLI demonstration requiring no running services.

Excluded:

- Embedding inference, ONNX model loading, vectors, or cloud calls.
- Collection creation, Qdrant writes, incremental synchronization, and stale deletion.
- Retrieval, benchmarks, MCP hosting, watchers, graph traversal, and attachments.

## Starting point

Milestone 0 provides the .NET 10 solution, configuration loading and validation,
metadata-only logging, the status command, and configuration/CLI tests.
`Knowledge.Core` and `Knowledge.Markdown` are shells. The Indexer currently
references Core and Qdrant, but not Markdown.

Keep application options in the Indexer composition root. Pass focused ingestion
settings into libraries rather than making libraries depend on Indexer options.
Keep Core independent of parser, tokenizer, filesystem-globbing, and Qdrant DTOs.

## Implementation sequence

### 1. Establish contracts and resolve token sizing

- Add the canonical `VaultDescriptor`, `KnowledgeDocument`, `WikiLink`, and
  `KnowledgeChunk` records from [03-domain-and-data-contracts.md](03-domain-and-data-contracts.md).
- Introduce only the small service boundaries needed by the ingestion pipeline;
  do not add unused retrieval, storage, or embedding interfaces.
- Add pure identity/hash helpers needed to populate complete chunk records:
  document ID, chunk ID, raw-byte file hash, and chunk hash. Follow the exact
  newline-delimited identity formulas in the canonical contracts. Define one
  unambiguous serialization for embedding-affecting chunk metadata and test it.
- Use model token counts, including the final title/section/tags context and
  special tokens. Character or whitespace estimates cannot enforce the hard cap.

**Sequencing dependency:** the architecture requires token-aware chunking, while
Milestone 2 and the Milestone 0 notes currently place tokenizer setup there.
Bring forward only the token-counting prerequisite needed by this milestone;
Milestone 2 retains inference, pooling, normalization, and vector validation.
Record this sequencing clarification in the implementation notes rather than
claiming that approximate sizing satisfies ADR-004.

Use a small token-counting interface in Core with a local BGE-compatible adapter
in `Knowledge.Embeddings`, reusable by Milestone 2. Evaluate
`Microsoft.ML.Tokenizers.BertTokenizer` against pinned BGE tokenizer artifacts
and committed reference token-ID/count cases before choosing it. The adapter
must match model normalization, WordPiece behavior, and special-token accounting.
No ONNX runtime or model weights are needed for counting.

Document a local tokenizer-asset setup step, revision/checksums, license, and asset
path configuration. Do not download assets during ingestion. The repository's
sample-vault inspection command may use its deterministic approximate counter
when no vocabulary is configured; it can underestimate tokens and does not
guarantee the model limit. A configured vocabulary must use the pinned local
WordPiece adapter and incompatible assets fail clearly. Milestone 2 validates
model-exact tokenization before embedding-backed indexing.

Acceptance: canonical models compile; identity/hash tests pass; token counts
match the pinned reference cases, including identifiers, Unicode, and punctuation.

### 2. Discover eligible source files

- Implement recursive discovery with the configured include globs, then exclude
  globs; excludes win. Cover root-level and nested `.md` files.
- Normalize vault-relative paths to `/`, remove redundant `.` segments, and retain
  observed filename casing. Sort using ordinal path ordering.
- Enforce containment against resolved filesystem paths. Support internal
  symlinks, reject external targets, detect directory cycles, and define stable
  handling of repeated traversal. Keep logical vault-relative paths for identity.
- Read each file once into a source snapshot containing raw bytes, decoded text,
  relative path, and filesystem modification time. Hash the original bytes;
  never substitute the current clock for `ModifiedUtc`.
- Validate vault readability. Report inaccessible files/directories as incomplete
  ingestion rather than silently dropping them.

Acceptance: scanner tests cover default excludes, custom globs, exclude precedence,
path casing, root containment, symlinks, and stable enumeration order.

### 3. Parse Markdown and selective frontmatter

- Use an AST parser with source locations and explicit table support; Markdig is
  the proposed parser. Preserve source slices instead of regenerating all Markdown
  from rendered HTML or an AST serializer.
- Reuse YamlDotNet for a leading YAML frontmatter block. Extract `title`, `tags`,
  `aliases`, and the configured opt-out field. Support scalar and sequence forms
  for tags/aliases with explicit, tested normalization and deduplication rules.
- Apply `ai-index: false` before chunk creation. Malformed or ambiguous control
  metadata fails the document; it must not become ordinary indexed body text.
- Resolve title as frontmatter title, then first H1, then filename without `.md`.
- Track heading hierarchy across skipped levels and repeated headings; heading
  syntax inside code fences does not create sections. Support ATX and Setext headings.
- Recognize paragraphs, lists, tables, blockquotes, fenced/indented code, and inline code.
- Extract wikilink targets and optional aliases outside code, using visible text
  for normalization. Preserve heading/block fragments without resolving links.
- Implement required support for frontmatter tags; notes without tags remain
  eligible. Defer inline hashtag extraction under the documented optional V1
  policy, avoiding false positives in headings and code.

Acceptance: metadata and structure tests cover title precedence, heading paths,
aliases, frontmatter tags, opt-out, wikilink aliases, and malformed frontmatter.
Tolerant Markdown recovery is allowed where the parser produces valid structure;
actual failures remain visible and are not reported as successful ingestion.

### 4. Normalize content and prove the first CLI slice

- Produce `ContentMarkdown` from source spans and `ContentNormalized` from a
  semantic AST traversal. Exclude frontmatter from chunk bodies.
- Remove presentational markers while preserving visible text, inline identifiers,
  code, list items, table cells, and blockquote content. Strip presentational HTML
  markup without losing its visible text. Do not extract attachment contents.
- Normalize whitespace and line endings in semantic text deterministically without
  flattening meaningful code or block boundaries.
- Construct embedding text with the canonical title, optional section path,
  optional tags, blank line, and normalized content. Exclude paths, IDs, hashes,
  timestamps, and YAML. Retain aliases on `KnowledgeDocument`; start with the
  canonical formatter without adding aliases to embedding text.
- Wire `chunks [--config <path>]` through the scanner, parser, and an initial
  whole-section chunker. Prove a small note end to end before implementing every
  oversized-block case.

Acceptance: a small fixture note produces a canonical chunk with correct Markdown,
normalized text, heading context, metadata, and identity through the CLI.

### 5. Complete structure-aware chunking

- Treat heading sections as primary boundaries. Keep coherent sections intact
  when they fit; do not merge unrelated sections merely to reach the target.
- Handle preamble and heading-less bodies as semantic blocks with title context.
  Empty, whitespace-only, or frontmatter-only notes produce no chunks.
- Respect configured defaults: target 300, soft maximum 400, overlap 40 tokens.
  The target is a preference. Preserve an indivisible semantic block above the
  soft maximum only when its complete formatted input still fits the model limit.
- Split oversized sections at block/paragraph boundaries, then sentences or
  token-aware text boundaries only as necessary. Preserve Unicode and source text.
- Apply overlap only to prose split from the same oversized section. Do not add
  overlap across ordinary sections or to code, lists, tables, or blockquotes.
  Include overlap in the final token budget and guarantee forward progress.
- Keep structured blocks intact where practical. Split oversized code by lines
  with continuation context, and tables by rows with repeated headers. Split large
  lists/quotes at item/block boundaries. Provide a final text fallback for a single
  oversized line, row, item, or sentence; never truncate content silently.
- Preserve original source text within pieces; identify any repeated fences,
  table headers, or continuation wrappers as generated structural context. Budget
  that context too, and derive hashes from the actual emitted Markdown.
- If title/heading/tag context alone leaves no room for content, fail the document
  with a safe diagnostic rather than dropping metadata or exceeding the hard limit.
- Assign zero-based chunk ordinals in document order. Use stable collection ordering
  and culture-independent formatting. Set chunk links from the content represented
  in each chunk; retain all document links on `KnowledgeDocument`.

Acceptance: every completed chunk's actual embedding text fits the model token
window; fixtures demonstrate structural preservation, bounded overlap, complete
content coverage, and repeatable boundaries/IDs/hashes.

### 6. Finish CLI behavior, fixtures, and documentation

- Emit one camel-case canonical `KnowledgeChunk` JSON object per stdout line
  (NDJSON), ordered by relative path and chunk ordinal. Empty runs emit no objects.
  Keep aliases on the canonical document model rather than silently adding fields
  to the chunk contract; test alias extraction at the document layer.
- Keep summaries and structured diagnostics on stderr, containing paths, error
  codes, and counts. Explicit chunk output contains the requested note content;
  operational logs and parser exception messages must not echo it.
- Complete processing for each document before emitting its chunks, so a failed
  document cannot appear partially successful. Continue other documents when safe.
- Return 0 for a complete run, including an empty eligible vault; 1 for incomplete
  ingestion; 2 for usage/configuration/tokenizer-setup errors; 130 for cancellation.
  Separate source-read failures from the existing configuration-read error path.
- Do not construct the Qdrant health probe or an inference session for `chunks`.
  Preserve configuration validation and current `status` behavior.
- Add `Knowledge.Core.Tests`, `Knowledge.Markdown.Tests`, and token-adapter tests;
  extend `Knowledge.Indexer.Tests` for command behavior. Add only the required
  project references and include test projects in the solution.
- Commit a synthetic sample vault and expected results with fixed modification
  times in tests. Document the command, output schema, setup, limitations, and
  milestone sequencing clarification. Update the README's implemented status only
  after the implementation passes its checks.

Acceptance: the CLI demonstration succeeds without Qdrant or ONNX weights; a
mixed valid/failed vault returns 1 and emits only complete successful documents.

## Required verification

The automated fixture suite must cover every Milestone 1 case:

- Heading hierarchy, small section, large section, and heading-less notes.
- Code block, list, table, blockquote, and their oversized variants.
- Wikilink with alias, frontmatter tags/aliases, and `ai-index: false`.
- Malformed Markdown, malformed YAML, and empty notes.

Additional boundary checks:

- Heading-like text and wikilinks inside code are not metadata.
- Context and special tokens can push an otherwise fitting body over the limit.
- Long metadata, repeated headings, huge single units, and overlap cannot cause
  truncation, infinite loops, missing source coverage, or duplication beyond the
  intentional prose overlap and repeated structural context.
- Repeated runs over unchanged fixtures produce byte-identical NDJSON. Touching a
  file may change `ModifiedUtc`, but must not change content-derived identities.
- LF/CRLF inputs preserve their source Markdown; file hashes use raw bytes.
- Excluded and opted-out notes never appear in output; unreadable/malformed notes
  produce safe diagnostics and nonzero incomplete-run status.
- Existing configuration and CLI tests continue to pass. Tests and runtime
  ingestion perform no network requests and require no running Qdrant service.

Implementation validation commands:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test Knowledge.sln --no-build --no-restore
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- chunks --config <sample-config.yaml>
```

Run the final command twice against the unchanged sample vault and compare stdout.
Review representative prose, code, and table chunks manually alongside the source.
Passing this milestone proves ingestion and chunk construction, not indexing,
embedding quality, retrieval quality, or completion of the full POC.

## Dependency references

These sources inform implementation choices; they do not supersede repository
architecture:

- [Markdig](https://github.com/xoofx/markdig): Markdown AST, source locations, and table extensions.
- [Microsoft.ML.Tokenizers](https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.tokenizers): local tokenizer APIs, including `BertTokenizer`.
- [Selected model artifacts](https://huggingface.co/BAAI/bge-small-en-v1.5/tree/main): pin a revision when acquiring tokenizer assets.
