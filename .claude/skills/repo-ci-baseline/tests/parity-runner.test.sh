#!/usr/bin/env bash
# Tests for the parity suites' shared parts: the runner (tests/parity.test.sh) and the fake gh (tests/parity/fake-gh.sh).
# A suite itself only shows the runner passes against the implementation it was recorded from; this shows it fails, and
# how, against one that differs (its stdout, exit code, stderr, the gh calls it makes, the argv of a call that writes,
# their order, the files it leaves), that a case's stdin, env, tree/ and setup reach the run, and that the fake answers
# what its header says.
#
#   tests/parity-runner.test.sh
#
# Copies both into a throwaway skill folder with a suite of its own (stub, one case) and runs them against a stub
# implementation, so no real case is run or written. A last section checks the committed cases: each has a file git tracks in gh/ (the folder a
# fresh clone needs) and no fixture its expected.calls doesn't read. It first tries that check on cases made to be wrong,
# in a git repo of its own and in a folder git treats as outside any repo. Needs what the suite needs (bash 4.4,
# coreutils, diff, grep, sed), python3 for the stub reference record mode runs, git, and jq for the fake's --jq.
set -euo pipefail
# A hook, or git rebase --exec, may have set these; the throwaway repo below must not be the caller's.
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX GIT_COMMON_DIR

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
fail() {
  echo "FAIL: $*" >&2
  failures=$((failures + 1))
}

skill="$work/skill"
mkdir -p "$skill/tests/parity/stub/cases" "$skill/github"
cp "$here/parity.test.sh" "$skill/tests/"
cp "$here/parity/fake-gh.sh" "$skill/tests/parity/"
runner="$skill/tests/parity.test.sh"
fake="$skill/tests/parity/fake-gh.sh"
cases="$skill/tests/parity/stub/cases"

# The stub implementation: one repo query and one PR comment per argument, then a line on stdout and one on stderr.
# STUB_MODE makes it differ from what its case expects in one way at a time.
cat > "$work/stub.sh" << 'EOF'
#!/usr/bin/env bash
set -u
mode="${STUB_MODE:-ok}"
query='query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) { pullRequests(states: OPEN, first: 50) { nodes { id } } } }'
body=$'Hello "there"\n\nmarker'
[[ "$mode" != argv ]] || body=$'Hello "there"\nmarker'
for repo in "$@"; do
  owner="${repo%%/*}" name="${repo#*/}"
  [[ "$mode" != order ]] || gh pr comment 1 -R "$repo" --body "$body" > /dev/null
  case "$mode" in
    spaced) gh api graphql -f query="$(printf '%s\n' "$query" | sed 's/ /\n  /g')" -f owner="$owner" -F name="$name" > /dev/null ;;
    unmatched) gh api graphql -f query="$query" -F owner=elsewhere -F name="$name" > /dev/null ;;
    *) gh api graphql -f query="$query" -F owner="$owner" -F name="$name" > /dev/null ;;
  esac
  [[ "$mode" != extra ]] || gh api graphql -f query="$query" -F owner="$owner" -F name="$name" > /dev/null
  [[ "$mode" == order ]] || gh pr comment 1 -R "$repo" --body "$body" > /dev/null
  case "$mode" in
    stdout) echo "$repo other" ;;
    token) [[ -z "${GH_TOKEN:-}${GITHUB_TOKEN:-}" && -d "$GH_CONFIG_DIR" && -z "$(ls -A "$GH_CONFIG_DIR")" ]] && echo "$repo ok" ;;
    terminal) [[ -z "${FORCE_COLOR:-}${CLICOLOR_FORCE:-}${PYTHON_COLORS:-}${COLUMNS:-}${LINES:-}" && "${NO_COLOR:-}" == 1 && "${TERM:-}" == dumb ]] && echo "$repo ok" ;;
    stdin) if read -r line; then echo "$repo read $line"; else echo "$repo ok"; fi ;;
    env) echo "$repo ${CASE_VAR-unset} ${REPOSITORY-unset} ${GITHUB_REPOSITORY-unset} ${ANTHROPIC_API_KEY-unset} ${ANTHROPIC_MODEL-unset}" ;;
    write | write-other)
      [[ ! -e in.txt ]] || cp in.txt copied.txt
      [[ $mode == write ]] && echo "$repo" > out.txt || echo "elsewhere" > out.txt
      echo "$repo ok"
      ;;
    git)
      git fetch -q origin main
      where=elsewhere
      [[ "$(git rev-parse --show-toplevel)" != "$PWD" ]] || where=here
      echo "$repo $(git log -1 --format=%s) $(git rev-parse HEAD) $where $(git branch -r --format='%(refname:short)') ${GIT_DIR-unset}"
      ;;
    *) echo "$repo ok" ;;
  esac
  case "$mode" in
    stderr-count) printf 'note: %s\nnote: again\n' "$repo" >&2 ;;
    stderr-shape) echo "nope" >&2 ;;
    record) echo "warn: $repo (x) [y]" >&2 ;;
    *) echo "note: $repo" >&2 ;;
  esac
