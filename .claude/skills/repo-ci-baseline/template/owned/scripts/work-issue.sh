#!/usr/bin/env bash
# Starts work on an issue: makes its branch and worktree from origin/main and prints the worktree's path.
#
#   scripts/work-issue.sh <issue-number>
#   cd "$(scripts/work-issue.sh 54)"
#
# The branch is fix/{n}-{slug} for an issue labelled bug, feature/{n}-{slug} for any other, where the slug is the issue's title in lowercase
# words (a leading "[Feature]" tag dropped, at most 6 words), and the folder is the branch with / as -, under ../<Repo>-worktrees/.
# Run it from the primary checkout or any worktree. An existing worktree for the branch is reused. Everything but the path goes to stderr.
set -euo pipefail

n="${1:-}"
[[ "$n" =~ ^[0-9]+$ ]] || { echo "usage: scripts/work-issue.sh <issue-number>" >&2; exit 1; }
command -v gh >/dev/null || { echo "❌ gh is needed to read the issue" >&2; exit 1; } # gh-repo: ok

primary="$(dirname "$(cd "$(git rev-parse --git-common-dir)" && pwd)")"
repo="$(basename "$primary")"

url="$(git remote get-url upstream 2>/dev/null || git remote get-url origin 2>/dev/null || true)"
url="${url%.git}"
repo_slug="${GITHUB_REPOSITORY:-}"
if [[ -z "$repo_slug" && -n "$url" ]]; then repo_slug="${url#*github.com[:/]}"; fi
[[ "$repo_slug" =~ ^[^/[:space:]]+/[^/[:space:]]+$ ]] || { echo "❌ can't tell which repository: set GITHUB_REPOSITORY or add an origin remote" >&2; exit 1; }
export GH_REPO="${repo_slug:?}"

title="$(gh issue view "$n" --json title --jq .title)"
labels="$(gh issue view "$n" --json labels --jq '.labels[].name')"
type=feature
grep -qx bug <<<"$labels" && type=fix

slug="$(sed -E 's/^\[[^]]*\][[:space:]]*//' <<<"$title" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9\n' ' ' | tr -s ' ' | sed -E 's/^ //; s/ $//' | cut -d' ' -f1-6 | tr ' ' '-')"
[[ -n "$slug" ]] || { echo "❌ issue #$n has no title to make a slug from" >&2; exit 1; }
branch="$type/$n-$slug"
bash "$primary/scripts/check-branch-name.sh" "$branch" >&2 || { echo "❌ $branch is not an allowed branch name" >&2; exit 1; }

dir="$(dirname "$primary")/$repo-worktrees/${branch//\//-}"
if [[ -d "$dir" ]]; then
  echo "reusing $dir" >&2
else
  git -C "$primary" fetch --quiet origin
  mkdir -p "$(dirname "$dir")"
  if git -C "$primary" show-ref --verify --quiet "refs/heads/$branch"; then
    git -C "$primary" worktree add --quiet "$dir" "$branch" >&2
  else
    git -C "$primary" worktree add --quiet -b "$branch" "$dir" origin/main >&2
  fi
  echo "made $branch in $dir" >&2
fi
echo "$dir"
