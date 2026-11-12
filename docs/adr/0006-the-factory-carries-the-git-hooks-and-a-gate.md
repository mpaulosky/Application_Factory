---
status: accepted
---
# The Factory carries the git hooks and its own gate

The Factory is the source of truth for the Baseline but didn't use any of it: no git hooks, no gate, only the Template tests workflow. A commit in the primary checkout, a bad branch name or a
push of a broken Template met no local check, where `dotfiles` (which also isn't a .NET repo) has refused all three. The Factory now carries the Template's git hooks (`pre-commit`, `pre-push`,
`post-checkout`) and `scripts/check-branch-name.sh` as copies of the Template's Owned files, with a `scripts/gate.sh` of its own. It builds on ADR 0005, which kept the Factory off the full .NET
Baseline; that stays true.

The gate runs two things: a test of the gate itself (`scripts/tests/gate.test.sh`), a check that each copy matches the Template's file, and the skill's `test.sh`. CI runs the same gate. Edit the
Template's file, then run `scripts/gate.sh --sync` to copy it over; the gate fails on a
copy that has drifted. Later records added to the gate the Markdown and YAML lint,
the bare-`#N` issue-reference check and the `git status --porcelain` guard check (`tests/check-git-status.sh`), both also in `--quick`, and the local review check. ADR 0012 splits the gate into
parts that CI runs in parallel, and adds the Sandcastle check, which a full gate runs when the branch changed a file it reads.

## Considered Options

- **Apply the whole Template to the Factory**: one mechanism, but it adds `ci.yml`, `release.yml`, `GitVersion.yml`, `global.json`, `codecov.yml` and the blog and coverage workflows to a repo
  with no solution, and a Template change would then ask the Factory for a Rollout. ADR 0005 ruled this out.
- **No local checks, CI only**: nothing to maintain, but a push to a bad branch name or a commit on the primary checkout is caught late, or not at all.
- **Hooks, branch-name check and a gate now; the review layer and Sandcastle later** (chosen): the cheap checks the Template's own authors rely on, with no pipeline to run.

## Consequences

- Commits are made in a linked worktree under `../Application_Factory-worktrees/`, and the hook refuses one in the primary checkout. `git config core.hooksPath .github/hooks` turns the hooks on
  in each clone; the setting is required, because git doesn't run any hook until it is on. `post-checkout` can only repair the setting and the execute bits after that; it can't bootstrap a fresh
clone, so CI runs the same gate for a push from a clone without hooks.
- A push is gated by `scripts/gate.sh`, so it runs `test.sh` and needs its tools (`actionlint`, `zizmor`, `yamllint`, `shellcheck`, `markdownlint-cli2`, `node`, `git`, and
  `pnpm` on the first run, for the review's tests). A push that changes what Sandcastle's check reads also runs that check, which needs the network (ADR 0012).
- The gate also runs ShellCheck on the shell files the branch changed (so `--quick` catches what the full gate's `test.sh` would), and skips `test.sh` for a clean tree that already passed it
  (`GATE_NO_CACHE=1` runs it again). These are the gate's own conveniences: they add no Baseline file.
- `scripts/gate.sh` is the Factory's own and is not overwritten by any Apply. The hooks and `check-branch-name.sh` are copies, kept in sync by the gate, as `dotfiles` does.
- Not carried yet: Sandcastle, `scripts/watch-pr.sh`, `scripts/work-issue.sh` and `scripts/cleanup.sh` (Owned Template files that reach the Fleet by a Rollout). Each needs its own decision.
  Claude Review and PR Auto-Merge: ADR 0008. The local review: ADR 0010.
