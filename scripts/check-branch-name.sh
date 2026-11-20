#!/usr/bin/env bash
# Checks a branch name against the branch standard shared by every repo.
#
#   scripts/check-branch-name.sh <branch>
#   scripts/check-branch-name.sh --pr [--author <login>] <branch>
#
# The pre-push hook runs the first form on each pushed branch. ci.yml's
# "Branch name" job runs the second on a PR's head branch, with the PR's
# author. The --pr form additionally accepts docs/release-notes and
# docs/backfill-blog-posts, which Actions create and no hook sees, and any
# dependabot/* name when the author is dependabot[bot]. Every other
# dependabot/* name, hook or --pr, must be shell-safe (see below).
# CI is what catches a push that skipped the hook: a Claude cloud session's
# claude/... branch, GitHub's web editor, or a clone without core.hooksPath.
# Exits 0 when the name is allowed, 1 (with the reason) when it isn't.
set -euo pipefail
# Check in ASCII whatever the caller's locale: in some UTF-8 locales bash's [a-z] and [A-Za-z] also match an accented letter, so the hook, CI and
# the scripts that call this one (cleanup.sh, work-issue.sh) would disagree on a name like fix/1-café.
export LC_ALL=C

pr=false
author=""
if [[ "${1:-}" == "--pr" ]]; then
  pr=true
  shift
  if [[ "${1:-}" == "--author" ]]; then
    author="${2:-}"
    shift 2 || shift $#
  fi
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
# so the hook allows them too, in shell-safe ASCII characters only ('@' and '+' included, which a scoped package or a version can carry).
# Sandcastle hands off no such branch (lib/branches.mts#isHandOffBranch). The --pr form below takes any other dependabot/* name only
# from dependabot[bot] itself, whose branch can't be renamed: a person's PR from such a name must be shell-safe like the hook's.
if [[ "$branch" =~ ^dependabot/[A-Za-z0-9._@+-]+(/[A-Za-z0-9._@+-]+)*$ ]]; then
  exit 0
fi

# Made by release.yml, backfill-blog-posts.yml and Dependabot.
if [[ "$pr" == true ]] \
  && [[ "$branch" == docs/release-notes || "$branch" == docs/backfill-blog-posts ||
    ("$author" == "dependabot[bot]" && "$branch" == dependabot/*) ]]; then
  exit 0
fi

# A refused name is printed with its control characters escaped (printf %q), so a newline or an escape in it can't start a line of
# its own in the log, a workflow command among them.
shown="$branch"
if [[ "$branch" == *[[:cntrl:]]* ]]; then
  shown="$(printf '%q' "$branch")"
fi
echo "Branch name '${shown}' does not match the branch standard."
echo "Expected: feature/{issue}-{slug}, fix/{issue}-{slug}, hotfix/{issue}-{slug}, or chore/{slug}"
exit 1
