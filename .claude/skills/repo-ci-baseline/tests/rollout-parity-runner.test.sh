#!/usr/bin/env bash
# Tests for the rollout parity suite's runner (tests/rollout-parity.test.sh) and its fake tool (tests/rollout-parity/fake-tool.sh).
# The suite itself only shows the runner passes against the implementation it was recorded from; this shows it fails, and
# how, against one that differs (its stdout, exit code, stderr, its calls and their order, what it leaves in HOME), that the
# run gets the copy of the skill folder with its stubs, fixed repos.txt and stand-in Template and a fresh HOME, a PATH of the
# fakes and the host's listed commands only (a case's missing taken off it), the defaults under a case's fixtures, its
# signals reset but for its ignore, the wrapper's PID, and setup.sh and after.sh, that a command naming the skill's file runs
# the copy's, that random names are written XXXXXXXX and a .git in HOME left out, that record mode writes what it should,
# and that the fake tool answers what its header says.
#
#   tests/rollout-parity-runner.test.sh
#
# Copies the runner, the fake gh and tool and the stderr match into a throwaway skill folder whose rollout.sh is a stub, with
# cases of its own, so no real case is run or written. Needs what the runner needs (bash 4.4, coreutils, findutils, diff,
# grep, sed and tac).
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
mkdir -p "$skill/tests/parity" "$skill/tests/rollout-parity/cases" "$skill/github/tests" "$skill/tests/rollout-parity/defaults/fakes" \
  "$skill/tests/rollout-parity/defaults/gh" "$skill/tests/rollout-parity/template" "$work/hostbin"
cp "$here/rollout-parity.test.sh" "$skill/tests/"
cp "$here/parity/fake-gh.sh" "$here/parity/stderr.sh" "$skill/tests/parity/"
cp "$here/rollout-parity/fake-tool.sh" "$skill/tests/rollout-parity/"
cp "$here/rollout-parity/template/"* "$skill/tests/rollout-parity/template/"
# The suite's defaults: a shfmt, which the stub runs with PATH alone in its environment, and gh auth status.
printf '#!/usr/bin/env bash\necho "default shfmt"\n' > "$skill/tests/rollout-parity/defaults/fakes/shfmt.sh"
echo 'default auth' > "$skill/tests/rollout-parity/defaults/gh/auth.status.json"
# The caller's own commands: one the run's PATH must not hold, one it holds a fake of, and a launcher for a command.
printf '#!/usr/bin/env bash\necho "host tool"\n' > "$work/hostbin/hosttool"
printf '#!/usr/bin/env bash\necho "host pnpm"\n' > "$work/hostbin/pnpm"
# shellcheck disable=SC2016 # its own $@, expanded when it runs
printf '#!/usr/bin/env bash\nexec "$@"\n' > "$work/hostbin/myrun"
chmod +x "$work/hostbin/"*
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
echo "token ${GH_TOKEN-unset} ${PYTHONPATH-unset} ${XDG_CONFIG_HOME-unset} $(ls -A "$GH_CONFIG_DIR" | wc -l) ${CLAUDE_CODE_OAUTH_TOKEN-unset} ${HTTPS_PROXY-unset} ${ANTHROPIC_API_KEY-unset} ${http_proxy-unset}${MY_PROXYISH+ $MY_PROXYISH}${CLAUDEX+ $CLAUDEX}"
mask="$(sed -n 's/^SigIgn:[[:space:]]*//p' "/proc/$$/status")"
echo "pid $([[ "$(cat "$FAKE_PID_FILE")" == "$$" ]] && echo ok || echo other), HUP $(((0x$mask >> 0) & 1)), INT $(((0x$mask >> 1) & 1))"
echo "hosttool $(command -v hosttool > /dev/null && echo found || echo hidden), skill $(ls "$dir/template/owned/.claude/skills/pre-push-review")"
echo "setup $(cat "$HOME/setup.txt" 2> /dev/null || echo none)"
if command -v pnpm > /dev/null; then pnpm --version; else echo "no pnpm"; fi
env -i PATH="$PATH" shfmt --version
gh auth status
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
if [[ "$mode" == names ]]; then
  echo "made rollout-session.ab12_x9Z, rollout-sdk.q1_Z9aaa and rollout-other.abc"
  mkdir -p "$HOME/logs/rollout-record.zz12" "$HOME/repo/.git"
  echo first > "$HOME/logs/review-App.k3_x9Zq1.log"
  sleep 0.05
  echo "second, in $TMPDIR" > "$HOME/logs/review-App.Pq7aaaaa.log"
  sleep 0.05
  echo third > "$HOME/logs/review-App.Aa0bbbbb.log"
  echo settings > "$HOME/logs/rollout-record.zz12/review-App.c4dd.settings.json"
  echo plain > "$HOME/logs/review-App.log"
  echo kept > "$HOME/repo/file"
  echo "[core]" > "$HOME/repo/.git/config"
