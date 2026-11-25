#!/usr/bin/env bash
# Roll a re-Apply round out across the Baseline repos: reapply.sh on
# TicketManager first, the rest only once its re-Apply PR has merged, one at a
# time so one pre-push gate runs at a time. Run it again after TicketManager
# lands. --auto checks the tools, runs /pre-push-review headless in a re-Apply
# worktree whose gate asks for it, and lands each PR, stopping only for a
# person (ADR 0016). The logic is github/rollout.cs (RolloutCore.cs; --auto:
# AutoRollout*.cs); see SKILL.md's Change the Baseline.
#
#   rollout.sh --brings <owner/repo#n,#n,...> [--root dir] [--dry-run] [--auto]
#
# It builds the C# file-based app from its own folder (ADR 0017), so the SDK is the one the Factory's global.json names, then runs the
# built dll in its place (exec), not dotnet run: the round is then this script's pid, so a SIGTERM or SIGHUP sent to it reaches the
# round's handler, and a SIGKILL of it ends the round (ADR 0017's Update for mpaulosky/Application_Factory#157). The round runs in the
# caller's folder, so a relative --root names what it names there. The runtime takes SIGTERM over before the app's first line runs, so
# the signals the caller ignores (SigIgn of /proc/$$/status) are passed on in ROLLOUT_SIGIGN, and the round keeps an ignored SIGTERM
# ignored. Two runs at once right after a change would build it together and trip over each other's build, so the build runs under a
# lock on github/ (flock(1), as status.sh's); without flock (macOS has none), start one run first. The build's output stays off stdout
# and is shown only when it fails; it leaves no MSBuild node, MSBuild server or compiler server behind. No dotnet on PATH exits 127.
set -euo pipefail
here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/github"
command -v dotnet > /dev/null || {
  echo "rollout.sh: dotnet isn't on PATH; github/rollout.cs runs on the .NET SDK the Factory's global.json names (ADR 0017)" >&2
  exit 127
}
build=(env DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
  DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false dotnet build "$here/rollout.cs" -t:Build -getProperty:TargetPath)
if command -v flock > /dev/null; then
  build=(flock -- "$here" "${build[@]}")
fi
said="$(mktemp)"
trap 'rm -f -- "$said"' EXIT
dll="$(cd -- "$here" && "${build[@]}" < /dev/null 2> "$said")" || {
  cat -- "$said" >&2
  echo "rollout.sh: dotnet build github/rollout.cs failed (above); nothing run" >&2
  exit 1
}
dll="${dll##*$'\n'}"
[[ "$dll" == /*.dll && -f "$dll" ]] || {
  echo "rollout.sh: dotnet build github/rollout.cs named no dll it built ('$dll'); nothing run" >&2
  exit 1
}
rm -f -- "$said"
trap - EXIT
ROLLOUT_SIGIGN="$(sed -n 's/^SigIgn:[[:space:]]*//p' "/proc/$$/status" 2> /dev/null || true)"
export ROLLOUT_SIGIGN
exec dotnet "$dll" "$@"
