#!/usr/bin/env bash
# The parity suites: a script's behaviour through its command line only, so the same cases judge any implementation of it
# (its C# port, ADR 0017) against the Python. One runner and one fake gh serve every suite; a suite is a folder
# tests/parity/<suite>/, named for the script it was recorded from (github/<suite>.py, else the Template's
# template/owned/.github/scripts/<suite>.py), and its README.md says what its cases cover and what they leave out on purpose.
#
#   tests/parity.test.sh <suite>                    run the suite's cases against python3 <that script>
#   tests/parity.test.sh <suite> <command> [arg]... run them against another implementation
#   PARITY_RECORD=1 tests/parity.test.sh <suite>
#                                                   record the cases from the Python reference (no command is accepted:
#                                                   a port never records its own oracle); review the diff before committing
#
# Each case is a folder under tests/parity/<suite>/cases/:
#
#   args             the implementation's arguments, one per line, taken verbatim (a blank line is an empty argument;
#                    an empty or missing file: none)
#   gh/              the fixtures tests/parity/fake-gh.sh answers the `gh` calls from (see its header for what an
#                    implementation must send), each read by a call in expected.calls; a case with no fixtures keeps
#                    an empty .gitkeep there, since git keeps no empty folder
#   expected.stdout  stdout, byte for byte
#   expected.code    the exit code
#   expected.stderr  one ERE per stderr line, in order: stderr has exactly that many lines, each matching its pattern
#                    (stderr carries the error text of the language, so only its shape is compared); no line is blank.
#                    A line that is exactly ... stands for any number of lines, none included, so a usage block whose
#                    wrapping is the argument parser's own is matched by its first line and ... alone
#   expected.calls   the fake's log of the `gh` calls made, in order: each call's fixture key, and for a `gh pr` call or a
#                    REST call with --paginate or --jq the key and the call's whole argv; the queries a run costs, and
#                    what it writes, are behaviour too
#
# and, when the script reads more than its arguments and gh, any of:
#
#   stdin            the implementation's stdin, byte for byte (missing: /dev/null)
#   env              NAME=value lines, set for the run on top of the fixed environment below (a blank line is ignored)
#   tree/            files copied into the folder the implementation runs from (missing: it starts empty)
#   setup.sh         a bash script run in that folder before the implementation (a .sh, so the gate's shellcheck and shfmt read it), to build what a file can't hold (a git
#                    repo with a local origin); it runs with PARITY_SCRATCH, a folder outside the run's for what the
#                    run mustn't see as its own (the bare origin), and the git identity and dates fixed, so its commits
#                    get the same hashes everywhere and the gh fixtures can name them
#   expected.tree/   the run's folder afterwards, every file byte for byte (a .git anywhere is left out); missing: the run
#                    must leave the folder holding nothing but a .git, so a script that writes no file is held to that
#
# Neither args, env nor an expected.* file may hold a carriage return (a setup error); stdin and a tree may, and
# tests/parity/.gitattributes stores every case file as it is, so a clone keeps them. Record mode writes
# expected.stdout, expected.code, expected.calls and expected.tree/ (removed when the run leaves no file). It writes a
# new case's expected.stderr with each line matched literally, for someone to loosen to the shape that matters; an existing
# one holds that loosening, so it is kept, and the run fails when it no longer fits. A case whose run makes a gh call the
# fake can't answer, or times out, is never recorded.
#
# Each case's run starts in a new temporary folder of its own (empty, or the case's tree/ and setup.sh), with
# stdin from /dev/null or the case's stdin, the fake gh first on PATH, no GitHub token in its environment and GH_CONFIG_DIR
# a new empty folder (nothing an earlier case wrote is left), no repository, Anthropic key or model the caller set (REPOSITORY, GITHUB_REPOSITORY, ANTHROPIC_API_KEY,
# ANTHROPIC_MODEL: a case's env sets them), no git repository or config the caller set (GIT_DIR and the rest unset,
# GIT_CONFIG_GLOBAL=/dev/null, GIT_CONFIG_NOSYSTEM=1), no colour or terminal width the caller set (NO_COLOR=1, TERM=dumb),
# LC_ALL=C.UTF-8 (best-effort: macOS has no such locale, so PYTHONUTF8=1 is set too), PYTHONDONTWRITEBYTECODE=1 (so a
# script's imports leave no __pycache__ beside it), TZ=UTC, and PARITY_TIMEOUT seconds (default 60) before it counts as hung. A command word that looks like a path (it holds a
# /, or after the first word a .) and names an existing file or folder is made absolute first, as is the value of a later
# --option=value word, so `--file src/Status.cs` works from anywhere; the first word is looked up on PATH unless it holds a /. Build a compiled implementation once before running the suite (a
# file-based C# app with `dotnet build <file>.cs`, then the command `dotnet run --no-build --file <file>.cs --`), so the
# build isn't timed inside the first case and two runs of the suite don't build it at once.
#
# Exit 0 when every case passes, 1 when one fails, 2 on a usage or setup error (a case missing a file or holding a bad
# one, a command that can't be run, a temporary folder that can't be made). Needs bash 4.4 or later, coreutils (mktemp,
# ln, cp, cat, sort, timeout), findutils (find), diff, grep and sed, and git for a case with a setup.sh; the fake gh needs
# bash and cat, and mktemp, rm and jq (any version) for a REST call with --jq.
set -euo pipefail

