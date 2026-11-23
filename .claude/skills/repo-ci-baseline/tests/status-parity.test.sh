#!/usr/bin/env bash
# The status parity suite: github/status.py's behaviour through its command line only, so the same cases judge any
# implementation of it (the C# port, mpaulosky/Application_Factory#97) against the Python.
#
#   tests/status-parity.test.sh                    run every case against python3 github/status.py
#   tests/status-parity.test.sh <command> [arg]...  run every case against another implementation
#   STATUS_PARITY_RECORD=1 tests/status-parity.test.sh
#                                                   write each case's expected.* from the implementation (the Python reference
#                                                   only; review the diff before committing it)
#
# Each case is a folder under tests/status-parity/cases/:
#
#   args             the implementation's arguments, one per line (empty: none)
#   gh/              the fixtures tests/status-parity/fake-gh.sh answers the `gh api` calls from (see its header)
#   expected.stdout  stdout, byte for byte
#   expected.code    the exit code
#   expected.stderr  one ERE per stderr line, in order: stderr has exactly that many lines, each matching its pattern
#                    (stderr carries the error text of the language, so only its shape is compared)
#   expected.calls   the fixture keys of the `gh` calls made, in order: the queries a run costs are behaviour too
#
# The implementation runs from an empty temporary folder with the fake gh first on PATH, LC_ALL=C.UTF-8 and TZ=UTC. Cases
# run one at a time: build a compiled implementation once before running the suite (a file-based C# app with
# `dotnet build <file>.cs`, then the command `dotnet run --no-build --file <file>.cs --`), since parallel first runs of one
# can fail while it builds.
#
# Not covered: the run with no repo arguments (it reads the Fleet's github/repos.txt), and a Copilot ask record younger
# than the 30 seconds it takes to settle (the clock isn't fixed; every record in the fixtures is long settled).
#
# Exit 0 when every case passes, 1 when one fails, 2 on a usage or setup error. Needs bash, diff, grep and sed; the
# fake gh needs only bash and cat.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
skill_dir="$(dirname "$here")"
cases_dir="$here/status-parity/cases"
fake="$here/status-parity/fake-gh.sh"

if [[ $# -gt 0 ]]; then
  impl=("$@")
else
  impl=(python3 "$skill_dir/github/status.py")
fi
record="${STATUS_PARITY_RECORD:-}"
if [[ -n "$record" && "$record" != 1 ]]; then
  echo "status-parity: STATUS_PARITY_RECORD must be 1 or unset, not '$record'." >&2
  exit 2
fi
if [[ ! -x "$fake" ]]; then
  echo "status-parity: $fake is missing or not executable." >&2
  exit 2
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin" "$tmp/cwd"
ln -s "$fake" "$tmp/bin/gh"

shopt -s nullglob
cases=("$cases_dir"/*/)
if [[ ${#cases[@]} -eq 0 ]]; then
  echo "status-parity: no cases under $cases_dir." >&2
  exit 2
fi

failed=0
for case_dir in "${cases[@]}"; do
  case_dir="${case_dir%/}"
  name="$(basename "$case_dir")"
  args=()
  if [[ -s "$case_dir/args" ]]; then
    mapfile -t args < "$case_dir/args"
  fi
  : > "$tmp/calls"
  code=0
  (cd "$tmp/cwd" && PATH="$tmp/bin:$PATH" LC_ALL=C.UTF-8 TZ=UTC FAKE_GH_DIR="$case_dir/gh" FAKE_GH_LOG="$tmp/calls" \
    "${impl[@]}" "${args[@]}" > "$tmp/stdout" 2> "$tmp/stderr") || code=$?

  if [[ -n "$record" ]]; then
    # A fixture set the reference can't run in full is never recorded: its expected.* would bake the gap in.
    if grep -q '^UNMATCHED ' "$tmp/calls"; then
      echo "status-parity: $name makes a gh call no fixture answers, not recorded: $(grep '^UNMATCHED ' "$tmp/calls" | head -1)" >&2
      exit 1
    fi
    cp "$tmp/stdout" "$case_dir/expected.stdout"
    printf '%s\n' "$code" > "$case_dir/expected.code"
    cp "$tmp/calls" "$case_dir/expected.calls"
    # A new case's stderr lines are matched literally until someone loosens them to the shape that matters; an existing
    # expected.stderr is kept, since it holds that loosening (the run without STATUS_PARITY_RECORD checks it still fits).
    if [[ ! -e "$case_dir/expected.stderr" ]]; then
      sed -e 's/[][\.*^$+?(){}|/]/\\&/g' -e 's/^/^/' -e 's/$/$/' "$tmp/stderr" > "$case_dir/expected.stderr"
    fi
    echo "recorded $name (exit $code)"
    continue
  fi

  problems=()
  if grep -q '^UNMATCHED ' "$tmp/calls"; then
    problems+=("a gh call no fixture answers: $(grep '^UNMATCHED ' "$tmp/calls" | head -1)")
  fi
  want_code="$(cat "$case_dir/expected.code")"
  [[ "$code" == "$want_code" ]] || problems+=("exit $code, expected $want_code")
  diff -u "$case_dir/expected.stdout" "$tmp/stdout" > "$tmp/diff" || problems+=("stdout differs:"$'\n'"$(cat "$tmp/diff")")
  diff -u "$case_dir/expected.calls" "$tmp/calls" > "$tmp/diff" || problems+=("gh calls differ:"$'\n'"$(cat "$tmp/diff")")
  mapfile -t patterns < "$case_dir/expected.stderr"
  mapfile -t lines < "$tmp/stderr"
  if [[ ${#lines[@]} -ne ${#patterns[@]} ]]; then
    problems+=("${#lines[@]} stderr line(s), expected ${#patterns[@]}:"$'\n'"$(cat "$tmp/stderr")")
  else
    for i in "${!patterns[@]}"; do
      grep -qE -- "${patterns[$i]}" <<< "${lines[$i]}" || problems+=("stderr line $((i + 1)) '${lines[$i]}' doesn't match '${patterns[$i]}'")
    done
  fi

  if [[ ${#problems[@]} -eq 0 ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name"
    printf '     %s\n' "${problems[@]}"
    failed=1
  fi
done

if [[ -n "$record" ]]; then
  exit 0
fi
if [[ $failed -ne 0 ]]; then
  echo "status-parity: a case failed against: ${impl[*]}" >&2
  exit 1
fi
echo "status-parity: ${#cases[@]} case(s) pass against: ${impl[*]}"
