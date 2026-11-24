#!/usr/bin/env bash
# One line per open PR across the repos in github/repos.txt, ending with the
# landing decision's next action. Read-only. The logic is github/status.cs
# (StatusCore.cs and Landing.cs); see references/automerge.md#status-and-the-landing-decision.
#
#   status.sh                   every repo in github/repos.txt
#   status.sh <owner/repo>...   just these
#
# It runs the C# file-based app from its own folder (ADR 0017), so the SDK is the one the Factory's global.json names, whatever a
# global.json in the caller's folder says; the first run after a change builds it (about a second, more on a fresh machine), so two
# runs at once right after a change can trip over each other's build: start one first. dotnet sends no CLI usage data, skips its
# first-run setup and prints no logo, and the build leaves no MSBuild node, MSBuild server or compiler server behind, which would keep
# the caller's stdout open (a $(status.sh) would wait for them). No dotnet on PATH exits 127 with a message saying so.
set -euo pipefail
here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/github"
command -v dotnet > /dev/null || {
  echo "status.sh: dotnet isn't on PATH; github/status.cs runs on the .NET SDK the Factory's global.json names (ADR 0017)" >&2
  exit 127
}
cd -- "$here"
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  UseSharedCompilation=false exec dotnet run --file "$here/status.cs" -- "$@"
