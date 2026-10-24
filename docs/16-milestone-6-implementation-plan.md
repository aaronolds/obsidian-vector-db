# Milestone 6: Retrieval benchmark

Implementation status: evaluator and tests implemented on 2026-10-23. Real-vault
acceptance closed on 2026-10-24: pinned assets were verified, the registered
vault corpus was indexed, and the approved 24-question benchmark was measured
and reviewed by the vault owner. Milestone 6 acceptance is complete.
Planning date: 2026-10-22; implementation date: 2026-10-23.

The acceptance closure plan and dated evidence are in section 9. The recorded
gates and outcomes define Milestone 6 completion.

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
- `KnowledgeCli.RunSearchAsync` and evaluation use
  `Knowledge.Runtime.KnowledgeSearchSession` for local ONNX/query embedding and
  Qdrant composition, with the profile from `IndexProfile.Create`. Preserve this
  shared composition. Do not evaluate by parsing rendered `knowledge search`
  output or duplicating the search algorithm.
- `QdrantKnowledgeRetriever.SearchAsync` validates the collection/profile,
  scopes every search to the configured vault, and returns ranked chunks with
  provenance. It does not create or repair collections. Preserve these gates.
- Put benchmark loading, validation, scoring, orchestration, and report writing
  in `src/Knowledge.Indexer/Evaluation/`. Use the existing retriever factory seam
  for asset-free CLI tests; avoid a new project or dependency framework.
- Extend `tests/Knowledge.Indexer.Tests/` for evaluator/CLI tests and the existing
  explicit `Knowledge.Indexing.IntegrationTests` suite for live wiring checks.
- `eval/README.md` documents dataset authoring, local invocation, and privacy;
  `eval/dense-baseline.md` records the measured and owner-reviewed baseline.

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
The vault owner confirmed the review on 2026-10-24. The baseline result is above
85%; exact-identifier and acronym questions scored 7/7 combined. The review found
no failure in that small identifier sample, while retaining the small sample size
as a limitation. Do not implement the later sparse/hybrid comparison in M6.

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

At the 2026-10-23 verification, real-vault evaluation had not yet run. The
2026-10-24 evidence below records the completed evaluation and owner review.

Additional verification on 2026-10-24:

- The full solution build succeeded with zero warnings and errors. The solution
  unit suites passed 132 Indexer, 8 Embeddings, and 21 MCP tests.
- The pinned ONNX integration suite passed 6/6; the local Qdrant/ONNX indexing
  integration suite passed 3/3, including the complete CLI evaluation path,
  private-content checks, unchanged indexed metadata, and report preservation
  after a setup failure.
- `scripts/setup-bge-model.sh` verified the pinned model digest. Local Qdrant
  v1.19.2 was already running; all live tests used unique disposable collections
  and cleaned them up.
- The registered open vault indexed successfully into the dedicated local
  acceptance collection: 410 Markdown files scanned, 404 indexed, 6 empty,
  3,707 chunks written, and zero failures. The unchanged second pass reported
  404 unchanged documents, zero chunk writes, and zero embedding calls.
- The user approved benchmark v1 expected paths before results were examined.
  Its SHA-256 is `fb5b338df3af94d8f1c2bcfdac55029cc2f31bdea61450789e5d152f83c908a5`.
  The run returned 22/24 Hit@5 (91.7%). Both misses and one successful result
  from each category were reviewed; the vault owner confirmed the relevance
  review on 2026-10-24. The private notes remain in ignored `eval/local/review-v1.md`.

## 9. Milestone 6 acceptance closure plan

### 9.1 Starting point and scope

Repository review on 2026-10-24, at revision `a456098`, confirmed:

- Dataset loading, source eligibility validation, fixed-K scoring, category
  aggregation, report metadata, atomic output, and the CLI are implemented.
- Unit tests cover core scoring and validation, including rank 5 versus rank 6,
  the 17/20 target boundary, fixed limit 5, content omission, and cancellation
  before report writing. The section 6 coverage audit added direct checks for
  alternative labels, empty/short results, exact path casing/folders, malformed
  results, terms, category denominators, maxLimit preflight, missing assets,
  retrieval failure privacy, and report-write failure preservation.
