#!/usr/bin/env bash
# Tests for scripts/lint-edited-file.sh, the Claude Code PostToolUse hook that lints the Markdown or YAML file a tool just wrote.
# Wiring it in (.claude/settings.json) is each repo's own.
#
# The linters are stubs on PATH, so the suite needs none of them: each stub fails when the file it is given holds the word BAD, and
# logs its arguments and working directory, which is how the cases tell which linter ran, on which path, with which config.
# Usage: scripts/tests/lint-edited-file.test.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="$ROOT/scripts/lint-edited-file.sh"
BASH_BIN="$(command -v bash)"

command -v python3 > /dev/null 2>&1 || { echo "lint-edited-file.test.sh needs python3" >&2; exit 1; }

PASSED=0
FAILED=0

WORK="$(mktemp -d)"
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
  if (($# == 0)); then pass "$label"; else fail "$label" "$@" "output: $OUTPUT"; fi
}

LOG="$WORK/linters.log"
STUBS="$WORK/stubs"
mkdir -p "$STUBS"
# make_stub <dir> <name>: a linter that logs "<name> <cwd> <args>" and exits 1 when its last argument's file holds BAD, 3 when it holds CRASH.
make_stub() {
  cat > "$1/$2" << 'EOF'
#!/usr/bin/env bash
name="$(basename "$0")"
echo "$name $(pwd -P) $*" >> "$STUB_LOG"
file="${!#}"
if grep -q CRASH "$file" 2> /dev/null; then echo "$name crashed" >&2; exit 3; fi
if grep -q BAD "$file" 2> /dev/null; then echo "$name: $file: a violation"; exit 1; fi
exit 0
EOF
  chmod +x "$1/$2"
}
for tool in markdownlint-cli2 yamllint actionlint zizmor; do make_stub "$STUBS" "$tool"; done

# A PATH holding the stubs and the basic tools the script needs (bash, git, python3), and no real linter.
TOOLS="$WORK/tools"
mkdir -p "$TOOLS"
for tool in bash git python3 tr basename dirname env cat grep mkdir; do
  src="$(command -v "$tool")" && ln -s "$src" "$TOOLS/$tool"
done

new_repo() {
  local dir="$WORK/$1"
  mkdir -p "$dir"
  git init --quiet "$dir"
  echo "$dir"
}

# run_hook <json> [path]: the script with the tool call on stdin. Sets OUTPUT (stdout and stderr) and STATUS.
run_hook() {
  local json="$1" path="${2:-$STUBS:$TOOLS}"
  : > "$LOG"
  OUTPUT="$(printf '%s' "$json" | env -u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE -u GIT_PREFIX \
    HOME="$WORK" GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_CEILING_DIRECTORIES="$WORK" \
    STUB_LOG="$LOG" PATH="$path" "$BASH_BIN" "$SCRIPT" 2>&1)"
  STATUS=$?
}

# edit_json <file>: what Claude Code sends after an Edit or Write.
edit_json() {
  python3 -I -c 'import json,sys; print(json.dumps({"tool_name": "Write", "tool_input": {"file_path": sys.argv[1]}}))' "$1"
}

ran() { grep -c "^$1 " "$LOG" || true; }

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
grep -q "^markdownlint-cli2 $repo --no-globs ./docs/ok.md\$" "$LOG" || p+=("not run from the repo root with --no-globs and ./docs/ok.md: $(cat "$LOG")")
[[ -z "$OUTPUT" ]] || p+=("a clean file printed output")
check "a clean Markdown file passes silently, linted from the repo root as a literal path" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/docs/bad.md")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2 (the code that returns stderr to Claude)")
[[ "$OUTPUT" == *"docs/bad.md"* && "$OUTPUT" == *"a violation"* ]] || p+=("output doesn't name the file and the linter's message")
check "a Markdown violation exits 2 with the linter's output" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/docs/crash.md")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2: a linter that crashes is not a clean file")
[[ "$OUTPUT" == *"crashed"* ]] || p+=("the crash output was dropped")
check "a linter that crashes fails the hook, not passes it" ${p[@]+"${p[@]}"}

