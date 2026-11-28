#!/usr/bin/env bash
# Summarizes the step timings log scripts/gate.sh and test.sh keep (scripts/lib/timings.sh, mpaulosky/Application_Factory#220) as a Markdown table, for the
# gate-timings skill's report. scripts/gate-timings.cs does the work (GateTimings.cs holds the logic and its contract) on the .NET SDK the Factory's
# global.json names; this runs it from scripts/, beside its empty shield files, so no Directory.Build file above the checkout reaches its build. As
# status.sh does, it first builds the app with --help, which reads nothing, under a lock on scripts/ (flock(1)), so two runs at once don't build it
# together (one builds, the other waits and finds it built); without flock, start one run first. No dotnet on PATH exits 127 with a message.
#
#   scripts/gate-timings.sh [--runs N] [--log FILE]   N defaults to 20; FILE to GATE_TIMINGS_LOG, else the primary checkout's docs/timings/gate-timings.tsv
set -euo pipefail
here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
command -v dotnet > /dev/null || {
  echo "gate-timings.sh: dotnet isn't on PATH; scripts/gate-timings.cs runs on the .NET SDK the Factory's global.json names (ADR 0017)" >&2
  exit 127
}
# It runs from scripts/, so the caller's folder goes with it: a relative --log is read from there, and the default log is that checkout's.
export GATE_TIMINGS_CALLER_DIR="$PWD"
cd -- "$here"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  UseSharedCompilation=false
run=(dotnet run --file "$here/gate-timings.cs")
# A build that fails here fails again in the run below, which says why.
if command -v flock > /dev/null; then
  flock -- "$here" "${run[@]}" -- --help > /dev/null 2>&1 || true
fi
exec "${run[@]}" -- "$@"
