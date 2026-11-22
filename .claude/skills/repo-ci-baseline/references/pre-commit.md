# Staged-content pre-commit

Template files (Owned): `.github/hooks/pre-commit`, `.github/hooks/tests/pre-commit.test.sh`, the hook-tests job in
`ci.yml`, and the lint config `.markdownlint-cli2.jsonc`. History: IssueTracker #170.

## Design

Before the lint, the hook refuses a commit made in the primary checkout; that rule is in
[git-hooks.md](git-hooks.md#worktree-standard). Everything below is the lint.

The hook lints what the commit will contain. It writes each staged Markdown file's **index** content, plus every
**staged** `.markdownlint-cli2.jsonc` (the root one and any nested one), into a temporary tree with the same layout (`git checkout-index
--prefix=<tmp>/`), then runs `markdownlint-cli2` from inside that tree. The config's `ignores` still apply there because
the paths are unchanged.

- Staged paths come from `git diff --cached --name-only -z --diff-filter=ACMR`. `-z` keeps paths with spaces intact. `R`
  is there because a rename is listed under its new path, so dropping it lets a renamed file skip the lint.
- Only a config that is in the index is used. A config that exists only in the working copy won't be in the commit, so
  it must not decide the result.
- Nested configs count, because markdownlint-cli2 applies a directory's config to the files under it, in CI and the gate
  alike. Snapshotting only the root config made vendored skills and Blazor-Server's `.sandcastle` prompts fail at commit
  time while passing in CI.
- Each name goes to markdownlint-cli2 as `:<name>`, its literal-path form, as `scripts/gate.sh` passes them: a bare
  argument is read as a glob (`a[1].md` would match `a1.md`), an option (`-dash.md`) or a negation (`!bang.md`).
- The `:` form came in markdownlint-cli2 0.4.0, so the hook needs 0.4.0 or later. It reads the version from the banner the linter
  prints when run with no arguments, and refuses the commit, naming what it found, when that is an older release or a banner it
  can't read: an older linter would read `:a.md` as a glob that matches nothing, and pass every commit.
  It runs the linter for the banner in an empty temporary folder, so a config in the working copy that isn't staged is neither
  read nor run, and reads only the `markdownlint-cli2 v…` line of its stdout, so a warning Node prints first doesn't hide it.
  When it reads no version, the refusal shows the first lines the linter printed on stderr (a missing node, say).
- It fails closed. The staged list is written to a file and a `git diff --cached` that fails refuses the commit, and so does
  a failed snapshot (`git ls-files` or either `git checkout-index`): an empty list or a tree missing a staged file would
  otherwise let the commit through unlinted. A temporary folder it can't make (the work folder, the one the version is read
  in, or the snapshot) refuses the commit too, saying which.

## Tests

`pre-commit.test.sh` runs the hook in a throwaway repo with a stub `markdownlint-cli2` on `PATH`. The stub must apply
rules read from the config it finds (a forbidden word, an ignored path prefix). A stub that only checks a config exists
can't tell which config the hook used. Cases that must exist:

- a violation staged but fixed only in the working copy → refused
- a violation only in the unstaged working copy → allowed
- a real staged rename carrying a violation → refused. Give the file enough unchanged lines that `git diff --cached
  --name-status` reports `R` (a one-line file shows up as an add), and make the case **fail** when it doesn't, rather
  than print a warning and go on to test the add path.
- a staged config that differs from the working copy's → the staged rules apply
- a config that isn't staged → not used
- a staged file under an ignored path → allowed
- a staged nested config → its rules apply under its directory; a nested config that isn't staged → not used
- a path with spaces, a deletion (skips the lint), and no staged Markdown (skips the lint)
- the hard names (`a[1].md`, `a*.md`, `-dash.md`, `!bang.md`, `:colon.md`, a space, a newline, a byte that isn't UTF-8) → each
  linted as that file and passed with its `:`; the stub refuses an argument without one, as the real linter would misread it.
  Only the name that isn't UTF-8 may be skipped (macOS refuses it), saying so, and only when a plain name can still be
  written; another name that can't be written, or a `git add` that fails, fails the case.
- a hard name renamed (guarded on `R`, as above) → linted under its new name; a hard name staged bad and fixed in the working
  copy → refused
- a markdownlint-cli2 before 0.4.0, or a banner without a version → refused; 0.4.0 and 0.10.0 → linted; a warning on stderr and
  a line on stdout before the banner → linted; an unstaged working-copy config that turns the banner off → linted; a linter
  that prints only an error on stderr and exits 127 → refused, showing that error
- no temporary folder → allowed with no staged Markdown, refused (saying why) with some; the folder the version is read in, or
  the snapshot folder, that can't be made (a `mkdir` wrapper on `PATH` fails the one `FAIL_MKDIR` names) → refused, saying
  which; `GIT_LITERAL_PATHSPECS` set → still linted
- with a real markdownlint-cli2 installed (skipped otherwise; one found under the stub folder fails the case), a staged
  `!bang.md` → linted as that file, not a negation. No glob-named case runs with the real linter here: the hook lints only
  staged files, so a decoy a glob could match would be staged, and linted, either way. The Factory's
  `owned-gate-files.test.sh` covers `a[1].md` and `a*.md` with a decoy beside each.
- a failing `git diff`, `git ls-files`, and each of the two `git checkout-index` calls on its own → refused, saying so (a `git`
  wrapper on `PATH` fails the subcommand, or the nth call of it, that `FAIL_GIT` and `FAIL_GIT_CALL` name)

Commit the test repo's setup with `git -c core.hooksPath=/dev/null`, so a global hooks path on the developer's machine
doesn't run during setup, and run every case with `GIT_CONFIG_GLOBAL=/dev/null` and `GIT_CONFIG_NOSYSTEM=1`, so a setting
there (`diff.renames`, say) can't change what git reports. Mutation-check the suite: swap the hook to copy the working-copy config and confirm the config
cases fail, and overwrite the renamed file so git sees an add and confirm the rename case fails.

## Adapting

**Only `.markdownlint-cli2.jsonc` files may configure the rules.** markdownlint-cli2 also reads `.markdownlint.json`, and the
hook snapshots only the jsonc file. IssueManager's `.markdownlint.json` was `{"default": false}`, which turned every
rule off in CI, the gate and the hook alike, so Markdown lint passed while checking nothing. The Template carries no
`.markdownlint.json`, and `apply.sh` reports one as a conflict: delete it, then `markdownlint-cli2 --fix '**/*.md'`.
To check what the configs enforce, lint a file with obvious violations (`#Bad`, `* bullet`, trailing spaces): it must
fail.

## Verify live

Commit a partly staged Markdown file whose staged version breaks a rule: the commit must be refused even though the
working copy is clean.
