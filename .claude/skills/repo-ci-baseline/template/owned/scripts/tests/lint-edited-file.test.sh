#!/usr/bin/env bash
# Tests for scripts/lint-edited-file.sh, the Claude Code PostToolUse hook that lints the Markdown or YAML file a tool just wrote.
# Wiring it in (.claude/settings.json) is each repo's own.
#
# The linters are stubs on PATH, so the suite needs none of them: each stub fails when the file it is given holds the word BAD
# (CRASH: exit 3), and logs its arguments and working directory, which is how the cases tell which linter ran, on which path,
# with which config. A last block runs the real markdownlint-cli2 when it is installed; an Application's hook-tests job has none,
# so there that block is skipped.
# Usage: scripts/tests/lint-edited-file.test.sh
set -uo pipefail

# A caller's hook or git variables must not leak into the throwaway repos.
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX GIT_COMMON_DIR CDPATH

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="$ROOT/scripts/lint-edited-file.sh"
BASH_BIN="$(command -v bash)"

command -v python3 > /dev/null 2>&1 || { echo "lint-edited-file.test.sh needs python3" >&2; exit 1; }

PASSED=0
FAILED=0

# Canonical: the script logs pwd -P paths, and on macOS mktemp returns /var/..., a symlink to /private/var/....
WORK="$(cd -P -- "$(mktemp -d)" && pwd -P)"
trap 'rm -rf "$WORK"' EXIT

# Keep the developer's git config out of every git call here.
export HOME="$WORK" GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1

pass() {
  PASSED=$((PASSED + 1))
  echo "ok   - $1"
}

fail() {
  FAILED=$((FAILED + 1))
  echo "FAIL - $1"
  shift
  local line
  for line in "$@"; do echo "       | $line"; done
}

