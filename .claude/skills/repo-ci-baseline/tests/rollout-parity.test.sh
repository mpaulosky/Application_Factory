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
# The copy of the skill folder is made once, in a new temporary folder, at <tmp>/factory/.claude/skills/repo-ci-baseline as
# in the Factory, so the Factory checkout --auto derives from it is <tmp>/factory: rollout.sh and github/ (without
# github/tests/, bin/, obj/ or __pycache__/), github/repos.txt replaced by tests/rollout-parity/repos.txt, reapply.sh and
# land.sh linked to tests/rollout-parity/fake-tool.sh, the stubs, and a stand-in Template, template/owned/ holding only
# tests/rollout-parity/template/'s global.json, pre-push-review skill (SKILL.md) and pre-push-reviewer agent, which --auto
# reads. Nothing else of the skill is in it, so a command word that names another file of the skill's is a setup error.
#
# Each case is a folder under tests/rollout-parity/cases/:
#
#   args             the implementation's arguments, one per line, taken verbatim (a blank line is an empty argument; an
#                    empty or missing file: none)
#   env              NAME=value lines, set for the run on top of the fixed environment below (a blank line is ignored)
#   missing          names of the fake tools on PATH (below) the run's PATH doesn't hold, one per line, so the preflight
#                    finds them missing
#   ignore           names of signals (HUP, INT, TERM), one per line, the run starts with ignored, as nohup starts one
#   setup.sh         a bash script run in the run's HOME, with its environment, before the run (one that fails is a setup
#                    error); a process it leaves running (one holding the lock) is after.sh's to end
#   after.sh         a bash script run in the run's HOME, with its environment, once the run has ended and before anything
#                    is compared: it exits non-zero, printing why, when the run left the world as it shouldn't (a process
#                    of the child's group still alive), and removes what it and setup.sh wrote in HOME
#   home/            the files the run's HOME starts with (a checkout under ~/github is a folder with a .gitkeep, since git
#                    keeps no empty folder); missing: it starts empty
#   gh/              the fixtures tests/parity/fake-gh.sh answers the gh calls from (see its header), with each of
#                    tests/rollout-parity/defaults/gh/'s whose key the case has no fixture for; a case with none keeps an
#                    empty .gitkeep there
#   fakes/           what each stub or fake tool does when called (fake-tool.sh's header): <tool>.sh, or <tool>.<n>.sh for
#                    its nth call, a bash script run with the call's arguments, on top of tests/rollout-parity/defaults/fakes/
#                    (its README says what each answers); tools are reapply.sh, land.sh, review.sh (a re-Apply worktree's
#                    scripts/review.sh, which a fixture writes) and the fake tools on PATH. A case that calls none of its own
#                    keeps an empty .gitkeep there
#   expected.stdout  stdout, byte for byte
#   expected.code    the exit code
#   expected.stderr  one ERE per stderr line, in order, as tests/parity.test.sh holds it (a line that is exactly ... stands
#                    for any number of lines; no line is blank)
#   expected.calls   the run's call log, in order: each gh call as the fake gh logs it, and each call of a stub or fake tool
#                    as its name and argv (fake-tool.sh's header)
#   expected.home/   HOME afterwards, every file byte for byte and every symlink by its target, a .git file or folder left
#                    out (git can't store one there); missing: the run must leave HOME as home/ gave it, so a run that
#                    writes nothing there is held to that
#
# Before stdout, stderr, the call log and HOME are compared, each run's HOME in them is written ~, the copy of the skill
# folder <skill> and the temporary folder that holds both <tmp>, since all are temporary folders whose names change from
# run to run, and the random part of a name --auto makes with mkstemp or mkdtemp (rollout-session.*, rollout-record.*,
# rollout-sdk.*, and review-<repo>.*.log and .settings.json) XXXXXXXX. A NUL byte in them is dropped. In HOME, a file's
# content is written so, and an entry's name too: a second one the same name would take gets -2 after it, a third -3, in
# the order they were last written.
#
# Each case's run starts in its HOME, a new folder (canonical, symlinks resolved) holding the case's home/, with stdin from
# /dev/null and the signals the launcher resets (INT, TERM, HUP, PIPE, XFSZ) at their defaults but for the case's ignore,
# and a PATH of two folders only: the fake tools, each a wrapper that runs fake-tool.sh under its name (git, claude, and
# the rest of what --auto's preflight looks for: dotnet, node, pnpm, shfmt, bwrap, socat, uvx and ruff), with the fake gh,
# and links to the host's own commands a run, a stub or a fixture uses (bash, python3, coreutils, jq, flock and a few
# more: HOST_COMMANDS below, where the host has them), so a tool the host has and the case doesn't fake can't be found.
# Its environment has no GitHub token, Anthropic or Claude Code variable, proxy, repository or PYTHONPATH, GH_CONFIG_DIR
# a new empty folder outside HOME, TMPDIR another, no XDG folder the caller set, no git repository or config the caller set
# (GIT_DIR and the rest unset, GIT_CONFIG_GLOBAL=/dev/null, GIT_CONFIG_NOSYSTEM=1), no colour or terminal width the caller
# set (NO_COLOR=1, TERM=dumb), LC_ALL=C.UTF-8 and PYTHONUTF8=1, PYTHONDONTWRITEBYTECODE=1 (so the copy's imports leave no
# __pycache__), TZ=UTC, dotnet's CLI usage data, first-run setup and logo off, FAKE_TOOL (fake-tool.sh's path, for a
# fixture's scripts/review.sh) and FAKE_PID_FILE (a file holding the PID of the process the runner started, the
# implementation's wrapper, for a fixture that signals it), and PARITY_TIMEOUT seconds (default 60) before it counts as
# hung. A command word that looks like a path (it holds a /, or after the first word a .) and names an existing file or
# folder is made absolute first, as is the value of a later --option=value word; the first word is looked up on the
# caller's PATH unless it holds a /, so a dotnet there isn't the fake one.
#
# Exit 0 when every case passes, 1 when one fails, 2 on a usage or setup error (a case missing a file or holding a bad one,
# a setup.sh that fails, a command that can't be run, a temporary folder that can't be made). Needs bash 4.4 or later,
# python3 (the launcher; the reference needs it too), coreutils (mktemp, mkdir, rm, ln, cp, mv, cat, tr, env, basename,
# dirname, timeout, sort), findutils (find), a diff with --no-dereference (GNU diffutils 3.3 or later), grep and sed; the
# cases' fixtures need jq and flock (util-linux) too.
set -euo pipefail

