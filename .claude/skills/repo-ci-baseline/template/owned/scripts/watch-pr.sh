#!/usr/bin/env bash
# Watches one pull request and exits with the reason when the session that opened it needs to act. Owned by the repo-ci-baseline
# Template (references/watch-pr.md). Read-only: it makes `gh api graphql` queries and writes only its seen file.
#   scripts/watch-pr.sh [--once] [--interval <seconds>] [--timeout <minutes>] <pr-number>
#   scripts/watch-pr.sh --would-skip      reads a comment body on stdin; exit 0 when the watcher skips it from the repository's owner, 1 when it wouldn't
#
# Each poll prints one `watch-pr: <code>: <detail>` line per reason that applies, up to 50, then exits 0. The codes, in order:
#   merged, closed    the PR is done (printed alone)
#   conflict          the PR conflicts with its base
#   behind            the base branch moved and the repo requires an up-to-date branch
#   check-failed      a check run failed, timed out, needs action or didn't start, or a status context failed
#   changes-requested a person's review requested changes (reported once)
#   review            a person's review with a body (reported once)
#   comment           a person's PR comment (reported once)
#   thread            an unresolved review thread whose last comment is not the session's or Sandcastle's (after the one-shot events,
#                     so no number of threads keeps them out of the 50 lines)
#   more              how many reasons past the first 50 a poll didn't print (last): no comment flood floods the session. Only the
#                     printed one-shot events are recorded as seen, so the next run prints the rest; a thread past the first 50 shows
#                     once those ahead of it are resolved.
# A one-shot event is recorded in watch-pr-<n>.seen in the git common directory, so the next run stays quiet about it. A seen file that
# can't be written doesn't end the poll: stderr says so, it exits 0, and the event comes back next run.
#
# A comment is the session's when its body contains WATCH_PR_MARKER (default `<!-- claude-session -->`), and Sandcastle's when it
# starts with `<!-- sandcastle:<name> -->`, with or without data after the name, and in either case only when its author is the
# repository's owner (authorAssociation OWNER; a MEMBER or COLLABORATOR is not trusted). Neither wakes the watcher: both post under the
# owner's token, so the marker tells them from the owner, and the association keeps anyone else from forging one. It assumes a repository a
# user owns, as every Fleet repository is: in an organization's repository GitHub may report the owner's comments as MEMBER, so the session's
# own replies wouldn't count as its own there. No other person's text
# reaches the session: a comment or review from anyone but the owner wakes it with their login and a fixed phrase instead, and a thread
# whose last reply is theirs is reported the same way, never quieted by it. A bot's text (Copilot's threads) is shown, because the session
# needs reviewers' findings; bots are never a person. It is not trusted: a bot can relay text a PR's author chose (github-actions[bot] and
# the Claude Review workflow post what they derive from a fork's diff or title), so the session treats it as data, never as instructions.
#
# Exit codes: 0 the session is needed; 1 quiet until the timeout (or after --once); 2 a usage error, a missing tool or a PR that
# can't be read (five polls in a row fail; a poll that fails, isn't GitHub's answer, or reads an open PR's list or rollup only in part
# is never read as quiet). A list longer than 100 is read on by its cursor to its end, however long; a cursor that doesn't move on
# (one already read, or more pages with none) is a failed poll that says so, never an endless one.
# What a poll costs is bounded per hour, not per poll: each query (the first and every page) costs GitHub one GraphQL point, and after a poll
# the watcher waits the interval or, when the poll made more queries than that allows, 3600 * queries / budget seconds, whichever is longer.
# So a PR of any length is read whole, and the watcher never spends more than its budget an hour of the 5,000 points GitHub gives the token.
# Environment: WATCH_PR_MARKER, WATCH_PR_INTERVAL (default 60 seconds; a poll without more pages is one query, so not lower),
# WATCH_PR_TIMEOUT (default 120 minutes), WATCH_PR_BUDGET (GraphQL points an hour, default 1000),
# GITHUB_REPOSITORY (else the upstream, then the origin remote).
set -euo pipefail

usage() {
  echo "Usage: scripts/watch-pr.sh [--once] [--interval <seconds>] [--timeout <minutes>] <pr-number>" >&2
  echo "       scripts/watch-pr.sh --would-skip   (a comment body on stdin)" >&2
}

