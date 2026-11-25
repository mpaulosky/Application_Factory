#!/usr/bin/env bash
# Check, and with --fix apply, the Baseline's GitHub settings on a repo.
# The logic is github/settings.cs (SettingsCore.cs); see references/github-settings.md.
#
#   github-settings.sh <owner/repo>...       report; exit 1 on any drift
#   github-settings.sh --fix <owner/repo>... apply the standard
#   github-settings.sh --all                 every repo in github/repos.txt
#
# It runs the C# file-based app from its own folder (ADR 0017), so the SDK is the one the Factory's global.json names, whatever a
# global.json in the caller's folder says; the first run after a change builds it (about a second, more on a fresh machine). Two runs
# at once would build it together and trip over each other's build, so each first runs it with --help, which builds it when it must and
# reads nothing, under a lock on github/ (flock(1)): one run builds, the others wait and find it built, and the lock is let go before
# the run itself (mpaulosky/Application_Factory#178). That build's output, a warning among it, stays off stdout. Without flock (macOS
# has none), start one run first. dotnet sends no CLI usage data, skips its first-run setup and prints no logo, and the build leaves
# no MSBuild node, MSBuild server or compiler server behind, which would keep
# the caller's stdout open (a $(github-settings.sh) would wait for them). That build leaves CA2266 out (-p:NoWarn=CA2266, this run's
# build only): dotnet run prints its build's warnings on stdout, so a first run would start its report, or its --json, with that line.
# The gate's build of the script still shows it (ADR 0017). No dotnet on PATH exits 127 with a message saying so.
set -euo pipefail
here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/github"
command -v dotnet > /dev/null || {
  echo "github-settings.sh: dotnet isn't on PATH; github/settings.cs runs on the .NET SDK the Factory's global.json names (ADR 0017)" >&2
  exit 127
}
cd -- "$here"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  UseSharedCompilation=false
run=(dotnet run --file "$here/settings.cs" -p:NoWarn=CA2266)
# A build that fails here fails again in the run below, which says why.
if command -v flock > /dev/null; then
  flock -- "$here" "${run[@]}" -- --help > /dev/null 2>&1 || true
fi
exec "${run[@]}" -- "$@"
