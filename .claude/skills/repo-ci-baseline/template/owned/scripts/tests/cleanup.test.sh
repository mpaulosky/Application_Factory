#!/usr/bin/env bash
# Tests for scripts/cleanup.sh, run in a throwaway clone with a bare origin and a fake `gh`.
# Usage: scripts/tests/cleanup.test.sh
#
# The fake gh answers `gh pr list --head <branch> ...` from the file $GH_FIX/<branch with / as _>: one "STATE headRefOid" line per PR.
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
cat > "$work/bin/gh" <<'GH'
#!/usr/bin/env bash
while [[ $# -gt 0 ]]; do [[ "$1" == --head ]] && { b="$2"; break; }; shift; done
f="$GH_FIX/${b//\//_}"; [[ -f "$f" ]] && cat "$f"
exit 0
GH
chmod +x "$work/bin/gh"
export PATH="$work/bin:$PATH"
export GITHUB_REPOSITORY=o/r

git init -q --bare "$work/origin.git"
git clone -q "$work/origin.git" "$work/repo" 2>/dev/null
repo="$work/repo"
cd "$repo" || exit 1
git checkout -q -b main
mkdir scripts
cp "$SCRIPTS/check-branch-name.sh" "$SCRIPTS/cleanup.sh" scripts/
git add . && git commit -q -m init && git push -q origin main

# branch <name> <pushed:yes|no>: a worktree beside the clone with one commit; prints its tip
branch() {
  local dir="$work/wt/${1//\//-}"
  git worktree add -q -b "$1" "$dir" main
  echo x > "$dir/f" && git -C "$dir" add f && git -C "$dir" commit -q -m "work on $1"
  [[ "$2" == yes ]] && git -C "$dir" push -q origin "$1" 2>/dev/null
  git -C "$dir" rev-parse HEAD
}
pr() { echo "$2 $3" >> "$GH_FIX/${1//\//_}"; }

tip=$(branch feature/1-merged yes);   pr feature/1-merged MERGED "$tip"
tip=$(branch fix/2-dirty yes);        pr fix/2-dirty MERGED "$tip"; echo dirt > "$work/wt/fix-2-dirty/f"
tip=$(branch chore/open-one yes);     pr chore/open-one OPEN "$tip"
tip=$(branch fix/3-unpushed yes);     pr fix/3-unpushed MERGED "$tip"
echo more > "$work/wt/fix-3-unpushed/g"; git -C "$work/wt/fix-3-unpushed" add g; git -C "$work/wt/fix-3-unpushed" commit -q -m more
tip=$(branch feature/4-closed yes);   pr feature/4-closed CLOSED "$tip"
tip=$(branch chore/nopr no)
tip=$(branch chore/remote-only yes);  pr chore/remote-only MERGED "$tip"
git worktree remove "$work/wt/chore-remote-only" && git branch -q -D chore/remote-only
git push -q origin main:refs/heads/Bad_Name

has_local() { git show-ref --verify --quiet "refs/heads/$1"; }
has_remote() { git ls-remote --exit-code --heads origin "$1" >/dev/null 2>&1; }

out=$(bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "dry run succeeds" "[[ $st == 0 ]]"
check "dry run changes nothing" "has_local feature/1-merged && [[ -d $work/wt/feature-1-merged ]] && has_remote chore/remote-only"
check "dry run says what it would do" "grep -q 'remove feature/1-merged' <<<\"\$out\""

( cd "$work/wt/feature-1-merged" && bash "$repo/scripts/cleanup.sh" >/dev/null 2>&1 ); check "refuses in a linked worktree" "[[ \$? != 0 ]]"
git checkout -q -b chore/elsewhere; bash scripts/cleanup.sh >/dev/null 2>&1; check "refuses off main" "[[ \$? != 0 ]]"; git checkout -q main; git branch -q -D chore/elsewhere

# shellcheck disable=SC2034 # out is read by the eval in check
out=$(bash scripts/cleanup.sh 2>&1); st=$?
check "runs" "[[ $st == 0 ]]"
check "removes a merged branch, its worktree and its origin branch" "! has_local feature/1-merged && [[ ! -d $work/wt/feature-1-merged ]] && ! has_remote feature/1-merged"
check "removes a merged branch that is only on origin" "! has_remote chore/remote-only"
check "keeps a merged branch whose worktree is dirty" "has_local fix/2-dirty && [[ -d $work/wt/fix-2-dirty ]]"
check "keeps a branch with an open PR" "has_local chore/open-one && has_remote chore/open-one"
check "keeps a branch with a commit the PR never had" "has_local fix/3-unpushed"
check "keeps a closed-unmerged branch by default" "has_local feature/4-closed && has_remote feature/4-closed"
check "keeps a branch with no PR" "has_local chore/nopr"
check "leaves a non-standard origin branch alone" "has_remote Bad_Name"
check "says why it kept one" "grep -q 'keep   fix/2-dirty: its worktree has uncommitted changes' <<<\"\$out\""

bash scripts/cleanup.sh --include-closed >/dev/null 2>&1
check "--include-closed removes a closed-unmerged branch" "! has_local feature/4-closed && ! has_remote feature/4-closed"

echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
