#!/usr/bin/env bash
# Tests for the Owned scripts/gate.sh's lists of changed files (the lines from "# Files added" to the SHELL_FILES mapfile), run in a
# throwaway repo, and the Markdown step with a stub linter: every name reaches its list and the linter as it is on disk (non-ASCII, not
# valid UTF-8, a tab or a newline among them), in a UTF-8 locale and in C, and a git that fails stops the gate. The gate's other steps
# need a .NET repo, so only these lines are run.
# Usage: bash tests/owned-gate-files.test.sh
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
gate="$here/../template/owned/scripts/gate.sh"
work="$(mktemp -d)" || exit 1
[[ -n "$work" && -d "$work" ]] || exit 1
trap 'command rm -rf -- "$work"' EXIT
PASSED=0; FAILED=0
check() { if eval "$2"; then PASSED=$((PASSED + 1)); echo "ok   - $1"; else FAILED=$((FAILED + 1)); echo "FAIL - $1"; fi; }

block="$work/lists.sh"
sed -n '/^# Files added, copied, modified or renamed/,/^mapfile -d .* SHELL_FILES/p' "$gate" > "$block"
check "the gate's list block is found, from its comment to the SHELL_FILES mapfile" \
  "grep -q '^# Files added' '$block' && grep -q 'SHELL_FILES' '$block' && [[ \$(wc -l < '$block') -gt 5 ]]"

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
  "x.md${newline}docs/blogs/y.md" "a.yaml${newline}pnpm-lock.yaml" "foo${newline}.github/workflows/a.yml" "a[1].md" "-dash.md")
for name in "${names[@]}"; do
  mkdir -p "$repo/$(dirname -- "$name")"
  printf 'x\n' > "$repo/$name"
  g "$repo" add -- "$name"
done
g "$repo" commit -q -m names

# lists [<git wrapper>]: run the block in the repo, then the gate's Markdown step with a markdownlint-cli2 stub that records its
# arguments; print each list's entries as "<list><TAB><name>" and each argument as "MD_ARG<TAB><arg>", NUL-separated.
stubs="$work/stubs"
mkdir -p "$stubs"
# shellcheck disable=SC2016 # the stub's own code, written as it is
printf '#!/usr/bin/env bash\nprintf "MD_ARG\\t%%s\\0" "$@" >> "$MD_ARGS_FILE"\n' > "$stubs/markdownlint-cli2"
chmod +x "$stubs/markdownlint-cli2"
md_step="$work/md-step.sh"
sed -n '/^step "Markdown lint/,/^fi$/p' "$gate" > "$md_step"
lists() {
  (
    cd "$repo" || exit 1
    set -euo pipefail
    [[ -z "${1:-}" ]] || eval "$1"
    # shellcheck disable=SC1090 # the block is the gate's own lines
    source "$block"
    for list in MD_FILES YAML_FILES WORKFLOW_FILES SHELL_FILES; do
      declare -n entries="$list"
      for entry in ${entries[@]+"${entries[@]}"}; do printf '%s\t%s\0' "$list" "$entry"; done
    done
    # The step's own helper and root, which the sourced lines use.
    # shellcheck disable=SC2329,SC2034
    step() { :; }
    # shellcheck disable=SC2034
    ROOT="$repo" MD_ARGS_FILE="$work/md-args"
    export MD_ARGS_FILE
    : > "$MD_ARGS_FILE"
    PATH="$stubs:$PATH"
    # shellcheck disable=SC1090 # the gate's own Markdown step
    source "$md_step"
    cat "$MD_ARGS_FILE"
  )
}
# run_lists [<git wrapper>]: lists, into the array OUT and its status into st.
run_lists() { local raw="$work/out"; lists "$@" > "$raw" 2> "$work/err"; st=$?; mapfile -d '' -t OUT < "$raw"; }
has() { local want="$1"$'\t'"$2" entry; for entry in ${OUT[@]+"${OUT[@]}"}; do [[ "$entry" == "$want" ]] && return 0; done; return 1; }
count() { local n=0 entry; for entry in ${OUT[@]+"${OUT[@]}"}; do [[ "$entry" == "$1"$'\t'* ]] && n=$((n + 1)); done; echo "$n"; }

check "the gate's Markdown step is found" "grep -q 'markdownlint-cli2' '$md_step' && [[ \$(wc -l < '$md_step') -gt 5 ]]"

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
  check "[$loc] YAML: not pnpm-lock.yaml" "has YAML_FILES conf/settings.yml && ! has YAML_FILES pnpm-lock.yaml && [[ \$(count YAML_FILES) == 4 ]]"
  check "[$loc] the workflow list holds the workflow alone" "has WORKFLOW_FILES .github/workflows/build.yml && [[ \$(count WORKFLOW_FILES) == 1 ]]"
  check "[$loc] shell: the extensionless scripts and a .sh" "has SHELL_FILES scripts/tool && has SHELL_FILES scripts/run.sh && [[ \$(count SHELL_FILES) == 3 ]]"
  check "[$loc] markdownlint-cli2 gets each name as a literal path (a leading :), never a glob or an option" \
    "has MD_ARG ':a[1].md' && has MD_ARG ':-dash.md' && has MD_ARG \":line\${newline}break.md\" && has MD_ARG \":\${latin1}.md\" && [[ \$(count MD_ARG) == 8 ]] && ! has MD_ARG a[1].md"
done
unset LC_ALL

# The real markdownlint-cli2, when installed, reads the gate's ':<name>' as that file: a[1].md is linted, not a1.md beside it, and
# -dash.md is a file, not an option. An Application's hook-tests job has none, so there this block is skipped.
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
  check "markdownlint-cli2 itself: a violation in a[1].md is reported" "[[ $rst != 0 ]] && [[ \"\$rout\" == *'a[1].md'* ]]"
  # shellcheck disable=SC2059
  printf "$bad" > "$real/-dash.md"
  real_md -dash.md
  check "markdownlint-cli2 itself: -dash.md is linted as a file" "[[ $rst != 0 ]] && [[ \"\$rout\" == *'-dash.md'* ]]"
  # shellcheck disable=SC2059
  printf "$bad" > "$real/line${newline}break.md"
  real_md "line${newline}break.md"
  check "markdownlint-cli2 itself: a name with a newline is linted as that file" "[[ $rst != 0 ]]"
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
