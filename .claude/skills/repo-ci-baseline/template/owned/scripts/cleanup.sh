#!/usr/bin/env bash
# Cleans up after merged work: run it in the primary checkout, on main, with a clean tree.
#
#   scripts/cleanup.sh [--dry-run] [--include-closed]
#
# Fetches and prunes origin, fast-forwards main to origin/main, then, for every branch that follows the branch standard (check-branch-name.sh),
# local and on origin, decides from the PR's state on GitHub, not from git ancestry (a squash merge leaves no ancestry to see). Only a PR whose head
# repository is the one origin points at counts (owner/name, compared without regard to case; origin's slug is read from a github.com URL, else
# the repository from GITHUB_REPOSITORY or the upstream remote is used, which a GitHub Enterprise origin should set), so a fork's PR of the same name cannot stand in for it:
#   - removed: the PR merged into main and the branch's tip is the PR's last head, so nothing unpushed is lost. The branch's worktree is removed
#     first, and only when it is clean (ignored files in it go with it). When the local branch is kept, its origin copy is kept too; an origin branch
#     alone is kept when it has moved or its tip is not the PR's last head. The origin branch is deleted only if it still holds the tip that was judged; one already gone by then (GitHub's auto-delete) counts as removed.
#   - removed with --include-closed only: the PR closed unmerged and the tip is its last head.
#   - kept, with the reason: a name outside the branch standard, an open PR, no PR or a tip no PR had, 100 or more PRs of that branch name, a PR merged into a branch
#     other than main, a dirty or unreadable worktree, a worktree whose folder can't be reached, a commit made after it was judged, a step git refused (a branch a worktree
#     is rebasing or bisecting included), or a closed PR without --include-closed.
# A worktree whose folder was deleted by hand has git's entry for it forgotten (only that one, and only if it is a real directory in .git/worktrees; a locked one is kept), when its
# parent folder is still there, searchable and not empty. A parent that is missing, empty or unsearchable may be an unmounted drive (its mount point is left empty), so the branch is
# kept, with the hint to run git worktree prune if the folder was deleted for good. --dry-run prints what would happen and changes no branch, worktree or commit (it still
# fetches and prunes the remote-tracking refs).
# Exits 1 when it can't run (not main, not the primary checkout, dirty main, main can't fast-forward, origin can't be fetched, no gh, no repository,
# a gh lookup failed, a bad argument, no scripts/check-branch-name.sh) and, after finishing the rest, when a removal git refused, a check of origin
# failed or the closing fetch failed. A branch is judged by its PR, and at most 100 PRs are read per branch name: 100 or more keeps the branch.
# A worktree on a detached HEAD belongs to no branch and is never touched.
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