setup_error() {
  echo "parity: $*" >&2
  exit 2
}

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
skill_dir="$(dirname -- "$here")"
fake="$here/parity/fake-gh.sh"

[[ $# -gt 0 ]] || setup_error "name a suite: tests/parity.test.sh <suite> [command [arg]...]."
suite="$1"
shift
[[ "$suite" =~ ^[a-z][a-z0-9_]*$ ]] || setup_error "'$suite' is not a suite name (a script's name: lowercase letters, digits and _)."
cases_dir="$here/parity/$suite/cases"
[[ -d "$cases_dir" ]] || setup_error "no suite '$suite': $cases_dir is not a folder."

record="${PARITY_RECORD:-}"
if [[ -n "$record" && "$record" != 1 ]]; then
  setup_error "PARITY_RECORD must be 1 or unset, not '$record'."
fi
if [[ -n "$record" && $# -gt 0 ]]; then
  setup_error "PARITY_RECORD records from the Python reference only; run it with no command."
fi
limit="${PARITY_TIMEOUT:-60}"
[[ "$limit" =~ ^[1-9][0-9]*$ ]] || setup_error "PARITY_TIMEOUT must be a whole number of seconds, not '$limit'."
command -v timeout > /dev/null || setup_error "timeout (coreutils) is not on PATH."

if [[ $# -gt 0 ]]; then
  impl=("$@")
elif [[ -f "$skill_dir/github/$suite.py" ]]; then
  impl=(python3 "$skill_dir/github/$suite.py")
elif [[ -f "$skill_dir/template/owned/.github/scripts/$suite.py" ]]; then
  impl=(python3 "$skill_dir/template/owned/.github/scripts/$suite.py")
else
  setup_error "no script for suite '$suite': neither github/$suite.py nor template/owned/.github/scripts/$suite.py is a file."
fi
# The command runs from a temporary folder, so a relative path in it would name nothing there.
# Only a word that looks like a path (it holds a /, or after the first word a .) is rewritten, so `dotnet run` stays as it
# is when the caller's folder holds a `run`; the value of a later --option=value word is treated the same way.
for i in "${!impl[@]}"; do
  word="${impl[i]}" option=""
  if [[ $i -gt 0 && "$word" == --?*=* ]]; then
    option="${word%%=*}=" word="${word#*=}"
  fi
  [[ -n "$word" && "$word" != /* && "$word" != -* ]] || continue
  [[ "$word" == */* || ($i -gt 0 && "$word" == *.*) ]] || continue
  if [[ -e "$word" ]]; then
    impl[i]="$option$(CDPATH='' cd -- "$(dirname -- "$word")" && pwd)/$(basename -- "$word")"
  fi
done
command -v -- "${impl[0]}" > /dev/null || setup_error "the command '${impl[0]}' can't be run (not found or not executable)."
[[ -f "$fake" && -x "$fake" ]] || setup_error "$fake is missing or not executable."

tmp="$(mktemp -d)" || setup_error "can't make a temporary folder."
trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin" "$tmp/ghconfig" || setup_error "can't make folders under $tmp."
ln -s "$fake" "$tmp/bin/gh" || setup_error "can't link the fake gh into $tmp/bin."

shopt -s nullglob
cases=("$cases_dir"/*/)
[[ ${#cases[@]} -gt 0 ]] || setup_error "no cases under $cases_dir."

# A case's files must be well formed before its run means anything: a missing or broken one is a setup error, not a
# difference between implementations. Record mode needs only gh/, and checks the files a case already has.
check_case() {
  local dir="$1" name="$2" file pattern n status
  [[ -d "$dir/gh" ]] || setup_error "$name has no gh/ folder."
  for file in "$dir/args" "$dir/env" "$dir"/expected.*; do
    [[ -f "$file" ]] || continue
    if grep -q $'\r' "$file"; then
      setup_error "$name/$(basename -- "$file") holds a carriage return."
    fi
  done
  for file in tree expected.tree; do
    [[ ! -e "$dir/$file" || -d "$dir/$file" ]] || setup_error "$name/$file is not a folder."
  done
  for file in stdin env setup.sh; do
    [[ ! -e "$dir/$file" || -f "$dir/$file" ]] || setup_error "$name/$file is not a file."
  done
  [[ ! -e "$dir/setup" ]] || setup_error "$name/setup is not read: a case's setup script is setup.sh."
  if [[ -f "$dir/env" ]]; then
    n=0
    while IFS= read -r pattern || [[ -n "$pattern" ]]; do
      n=$((n + 1))
      [[ -z "$pattern" || "$pattern" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] || setup_error "$name/env line $n is not NAME=value: $pattern"
    done < "$dir/env"
  fi
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

# stderr_fits <patterns file> <stderr file>: whether stderr fits patterns that hold a ... line, which matches any number of lines
# (none included). Glob matching with one backtrack point: a pattern line matches one line, and on a mismatch the last ...
# takes one more line.
stderr_fits() {
  local -a patterns=() lines=()
  local p=0 l=0 star=-1 mark=0
  mapfile -t patterns < "$1"
  mapfile -t lines < "$2"
  while ((l < ${#lines[@]})); do
    if ((p < ${#patterns[@]})) && [[ "${patterns[p]}" == ... ]]; then
      star=$p mark=$l p=$((p + 1))
    elif ((p < ${#patterns[@]})) && grep -qE -- "${patterns[p]}" <<< "${lines[l]}"; then
      p=$((p + 1)) l=$((l + 1))
    elif ((star >= 0)); then
      mark=$((mark + 1)) p=$((star + 1)) l=$mark
    else
      return 1
    fi
  done
  while ((p < ${#patterns[@]})) && [[ "${patterns[p]}" == ... ]]; do p=$((p + 1)); done
  ((p == ${#patterns[@]}))
}

# stderr_problems <patterns file> <stderr file>: prints the ways stderr doesn't fit its patterns, nothing when it does.
stderr_problems() {
  local -a patterns=() lines=()
  local i
  mapfile -t patterns < "$1"
  mapfile -t lines < "$2"
  if grep -qx -- '\.\.\.' "$1"; then
    if ! stderr_fits "$1" "$2"; then
      echo "stderr doesn't fit its patterns (a ... line stands for any number of lines):"
      cat -- "$2"
    fi
    return
  fi
  if [[ ${#lines[@]} -ne ${#patterns[@]} ]]; then
    echo "${#lines[@]} stderr line(s), expected ${#patterns[@]}:"
    cat -- "$2"
    return
  fi
  for ((i = 0; i < ${#patterns[@]}; i++)); do
    grep -qE -- "${patterns[i]}" <<< "${lines[i]}" || echo "stderr line $((i + 1)) '${lines[i]}' doesn't match '${patterns[i]}'"
  done
}

# tree_problems <case folder> <run folder>: prints how the run's folder differs from the case's expected.tree/ (or, with
# none, the files it left), nothing when it fits. A .git is left out either way.
tree_problems() {
  local left
  if [[ -d "$1/expected.tree" ]]; then
    diff -r -x .git -- "$1/expected.tree" "$2" > "$tmp/diff" 2>&1 || echo "the files differ:"$'\n'"$(cat "$tmp/diff")"
    return
  fi
  left="$(cd -- "$2" && find . -mindepth 1 -maxdepth 1 ! -name .git | sort)"
  [[ -z "$left" ]] || echo "the run left files, and the case has no expected.tree/:"$'\n'"$left"
}

# What git sees in the case's setup and in the run: no repository, config or object store the caller set (a hook sets
# GIT_DIR and the rest), no repository above the run's folder, and SHA-1 object names whatever the caller or git's own
# default asks for, since the gh fixtures name the setup's commits by hash. Every -u comes before the first NAME=value,
# as env reads its options before its assignments.
git_env=(-u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE -u GIT_PREFIX -u GIT_COMMON_DIR -u GIT_OBJECT_DIRECTORY
  -u GIT_ALTERNATE_OBJECT_DIRECTORIES -u GIT_NAMESPACE -u GIT_CONFIG -u GIT_CONFIG_PARAMETERS -u GIT_CONFIG_COUNT -u GIT_TEMPLATE_DIR
  GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_CEILING_DIRECTORIES="$tmp/run" GIT_TERMINAL_PROMPT=0
  GIT_DEFAULT_HASH=sha1)
# The setup's commits: the same identity and dates everywhere, so their hashes are too.
git_identity=(GIT_AUTHOR_NAME=Parity GIT_AUTHOR_EMAIL=parity@example.com GIT_AUTHOR_DATE='2026-01-01T00:00:00Z'
  GIT_COMMITTER_NAME=Parity GIT_COMMITTER_EMAIL=parity@example.com GIT_COMMITTER_DATE='2026-01-01T00:00:00Z')

failed=()
for case_dir in "${cases[@]}"; do
  case_dir="${case_dir%/}"
  name="$(basename -- "$case_dir")"
  check_case "$case_dir" "$name"
  args=()
  if [[ -s "$case_dir/args" ]]; then
    mapfile -t args < "$case_dir/args"
  fi
  case_env=()
  if [[ -s "$case_dir/env" ]]; then
    while IFS= read -r line || [[ -n "$line" ]]; do
      [[ -z "$line" ]] || case_env+=("$line")
    done < "$case_dir/env"
  fi
  stdin=/dev/null
  [[ ! -f "$case_dir/stdin" ]] || stdin="$case_dir/stdin"
  run="$tmp/run/cwd"
  rm -rf "$tmp/run"
  mkdir -p "$run" "$tmp/run/scratch" || setup_error "can't make the run's folders under $tmp."
  if [[ -d "$case_dir/tree" ]]; then
    cp -R "$case_dir/tree/." "$run/" || setup_error "can't copy $name/tree into the run's folder."
  fi
  if [[ -f "$case_dir/setup.sh" ]]; then
    (cd "$run" && env "${git_env[@]}" "${git_identity[@]}" PARITY_SCRATCH="$tmp/run/scratch" \
      bash "$case_dir/setup.sh" < /dev/null > "$tmp/setup.log" 2>&1) \
      || setup_error "$name/setup.sh failed:"$'\n'"$(cat "$tmp/setup.log")"
  fi
  : > "$tmp/calls"
  # Each case starts with an empty GH_CONFIG_DIR, whatever an earlier case's run left in it (its folder is new above).
  rm -rf -- "$tmp/ghconfig"
  mkdir "$tmp/ghconfig" || setup_error "can't make folders under $tmp."
  code=0
  (cd "$run" && timeout -k 5 "$limit" env -u GH_TOKEN -u GITHUB_TOKEN -u GH_ENTERPRISE_TOKEN -u GITHUB_ENTERPRISE_TOKEN \
    -u FORCE_COLOR -u CLICOLOR_FORCE -u PYTHON_COLORS -u COLUMNS -u LINES \
    -u REPOSITORY -u GITHUB_REPOSITORY -u ANTHROPIC_API_KEY -u ANTHROPIC_MODEL "${git_env[@]}" NO_COLOR=1 TERM=dumb \
    PATH="$tmp/bin:$PATH" GH_CONFIG_DIR="$tmp/ghconfig" LC_ALL=C.UTF-8 PYTHONUTF8=1 PYTHONDONTWRITEBYTECODE=1 TZ=UTC \
    FAKE_GH_DIR="$case_dir/gh" FAKE_GH_LOG="$tmp/calls" ${case_env[@]+"${case_env[@]}"} \
    "${impl[@]}" ${args[@]+"${args[@]}"} < "$stdin" > "$tmp/stdout" 2> "$tmp/stderr") || code=$?

  problems=()
  if [[ $code -eq 124 || $code -eq 137 ]]; then
    problems+=("timed out after $limit second(s) (exit $code)")
  fi
  if grep -q '^UNMATCHED ' "$tmp/calls"; then
    problems+=("a gh call the fake couldn't answer (no fixture for it, an argument it doesn't know or an unreadable fixture; from a port, its call likely differs from the reference's): $(grep '^UNMATCHED ' "$tmp/calls" | head -3)")
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
    rm -rf "$case_dir/expected.tree"
    if [[ -n "$(cd -- "$run" && find . -mindepth 1 -maxdepth 1 ! -name .git)" ]]; then
      cp -R "$run/." "$case_dir/expected.tree/" || setup_error "can't record $name/expected.tree."
      find "$case_dir/expected.tree" -name .git -prune -exec rm -rf {} + || setup_error "can't drop .git from $name/expected.tree."
    fi
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
  tree_report="$(tree_problems "$case_dir" "$run")"
  [[ -z "$tree_report" ]] || problems+=("$tree_report")

  if [[ ${#problems[@]} -eq 0 ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name"
    printf '     %s\n' "${problems[@]}"
    failed+=("$name")
  fi
done

if [[ ${#failed[@]} -gt 0 ]]; then
  echo "parity: $suite: ${#failed[@]} case(s) failed against ${impl[*]}: ${failed[*]}" >&2
  exit 1
fi
if [[ -n "$record" ]]; then
  echo "parity: $suite: ${#cases[@]} case(s) recorded from ${impl[*]}"
else
  echo "parity: $suite: ${#cases[@]} case(s) pass against: ${impl[*]}"
fi
