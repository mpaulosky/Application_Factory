#!/usr/bin/env bash
# The Application Factory's pre-push gate, run by .github/hooks/pre-push (a copy of the Template's) and safe to run by hand.
# The Factory isn't a .NET repo, so its gate is what its CI runs: first this script's own test and the review's three stand-alone tests (they install
# the packages with pnpm), then that its copies of Template files haven't drifted from the Template (ADR 0006), then that no Owned file has a bare #N,
# then that no Owned script reads git status --porcelain without --untracked-files (tests/check-git-status.sh), then the Markdown and YAML lint, then shellcheck of the shell files the branch changes, then that the branch passed the local review (scripts/review.sh check, ADR 0010; GATE_SKIP_REVIEW=1 skips it),
# then the repo-ci-baseline skill's test.sh (skipped when it already passed on the same clean tree; GATE_NO_CACHE=1 runs it). This script is the Factory's own, not a copy of the Template's scripts/gate.sh.

# GATE_PART=self|static|template|sandcastle runs one part of this gate, which is how CI runs a part per job (ADR 0012): self is this script's own test and the review's tests,
# static the copies, the issue references, the git status guards, the lint and shellcheck, template the review check and test.sh, sandcastle the Template's Sandcastle check.
# With no GATE_PART every part runs, in that order, except that sandcastle runs only when the branch changed a file it reads (a part run by name always runs). The pre-push hook drops GATE_PART.
set -euo pipefail

# scripts/gate.sh --quick: the checks that finish in about a minute (its test, the review tests, the copies' drift, the bare-#N check, the git status guard check, lint, shellcheck of the shell files the branch changes), then stop before the local
# review check and test.sh. It is for iterating, never for pushing: the pre-push hook and CI call this script with no argument.
# scripts/gate.sh --sync: copies every Template file the Factory carries over its copy (cp -p keeps the execute bit), prints the ones that changed,
# and stops. A copy edited by hand is overwritten, so edit the Template's file. Run it after editing the Template's file; the drift check below is what finds a copy you forgot.
quick=0
sync=0
case "${1:-}" in
  "") ;;
  --quick) quick=1 ;;
  --sync) sync=1 ;;
  *) echo "Usage: scripts/gate.sh [--quick|--sync]" >&2; exit 2 ;;
esac
if (($# > 1)); then echo "Usage: scripts/gate.sh [--quick|--sync]" >&2; exit 2; fi

part="${GATE_PART:-}"
case "$part" in
  "" | self | static | template | sandcastle) ;;
  *) echo "GATE_PART must be self, static, template or sandcastle, not '$part'." >&2; exit 2 ;;
esac
if ((quick)) && [[ -n "$part" ]]; then echo "scripts/gate.sh --quick runs no single part: unset GATE_PART." >&2; exit 2; fi

ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"
# Under a hook git sets GIT_DIR (and GIT_INDEX_FILE in a linked worktree), as the Template's gate.sh says. test.sh's tests
# build scratch repos, and with these set their git commands write into this repo instead.
# shellcheck disable=SC2046 # one variable name per word, on purpose
unset $(git rev-parse --local-env-vars)

CYAN='\033[0;36m'; RED='\033[0;31m'; RESET='\033[0m'
skill=".claude/skills/repo-ci-baseline"
owned="$skill/template/owned"

# The Factory carries these Owned files as copies, for its git hooks, review layer and issue and PR process. Edit the Template's file, then run scripts/gate.sh --sync to copy it over.
# Keep this list in step with ADR 0006, ADR 0008, ADR 0009, ADR 0010, ADR 0011 and CLAUDE.md.
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
  scripts/lint-edited-file.sh
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

if ((sync)); then
  changed=0
  for file in "${copies[@]}"; do
    if ! cmp -s "$owned/$file" "$file" || [[ "$([[ -x "$file" ]] && echo x)" != "$([[ -x "$owned/$file" ]] && echo x)" ]]; then
      mkdir -p "$(dirname "$file")"
      command cp -pf "$owned/$file" "$file"
      echo "synced $file"
      changed=$((changed + 1))
    fi
  done
  echo "$changed copies synced from the Template."
  exit 0
