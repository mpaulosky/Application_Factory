#!/usr/bin/env bash
# Local quality gate, run by .github/hooks/pre-push and safe to run by hand.
# Lints the Markdown, YAML, workflow and shell files changed since origin/main
# (every unpushed commit, with the same configs as CI), checks that the
# changed shell files are formatted as shfmt formats them, lints the changed
# Python files with ruff (bug-finding rules only), checks that the
# branch's diff passed the local review (/pre-push-review), that its own
# scripts and skills tell every gh command its repository, that
# .github/copilot-instructions.md matches the repo's code-review skill (when
# it has one), runs the repo's own checks in .github/ci/gate-checks.sh, checks
# that every central NuGet pin is referenced (check_central_pins.py), builds
# the solution, then runs each test project CI's discover_tests.py finds under
# tests/. Exits non-zero on the first
# failing gate. GATE_SKIP_REVIEW=1 skips the review check, for gate runs before
# the push (the review's own, and while iterating), never for a push.
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"
# Under a hook git sets GIT_DIR (and GIT_INDEX_FILE in a linked worktree); a
# test that runs git in a temp folder would inherit them and read this repo.
# From the root, git finds the repo without them.
# shellcheck disable=SC2046 # one variable name per word, on purpose
unset $(git rev-parse --local-env-vars)

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
RESET='\033[0m'

step() { echo -e "\n${CYAN}▶ $1${RESET}"; }
# printable <text>: what a tool printed, as a message quotes it: its first 80 bytes (in C, so a cut never splits a character into a
# lone byte the terminal reads), every byte but printable ASCII as ?, so it can't move the terminal or hide in a name.
printable() {
  local LC_ALL=C shown
  shown="${1:0:80}"
  printf '%s' "${shown//[!\ -~]/?}"
}

# Files added, copied, modified or renamed since this branch left origin/main.
# Without an origin/main (a fresh clone of a fork, say) lint every tracked file; the Shell format step skips then (below).
# NUL-separated, so a name git would quote (non-ASCII, a tab, a newline, a quote) reaches the linters as it is on disk. Written to a
# file first, so a git that fails stops the gate here (set -e) instead of leaving every list empty.
CHANGED_FILE="$(mktemp)"
trap 'rm -f "$CHANGED_FILE"' EXIT
if BASE="$(git merge-base HEAD origin/main 2> /dev/null)"; then
  git diff --name-only -z --diff-filter=ACMR "$BASE" HEAD > "$CHANGED_FILE"
else
  BASE=""
  git ls-files -z > "$CHANGED_FILE"
