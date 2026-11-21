#!/usr/bin/env bash
# Tests for the Owned scripts/gate.sh's lists of changed files (the lines from "# Files added" to the PY_FILES mapfile), run in a
# throwaway repo, and the Markdown, YAML, shell lint, shell format and Python lint steps with stub linters: every name reaches its list and its linter as it is on disk
# (non-ASCII, not valid UTF-8, a tab, a newline or a leading '-' among them), in a UTF-8 locale and in C, and a git that fails stops the
# gate. The gate's other steps need a .NET repo, so only these lines are run.
# Usage: bash tests/owned-gate-files.test.sh
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
gate="$here/../template/owned/scripts/gate.sh"
work="$(mktemp -d)" || exit 1
[[ -n "$work" && -d "$work" ]] || exit 1
trap 'command rm -rf -- "$work"' EXIT
PASSED=0
FAILED=0
check() { if eval "$2"; then
  PASSED=$((PASSED + 1))
  echo "ok   - $1"
else
  FAILED=$((FAILED + 1))
  echo "FAIL - $1"
fi; }

block="$work/lists.sh"
sed -n '/^# Files added, copied, modified or renamed/,/^mapfile -d .* PY_FILES/p' "$gate" > "$block"
check "the gate's list block is found, from its comment to the PY_FILES mapfile" \
  "grep -q '^# Files added' '$block' && grep -q '^mapfile .* SHELL_FILES' '$block' && grep -q '^mapfile .* SHFMT_FILES' '$block' && grep -q '^mapfile .* PY_FILES' '$block' && [[ \$(wc -l < '$block') -gt 5 ]]"

export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null
g() { git -C "$1" -c user.name=t -c user.email=t@t "${@:2}"; }
origin="$work/origin.git"
repo="$work/repo"
git init -q --bare "$origin"
git init -q -b main "$repo"
printf 'base\n' > "$repo/README.md"
g "$repo" add README.md
g "$repo" commit -q -m base
g "$repo" remote add origin "$origin"
g "$repo" push -q origin main
g "$repo" fetch -q origin
g "$repo" checkout -q -b feature/1-names

tab=$'\t'
newline=$'\n'
latin1="$(printf 'caf\xe9')"
names=("notes/café.md" "a b.md" "docs/blogs/post.md" "tab${tab}name.md" "line${newline}break.md" "conf/settings.yml" "pnpm-lock.yaml"
  ".github/workflows/build.yml" "scripts/tool" "scripts/run.sh" "src/app.cs" "${latin1}.md" "scripts/${latin1}"
  "x.md${newline}docs/blogs/y.md" "a.yaml${newline}pnpm-lock.yaml" "foo${newline}.github/workflows/a.yml" "a[1].md" "-dash.md" "-dash.yml" "-dash.sh"
  ".github/hooks/pre-push" "tools/.github/hooks/pre-push" "tools/a.py" "-dash.py" "${latin1}.py" "UPPER.PY" "notpy.pyc")
for name in "${names[@]}"; do
  mkdir -p "$repo/$(dirname -- "$name")"
  printf 'x\n' > "$repo/$name"
  g "$repo" add -- "$name"
done
g "$repo" commit -q -m names

# lists [<git wrapper>]: run the block in the repo, then the gate's Markdown, YAML and shell steps with stubs of their linters
# (markdownlint-cli2, yamllint, shellcheck, shfmt) that record their arguments; print each list's entries as "<list><TAB><name>" and each argument as
# "MD_ARG<TAB><arg>", "YL_ARG<TAB><arg>", "SC_ARG<TAB><arg>", "SF_ARG<TAB><arg>" or "RF_ARG<TAB><arg>", NUL-separated. The shfmt stub answers --version with
# $SHFMT_STUB_VERSION (v3.14.1 when unset) and exit $SHFMT_STUB_VERSION_EXIT (0 when unset), and otherwise exits $SHFMT_STUB_EXIT (0 when unset).
# The ruff stub does the same with STUB_RUFF_VERSION (ruff 0.17.6), STUB_RUFF_VERSION_EXIT and STUB_RUFF_EXIT; the uvx stub records its
# arguments as UV_ARG.
stubs="$work/stubs"
mkdir -p "$stubs"
# shellcheck disable=SC2016 # the stub's own code, written as it is
for stub in markdownlint-cli2:MD_ARG yamllint:YL_ARG shellcheck:SC_ARG; do
  # shellcheck disable=SC2016 # the stub's own code, written as it is
  printf '#!/usr/bin/env bash\nprintf "%s\\t%%s\\0" "$@" >> "$MD_ARGS_FILE"\n' "${stub#*:}" > "$stubs/${stub%%:*}"
  chmod +x "$stubs/${stub%%:*}"
