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
#   gh api <path> [--paginate [--slurp]] [--jq <expression>]        a REST GET; --slurp only with --paginate and never with --jq
#   gh api -X|--method <METHOD> <path> [-H <header>]... [--input -] a REST call of any method, its body (--input -) on stdin
#   gh pr ready|update-branch <n> -R <owner/repo>
#   gh pr comment <n> -R <owner/repo> --body <text>
#   gh pr edit <n> -R <owner/repo> --add-reviewer|--add-label <value>
#   gh pr merge <n> -R <owner/repo> [--auto] [--squash] [--match-head-commit <sha>]...
#   gh pr view <n> -R <owner/repo> --json <fields> --jq <expression>
#   gh pr list -R <owner/repo> [--head <branch>] [--state <state>] [--limit <n>] --json <fields> --jq <expression>
#   gh auth status                                                   no other argument
#
# A GraphQL call is told apart by its query with all whitespace removed, so spacing and line breaks don't matter:
#
#   repo.<owner>.<name>           a query holding pullRequests( ... states:OPEN (the repo query)
#   events.<id>[.<cursor>]        a query holding timelineItems( (a PR's label and review-request events)
#   comments.<id>[.<cursor>]      a query holding comments(last: (a PR's comments, paged backwards)
#   threads.<id>                  a query holding reviewThreads( and comments(first: (a PR's threads with their comments)
#   rest.<path>                   gh api <path>, with or without --paginate, --slurp and --jq (the path's query string is in it),
#                                 or -X GET (--method GET)
#   rest.<METHOD>.<path>          gh api -X|--method <METHOD> <path>, for any METHOD but GET (rest.PATCH.repos_o_app)
#   pr.<sub>.<owner>.<name>.<n>   gh pr <sub> (a view's key ends .<fields> too: pr.view.<owner>.<name>.<n>.<fields>)
#   pr.list.<owner>.<name>        gh pr list, which takes no PR number
#   auth.status                   gh auth status (rollout.sh --auto's preflight asks it)
#
# A cursor given but empty is part of the key too (comments.<id>.), so it never passes for a first page. A cursor of digits
# alone is unmatched: <key>.<digits> names the nth call's fixture below, so comments.<id>.1.json would answer both the page
# after cursor 1 and the first un-paged call, and a case would pass on the wrong answer. Every key part has
# each / replaced by _, so a value can't name a file outside FAKE_GH_DIR. The nth call with a key is answered from
# FAKE_GH_DIR/<key>.<n>.json or .fail when there is one, else from <key>.json or <key>.fail, so a looping run can see a
# repo change between polls: a .json is printed on stdout (exit 0), a .fail on stderr (exit 1: a GitHub failure). For a
# `gh pr view` or `gh pr list`, the .json holds what its --jq would print: the fake runs no jq there, and the expression is in the logged
# argv, which a port must send unchanged. For a REST call, the .json holds the API's response, and with --jq the fake runs
# real jq over it and prints each result as gh does (a string raw, null as an empty line, anything else as compact JSON
# with sorted keys and Go's escapes; see the jq call for the one exception), so the calls that read one path through different
# expressions share its fixture; --paginate is taken as given, the fixture being every page at once (with --slurp, the list
# of pages it prints: [page, ...], which the fake checks a fixture that is JSON is, with jq).
#
# Each call is logged as its key; a `gh pr` call, and a REST call with --paginate, --jq, -X, --method or -H, as its key, a space and its whole
# argv, each argument as it is or, when it
# holds anything but letters, digits and _ . / : @ , + = # -, in double quotes with \ " and line breaks, tabs and carriage
# returns written \\ \" \n \t \r, so a line holds one call and the body a comment carries is compared too. A REST call with
# --input - reads one JSON value from stdin, and its line ends with " <<< " and that body as canonical JSON (jq -cS: compact,
# every object's keys sorted), quoted as an argument is, so two implementations that serialize the same body differently log
# the same line; stdin that isn't one JSON value is unmatched, and jq is needed for it.
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
    ready | update-branch | comment | edit | merge | view | list) shift ;;
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
        [[ ($sub == view || $sub == list) && $# -ge 2 ]] || unmatched "gh $all"
        fields="$2"
        ;;
      --jq)
        [[ ($sub == view || $sub == list) && $# -ge 2 ]] || unmatched "gh $all"
        jq=1
        ;;
      --head | --state | --limit) [[ $sub == list && $# -ge 2 ]] || unmatched "gh $all" ;;
      --auto | --squash)
        [[ $sub == merge ]] || unmatched "gh $all"
        shift
        continue
        ;;
      *)
        [[ $sub != list && -z "$number" && "$1" =~ ^[1-9][0-9]*$ ]] || unmatched "gh $all"
        number="$1"
        shift
        continue
        ;;
    esac
    shift 2
  done
  [[ (-n "$number" || $sub == list) && "$repo" == ?*/?* ]] || unmatched "gh $all"
  [[ ($sub != view && $sub != list) || (-n "$fields" && -n "$jq") ]] || unmatched "gh $all"
  if [[ $sub == list ]]; then
    key="pr.list.$(part "${repo%%/*}").$(part "${repo#*/}")"
  else
    key="pr.$sub.$(part "${repo%%/*}").$(part "${repo#*/}").$number${fields:+.$(part "$fields")}"
  fi
  entry="$key gh"
  for arg in "${argv[@]}"; do
    entry+=" $(encode "$arg")"
  done
elif [[ "${1:-}" == auth ]]; then
  [[ $# -eq 2 && "$2" == status ]] || unmatched "gh $all"
  key="auth.status"
  entry="$key"
else
  [[ "${1:-}" == api ]] || unmatched "gh $all"
  shift

  if [[ "${1:-}" != graphql ]]; then
    # The path is the one argument that isn't an option, before, between or after them, as gh takes it.
    path="" paginate="" slurp="" jq="" has_jq="" method="" headers="" input=""
    while [[ $# -gt 0 ]]; do
      case "$1" in
        -X | --method)
          [[ $# -ge 2 && -z "$method" && "$2" =~ ^[A-Z]+$ ]] || unmatched "gh $all"
          method="$2"
          shift 2
          ;;
        -H)
          [[ $# -ge 2 && -n "$2" ]] || unmatched "gh $all"
          headers=1
          shift 2
          ;;
        --input)
          [[ $# -ge 2 && -z "$input" && "$2" == - ]] || unmatched "gh $all"
          input=1
          shift 2
          ;;
        --paginate)
          [[ -z "$paginate" ]] || unmatched "gh $all"
          paginate=1
          shift
          ;;
        --slurp)
          [[ -z "$slurp" ]] || unmatched "gh $all"
          slurp=1
          shift
          ;;
        --jq)
          [[ $# -ge 2 && -z "$has_jq" && -n "$2" ]] || unmatched "gh $all"
          jq="$2" has_jq=1
          shift 2
          ;;
        *)
          [[ -z "$path" && -n "$1" && "$1" != -* ]] || unmatched "gh $all"
          path="$1"
          shift
          ;;
      esac
    done
    [[ -n "$path" ]] || unmatched "gh $all"
    # gh refuses --slurp without --paginate, and with --jq.
    [[ -z "$slurp" || (-n "$paginate" && -z "$has_jq") ]] || unmatched "gh $all"
    if [[ -z "$method" || "$method" == GET ]]; then
      key="rest.$(part "$path")"
    else
      key="rest.$method.$(part "$path")"
    fi
    if [[ -n "$paginate$has_jq$method$headers$input" ]]; then
      entry="$key gh"
      for arg in "${argv[@]}"; do
        entry+=" $(encode "$arg")"
      done
    fi
    if [[ -n "$input" ]]; then
      command -v jq > /dev/null || unmatched "$key (jq, which a call with --input needs, is not on PATH)"
      # -s reads every value on stdin into one list, so a body of none, or of two, is refused rather than logged as one.
      body="$(jq -cS -s 'if length == 1 then .[0] else error("not one value") end' 2> /dev/null)" \
        || unmatched "$key (its --input body is not one JSON value)"
      entry+=" <<< $(encode "$body")"
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
  if [[ "$fixture" == *.json && -n "${slurp:-}" ]]; then
    # --slurp prints the list of pages, so a fixture that is JSON must be one: a page alone would pass for the pages it
    # holds. One that isn't JSON at all stands for an answer cut short, which a script must survive.
    command -v jq > /dev/null || unmatched "$key (jq, which a call with --slurp needs, is not on PATH)"
    if jq -e -s . < "$file" > /dev/null 2>&1; then
      jq -e -s 'length == 1 and (.[0] | type == "array" and all(.[]; type == "array"))' < "$file" > /dev/null \
        || unmatched "$key (its --slurp fixture $fixture is not a list of pages)"
    fi
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
