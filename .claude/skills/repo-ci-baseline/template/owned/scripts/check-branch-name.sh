#!/usr/bin/env bash
# Checks a branch name against the branch standard shared by every repo.
#
#   scripts/check-branch-name.sh <branch>
#   scripts/check-branch-name.sh --pr <branch>
#
# The pre-push hook runs the first form on each pushed branch. ci.yml's
# "Branch name" job runs the second on a PR's head branch, which also accepts
# the branches Actions and Dependabot create, since no hook ever sees them.
# The pre-push hook also lets a person push to a Dependabot branch (see below).
# CI is what catches a push that skipped the hook: a Claude cloud session's
# claude/... branch, GitHub's web editor, or a clone without core.hooksPath.
# Exits 0 when the name is allowed, 1 (with the reason) when it isn't.
set -euo pipefail

pr=false
if [[ "${1:-}" == "--pr" ]]; then
  pr=true
  shift
fi
branch="${1:-}"

# feature/, fix/ and hotfix/ name their issue; chore/ is for work without one,
# so its slug starts with a letter and can't pass for an issue number. Slugs
# are lowercase letters and digits in words joined by '-'.
if [[ "$branch" =~ ^(feature|fix|hotfix)/[0-9]+-[a-z0-9]+(-[a-z0-9]+)*$ ]] \
  || [[ "$branch" =~ ^chore/[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]]; then
  exit 0
fi

# Dependabot's branches can't be renamed, and a person pushes to one to fix its PR (mirroring a bump into the Template's file, say),
# so the hook allows them too, in shell-safe characters only ('@' and '+' included, which a scoped package or a version can carry).
# Sandcastle hands off no such branch (lib/branches.mts#isHandOffBranch). The --pr form below takes any dependabot/* name, as it did
# before the hook did: CI quotes the name, and refusing a Dependabot branch for a character would block its PR.
if [[ "$branch" =~ ^dependabot/[A-Za-z0-9._@+-]+(/[A-Za-z0-9._@+-]+)*$ ]]; then
  exit 0
fi

# Made by release.yml, backfill-blog-posts.yml and Dependabot.
if [[ "$pr" == true ]] \
  && [[ "$branch" == docs/release-notes || "$branch" == docs/backfill-blog-posts || "$branch" == dependabot/* ]]; then
  exit 0
fi

echo "Branch name '${branch}' does not match the branch standard."
echo "Expected: feature/{issue}-{slug}, fix/{issue}-{slug}, hotfix/{issue}-{slug}, or chore/{slug}"
exit 1
