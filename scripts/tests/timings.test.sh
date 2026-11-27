#!/usr/bin/env bash
# Tests the step timings (mpaulosky/Application_Factory#220): scripts/lib/timings.sh writes one line per step, with its run, start, branch, commit, script, step,
# seconds and status (passed, failed when the run failed or it was marked so, skipped when marked so), cleans a name's control characters, joins the run
# GATE_TIMINGS_RUN names, writes nothing under GATE_TIMINGS=off, writes to the primary checkout's docs/timings/ from a linked worktree, and leaves a run's
# result and output alone when its log can't be written; test.sh's step and end hooks log a step in which a check failed as failed and the others as passed;
# and scripts/gate-timings.sh runs its C# summary from any folder, with the caller's paths and exit status. scripts/tests/gate.test.sh checks the gate's
# own use of the library.
#
#   scripts/tests/timings.test.sh
#
# The bodies the tests run are code for another bash, written in single quotes on purpose.
# shellcheck disable=SC2016
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
lib="$here/scripts/lib/timings.sh"
work="$(mktemp -d)"
trap 'chmod -R u+w "$work" 2> /dev/null || true; rm -rf "$work"' EXIT
unset GATE_TIMINGS GATE_TIMINGS_LOG GATE_TIMINGS_RUN

pass=0
failed=0
check() { # check <description> <condition: 0 for ok>
  if [[ "$2" == 0 ]]; then
    echo "ok   - $1"
    pass=$((pass + 1))
  else
    echo "FAIL - $1"
    failed=$((failed + 1))
  fi
}
ok() { if "$@"; then echo 0; else echo 1; fi; }

tgit() { git -c user.name=t -c user.email=t@example.com -c commit.gpgsign=false -c core.hooksPath=/dev/null "$@"; }
repo="$work/repo"
mkdir -p "$repo"
tgit -C "$repo" init -q -b main
tgit -C "$repo" commit -q --allow-empty -m base
head="$(git -C "$repo" rev-parse --short=8 HEAD)"

# timed <body>: runs the body in a bash in the repo with the library sourced, and its exit status in $status.
timed() {
  status=0
  (cd "$repo" && bash -c "set -euo pipefail; source '$lib'; $1") > "$work/out" 2> "$work/err" || status=$?
}

