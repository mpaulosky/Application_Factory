#!/usr/bin/env bash
# Cleans up after merged work: run it in the primary checkout, on main, with a clean tree.
#
#   scripts/cleanup.sh [--dry-run] [--include-closed]
#
# Fetches and prunes origin, fast-forwards main to origin/main, then, for every branch that follows the branch standard (check-branch-name.sh),
# local and on origin, decides from the PR's state on GitHub, not from git ancestry (a squash merge leaves no ancestry to see). Only a PR whose head
# repository is the one origin points at counts (owner/name, compared without regard to case; origin's slug is read from a github.com URL, else
# GH_REPO's is used), so a fork's PR of the same name cannot stand in for it:
#   - removed: the PR merged into main and the branch's tip is the PR's last head, so nothing unpushed is lost. The branch's worktree is removed
#     first, and only when it is clean (ignored files in it go with it). When the local branch is kept, its origin copy is kept too; an origin branch
#     alone is kept when it has moved or its tip is not the PR's last head. The origin branch is deleted only if it still holds the tip that was judged.
#   - removed with --include-closed only: the PR closed unmerged and the tip is its last head.
#   - kept, with the reason: a name outside the branch standard, an open PR, no PR or a tip no PR had, a PR merged into a branch other than main,
#     a dirty or unreadable worktree, a step git refused, or a closed PR without --include-closed.
# A worktree whose folder was deleted by hand is pruned first. --dry-run prints what would happen and changes no branch, worktree or commit (it still
# fetches and prunes the remote-tracking refs).
# Exits 1 when it can't run (not main, not the primary checkout, dirty main, main can't fast-forward, no gh, no repository, a gh lookup failed,
# a bad argument, no scripts/check-branch-name.sh) and, after finishing the rest, when a removal git refused.
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

[[ -f "$check_name" ]] || die "scripts/check-branch-name.sh is missing: Apply the Template first"
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
# A PR counts only when its head repository is the one origin points at: origin's owner/name when it is a github.com URL, else GH_REPO's.
lc() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
origin_url="$(git remote get-url origin 2>/dev/null || true)"
origin_url="${origin_url%.git}"
origin_url="${origin_url%/}"
origin_slug="$repo"
if [[ "$origin_url" == *github.com[:/]* && "${origin_url#*github.com[:/]}" =~ ^[^/[:space:]]+/[^/[:space:]]+$ ]]; then origin_slug="${origin_url#*github.com[:/]}"; fi
origin_slug="$(lc "$origin_slug")"

run() { if [[ "$dry" == true ]]; then echo "   would: $*"; else "$@"; fi; }

git fetch --prune --quiet origin
if [[ "$dry" == true ]]; then
  echo "   would: git merge --ff-only origin/main"
else
  git merge --ff-only --quiet origin/main || die "main has commits origin/main lacks, so it can't fast-forward: resolve them first"
fi

# verdict <branch> <tip-sha> -> merged | merged-other:<base> | closed | open | none. Exits 1 (the caller's $(...) fails) when gh does.
verdict() {
  local branch="$1" sha="$2" state oid base head prs seen=none
  prs="$(gh pr list --state all --limit 100 --head "$branch" --json state,headRefOid,baseRefName,headRepository \
    --jq '.[] | "\(.state) \(.headRefOid) \(.baseRefName) \(.headRepository.nameWithOwner)"')" \
    || { echo "❌ gh pr list failed for $branch" >&2; exit 1; }
  while read -r state oid base head; do
    [[ -n "$state" && "$(lc "$head")" == "$origin_slug" ]] || continue
    if [[ "$state" == OPEN ]]; then echo open; return; fi
    if [[ "$state" == MERGED && "$oid" == "$sha" ]]; then
      if [[ "$base" == main ]]; then seen=merged; elif [[ "$seen" != merged ]]; then seen="merged-other:$base"; fi
    elif [[ "$state" == CLOSED && "$oid" == "$sha" && "$seen" == none ]]; then seen=closed; fi
  done <<<"$prs"
  echo "$seen"
}

