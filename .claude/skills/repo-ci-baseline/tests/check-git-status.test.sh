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


# The setting counts only in the call's own words: not another command's -u, nor a comment's text
fixture u-sort scripts/a.sh '[[ -z "$(git status --porcelain | sort -u)" ]]'
run_checker u-sort
check "a sort -u after the call is not its setting" "[[ $st == 1 ]]"
fixture u-add scripts/a.sh 'git add -u && [[ -z "$(git status --porcelain)" ]]'
run_checker u-add
check "a git add -u before the call is not its setting" "[[ $st == 1 ]]"
fixture u-comment scripts/a.sh 'git status --porcelain # --untracked-files=all'
run_checker u-comment
check "a setting named in a trailing comment is not a setting" "[[ $st == 1 ]]"
fixture u-bare scripts/a.sh 'git status --porcelain -u'
run_checker u-bare
check "-u alone is a setting (it means all)" "[[ $st == 0 ]]"
fixture u-normal scripts/a.sh 'git status --porcelain -unormal'
run_checker u-normal
check "-unormal is a setting" "[[ $st == 0 ]]"
fixture u-long-x scripts/a.sh 'git status --porcelain --untracked-files-x'
run_checker u-long-x
check "--untracked-files-x is not git's option" "[[ $st == 1 ]]"
fixture u-space scripts/a.sh 'git status --porcelain --untracked-files all'
run_checker u-space
check "--untracked-files all (with a space) is a setting" "[[ $st == 0 ]]"
fixture porcelain-v2 scripts/a.sh 'git status --porcelain=v2'
run_checker porcelain-v2
check "--porcelain=v2 is found" "[[ $st == 1 ]]"
# global options in any form
fixture g1 scripts/a.sh 'git -C "$(dirname "$x")" status --porcelain'
run_checker g1
check "git -C with a nested quote and command substitution is found" "[[ $st == 1 ]]"
fixture g2 scripts/a.sh 'git --git-dir x --work-tree y status --porcelain'
run_checker g2
check "long global options with a space-separated value are found" "[[ $st == 1 ]]"
fixture g3 scripts/a.sh 'git -c a=b -p status --porcelain'
run_checker g3
check "git -c k=v and -p before status are found" "[[ $st == 1 ]]"
fixture g4 scripts/a.sh 'git -C"$d" status --porcelain'
run_checker g4
check "git -C\"\$d\" without a space is found" "[[ $st == 1 ]]"
fixture status-word scripts/a.sh 'git log --oneline | grep status --porcelain'
run_checker status-word
check "a status after a pipe is not git's" "[[ $st == 0 ]]"
# a comment line is not joined to the next line
fixture comment-cont scripts/a.sh '# a note \
git status --porcelain'
run_checker comment-cont
check "a comment ending in a backslash does not hide the next line" "[[ $st == 1 ]] && grep -q 'scripts/a.sh:2:' <<<\"\$out\""
# a folder that can't be listed
fixture nolist scripts/sub/a.sh 'echo hi'
chmod 000 "$work/nolist/scripts/sub"
if [[ "$(id -u)" != 0 ]]; then
  run_checker nolist
  check "a folder that can't be listed is exit 2, not a pass" "[[ $st == 2 ]] && grep -q 'could not list' <<<\"\$out\""
else
  echo "skip - the unreadable file and folder cases need a user other than root"
fi
chmod 755 "$work/nolist/scripts/sub"

# each terminator of the call is pinned by a -u after it that must not count
for form in 'git status --porcelain; sort -u' 'git status --porcelain && git add -u' 'x="$(git status --porcelain)"; foo -u' 'git status --porcelain 2>&1; foo -u' 'git status --porcelain & foo -u' 'if (git status --porcelain) && foo -u; then :; fi'; do
  fixture term scripts/a.sh "$form"
  run_checker term
  check "a -u after the call's end is not its setting: $form" "[[ $st == 1 ]]"
done
fixture sub1 scripts/a.sh 'git -C $(dirname "$d") status --porcelain'
run_checker sub1
check "an unquoted command substitution inside the call does not hide it" "[[ $st == 1 ]]"
fixture sub2 scripts/a.sh 'git status --porcelain $(sort -u f)'
run_checker sub2
check "a -u inside a command substitution is not the call's setting" "[[ $st == 1 ]]"
fixture sub3 scripts/a.sh 'git status --porcelain $(echo --untracked-files=all)'
run_checker sub3
check "and a setting that is only text inside one is not either" "[[ $st == 1 ]]"
fixture sub-and scripts/a.sh 'git -C $(cd x && pwd) status --porcelain'
run_checker sub-and
check "a && inside a substitution does not end the call" "[[ $st == 1 ]]"
fixture sub-pipe scripts/a.sh 'git -C $(dirname "$d" | head -1) status --porcelain'
run_checker sub-pipe
check "a | inside a substitution does not end the call" "[[ $st == 1 ]]"
fixture sub-pipe-ok scripts/a.sh 'git status --porcelain $(a | b) --untracked-files=all'
run_checker sub-pipe-ok
check "and the setting after a substitution with a | in it is still seen" "[[ $st == 0 ]]"
fixture sub-dq scripts/a.sh 'git status --porcelain "$(sort -u f)"'
run_checker sub-dq
check "a -u in a substitution inside double quotes is not the call's setting" "[[ $st == 1 ]]"
fixture sub-bt scripts/a.sh 'git status --porcelain `sort -u f`'
run_checker sub-bt
check "a -u in backticks is not the call's setting" "[[ $st == 1 ]]"
fixture sub-arith scripts/a.sh 'git status --porcelain $((1+2))'
run_checker sub-arith
check "arithmetic expansion after the call leaves it found" "[[ $st == 1 ]]"
fixture sub-nested scripts/a.sh 'git status --porcelain "$(echo "a)b" -u)"'
run_checker sub-nested
check "a ) inside quotes inside a substitution does not close it" "[[ $st == 1 ]]"
fixture subshell scripts/a.sh '(cd "$d" && git status --porcelain)'
run_checker subshell
check "a call inside a subshell is found" "[[ $st == 1 ]]"
fixture subshell-ok scripts/a.sh '(cd "$d" && git status --porcelain -uall)'
run_checker subshell-ok
check "and its setting is seen" "[[ $st == 0 ]]"
fixture quoted-term scripts/a.sh 'git -C "a;b" status --porcelain'
run_checker quoted-term
check "a ; inside quotes does not end the call early" "[[ $st == 1 ]]"
fixture quoted-term-ok scripts/a.sh 'git -C "a;b" status --porcelain --untracked-files=all'
run_checker quoted-term-ok
check "and the setting after it is still seen" "[[ $st == 0 ]]"
# a file that can't be read at all
fixture ro-dir scripts/a.sh 'git status --porcelain'
chmod 444 "$work/ro-dir/scripts"
if [[ "$(id -u)" != 0 ]]; then
  run_checker ro-dir
  check "a file in a folder that can't be searched is exit 2, not skipped" "[[ $st == 2 ]]"
fi
chmod 755 "$work/ro-dir/scripts"
mkdir -p "$work/dangling/scripts"; ln -s "$work/dangling/nowhere" "$work/dangling/scripts/a.sh"
run_checker dangling
check "a dangling symlink named *.sh is exit 2, not skipped" "[[ $st == 2 ]]"

echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
