---
status: accepted
---
# The Template tests workflow runs the gate in parallel parts, and the pre-push gate runs the Sandcastle check when its files changed

The `repo-ci-baseline Template` workflow took 9 to 13 minutes on every pull request. It ran one job with two steps in a row: `scripts/gate.sh` (about 10 minutes on a GitHub runner) and the
Sandcastle check (about 3 minutes). The pre-push hook already runs the same gate, so a push from a clone with the hooks on had run everything except the Sandcastle check, which `CLAUDE.md`
only asked people to run by hand. This ADR builds on ADR 0006, which gave the Factory the gate and made CI run the same gate for a push from a clone without hooks.

## Decision

- **CI runs the gate one part per job, in parallel.** `scripts/gate.sh` takes `GATE_PART` (`self`, `static`, `template` or `sandcastle`): `self` is the gate's own test and the review's tests,
  `static` the copies, the issue-reference and `git status` checks, the Markdown and YAML lint and shellcheck, `template` the local review check and `test.sh`, and `sandcastle` the Template's
  Sandcastle check. With no `GATE_PART` every part runs, in that order, so the pre-push hook and a gate started by hand are unchanged. The workflow has a job per part, each installing the same
  tools through one local action (`.github/actions/template-tools`), and the run takes as long as its slowest part instead of the sum.
- **One job still gates the pull request.** `Template tests` keeps its name, because the ruleset requires it by name and PR Auto-Merge, the landing scripts and the Sandcastle host read it by
  name. It now `needs` every part job, runs with `always()`, and fails unless every part passed: a skipped required check counts as passing, so a part that failed or was skipped must not leave it
  skipped. A gate test (`scripts/tests/gate.test.sh`) fails when a part has no job, a part runs twice, or `Template tests` stops needing one.
- **The gate runs the Sandcastle check when the branch changed a file it reads.** In a full gate the check runs when the diff against the merge base with `origin/main` touches the Template's
  `.sandcastle/` code, the Seed's package files, or the check and its tests, and also when that can't be told (no `origin/main`, a git failure). Its pass is remembered for a clean tree, as
  `test.sh`'s is. A part run by name always runs, so CI is unchanged by the path rule.
- **A push never runs part of the gate.** The pre-push hook drops `GATE_PART`, as it drops `GATE_SKIP_REVIEW` and `GATE_SKIP_SELF_TEST`. This is a change to a Template Owned file (`pre-push`), so it
  reaches the Fleet by a Rollout; an Application's gate has no parts, so the change does nothing there.

## Considered Options

- **Trust the local gate and skip it in CI**: the fastest, but a push with `--no-verify`, from a clone without `core.hooksPath`, from a Claude cloud session or from GitHub's web editor never ran
  the hook, and CI has no way to verify that a local pass happened. The required check would stop meaning anything. Rejected.
- **Split the workflow into several workflows**: the same parallelism, but each workflow is its own check name, and the ruleset, PR Auto-Merge and the Sandcastle host would each need to know all
  of them. One workflow with an aggregating job keeps one name.
- **Split `test.sh` into shards as well**: `test.sh` is now the slowest part (about 5 minutes). Its sections are independent, so a later change can shard it; not done here, because it changes the
  Template's own test runner and the parts alone take the run from the sum to the maximum.
- **Run only the parts a diff needs in CI** (path filters): saves the most on a docs-only change, but a wrong filter silently stops testing something, so it waits until the parts have run in
  production. It would be decided inside the jobs, so the required check still reports.

## Consequences

- A pull request's `Template tests` check finishes when its slowest part does, about 5 to 6 minutes by the timings of the last runs, instead of 9 to 13. The aggregator adds a few seconds. Four
  runners start per pull request instead of one, so the billed minutes are about the same or slightly higher.
- A pull request shows five checks instead of one; only `Template tests` is required, and the others name the part that failed.
- The tools are installed in each job (about 20 seconds). The action pins the same versions the workflow did, and puts `actionlint` in `/usr/local/bin` instead of writing `GITHUB_PATH`, which
  zizmor's `github-env` audit flags in an action file.
- A push that changes the Sandcastle files takes the Sandcastle check's time (a few minutes, the network and the newest packages the Seed's ranges allow) locally; one that doesn't is unchanged.
  Without this, a Sandcastle failure was first seen in CI.
- The Application Factory's gate gains a mode; an Application's does not, and nothing else in the Template changes.
