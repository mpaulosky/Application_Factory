#!/usr/bin/env bash
# Type-check and test the Template's Sandcastle code (.sandcastle/), the way a new repo gets it.
#
#   tests/sandcastle-check.test.sh
#
# Applies the Template to a throwaway repo with no commits, so Sandcastle's files go in (apply.sh adds them to a repo with
# a .sandcastle/ folder or none yet), installs the Seed package.json's packages, and runs its check:sandcastle script:
# tsc --noEmit and the .sandcastle/**/*.test.mts tests. Nothing is installed in the skill itself.
#
# The install is not frozen. A new repo starts with no lockfile and takes the newest versions the Seed's ranges allow,
# so this is the check a new repo would meet; a range that lets in a release the code can't build with fails here first.
# test.sh does not run this (it needs pnpm and the network); the Template tests workflow does, as its own step.
set -euo pipefail

skill_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# A caller's hooks or git variables must not leak into the throwaway repo.
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX

git init -q -b main "$work/repo"
"$skill_dir/apply.sh" "$work/repo" > "$work/apply.log" 2>&1 || { cat "$work/apply.log" >&2; exit 1; }
[[ -d "$work/repo/.sandcastle" ]] || { echo "sandcastle-check: Apply left no .sandcastle/ in the new repo" >&2; exit 1; }

cd "$work/repo"
pnpm install --no-frozen-lockfile
pnpm run check:sandcastle
