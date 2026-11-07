---
status: accepted
---
# The Factory carries the git hooks and its own gate

The Factory is the source of truth for the Baseline but didn't use any of it: no git hooks, no gate, only the Template tests workflow. A commit in the primary checkout, a bad branch name or a
push of a broken Template met no local check, where `dotfiles` (which also isn't a .NET repo) has refused all three. The Factory now carries the Template's git hooks (`pre-commit`, `pre-push`,
`post-checkout`) and `scripts/check-branch-name.sh` as copies of the Template's Owned files, with a `scripts/gate.sh` of its own. It builds on ADR 0005, which kept the Factory off the full .NET
Baseline; that stays true.

The gate runs two things: a test of the gate itself (`scripts/tests/gate.test.sh`), a check that each copy matches the Template's file, and the skill's `test.sh`. CI runs the same gate. Edit the
Template's file, then copy it over; the gate fails on a
copy that has drifted.

## Considered Options

- **Apply the whole Template to the Factory**: one mechanism, but it adds `ci.yml`, `release.yml`, `GitVersion.yml`, `global.json`, `codecov.yml` and the blog and coverage workflows to a repo
  with no solution, and a Template change would then ask the Factory for a Rollout. ADR 0005 ruled this out.
- **No local checks, CI only**: nothing to maintain, but a push to a bad branch name or a commit on the primary checkout is caught late, or not at all.
- **Hooks, branch-name check and a gate now; the review layer and Sandcastle later** (chosen): the cheap checks the Template's own authors rely on, with no pipeline to run.

## Consequences

- Commits are made in a linked worktree under `../Application_Factory-worktrees/`, and the hook refuses one in the primary checkout. `git config core.hooksPath .github/hooks` turns the hooks on
  in each clone; the setting is required, because git doesn't run any hook until it is on. `post-checkout` can only repair the setting and the execute bits after that; it can't bootstrap a fresh
clone, so CI runs the same gate for a push from a clone without hooks.
- A push is gated by `scripts/gate.sh`, so it runs `test.sh` and needs its tools (`actionlint`, `zizmor`, `yamllint`, `shellcheck`, `markdownlint-cli2`, `node`, `git`).
- `scripts/gate.sh` is the Factory's own and is not overwritten by any Apply. The hooks and `check-branch-name.sh` are copies, kept in sync by the gate, as `dotfiles` does.
- Not carried yet: the local review (`/pre-push-review`, `scripts/review.sh`), Claude Review, PR Auto-Merge, Sandcastle and `scripts/watch-pr.sh`. Each needs its own decision.
