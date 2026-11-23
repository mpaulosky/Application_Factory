#!/usr/bin/env bash
# The status parity suite: github/status.py's behaviour through its command line only, so the same cases judge any
# implementation of it (the C# port, mpaulosky/Application_Factory#97) against the Python.
#
#   tests/status-parity.test.sh                    run every case against python3 github/status.py
#   tests/status-parity.test.sh <command> [arg]...  run every case against another implementation
#   STATUS_PARITY_RECORD=1 tests/status-parity.test.sh
#                                                   record the cases from the Python reference (no command is accepted:
#                                                   a port never records its own oracle); review the diff before committing
#
# Each case is a folder under tests/status-parity/cases/:
#
#   args             the implementation's arguments, one per line, taken verbatim (a blank line is an empty argument;
#                    an empty or missing file: none)
#   gh/              the fixtures tests/status-parity/fake-gh.sh answers the `gh api` calls from (see its header for what
#                    an implementation must send)
#   expected.stdout  stdout, byte for byte
#   expected.code    the exit code
#   expected.stderr  one ERE per stderr line, in order: stderr has exactly that many lines, each matching its pattern
#                    (stderr carries the error text of the language, so only its shape is compared); no line is blank
#   expected.calls   the fixture keys of the `gh` calls made, in order: the queries a run costs are behaviour too
#
# No case file holds a carriage return. Record mode writes expected.stdout, expected.code and expected.calls. It writes a
# new case's expected.stderr with each line matched literally, for someone to loosen to the shape that matters; an existing
# one holds that loosening, so it is kept, and the run fails when it no longer fits. A case whose run makes a gh call no
# fixture answers, or times out, is never recorded.
#
# The implementation runs from an empty temporary folder, with stdin from /dev/null, the fake gh first on PATH, no GitHub
# token in its environment and GH_CONFIG_DIR an empty folder, LC_ALL=C.UTF-8 (best-effort: macOS has no such locale, so
# PYTHONUTF8=1 is set too), TZ=UTC, and STATUS_PARITY_TIMEOUT seconds (default 60) before it counts as hung. Each command
# word that names an existing file or folder is made absolute first, so `--file src/Status.cs` works from anywhere; the
# first word is looked up on PATH unless it holds a /. Build a compiled implementation once before running the suite (a
# file-based C# app with `dotnet build <file>.cs`, then the command `dotnet run --no-build --file <file>.cs --`), so the
# build isn't timed inside the first case and two runs of the suite don't build it at once.
#
# Not covered, on purpose:
# - The run with no repo arguments: it reads the Fleet's github/repos.txt, and a flag to point it elsewhere would change
#   status.py for a test. status.py reads repos.txt on every run (the cases' repos aren't in it, so nothing it holds
#   changes their output today).
# - A Copilot ask record younger than the 30 seconds it takes to settle: the clock isn't fixed, and every record in the
#   fixtures is long settled.
# - A repo argument with no /: status.py fails with a Python traceback, which is no contract a port should copy.
#
# Exit 0 when every case passes, 1 when one fails, 2 on a usage or setup error (a case missing a file or holding a bad
# one, a command that can't be run, a temporary folder that can't be made). Needs bash 4.4 or later, coreutils (mktemp,
# ln, cp, cat, timeout), diff, grep and sed; the fake gh needs only bash and cat.
set -euo pipefail

setup_error() {
  echo "status-parity: $*" >&2
  exit 2
}

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
skill_dir="$(dirname -- "$here")"
cases_dir="$here/status-parity/cases"
fake="$here/status-parity/fake-gh.sh"

record="${STATUS_PARITY_RECORD:-}"
if [[ -n "$record" && "$record" != 1 ]]; then
  setup_error "STATUS_PARITY_RECORD must be 1 or unset, not '$record'."
