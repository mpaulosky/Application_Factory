#!/usr/bin/env bash
# Tests for the status parity suite's own parts: the runner (tests/status-parity.test.sh) and the fake gh
# (tests/status-parity/fake-gh.sh). The suite itself only shows the runner passes against the implementation it was
# recorded from; this shows it fails, and how, against one that differs, and that the fake answers what its header says.
#
#   tests/status-parity-runner.test.sh
#
# Copies both into a throwaway skill folder with one case of its own and runs them against a stub implementation, so no
# real case is run or written. A last section only reads the committed cases: each has a file git tracks in gh/ (the
# folder a fresh clone needs) and no fixture its expected.calls doesn't read. Needs what the suite needs (bash 4.4,
# coreutils, diff, grep, sed), python3 for the stub reference record mode runs, and git (outside a git work tree the
# tracked-file check is skipped).
set -euo pipefail

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
fail() {
  echo "FAIL: $*" >&2
  failures=$((failures + 1))
}

skill="$work/skill"
mkdir -p "$skill/tests/status-parity/cases" "$skill/github"
cp "$here/status-parity.test.sh" "$skill/tests/"
cp "$here/status-parity/fake-gh.sh" "$skill/tests/status-parity/"
runner="$skill/tests/status-parity.test.sh"
fake="$skill/tests/status-parity/fake-gh.sh"
cases="$skill/tests/status-parity/cases"

# The stub implementation: one repo query per argument, then a line on stdout and one on stderr. STUB_MODE makes it
# differ from what its case expects in one way at a time.
cat > "$work/stub.sh" << 'EOF'
#!/usr/bin/env bash
set -u
mode="${STUB_MODE:-ok}"
query='query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) { pullRequests(states: OPEN, first: 50) { nodes { id } } } }'
for repo in "$@"; do
  owner="${repo%%/*}" name="${repo#*/}"
  case "$mode" in
    spaced) gh api graphql -f query="$(printf '%s\n' "$query" | sed 's/ /\n  /g')" -f owner="$owner" -F name="$name" > /dev/null ;;
    unmatched) gh api graphql -f query="$query" -F owner=elsewhere -F name="$name" > /dev/null ;;
    *) gh api graphql -f query="$query" -F owner="$owner" -F name="$name" > /dev/null ;;
  esac
  [[ "$mode" != extra ]] || gh api graphql -f query="$query" -F owner="$owner" -F name="$name" > /dev/null
  case "$mode" in
    stdout) echo "$repo other" ;;
    token) [[ -z "${GH_TOKEN:-}${GITHUB_TOKEN:-}" && -d "$GH_CONFIG_DIR" && -z "$(ls -A "$GH_CONFIG_DIR")" ]] && echo "$repo ok" ;;
    terminal) [[ -z "${FORCE_COLOR:-}${CLICOLOR_FORCE:-}${PYTHON_COLORS:-}${COLUMNS:-}${LINES:-}" && "${NO_COLOR:-}" == 1 && "${TERM:-}" == dumb ]] && echo "$repo ok" ;;
    stdin) if read -r line; then echo "$repo read $line"; else echo "$repo ok"; fi ;;
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
# The reference record mode runs (python3 github/status.py) is the same stub.
cat > "$skill/github/status.py" << EOF
import os, sys
os.execvp("bash", ["bash", "$work/stub.sh", *sys.argv[1:]])
EOF

new_case() {
  local dir="$cases/$1"
  rm -rf "$dir"
  mkdir -p "$dir/gh"
  echo '{}' > "$dir/gh/repo.o.app.json"
  echo o/app > "$dir/args"
  echo "o/app ok" > "$dir/expected.stdout"
  echo 0 > "$dir/expected.code"
  echo repo.o.app > "$dir/expected.calls"
  echo '^note: o/app$' > "$dir/expected.stderr"
}

