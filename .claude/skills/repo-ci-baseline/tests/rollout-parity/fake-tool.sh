#!/usr/bin/env bash
# A fake for every command but gh that the rollout parity suite (tests/rollout-parity.test.sh) puts in a run's way: the stub
# reapply.sh and land.sh in its temporary skill folder (rollout calls them by absolute path, not through PATH), the tools on
# PATH (git, claude, and the rest --auto's preflight looks for), and the scripts/review.sh a case's fixture writes into a
# re-Apply worktree. tests/parity/fake-gh.sh fakes gh. The runner links this file under each stub's name, and the name it
# was called by is the tool; a tool on PATH is a wrapper script that runs it with FAKE_TOOL_NAME set instead, so its real
# path (which --auto asks of git and node) is its own and not this file's.
#
#   FAKE_TOOL_NAME the tool's name, when it isn't the name this file was called by (unset here before a fixture runs, so a
#                  tool the fixture calls gets its own)
#   FAKE_TOOL_DIR  the case's fakes/ folder (required)
#   FAKE_GH_LOG    the run's call log (required), which the fake gh writes too, so the log holds every call in order
#   FAKE_HOME      the case's HOME (required), written ~ in the log, so a path under it reads the same in every run
#
# Each call is logged as one line: the tool's name, then each argument as fake-gh.sh writes one (as it is when it holds only
# letters, digits and _ . / : @ , + = # - ~, else in double quotes with \ " and line breaks, tabs and carriage returns
# written \\ \" \n \t \r), with every $FAKE_HOME in it written ~ first.
#
# The nth call of a tool runs FAKE_TOOL_DIR/<tool>.<n>.sh when there is one, else <tool>.sh, where <tool> is its name without
# a .sh (reapply.sh's are reapply.<n>.sh and reapply.sh), with bash, in this process (so a signal sent to the caller's child
# reaches it), with the call's arguments, folder, environment, stdin, stdout and stderr; its exit code is the call's. A call no fixture answers, or one whose fixture isn't a readable file, is logged as
# "UNMATCHED <the line it would have logged>" and exits 97, as the fake gh does: a fixture set is complete, so a call
# nobody planned for is a difference between implementations. Exit 97 too when a variable above is unset.
set -euo pipefail

if [[ -z "${FAKE_TOOL_DIR:-}" || -z "${FAKE_GH_LOG:-}" || -z "${FAKE_HOME:-}" ]]; then
  echo "fake tool: FAKE_TOOL_DIR, FAKE_GH_LOG and FAKE_HOME must all be set." >&2
  exit 97
fi
tool="${FAKE_TOOL_NAME:-$(basename -- "$0")}"
unset FAKE_TOOL_NAME
base="${tool%.sh}"

plain='^[A-Za-z0-9_./:@,+=#~-]+$'
encode() {
  local arg="${1//"$FAKE_HOME"/\~}"
  if [[ "$arg" =~ $plain ]]; then
    printf '%s' "$arg"
    return
  fi
  arg="${arg//\\/\\\\}"
  arg="${arg//\"/\\\"}"
  arg="${arg//$'\n'/\\n}"
  arg="${arg//$'\t'/\\t}"
  arg="${arg//$'\r'/\\r}"
  printf '"%s"' "$arg"
}

entry="$tool"
for arg in "$@"; do
  entry+=" $(encode "$arg")"
done

# This is the nth call of the tool: one more than the calls of it logged already.
n=1
if [[ -f "$FAKE_GH_LOG" ]]; then
  while IFS= read -r logged || [[ -n "$logged" ]]; do
    [[ "${logged%% *}" != "$tool" ]] || n=$((n + 1))
  done < "$FAKE_GH_LOG"
fi

for fixture in "$base.$n.sh" "$base.sh"; do
  file="$FAKE_TOOL_DIR/$fixture"
  [[ -e "$file" ]] || continue
  if [[ ! -f "$file" || ! -r "$file" ]]; then
    printf 'UNMATCHED %s (its fixture %s is not a readable file)\n' "$entry" "$fixture" >> "$FAKE_GH_LOG"
    echo "fake $tool: its fixture $fixture is not a readable file" >&2
    exit 97
  fi
  printf '%s\n' "$entry" >> "$FAKE_GH_LOG"
  exec bash "$file" "$@"
done
printf 'UNMATCHED %s\n' "$entry" >> "$FAKE_GH_LOG"
echo "fake $tool: no fixture answers: $entry" >&2
exit 97
