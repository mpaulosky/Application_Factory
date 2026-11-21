# Git hooks and branch names

Template files (Owned), among them: `.github/hooks/` (`pre-commit`, `pre-push`, `post-checkout`), `.github/hooks/tests/`,
`scripts/gate.sh`, `scripts/check-branch-name.sh` with its test,
`scripts/check-git-hooks.sh` ([Session-start check](#session-start-check)) and `scripts/lint-edited-file.sh` ([Edit-time lint](#edit-time-lint)) with their tests,
`.claude/rules/markdown-and-yaml-edits.md` and `.claude/rules/script-edits.md` (the [Edit-time lint](#edit-time-lint)'s session rules),
`scripts/work-issue.sh` and `scripts/cleanup.sh` with their tests, and the `branch-name` job in `ci.yml`. History: IssueTracker #182 (the branch standard).

## Branch-name standard

Every repo enforces the same rule twice, with one script, `scripts/check-branch-name.sh`: the pre-push hook checks
each pushed branch, and `ci.yml`'s **Branch name** job, a required status check, checks a PR's head branch.

| Branch | Use |
| --- | --- |
| `feature/{issue}-{slug}` | New behaviour |
| `fix/{issue}-{slug}` | A bug fix |
| `hotfix/{issue}-{slug}` | An urgent fix |
| `chore/{slug}` | Work without an issue |

`{slug}` is lowercase letters and digits in words joined by `-` (`upgrade-net10`, `oauth2-login`). A `chore/` slug
starts with a letter, so it can't pass for an issue-linked name (`chore/7-cleanup` is refused). The check:

```bash
[[ "$branch" =~ ^(feature|fix|hotfix)/[0-9]+-[a-z0-9]+(-[a-z0-9]+)*$ ]] || [[ "$branch" =~ ^chore/[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]]
```

`squad/` and `sprint/` are retired.

**CI is the gate; the hook is the early warning.** A push can skip the hook: a Claude cloud session (claude.ai/code)
pushes `claude/...` branches from a sandbox where `core.hooksPath` was never set, and so do GitHub's web editor and any
clone without the one-time setup. Before the CI check, such branches reached PRs in four repos (Blazor-Server #113,
TicketManager #95, IssueManager #222, Articles #272, all `claude/project-thread-ot9sjt`). The CI job refuses them
until the branch is renamed. Rename it on GitHub (the repo's Branches page, or
`gh api -X POST repos/<owner>/<repo>/branches/<branch>/rename -f new_name=fix/42-null-children`, with `/` in the old
name written as `%2F`) and the open PR follows the new name. Then close and reopen the PR (`gh pr close <n>` and
`gh pr reopen <n>`): a rename makes no new commit, so no check runs, and re-running the failed check reuses the old
name. The reopened run checks the new one. A repo's `CLAUDE.md` can ask cloud sessions to name the branch to the
standard in the first place.

The check guards the process, not against a hostile PR. Under `pull_request` the job runs the PR's own copy of the
script and of `ci.yml`, so a PR can change either, as it can for `Build Solution`; such an edit shows in its review.

The CI job also accepts the branches that only Actions and Dependabot create, which no hook sees: `docs/release-notes` (`release.yml`),
`docs/backfill-blog-posts` (`backfill-blog-posts.yml`) and, from a PR `dependabot[bot]` opened, any `dependabot/*` whatever its characters (its branch can't be renamed).
The job runs `check-branch-name.sh --pr --author "$PR_AUTHOR" "$HEAD_REF"`, with the author from `github.event.pull_request.user.login`, which a pusher can't set; `test.sh` pins both lines.
The hook, and the CI job for anyone else's PR, accept a `dependabot/*` branch in shell-safe ASCII characters only (`A-Za-z0-9._@+-` per segment), so a person can push a fix to a Dependabot PR.
A refused name is printed with its control characters escaped (`printf %q`), so a newline in it can't start a line of its own in the log.
Fork PRs skip the job, which a required check counts as passing.

## Worktree standard

Every change is made in its own linked worktree, never in the primary checkout. The primary checkout stays on `main`
for pulls and dry-run surveys. A branch's worktree lives beside the repo, in `../<Repo>-worktrees/`, named after the
branch with `/` turned into `-`:

```bash
wt="$(scripts/work-issue.sh 42)" && cd "$wt"
```

`scripts/work-issue.sh <n>` (exactly one argument) reads issue `n` with `gh`, fetches and prunes `origin`, and makes its branch and worktree, then prints the path. The branch starts from the local
branch of that name if there is one (checked out as it is, even if it is behind `origin`), else from `origin/<branch>` when that branch is on `origin`, else from `origin/main`. The branch is
`fix/{n}-{slug}` for an issue labelled `bug` (any case), `feature/{n}-{slug}` for any other; the slug is the title's lowercase `a-z0-9` words (a leading `[tag]` dropped, at most 6 words and 50
characters, `issue` when none is left). An issue renamed since keeps the branch already named for its number, local or on `origin` (the branch the title gives wins when it exists; else the first in
name order, local first); a branch with the number that is outside the branch standard, under another prefix, or with leading zeros in the number is skipped. A branch it makes from `origin/main` has
no upstream (it would track `main`, and a plain `git push` would refuse): the first push is `git push -u origin HEAD`; one made from `origin/<branch>` tracks that branch. It reuses the branch's
worktree if there is one and its folder is still a worktree of this clone with the branch checked out (a folder recreated or replaced since, or one git can't read, such as a `safe.directory`
refusal, is refused: fix that, or move the folder away and run `git worktree prune`); git's entry for one whose folder was deleted by hand is forgotten (that one only, and only a real directory
entry) and the worktree made again. It refuses, with a hint, a locked worktree whose folder is missing, one whose folder can't be reached (its parent folder is missing, empty, unreadable or
unsearchable, which an unmounted drive also looks like: run `git worktree prune` if it was deleted for good), the primary checkout holding the branch, and a directory of that name that is not a
worktree of the branch. When `origin` can't be fetched (but `gh` works: the issue is read first) it still prints a worktree the branch already has, with a warning, and stops otherwise. It needs `gh`
logged in, and for an origin that is not on github.com, `GITHUB_REPOSITORY`. Work without an issue is made by hand:
`git worktree add --no-track -b chore/upgrade-net10 ../<Repo>-worktrees/chore-upgrade-net10 origin/main`.

Once the PR merges, `scripts/cleanup.sh` (run in the primary checkout, on a clean `main`) fast-forwards `main`, then removes each merged branch with its worktree and its `origin` branch. It decides
from the PR's state on GitHub (a squash merge leaves no ancestry for `git branch --merged`): the PR must be from the repository `origin` points at (a fork's PR of the same name doesn't count),
merged into `main`, and the branch's tip must be the PR's last head, so no unpushed commit is lost. It keeps, with the reason, a branch with an open PR, no PR, 100 or more PRs of that name, a PR
merged elsewhere, a commit made after it was judged, a branch deleted after it was judged, a branch a worktree is rebasing or bisecting, a dirty worktree, or a worktree whose folder can't be reached
(and that local branch's `origin` copy too), and removes a closed-unmerged one only with `--include-closed`. It also keeps a branch an open PR is based on (a stacked PR, looked up in the repository
`origin` points at), which deleting would close. It deletes a local branch only if it still holds the tip it judged (compared in the delete itself). It deletes the `origin` branch only if `origin`
still holds the tip it judged (one GitHub already deleted counts as removed, and so does a local branch that vanishes while its own delete runs), and for a worktree whose folder was deleted by hand
it forgets git's entry for that one worktree only (a locked one is kept; a parent folder that is missing, empty, unreadable or unsearchable may be an unmounted drive, so the branch is kept with the
hint to run `git worktree prune`). It needs `gh` logged in, and for an `origin` that is not on github.com, `GITHUB_REPOSITORY`. It exits 1 when it can't run (not on `main`, an `origin` push URL that
is not shown to be the fetch URL's repository (the same text, or both github.com URLs for the same owner/name; `CLEANUP_PUSH_URL_OK=1` accepts push URLs you have checked,
and warns about each; both messages print a scheme URL's text up to the last `@` before the first `/`, its userinfo, which can hold a token, as `***`, add a note when that text holds a
`?`, `#`, `\` or second `@` (another URL reader may take another host), and print every byte that is not printable ASCII, a control character or a bidi override among them, as `?`), not
the primary checkout, a dirty `main`, no `gh` or repository, a bad argument), can't fetch `origin`, `main` can't fast-forward or a `gh` lookup fails, and, after doing the rest, when a removal git
refused, a branch a worktree is rebasing or bisecting (a real run only), a check of `origin` or the closing fetch failed, or a commit was made, or the branch deleted, after a branch was judged.
`--dry-run` shows the steps. `git worktree remove` alone leaves the local and `origin` branches behind.

`pre-commit` enforces the rule that a change is committed in a linked worktree. Claude Code's own `.claude/worktrees/` (ignored by the Template's
`.gitignore`) also passes the check, since any linked worktree does, but people use `../<Repo>-worktrees/`.

## The hooks

Hooks live in `.github/hooks/` and are switched on once per clone with `git config core.hooksPath .github/hooks`.
`apply.sh` sets it in the clone it Applies to; the repo's README or CONTRIBUTING tells everyone else.

- **`post-checkout`** repairs `core.hooksPath` and the hooks' execute bits after checkouts. Git only runs it once
  `core.hooksPath` already points at `.github/hooks`, so it can't bootstrap a fresh clone: its header comment says this,
  and names the one-time command.
- **`pre-commit`** first refuses a commit made in the primary checkout (its git dir is the common one), naming the
  commands to use instead: `git worktree add --no-track -b` for a new branch, or, when the primary checkout is already on its
  own branch, `git switch main` and `git worktree add` to move that branch into a worktree. Two cases are exempt: a
  repo with no commits on any branch (an orphan branch in a repo with history is not), so a new repo's Apply can be
  its first commit on `main`, and a clone with `git config baseline.allowPrimaryCommits true`, for a clone that can't
  use worktrees (a sandbox or a one-off). Hooks don't run in Actions, so the release workflow's commits are unaffected.
  Then it lints the staged content of Markdown files; see [pre-commit.md](pre-commit.md).
- **`pre-push`**, in order (step 3 runs `scripts/check-branch-name.sh`):
  1. Read the refs from stdin, and gate the refs being pushed, not the checked-out branch. Classify by the *remote* ref,
     so a tag pushed onto a branch still counts as a branch push.
  2. Refuse pushes to, and deletions of, the protected branches (`main`, `preview`, `dev`).
  3. Refuse a branch name outside the standard.
  4. Refuse a pushed commit that isn't the checked-out `HEAD`, because the gate only tests the working tree.
  5. Refuse a working tree with uncommitted or untracked changes, for the same reason. Ignored files don't count.
  6. A push with no branch updates (tags only, or deletions only) skips the gate.
  7. Refuse a branch behind `origin/main`: `main-rules` requires a PR to be up to date before it merges, so its PR
     couldn't merge as pushed (it would sit at `BEHIND`), and the gate would test it against an old main. The hook
     fetches main first; offline it compares with the last fetched copy, and with no `origin/main` it skips the
     check. The fix it names is `git merge origin/main`, which needs no force push; squash merges drop the merge
     commit. From Articles' unmerged `chore/pre-push-main-sync-gate`, written after a branch kept being pushed once an earlier push had auto-merged.
  8. Run `scripts/gate.sh`. No config key skips it any more: the old `sandcastle.gatedHead` skip (Blazor-Server #93)
     was removed after Blazor-Server #188's review, because a Sandcastle sandbox can write `.git/config`. That
     doesn't make the hook a defence against a writable `.git/config`. The hook only runs because the clone's
     config sets `core.hooksPath`, so anything that can write that file can point it elsewhere and turn the hook
     off. The hook is a local convenience; the ruleset and CI are the gate. A Sandcastle repo must keep agents
     from writing the clone's `.git/config`, and run all host git with hooks off, since the relative `hooksPath`
     makes each worktree's own `.github/hooks/*` the ones that fire; see step 7 of the Sandcastle setup in the
     README.
- **`scripts/gate.sh`**, also safe to run by hand: lint the Markdown, YAML, workflow, shell and Python files changed since
  `origin/main`, using CI's configs, and prefer installed tools with pinned fallbacks. The changed files are read NUL-separated
  (`git diff -z`, or `git ls-files -z` without an `origin/main`; a git that fails stops the gate) and sorted into the lint lists in bash
  under `LC_ALL=C`, so each name is matched whole and byte for byte: a non-ASCII name, one that isn't valid UTF-8, or one with a tab or
  newline reaches its linter as it is on disk. Markdown names go to `markdownlint-cli2` as `:<name>`, its literal-path form, so `a[1].md`
  isn't read as a glob and `-dash.md` isn't read as an option; YAML and shell names go to `yamllint` and `shellcheck` after a `--`, so
  `-dash.yml` and `-dash.sh` aren't either. The **Shell format** step then runs `shfmt -d --` on the changed shell files `.editorconfig` gives the shell style to
  (a `*.sh` or a hook in `.github/hooks`; an extensionless `scripts/` file is in no section, so shellcheck alone checks it), with no style flags, so shfmt reads
  `.editorconfig`: a repo's own scripts take the style as they are touched, with no one-off reformat ([the Factory's ADR 0015](../../../../docs/adr/0015-shell-files-are-formatted-with-shfmt.md)).
  It needs shfmt v3.14 or a later v3 release, which `check_gh_repo.py` needs too, and stops, naming it, without one. shfmt exiting 1 is a diff or a file it can't parse, and
  any other status stops the gate as a failed shfmt, with no `shfmt -w` advice. Without an `origin/main` it is skipped with a note, since the
  changed files are then every tracked file. The **Python lint** step runs ruff's bug-finding rules on the changed `*.py` files, with the flags the edit-time lint
  passes (`check --isolated --no-cache --select F,B --target-version py312 --`): `--isolated` reads no `pyproject.toml` or `ruff.toml`, so every repo checks the same rules
  ([the Factory's ADR 0011](../../../../docs/adr/0011-markdown-and-yaml-are-linted-as-they-are-written.md), Update: Python). An installed ruff must be a 0.17 release, since a
  later one adds rules (another, or a `--version` that fails, stops the gate, naming what it printed with every byte but printable ASCII as `?`); without one it runs
  `uvx ruff@0.17.6`, and without uv it stops, naming both. ruff exiting 1 is a finding, and any other status stops the gate as a failed ruff. Unlike shell format it runs
  without an `origin/main` too, on every tracked Python file, as the shell lint does. `tests/owned-gate-files.test.sh` (the Factory's) runs those lines in a throwaway
  repo, in a UTF-8 locale and in C, the YAML, shell lint, shell format and Python lint steps with stubs, and the Markdown step with a stub and, when installed, the real tool;
  `.github/hooks/tests/pre-push.test.sh` runs the whole gate with the real shfmt, and shows it reads the repo's `.editorconfig`. Then build and run each test
  project, where the stack allows it locally. Which steps it runs depends on the repo; what's standard is that
  everything it checks, CI checks too, except the Shell format step (CI would need the PR's base to know which files changed), the Python lint of a repo's own Python
  (CI's `python-tests` job lints only the Template's: `.github/scripts` and the hardening-review scripts) and the local review check below.
  After the lints it runs `python3 -I .github/scripts/check_gh_repo.py --repo`, which refuses a `gh` command in the
  repo's own `scripts/`, `.github/ci/` and skills that isn't told its repository (not the Owned Baseline files;
  [gotchas.md](gotchas.md)). It reads each script with shfmt v3.14 or a later v3, which must be installed: without it the check fails
  ([ADR 0011](../docs/adr/0011-the-gh-repo-check-parses-shell-with-shfmt.md)).
  Then it checks that the branch's diff passed the local review (`scripts/review.sh check`, [pre-push-review.md](pre-push-review.md)) in a repo that
  has `.sandcastle/lib/review-ledger.mts`; `GATE_SKIP_REVIEW=1` skips that for gate runs you start yourself (the hook drops the variable, so a push never
  skips it; it drops `GATE_SKIP_SELF_TEST` too, the switch of a gate that tests itself, and `GATE_PART`, which makes a gate run one part of itself), and CI doesn't repeat it, since CI has no ledger.
  Then it runs `bash scripts/sync-copilot-review.sh --check` when the repo has the script: the Copilot review instructions must match the
  code-review skill, and a repo without the skill passes ([copilot-review-sync.md](copilot-review-sync.md)).
  Between the lints and the build it runs the Seed `.github/ci/gate-checks.sh <merge-base>` when the repo has one:
  checks only that repo needs, such as Blazor-Server's Sandcastle TypeScript check. The merge
  base is empty without an `origin/main`.

## Tests

`.github/hooks/tests/pre-push.test.sh` runs the hook in a throwaway repo with stub tools that log each call, so no real
build runs. It needs cases for:

- each protected-branch rule and deletion
- the tag and mixed pushes
- a push that isn't the checked-out commit
- an untracked or unstaged file
- `.github/ci/gate-checks.sh` running with the merge base, and failing the push before the build
- a `sandcastle.gatedHead` marker for `HEAD` not skipping the gate (pushed and by hand)
- a branch behind `origin/main` refused before the gate (pushed and by hand) with the `git merge origin/main` fix,
  the same branch running the gate once it has merged main, no `origin/main` skipping the check, and a stale local
  `origin/main` refreshed by the hook's fetch
- **each accepted branch prefix** (including slugs with digits), and each retired or malformed name (`squad/`,
  `sprint/`, a prefix missing its issue number, a `chore/` slug starting with a digit, and an uppercase letter in an
  otherwise valid name such as `chore/tidy-Up`). Each bad name must break exactly one rule, or it can't catch a
  regression in that rule.

`pre-commit.test.sh` runs its lint cases in a linked worktree. Its worktree cases cover a commit in the primary
checkout and from a subfolder of it (refused, before the lint), the opt-out, a linked worktree, a repo's first commit,
and `git commit` end to end, which runs the hook with `GIT_DIR` set.

`scripts/tests/check-branch-name.test.sh` covers the script's push form and its `--pr` form, which the CI job uses: the Actions
branches (`docs/release-notes`, `docs/backfill-blog-posts`, refused for a push), any `dependabot/*` allowed with `--pr --author 'dependabot[bot]'` and only a
shell-safe one for any other author, none or an empty one (and `--author` before `--pr` read as the branch), a missing `--author` value or branch refused with the usual message,
a refused name's control characters printed escaped on one line,
only a shell-safe `dependabot/*` allowed for a push (a backtick, quote, `|` or `&` refused),
non-ASCII names (`fix/1-café`, `dependabot/aé`) refused under `LC_ALL=C`, under `C.UTF-8` and `en_US.UTF-8` when installed, and under every
other UTF-8 locale of the machine where `[a-z]` matches an accented letter (a skip line says when there is none), with `fix/1-cafe` still allowed,
and a check that the script exports `LC_ALL=C` before its first regex match, which holds on any machine (so the hook, CI, `cleanup.sh` and
`work-issue.sh` agree on a name whatever the caller's locale),
a `claude/...` branch refused, and other `docs/` names refused.

CI's hook-tests job runs this suite, `pre-commit.test.sh`, `check-branch-name.test.sh`, `check-pr-title.test.sh`, `sync-copilot-review.test.sh`, `watch-pr.test.sh`,
`cleanup.test.sh`, `lint-edited-file.test.sh` and `work-issue.test.sh`.

## Session-start check

`scripts/check-git-hooks.sh` (with `scripts/tests/check-git-hooks.test.sh`) warns when a clone's `core.hooksPath` isn't
`.github/hooks`, because git then never runs `pre-push` and a push skips the gate without a word. It prints nothing when
the hooks are on, and stays silent in Claude Code cloud sessions, in GitHub Actions and where git can't write its config
(Sandcastle's sandbox). It always exits 0. Taken from auth0-management-app.

The Template doesn't carry `.claude/settings.json`, so each repo that wants the warning adds the hook by hand:

```json
{
  "hooks": {
    "SessionStart": [
      { "hooks": [{ "type": "command", "command": "\"$CLAUDE_PROJECT_DIR\"/scripts/check-git-hooks.sh" }] }
    ]
  }
}
```

## Edit-time lint

`scripts/lint-edited-file.sh` (with `scripts/tests/lint-edited-file.test.sh`) is a Claude Code `PostToolUse` hook. After each Edit, Write or MultiEdit
it lints the file the tool wrote, so an agent fixes a violation in the same turn and the pre-commit hook, the gate, CI and a reviewer never see it
([the Factory's ADR 0011](../../../../docs/adr/0011-markdown-and-yaml-are-linted-as-they-are-written.md)). A Markdown file goes through `markdownlint-cli2` with the repo's
`.markdownlint-cli2.jsonc`. A YAML file goes through `yamllint` with `.yamllint.yml`; a workflow also goes through `actionlint` and `zizmor --offline`, and `.github/dependabot.yml` through `zizmor`,
at the repo root or under any folder (the Factory edits the Template's originals under `.claude/skills/`).
These are the checks the CI lint workflows run; a gate runs `actionlint` and `zizmor` only where it lints workflows (an Application's gate does when a workflow changed; the Factory's does not).
A shell file goes through `shellcheck` and `shfmt -d`, the gate's Shell lint and Shell format steps, on the files the gate takes for shell files: a `*.sh`, an extensionless file in the
root's `scripts/`, or a git hook (`pre-*`, `post-*`, `commit-msg`, `prepare-commit-msg`, matched in the C locale) in the root's `.github/hooks`, or in a `.github/hooks` under a folder
whose `.editorconfig` has a `[.github/hooks/*]` section (the Template's copy in the Factory, whose gate takes only its `pre-commit`, `pre-push` and `post-checkout`; an
Application's gate takes the root's hooks alone, and the hook's note says so). shfmt gets no style flags, so it reads `.editorconfig`; it skips the extensionless `scripts/`
files, which no section styles, and runs only when it is v3.14 or a later v3 release, as the gate needs (another, or a `--version` that fails or prints nothing, is named in
the note, and shellcheck still runs). When shfmt exits 1 (a diff, or a file it can't parse) the report adds the `shfmt -w` command that writes the format, with the path quoted
for a shell, and says to fix the syntax if the output is a syntax error; a shfmt that crashes gets no such hint. Neither tool runs code from the repository, and shfmt reads
only `.editorconfig`.

A Python file (a `*.py`, case and all, as the gate matches it: not a `.PY`, `.pyi` or `.pyc`) goes through ruff with the gate's Python lint flags, `check --isolated --no-cache
--select F,B --target-version py312 --`: `--isolated` reads no `pyproject.toml`, `ruff.toml` or `.ruff.toml`, so a config an agent writes can't turn a rule off, and
`--no-cache` writes no `.ruff_cache`. ruff runs no code from the repository. The hook clears every `RUFF_` variable first (`RUFF_OUTPUT_FILE` would send the report to a
file, and the agent would see a clean edit). It runs only a 0.17 release, the line the gate accepts; another, or a `--version` that fails or prints nothing, is named in
the note (its first 40 bytes, each other than a letter, digit, space or `_ . + -` as `?`).

shellcheck reads its `.shellcheckrc`, and `external-sources` there lets it open the files a `source` line names; `--check-sourced` would then quote their lines in the report the
agent reads. So the hook clears `SHELLCHECK_OPTS`, and shellcheck reads no config at all: it runs with `--norc`, and the hook turns the `.shellcheckrc` or `shellcheckrc`
nearest the file (its folder and each one above it, up to the repository root, in the order shellcheck looks) into the flag each of its keys has: `disable` becomes `-e`,
`enable` `-o`, `shell` `-s`, `source-path` `-P` and `extended-analysis` `--extended-analysis`. Nothing is read after the check, so no file or pipe is left for another
process to change, and the hook needs a shellcheck with `--norc`, not `--rcfile`. A config above the repository root, or the person's own in their home, is not read at edit
time; the gate reads it. The config is read without a here-string, which bash may write to a temp file of its own. shellcheck is not run while a config on the path is a
symlink, not a regular file, unreadable, names `external-sources` or `check-sourced` in any case, holds a line other than a blank, a comment or one of those five keys (an
allow-list, so a key a later shellcheck adds can't slip past), gives one no value or a value of a form the hook doesn't pass on (`disable` and `enable` take codes or check
names with commas, starting with a letter or digit, `shell` one of `sh`, `bash`, `dash`, `ksh` and `busybox`, `extended-analysis` `true` or `false`), holds a NUL byte (which
bash would drop as it reads, so the length would come out short), has a line over 4,096
bytes or is over 65,536 in all (bounds chosen well inside what a command line carries, which no real config comes near; only 65,537 bytes of a config are read), or sets
`extended-analysis` for a shellcheck whose `--help` lists no `--extended-analysis` (before 0.9), which would stop on it with a usage error: the note names the config and
why (an unknown key by its first 40 bytes, each one other than a letter, digit, space or `_ . + -` shown as `?`, so no control byte an agent wrote reaches the person's
terminal; every note, of every kind, shows a control character other than a tab, a format character (a bidi override or isolate, a zero-width
one, a BOM), a line or paragraph separator, or a byte that isn't UTF-8, as `?`, whatever the locale, so a folder an agent named can't carry an
escape sequence, or a bidi or zero-width character that changes how it reads (a letter that looks like another
still shows as it is), and the one exit-1 message that names a path shows every byte of it but printable ASCII as `?`), and
the gate still checks the file. The help is judged by what it prints, not its exit status: a usage without the flag gets
that note, and a `--help` that prints no usage gets one of its own. Each value is one argument of its flag: `source-path`
may start with a dash, since shellcheck takes the argument after `-P` as its value, and `disable` and `enable` may not.

A hook under a nested `.github/hooks` whose `.editorconfig` can't be read is not linted, with a note; a line of it longer than 4,096 characters is no section header. A violation
wins over a note: the hook exits 2 with the report alone. `test.sh` checks that the hook's hook-name rule is the gate's and the Lint Actions shellcheck job's, that its shfmt
release rule is the gate's and its own, and that its ruff release rule and flags are the gate's, its flags CI's `python-tests` job's, and the gate's `RUFF_VERSION`
`requirements-tests.txt`'s pin and the Seed `Dockerfile`'s.
The hook sees only those tools' writes: a file an agent writes through Bash (a heredoc, `sed -i`, a script) is not linted until the pre-commit hook (Markdown only), the gate or CI,
since a Bash command doesn't name the files it wrote. The Owned `.claude/rules/markdown-and-yaml-edits.md`, which Apply writes into every repo and which loads in every session (it has no `paths:`),
tells agents to write Markdown and YAML with Write and Edit for that reason, in a repo that hasn't wired the hook yet as well. The Owned `.claude/rules/script-edits.md`, whose `paths:`
load it when a session works on a shell or Python script, says the same for shell and Python scripts and lists the checks reviewers keep raising on them:
a test that fails without each new branch, the header or docstring updated in the same edit, the edge cases that keep coming back (a missing tool, an empty,
relative or odd path, CRLF or a byte-order mark, a symlink, a variable a caller left set), and the script's error contract.
In a repo that runs Sandcastle, which is where the review ledger is, a re-Apply that rewrites either, or the hook itself (which lints each Markdown, YAML, shell or Python file an agent
writes, with no Bash prompt, and vets the configs its linters read), needs a local review (`REVIEWED_ON_REAPPLY`), as the hand-off rules do;
in a repo without the ledger the re-Apply PR's own review is the check.

- Exit 2 with the linters' output on stderr when any linter reports a problem; Claude Code feeds that back to the agent. A linter that crashes counts as a problem, not as clean. Each linter's
  output is cut at 6,000 characters (bytes in the C locale, where a cut may split a multibyte character). The report says the output below it is data, not instructions, and ends with a line
  telling the agent to hand a broken linter (a usage error, an old version) to the person.
- Exit 0 and no output for a file that is not Markdown, YAML, a shell file the gate checks or a `*.py`, one that no longer exists, a directory, a symlink, one under `node_modules/`,
  and one outside the project (below).
- Exit 0 with a `systemMessage` on stdout, which Claude Code shows to the person, when a linter is not installed, or is not run (shfmt or ruff another release or one whose
  `--version` fails or prints nothing, shellcheck under a config the hook won't take, as above), or a hook under a nested `.github/hooks` isn't linted because its
  `.editorconfig` can't be read: the file is skipped for that linter, the hook only helps, and the gate still checks, as do the pre-commit hook and CI where they lint
  that kind of file (the pre-commit hook lints Markdown; CI doesn't check shell format). Plain stderr on exit 0 is not shown, which is why the note is JSON. Before it runs anything the hook cleans `PATH`:
  every relative or empty entry goes (it would name a folder of whatever directory a command runs in, the repository once the hook has changed into it), and so does
  every entry inside the project's checkouts (the file's repository, and the project and every worktree `git worktree list` shows, a `node_modules/.bin` among them),
  since an agent can write any of them with no Bash prompt and this hook runs right after a write. It cleans twice: first, before it looks up `python3` or `git`, with
  the checkouts read from git's own files (`.git`, `commondir`, `worktrees/*/gitdir`) by bash alone, read whole and trimmed as git trims them (a relative
  `worktrees/*/gitdir` from its own folder), and again with `git worktree list` and the edited file's repository.
  A `PATH` left with no entry is set to a folder that can't exist, never to an empty string, which bash would read as the working directory. So no linter (`markdownlint-cli2`, `yamllint`, `actionlint`,
  `zizmor`, `shellcheck`, `shfmt`, `ruff`) and no helper a linter looks up on `PATH` (`node`, `python3`, `bash`, `shellcheck`) comes from there. A linter whose real file is a symlink into a checkout
  is passed over for the next one on `PATH`; one found only in such places is named apart in the note ("found only inside the project or on a relative PATH entry"),
  and an exported function of that name counts as not installed. Each linter runs by the path `PATH` found it under, so a shim that picks its program by name (mise)
  still works. Install them outside the project, on an absolute `PATH` entry: `pnpm add -g markdownlint-cli2`, `uv tool install yamllint`, `uv tool install zizmor`,
  `uv tool install ruff@0.17.6`, and actionlint, shellcheck and shfmt from their release binaries or a package manager. The pre-commit hook and the gate still use
  the repo's own `markdownlint-cli2` (only it has one),
  because a person runs those.
- Exit 1 with a message on stderr when `CLAUDE_PROJECT_DIR` is unset or one of those git files is longer than any that names a folder (`GIT_FILE_MAX`), for any edit,
  since both are checked before the input is read; when its input is not JSON, `python3` is missing (or found only where the hook runs nothing from,
  which the message says), or, for a Markdown, YAML or shell file, `git` is missing (the same) or older than 2.36
  (`git worktree list -z`), so a broken hook is visible and is not read as a clean file.
- Only the project's own checkout and the linked worktrees it lists are linted: the file's repository root must be one of the paths `git worktree list` prints for `CLAUDE_PROJECT_DIR`,
  which Claude Code
  sets and the hook requires (unset or empty is exit 1). A linter loads the repository's own config, and `markdownlint-cli2` runs JavaScript from its
  config, so a file in another clone, a nested repository, or a directory whose `.git` file or `commondir` merely names the project's git directory (git reports the project's common directory for it)
  is left alone. A project that is not a git repository lints nothing. A worktree entry git still lists after its directory was removed is trusted until `git worktree prune` removes it.
  `-z` keeps a worktree path with a newline from forging a second entry,
  and the file's name, folder, repository root and each listed worktree's resolved path keep a trailing newline (`$(...)` would drop it and lint the sibling folder).
  `GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_PREFIX`, `GIT_COMMON_DIR` and `CDPATH`,
  inherited from a git hook, are cleared first.
- A Markdown file is linted only through markdownlint configs the hook can vouch for: each one on the file's path (its folder and each folder above it, up to the
  repository root, where `markdownlint-cli2` stops looking) must be a regular file whose bytes, as they are or with each CRLF made LF (a checkout
  that converts line endings), are the blob `HEAD` holds at its path
  (`git rev-parse HEAD:<path>` against `git hash-object --no-filters`, which read neither the index nor the working tree's status, so no
  `core.fsmonitor` command or clean filter from the repository's config runs; never in a repository with no commit), and unable to
  load JavaScript: no `.cjs` or `.mjs` config, and no `customRules`, `markdownItPlugins`, `outputFormatters`, `modulePaths` or `extends` in it, read as text, byte for byte.
  A config read as text must hold no backslash either, since a JSON or YAML escape (`\u0075`, `\x52`, a folded line) could spell one of those keys where the text doesn't show
  it. A file of any of those names that is a symlink, `package.json` included, is refused whatever it holds. A `package.json` counts only when it has a `markdownlint-cli2`
  key, and then only that key is read, decoded (all of it, as text, when it doesn't parse). Otherwise an agent could write a config, or the code a
  committed one names, and the hook would run it with no Bash prompt. The file is then left unlinted: exit 0, with a `systemMessage` naming the config and why; the
  pre-commit hook, the gate and CI still lint it. The real-linter test block commits its config for this reason.
- The path reaches the linter whole: from the repo root of the file's own repository, as `./<path>`, and for Markdown with `--no-globs`, so a name with `[`, `*` or a leading `-` is not read as a glob
  or an option (checked against the real tool: `a[1].md` and `x*y.md` are linted as those files). The config's `ignores` still apply.
- A `file_path` holding a lone surrogate (valid JSON, but no UTF-8) is still read, not an exit 1: `\udc80` to `\udcff` stand for the raw bytes `0x80` to `0xff`
  (Python's `surrogateescape`) and any other lone surrogate for its three-byte form (`surrogatepass`). A file with that name is linted; a path that names none exits 0, as for any missing file.
- The tests use stub linters on `PATH` that fail on a marker word and log their arguments and working directory, and check each linter's exact arguments, which stream carries the report, and the
  repository boundary. A last block runs the real `markdownlint-cli2` when it is installed; an Application's hook-tests job installs none, so there it is skipped.

The Template doesn't carry `.claude/settings.json`, so each repo adds the hook by hand (the script's header shows the same entry):

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit",
        "hooks": [{ "type": "command", "command": "\"$CLAUDE_PROJECT_DIR\"/scripts/lint-edited-file.sh" }]
      }
    ]
  }
}
```

## Adapting

- A repo's own gate steps go in the Seed `.github/ci/gate-checks.sh`, each guarded by the paths it covers. A step
  added to `gate.sh` itself is drift, reverted by the next Apply.
- Update every doc that lists branch names (`CLAUDE.md`, `CONTRIBUTING.md`, the README).
- The Standardize branch is pushed through the *new* hook, because `core.hooksPath` is relative to the worktree. Its
  name, `chore/standardize-baseline`, passes both the old rules and the new one.
- **Move existing worktrees** into `../<Repo>-worktrees/` with `git worktree move`, and update any doc that names
  another folder (IssueManager used `../IssueManager.worktrees/`). Once the Apply is merged, anything still
  uncommitted in the primary checkout moves to a worktree through a stash, as the hook's message shows.
- **Run one pre-push gate at a time.** A gate that runs Aspire or Testcontainers tests starts fixed-name containers, so
  two pushes gated in parallel (one per worktree) collide and fail each other.

## Verify live

Push a branch named under a retired prefix (`git push --dry-run` still runs the hook): it's refused with the standard's
message. Push a `fix/{issue}-{slug}` branch: the gate runs. Open a PR from a branch outside the standard (pushed with
`--no-verify`): the **Branch name** check fails. Rename the branch, close and reopen the PR: it passes. Stage a change in the primary checkout and commit: it's
refused with the `git worktree add` command.