done
[[ "$mode" != hang ]] || sleep 30
[[ "$mode" != code ]] || exit 3
exit 0
EOF
chmod +x "$work/stub.sh"
# The reference record mode runs (python3 github/stub.py) is the same stub.
cat > "$skill/github/stub.py" << EOF
import os, sys
os.execvp("bash", ["bash", "$work/stub.sh", *sys.argv[1:]])
EOF

comment_call='pr.comment.o.app.1 gh pr comment 1 -R o/app --body "Hello \"there\"\n\nmarker"'
new_case() {
  local dir="$cases/$1"
  rm -rf "$dir"
  mkdir -p "$dir/gh"
  echo '{}' > "$dir/gh/repo.o.app.json"
  : > "$dir/gh/pr.comment.o.app.1.json"
  echo o/app > "$dir/args"
  echo "o/app ok" > "$dir/expected.stdout"
  echo 0 > "$dir/expected.code"
  printf '%s\n' repo.o.app "$comment_call" > "$dir/expected.calls"
  echo '^note: o/app$' > "$dir/expected.stderr"
}

# suite <expected exit> <what> [VAR=value]... [-- <command> [arg]...]: runs the copied runner on the stub suite from
# $work, keeps its stdout and stderr in $out and $err.
suite() {
  local want="$1" what="$2" code=0
  shift 2
  local -a vars=()
  while [[ $# -gt 0 && "$1" != -- ]]; do
    vars+=("$1")
    shift
  done
  [[ $# -eq 0 ]] || shift
  (cd "$work" && env ${vars[@]+"${vars[@]}"} bash "$runner" stub "$@" < /dev/null > "$work/out" 2> "$work/err") || code=$?
  out="$(cat "$work/out")" err="$(cat "$work/err")"
  if [[ $code -ne $want ]]; then
    fail "$what: exit $code, expected $want"$'\n'"$out"$'\n'"$err"
    return 1
  fi
}
says() {
  local what="$1" text="$2"
  grep -qF -- "$text" <<< "$out"$'\n'"$err" || fail "$what: no '$text' in"$'\n'"$out"$'\n'"$err"
}

# --- The runner against a stub that matches, and one that differs in each way it compares ---

new_case c1
suite 0 "a matching implementation" -- "$work/stub.sh" && says "a matching implementation" "1 case(s) pass against"
suite 0 "a command given by a relative path" -- ./stub.sh || true
suite 0 "a relative path as a later word" -- bash stub.sh || true
suite 0 "a query spaced differently, with -f and -F swapped" STUB_MODE=spaced -- ./stub.sh || true
suite 0 "a caller's GitHub token and gh config are not passed on" STUB_MODE=token GH_TOKEN=secret GITHUB_TOKEN=secret -- ./stub.sh || true
suite 0 "a caller's colour and terminal width are not passed on" STUB_MODE=terminal FORCE_COLOR=1 CLICOLOR_FORCE=1 \
  PYTHON_COLORS=1 COLUMNS=10 LINES=5 NO_COLOR= TERM=xterm-256color -- ./stub.sh || true
# launch.sh run --file=<script>: runs the script, and fails when its first word isn't `run` as given.
cat > "$work/launch.sh" << 'EOF'
#!/usr/bin/env bash
[[ "$1" == run && "$2" == --file=/* ]] || exit 9
f="${2#--file=}"
shift 2
exec bash "$f" "$@"
EOF
chmod +x "$work/launch.sh"
mkdir "$work/run"
suite 0 "a word that only happens to name a folder stays as it is, and an --option=path value is made absolute" \
  -- ./launch.sh run --file=stub.sh || true
(cd "$work" && STUB_MODE=stdin bash "$runner" stub ./stub.sh <<< "typed" > "$work/out" 2>&1) || fail "the implementation reads the caller's stdin: $(cat "$work/out")"
(cd "$skill" && CDPATH="$skill" bash tests/parity.test.sh stub "$work/stub.sh" > "$work/out" 2>&1) || fail "CDPATH breaks the runner: $(cat "$work/out")"

for difference in "stdout:stdout differs:" "code:exit 3, expected 0" "extra:gh calls differ:" \
  "argv:-$comment_call" "order:gh calls differ:" \
  "stderr-count:2 stderr line(s), expected 1" "stderr-shape:stderr line 1 'nope' doesn't match" \
  "unmatched:a gh call the fake couldn't answer" "hang:timed out after 1 second(s)"; do
  mode="${difference%%:*}" text="${difference#*:}"
  if suite 1 "an implementation that differs ($mode)" STUB_MODE="$mode" PARITY_TIMEOUT=1 -- ./stub.sh; then
    says "$mode" "FAIL c1"
    says "$mode" "$text"
    grep -qF "stub: 1 case(s) failed against $work/stub.sh: c1" <<< "$err" || fail "$mode: the stderr summary doesn't name the case: $err"
  fi
done
suite 1 "the unmatched call is named on one line" STUB_MODE=unmatched -- ./stub.sh || true
says "the unmatched call is named on one line" "UNMATCHED repo.elsewhere.app"

# --- Usage and setup errors exit 2 ---

suite 2 "a command that can't be run" -- ./no-such-command && says "a missing command" "the command './no-such-command' can't be run"
suite 2 "a bad PARITY_RECORD" PARITY_RECORD=2 || true
suite 2 "record mode with a command" PARITY_RECORD=1 -- ./stub.sh && says "record mode with a command" "records from the Python reference only"
suite 2 "a bad PARITY_TIMEOUT" PARITY_TIMEOUT=0 -- ./stub.sh || true
# runner <expected exit> <what> <text> [arg]...: the copied runner with these arguments instead of the stub suite's.
runner() {
  local want="$1" what="$2" text="$3" code=0
  shift 3
  (cd "$work" && bash "$runner" "$@" < /dev/null > "$work/out" 2>&1) || code=$?
  out="$(cat "$work/out")" err=""
  [[ $code -eq $want ]] || fail "$what: exit $code, expected $want"$'\n'"$out"
  says "$what" "$text"
}
runner 2 "no suite named" "name a suite"
runner 2 "a suite that doesn't exist" "no suite 'other'" other ./stub.sh
runner 2 "a suite name that could name a path" "'../stub' is not a suite name" ../stub ./stub.sh
# A suite's reference is github/<suite>.py, else the Template's .github/scripts/<suite>.py.
mkdir -p "$skill/tests/parity/tpl/cases" "$skill/template/owned/.github/scripts"
new_case c1
cp -R "$cases/c1" "$skill/tests/parity/tpl/cases/"
runner 2 "a suite with no script" "no script for suite 'tpl': neither github/tpl.py nor template/owned/.github/scripts/tpl.py is a file." tpl
cp "$skill/github/stub.py" "$skill/template/owned/.github/scripts/tpl.py"
runner 0 "a suite whose script is the Template's" "tpl: 1 case(s) pass against: python3 $skill/template/owned/.github/scripts/tpl.py" tpl
rm -rf "$skill/tests/parity/tpl" "$skill/template" "$cases/c1"
chmod -x "$fake"
suite 2 "a fake gh that isn't executable" -- ./stub.sh || true
chmod +x "$fake"

broken() {
  local what="$1" text="$2"
  shift 2
  new_case c1
  "$@"
  suite 2 "$what" -- ./stub.sh && says "$what" "$text"
}
broken "a missing expected.code" "c1 has no expected.code" rm "$cases/c1/expected.code"
broken "a missing expected.stderr" "c1 has no expected.stderr" rm "$cases/c1/expected.stderr"
broken "a missing expected.stdout" "c1 has no expected.stdout" rm "$cases/c1/expected.stdout"
broken "a missing expected.calls" "c1 has no expected.calls" rm "$cases/c1/expected.calls"
broken "a missing gh/" "c1 has no gh/ folder" rm -r "$cases/c1/gh"
broken "an expected.code that isn't one" "c1/expected.code is not an exit code" eval "echo zero > '$cases/c1/expected.code'"
broken "a blank stderr pattern" "c1/expected.stderr line 1 is blank" eval "echo > '$cases/c1/expected.stderr'"
broken "an invalid stderr pattern" "c1/expected.stderr line 1 is not a valid ERE" eval "echo '^note: (' > '$cases/c1/expected.stderr'"
broken "a carriage return in args" "c1/args holds a carriage return" eval "printf 'o/app\r\n' > '$cases/c1/args'"
broken "a carriage return in env" "c1/env holds a carriage return" eval "printf 'A=b\r\n' > '$cases/c1/env'"
broken "an env line that isn't NAME=value" "c1/env line 2 is not NAME=value: -u A" eval "printf 'A=b\n-u A\n' > '$cases/c1/env'"
broken "a tree/ that isn't a folder" "c1/tree is not a folder" touch "$cases/c1/tree"
broken "an expected.tree/ that isn't a folder" "c1/expected.tree is not a folder" touch "$cases/c1/expected.tree"
broken "a stdin that isn't a file" "c1/stdin is not a file" mkdir "$cases/c1/stdin"
broken "a setup that isn't a file" "c1/setup is not a file" mkdir "$cases/c1/setup"
rm -rf "$cases/c1"
suite 2 "no cases" -- ./stub.sh && says "no cases" "no cases under"

# --- Record mode, from the stub reference ---

new_case c1
rm "$cases/c1"/expected.*
suite 0 "record mode writes a new case" PARITY_RECORD=1 STUB_MODE=record || true
recorded="$(cat "$cases/c1/expected.stdout" "$cases/c1/expected.code" "$cases/c1/expected.calls")"
[[ "$recorded" == $'o/app ok\n0\nrepo.o.app\n'"$comment_call" ]] || fail "record mode wrote the wrong expected.stdout, .code or .calls: $recorded"
[[ "$(cat "$cases/c1/expected.stderr")" == '^warn: o/app \(x\) \[y\]$' ]] \
  || fail "a new case's stderr isn't matched literally: $(cat "$cases/c1/expected.stderr")"
suite 0 "a recorded case passes" STUB_MODE=record -- ./stub.sh || true
echo '^warn: ' > "$cases/c1/expected.stderr"
suite 0 "record mode keeps a loosened expected.stderr" PARITY_RECORD=1 STUB_MODE=record || true
[[ "$(cat "$cases/c1/expected.stderr")" == '^warn: ' ]] || fail "record mode rewrote a kept expected.stderr"
suite 1 "record mode reports a kept expected.stderr that no longer fits" PARITY_RECORD=1 STUB_MODE=stderr-shape \
  && says "a stale kept expected.stderr" "the kept expected.stderr no longer fits"
new_case c1
rm "$cases/c1"/expected.*
suite 1 "record mode refuses a case with an unmatched call" PARITY_RECORD=1 STUB_MODE=unmatched \
  && says "an unmatched call in record mode" "FAIL c1: not recorded"
[[ ! -e "$cases/c1/expected.stdout" ]] || fail "record mode wrote a case with an unmatched call"
rm -rf "$cases/c1"

# --- A case's stdin, env, tree/, setup and expected.tree/ ---

new_case c1
printf 'typed\nmore\n' > "$cases/c1/stdin"
echo "o/app read typed" > "$cases/c1/expected.stdout"
suite 0 "the case's stdin is the implementation's" STUB_MODE=stdin -- ./stub.sh || true

new_case c1
printf '%s\n' "CASE_VAR=a value=with=signs" "" > "$cases/c1/env"
echo "o/app a value=with=signs unset unset unset unset" > "$cases/c1/expected.stdout"
suite 0 "the case's env is set, and a caller's repository and Anthropic key and model are not passed on" STUB_MODE=env \
  REPOSITORY=caller/repo GITHUB_REPOSITORY=caller/repo ANTHROPIC_API_KEY=secret ANTHROPIC_MODEL=model -- ./stub.sh || true

# write_case <name>: a case whose tree holds in.txt, which the stub copies, and whose expected.tree/ holds what it writes.
write_case() {
  new_case "$1"
  mkdir -p "$cases/$1/tree" "$cases/$1/expected.tree"
  echo "input" > "$cases/$1/tree/in.txt"
  cp "$cases/$1/tree/in.txt" "$cases/$1/expected.tree/in.txt"
  cp "$cases/$1/tree/in.txt" "$cases/$1/expected.tree/copied.txt"
  echo "o/app" > "$cases/$1/expected.tree/out.txt"
}
write_case c1
suite 0 "a run that leaves its expected.tree/" STUB_MODE=write -- ./stub.sh || true
suite 1 "a run that writes other bytes" STUB_MODE=write-other -- ./stub.sh && says "a file that differs" "the files differ:"
rm "$cases/c1/expected.tree/copied.txt"
suite 1 "a run that writes a file expected.tree/ hasn't" STUB_MODE=write -- ./stub.sh && says "an extra file" "Only in"
new_case c1
suite 1 "a run that writes a file with no expected.tree/" STUB_MODE=write -- ./stub.sh \
  && says "a file with no expected.tree/" "the run left files, and the case has no expected.tree/:"$'\n'"./out.txt"
# Each case runs in a folder of its own: c2 would see c1's files in a shared one.
write_case c1
new_case c2
mkdir "$cases/c2/expected.tree"
echo "o/app" > "$cases/c2/expected.tree/out.txt"
suite 0 "each case starts from its own folder" STUB_MODE=write -- ./stub.sh || true
rm -rf "$cases/c2"

write_case c1
rm -rf "$cases/c1/expected.tree" "$cases/c1/expected.stdout"
suite 0 "record mode writes expected.tree/" PARITY_RECORD=1 STUB_MODE=write || true
[[ "$(cd "$cases/c1/expected.tree" && find . -type f | sort | tr '\n' ' ')" == "./copied.txt ./in.txt ./out.txt " ]] \
  || fail "record mode wrote the wrong expected.tree/: $(cd "$cases/c1/expected.tree" && find . | sort)"
rm -rf "$cases/c1/tree"
suite 0 "record mode removes expected.tree/ when the run leaves no file" PARITY_RECORD=1 || true
[[ ! -e "$cases/c1/expected.tree" ]] || fail "record mode kept an expected.tree/ the run no longer leaves"

# setup builds a git repo in the run's folder, with a local origin under PARITY_SCRATCH; the run sees it, not a caller's.
new_case c1
cat > "$cases/c1/setup" << 'EOF'
set -euo pipefail
[[ -d "$PARITY_SCRATCH" && "$PARITY_SCRATCH" != "$PWD"/* ]]
git init -q -b main .
git commit -q --allow-empty -m "the setup's commit"
git init -q --bare -b main "$PARITY_SCRATCH/origin.git"
git remote add origin "$PARITY_SCRATCH/origin.git"
git push -q origin main
EOF
rm "$cases/c1/expected.stdout"
suite 0 "record mode runs the setup" PARITY_RECORD=1 STUB_MODE=git || true
# The fixed identity and dates give this hash on every machine, which is what lets a gh fixture name a setup's commit.
[[ "$(cat "$cases/c1/expected.stdout")" == "o/app the setup's commit d2acfaa173b3254e8fa804e9d51a9444485f29b3 here origin/main unset" ]] \
  || fail "the run didn't see the setup's repo, its origin and its fixed hash: $(cat "$cases/c1/expected.stdout")"
[[ ! -e "$cases/c1/expected.tree" ]] || fail "record mode wrote an expected.tree/ for a run that left only .git"
# Again with a caller's GIT_DIR and git config set: they reach neither the setup nor the run.
suite 0 "a caller's GIT_DIR and git config reach no case" STUB_MODE=git GIT_DIR="$work/nowhere" \
  GIT_CONFIG_GLOBAL="$work/nowhere.gitconfig" -- ./stub.sh || true
# A run that leaves a file beside its .git: record mode keeps the file and drops the .git.
echo 'echo kept > kept.txt' >> "$cases/c1/setup"
suite 0 "record mode with a file beside the .git" PARITY_RECORD=1 STUB_MODE=git || true
[[ "$(cd "$cases/c1/expected.tree" && find . | sort | tr '\n' ' ')" == ". ./kept.txt " ]] \
  || fail "record mode's expected.tree/ isn't the run's files without its .git: $(cd "$cases/c1/expected.tree" && find . | sort)"
new_case c1
echo 'echo the setup broke; exit 3' > "$cases/c1/setup"
suite 2 "a setup that fails" -- ./stub.sh && says "a setup that fails" "c1/setup failed:"$'\n'"the setup broke"
rm -rf "$cases/c1"

# --- The fake gh on its own ---

gh_dir="$work/gh" gh_log="$work/gh.log"
mkdir "$gh_dir" "$gh_dir/threads.dir.key.json"
echo '{"a":1}' > "$gh_dir/repo.o.app.json"
echo 'HTTP 502: Bad Gateway' > "$gh_dir/repo.o.down.fail"
for key in events.PR_1 events.PR_1.E1 events.PR_1. comments.PR_1 comments.PR_1.C1 threads.PR_1 rest.repos_o_app \
  threads.a_b events.PR_1.c_d; do
  echo "\"$key\"" > "$gh_dir/$key.json"
done
mkdir -p "$work/escape"
echo '"outside"' > "$work/escape.json"

# fake <expected exit> <expected stdout or -> <what> [arg]...: runs the fake with a fresh log.
fake() {
  local want="$1" stdout="$2" what="$3" code=0
  shift 3
  : > "$gh_log"
  FAKE_GH_DIR="$gh_dir" FAKE_GH_LOG="$gh_log" "$fake" "$@" > "$work/fout" 2> "$work/ferr" || code=$?
  [[ $code -eq $want ]] || fail "fake gh, $what: exit $code, expected $want ($(cat "$work/ferr"))"
  [[ "$stdout" == - || "$(cat "$work/fout")" == "$stdout" ]] || fail "fake gh, $what: stdout '$(cat "$work/fout")', expected '$stdout'"
}
logged() {
  [[ "$(cat "$gh_log")" == "$2" ]] || fail "fake gh, $1: logged '$(cat "$gh_log")', expected '$2'"
}

repo_q='query { repository { pullRequests(states: OPEN, first: 50) { nodes { id } } } }'
events_q='query { node { timelineItems(first: 100) { nodes { id } } } }'
comments_q='query { node { comments(last: 100) { nodes { id } } } }'
threads_q='query { node { reviewThreads(first: 100) { nodes { comments(first: 10) { nodes { url } } } } } }'

fake 0 '{"a":1}' "the repo query" api graphql -f query="$repo_q" -F owner=o -F name=app && logged "the repo query" repo.o.app
fake 0 '{"a":1}' "the repo query with its spacing changed" api graphql -F name=app -f query="${repo_q// /$'\n'}" -f owner=o
fake 1 '' "a .fail fixture" api graphql -f query="$repo_q" -F owner=o -F name=down && logged "a .fail fixture" repo.o.down
grep -qF 'HTTP 502: Bad Gateway' "$work/ferr" || fail "fake gh: a .fail fixture isn't printed on stderr"
fake 0 '"events.PR_1"' "the events query" api graphql -f query="$events_q" -f id=PR_1
fake 0 '"events.PR_1.E1"' "an events page" api graphql -f query="$events_q" -f id=PR_1 -f cursor=E1
fake 0 '"events.PR_1."' "an empty cursor has its own key" api graphql -f query="$events_q" -f id=PR_1 -f cursor=
fake 0 '"comments.PR_1"' "the comments query" api graphql -f query="$comments_q" -F id=PR_1
fake 0 '"comments.PR_1.C1"' "a comments page" api graphql -f query="$comments_q" -f id=PR_1 -f cursor=C1
fake 0 '"threads.PR_1"' "the threads query" api graphql -f query="$threads_q" -f id=PR_1
fake 0 '"rest.repos_o_app"' "a REST path" api repos/o/app && logged "a REST path" rest.repos_o_app
fake 0 '"threads.a_b"' "a / in an id is a _" api graphql -f query="$threads_q" -f id=a/b
fake 0 '"events.PR_1.c_d"' "a / in a cursor is a _" api graphql -f query="$events_q" -f id=PR_1 -f cursor=c/d
fake 97 '' "a value can't climb out of FAKE_GH_DIR" api graphql -f query="$threads_q" -f id=../escape
logged "a value can't climb out of FAKE_GH_DIR" "UNMATCHED threads..._escape"
fake 97 '' "a call no fixture answers" api graphql -f query="$repo_q" -F owner=o -F name=none
logged "a call no fixture answers" "UNMATCHED repo.o.none"
fake 97 '' "a query it doesn't know" api graphql -f query=$'query {\n viewer { login } }'
[[ "$(wc -l < "$gh_log")" -eq 1 ]] || fail "fake gh: an unknown query isn't logged on one line: $(cat "$gh_log")"
fake 97 '' "an unknown flag" api graphql -f query="${repo_q// /$'\n'}" --paginate
[[ "$(wc -l < "$gh_log")" -eq 1 && "$(cat "$gh_log")" == "UNMATCHED gh api graphql -f query="*"--paginate" ]] \
  || fail "fake gh: an unknown flag with a multi-line query isn't logged as one line naming the call: $(cat "$gh_log")"
fake 97 '' "an unknown variable" api graphql -f query="$repo_q" -F owner=o -F name=app -f number=7
[[ "$(cat "$gh_log")" == "UNMATCHED gh api graphql "*"number=7" ]] || fail "fake gh: an unknown variable was answered: $(cat "$gh_log")"
fake 97 '' "a flag with no value" api graphql -f
fake 97 '' "a command other than api" pr list && logged "a command other than api" "UNMATCHED gh pr list"
fake 97 '' "a REST call with more arguments" api repos/o/app --method POST

# A REST call with --paginate and --jq: real jq over the fixture, a string printed raw, and the whole argv logged.
printf '%s\n' '[{"n":1,"draft":false,"t":"a\u2028b"},{"n":2,"draft":true,"t":"c"}]' > "$gh_dir/rest.repos_o_app_releases?per_page=100.json"
rel="repos/o/app/releases?per_page=100"
fake 0 "$(printf '%s\n' '[{"n":1,"draft":false,"t":"a\u2028b"},{"n":2,"draft":true,"t":"c"}]')" "--paginate alone" api "$rel" --paginate
logged "--paginate alone" "rest.repos_o_app_releases?per_page=100 gh api \"$rel\" --paginate"
fake 0 $'{"n":1,"draft":false,"t":"a\u2028b"}' "--jq, with --paginate" api "$rel" --paginate --jq '.[] | select(.draft | not) | @json'
logged "--jq, with --paginate" "rest.repos_o_app_releases?per_page=100 gh api \"$rel\" --paginate --jq \".[] | select(.draft | not) | @json\""
fake 0 $'a\u2028b\nc' "a string result is printed raw" api "$rel" --jq '.[].t'
fake 0 '' "a result of nothing prints nothing" api "$rel" --jq '.[] | select(.n > 5)'
[[ ! -s "$work/fout" ]] || fail "fake gh: a --jq with no result printed '$(cat "$work/fout")'"
fake 0 '"rest.repos_o_app"' "a plain REST call is still logged as its key" api repos/o/app && logged "a plain REST call" rest.repos_o_app
fake 97 '' "an expression jq can't run" api "$rel" --jq '.[] | nope('
logged "an expression jq can't run" "UNMATCHED rest.repos_o_app_releases?per_page=100 (jq can't run .[] | nope( over its fixture rest.repos_o_app_releases?per_page=100.json)"
fake 97 '' "--jq twice" api "$rel" --jq . --jq .
fake 97 '' "--jq with no expression" api "$rel" --jq
fake 97 '' "an empty --jq" api "$rel" --jq ''
fake 97 '' "--paginate twice" api "$rel" --paginate --paginate
echo 'HTTP 502: Bad Gateway' > "$gh_dir/rest.repos_o_down.fail"
fake 1 '' "a .fail fixture for a call with --jq" api repos/o/down --paginate --jq .title
logged "a .fail fixture for a call with --jq" 'rest.repos_o_down gh api repos/o/down --paginate --jq .title'
# jq missing from PATH: the call is unmatched, not answered with the raw fixture.
mkdir "$work/nojq"
for tool in bash cat mktemp rm; do ln -s "$(command -v "$tool")" "$work/nojq/$tool"; done
code=0
PATH="$work/nojq" FAKE_GH_DIR="$gh_dir" FAKE_GH_LOG="$gh_log" "$work/nojq/bash" "$fake" api "$rel" --jq . > "$work/fout" 2>&1 || code=$?
[[ $code -eq 97 && "$(tail -1 "$gh_log")" == *"jq, which a call with --jq needs, is not on PATH"* ]] \
  || fail "fake gh: jq missing gives exit $code and logged $(tail -1 "$gh_log")"
fake 97 '' "an unreadable fixture" api graphql -f query="$threads_q" -f id=dir.key
logged "an unreadable fixture is logged as unmatched, not as answered" \
  "UNMATCHED threads.dir.key (its fixture threads.dir.key.json is not a readable file)"

# The gh pr calls: each logged as its key and its whole argv, one line however many lines the body holds.
for key in pr.ready.o.app.7 pr.update-branch.o.app.7 pr.comment.o.app.7 pr.edit.o.app.7 pr.merge.o.app.7 pr.view.o.app.7.state \
  pr.view.o.a_b.7.state; do
  echo "$key" > "$gh_dir/$key.json"
done
echo 'HTTP 403: refused' > "$gh_dir/pr.edit.o.down.7.fail"
fake 0 pr.ready.o.app.7 "gh pr ready" pr ready 7 -R o/app && logged "gh pr ready" "pr.ready.o.app.7 gh pr ready 7 -R o/app"
fake 0 pr.update-branch.o.app.7 "gh pr update-branch" pr update-branch 7 -R o/app
fake 0 pr.comment.o.app.7 "gh pr comment" pr comment 7 -R o/app --body $'a "b" \\c\n\td\r'
logged "gh pr comment, its body quoted and escaped" 'pr.comment.o.app.7 gh pr comment 7 -R o/app --body "a \"b\" \\c\n\td\r"'
fake 0 pr.edit.o.app.7 "gh pr edit --add-reviewer" pr edit 7 -R o/app --add-reviewer @copilot
logged "gh pr edit --add-reviewer" "pr.edit.o.app.7 gh pr edit 7 -R o/app --add-reviewer @copilot"
fake 0 pr.edit.o.app.7 "gh pr edit --add-label, --repo" pr edit 7 --repo o/app --add-label review:claude
logged "gh pr edit --add-label, --repo" "pr.edit.o.app.7 gh pr edit 7 --repo o/app --add-label review:claude"
fake 0 pr.merge.o.app.7 "gh pr merge" pr merge 7 -R o/app --auto --squash --match-head-commit abc
fake 0 pr.view.o.app.7.state "gh pr view" pr view 7 -R o/app --json state --jq .state
logged "gh pr view, its --jq quoted" 'pr.view.o.app.7.state gh pr view 7 -R o/app --json state --jq .state'
fake 0 - "an empty argument is quoted" pr comment 7 -R o/app --body ''
logged "an empty argument is quoted" 'pr.comment.o.app.7 gh pr comment 7 -R o/app --body ""'
fake 0 pr.view.o.a_b.7.state "a / in a repo's name is a _" pr view 7 -R o/a/b --json state --jq .state
fake 1 '' "a .fail fixture for a gh pr call" pr edit 7 -R o/down --add-label review:claude
logged "a .fail fixture for a gh pr call" "pr.edit.o.down.7 gh pr edit 7 -R o/down --add-label review:claude"
fake 97 '' "a gh pr subcommand the fake doesn't know" pr close 7 -R o/app
fake 97 '' "a flag of another subcommand" pr ready 7 -R o/app --body x
fake 97 '' "a flag it doesn't know" pr merge 7 -R o/app --rebase
fake 97 '' "a flag with no value" pr comment 7 -R o/app --body
fake 97 '' "no PR number" pr ready -R o/app
fake 97 '' "two PR numbers" pr ready 7 8 -R o/app
fake 97 '' "no repo" pr ready 7
fake 97 '' "a repo with no /" pr ready 7 -R app
fake 97 '' "a view without --jq" pr view 7 -R o/app --json state
fake 97 '' "a call with no fixture" pr ready 8 -R o/app && logged "a gh pr call with no fixture" "UNMATCHED pr.ready.o.app.8"

# The nth call with a key gets <key>.<n>.json or .fail when there is one, else <key>.json or .fail.
seq_dir="$work/seq"
mkdir "$seq_dir"
echo '"first"' > "$seq_dir/repo.o.app.1.json"
echo 'HTTP 502: second' > "$seq_dir/repo.o.app.2.fail"
echo '"third"' > "$seq_dir/repo.o.app.3.json"
echo '"one view"' > "$seq_dir/pr.view.o.app.7.state.1.json"
echo '"only once"' > "$seq_dir/repo.o.lib.1.json"
: > "$gh_log"
seq_answers=""
for call in app view app app lib app lib; do
  code=0
  if [[ $call == view ]]; then
    FAKE_GH_DIR="$seq_dir" FAKE_GH_LOG="$gh_log" "$fake" pr view 7 -R o/app --json state --jq .state > "$work/fout" 2> "$work/ferr" || code=$?
  else
    FAKE_GH_DIR="$seq_dir" FAKE_GH_LOG="$gh_log" "$fake" api graphql -f query="$repo_q" -F owner=o -F name="$call" > "$work/fout" 2> "$work/ferr" || code=$?
  fi
  seq_answers+="$code:$(cat "$work/fout")"$'\n'
done
want_seq=$'0:"first"\n0:"one view"\n1:\n0:"third"\n0:"only once"\n97:\n97:\n'
[[ "$seq_answers" == "$want_seq" ]] || fail "fake gh, sequenced answers: got"$'\n'"$seq_answers"
logged "sequenced answers" $'repo.o.app\npr.view.o.app.7.state gh pr view 7 -R o/app --json state --jq .state\nrepo.o.app\nrepo.o.app\nrepo.o.lib\nUNMATCHED repo.o.app\nUNMATCHED repo.o.lib'
echo '"the rest"' > "$seq_dir/repo.o.app.json"
FAKE_GH_DIR="$seq_dir" FAKE_GH_LOG="$gh_log" "$fake" api graphql -f query="$repo_q" -F owner=o -F name=app > "$work/fout" 2> /dev/null \
  || fail "fake gh: a call past the numbered fixtures isn't answered from <key>.json"
[[ "$(cat "$work/fout")" == '"the rest"' ]] || fail "fake gh: a call past the numbered fixtures got $(cat "$work/fout")"

code=0
env -u FAKE_GH_DIR FAKE_GH_LOG="$gh_log" "$fake" api repos/o/app > /dev/null 2>&1 || code=$?
[[ $code -eq 97 ]] || fail "fake gh: FAKE_GH_DIR unset gives exit $code, expected 97"
code=0
env -u FAKE_GH_LOG FAKE_GH_DIR="$gh_dir" "$fake" api repos/o/app > /dev/null 2>&1 || code=$?
[[ $code -eq 97 ]] || fail "fake gh: FAKE_GH_LOG unset gives exit $code, expected 97"

# --- The committed cases: what a fresh clone has, and no fixture nothing reads ---

# case_problems <cases folder>: one line per case a fresh clone would break (no file in its gh/ that git tracks) or with
# a fixture no call in its expected.calls reads (a <key>.<n> one when fewer than n calls have the key); nothing when every
# case is sound. git runs without the caller's GIT_DIR,
# GIT_WORK_TREE, GIT_INDEX_FILE, GIT_PREFIX and GIT_COMMON_DIR (a hook sets them), and outside a git work tree only the
# fixtures are checked.
case_problems() {
  local cases_dir="$1" tracked="" dir name fixture key keys
  local -a clean_git=(env -u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE -u GIT_PREFIX -u GIT_COMMON_DIR git -C "$cases_dir")
  local in_tree=""
  [[ "$("${clean_git[@]}" rev-parse --is-inside-work-tree 2> /dev/null)" != true ]] || in_tree=1
  [[ -z "$in_tree" ]] || tracked=$'\n'"$("${clean_git[@]}" ls-files -- .)"
  for dir in "$cases_dir"/*/; do
    dir="${dir%/}" name="${dir##*/}"
    if [[ -n "$in_tree" && "$tracked" != *$'\n'"$name/gh/"* ]]; then
      echo "case $name has no file in gh/ that git tracks, so a fresh clone has no gh/ folder: git add its gh/ files, or add gh/.gitkeep when it has none"
    fi
    for fixture in "$dir"/gh/*.json "$dir"/gh/*.fail; do
      [[ -e "$fixture" ]] || continue
      # A key is the first word of its logged call. <key>.<n> is the nth call's answer, unless a call has that whole key
      # (a PR's number ends one: pr.ready.<owner>.<name>.<n>).
      key="${fixture##*/}" key="${key%.*}"
      keys="$(cut -d ' ' -f 1 "$dir/expected.calls")"
      grep -qxF -- "$key" <<< "$keys" && continue
      if [[ "$key" =~ ^(.+)\.([1-9][0-9]*)$ ]] && (($(grep -cxF -- "${BASH_REMATCH[1]}" <<< "$keys" || true) >= BASH_REMATCH[2])); then
        continue
      fi
      echo "case $name: gh/${fixture##*/} answers no call in expected.calls"
    done
  done
}

# Against cases made to be wrong: an untracked gh/, a stray fixture, and sound ones (an empty-cursor key among them).
probe="$work/probe"
mkdir -p "$probe/cases"
for name in sound untracked stray; do
  mkdir -p "$probe/cases/$name/gh"
  echo '{}' > "$probe/cases/$name/gh/repo.o.app.json"
  printf '%s\n' repo.o.app 'events.PR_1.' 'pr.ready.o.app.7 gh pr ready 7 -R o/app' repo.o.app > "$probe/cases/$name/expected.calls"
done
echo '{}' > "$probe/cases/sound/gh/events.PR_1..json"
echo '{}' > "$probe/cases/sound/gh/repo.o.app.2.json"
: > "$probe/cases/sound/gh/pr.ready.o.app.7.json"
echo '{}' > "$probe/cases/stray/gh/repo.o.app.3.json"
git init -q "$probe"
# A copy inside the probe repo, with git told to look no higher than its folder: outside any work tree, wherever TMPDIR is.
mkdir "$probe/plain"
cp -r "$probe/cases" "$probe/plain/cases"
git -C "$probe" add cases/sound cases/stray
want_tree="case untracked has no file in gh/ that git tracks, so a fresh clone has no gh/ folder: git add its gh/ files, or add gh/.gitkeep when it has none"
want_stray="case stray: gh/repo.o.app.3.json answers no call in expected.calls"
got="$(case_problems "$probe/cases")"
[[ "$got" == "$want_stray"$'\n'"$want_tree" || "$got" == "$want_tree"$'\n'"$want_stray" ]] \
  || fail "the committed-cases check, on an untracked gh/ and a stray fixture, said: $got"
got="$(GIT_DIR="$work/nowhere" GIT_WORK_TREE="$work" case_problems "$probe/cases")"
[[ "$got" == *"$want_tree"* ]] || fail "the committed-cases check follows the caller's GIT_DIR: $got"
got="$(GIT_CEILING_DIRECTORIES="$probe/plain" case_problems "$probe/plain/cases")"
[[ "$got" == "$want_stray" ]] || fail "the committed-cases check outside a git work tree said: $got"

for suite_dir in "$here"/parity/*/; do
  got="$(case_problems "$suite_dir/cases")"
  [[ -z "$got" ]] || fail "the committed cases of ${suite_dir%/}:"$'\n'"$got"
done

if [[ $failures -ne 0 ]]; then
  echo "parity-runner: $failures check(s) failed." >&2
  exit 1
fi
echo "parity-runner: every check passed."
