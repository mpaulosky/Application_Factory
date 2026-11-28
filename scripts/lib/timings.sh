# shellcheck shell=bash
# The step timings scripts/gate.sh and the repo-ci-baseline skill's test.sh keep (mpaulosky/Application_Factory#220): sourced by both, it appends one line per
# step to a tab-separated log, which scripts/gate-timings.sh summarizes and the gate-timings skill reports on. A line is
#
#   run  started  branch  commit  script  step  seconds  status
#
# run names the run: test.sh started by the gate takes the gate's (GATE_TIMINGS_RUN), so the gate's run holds the steps of both. started is the step's start as
# an ISO 8601 UTC time, seconds its wall time to a tenth (whole seconds on a bash without EPOCHREALTIME, older than 5), and status passed, failed or skipped
# (a cached pass covered it, or there was nothing for it to check). Names are logged as printable ASCII, every other byte a space, so a line keeps its
# eight fields and holds no escape a terminal or a reader would act on.
#
# The log is docs/timings/gate-timings.tsv in the checkout this file is in, found from this file's own folder and not the caller's (test.sh run through
# the ~/.claude/skills symlink from another repository logs to the Factory, never into that repository): the parent of the git folder every worktree of
# the clone shares, so every linked worktree's runs land in one log, which .gitignore leaves out. A git folder not named .git (a bare clone, a separate
# git folder, a submodule) has no checkout around it, and nothing is logged. GATE_TIMINGS_LOG names another file, by an absolute path (a relative one
# logs nothing, as the gate and the summary would read it from different folders), and GATE_TIMINGS=off, that value only, keeps none. A log, its folder
# or the default's docs/ that is a symlink, or a log that isn't a regular file, is never written through. Timing never changes a step's result or output:
# a log that can't be written is skipped without a word, and nothing here exits.
#
#   timings_start <script>   once, before the first step
#   timings_step <name>      the step <name> starts; the one open before it ends as passed, or as timings_mark set it
#   timings_mark <status>    the open step ends as <status> (failed or skipped) when it ends
#   timings_end              the open step ends now, as passed or as marked
#   timings_finish <code>    from the EXIT trap: the open step ends, as failed when <code> isn't 0 and it wasn't marked
#   timings_slowest <n>      prints this run's <n> slowest steps, of every script that logged to it

timings_on=0
# The folder this file is in, physical, for the git calls that find the log, the branch and the commit.
timings_dir="$(CDPATH='' cd -P -- "$(dirname -- "${BASH_SOURCE[0]}")" 2> /dev/null && pwd -P)" || timings_dir=""
timings_log=""
timings_docs=""
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

# timings_clean <text>: the text with every byte that isn't printable ASCII (a tab, a line break, an escape, a C1 or bidi character's bytes) as a space,
# so it can't break a log line into two, shift its columns or reach a terminal as a control. LC_ALL=C makes the range bytewise.
timings_clean() {
  local LC_ALL=C text="$1"
  printf '%s' "${text//[^ -~]/ }"
}

# timings_writable: whether the log may be written: no symlink at the log or its folder (or docs/, for the default log), and the log a regular file or
# not there yet.
timings_writable() {
  local folder
  folder="$(dirname -- "$timings_log")"
  [[ ! -L "$timings_log" && ! -L "$folder" ]] || return 1
  [[ -z "$timings_docs" || ! -L "$timings_docs" ]] || return 1
  [[ ! -e "$timings_log" || -f "$timings_log" ]]
}

timings_start() {
  timings_on=0
  [[ "${GATE_TIMINGS:-}" != off ]] || return 0
  [[ -n "$timings_dir" ]] || return 0
  local common
  timings_docs=""
  if [[ -n "${GATE_TIMINGS_LOG:-}" ]]; then
    [[ "$GATE_TIMINGS_LOG" == /* ]] || return 0
    timings_log="$GATE_TIMINGS_LOG"
  else
    # The common git folder is the primary checkout's .git, whichever worktree runs; its parent is that checkout.
    common="$(git -C "$timings_dir" rev-parse --path-format=absolute --git-common-dir 2> /dev/null)" || return 0
    common="${common%/}"
    [[ "$(basename -- "$common")" == .git ]] || return 0
    timings_docs="$(dirname -- "$common")/docs"
    timings_log="$timings_docs/timings/gate-timings.tsv"
    [[ ! -L "$timings_docs" && ! -L "$timings_docs/timings" ]] || return 0
  fi
  mkdir -p -- "$(dirname -- "$timings_log")" 2> /dev/null || return 0
  timings_writable || return 0
  timings_script="$(timings_clean "$1")"
  timings_branch="$(timings_clean "$(git -C "$timings_dir" symbolic-ref -q --short HEAD 2> /dev/null || echo detached)")"
  timings_commit="$(timings_clean "$(git -C "$timings_dir" rev-parse -q --short=8 --verify HEAD 2> /dev/null || echo none)")"
  if [[ -z "${GATE_TIMINGS_RUN:-}" ]]; then
    GATE_TIMINGS_RUN="$(date -u +%Y%m%dT%H%M%SZ).$$"
  else
    # An inherited run id is logged too, so it is cleaned like a name.
    GATE_TIMINGS_RUN="$(timings_clean "$GATE_TIMINGS_RUN")"
  fi
  export GATE_TIMINGS_RUN
  timings_on=1
}

timings_end() {
  ((timings_on)) && [[ -n "$timings_open" ]] || return 0
  local status="${timings_open_mark:-${1:-passed}}" step="$timings_open" micros tenths
  micros=$(($(timings_now) - timings_open_at))
  ((micros >= 0)) || micros=0
  tenths=$(((micros + 50000) / 100000))
  timings_open=""
  timings_open_mark=""
  timings_writable || return 0
  { printf '%s\t%s\t%s\t%s\t%s\t%s\t%d.%d\t%s\n' "$GATE_TIMINGS_RUN" "$timings_open_started" "$timings_branch" "$timings_commit" \
    "$timings_script" "$step" $((tenths / 10)) $((tenths % 10)) "$status" >> "$timings_log"; } 2> /dev/null || true
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

# The names it prints are cleaned again (printable ASCII only), as another writer's line can carry this run's id. awk keeps the first <n> lines and still
# reads the rest, so sort never meets a closed pipe (head would close it, and pipefail would drop the lot).
timings_slowest() {
  ((timings_on)) && [[ -f "$timings_log" ]] || return 0
  local lines
  lines="$(LC_ALL=C awk -F '\t' -v run="$GATE_TIMINGS_RUN" '$1 == run && $8 != "skipped" {
      script = $5; step = $6; gsub(/[^ -~]/, " ", script); gsub(/[^ -~]/, " ", step); printf "%8.1fs  %s: %s\n", $7, script, step }' "$timings_log" 2> /dev/null \
    | LC_ALL=C sort -rn | LC_ALL=C awk -v n="$1" 'NR <= n')" || return 0
  [[ -n "$lines" ]] || return 0
  printf '\nSlowest steps of this run (all of them: %s):\n%s\n' "$(timings_clean "$timings_log")" "$lines"
}