done
cat > "$stubs/shfmt" << 'STUB'
#!/usr/bin/env bash
if [[ "${1:-}" == --version ]]; then
  echo "${SHFMT_STUB_VERSION-v3.14.1}"
  exit "${SHFMT_STUB_VERSION_EXIT:-0}"
fi
printf 'SF_ARG\t%s\0' "$@" >> "$MD_ARGS_FILE"
exit "${SHFMT_STUB_EXIT:-0}"
STUB
chmod +x "$stubs/shfmt"
cat > "$stubs/ruff" << 'STUB'
#!/usr/bin/env bash
if [[ "${1:-}" == --version ]]; then
  echo "${STUB_RUFF_VERSION-ruff 0.17.6}"
  exit "${STUB_RUFF_VERSION_EXIT:-0}"
fi
printf 'RF_ARG\t%s\0' "$@" >> "$MD_ARGS_FILE"
exit "${STUB_RUFF_EXIT:-0}"
STUB
chmod +x "$stubs/ruff"
uvx_stubs="$work/uvx-stubs"
mkdir -p "$uvx_stubs"
cat > "$uvx_stubs/uvx" << 'STUB'
#!/usr/bin/env bash
printf 'UV_ARG\t%s\0' "$@" >> "$MD_ARGS_FILE"
exit "${STUB_RUFF_EXIT:-0}"
STUB
chmod +x "$uvx_stubs/uvx"
md_step="$work/md-step.sh"
sed -n '/^step "Markdown lint/,/^fi$/p' "$gate" > "$md_step"
# The YAML and shell steps, and the run_tool helper the shell step calls.
other_steps="$work/other-steps.sh"
sed -n '/^SHELLCHECK_VERSION=/p; /^run_tool() {/,/^}/p; /^step "YAML lint/,/^fi$/p; /^step "Shell lint/,/^fi$/p' "$gate" > "$other_steps"
format_step="$work/format-step.sh"
sed -n '/^# Each changed shell file is formatted/,/^fi$/p' "$gate" > "$format_step"
# The helpers and settings the shell format and Python lint steps read.
helpers="$work/helpers.sh"
sed -n '/^printable() {/,/^}/p; /^have_uvx() /p; /^RUFF_VERSION=/p; /^RUFF_RELEASE=/p; /^RUFF_ARGS=/p' "$gate" > "$helpers"
py_step="$work/py-step.sh"
sed -n '/^# Each changed Python file passes/,/^fi$/p' "$gate" > "$py_step"
lists() {
  (
    cd "$repo" || exit 1
    set -euo pipefail
    [[ -z "${1:-}" ]] || eval "$1"
    # shellcheck disable=SC1090 # the block is the gate's own lines
    source "$block"
    for list in MD_FILES YAML_FILES WORKFLOW_FILES SHELL_FILES SHFMT_FILES PY_FILES; do
      declare -n entries="$list"
      for entry in ${entries[@]+"${entries[@]}"}; do printf '%s\t%s\0' "$list" "$entry"; done
    done
    # The step's own helper and root, which the sourced lines use.
    # shellcheck disable=SC2329,SC2034
    step() { :; }
    # shellcheck disable=SC2034 # read by the sourced steps
    ROOT="$repo" MD_ARGS_FILE="$work/md-args" YELLOW='' CYAN='' RESET=
    export MD_ARGS_FILE
    : > "$MD_ARGS_FILE"
    # shellcheck disable=SC2030 # the stubs first, in this subshell only
    PATH="$stubs:$PATH"
    # shellcheck disable=SC1090 # the gate's own Markdown step
    source "$md_step"
    # shellcheck disable=SC1090 # the gate's own helpers and ruff settings
    source "$helpers"
    # shellcheck disable=SC1090 # the gate's own YAML and shell steps
    source "$other_steps"
    # shellcheck disable=SC1090 # the gate's own shell format step
    source "$format_step"
    # shellcheck disable=SC1090 # the gate's own Python lint step
    source "$py_step"
    cat "$MD_ARGS_FILE"
  )
}
# run_lists [<git wrapper>]: lists, into the array OUT and its status into st.
run_lists() {
  local raw="$work/out"
  lists "$@" > "$raw" 2> "$work/err"
  st=$?
  mapfile -d '' -t OUT < "$raw"
}
has() {
  local want="$1"$'\t'"$2" entry
  for entry in ${OUT[@]+"${OUT[@]}"}; do [[ "$entry" == "$want" ]] && return 0; done
  return 1
}
count() {
  local n=0 entry
  for entry in ${OUT[@]+"${OUT[@]}"}; do [[ "$entry" == "$1"$'\t'* ]] && n=$((n + 1)); done
  echo "$n"
}
# first <prefix>: the first argument recorded under that prefix.
first() {
  local entry
  for entry in ${OUT[@]+"${OUT[@]}"}; do [[ "$entry" != "$1"$'\t'* ]] || {
    echo "${entry#*$'\t'}"
    return
  }; done
}

