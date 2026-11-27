# shellcheck shell=bash
# The step timings scripts/gate.sh and the repo-ci-baseline skill's test.sh keep (mpaulosky/Application_Factory#220): sourced by both, it appends one line per
# step to a tab-separated log, which scripts/gate-timings.sh summarizes and the gate-timings skill reports on. A line is
#
#   run  started  branch  commit  script  step  seconds  status
#
# run names the run: test.sh started by the gate takes the gate's (GATE_TIMINGS_RUN), so the gate's run holds the steps of both. started is the step's start as
# an ISO 8601 UTC time, seconds its wall time to a tenth, and status passed, failed or skipped (a cached pass covered it). The log is the primary checkout's
# docs/timings/gate-timings.tsv, which .gitignore leaves out, so every linked worktree's runs land in one log; GATE_TIMINGS_LOG names another file, and
# GATE_TIMINGS=off keeps none. Timing never changes a step's result or output: a log that can't be written is skipped without a word, and nothing here exits.
#
#   timings_start <script>   once, before the first step
#   timings_step <name>      the step <name> starts; the one open before it ends as passed, or as timings_mark set it
#   timings_mark <status>    the open step ends as <status> (failed or skipped) when it ends
#   timings_end              the open step ends now, as passed or as marked
#   timings_finish <code>    from the EXIT trap: the open step ends, as failed when <code> isn't 0 and it wasn't marked
#   timings_slowest <n>      prints this run's <n> slowest steps, of every script that logged to it

timings_on=0
timings_log=""
timings_script=""
timings_branch=""
timings_commit=""
timings_open=""
timings_open_at=""
timings_open_started=""
timings_open_mark=""

# timings_now: the time in microseconds. EPOCHREALTIME's separator follows the locale, so every non-digit is dropped; a bash older than 5 has only whole seconds.
timings_now() {
  local now="${EPOCHREALTIME:-}"
  if [[ -n "$now" ]]; then
    printf '%s\n' "${now//[!0-9]/}"
  else
    printf '%s000000\n' "$(date +%s)"
  fi
}

# timings_clean <text>: the text with each control character (a tab, a line break, an escape) as a space, so it can't break a log line into two or
# shift its columns.
timings_clean() {
  local text="$1"
  printf '%s' "${text//[[:cntrl:]]/ }"
}

timings_start() {
  timings_on=0
  [[ "${GATE_TIMINGS:-}" != off ]] || return 0
  local common
  if [[ -n "${GATE_TIMINGS_LOG:-}" ]]; then
    timings_log="$GATE_TIMINGS_LOG"
  else
    # The common git folder is the primary checkout's .git, whichever worktree runs; its parent is that checkout.
    common="$(git rev-parse --path-format=absolute --git-common-dir 2> /dev/null)" || return 0
    timings_log="$(dirname -- "$common")/docs/timings/gate-timings.tsv"
  fi
  mkdir -p -- "$(dirname -- "$timings_log")" 2> /dev/null || return 0
  timings_script="$(timings_clean "$1")"
  timings_branch="$(timings_clean "$(git symbolic-ref -q --short HEAD 2> /dev/null || echo detached)")"
  timings_commit="$(git rev-parse -q --short=8 --verify HEAD 2> /dev/null || echo none)"
  if [[ -z "${GATE_TIMINGS_RUN:-}" ]]; then
    GATE_TIMINGS_RUN="$(date -u +%Y%m%dT%H%M%SZ).$$"
    export GATE_TIMINGS_RUN
  fi
  timings_on=1
}

timings_end() {
  ((timings_on)) && [[ -n "$timings_open" ]] || return 0
  local status="${timings_open_mark:-${1:-passed}}" micros tenths
  micros=$(($(timings_now) - timings_open_at))
  ((micros >= 0)) || micros=0
  tenths=$(((micros + 50000) / 100000))
  { printf '%s\t%s\t%s\t%s\t%s\t%s\t%d.%d\t%s\n' "$GATE_TIMINGS_RUN" "$timings_open_started" "$timings_branch" "$timings_commit" \
    "$timings_script" "$timings_open" $((tenths / 10)) $((tenths % 10)) "$status" >> "$timings_log"; } 2> /dev/null || true
  timings_open=""
  timings_open_mark=""
}

timings_step() {
  ((timings_on)) || return 0
  timings_end
  timings_open="$(timings_clean "$1")"
  timings_open_at="$(timings_now)"
  timings_open_started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  timings_open_mark=""
}

timings_mark() {
  ((timings_on)) && [[ -n "$timings_open" ]] || return 0
  timings_open_mark="$1"
}

timings_finish() {
  ((timings_on)) || return 0
  if [[ "${1:-0}" != 0 ]]; then
    timings_end failed
  else
    timings_end
  fi
}

timings_slowest() {
  ((timings_on)) && [[ -f "$timings_log" ]] || return 0
  local lines
  lines="$(awk -F '\t' -v run="$GATE_TIMINGS_RUN" '$1 == run && $8 != "skipped" { printf "%8.1fs  %s: %s\n", $7, $5, $6 }' "$timings_log" 2> /dev/null \
    | LC_ALL=C sort -rn | head -n "$1")" || return 0
  [[ -n "$lines" ]] || return 0
  printf '\nSlowest steps of this run (all of them: %s):\n%s\n' "$timings_log" "$lines"
}