printf '# T\n\nBAD\n' > "$repo/docs/UPPER.MD"
run_hook "$(edit_json "$repo/docs/UPPER.MD")"
if [[ $STATUS -eq 2 ]]; then pass "an upper-case .MD extension is linted"; else fail "an upper-case .MD extension is linted" "exit $STATUS, want 2" "output: $OUTPUT"; fi

printf '# T\n\nBAD\n' > "$repo/docs/we ird[1]*.md"
run_hook "$(edit_json "$repo/docs/we ird[1]*.md")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
grep -qF -- "--no-globs ./docs/we ird[1]*.md" "$LOG" || p+=("the path was not passed whole and literal: $(cat "$LOG")")
check "a path with a space and glob characters reaches the linter whole" ${p[@]+"${p[@]}"}

printf '# T\n\nBAD\n' > "$repo/-dash.md"
run_hook "$(edit_json "$repo/-dash.md")"
p=()
grep -qF -- "--no-globs ./-dash.md" "$LOG" || p+=("a name starting with - is not guarded by ./: $(cat "$LOG")")
check "a file name that starts with - is not read as an option" ${p[@]+"${p[@]}"}

mkdir -p "$repo/node_modules/pkg"
printf '# T\n\nBAD\n' > "$repo/node_modules/pkg/README.md"
run_hook "$(edit_json "$repo/node_modules/pkg/README.md")"
if [[ $STATUS -eq 0 && -z "$(cat "$LOG")" ]]; then pass "a file under node_modules is not linted"; else fail "a file under node_modules is not linted" "exit $STATUS, log: $(cat "$LOG")" "output: $OUTPUT"; fi

# The repo's own node_modules/.bin copy wins over PATH, as in the pre-commit hook.
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
grep -q "^local-markdownlint " "$LOG" || p+=("the node_modules copy did not run")
[[ "$(ran markdownlint-cli2)" == 0 ]] || p+=("the PATH copy ran as well")
check "the repo's node_modules/.bin/markdownlint-cli2 is preferred" ${p[@]+"${p[@]}"}
rm -rf "$repo/node_modules/.bin"

# --- YAML -----------------------------------------------------------------
run_hook "$(edit_json "$repo/config.yml")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
grep -q "^yamllint $repo -c .yamllint.yml ./config.yml\$" "$LOG" || p+=("yamllint did not get the repo's config and ./config.yml: $(cat "$LOG")")
[[ "$(ran actionlint)" == 0 && "$(ran zizmor)" == 0 ]] || p+=("a plain YAML file went through the workflow linters")
check "a plain YAML file goes through yamllint with the repo's config, and only that" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/bad.yml")"
if [[ $STATUS -eq 2 && "$OUTPUT" == *"a violation"* ]]; then pass "a YAML violation exits 2"; else fail "a YAML violation exits 2" "exit $STATUS" "output: $OUTPUT"; fi

run_hook "$(edit_json "$repo/.github/workflows/ci.yml")"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
for tool in yamllint actionlint zizmor; do [[ "$(ran "$tool")" == 1 ]] || p+=("$tool did not run once"); done
grep -q "^zizmor $repo --offline --min-severity medium -- ./.github/workflows/ci.yml\$" "$LOG" || p+=("zizmor's arguments: $(cat "$LOG")")
check "a workflow goes through yamllint, actionlint and zizmor" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/.github/workflows/bad.yml")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
[[ "$(grep -c 'a violation' <<< "$OUTPUT")" -ge 3 ]] || p+=("not every linter's findings were reported together")
check "a workflow's findings from all three linters come back in one message" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/.github/dependabot.yml")"
p=()
[[ "$(ran yamllint)" == 1 && "$(ran zizmor)" == 1 && "$(ran actionlint)" == 0 ]] || p+=("want yamllint and zizmor, not actionlint: $(cat "$LOG")")
check "dependabot.yml goes through yamllint and zizmor, not actionlint" ${p[@]+"${p[@]}"}

