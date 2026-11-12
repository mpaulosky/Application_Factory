---
status: accepted
---
# Markdown and YAML are linted as they are written, and Copilot review is retired once the local review catches what it catches

A lint failure in a Markdown or YAML file used to surface late: at the commit (the `pre-commit` hook lints staged Markdown only), at the push (the Factory's gate runs `markdownlint-cli2` and
`yamllint` over the whole repo), in CI (`lint-markdown`, `lint-yaml`, `lint-actions`), or in a review, where it costs a re-review cycle. Copilot's review of a pull request also finds
problems the local review (ADR 0010) misses, and each one costs a fix commit, a new local review and a new CI run. The research note
[`docs/research/copilot-review-alignment.md`](../research/copilot-review-alignment.md) inventories those escapes. This ADR builds on ADR 0006 and ADR 0010 and makes two decisions.

## Decision 1: lint the file when the tool writes it

The Template gains `scripts/lint-edited-file.sh`, a Claude Code `PostToolUse` hook for Edit, Write and MultiEdit. It lints the one file the tool wrote: `markdownlint-cli2` for `*.md`; `yamllint` for
`*.yml` and `*.yaml`; `actionlint` and `zizmor --offline` as well for a workflow, and `zizmor` for `dependabot.yml`. These are the checks `lint-markdown`, `lint-yaml` and `lint-actions` run in CI; the
Factory's gate runs only the first two kinds over the whole repo, and an Application's gate runs `actionlint` and `zizmor` when a workflow changed. A problem exits 2 with the linters' output on
stderr (cut at 6,000 characters per linter), which Claude Code returns to the agent, so it fixes the file in the same turn. The script and its test are Owned; the reference
([`git-hooks.md`](../../.claude/skills/repo-ci-baseline/references/git-hooks.md#edit-time-lint)) is the skill's own.

- The hook lints only the project's own checkout and the linked worktrees `git worktree list` shows for `CLAUDE_PROJECT_DIR` (required; unset is an error), and never a symlink or a file under
  `node_modules/`. A linter loads the repository's own config and binaries, and `markdownlint-cli2` runs JavaScript from its config, so a file in another clone, or in a directory that only names the
  project's git directory, must not reach it. Comparing git common directories is not enough: git reports the project's for such a directory.
  The list is read with `git worktree list --porcelain -z`, so a worktree path with a newline cannot forge an entry. That needs git 2.36 or later: with an older or missing git the hook exits 1 with a
  message for a Markdown or YAML file, and does not skip it silently.
- The Factory carries a copy of the script (`scripts/gate.sh` checks the copy) and turns the hook on in its own `.claude/settings.json`. The Factory has no settings file today; this one holds the
  hook and nothing else, so no permission, model or environment setting is decided here. `scripts/tests/gate.test.sh` checks that the file names an executable script.
- The Template does not carry `.claude/settings.json` (permissions are per user, `SKILL.md`), so an Application adds the hook by hand. Registering it in every Application is a separate decision,
  not made here. The hook is not registered in `~/.claude/settings.json` for every repo: that would run each repo's linter config in a repo the person has not chosen to trust.
- The hook only helps. A linter that is not installed is named in a `systemMessage` and skipped, and the `pre-commit` hook, the gate and CI stay the checks that block, because a hook does not run
  for an edit made outside Claude Code and can be turned off.

## Decision 2: retire Copilot's review of pull requests, when the local review earns it

The `main-rules` ruleset requests a Copilot review on every push to a pull request (`copilot_code_review`). It is removed, through `github-settings.sh` and the Fleet's `main-rules.json`, only after the
local review catches the kinds of finding Copilot raised on past pull requests, measured by replaying those pull requests' diffs through the review and comparing (the research note's plan, items 1 to
6). Until then Copilot's review stays on, and every finding it makes becomes a test case for the local review. The removal is its own pull request, with the replay's numbers in the description.

## Considered Options

- **A hook per file at write time** (chosen): the feedback arrives while the agent still holds the file in context, and it lints one file, not the repo.
- **Lint at the end of the turn** (a `Stop` hook): it would lint every changed file once, but the agent has by then written more on top of the violation, and a `Stop` hook that blocks makes the turn
  loop.
- **Rely on the `pre-commit` hook and the gate**: they exist, and they are what keeps working when the hook does not. They run after the agent has moved on, and the gate's failure comes at the push,
  after the local review has recorded its result for a diff that the lint fix then changes.
- **Put the hook in the Template's `.claude/settings.json`**: rejected; `SKILL.md` keeps that file out of the Template, and a project hook command runs on every contributor's machine.
- **Remove Copilot review now**: rejected; the research note found 19 findings on four pull requests that had a clean local review. Removing the backstop before the review improves loses those
  catches.

## Consequences

- Every Markdown or YAML edit by Claude costs one lint run of that one file. A repo whose linters are missing sees a note per edit (a `systemMessage`, shown to the person).
- The commit hook still lints only staged Markdown and has no YAML step. Neither changes here. Adding YAML to the commit hook is a later step.
- The script and its test are Owned Template files: they reach the Fleet by a Rollout (`rollout.sh`), and each Application then adds the hook to its `.claude/settings.json` by hand.
  `scripts/gate.sh` copies the script to the Factory and the `review-ledger.mts` copy gains the new path.
- A hook that lints with the wrong config would pass what CI fails. The test therefore checks the working directory, the config argument and the exact arguments each linter receives, which stream the
  report is on, and the repository boundary. It also runs the real `markdownlint-cli2` against a config with an `ignores` entry and names containing `[` and `*`, but only where that tool is installed
  (the Factory's CI and a developer's machine), not in an Application's hook-tests job.
- Removing `copilot_code_review` later reduces review latency and the Fleet's Copilot usage; it also removes a second pair of eyes. The replay's recall number is what decides, not this ADR.