fi
if [[ -n "$record" && $# -gt 0 ]]; then
  setup_error "STATUS_PARITY_RECORD records from the Python reference only; run it with no command."
fi
limit="${STATUS_PARITY_TIMEOUT:-60}"
[[ "$limit" =~ ^[1-9][0-9]*$ ]] || setup_error "STATUS_PARITY_TIMEOUT must be a whole number of seconds, not '$limit'."
command -v timeout > /dev/null || setup_error "timeout (coreutils) is not on PATH."

if [[ $# -gt 0 ]]; then
  impl=("$@")
else
  impl=(python3 "$skill_dir/github/status.py")
fi
# The command runs from an empty folder, so a relative path in it would name nothing there.
for i in "${!impl[@]}"; do
  word="${impl[i]}"
  [[ "$word" != /* && "$word" != -* ]] || continue
  [[ $i -gt 0 || "$word" == */* ]] || continue
  if [[ -e "$word" ]]; then
    impl[i]="$(CDPATH='' cd -- "$(dirname -- "$word")" && pwd)/$(basename -- "$word")"
  fi
done
command -v -- "${impl[0]}" > /dev/null || setup_error "the command '${impl[0]}' can't be run (not found or not executable)."
[[ -f "$fake" && -x "$fake" ]] || setup_error "$fake is missing or not executable."

tmp="$(mktemp -d)" || setup_error "can't make a temporary folder."
trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin" "$tmp/cwd" "$tmp/ghconfig" || setup_error "can't make folders under $tmp."
ln -s "$fake" "$tmp/bin/gh" || setup_error "can't link the fake gh into $tmp/bin."

shopt -s nullglob
cases=("$cases_dir"/*/)
[[ ${#cases[@]} -gt 0 ]] || setup_error "no cases under $cases_dir."

# A case's files must be well formed before its run means anything: a missing or broken one is a setup error, not a
# difference between implementations. Record mode needs only gh/, and checks the files a case already has.
check_case() {
  local dir="$1" name="$2" file pattern n status
  [[ -d "$dir/gh" ]] || setup_error "$name has no gh/ folder."
  for file in "$dir/args" "$dir"/expected.*; do
    [[ -f "$file" ]] || continue
    if grep -q $'\r' "$file"; then
      setup_error "$name/$(basename -- "$file") holds a carriage return."
    fi
  done
  if [[ -z "$record" ]]; then
    for file in expected.stdout expected.code expected.calls expected.stderr; do
      [[ -f "$dir/$file" ]] || setup_error "$name has no $file."
    done
  fi
  if [[ -f "$dir/expected.code" && ! "$(cat "$dir/expected.code")" =~ ^[0-9]+$ ]]; then
    setup_error "$name/expected.code is not an exit code."
  fi
  [[ -f "$dir/expected.stderr" ]] || return 0
  n=0
  while IFS= read -r pattern || [[ -n "$pattern" ]]; do
    n=$((n + 1))
    [[ -n "$pattern" ]] || setup_error "$name/expected.stderr line $n is blank (it would match any line)."
    status=0
    grep -qE -- "$pattern" < /dev/null 2> /dev/null || status=$?
    [[ $status -ne 2 ]] || setup_error "$name/expected.stderr line $n is not a valid ERE: $pattern"
  done < "$dir/expected.stderr"
}

# stderr_problems <patterns file> <stderr file>: prints the ways stderr doesn't fit its patterns, nothing when it does.
stderr_problems() {
  local -a patterns=() lines=()
  local i
  mapfile -t patterns < "$1"
  mapfile -t lines < "$2"
  if [[ ${#lines[@]} -ne ${#patterns[@]} ]]; then
    echo "${#lines[@]} stderr line(s), expected ${#patterns[@]}:"
    cat -- "$2"
    return
  fi
  for ((i = 0; i < ${#patterns[@]}; i++)); do
    grep -qE -- "${patterns[i]}" <<< "${lines[i]}" || echo "stderr line $((i + 1)) '${lines[i]}' doesn't match '${patterns[i]}'"
  done
}

failed=()
for case_dir in "${cases[@]}"; do
  case_dir="${case_dir%/}"
  name="$(basename -- "$case_dir")"
  check_case "$case_dir" "$name"
  args=()
  if [[ -s "$case_dir/args" ]]; then
    mapfile -t args < "$case_dir/args"
  fi
  : > "$tmp/calls"
  code=0
  (cd "$tmp/cwd" && timeout -k 5 "$limit" env -u GH_TOKEN -u GITHUB_TOKEN -u GH_ENTERPRISE_TOKEN -u GITHUB_ENTERPRISE_TOKEN \
    PATH="$tmp/bin:$PATH" GH_CONFIG_DIR="$tmp/ghconfig" LC_ALL=C.UTF-8 PYTHONUTF8=1 TZ=UTC \
    FAKE_GH_DIR="$case_dir/gh" FAKE_GH_LOG="$tmp/calls" \
    "${impl[@]}" ${args[@]+"${args[@]}"} < /dev/null > "$tmp/stdout" 2> "$tmp/stderr") || code=$?

  problems=()
  if [[ $code -eq 124 || $code -eq 137 ]]; then
    problems+=("timed out after $limit second(s) (exit $code)")
  fi
  if grep -q '^UNMATCHED ' "$tmp/calls"; then
    problems+=("a gh call no fixture answers (the implementation's call differs from the reference's): $(grep '^UNMATCHED ' "$tmp/calls" | head -3)")
  fi

  if [[ -n "$record" ]]; then
    # A run the reference can't finish against its fixtures is never recorded: its expected.* would bake the gap in.
    if [[ ${#problems[@]} -gt 0 ]]; then
      echo "FAIL $name: not recorded"
      printf '     %s\n' "${problems[@]}"
      failed+=("$name")
      continue
    fi
    cp "$tmp/stdout" "$case_dir/expected.stdout"
    printf '%s\n' "$code" > "$case_dir/expected.code"
    cp "$tmp/calls" "$case_dir/expected.calls"
    if [[ ! -e "$case_dir/expected.stderr" ]]; then
      sed -e 's#[][\.*^$+?(){}|]#\\&#g' -e 's/^/^/' -e 's/$/$/' "$tmp/stderr" > "$case_dir/expected.stderr"
    else
      stderr_report="$(stderr_problems "$case_dir/expected.stderr" "$tmp/stderr")"
      if [[ -n "$stderr_report" ]]; then
        echo "FAIL $name: recorded, but the kept expected.stderr no longer fits"
        printf '     %s\n' "$stderr_report"
        failed+=("$name")
        continue
      fi
    fi
    echo "recorded $name (exit $code)"
    continue
  fi

  want_code="$(cat "$case_dir/expected.code")"
  [[ "$code" == "$want_code" ]] || problems+=("exit $code, expected $want_code")
  diff -u "$case_dir/expected.stdout" "$tmp/stdout" > "$tmp/diff" || problems+=("stdout differs:"$'\n'"$(cat "$tmp/diff")")
  diff -u "$case_dir/expected.calls" "$tmp/calls" > "$tmp/diff" || problems+=("gh calls differ:"$'\n'"$(cat "$tmp/diff")")
  stderr_report="$(stderr_problems "$case_dir/expected.stderr" "$tmp/stderr")"
  [[ -z "$stderr_report" ]] || problems+=("$stderr_report")

  if [[ ${#problems[@]} -eq 0 ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name"
    printf '     %s\n' "${problems[@]}"
    failed+=("$name")
  fi
done

if [[ ${#failed[@]} -gt 0 ]]; then
  echo "status-parity: ${#failed[@]} case(s) failed against ${impl[*]}: ${failed[*]}" >&2
  exit 1
fi
if [[ -n "$record" ]]; then
  echo "status-parity: ${#cases[@]} case(s) recorded from ${impl[*]}"
else
  echo "status-parity: ${#cases[@]} case(s) pass against: ${impl[*]}"
fi
