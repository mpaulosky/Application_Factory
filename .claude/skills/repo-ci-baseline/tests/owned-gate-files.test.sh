#!/usr/bin/env bash
# Tests for the Owned scripts/gate.sh's lists of changed files (the lines from "# Files added" to the SHELL_FILES mapfile), run in a
# throwaway repo: every name reaches its list as it is on disk, a non-ASCII one, one with a tab or a newline among them, and a git that
# fails stops the gate. The gate's other steps need a .NET repo, so only these lines are run.
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
names=("notes/café.md" "a b.md" "docs/blogs/post.md" "tab${tab}name.md" "line${newline}break.md" "conf/settings.yml" "pnpm-lock.yaml"
  ".github/workflows/build.yml" "scripts/tool" "scripts/run.sh" "src/app.cs")
for name in "${names[@]}"; do
  mkdir -p "$repo/$(dirname "$name")"
  printf 'x\n' > "$repo/$name"
  g "$repo" add -- "$name"
done
g "$repo" commit -q -m names

# lists [<git wrapper>]: run the block in the repo and print each list's entries, one per line, shell-quoted.
lists() {
  (
    cd "$repo" || exit 1
    set -euo pipefail
    [[ -z "${1:-}" ]] || eval "$1"
    # shellcheck disable=SC1090 # the block is the gate's own lines
    source "$block"
    for list in MD_FILES YAML_FILES WORKFLOW_FILES SHELL_FILES; do
      declare -n entries="$list"
      for entry in "${entries[@]}"; do printf '%s %q\n' "$list" "$entry"; done
    done
  )
}
out="$(lists)"; st=$?
check "the block runs" "[[ $st == 0 ]]"
has() { grep -qxF -- "$1 $(printf '%q' "$2")" <<< "$out"; }
check "a non-ASCII Markdown name is listed as it is on disk" "has MD_FILES 'notes/café.md'"
check "a name with a space is listed" "has MD_FILES 'a b.md'"
check "a name with a tab is listed" "has MD_FILES \"tab\${tab}name.md\""
check "a name with a newline is listed whole" "has MD_FILES \"line\${newline}break.md\""
check "docs/blogs/ stays out of the Markdown list" "! grep -q 'docs/blogs' <<< \"\$out\""
check "no name is quoted by git (no backslash-escaped octal, no surrounding quotes)" "! grep -qE '\\\\[0-9]{3}|\"' <<< \"\$out\""
check "the Markdown list holds exactly those four" "[[ \$(grep -c '^MD_FILES ' <<< \"\$out\") == 4 ]]"
check "YAML: the config and the workflow, not pnpm-lock.yaml" \
  "has YAML_FILES conf/settings.yml && has YAML_FILES .github/workflows/build.yml && ! grep -q pnpm-lock <<< \"\$out\""
check "the workflow list holds the workflow alone" "has WORKFLOW_FILES .github/workflows/build.yml && [[ \$(grep -c '^WORKFLOW_FILES ' <<< \"\$out\") == 1 ]]"
check "shell: an extensionless script and a .sh" "has SHELL_FILES scripts/tool && has SHELL_FILES scripts/run.sh && [[ \$(grep -c '^SHELL_FILES ' <<< \"\$out\") == 2 ]]"

# Without origin/main every tracked file is listed, by the same rules.
g "$repo" remote remove origin
g "$repo" update-ref -d refs/remotes/origin/main
out="$(lists)"; st=$?
check "with no origin/main the block runs on every tracked file" "[[ $st == 0 ]] && has MD_FILES README.md && has MD_FILES 'notes/café.md'"
check "with no origin/main a newline name is still listed whole" "has MD_FILES \"line\${newline}break.md\""

# A git that fails stops the block, instead of leaving the lists empty.
# shellcheck disable=SC2016 # the wrapper is code for lists to eval, so it must not expand here
out="$(lists 'git() { if [[ "$1" == ls-files ]]; then return 1; fi; command git "$@"; }' 2>&1)"; st=$?
check "a git ls-files that fails stops the block (set -e), and lists nothing" "[[ $st != 0 ]] && ! grep -q '^MD_FILES' <<< \"\$out\""

echo
echo "$PASSED passed, $FAILED failed"
[[ $FAILED -eq 0 ]]