check "the gate's Markdown step is found" "grep -q 'markdownlint-cli2' '$md_step' && [[ \$(wc -l < '$md_step') -gt 5 ]]"
check "the gate's YAML and shell steps and run_tool are found" \
  "grep -q '^run_tool() {' '$other_steps' && grep -q '^step \"YAML lint' '$other_steps' && grep -q '^step \"Shell lint' '$other_steps'"
check "the gate's helpers and ruff settings are found" \
  "grep -q '^printable() {' '$helpers' && grep -q '^have_uvx() ' '$helpers' && grep -q '^RUFF_VERSION=' '$helpers' && grep -q '^RUFF_RELEASE=' '$helpers' && grep -q '^RUFF_ARGS=' '$helpers'"
check "the gate's Python lint step is found, and only it" \
  "grep -q '^step \"Python lint' '$py_step' && grep -qF '\"\${RUFF_ARGS[@]}\" -- \"\${PY_FILES[@]}\"' '$py_step' && ! grep -q 'check_gh_repo.py --repo' '$py_step'"
check "the gate's shell format step is found, and only it" \
  "grep -q '^step \"Shell format' '$format_step' && grep -q 'shfmt -d --' '$format_step' && ! grep -q 'check_gh_repo.py --repo' '$format_step'"

# The gate runs in whatever locale the caller has: a UTF-8 one and C are both tried.
utf8="$(locale -a 2> /dev/null | grep -iE '^(C|en_US)\.utf-?8$' | head -n 1 || true)"
# Without one, the not-valid-UTF-8 name is only tried in C, where grep never dropped it: say so.
[[ -n "$utf8" ]] || echo "skip - no C.UTF-8 or en_US.UTF-8 locale here, so the UTF-8 half (where grep dropped a not-valid-UTF-8 name) doesn't run"
for loc in ${utf8:+"$utf8"} C; do
  export LC_ALL="$loc"
  run_lists
  check "[$loc] the block runs" "[[ $st == 0 ]]"
  check "[$loc] a non-ASCII Markdown name is listed as it is on disk" "has MD_FILES 'notes/café.md'"
  check "[$loc] a name that isn't valid UTF-8 is listed, byte for byte" "has MD_FILES \"\${latin1}.md\" && has SHELL_FILES \"scripts/\${latin1}\""
  check "[$loc] a name with a space is listed" "has MD_FILES 'a b.md'"
  check "[$loc] a name with a tab is listed" "has MD_FILES \"tab\${tab}name.md\""
  check "[$loc] a name with a newline is listed whole" "has MD_FILES \"line\${newline}break.md\""
  check "[$loc] an exclusion matches a whole name, not a line of one" \
    "has MD_FILES \"x.md\${newline}docs/blogs/y.md\" && has YAML_FILES \"a.yaml\${newline}pnpm-lock.yaml\""
  check "[$loc] an inclusion matches a whole name, not a line of one" \
    "! has WORKFLOW_FILES \"foo\${newline}.github/workflows/a.yml\" && has YAML_FILES \"foo\${newline}.github/workflows/a.yml\""
  check "[$loc] docs/blogs/ stays out of the Markdown list" "! has MD_FILES docs/blogs/post.md"
  check "[$loc] the Markdown list holds exactly the eight others" "[[ \$(count MD_FILES) == 8 ]]"
  check "[$loc] YAML: not pnpm-lock.yaml" "has YAML_FILES conf/settings.yml && ! has YAML_FILES pnpm-lock.yaml && [[ \$(count YAML_FILES) == 5 ]]"
  check "[$loc] the workflow list holds the workflow alone" "has WORKFLOW_FILES .github/workflows/build.yml && [[ \$(count WORKFLOW_FILES) == 1 ]]"
  check "[$loc] shell: the extensionless scripts, a .sh and the root hook, not a hook in another folder" \
    "has SHELL_FILES scripts/tool && has SHELL_FILES scripts/run.sh && has SHELL_FILES .github/hooks/pre-push && ! has SHELL_FILES tools/.github/hooks/pre-push && [[ \$(count SHELL_FILES) == 5 ]]"
  check "[$loc] shfmt: the .sh files and the root hook, not an extensionless scripts/ file, which no .editorconfig section styles" \
    "has SHFMT_FILES scripts/run.sh && has SHFMT_FILES -dash.sh && has SHFMT_FILES .github/hooks/pre-push && ! has SHFMT_FILES scripts/tool && [[ \$(count SHFMT_FILES) == 3 ]]"
  check "[$loc] markdownlint-cli2 gets each name as a literal path (a leading :), never a glob or an option" \
    "has MD_ARG ':a[1].md' && has MD_ARG ':-dash.md' && has MD_ARG \":line\${newline}break.md\" && has MD_ARG \":\${latin1}.md\" && [[ \$(count MD_ARG) == 8 ]] && ! has MD_ARG a[1].md"
  check "[$loc] yamllint gets '--' before the names, so -dash.yml is a file, not an option" \
    "[[ \$(first YL_ARG) == -- ]] && has YL_ARG -dash.yml && [[ \$(count YL_ARG) == 6 ]]"
  check "[$loc] shellcheck gets '--' before the names, so -dash.sh is a file, not an option" \
    "[[ \$(first SC_ARG) == -- ]] && has SC_ARG -dash.sh && [[ \$(count SC_ARG) == 6 ]]"
  check "[$loc] shfmt gets -d, then '--' before the names, so -dash.sh is a file, not an option" \
    "[[ \$(first SF_ARG) == -d ]] && has SF_ARG -- && has SF_ARG -dash.sh && [[ \$(count SF_ARG) == 5 ]]"
  check "[$loc] Python: every *.py, byte for byte, not a .PY or a .pyc" \
    "has PY_FILES tools/a.py && has PY_FILES -dash.py && has PY_FILES \"\${latin1}.py\" && ! has PY_FILES UPPER.PY && [[ \$(count PY_FILES) == 3 ]]"
  check "[$loc] ruff gets the gate's flags, then '--' before the names, so -dash.py is a file, not an option" \
    "[[ \$(first RF_ARG) == check ]] && has RF_ARG --isolated && has RF_ARG -- && has RF_ARG -dash.py && [[ \$(count RF_ARG) == 11 ]]"
