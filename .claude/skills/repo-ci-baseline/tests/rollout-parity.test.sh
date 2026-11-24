#!/usr/bin/env bash
# The rollout parity suite: rollout.sh's behaviour through its command line only, so the same cases judge rollout.py with
# auto_rollout.py and their C# port (ADR 0017). It has a runner of its own, beside tests/parity.test.sh's, because rollout
# calls reapply.sh and land.sh by absolute path beside itself and keeps its lock, logs and sessions under ~: each run is of a
# temporary copy of the skill folder, holding stubs for those two, with a HOME of its own. Its cases are in
# tests/rollout-parity/cases/, and tests/rollout-parity/README.md says what they cover and leave out on purpose, --auto's
# waits among them (a review session's 180 minutes, the 10-second waits and a record's 600 seconds have no seam).
#
#   tests/rollout-parity.test.sh                    run the cases against the copy's rollout.sh (the reference: whatever
#                                                   the skill's rollout.sh runs)
#   tests/rollout-parity.test.sh <command> [arg]... run them against another implementation; a word that names a file
#                                                   in the skill folder (github/rollout.py, say) names the copy's, so it
#                                                   finds the stubs and the fixed repos.txt as the reference does
#   PARITY_RECORD=1 tests/rollout-parity.test.sh   record the cases from the reference (no command is accepted, so a port
#                                                   under test never records its own oracle); review the diff before
#                                                   committing
#
# The copy of the skill folder is made once, in a new temporary folder: rollout.sh and github/ (without github/tests/, bin/,
# obj/ or __pycache__/), github/repos.txt replaced by tests/rollout-parity/repos.txt, and reapply.sh and land.sh linked to
# tests/rollout-parity/fake-tool.sh, the stubs. Nothing else of the skill is in it, so a command word that names another
# file of the skill's is a setup error.
#
# Each case is a folder under tests/rollout-parity/cases/:
#
#   args             the implementation's arguments, one per line, taken verbatim (a blank line is an empty argument; an
#                    empty or missing file: none)
#   env              NAME=value lines, set for the run on top of the fixed environment below (a blank line is ignored)
#   home/            the files the run's HOME starts with (a checkout under ~/github is a folder with a .gitkeep, since git
#                    keeps no empty folder); missing: it starts empty
#   gh/              the fixtures tests/parity/fake-gh.sh answers the gh calls from (see its header); a case with none keeps
#                    an empty .gitkeep there
#   fakes/           what each stub or fake tool does when called (fake-tool.sh's header): <tool>.sh, or <tool>.<n>.sh for
#                    its nth call, a bash script run with the call's arguments; tools are reapply.sh, land.sh, git and
#                    claude. A case that calls none keeps an empty .gitkeep there
#   expected.stdout  stdout, byte for byte
#   expected.code    the exit code
#   expected.stderr  one ERE per stderr line, in order, as tests/parity.test.sh holds it (a line that is exactly ... stands
#                    for any number of lines; no line is blank)
#   expected.calls   the run's call log, in order: each gh call as the fake gh logs it, and each call of a stub or fake tool
#                    as its name and argv (fake-tool.sh's header)
#   expected.home/   HOME afterwards, every file byte for byte and every symlink by its target; missing: the run must leave
#                    HOME as home/ gave it, so a run that writes nothing there is held to that
#
# Before stdout, stderr and the call log are compared, each run's HOME in them is written ~ and the copy of the skill folder
# <skill>, since both are temporary folders whose names change from run to run. A NUL byte in them is dropped.
#
# Each case's run starts in its HOME, a new folder (canonical, symlinks resolved) holding the case's home/, with stdin from
# /dev/null, the fake gh and git and claude (fake-tool.sh) first on PATH, no GitHub token, Anthropic key or model, repository
# or PYTHONPATH in its environment, GH_CONFIG_DIR a new empty folder outside HOME, no XDG folder the caller set, no git
# repository or config the caller set (GIT_DIR and the rest unset, GIT_CONFIG_GLOBAL=/dev/null, GIT_CONFIG_NOSYSTEM=1), no
# colour or terminal width the caller set (NO_COLOR=1, TERM=dumb), LC_ALL=C.UTF-8 and PYTHONUTF8=1, PYTHONDONTWRITEBYTECODE=1
# (so the copy's imports leave no __pycache__), TZ=UTC, dotnet's CLI usage data, first-run setup and logo off, and
# PARITY_TIMEOUT seconds (default 60) before it counts as hung. A command word that looks like a path (it holds a /, or
# after the first word a .) and names an existing file or folder is made absolute first, as is the value of a later
# --option=value word; the first word is looked up on PATH unless it holds a /.
#
# Exit 0 when every case passes, 1 when one fails, 2 on a usage or setup error (a case missing a file or holding a bad one,
# a command that can't be run, a temporary folder that can't be made). Needs bash 4.4 or later, coreutils (mktemp, mkdir,
# rm, ln, cp, cat, tr, env, basename, dirname, timeout), findutils (find), a diff with --no-dereference (GNU diffutils 3.3 or
# later), grep and sed; the reference needs python3.
set -euo pipefail

