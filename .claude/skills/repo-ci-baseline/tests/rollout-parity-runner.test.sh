#!/usr/bin/env bash
# Tests for the rollout parity suite's runner (tests/rollout-parity.test.sh) and its fake tool (tests/rollout-parity/fake-tool.sh).
# The suite itself only shows the runner passes against the implementation it was recorded from; this shows it fails, and
# how, against one that differs (its stdout, exit code, stderr, its calls and their order, what it leaves in HOME), that the
# run gets the copy of the skill folder with its stubs and fixed repos.txt and a fresh HOME, that a command naming the skill's
# file runs the copy's, that record mode writes what it should, and that the fake tool answers what its header says.
#
#   tests/rollout-parity-runner.test.sh
#
# Copies the runner, the fake gh and tool and the stderr match into a throwaway skill folder whose rollout.sh is a stub, with
# cases of its own, so no real case is run or written. Needs what the runner needs (bash 4.4, coreutils, findutils, diff,
# grep and sed).
set -euo pipefail

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
work="$(CDPATH='' cd -- "$work" && pwd -P)"

failures=0
fail() {
  echo "FAIL: $*" >&2
  failures=$((failures + 1))
}

skill="$work/skill"
mkdir -p "$skill/tests/parity" "$skill/tests/rollout-parity/cases" "$skill/github/tests"
cp "$here/rollout-parity.test.sh" "$skill/tests/"
cp "$here/parity/fake-gh.sh" "$here/parity/stderr.sh" "$skill/tests/parity/"
cp "$here/rollout-parity/fake-tool.sh" "$skill/tests/rollout-parity/"
printf '%s\n' 'owner/suite-list' > "$skill/tests/rollout-parity/repos.txt"
printf '%s\n' 'owner/real-list' > "$skill/github/repos.txt"
: > "$skill/github/tests/test_x.py"
: > "$skill/not-copied.sh"
runner="$skill/tests/rollout-parity.test.sh"
tool="$skill/tests/rollout-parity/fake-tool.sh"
cases="$skill/tests/rollout-parity/cases"

# The stub rollout: what it sees of its folder and HOME, then a call of each kind, then a line on stdout and one on stderr.
# STUB_MODE makes it differ from what its case expects in one way at a time.
cat > "$skill/rollout.sh" << 'EOF'
#!/usr/bin/env bash
set -u
mode="${STUB_MODE:-ok}"
dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
echo "from $dir, in $(pwd -P), home $HOME"
echo "list $(cat "$dir/github/repos.txt"), tests $([[ -e "$dir/github/tests" ]] && echo kept || echo dropped)"
echo "token ${GH_TOKEN-unset} ${PYTHONPATH-unset} ${XDG_CONFIG_HOME-unset} $(ls -A "$GH_CONFIG_DIR" | wc -l)"
[[ "$mode" != order ]] || git rev-parse HEAD
"$dir/reapply.sh" --brings "$1" "$HOME/github/App"
echo "reapply exited $?"
gh pr list -R owner/app --head b --json number --jq '.[0]'
[[ "$mode" == order ]] || git rev-parse HEAD
[[ "$mode" != extra ]] || git status
[[ "$mode" != unmatched ]] || claude -p hello
[[ "$mode" != home ]] || echo left > "$HOME/left.txt"
[[ "$mode" != stdout ]] || echo "another line"
[[ "$mode" != stderr ]] || echo "warning: other" >&2
[[ "$mode" != hang ]] || sleep 30
echo "error: done" >&2
[[ "$mode" != code ]] || exit 3
exit 0
EOF
chmod +x "$skill/rollout.sh"
# Another implementation in github/: it runs the rollout.sh beside its own folder, the copy's when it is the copy's.
# shellcheck disable=SC2016 # its own $0 and $@, expanded when it runs
printf '#!/usr/bin/env bash\nexec "$(dirname -- "$0")/../rollout.sh" "$@"\n' > "$skill/github/alt.sh"

# make_case <name>: a case the stub, as it is, passes.
make_case() {
  local dir="$cases/$1"
  mkdir -p "$dir/gh" "$dir/fakes" "$dir/home/github/App"
  : > "$dir/home/github/App/.gitkeep"
  printf '%s\n' '#80' > "$dir/args"
  echo '7' > "$dir/gh/pr.list.owner.app.json"
  # shellcheck disable=SC2016 # the fixture's own $3, expanded when it runs
  printf '#!/usr/bin/env bash\necho "reapplying ${3##*/}"\nexit 4\n' > "$dir/fakes/reapply.sh"
  printf '#!/usr/bin/env bash\necho abc\n' > "$dir/fakes/git.sh"
  printf '%s\n' 'from <skill>, in ~, home ~' 'list owner/suite-list, tests dropped' 'token unset unset unset 0' 'reapplying App' \
    'reapply exited 4' 7 abc > "$dir/expected.stdout"
  echo 0 > "$dir/expected.code"
  printf '%s\n' '^error: done$' > "$dir/expected.stderr"
  printf '%s\n' 'reapply.sh --brings #80 ~/github/App' 'pr.list.owner.app gh pr list -R owner/app --head b --json number --jq ".[0]"' \
    'git rev-parse HEAD' > "$dir/expected.calls"
}
make_case one