done
unset LC_ALL

# With a .yamllint.yml the YAML step passes it first, then '--', then the names.
printf 'extends: default\n' > "$repo/.yamllint.yml"
run_lists
yl=()
for entry in ${OUT[@]+"${OUT[@]}"}; do [[ "$entry" != YL_ARG$'\t'* ]] || yl+=("${entry#*$'\t'}"); done
check "with a .yamllint.yml, yamllint gets -c .yamllint.yml, then '--', then the names" \
  "[[ $st == 0 && \"\${yl[0]:-}\" == -c && \"\${yl[1]:-}\" == .yamllint.yml && \"\${yl[2]:-}\" == -- && \${#yl[@]} == 8 ]]"
rm "$repo/.yamllint.yml"

# A branch with no change, and one whose only change no list takes: the block and the steps run, and every list is empty.
g "$repo" checkout -q -b chore/empty origin/main
run_lists
check "a branch with no changed file runs, with every list empty and no linter run" \
  "[[ $st == 0 ]] && [[ \${#OUT[@]} == 0 ]]"
mkdir -p "$repo/src" && printf 'x\n' > "$repo/src/other.cs" && g "$repo" add src/other.cs && g "$repo" commit -q -m cs
run_lists
check "a branch whose only change is a .cs file runs, with every list empty and no linter run" \
  "[[ $st == 0 ]] && [[ \${#OUT[@]} == 0 ]]"
g "$repo" checkout -q feature/1-names
unset LC_ALL

# The shell format step alone, on one name, with the shfmt stub: a release other than v3.14 or a later v3, a missing shfmt and a file shfmt
# would change each stop the gate with a message; a v3 release from v3.14 on runs it.
# format_run [<PATH>]: the step on -dash.sh with that PATH (the stubs first when unset); status in fst, stderr in ferr, and the step's
# heading in $work/steps.
format_run() {
  : > "$work/steps"
  # shellcheck disable=SC2034 # read inside the checks' eval strings
  ferr="$(cd "$repo" && (
    set -euo pipefail
    # shellcheck disable=SC2329 # called by the sourced step
    step() { printf '%s\n' "$1" >> "$work/steps"; }
    # shellcheck disable=SC2034 # read by the sourced step; FORMAT_BASE stands for the merge base the gate found (empty: no origin/main)
    SHFMT_FILES=(-dash.sh) MD_ARGS_FILE="$work/md-args" BASE="${FORMAT_BASE-base}" YELLOW='' RESET=
    export MD_ARGS_FILE
    : > "$MD_ARGS_FILE"
    # shellcheck disable=SC2030,SC2031 # the step's own PATH, in this subshell only
    PATH="${1:-$stubs:$PATH}"
    # shellcheck disable=SC1090 # the gate's own helpers
    source "$helpers"
    # shellcheck disable=SC1090 # the gate's own shell format step
    source "$format_step"
  ) 2>&1 > /dev/null)"
  fst=$?
}
for version in v3.14.0 v3.14.1 v3.15.2 v3.19.0 v3.19.9 v3.20.0 v3.99.9 v3.100.0; do
  SHFMT_STUB_VERSION="$version" format_run
  check "shell format: shfmt $version runs" "[[ $fst == 0 ]] && grep -qF -- '-dash.sh' '$work/md-args'"
