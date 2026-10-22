# Contributing

Read [AGENTS.md](AGENTS.md), the relevant architecture/milestone documents under
`docs/`, and the applicable [architecture decisions](docs/adr/) before changing
cross-project contracts. Keep changes focused and document behavior changes.

## Build and unit tests

Install a stable .NET 10 SDK compatible with `global.json`. From the repository
root, run the same Release commands as GitHub CI:

```sh
dotnet restore Knowledge.sln
dotnet build Knowledge.sln --configuration Release --no-restore
dotnet test Knowledge.sln --configuration Release --no-build --no-restore --logger trx --results-directory TestResults
```

The solution contains the asset-free Indexer and Embeddings unit suites.
Neither Docker nor ONNX weights are needed. Add focused tests for changed
behavior, preserve nullable safety, and keep warnings-as-errors enabled.

The two integration projects are deliberately outside the solution. Run them
explicitly for changes to real inference or storage behavior, using the pinned
local model assets and, for indexing, local Qdrant. See the
[README](README.md#tests) and milestone acceptance documentation. Use synthetic
vaults and isolated disposable collections; never delete another collection.
Do not claim integration acceptance from unit tests alone.

## Dependency changes

Review Dependabot PRs rather than auto-merging them. Major upgrades, Qdrant and
ONNX compatibility, and Python reference-fixture reproducibility require
particular care. Model assets remain pinned and are not updated by Dependabot.
NuGet CI audits include direct/transitive dependencies in both the solution and
the separate integration projects. See
[GitHub automation](docs/14-github-automation.md#dependency-maintenance-and-auditing)
for the exact audit command and coverage.

## Data and architecture safety

- Keep Core dependency-free and put behavior in its owning project.
- Preserve deterministic chunking, provenance, Obsidian semantics, and explicit
  configuration validation.
- Keep inference local, logs metadata-only, and Qdrant loopback-only by default.
- Preserve indexing discovery/compatibility gates, document replacement/retry
  behavior, and read-only vault-scoped search.
- Never commit real vault content, credentials, generated build/test output, or
  ONNX weights. Use synthetic examples and redact paths and diagnostics.
- This repository and its issues, PRs, and hosted CI output are public. Do not
  post private note text, queries, vectors, or identifying filesystem paths.

## Pull requests and issues

Use a focused branch and describe the change, motivation, verification performed,
and documentation impact in the PR template. Report bugs with minimal synthetic
reproductions, SDK/application versions, and safe diagnostics using the issue forms.

Copilot feedback is advisory: verify findings and resolve meaningful comments.
It does not approve PRs or replace maintainer review. Repository settings, not
the instruction file alone, enable automatic review. See the
[activation guide](docs/14-github-automation.md) for required checks and setup.
