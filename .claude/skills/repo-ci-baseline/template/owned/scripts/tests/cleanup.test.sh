#!/usr/bin/env bash
# Tests for scripts/cleanup.sh, run in a throwaway clone with a bare origin and a fake `gh`.
# Usage: scripts/tests/cleanup.test.sh
#
# The fake gh answers `gh pr list --head <branch> ...` from the file $GH_FIX/<branch with / as _>: one "STATE headRefOid baseRefName owner" line per PR
# (what cleanup.sh's --jq makes of gh's JSON). It fails unless it is given --state all and --json, and GH_REPO. A file $GH_FIX/_fail makes it exit 1.
# A file $GH_FIX/<branch>.hook is run once, the first time that branch is asked about (a push that lands after the fetch).
# shellcheck disable=SC2034,SC2015 # out and o are read by the eval in check; the setup lines are meant as "or stop"
set -uo pipefail

unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX
SCRIPTS="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'command rm -rf "$work"' EXIT
mkdir "$work/no-template"
export GIT_TEMPLATE_DIR="$work/no-template" GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@t

PASSED=0; FAILED=0
ok() { PASSED=$((PASSED + 1)); echo "ok   - $1"; }
bad() { FAILED=$((FAILED + 1)); echo "FAIL - $1"; }
check() { if eval "$2"; then ok "$1"; else bad "$1"; fi; }

