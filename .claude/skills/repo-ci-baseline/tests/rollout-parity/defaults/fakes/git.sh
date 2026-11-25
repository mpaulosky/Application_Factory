#!/usr/bin/env bash
# The default git: a checkout whose git folder is <checkout>/.git, and its re-Apply worktree at
# <checkout>-worktrees/chore-reapply-baseline, at HEAD 2222... (the same on the remote branch), with nothing changed and no
# Owned file in its diff. The branch has commits main lacks unless FAKE_GIT_AHEAD=0. Anything else is a call nobody
# planned for.
head=2222222222222222222222222222222222222222
branch=chore/reapply-baseline
if [[ "$*" == --exec-path ]]; then
  exit 0
fi
if [[ "${1:-}" != -C || $# -lt 3 ]]; then
  echo "UNMATCHED git $* (no default answers it)" >> "$FAKE_GH_LOG"
  exit 97
fi
path="$2"
shift 2
checkout="$path"
if [[ "$path" == *-worktrees/chore-reapply-baseline ]]; then
  checkout="${path%-worktrees/chore-reapply-baseline}"
fi
case "$*" in
  "rev-parse --path-format=absolute --git-common-dir") echo "$checkout/.git" ;;
  "rev-parse --path-format=absolute --git-dir") echo "$checkout/.git/worktrees/chore-reapply-baseline" ;;
  "rev-parse HEAD" | "rev-parse --verify --quiet refs/heads/$branch") echo "$head" ;;
  "status --porcelain=v1 -z --untracked-files=all" | "diff --no-renames --name-only -z origin/main...HEAD") ;;
  "ls-remote origin refs/heads/$branch") printf '%s\trefs/heads/%s\n' "$head" "$branch" ;;
  "merge-base --is-ancestor HEAD origin/main") [[ "${FAKE_GIT_AHEAD:-1}" == 0 ]] ;;
  *)
    echo "UNMATCHED git -C $path $* (no default answers it)" >> "$FAKE_GH_LOG"
    exit 97
    ;;
esac
