#!/usr/bin/env bash
# A guard that reads `git status --porcelain` in an Owned shell script names its untracked-files setting: print each one that doesn't and exit 1.
#
#   bash check-git-status.sh <the Template's owned/ folder>
#
# Exit 0: none found. Exit 1: found, printed. Exit 2: the folder is missing or a file could not be read (nothing was fully checked).
# Without --untracked-files=all (or =no, when ignoring them is the point), a user's status.showUntrackedFiles=no hides an untracked file from the
# guard, so a "clean tree" check passes on a dirty one. A script's tests, markdown and Sandcastle's TypeScript are not scanned: a test sets its own repo's
# config, and Sandcastle runs `git status` in a fresh sandbox clone. test.sh runs this, and so does the Factory's scripts/gate.sh (also with --quick).
set -euo pipefail

[[ $# == 1 && -d "$1" ]] || { echo "usage: check-git-status.sh <the Template's owned/ folder, which must exist>" >&2; exit 2; }
found=0
while IFS= read -r -d '' file; do
  case "$file" in */tests/*) continue ;; esac
  case "${file##*/}" in *.sh | pre-commit | pre-push | post-checkout) ;; *) continue ;; esac
  lines="$(grep -nE '(^|[^[:alnum:]_-])git( +-[A-Za-z] +[^ ]+)* +status( |$)' "$file" | grep -e '--porcelain' | grep -vE -e '--untracked-files(=| )' | grep -vE '^[0-9]+:[[:space:]]*#' || true)" \
    || { echo "could not read $file" >&2; exit 2; }
  if [[ -n "$lines" ]]; then
    while IFS= read -r line; do echo "$file:$line"; done <<<"$lines"
    found=1
  fi
done < <(find "$1" -type f -print0)
if ((found)); then
  echo "Give each git status --porcelain above --untracked-files=all (or =no on purpose), so status.showUntrackedFiles can't weaken the check." >&2
  exit 1
fi
