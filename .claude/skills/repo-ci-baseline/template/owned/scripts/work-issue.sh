#!/usr/bin/env bash
# Starts work on an issue: makes its branch and worktree and prints the worktree's path.
#
#   scripts/work-issue.sh <issue-number>
#   cd "$(scripts/work-issue.sh 54)"
#
# The branch is fix/{n}-{slug} for an issue labelled bug (any case), feature/{n}-{slug} for any other. The slug is the issue's title in lowercase
# a-z0-9 words (a leading [tag] dropped, other characters, non-ASCII ones included, read as word breaks, at most 6 words and 50 characters; "issue"
# when none is left), and the folder is the branch with / as -, under ../<Repo>-worktrees/. A leading-zero number is read as the same number (054 is 54).
# The branch starts from origin/main, or from origin/<branch> when that branch is already on origin. A worktree the branch already has is reused,
# wherever it is, if its folder still exists (git's entry for one deleted by hand is forgotten, that one only, and the worktree made again; a locked
# one is an error); a directory in the way that is not that worktree is an error. An issue number has at most 9 digits. Run it from the primary checkout or any worktree.
# Everything but the path goes to stderr.
set -euo pipefail

die() { echo "❌ $*" >&2; exit 1; }
n="${1:-}"
[[ "$n" =~ ^[0-9]{1,9}$ ]] || { echo "usage: scripts/work-issue.sh <issue-number>" >&2; exit 1; }
n=$((10#$n))
command -v gh >/dev/null || die "gh is needed to read the issue" # gh-repo: ok

primary="$(git worktree list --porcelain | awk '/^worktree / && !seen {print substr($0,10); seen=1}')"
[[ -n "$primary" ]] || die "can't find the primary checkout"
repo="$(basename "$primary")"
[[ -f "$primary/scripts/check-branch-name.sh" ]] || die "scripts/check-branch-name.sh is missing from $primary: Apply the Template first"

# slug_of <url> -> owner/name of a github.com URL (https, ssh://, git@github.com:o/r, with a port, userinfo, .git and a trailing /), else nothing.
slug_of() {
  local u="${1%/}"
  u="${u%.git}"; u="${u%/}"
  if [[ "$u" =~ ^((ssh|https?|git)://)?([^@/[:space:]]+@)?github\.com(:[0-9]+)?[:/]([^/[:space:]]+/[^/[:space:]]+)$ ]]; then printf '%s' "${BASH_REMATCH[5]}"; fi
}
url="$(git remote get-url upstream 2>/dev/null || git remote get-url origin 2>/dev/null || true)"
repo_slug="${GITHUB_REPOSITORY:-}"
if [[ -z "$repo_slug" ]]; then repo_slug="$(slug_of "$url")"; fi
[[ "$repo_slug" =~ ^[^/[:space:]]+/[^/[:space:]]+$ ]] || die "can't tell which repository: set GITHUB_REPOSITORY or add a github.com origin remote"
export GH_REPO="${repo_slug:?}"

title="$(gh issue view "$n" --json title --jq .title)"
labels="$(gh issue view "$n" --json labels --jq '.labels[].name')"
[[ -n "$title" ]] || die "issue #$n has no title"
type=feature
grep -qix bug <<<"$labels" && type=fix

slug="$(sed -E 's/^\[[^]]*\][[:space:]]*//' <<<"$title" | tr '[:upper:]' '[:lower:]' | LC_ALL=C tr -c 'a-z0-9\n' ' ' | tr -s ' ' | sed -E 's/^ //; s/ $//' | cut -d' ' -f1-6 | tr ' ' '-' | cut -c1-50 | sed -E 's/-+$//')"
slug="${slug:-issue}"
branch="$type/$n-$slug"
bash "$primary/scripts/check-branch-name.sh" "$branch" >&2 || die "$branch is not an allowed branch name"

dir="$(dirname "$primary")/$repo-worktrees/${branch//\//-}"
existing="$(git -C "$primary" worktree list --porcelain | awk -v ref="refs/heads/$branch" '/^worktree /{p=substr($0,10)} $0=="branch " ref{print p}')"
if [[ -n "$existing" && -d "$existing" ]]; then
  echo "reusing $existing" >&2
  echo "$existing"
  exit 0
fi
# A worktree whose folder was deleted by hand is stale: forget git's entry for it (this one only), then make the worktree again.
if [[ -n "$existing" ]]; then
  common="$(cd "$primary" && cd "$(git rev-parse --git-common-dir)" && pwd)"
  for d in "$common"/worktrees/*/; do
    [[ -f "${d}gitdir" && "$(<"${d}gitdir")" == "$existing/.git" ]] || continue
    [[ ! -e "${d}locked" ]] || die "$existing is a missing but locked worktree of $branch: run git worktree unlock '$existing', then try again"
    command rm -rf -- "$d"
  done
fi
[[ ! -e "$dir" ]] || die "$dir exists but is not a worktree of $branch: move it away first"

git -C "$primary" fetch --quiet origin
mkdir -p "$(dirname "$dir")"
if git -C "$primary" show-ref --verify --quiet "refs/heads/$branch"; then
  git -C "$primary" worktree add --quiet "$dir" "$branch" >&2
elif git -C "$primary" show-ref --verify --quiet "refs/remotes/origin/$branch"; then
  git -C "$primary" worktree add --quiet -b "$branch" "$dir" "origin/$branch" >&2
else
  git -C "$primary" worktree add --quiet -b "$branch" "$dir" origin/main >&2
fi
echo "made $branch in $dir" >&2
echo "$dir"
