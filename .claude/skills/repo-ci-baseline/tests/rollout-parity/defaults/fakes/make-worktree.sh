#!/usr/bin/env bash
# Not a tool's fixture: what a case's reapply.sh fixture runs (bash "$FAKE_TOOL_DIR/make-worktree.sh" "$3") to make the
# re-Apply worktree reapply.sh would, as the default git describes it: the checkout's git folder, the worktree's .git
# link (neither is compared, as git can't store a .git), its scripts/review.sh, which runs the fake tool as review.sh, and
# its node_modules/.bin/tsx, which the round looks for before it records a review.
set -euo pipefail
checkout="$1"
worktree="$checkout-worktrees/chore-reapply-baseline"
mkdir -p "$checkout/.git" "$worktree/scripts" "$worktree/node_modules/.bin"
printf 'gitdir: %s/.git/worktrees/chore-reapply-baseline\n' "$checkout" > "$worktree/.git"
# shellcheck disable=SC2016 # expanded when the script runs, from the run's environment
printf '#!/usr/bin/env bash\nFAKE_TOOL_NAME=review.sh exec bash "$FAKE_TOOL" "$@"\n' > "$worktree/scripts/review.sh"
printf '#!/bin/sh\n' > "$worktree/node_modules/.bin/tsx"
chmod +x "$worktree/scripts/review.sh" "$worktree/node_modules/.bin/tsx"
