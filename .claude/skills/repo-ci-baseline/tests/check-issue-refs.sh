#!/usr/bin/env bash
# A bare #N in an Owned file: print each one and exit 1.
#
#   bash check-issue-refs.sh <the Template's owned/ folder>
#
# Exit 0: none found. Exit 1: found, printed. Exit 2: the folder is missing, the arguments are wrong, a file or directory is unreadable, or the scan itself failed (nothing was (fully) checked).
# The rule and what it scans are in IssueRefs.cs's header. test.sh runs this, and so does the Factory's scripts/gate.sh (also with --quick).
# It runs the C# file-based app check-issue-refs.cs from its own folder (ADR 0017), so the SDK is the one the Factory's global.json names and the
# three empty Directory.* files beside it keep any of the scanned folder's out of the build; the folder is passed as an absolute path. dotnet sends no CLI
# usage data, skips its first-run setup and prints no logo, and the build leaves no MSBuild node, MSBuild server or compiler server behind. No dotnet on PATH exits 2 with a message saying so: nothing was checked.
set -euo pipefail
here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
command -v dotnet > /dev/null || {
  echo "check-issue-refs.sh: dotnet isn't on PATH; tests/check-issue-refs.cs runs on the .NET SDK the Factory's global.json names (ADR 0017)" >&2
  exit 2
}
args=()
for arg in "$@"; do
  [[ "$arg" == /* || -z "$arg" ]] || arg="$PWD/$arg"
  args+=("$arg")
done
cd -- "$here"
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  UseSharedCompilation=false exec dotnet run --file "$here/check-issue-refs.cs" -- ${args[@]+"${args[@]}"}