setup_error() {
  echo "rollout parity: $*" >&2
  exit 2
}

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
skill_dir="$(dirname -- "$here")"
suite_dir="$here/rollout-parity"
cases_dir="$suite_dir/cases"
defaults_dir="$suite_dir/defaults"
template_dir="$suite_dir/template"
fake_gh="$here/parity/fake-gh.sh"
fake_tool="$suite_dir/fake-tool.sh"
[[ -d "$cases_dir" ]] || setup_error "$cases_dir is not a folder."
for file in "$fake_gh" "$fake_tool"; do
  [[ -f "$file" && -x "$file" ]] || setup_error "$file is missing or not executable."
done
[[ -f "$suite_dir/repos.txt" ]] || setup_error "$suite_dir/repos.txt, the copy's fixed github/repos.txt, is missing."
[[ -d "$defaults_dir/fakes" && -d "$defaults_dir/gh" ]] || setup_error "$defaults_dir has no fakes/ and gh/ folders."
for file in global.json pre-push-review.md pre-push-reviewer.md; do
  [[ -f "$template_dir/$file" ]] || setup_error "$template_dir/$file, the stand-in Template's, is missing."
done
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
python="$(command -v python3)" || setup_error "python3, which starts each run, is not on PATH."
[[ -f "$skill_dir/rollout.sh" && -d "$skill_dir/github" ]] || setup_error "$skill_dir holds no rollout.sh and github/ to copy."

