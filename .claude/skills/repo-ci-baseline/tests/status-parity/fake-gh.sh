#!/usr/bin/env bash
# A fake `gh` for the status parity suite (tests/status-parity.test.sh): it answers the `gh api` calls github/status.py
# makes from the fixture files of one case, so any implementation of status can be run against the same GitHub.
#
#   FAKE_GH_DIR   the case's gh/ folder (required)
#   FAKE_GH_LOG   a file each call's fixture key is appended to, one per line (required)
#
# A call is told apart by its GraphQL query (the -f query= argument) or its REST path, and answered from
# $FAKE_GH_DIR/<key>.json (printed, exit 0) or $FAKE_GH_DIR/<key>.fail (printed on stderr, exit 1):
#
#   repo.<owner>.<name>           the repo query (pullRequests(states: OPEN ...), -F owner= -F name=
#   comments.<id>[.<cursor>]      a PR's comments (comments(last: 100 ...), -f id= [-f cursor=]
#   threads.<id>                  a PR's review threads with their comments (isResolved path line ...), -f id=
#   events.<id>[.<cursor>]        a PR's label and review-request events (timelineItems(...), -f id= [-f cursor=]
#   rest.<path, / as _>           a REST call: gh api <path>
#
# A call no fixture answers is logged as "UNMATCHED <argv>" and exits 97, which fails the case: a fixture set is
# complete, so a call nobody planned for is a difference between implementations, not a GitHub failure.
set -euo pipefail

: "${FAKE_GH_DIR:?fake gh: FAKE_GH_DIR is not set}"
: "${FAKE_GH_LOG:?fake gh: FAKE_GH_LOG is not set}"

unmatched() {
  printf 'UNMATCHED %s\n' "$*" >> "$FAKE_GH_LOG"
  echo "fake gh: no fixture answers: gh $*" >&2
  exit 97
}

[[ "${1:-}" == api ]] || unmatched "$@"
argv=("$@")
shift

if [[ "${1:-}" != graphql ]]; then
  [[ $# -eq 1 ]] || unmatched "${argv[@]}"
  key="rest.${1//\//_}"
else
  shift
  query="" owner="" name="" id="" cursor=""
  while [[ $# -gt 0 ]]; do
    [[ $# -ge 2 ]] || unmatched "${argv[@]}"
    case "$1:$2" in
      -f:query=*) query="${2#query=}" ;;
      -F:owner=*) owner="${2#owner=}" ;;
      -F:name=*) name="${2#name=}" ;;
      -f:id=*) id="${2#id=}" ;;
      -f:cursor=*) cursor="${2#cursor=}" ;;
      *) unmatched "${argv[@]}" ;;
    esac
    shift 2
  done
  case "$query" in
    *"pullRequests(states: OPEN"*) key="repo.$owner.$name" ;;
    *"timelineItems("*) key="events.$id${cursor:+.$cursor}" ;;
    *"comments(last: 100"*) key="comments.$id${cursor:+.$cursor}" ;;
    *"isResolved path line"*) key="threads.$id" ;;
    *) unmatched "${argv[@]}" ;;
  esac
fi

if [[ -f "$FAKE_GH_DIR/$key.json" ]]; then
  printf '%s\n' "$key" >> "$FAKE_GH_LOG"
  cat "$FAKE_GH_DIR/$key.json"
elif [[ -f "$FAKE_GH_DIR/$key.fail" ]]; then
  printf '%s\n' "$key" >> "$FAKE_GH_LOG"
  cat "$FAKE_GH_DIR/$key.fail" >&2
  exit 1
else
  unmatched "${argv[@]}"
fi