# check <label> <problem>...: passes when no problem was given.
check() {
  local label="$1"
  shift
  if (($# == 0)); then pass "$label"; else fail "$label" "$@" "stdout: $STDOUT" "stderr: $STDERR"; fi
}

LOG="$WORK/linters.log"
STUBS="$WORK/stubs"
mkdir -p "$STUBS"
# make_stub <dir> <name>: a linter that logs "<name> <cwd> <args>"; exit 4 when its last argument is not a file, 3 when the file
# holds CRASH, 1 when it holds BAD (BIG<n>: after printing n characters), else 0.
make_stub() {
  cat > "$1/$2" << 'EOF'
#!/usr/bin/env bash
name="$(basename "$0")"
echo "$name $(pwd -P) $*" >> "$STUB_LOG"
file="${!#}"
[[ -f "$file" ]] || { echo "$name: $file: no such file" >&2; exit 4; }
if grep -q CRASH "$file"; then echo "$name crashed" >&2; exit 3; fi
if grep -q 'BIG[0-9]' "$file"; then
  n="$(grep -o 'BIG[0-9]*' "$file" | head -n 1)"
  printf '%*s' "${n#BIG}" '' | tr ' ' Q
  exit 1
fi
if grep -q BAD "$file"; then echo "$name: $file: a violation"; exit 1; fi
exit 0
EOF
  chmod +x "$1/$2"
}
for tool in markdownlint-cli2 yamllint actionlint zizmor; do make_stub "$STUBS" "$tool"; done

# A PATH holding the stubs and the basic tools the script and stubs need, and no real linter.
link_tools() {
  mkdir -p "$1"
  local tool src
  for tool in bash git python3 tr basename dirname env cat grep head mkdir; do
    src="$(command -v "$tool")" && ln -s "$src" "$1/$tool"
  done
}
TOOLS="$WORK/tools"
link_tools "$TOOLS"

new_repo() {
  local dir="$WORK/$1"
  mkdir -p "$dir"
  git init --quiet "$dir"
  echo "$dir"
}

# run_hook <json> [path] [project]: the script with the tool call on stdin, from a directory that is no repository, with
# CLAUDE_PROJECT_DIR=<project> (default $repo) and the assignments in RUN_ENV. Sets STDOUT, STDERR, OUTPUT (both) and STATUS.
RUN_ENV=(RUN_ENV_UNUSED=1)
run_hook() {
  local json="$1" path="${2:-$STUBS:$TOOLS}" project="${3:-$repo}"
  : > "$LOG"
  mkdir -p "$WORK/cwd"
  STDOUT="$(cd "$WORK/cwd" && printf '%s' "$json" | env -u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE -u GIT_PREFIX \
    HOME="$WORK" GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_CEILING_DIRECTORIES="$WORK" \
    CLAUDE_PROJECT_DIR="$project" STUB_LOG="$LOG" PATH="$path" "${RUN_ENV[@]}" "$BASH_BIN" "$SCRIPT" 2> "$WORK/stderr")"
  STATUS=$?
  STDERR="$(cat "$WORK/stderr")"
  OUTPUT="$STDOUT$STDERR"
}

# edit_json <file>: what Claude Code sends after an Edit or Write.
edit_json() {
  python3 -I -c 'import json,sys; print(json.dumps({"tool_name": "Write", "tool_input": {"file_path": sys.argv[1]}}))' "$1"
}

# system_message <stdout>: the systemMessage of a JSON object, or nothing.
system_message() {
  python3 -I -c 'import json,sys; print(json.loads(sys.argv[1])["systemMessage"])' "$1" 2> /dev/null || true
}

ran() { grep -c "^$1 " "$LOG" || true; }
log_has() { grep -qF -- "$1" "$LOG"; }
log_empty() { [[ ! -s "$LOG" ]]; }

repo="$(new_repo app)"
mkdir -p "$repo/docs" "$repo/.github/workflows"
printf '# Title\n' > "$repo/docs/ok.md"
printf '# Title\n\nBAD\n' > "$repo/docs/bad.md"
printf '# Title\n\nCRASH\n' > "$repo/docs/crash.md"
printf 'key: value\n' > "$repo/config.yml"
printf 'key: BAD\n' > "$repo/bad.yml"
printf 'name: ci\n' > "$repo/.github/workflows/ci.yml"
printf 'name: BAD\n' > "$repo/.github/workflows/bad.yml"
printf 'version: 2\n' > "$repo/.github/dependabot.yml"
printf 'rules: {}\n' > "$repo/.yamllint.yml"

# --- Markdown -------------------------------------------------------------
run_hook "$(edit_json "$repo/docs/ok.md")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
[[ "$(ran markdownlint-cli2)" == 1 ]] || p+=("markdownlint-cli2 did not run once")
log_has "markdownlint-cli2 $repo --no-globs ./docs/ok.md" || p+=("not run from the repo root with --no-globs and ./docs/ok.md: $(cat "$LOG")")
[[ -z "$OUTPUT" ]] || p+=("a clean file printed output")
check "a clean Markdown file passes silently, linted from the repo root as a literal path" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/docs/bad.md")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2 (the code that returns stderr to Claude)")
[[ "$STDERR" == "Lint found problems in docs/bad.md."* && "$STDERR" == *"a violation"* ]] || p+=("stderr lacks the header and the linter's message")
[[ -z "$STDOUT" ]] || p+=("the report went to stdout, which Claude Code does not return on exit 2")
[[ "$STDERR" == *"tell the person instead of retrying"* ]] || p+=("no hint for a linter that is itself broken")
check "a Markdown violation exits 2 with the report on stderr only" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/docs/crash.md")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2: a linter that crashes is not a clean file")
[[ "$STDERR" == *"crashed"* && "$STDERR" == *"(exit 3)"* ]] || p+=("the crash output or exit status was dropped")
check "a linter that crashes fails the hook, not passes it" ${p[@]+"${p[@]}"}

printf '# T\n\nBAD\n' > "$repo/docs/UPPER.MD"
run_hook "$(edit_json "$repo/docs/UPPER.MD")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
check "an upper-case .MD extension is linted" ${p[@]+"${p[@]}"}

printf '# T\n\nBAD\n' > "$repo/docs/we ird[1]*.md"
run_hook "$(edit_json "$repo/docs/we ird[1]*.md")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
log_has "--no-globs ./docs/we ird[1]*.md" || p+=("the path was not passed whole and literal: $(cat "$LOG")")
check "a path with a space and glob characters reaches the linter whole" ${p[@]+"${p[@]}"}

printf '# T\n\nBAD\n' > "$repo/-dash.md"
run_hook "$(edit_json "$repo/-dash.md")"
p=()
log_has "--no-globs ./-dash.md" || p+=("a name starting with - is not guarded by ./: $(cat "$LOG")")
check "a file name that starts with - is not read as an option" ${p[@]+"${p[@]}"}

