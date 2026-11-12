#!/usr/bin/env bash
# A guard that reads `git status --porcelain` in an Owned shell script names its untracked-files setting: print each one that doesn't and exit 1.
#
#   bash check-git-status.sh <the Template's owned/ folder>
#
# Exit 0: none found. Exit 1: found, printed. Exit 2: the folder is missing, or the folder or a file in it could not be read (nothing was fully checked).
# The rule and what it scans are in check-git-status.py's header. test.sh runs this, and so does the Factory's scripts/gate.sh (also with --quick).
set -euo pipefail
exec python3 -I "$(dirname "${BASH_SOURCE[0]}")/check-git-status.py" "$@"