# slug_of <url> -> owner/name of a github.com URL (https, ssh://, git@github.com:o/r, with a port, userinfo, .git and a trailing /), else nothing.
slug_of() {
  local u="${1%/}"
  u="${u%.git}"; u="${u%/}"
  if [[ "$u" =~ ^((ssh|https?|git)://)?([^@/[:space:]]+@)?github\.com(:[0-9]+)?[:/]([^/[:space:]]+/[^/[:space:]]+)$ ]]; then printf '%s' "${BASH_REMATCH[5]}"; fi
}
url="$(git remote get-url upstream 2>/dev/null || git remote get-url origin 2>/dev/null || true)"
repo="${GITHUB_REPOSITORY:-}"
if [[ -z "$repo" ]]; then repo="$(slug_of "$url")"; fi
[[ "$repo" =~ ^[^/[:space:]]+/[^/[:space:]]+$ ]] || die "can't tell which repository: set GITHUB_REPOSITORY or add a github.com upstream or origin remote"
export GH_REPO="${repo:?}"
# A PR counts only when its head repository is the one origin points at: origin's owner/name when it is a github.com URL, else GH_REPO's.
lc() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
origin_slug="$(slug_of "$(git remote get-url origin 2>/dev/null || true)")"
origin_slug="$(lc "${origin_slug:-$repo}")"

# resolve_path <dir> <path>: <path> made absolute against <dir>, with . and .. collapsed (text only: the folder may be gone, so no symlinks are followed).
resolve_path() {
  local p="$2" out="" seg rest
  [[ "$p" == /* ]] || p="$1/$p"
  rest="$p/"
  while [[ -n "$rest" ]]; do
    seg="${rest%%/*}"; rest="${rest#*/}"
    case "$seg" in ''|.) ;; ..) out="${out%/*}" ;; *) out="$out/$seg" ;; esac
  done
  printf '%s' "${out:-/}"
}
# stale_entry <path>: prints git's admin directory for the worktree at <path> (whose folder was deleted by hand). Git writes its gitdir file as an absolute
# path, or relative to the admin directory with worktree.useRelativePaths. Returns 1 when there is none.
stale_entry() {
  local common d g
  common="$(cd "$(git rev-parse --git-common-dir)" && pwd -P)"
  for d in "$common"/worktrees/*/; do
    [[ -d "$d" && ! -L "${d%/}" && -f "${d}gitdir" ]] || continue # a real directory only: never follow a symlink out of .git/worktrees
    g="$(<"${d}gitdir")"
    if [[ "$(resolve_path "${d%/}" "$g")" == "$(resolve_path / "$1")/.git" ]]; then printf '%s' "$d"; return 0; fi
  done
  return 1
}

run() { if [[ "$dry" == true ]]; then echo "   would: $*"; else "$@"; fi; }

git fetch --prune --quiet origin || die "can't fetch origin"
if [[ "$dry" == true ]]; then
  echo "   would: git merge --ff-only origin/main"
else
  git merge --ff-only --quiet origin/main || die "main has commits origin/main lacks, so it can't fast-forward: resolve them first"
fi

# verdict <branch> <tip-sha> -> merged | merged-other:<base> | closed | open | capped | none. Exits 1 (the caller's $(...) fails) when gh does.
verdict() {
  local branch="$1" sha="$2" state oid base head prs seen=none rows=0
  prs="$(gh pr list --state all --limit 100 --head "$branch" --json state,headRefOid,baseRefName,headRepository \
    --jq '.[] | "\(.state) \(.headRefOid) \(.baseRefName) \(.headRepository.nameWithOwner)"')" \
    || { echo "❌ gh pr list failed for $branch" >&2; exit 1; }
  [[ -z "$prs" ]] || rows="$(grep -c '' <<<"$prs")"
  if [[ "$rows" -ge 100 ]]; then echo capped; return; fi # --limit 100 may have cut the branch's own PR off: it can't be judged
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
    capped) echo "100 or more PRs share this branch name, so its own can't be told apart" ;;
    *) echo "no PR from origin's repository has this tip, or no PR" ;;
  esac
}

removed=0; kept=0; failed=0; kept_local=" "
keep() { echo "keep   $1: $2"; kept=$((kept + 1)); }

# Local branches
while read -r ref; do
  b="${ref#refs/heads/}"
  [[ -n "$b" && "$b" != main ]] || continue
  bash "$check_name" "$b" >/dev/null 2>&1 || { keep "$b" "not a standard branch name"; continue; }
  tip="$(git rev-parse "$ref")"
  d="$(decide "$b" "$tip")" || exit 1
  if [[ "$d" != removable ]]; then keep "$b" "$d"; kept_local+="$b "; continue; fi
  wt="$(git worktree list --porcelain | awk -v ref="$ref" '/^worktree /{p=substr($0,10)} $0=="branch " ref{print p}')"
  stale=""
  if [[ -n "$wt" && ! -e "$wt" ]]; then
    # Deleted by hand, or out of reach? An unmounted drive leaves its mount point behind: empty. A parent that is missing, empty or unsearchable can't tell the two apart, so it is kept.
    p="$(dirname "$wt")"
    if [[ ! -d "$p" || ! -x "$p" || -z "$(ls -A "$p" 2>/dev/null)" ]]; then
      keep "$b" "its worktree's folder can't be reached ($wt): its parent folder is missing, empty or unsearchable, so it may be on an unmounted drive. If you deleted it, run git worktree prune, then run this again"
      kept_local+="$b "; continue
    fi
    stale="$wt"; wt="" # only git's stale entry for it is left
  fi
  if [[ -n "$wt" ]]; then
    if ! st="$(git -C "$wt" status --porcelain 2>/dev/null)"; then keep "$b" "its worktree can't be read ($wt)"; kept_local+="$b "; continue; fi
    if [[ -n "$st" ]]; then keep "$b" "its worktree has uncommitted changes"; kept_local+="$b "; continue; fi
  fi
  if [[ -n "$stale" ]]; then
    if ! entry="$(stale_entry "$stale")"; then keep "$b" "its worktree's folder is missing and git's entry for it was not found ($stale): git worktree prune"; kept_local+="$b "; continue; fi
    if [[ -e "${entry}locked" ]]; then keep "$b" "its worktree is locked and its folder is missing: git worktree unlock '$stale', then run this again"; kept_local+="$b "; continue; fi
    if [[ "$dry" == true ]]; then echo "   would: forget the missing worktree $stale"; else command rm -rf -- "$entry"; fi
  fi
  echo "remove $b${wt:+ (worktree $wt)}"
  if [[ -n "$wt" ]] && ! run git worktree remove "$wt"; then keep "$b" "git could not remove its worktree"; kept_local+="$b "; failed=1; continue; fi
  # The branch goes only if it still holds the tip that was judged (a commit made since stays, as the origin branch's lease keeps one), and git branch -D itself
  # refuses a branch a worktree has checked out or is rebasing or bisecting, which the worktree list shows as detached.
  if [[ "$dry" != true && "$(git rev-parse "$ref" 2>/dev/null || true)" != "$tip" ]]; then
    keep "$b" "it has a commit that was made after it was judged"; kept_local+="$b "; failed=1; continue
  fi
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
    # GitHub may have deleted it since the fetch (auto-delete of merged branches): then it is gone, which is what was wanted. Only ls-remote's
    # status 2 (no such ref) says so; a branch still there, or a check that failed, is kept.
    rc=0; git ls-remote --exit-code --heads origin "refs/heads/$b" >/dev/null 2>&1 || rc=$?
    if [[ "$rc" == 0 ]]; then keep "origin/$b" "git could not delete it (it may have moved since the fetch)"; failed=1; continue
    elif [[ "$rc" != 2 ]]; then keep "origin/$b" "git could not delete it, and origin could not be checked"; failed=1; continue; fi
  fi
  removed=$((removed + 1))
done < <(git for-each-ref --format='%(refname)' refs/remotes/origin)

if [[ "$dry" != true ]] && ! git fetch --prune --quiet origin; then echo "❌ the closing fetch of origin failed" >&2; failed=1; fi
echo "Done: $removed removed, $kept kept$([[ "$dry" == true ]] && echo " (dry run)")."
[[ "$failed" == 0 ]] || exit 1
