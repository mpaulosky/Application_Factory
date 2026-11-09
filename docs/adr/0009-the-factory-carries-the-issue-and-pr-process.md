---
status: proposed
---
# The Factory carries the Baseline's issue and PR process

The Factory is where new Applications get their way of working with issues and pull requests, yet its own repo ran on a fraction of it: no issue or PR template, no PR title check, no lint
workflows, no
Dependabot. ADR 0006 and ADR 0008 added the git hooks, a gate, Claude Review and PR Auto-Merge and left the rest out, and CLAUDE.md says not to add the rest without an ADR. This is that ADR.
The Factory now carries the parts of the Baseline that are not about a .NET Application, as copies of the Template's Owned files (kept in step by `scripts/gate.sh`) or, where the Template's
file does not fit,
a file of its own. It still carries no `.sandcastle/`, local review, .NET workflow, release workflow or `package.json`.

| Carried | How |
| --- | --- |
| Issue templates: `.github/ISSUE_TEMPLATE/{bug_report.md,feature_request.md,config.yml}`, and `.github/pull_request_template.md` | copies |
| `pr-title.yml` with `scripts/check-pr-title.sh` | copies |
| `lint-markdown.yml`, `lint-yaml.yml`, `lint-actions.yml` (actionlint, zizmor, shellcheck) | copies |
| `dependabot-auto-merge.yml` | copy |
| `.github/dependabot.yml` | the Factory's own: the `github-actions` entry only, as the Template's other ecosystems (nuget, dotnet-sdk, npm) have nothing here to update |
| `.yamllint.yml`, `.markdownlint-cli2.jsonc` | the Factory's own, each starting from the Template's file and free to differ (the Markdown one already does, with its ignore list); the lint workflows read them, so an exclusion for a Template or Seed file goes here, never in a copy |

Not carried: `docs/PROCESS.md` and `docs/CONTRIBUTING.md` (they describe an Application's flow and its .NET required checks; CLAUDE.md is the Factory's process document), `ci.yml`,
`release.yml`,
`code-metrics.yml`, `codeql-analysis.yml`, `sandcastle.yml`, `backfill-blog-posts.yml` and `copilot-review-sync.yml`.

## Considered Options

- **Carry nothing more** (the status quo): no maintenance, but the Factory ships a way of working it does not use, a Factory PR gets no title check, and the ruleset standard
  (`github/settings.json`) names
  six required checks no Factory workflow reports.
- **Carry the whole Baseline**: rejected by ADR 0005 and ADR 0008; the .NET, release and Sandcastle workflows would fail or do nothing without a solution, a `package.json` and the
  Sandcastle pin.
- **Carry the process, not the .NET** (chosen): everything in the table is repo-agnostic, so it works in the Factory unchanged, and a new Application's way of working is the one the Factory
  itself is run by.

## Consequences

- `scripts/gate.sh` checks the new copies the way it checks the hooks (the list grows; the gate fails when a copy drifts). A change starts in `template/owned/` and is copied over.
- The `Template tests` workflow already runs the same linters through `test.sh`. The lint workflows duplicate that work on purpose: they report the check names the ruleset standard requires
  (`PR title`, `markdownlint`,
  `yamllint`, `actionlint`, `zizmor`, `shellcheck`), so the Factory's ruleset is the Fleet's, not a special case.
- `github-settings.sh --fix` then requires those six checks in the Factory's ruleset. `Build Solution`, `Test Suite` and `Branch name` stay unreported by any Factory workflow, so the check
  keeps its `MANUAL` line
  for them; the `factory` scope should report checks no workflow produces as information, not as work to do (a small follow-up).
- The lint workflows run over the whole repo, Template and Seed files included. If one of them fails on a Seed file (a YAML without a document start, say), the fix is an exclusion in the
  Factory's own `.yamllint.yml` or `.markdownlint-cli2.jsonc`, never an edit that changes what an Application receives. Check this in the implementing PR before the checks are made required.
- `dependabot-auto-merge.yml` arms native auto-merge on Dependabot's PRs once their required checks pass, without Copilot's review: the Baseline's policy for Dependabot (see
  `references/automerge.md`), now also the Factory's.
  With pinned actions required, those PRs are how the Factory's SHA pins stay current.
- A Dependabot bump of an action in a copied workflow would edit `.github/workflows/` and not its source in `template/owned/`, so the gate would fail the copy and block the merge. The Factory's
  `dependabot.yml` therefore lists both directories, the repo root and `.claude/skills/repo-ci-baseline/template/owned`, in one `github-actions` group, so a single PR bumps the
  Template's file and its copy
  together and the gate stays green. That also bumps what the Fleet receives, by a Rollout; the same bump reaches each Application through its own Dependabot. Whether Dependabot groups across the two
  directories as expected is not verified here: the implementing PR checks it, and if it does not, the fallback is that a maintainer mirrors the bump (edit the Template's file, copy it over) before merging.
- Nothing is rolled out: no Owned Template file changes.