done
for version in v3.13.9 v3.1.0 v3.9.0 v3.10.0 v3.14 v3.14. v2.6.4 v4.0.0 3.14.1 xv3.14.1 "v3.14.1 extra" "v3.14.1
v3.14.1"; do
  SHFMT_STUB_VERSION="$version" format_run
  check "shell format: shfmt '${version//$'\n'/\\n}' is refused, by name, before it runs" \
    "[[ $fst != 0 ]] && [[ \"\$ferr\" == *'v3.14 or a later v3 release, not'* ]] && ! grep -qF -- '-dash.sh' '$work/md-args'"
done
# A --version that fails, even after printing a release the gate takes, or that prints nothing, is refused with its own message: its
# answer can't be trusted, and it names no release.
SHFMT_STUB_VERSION=v3.14.1 SHFMT_STUB_VERSION_EXIT=1 format_run
check "shell format: a shfmt whose --version fails is refused, before it runs, as failed" \
  "[[ $fst != 0 ]] && [[ \"\$ferr\" == *\"shfmt --version exited 1 and printed 'v3.14.1', so its release is unknown\"* && \"\$ferr\" != *\"not '\"* ]] && ! grep -qF -- '-dash.sh' '$work/md-args'"
SHFMT_STUB_VERSION="" format_run
check "shell format: a shfmt whose --version prints nothing is refused, before it runs, as failed" \
  "[[ $fst != 0 ]] && [[ \"\$ferr\" == *\"shfmt --version exited 0 and printed '', so its release is unknown\"* ]] && ! grep -qF -- '-dash.sh' '$work/md-args'"
# What a release that isn't v3.14 or later prints is shown with every byte but printable ASCII as ?: an escape sequence in it doesn't
# reach the terminal.
SHFMT_STUB_VERSION=$'v3.1\033]0;x\007' format_run
check "shell format: a release holding an escape sequence is shown with ? for its control bytes" \
  "[[ $fst != 0 ]] && [[ \"\$ferr\" == *\"not 'v3.1?]0;x?'.\"* && \"\$ferr\" != *\$'\\033'* ]]"
# Bytes outside ASCII too, one ? each, and the cut is at 80 bytes, whatever the caller's locale: in a UTF-8 one, é and a bidi override
# would otherwise be one ? each, and 40 é's would all be shown.
eacute=$'\303\251'
many="v3.$(for _ in {1..40}; do printf '%s' "$eacute"; done)"
want_many="v3.$(printf '?%.0s' {1..77})"
for loc in ${utf8:+"$utf8"} C; do
  LC_ALL="$loc" SHFMT_STUB_VERSION=$'v3.\303\251\342\200\256x' format_run
  check "[$loc] shell format: a release's bytes outside ASCII are shown as one ? each" "[[ $fst != 0 ]] && [[ \"\$ferr\" == *\"not 'v3.?????x'.\"* ]]"
  LC_ALL="$loc" SHFMT_STUB_VERSION=$'v3.\377' format_run
  check "[$loc] shell format: a byte that isn't UTF-8 is shown as ?" "[[ $fst != 0 ]] && [[ \"\$ferr\" == *\"not 'v3.?'.\"* ]]"
  LC_ALL="$loc" SHFMT_STUB_VERSION="$many" format_run
  check "[$loc] shell format: an answer of 83 bytes in 43 characters is cut at 80 bytes" "[[ $fst != 0 ]] && [[ \"\$ferr\" == *\"not '$want_many'.\"* ]]"
done
# The answer is cut at 80 bytes in the message: 79, 80 and 81 of it, in ASCII.
for n in 79 80 81; do
  long="v3.$(printf '%0*d' $((n - 3)) 0)"
  SHFMT_STUB_VERSION="$long" format_run
  check "shell format: an answer of $n bytes is shown cut at 80" "[[ $fst != 0 ]] && [[ \"\$ferr\" == *\"not '\${long:0:80}'.\"* ]]"
