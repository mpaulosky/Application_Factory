#!/usr/bin/env bash
# Tests for mpaulosky/Application_Factory#27: a repo in github/repos.txt with a narrowed scope is named in an ADR.
#
# A second word on a repo's line narrows what github-settings.sh checks. `factory` runs every check and narrows nothing; any other
# scope (today only `repo-settings-only`) skips checks, so the repo must appear, by its full name, in an ADR of the Factory's
# (docs/adr/) that explains the narrowing. Without that, a repo can be left out of the standard without anyone deciding it.
# Usage: tests/repos-scope.test.sh
set -uo pipefail

SKILL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ADR_DIR="$(cd "$SKILL_DIR/../../.." && pwd)/docs/adr"
fail=0

[[ -d "$ADR_DIR" ]] || { echo "FAIL: no ADR directory at $ADR_DIR" >&2; exit 1; }

while read -r repo scope _; do
  [[ -z "$repo" || "$repo" == \#* ]] && continue
  [[ -z "$scope" || "$scope" == "factory" ]] && continue
  if ! grep -rlF -- "$repo" "$ADR_DIR" >/dev/null; then
    echo "FAIL: $repo has the narrowed scope '$scope' in github/repos.txt and no ADR in docs/adr/ names it" >&2
    fail=1
  fi
done < "$SKILL_DIR/github/repos.txt"

# The check itself: a narrowed scope with no ADR must be caught.
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/adr"; echo "mentions mpaulosky/Other only" > "$tmp/adr/0001.md"
if grep -rlF -- "mpaulosky/Narrowed" "$tmp/adr" >/dev/null; then
  echo "FAIL: the ADR search matched a repo no ADR names" >&2; fail=1
fi

((fail)) && exit 1
echo "repos-scope: ok"