printf 'key: BAD\n' > "$repo/other.yaml"
run_hook "$(edit_json "$repo/other.yaml")"
if [[ $STATUS -eq 2 && "$(ran yamllint)" == 1 ]]; then pass "a .yaml file is linted as YAML"; else fail "a .yaml file is linted as YAML" "exit $STATUS" "output: $OUTPUT"; fi

printf 'name: ci\n' > "$repo/.github/workflows/other.yaml"
run_hook "$(edit_json "$repo/.github/workflows/other.yaml")"
if [[ "$(ran actionlint)" == 1 && "$(ran zizmor)" == 1 ]]; then pass "a workflow named .yaml goes through actionlint and zizmor"; else fail "a workflow named .yaml goes through actionlint and zizmor" "log: $(cat "$LOG")"; fi

rm "$repo/.yamllint.yml"
run_hook "$(edit_json "$repo/config.yml")"
if grep -q "^yamllint $repo ./config.yml\$" "$LOG"; then pass "without a .yamllint.yml, yamllint runs with its defaults"; else fail "without a .yamllint.yml, yamllint runs with its defaults" "log: $(cat "$LOG")" "output: $OUTPUT"; fi
printf 'rules: {}\n' > "$repo/.yamllint.yml"

# --- Not linted -----------------------------------------------------------
printf 'BAD\n' > "$repo/script.sh"
run_hook "$(edit_json "$repo/script.sh")"
if [[ $STATUS -eq 0 && -z "$(cat "$LOG")" ]]; then pass "a file that is neither Markdown nor YAML is left alone"; else fail "a file that is neither Markdown nor YAML is left alone" "exit $STATUS, log: $(cat "$LOG")" "output: $OUTPUT"; fi

run_hook "$(edit_json "$repo/docs/missing.md")"
if [[ $STATUS -eq 0 && -z "$OUTPUT" ]]; then pass "a file that no longer exists is left alone"; else fail "a file that no longer exists is left alone" "exit $STATUS" "output: $OUTPUT"; fi

mkdir -p "$WORK/not-a-repo"
printf '# T\n\nBAD\n' > "$WORK/not-a-repo/x.md"
run_hook "$(edit_json "$WORK/not-a-repo/x.md")"
if [[ $STATUS -eq 0 && -z "$(cat "$LOG")" ]]; then pass "a file outside any git repository is left alone"; else fail "a file outside any git repository is left alone" "exit $STATUS, log: $(cat "$LOG")" "output: $OUTPUT"; fi

# A repository nested in the project is its own root: its configs apply, not the outer repo's.
inner="$(new_repo app/inner)"
printf '# T\n\nBAD\n' > "$inner/x.md"
run_hook "$(edit_json "$inner/x.md")"
if grep -q "^markdownlint-cli2 $inner " "$LOG"; then pass "a nested repository is linted from its own root"; else fail "a nested repository is linted from its own root" "log: $(cat "$LOG")" "output: $OUTPUT"; fi

# A linked worktree is its own root too.
git -C "$repo" -c user.email=t@t -c user.name=t -c commit.gpgsign=false commit --quiet --allow-empty -m init
git -C "$repo" worktree add --quiet -b wt "$WORK/wt" > /dev/null 2>&1
printf '# T\n\nBAD\n' > "$WORK/wt/w.md"
run_hook "$(edit_json "$WORK/wt/w.md")"
p=()
[[ $STATUS -eq 2 ]] || p+=("exit $STATUS, want 2")
grep -q "^markdownlint-cli2 $WORK/wt " "$LOG" || p+=("not run from the worktree's root: $(cat "$LOG")")
check "a file in a linked worktree is linted from that worktree" ${p[@]+"${p[@]}"}

# --- Input that is not a file edit ----------------------------------------
for json in '{}' '{"tool_input": {}}' '{"tool_input": {"file_path": 5}}' '[]' '{"tool_input": "x"}'; do
  run_hook "$json"
  if [[ $STATUS -eq 0 && -z "$(cat "$LOG")" ]]; then pass "input $json lints nothing and exits 0"; else fail "input $json lints nothing and exits 0" "exit $STATUS, log: $(cat "$LOG")" "output: $OUTPUT"; fi
