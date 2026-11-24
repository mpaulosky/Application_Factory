#!/usr/bin/env bash
# A fake `gh` for the parity suites (tests/parity.test.sh): it answers the `gh` calls a script makes from the fixture
# files of one case, so any implementation of the script can be run against the same GitHub.
#
#   FAKE_GH_DIR   the case's gh/ folder (required)
#   FAKE_GH_LOG   a file each call is appended to, one line per call (required)
#
# What an implementation must send, so the fake can tell its calls apart:
#
#   gh api graphql -f query=<query> [-f|-F <variable>=<value>]...   variables: owner, name, id, cursor
#   gh api <path> [--paginate] [--jq <expression>]                  a REST GET
#   gh pr ready|update-branch <n> -R <owner/repo>
#   gh pr comment <n> -R <owner/repo> --body <text>
#   gh pr edit <n> -R <owner/repo> --add-reviewer|--add-label <value>
#   gh pr merge <n> -R <owner/repo> [--auto] [--squash] [--match-head-commit <sha>]...
#   gh pr view <n> -R <owner/repo> --json <fields> --jq <expression>
#
# A GraphQL call is told apart by its query with all whitespace removed, so spacing and line breaks don't matter:
#
#   repo.<owner>.<name>           a query holding pullRequests( ... states:OPEN (the repo query)
#   events.<id>[.<cursor>]        a query holding timelineItems( (a PR's label and review-request events)
#   comments.<id>[.<cursor>]      a query holding comments(last: (a PR's comments, paged backwards)
#   threads.<id>                  a query holding reviewThreads( and comments(first: (a PR's threads with their comments)
#   rest.<path>                   gh api <path>, with or without --paginate and --jq (the path's query string is in it)
#   pr.<sub>.<owner>.<name>.<n>   gh pr <sub> (a view's key ends .<fields> too: pr.view.<owner>.<name>.<n>.<fields>)
#
# A cursor given but empty is part of the key too (comments.<id>.), so it never passes for a first page. A cursor of digits
# alone is unmatched: <key>.<digits> names the nth call's fixture below, so comments.<id>.1.json would answer both the page
# after cursor 1 and the first un-paged call, and a case would pass on the wrong answer. Every key part has
# each / replaced by _, so a value can't name a file outside FAKE_GH_DIR. The nth call with a key is answered from
# FAKE_GH_DIR/<key>.<n>.json or .fail when there is one, else from <key>.json or <key>.fail, so a looping run can see a
# repo change between polls: a .json is printed on stdout (exit 0), a .fail on stderr (exit 1: a GitHub failure). For a
# `gh pr view`, the .json holds what its --jq would print: the fake runs no jq there, and the expression is in the logged
# argv, which a port must send unchanged. For a REST call, the .json holds the API's response, and with --jq the fake runs
# real jq over it and prints a string result raw, as gh does, so the calls that read one path through different
# expressions share its fixture; --paginate is taken as given, the fixture being every page at once.
#
# Each call is logged as its key; a `gh pr` call, and a REST call with --paginate or --jq, as its key, a space and its whole
# argv, each argument as it is or, when it
# holds anything but letters, digits and _ . / : @ , + = # -, in double quotes with \ " and line breaks, tabs and carriage
# returns written \\ \" \n \t \r, so a line holds one call and the body a comment carries is compared too.
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