mkdir -p "$repo/node_modules/pkg"
printf '# T\n\nBAD\n' > "$repo/node_modules/pkg/README.md"
run_hook "$(edit_json "$repo/node_modules/pkg/README.md")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
log_empty || p+=("a file under node_modules was linted: $(cat "$LOG")")
check "a file under node_modules is not linted" ${p[@]+"${p[@]}"}

# The repo's own node_modules/.bin copy wins over PATH, as in the pre-commit hook; one that is not executable does not.
mkdir -p "$repo/node_modules/.bin"
cat > "$repo/node_modules/.bin/markdownlint-cli2" << 'EOF'
#!/usr/bin/env bash
echo "local-markdownlint $*" >> "$STUB_LOG"
exit 0
EOF
chmod +x "$repo/node_modules/.bin/markdownlint-cli2"
run_hook "$(edit_json "$repo/docs/bad.md")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0 from the local copy")
log_has "local-markdownlint " || p+=("the node_modules copy did not run")
[[ "$(ran markdownlint-cli2)" == 0 ]] || p+=("the PATH copy ran as well")
check "the repo's node_modules/.bin/markdownlint-cli2 is preferred" ${p[@]+"${p[@]}"}
chmod -x "$repo/node_modules/.bin/markdownlint-cli2"
run_hook "$(edit_json "$repo/docs/bad.md")"
p=()
[[ $STATUS -eq 2 && "$(ran markdownlint-cli2)" == 1 ]] || p+=("exit $STATUS: a non-executable local copy was not skipped for the PATH one")
check "a node_modules/.bin copy that is not executable falls back to PATH" ${p[@]+"${p[@]}"}
rm -rf "$repo/node_modules/.bin"