export GH_FIX="$work/fix"; mkdir "$GH_FIX"
mkdir "$work/bin"
cat > "$work/bin/gh" <<'GH' # gh-repo: ok
#!/usr/bin/env bash
[[ "$*" == *"--state all"* && "$*" == *"--json"* && "${GH_REPO:-}" == "o/r" ]] || { echo "fake gh: bad call: $* (GH_REPO=${GH_REPO:-})" >&2; exit 2; }
[[ ! -f "$GH_FIX/_fail" ]] || { echo "fake gh: network down" >&2; exit 1; }
while [[ $# -gt 0 ]]; do [[ "$1" == --head ]] && { b="$2"; break; }; shift; done
f="$GH_FIX/${b//\//_}"
if [[ -f "$f.hook" ]]; then bash "$f.hook"; rm -f "$f.hook"; fi
[[ -f "$f" ]] && cat "$f"
exit 0
GH
chmod +x "$work/bin/gh" # gh-repo: ok
export PATH="$work/bin:$PATH"
export GITHUB_REPOSITORY=o/r

git init -q --bare "$work/origin.git"
git clone -q "$work/origin.git" "$work/repo" 2>/dev/null
repo="$work/repo"
cd "$repo" || exit 1
git checkout -q -b main
mkdir scripts
cp "$SCRIPTS/check-branch-name.sh" "$SCRIPTS/cleanup.sh" scripts/
git add . && git commit -q -m init && git push -q origin main || { echo "setup failed"; exit 1; }
git remote set-head origin main

# A second clone, to push to origin as someone else would.
git clone -q "$work/origin.git" "$work/other" 2>/dev/null
other_push() { # other_push <ref> <file>: a commit on top of origin's <ref>, pushed from the second clone
  git -C "$work/other" fetch -q origin && git -C "$work/other" checkout -q -B tmp "origin/$1" \
    && echo "$2" > "$work/other/$2" && git -C "$work/other" add "$2" && git -C "$work/other" commit -q -m "$2" \
    && git -C "$work/other" push -q origin "tmp:refs/heads/$1"
}

# branch <name> <pushed:yes|no>: a worktree beside the clone with one commit; prints its tip
branch() {
  local dir="$work/wt/${1//\//-}"
  git worktree add -q -b "$1" "$dir" main >&2 || exit 1
  echo x > "$dir/f" && git -C "$dir" add f && git -C "$dir" commit -q -m "work on $1"
  if [[ "$2" == yes ]]; then git -C "$dir" push -q origin "$1" 2>/dev/null || exit 1; fi
  git -C "$dir" rev-parse HEAD
}
pr() { echo "$2 $3 ${4:-main} ${5:-o}" >> "$GH_FIX/${1//\//_}"; } # pr <branch> <STATE> <oid> [base] [owner]
remote_only() { git worktree remove "$work/wt/${1//\//-}" && git branch -q -D "$1"; }

tip=$(branch feature/1-merged yes);       pr feature/1-merged MERGED "$tip"
tip=$(branch fix/2-dirty yes);            pr fix/2-dirty MERGED "$tip"; echo dirt > "$work/wt/fix-2-dirty/f"
tip=$(branch chore/open-one yes);         pr chore/open-one OPEN "$tip"
tip=$(branch fix/3-unpushed yes);         pr fix/3-unpushed MERGED "$tip"
echo more > "$work/wt/fix-3-unpushed/g"; git -C "$work/wt/fix-3-unpushed" add g; git -C "$work/wt/fix-3-unpushed" commit -q -m more
tip=$(branch feature/4-closed yes);       pr feature/4-closed CLOSED "$tip"
tip=$(branch chore/nopr no)
tip=$(branch chore/remote-only yes);      pr chore/remote-only MERGED "$tip"; remote_only chore/remote-only
tip=$(branch chore/forked yes);           pr chore/forked MERGED "$tip" main someone; remote_only chore/forked
tip=$(branch chore/stacked yes);          pr chore/stacked MERGED "$tip" feature/9-base; remote_only chore/stacked
tip=$(branch chore/open-and-merged yes);  pr chore/open-and-merged MERGED "$tip"; pr chore/open-and-merged OPEN "$tip"
tip=$(branch chore/closed-then-merged yes); pr chore/closed-then-merged CLOSED "$tip"; pr chore/closed-then-merged MERGED "$tip"
tip=$(branch chore/locked yes);           pr chore/locked MERGED "$tip"; git worktree lock "$work/wt/chore-locked"
tip=$(branch chore/lease-race yes);       pr chore/lease-race MERGED "$tip"; remote_only chore/lease-race
git push -q origin main:refs/heads/Bad_Name

has_local() { git show-ref --verify --quiet "refs/heads/$1"; }
has_remote() { git ls-remote --exit-code --heads origin "$1" >/dev/null 2>&1; }

main_before=$(git rev-parse main)
other_push main from-someone-else

# gh failing is an error, and nothing is removed
touch "$GH_FIX/_fail"
out=$(bash scripts/cleanup.sh 2>&1); st=$?
rm -f "$GH_FIX/_fail"
git reset -q --hard "$main_before"
check "a gh failure fails the run" "[[ $st != 0 ]] && grep -q 'gh pr list failed' <<<\"\$out\""
check "a gh failure removes nothing" "has_local feature/1-merged && [[ -d $work/wt/feature-1-merged ]] && has_remote chore/remote-only"

# --dry-run
out=$(bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "dry run succeeds" "[[ $st == 0 ]]"
check "dry run changes nothing" "has_local feature/1-merged && [[ -d $work/wt/feature-1-merged ]] && has_remote chore/remote-only && has_remote feature/1-merged && [[ \$(git rev-parse main) == $main_before ]]"
check "dry run lists every step" "grep -q 'would: git worktree remove .*feature-1-merged' <<<\"\$out\" && grep -q 'would: git branch -q -D feature/1-merged' <<<\"\$out\" && grep -q 'would: git push .*feature/1-merged' <<<\"\$out\" && grep -q 'would: git merge --ff-only origin/main' <<<\"\$out\""

# Refusals (main is behind origin/main here, as it is for the real run below)
o=$( cd "$work/wt/feature-1-merged" && bash "$repo/scripts/cleanup.sh" 2>&1 ); st=$?
check "refuses in a linked worktree" "[[ $st != 0 ]] && grep -q 'primary checkout' <<<\"\$o\""
git checkout -q -b chore/elsewhere; o=$(bash scripts/cleanup.sh 2>&1); st=$?; git checkout -q main; git branch -q -D chore/elsewhere
check "refuses off main" "[[ $st != 0 ]] && grep -q 'run this on main' <<<\"\$o\""
echo dirt >> scripts/check-branch-name.sh; o=$(bash scripts/cleanup.sh 2>&1); st=$?; git checkout -q -- scripts
check "refuses a dirty main" "[[ $st != 0 ]] && grep -q 'uncommitted changes' <<<\"\$o\" && has_local feature/1-merged"
o=$(bash scripts/cleanup.sh --bogus 2>&1); st=$?
check "refuses an unknown flag" "[[ $st != 0 ]] && grep -q '^usage:' <<<\"\$o\""
mv scripts/check-branch-name.sh "$work/cbn.sh"; o=$(bash scripts/cleanup.sh 2>&1); st=$?; mv "$work/cbn.sh" scripts/check-branch-name.sh
check "refuses without check-branch-name.sh" "[[ $st != 0 ]] && grep -q 'check-branch-name.sh is missing' <<<\"\$o\""
echo local > local-main && git add local-main && git commit -q -m "local main commit"
o=$(bash scripts/cleanup.sh 2>&1); st=$?; git reset -q --hard "$main_before"
check "refuses when main can't fast-forward" "[[ $st != 0 ]] && grep -q \"can't fast-forward\" <<<\"\$o\" && has_local feature/1-merged"

cat > "$GH_FIX/chore_lease-race.hook" <<HOOK
git -C "$work/other" fetch -q origin && git -C "$work/other" checkout -q -B tmp origin/chore/lease-race && echo late > "$work/other/late" \
  && git -C "$work/other" add late && git -C "$work/other" commit -q -m late && git -C "$work/other" push -q origin tmp:refs/heads/chore/lease-race
HOOK
git fetch -q origin; lease_tip_before=$(git rev-parse origin/chore/lease-race)
# The real run: one removal (the locked worktree) is refused by git, so it exits 1 after doing the rest
out=$(bash scripts/cleanup.sh 2>&1); st=$?
check "exits 1 when git refused a removal, after doing the rest" "[[ $st == 1 ]]"
check "fast-forwards main to origin/main" "[[ \$(git rev-parse main) == \$(git rev-parse origin/main) && -f from-someone-else ]]"
check "removes a merged branch, its worktree and its origin branch" "! has_local feature/1-merged && [[ ! -d $work/wt/feature-1-merged ]] && ! has_remote feature/1-merged"
check "removes a merged branch that is only on origin" "! has_remote chore/remote-only"
check "keeps a merged branch whose worktree is dirty, and its origin branch" "has_local fix/2-dirty && [[ -d $work/wt/fix-2-dirty ]] && has_remote fix/2-dirty"
check "keeps a branch with an open PR" "has_local chore/open-one && has_remote chore/open-one"
check "keeps a branch with a commit the PR never had, and its origin branch" "has_local fix/3-unpushed && has_remote fix/3-unpushed"
check "keeps a closed-unmerged branch by default" "has_local feature/4-closed && has_remote feature/4-closed"
check "keeps a branch with no PR" "has_local chore/nopr"
check "leaves a non-standard origin branch alone" "has_remote Bad_Name"
check "does not mistake origin/HEAD for a branch" "! grep -q 'origin/origin' <<<\"\$out\""
check "says why it kept one" "grep -q 'keep   fix/2-dirty: its worktree has uncommitted changes' <<<\"\$out\" && grep -q 'keep   origin/fix/2-dirty: its local branch was kept' <<<\"\$out\""
check "ignores a PR from a fork with the same branch name" "has_remote chore/forked && grep -q 'keep   origin/chore/forked: no PR of this repository' <<<\"\$out\""
check "keeps a branch whose PR merged into another branch" "has_remote chore/stacked && grep -q 'merged into feature/9-base, not main' <<<\"\$out\""
check "an open PR beside a merged one keeps the branch" "has_local chore/open-and-merged && has_remote chore/open-and-merged"
check "a closed PR beside a merged one at the same tip counts as merged" "! has_local chore/closed-then-merged && ! has_remote chore/closed-then-merged"
check "keeps a locked worktree's branch and says git refused" "has_local chore/locked && has_remote chore/locked && grep -q 'keep   chore/locked: git could not remove its worktree' <<<\"\$out\""
check "keeps an origin branch that moved after the fetch" "has_remote chore/lease-race && [[ \$(git rev-parse origin/chore/lease-race) != $lease_tip_before ]] && grep -q 'keep   origin/chore/lease-race: git could not delete it' <<<\"\$out\""

bash scripts/cleanup.sh --include-closed >/dev/null 2>&1
check "--include-closed removes a closed-unmerged branch" "! has_local feature/4-closed && ! has_remote feature/4-closed"

echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
