#!/usr/bin/env bash
# Tests for .github/hooks/pre-commit.
# Each lint case stages Markdown in a linked worktree of a throwaway repo and
# runs the hook; the worktree cases also run it in the primary checkout. A stub
# `markdownlint-cli2` logs the files it is given and applies two rules from the
# .markdownlint-cli2.jsonc it finds in its working directory: "forbid" names a
# word that fails a file, and "ignores" lists "prefix/**" paths it skips. A
# nested config's "forbid" replaces the root's for the files under it. So a
# case can tell whether the hook linted the staged content or the working
# copy, and under which config, without needing the real linter. The stub
# takes only literal paths (a leading ':'), as the real linter needs them:
# without one it reads a name as a glob, an option or a negation. Run with no
# arguments it prints a banner, as the real one does, naming STUB_LINT_VERSION
# (0.24.0 by default), or STUB_LINT_BANNER as it is, unless the folder it runs
# in has a config with "noBanner" (as the real one would read it there); with
# STUB_LINT_NOISE set it prints a warning on stderr and a line on stdout before
# the banner, and with STUB_LINT_BROKEN set it prints only an error on stderr
# and exits 127, as a linter whose node is missing would. It logs its arguments, NUL-separated, to $ARGS too, so a case can
# match one exactly. A `git`
# wrapper fails the git subcommand FAIL_GIT names (only its FAIL_GIT_CALL-th
# call, when that is set), so a case can tell that the hook refuses a commit it
# couldn't lint. A `mkdir` wrapper fails for a folder whose name ends in the
# FAIL_MKDIR it is given.
# Usage: .github/hooks/tests/pre-commit.test.sh
set -uo pipefail

HOOK="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/pre-commit"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

PRIMARY="$WORK/repo"
# The lint cases run in a linked worktree, where commits are allowed.
REPO="$WORK/repo-worktrees/fix-1-lint"
STUBS="$WORK/bin"
LOG="$WORK/lint.log"
ARGS="$WORK/lint.args"

