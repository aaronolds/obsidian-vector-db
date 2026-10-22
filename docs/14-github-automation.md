# GitHub automation

## Delivery and activation

The repository files define Linux CI, NuGet auditing, Dependabot updates, issue/PR
templates, and Copilot instructions. They do not activate branch rules or Copilot
automatic review by themselves. This setup was prepared as local changes only:
no commit, push, PR, or repository/personal settings changes were performed.

The repository is public. Hosted workflows execute only after the owner publishes
the files; Dependabot configuration must be on the default branch. Follow the
rollout steps below to activate and verify GitHub behavior.

## Build and unit tests

`.github/workflows/ci.yml` runs on PRs targeting `main`, pushes to `main`, and
manual dispatch. The stable check name is **Build and unit tests**.

It installs stable .NET 10 on `ubuntu-24.04`, compatible with `global.json`, then
restores, builds Release, and runs the solution's two asset-free unit suites.
TRX results are uploaded after success or failure when available and retained
for seven days. Build failures before test execution may have no test artifact.
Only TRX files are uploaded, not build output, model weights, or application data.

The separate real-model and Qdrant integration suites are not executed. They need
explicit local acceptance using synthetic vaults, pinned assets, and isolated
collections. There are no model downloads, hosted Qdrant services, release
packages, publishing jobs, or application/cloud inference calls in these workflows.

Both workflows have read-only repository access, SHA-pinned official actions,
checkout credentials disabled, 15-minute job limits, and concurrency cancellation
for superseded runs of a PR/ref. They do not use `pull_request_target`, PATs,
repository secrets, or self-hosted runners. They have no path filters, so
documentation-only PRs still produce required checks.

## Dependency maintenance and auditing

`.github/dependabot.yml` requests weekly updates for:

| Ecosystem | Manifests |
| --- | --- |
| NuGet | All projects under `src/*` and `tests/*`, including integration tests |
| GitHub Actions | Action references in `.github/workflows/` |
| Docker Compose | Qdrant image in `docker/docker-compose.yml` |
| pip | Reference-generation tools in `scripts/requirements-bge-reference.txt` |

Routine minor/patch NuGet, Actions, and pip updates are grouped. Major updates
remain separate review decisions. Open version-update PRs are limited per
ecosystem (5 NuGet, 3 Actions, 2 Compose, 3 pip); nothing auto-merges.
Dependabot proposes changes to existing pins rather than silently changing them.
Review runtime/model/Qdrant compatibility and reference-fixture reproducibility
before merging. Model checkpoint revisions, tokenizer assets, and ONNX weights
are outside Dependabot's update coverage.

`.github/workflows/dependency-audit.yml` provides the **Dependency audit** check.
It runs on PRs targeting `main`, pushes to `main`, manual dispatch, and Mondays
at 07:23 UTC. Each restore explicitly enables NuGet vulnerability auditing for
all direct/transitive dependencies and all reported severities. Warnings,
including unavailable vulnerability data, are errors; the job must not report
success when it could not audit. Restore failures stop the job.

Run its commands locally:

```sh
dotnet restore Knowledge.sln --force -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true
dotnet restore tests/Knowledge.Embeddings.IntegrationTests/Knowledge.Embeddings.IntegrationTests.csproj --force -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true
dotnet restore tests/Knowledge.Indexing.IntegrationTests/Knowledge.Indexing.IntegrationTests.csproj --force -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true
```

These commands restore packages without loading model weights or contacting
Qdrant. Network access to package/advisory sources is required. Audit policy is
explicit in the workflow, not a global change to application project files.
`dotnet package list --vulnerable` alone is not an enforcing check.

In repository **Settings > Advanced Security** (or **Code security** where shown),
check that the dependency graph and Dependabot alerts are enabled, then enable
Dependabot security updates. Version-update configuration and security-update
settings are distinct. GitHub controls security-update eligibility and limits.
The enforcing workflow covers NuGet only; Compose/pip/Actions version updates
are not a complete vulnerability scan for those ecosystems. CodeQL and the
dependency-review action are deliberately not configured in this iteration.

## Main branch protection