fi
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
  printf '#!/usr/bin/env bash\necho "fake pnpm"\n' > "$dir/fakes/pnpm.sh"
  printf '%s\n' 'from <skill>, in ~, home ~' 'list owner/suite-list, tests dropped' 'token unset unset unset 0 unset unset unset unset' \
    'pid ok, HUP 0, INT 0' 'hosttool hidden, skill SKILL.md' 'setup none' 'fake pnpm' 'default shfmt' 'default auth' 'reapplying App' \
    'reapply exited 4' 7 abc > "$dir/expected.stdout"
  echo 0 > "$dir/expected.code"
  printf '%s\n' '^error: done$' > "$dir/expected.stderr"
  printf '%s\n' 'pnpm --version' 'shfmt --version' 'auth.status' 'reapply.sh --brings #80 ~/github/App' \
    'pr.list.owner.app gh pr list -R owner/app --head b --json number --jq ".[0]"' 'git rev-parse HEAD' > "$dir/expected.calls"
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
  (cd "$skill" && env PATH="$work/hostbin:$PATH" ${vars[@]+"${vars[@]}"} bash "$runner" "$@") > "$work/out" 2>&1 || code=$?
  [[ $code -eq $want ]] || fail "$what: exit $code, expected $want: $(tail -5 "$work/out")"
}
said() {
  grep -qF -- "$2" "$work/out" || fail "$1: the runner didn't say '$2': $(tail -5 "$work/out")"
}

run_runner 0 "a matching implementation" -- && said "a matching implementation" "ok   one"
said "the summary names the copy <skill>" "1 case(s) pass against: <skill>/rollout.sh"
# The caller's token, PYTHONPATH, XDG folder, Claude Code sign-in and proxy don't reach the run.
run_runner 0 "a caller's GH_TOKEN, PYTHONPATH, XDG_CONFIG_HOME, CLAUDE_CODE_OAUTH_TOKEN, HTTPS_PROXY, ANTHROPIC_API_KEY and http_proxy" \
  GH_TOKEN=secret PYTHONPATH=/x XDG_CONFIG_HOME=/x CLAUDE_CODE_OAUTH_TOKEN=secret HTTPS_PROXY=http://u:p@proxy ANTHROPIC_API_KEY=secret \
  http_proxy=http://u:p@proxy --
# A name that only looks like one of them does reach it.
run_runner 1 "a caller's MY_PROXYISH and CLAUDEX" MY_PROXYISH=kept CLAUDEX=kept -- \
  && said "a caller's MY_PROXYISH and CLAUDEX" "+token unset unset unset 0 unset unset unset unset kept kept"
# A signal the caller ignores is reset to its default for the run: the case's ignore is what sets one.
code=0
(cd "$skill" && env PATH="$work/hostbin:$PATH" python3 -c 'import os, signal, sys; signal.signal(signal.SIGINT, signal.SIG_IGN); os.execvp("bash", sys.argv[1:])' \
  bash "$runner") > "$work/out" 2>&1 || code=$?