done
export SHFMT_STUB_EXIT=1
format_run
check "shell format: a file shfmt would change, or can't parse, stops the gate, and says to run shfmt -w or fix the syntax" \
  "[[ $fst != 0 ]] && [[ \"\$ferr\" == *'run shfmt -w on each file'*'fix the syntax of any file it could not parse'* ]]"
# Any other status is shfmt failing, not a diff: the gate stops, saying so, with no shfmt -w advice.
for code in 2 3 126; do
  SHFMT_STUB_EXIT=$code format_run
  check "shell format: a shfmt that exits $code stops the gate as a failed shfmt, not a diff" \
    "[[ $fst != 0 ]] && [[ \"\$ferr\" == *'shfmt failed (exit $code) without checking the format'* ]] && [[ \"\$ferr\" != *'run shfmt -w'* ]]"
done
unset SHFMT_STUB_EXIT
# Without an origin/main the changed files are every tracked file: the step is skipped with a note, and shfmt doesn't run.
FORMAT_BASE='' format_run
check "shell format: with no origin/main the step is skipped, saying why, and shfmt doesn't run" \
  "[[ $fst == 0 ]] && ! grep -qF -- '-dash.sh' '$work/md-args'"
check "shell format: with no origin/main the heading gives no count" "[[ \"\$(cat '$work/steps')\" == 'Shell format' ]]"
format_run
check "shell format: with an origin/main the heading counts the changed files" "[[ \"\$(cat '$work/steps')\" == 'Shell format (1 changed file(s))' ]]"
# shellcheck disable=SC2034 # read inside the check's eval string
skip_out="$(cd "$repo" && (
  set -euo pipefail
  # shellcheck disable=SC2329 # called by the sourced step
  step() { :; }
  # shellcheck disable=SC2034 # read by the sourced step
  SHFMT_FILES=(-dash.sh) MD_ARGS_FILE="$work/md-args" BASE='' YELLOW='' RESET=''
  # shellcheck disable=SC2030,SC2031 # the stubs first, in this subshell only
  PATH="$stubs:$PATH"
  # shellcheck disable=SC1090 # the gate's own helpers
  source "$helpers"
  # shellcheck disable=SC1090 # the gate's own shell format step
  source "$format_step"
) 2>&1)"
check "shell format: the skip says there is no origin/main" "[[ \"\$skip_out\" == *'No origin/main'*'skipping the shell format check'* ]]"
noshfmt="$work/noshfmt"
mkdir -p "$noshfmt"
for tool in bash env; do ln -s "$(command -v "$tool")" "$noshfmt/$tool"; done
format_run "$noshfmt"
check "shell format: without shfmt the gate stops and names it" "[[ $fst != 0 ]] && [[ \"\$ferr\" == *'needs shfmt v3.14 or a later v3 release (check_gh_repo.py needs it too): install it on PATH'* ]]"

# The Python lint step alone, on one name, with the ruff and uvx stubs: an installed ruff must be a 0.17 release, whose answer is shown
# as printable() shows it; without one the pinned release runs through uvx; without either the gate stops, naming ruff.
# py_run [<PATH>]: the step on -dash.py (on no file when PY_NONE is set) with that PATH (the stubs first when unset); status in pst, stderr in
# perr, and each argument the stubs got in PARGS ("RF <arg>" or "UV <arg>").
py_run() {
  : > "$work/steps"
  # shellcheck disable=SC2034 # read inside the checks' eval strings
  perr="$(cd "$repo" && (
    set -euo pipefail
    # shellcheck disable=SC2329 # called by the sourced step
    step() { printf '%s\n' "$1" >> "$work/steps"; }
    # shellcheck disable=SC2034 # read by the sourced step
    PY_FILES=(-dash.py) MD_ARGS_FILE="$work/md-args"
    [[ -z "${PY_NONE:-}" ]] || PY_FILES=()
    export MD_ARGS_FILE
    : > "$MD_ARGS_FILE"
    # shellcheck disable=SC2030,SC2031 # the step's own PATH, in this subshell only
    PATH="${1:-$stubs:$PATH}"
    # shellcheck disable=SC1090 # the gate's own helpers and ruff settings
    source "$helpers"
    # shellcheck disable=SC1090 # the gate's own Python lint step
    source "$py_step"
  ) 2>&1 > /dev/null)"
  pst=$?
  mapfile -d '' -t PARGS < "$work/md-args"
  PARGS=("${PARGS[@]/#RF_ARG$'\t'/RF }")
  PARGS=("${PARGS[@]/#UV_ARG$'\t'/UV }")
}
py_run
check "python lint: ruff 0.17.6 gets exactly the gate's flags, '--' and the name" \
  "[[ $pst == 0 && \"\${PARGS[*]}\" == 'RF check RF --isolated RF --no-cache RF --select RF F,B RF --target-version RF py312 RF -- RF -dash.py' ]]"