After publishing and observing successful checks, open repository
**Settings > Rules > Rulesets > New ruleset > New branch ruleset**.

Create an active `main-protection` ruleset targeting the default branch:

1. Enable **Require a pull request before merging**.
2. Leave required approving reviews at **0** for the initial owner-driven
   workflow. Authors cannot approve their own PR, and Copilot posts comment
   reviews rather than approvals. Require human approval once another maintainer
   is available, if desired.
3. Enable **Require conversation resolution before merging**.
4. Enable **Require status checks to pass**, selecting **Build and unit tests**
   and **Dependency audit** from GitHub Actions after they have run. Require the
   branch to be up to date before merging.
5. Enable **Restrict deletions** and **Block force pushes**.
6. Keep the bypass list empty unless an explicit maintainer recovery policy is
   adopted. Do not enable auto-merge as part of this setup.

Do not add a required check before its workflow is published and producing that
exact name. Copilot review is advisory, not a required approving review or a
replacement for the two deterministic checks.

## Automatic Copilot reviews

Use GitHub's native review feature, not an Actions bot. No Copilot token, PAT,
custom reviewer workflow, or cloud-agent environment setup is needed.

Copilot review availability depends on the requesting user's Copilot plan,
applicable policies, and usage allowance. Public visibility makes repository
rulesets available but does not itself grant Copilot review entitlement. Usage
can consume the applicable Copilot allowance/credits; confirm billing before
activation.

In **Settings > Rules > Rulesets**, create a separate active branch ruleset
named `copilot-review`:

1. Target **all branches** so PRs to non-default branches also qualify.
2. Enable **Automatically request Copilot code review**.
3. Leave **Review draft pull requests** off.
4. Leave **Review new pushes** off.
5. Save the rule and verify it with a new non-draft PR.

Eligible PRs receive an initial review on opening or becoming ready for review,
not an automatic review for every subsequent push. Request re-review from the
PR's Reviewers menu when needed. Other applicable rulesets or authors' personal
Copilot settings can independently enable draft/new-push reviews; keep those
settings off too if this behavior must be uniform. Copilot review entitlement or
policy may prevent reviews for some authors, including automation accounts;
verify both a maintainer PR and a Dependabot PR rather than assuming coverage.

`.github/copilot-instructions.md` supplies concise project-specific correctness,
privacy, indexing, and test guidance. Copilot review reads instructions from the
PR's base branch, so publish the file there before testing its effect on another
PR. Keep custom instructions enabled in Copilot review settings. The instruction
file does not trigger reviews. `AGENTS.md` remains the fuller contributor/agent
reference; keep the two consistent.

## Rollout and hosted acceptance

1. Review and publish these local files using the owner's normal PR process.
2. Confirm **Build and unit tests** and **Dependency audit** pass on a PR and
   after merging to `main`; download a TRX artifact and inspect the test result.
3. On the default branch, inspect **Insights > Dependency graph > Dependabot**
   for accepted configuration/update jobs covering all four ecosystems. Check
   alerts and enable security updates in repository settings.
4. Activate `main-protection` only after both checks exist. Verify a failing or
   missing check blocks a PR and direct force pushes/deletions are disallowed.
5. Activate `copilot-review` after confirming entitlement/usage. Verify a
   non-draft PR receives review, a draft waits until ready, and subsequent pushes
   do not automatically re-request review. Check relevant personal settings and
   verify an automation-authored PR separately.

Local build, test, audit, and YAML validation do not prove hosted triggers,
Dependabot execution, ruleset enforcement, or Copilot reviews work. Those remain
pending until the owner completes hosted acceptance. No live settings changes
are part of this local-file delivery.

## Official references

- [Managing rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets)
- [Configuring Copilot code review](https://docs.github.com/en/copilot/how-tos/copilot-on-github/set-up-copilot/configure-code-review)
- [Repository custom instructions](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/add-custom-instructions/add-repository-instructions)
- [Dependabot options](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference)
- [Configuring Dependabot security updates](https://docs.github.com/en/code-security/dependabot/dependabot-security-updates/configuring-dependabot-security-updates)
