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
# Keep this list in step with ADR 0006, ADR 0008, ADR 0009 and CLAUDE.md.
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
)

# The test runs this script against throwaway repos, with GATE_SKIP_SELF_TEST=1 so those runs don't test themselves again.
if [[ "${GATE_SKIP_SELF_TEST:-}" != "1" ]]; then
  echo -e "\n${CYAN}▶ scripts/tests/gate.test.sh${RESET}"
  "$ROOT/scripts/tests/gate.test.sh"
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

echo -e "\n${CYAN}▶ test.sh${RESET}"
"$ROOT/$skill/test.sh"