done

run_hook 'not json'
p=()
[[ $STATUS -eq 1 ]] || p+=("exit $STATUS, want 1: input that is not JSON is the hook's own failure")
[[ "$OUTPUT" == *"not JSON"* ]] || p+=("no message")
check "input that is not JSON exits 1 with a message, not 0" ${p[@]+"${p[@]}"}

# --- Missing tools --------------------------------------------------------
NOLINT="$WORK/nolint"
mkdir -p "$NOLINT"
for tool in bash git python3 tr basename dirname env cat grep mkdir; do
  src="$(command -v "$tool")" && ln -s "$src" "$NOLINT/$tool"
done
run_hook "$(edit_json "$repo/docs/bad.md")" "$NOLINT"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
[[ "$OUTPUT" == *"markdownlint-cli2 not installed"* ]] || p+=("no note that the file was not linted")
check "without markdownlint-cli2, the hook says so and exits 0" ${p[@]+"${p[@]}"}

run_hook "$(edit_json "$repo/.github/workflows/ci.yml")" "$NOLINT"
p=()
[[ $STATUS -eq 0 ]] || p+=("exit $STATUS, want 0")
[[ "$OUTPUT" == *"yamllint actionlint zizmor not installed"* ]] || p+=("the note doesn't name all three")
check "without the YAML linters, the hook names each missing one" ${p[@]+"${p[@]}"}

NOPY="$WORK/nopy"
mkdir -p "$NOPY"
run_hook "$(edit_json "$repo/docs/bad.md")" "$NOPY"
if [[ $STATUS -eq 1 && "$OUTPUT" == *"python3 not found"* ]]; then pass "without python3, the hook exits 1 with a message"; else fail "without python3, the hook exits 1 with a message" "exit $STATUS" "output: $OUTPUT"; fi

# --- The real markdownlint-cli2, when installed ----------------------------
if command -v markdownlint-cli2 > /dev/null 2>&1; then
  real="$(new_repo real)"
  cat > "$real/.markdownlint-cli2.jsonc" << 'EOF'
{ "config": { "default": true }, "ignores": ["skipped/**"] }
EOF
  mkdir -p "$real/skipped"
  printf '# T\n\n\n\ntext\n' > "$real/bad.md"
  printf '# T\n\n\n\ntext\n' > "$real/skipped/bad.md"
  printf '# T\n\ntext\n' > "$real/good.md"
  REAL_PATH="$(dirname "$(command -v markdownlint-cli2)"):$TOOLS"
  run_hook "$(edit_json "$real/bad.md")" "$REAL_PATH"
  if [[ $STATUS -eq 2 && "$OUTPUT" == *MD012* ]]; then pass "markdownlint-cli2 itself: a violation exits 2"; else fail "markdownlint-cli2 itself: a violation exits 2" "exit $STATUS" "output: $OUTPUT"; fi
  run_hook "$(edit_json "$real/good.md")" "$REAL_PATH"
  if [[ $STATUS -eq 0 ]]; then pass "markdownlint-cli2 itself: a clean file exits 0"; else fail "markdownlint-cli2 itself: a clean file exits 0" "exit $STATUS" "output: $OUTPUT"; fi
  run_hook "$(edit_json "$real/skipped/bad.md")" "$REAL_PATH"
  if [[ $STATUS -eq 0 ]]; then pass "markdownlint-cli2 itself: a file the config ignores passes"; else fail "markdownlint-cli2 itself: a file the config ignores passes" "exit $STATUS" "output: $OUTPUT"; fi
else
  echo "skip - markdownlint-cli2 is not installed, so its own run is not tested"
fi

if [[ -x "$SCRIPT" ]]; then
  pass "scripts/lint-edited-file.sh is executable"
else
  fail "scripts/lint-edited-file.sh is executable"
fi

echo
echo "$PASSED passed, $FAILED failed"
[[ $FAILED -eq 0 ]]