mkdir -p "$STUBS"
cat > "$STUBS/markdownlint-cli2" << EOF
#!/usr/bin/env bash
if [[ \$# -eq 0 ]]; then
  if [[ -n "\${STUB_LINT_BROKEN:-}" ]]; then
    echo "env: 'node': No such file or directory (stub)" >&2
    exit 127
  fi
  if [[ -n "\${STUB_LINT_NOISE:-}" ]]; then
    echo "(node:1) Warning: something (stub)" >&2
    echo "Some notice before the banner (stub)"
  fi
  if grep -qs noBanner .markdownlint-cli2.jsonc; then
    :
  elif [[ -n "\${STUB_LINT_BANNER+set}" ]]; then
    echo "\$STUB_LINT_BANNER"
  else
    echo "markdownlint-cli2 v\${STUB_LINT_VERSION:-0.24.0} (markdownlint v0.38.0)"
  fi
  exit 2
fi
echo "markdownlint-cli2 \$*" >> "$LOG"
printf '%s\0' "\$@" >> "$ARGS"
[[ -f .markdownlint-cli2.jsonc ]] || { echo "no config in \$PWD"; exit 2; }
word=\$(sed -n 's/.*"forbid": *"\\([^"]*\\)".*/\\1/p' .markdownlint-cli2.jsonc)
ignored=\$(sed -n 's/.*"ignores": *\\[ *"\\([^"]*\\)\\*\\*".*/\\1/p' .markdownlint-cli2.jsonc)
status=0
for file in "\$@"; do
  [[ "\$file" == :* ]] || { echo "\$file: not a literal path (no leading :)"; exit 3; }
  file="\${file#:}"
  if [[ -n "\$ignored" && "\$file" == "\$ignored"* ]]; then
    continue
  fi
  # A nested config's "forbid" replaces the root's for the files under it.
  file_word="\$word" dir=\$(dirname -- "\$file")
  while [[ "\$dir" != "." ]]; do
    if [[ -f "\$dir/.markdownlint-cli2.jsonc" ]]; then
      file_word=\$(sed -n 's/.*"forbid": *"\\([^"]*\\)".*/\\1/p' "\$dir/.markdownlint-cli2.jsonc")
      break
    fi
    dir=\$(dirname -- "\$dir")
  done
  if grep -q -e "\$file_word" -- "\$file"; then
    echo "\$file: \$file_word found"
    status=1
  fi
done
exit \$status
EOF
chmod +x "$STUBS/markdownlint-cli2"

GIT_STUBS="$WORK/git-bin"
mkdir -p "$GIT_STUBS"
cat > "$GIT_STUBS/git" << EOF
#!/usr/bin/env bash
for arg in "\$@"; do
  [[ -n "\${FAIL_GIT:-}" && "\$arg" == "\$FAIL_GIT" ]] || continue
  echo x >> "$WORK/git-calls"
  if [[ -z "\${FAIL_GIT_CALL:-}" || "\$(wc -l < "$WORK/git-calls")" -eq "\$FAIL_GIT_CALL" ]]; then
    echo "git \$FAIL_GIT: failed (stub)" >&2
    exit 128
  fi
done
exec "$(command -v git)" "\$@"
EOF
chmod +x "$GIT_STUBS/git"

MKDIR_STUBS="$WORK/mkdir-bin"
mkdir -p "$MKDIR_STUBS"
cat > "$MKDIR_STUBS/mkdir" << EOF
#!/usr/bin/env bash
if [[ -n "\${FAIL_MKDIR:-}" && "\${*: -1}" == *"\$FAIL_MKDIR" ]]; then
  echo "mkdir: cannot create directory (stub)" >&2
  exit 1
fi
exec "$(command -v mkdir)" "\$@"
EOF
chmod +x "$MKDIR_STUBS/mkdir"

unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX
# No user or system config, so a setting there (diff.renames, a hooksPath)
# can't change what git reports here.
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1
export GIT_AUTHOR_NAME=test GIT_AUTHOR_EMAIL=test@example.com
export GIT_COMMITTER_NAME=test GIT_COMMITTER_EMAIL=test@example.com

git init -q -b main "$PRIMARY"
echo '{ "forbid": "BAD", "ignores": ["docs/blogs/**"] }' > "$PRIMARY/.markdownlint-cli2.jsonc"
echo '# Readme' > "$PRIMARY/README.md"
git -C "$PRIMARY" add .
# No hooks here: this is setup, and a global hooksPath would otherwise run.
git -C "$PRIMARY" -c core.hooksPath=/dev/null commit -q -m init
git -C "$PRIMARY" worktree add -q -b fix/1-lint "$REPO"

PASSED=0
FAILED=0
OUTPUT=""
STATUS=0

# run_hook [dir]: run the hook in dir, the lint worktree by default.
run_hook() {
  : > "$LOG"
  : > "$ARGS"
  OUTPUT="$(cd "${1:-$REPO}" && PATH="$STUBS:$PATH" bash "$HOOK" 2>&1)"
  STATUS=$?
}

# run_hook_failing_git <subcommand> [<call>]: run the hook in the lint worktree
# with that git subcommand failing (only its <call>-th call, when given).
run_hook_failing_git() {
  : > "$LOG"
  : > "$ARGS"
  : > "$WORK/git-calls"
  OUTPUT="$(cd "$REPO" && PATH="$GIT_STUBS:$STUBS:$PATH" FAIL_GIT="$1" FAIL_GIT_CALL="${2:-}" bash "$HOOK" 2>&1)"
  STATUS=$?
}

pass() {
  PASSED=$((PASSED + 1))
  echo "ok   - $1"
}

fail() {
  FAILED=$((FAILED + 1))
  echo "FAIL - $1: $2"
  while IFS= read -r line; do echo "       | $line"; done <<< "$OUTPUT"
}

# expect <name> <allowed|refused> <linted|not-linted>
expect() {
  local name="$1" outcome="$2" lint="$3"
  if [[ "$outcome" == allowed && $STATUS -ne 0 ]]; then
    fail "$name" "expected the commit to be allowed, exit $STATUS"
  elif [[ "$outcome" == refused && $STATUS -eq 0 ]]; then
    fail "$name" "expected the commit to be refused"
  elif [[ "$lint" == linted && ! -s "$LOG" ]]; then
    fail "$name" "expected markdownlint-cli2 to run"
  elif [[ "$lint" == not-linted && -s "$LOG" ]]; then
    fail "$name" "expected markdownlint-cli2 not to run"
  else
    pass "$name"
  fi
}

# got_arg <arg>: whether the linter was given exactly <arg>.
got_arg() {
  local arg
  while IFS= read -r -d '' arg; do
    [[ "$arg" == "$1" ]] && return 0
  done < "$ARGS"
  return 1
}

# reset_repo: drop staged and unstaged changes so each case starts clean.
reset_repo() {
  git -C "$REPO" reset -q --hard
  git -C "$REPO" clean -qfd
}

run_hook
expect "with no staged Markdown the hook skips the lint" allowed not-linted

echo 'Some text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
run_hook
expect "clean staged Markdown passes" allowed linted
reset_repo

echo 'BAD text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
run_hook
expect "a violation in staged Markdown refuses the commit" refused linted
reset_repo

echo 'BAD text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
git -C "$REPO" show HEAD:README.md > "$REPO/README.md"
run_hook
expect "a violation that is staged but fixed only in the working copy refuses the commit" refused linted
reset_repo

echo 'Some text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
echo 'BAD text.' >> "$REPO/README.md"
run_hook
expect "a violation only in the unstaged working copy doesn't refuse the commit" allowed linted
reset_repo

mkdir -p "$REPO/docs/sub dir"
echo 'BAD text.' > "$REPO/docs/sub dir/my notes.md"
git -C "$REPO" add "docs/sub dir/my notes.md"
run_hook
expect "a staged file whose path has spaces is linted" refused linted
reset_repo

git -C "$REPO" rm -q README.md
run_hook
expect "deleting a Markdown file skips the lint" allowed not-linted
reset_repo

# Enough unchanged lines that git reports a rename (R), not an add.
printf '# Notes\n\n%s\n' line1 line2 line3 line4 line5 line6 line7 line8 > "$REPO/NOTES.md"
git -C "$REPO" add NOTES.md
git -C "$REPO" -c core.hooksPath=/dev/null commit -q -m notes
git -C "$REPO" mv NOTES.md GUIDE.md
echo 'BAD text.' >> "$REPO/GUIDE.md"
git -C "$REPO" add GUIDE.md
# The case only proves something if git reports a rename; fail it otherwise.
OUTPUT="$(git -C "$REPO" diff --cached --name-status)"
if [[ "$OUTPUT" == R* ]]; then
  run_hook
  expect "a violation in a renamed Markdown file refuses the commit" refused linted
else
  fail "a violation in a renamed Markdown file refuses the commit" "setup: git didn't report a staged rename"
fi
git -C "$REPO" reset -q --hard HEAD~1

# The stub linter fails without a config, so an unstaged config shows up here.
git -C "$REPO" rm -q --cached .markdownlint-cli2.jsonc
echo 'Some text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
run_hook
expect "a config that isn't staged isn't used" refused linted
git -C "$REPO" add .markdownlint-cli2.jsonc
reset_repo

# The staged config forbids STAGED; the working copy's forbids WORKING instead.
echo '{ "forbid": "STAGED", "ignores": ["docs/blogs/**"] }' > "$REPO/.markdownlint-cli2.jsonc"
git -C "$REPO" add .markdownlint-cli2.jsonc
echo '{ "forbid": "WORKING", "ignores": ["docs/blogs/**"] }' > "$REPO/.markdownlint-cli2.jsonc"
echo 'STAGED text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
run_hook
expect "the staged config's rules apply" refused linted
reset_repo

echo '{ "forbid": "STAGED", "ignores": ["docs/blogs/**"] }' > "$REPO/.markdownlint-cli2.jsonc"
git -C "$REPO" add .markdownlint-cli2.jsonc
echo '{ "forbid": "WORKING", "ignores": ["docs/blogs/**"] }' > "$REPO/.markdownlint-cli2.jsonc"
echo 'WORKING text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
run_hook
expect "the working copy's config rules don't apply" allowed linted
reset_repo

mkdir -p "$REPO/docs/blogs"
echo 'BAD text.' > "$REPO/docs/blogs/post.md"
git -C "$REPO" add docs/blogs/post.md
run_hook
expect "a staged file under an ignored path passes" allowed linted
reset_repo

# A nested config (here allowing BAD under vendor/) applies to its directory,
# as it does in CI, but only once it's staged.
mkdir -p "$REPO/vendor"
echo '{ "forbid": "NESTED" }' > "$REPO/vendor/.markdownlint-cli2.jsonc"
echo 'BAD text.' > "$REPO/vendor/notes.md"
git -C "$REPO" add vendor
run_hook
expect "a staged nested config's rules apply under its directory" allowed linted
reset_repo

mkdir -p "$REPO/vendor"
echo '{ "forbid": "NESTED" }' > "$REPO/vendor/.markdownlint-cli2.jsonc"
echo 'BAD text.' > "$REPO/vendor/notes.md"
git -C "$REPO" add vendor/notes.md
run_hook
expect "a nested config that isn't staged isn't used" refused linted
reset_repo

# markdownlint-cli2 reads a bare argument as a glob, an option or a negation.
# a1.md is a decoy a[1].md or a*.md read as a glob would match. The names are
# shown with printf %q, so a newline or a byte that isn't UTF-8 shows as such.
# Only the name that isn't UTF-8 may be skipped (macOS's filesystem refuses
# it), and only when a plain name can still be written; any other name that
# can't be written, or a git add that fails, fails the case.
for name in 'a[1].md' 'a*.md' '-dash.md' '!bang.md' ':colon.md' 'a b.md' $'new\nline.md' $'\xff.md'; do
  shown="$(printf '%q' "$name")"
  if ! written="$({ echo 'BAD text.' > "$REPO/$name"; } 2>&1)"; then
    if [[ "$name" == $'\xff.md' ]] && { echo 'Some text.' > "$REPO/plain.md"; } 2> /dev/null; then
      rm -f "$REPO/plain.md"
      echo "skip - this filesystem refuses the name $shown: $written"
    else
      fail "a staged $shown is linted as that file" "setup: couldn't write it: $written"
      reset_repo
    fi
    continue
  fi
  echo 'Some text.' > "$REPO/a1.md"
  # --literal-pathspecs, or git reads :colon.md's ':' as pathspec magic.
  if ! git -C "$REPO" --literal-pathspecs add -- "$name" a1.md; then
    fail "a staged $shown is linted as that file" "setup: git add failed"
    reset_repo
    continue
  fi
  run_hook
  expect "a staged $shown is linted as that file" refused linted
  if got_arg ":$name"; then
    pass "the linter gets $shown as a literal path, one ':' before the name"
  else
    fail "the linter gets $shown as a literal path, one ':' before the name" "its arguments: $(cat "$LOG")"
  fi
  reset_repo
done

# A hard name renamed is linted under its new name, and a hard name partly
# staged is linted as staged.
printf '# Notes\n\n%s\n' line1 line2 line3 line4 line5 line6 line7 line8 > "$REPO/a[1] notes.md"
git -C "$REPO" add -- 'a[1] notes.md'
git -C "$REPO" -c core.hooksPath=/dev/null commit -q -m notes
git -C "$REPO" mv -- 'a[1] notes.md' '-b[2] guide.md'
echo 'BAD text.' >> "$REPO/-b[2] guide.md"
git -C "$REPO" add -- '-b[2] guide.md'
# As above, the case only proves something if git reports a rename.
OUTPUT="$(git -C "$REPO" diff --cached --name-status)"
if [[ "$OUTPUT" == R* ]]; then
  run_hook
  expect "a violation in a renamed hard-named file refuses the commit" refused linted
  if got_arg ':-b[2] guide.md' && ! got_arg ':a[1] notes.md'; then
    pass "a renamed hard-named file is linted under its new name"
  else
    fail "a renamed hard-named file is linted under its new name" "its arguments: $(cat "$LOG")"
  fi
else
  fail "a violation in a renamed hard-named file refuses the commit" "setup: git didn't report a staged rename"
fi
git -C "$REPO" reset -q --hard HEAD~1
echo 'BAD text.' > "$REPO/!part [1].md"
git -C "$REPO" add -- '!part [1].md'
echo 'Some text.' > "$REPO/!part [1].md"
run_hook
expect "a hard-named file staged bad and fixed only in the working copy refuses the commit" refused linted
reset_repo

# A commit with no Markdown needs no temporary folder; one with Markdown is
# refused, saying why, when it can't have one.
echo 'text' > "$REPO/notes.txt"
git -C "$REPO" add notes.txt
TMPDIR="$WORK/no-such-dir" run_hook
expect "with no staged Markdown and no temporary folder, the hook lets the commit through" allowed not-linted
reset_repo
echo 'Some text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
TMPDIR="$WORK/no-such-dir" run_hook
expect "with staged Markdown and no temporary folder, the hook refuses the commit" refused not-linted
if [[ "$OUTPUT" == *"Couldn't make a temporary folder to lint the staged Markdown in: the commit is refused."* ]]; then
  pass "without a temporary folder the hook says why it refuses the commit"
else
  fail "without a temporary folder the hook says why it refuses the commit" "no reason in the output"
fi
# The two folders made inside it: the one the version is read in, and the
# snapshot. Either one failing refuses the commit, saying which.
for failing in "/probe:to read markdownlint-cli2's version in" "/snapshot:to lint the staged Markdown in"; do
  said="${failing#*:}" failing="${failing%%:*}"
  : > "$LOG"
  : > "$ARGS"
  OUTPUT="$(cd "$REPO" && PATH="$MKDIR_STUBS:$STUBS:$PATH" FAIL_MKDIR="$failing" bash "$HOOK" 2>&1)"
  STATUS=$?
  expect "a $failing folder that can't be made refuses the commit" refused not-linted
  if [[ "$OUTPUT" == *"Couldn't make a temporary folder $said: the commit is refused."* ]]; then
    pass "a $failing folder that can't be made says why the commit is refused"
  else
    fail "a $failing folder that can't be made says why the commit is refused" "expected 'Couldn't make a temporary folder $said'"
  fi
done
reset_repo

# A caller's GIT_LITERAL_PATHSPECS would make the hook's '*.md' match no file
# and let the commit through unlinted: the hook drops it.
echo 'BAD text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
GIT_LITERAL_PATHSPECS=1 run_hook
expect "with GIT_LITERAL_PATHSPECS set, staged Markdown is still linted" refused linted
reset_repo

# The ':' prefix came in markdownlint-cli2 0.4.0: an older one, or one whose
# version can't be read, refuses the commit before it lints.
echo 'Some text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
STUB_LINT_VERSION=0.3.2 run_hook
expect "a markdownlint-cli2 older than 0.4.0 refuses the commit" refused not-linted
if [[ "$OUTPUT" == *'markdownlint-cli2 v0.3.2 (markdownlint v0.38.0), not markdownlint-cli2 0.4.0 or later'* ]]; then
  pass "an old markdownlint-cli2's refusal names its version"
else
  fail "an old markdownlint-cli2's refusal names its version" "no version in the output"
fi
STUB_LINT_VERSION=0.4.0 run_hook
expect "markdownlint-cli2 0.4.0 lints" allowed linted
STUB_LINT_VERSION=0.10.0 run_hook
expect "markdownlint-cli2 0.10.0 lints (its version is read as a number)" allowed linted
STUB_LINT_BANNER='Usage: something else' run_hook
expect "a markdownlint-cli2 whose version can't be read refuses the commit" refused not-linted
STUB_LINT_BROKEN=1 run_hook
expect "a markdownlint-cli2 that can't start refuses the commit" refused not-linted
if [[ "$OUTPUT" == *"It said:"*"'node': No such file or directory (stub)"* ]]; then
  pass "a markdownlint-cli2 that can't start shows what it said"
else
  fail "a markdownlint-cli2 that can't start shows what it said" "its stderr isn't in the output"
fi
# Node's warnings on stderr, and a line on stdout before the banner, don't hide it.
STUB_LINT_NOISE=1 run_hook
expect "a warning before markdownlint-cli2's banner doesn't hide its version" allowed linted
# The version is read in an empty folder: a config in the working copy that
# isn't staged (here one that turns the banner off) isn't read, or run.
cp "$REPO/.markdownlint-cli2.jsonc" "$WORK/config.saved"
echo '{ "forbid": "BAD", "noBanner": true }' > "$REPO/.markdownlint-cli2.jsonc"
run_hook
expect "an unstaged config in the working copy doesn't change the version check" allowed linted
cp "$WORK/config.saved" "$REPO/.markdownlint-cli2.jsonc"
reset_repo

# A git that fails refuses the commit, rather than linting nothing or a tree
# missing the staged files.
echo 'Some text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
# diff runs twice: whether any Markdown is staged, then the list of it.
# checkout-index runs twice too: for the configs, then for the staged files.
# Each refusal names what failed, and says what to do.
for failing in "diff 1:Markdown can't be listed" "diff 2:Markdown can't be listed" "ls-files:markdownlint configs" \
  "checkout-index 1:markdownlint configs" "checkout-index 2:Markdown files"; do
  said="${failing#*:}" failing="${failing%%:*}"
  # shellcheck disable=SC2086 # the subcommand and its call number, as two words
  run_hook_failing_git $failing
  expect "a failing git $failing refuses the commit" refused not-linted
  if [[ "$OUTPUT" == *"$said"*'the commit is refused'* && "$OUTPUT" == *"See git's message above"* ]]; then
    pass "a failing git $failing says what failed, that the commit is refused, and what to do"
  else
    fail "a failing git $failing says what failed, that the commit is refused, and what to do" "expected '$said', the refusal and what to do"
  fi
done
run_hook_failing_git no-such-subcommand
expect "with every git command working, the hook lints as before" allowed linted
reset_repo

# ── Commits only in a linked worktree ──

echo 'Some text.' >> "$PRIMARY/README.md"
git -C "$PRIMARY" add README.md
run_hook "$PRIMARY"
expect "a commit in the primary checkout is refused before the lint" refused not-linted

# The message's paths are absolute, so they work from wherever git commit ran.
# pwd -P, as git resolves symlinks in the top level (macOS's /var).
WORKTREES_ABS="$(cd "$WORK" && pwd -P)/repo-worktrees/<folder>"
expect_folder() {
  if [[ "$OUTPUT" == *"git worktree add --no-track -b <branch> \"$WORKTREES_ABS\" origin/main"* ]]; then
    pass "$1"
  else
    fail "$1" "expected the absolute folder \"$WORKTREES_ABS\""
  fi
}
expect_folder "the refusal gives the absolute worktree folder"

# A subfolder of the primary checkout is still the primary checkout.
mkdir -p "$PRIMARY/docs"
run_hook "$PRIMARY/docs"
expect "a commit from a subfolder of the primary checkout is refused" refused not-linted
expect_folder "the refusal from a subfolder gives the same absolute worktree folder"

git -C "$PRIMARY" config baseline.allowPrimaryCommits true
run_hook "$PRIMARY"
expect "baseline.allowPrimaryCommits allows a commit in the primary checkout" allowed linted
git -C "$PRIMARY" config --unset baseline.allowPrimaryCommits
git -C "$PRIMARY" reset -q --hard

echo 'Some text.' >> "$REPO/README.md"
git -C "$REPO" add README.md
run_hook
expect "a commit in a linked worktree is allowed" allowed linted
reset_repo

FRESH="$WORK/fresh"
git init -q -b main "$FRESH"
echo '{ "forbid": "BAD" }' > "$FRESH/.markdownlint-cli2.jsonc"
echo '# Readme' > "$FRESH/README.md"
git -C "$FRESH" add .
run_hook "$FRESH"
expect "a repo's first commit is allowed in the primary checkout" allowed linted

# On a branch of its own in the primary checkout, the refusal's commands move
# that branch, with its staged and untracked changes, into a worktree, and
# what was staged is still staged there (README.md), the rest still not (new.md).
git -C "$PRIMARY" switch -q -c fix/2-moved
echo 'Staged.' >> "$PRIMARY/README.md"
git -C "$PRIMARY" add README.md
echo 'Untracked.' > "$PRIMARY/new.md"
run_hook "$PRIMARY"
expect "a commit on its own branch in the primary checkout is refused" refused not-linted
# The command lines, without their colour codes.
RECOVERY="$(sed -e 's/\x1b\[[0-9;]*m//g' -n -e 's/^      \(git .*\|cd .*\)$/\1/p' <<< "$OUTPUT")"
MOVED="$(cd "$WORK" && pwd -P)/repo-worktrees/fix-2-moved"
if [[ "$RECOVERY" != *"git switch main"* ]]; then
  fail "the refusal's commands move the branch into a worktree" "no git switch main in the message"
elif ! (cd "$PRIMARY" && set -e && eval "$RECOVERY") &> /dev/null; then
  fail "the refusal's commands move the branch into a worktree" "the commands failed"
elif [[ "$(git -C "$MOVED" branch --show-current)" != fix/2-moved ]] \
  || ! grep -q Staged "$MOVED/README.md" || [[ ! -f "$MOVED/new.md" ]] \
  || ! git -C "$MOVED" diff --cached --quiet --exit-code -- new.md \
  || git -C "$MOVED" diff --cached --quiet -- README.md \
  || ! git -C "$MOVED" diff --quiet -- README.md \
  || [[ "$(git -C "$PRIMARY" branch --show-current)" != main ]] \
  || [[ -n "$(git -C "$PRIMARY" status --porcelain)" ]]; then
  fail "the refusal's commands move the branch into a worktree" "the worktree or primary checkout isn't as expected"
else
  pass "the refusal's commands move the branch into a worktree"
fi

# An orphan branch has no HEAD commit, but the repo has history.
git -C "$PRIMARY" checkout -q --orphan fresh-start
run_hook "$PRIMARY"
expect "a commit on an orphan branch in the primary checkout is refused" refused not-linted
git -C "$PRIMARY" checkout -q -f main

# End to end through git commit, which runs the hook with GIT_DIR and
# GIT_INDEX_FILE set, and in a worktree points GIT_DIR at .git/worktrees/<name>.
HOOKS_DIR="$(dirname "$HOOK")"
echo 'Some text.' >> "$PRIMARY/README.md"
if PATH="$STUBS:$PATH" git -C "$PRIMARY" -c core.hooksPath="$HOOKS_DIR" commit -q -am primary &> /dev/null; then
  fail "git commit in the primary checkout is refused" "the commit was made"
  git -C "$PRIMARY" reset -q --hard HEAD~1
else
  pass "git commit in the primary checkout is refused"
fi
git -C "$PRIMARY" reset -q --hard
echo 'Some text.' >> "$REPO/README.md"
if OUTPUT="$(PATH="$STUBS:$PATH" git -C "$REPO" -c core.hooksPath="$HOOKS_DIR" commit -q -am worktree 2>&1)"; then
  pass "git commit in a linked worktree is allowed"
else
  fail "git commit in a linked worktree is allowed" "the commit was refused"
fi

# ── The real markdownlint-cli2, when one is installed ──
# The stub takes the ':' prefix on trust; the real linter must read ':!bang.md'
# as that file, not a negation that lints nothing. (A name like a[1].md is no
# case here: the hook lints only staged files, so a decoy it could match as a
# glob would be staged, and linted, either way. owned-gate-files.test.sh, in
# the Application Factory, covers it with a decoy beside the file.) The suite's
# own PATH never holds the stub; a markdownlint-cli2 found under $STUBS fails.
REAL_LINT="$(command -v markdownlint-cli2 || true)"
if [[ -z "$REAL_LINT" ]]; then
  echo "skip - no markdownlint-cli2 installed, so the real-linter cases don't run"
elif [[ "$REAL_LINT" == "$STUBS"/* ]]; then
  fail "the real-linter case runs the real markdownlint-cli2" "found the stub, $REAL_LINT"
else
  REAL="$WORK/real"
  git init -q -b main "$REAL"
  git -C "$REAL" config baseline.allowPrimaryCommits true
  echo '{ "config": { "default": true } }' > "$REAL/.markdownlint-cli2.jsonc"
  printf '# Readme\n' > "$REAL/README.md"
  git -C "$REAL" add .
  git -C "$REAL" -c core.hooksPath=/dev/null commit -q -m init
  # MD018: no space after the '#'.
  printf '#Bad heading\n' > "$REAL/!bang.md"
  git -C "$REAL" --literal-pathspecs add -- '!bang.md'
  OUTPUT="$(cd "$REAL" && bash "$HOOK" 2>&1)"
  STATUS=$?
  if [[ $STATUS -ne 0 && "$OUTPUT" == *'!bang.md'*MD018* ]]; then
    pass "the real linter lints a staged !bang.md as that file, not a negation"
  else
    fail "the real linter lints a staged !bang.md as that file, not a negation" "exit $STATUS, no MD018 for !bang.md"
  fi
fi

echo
echo "$PASSED passed, $FAILED failed"
[[ $FAILED -eq 0 ]]
