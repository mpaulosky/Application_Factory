---
status: accepted
---
# CI runs test.sh in shards, and the review's tests as their own part

ADR 0012 split the `repo-ci-baseline Template` workflow into one job per gate part, which took a pull request's run from about 13 minutes to about 5½. The run
now takes as long as its slowest job: the `template` part, which is `test.sh` (about 5 minutes), followed by the `self` part (`scripts/tests/gate.test.sh`, about
3 minutes, then the review's stand-alone tests, about 1 minute). ADR 0012 deferred two ways of going further: sharding `test.sh`, and path filters that skip the
parts a diff doesn't need. A look at the last 40 commits on `main` decided between them. 30 changed the skill, which `test.sh` reads, and `test.sh` also reads the
Factory's own scripts, skills and `docs/adr/`. A filter would have skipped the `template` part on about 1 pull request in 20, and the Sandcastle part, which already
finishes before `test.sh`, saves no time when skipped.

## Decision

- **`test.sh` runs in three shards.** `TEST_SH_SHARD` (`hooks`, `scripts` or `checks`) runs one: `hooks` is the hook and Owned script tests, `scripts` the auto-merge,
  release, hardening-review, renderer, GitHub script and benchmark tests, `checks` everything from actionlint on (lint, the Owned-file checks, the Sandcastle workflow
  and Seed checks, and the Apply and Reapply smoke tests). With no `TEST_SH_SHARD` every shard runs, in that order, as before. Every step sits in exactly one shard, and
  `scripts/tests/gate.test.sh` fails when a step falls outside them, a shard appears twice, or the declared list and the blocks disagree.
- **Only the `template` part, run by name, runs a shard.** `scripts/gate.sh` passes `TEST_SH_SHARD` to `test.sh` when `GATE_PART=template`, and unsets it for every other
  run, so a push, a full gate and `--quick` run the whole of `test.sh` whatever the environment holds. Each shard's pass is remembered for a clean tree on its own, apart
  from the whole run's. The whole run and a shard overlap, so a whole run first clears every shard's pass for the tree and a shard run clears the whole
  run's: a failure leaves no pass standing that covers what failed. Shards don't overlap, so one shard's run leaves another's pass. The pre-push hook, a Template Owned file, doesn't change.
- **The workflow's `template` job is a matrix over the shards.** `Gate - test.sh (<shard>)` runs once per shard with `fail-fast: false`. The aggregator still needs one
  `template` result, which GitHub reports as `failure` when any shard failed, so `Template tests` still fails unless every shard passed. The gate test checks that the
  matrix lists every shard `test.sh` declares, once, and that the step passes the matrix's shard.
- **The review's stand-alone tests are their own part, `review`.** `GATE_PART=self` runs `scripts/tests/gate.test.sh` alone and `GATE_PART=review` the three
  `.sandcastle/lib/` tests. A full gate and `--quick` run both, in the same order as before. The aggregator needs five parts.

## Considered Options

- **Shard `test.sh` and split the `self` part** (chosen): every pull request gets faster, nothing is ever skipped, and the shards are the same code the pre-push hook runs.
- **Path filters** (ADR 0012's other deferred option): rejected for now, for the history above. It saves about 4½ minutes on few pull requests, at the cost of a skip
  list that must change whenever `test.sh` starts reading a new file, and a wrong list silently stops a test running.
- **Shard `test.sh` alone**: `self` (about 4 minutes) would then be the slowest job, so the run would gain about a minute and a half instead of two.
- **Move the review's tests into the `static` part** instead of a new part: no fifth job, but the gate test's pnpm cases run the gate with a bare `PATH`, where the
  static part's linters don't exist, so they would have to change to keep testing the install step. Rejected.

## Consequences

- A pull request's run should take about 3 to 3½ minutes. Locally the shards took 96, 112 and 49 seconds and `gate.test.sh` about 3 minutes, so `self` becomes the
  slowest job; sharding `gate.test.sh` is the next step if that matters.
- Eight jobs start per pull request instead of five: the `template` part's three shards, the four other parts and the aggregator. Each part job installs the tools
  (about 20 seconds), so billed minutes rise slightly.
- A new step in `test.sh` must go inside a shard block. The gate test fails if it doesn't.
- `test.sh` is the skill's, not a Template Owned file, so nothing reaches the Fleet and no Rollout is needed.
