# Milestone 6: Retrieval benchmark

Implementation status: evaluator and tests implemented on 2026-10-23. The
real-vault benchmark and reviewed dense baseline remain pending because this
checkout has no configured real-vault dataset or ONNX model weights. Planning
date: 2026-10-22; implementation date: 2026-10-23.

## 1. Outcome and scope

Implement only Milestone 6 from [the delivery plan](05-implementation-plan.md):
author at least 20 representative real-vault queries, automate Hit@5, report
categories, and capture and review the first dense baseline. Follow
[the evaluation plan](04-retrieval-evaluation-plan.md), POC AC-006, AC-007,
AC-008, and AC-010, the architecture and data contracts, and ADR-003, ADR-005,
ADR-008, and ADR-010. The path, duplicate-result, candidate-count, and threshold
defaults in [the open questions](08-open-questions-and-assumptions.md) also apply.

Keep local BGE-small-en-v1.5, 384-dimensional cosine search, payload schema
version 1, embedding schema version 2, and the current index profile unchanged.
No architectural change is proposed. Record any implementation infeasibility
and evidence before proposing a departure from an accepted decision.

Excluded: MCP hosting, watching, hybrid/sparse search, RRF, reranking, graph
expansion, diversification, model or chunking changes, additional metrics such
as MRR/nDCG, cloud services, and automatic retrieval tuning. A low baseline is
evidence for later work, not authorization to implement that work here.

## 2. Existing implementation and ownership

- `Knowledge.Core` already defines `IKnowledgeRetriever`, search request/result
  records, identities, and schema constants. No benchmark-specific Core contract
  is needed; keep Core independent of infrastructure.
- `KnowledgeCli.RunSearchAsync` constructs local ONNX/query embedding services
  and `QdrantKnowledgeRetriever` using `IndexProfile.Create`. Reuse this
  composition through a narrow shared helper if necessary. Do not evaluate by
  parsing rendered `knowledge search` output or duplicating the search algorithm.
- `QdrantKnowledgeRetriever.SearchAsync` validates the collection/profile,
  scopes every search to the configured vault, and returns ranked chunks with
  provenance. It does not create or repair collections. Preserve these gates.
- Put benchmark loading, validation, scoring, orchestration, and report writing
  in `src/Knowledge.Indexer/Evaluation/`. Use the existing retriever factory seam
  for asset-free CLI tests; avoid a new project or dependency framework.
- Extend `tests/Knowledge.Indexer.Tests/` for evaluator/CLI tests and the existing
  explicit `Knowledge.Indexing.IntegrationTests` suite for live wiring checks.
- `eval/README.md` is currently a placeholder. It will document dataset
  authoring, local invocation, report format, baseline evidence, and privacy.

## 3. Benchmark dataset and ground truth

Use the JSON array format in the evaluation plan: `id`, `query`, `expectedPaths`,
optional `expectedTerms`, and `category`. Keep the dataset compatible with that
format; use a SHA-256 of its bytes as the authoritative benchmark version in
reports, with a human version label recorded in the baseline review.

Author 20–50 questions against the actual evaluation vault before inspecting
their retrieval outcomes. Aim for 24 initially, with coverage of conceptual,
paraphrased, acronym, exact-identifier, architecture-decision,
implementation-detail, multiple-plausible-note, and intentionally difficult
questions. Assign each query one primary category so category totals reconcile
with the overall total; document overlapping traits in the authoring notes.
Prefer useful coverage over an artificial equal distribution.

For each question, manually verify that at least one expected note actually
answers it, record all acceptable alternative paths, and keep labels independent
of what dense retrieval happened to return. Include code-heavy notes and
questions whose wording differs from note wording. Do not invent paths or use
the example Azure questions as real ground truth without checking the vault.

Validate the complete dataset before inference or network access:

- Require at least 20 entries, unique nonblank IDs, nonblank queries and
  categories, and at least one nonblank expected path per entry. Reject malformed
  JSON, null entries, invalid property types, and unsupported fields with safe
  diagnostics that identify an entry number/field without echoing its contents.
- Use short metadata-safe IDs and category slugs; reject control characters.
  Validate every query with the existing BGE query/token-limit rules before the
  first search, so a later invalid query cannot produce a misleading partial run.
- Require canonical vault-relative Markdown paths using `/` and discovered
  casing. Reject rooted, escaping, empty, and noncanonical paths; never lowercase
  or match by basename, substring, title, or platform-insensitive equality.
- Check expected sources exist and are eligible under the configured discovery,
  exclusion, containment/symlink, and opt-out rules. Reuse existing ingestion
  behavior rather than building a second scanner/parser. Missing, unreadable,
  excluded, or opted-out ground truth is a dataset/setup error. An eligible source
  missing from the index remains a retrieval miss and a review diagnostic.
- If supplied, `expectedTerms` must contain nonblank strings. Their presence or
  absence must never change Hit@5 or eligibility.

Use a checked-in `eval/retrieval-benchmark.json` only when the real-vault dataset
is suitable for repository publication. Otherwise keep the real benchmark in
ignored `eval/local/`, with private queries, paths, terms, and detailed review
notes there; commit only a safe schema example and the reviewed aggregate
baseline. Add the ignore rule before creating private artifacts. Synthetic test
fixtures validate mechanics but do not satisfy the real-vault baseline gate.

## 4. Evaluation command and exact scoring

Add this proposed CLI surface, including command help:

```sh
knowledge evaluate --benchmark <path> --output <report.json> [--config <path>]
```

`--benchmark` and `--output` are required and resolve relative to the caller's
working directory. Configuration defaults to `knowledge.yaml`; existing
configuration-file-relative vault/model paths and `KNOWLEDGE_` overrides retain
their semantics. Reject unknown, missing, or repeated options and prevent the
report destination from overwriting the dataset, configuration, or a vault note.

Execution sequence:

1. Parse arguments and validate configuration, dataset, source eligibility, and
   output destination. Require dense mode and `retrieval.maxLimit >= 5`.
2. Require and validate the pinned local model/tokenizer assets; reuse the
   existing query formatter and runtime setup. Construct one runtime/client for
   the run. No model downloads, index writes, or implicit indexing/rebuild.
3. Execute queries sequentially through `IKnowledgeRetriever` with `Limit = 5`,
   no path/tag filters, and the configured vault ID. Do not inherit
   `retrieval.defaultLimit` or introduce a score threshold. Preserve cancellation.
4. Score the first five ranked **chunks** as returned. A query passes if any of
   those chunks has a path equal to any canonical expected path using ordinal,
   case-sensitive comparison. Do not deduplicate documents, overfetch, rerank,
   or reorder ties. Repeated chunks from one note consume result slots.
5. An empty or shorter result set is valid and uses its available ranks; no
   expected match is a miss. Multiple expected paths still yield one binary
   outcome. Record the first matching rank (1–5), if any, for review.
6. Compute overall Hit@5 as passing queries / total queries, and each category
   using its own denominator. Compare the unrounded ratio to 0.85; display
   counts alongside percentages and do not average category percentages.
7. Write a complete report only after every query succeeds operationally.
   Use a temporary sibling file and replacement so a failed/cancelled run does
   not replace an existing complete report. Emit only safe operational output.

Optional term diagnostics use case-insensitive literal presence in returned
Markdown within the same five results. Store matched/total term counts without
term text. They are clues for human review, never a second success criterion.

Exit codes: `0` for a completed benchmark, including a measured result below
85%; `1` for retrieval/transport/inference failure or report-write failure; `2`
for usage, dataset, configuration, asset, or collection/profile setup errors;
`130` for cancellation. Abort operational failures instead of counting them as
misses, dropping queries, or presenting partial metrics as the dense baseline.
Report `targetMet` separately from operational success. The target is advisory,
consistent with AC-010, rather than a mandatory CI quality gate.