- The Qdrant/ONNX integration suite now exercises the complete real-runtime
  `knowledge evaluate` CLI path against a synthetic vault and disposable collection.
- No `knowledge.local.yaml`, `eval/local/` dataset/report, or pinned ONNX weights
  were present at the start of this execution. The private config, verified weights, indexed collection, and benchmark were
  then created. The user approved benchmark labels, and the measured baseline
  and owner relevance review are complete.

Follow documents 01–08 and the accepted ADRs. Keep BGE-small-en-v1.5, local
inference, 384-dimensional cosine vectors, payload schema 1, embedding schema 2,
query formatting, chunking, and the current index profile unchanged. Core stays
independent of infrastructure; evaluation stays in `Knowledge.Indexer` and
reuses `Knowledge.Runtime` composition and the existing retriever.

Do not expand MCP, implement watching, tune retrieval, add hybrid/sparse search,
reranking, graph expansion, deduplication, thresholds, or new metrics. Existing
Milestone 7 work is outside this acceptance effort. If an implementation issue
makes an accepted decision infeasible, record the failing case, evidence,
affected requirement/ADR, and proposed resolution in this document before
changing the design. A result below 85% is not architectural infeasibility.

### 9.2 Phase A: establish the evaluation inputs

Execution needs the actual vault root and stable vault ID, intended include and
exclude rules, and an available reviewer familiar with the notes. Obtain these
inputs before reading a real vault or inventing ground truth; the repository
docs and synthetic sample vault are not substitutes. Automated test work in
Phase B can proceed independently of these inputs.

1. Create ignored `knowledge.local.yaml` using the existing sample configuration
   and documented file-relative path rules. Select a new dedicated disposable
   collection, such as `obsidian-knowledge-m6-acceptance-<run-id>`, rather than an
   existing user collection. Keep logging content and queries disabled.
2. Confirm that `knowledge.local.yaml` and every private artifact under
   `eval/local/` are ignored before writing private content. Record machine paths,
   source manifests, query text, detailed results, and review notes only locally.
3. Install or locate the pinned ONNX weights using the existing explicit setup
   procedure, `./scripts/setup-bge-model.sh`, and verify model/tokenizer digests.
   Acquisition is a setup step; add no runtime download or remote fallback.
4. Establish a quiet evaluation window with no source edits, sync changes, or
   concurrent index writers. Use the same vault snapshot, configuration,
   benchmark, and software revision throughout the acceptance run.
   Record the evaluated Git revision and working-tree state; commit code fixes
   before measurement or retain the exact patch and its hash with the private
   run evidence so the evaluated software can be reproduced.

Deliverable: a usable private configuration, verified assets, selected collection,
and identified dataset/review owner. Missing inputs leave real-vault acceptance
pending; record the exact missing input rather than claiming synthetic acceptance.

### 9.3 Phase B: verify and complete the evaluator implementation

Audit section 6 against the implementation and existing tests. Add focused tests
for uncovered behavioral requirements, and fix only demonstrated Milestone 6
defects. Do not rewrite the evaluator or add a new framework.

| Area | Work to verify or complete |
|---|---|
| Dataset preflight | Malformed/null/unknown/duplicate fields; 20–50 entries; unique IDs; canonical path casing; missing, unreadable, excluded, opted-out, and empty sources; an invalid late query rejects the whole dataset before retrieval |
| Scoring | Rank 1/5/6; empty and short results; duplicate chunks consuming slots; alternative expected paths; case and folder distinctions; category denominators; terms never changing the hit outcome |
| CLI safety | Required/repeated/unknown options; config/environment/path resolution; `maxLimit < 5`; dataset/config/vault output protection; no filtering; fixed K despite a different default limit |
| Failure integrity | Missing/incompatible collection, asset failure, Qdrant/inference failure, malformed results, mid-run cancellation, report-write failure, and source changes abort without replacing a prior complete report |
| Privacy and reporting | Query, note, and server-error sentinels absent from stdout/stderr/reports; complete provenance/version/configuration metadata; deterministic category order and invariant numeric output |
| Read-only evaluation | No collection, payload-index, or point mutation; unchanged sources and indexed state before/after evaluation |