# run_runner <want exit> <what> [VAR=value]... -- [command...]: runs the runner with the stub's environment and checks its exit.
run_runner() {
  local want="$1" what="$2" code=0
  shift 2
  local vars=()
  while [[ $# -gt 0 && "$1" != -- ]]; do
    vars+=("$1")
    shift
  done
  shift
  (cd "$skill" && env ${vars[@]+"${vars[@]}"} bash "$runner" "$@") > "$work/out" 2>&1 || code=$?
  [[ $code -eq $want ]] || fail "$what: exit $code, expected $want: $(tail -5 "$work/out")"
}
said() {
  grep -qF -- "$2" "$work/out" || fail "$1: the runner didn't say '$2': $(tail -5 "$work/out")"
}

run_runner 0 "a matching implementation" -- && said "a matching implementation" "ok   one"
said "the summary names the copy <skill>" "1 case(s) pass against: <skill>/rollout.sh"
# The caller's token, PYTHONPATH and XDG folder don't reach the run.
run_runner 0 "a caller's GH_TOKEN, PYTHONPATH and XDG_CONFIG_HOME" GH_TOKEN=secret PYTHONPATH=/x XDG_CONFIG_HOME=/x --
# A command that names the skill's own file runs the copy's, which finds the stubs and the suite's repos.txt.
run_runner 0 "a command naming the skill's file" -- bash github/alt.sh
said "a command naming the skill's file" "against: bash <skill>/github/alt.sh"
run_runner 0 "the same command by its absolute path" -- bash "$skill/github/alt.sh"

for mode in stdout code stderr extra order home; do
  run_runner 1 "STUB_MODE=$mode" STUB_MODE="$mode" -- && said "STUB_MODE=$mode" "FAIL one"
done
run_runner 1 "a changed stdout" STUB_MODE=stdout -- && said "a changed stdout" "+another line"
run_runner 1 "a changed exit" STUB_MODE=code -- && said "a changed exit" "exit 3, expected 0"
run_runner 1 "a changed stderr" STUB_MODE=stderr -- && said "a changed stderr" "2 stderr line(s), expected 1"
run_runner 1 "an extra call" STUB_MODE=extra -- && said "an extra call" "+git status"
run_runner 1 "calls in another order" STUB_MODE=order -- && said "calls in another order" "calls differ"
run_runner 1 "a file left in HOME" STUB_MODE=home -- && said "a file left in HOME" "HOME differs"
run_runner 1 "a call of a tool with no fixture" STUB_MODE=unmatched -- && said "a call of a tool with no fixture" "UNMATCHED claude -p hello"
run_runner 1 "a hung run" STUB_MODE=hang PARITY_TIMEOUT=2 -- && said "a hung run" "timed out after 2 second(s)"

# A file the run leaves in HOME passes when expected.home/ holds it, and the next case's HOME starts from its own home/ again.
make_case two
cp -R "$cases/one/home" "$cases/one/expected.home"
echo left > "$cases/one/expected.home/left.txt"
run_runner 1 "a case's HOME left as it was, with expected.home/ holding more" -- && said "expected.home/" "FAIL one"
# Case one's env makes it leave the file; case two, run next, must not find it.
echo STUB_MODE=home > "$cases/one/env"
run_runner 0 "HOME per case, and a case's env" --
rm -rf "$cases/one/expected.home" "$cases/one/env" "$cases/two"

# Setup errors: exit 2, and nothing is run.
run_runner 2 "a command naming a skill file the copy lacks" -- bash not-copied.sh
said "a command naming a skill file the copy lacks" "is in the skill folder but not in its copy"
run_runner 2 "a command that can't be run" -- ./no-such-command
run_runner 2 "a bad PARITY_TIMEOUT" PARITY_TIMEOUT=0 --
run_runner 2 "record mode with a command" PARITY_RECORD=1 -- bash github/alt.sh
for file in gh fakes expected.calls; do
  mv "$cases/one/$file" "$work/moved"
  run_runner 2 "a case with no $file" --
  mv "$work/moved" "$cases/one/$file"
done
printf '0\r\n' > "$cases/one/expected.code"
run_runner 2 "a carriage return in expected.code" -- && said "a carriage return in expected.code" "expected.code holds a carriage return"
echo 0 > "$cases/one/expected.code"
printf '%s\n' '^error: done$' '' > "$cases/one/expected.stderr"
run_runner 2 "a blank expected.stderr line" -- && said "a blank expected.stderr line" "line 2 is blank"
printf '%s\n' '^error: done$' > "$cases/one/expected.stderr"
run_runner 0 "the case restored" --

# Record mode writes the expected files from the reference, and records no run with a call no fixture answers. Case one is
# moved aside, so only the fresh case is recorded, and its files are what the fresh case's must come out as.
make_case fresh
rm "$cases/fresh"/expected.*
mv "$cases/one" "$work/one"
run_runner 0 "record mode" PARITY_RECORD=1 -- && said "record mode" "recorded fresh (exit 0)"
for file in stdout calls stderr code; do
  cmp -s "$work/one/expected.$file" "$cases/fresh/expected.$file" || fail "record mode wrote expected.$file unlike the hand-made case's"
done
[[ ! -e "$cases/fresh/expected.home" ]] || fail "record mode wrote expected.home/ for a run that left HOME as it was"
run_runner 0 "record mode, a run that writes in HOME" PARITY_RECORD=1 STUB_MODE=home --
[[ "$(cat "$cases/fresh/expected.home/left.txt" 2> /dev/null)" == left ]] || fail "record mode didn't write expected.home/ with the file the run left"
run_runner 0 "a recorded expected.home/" STUB_MODE=home -- && said "a recorded expected.home/" "ok   fresh"
# Recorded again from a run that leaves HOME as it was, the old expected.home/ goes.
run_runner 0 "record mode, HOME left as it was again" PARITY_RECORD=1 --
[[ ! -e "$cases/fresh/expected.home" ]] || fail "record mode kept an expected.home/ the run no longer leaves"
cp "$cases/fresh/expected.stdout" "$work/stdout"
run_runner 1 "record mode, a call no fixture answers" PARITY_RECORD=1 STUB_MODE=unmatched -- && said "record mode, unmatched" "FAIL fresh: not recorded"
cmp -s "$work/stdout" "$cases/fresh/expected.stdout" || fail "record mode wrote expected.stdout for a run with an unmatched call"
rm -rf "$cases/fresh"
mv "$work/one" "$cases/one"

# The fake tool, called by its link's name: numbered fixtures, ~ for FAKE_HOME, quoting, and exit 97 for what it can't answer.
fdir="$work/fakes" flog="$work/flog" fhome="$work/fhome"
mkdir -p "$fdir" "$fhome" "$work/links"
ln -s "$tool" "$work/links/reapply.sh"
ln -s "$tool" "$work/links/git"
printf '#!/usr/bin/env bash\necho "first $#"\nexit 5\n' > "$fdir/git.1.sh"
# shellcheck disable=SC2016 # the fixture's own $1, expanded when it runs
printf '#!/usr/bin/env bash\necho "rest $1"\n' > "$fdir/git.sh"
printf '#!/usr/bin/env bash\necho "reapply $*"\n' > "$fdir/reapply.sh"
mkdir "$fdir/claude.sh"
ln -s "$tool" "$work/links/claude"
call() {
  local want="$1" stdout="$2" what="$3" code=0
  shift 3
  FAKE_TOOL_DIR="$fdir" FAKE_GH_LOG="$flog" FAKE_HOME="$fhome" "$@" > "$work/fout" 2> "$work/ferr" || code=$?
  [[ $code -eq $want ]] || fail "fake tool, $what: exit $code, expected $want ($(cat "$work/ferr"))"
  [[ "$stdout" == - || "$(cat "$work/fout")" == "$stdout" ]] || fail "fake tool, $what: stdout '$(cat "$work/fout")', expected '$stdout'"
}
: > "$flog"
call 5 "first 2" "the first call's fixture" "$work/links/git" -C "$fhome/github/App"
call 0 "rest x" "a later call's fixture" "$work/links/git" x
call 0 "rest y" "a later call's fixture again" "$work/links/git" y
call 0 "reapply --brings #80 $fhome/a b" "a .sh tool's fixture is named without its .sh" "$work/links/reapply.sh" --brings '#80' "$fhome/a b"
call 97 '' "a fixture that isn't a file" "$work/links/claude" -p hi
want_log=$'git -C ~/github/App\ngit x\ngit y\nreapply.sh --brings #80 "~/a b"\nUNMATCHED claude -p hi (its fixture claude.sh is not a readable file)'
want_log+=$'\n'
[[ "$(head -5 "$flog")"$'\n' == "$want_log" ]] || fail "fake tool's log: got"$'\n'"$(cat "$flog")"
rm "$fdir/git.sh"
: > "$flog"
call 5 - "numbering after a reset log" "$work/links/git"
call 97 '' "a later call with no unnumbered fixture" "$work/links/git" $'a\tb\nc "d" \\e'
[[ "$(tail -1 "$flog")" == 'UNMATCHED git "a\tb\nc \"d\" \\e"' ]] || fail "fake tool: an argument with specials logged as $(tail -1 "$flog")"
code=0
env -u FAKE_HOME FAKE_TOOL_DIR="$fdir" FAKE_GH_LOG="$flog" "$work/links/git" > /dev/null 2>&1 || code=$?
[[ $code -eq 97 ]] || fail "fake tool with FAKE_HOME unset: exit $code, expected 97"

if [[ $failures -gt 0 ]]; then
  echo "rollout-parity-runner: $failures check(s) failed." >&2
  exit 1
fi
echo "rollout-parity-runner: every check passed."