# The output cap (6000 characters): n-1, n and n+1.
for n in 5999 6000 6001; do
  printf '# T\n\nBIG%s\n' "$n" > "$repo/docs/big.md"
  run_hook "$(edit_json "$repo/docs/big.md")"
  got="$(printf '%s' "$STDERR" | tr -cd Q | wc -c)"
  got="${got// /}"
  want=$n
  ((n > 6000)) && want=6000
  p=()
  [[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
  [[ "$got" == "$want" ]] || p+=("the report held $got Q characters, want $want")
  if ((n > 6000)); then
    [[ "$STDERR" == *"output cut at 6000 of 6001 characters"* ]] || p+=("no truncation note")
  else
    [[ "$STDERR" != *"output cut"* ]] || p+=("a note on output that was not cut")
  fi
  check "linter output of $n characters is cut at 6000 and only then" ${p[@]+"${p[@]}"}
done

# --- YAML -----------------------------------------------------------------
run_hook "$(edit_json "$repo/config.yml")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
log_has "yamllint $repo -c .yamllint.yml ./config.yml" || p+=("yamllint did not get the repo's config and ./config.yml: $(cat "$LOG")")
[[ "$(ran actionlint)" == 0 && "$(ran zizmor)" == 0 ]] || p+=("a plain YAML file went through the workflow linters")
check "a plain YAML file goes through yamllint with the repo's config, and only that" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/bad.yml")"
p=()
[[ $STATUS -eq 2 && "$STDERR" == *"a violation"* && -z "$STDOUT" ]] || p+=("exit $STATUS; the report belongs on stderr")
check "a YAML violation exits 2" ${p[@]+"${p[@]}"}

printf 'key: BAD\n' > "$repo/other.yaml"
run_hook "$(edit_json "$repo/other.yaml")"
p=()
[[ $STATUS -eq 2 && "$(ran yamllint)" == 1 ]] || p+=("exit $STATUS, yamllint runs: $(ran yamllint)")
check "a .yaml file is linted as YAML" ${p[@]+"${p[@]}"}

printf 'key: BAD\n' > "$repo/UPPER.YML"
run_hook "$(edit_json "$repo/UPPER.YML")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
check "an upper-case .YML extension is linted" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/.github/workflows/ci.yml")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
for tool in yamllint actionlint zizmor; do [[ "$(ran "$tool")" == 1 ]] || p+=("$tool did not run once"); done
log_has "yamllint $repo -c .yamllint.yml ./.github/workflows/ci.yml" || p+=("yamllint's arguments: $(cat "$LOG")")
log_has "actionlint $repo -- ./.github/workflows/ci.yml" || p+=("actionlint's arguments: $(cat "$LOG")")
log_has "zizmor $repo --offline --min-severity medium -- ./.github/workflows/ci.yml" || p+=("zizmor's arguments: $(cat "$LOG")")
check "a workflow goes through yamllint, actionlint and zizmor with their arguments" ${p[@]+"${p[@]}"}

printf 'name: ci\n' > "$repo/.github/workflows/other.yaml"
run_hook "$(edit_json "$repo/.github/workflows/other.yaml")"
p=()
log_has "actionlint $repo -- ./.github/workflows/other.yaml" || p+=("actionlint did not get the .yaml workflow: $(cat "$LOG")")
log_has "zizmor $repo --offline --min-severity medium -- ./.github/workflows/other.yaml" || p+=("zizmor did not get the .yaml workflow")
check "a workflow named .yaml goes through actionlint and zizmor" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/.github/workflows/bad.yml")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
[[ "$(grep -c 'a violation' <<< "$STDERR")" -ge 3 ]] || p+=("not every linter's findings were reported together")
check "a workflow's findings from all three linters come back in one message" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/.github/dependabot.yml")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
[[ "$(ran yamllint)" == 1 && "$(ran zizmor)" == 1 && "$(ran actionlint)" == 0 ]] || p+=("want yamllint and zizmor, not actionlint: $(cat "$LOG")")
log_has "zizmor $repo --offline --min-severity medium -- ./.github/dependabot.yml" || p+=("zizmor's arguments: $(cat "$LOG")")
check "dependabot.yml goes through yamllint and zizmor, not actionlint" ${p[@]+"${p[@]}"}

printf 'version: 2\n' > "$repo/.github/dependabot.yaml"
run_hook "$(edit_json "$repo/.github/dependabot.yaml")"
p=()
[[ "$(ran zizmor)" == 1 && "$(ran actionlint)" == 0 ]] || p+=("dependabot.yaml: $(cat "$LOG")")
check "dependabot.yaml is audited by zizmor too" ${p[@]+"${p[@]}"}

printf 'version: BAD\n' > "$repo/.github/dependabot.yml"
run_hook "$(edit_json "$repo/.github/dependabot.yml")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
check "a dependabot.yml finding exits 2" ${p[@]+"${p[@]}"}
printf 'version: 2\n' > "$repo/.github/dependabot.yml"

rm "$repo/.yamllint.yml"
run_hook "$(edit_json "$repo/config.yml")"
p=()
log_has "yamllint $repo ./config.yml" || p+=("log: $(cat "$LOG")")
check "without a .yamllint.yml, yamllint runs with its defaults" ${p[@]+"${p[@]}"}
printf 'rules: {}\n' > "$repo/.yamllint.yml"

# --- Not linted -----------------------------------------------------------
printf 'BAD\n' > "$repo/script.sh"
run_hook "$(edit_json "$repo/script.sh")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
log_empty || p+=("a .sh file was linted: $(cat "$LOG")")
check "a file that is neither Markdown nor YAML is left alone" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/docs/missing.md")"
p=()
[[ $STATUS -eq 0 && -z "$OUTPUT" ]] || p+=("exit $STATUS, output: $OUTPUT")
log_empty || p+=("a linter ran on a file that does not exist: $(cat "$LOG")")
check "a file that no longer exists is left alone" ${p[@]+"${p[@]}"}

mkdir -p "$repo/docs/dir.md"
run_hook "$(edit_json "$repo/docs/dir.md")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
log_empty || p+=("a directory named dir.md was linted: $(cat "$LOG")")
check "a directory with a Markdown name is left alone" ${p[@]+"${p[@]}"}

printf '# T\n\nBAD\n' > "$WORK/outside.md"
ln -s "$WORK/outside.md" "$repo/docs/link.md"
run_hook "$(edit_json "$repo/docs/link.md")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
log_empty || p+=("a symlink to a file outside the repo was linted: $(cat "$LOG")")
check "a symlink is left alone" ${p[@]+"${p[@]}"}

mkdir -p "$WORK/not-a-repo"
printf '# T\n\nBAD\n' > "$WORK/not-a-repo/x.md"
run_hook "$(edit_json "$WORK/not-a-repo/x.md")" "$STUBS:$TOOLS" "$WORK/not-a-repo"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
log_empty || p+=("a file outside any repository was linted: $(cat "$LOG")")
check "a file outside any git repository is left alone" ${p[@]+"${p[@]}"}

# Another clone: its node_modules binary and config must never run (markdownlint-cli2 runs JavaScript from its config).
other="$(new_repo other)"
mkdir -p "$other/node_modules/.bin"
cat > "$other/node_modules/.bin/markdownlint-cli2" << 'EOF'
#!/usr/bin/env bash
echo "ATTACKER-BINARY-RAN" >> "$STUB_LOG"
exit 0
EOF
chmod +x "$other/node_modules/.bin/markdownlint-cli2"
printf '# T\n\nBAD\n' > "$other/x.md"
run_hook "$(edit_json "$other/x.md")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
log_empty || p+=("a file in an unrelated repo reached a linter: $(cat "$LOG")")
check "a file in another repository is left alone, and its node_modules binary is never run" ${p[@]+"${p[@]}"}

# A repository nested in the project is another repository.
inner="$(new_repo app/inner)"
printf '# T\n\nBAD\n' > "$inner/x.md"
run_hook "$(edit_json "$inner/x.md")"
p=()
log_empty || p+=("a nested repository was linted: $(cat "$LOG")")
check "a repository nested in the project is left alone" ${p[@]+"${p[@]}"}

# A linked worktree of the project is the project: linted from its own root.
git -C "$repo" -c user.email=t@t -c user.name=t -c commit.gpgsign=false commit --quiet --allow-empty -m init
git -C "$repo" worktree add --quiet -b wt "$WORK/wt" > /dev/null 2>&1
printf '# T\n\nBAD\n' > "$WORK/wt/w.md"
run_hook "$(edit_json "$WORK/wt/w.md")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
log_has "markdownlint-cli2 $WORK/wt " || p+=("not run from the worktree's root: $(cat "$LOG")")
check "a file in a linked worktree of the project is linted from that worktree" ${p[@]+"${p[@]}"}

# A project directory reached through a symlink.
ln -s "$repo" "$WORK/link"
run_hook "$(edit_json "$WORK/link/docs/bad.md")" "$STUBS:$TOOLS" "$WORK/link"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
log_has "markdownlint-cli2 $repo " || p+=("not run from the real root: $(cat "$LOG")")
check "a file reached through a symlinked project directory is linted from the real root" ${p[@]+"${p[@]}"}

# Started from a git hook: GIT_DIR and friends name another repository.
RUN_ENV=(GIT_DIR="$other/.git" GIT_WORK_TREE="$other" GIT_INDEX_FILE="$other/.git/index" CDPATH="$WORK")
run_hook "$(edit_json "$repo/docs/bad.md")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2: the inherited GIT_DIR sent the lookup to another repository")
log_has "markdownlint-cli2 $repo " || p+=("not run from the project root: $(cat "$LOG")")
check "inherited GIT_DIR, GIT_WORK_TREE, GIT_INDEX_FILE and CDPATH do not change which repository is linted" ${p[@]+"${p[@]}"}
RUN_ENV=(RUN_ENV_UNUSED=1)

# --- Input that is not a file edit ----------------------------------------
for json in '{}' '{"tool_input": {}}' '{"tool_input": {"file_path": 5}}' '{"tool_input": {"file_path": ""}}' '{"tool_input": {"file_path": null}}' '[]' '{"tool_input": "x"}'; do
  run_hook "$json"
  p=()
  [[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
  log_empty || p+=("a linter ran: $(cat "$LOG")")
  check "input $json lints nothing and exits 0" ${p[@]+"${p[@]}"}
done

for json in 'not json' ''; do
  run_hook "$json"
  p=()
  [[ $STATUS -eq 1 ]] || p+=("exit $STATUS, want 1: input that is not JSON is the hook's own failure")
  [[ "$STDERR" == *"could not read the edited file path"* && -z "$STDOUT" ]] || p+=("no message on stderr")
  check "input '$json' exits 1 with a message on stderr, not 0" ${p[@]+"${p[@]}"}
done

# --- Missing tools --------------------------------------------------------
NOLINT="$WORK/nolint"
link_tools "$NOLINT"
run_hook "$(edit_json "$repo/docs/bad.md")" "$NOLINT"
msg="$(system_message "$STDOUT")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
[[ -z "$STDERR" ]] || p+=("the note went to stderr, which Claude Code does not show on exit 0")
[[ "$msg" == *"docs/bad.md was not fully linted: markdownlint-cli2 not installed"* ]] || p+=("stdout is not a systemMessage naming the file and tool: $STDOUT")
check "without markdownlint-cli2, a systemMessage on stdout says so and the hook exits 0" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/.github/workflows/ci.yml")" "$NOLINT"
msg="$(system_message "$STDOUT")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
[[ "$msg" == *"yamllint actionlint zizmor not installed"* ]] || p+=("the note doesn't name all three: $STDOUT")
check "without the YAML linters, the note names each missing one" ${p[@]+"${p[@]}"}

# Only zizmor missing.
NOZIZ="$WORK/noziz"
link_tools "$NOZIZ"
for tool in yamllint actionlint; do make_stub "$NOZIZ" "$tool"; done
run_hook "$(edit_json "$repo/.github/dependabot.yml")" "$NOZIZ"
msg="$(system_message "$STDOUT")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
[[ "$msg" == *"was not fully linted: zizmor not installed"* ]] || p+=("the note doesn't name zizmor alone: $STDOUT")
check "dependabot.yml without zizmor names zizmor only" ${p[@]+"${p[@]}"}

# A violation and a missing linter together: the violation wins, exit 2, nothing on stdout.
run_hook "$(edit_json "$repo/.github/workflows/bad.yml")" "$NOZIZ"
p=()
[[ $STATUS -eq 2 && -z "$STDOUT" && "$STDERR" == *"a violation"* ]] || p+=("exit $STATUS")
check "a violation with a linter missing still exits 2" ${p[@]+"${p[@]}"}

NOPY="$WORK/nopy"
mkdir -p "$NOPY"
run_hook "$(edit_json "$repo/docs/bad.md")" "$NOPY"
p=()
[[ $STATUS -eq 1 && "$STDERR" == *"python3 not found"* ]] || p+=("exit $STATUS")
check "without python3, the hook exits 1 with a message" ${p[@]+"${p[@]}"}

# --- The real markdownlint-cli2, when installed ----------------------------
REAL_PATH="$TOOLS:$PATH"
if PATH="$REAL_PATH" markdownlint-cli2 --version > /dev/null 2>&1; then
  real="$(new_repo real)"
  cat > "$real/.markdownlint-cli2.jsonc" << 'EOF'
{ "config": { "default": true }, "ignores": ["skipped/**"] }
EOF
  mkdir -p "$real/skipped" "$real/docs"
  printf '# T\n\n\n\ntext\n' > "$real/bad.md"
  printf '# T\n\n\n\ntext\n' > "$real/skipped/bad.md"
  printf '# T\n\ntext\n' > "$real/good.md"
  printf '# T\n\n\n\ntext\n' > "$real/docs/a[1].md"
  printf '# T\n\n\n\ntext\n' > "$real/docs/x*y.md"
  run_hook "$(edit_json "$real/bad.md")" "$REAL_PATH" "$real"
  p=()
  [[ $STATUS -eq 2 && "$STDERR" == *MD012* ]] || p+=("exit $STATUS")
  check "markdownlint-cli2 itself: a violation exits 2" ${p[@]+"${p[@]}"}
  run_hook "$(edit_json "$real/good.md")" "$REAL_PATH" "$real"
  p=()
  [[ $STATUS -eq 0 ]] || p+=("exit $STATUS")
  check "markdownlint-cli2 itself: a clean file exits 0" ${p[@]+"${p[@]}"}
  run_hook "$(edit_json "$real/skipped/bad.md")" "$REAL_PATH" "$real"
  p=()
  [[ $STATUS -eq 0 ]] || p+=("exit $STATUS")
  check "markdownlint-cli2 itself: a file the config ignores passes" ${p[@]+"${p[@]}"}
  run_hook "$(edit_json "$real/docs/a[1].md")" "$REAL_PATH" "$real"
  p=()
  [[ $STATUS -eq 2 && "$STDERR" == *MD012* ]] || p+=("exit $STATUS")
  check "markdownlint-cli2 itself: a name with [ ] is linted, not matched as a glob" ${p[@]+"${p[@]}"}
  run_hook "$(edit_json "$real/docs/x*y.md")" "$REAL_PATH" "$real"
  p=()
  [[ $STATUS -eq 2 && "$STDERR" == *MD012* ]] || p+=("exit $STATUS")
  check "markdownlint-cli2 itself: a name with * is linted, not matched as a glob" ${p[@]+"${p[@]}"}
else
  echo "skip - markdownlint-cli2 is not installed (or does not run), so its own run is not tested"
fi

if [[ -x "$SCRIPT" ]]; then
  pass "scripts/lint-edited-file.sh is executable"
else
  fail "scripts/lint-edited-file.sh is executable"
fi

echo
echo "$PASSED passed, $FAILED failed"
[[ $FAILED -eq 0 ]]
