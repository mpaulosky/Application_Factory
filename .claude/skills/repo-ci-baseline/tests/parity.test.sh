#!/usr/bin/env bash
# The parity suites: a script's behaviour through its command line only, so the same cases judge any implementation of it
# (its C# port, ADR 0017) against the suite's reference (below). One runner and one fake gh serve every suite; a suite is a folder
# tests/parity/<suite>/, named for the script it was recorded from, and its README.md says what its cases cover and what they
# leave out on purpose, and anything a run of it needs besides (check_gh_repo's runs with its shfmt shim first on PATH).
# The suite's reference is that script: github/<suite>.py, else github/<suite>.cs once a port has replaced the .py (a
# Factory-only script, whose .py goes in the PR that ports it, ADR 0017), else the Template's template/owned/.github/scripts/<suite>.py.
#
#   tests/parity.test.sh <suite>                    run the suite's cases against its reference: python3 <the .py>, or
#                                                   dotnet run --no-build --file <the .cs> --, built once first and run
#                                                   from the .cs's folder, as its .sh runs it, so dotnet picks the SDK
#                                                   that built it (its suite's cases have no tree/, setup.sh or expected.tree/)
#   tests/parity.test.sh <suite> <command> [arg]... run them against another implementation
#   PARITY_RECORD=1 tests/parity.test.sh <suite>
#                                                   record the cases from the suite's reference (no command is accepted,
#                                                   so a port under test never records its own oracle; once a port has
#                                                   replaced the .py, the .cs is the reference and records); review the diff
#                                                   before committing
#
# A suite may hold github/, files laid over a copy of the skill's github/ folder, for a script that reads files beside it (its
# standard, repos.txt) that the suite fixes for its cases. The runner then copies the skill's github/ (but its tests/), the
# skill's top-level *.sh wrappers and the nearest global.json above github/ (so a C# script picks the same SDK) into a folder
# of its own, lays the suite's github/ over the copy, and runs the reference from the copy. A command word (or the value of a
# later --option=value word) that names a file under the skill's folder that the copy holds is made to name the copy's, so
# `tests/parity.test.sh settings github-settings.sh` runs the copied wrapper on the copied github/. The skill's own files are
# never written to.
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
#                    REST call with --paginate, --jq, -X, -H or --input the key and the call's whole argv, then for --input
#                    ` <<< ` and its body as canonical JSON; the queries a run costs, and what it writes, are behaviour too
#
# and, when the script reads more than its arguments and gh, any of:
#
#   stdin            the implementation's stdin, byte for byte (missing: /dev/null)
#   env              NAME=value lines, set for the run on top of the fixed environment below (a blank line is ignored)
#   tree/            files copied into the folder the implementation runs from (missing: it starts empty)
#   setup.sh         a bash script run in that folder before the implementation, to build what a file can't hold (a git
#                    repo with a local origin); a .sh, so the gate's shellcheck and shfmt read it. It runs with
#                    PARITY_SCRATCH, a folder outside the run's for what the run mustn't see as its own (the bare
#                    origin), and the git identity and dates fixed, so its commits get the same hashes everywhere and
#                    the gh fixtures can name them
#   expected.tree/   the run's folder afterwards, every file byte for byte and every symlink by its target, not followed
#                    (a .git anywhere is left out); missing: the run must leave the folder holding nothing but a .git,
#                    so a script that writes no file is held to that
#
# Neither args, env nor an expected.* file but expected.stdout may hold a carriage return (a setup error, since each is read
# line by line); stdin, a tree and expected.stdout, compared byte for byte, may, and
# tests/parity/.gitattributes stores every case file as it is, so a clone keeps them, and tests/parity/.editorconfig keeps
# an editor from trimming or converting them. Record mode writes
# expected.stdout, expected.code, expected.calls and expected.tree/ (removed when the run leaves no file). It writes a
# new case's expected.stderr with each line matched literally, for someone to loosen to the shape that matters; an existing
# one holds that loosening, so it is kept, and the run fails when it no longer fits. A case whose run makes a gh call the
# fake can't answer, times out, or leaves an empty folder (git stores none, so a fresh clone's expected.tree/ would lack
# it) is never recorded: its files are left as they were.
#
# Each case's run starts in a new temporary folder of its own (empty, or the case's tree/ and setup.sh; a C# reference
# then changes to its .cs's folder, which is why its suite's cases may have none of those), with
# stdin from /dev/null or the case's stdin, the fake gh first on PATH, no GitHub token in its environment and GH_CONFIG_DIR
# a new empty folder (nothing an earlier case wrote is left), no repository, Anthropic key or model the caller set (REPOSITORY, GITHUB_REPOSITORY, ANTHROPIC_API_KEY,
# ANTHROPIC_MODEL: a case's env sets them), no git repository or config the caller set (GIT_DIR and the rest unset,
# GIT_CONFIG_GLOBAL=/dev/null, GIT_CONFIG_NOSYSTEM=1), no colour or terminal width the caller set (NO_COLOR=1, TERM=dumb),
# LC_ALL=C.UTF-8 (best-effort: macOS has no such locale, so PYTHONUTF8=1 is set too), PYTHONDONTWRITEBYTECODE=1 (so a
# script's imports leave no __pycache__ beside it), TZ=UTC, dotnet's CLI usage data, first-run setup and logo off
# (DOTNET_CLI_TELEMETRY_OPTOUT, DOTNET_SKIP_FIRST_TIME_EXPERIENCE and DOTNET_NOLOGO set to 1), and PARITY_TIMEOUT seconds
# (default 60) before it counts as hung. A command word that looks like a path (it holds a
# /, or after the first word a .) and names an existing file or folder is made absolute first, as is the value of a later
# --option=value word, so `--file src/Status.cs` works from anywhere; the first word is looked up on PATH unless it holds a /. Build a compiled implementation once before running the suite (a
# file-based C# app with `dotnet build <file>.cs`, then the command `dotnet run --no-build --file <file>.cs --`), so the
# build isn't timed inside the first case and two runs of the suite don't build it at once; a .cs reference the runner picks
# is built that way for you, from its own folder, and a build that fails is a setup error.
#
# Exit 0 when every case passes, 1 when one fails, 2 on a usage or setup error (a case missing a file or holding a bad
# one, a command that can't be run, a temporary folder that can't be made). Needs bash 4.4 or later, coreutils (mktemp,
# mkdir, rm, ln, cp, cat, head, sort, env, basename, dirname, timeout), findutils (find), a diff with --no-dereference (GNU diffutils 3.3 or later), grep and sed, and git 2.32 or later
# (GIT_CONFIG_GLOBAL) for a case with a setup.sh; the fake gh needs bash and cat, and mktemp, rm and jq 1.6 or later (for walk) for a REST call with --jq or --input.
set -euo pipefail

