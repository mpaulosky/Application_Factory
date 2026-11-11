#!/usr/bin/env bash
# Cleans up after merged work: run it in the primary checkout, on main.
#
#   scripts/cleanup.sh [--dry-run] [--include-closed]
#
# Fetches and prunes origin, rebases main onto origin/main, then, for every branch that follows the branch standard (check-branch-name.sh),
# local and on origin, decides from the PR's state on GitHub, not from git ancestry (a squash merge leaves no ancestry to see):
#   - removed: the PR merged and the branch's tip is the PR's last head, so nothing unpushed is lost. The branch's worktree is removed first,
#     and only when it is clean. Both the local branch and the origin branch go.
#   - removed with --include-closed only: the PR closed unmerged and the tip is its last head.
#   - kept, with the reason: an open PR, no PR, a tip the PR never had, a dirty worktree, or a closed PR without --include-closed.
# --dry-run prints what would happen and changes nothing. Exits 1 when it can't run (not main, not the primary checkout, dirty main, no gh).
set -euo pipefail

dry=false
closed=false
for a in "$@"; do
  case "$a" in
    --dry-run) dry=true ;;
    --include-closed) closed=true ;;
    *) echo "usage: scripts/cleanup.sh [--dry-run] [--include-closed]" >&2; exit 1 ;;
  esac
done

die() { echo "❌ $*" >&2; exit 1; }
root="$(git rev-parse --show-toplevel)"
cd "$root"
check_name="$root/scripts/check-branch-name.sh"

[[ "$(git rev-parse --git-dir)" == "$(git rev-parse --git-common-dir)" ]] || die "run this in the primary checkout, not a linked worktree"
[[ "$(git branch --show-current)" == "main" ]] || die "run this on main"
[[ -z "$(git status --porcelain)" ]] || die "main has uncommitted changes"
command -v gh >/dev/null || die "gh is needed to read PR states" # gh-repo: ok
url="$(git remote get-url upstream 2>/dev/null || git remote get-url origin 2>/dev/null || true)"
url="${url%.git}"
repo="${GITHUB_REPOSITORY:-}"
if [[ -z "$repo" && -n "$url" ]]; then repo="${url#*github.com[:/]}"; fi
[[ "$repo" =~ ^[^/[:space:]]+/[^/[:space:]]+$ ]] || die "can't tell which repository: set GITHUB_REPOSITORY or add an origin remote"
export GH_REPO="${repo:?}"

run() { if [[ "$dry" == true ]]; then echo "   would: $*"; else "$@"; fi; }

git fetch --prune --quiet origin
if [[ "$dry" == true ]]; then echo "   would: git rebase origin/main"; else git rebase --quiet origin/main; fi

# verdict <branch> <tip-sha> -> merged | closed | open | none
verdict() {
  local branch="$1" sha="$2" state oid seen=none
  while read -r state oid; do
    [[ -n "$state" ]] || continue
    if [[ "$state" == OPEN ]]; then echo open; return; fi
    if [[ "$state" == MERGED && "$oid" == "$sha" ]]; then seen=merged
    elif [[ "$state" == CLOSED && "$oid" == "$sha" && "$seen" == none ]]; then seen=closed; fi
  done < <(gh pr list --state all --head "$branch" --json state,headRefOid --jq '.[] | "\(.state) \(.headRefOid)"')
  echo "$seen"
}

# decide <branch> <sha> -> removable | the reason it is kept
decide() {
  local v; v="$(verdict "$1" "$2")"
  case "$v" in
    merged) echo removable ;;
    closed) if [[ "$closed" == true ]]; then echo removable; else echo "its PR closed unmerged (use --include-closed)"; fi ;;
    open) echo "its PR is open" ;;
    *) echo "no PR has this tip, or no PR" ;;
  esac
}

removed=0; kept=0
keep() { echo "keep   $1: $2"; kept=$((kept + 1)); }

# Local branches
while read -r b; do
  [[ -n "$b" && "$b" != main ]] || continue
  bash "$check_name" "$b" >/dev/null 2>&1 || { keep "$b" "not a standard branch name"; continue; }
  d="$(decide "$b" "$(git rev-parse "refs/heads/$b")")"
  if [[ "$d" != removable ]]; then keep "$b" "$d"; continue; fi
  wt="$(git worktree list --porcelain | awk -v ref="refs/heads/$b" '/^worktree /{p=substr($0,10)} $0=="branch " ref{print p}')"
  if [[ -n "$wt" && -n "$(git -C "$wt" status --porcelain)" ]]; then keep "$b" "its worktree has uncommitted changes"; continue; fi
  echo "remove $b${wt:+ (worktree $wt)}"
  [[ -z "$wt" ]] || run git worktree remove "$wt"
  run git branch -D "$b" >/dev/null
  removed=$((removed + 1))
done < <(git for-each-ref --format='%(refname:short)' refs/heads)

# Branches left on origin
while read -r r; do
  b="${r#origin/}"
  [[ "$b" != HEAD && "$b" != main && -n "$b" ]] || continue
  bash "$check_name" "$b" >/dev/null 2>&1 || { keep "origin/$b" "not a standard branch name"; continue; }
  d="$(decide "$b" "$(git rev-parse "refs/remotes/origin/$b")")"
  if [[ "$d" != removable ]]; then keep "origin/$b" "$d"; continue; fi
  echo "remove origin/$b"
  run git push --quiet origin --delete "$b"
  removed=$((removed + 1))
done < <(git for-each-ref --format='%(refname:short)' refs/remotes/origin)

run git worktree prune
[[ "$dry" == true ]] || git fetch --prune --quiet origin
echo "Done: $removed removed, $kept kept$([[ "$dry" == true ]] && echo " (dry run)")."