[[ $code -eq 0 ]] || fail "a caller that ignores SIGINT: exit $code, expected 0 (the run must start with SIGINT at its default): $(tail -5 "$work/out")"
# The first word is looked up on the caller's PATH, which the run's PATH doesn't hold.
run_runner 0 "a command found on the caller's PATH" -- myrun rollout.sh
said "a command found on the caller's PATH" "against: myrun <skill>/rollout.sh"
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

# A case's missing takes a fake tool off PATH, and its ignore starts the run with the signal ignored.
echo pnpm > "$cases/one/missing"
run_runner 1 "a case's missing" -- && said "a case's missing" "+no pnpm"
rm "$cases/one/missing"
echo HUP > "$cases/one/ignore"
run_runner 1 "a case's ignore" -- && said "a case's ignore" "+pid ok, HUP 1, INT 0"
printf '%s\n' HUP INT > "$cases/one/ignore"
run_runner 1 "a case's ignore of two signals" -- && said "a case's ignore of two signals" "+pid ok, HUP 1, INT 1"
rm "$cases/one/ignore"
# The case's fixtures win over the defaults: its shfmt.sh over the default's, its auth.status.fail over the default .json.
printf '#!/usr/bin/env bash\necho "case shfmt"\n' > "$cases/one/fakes/shfmt.sh"
run_runner 1 "a case's fixture over a default" -- && said "a case's fixture over a default" "+case shfmt"
rm "$cases/one/fakes/shfmt.sh"
echo 'signed out' > "$cases/one/gh/auth.status.fail"
run_runner 1 "a case's .fail over a default .json" -- && said "a case's .fail over a default .json" "-default auth"
rm "$cases/one/gh/auth.status.fail"
# A case's numbered fixture hides the default too: with only a second call's, the first call has no answer, not the default's.
for fixture in auth.status.2.fail auth.status.2.json; do
  echo 'numbered auth' > "$cases/one/gh/$fixture"
  run_runner 1 "a case's $fixture over a default .json" -- && said "a case's $fixture over a default .json" "UNMATCHED"
  rm "$cases/one/gh/$fixture"
done
# A fixture of another key that starts the same way doesn't hide it.
for fixture in auth.status.x1.json auth.status.2x.fail; do
  echo 'other key' > "$cases/one/gh/$fixture"
  run_runner 0 "a case's $fixture beside a default .json" --
  rm "$cases/one/gh/$fixture"
done
# setup.sh runs in HOME before the run, and after.sh after it; one that fails is a setup error, the other a failed case.
printf '#!/usr/bin/env bash\necho done > setup.txt\n' > "$cases/one/setup.sh"
printf '#!/usr/bin/env bash\nrm setup.txt\n' > "$cases/one/after.sh"
sed -i 's/^setup none$/setup done/' "$cases/one/expected.stdout"
run_runner 0 "setup.sh and after.sh" --
printf '#!/usr/bin/env bash\necho "still running"\nexit 3\n' > "$cases/one/after.sh"
run_runner 1 "an after.sh that fails" -- && said "an after.sh that fails" "after.sh exited 3: still running"
printf '#!/usr/bin/env bash\necho "no lock"\nexit 1\n' > "$cases/one/setup.sh"
run_runner 2 "a setup.sh that fails" -- && said "a setup.sh that fails" "one/setup.sh failed: no lock"
rm "$cases/one/setup.sh" "$cases/one/after.sh"
sed -i 's/^setup done$/setup none/' "$cases/one/expected.stdout"

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
run_runner 2 "a command that is no file on PATH" -- shopt && said "a command that is no file on PATH" "the command 'shopt' is not a file on PATH"
# A PATH of the caller's commands but python3.
mkdir -p "$work/nopython"
IFS=: read -ra dirs <<< "$PATH"
for dir in "$work/hostbin" "${dirs[@]}"; do
  for command in "$dir"/*; do
    name="${command##*/}"
    [[ -x "$command" && ! -e "$work/nopython/$name" && "$name" != python3* ]] || continue
    ln -s -- "$command" "$work/nopython/$name"
  done
