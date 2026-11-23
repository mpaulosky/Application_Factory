#!/usr/bin/env bash
# A fake `gh` for the status parity suite (tests/status-parity.test.sh): it answers the `gh api` calls github/status.py
# makes from the fixture files of one case, so any implementation of status can be run against the same GitHub.
#
#   FAKE_GH_DIR   the case's gh/ folder (required)
#   FAKE_GH_LOG   a file each call's fixture key is appended to, one per line (required)
#
# What an implementation must send, so the fake can tell its calls apart:
#
#   gh api graphql -f query=<query> [-f|-F <variable>=<value>]...   variables: owner, name, id, cursor
#   gh api <path>                                                   a REST GET
#
# A GraphQL call is told apart by its query with all whitespace removed, so spacing and line breaks don't matter:
#
#   repo.<owner>.<name>           a query holding pullRequests( ... states:OPEN (the repo query)
#   events.<id>[.<cursor>]        a query holding timelineItems( (a PR's label and review-request events)
#   comments.<id>[.<cursor>]      a query holding comments(last: (a PR's comments, paged backwards)
#   threads.<id>                  a query holding reviewThreads( and comments(first: (a PR's threads with their comments)
#   rest.<path>                   gh api <path>
#
# A cursor given but empty is part of the key too (comments.<id>.), so it never passes for a first page. Every key part has
# each / replaced by _, so a value can't name a file outside FAKE_GH_DIR. The call is answered from FAKE_GH_DIR/<key>.json
# (printed on stdout, exit 0) or FAKE_GH_DIR/<key>.fail (printed on stderr, exit 1: a GitHub failure), and the key is logged.
#
# Exit 97 is the fake's own: a call no fixture answers, an argument it doesn't know or a fixture it can't read (each
# logged as one line, "UNMATCHED <what was asked>"), or FAKE_GH_DIR or FAKE_GH_LOG unset. A fixture set is complete, so a call nobody
# planned for is a difference between implementations, not a GitHub failure.
set -euo pipefail

if [[ -z "${FAKE_GH_DIR:-}" || -z "${FAKE_GH_LOG:-}" ]]; then
  echo "fake gh: FAKE_GH_DIR and FAKE_GH_LOG must both be set." >&2
  exit 97
fi

# One line naming what was asked: the key when there is one, else the arguments with line breaks collapsed and cut short.
unmatched() {
  local what="$1"
  what="${what//$'\n'/ }"
  what="${what//$'\r'/ }"
  ((${#what} <= 200)) || what="${what:0:200}..."
  printf 'UNMATCHED %s\n' "$what" >> "$FAKE_GH_LOG"
  echo "fake gh: no fixture answers: $what" >&2
  exit 97
}

part() { printf '%s' "${1//\//_}"; }

all="$*"
[[ "${1:-}" == api ]] || unmatched "gh $all"
shift

if [[ "${1:-}" != graphql ]]; then
  [[ $# -eq 1 && -n "$1" && "$1" != -* ]] || unmatched "gh api $*"
  key="rest.$(part "$1")"
else
  shift
  query="" owner="" name="" id="" cursor="" has_cursor=""
  while [[ $# -gt 0 ]]; do
    [[ $# -ge 2 && ("$1" == -f || "$1" == -F) ]] || unmatched "gh $all"
    case "$2" in
      query=*) query="${2#query=}" ;;
      owner=*) owner="${2#owner=}" ;;
      name=*) name="${2#name=}" ;;
      id=*) id="${2#id=}" ;;
      cursor=*) cursor="${2#cursor=}" has_cursor=1 ;;
      *) unmatched "gh $all" ;;
    esac
    shift 2
  done
  q="${query//[[:space:]]/}"
  page="${has_cursor:+.$(part "$cursor")}"
  case "$q" in
    *"pullRequests("*"states:OPEN"*) key="repo.$(part "$owner").$(part "$name")" ;;
    *"timelineItems("*) key="events.$(part "$id")$page" ;;
    *"comments(last:"*) key="comments.$(part "$id")$page" ;;
    *"reviewThreads("*"comments(first:"*) key="threads.$(part "$id")" ;;
    *) unmatched "gh api graphql with a query the fake doesn't know: ${q:0:80}" ;;
  esac
fi

for answer in json fail; do
  file="$FAKE_GH_DIR/$key.$answer"
  [[ -e "$file" ]] || continue
  [[ -f "$file" && -r "$file" ]] || unmatched "$key (its fixture $key.$answer is not a readable file)"
  printf '%s\n' "$key" >> "$FAKE_GH_LOG"
  if [[ "$answer" == json ]]; then
    cat -- "$file"
    exit 0
  fi
  cat -- "$file" >&2
  exit 1
done
unmatched "$key"