# One argument as the log writes it: as it is when it is plain, else quoted with its specials escaped.
plain='^[A-Za-z0-9_./:@,+=#-]+$'
encode() {
  local arg="$1"
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

all="$*"
argv=("$@")
entry=""

if [[ "${1:-}" == pr ]]; then
  shift
  sub="${1:-}"
  case "$sub" in
    ready | update-branch | comment | edit | merge | view) shift ;;
    *) unmatched "gh $all" ;;
  esac
  number="" repo="" fields="" jq=""
  while [[ $# -gt 0 ]]; do
    case "$1" in
      -R | --repo)
        [[ $# -ge 2 ]] || unmatched "gh $all"
        repo="$2"
        ;;
      --body) [[ $sub == comment && $# -ge 2 ]] || unmatched "gh $all" ;;
      --add-reviewer | --add-label) [[ $sub == edit && $# -ge 2 ]] || unmatched "gh $all" ;;
      --match-head-commit) [[ $sub == merge && $# -ge 2 ]] || unmatched "gh $all" ;;
      --json)
        [[ $sub == view && $# -ge 2 ]] || unmatched "gh $all"
        fields="$2"
        ;;
      --jq)
        [[ $sub == view && $# -ge 2 ]] || unmatched "gh $all"
        jq=1
        ;;
      --auto | --squash)
        [[ $sub == merge ]] || unmatched "gh $all"
        shift
        continue
        ;;
      *)
        [[ -z "$number" && "$1" =~ ^[1-9][0-9]*$ ]] || unmatched "gh $all"
        number="$1"
        shift
        continue
        ;;
    esac
    shift 2
  done
  [[ -n "$number" && "$repo" == ?*/?* ]] || unmatched "gh $all"
  [[ $sub != view || (-n "$fields" && -n "$jq") ]] || unmatched "gh $all"
  key="pr.$sub.$(part "${repo%%/*}").$(part "${repo#*/}").$number${fields:+.$(part "$fields")}"
  entry="$key gh"
  for arg in "${argv[@]}"; do
    entry+=" $(encode "$arg")"
  done
else
  [[ "${1:-}" == api ]] || unmatched "gh $all"
  shift

  if [[ "${1:-}" != graphql ]]; then
    [[ $# -ge 1 && -n "$1" && "$1" != -* ]] || unmatched "gh api $*"
    key="rest.$(part "$1")"
    shift
    paginate="" jq="" has_jq=""
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --paginate)
          [[ -z "$paginate" ]] || unmatched "gh $all"
          paginate=1
          shift
          ;;
        --jq)
          [[ $# -ge 2 && -z "$has_jq" && -n "$2" ]] || unmatched "gh $all"
          jq="$2" has_jq=1
          shift 2
          ;;
        *) unmatched "gh $all" ;;
      esac
    done
    if [[ -n "$paginate$has_jq" ]]; then
      entry="$key gh"
      for arg in "${argv[@]}"; do
        entry+=" $(encode "$arg")"
      done
    fi
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
    [[ ! ("$has_cursor" && "$cursor" =~ ^[0-9]+$) ]] \
      || unmatched "gh api graphql with the cursor $cursor, digits alone, which a fixture name would read as a call number"
    page="${has_cursor:+.$(part "$cursor")}"
    case "$q" in
      *"pullRequests("*"states:OPEN"*) key="repo.$(part "$owner").$(part "$name")" ;;
      *"timelineItems("*) key="events.$(part "$id")$page" ;;
      *"comments(last:"*) key="comments.$(part "$id")$page" ;;
      *"reviewThreads("*"comments(first:"*) key="threads.$(part "$id")" ;;
      *) unmatched "gh api graphql with a query the fake doesn't know: ${q:0:80}" ;;
    esac
  fi
  entry="${entry:-$key}"
fi

# This is the nth call with the key: one more than the calls with it logged already.
n=1
if [[ -f "$FAKE_GH_LOG" ]]; then
  while IFS= read -r logged || [[ -n "$logged" ]]; do
    [[ "${logged%% *}" != "$key" ]] || n=$((n + 1))
  done < "$FAKE_GH_LOG"
fi

for fixture in "$key.$n.json" "$key.$n.fail" "$key.json" "$key.fail"; do
  file="$FAKE_GH_DIR/$fixture"
  [[ -e "$file" ]] || continue
  [[ -f "$file" && -r "$file" ]] || unmatched "$key (its fixture $fixture is not a readable file)"
  if [[ "$fixture" == *.json && -n "${has_jq:-}" ]]; then
    # gh runs the expression over the response and prints each result as gh does: a string raw, null as an empty line,
    # and anything else as compact JSON with its keys sorted at every level and <, >, &, U+2028 and U+2029 escaped (Go's
    # encoder). A number that isn't an integer is the one result printed otherwise (gh writes 1.5 as 1.50).
    command -v jq > /dev/null || unmatched "$key (jq, which a call with --jq needs, is not on PATH)"
    # jq reads the program from a file (-f): jq before 1.7 refuses -- ahead of a program given as an argument, and without
    # -- a program that starts with - (such as -.n) would be read as an option.
    answer="$(mktemp)" || unmatched "$key (no temporary file for jq's answer)"
    program="$(mktemp)" || {
      rm -f "$answer"
      unmatched "$key (no temporary file for jq's program)"
    }
    trap 'rm -f "$answer" "$program"' EXIT
    {
      printf '%s\n' 'def gh_escape(from; to): split(from) | join(to);'
      printf '%s\n' 'def gh_print: if type == "string" then . elif . == null then "" else'
      printf '%s\n' '  walk(if type == "object" then to_entries | sort_by(.key) | from_entries else . end) | tojson'
      printf '%s\n' '  | gh_escape("<"; "\\u003c") | gh_escape(">"; "\\u003e") | gh_escape("&"; "\\u0026")'
      printf '%s\n' '  | gh_escape("\u2028"; "\\u2028") | gh_escape("\u2029"; "\\u2029") end;'
      printf '(\n%s\n) | gh_print\n' "$jq"
    } > "$program" || unmatched "$key (can't write jq's program)"
    jq -r -f "$program" < "$file" > "$answer" 2> /dev/null || unmatched "$key (jq can't run $jq over its fixture $fixture)"
    printf '%s\n' "$entry" >> "$FAKE_GH_LOG"
    cat -- "$answer"
    exit 0
  fi
  printf '%s\n' "$entry" >> "$FAKE_GH_LOG"
  if [[ "$fixture" == *.json ]]; then
    cat -- "$file"
    exit 0
  fi
  cat -- "$file" >&2
  exit 1
done
unmatched "$key"
