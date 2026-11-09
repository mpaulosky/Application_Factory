---
status: accepted
---
# The Factory carries the Baseline's issue and PR process

The Factory is where new Applications get their way of working with issues and pull requests, yet its own repo ran on a fraction of it: no issue or PR template, no PR title check, no lint
workflows and no Dependabot. ADR 0006 and ADR 0008 added the git hooks, a gate, Claude Review and PR Auto-Merge and left the rest out, and CLAUDE.md says not to add the rest without an ADR.
This is that ADR. The Factory now carries the parts of the Baseline that are not about a .NET Application, as copies of the Template's Owned files (kept in step by `scripts/gate.sh`) or, where the
Template's file does not fit, a file of its own. It still carries no `.sandcastle/`, local review, .NET workflow, release workflow or `package.json`.

| Carried | How |
| --- | --- |
| Issue templates (`.github/ISSUE_TEMPLATE/{bug_report.md,feature_request.md,config.yml}`) and `.github/pull_request_template.md` | copies |
| `pr-title.yml` with `scripts/check-pr-title.sh` | copies |
| `lint-markdown.yml`, `lint-yaml.yml`, `lint-actions.yml` (actionlint, zizmor, shellcheck) | copies |
| `.github/dependabot.yml` | the Factory's own: the `github-actions` block of the Template's Seed, with its groups limited to minor and patch updates (see Consequences) |
| `.yamllint.yml`, `.markdownlint-cli2.jsonc` | the Factory's own, each starting from the Template's file and free to differ; the lint workflows read them, so an exclusion goes here, never in a copy |

Not carried: `dependabot-auto-merge.yml` (see Consequences), `docs/PROCESS.md`, `docs/CONTRIBUTING.md`, `docs/SECURITY.md` and `docs/CODE_OF_CONDUCT.md` (the first two describe an Application's flow
and its .NET required checks, and CLAUDE.md is the Factory's process document; the other two can follow if the repo gets outside contributors), `ci.yml`, `release.yml`, `code-metrics.yml`,
`codeql-analysis.yml`, `sandcastle.yml`, `backfill-blog-posts.yml` and `copilot-review-sync.yml`.

## Considered Options

- **Carry nothing more** (the status quo): no maintenance, but the Factory ships a way of working it does not use, a Factory PR gets no title check, and the ruleset standard
  (`github/settings.json`) names six required checks no Factory workflow reports.
- **Carry the whole Baseline**: rejected by ADR 0005 and ADR 0008; the .NET, release and Sandcastle workflows would fail or do nothing without a solution, a `package.json` and the Sandcastle pin.
- **Carry the process, not the .NET** (chosen): everything in the table is repo-agnostic, so it works in the Factory unchanged, and a new Application's way of working is the one the Factory
  itself is run by.
- **Carry Dependabot Auto-Merge too**, as the Fleet does: rejected. It merges a Dependabot PR on required checks alone, with no review. The Factory is what every Application is created from, and a
  bump to an action in `template/owned/` reaches the Fleet by a Rollout, so a bump nobody read is one Rollout from every repo.

## Consequences

- `scripts/gate.sh` checks the new copies the way it checks the hooks (the list grows; the gate fails when a copy drifts). A change starts in `template/owned/` and is copied over.
- The `Template tests` workflow already runs the same linters through `test.sh`. The lint workflows duplicate that work on purpose: they report the check names the ruleset standard requires
  (`PR title`, `markdownlint`, `yamllint`, `actionlint`, `zizmor`, `shellcheck`), so the Factory's ruleset is the Fleet's, not a special case.
  `github-settings.sh --fix` requires each check once a workflow on `main` reports it.
- `Build Solution`, `Test Suite` and `Branch name` stay unreported by any Factory workflow. Under the `factory` scope the settings check reports a required check no workflow produces as
  information, not as work to do, so its `MANUAL` line stays meaningful for the Fleet.
- The lint workflows run over the whole repo, Template and Seed files included. ADRs 0001 to 0005 held paragraphs past the 200-character limit that nothing linted, because the pre-commit hook checks
  staged files only; they are re-wrapped (text unchanged), not excluded. If a lint fails on a Seed file, the fix is an exclusion in the Factory's own `.yamllint.yml` or
  `.markdownlint-cli2.jsonc`, never an edit that changes what an Application receives.
- Dependabot PRs go through the normal path: the ruleset asks Copilot to review the head, and PR Auto-Merge merges the PR once the head is reviewed, its threads are resolved and `Template tests`
  is green. Claude Review does not cover Dependabot's PRs (they get no Actions secrets), so when Copilot cannot review one, a person reviews and merges it. With about one `github-actions` PR a
  week that costs little.
- The Factory's `dependabot.yml` keeps the Seed's weekly schedule, 7-day cooldown and its ignore of `anthropics/claude-code-action` (which moves only through the Template). Its group covers minor
  and patch updates only, so a major bump arrives as its own PR to look at, not as a line in a group.
- A Dependabot bump of an action in a copied workflow would edit `.github/workflows/` and not its source in `template/owned/`, so the gate would fail the copy and block the merge. The
  `dependabot.yml` therefore lists both directories, the repo root and `.claude/skills/repo-ci-baseline/template/owned`, in one group, so a single PR bumps the Template's file and its copy
  together. GitHub's documentation does not say that the `github-actions` ecosystem accepts a directory other than `/`, nor how a pattern group behaves across `directories`, so the PR that
  adds `dependabot.yml` tests it. If Dependabot does not group them, the fallback is that a person mirrors the bump (edit the Template's file, copy it over) before merging; an Update note on this
  ADR records which it is. The bump also changes what the Fleet receives, by a Rollout.
- Nothing is rolled out: no Owned Template file changes.