log="$work/log.tsv"
GATE_TIMINGS_LOG="$log" timed 'timings_start gate.sh; timings_step one; sleep 0.2; timings_step two; timings_mark skipped; timings_step three; timings_end'
mapfile -t lines < "$log"
IFS=$'\t' read -r -a first <<< "${lines[0]}"
check "a step's line has eight tab-separated fields" "$(ok test "${#first[@]}" -eq 8)"
check "its run, branch, commit and script are the run's" "$(ok test "${first[0]}" != "" -a "${first[2]}" == main -a "${first[3]}" == "$head" -a "${first[4]}" == gate.sh)"
check "its start is an ISO 8601 UTC time" "$(ok grep -qE '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$' <<< "${first[1]}")"
check "its seconds are the step's wall time, to a tenth" "$(ok grep -qE '^0\.[2-9]$' <<< "${first[6]}")"
check "steps are logged in order, each as passed unless marked" "$(ok test "$(cut -f6,8 "$log" | tr '\t\n' ':,')" == "one:passed,two:skipped,three:passed,")"
check "every step of a run has the same run" "$(ok test "$(cut -f1 "$log" | sort -u | wc -l)" -eq 1)"
check "timing prints nothing" "$(ok test ! -s "$work/out" -a ! -s "$work/err")"

rm -f "$log"
GATE_TIMINGS_LOG="$log" timed 'timings_start gate.sh; trap '\''timings_finish $?'\'' EXIT; timings_step "lint"; exit 3'
check "a run that fails keeps its exit status" "$(ok test "$status" == 3)"
check "and its open step is logged as failed" "$(ok test "$(cut -f6,8 "$log")" == $'lint\tfailed')"
rm -f "$log"
GATE_TIMINGS_LOG="$log" timed 'timings_start gate.sh; trap '\''timings_finish $?'\'' EXIT; timings_step "test.sh"; timings_mark skipped; exit 1'
check "a step marked skipped stays skipped when the run fails after it" "$(ok test "$(cut -f8 "$log")" == skipped)"
rm -f "$log"
GATE_TIMINGS_LOG="$log" timed 'timings_start gate.sh; trap '\''timings_finish $?'\'' EXIT; timings_step "last"'
check "a run that passes logs its open step as passed" "$(ok test "$(cut -f6,8 "$log")" == $'last\tpassed')"

rm -f "$log"
GATE_TIMINGS_LOG="$log" timed "timings_start \$'test\\tsh'; timings_step \$'a\\tb\\nc\\e[0m'; timings_end"
check "a name's tab, line break and escape become spaces, so the line keeps its eight fields" \
  "$(ok test "$(wc -l < "$log")" -eq 1 -a "$(awk -F '\t' '{ print NF }' "$log")" == 8 -a "$(cut -f5 "$log")" == "test sh")"

rm -f "$log"
GATE_TIMINGS_LOG="$log" GATE_TIMINGS_RUN=the-gates-run timed 'timings_start test.sh; timings_step one; timings_end'
check "a run started by another joins the run GATE_TIMINGS_RUN names" "$(ok test "$(cut -f1 "$log")" == the-gates-run)"
GATE_TIMINGS_LOG="$log" timed 'timings_start gate.sh; echo "run=$GATE_TIMINGS_RUN"; bash -c '\''echo "child=$GATE_TIMINGS_RUN"'\'''
check "a run's id is exported, so the scripts it starts join it" \
  "$(ok test "$(sed -n 's/^run=//p' "$work/out")" == "$(sed -n 's/^child=//p' "$work/out")" -a -n "$(sed -n 's/^run=//p' "$work/out")")"

rm -f "$log"
GATE_TIMINGS=off GATE_TIMINGS_LOG="$log" timed 'timings_start gate.sh; timings_step one; timings_mark skipped; timings_end; timings_finish 1; timings_slowest 5'
check "GATE_TIMINGS=off writes no log and prints nothing" "$(ok test ! -e "$log" -a ! -s "$work/out" -a ! -s "$work/err" -a "$status" == 0)"

: > "$work/a-file"
GATE_TIMINGS_LOG="$work/a-file/log.tsv" timed 'timings_start gate.sh; trap '\''timings_finish $?; timings_slowest 5'\'' EXIT; timings_step one; timings_step two; echo done'
check "a log that can't be written leaves the run passing, with its own output only" \
  "$(ok test "$status" == 0 -a "$(cat "$work/out")" == "done" -a ! -s "$work/err")"
mkdir -p "$work/ro"
: > "$work/ro/log.tsv"
chmod a-w "$work/ro/log.tsv"
GATE_TIMINGS_LOG="$work/ro/log.tsv" timed 'timings_start gate.sh; trap '\''timings_finish $?'\'' EXIT; timings_step one; exit 4'
check "a read-only log leaves a failing run's status as it was, and says nothing" "$(ok test "$status" == 4 -a ! -s "$work/err" -a ! -s "$work/ro/log.tsv")"
chmod u+w "$work/ro/log.tsv"

tgit -C "$repo" worktree add -q --detach "$work/linked"
(cd "$work/linked" && bash -c "source '$lib'; timings_start gate.sh; timings_step one; timings_end")
check "a linked worktree logs to the primary checkout's docs/timings/" \
  "$(ok test -s "$repo/docs/timings/gate-timings.tsv" -a ! -e "$work/linked/docs/timings")"
check "under its own branch name: a detached HEAD is detached" "$(ok test "$(cut -f3 "$repo/docs/timings/gate-timings.tsv")" == detached)"

rm -f "$log"
GATE_TIMINGS_LOG="$log" timed 'timings_start gate.sh; trap '\''timings_finish $?; timings_slowest 2'\'' EXIT; timings_step fast; timings_step slow; sleep 0.3; timings_step skip; timings_mark skipped'
check "the end of a run prints its slowest steps, slowest first, without skipped ones" \
  "$(ok test "$(grep -cE '^ +[0-9.]+s  gate.sh: ' "$work/out")" == 2 -a "$(grep -E '^ +[0-9.]+s  gate.sh: ' "$work/out" | head -n 1 | sed 's/.*: //')" == slow)"
check "and names the log" "$(ok grep -qF "$log" "$work/out")"

# test.sh's hooks: its step function, from test.sh itself, and its end of run, with the library.
testsh="$here/.claude/skills/repo-ci-baseline/test.sh"
{
  echo "set -euo pipefail"
  echo "source '$lib'"
  echo "failures=0"
  echo "step_failures=0"
  echo 'fail() { failures=$((failures + 1)); }'
  sed -n '/^step() {$/,/^}$/p' "$testsh"
  echo 'timings_start test.sh'
  echo 'step "clean"'
  echo 'step "broken"; fail'
  echo 'step "last"'
  sed -n '/^((failures == step_failures)) || timings_mark failed$/,/^timings_end$/p' "$testsh"
} > "$work/testsh-hooks.sh"
rm -f "$log"
(cd "$repo" && GATE_TIMINGS_LOG="$log" bash "$work/testsh-hooks.sh" > /dev/null)
check "test.sh logs a step in which a check failed as failed, and the others as passed" \
  "$(ok test "$(cut -f6,8 "$log" | tr '\t\n' ':,')" == "clean:passed,broken:failed,last:passed,")"
{
  echo "set -euo pipefail"
  echo "source '$lib'"
  echo "failures=0"
  echo "step_failures=0"
  echo 'fail() { failures=$((failures + 1)); }'
  sed -n '/^step() {$/,/^}$/p' "$testsh"
  echo 'timings_start test.sh'
  echo 'step "only"; fail'
  sed -n '/^((failures == step_failures)) || timings_mark failed$/,/^timings_end$/p' "$testsh"
} > "$work/testsh-last.sh"
rm -f "$log"
(cd "$repo" && GATE_TIMINGS_LOG="$log" bash "$work/testsh-last.sh" > /dev/null)
check "and the last step too, when a check in it failed" "$(ok test "$(cut -f6,8 "$log")" == $'only\tfailed')"
check "test.sh starts its timing as test.sh, with its shard" "$(ok grep -qF 'timings_start "test.sh${shard:+ ($shard shard)}"' "$testsh")"

# The summary, end to end: scripts/gate-timings.sh runs scripts/gate-timings.cs (its rules are tests/Scripts.Tests/GateTimings's, on GateTimings.cs).
summary="$here/scripts/gate-timings.sh"
printf '%s\n' \
  $'r1\t2026-01-01T00:00:00Z\tb\tc\tgate.sh\tlint\t10.0\tpassed' \
  $'r2\t2026-01-01T00:00:00Z\tb\tc\tgate.sh\tlint\t20.0\tpassed' \
  $'r2\t2026-01-01T00:00:00Z\tb\tc\tgate.sh\ttest.sh\t1.0\tskipped' > "$work/fixture.tsv"
status=0
out="$(cd "$work" && "$summary" --log fixture.tsv)" || status=$?
check "the summary prints the table, reading a relative --log from the caller's folder" \
  "$(ok test "$status" == 0 && grep -qxF '| gate.sh | lint | 2 | 15.0 | 20.0 | 20.0 | +100% |' <<< "$out")"
cp "$work/fixture.tsv" "$repo/docs/timings/gate-timings.tsv"
status=0
out="$(cd "$work/linked" && "$summary")" || status=$?
check "its default log is the caller's primary checkout's, from a linked worktree" \
  "$(ok test "$status" == 0 && grep -qF "from $repo/docs/timings/gate-timings.tsv:" <<< "$out")"
status=0
"$summary" --runs 0 > /dev/null 2>&1 || status=$?
check "a bad argument's exit 2 comes through" "$(ok test "$status" == 2)"
status=0
"$summary" --log "$work/none.tsv" > /dev/null 2> "$work/err" || status=$?
check "a missing log's exit 1 comes through, saying a gate run writes it" "$(ok test "$status" == 1 && grep -q 'a run of scripts/gate.sh writes it' "$work/err")"

echo
echo "timings.test.sh: $pass passed, $failed failed"
[[ $failed == 0 ]]
