---
status: accepted
---
# Markdown, YAML and shell are linted as they are written

The file keeps its first name, so links to it still work; the Update at the end added shell files.

A lint failure in a Markdown or YAML file used to surface late: at the commit (the `pre-commit` hook lints staged Markdown only), at the push (the Factory's gate runs `markdownlint-cli2` and
`yamllint` over the whole repo), in CI (`lint-markdown`, `lint-yaml`, `lint-actions`), or in a review, where it costs a re-review cycle. This ADR builds on ADR 0006 and ADR 0010.
Copilot review is not decided here: retiring it is the proposed [ADR 0014](0014-copilot-review-is-retired-once-the-local-review-catches-what-it-catches.md).

## Decision: lint the file when the tool writes it

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

## Considered Options

- **A hook per file at write time** (chosen): the feedback arrives while the agent still holds the file in context, and it lints one file, not the repo.
- **Lint at the end of the turn** (a `Stop` hook): it would lint every changed file once, but the agent has by then written more on top of the violation, and a `Stop` hook that blocks makes the turn
  loop.
- **Rely on the `pre-commit` hook and the gate**: they exist, and they are what keeps working when the hook does not. They run after the agent has moved on, and the gate's failure comes at the push,
  after the local review has recorded its result for a diff that the lint fix then changes.
- **Put the hook in the Template's `.claude/settings.json`**: rejected; `SKILL.md` keeps that file out of the Template, and a project hook command runs on every contributor's machine.

## Consequences

- Every Markdown or YAML edit by Claude costs one lint run of that one file. A repo whose linters are missing sees a note per edit (a `systemMessage`, shown to the person).
- The commit hook still lints only staged Markdown and has no YAML step. Neither changes here. Adding YAML to the commit hook is a later step.
- The script and its test are Owned Template files: they reach the Fleet by a Rollout (`rollout.sh`), and each Application then adds the hook to its `.claude/settings.json` by hand.
  `scripts/gate.sh` copies the script to the Factory and the `review-ledger.mts` copy gains the new path.
- A hook that lints with the wrong config would pass what CI fails. The test therefore checks the working directory, the config argument and the exact arguments each linter receives, which stream the
  report is on, and the repository boundary. It also runs the real `markdownlint-cli2` against a config with an `ignores` entry and names containing `[` and `*`, but only where that tool is installed
  (the Factory's CI and a developer's machine), not in an Application's hook-tests job.

## Update

The hook no longer runs what an agent could have written with no Bash prompt: a Markdown file is linted only through markdownlint configs that are regular files,
committed as they are, and unable to load JavaScript (no `.cjs` or `.mjs` config; no `customRules`, `markdownItPlugins`, `outputFormatters`, `modulePaths` or `extends`;
no backslash in one read as text), and the hook cleans `PATH` before it runs anything: no relative or empty entry, and none inside the project's checkouts (the
repository's `node_modules/.bin`, or another worktree's), so no linter or helper a linter looks up comes from there. Otherwise the file is left unlinted with a `systemMessage` saying why;
the pre-commit hook, the gate and CI still lint it. The
Template's `references/git-hooks.md` (Edit-time lint) has the detail.

The hook sees only what Edit, Write and MultiEdit write. A file an agent wrote through Bash (a heredoc, `sed -i`, a script) went unlinted until the commit hook for Markdown, and
until the gate or CI for YAML, which the commit hook doesn't lint.
The Factory's `CLAUDE.md` therefore says to write and edit Markdown and YAML with Write and Edit only. Extending the hook to Bash was not chosen: a Bash command
doesn't name the files it wrote, so the hook would have to diff the tree after every command.
The Template carries the same rule for Applications as the Owned `.claude/rules/markdown-and-yaml-edits.md`, which Apply writes into every repo, with or without the hook
wired in, and which loads in every session. In a repo that runs Sandcastle, where the review ledger is, a re-Apply that rewrites it needs a local review (`REVIEWED_ON_REAPPLY`),
as the hand-off rules do; elsewhere the re-Apply PR's own review is the check. It reaches the Fleet by a Rollout.

