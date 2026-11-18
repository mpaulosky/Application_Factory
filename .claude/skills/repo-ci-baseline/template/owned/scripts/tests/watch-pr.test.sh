#!/usr/bin/env bash
# Tests for scripts/watch-pr.sh, run in throwaway git repositories against a fake `gh` and a fake `sleep`.
# Usage: scripts/tests/watch-pr.test.sh
#
# The fake gh ignores the query it is given and prints a canned GraphQL response, so the script must read gh's whole output with jq
# itself (no `gh --jq`). The response has GitHub's own shape, under .data.repository.pullRequest:
#   state, merged, mergeable, mergeStateStatus, headRefOid,
#   statusCheckRollup.{commit.oid, contexts.nodes[]}  (CheckRun: name, conclusion, status; StatusContext: context, state),
#   reviewThreads.nodes[]  (isResolved, comments.nodes[]: author { __typename, login }, body; the last one is the thread's last comment),
#   reviews.nodes[] and comments.nodes[]  (id, author { __typename, login }, body, and state for a review).
set -uo pipefail

# Hooks that run this file set GIT_* variables that would aim git init and rev-parse at the outer repo.
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX
unset GITHUB_REPOSITORY WATCH_PR_MARKER WATCH_PR_INTERVAL WATCH_PR_TIMEOUT GH_REPO

SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/watch-pr.sh"
work="$(mktemp -d)"
trap 'command rm -rf "$work"' EXIT
mkdir "$work/no-template"
export GIT_TEMPLATE_DIR="$work/no-template"

if ! command -v jq >/dev/null 2>&1; then
  echo "skip - jq is not installed, and watch-pr.sh needs it"
  exit 0
fi

PASSED=0
FAILED=0
out=""
err=""
status=0
n=0
repo=""

# A fake gh: logs its arguments (and GH_REPO) per call, then prints $GH_FIXTURE. GH_FAKE_FAIL_FILE holds a count of calls
# still to fail; GH_FAKE_GARBAGE=1 prints text that is not JSON.
fakebin="$work/fakebin"
mkdir "$fakebin"
cat > "$fakebin/gh" <<'EOF' # gh-repo: ok
#!/usr/bin/env bash
{
  printf 'GH_REPO=%s ::' "${GH_REPO-}"
  printf ' %s' "$@"
  printf '\n'
} >> "$GH_LOG"
if [[ -n "${GH_FAKE_FAIL_FILE-}" && -s "$GH_FAKE_FAIL_FILE" ]]; then
  left="$(<"$GH_FAKE_FAIL_FILE")"
  if ((left > 0)); then
    echo $((left - 1)) > "$GH_FAKE_FAIL_FILE"
    echo "gh: HTTP 502" >&2 # gh-repo: ok
    exit 1
  fi
fi
if [[ -n "${GH_FAKE_GARBAGE-}" ]]; then
  echo "<html>Bad gateway</html>"
  exit 0
fi
# A page query (one with -f cursor=<c>) is answered from $GH_PAGES_DIR/<c>.json, so a test can chain pages by cursor.
for arg in "$@"; do
  if [[ "$arg" == cursor=* && -n "${GH_PAGES_DIR-}" ]]; then
    cat "$GH_PAGES_DIR/${arg#cursor=}.json" 2>/dev/null || { echo "gh: no page ${arg#cursor=}" >&2; exit 1; } # gh-repo: ok
    exit 0
  fi
done
cat "$GH_FIXTURE"
EOF
chmod +x "$fakebin/gh" # gh-repo: ok
# A fake sleep that returns at once and logs how long it was asked to wait.
cat > "$fakebin/sleep" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$SLEEP_LOG"
EOF
chmod +x "$fakebin/sleep"