fi
mapfile -d '' -t CHANGED < "$CHANGED_FILE"
# pick <regex> [<regex>]: the changed files matching the extended regex $1, and not $2 when it's given, NUL-separated. Matched in bash,
# not grep: grep in a UTF-8 locale drops a name that isn't valid UTF-8, and its ^ and $ also match at a newline inside a name. With
# LC_ALL=C a name is matched byte for byte, and ^ and $ anchor at its two ends only. Nothing here can fail.
pick() {
  local LC_ALL=C name
  for name in ${CHANGED[@]+"${CHANGED[@]}"}; do
    [[ "$name" =~ $1 ]] || continue
    [[ $# -lt 2 || ! "$name" =~ $2 ]] || continue
    printf '%s\0' "$name"
  done
}

mapfile -d '' -t MD_FILES < <(pick '\.md$' '^docs/blogs/')
# pnpm writes its lockfile in its own YAML style; it isn't hand-edited.
mapfile -d '' -t YAML_FILES < <(pick '\.ya?ml$' '(^|/)pnpm-lock\.yaml$')
mapfile -d '' -t WORKFLOW_FILES < <(pick '^\.github/(workflows/[^/]+\.ya?ml|dependabot\.ya?ml|actionlint\.ya?ml|zizmor\.ya?ml)$')
# Shell scripts, plus extensionless scripts and git hooks.
mapfile -d '' -t SHELL_FILES < <(pick '\.sh$|^scripts/[^/.]+$|^\.github/hooks/((pre|post)-[a-z-]+|(prepare-)?commit-msg)$')
# The shell files shfmt formats: those .editorconfig gives the shell style to (its [*.sh] and [.github/hooks/*] sections). An
# extensionless scripts/ file is in no section, so shfmt would format it in its own defaults: shellcheck alone checks it.
mapfile -d '' -t SHFMT_FILES < <(pick '\.sh$|^\.github/hooks/((pre|post)-[a-z-]+|(prepare-)?commit-msg)$')
mapfile -d '' -t PY_FILES < <(pick '\.py$')

# An installed markdownlint-cli2 (as the pre-commit hook uses) when there is
# one; otherwise this pinned version, never whatever the registry serves at push time.
MARKDOWNLINT_CLI2_VERSION="0.24.0"
# The same for the workflow and shell linters. The fallbacks run through uvx or
# docker, so an installed tool only needs to be on PATH to be preferred. Keep
# these in step with .github/workflows/lint-actions.yml.
ACTIONLINT_VERSION="1.7.12"
ZIZMOR_VERSION="1.30.1"
SHELLCHECK_VERSION="v0.11.0"
# ruff: an installed one is used only when it is a 0.17 release (a later one adds rules to F and B, so the same file could pass here and
# fail there); without one, or with another release or a failing --version, this release through uvx --no-config. The edit-time lint (scripts/lint-edited-file.sh) has the same release rule and flags, and
# requirements-tests.txt the same pin, for CI's python-tests job: test.sh keeps them equal.
RUFF_VERSION="0.17.6"
RUFF_RELEASE='^ruff 0\.17\.[0-9]+$'
RUFF_ARGS=(check --isolated --no-cache --select "F,B" --target-version py312)

# Fallback probes: can the fallback actually run here? A docker binary with a
# stopped daemon can't, and must skip rather than fail the gate.
have_docker() { docker info &> /dev/null; }
have_uvx() { command -v uvx &> /dev/null; }

# Runs an installed tool, else its fallback command, else warns that the check
# is skipped locally (CI's Lint Actions workflow still runs it).
# Usage: run_tool <name> <install hint> <probe function> <fallback command...> -- <args...>
run_tool() {
  local name="$1" hint="$2" probe="$3"
  shift 3
  local fallback=()
  while [[ $# -gt 0 && "$1" != "--" ]]; do
    fallback+=("$1")
    shift
  done
  if [[ $# -eq 0 ]]; then
    echo "run_tool ${name}: missing -- before the tool arguments" >&2
    exit 2
  fi
  shift
  if command -v "$name" &> /dev/null; then
    "$name" "$@"
  elif "$probe"; then
    "${fallback[@]}" "$@"
  else
    echo -e "${YELLOW}⚠️  ${name} not found — skipping. CI's Lint Actions workflow still runs it.${RESET}"
    echo -e "   To enable: ${CYAN}${hint}${RESET}"
  fi
}

step "Markdown lint (${#MD_FILES[@]} changed file(s))"
if [[ ${#MD_FILES[@]} -gt 0 ]]; then
  # markdownlint-cli2 reads each argument as a glob, so a[1].md would match a1.md: a leading ':' makes it a literal path (and keeps
  # a name that starts with '-' from being read as an option).
  MD_PATHS=("${MD_FILES[@]/#/:}")
  if [[ -x "$ROOT/node_modules/.bin/markdownlint-cli2" ]]; then
    "$ROOT/node_modules/.bin/markdownlint-cli2" "${MD_PATHS[@]}"
  elif command -v markdownlint-cli2 &> /dev/null; then
    markdownlint-cli2 "${MD_PATHS[@]}"
  elif command -v pnpm &> /dev/null; then
    pnpm dlx "markdownlint-cli2@${MARKDOWNLINT_CLI2_VERSION}" "${MD_PATHS[@]}"
  else
    # Every repo uses pnpm, never npm or npx, so without pnpm there is no way to lint.
    echo "Markdown lint needs pnpm: corepack enable pnpm, or pnpm add -g markdownlint-cli2." >&2
    exit 1
  fi
fi

step "YAML lint (${#YAML_FILES[@]} changed file(s))"
if [[ ${#YAML_FILES[@]} -gt 0 ]]; then
  if command -v yamllint &> /dev/null; then
    YAMLLINT_ARGS=()
    [[ -f .yamllint.yml ]] && YAMLLINT_ARGS=(-c .yamllint.yml)
    # '--' keeps a name that starts with '-' from being read as an option.
    yamllint ${YAMLLINT_ARGS[@]+"${YAMLLINT_ARGS[@]}"} -- "${YAML_FILES[@]}"
  else
    echo -e "${YELLOW}⚠️  yamllint not found — skipping. CI's Lint YAML workflow still checks these files.${RESET}"
    echo -e "   To enable: ${CYAN}pipx install yamllint${RESET}"
  fi
fi

# actionlint checks workflow syntax, expressions and contexts, and lints
# every run: block with shellcheck. zizmor audits the workflows for security
# problems: script injection, over-broad permissions, unpinned actions.
step "Workflow lint (${#WORKFLOW_FILES[@]} changed file(s))"
if [[ ${#WORKFLOW_FILES[@]} -gt 0 ]]; then
  # No file arguments: actionlint finds every workflow, and zizmor audits the
  # whole repo (workflows and dependabot.yml), as CI does.
  run_tool actionlint "install actionlint (github.com/rhysd/actionlint), or Docker" have_docker \
    docker run --rm -v "$ROOT:/repo" -w /repo "rhysd/actionlint:${ACTIONLINT_VERSION}" \
    --
  # --offline: no GitHub token needed locally. CI also runs the online audits.
  run_tool zizmor "pipx install zizmor, or install uv" have_uvx \
    uvx "zizmor@${ZIZMOR_VERSION}" \
    -- --offline --min-severity medium .
fi

step "Shell lint (${#SHELL_FILES[@]} changed file(s))"
if [[ ${#SHELL_FILES[@]} -gt 0 ]]; then
  run_tool shellcheck "install shellcheck, or Docker" have_docker \
    docker run --rm -v "$ROOT:/mnt" -w /mnt "koalaman/shellcheck:${SHELLCHECK_VERSION}" \
    -- -- "${SHELL_FILES[@]}"
fi

# Each changed shell file is formatted as shfmt formats it, in the style .editorconfig gives, so a repo's own scripts take the
# style as they are touched, with no one-off reformat. shfmt gets no style flags, so it reads that file. It must be v3.14 or a later
# v3 release, the one check_gh_repo.py below needs too: another release can format the same file differently.
# Without an origin/main the changed files are every tracked file, and a repo's own scripts would all have to be reformatted at once:
# the step is skipped, as there is no branch to tell its changes by, and its heading gives no count.
SHFMT_COUNT=""
[[ -z "$BASE" ]] || SHFMT_COUNT=" (${#SHFMT_FILES[@]} changed file(s))"
step "Shell format${SHFMT_COUNT}"
if [[ -z "$BASE" ]]; then
  echo -e "${YELLOW}⚠️  No origin/main, so no changed files to tell apart: skipping the shell format check (git fetch origin main to run it).${RESET}"
elif [[ ${#SHFMT_FILES[@]} -gt 0 ]]; then
  if ! command -v shfmt &> /dev/null; then
    echo "❌ Shell format needs shfmt v3.14 or a later v3 release (check_gh_repo.py needs it too): install it on PATH." >&2
    exit 1
  fi
  SHFMT_VERSION_RC=0
  SHFMT_FOUND="$(shfmt --version 2>&1)" || SHFMT_VERSION_RC=$?
  SHFMT_SHOWN="$(printable "$SHFMT_FOUND")"
  # A --version that fails or prints nothing says nothing of the release: it gets its own message, with its exit status and output.
  if ((SHFMT_VERSION_RC != 0)) || [[ -z "$SHFMT_FOUND" ]]; then
    echo "❌ Shell format needs shfmt v3.14 or a later v3 release, and shfmt --version exited $SHFMT_VERSION_RC and printed '$SHFMT_SHOWN', so its release is unknown: check the shfmt install." >&2
    exit 1
  fi
  if ! [[ "$SHFMT_FOUND" =~ ^v3\.(1[4-9]|[2-9][0-9]|[1-9][0-9]{2,})\.[0-9]+$ ]]; then
    echo "❌ Shell format needs shfmt v3.14 or a later v3 release, not '$SHFMT_SHOWN'." >&2
    exit 1
  fi
  # shfmt exits 1 both for a file it would change (it prints the diff) and for one it can't parse (it prints the error); any other status
  # is shfmt itself failing (a crash, a usage error), with no diff to follow.
  SHFMT_RC=0
  shfmt -d -- "${SHFMT_FILES[@]}" || SHFMT_RC=$?
  if ((SHFMT_RC == 1)); then
    echo "❌ shfmt: run shfmt -w on each file whose diff is shown above (with no style flags, so it reads .editorconfig), and fix the syntax of any file it could not parse." >&2
    exit 1
  elif ((SHFMT_RC != 0)); then
    echo "❌ shfmt failed (exit $SHFMT_RC) without checking the format: this is no format problem in the files. Check the shfmt install." >&2
    exit 1
  fi
fi

# Each changed Python file passes ruff's F (pyflakes) and B (bugbear) rules, the ones that find bugs, not style. --isolated: ruff
# reads no pyproject.toml or ruff.toml, so every repo checks the same rules and the edit-time lint, which reads no config an agent
# could write, agrees with the gate. Like the shell lint it lints every tracked Python file without an origin/main.
step "Python lint (${#PY_FILES[@]} changed file(s))"
if [[ ${#PY_FILES[@]} -gt 0 ]]; then
  # An installed ruff 0.17 is used; another release, or one whose release is unknown, is passed over for the pinned one through uvx, so
  # a newer ruff from the system's packages doesn't stop the push. uvx runs with --no-config: it reads no uv config at all, so no uv.toml or
  # pyproject.toml in the checkout can send the fetch to another index (nor can the user's own uv.toml name a mirror for it).
  RUFF=() RUFF_SKIPPED=""
  if command -v ruff &> /dev/null; then
    RUFF_VERSION_RC=0
    RUFF_FOUND="$(ruff --version 2>&1)" || RUFF_VERSION_RC=$?
    RUFF_SHOWN="$(printable "$RUFF_FOUND")"
    if ((RUFF_VERSION_RC != 0)) || [[ -z "$RUFF_FOUND" ]]; then
      RUFF_SKIPPED="ruff --version exited $RUFF_VERSION_RC and printed '$RUFF_SHOWN', so its release is unknown: check the ruff install"
    elif ! [[ "$RUFF_FOUND" =~ $RUFF_RELEASE ]]; then
      RUFF_SKIPPED="the installed ruff is '$RUFF_SHOWN', not a 0.17 release: put ruff 0.17 first on PATH (uv tool install ruff@${RUFF_VERSION})"
    else
      RUFF=(ruff)
    fi
  fi
  if ((${#RUFF[@]} == 0)); then
    if have_uvx; then
      [[ -z "$RUFF_SKIPPED" ]] || echo "  ${RUFF_SKIPPED}; running uvx ruff@${RUFF_VERSION} instead."
      RUFF=(uvx --no-config "ruff@${RUFF_VERSION}")
    elif [[ -n "$RUFF_SKIPPED" ]]; then
      echo "❌ Python lint needs ruff 0.17: ${RUFF_SKIPPED}, or install uv." >&2
      exit 1
    else
      echo "❌ Python lint needs ruff 0.17: uv tool install ruff@${RUFF_VERSION}, or install uv." >&2
      exit 1
    fi
  fi
  # ruff exits 1 for a problem it found (it lists them) and 2 for its own failure (a usage error, a file it can't read).
  RUFF_RC=0
  "${RUFF[@]}" "${RUFF_ARGS[@]}" -- "${PY_FILES[@]}" || RUFF_RC=$?
  if ((RUFF_RC == 1)); then
    # The command to paste, with --fix: the gate's own ruff and flags (fixed words, printed as they are), so no pyproject.toml or
    # ruff.toml changes what it fixes, and the files shell-quoted; more than five are left as <file>... for the person to fill in.
    if ((${#PY_FILES[@]} <= 5)); then RUFF_FIX_FILES="$(printf ' %q' "${PY_FILES[@]}")"; else RUFF_FIX_FILES=" <file>..."; fi
    echo "❌ ruff: fix each problem shown above, or, for a false alarm, add # noqa: <code> to the line with the reason. ruff fixes the ones marked [*] itself with:" >&2
    printf '  %s --fix --%s\n' "${RUFF[*]} ${RUFF_ARGS[*]}" "$RUFF_FIX_FILES" >&2
    exit 1
  elif ((RUFF_RC != 0)) && [[ "${RUFF[0]}" == uvx ]]; then
    echo "❌ uvx ruff@${RUFF_VERSION} failed (exit $RUFF_RC) without checking the files: if uvx could not fetch it (offline, or an index only a uv config names, which --no-config skips), install ruff 0.17 first on PATH (uv tool install ruff@${RUFF_VERSION}); otherwise check the file it names." >&2
    exit 1
  elif ((RUFF_RC != 0)); then
    echo "❌ ruff failed (exit $RUFF_RC) without checking the files: check the ruff install, or the file it names." >&2
    exit 1
  fi
fi

# A gh command not told its repository reaches gh's default one, a fork in a
# fork's clone. The check reads this repo's scripts/, .github/ci/ and skills,
# not the Owned Baseline files, and needs no base (mpaulosky/dotfiles#321).
# It parses each script with shfmt v3.14 or a later v3, since it reads shfmt's
# syntax tree, and fails, naming it, when shfmt is missing or another release.
step "GitHub commands told their repository (.github/scripts/check_gh_repo.py)"
python3 -I .github/scripts/check_gh_repo.py --repo "$ROOT"

# The local review (/pre-push-review) must have passed this branch's diff, with
# no open Critical or Warning finding: scripts/review.sh keeps the record under
# .git/review/. Sandcastle's sandboxes set GATE_SKIP_REVIEW=1 (lib/skills.mts),
# since the host runs the review's rounds on a built branch itself (lib/host-review.mts). A repo without a .sandcastle/ folder
# has no ledger code to run, so it has no review to check.
step "Local review (scripts/review.sh check)"
if [[ "${GATE_SKIP_REVIEW:-}" == "1" ]]; then
  echo -e "${GREEN}✅ Local review check skipped (GATE_SKIP_REVIEW=1).${RESET}"
elif [[ ! -f .sandcastle/lib/review-ledger.mts ]]; then
  echo -e "${YELLOW}⚠️  No .sandcastle/lib/review-ledger.mts — this repo has no local review to check.${RESET}"
elif [[ -z "$BASE" ]]; then
  # A remote with no main yet (a repo's first push) has nothing to fetch and no review to check
  # (ls-remote exits 2 when nothing matches); a remote that has one, or that can't be reached,
  # fails: the first needs the fetch, and the second can't be told from a main we can't see.
  remote_main=0
  git ls-remote --exit-code origin refs/heads/main > /dev/null 2>&1 || remote_main=$?
  if [[ $remote_main -ne 2 ]]; then
    echo "❌ No origin/main to diff against, so the review can't be checked: run git fetch origin main (or the remote could not be reached)." >&2
    exit 1
  fi
  echo -e "${YELLOW}⚠️  The remote has no main yet, so there is no diff to review: skipping the local review.${RESET}"
else
  scripts/review.sh check "$BASE"
fi

# The Copilot review instructions are generated from the repo's code-review
# skill (scripts/sync-copilot-review.sh), so they can't drift from what Claude's
# reviewers use. A repo without the skill has nothing to check and passes.
if [[ -f scripts/sync-copilot-review.sh ]]; then
  step "Copilot review instructions (scripts/sync-copilot-review.sh --check)"
  bash scripts/sync-copilot-review.sh --check
fi

# Checks only this repo needs (Seed, so Apply never overwrites them). It gets
# the merge base, empty without an origin/main, to find its own changed files.
if [[ -f .github/ci/gate-checks.sh ]]; then
  step "Repo checks (.github/ci/gate-checks.sh)"
  bash .github/ci/gate-checks.sh "$BASE"
fi

# A central NuGet pin no project references holds the package back wherever it comes in through another one, under transitive
# pinning, and Dependabot's bumps don't raise it, so restore fails with NU1109. Checked before the build, as CI checks it before restore.
step "Central package pins (.github/scripts/check_central_pins.py)"
python3 -I .github/scripts/check_central_pins.py --root "$ROOT"

step "Build"
mapfile -t SOLUTIONS < <(find . -maxdepth 1 -name '*.slnx')
dotnet build "${SOLUTIONS[@]}" --configuration Release -warnaserror

step "Tests"
# CI's discovery (ci.yml's discover-tests job): a project is a test project when
# IsTestProject resolves to true for it, at any depth under tests/. Helper
# libraries such as TestingSupport.Library don't set it, and dotnet test on
# Microsoft Testing Platform fails on them ("No test projects were found").
TEST_LIST="$(python3 .github/scripts/discover_tests.py --list)"
mapfile -t TEST_PROJECTS < <(grep . <<< "$TEST_LIST" || true)
if [[ ${#TEST_PROJECTS[@]} -eq 0 ]]; then
  echo -e "${YELLOW}No test projects under tests/ — skipping.${RESET}"
fi
for project in "${TEST_PROJECTS[@]}"; do
  dotnet test "$project" --configuration Release
done

echo -e "\n${GREEN}✅ Gate passed.${RESET}"