setup_error() {
  echo "parity: $*" >&2
  exit 2
}

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
skill_dir="$(dirname -- "$here")"
fake="$here/parity/fake-gh.sh"
[[ -f "$here/parity/stderr.sh" ]] || setup_error "$here/parity/stderr.sh is missing."
# shellcheck source=/dev/null # tests/parity/stderr.sh, which shellcheck reads on its own
. "$here/parity/stderr.sh"

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
  setup_error "PARITY_RECORD records from the suite's reference only (github/$suite.py, github/$suite.cs or the Template's $suite.py); run it with no command."
fi
limit="${PARITY_TIMEOUT:-60}"
[[ "$limit" =~ ^[1-9][0-9]*$ ]] || setup_error "PARITY_TIMEOUT must be a whole number of seconds, not '$limit'."
command -v timeout > /dev/null || setup_error "timeout (coreutils) is not on PATH."

# build_cs: a C# reference, built once below, before the first case.
build_cs=""
if [[ $# -gt 0 ]]; then
  impl=("$@")
elif [[ -f "$skill_dir/github/$suite.py" ]]; then
  impl=(python3 "$skill_dir/github/$suite.py")
elif [[ -f "$skill_dir/github/$suite.cs" ]]; then
  build_cs="$skill_dir/github/$suite.cs"
  impl=(dotnet run --no-build --file "$build_cs" --)
elif [[ -f "$skill_dir/template/owned/.github/scripts/$suite.py" ]]; then
  impl=(python3 "$skill_dir/template/owned/.github/scripts/$suite.py")
else
  setup_error "no script for suite '$suite': none of github/$suite.py, github/$suite.cs and template/owned/.github/scripts/$suite.py is a file."
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
# shown: the command as the summary names it (a C# reference's dotnet run, not the script that runs it from its folder).
shown="${impl[*]}"
command -v -- "${impl[0]}" > /dev/null || setup_error "the command '${impl[0]}' can't be run (not found or not executable)."
[[ -f "$fake" && -x "$fake" ]] || setup_error "$fake is missing or not executable."

tmp="$(mktemp -d)" || setup_error "can't make a temporary folder."
trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin" "$tmp/ghconfig" || setup_error "can't make folders under $tmp."
ln -s "$fake" "$tmp/bin/gh" || setup_error "can't link the fake gh into $tmp/bin."
# A C# reference is built once, from its own folder, so the SDK is the one the global.json above it names (ADR 0017), and
# each case runs it from that folder too (a script in tmp/bin that cds there), since dotnet picks the SDK by the folder it runs
# in: from the case's folder, --no-build could run on an SDK other than the one that built it.
# The suite's github/ over a copy of the skill's, and the command's words that name a skill file the copy holds made to name the copy's.
overlay="$here/parity/$suite/github"
if [[ -e "$overlay" ]]; then
  [[ -d "$overlay" ]] || setup_error "$overlay is not a folder."
  copy="$tmp/skill"
  {
    mkdir "$copy" && cp -R "$skill_dir/github" "$copy/github" && rm -rf "$copy/github/tests" && cp -R "$overlay/." "$copy/github/"
  } || setup_error "can't copy the skill's github/ and lay $overlay over it."
  # Each wrapper's copy is checked on its own: a loop's status is only its last iteration's.
  for wrapper in "$skill_dir"/*.sh; do
    [[ ! -f "$wrapper" ]] || cp "$wrapper" "$copy/" || setup_error "can't copy $wrapper beside the copy of github/."
  done
  folder="$(CDPATH='' cd -- "$skill_dir/github" && pwd -P)"
  while [[ ! -f "$folder/global.json" && "$folder" != / ]]; do folder="$(dirname -- "$folder")"; done
  if [[ -f "$folder/global.json" ]]; then
    cp "$folder/global.json" "$copy/global.json" || setup_error "can't copy $folder/global.json beside the copy of github/."
  fi
  for i in "${!impl[@]}"; do
    word="${impl[i]}" option=""
    if [[ $i -gt 0 && "$word" == --?*=* ]]; then
      option="${word%%=*}=" word="${word#*=}"
    fi
    if [[ "$word" == "$skill_dir"/* && -e "$copy/${word#"$skill_dir"/}" ]]; then
      impl[i]="$option$copy/${word#"$skill_dir"/}"
    fi
  done
  [[ -z "$build_cs" ]] || build_cs="$copy/${build_cs#"$skill_dir"/}"
fi
if [[ -n "$build_cs" ]]; then
  (cd -- "$(dirname -- "$build_cs")" && DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 \
    MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false \
    dotnet build "$build_cs" < /dev/null > "$tmp/build.log" 2>&1) || setup_error "dotnet build $build_cs failed:"$'\n'"$(cat "$tmp/build.log")"
  {
    printf '#!/usr/bin/env bash\ncd -- %q && exec dotnet run --no-build --file %q -- "$@"\n' "$(dirname -- "$build_cs")" "$build_cs" > "$tmp/bin/reference"
    chmod +x "$tmp/bin/reference"
  } || setup_error "can't write $tmp/bin/reference."
  impl=("$tmp/bin/reference")
fi

shopt -s nullglob
cases=("$cases_dir"/*/)
[[ ${#cases[@]} -gt 0 ]] || setup_error "no cases under $cases_dir."

# A case's files must be well formed before its run means anything: a missing or broken one is a setup error, not a
# difference between implementations. Record mode needs only gh/, and checks the files a case already has.
check_case() {
  local dir="$1" name="$2" file pattern n status
  [[ -d "$dir/gh" ]] || setup_error "$name has no gh/ folder."
  for file in "$dir/args" "$dir/env" "$dir"/expected.*; do
    [[ -f "$file" && "$file" != "$dir/expected.stdout" ]] || continue
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
  # A C# reference runs from its .cs's folder, not the case's, so files a case puts there or expects there would go unseen.
  if [[ -n "$build_cs" ]]; then
    for file in tree setup.sh expected.tree; do
      [[ ! -e "$dir/$file" ]] || setup_error "$name/$file: a C# reference runs from its .cs's folder, so a case of its suite has no $file."
    done
  fi
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

# tree_problems <case folder> <run folder>: prints how the run's folder differs from the case's expected.tree/ (or, with
# none, the files it left), nothing when it fits. A .git is left out either way. A symlink is compared by its target and not
# followed, so one to nothing or out of the folder (a case's setup.sh may make them) is held to where it points.
tree_problems() {
  local left
  if [[ -d "$1/expected.tree" ]]; then
    diff -r --no-dereference -x .git -- "$1/expected.tree" "$2" > "$tmp/diff" 2>&1 || echo "the files differ:"$'\n'"$(cat "$tmp/diff")"
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
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 \
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
    # git stores no empty folder, so one in expected.tree/ would be missing from a fresh clone and fail the case there.
    empty="$(cd -- "$run" && find . -mindepth 1 -name .git -prune -o -type d -empty -print | sort)"
    if [[ -n "$empty" ]]; then
      echo "FAIL $name: not recorded: the run left an empty folder, which git can't store:"
      while IFS= read -r folder; do echo "     $folder"; done <<< "$empty"
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
  echo "parity: $suite: ${#failed[@]} case(s) failed against $shown: ${failed[*]}" >&2
  exit 1
fi
if [[ -n "$record" ]]; then
  echo "parity: $suite: ${#cases[@]} case(s) recorded from $shown"
else
  echo "parity: $suite: ${#cases[@]} case(s) pass against: $shown"
fi