# A PATH with the tools the script may need but no jq, for the missing-tool case.
nojq="$work/nojq"
mkdir "$nojq"
for tool in bash env git sed grep cat date mkdir mktemp dirname basename head tail tr sort uniq awk wc cut printf rm mv cp ln touch tee xargs readlink realpath id seq expr test true false; do
  path="$(command -v "$tool" 2>/dev/null || true)"
  [[ -n "$path" && "$path" == /* ]] && ln -s "$path" "$nojq/$tool"
done
cp "$fakebin/gh" "$fakebin/sleep" "$nojq/" # gh-repo: ok

export GH_LOG="$work/gh.log"
export SLEEP_LOG="$work/sleep.log"
export GH_FIXTURE="$work/fixture.json"
export GH_FAKE_FAIL_FILE="$work/fail-count"

SHA="0123456789abcdef0123456789abcdef01234567"
PERSON='{"__typename":"User","login":"reviewer"}'
OWNER='{"__typename":"User","login":"owner"}'
COPILOT='{"__typename":"Bot","login":"copilot-pull-request-reviewer"}'

# new_repo [origin-url]: a scratch repository (with an origin remote unless the argument is "<none>") and a quiet, green PR.
new_repo() {
  repo="$work/repo $((++n))"
  git init -q -b main "$repo"
  git -C "$repo" -c user.name=t -c user.email=t@example.com commit -q --allow-empty -m init
  if [[ "${1-https://github.com/acme/widgets.git}" != "<none>" ]]; then
    git -C "$repo" remote add origin "${1-https://github.com/acme/widgets.git}"
  fi
  : > "$GH_LOG"
  : > "$SLEEP_LOG"
  rm -f "$GH_FAKE_FAIL_FILE"
  unset GH_FAKE_GARBAGE GH_PAGES_DIR
  pr '{}'
}

# pr <json>: the fixture is a green, open PR with the given object merged over it (objects merge, arrays are replaced).
pr() {
  jq -n --argjson over "$1" --arg sha "$SHA" '
    {state: "OPEN", merged: false, mergeable: "MERGEABLE", mergeStateStatus: "CLEAN", headRefOid: $sha,
     statusCheckRollup: {commit: {oid: $sha}, contexts: {pageInfo: {hasNextPage: false}, nodes: []}},
     reviewThreads: {pageInfo: {hasNextPage: false}, nodes: []},
     reviews: {pageInfo: {hasPreviousPage: false}, nodes: []},
     comments: {pageInfo: {hasPreviousPage: false}, nodes: []}} * $over
    | {data: {repository: {pullRequest: .}}}' > "$GH_FIXTURE"
}
# checks <nodes-json>: the PR's checks.
checks() { pr "{\"statusCheckRollup\":{\"contexts\":{\"nodes\":$1}}}"; }
# thread <resolved> <author-json> <body>: one thread whose only (so last) comment is that.
thread() {
  pr "$(jq -n --argjson resolved "$1" --argjson author "$2" --arg body "$3" \
    '{reviewThreads: {nodes: [{isResolved: $resolved, comments: {nodes: [{author: $author, body: $body}]}}]}}')"
}
# review <id> <state> <author-json> <body>
review() {
  pr "$(jq -n --arg id "$1" --arg state "$2" --argjson author "$3" --arg body "$4" \
    '{reviews: {nodes: [{id: $id, state: $state, author: $author, body: $body}]}}')"
}
# comment <id> <author-json> <body>
comment() {
  pr "$(jq -n --arg id "$1" --argjson author "$2" --arg body "$3" '{comments: {nodes: [{id: $id, author: $author, body: $body}]}}')"
}

# run <args...>: the script from the scratch repository, standard output and error kept apart.
run() {
  out="$(cd "$repo" && PATH="$fakebin:$PATH" bash "$SCRIPT" "$@" 2>"$work/err")"
  status=$?
  err="$(<"$work/err")"
}
run_in() { # run_in <dir> <args...>
  local dir="$1"
  shift
  out="$(cd "$dir" && PATH="$fakebin:$PATH" bash "$SCRIPT" "$@" 2>"$work/err")"
  status=$?
  err="$(<"$work/err")"
}
run_env() { # run_env <VAR=value>... -- <args...>
  local -a vars=()
  while [[ "$1" != "--" ]]; do vars+=("$1"); shift; done
  shift
  out="$(cd "$repo" && env "${vars[@]}" PATH="$fakebin:$PATH" bash "$SCRIPT" "$@" 2>"$work/err")"
  status=$?
  err="$(<"$work/err")"
}
polls() { grep -c '^GH_REPO=' "$GH_LOG"; }
sleeps() { grep -c . "$SLEEP_LOG"; }

check() {
  local description="$1"
  shift
  if "$@"; then
    PASSED=$((PASSED + 1)); echo "ok   - $description"
  else
    FAILED=$((FAILED + 1)); echo "FAIL - $description (exit $status)"
    while IFS= read -r line; do echo "       | $line"; done <<< "$out"
    while IFS= read -r line; do echo "       ! $line"; done <<< "$err"
  fi
}
status_is() { [[ $status -eq $1 ]]; }
out_has() { [[ $out == *"$1"* ]]; }
out_lacks() { [[ $out != *"$1"* ]]; }
out_is_empty() { [[ -z $out ]]; }
out_line_count_is() { [[ $(grep -c . <<< "$out") -eq $1 ]]; }
err_has() { [[ $err == *"$1"* ]]; }
polls_are() { [[ $(polls) -eq $1 ]]; }
sleeps_are() { [[ $(sleeps) -eq $1 ]]; }
not() { ! "$@"; }
in_order() { [[ "$1" == *"$2"*"$3"* ]]; } # in_order <text> <a> <b>: <a> comes before <b> in <text>
log_has() { grep -qF -- "$1" "$GH_LOG"; }
log_lacks() { ! grep -qF -- "$1" "$GH_LOG"; }

# Exit 0 and the reason's line: the session is needed.
woken() { # woken <code>
  check "$1 wakes the session: exit 0" status_is 0
  check "$1 prints a 'watch-pr: $1:' line" out_has "watch-pr: $1:"
}
quiet() { # quiet <description>
  check "$1: exit 1" status_is 1
  check "$1: prints nothing" out_is_empty
}

# ---------------------------------------------------------------------------
# Each reason, alone, with --once.
# ---------------------------------------------------------------------------

new_repo
run --once 7
quiet "a green open PR with nothing to say"
check "a quiet poll is one gh call" polls_are 1 # gh-repo: ok
check "--once never sleeps" sleeps_are 0

new_repo
pr '{"state":"MERGED","merged":true}'
run --once 7
woken merged
check "merged: nothing else is printed" out_line_count_is 1

new_repo
pr '{"state":"CLOSED","merged":false}'
run --once 7
woken closed
check "closed: nothing else is printed" out_line_count_is 1

new_repo
pr '{"state":"MERGED","merged":true,"mergeable":"CONFLICTING","mergeStateStatus":"DIRTY"}'
run --once 7
check "merged: a conflict is not reported beside it" out_lacks "conflict"

new_repo
pr '{"mergeable":"CONFLICTING","mergeStateStatus":"DIRTY"}'
run --once 7
woken conflict
check "conflict: behind is not reported beside it" out_lacks "watch-pr: behind"

new_repo
pr '{"mergeStateStatus":"BEHIND"}'
run --once 7
woken behind

new_repo
pr '{"mergeable":"UNKNOWN","mergeStateStatus":"UNKNOWN"}'
run --once 7
quiet "mergeable UNKNOWN (not yet known)"

new_repo
checks '[{"__typename":"CheckRun","name":"Unit tests","status":"COMPLETED","conclusion":"FAILURE"}]'
run --once 7
woken check-failed
check "check-failed names the check" out_has "Unit tests"

for conclusion in TIMED_OUT ACTION_REQUIRED STARTUP_FAILURE; do
  new_repo
  checks "[{\"__typename\":\"CheckRun\",\"name\":\"Build\",\"status\":\"COMPLETED\",\"conclusion\":\"$conclusion\"}]"
  run --once 7
  woken check-failed
  check "a check run that ended $conclusion is named" out_has "Build"
done

new_repo
checks '[{"__typename":"StatusContext","context":"ci/legacy","state":"FAILURE"}]'
run --once 7
woken check-failed
check "a failed status context is named" out_has "ci/legacy"

new_repo
checks '[{"__typename":"StatusContext","context":"ci/legacy","state":"ERROR"}]'
run --once 7
woken check-failed

new_repo
checks '[{"__typename":"CheckRun","name":"Lint","status":"COMPLETED","conclusion":"FAILURE"},{"__typename":"CheckRun","name":"Docs","status":"COMPLETED","conclusion":"TIMED_OUT"},{"__typename":"CheckRun","name":"Fine","status":"COMPLETED","conclusion":"SUCCESS"}]'
run --once 7
check "check-failed names every failed check" out_has "Lint"
check "check-failed names every failed check (second)" out_has "Docs"
check "check-failed leaves a passing check out" out_lacks "Fine"

for entry in 'CheckRun|COMPLETED|CANCELLED' 'CheckRun|IN_PROGRESS|' 'CheckRun|QUEUED|' 'CheckRun|COMPLETED|SKIPPED' 'CheckRun|COMPLETED|NEUTRAL' 'CheckRun|COMPLETED|SUCCESS'; do
  IFS='|' read -r kind run_status conclusion <<< "$entry"
  new_repo
  checks "[{\"__typename\":\"$kind\",\"name\":\"Job\",\"status\":\"$run_status\",\"conclusion\":$(jq -n --arg c "$conclusion" 'if $c == "" then null else $c end')}]"
  run --once 7
  quiet "a check run $run_status/${conclusion:-none} doesn't wake the session"
done

new_repo
checks '[{"__typename":"StatusContext","context":"ci/legacy","state":"PENDING"}]'
run --once 7
quiet "a pending status context doesn't wake the session"

new_repo
thread false "$PERSON" "Please rename this."
run --once 7
woken thread
check "thread: the comment's first line is shown" out_has "Please rename this."
check "thread: the author is shown" out_has "reviewer"

new_repo
thread true "$PERSON" "Please rename this."
run --once 7
quiet "a resolved thread"

new_repo
thread false "$COPILOT" "Consider a constant here."
run --once 7
woken thread

new_repo
review R1 CHANGES_REQUESTED "$PERSON" "Needs another test."
run --once 7
woken changes-requested

new_repo
review R1 CHANGES_REQUESTED "$PERSON" ""
run --once 7
woken changes-requested
check "changes requested with no body is still reported" status_is 0

new_repo
review R1 CHANGES_REQUESTED "$COPILOT" "Bot asks for changes."
run --once 7
check "a bot's CHANGES_REQUESTED is not a person's" not out_has "changes-requested"

new_repo
review R1 COMMENTED "$PERSON" "Looks fine overall, one question."
run --once 7
woken review
check "review: the body is shown" out_has "Looks fine overall"

new_repo
review R1 APPROVED "$PERSON" ""
run --once 7
quiet "an approval with no body"

new_repo
review R1 COMMENTED "$COPILOT" "Copilot reviewed 3 files."
run --once 7
quiet "a bot's review body"

new_repo
comment C1 "$PERSON" "Can you also update the docs?"
run --once 7
woken comment
check "comment: the body is shown" out_has "update the docs"

new_repo
comment C1 "$OWNER" "Merge when green."
run --once 7
woken comment
check "the owner's unmarked comment counts as a person's" out_has "Merge when green."

new_repo
comment C1 '{"__typename":"Bot","login":"github-actions"}' "Coverage is 91%."
run --once 7
quiet "a bot's comment"

# Several reasons at once are all printed, one per line.
new_repo
pr '{"mergeStateStatus":"BEHIND"}'
jq --arg body "Fix this" '.data.repository.pullRequest.reviewThreads.nodes = [{isResolved:false, comments:{nodes:[{author:{__typename:"User",login:"reviewer"}, body:$body}]}}]' "$GH_FIXTURE" > "$work/f" && mv "$work/f" "$GH_FIXTURE"
run --once 7
check "every reason that applies is printed" out_has "watch-pr: behind"
check "every reason that applies is printed (thread)" out_has "watch-pr: thread"
check "every reason is on a line of its own" out_line_count_is 2
check "several reasons: exit 0" status_is 0

# ---------------------------------------------------------------------------
# What the session and Sandcastle post themselves doesn't wake it.
# ---------------------------------------------------------------------------

sandcastle_bodies=(
  $'<!-- sandcastle:pr -->\nCloses #1'
  $'<!-- sandcastle:follow-up -->\nPass summary'
  $'<!-- sandcastle:follow-up-reply -->\nFixed in abc.'
  $'<!-- sandcastle:give-up -->\nSandcastle stopped its follow-up passes on this PR.'
  $'<!-- sandcastle:worktree-tampered -->\nSandcastle stopped: the worktree'
  $'<!-- sandcastle:build-failed -->\nThe build failed.'
  $'<!-- sandcastle:design -->\nThe design.'
  $'<!-- sandcastle:umbrella -->\nTracked as an umbrella.'
  "<!-- sandcastle:approved-head $SHA -->"
  "<!-- sandcastle:overlap $SHA issue#12 -->"
)
for body in "${sandcastle_bodies[@]}"; do
  label="${body%%$'\n'*}"
  new_repo
  thread false "$OWNER" "$body"
  run --once 7
  quiet "a thread whose last comment starts $label"
  new_repo
  comment C1 "$OWNER" "$body"
  run --once 7
  quiet "a PR comment that starts $label"
  new_repo
  review R1 COMMENTED "$OWNER" "$body"
  run --once 7
  quiet "a review that starts $label"
done

new_repo
thread false "$OWNER" $'Fixed in abc123.\n\n<!-- claude-session -->'
run --once 7
quiet "a thread whose last comment carries the session's marker mid-body"

new_repo
comment C1 "$OWNER" $'Pushed the fix.\n<!-- claude-session -->\nMore text.'
run --once 7
quiet "a PR comment with the session's marker mid-body"

new_repo
review R1 CHANGES_REQUESTED "$OWNER" $'Self-review notes.\n<!-- claude-session -->'
run --once 7
quiet "a CHANGES_REQUESTED review the session marked"

# Only the thread's last comment decides.
new_repo
pr "$(jq -n --argjson p "$PERSON" --argjson o "$OWNER" \
  '{reviewThreads: {nodes: [{isResolved: false, comments: {nodes: [{author: $p, body: "Please change this."}, {author: $o, body: "Done.\n<!-- claude-session -->"}]}}]}}')"
run --once 7
quiet "a thread the session answered last"
new_repo
pr "$(jq -n --argjson p "$PERSON" --argjson o "$OWNER" \
  '{reviewThreads: {nodes: [{isResolved: false, comments: {nodes: [{author: $o, body: "Done.\n<!-- claude-session -->"}, {author: $p, body: "Still wrong."}]}}]}}')"
run --once 7
woken thread

# A quoted or mid-body Sandcastle marker is not Sandcastle's.
new_repo
comment C1 "$PERSON" $'> <!-- sandcastle:pr -->\n\nWhy is this here?'
run --once 7
woken comment
new_repo
comment C1 "$PERSON" "Look at <!-- sandcastle:give-up --> in the log."
run --once 7
woken comment
new_repo
thread false "$PERSON" $'See\n<!-- sandcastle:follow-up -->'
run --once 7
woken thread

# WATCH_PR_MARKER replaces the session's marker.
new_repo
comment C1 "$OWNER" $'Done.\n<!-- my-session -->'
run_env WATCH_PR_MARKER='<!-- my-session -->' -- --once 7
quiet "WATCH_PR_MARKER: a comment with the repo's own marker"
new_repo
comment C1 "$OWNER" $'Done.\n<!-- claude-session -->'
run_env WATCH_PR_MARKER='<!-- my-session -->' -- --once 7
woken comment
check "WATCH_PR_MARKER replaces the default marker, not adds to it" status_is 0
new_repo
comment C1 "$OWNER" $'Done.\n<!-- my-session -->'
run --once 7
woken comment

# ---------------------------------------------------------------------------
# The seen file: one-shot events are reported once.
# ---------------------------------------------------------------------------

new_repo
comment C1 "$PERSON" "Can you also update the docs?"
run --once 7
woken comment
run --once 7
quiet "a comment already reported"
comment C2 "$PERSON" "And the changelog?"
run --once 7
woken comment
check "a new comment is reported" out_has "changelog"

new_repo
review R1 COMMENTED "$PERSON" "Looks fine overall, one question."
run --once 7
run --once 7
quiet "a review already reported"

new_repo
review R1 CHANGES_REQUESTED "$PERSON" "Needs another test."
run --once 7
woken changes-requested
run --once 7
quiet "a CHANGES_REQUESTED review already reported"

new_repo
thread false "$PERSON" "Please rename this."
run --once 7
run --once 7
woken thread
check "an unresolved thread is reported again until it's resolved" out_has "Please rename this."

new_repo
pr '{"mergeStateStatus":"BEHIND"}'
run --once 7
run --once 7
woken behind
new_repo
checks '[{"__typename":"CheckRun","name":"Lint","status":"COMPLETED","conclusion":"FAILURE"}]'
run --once 7
run --once 7
woken check-failed
new_repo
pr '{"mergeable":"CONFLICTING","mergeStateStatus":"DIRTY"}'
run --once 7
run --once 7
woken conflict

# The seen file lives in the git common dir, one per PR.
new_repo
comment C1 "$PERSON" "Can you also update the docs?"
run --once 7
common="$(git -C "$repo" rev-parse --absolute-git-dir)"
check "the seen file is watch-pr-<n>.seen under the git directory" test -f "$common/watch-pr-7.seen"
check "the seen file holds the reported node id" grep -qxF C1 "$common/watch-pr-7.seen"
check "the seen file is not in the working tree" not test -e "$repo/watch-pr-7.seen"
run --once 8
woken comment
check "another PR has its own seen file" test -f "$common/watch-pr-8.seen"

# A linked worktree shares the main checkout's seen file.
new_repo
git -C "$repo" worktree add -q "$work/linked $n" -b other
comment C1 "$PERSON" "Can you also update the docs?"
run --once 7
run_in "$work/linked $n" --once 7
quiet "a comment reported from the main checkout, polled again from a linked worktree"
check "the linked worktree's seen file is the main checkout's" test -f "$(git -C "$repo" rev-parse --absolute-git-dir)/watch-pr-7.seen"

# A comment the session or Sandcastle posted isn't recorded as seen or reported.
new_repo
comment C1 "$OWNER" $'<!-- sandcastle:pr -->\nCloses #1'
run --once 7
quiet "a Sandcastle comment alone"

# ---------------------------------------------------------------------------
# Read-only, and told its repository.
# ---------------------------------------------------------------------------

new_repo
pr '{"mergeStateStatus":"BEHIND"}'
run --once 7
check "every gh call is 'api graphql'" test "$(grep -vc ':: api graphql' "$GH_LOG")" -eq 0 # gh-repo: ok
check "no gh call carries a mutation" log_lacks "mutation" # gh-repo: ok
check "the query names the PR number" log_has "7"

new_repo
run --once 7
check "GH_REPO is taken from the origin remote" log_has "GH_REPO=acme/widgets ::"

new_repo "git@github.com:acme/widgets.git"
run --once 7
check "GH_REPO is taken from an scp-style remote" log_has "GH_REPO=acme/widgets ::"

new_repo
git -C "$repo" remote add upstream https://github.com/upstream-org/widgets.git
run --once 7
check "the upstream remote wins over origin" log_has "GH_REPO=upstream-org/widgets ::"

new_repo
run_env GITHUB_REPOSITORY=env-org/from-env -- --once 7
check "GITHUB_REPOSITORY wins over the remotes" log_has "GH_REPO=env-org/from-env ::"

new_repo "<none>"
run --once 7
check "no remote and no GITHUB_REPOSITORY: exit 2" status_is 2
check "no remote and no GITHUB_REPOSITORY: nothing is polled" polls_are 0

# ---------------------------------------------------------------------------
# Usage.
# ---------------------------------------------------------------------------

new_repo
run
check "no PR number: exit 2" status_is 2
check "no PR number: usage on stderr" err_has "Usage"
check "no PR number: nothing polled" polls_are 0
run --once abc
check "a non-numeric PR number: exit 2" status_is 2
check "a non-numeric PR number: nothing polled" polls_are 0
run --once 7 8
check "two PR numbers: exit 2" status_is 2
run --once --interval 0 7
check "--interval 0: exit 2" status_is 2
check "--interval 0: nothing polled" polls_are 0
run --once --interval soon 7
check "a non-numeric --interval: exit 2" status_is 2
run --once --timeout 0 7
check "--timeout 0: exit 2" status_is 2
run --once --timeout abc 7
check "a non-numeric --timeout: exit 2" status_is 2
run --nope 7
check "an unknown flag: exit 2" status_is 2
check "an unknown flag: nothing polled" polls_are 0
run_env WATCH_PR_INTERVAL=0 -- --once 7
check "WATCH_PR_INTERVAL=0: exit 2" status_is 2
run_env WATCH_PR_TIMEOUT=abc -- --once 7
check "a non-numeric WATCH_PR_TIMEOUT: exit 2" status_is 2

new_repo
out="$(cd "$repo" && PATH="$nojq" bash "$SCRIPT" --once 7 2>"$work/err")"
status=$?
err="$(<"$work/err")"
check "no jq on PATH: exit 2" status_is 2
check "no jq on PATH: the message names jq" err_has "jq"
check "no jq on PATH: nothing polled" polls_are 0

new_repo
mkdir "$work/nogh-$n"
for tool in bash env git jq sed grep cat date mkdir mktemp dirname basename head tail tr sort uniq awk wc cut rm mv; do
  path="$(command -v "$tool" 2>/dev/null || true)"
  [[ -n "$path" && "$path" == /* ]] && ln -s "$path" "$work/nogh-$n/$tool"
done
out="$(cd "$repo" && PATH="$work/nogh-$n" bash "$SCRIPT" --once 7 2>"$work/err")"
status=$?
err="$(<"$work/err")"
check "no gh on PATH: exit 2" status_is 2 # gh-repo: ok
check "no gh on PATH: the message names gh" err_has "gh" # gh-repo: ok

# ---------------------------------------------------------------------------
# Polling, retries and the timeout.
# ---------------------------------------------------------------------------

# A quiet PR is polled every interval until the timeout: --timeout 3 --interval 60 is three minutes of polls.
new_repo
run --interval 60 --timeout 3 7
quiet "a quiet PR until the timeout"
check "the timeout is a number of minutes: three minutes at 60s is at least three polls" test "$(polls)" -ge 3
check "the timeout is a number of minutes: three minutes at 60s is at most four polls" test "$(polls)" -le 4
check "it sleeps the interval between polls" grep -qx 60 "$SLEEP_LOG"
check "it sleeps only the interval" test "$(grep -vcx 60 "$SLEEP_LOG")" -eq 0

new_repo
run_env WATCH_PR_INTERVAL=30 WATCH_PR_TIMEOUT=1 -- 7
quiet "the interval and timeout defaults come from the environment"
check "WATCH_PR_INTERVAL sets the sleep" grep -qx 30 "$SLEEP_LOG"

new_repo
run_env WATCH_PR_INTERVAL=30 WATCH_PR_TIMEOUT=1 -- --interval 20 7
check "a flag wins over WATCH_PR_INTERVAL" grep -qx 20 "$SLEEP_LOG"

new_repo
run 7 --once
check "the PR number may come before --once" status_is 1

# The default interval is 60 seconds (about 1-3 GraphQL points a poll, so not lower).
new_repo
run_env WATCH_PR_TIMEOUT=2 -- 7
check "the default interval is 60 seconds" grep -qx 60 "$SLEEP_LOG"

# Wakes as soon as something happens, without waiting out the timeout.
new_repo
pr '{"state":"MERGED","merged":true}'
run --interval 60 --timeout 120 7
woken merged
check "a first poll with a reason doesn't sleep" sleeps_are 0
check "a first poll with a reason is one gh call" polls_are 1 # gh-repo: ok

# One failed poll is retried silently; the next one succeeds.
new_repo
echo 1 > "$GH_FAKE_FAIL_FILE"
pr '{"state":"MERGED","merged":true}'
run --interval 60 --timeout 120 7
woken merged
check "one failed poll is retried" polls_are 2
check "one failed poll leaves a note on stderr" err_has "watch-pr"
check "one failed poll doesn't put the failure on stdout" out_lacks "502"

# Five failed polls in a row are an error: exit 2, not quiet.
new_repo
echo 100 > "$GH_FAKE_FAIL_FILE"
run --interval 60 --timeout 120 7
check "five failed polls in a row: exit 2" status_is 2
check "five failed polls in a row: stdout is empty" out_is_empty
check "five failed polls in a row: it gave up at five" polls_are 5
check "five failed polls in a row: stderr says the PR can't be read" err_has "watch-pr"

# Four failures then a success is not an error.
new_repo
echo 4 > "$GH_FAKE_FAIL_FILE"
pr '{"state":"CLOSED"}'
run --interval 60 --timeout 120 7
woken closed

# A failure then a success then failures: the count is of consecutive failures.
new_repo
echo 3 > "$GH_FAKE_FAIL_FILE"
pr '{}'
run --interval 60 --timeout 1 7
check "three failures, then quiet until the timeout: exit 1" status_is 1

# A response with GitHub's shape whose text can't be read (a number for a comment body) is a failed poll every time: the failures add up
# across polls and end the watcher, rather than each one counting as the first.
new_repo
echo '{"data":{"repository":{"pullRequest":{"state":"OPEN","mergeable":"MERGEABLE","mergeStateStatus":"CLEAN","headRefOid":"x","statusCheckRollup":null,"reviewThreads":{"pageInfo":{"hasNextPage":false},"nodes":[]},"reviews":{"pageInfo":{"hasPreviousPage":false},"nodes":[]},"comments":{"pageInfo":{"hasPreviousPage":false},"nodes":[{"id":"C_1","author":{"__typename":"User","login":"reviewer"},"body":5}]}}}}}' > "$GH_FIXTURE"
run --interval 60 --timeout 120 7
check "an unreadable answer five times: exit 2" status_is 2
check "an unreadable answer is never reported as quiet" out_is_empty
check "an unreadable answer: it gave up at five" polls_are 5

# Output that isn't JSON is a failure, never quiet.
new_repo
export GH_FAKE_GARBAGE=1
run --interval 60 --timeout 120 7
check "non-JSON output five times: exit 2" status_is 2
check "non-JSON output is never reported as quiet" out_is_empty
new_repo
export GH_FAKE_GARBAGE=1
run --once 7
check "non-JSON output with --once is not quiet: it doesn't exit 1" not status_is 1
unset GH_FAKE_GARBAGE

# A response of the wrong shape (an API error body) is a failure, never quiet.
new_repo
echo '{"errors":[{"message":"Could not resolve to a PullRequest"}],"data":{"repository":{"pullRequest":null}}}' > "$GH_FIXTURE"
run --interval 60 --timeout 120 7
check "a null PR in the response is an error, not quiet: exit 2" status_is 2
check "a null PR in the response prints nothing on stdout" out_is_empty
new_repo
echo '{}' > "$GH_FIXTURE"
run --once 7
check "an empty object in --once mode is not quiet" not status_is 1
new_repo
echo '{"data":{"repository":{"pullRequest":{"state":"OPEN"}}}}' > "$GH_FIXTURE"
run --once 7
check "a response missing its lists is not quiet" not status_is 1

# A list GitHub says has more pages, or a rollup that isn't of headRefOid, is an incomplete read: a failed poll, never quiet.
for incomplete in \
  '{"reviewThreads":{"pageInfo":{"hasNextPage":true}}}' \
  '{"reviews":{"pageInfo":{"hasPreviousPage":true}}}' \
  '{"comments":{"pageInfo":{"hasPreviousPage":true}}}' \
  '{"statusCheckRollup":{"contexts":{"pageInfo":{"hasNextPage":true}}}}' \
  '{"statusCheckRollup":{"commit":{"oid":"not-the-head"}}}'; do
  new_repo
  pr "$incomplete"
  run --once 7
  check "an incomplete read ($incomplete) is not quiet" not status_is 1
  check "an incomplete read ($incomplete) is a failed poll: exit 2" status_is 2
done

# A list with more pages than the first query took is read to its end, page by page, by its cursor: threads and checks forward (after),
# reviews and comments back (before, since the query takes the last 100). An event on a later page is found like one on the first.
# page <cursor> <list> <nodes-json> <more> [<next-cursor>] [<over-json>]: the answer to a page query for <list> (threads, reviews, comments, checks).
page() {
  local c="$1" list="$2" nodes="$3" more="$4" next="${5-}" over="${6-}"
  [[ -n "$over" ]] || over='{}'
  jq -n --arg list "$list" --argjson nodes "$nodes" --argjson more "$more" --arg next "$next" --arg sha "$SHA" --argjson over "$over" '
    ($next | if . == "" then null else . end) as $cur
    | if $list == "threads" then {reviewThreads: {pageInfo: {hasNextPage: $more, endCursor: $cur}, nodes: $nodes}}
      elif $list == "checks" then {headRefOid: $sha, statusCheckRollup: {commit: {oid: $sha}, contexts: {pageInfo: {hasNextPage: $more, endCursor: $cur}, nodes: $nodes}}}
      else {($list): {pageInfo: {hasPreviousPage: $more, startCursor: $cur}, nodes: $nodes}} end
    | . * $over | {data: {repository: {pullRequest: .}}}' > "$GH_PAGES_DIR/$c.json"
}
pages_repo() { new_repo; export GH_PAGES_DIR="$work/pages $n"; mkdir -p "$GH_PAGES_DIR"; }
UNRESOLVED="[{\"isResolved\":false,\"comments\":{\"nodes\":[{\"author\":$PERSON,\"body\":\"on page two\"}]}}]"
RESOLVED="[{\"isResolved\":true,\"comments\":{\"nodes\":[{\"author\":$PERSON,\"body\":\"done\"}]}}]"
FAILING='[{"__typename":"CheckRun","name":"late-check","status":"COMPLETED","conclusion":"FAILURE"}]'
PASSING='[{"__typename":"CheckRun","name":"ok-check","status":"COMPLETED","conclusion":"SUCCESS"}]'

pages_repo
pr '{"reviewThreads":{"pageInfo":{"hasNextPage":true,"endCursor":"T1"},"nodes":'"$RESOLVED"'}}'
page T1 threads "$UNRESOLVED" false
run --once 7
check "an unresolved thread on the second page of threads is reported" status_is 0
check "it is reported as a thread" grep -q '^watch-pr: thread:' <<<"$out"
check "the threads page is asked for after the first page's end cursor" grep -qF 'cursor=T1' "$GH_LOG"
# shellcheck disable=SC2016 # GraphQL's $cursor, not the shell's
check "the threads page query reads forward from it" grep -qF 'reviewThreads(first:100,after:$cursor)' "$GH_LOG"

pages_repo
pr '{"reviews":{"pageInfo":{"hasPreviousPage":true,"startCursor":"R1"},"nodes":[]}}'
page R1 reviews "[{\"id\":\"PRR_old\",\"state\":\"CHANGES_REQUESTED\",\"author\":$PERSON,\"body\":\"older\"}]" false
run --once 7
check "a changes-requested review on an earlier page of reviews is reported" grep -q '^watch-pr: changes-requested:' <<<"$out"
# shellcheck disable=SC2016 # GraphQL's $cursor, not the shell's
check "the reviews page query reads back from the start cursor" grep -qF 'reviews(last:100,before:$cursor)' "$GH_LOG"

pages_repo
pr '{"comments":{"pageInfo":{"hasPreviousPage":true,"startCursor":"C1"},"nodes":[]}}'
page C1 comments "[{\"id\":\"IC_old\",\"author\":$PERSON,\"body\":\"an older comment\"}]" false
run --once 7
check "a person's comment on an earlier page of comments is reported" grep -q '^watch-pr: comment:' <<<"$out"
# shellcheck disable=SC2016 # GraphQL's $cursor, not the shell's
check "the comments page query reads back from the start cursor" grep -qF 'comments(last:100,before:$cursor)' "$GH_LOG"

pages_repo
pr '{"statusCheckRollup":{"contexts":{"pageInfo":{"hasNextPage":true,"endCursor":"K1"},"nodes":'"$PASSING"'}}}'
page K1 checks "$FAILING" false
run --once 7
check "a failing check on the second page of checks is reported" grep -q '^watch-pr: check-failed:.*late-check' <<<"$out"

# Pages that hold nothing to act on leave the PR quiet: paging is a whole read, not a failed one.
pages_repo
pr '{"reviewThreads":{"pageInfo":{"hasNextPage":true,"endCursor":"T1"},"nodes":[]},"statusCheckRollup":{"contexts":{"pageInfo":{"hasNextPage":true,"endCursor":"K1"},"nodes":[]}}}'
page T1 threads "$RESOLVED" false
page K1 checks "$PASSING" false
run --once 7
check "a PR read whole over several pages with nothing to act on is quiet: exit 1" status_is 1

# Ten pages after the first are read (n-1, n); an eleventh is refused as more than the watcher reads, with that reason, not as a malformed answer (n+1).
for extra in 9 10 11; do
  pages_repo
  pr '{"reviewThreads":{"pageInfo":{"hasNextPage":true,"endCursor":"T1"},"nodes":[]}}'
  for ((i = 1; i <= extra; i++)); do
    if ((i < extra)); then page "T$i" threads "$RESOLVED" true "T$((i + 1))"; else page "T$i" threads "$RESOLVED" false; fi
  done
  run --once 7
  if ((extra <= 10)); then
    check "a list of $extra pages after the first is read whole: quiet" status_is 1
  else
    check "a list of $extra pages after the first is a failed poll: exit 2" status_is 2
    check "it says the list is longer than the watcher reads" grep -q 'more than 10 pages' <<<"$err"
    check "it doesn't call the answer malformed" not grep -q 'not a pull request' <<<"$err"
  fi
done

# A page GitHub says has more but gives no cursor for, a page that fails, a page of another shape, or a checks page of another head commit is an
# incomplete read: a failed poll, never quiet.
pages_repo
pr '{"reviewThreads":{"pageInfo":{"hasNextPage":true,"endCursor":null},"nodes":[]}}'
run --once 7
check "more pages with no cursor is a failed poll: exit 2" status_is 2
check "it says no cursor was given" grep -q 'no cursor' <<<"$err"
pages_repo
pr '{"reviews":{"pageInfo":{"hasPreviousPage":true,"startCursor":"missing"},"nodes":[]}}'
run --once 7
check "a page that fails to be read is a failed poll: exit 2" status_is 2
pages_repo
pr '{"comments":{"pageInfo":{"hasPreviousPage":true,"startCursor":"C1"},"nodes":[]}}'
echo '{"data":{"repository":{"pullRequest":{"comments":{"nodes":"nope"}}}}}' > "$GH_PAGES_DIR/C1.json"
run --once 7
check "a page whose nodes aren't a list is a failed poll: exit 2" status_is 2
pages_repo
pr '{"statusCheckRollup":{"contexts":{"pageInfo":{"hasNextPage":true,"endCursor":"K1"},"nodes":[]}}}'
page K1 checks "$PASSING" false "" '{"headRefOid":"another-head","statusCheckRollup":{"commit":{"oid":"another-head"}}}'
run --once 7
check "a checks page of another head (a push between pages) is a failed poll: exit 2" status_is 2
check "it says the head moved" grep -q 'head moved' <<<"$err"
pages_repo
pr '{"reviews":{"pageInfo":{"hasPreviousPage":true,"startCursor":"R1"},"nodes":[]}}'
page R1 reviews '[]' false
jq '.data.repository.pullRequest.reviews.pageInfo.hasPreviousPage = "no"' "$GH_PAGES_DIR/R1.json" > "$GH_PAGES_DIR/R1.tmp" && mv "$GH_PAGES_DIR/R1.tmp" "$GH_PAGES_DIR/R1.json"
run --once 7
check "a page whose pageInfo flag isn't a boolean is a failed poll: exit 2" status_is 2
check "it names the page it couldn't read" grep -q "page 2 of the PR's reviews is not a list" <<<"$err"

# Pages keep GitHub's order: an earlier page of comments (read back) comes before the first query's newest ones, and a later page of
# threads (read forward) after the first's, so the reasons are printed oldest first.
pages_repo
pr "{\"comments\":{\"pageInfo\":{\"hasPreviousPage\":true,\"startCursor\":\"C1\"},\"nodes\":[{\"id\":\"IC_new\",\"author\":$PERSON,\"body\":\"newer\"}]}}"
page C1 comments "[{\"id\":\"IC_old\",\"author\":$PERSON,\"body\":\"older\"}]" false
run --once 7
check "an earlier page's comment is printed before the newest page's" in_order "$out" older newer
pages_repo
pr "{\"reviewThreads\":{\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"T1\"},\"nodes\":[{\"isResolved\":false,\"comments\":{\"nodes\":[{\"author\":$PERSON,\"body\":\"first-page\"}]}}]}}"
page T1 threads "[{\"isResolved\":false,\"comments\":{\"nodes\":[{\"author\":$PERSON,\"body\":\"second-page\"}]}}]" false
run --once 7
check "a later page's thread is printed after the first page's" in_order "$out" first-page second-page

# An incomplete read that paging can't mend (a rollup of another commit) says so, not that the answer is malformed.
new_repo
pr '{"statusCheckRollup":{"commit":{"oid":"not-the-head"}}}'
run --once 7
check "a rollup of another commit is reported as an incomplete read" grep -q 'incomplete' <<<"$err"
check "and not as an answer that isn't a pull request" not grep -q 'not a pull request' <<<"$err"
new_repo
echo '{"data":{"repository":{"pullRequest":null}}}' > "$GH_FIXTURE"
run --once 7
check "an answer with no pull request still says so" grep -q 'not a pull request' <<<"$err"

# A merged or closed PR is reported whatever its pageInfo holds: its rollup may be of nothing once the branch is gone, and its lists' pages are never read.
for done_state in MERGED CLOSED; do
  for over in '{"reviewThreads":{"pageInfo":{"hasNextPage":true}}}' '{"reviews":{"pageInfo":{"hasPreviousPage":true}}}' \
    '{"comments":{"pageInfo":{"hasPreviousPage":true}}}' '{"statusCheckRollup":{"commit":{"oid":"not-the-head"}}}'; do
    new_repo
    pr "$(jq -n --arg state "$done_state" --argjson over "$over" '{state: $state, merged: ($state == "MERGED")} * $over')"
    run --once 7
    check "a $done_state PR with $over is still reported: exit 0" status_is 0
  done
done

# A merged or closed PR's lists must still be arrays: the shape check keeps that rule outside its OPEN-only part.
for done_state in MERGED CLOSED; do
  for list in reviewThreads reviews comments; do
    new_repo
    pr "$(jq -n --arg state "$done_state" --arg list "$list" '{state: $state, merged: ($state == "MERGED")} * {($list): {nodes: null}}')"
    run --once 7
    check "a $done_state PR whose $list is not an array is a failed poll: exit 2" status_is 2
  done
done

# An OPEN PR whose rollup isn't of its head, or a response with neither oid, is a failed poll, and a missing pageInfo is no proof of a whole read.
for incomplete in '{"headRefOid":null,"statusCheckRollup":{"commit":{"oid":null}}}' '{"statusCheckRollup":{"commit":null}}' \
  '{"reviewThreads":{"pageInfo":null}}' '{"reviews":{"pageInfo":null}}' '{"comments":{"pageInfo":null}}' \
  '{"statusCheckRollup":{"contexts":{"pageInfo":null}}}'; do
  new_repo
  pr "$incomplete"
  run --once 7
  check "an open PR with $incomplete is a failed poll: exit 2" status_is 2
done

# The query asks for what the shape check requires (the fake gh ignores the query, so pin its text).
new_repo
pr '{}'
run --once 7
for wanted in 'reviewThreads(first:100){pageInfo{hasNextPage endCursor}' 'reviews(last:100){pageInfo{hasPreviousPage startCursor}' \
  'comments(last:100){pageInfo{hasPreviousPage startCursor}' 'contexts(first:100){pageInfo{hasNextPage endCursor}' 'statusCheckRollup{commit{oid}' 'headRefOid'; do
  check "the query asks for $wanted" grep -qF -- "$wanted" "$GH_LOG"
done
check "the query no longer reads the last commit" not grep -qF 'commits(last' "$GH_LOG"

# A PR whose head repository is gone, or a fork, is watched from its own rollup: the query reads no head ref.
new_repo
pr '{}'
run --once 7
check "the query reads no head ref" not grep -qE 'headRef([^O]|$)|headRepository' "$GH_LOG"
# No rollup yet is no check reported: quiet, unless the PR is UNSTABLE (a check failed), which is an incomplete read.
new_repo
pr '{"statusCheckRollup":null}'
run --once 7
check "an open PR with no rollup is quiet" status_is 1
new_repo
pr '{"statusCheckRollup":null,"mergeStateStatus":"UNSTABLE"}'
run --once 7
check "an UNSTABLE PR with no rollup is a failed poll: exit 2" status_is 2
# A rollup with no contexts, or contexts with no nodes, is no whole read.
for incomplete in '{"statusCheckRollup":{"contexts":null}}' '{"statusCheckRollup":{"contexts":{"nodes":null}}}'; do
  new_repo
  pr "$incomplete"
  run --once 7
  check "an open PR with rollup $incomplete is a failed poll: exit 2" status_is 2
done

# A failing check on the PR rollup is found even though the PR has no commits list at all.
new_repo
checks '[{"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"FAILURE"}]'
run --once 7
check "a failing check on the PR rollup is reported" status_is 0

# ---------------------------------------------------------------------------
# --would-skip: the classifier alone, reading a comment body on stdin.
# ---------------------------------------------------------------------------

would_skip() { # would_skip <body>: sets status
  out="$(printf '%s' "$1" | PATH="$fakebin:$PATH" bash "$SCRIPT" --would-skip 2>"$work/err")"
  status=$?
  err="$(<"$work/err")"
}

new_repo
: > "$GH_LOG"
would_skip $'Fixed.\n\n<!-- claude-session -->'
check "--would-skip: the session marker mid-body is skipped (exit 0)" status_is 0
would_skip "Please fix this"
check "--would-skip: plain text isn't skipped (exit 1)" status_is 1
would_skip ""
check "--would-skip: an empty body isn't skipped (exit 1)" status_is 1
would_skip "<!-- sandcastle:pr -->"
check "--would-skip: a Sandcastle marker alone is skipped" status_is 0
would_skip "<!-- sandcastle:approved-head $SHA -->"
check "--would-skip: a marker with data after its name is skipped" status_is 0
would_skip "<!-- sandcastle:overlap $SHA issue#12 -->"
check "--would-skip: the overlap marker is skipped" status_is 0
would_skip $'<!-- sandcastle:follow-up-reply -->\nFixed in abc.'
check "--would-skip: a hyphenated marker name is skipped" status_is 0
would_skip "> <!-- sandcastle:pr -->"
check "--would-skip: a quoted marker isn't skipped" status_is 1
would_skip "text <!-- sandcastle:pr -->"
check "--would-skip: a marker mid-line isn't skipped" status_is 1
would_skip $'First line\n<!-- sandcastle:pr -->'
check "--would-skip: a Sandcastle marker on a later line isn't skipped" status_is 1
would_skip " <!-- sandcastle:pr -->"
check "--would-skip: a marker after a leading space isn't skipped" status_is 1
would_skip "<!-- sandcastle: -->"
check "--would-skip: an empty marker name isn't skipped" status_is 1
would_skip "<!-- sandcastle:PR -->"
check "--would-skip: an upper-case marker name isn't skipped" status_is 1
would_skip "<!-- sandcastle:pr"
check "--would-skip: an unclosed marker isn't skipped" status_is 1
would_skip "<!-- other:pr -->"
check "--would-skip: another tool's marker isn't skipped" status_is 1
check "--would-skip needs no PR number, and polls nothing" polls_are 0
check "--would-skip prints nothing" out_is_empty

out="$(printf '%s' '<!-- my-session -->' | WATCH_PR_MARKER='<!-- my-session -->' PATH="$fakebin:$PATH" bash "$SCRIPT" --would-skip 2>/dev/null)"
status=$?
check "--would-skip honours WATCH_PR_MARKER" status_is 0
would_skip $'Fixed.\n<!-- claude-session -->'
out="$(printf '%s' $'Fixed.\n<!-- claude-session -->' | WATCH_PR_MARKER='<!-- my-session -->' PATH="$fakebin:$PATH" bash "$SCRIPT" --would-skip 2>/dev/null)"
status=$?
check "--would-skip: with WATCH_PR_MARKER set, the default marker is no longer the session's" status_is 1

echo
echo "$PASSED passed, $FAILED failed"
[[ $FAILED -eq 0 ]]
