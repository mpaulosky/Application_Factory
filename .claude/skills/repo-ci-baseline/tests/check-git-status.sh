#!/usr/bin/env bash
# A guard that reads `git status --porcelain` in an Owned shell script names its untracked-files setting: print each one that doesn't and exit 1.
#
#   bash check-git-status.sh <the Template's owned/ folder>
#
# Exit 0: none found. Exit 1: found, printed. Exit 2: the folder is missing, the folder or a file in it could not be read, or the scan itself failed (nothing was fully checked).
# The rule and what it scans are in GitStatusGuards.cs's header. test.sh runs this, and so does the Factory's scripts/gate.sh (also with --quick).
# It runs the C# file-based app check-git-status.cs from its own folder (ADR 0017), so the SDK is the one the Factory's global.json names and the
# three empty Directory.* files beside it keep any of the scanned folder's out of the build; the folder is passed as an absolute path, so a relative
# one is printed absolute. dotnet sends no CLI usage data, skips its first-run setup and prints no logo, and the build leaves no MSBuild node,
# MSBuild server or compiler server behind. No dotnet on PATH exits 2 with a message saying so: nothing was checked.
set -euo pipefail
here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
command -v dotnet > /dev/null || {
  echo "check-git-status.sh: dotnet isn't on PATH; tests/check-git-status.cs runs on the .NET SDK the Factory's global.json names (ADR 0017)" >&2
  exit 2
}
args=()
for arg in "$@"; do
  [[ "$arg" == /* || -z "$arg" ]] || arg="$PWD/$arg"
  args+=("$arg")
done
cd -- "$here"
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  UseSharedCompilation=false exec dotnet run --file "$here/check-git-status.cs" -- ${args[@]+"${args[@]}"}
