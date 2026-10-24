# Evaluation

Milestone 6 provides a local dense retrieval evaluator. The private real-vault
benchmark and owner-reviewed measured baseline are recorded in
[dense-baseline.md](dense-baseline.md). Milestone 6 acceptance is complete.

Create a JSON array with 20–50 entries, each containing a short `id`, `query`,
one primary `category`, and one or more exact vault-relative `expectedPaths`.
Optional `expectedTerms` support diagnostics only. The evaluator checks the full
dataset and source labels before searching. It compares exact case-sensitive
paths in the first five ranked chunks and reports overall and per-category
Hit@5. It performs read-only retrieval against the existing compatible index.

Use the configured real vault and pinned local BGE assets:

```sh
dotnet run --project src/Knowledge.Indexer/Knowledge.Indexer.csproj -- \
  evaluate --benchmark eval/local/retrieval-benchmark.json \
  --output eval/local/dense-baseline.json --config knowledge.local.yaml
```

The real benchmark and detailed report stay in ignored `eval/local/`, since
queries and paths may reveal private vault content. Reports omit query text and
note bodies, but contain relative paths and note/chunk identities. Do not commit
private query rows or detailed reports. Commit a reviewed aggregate summary only
when it is safe to publish. Do not treat a synthetic-vault run as the real-vault
baseline. The measured baseline is summarized in
[dense-baseline.md](dense-baseline.md). The owner-reviewed misses and category-spanning successes are documented in the
private review artifact. The 85% target is advisory; a measured miss is still a
successful evaluation run.

The [Milestone 6 implementation plan](../docs/16-milestone-6-implementation-plan.md)
defines validation, scoring, report fields, privacy, and completion evidence.
The authoritative metric requirements are in the
[retrieval evaluation plan](../docs/04-retrieval-evaluation-plan.md).
For the execution sequence and evidence required to close acceptance, follow the
[Milestone 6 acceptance closure plan](../docs/16-milestone-6-implementation-plan.md#9-milestone-6-acceptance-closure-plan).