The hook lints shell files too, with the gate's two shell checks: `shellcheck`, and `shfmt -d` with no style flags, so it reads `.editorconfig` ([ADR 0015](0015-shell-files-are-formatted-with-shfmt.md),
whose Update makes an Application's gate check the changed shell files' format). A shell file is one the gate takes for one: a `*.sh`, an extensionless file in the root's
`scripts/`, or a git hook in the root's `.github/hooks`, or in a `.github/hooks` under a folder whose `.editorconfig` has a `[.github/hooks/*]` section (the Template's copy in the
Factory). shfmt skips the extensionless `scripts/` files, which no `.editorconfig` section styles, and runs only when it is v3.14 or a later v3 release, as the gate needs; another
is named in the `systemMessage`. Neither tool runs code from the repository, and shfmt reads only `.editorconfig`. shellcheck's `.shellcheckrc` can turn on `external-sources`, which
opens the files a `source` line names, and `--check-sourced` quotes their lines in the report the agent reads, a way round what the person's settings deny Claude. So the hook clears
`SHELLCHECK_OPTS`, reads the configs between the file and the repository root itself, and runs shellcheck with `--norc` and the nearest config's settings as the flags each
key has (`disable` as `-e`, `enable` as `-o`, `shell` as `-s`, `source-path` as `-P`, `extended-analysis` as `--extended-analysis`): shellcheck reads no config at all, so one
above the root, or one changed after the check, is never read. Two hand-overs were tried first and dropped. A temporary copy, a named file in a shared folder, can be swapped
before shellcheck reads it. A pipe (`--rcfile <(...)`) can be reached by another process running as the person through `/proc/<pid>/fd`, and is a named FIFO in `TMPDIR` on a
bash without `/dev/fd`. Flags leave nothing to reach. It doesn't run shellcheck under a config that is a symlink, not a regular file, unreadable, names `external-sources` or
`check-sourced`, sets a key outside an allow-list (`disable`, `enable`, `source-path`, `shell`, `extended-analysis`), gives one no value or a value of a form the hook
doesn't pass on, holds a NUL byte, has a line over 4,096 bytes or is over 65,536 in all (of which the hook reads no more than one byte past the cap), or sets `extended-analysis` for a
shellcheck whose `--help` doesn't list that flag (before 0.9); the note says so, and quotes an agent-written key with every byte but a letter, digit, space or `_ . + -` as `?`.
Every note of the hook, of every kind, goes through one filter that shows a control character other than a tab, a format character (a bidi
override, a zero-width one), a line or paragraph separator, or a byte that isn't UTF-8 as `?`, since a note quotes paths an agent named.
Plain `--norc` alone was not chosen: a repo's own `disable=` lines would then fail files its gate passes, and the flags keep them.
`test.sh` keeps the hook's hook-name and shfmt release rules equal to the gate's. The report's first line now says only that the gate checks the same file
before the push, which is true of every kind; the pre-commit hook lints only Markdown, and an Application's CI doesn't check shell format. A note about a hook under a nested
`.github/hooks` says instead that the gate checks it only where it takes that folder, since an Application's gate takes the root's hooks alone.

The Owned `.claude/rules/script-edits.md` says the same for shell scripts as the Markdown and YAML rule does (the hook lints no Python), and lists the checks reviewers keep
raising on script changes: a test that fails without each new branch, the header or docstring updated in the same edit, the edge cases that keep coming back (a missing tool
or another release, an empty, relative or odd path, CRLF or a byte-order mark, a symlink, a variable a caller left set), and the script's error contract. Its `paths:` front
matter loads it when a session works on a shell or Python script, not in every session. It is in `REVIEWED_ON_REAPPLY` too, since it steers how an agent writes code, and the
Factory carries a copy of it (`scripts/gate.sh` lists it): the Factory's sessions edit scripts more than anything else, and its `CLAUDE.md` holds no such checklist.
The Markdown and YAML rule has no copy, since the Factory's `CLAUDE.md` says the same.

These reach the Fleet by a Rollout: the Owned `scripts/lint-edited-file.sh`, `scripts/gate.sh` and `.claude/rules/script-edits.md` arrive at each Application's next
Rollout. All three are in `REVIEWED_ON_REAPPLY`, so that re-Apply needs a local review where the ledger is: the hook joined the set with this Update, since it now lints
each Markdown, YAML or shell file an agent writes, with no Bash prompt, and vets the configs its linters read. The Seed `.sandcastle/roles/shared-rules.md` and
`Dockerfile` changes never reach an Application that has them; it brings them over by hand (the skill's `references/sandcastle.md`, Migrating a repo).