# suite <expected exit> <what> [VAR=value]... [-- <command> [arg]...]: runs the copied runner from $work, keeps its
# stdout and stderr in $out and $err.
suite() {
  local want="$1" what="$2" code=0
  shift 2
  local -a vars=()
  while [[ $# -gt 0 && "$1" != -- ]]; do
    vars+=("$1")
    shift
  done
  [[ $# -eq 0 ]] || shift
  (cd "$work" && env ${vars[@]+"${vars[@]}"} bash "$runner" "$@" < /dev/null > "$work/out" 2> "$work/err") || code=$?
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
(cd "$work" && STUB_MODE=stdin bash "$runner" ./stub.sh <<< "typed" > "$work/out" 2>&1) || fail "the implementation reads the caller's stdin: $(cat "$work/out")"
(cd "$skill" && CDPATH="$skill" bash tests/status-parity.test.sh "$work/stub.sh" > "$work/out" 2>&1) || fail "CDPATH breaks the runner: $(cat "$work/out")"

for difference in "stdout:stdout differs:" "code:exit 3, expected 0" "extra:gh calls differ:" \
  "stderr-count:2 stderr line(s), expected 1" "stderr-shape:stderr line 1 'nope' doesn't match" \
  "unmatched:a gh call the fake couldn't answer" "hang:timed out after 1 second(s)"; do
  mode="${difference%%:*}" text="${difference#*:}"
  if suite 1 "an implementation that differs ($mode)" STUB_MODE="$mode" STATUS_PARITY_TIMEOUT=1 -- ./stub.sh; then
    says "$mode" "FAIL c1"
    says "$mode" "$text"
    grep -qF "1 case(s) failed against $work/stub.sh: c1" <<< "$err" || fail "$mode: the stderr summary doesn't name the case: $err"
  fi
done
suite 1 "the unmatched call is named on one line" STUB_MODE=unmatched -- ./stub.sh || true
says "the unmatched call is named on one line" "UNMATCHED repo.elsewhere.app"

# --- Usage and setup errors exit 2 ---

suite 2 "a command that can't be run" -- ./no-such-command && says "a missing command" "the command './no-such-command' can't be run"
suite 2 "a bad STATUS_PARITY_RECORD" STATUS_PARITY_RECORD=2 || true
suite 2 "record mode with a command" STATUS_PARITY_RECORD=1 -- ./stub.sh && says "record mode with a command" "records from the Python reference only"
suite 2 "a bad STATUS_PARITY_TIMEOUT" STATUS_PARITY_TIMEOUT=0 -- ./stub.sh || true
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
rm -rf "$cases/c1"
suite 2 "no cases" -- ./stub.sh && says "no cases" "no cases under"

# --- Record mode, from the stub reference ---

new_case c1
rm "$cases/c1"/expected.*
suite 0 "record mode writes a new case" STATUS_PARITY_RECORD=1 STUB_MODE=record || true
recorded="$(cat "$cases/c1/expected.stdout" "$cases/c1/expected.code" "$cases/c1/expected.calls")"
[[ "$recorded" == $'o/app ok\n0\nrepo.o.app' ]] || fail "record mode wrote the wrong expected.stdout, .code or .calls: $recorded"
[[ "$(cat "$cases/c1/expected.stderr")" == '^warn: o/app \(x\) \[y\]$' ]] \
  || fail "a new case's stderr isn't matched literally: $(cat "$cases/c1/expected.stderr")"
suite 0 "a recorded case passes" STUB_MODE=record -- ./stub.sh || true
echo '^warn: ' > "$cases/c1/expected.stderr"
suite 0 "record mode keeps a loosened expected.stderr" STATUS_PARITY_RECORD=1 STUB_MODE=record || true
[[ "$(cat "$cases/c1/expected.stderr")" == '^warn: ' ]] || fail "record mode rewrote a kept expected.stderr"
suite 1 "record mode reports a kept expected.stderr that no longer fits" STATUS_PARITY_RECORD=1 STUB_MODE=stderr-shape \
  && says "a stale kept expected.stderr" "the kept expected.stderr no longer fits"
new_case c1
rm "$cases/c1"/expected.*
suite 1 "record mode refuses a case with an unmatched call" STATUS_PARITY_RECORD=1 STUB_MODE=unmatched \
  && says "an unmatched call in record mode" "FAIL c1: not recorded"
[[ ! -e "$cases/c1/expected.stdout" ]] || fail "record mode wrote a case with an unmatched call"
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
fake 97 '' "an unreadable fixture" api graphql -f query="$threads_q" -f id=dir.key
logged "an unreadable fixture is logged as unmatched, not as answered" \
  "UNMATCHED threads.dir.key (its fixture threads.dir.key.json is not a readable file)"
code=0
env -u FAKE_GH_DIR FAKE_GH_LOG="$gh_log" "$fake" api repos/o/app > /dev/null 2>&1 || code=$?
[[ $code -eq 97 ]] || fail "fake gh: FAKE_GH_DIR unset gives exit $code, expected 97"
code=0
env -u FAKE_GH_LOG FAKE_GH_DIR="$gh_dir" "$fake" api repos/o/app > /dev/null 2>&1 || code=$?
[[ $code -eq 97 ]] || fail "fake gh: FAKE_GH_LOG unset gives exit $code, expected 97"

# --- The committed cases: what a fresh clone has, and no fixture nothing reads ---

# case_problems <cases folder>: one line per case a fresh clone would break (no file in its gh/ that git tracks) or with
# a fixture no call in its expected.calls reads; nothing when every case is sound. git runs without the caller's GIT_*
# variables (a hook sets them), and outside a git work tree only the fixtures are checked.
case_problems() {
  local cases_dir="$1" tracked="" dir name fixture key
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
      key="${fixture##*/}" key="${key%.*}"
      grep -qxF -- "$key" "$dir/expected.calls" || echo "case $name: gh/${fixture##*/} answers no call in expected.calls"
    done
  done
}

# Against cases made to be wrong: an untracked gh/, a stray fixture, and sound ones (an empty-cursor key among them).
probe="$work/probe"
mkdir -p "$probe/cases"
for name in sound untracked stray; do
  mkdir -p "$probe/cases/$name/gh"
  echo '{}' > "$probe/cases/$name/gh/repo.o.app.json"
  printf '%s\n' repo.o.app 'events.PR_1.' > "$probe/cases/$name/expected.calls"
done
echo '{}' > "$probe/cases/sound/gh/events.PR_1..json"
echo '{}' > "$probe/cases/stray/gh/comments.PR_9.json"
cp -r "$probe/cases" "$work/plain-cases"
git init -q "$probe"
git -C "$probe" add cases/sound cases/stray
want_tree="case untracked has no file in gh/ that git tracks, so a fresh clone has no gh/ folder: git add its gh/ files, or add gh/.gitkeep when it has none"
want_stray="case stray: gh/comments.PR_9.json answers no call in expected.calls"
got="$(case_problems "$probe/cases")"
[[ "$got" == "$want_stray"$'\n'"$want_tree" || "$got" == "$want_tree"$'\n'"$want_stray" ]] \
  || fail "the committed-cases check, on an untracked gh/ and a stray fixture, said: $got"
got="$(GIT_DIR="$work/nowhere" GIT_WORK_TREE="$work" case_problems "$probe/cases")"
[[ "$got" == *"$want_tree"* ]] || fail "the committed-cases check follows the caller's GIT_DIR: $got"
got="$(case_problems "$work/plain-cases")"
[[ "$got" == "$want_stray" ]] || fail "the committed-cases check outside a git work tree said: $got"

got="$(case_problems "$here/status-parity/cases")"
[[ -z "$got" ]] || fail "the committed cases:"$'\n'"$got"

if [[ $failures -ne 0 ]]; then
  echo "status-parity-runner: $failures check(s) failed." >&2
  exit 1
fi
echo "status-parity-runner: every check passed."