# decide <branch> <tip-sha> -> removable | the reason it is kept
decide() {
  local v
  v="$(verdict "$1" "$2")" || exit 1
  case "$v" in
    merged) echo removable ;;
    merged-other:*) echo "its PR merged into ${v#merged-other:}, not main" ;;
    closed) if [[ "$closed" == true ]]; then echo removable; else echo "its PR closed unmerged (use --include-closed)"; fi ;;
    open) echo "its PR is open" ;;
    *) echo "no PR from origin's repository has this tip, or no PR" ;;
  esac
}

run git worktree prune
removed=0; kept=0; failed=0; kept_local=" "
keep() { echo "keep   $1: $2"; kept=$((kept + 1)); }

# Local branches
while read -r ref; do
  b="${ref#refs/heads/}"
  [[ -n "$b" && "$b" != main ]] || continue
  bash "$check_name" "$b" >/dev/null 2>&1 || { keep "$b" "not a standard branch name"; continue; }
  d="$(decide "$b" "$(git rev-parse "$ref")")" || exit 1
  if [[ "$d" != removable ]]; then keep "$b" "$d"; kept_local+="$b "; continue; fi
  wt="$(git worktree list --porcelain | awk -v ref="$ref" '/^worktree /{p=substr($0,10)} $0=="branch " ref{print p}')"
  if [[ -n "$wt" && ! -d "$wt" ]]; then wt=""; fi # a folder deleted by hand: stale metadata, which the prune above removes
  if [[ -n "$wt" ]]; then
    if ! st="$(git -C "$wt" status --porcelain 2>/dev/null)"; then keep "$b" "its worktree can't be read ($wt)"; kept_local+="$b "; continue; fi
    if [[ -n "$st" ]]; then keep "$b" "its worktree has uncommitted changes"; kept_local+="$b "; continue; fi
  fi
  echo "remove $b${wt:+ (worktree $wt)}"
  if [[ -n "$wt" ]] && ! run git worktree remove "$wt"; then keep "$b" "git could not remove its worktree"; kept_local+="$b "; failed=1; continue; fi
  if ! run git branch -q -D "$b"; then keep "$b" "git could not delete the branch"; kept_local+="$b "; failed=1; continue; fi
  removed=$((removed + 1))
done < <(git for-each-ref --format='%(refname)' refs/heads)

# Branches left on origin
while read -r ref; do
  b="${ref#refs/remotes/origin/}"
  [[ -n "$b" && "$b" != HEAD && "$b" != main ]] || continue
  bash "$check_name" "$b" >/dev/null 2>&1 || { keep "origin/$b" "not a standard branch name"; continue; }
  if [[ "$kept_local" == *" $b "* ]]; then keep "origin/$b" "its local branch was kept"; continue; fi
  sha="$(git rev-parse "$ref")"
  d="$(decide "$b" "$sha")" || exit 1
  if [[ "$d" != removable ]]; then keep "origin/$b" "$d"; continue; fi
  echo "remove origin/$b"
  # The lease deletes the branch only if origin still holds the tip that was judged.
  if ! run git push --quiet --force-with-lease="refs/heads/$b:$sha" origin ":refs/heads/$b"; then
    # GitHub may have deleted it since the fetch (auto-delete of merged branches): then it is gone, which is what was wanted.
    if git ls-remote --exit-code --heads origin "$b" >/dev/null 2>&1; then
      keep "origin/$b" "git could not delete it (it may have moved since the fetch)"; failed=1; continue
    fi
  fi
  removed=$((removed + 1))
done < <(git for-each-ref --format='%(refname)' refs/remotes/origin)

run git worktree prune
[[ "$dry" == true ]] || git fetch --prune --quiet origin
echo "Done: $removed removed, $kept kept$([[ "$dry" == true ]] && echo " (dry run)")."
[[ "$failed" == 0 ]] || exit 1