check "python lint: the heading counts the changed files" "[[ \"\$(cat '$work/steps')\" == 'Python lint (1 changed file(s))' ]]"
for version in "ruff 0.17.0" "ruff 0.17.6" "ruff 0.17.10"; do
  STUB_RUFF_VERSION="$version" py_run
  check "python lint: $version runs" "[[ $pst == 0 && \${#PARGS[@]} == 9 ]]"
done
for version in "ruff 0.16.9" "ruff 0.18.0" "ruff 1.17.0" "ruff 0.17" "0.17.6" "ruff 0.17.6 extra" "xruff 0.17.6" "ruff 0.170.1" "ruff 0.17.6
ruff 0.17.6"; do
  STUB_RUFF_VERSION="$version" py_run
  check "python lint: '${version//$'\n'/\\n}' is refused, by name, before it runs" \
    "[[ $pst != 0 && \${#PARGS[@]} == 0 ]] && [[ \"\$perr\" == *'needs ruff 0.17, not'*'uv tool install ruff@0.17.6'* ]]"
done
STUB_RUFF_VERSION_EXIT=1 py_run
check "python lint: a ruff whose --version fails is refused, before it runs, as failed" \
  "[[ $pst != 0 && \${#PARGS[@]} == 0 ]] && [[ \"\$perr\" == *\"ruff --version exited 1 and printed 'ruff 0.17.6', so its release is unknown\"* && \"\$perr\" != *\"not '\"* ]]"
STUB_RUFF_VERSION="" py_run
check "python lint: a ruff whose --version prints nothing is refused, before it runs, as failed" \
  "[[ $pst != 0 && \${#PARGS[@]} == 0 ]] && [[ \"\$perr\" == *\"ruff --version exited 0 and printed '', so its release is unknown\"* ]]"
for loc in ${utf8:+"$utf8"} C; do
  LC_ALL="$loc" STUB_RUFF_VERSION=$'ruff 0.1\033]0;x\007\303\251' py_run
  check "[$loc] python lint: a release's control bytes and bytes outside ASCII are shown as one ? each" \
    "[[ $pst != 0 ]] && [[ \"\$perr\" == *\"not 'ruff 0.1?]0;x???'\"* && \"\$perr\" != *\$'\\033'* ]]"
done
STUB_RUFF_EXIT=1 py_run
check "python lint: a problem ruff finds stops the gate, saying to fix it or mark a false alarm" \
  "[[ $pst != 0 ]] && [[ \"\$perr\" == *'ruff: fix each problem shown above'*'# noqa: <code>'* ]]"
for code in 2 3 126; do
  STUB_RUFF_EXIT=$code py_run
  check "python lint: a ruff that exits $code stops the gate as a failed ruff, not a problem in the files" \
    "[[ $pst != 0 ]] && [[ \"\$perr\" == *'ruff failed (exit $code) without checking the files'* && \"\$perr\" != *'fix each problem'* ]]"
done
# Without ruff, the pinned release through uvx; without either, the gate stops. The tools folder holds bash and env alone, then uvx.
noruff="$work/noruff"
mkdir -p "$noruff"
for tool in bash env; do ln -s "$(command -v "$tool")" "$noruff/$tool"; done
py_run "$noruff:$uvx_stubs"
check "python lint: without ruff, uvx runs ruff@0.17.6 with the gate's flags" \
  "[[ $pst == 0 && \"\${PARGS[*]}\" == 'UV ruff@0.17.6 UV check UV --isolated UV --no-cache UV --select UV F,B UV --target-version UV py312 UV -- UV -dash.py' ]]"
STUB_RUFF_EXIT=1 py_run "$noruff:$uvx_stubs"
check "python lint: a problem ruff finds through uvx stops the gate too" "[[ $pst != 0 ]] && [[ \"\$perr\" == *'ruff: fix each problem shown above'* ]]"
# shellcheck disable=SC2031 # this shell's own PATH, which only py_run's subshell changes
py_run "$stubs:$uvx_stubs:$PATH"
check "python lint: an installed ruff is used, not uvx" "[[ $pst == 0 && \"\${PARGS[0]:-}\" == 'RF check' ]]"
py_run "$noruff"
check "python lint: without ruff or uv the gate stops and names both" \
  "[[ $pst != 0 && \${#PARGS[@]} == 0 ]] && [[ \"\$perr\" == *'needs ruff 0.17: uv tool install ruff@0.17.6, or install uv'* ]]"
