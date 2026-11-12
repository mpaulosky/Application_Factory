#!/usr/bin/env bash
# A guard that reads `git status --porcelain` in an Owned shell script names its untracked-files setting: print each one that doesn't and exit 1.
#
#   bash check-git-status.sh <the Template's owned/ folder>
#
# Exit 0: none found. Exit 1: found, printed. Exit 2: the folder is missing, or the folder or a file in it could not be read (nothing was fully checked).
# A call may break over lines with a trailing backslash (the lines are joined first); git's global options (-C <dir>, --no-pager, --git-dir=<dir>) may come before `status`.
# Without --untracked-files=all or -uall (or =no / -uno, when ignoring them is the point), a user's status.showUntrackedFiles=no hides an untracked file from the
# guard, so a "clean tree" check passes on a dirty one. A script's tests, markdown and Sandcastle's TypeScript are not scanned: a test sets its own repo's
# config, and Sandcastle runs `git status` in a fresh sandbox clone. test.sh runs this, and so does the Factory's scripts/gate.sh (also with --quick).
set -euo pipefail

[[ $# == 1 && -d "$1" ]] || { echo "usage: check-git-status.sh <the Template's owned/ folder, which must exist>" >&2; exit 2; }
root="${1%/}"
list="$(mktemp)" || exit 2
trap 'command rm -f -- "$list"' EXIT
find "$root" -type f -print0 >"$list" || { echo "could not list $root" >&2; exit 2; }
# a global option of git (-C <dir>, -c k=v, --no-pager, --git-dir=<dir>), its value a word or a quoted string
opt='(-[A-Za-z]+ +("[^"]*"|'"'"'[^'"'"']*'"'"'|[^ ]+)|--[a-z-]+(=("[^"]*"|[^ ]+))?)'
call="(^|[^[:alnum:]_-])git( +${opt})* +status( |\$)"
setting='--untracked-files|[[:space:]]-u(all|no|normal)?([^[:alnum:]-]|$)'
found=0
while IFS= read -r -d '' file; do
  rel="${file#"$root"/}"
  case "$rel" in tests/* | */tests/*) continue ;; esac # only the Template's own folders below $root count, not where the checkout lives
  case "${file##*/}" in *.sh | pre-commit | pre-push | post-checkout) ;; *) continue ;; esac
  [[ -r "$file" ]] || { echo "could not read $file" >&2; exit 2; }
  # the file's logical lines (a trailing backslash joins the next line), each with the number of its first line
  joined="$(awk '{ if (buf == "") start = NR; l = $0; if (l ~ /\\$/) { sub(/\\$/, "", l); buf = buf l " "; next } print start ":" buf l; buf = "" } END { if (buf != "") print start ":" buf }' "$file")" \
    || { echo "could not read $file" >&2; exit 2; }
  lines="$(printf '%s\n' "$joined" | grep -E "$call" | grep -e '--porcelain' | grep -vE -e "$setting" | grep -vE '^[0-9]+:[[:space:]]*#' || true)"
  if [[ -n "$lines" ]]; then
    while IFS= read -r line; do echo "$file:$line"; done <<<"$lines"
    found=1
  fi
done <"$list"
if ((found)); then
  echo "Give each git status --porcelain above --untracked-files=all (or =no on purpose), so status.showUntrackedFiles can't weaken the check." >&2
  exit 1
fi