## 5. Report and reproducibility

Version the machine-readable report format independently from the benchmark.
Record:

- Run timestamp, application version, Git revision when available, .NET/ONNX
  runtime and Qdrant versions, and retrieval mode (`dense`).
- Benchmark SHA-256/version, query count, vault ID, collection, indexed
  document/chunk counts, and an identifier/hash for the evaluated source
  snapshot. Keep absolute workstation paths out of the report.
- Model identity, pinned asset revision/digests, dimensions, cosine distance,
  payload and embedding schema versions, query-format identifier, index-profile
  fingerprint, chunk settings, and effective include/exclude/opt-out rules.
- Fixed K of 5, target of 0.85, passing/total counts, overall and per-category
  Hit@5, and `targetMet`.
- Per-query safe ID, category, hit/miss, first matching rank, and returned
  document/chunk IDs, relative paths, ranks, and scores. Include only term counts
  if term diagnostics are implemented. Never serialize the full search-result
  object, which contains original Markdown.

Sort category summaries ordinally and retain dataset order for per-query rows.
Use invariant numeric formatting. Scores/timings/timestamps are diagnostic;
do not promise byte-identical reruns or identical membership at tied rank cuts.

Run against a fixed corpus and index with no concurrent source edits or index
writers. Record the source manifest hash and successful indexing evidence before
evaluation; check that source hashes and index counts remain unchanged afterward.
This is an acceptance procedure, not a new transactional snapshot guarantee.
Reruns are comparable only with the same benchmark, source snapshot, and index
configuration. Do not change labels to improve the reported baseline.

Reports are deliberate local output artifacts, separate from application logs.
Keep query text, note bodies, headings/titles, embedding inputs, vectors, and
server exception bodies out of stdout/stderr/logs and default reports.
`logContent` and `logQueries` stay false. Detailed local reports may still contain
sensitive paths/identities; keep them ignored. Commit a sanitized aggregate
summary with hashes and version/configuration metadata, not private query rows.

## 6. Implementation order and validation

1. Add benchmark DTOs/loader and whole-dataset validation with asset-free tests.
   Establish ignored local artifact paths and authoring documentation.
2. Add the evaluator over `IKnowledgeRetriever`, exact scoring, category
   aggregation, and report DTOs/writer. Test with controlled ranked results.
3. Add CLI parsing/help and reuse the search composition and factory seam.
   Validate all inputs before retrieval; preserve privacy and exit conventions.
4. Add live integration coverage using a synthetic temporary vault and unique
   disposable collection. Index through the existing writer, evaluate through
   the real retriever, and assert report/provenance wiring, vault isolation, and
   unchanged sources/index state. Do not assert a semantic quality threshold for
   synthetic fixtures. Remove only test-created collections/files.
5. Author/review the real-vault benchmark, run the dense baseline, inspect
   failures and representative successes, and capture the reviewed result.
6. Update `README.md`, `eval/README.md`, configuration/CLI documentation,
   milestone status, and `AGENTS.md` to reflect only verified behavior.

Unit coverage must include:

| Area | Required cases |
|---|---|
| Dataset | Fewer than 20 entries; duplicate IDs; missing/null/blank fields; invalid JSON/types; invalid/traversing paths; ineligible ground truth; invalid/oversized query |
| Ranking | Match at ranks 1 and 5; rank 6 does not count; duplicate chunks consume slots; multiple acceptable paths; empty/fewer-than-five results; case mismatch and same filename in another folder do not match |
| Metrics | Mixed passes/misses; category denominators; exactly 17/20 meets target and 16/20 does not; rounding cannot change target status; terms never change scoring |
| CLI | Help without configuration/assets/network; invalid options; fixed limit 5 despite configured default; maxLimit below 5 rejected; shared config/env/path behavior |
| Failures | Missing/incompatible collection; asset error; Qdrant/inference failure; cancellation; report-write failure; existing report preserved on incomplete runs |
| Privacy/read-only | Query/note/server-error sentinels absent from logs/errors/reports; no vectors or serialized Markdown; no collection/payload/point mutation during evaluation |