setup_error() {
  echo "rollout parity: $*" >&2
  exit 2
}

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
skill_dir="$(dirname -- "$here")"
suite_dir="$here/rollout-parity"
cases_dir="$suite_dir/cases"
fake_gh="$here/parity/fake-gh.sh"
fake_tool="$suite_dir/fake-tool.sh"
[[ -d "$cases_dir" ]] || setup_error "$cases_dir is not a folder."
for file in "$fake_gh" "$fake_tool"; do
  [[ -f "$file" && -x "$file" ]] || setup_error "$file is missing or not executable."
done
[[ -f "$suite_dir/repos.txt" ]] || setup_error "$suite_dir/repos.txt, the copy's fixed github/repos.txt, is missing."
[[ -f "$here/parity/stderr.sh" ]] || setup_error "$here/parity/stderr.sh is missing."
# shellcheck source=/dev/null # tests/parity/stderr.sh, which shellcheck reads on its own
. "$here/parity/stderr.sh"

record="${PARITY_RECORD:-}"
if [[ -n "$record" && "$record" != 1 ]]; then
  setup_error "PARITY_RECORD must be 1 or unset, not '$record'."
fi
if [[ -n "$record" && $# -gt 0 ]]; then
  setup_error "PARITY_RECORD records from the reference (the copy's rollout.sh) only; run it with no command."
fi
limit="${PARITY_TIMEOUT:-60}"
[[ "$limit" =~ ^[1-9][0-9]*$ ]] || setup_error "PARITY_TIMEOUT must be a whole number of seconds, not '$limit'."
command -v timeout > /dev/null || setup_error "timeout (coreutils) is not on PATH."
[[ -f "$skill_dir/rollout.sh" && -d "$skill_dir/github" ]] || setup_error "$skill_dir holds no rollout.sh and github/ to copy."

tmp="$(mktemp -d)" || setup_error "can't make a temporary folder."
trap 'rm -rf "$tmp"' EXIT
tmp="$(CDPATH='' cd -- "$tmp" && pwd -P)" || setup_error "can't resolve the temporary folder."
layout="$tmp/skill"
mkdir "$layout" "$tmp/bin" || setup_error "can't make folders under $tmp."
{
  cp -p "$skill_dir/rollout.sh" "$layout/" \
    && cp -R "$skill_dir/github" "$layout/" \
    && rm -rf "$layout/github/tests" "$layout/github/bin" "$layout/github/obj" \
    && find "$layout" -name __pycache__ -prune -exec rm -rf {} + \
    && cp "$suite_dir/repos.txt" "$layout/github/repos.txt" \
    && ln -s "$fake_tool" "$layout/reapply.sh" \
    && ln -s "$fake_tool" "$layout/land.sh" \
    && ln -s "$fake_gh" "$tmp/bin/gh" \
    && ln -s "$fake_tool" "$tmp/bin/git" \
    && ln -s "$fake_tool" "$tmp/bin/claude"
} || setup_error "can't make the copy of the skill folder under $tmp."

if [[ $# -gt 0 ]]; then
  impl=("$@")
else
  impl=("$layout/rollout.sh")
fi
# The command runs from the case's HOME, so a relative path in it would name nothing there, and one in the skill folder must
# name the copy's file, which finds the stubs beside it. Only a word that looks like a path (it holds a /, or after the first
# word a .) is rewritten, so `dotnet run` stays as it is when the caller's folder holds a `run`; the value of a later
# --option=value word is treated the same way.
for i in "${!impl[@]}"; do
  word="${impl[i]}" option=""
  if [[ $i -gt 0 && "$word" == --?*=* ]]; then
    option="${word%%=*}=" word="${word#*=}"
  fi
  [[ -n "$word" && "$word" != -* ]] || continue
  [[ "$word" == */* || ($i -gt 0 && "$word" == *.*) ]] || continue
  [[ -e "$word" ]] || continue
  path="$(CDPATH='' cd -- "$(dirname -- "$word")" && pwd)/$(basename -- "$word")"
  if [[ "$path" == "$skill_dir"/* ]]; then
    [[ -e "$layout/${path#"$skill_dir"/}" ]] \
      || setup_error "$word is in the skill folder but not in its copy (only rollout.sh and github/ are copied)."
    path="$layout/${path#"$skill_dir"/}"
  fi
  impl[i]="$option$path"
done
command -v -- "${impl[0]}" > /dev/null || setup_error "the command '${impl[0]}' can't be run (not found or not executable)."
# shown: the command as the summary names it, the copy written <skill>.
shown="${impl[*]}"
shown="${shown//"$layout"/<skill>}"

shopt -s nullglob
cases=("$cases_dir"/*/)
[[ ${#cases[@]} -gt 0 ]] || setup_error "no cases under $cases_dir."

# A case's files must be well formed before its run means anything: a missing or broken one is a setup error, not a
# difference between implementations. Record mode needs only gh/ and fakes/, and checks the files a case already has.
check_case() {
  local dir="$1" name="$2" file pattern n status
  for file in gh fakes; do
    [[ -d "$dir/$file" ]] || setup_error "$name has no $file/ folder."
  done
  for file in home expected.home; do
    [[ ! -e "$dir/$file" || -d "$dir/$file" ]] || setup_error "$name/$file is not a folder."
  done
  for file in "$dir/args" "$dir/env" "$dir"/expected.*; do
    [[ -f "$file" && "$file" != "$dir/expected.stdout" ]] || continue
    if grep -q $'\r' "$file"; then
      setup_error "$name/$(basename -- "$file") holds a carriage return."
    fi
  done
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

# normalize <file>: the run's HOME written ~ and the copy of the skill folder <skill>, in place.
normalize() {
  local text
  text="$(
    tr -d '\0' < "$1"
    printf x
  )"
  text="${text%x}"
  text="${text//"$layout"/<skill>}"
  text="${text//"$home"/\~}"
  printf '%s' "$text" > "$1"
}

# home_problems <case folder>: prints how the run's HOME differs from the case's expected.home/ (or, with none, its home/),
# nothing when it fits. A symlink is compared by its target and not followed.
home_problems() {
  local want="$1/expected.home"
  [[ -d "$want" ]] || want="$1/home"
  [[ -d "$want" ]] || want="$tmp/empty"
  diff -r --no-dereference -- "$want" "$home" > "$tmp/diff" 2>&1 || echo "HOME differs:"$'\n'"$(cat "$tmp/diff")"
}

# What git, should a fixture run the real one, sees: no repository or config the caller set, none above HOME. Every -u
# comes before the first NAME=value, as env reads its options before its assignments.
git_env=(-u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE -u GIT_PREFIX -u GIT_COMMON_DIR -u GIT_OBJECT_DIRECTORY
  -u GIT_ALTERNATE_OBJECT_DIRECTORIES -u GIT_NAMESPACE -u GIT_CONFIG -u GIT_CONFIG_PARAMETERS -u GIT_CONFIG_COUNT -u GIT_TEMPLATE_DIR
  GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_CEILING_DIRECTORIES="$tmp/run" GIT_TERMINAL_PROMPT=0)

mkdir "$tmp/empty" || setup_error "can't make folders under $tmp."
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
  rm -rf "$tmp/run"
  home="$tmp/run/home"
  mkdir -p "$home" "$tmp/run/ghconfig" || setup_error "can't make the run's folders under $tmp."
  if [[ -d "$case_dir/home" ]]; then
    cp -R "$case_dir/home/." "$home/" || setup_error "can't copy $name/home into the run's HOME."
  fi
  : > "$tmp/run/calls"
  code=0
  (cd "$home" && timeout -k 5 "$limit" env -u GH_TOKEN -u GITHUB_TOKEN -u GH_ENTERPRISE_TOKEN -u GITHUB_ENTERPRISE_TOKEN \
    -u FORCE_COLOR -u CLICOLOR_FORCE -u PYTHON_COLORS -u COLUMNS -u LINES -u PYTHONPATH \
    -u XDG_CONFIG_HOME -u XDG_DATA_HOME -u XDG_STATE_HOME -u XDG_CACHE_HOME -u XDG_RUNTIME_DIR \
    -u REPOSITORY -u GITHUB_REPOSITORY -u ANTHROPIC_API_KEY -u ANTHROPIC_MODEL "${git_env[@]}" NO_COLOR=1 TERM=dumb \
    HOME="$home" PATH="$tmp/bin:$PATH" GH_CONFIG_DIR="$tmp/run/ghconfig" LC_ALL=C.UTF-8 PYTHONUTF8=1 PYTHONDONTWRITEBYTECODE=1 TZ=UTC \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 \
    FAKE_GH_DIR="$case_dir/gh" FAKE_GH_LOG="$tmp/run/calls" FAKE_TOOL_DIR="$case_dir/fakes" FAKE_HOME="$home" \
    ${case_env[@]+"${case_env[@]}"} "${impl[@]}" ${args[@]+"${args[@]}"} < /dev/null > "$tmp/run/stdout" 2> "$tmp/run/stderr") || code=$?
  for file in stdout stderr calls; do
    normalize "$tmp/run/$file"
  done

  problems=()
  if [[ $code -eq 124 || $code -eq 137 ]]; then
    problems+=("timed out after $limit second(s) (exit $code)")
  fi
  if grep -q '^UNMATCHED ' "$tmp/run/calls"; then
    problems+=("a call no fixture answers (from a port, its call likely differs from the reference's): $(grep '^UNMATCHED ' "$tmp/run/calls" | head -3)")
  fi

  if [[ -n "$record" ]]; then
    # A run the reference can't finish against its fixtures is never recorded: its expected.* would bake the gap in.
    if [[ ${#problems[@]} -gt 0 ]]; then
      echo "FAIL $name: not recorded"
      printf '     %s\n' "${problems[@]}"
      failed+=("$name")
      continue
    fi
    rm -rf "$case_dir/expected.home"
    if [[ -n "$(home_problems "$case_dir")" ]]; then
      # git stores no empty folder, so one in expected.home/ would be missing from a fresh clone and fail the case there.
      empty="$(cd -- "$home" && find . -mindepth 1 -type d -empty -print)"
      if [[ -n "$empty" ]]; then
        echo "FAIL $name: not recorded: the run left an empty folder in HOME, which git can't store:"
        printf '     %s\n' "$empty"
        failed+=("$name")
        continue
      fi
      cp -R "$home/." "$case_dir/expected.home/" || setup_error "can't record $name/expected.home."
    fi
    cp "$tmp/run/stdout" "$case_dir/expected.stdout"
    printf '%s\n' "$code" > "$case_dir/expected.code"
    cp "$tmp/run/calls" "$case_dir/expected.calls"
    if [[ ! -e "$case_dir/expected.stderr" ]]; then
      sed -e 's#[][\.*^$+?(){}|]#\\&#g' -e 's/^/^/' -e 's/$/$/' "$tmp/run/stderr" > "$case_dir/expected.stderr"
    else
      stderr_report="$(stderr_problems "$case_dir/expected.stderr" "$tmp/run/stderr")"
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
  diff -u "$case_dir/expected.stdout" "$tmp/run/stdout" > "$tmp/diff" || problems+=("stdout differs:"$'\n'"$(cat "$tmp/diff")")
  diff -u "$case_dir/expected.calls" "$tmp/run/calls" > "$tmp/diff" || problems+=("calls differ:"$'\n'"$(cat "$tmp/diff")")
  stderr_report="$(stderr_problems "$case_dir/expected.stderr" "$tmp/run/stderr")"
  [[ -z "$stderr_report" ]] || problems+=("$stderr_report")
  home_report="$(home_problems "$case_dir")"
  [[ -z "$home_report" ]] || problems+=("$home_report")

  if [[ ${#problems[@]} -eq 0 ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name"
    printf '     %s\n' "${problems[@]}"
    failed+=("$name")
  fi
done

if [[ ${#failed[@]} -gt 0 ]]; then
  echo "rollout parity: ${#failed[@]} case(s) failed against $shown: ${failed[*]}" >&2
  exit 1
fi
if [[ -n "$record" ]]; then
  echo "rollout parity: ${#cases[@]} case(s) recorded from $shown"
else
  echo "rollout parity: ${#cases[@]} case(s) pass against: $shown"
fi