done
run_runner 2 "no python3 on PATH" PATH="$work/nopython" -- && said "no python3 on PATH" "python3, which starts each run, is not on PATH"
for file in global.json pre-push-review.md pre-push-reviewer.md; do
  mv "$skill/tests/rollout-parity/template/$file" "$work/moved"
  run_runner 2 "no stand-in $file" -- && said "no stand-in $file" "template/$file, the stand-in Template's, is missing"
  mv "$work/moved" "$skill/tests/rollout-parity/template/$file"
done
for folder in fakes gh; do
  mv "$skill/tests/rollout-parity/defaults/$folder" "$work/moved"
  run_runner 2 "no defaults/$folder/" -- && said "no defaults/$folder/" "has no fakes/ and gh/ folders"
  mv "$work/moved" "$skill/tests/rollout-parity/defaults/$folder"
done
# The case's own args moves aside while each of these, args too, is a folder, then while each holds a carriage return.
mv "$cases/one/args" "$work/args"
for file in args env setup.sh after.sh missing ignore; do
  mkdir "$cases/one/$file"
  run_runner 2 "a $file that is a folder" -- && said "a $file that is a folder" "one/$file is not a file"
  rmdir "$cases/one/$file"
done
for file in args env missing ignore; do
  case "$file" in
    args) printf '#80\r\n' ;;
    env) printf 'A=b\r\n' ;;
    missing) printf 'pnpm\r\n' ;;
    ignore) printf 'HUP\r\n' ;;
  esac > "$cases/one/$file"
  run_runner 2 "a carriage return in $file" -- && said "a carriage return in $file" "one/$file holds a carriage return"
  rm "$cases/one/$file"
done
mv "$work/args" "$cases/one/args"
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
echo gh > "$cases/one/missing"
run_runner 2 "a missing that names no fake tool" -- && said "a missing that names no fake tool" "one/missing names 'gh'"
rm "$cases/one/missing"
echo KILL > "$cases/one/ignore"
run_runner 2 "an ignore that names another signal" -- && said "an ignore that names another signal" "one/ignore names 'KILL'"
rm "$cases/one/ignore"
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
# A random name is written XXXXXXXX, in output and in HOME (a second the same name -2, in the order they were written), the
# temporary folder <tmp> in a file's content, and a .git in HOME is left out of what is recorded and compared.
run_runner 0 "record mode, random names and a .git" PARITY_RECORD=1 STUB_MODE=names --
grep -qxF 'made rollout-session.XXXXXXXX, rollout-sdk.XXXXXXXX and rollout-other.abc' "$cases/fresh/expected.stdout" \
  || fail "record mode didn't write each random name XXXXXXXX in stdout, and only those: $(grep made "$cases/fresh/expected.stdout")"
[[ "$(cat "$cases/fresh/expected.home/logs/review-App.XXXXXXXX.log" 2> /dev/null)" == first ]] \
  || fail "record mode didn't write the first random name in HOME XXXXXXXX: $(ls "$cases/fresh/expected.home/logs" 2>&1)"
[[ "$(cat "$cases/fresh/expected.home/logs/review-App.XXXXXXXX.log-2" 2> /dev/null)" == "second, in <tmp>/run/tmp" ]] \
  || fail "record mode didn't write the second random name -2, with <tmp> in its content: $(ls "$cases/fresh/expected.home/logs" 2>&1)"
[[ "$(cat "$cases/fresh/expected.home/logs/review-App.XXXXXXXX.log-3" 2> /dev/null)" == third ]] \
  || fail "record mode didn't write the third random name -3: $(ls "$cases/fresh/expected.home/logs" 2>&1)"
