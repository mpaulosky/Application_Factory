#!/usr/bin/env bash
# Tests for tests/check-git-status.sh, on folders of fixtures.
# Usage: bash tests/check-git-status.test.sh
# shellcheck disable=SC2016,SC2034 # the fixtures are text that must not expand; $out and $st are read inside the checks' eval strings
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
checker="$here/check-git-status.sh"
work="$(mktemp -d)" || exit 1
[[ -n "$work" && -d "$work" ]] || exit 1
trap 'command rm -rf -- "$work"' EXIT
PASSED=0; FAILED=0
check() { if eval "$2"; then PASSED=$((PASSED + 1)); echo "ok   - $1"; else FAILED=$((FAILED + 1)); echo "FAIL - $1"; fi; }

# fixture <name> <relative file> <content>: a folder $work/<name> holding that one file
fixture() { mkdir -p "$work/$1/$(dirname "$2")"; printf '%s\n' "$3" > "$work/$1/$2"; }
# run_checker <name>: exit status in $st, output in $out
run_checker() { out="$(bash "$checker" "$work/$1" 2>&1)"; st=$?; }

fixture bad scripts/a.sh '[[ -z "$(git status --porcelain)" ]] || exit 1'
run_checker bad
check "a bare git status --porcelain in a script is found, with file and line" "[[ $st == 1 ]] && grep -q 'scripts/a.sh:1:' <<<\"\$out\" && grep -q 'untracked-files' <<<\"\$out\""
fixture bad-c scripts/a.sh 'git -C "$wt" status --porcelain 2>/dev/null'
run_checker bad-c
check "git -C <dir> status --porcelain is found too" "[[ $st == 1 ]]"
fixture bad-hook .github/hooks/pre-push 'if [[ -n "$(git status --porcelain)" ]]; then exit 1; fi'
run_checker bad-hook
check "a hook without the .sh suffix is scanned" "[[ $st == 1 ]]"
fixture bad-order scripts/a.sh 'git status --porcelain --branch'
run_checker bad-order
check "other options do not count as the setting" "[[ $st == 1 ]]"
fixture good scripts/a.sh '[[ -z "$(git status --porcelain --untracked-files=all)" ]] || exit 1'
run_checker good
check "--untracked-files=all passes" "[[ $st == 0 && -z \"\$out\" ]]"
fixture good-no scripts/a.sh 'git status --porcelain --untracked-files=no'
run_checker good-no
check "--untracked-files=no passes: it is chosen on purpose" "[[ $st == 0 ]]"
fixture good-sep scripts/a.sh 'git status --porcelain --untracked-files all'
run_checker good-sep
check "--untracked-files all (two words) passes" "[[ $st == 0 ]]"
fixture short scripts/a.sh 'git status --short | head -n 20'
run_checker short
check "git status --short, which a person reads, is not a guard" "[[ $st == 0 ]]"
fixture comment scripts/a.sh '# git status --porcelain would hide it'
run_checker comment
check "a comment is not scanned" "[[ $st == 0 ]]"
fixture test-file scripts/tests/a.test.sh 'git -C "$repo" status --porcelain'
run_checker test-file
check "a test is not scanned" "[[ $st == 0 ]]"
fixture md docs/a.md 'run git status --porcelain'
run_checker md
check "a Markdown file is not scanned" "[[ $st == 0 ]]"
fixture lookalike scripts/a.sh 'my-git status --porcelain'
run_checker lookalike
check "another command that ends in git is not git" "[[ $st == 0 ]]"
bash "$checker" "$work/missing" >/dev/null 2>&1; st=$?
check "a missing folder is exit 2, not a pass" "[[ $st == 2 ]]"
bash "$checker" >/dev/null 2>&1; st=$?
check "no argument is exit 2" "[[ $st == 2 ]]"
bash "$checker" "$here/../template/owned" >/dev/null 2>&1; st=$?
check "the Template's own Owned scripts pass" "[[ $st == 0 ]]"

# the checkout's own path may hold a folder named tests: only folders below the one given are skipped
fixture under/tests/owned scripts/a.sh 'git status --porcelain'
out="$(bash "$checker" "$work/under/tests/owned" 2>&1)"; st=$?
check "a checkout below a folder named tests is still scanned" "[[ $st == 1 ]]"
fixture skipped scripts/tests/a.sh 'git status --porcelain'
run_checker skipped
check "a tests folder inside the given folder is skipped" "[[ $st == 0 ]]"
fixture opt1 scripts/a.sh 'git --no-pager status --porcelain'
run_checker opt1
check "git --no-pager status --porcelain is found" "[[ $st == 1 ]]"
fixture opt2 scripts/a.sh 'git --no-optional-locks status --porcelain'
run_checker opt2
check "git --no-optional-locks status --porcelain is found" "[[ $st == 1 ]]"
fixture opt3 scripts/a.sh 'git --git-dir=x status --porcelain'
run_checker opt3
check "git --git-dir=x status --porcelain is found" "[[ $st == 1 ]]"
fixture opt4 scripts/a.sh 'git -C "$dir with space" status --porcelain'
run_checker opt4
check "git -C \"a dir with spaces\" status --porcelain is found" "[[ $st == 1 ]]"
fixture env scripts/a.sh 'GIT_X=1 git status --porcelain'
run_checker env
check "a git call after an environment assignment is found" "[[ $st == 1 ]]"
fixture short-u scripts/a.sh '[[ -z "$(git status --porcelain -uall)" ]]'
run_checker short-u
check "-uall names the setting" "[[ $st == 0 ]]"
fixture short-uno scripts/a.sh 'git status --porcelain -uno'
run_checker short-uno
check "-uno names the setting" "[[ $st == 0 ]]"
fixture cont-bad scripts/a.sh 'git status \
  --porcelain'
run_checker cont-bad
check "a call broken over lines is found, with the number of its first line" "[[ $st == 1 ]] && grep -q 'scripts/a.sh:1:' <<<\"\$out\""
fixture cont-ok scripts/a.sh 'git status --porcelain \
  --untracked-files=all'
run_checker cont-ok
check "a setting on the continuation line counts" "[[ $st == 0 ]]"
fixture unreadable scripts/a.sh 'git status --porcelain'
chmod 000 "$work/unreadable/scripts/a.sh"
if [[ "$(id -u)" != 0 ]]; then
  run_checker unreadable
  check "an unreadable file is exit 2 and named, not a pass" "[[ $st == 2 ]] && grep -q 'could not read' <<<\"\$out\""
fi
chmod 600 "$work/unreadable/scripts/a.sh"


echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