fi

run_self() {
  # The test runs this script against throwaway repos, with GATE_SKIP_SELF_TEST=1 so those runs don't test themselves again.
  if [[ "${GATE_SKIP_SELF_TEST:-}" != "1" ]]; then
    echo -e "\n${CYAN}▶ scripts/tests/gate.test.sh${RESET}"
    "$ROOT/scripts/tests/gate.test.sh"

    # The three copied tests that stand alone (ADR 0010); the Template's Sandcastle check runs the rest.
    echo -e "\n${CYAN}▶ Local review tests${RESET}"
    if [[ ! -x node_modules/.bin/tsx ]]; then
      if ! command -v pnpm >/dev/null 2>&1; then
        echo "❌ node_modules/.bin/tsx not found, and pnpm isn't installed: run corepack enable pnpm, then pnpm install." >&2
        exit 1
      fi
      pnpm install --frozen-lockfile >&2
      if [[ ! -x node_modules/.bin/tsx ]]; then echo "❌ pnpm install did not install node_modules/.bin/tsx." >&2; exit 1; fi
    fi
    node_modules/.bin/tsx --test .sandcastle/lib/review-ledger.test.mts .sandcastle/lib/security-record.test.mts .sandcastle/lib/plain-text.test.mts
  fi

}

run_static() {
  echo -e "\n${CYAN}▶ Copies of Template files${RESET}"
  drifted=()
  for file in "${copies[@]}"; do
    # Content and execute bit, as Apply compares them.
    if ! cmp -s "$file" "$owned/$file" || [[ "$([[ -x "$file" ]] && echo x)" != "$([[ -x "$owned/$file" ]] && echo x)" ]]; then
      drifted+=("$file")
    fi
  done
  if ((${#drifted[@]})); then
    echo -e "${RED}❌ These differ from the Template's file (edit $owned/<file>, then run scripts/gate.sh --sync):${RESET}" >&2
    printf '   %s\n' "${drifted[@]}" >&2
    exit 1
  fi
  echo "${#copies[@]} copies match the Template."

  # test.sh's issue-reference rule (a bare #N in an Owned file), run here too so --quick catches it and not only the full gate's last step.
  echo -e "\n${CYAN}▶ Issue references in Owned files${RESET}"
  # Exit 1 is a bare #N (the checker printed it); anything else (2: a folder, file or directory it could not check) is the check itself failing, and fails the gate too. An uncaught Python error also exits 1, with its traceback above.
  if ! command -v python3 >/dev/null 2>&1; then
    echo -e "${RED}❌ python3 not found: the issue-reference check needs it.${RESET}" >&2
    exit 1
  fi
  refs_rc=0
  python3 -I "$skill/tests/check-issue-refs.py" "$owned" || refs_rc=$?
  if ((refs_rc == 1)); then
    echo -e "${RED}❌ Write issue references in Owned files as mpaulosky/dotfiles#N (or <Repo>#N), not a bare #N.${RESET}" >&2
    exit 1
  elif ((refs_rc != 0)); then
    echo -e "${RED}❌ check-issue-refs.py failed (exit $refs_rc) without checking: see its output above.${RESET}" >&2
    exit 1
  fi

  # test.sh's git status rule (a guard reading git status --porcelain names --untracked-files), run here too so --quick catches it. Same exit codes as the issue-reference check.
  echo -e "\n${CYAN}▶ git status guards in Owned scripts${RESET}"
  status_rc=0
  bash "$skill/tests/check-git-status.sh" "$owned" || status_rc=$?
  if ((status_rc == 1)); then
    echo -e "${RED}❌ Give each git status --porcelain in an Owned script --untracked-files=all.${RESET}" >&2
    exit 1
  elif ((status_rc != 0)); then
    echo -e "${RED}❌ check-git-status.sh failed (exit $status_rc) without checking: see its output above.${RESET}" >&2
    exit 1
  fi

  # What the Lint Markdown and Lint YAML workflows run over the whole repo, so a failure shows here and not first on GitHub (ADR 0009).
  # The pre-commit hook lints staged Markdown only; test.sh lints the Template's files only.
  echo -e "\n${CYAN}▶ markdownlint (whole repo, as Lint Markdown)${RESET}"
  markdownlint-cli2 '**/*.md' || { echo -e "${RED}❌ markdownlint${RESET}" >&2; exit 1; }
  echo -e "\n${CYAN}▶ yamllint (whole repo, as Lint YAML)${RESET}"
  yamllint -c .yamllint.yml . || { echo -e "${RED}❌ yamllint${RESET}" >&2; exit 1; }
  # Lint with ShellCheck the shell files this branch changes (added, copied, modified, renamed or type-changed, committed or not, against its merge base with origin/main; every tracked and untracked
  # shell file when there is no origin/main or no merge base), so --quick finds what test.sh's shellcheck step would fail on at the end of the full gate. A shell file is a *.sh or one of the three git hooks.
  echo -e "\n${CYAN}▶ shellcheck (changed shell files)${RESET}"
  if ! command -v shellcheck >/dev/null 2>&1; then
    echo -e "${RED}❌ shellcheck not found: the gate needs it (test.sh runs it too).${RESET}" >&2
    exit 1
  fi
  # NUL-separated names (-z): git quotes a name with a non-ASCII byte, a tab or a quote otherwise, and the file would never be found.
  # The list is written to a file first: a git failure inside a process substitution would not stop the gate, and the step would pass having checked nothing.
  changed_list="$(mktemp)"
  trap 'command rm -f -- "$changed_list"' EXIT
  if git rev-parse -q --verify refs/remotes/origin/main >/dev/null && merge_base="$(git merge-base origin/main HEAD 2>/dev/null)"; then
    { git diff -z --name-only --diff-filter=ACMRT "$merge_base" && git ls-files -z --others --exclude-standard; } >"$changed_list"
  else
    git ls-files -z --cached --others --exclude-standard >"$changed_list"
  fi || { echo -e "${RED}❌ could not list the changed files (git failed): the shell files were not checked.${RESET}" >&2; exit 1; }
  shell_changed=()
  while IFS= read -r -d '' f; do
    [[ -f "$f" ]] || continue
    case "${f##*/}" in *.sh | pre-commit | pre-push | post-checkout) shell_changed+=("$f") ;; esac
  done <"$changed_list"
  if ((${#shell_changed[@]})); then
    shellcheck "${shell_changed[@]}" || { echo -e "${RED}❌ shellcheck${RESET}" >&2; exit 1; }
  fi
  echo "${#shell_changed[@]} changed shell file(s) checked."

}

# run_cached <name> <label> <command...>: runs the command (minutes long), unless it already passed on this tree. A pass is remembered for the tree HEAD points at, when nothing is
# uncommitted or untracked (ignored files don't count), in the common git directory so every worktree of the clone shares it: the same tree is not checked twice. Only a pass is stored, and
# only when the tree is still that clean tree after the command ran. GATE_NO_CACHE=1 always runs it. Any run that goes on to run the command clears the old pass first (and fails if it can't),
# so a failure leaves nothing remembered for the next run. A git that fails here (a broken index, a lock) leaves the tree unproven clean, so the command runs and nothing is stored.
run_cached() {
  local name="$1" label="$2" cache="" dirty tree common after
  shift 2
  if dirty="$(git status --porcelain --untracked-files=all)" && [[ -z "$dirty" ]] \
    && tree="$(git rev-parse -q --verify 'HEAD^{tree}' 2>/dev/null)" && common="$(git rev-parse --git-common-dir)"; then
    cache="$common/gate-cache/$name-$tree"
  fi
  if [[ -n "$cache" && -f "$cache" && "${GATE_NO_CACHE:-}" != "1" ]]; then
    echo "$label already passed on this tree (${cache##*-}): skipped. GATE_NO_CACHE=1 runs it again."
    return 0
  fi
  if [[ -n "$cache" ]]; then
    command rm -f -- "$cache" 2>/dev/null || true
    if [[ -e "$cache" ]]; then
      echo -e "${RED}❌ could not clear the remembered $label pass ($cache): a failure now would leave it for the next run. Remove it by hand.${RESET}" >&2
      exit 1
    fi
  fi
  "$@"
  # Remembering the pass is best effort: a folder that can't be written to must not turn a passing command into a failing gate.
  if [[ -n "$cache" ]]; then
    if after="$(git status --porcelain --untracked-files=all)" && [[ -z "$after" && "$(git rev-parse -q --verify 'HEAD^{tree}' 2>/dev/null)" == "$tree" ]]; then
      { mkdir -p "${cache%/*}" && : >"$cache"; } 2>/dev/null || echo "could not record the $label pass in ${cache%/*}: it will run again next time." >&2
    else
      echo "the tree changed while $label ran: this pass is not remembered." >&2
    fi
  fi
}

# sandcastle_changed: 0 when the branch changed a file Sandcastle's check reads (the Template's .sandcastle/ code, the Seed's package files, the check and its tests), or when
# that can't be told (no origin/main or merge base, a git failure): then the check runs, as the shellcheck step does for every shell file.
sandcastle_changed() {
  local base files others
  git rev-parse -q --verify refs/remotes/origin/main >/dev/null && base="$(git merge-base origin/main HEAD 2>/dev/null)" || return 0
  files="$(git diff --name-only "$base")" || return 0
  others="$(git ls-files --others --exclude-standard)" || return 0
  grep -Eq "^\.claude/skills/repo-ci-baseline/(template/[^/]+/\.sandcastle/|template/seed/(package\.json|pnpm)|tests/sandcastle)" <<<"$files"$'\n'"$others"
}

run_sandcastle() {
  echo -e "\n${CYAN}▶ Sandcastle check${RESET}"
  run_cached sandcastle "Sandcastle check" "$ROOT/$skill/tests/sandcastle-check.test.sh"
}

run_template() {
  # This branch's diff must have passed the local review (/pre-push-review), as an Application's gate checks it (ADR 0010).
  # GATE_SKIP_REVIEW=1 is for gate runs started by hand and for CI, which has no .git/review/ ledger; the pre-push hook removes it.
  echo -e "\n${CYAN}▶ Local review (scripts/review.sh check)${RESET}"
  if [[ "${GATE_SKIP_REVIEW:-}" == "1" ]]; then
    echo "Local review check skipped (GATE_SKIP_REVIEW=1)."
  elif ! git rev-parse --verify --quiet refs/remotes/origin/main >/dev/null; then
    echo "❌ No origin/main to diff against, so the review can't be checked: run git fetch origin main." >&2
    exit 1
  elif ! BASE="$(git merge-base HEAD refs/remotes/origin/main 2>/dev/null)"; then
    echo "❌ HEAD and origin/main share no history to diff from (a shallow clone, or unrelated histories): run git fetch --unshallow origin main, or rebase the branch onto origin/main." >&2
    exit 1
  else
    scripts/review.sh check "$BASE"
  fi


  echo -e "\n${CYAN}▶ test.sh${RESET}"
  run_cached test-sh "test.sh" "$ROOT/$skill/test.sh"
}

case "$part" in
  self) run_self ;;
  static) run_static ;;
  template) run_template ;;
  sandcastle) run_sandcastle ;;
  *)
    run_self
    run_static
    if ((quick)); then
      echo -e "\n${CYAN}quick gate passed${RESET}: the local review check, test.sh and the Sandcastle check did not run. Run scripts/gate.sh, with no argument, before pushing."
      exit 0
    fi
    run_template
    if sandcastle_changed; then
      run_sandcastle
    else
      echo -e "\n${CYAN}▶ Sandcastle check${RESET}\nskipped: the branch changed nothing Sandcastle's check reads (GATE_PART=sandcastle runs it)."
    fi
    ;;
esac