Extend `tests/Knowledge.Indexing.IntegrationTests/LocalIndexingTests.cs` with a
focused full-CLI evaluation case using pinned assets, a temporary synthetic vault,
and a unique disposable collection. Invoke the real CLI without the injected
retriever factory; exercise JSON loading, runtime composition, Qdrant status,
retrieval, stability checking, and JSON report writing together. Assert a complete
report, valid ranked provenance, vault isolation, content omission, and unchanged
source/index state. Include an operational failure that preserves existing
report bytes. Keep semantic quality targets out of this synthetic wiring test.

Run from the repository root after any necessary changes:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore
dotnet test Knowledge.sln --no-build --no-restore
dotnet test tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj
docker compose -f docker/docker-compose.yml up -d
dotnet test tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj
```

Deliverable: passing asset-free tests and live embedding/Qdrant/CLI evidence,
with versions, test counts, exit codes, and disposable-test cleanup recorded.
Do not substitute a successful integration build for a live test result.

Verification on 2026-10-24: the section 6 audit and follow-up tests passed. The
full solution built with zero warnings/errors and its unit suites passed 132
Indexer, 8 Embeddings, and 21 MCP tests. The pinned ONNX suite passed 6/6 and
the live Qdrant indexing/evaluation suite passed 3/3. The standard and local-cache
solution restore attempts stalled without output and were stopped; the verified
build and test runs used existing restored assets.

### 9.4 Phase C: author and freeze genuine ground truth

1. Read eligible real-vault sources and author 20–50 useful questions; aim for 24.
   Cover conceptual, paraphrased, acronym, exact-identifier, architecture-decision,
   implementation-detail, multiple-plausible-note, and intentionally difficult
   cases. Include code-heavy sources. Assign one primary category per question;
   coverage matters more than equal category sizes.
2. Verify each expected path against authoritative Markdown and record why its
   content answers the question, plus acceptable alternative paths. Preserve
   discovered path casing and eligibility. A reviewer familiar with the vault
   checks the questions and labels before seeing retrieval results.
3. Save `eval/local/retrieval-benchmark-v1.json` in the existing JSON format.
   Save private label rationale and coverage notes separately; do not add fields
   to the strict benchmark schema. Use metadata-safe IDs/category slugs.
4. Freeze the dataset and record its SHA-256 and version before inspecting any
   benchmark retrieval outcomes. Never revise labels merely to improve Hit@5.
   Genuine label corrections require a new dataset version/hash and a new run,
   preserving the original artifacts and explanation.

Deliverable: independently verified ground truth and an immutable benchmark
version. Repeated synthetic questions do not meet this gate.

### 9.5 Phase D: index and measure the dense baseline

After Phases A–C pass, start/confirm local Qdrant with the checked-in Compose
configuration. Keep its existing loopback-only gRPC binding. Run the real-vault
commands from the repository root, using the dedicated collection in the private
configuration:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- index --config knowledge.local.yaml
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- index --config knowledge.local.yaml
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- status --config knowledge.local.yaml
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- evaluate --benchmark eval/local/retrieval-benchmark-v1.json --output eval/local/runs/baseline-v1/dense-baseline.json --config knowledge.local.yaml
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj --no-build -- status --config knowledge.local.yaml
```

Require complete successful indexing. The second unchanged index pass must
report zero embedding calls and zero chunk writes. Do not run evaluation against
a failed or partial indexing run. Use `index` against the fresh collection;
collection-wide `rebuild` of an existing collection is unnecessary here.

Before and after evaluation, capture and compare source-manifest hashes,
vault-scoped document/chunk counts, and sorted indexed-metadata fingerprints
including point IDs, file hashes, chunk ordinals/counts, schema, and profile.
Use existing read-only store methods or a small local acceptance helper;
do not add a public command or persistent catalog solely for this comparison.
Keep detailed metadata private and never dump vectors or note bodies into logs.
Counts alone do not demonstrate unchanged index state. The CLI already checks
source stability, but does not itself compare index state after the run.

Require exit code 0 and a complete JSON report with the frozen benchmark/source
hashes, exact K=5, dense mode, model/asset digests, schema/profile, runtime/server
versions, index counts, per-category totals, and per-query outcomes. Check that
category totals reconcile with overall totals. Preserve raw reports under
run-specific paths instead of overwriting prior runs.

