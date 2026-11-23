#!/usr/bin/env bash
# PROTOTYPE (research/status-cs-trial, never merged) measurement for mpaulosky/Application_Factory#97: warm start of status.py against the C# port, both answered by
# the parity suite's fake gh on one case, median of N runs each, in milliseconds.
#   measure.sh <skill dir> <case name> <runs>
set -euo pipefail
skill="$1" case_name="$2" runs="$3"
work="$(mktemp -d)"
mkdir "$work/bin" "$work/cwd"
ln -s "$skill/tests/status-parity/fake-gh.sh" "$work/bin/gh"
case_dir="$skill/tests/status-parity/cases/$case_name"
mapfile -t args < "$case_dir/args"
export PATH="$work/bin:$PATH" FAKE_GH_DIR="$case_dir/gh" FAKE_GH_LOG="$work/calls" GH_CONFIG_DIR="$work/cwd" TZ=UTC DOTNET_NOLOGO=1
median() { sort -n | awk '{a[NR]=$1} END {print a[int((NR+1)/2)]}'; }
time_it() { # time_it <label> <command...>: prints label and median ms
  local label="$1"
  shift
  "$@" "${args[@]}" > /dev/null 2>&1 || true # one run first, so the OS cache is warm
  for _ in $(seq "$runs"); do
    local start end
    start=$(date +%s%N)
    (cd "$work/cwd" && "$@" "${args[@]}" > /dev/null 2>&1) || true
    end=$(date +%s%N)
    echo $(((end - start) / 1000000))
  done | median | sed "s/^/$label: /;s/\$/ ms/"
}
time_it "python3 status.py" python3 "$skill/github/status.py"
time_it "dotnet run --no-build --file status.cs" dotnet run --no-build --file "$skill/github/status.cs" --
time_it "dotnet run --file status.cs" dotnet run --file "$skill/github/status.cs" --
dll="$(find "$HOME/.local/share/dotnet/runfile" -path '*status-*/bin/debug/status.dll' -newer "$skill/github/status.cs" | head -1)"
[[ -n "$dll" ]] && time_it "dotnet status.dll" dotnet "$dll"
echo "calls per run: $(sort -u "$work/calls" | wc -l)"