# The fake tools on PATH, and the host's commands a run may use beside them. A name in both would be the fake.
PATH_FAKES=(git claude dotnet node pnpm shfmt bwrap socat uvx ruff)
HOST_COMMANDS=(bash sh env python3 cat ls wc sleep dirname basename mktemp rm rmdir mkdir touch cp mv ln chmod readlink
  realpath jq grep sed awk tr head tail cut sort uniq tee printf echo test true false kill ps flock timeout date id stat
  find xargs diff cmp)
SIGNALS=(HUP INT TERM)

tmp="$(mktemp -d)" || setup_error "can't make a temporary folder."
trap 'rm -rf "$tmp"' EXIT
tmp="$(CDPATH='' cd -- "$tmp" && pwd -P)" || setup_error "can't resolve the temporary folder."
layout="$tmp/factory/.claude/skills/repo-ci-baseline"
owned="$layout/template/owned"
mkdir -p "$layout" "$owned/.claude/skills/pre-push-review" "$owned/.claude/agents" "$tmp/host" \
  || setup_error "can't make folders under $tmp."
{
  cp -p "$skill_dir/rollout.sh" "$layout/" \
    && cp -R "$skill_dir/github" "$layout/" \
    && rm -rf "$layout/github/tests" "$layout/github/bin" "$layout/github/obj" \
    && find "$layout" -name __pycache__ -prune -exec rm -rf {} + \
    && cp "$suite_dir/repos.txt" "$layout/github/repos.txt" \
    && cp "$template_dir/global.json" "$owned/global.json" \
    && cp "$template_dir/pre-push-review.md" "$owned/.claude/skills/pre-push-review/SKILL.md" \
    && cp "$template_dir/pre-push-reviewer.md" "$owned/.claude/agents/pre-push-reviewer.md" \
    && ln -s "$fake_tool" "$layout/reapply.sh" \
    && ln -s "$fake_tool" "$layout/land.sh"
} || setup_error "can't make the copy of the skill folder under $tmp."
for name in "${HOST_COMMANDS[@]}"; do
  found="$(type -P -- "$name")" || continue
  ln -s "$found" "$tmp/host/$name" || setup_error "can't link $found into $tmp/host."