An exit code of 0 can legitimately accompany `targetMet: false`. Operational
failures, incompatible profiles, source/index changes, or unverified labels
invalidate acceptance evidence; resolve the cause and start a new recorded run.
Never count those failures as retrieval misses or combine partial runs.

Deliverable: a reproducible real-vault dense report and source/index stability
evidence. Report the measured ratio without tuning this baseline.

Execution on 2026-10-24 used benchmark v1 (SHA-256
`fb5b338df3af94d8f1c2bcfdac55029cc2f31bdea61450789e5d152f83c908a5`) at revision
`a456098b3ca006d48f2102813948d0d98107de10`. Both indexing passes succeeded; the
second pass wrote zero chunks and made zero embedding calls. The 24-question run
exited 0 with 22/24 Hit@5 (91.7%), `targetMet: true`. It recorded 404 documents,
3,707 chunks, BGE-small-en-v1.5 pinned digests, .NET 10.0.13, ONNX Runtime
1.31.0.0, and Qdrant 1.19.2. Both the source snapshot hash
`0f26cf56b523526fadca9c7a0436f4c2f6c96e96c5e24bf01e21ab7cf5d796e5` and indexed
metadata fingerprint `de3e0e02771fabd170de7ad91a566ba356ced51e778daab11358e75576b5c57a`
matched before and after evaluation. The configured parent root indexed 19
nested `Personal/` documents / 69 chunks. Aggregate results and the recommendation
are recorded in `eval/dense-baseline.md`; detailed review remains ignored in
`eval/local/`.

### 9.6 Phase E: review relevance and record closure

Inspect every miss and representative successes across categories against the
same frozen source/index state. Keep returned Markdown in deliberate local
inspection output, separate from application logs. For each reviewed safe query
ID record: whether the passage answers the question, section provenance and
heading context, chunk coherence/size, duplicate-result dominance, and code-heavy
retrieval behavior. Distinguish label errors, stale/incomplete indexing, and dense
retrieval limitations. Do not silently remove difficult questions.

Populate `eval/dense-baseline.md` with a sanitized aggregate review: benchmark
version/hash, source snapshot hash, evaluated Git revision, model/asset and index
configuration, runtime/server versions, counts, overall and category Hit@5,
`targetMet`, reviewed safe IDs, failure patterns, and a recommended future
experiment or a reason to keep dense retrieval. Keep private examples and detailed
per-query artifacts under `eval/local/`. Recommend later work without implementing
it in Milestone 6. A sub-85% result can close this milestone with honest analysis;
it cannot justify claiming that the quality target was met.

| Closure gate | Required evidence |
|---|---|
| Evaluator correctness | Section 6 coverage audit, passing solution tests, live model tests, and full-CLI Qdrant/ONNX check |
| AC-006 semantic search | A reviewed successful natural-language/paraphrased query returns a passage that answers the question |
| AC-007 identifier baseline | Exact-identifier/acronym cases are measured and their limitations documented |
| AC-008 provenance | Reviewed results identify the correct note and section and preserve requested Markdown |
| AC-010 benchmark | At least 20 verified real-vault questions; complete exact-path top-five metrics overall and by category |
| Reproducibility and privacy | Frozen benchmark/source hashes, model/profile/version metadata, successful indexing, unchanged sources/index, private artifacts excluded from Git |
| Human review | All misses and category-spanning successes reviewed; findings and recommendation recorded |

All closure gates passed on 2026-10-24. Milestone 6 is marked complete in the
introduction, roadmap, `README.md`, `eval/README.md`, and `AGENTS.md`. Milestone 7
acceptance remains separate and pending.

After the owner confirmed review, the dedicated disposable acceptance collection
`obsidian-knowledge-m6-acceptance-20261024` was removed and verified absent. The
private benchmark, raw report, and review notes remain ignored under
`eval/local/`; the temporary snapshot helper and its build output were removed.
The local configuration and downloaded pinned model assets remain available for
future evaluation. No user collection, shared Qdrant volume, or Markdown note was
changed.
