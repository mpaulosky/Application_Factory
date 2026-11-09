#!/usr/bin/env bash
# The Application Factory's pre-push gate, run by .github/hooks/pre-push (a copy of the Template's) and safe to run by hand.
# The Factory isn't a .NET repo, so its gate is what its CI runs: first this script's own test, then that its copies of Template files haven't drifted
# from the Template (ADR 0006), then the repo-ci-baseline skill's test.sh. This script is the Factory's own, not a copy of
# the Template's scripts/gate.sh.
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"
# Under a hook git sets GIT_DIR (and GIT_INDEX_FILE in a linked worktree), as the Template's gate.sh says. test.sh's tests
# build scratch repos, and with these set their git commands write into this repo instead.
# shellcheck disable=SC2046 # one variable name per word, on purpose
unset $(git rev-parse --local-env-vars)

CYAN='\033[0;36m'; RED='\033[0;31m'; RESET='\033[0m'
skill=".claude/skills/repo-ci-baseline"
owned="$skill/template/owned"

# The Factory carries these Owned files as copies, for its git hooks, review layer and issue and PR process. Edit the Template's file and copy it over.
# Keep this list in step with ADR 0006, ADR 0008, ADR 0009, ADR 0010 and CLAUDE.md.
copies=(
  .github/hooks/pre-commit
  .github/hooks/pre-push
  .github/hooks/post-checkout
  scripts/check-branch-name.sh
  .github/workflows/claude-review.yml
  .github/workflows/pr-automerge.yml
  .github/workflows/pr-review-submitted.yml
  .github/scripts/post_claude_review.py
  .github/ISSUE_TEMPLATE/bug_report.md
  .github/ISSUE_TEMPLATE/feature_request.md
  .github/ISSUE_TEMPLATE/config.yml
  .github/pull_request_template.md
  .github/workflows/pr-title.yml
  scripts/check-pr-title.sh
  .github/workflows/lint-markdown.yml
  .github/workflows/lint-yaml.yml
  .github/workflows/lint-actions.yml
  scripts/review.sh
  .sandcastle/lib/review-ledger.mts
  .sandcastle/lib/security-record.mts
  .sandcastle/lib/errors.mts
  .sandcastle/lib/host-limit.mts
  .sandcastle/lib/plain-text.mts
  .sandcastle/lib/review-ledger.test.mts
  .sandcastle/lib/security-record.test.mts
  .sandcastle/lib/plain-text.test.mts
  .claude/skills/pre-push-review/SKILL.md
  .claude/skills/pre-push-review/lenses.md
  .claude/skills/pre-push-review/review-bar.md
  .claude/agents/pre-push-reviewer.md
  .security/README.md
)

# The test runs this script against throwaway repos, with GATE_SKIP_SELF_TEST=1 so those runs don't test themselves again.
if [[ "${GATE_SKIP_SELF_TEST:-}" != "1" ]]; then
  echo -e "\n${CYAN}▶ scripts/tests/gate.test.sh${RESET}"
  "$ROOT/scripts/tests/gate.test.sh"

# The three copied tests that stand alone (ADR 0010); the Template's Sandcastle check runs the rest.
  echo -e "\n${CYAN}▶ Local review tests${RESET}"
  if [[ ! -x node_modules/.bin/tsx ]]; then pnpm install --frozen-lockfile >&2; fi
  node_modules/.bin/tsx --test .sandcastle/lib/review-ledger.test.mts .sandcastle/lib/security-record.test.mts .sandcastle/lib/plain-text.test.mts
fi

echo -e "\n${CYAN}▶ Copies of Template files${RESET}"
drifted=()
for file in "${copies[@]}"; do
  # Content and execute bit, as Apply compares them.
  if ! cmp -s "$file" "$owned/$file" || [[ "$([[ -x "$file" ]] && echo x)" != "$([[ -x "$owned/$file" ]] && echo x)" ]]; then
    drifted+=("$file")
  fi
done
if ((${#drifted[@]})); then
  echo -e "${RED}❌ These differ from the Template's file (edit $owned/<file> and copy it over):${RESET}" >&2
  printf '   %s\n' "${drifted[@]}" >&2
  exit 1
fi
echo "${#copies[@]} copies match the Template."

# What the Lint Markdown and Lint YAML workflows run over the whole repo, so a failure shows here and not first on GitHub (ADR 0009).
# The pre-commit hook lints staged Markdown only; test.sh lints the Template's files only.
echo -e "\n${CYAN}▶ markdownlint (whole repo, as Lint Markdown)${RESET}"
markdownlint-cli2 '**/*.md' || { echo -e "${RED}❌ markdownlint${RESET}" >&2; exit 1; }
echo -e "\n${CYAN}▶ yamllint (whole repo, as Lint YAML)${RESET}"
yamllint -c .yamllint.yml . || { echo -e "${RED}❌ yamllint${RESET}" >&2; exit 1; }
# This branch's diff must have passed the local review (/pre-push-review), as an Application's gate checks it (ADR 0010).
# GATE_SKIP_REVIEW=1 is for gate runs started by hand and for CI, which has no .git/review/ ledger; the pre-push hook removes it.
echo -e "\n${CYAN}▶ Local review (scripts/review.sh check)${RESET}"
if [[ "${GATE_SKIP_REVIEW:-}" == "1" ]]; then
  echo "Local review check skipped (GATE_SKIP_REVIEW=1)."
elif ! BASE="$(git merge-base HEAD origin/main 2>/dev/null)"; then
  echo "❌ No origin/main to diff against, so the review can't be checked: run git fetch origin main." >&2
  exit 1
else
  scripts/review.sh check "$BASE"
fi

echo -e "\n${CYAN}▶ test.sh${RESET}"
"$ROOT/$skill/test.sh"
