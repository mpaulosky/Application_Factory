#!/usr/bin/env bash
# One line per open PR across the repos in github/repos.txt, ending with the
# landing decision's next action. Read-only. The logic is github/status.cs
# (StatusCore.cs and Landing.cs); see references/automerge.md#status-and-the-landing-decision.
#
#   status.sh                   every repo in github/repos.txt
#   status.sh <owner/repo>...   just these
#
# It runs the C# file-based app from its own folder (ADR 0017), so the SDK is the one the Factory's global.json names, whatever a
# global.json in the caller's folder says; the first run after a change builds it (about a second, more on a fresh machine). Two runs
# at once would build it together and trip over each other's build, so each first runs it with --help, which builds it when it must and
# reads nothing, under a lock on github/ (flock(1)): one run builds, the others wait and find it built, and the lock is let go before
# the run itself (mpaulosky/Application_Factory#178). That build's output, a warning among it, stays off stdout. Without flock (macOS
# has none), start one run first. dotnet sends no CLI usage data, skips its first-run setup and prints no logo, and the build leaves
# no MSBuild node, MSBuild server or compiler server behind, which would keep
# the caller's stdout open (a $(status.sh) would wait for them). No dotnet on PATH exits 127 with a message saying so.
set -euo pipefail
here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/github"
command -v dotnet > /dev/null || {
  echo "status.sh: dotnet isn't on PATH; github/status.cs runs on the .NET SDK the Factory's global.json names (ADR 0017)" >&2
  exit 127
}
cd -- "$here"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  UseSharedCompilation=false
run=(dotnet run --file "$here/status.cs")
# A build that fails here fails again in the run below, which says why.
if command -v flock > /dev/null; then
  flock -- "$here" "${run[@]}" -- --help > /dev/null 2>&1 || true
fi
exec "${run[@]}" -- "$@"