# A branch that changes no Python file needs neither ruff nor uv, and an installed ruff is not run: ruff given no file would lint the
# whole folder it runs in.
PY_NONE=1 py_run "$noruff"
check "python lint: with no changed Python file, no ruff or uv is needed" \
  "[[ $pst == 0 && \${#PARGS[@]} == 0 && -z \"\$perr\" ]] && [[ \"\$(cat '$work/steps')\" == 'Python lint (0 changed file(s))' ]]"
PY_NONE=1 py_run
check "python lint: with no changed Python file, an installed ruff is not run" "[[ $pst == 0 && \${#PARGS[@]} == 0 ]]"

# The real markdownlint-cli2, when installed, reads the gate's ':<name>' as that file: a[1].md is linted, not a1.md beside it, and
# -dash.md is a file, not an option. Each failing check asks for the planted MD012, so a usage error or a missing file can't pass
# it. Skipped, with a skip line, where markdownlint-cli2 isn't installed (the Factory's template-tools action installs it).
if command -v markdownlint-cli2 > /dev/null 2>&1; then
  real="$work/real"
  mkdir -p "$real"
  printf '{ "config": { "default": true } }\n' > "$real/.markdownlint-cli2.jsonc"
  bad='# T\n\n\n\ntext\n'
  # real_md <name>...: the gate's Markdown step on those names, with the real tool; status in rst, output in rout.
  real_md() {
    # shellcheck disable=SC2034 # read inside the checks' eval strings
    rout="$(cd "$real" && (
      set -euo pipefail
      # shellcheck disable=SC2329 # called by the sourced step
      step() { :; }
      # shellcheck disable=SC2034 # read by the sourced step
      ROOT="$real" MD_FILES=("$@")
      # shellcheck disable=SC1090 # the gate's own Markdown step
      source "$md_step"
    ) 2>&1)"
    rst=$?
  }
  printf '# T\n' > "$real/a[1].md"
  # shellcheck disable=SC2059 # the format is the fixture
  printf "$bad" > "$real/a1.md"
  real_md 'a[1].md'
  check "markdownlint-cli2 itself: a[1].md is linted, not the a1.md a glob would match" "[[ $rst == 0 ]]"
  # shellcheck disable=SC2059
  printf "$bad" > "$real/a[1].md"
  printf '# T\n' > "$real/a1.md"
  real_md 'a[1].md'
  check "markdownlint-cli2 itself: a violation in a[1].md is reported" "[[ $rst != 0 ]] && [[ \"\$rout\" == *'a[1].md'*MD012* ]]"
  # shellcheck disable=SC2059
  printf "$bad" > "$real/-dash.md"
  real_md -dash.md
  check "markdownlint-cli2 itself: -dash.md is linted as a file" "[[ $rst != 0 ]] && [[ \"\$rout\" == *'-dash.md'*MD012* ]]"
  # shellcheck disable=SC2059
  printf "$bad" > "$real/line${newline}break.md"
  real_md "line${newline}break.md"
  check "markdownlint-cli2 itself: a name with a newline is linted as that file" "[[ $rst != 0 ]] && [[ \"\$rout\" == *'break.md'*MD012* ]]"
else
  echo "skip - markdownlint-cli2 isn't installed, so the gate's ':<name>' form isn't tried with the real tool"
fi

# A git diff that fails stops the block, instead of leaving the lists empty.
# shellcheck disable=SC2016 # the wrapper is code for lists to eval, so it must not expand here
run_lists 'git() { if [[ "$1" == diff ]]; then return 1; fi; command git "$@"; }'
check "a git diff that fails stops the block (set -e), and lists nothing" "[[ $st != 0 ]] && [[ \$(count MD_FILES) == 0 ]] && [[ \$(count MD_ARG) == 0 ]]"

# Without origin/main every tracked file is listed, by the same rules.
g "$repo" remote remove origin
g "$repo" update-ref -d refs/remotes/origin/main
run_lists
check "with no origin/main the block runs on every tracked file" "[[ $st == 0 ]] && has MD_FILES README.md && has MD_FILES 'notes/café.md'"
check "with no origin/main a newline name is still listed whole" "has MD_FILES \"line\${newline}break.md\""

# A git ls-files that fails stops the block too.
# shellcheck disable=SC2016 # the wrapper is code for lists to eval, so it must not expand here
run_lists 'git() { if [[ "$1" == ls-files ]]; then return 1; fi; command git "$@"; }'
check "a git ls-files that fails stops the block (set -e), and lists nothing" "[[ $st != 0 ]] && [[ \$(count MD_FILES) == 0 ]]"

echo
echo "$PASSED passed, $FAILED failed"
[[ $FAILED -eq 0 ]]