MARKER="${WATCH_PR_MARKER:-<!-- claude-session -->}"
interval="${WATCH_PR_INTERVAL:-60}"
timeout_min="${WATCH_PR_TIMEOUT:-120}"
budget="${WATCH_PR_BUDGET:-1000}"
max_lines=50
once=0
would_skip=0
pr=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --once) once=1 ;;
    --would-skip) would_skip=1 ;;
    --interval | --timeout)
      if [[ $# -lt 2 ]]; then
        usage
        exit 2
      fi
      if [[ "$1" == "--interval" ]]; then interval="$2"; else timeout_min="$2"; fi
      shift
      ;;
    --interval=*) interval="${1#*=}" ;;
    --timeout=*) timeout_min="${1#*=}" ;;
    -h | --help)
      usage
      exit 0
      ;;
    -*)
      echo "watch-pr: unknown option $1" >&2
      usage
      exit 2
      ;;
    *)
      if [[ -n "$pr" ]]; then
        usage
        exit 2
      fi
      pr="$1"
      ;;
  esac
  shift
done

if ! command -v jq > /dev/null 2>&1; then
  echo "watch-pr: jq is required and was not found on PATH" >&2
  exit 2
fi

# The classifier, written once for the poll and for --would-skip. A body is ours when it carries the session's marker anywhere,
# or starts (\A, not ^, which also matches after a newline) with a Sandcastle marker: a name of lower-case letters and hyphens,
# then optionally data (approved-head and overlap carry some), then `-->`. The poll counts it only on a node whose author is the
# repository's owner (is_owner): anyone can comment on a public repository's PR, and so anyone can write the marker. --would-skip reads a body alone.
# shellcheck disable=SC2016 # jq's $variables are not the shell's
OURS_JQ='
  def ours($body):
    ($body // "") as $s
    | ($s | contains($session)) or ($s | test("\\A<!-- sandcastle:[a-z-]+( [^>\n]*)?-->"));
  def is_owner: .authorAssociation == "OWNER";
  def ours_node: is_owner and ours(.body);
'

if ((would_skip)); then
  [[ "$(jq -Rs --arg session "$MARKER" "$OURS_JQ"' ours(.)')" == "true" ]]
  exit $?
fi

if [[ -z "$pr" || ! "$pr" =~ ^[1-9][0-9]*$ ]]; then
  usage
  exit 2
fi
if [[ ! "$interval" =~ ^[1-9][0-9]*$ ]]; then
  echo "watch-pr: --interval must be a whole number of seconds, 1 or more" >&2
  exit 2
fi
if [[ ! "$timeout_min" =~ ^[1-9][0-9]*$ ]]; then
  echo "watch-pr: --timeout must be a whole number of minutes, 1 or more" >&2
  exit 2
fi
if [[ ! "$budget" =~ ^[1-9][0-9]{0,8}$ ]]; then
  echo "watch-pr: WATCH_PR_BUDGET must be a whole number of GraphQL points an hour, 1 to 999999999" >&2
  exit 2
fi

if ! command -v gh > /dev/null 2>&1; then                       # gh-repo: ok
  echo "watch-pr: gh is required and was not found on PATH" >&2 # gh-repo: ok
  exit 2
fi

url="$(git remote get-url upstream 2> /dev/null || git remote get-url origin 2> /dev/null || true)"
url="${url%.git}"
repo="${GITHUB_REPOSITORY:-}"
if [[ -z "$repo" && -n "$url" ]]; then repo="${url#*github.com[:/]}"; fi
if [[ ! "$repo" =~ ^[^/[:space:]]+/[^/[:space:]]+$ ]]; then
  echo "watch-pr: can't tell which repository to watch: set GITHUB_REPOSITORY or add an upstream or origin remote" >&2
  exit 2
fi
export GH_REPO="${repo:?}"

common_dir="$(cd "$(git rev-parse --git-common-dir)" && pwd)"
seen_file="$common_dir/watch-pr-$pr.seen"

# shellcheck disable=SC2016 # GraphQL's $variables are not the shell's
QUERY='query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){state merged mergeable mergeStateStatus headRefOid statusCheckRollup{commit{oid} contexts(first:100){pageInfo{hasNextPage endCursor} nodes{__typename ... on CheckRun{name status conclusion} ... on StatusContext{context state}}}} reviewThreads(first:100){pageInfo{hasNextPage endCursor} nodes{isResolved comments(last:1){nodes{author{__typename login} authorAssociation body}}}} reviews(last:100){pageInfo{hasPreviousPage startCursor} nodes{id state author{__typename login} authorAssociation body}} comments(last:100){pageInfo{hasPreviousPage startCursor} nodes{id author{__typename login} authorAssociation body}}}}}'

# A list with more than the first query's 100 is read on, a page of 100 at a time, from its cursor: threads and checks forward from their end cursor,
# reviews and comments back from their start cursor (the first query took the newest 100). Each page query reads that one list.
# shellcheck disable=SC2016 # GraphQL's $variables are not the shell's
PAGE_PR='query($owner:String!,$name:String!,$number:Int!,$cursor:String!){repository(owner:$owner,name:$name){pullRequest(number:$number){'
# shellcheck disable=SC2016
PAGE_QUERY_threads="${PAGE_PR}"'reviewThreads(first:100,after:$cursor){pageInfo{hasNextPage endCursor} nodes{isResolved comments(last:1){nodes{author{__typename login} authorAssociation body}}}}}}}'
# shellcheck disable=SC2016
PAGE_QUERY_reviews="${PAGE_PR}"'reviews(last:100,before:$cursor){pageInfo{hasPreviousPage startCursor} nodes{id state author{__typename login} authorAssociation body}}}}}'
# shellcheck disable=SC2016
PAGE_QUERY_comments="${PAGE_PR}"'comments(last:100,before:$cursor){pageInfo{hasPreviousPage startCursor} nodes{id author{__typename login} authorAssociation body}}}}}'
# shellcheck disable=SC2016
PAGE_QUERY_checks="${PAGE_PR}"'headRefOid statusCheckRollup{commit{oid} contexts(first:100,after:$cursor){pageInfo{hasNextPage endCursor} nodes{__typename ... on CheckRun{name status conclusion} ... on StatusContext{context state}}}}}}}'

# The answer must be GitHub's pull request, with its lists present, or it is no answer at all.
BASE_JQ='
  .data.repository.pullRequest
  | type == "object" and (.state | type == "string")
    and ([.reviewThreads.nodes, .reviews.nodes, .comments.nodes] | all(type == "array"))
'

# The response must have GitHub's shape, with every list present and read whole: anything else is a failed poll, never a quiet one.
# The checks come from the PR's own rollup, not the last of its commits (GitHub lists them by date, so the last needn't be the head) and not
# the head ref (which lives in the head repository: a fork's, or one that is gone), and only when that rollup is of headRefOid.
# No rollup means no check has reported, unless the PR is UNSTABLE (a check failed): then the read is incomplete. It runs after read_rest,
# which has read every list with more pages to its end, so a page flag still true means a list was left unread: an incomplete read. Only an open PR's lists are read: a merged or closed one is reported from its state alone, whatever its pageInfo or its rollup hold (its lists must still be arrays).
SHAPE_JQ='
  .data.repository.pullRequest
  | type == "object" and (.state | type == "string")
    and ([.reviewThreads.nodes, .reviews.nodes, .comments.nodes] | all(type == "array"))
    and (.state != "OPEN" or (
      (.reviewThreads.pageInfo.hasNextPage == false)
      and (.reviews.pageInfo.hasPreviousPage == false)
      and (.comments.pageInfo.hasPreviousPage == false)
      and (.headRefOid | type == "string")
      and ((.statusCheckRollup == null and .mergeStateStatus != "UNSTABLE") or (.statusCheckRollup != null and (
        .statusCheckRollup.commit.oid == .headRefOid
        and (.statusCheckRollup.contexts | (.nodes | type == "array") and .pageInfo.hasNextPage == false))))))
'

# One line per reason: code, detail and the node id of a one-shot event (else empty), apart by the unit separator.
# shellcheck disable=SC2016
REASONS_JQ="$OURS_JQ"'
  # clean: every control and format character (Cc, Cf: the bidi controls, zero-width ones, the byte-order mark, the soft hyphen and the tag
  # characters among them), the line and paragraph separators (Zl, Zp), every default-ignorable code point (the list Unicode keeps, unassigned
  # ones included: the variation selectors, the Hangul fillers, the Khmer inherent vowels) and the blank braille cell, which a terminal
  # shows as nothing, each made a space, so no printed text is reordered or hidden; then the first 200 characters. The default-ignorable
  # code points are also listed by range, so a jq whose Oniguruma has older Unicode tables (U+180F joined in Unicode 14)
  # never blanks fewer of them.
  def clean: tostring | gsub("[\\p{Cc}\\p{Cf}\\p{Zl}\\p{Zp}\\p{Default_Ignorable_Code_Point}\u00ad\u034f\u061c\u115f\u1160\u17b4\u17b5\u180b-\u180f\u200b-\u200f\u202a-\u202e\u2060-\u206f\u3164\ufe00-\ufe0f\ufeff\uffa0\ufff0-\ufff8\ud82f\udca0-\ud82f\udca3\ud834\udd73-\ud834\udd7a\udb40\udc00-\udb43\udfff\u2800]"; " ") | .[0:200];
  def first_line: (. // "") | split("\n")[0];
  def person: (.author.__typename // "") != "Bot";
  def who: .author.login // "unknown";
  # What the session is shown of a node: the first line of its body when its author is the owner or a bot, else a fixed phrase.
  def said: if is_owner or (person | not) then .body | first_line else "(text not shown: the author is not the repository owner)" end;
  def line($code; $detail; $id): [$code, ($detail | clean), $id] | join("\u001f");
  .data.repository.pullRequest as $pr
  | ($seen | sub("\n+\\z"; "") | split("\n")) as $seen_ids
  | if $pr.state == "MERGED" or $pr.merged == true then line("merged"; "the PR was merged"; "")
    elif $pr.state == "CLOSED" then line("closed"; "the PR was closed without merging"; "")
    else
      (if $pr.mergeable == "CONFLICTING" then line("conflict"; "the PR conflicts with its base branch"; "")
       elif $pr.mergeStateStatus == "BEHIND" then line("behind"; "the base branch has moved on; merge it into the PR"; "")
       else empty end),
      ([($pr.statusCheckRollup.contexts.nodes // [])[]
        | select((.conclusion as $c | ["FAILURE", "TIMED_OUT", "ACTION_REQUIRED", "STARTUP_FAILURE"] | index($c))
                 or (.state as $s | ["FAILURE", "ERROR"] | index($s)))
        | (.name // .context)]
       | if length > 0 then line("check-failed"; join(", "); "") else empty end),
      ($pr.reviews.nodes[]
       | select(person and (ours_node | not) and (.id as $id | $seen_ids | index($id) | not))
       | if .state == "CHANGES_REQUESTED" then line("changes-requested"; "\(who): \(if (.body // "") == "" then "(no text)" else said end)"; .id)
         elif ((.body // "") | test("\\S")) and .state != "PENDING" then line("review"; "\(who): \(said)"; .id)
         else empty end),
      ($pr.comments.nodes[]
       | select(person and (ours_node | not) and (.id as $id | $seen_ids | index($id) | not))
       | line("comment"; "\(who): \(said)"; .id)),
      # Threads last: one is reported every poll until resolved, so many of them must not keep the one-shot events above out of the
      # 50 lines a poll prints.
      ($pr.reviewThreads.nodes[]
       | select(.isResolved == false)
       | (.comments.nodes | last) as $last
       | select($last != null and ($last | ours_node | not))
       | line("thread"; "\($last | who): \($last | said)"; ""))
    end
'

errfile="$(mktemp)"
pagefile="$(mktemp)"
nodesfile="$(mktemp)"
trap 'command rm -f "$errfile" "$pagefile" "$nodesfile"' EXIT

# read_rest: reads each list of the open PR in $resp that has more pages to its end, merging the pages into $resp (later pages after the
# first's threads and checks, earlier ones before its reviews and comments; a review or comment with an id a later page repeats, as when the list
# shifted between page queries, is kept once). Returns 1 with $reason set when a page can't be read whole: gh fails, GitHub gives no cursor, the first
# answer's checks are not of its head yet (no page of them is read then), a checks page is of another head than the first answer's or its
# rollup of another commit, the page isn't the list's shape, or GitHub hands back a cursor this list has already been read from (it would
# never end). There is no cap on the pages: a list is read until GitHub says it has no more. A page goes
# through a file, not an argument: 100 comment bodies can pass the argument length limit. Each page's nodes are kept in a file and put into $resp
# once, after the list's last page, and the ids are kept once in a keyed set, so reading a list costs time linear in its items, never in the
# square of its items or its pages. Pages are read at different moments, so a list
# that shifts between them (a comment deleted, a check re-run) can be read without an item for one poll; the next poll reads it afresh.
read_rest() {
  local list path flag cur_field order pages cursor query head_oid used info
  if [[ "$(printf '%s' "$resp" | jq -r '.data.repository.pullRequest.state')" != OPEN ]]; then return 0; fi
  head_oid="$(printf '%s' "$resp" | jq -r '.data.repository.pullRequest.headRefOid // ""')"
  for list in threads reviews comments checks; do
    case "$list" in
      threads) path=.reviewThreads flag=hasNextPage cur_field=endCursor order=after query="$PAGE_QUERY_threads" ;;
      reviews) path=.reviews flag=hasPreviousPage cur_field=startCursor order=before query="$PAGE_QUERY_reviews" ;;
      comments) path=.comments flag=hasPreviousPage cur_field=startCursor order=before query="$PAGE_QUERY_comments" ;;
      checks) path=.statusCheckRollup.contexts flag=hasNextPage cur_field=endCursor order=after query="$PAGE_QUERY_checks" ;;
    esac
    pages=0
    used=$'\n'       # the cursors this list has been read from, one per line
    : > "$nodesfile" # each page's nodes, one array per line, in the order read
    # The list's page information: the first answer's, then the last page's. $resp is left as it is until the list's last page is read.
    info="$(printf '%s' "$resp" | jq -c ".data.repository.pullRequest$path.pageInfo")"
    while [[ "$(jq -rn --argjson info "$info" --arg flag "$flag" '$info[$flag] // false')" == true ]]; do
      if [[ "$list" == checks ]] && ! printf '%s' "$resp" | jq -e --arg head "$head_oid" '.data.repository.pullRequest.statusCheckRollup.commit.oid == $head' > /dev/null 2>&1; then
        reason="an incomplete read of the open PR: its checks are not of its head commit yet"
        return 1
      fi
      pages=$((pages + 1))
      cursor="$(jq -rn --argjson info "$info" --arg f "$cur_field" '$info[$f] // "" | strings')"
      if [[ -z "$cursor" ]]; then
        reason="an incomplete read: GitHub says the PR's $list have more pages but gave no cursor for them"
        return 1
      fi
      if [[ "$used" == *$'\n'"$cursor"$'\n'* ]]; then
        reason="an incomplete read: page $pages of the PR's $list gave a cursor already read from, so its pages would never end"
        return 1
      fi
      used+="$cursor"$'\n'
      queries=$((queries + 1))
      if ! gh api graphql -f query="$query" -f owner="${repo%%/*}" -f name="${repo#*/}" -F number="$pr" -f cursor="$cursor" > "$pagefile" 2> "$errfile"; then
        reason="gh failed reading page $((pages + 1)) of the PR's $list: $(head -n 1 "$errfile")"
        return 1
      fi
      # A page naming another head is a push between pages; a page naming none is no list of checks (the shape check below says so).
      if [[ "$list" == checks ]] && jq -e --arg head "$head_oid" '.data.repository.pullRequest.headRefOid | type == "string" and . != $head' "$pagefile" > /dev/null 2>&1; then
        reason="an incomplete read: the PR's head moved while its checks were read"
        return 1
      fi
      if ! jq -e --arg flag "$flag" ".data.repository.pullRequest$path | type == \"object\" and (.nodes | type == \"array\") and (.pageInfo[\$flag] | type == \"boolean\")" "$pagefile" > /dev/null 2>&1; then
        reason="an incomplete read: page $((pages + 1)) of the PR's $list is not a list of them"
        return 1
      fi
      if [[ "$list" == checks ]] && ! jq -e --arg head "$head_oid" '.data.repository.pullRequest | .headRefOid == $head and .statusCheckRollup.commit.oid == $head' "$pagefile" > /dev/null 2>&1; then
        reason="an incomplete read: page $((pages + 1)) of the PR's checks is not of its head commit"
        return 1
      fi
      if ! jq -c ".data.repository.pullRequest$path.nodes" "$pagefile" >> "$nodesfile" \
        || ! info="$(jq -c ".data.repository.pullRequest$path.pageInfo" "$pagefile")"; then
        reason="the PR's $list could not be put together"
        return 1
      fi
    done
    if ((pages == 0)); then continue; fi
    # Threads and checks: the first answer's, then each page's. Reviews and comments: the pages from the oldest (the last read), then the first
    # answer's, each id kept at its first place in that order (a null id is always kept), found in one pass with a set of the ids kept. The pass
    # is a foreach that emits each node it keeps, never an array appended to in its state: jq copies such an array on every append, which is
    # time in the square of the items.
    if ! resp="$(printf '%s' "$resp" | jq -c --slurpfile pages "$nodesfile" --argjson info "$info" --arg order "$order" "
        .data.repository.pullRequest$path |= (.pageInfo = \$info
          | .nodes = (if \$order == \"after\" then .nodes + [\$pages[][]]
              else [foreach ((\$pages | reverse)[][], .nodes[]) as \$n ({kept: {}, keep: false};
                  (\$n.id | tojson) as \$key
                  | if \$n.id == null then .keep = true
                    elif .kept[\$key] then .keep = false
                    else .kept[\$key] = true | .keep = true end;
                  if .keep then \$n else empty end)] end))")"; then
      reason="the PR's $list could not be put together"
      return 1
    fi
  done
}

failures=0
elapsed=0

while true; do
  ok=1
  resp=""
  queries=1 # the queries this poll makes: the first, and one per page read_rest asks for (a failed one too)
  if ! resp="$(gh api graphql -f query="$QUERY" -f owner="${repo%%/*}" -f name="${repo#*/}" -F number="$pr" 2> "$errfile")"; then
    ok=0
    reason="gh failed: $(head -n 1 "$errfile")"
  elif ! printf '%s' "$resp" | jq -e "$BASE_JQ" > /dev/null 2>&1; then
    ok=0
    reason="the answer is not a pull request"
  elif ! read_rest; then
    ok=0
  elif ! printf '%s' "$resp" | jq -e "$SHAPE_JQ" > /dev/null 2>&1; then
    ok=0
    reason="an incomplete read of the open PR: its checks are not of its head commit yet, or a list's page information is missing"
  fi

  if ((ok)); then
    # The seen ids go to jq from the file, never as an argument: no list of them, however long, can pass the argument length limit.
    seen_from=/dev/null
    if [[ -f "$seen_file" ]]; then seen_from="$seen_file"; fi
    if ! reasons="$(printf '%s' "$resp" | jq -r --arg session "$MARKER" --rawfile seen "$seen_from" "$REASONS_JQ" 2> /dev/null)"; then
      ok=0
      reason="the answer could not be read"
    fi
  fi

  # The wait after this poll: the interval, or longer when the poll's queries would pass the hourly budget at that pace.
  wait=$(((queries * 3600 + budget - 1) / budget))
  if ((wait < interval)); then wait="$interval"; fi

  if ((ok)); then
    failures=0
    if [[ -n "$reasons" ]]; then
      # At most max_lines reasons are printed; only a printed one-shot event is recorded as seen, so the next run prints the rest.
      printed=0
      more=0
      while IFS=$'\x1f' read -r code detail id; do
        if ((printed >= max_lines)); then
          more=$((more + 1))
          continue
        fi
        printed=$((printed + 1))
        echo "watch-pr: $code: $detail"
        # A seen file that can't be written (a read-only git directory, a full disk) doesn't end the poll: the session is still needed.
        if [[ -n "$id" ]] && ! { echo "$id" >> "$seen_file"; } 2> /dev/null; then
          echo "watch-pr: can't record $id in $seen_file; the next run reports it again" >&2
        fi
      done <<< "$reasons"
      if ((more)); then echo "watch-pr: more: $more more not printed; act on these and run again"; fi
      exit 0
    fi
    if ((once)); then exit 1; fi
    # The timeout counts the waits between quiet polls; a failed poll's wait doesn't count, and at most four of them run in a row.
    if ((elapsed + wait > timeout_min * 60)); then exit 1; fi
    elapsed=$((elapsed + wait))
  else
    failures=$((failures + 1))
    echo "watch-pr: poll $failures failed: $reason" >&2
    if ((once || failures >= 5)); then
      echo "watch-pr: can't read PR $pr of $repo" >&2
      exit 2
    fi
  fi
  sleep "$wait"
done