done

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
# The run's PATH holds the fakes, so the first word is found on the caller's PATH now, as the caller would find it.
[[ "${impl[0]}" == */* ]] || impl[0]="$(type -P -- "${impl[0]}")" || setup_error "the command '${impl[0]}' is not a file on PATH."

shopt -s nullglob
cases=("$cases_dir"/*/)
[[ ${#cases[@]} -gt 0 ]] || setup_error "no cases under $cases_dir."

# is_one_of <word> <name>...: whether word is one of the names.
is_one_of() {
  local word="$1" name
  shift
  for name in "$@"; do
    [[ "$word" != "$name" ]] || return 0
  done
  return 1
}

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
  for file in args env missing ignore setup.sh after.sh; do
    [[ ! -e "$dir/$file" || -f "$dir/$file" ]] || setup_error "$name/$file is not a file."
  done
  for file in "$dir/args" "$dir/env" "$dir/missing" "$dir/ignore" "$dir"/expected.*; do
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
  if [[ -f "$dir/missing" ]]; then
    while IFS= read -r pattern || [[ -n "$pattern" ]]; do
      is_one_of "$pattern" "${PATH_FAKES[@]}" || setup_error "$name/missing names '$pattern', not one of: ${PATH_FAKES[*]}"
    done < "$dir/missing"
  fi
  if [[ -f "$dir/ignore" ]]; then
    while IFS= read -r pattern || [[ -n "$pattern" ]]; do
      is_one_of "$pattern" "${SIGNALS[@]}" || setup_error "$name/ignore names '$pattern', not one of: ${SIGNALS[*]}"
    done < "$dir/ignore"
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

# The random part of a name --auto makes with mkstemp or mkdtemp, written XXXXXXXX (sed -E).
random_names=(-e 's/(rollout-(session|record|sdk)\.)[A-Za-z0-9_]+/\1XXXXXXXX/g'
  -e 's/(review-[A-Za-z0-9_-]+\.)[A-Za-z0-9_]+(\.log|\.settings\.json)/\1XXXXXXXX\2/g')

# normalize <file>: the run's HOME written ~, the copy of the skill folder <skill>, the temporary folder <tmp> and a random
# name's random part XXXXXXXX, in place.
normalize() {
  local text
  text="$(
    tr -d '\0' < "$1"
    printf x
  )"
  text="${text%x}"
  text="${text//"$layout"/<skill>}"
  text="${text//"$home"/\~}"
  text="${text//"$tmp"/<tmp>}"
  printf '%s' "$text" | sed -E "${random_names[@]}" > "$tmp/normalized"
  cat "$tmp/normalized" > "$1"
}

# normalize_home: every file in the run's HOME normalized, and every name with a random part renamed, the nth that would
# take a name already there getting -n after it, in the order they were last written; the deepest first, so a folder is
# renamed after what is in it.
normalize_home() {
  local path entry name new target n
  # The names first: normalizing a file's content writes it, which would change the order they were written in. The sort runs in the C
  # locale: in another, sort -n may not read %T@'s . as a decimal point, so entries written in one second would tie.
  while IFS= read -r -d '' entry; do
    path="${entry#* * }"
    name="${path##*/}"
    new="$(printf '%s' "$name" | sed -E "${random_names[@]}")"
    [[ "$new" != "$name" ]] || continue
    target="${path%/*}/$new" n=1
    while [[ -e "$target" || -L "$target" ]]; do
      n=$((n + 1))
      target="${path%/*}/$new-$n"
    done
    mv -- "$path" "$target"
  done < <(find "$home" -mindepth 1 -printf '%d %T@ %p\0' | LC_ALL=C sort -z -k1,1nr -k2,2n)
  while IFS= read -r -d '' path; do
    normalize "$path"
  done < <(find "$home" -name .git -prune -o -type f -print0)
}

# home_problems <case folder>: prints how the run's HOME differs from the case's expected.home/ (or, with none, its home/),
# nothing when it fits. A symlink is compared by its target and not followed, and a .git file or folder is left out.
home_problems() {
  local want="$1/expected.home"
  [[ -d "$want" ]] || want="$1/home"
  [[ -d "$want" ]] || want="$tmp/empty"
  diff -r --no-dereference -x .git -- "$want" "$home" > "$tmp/diff" 2>&1 || echo "HOME differs:"$'\n'"$(cat "$tmp/diff")"
}

# What git, should a fixture run the real one, sees: no repository or config the caller set, none above HOME. Every -u
# comes before the first NAME=value, as env reads its options before its assignments.
git_env=(-u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE -u GIT_PREFIX -u GIT_COMMON_DIR -u GIT_OBJECT_DIRECTORY
  -u GIT_ALTERNATE_OBJECT_DIRECTORIES -u GIT_NAMESPACE -u GIT_CONFIG -u GIT_CONFIG_PARAMETERS -u GIT_CONFIG_COUNT -u GIT_TEMPLATE_DIR
  GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_CEILING_DIRECTORIES="$tmp/run" GIT_TERMINAL_PROMPT=0)
# Every Anthropic and Claude Code variable and every proxy the caller set, which --auto's preflight reads (a sign-in secret,
# a cloud, a URL with a sign-in): a case sets one in its env.
caller_unset=()
while IFS= read -r name; do
  lower="${name,,}"
  if [[ "$name" =~ ^(ANTHROPIC_|CLAUDE_) || "$lower" == *proxy ]]; then
    caller_unset+=(-u "$name")
  fi
done < <(compgen -e)

# The launcher: the signals it resets back to their defaults (a caller's background job ignores INT, and Python ignores
# PIPE and XFSZ), the case's ignored, the PID written for the fixtures, then the implementation in its place.
launch='import os, signal, sys
for name in ("SIGINT", "SIGTERM", "SIGHUP", "SIGPIPE", "SIGXFSZ"):
    signal.signal(getattr(signal, name), signal.SIG_DFL)
for name in sys.argv[2].split():
    signal.signal(getattr(signal, "SIG" + name), signal.SIG_IGN)
with open(sys.argv[1], "w") as handle:
    handle.write(str(os.getpid()))
os.execv(sys.argv[3], sys.argv[3:])'

# make_bin <folder> <case folder>: the fake tools on PATH, but for the case's missing, and the fake gh, each a wrapper that
# sets the fakes' variables itself, since --auto runs shfmt with PATH alone in its environment.
make_bin() {
  local bin="$1" dir="$2" tool missing=()
  mkdir "$bin" || return 1
  [[ ! -s "$dir/missing" ]] || mapfile -t missing < "$dir/missing"
  for tool in "${PATH_FAKES[@]}"; do
    ! is_one_of "$tool" ${missing[@]+"${missing[@]}"} || continue
    printf '#!%s\nexport FAKE_TOOL_NAME=%q FAKE_TOOL_DIR=%q FAKE_GH_LOG=%q FAKE_HOME=%q\nexec %q %q "$@"\n' "$BASH" "$tool" \
      "$tmp/run/fakes" "$tmp/run/calls" "$home" "$BASH" "$fake_tool" > "$bin/$tool" || return 1
    chmod +x "$bin/$tool" || return 1
  done
  printf '#!%s\nexport FAKE_GH_DIR=%q FAKE_GH_LOG=%q\nexec %q %q "$@"\n' "$BASH" "$tmp/run/gh" "$tmp/run/calls" "$BASH" \
    "$fake_gh" > "$bin/gh" && chmod +x "$bin/gh"
}

# merge_gh <case's gh/> <folder>: the case's gh fixtures, and each default whose key the case has no fixture for in any
# form (a .json, a .fail, a numbered one): the fake gh prefers a .json to a .fail, so a case's auth.status.fail must hide
# the default auth.status.json, not sit beside it.
merge_gh() {
  local file key
  for file in "$defaults_dir/gh"/*; do
    key="$(basename -- "$file")"
    key="${key%.json}" key="${key%.fail}"
    [[ -z "$(find "$1" -maxdepth 1 \( -name "$key.json" -o -name "$key.fail" -o -name "$key.*[0-9].json" -o -name "$key.*[0-9].fail" \) -print -quit)" ]] \
      || continue
    cp -- "$file" "$2/" || return 1
  done
  cp -R "$1/." "$2/"
}

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
  ignored=""
  if [[ -s "$case_dir/ignore" ]]; then
    ignored="$(tr '\n' ' ' < "$case_dir/ignore")"
  fi
  rm -rf "$tmp/run"
  home="$tmp/run/home"
  mkdir -p "$home" "$tmp/run/ghconfig" "$tmp/run/tmp" "$tmp/run/fakes" "$tmp/run/gh" || setup_error "can't make the run's folders under $tmp."
  if [[ -d "$case_dir/home" ]]; then
    cp -R "$case_dir/home/." "$home/" || setup_error "can't copy $name/home into the run's HOME."
  fi
  { cp -R "$defaults_dir/fakes/." "$case_dir/fakes/." "$tmp/run/fakes/" && merge_gh "$case_dir/gh" "$tmp/run/gh"; } \
    || setup_error "can't copy $name's fixtures over the defaults."
  make_bin "$tmp/run/bin" "$case_dir" || setup_error "can't make the run's fake tools under $tmp."
  : > "$tmp/run/calls"
  run_env=(-u GH_TOKEN -u GITHUB_TOKEN -u GH_ENTERPRISE_TOKEN -u GITHUB_ENTERPRISE_TOKEN
    -u FORCE_COLOR -u CLICOLOR_FORCE -u PYTHON_COLORS -u COLUMNS -u LINES -u PYTHONPATH
    -u XDG_CONFIG_HOME -u XDG_DATA_HOME -u XDG_STATE_HOME -u XDG_CACHE_HOME -u XDG_RUNTIME_DIR
    -u REPOSITORY -u GITHUB_REPOSITORY ${caller_unset[@]+"${caller_unset[@]}"} "${git_env[@]}" NO_COLOR=1 TERM=dumb
    HOME="$home" PATH="$tmp/run/bin:$tmp/host" GH_CONFIG_DIR="$tmp/run/ghconfig" TMPDIR="$tmp/run/tmp" LC_ALL=C.UTF-8
    PYTHONUTF8=1 PYTHONDONTWRITEBYTECODE=1 TZ=UTC DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
    FAKE_GH_DIR="$tmp/run/gh" FAKE_GH_LOG="$tmp/run/calls" FAKE_TOOL_DIR="$tmp/run/fakes" FAKE_HOME="$home"
    FAKE_TOOL="$fake_tool" FAKE_PID_FILE="$tmp/run/pid" ${case_env[@]+"${case_env[@]}"})
  if [[ -f "$case_dir/setup.sh" ]]; then
    (cd "$home" && env "${run_env[@]}" "$BASH" "$case_dir/setup.sh" < /dev/null > "$tmp/run/setup.out" 2>&1) \
      || setup_error "$name/setup.sh failed: $(tail -5 "$tmp/run/setup.out")"
  fi
  code=0
  (cd "$home" && timeout -k 5 "$limit" env "${run_env[@]}" "$python" -c "$launch" "$tmp/run/pid" "$ignored" \
    "${impl[@]}" ${args[@]+"${args[@]}"} < /dev/null > "$tmp/run/stdout" 2> "$tmp/run/stderr") || code=$?

  problems=()
  if [[ $code -eq 124 || $code -eq 137 ]]; then
    problems+=("timed out after $limit second(s) (exit $code)")
  fi
  if [[ -f "$case_dir/after.sh" ]]; then
    after=0
    (cd "$home" && env "${run_env[@]}" "$BASH" "$case_dir/after.sh" < /dev/null > "$tmp/run/after.out" 2>&1) || after=$?
    [[ $after -eq 0 ]] || problems+=("after.sh exited $after: $(tail -5 "$tmp/run/after.out")")
  fi
  for file in stdout stderr calls; do
    normalize "$tmp/run/$file"
  done
  normalize_home
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
      # git stores no .git and no empty folder, so either in expected.home/ would be missing from a fresh clone and fail the
      # case there: a .git is left out, as the comparison leaves it out, and an empty folder isn't recorded.
      rm -rf "$tmp/recorded"
      { mkdir "$tmp/recorded" && cp -R "$home/." "$tmp/recorded/"; } || setup_error "can't copy the run's HOME to record $name."
      find "$tmp/recorded" -name .git -prune -exec rm -rf {} +
      empty="$(cd -- "$tmp/recorded" && find . -mindepth 1 -type d -empty -print)"
      if [[ -n "$empty" ]]; then
        echo "FAIL $name: not recorded: the run left an empty folder in HOME, which git can't store:"
        printf '     %s\n' "$empty"
        failed+=("$name")
        continue
      fi
      mv "$tmp/recorded" "$case_dir/expected.home" || setup_error "can't record $name/expected.home."
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