Run the required solution checks from the repository root:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test Knowledge.sln --no-build --no-restore
```

For live wiring, use existing pinned assets and start local Qdrant only as needed:

```sh
docker compose -f docker/docker-compose.yml up -d
dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj
```

The proposed real-vault invocation after implementation is:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  evaluate --benchmark eval/local/retrieval-benchmark.json \
  --output eval/local/dense-baseline.json --config knowledge.local.yaml
```

## 7. Baseline review and exit criteria

Create a short `eval/dense-baseline.md` with the benchmark version/hash, source
snapshot identifier, index/model configuration, overall and category counts and
Hit@5, target status, reviewed query IDs, notable failure patterns, and the
recommended next retrieval experiment. Keep private examples in local artifacts.

Manually inspect all misses and a sample of successes spanning categories. Check
whether passages answer the question, heading context is sufficient, chunks are
too broad/narrow, duplicates dominate, and code-heavy material retrieves well.
Separate bad labels or stale/incomplete indexing from dense semantic weaknesses;
record any corrections as a new benchmark/run rather than overwriting history.
If below 85%, document the failure analysis before recommending later hybrid or
other architecture work. Do not implement the recommendation in Milestone 6.

Milestone 6 is complete only when the evaluator is tested, at least 20 genuine
real-vault questions are evaluated, complete dense metrics/configuration are
captured, and the manual review and recommendation are recorded. A missing real
vault, pinned assets, local Qdrant, or verified labels leaves live acceptance
pending; synthetic tests and a report template alone cannot close the milestone.

Future model/chunking/normalization/retrieval changes must rerun the same
versioned benchmark and explain material regressions. Automated private-vault CI
and post-POC metrics remain outside this milestone.

## 8. Implementation and verification evidence

Implemented:

- `knowledge evaluate --benchmark <path> --output <path> [--config <path>]`
  validates the complete JSON dataset, BGE query token limits, canonical expected
  paths, and expected-source eligibility before retrieval. It uses the existing
  local query embedding and read-only dense Qdrant retriever, requests exactly
  five chunks without filters, and compares exact case-sensitive relative paths.
- The report captures overall/category Hit@5, per-query safe IDs and returned
  provenance metadata, source and benchmark hashes, model and tokenizer digests,
  index profile, collection counts, Qdrant server version, runtime metadata, and
  configuration. It omits query text, terms, and Markdown bodies. Reports write
  through a temporary sibling file after the full run and a source-manifest
  stability check. Private evaluation artifacts are ignored under `eval/local/`.
- Added asset-free dataset/scoring/CLI coverage and a real Qdrant/ONNX integration
  case. Updated the command reference, agent guidance, roadmap, and evaluation
  instructions.

Verification on 2026-10-23:

- `dotnet restore Knowledge.sln` succeeded.
- `dotnet build Knowledge.sln --no-restore -m:1 -p:UseSharedCompilation=false`
  succeeded with zero warnings and errors.
- The complete solution unit suite passed: Indexer 120/120 and Embeddings 8/8.
- `tests/Knowledge.Indexing.IntegrationTests` compiled successfully. Its live
  Qdrant/ONNX suite was not run because the ignored pinned ONNX weights are absent
  from this checkout.
- `knowledge.yaml` points at the repository `docs/` directory as its sample
  vault. No real user-vault configuration or privately verified benchmark was
  supplied, so no genuine real-vault baseline or manual quality review is claimed.

The evaluator implementation is complete; Milestone 6 acceptance remains pending
until the user runs it against a verified real vault with pinned ONNX assets and
local Qdrant, then records the dense results and human review in
`eval/dense-baseline.md`.
