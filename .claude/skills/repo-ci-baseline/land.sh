#!/usr/bin/env bash
# Watch a round's PRs and act on the landing decision until they land: update a
# branch that's BEHIND, ask Copilot once per PR (and add review:claude where it
# can't review), mark a draft ready (--ready),
# arm auto-merge where the decision says so (dotfiles, an unarmed release-blog
# PR), and leave a Baseline repo's reviewed PR to its PR Auto-Merge. Prints only
# changes. The logic is github/land.cs (LandCore.cs, StatusCore.cs and Landing.cs); see
# references/automerge.md#landing-a-round.
#
#   land.sh [--once] [--dry-run] [--ready] [--interval s] [--timeout min] [--blog-wait min] [<owner/repo>|<owner/repo#n>]...
#
# It runs the C# file-based app from its own folder (ADR 0017), so the SDK is the one the Factory's global.json names, whatever a
# global.json in the caller's folder says; the first run after a change builds it (about a second, more on a fresh machine), so two
# runs at once right after a change can trip over each other's build: start one first. dotnet sends no CLI usage data, skips its
# first-run setup and prints no logo, and the build leaves no MSBuild node, MSBuild server or compiler server behind, which would keep
# the caller's stdout open (a $(land.sh) would wait for them). No dotnet on PATH exits 127 with a message saying so.
set -euo pipefail
here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/github"
command -v dotnet > /dev/null || {
  echo "land.sh: dotnet isn't on PATH; github/land.cs runs on the .NET SDK the Factory's global.json names (ADR 0017)" >&2
  exit 127
}
cd -- "$here"
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  UseSharedCompilation=false exec dotnet run --file "$here/land.cs" -- "$@"