[[ "$(cat "$cases/fresh/expected.home/logs/rollout-record.XXXXXXXX/review-App.XXXXXXXX.settings.json" 2> /dev/null)" == settings ]] \
  || fail "record mode didn't rename a random-named file in a random-named folder: $(find "$cases/fresh/expected.home/logs" 2>&1)"
[[ "$(cat "$cases/fresh/expected.home/logs/review-App.log" 2> /dev/null)" == plain ]] || fail "record mode renamed review-App.log, which has no random part"
[[ -f "$cases/fresh/expected.home/repo/file" && ! -e "$cases/fresh/expected.home/repo/.git" ]] || fail "record mode kept a .git in expected.home/"
run_runner 0 "random names and a .git, compared" STUB_MODE=names -- && said "random names and a .git, compared" "ok   fresh"
# The runner sorts HOME's entries in the C locale, where sort -n reads %T@'s . as a decimal point: a sort that orders them
# wrongly in any other locale (as sort -n does where the decimal point is a comma) leaves the comparison as it was.
mkdir -p "$work/sortbin"
real_sort="$(type -P sort)"
cat > "$work/sortbin/sort" << END
#!/usr/bin/env bash
[[ "\${LC_ALL-}" != C ]] || exec "$real_sort" "\$@"
"$real_sort" "\$@" | tr '\\0\\n' '\\n\\0' | tac | tr '\\0\\n' '\\n\\0'
END
chmod +x "$work/sortbin/sort"
run_runner 0 "random names and a .git, compared under another locale's sort" PATH="$work/sortbin:$work/hostbin:$PATH" STUB_MODE=names -- \
  && said "random names, another locale's sort" "ok   fresh"
# A listing of HOME that fails stops the runner rather than leaving HOME as the run left it.
mkdir -p "$work/findbin"
real_find="$(type -P find)"
cat > "$work/findbin/find" << END
#!/usr/bin/env bash
for word in "\$@"; do [[ "\$word" != -printf ]] || exit 1; done
exec "$real_find" "\$@"
END
chmod +x "$work/findbin/find"
run_runner 2 "a listing of HOME that fails" PATH="$work/findbin:$work/hostbin:$PATH" STUB_MODE=names -- \
  && said "a listing of HOME that fails" "can't list the run's HOME"
sed -i 's/!= -printf ]]/!= -print0 ]]/' "$work/findbin/find"
run_runner 2 "a listing of HOME's files that fails" PATH="$work/findbin:$work/hostbin:$PATH" STUB_MODE=names -- \
  && said "a listing of HOME's files that fails" "can't list the files in the run's HOME"
printf '#!/usr/bin/env bash\nexit 1\n' > "$work/sortbin/sort"
run_runner 2 "a sort of HOME's listing that fails" PATH="$work/sortbin:$work/hostbin:$PATH" STUB_MODE=names -- \
  && said "a sort of HOME's listing that fails" "can't list the run's HOME"
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
# FAKE_TOOL_NAME names the tool in place of the name it was called by, and is unset before the fixture runs.
# shellcheck disable=SC2016 # the fixture's own variable, expanded when it runs
printf '#!/usr/bin/env bash\necho "node ${FAKE_TOOL_NAME-unset}"\n' > "$fdir/node.sh"
: > "$flog"
call 0 "node unset" "a tool named by FAKE_TOOL_NAME" env FAKE_TOOL_NAME=node "$tool" -p process.execPath
[[ "$(cat "$flog")" == "node -p process.execPath" ]] || fail "fake tool: a call named by FAKE_TOOL_NAME logged as $(cat "$flog")"
code=0
env -u FAKE_HOME FAKE_TOOL_DIR="$fdir" FAKE_GH_LOG="$flog" "$work/links/git" > /dev/null 2>&1 || code=$?
[[ $code -eq 97 ]] || fail "fake tool with FAKE_HOME unset: exit $code, expected 97"

if [[ $failures -gt 0 ]]; then
  echo "rollout-parity-runner: $failures check(s) failed." >&2
  exit 1
fi
echo "rollout-parity-runner: every check passed."
