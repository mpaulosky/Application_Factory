#!/usr/bin/env bash
# Tests scripts/gate.sh against throwaway repos with a stub test.sh and a stub review.sh: matching copies run the stub and pass its exit
# status on; a changed, missing or execute-bit-mismatched copy stops the gate before the stub runs; the local review check (ADR 0010)
# skips only on GATE_SKIP_REVIEW=1, fails without an origin/main or when origin/main shares no history with HEAD, and passes review.sh check's status on, ahead of test.sh. It also checks
# that gate.sh names each copied stand-alone test and lists every copy, that repo-config.mts lists the Factory's own files under Owned patterns,
# that --quick stops after the lint and --sync restores drifted, missing and non-executable copies and then stops, that a bare #N in an Owned file fails the gate
# and --quick (and a missing python3 or a broken checker fails closed), that dependabot.yml watches the root package.json, and the review tests' install step (a missing pnpm, a pnpm that leaves no tsx, a pnpm that installs it).
#
#   scripts/tests/gate.test.sh
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX

pass=0; failed=0
check() { # check <description> <condition: 0 for ok>
  if [[ "$2" == 0 ]]; then echo "ok   - $1"; pass=$((pass + 1)); else echo "FAIL - $1"; failed=$((failed + 1)); fi
}

skill=".claude/skills/repo-ci-baseline"
copies=(.github/hooks/pre-commit .github/hooks/pre-push .github/hooks/post-checkout scripts/check-branch-name.sh .github/workflows/claude-review.yml .github/workflows/pr-automerge.yml .github/workflows/pr-review-submitted.yml .github/scripts/post_claude_review.py .github/ISSUE_TEMPLATE/bug_report.md .github/ISSUE_TEMPLATE/feature_request.md .github/ISSUE_TEMPLATE/config.yml .github/pull_request_template.md .github/workflows/pr-title.yml scripts/check-pr-title.sh .github/workflows/lint-markdown.yml .github/workflows/lint-yaml.yml .github/workflows/lint-actions.yml scripts/review.sh .sandcastle/lib/review-ledger.mts .sandcastle/lib/security-record.mts .sandcastle/lib/errors.mts .sandcastle/lib/host-limit.mts .sandcastle/lib/plain-text.mts .sandcastle/lib/review-ledger.test.mts .sandcastle/lib/security-record.test.mts .sandcastle/lib/plain-text.test.mts .claude/skills/pre-push-review/SKILL.md .claude/skills/pre-push-review/lenses.md .claude/skills/pre-push-review/review-bar.md .claude/agents/pre-push-reviewer.md .security/README.md)

# A repo shaped like the Factory: gate.sh, the Template's Owned originals, matching copies, and a stub test.sh that records
# that it ran and exits with $STUB_EXIT.
new_repo() {
  local repo="$work/$1"
  mkdir -p "$repo/scripts" "$repo/$skill"
  git -C "$repo" init -q
  cp -p "$here/scripts/gate.sh" "$repo/scripts/gate.sh"
  local f
  for f in "${copies[@]}"; do
    mkdir -p "$repo/$skill/template/owned/$(dirname "$f")" "$repo/$(dirname "$f")"
    cp -p "$here/$f" "$repo/$skill/template/owned/$f"
    cp -p "$here/$f" "$repo/$f"
  done
  cp -p "$here/.yamllint.yml" "$here/.markdownlint-cli2.jsonc" "$repo/"
  cp -p "$here/$skill/template/owned/.markdownlint-cli2.jsonc" "$repo/$skill/template/owned/"
  mkdir -p "$repo/$skill/tests"
  cp -p "$here/$skill/tests/check-issue-refs.py" "$here/$skill/tests/check-git-status.sh" "$here/$skill/tests/check-git-status.py" "$repo/$skill/tests/"
  cat > "$repo/$skill/test.sh" <<'STUB'
#!/usr/bin/env bash
touch "$(dirname "${BASH_SOURCE[0]}")/ran"
exit "${STUB_EXIT:-0}"
STUB
  chmod +x "$repo/$skill/test.sh"
  echo "$repo"
}

# stub_review <repo>: replaces scripts/review.sh, in the repo and in the Template's originals so the copy check still passes, with a stub that
# records its arguments in <repo>/review-args and exits with $REVIEW_EXIT.
stub_review() {
  local repo="$1" f
  for f in "$repo/scripts/review.sh" "$repo/$skill/template/owned/scripts/review.sh"; do
    cat > "$f" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" > "$(dirname "${BASH_SOURCE[0]}")/../review-args"
exit "${REVIEW_EXIT:-0}"
STUB
    chmod +x "$f"
  done
}

# tcommit <repo> <message>: an empty commit that doesn't depend on the developer's global git config (signing, hooks).
tcommit() { git -C "$1" -c user.name=t -c user.email=t@example.com -c commit.gpgsign=false -c core.hooksPath=/dev/null commit -q --allow-empty -m "$2"; }

# with_origin <repo>: a base commit, a commit of the branch's own on top of it, and an origin/main that has moved on from the base. The merge
# base ($base) is then neither HEAD nor origin/main's tip, so a gate that passed either of those would fail the argument check.
with_origin() {
  tcommit "$1" base
  base="$(git -C "$1" rev-parse HEAD)"
  tcommit "$1" branch
  local moved
  moved="$(git -C "$1" -c user.name=t -c user.email=t@example.com commit-tree -p "$base" -m moved "$base^{tree}")"
  git -C "$1" update-ref refs/remotes/origin/main "$moved"
}

# run <repo>: the gate's exit status in $status, and whether the stub ran in $ran. GATE_SKIP_REVIEW is 1 unless $REVIEW_SKIP is set:
# to a value, which is passed as it is, or to nothing, which leaves GATE_SKIP_REVIEW unset.
run() {
  rm -f "$1/$skill/ran" "$1/review-args"
  status=0
  local skip=(GATE_SKIP_REVIEW=1)
  if [[ -n "${REVIEW_SKIP+x}" ]]; then skip=(); [[ -z "$REVIEW_SKIP" ]] || skip=(GATE_SKIP_REVIEW="$REVIEW_SKIP"); fi
  # $GATE_ARGS is the gate's arguments, split on spaces.
  local gate_args=()
  [[ -z "${GATE_ARGS:-}" ]] || read -ra gate_args <<<"$GATE_ARGS"
  (cd "$1" && env -u GATE_SKIP_REVIEW "${skip[@]+"${skip[@]}"}" GATE_SKIP_SELF_TEST=1 STUB_EXIT="${STUB_EXIT:-0}" REVIEW_EXIT="${REVIEW_EXIT:-0}" bash scripts/gate.sh "${gate_args[@]+"${gate_args[@]}"}" >"$1/out" 2>&1) || status=$?
  [[ -e "$1/$skill/ran" ]] && ran=yes || ran=no
}

repo="$(new_repo match)"
run "$repo"
check "matching copies pass" "$([[ $status == 0 ]] && echo 0 || echo 1)"
check "matching copies run test.sh" "$([[ $ran == yes ]] && echo 0 || echo 1)"

repo="$(new_repo status)"
STUB_EXIT=3 run "$repo"
check "test.sh's exit status is the gate's" "$([[ $status == 3 ]] && echo 0 || echo 1)"

repo="$(new_repo content)"
echo "# local edit" >> "$repo/.github/hooks/pre-push"
run "$repo"
check "a changed copy fails the gate" "$([[ $status == 1 ]] && echo 0 || echo 1)"
check "a changed copy stops before test.sh" "$([[ $ran == no ]] && echo 0 || echo 1)"
check "a changed copy is named in the output" "$(grep -q '\.github/hooks/pre-push' "$repo/out" && echo 0 || echo 1)"

repo="$(new_repo missing)"
rm "$repo/scripts/check-branch-name.sh"
run "$repo"
check "a missing copy fails the gate" "$([[ $status == 1 ]] && echo 0 || echo 1)"
check "a missing copy stops before test.sh" "$([[ $ran == no ]] && echo 0 || echo 1)"
check "a missing copy is named in the output" "$(grep -q 'scripts/check-branch-name.sh' "$repo/out" && echo 0 || echo 1)"

repo="$(new_repo execbit)"
chmod -x "$repo/.github/hooks/pre-commit"
run "$repo"
check "a copy without the execute bit fails the gate" "$([[ $status == 1 ]] && echo 0 || echo 1)"
check "a copy without the execute bit stops before test.sh" "$([[ $ran == no ]] && echo 0 || echo 1)"

repo="$(new_repo execbit-original)"
chmod -x "$repo/$skill/template/owned/.github/hooks/post-checkout"
run "$repo"
check "a copy executable where the original isn't fails the gate" "$([[ $status == 1 ]] && echo 0 || echo 1)"

# --quick stops after the lint, before the review check and test.sh, and never passes the gate for a push: only the bare script does.
repo="$(new_repo quick)"; stub_review "$repo"; with_origin "$repo"
GATE_ARGS=--quick STUB_EXIT=3 REVIEW_EXIT=1 REVIEW_SKIP='' run "$repo"
check "--quick passes without running test.sh" "$([[ $status == 0 && $ran == no ]] && echo 0 || echo 1)"
check "--quick doesn't call review.sh" "$([[ ! -e $repo/review-args ]] && echo 0 || echo 1)"
check "--quick runs the Markdown and YAML lint" "$(grep -q 'markdownlint (whole repo' "$repo/out" && grep -q 'yamllint (whole repo' "$repo/out" && echo 0 || echo 1)"
check "--quick says it is not the gate" "$(grep -q 'Run scripts/gate.sh, with no argument, before pushing' "$repo/out" && echo 0 || echo 1)"
# A Markdown or YAML lint failure stops --quick too.
repo2="$(new_repo quick-lint)"
{ printf '# T\n\n'; printf 'word %.0s' $(seq 1 80); printf '\n'; } > "$repo2/bad.md"
GATE_ARGS=--quick run "$repo2"
check "--quick fails on a Markdown lint error" "$([[ $status == 1 ]] && grep -q 'MD013' "$repo2/out" && echo 0 || echo 1)"
rm "$repo2/bad.md"; printf 'a: [\n' > "$repo2/bad.yml"
GATE_ARGS=--quick run "$repo2"
check "--quick fails on a YAML lint error" "$([[ $status == 1 && $ran == no ]] && grep -q 'bad.yml' "$repo2/out" && grep -q 'syntax error' "$repo2/out" && echo 0 || echo 1)"
# The ShellCheck step over the shell files the branch changes. The scripts' own text: $unquoted must reach the file as written (SC2086 is what shellcheck then says).
# shellcheck disable=SC2016
bad_script='#!/usr/bin/env bash\necho $unquoted\n'
commit_all() { git -C "$1" add -A; git -C "$1" -c user.name=t -c user.email=t@example.com -c commit.gpgsign=false -c core.hooksPath=/dev/null commit -q -m "$2"; }

# An uncommitted file, a committed one and a hook without the .sh suffix stop --quick and the full gate; the full gate fails in this step, before test.sh.
repo3="$(new_repo quick-shellcheck)"; stub_review "$repo3"; with_origin "$repo3"
printf '%b' "$bad_script" > "$repo3/scripts/bad.sh"
GATE_ARGS=--quick run "$repo3"
check "--quick fails on a shellcheck finding in an uncommitted shell file" "$([[ $status == 1 && $ran == no ]] && grep -q 'SC2086' "$repo3/out" && echo 0 || echo 1)"
run "$repo3"
check "the full gate fails in the shellcheck step too, before test.sh" "$([[ $status == 1 && $ran == no ]] && grep -q 'SC2086' "$repo3/out" && echo 0 || echo 1)"
commit_all "$repo3" bad
GATE_ARGS=--quick run "$repo3"
check "a committed shell file with a finding fails too" "$([[ $status == 1 ]] && grep -q 'SC2086' "$repo3/out" && echo 0 || echo 1)"
git -C "$repo3" rm -q -f scripts/bad.sh
printf '%b' "$bad_script" > "$repo3/.github/hooks/pre-push"; printf '%b' "$bad_script" > "$repo3/$skill/template/owned/.github/hooks/pre-push"
GATE_ARGS=--quick run "$repo3"
check "a hook without the .sh suffix is checked" "$([[ $status == 1 ]] && grep -qF 'In .github/hooks/pre-push' "$repo3/out" && grep -q 'SC2086' "$repo3/out" && echo 0 || echo 1)"
cp -p "$here/.github/hooks/pre-push" "$repo3/.github/hooks/pre-push"; cp -p "$here/.github/hooks/pre-push" "$repo3/$skill/template/owned/.github/hooks/pre-push"
printf '%b' "$bad_script" > "$repo3/notes.txt"
GATE_ARGS=--quick run "$repo3"
check "a deleted shell file is not checked, nor a file that is not a shell file, and clean ones pass" "$([[ $status == 0 ]] && grep -q '[1-9][0-9]* changed shell file(s) checked' "$repo3/out" && echo 0 || echo 1)"

# A file with a non-ASCII name is found (git quotes it without -z).
repo4="$(new_repo quick-shellcheck-name)"; stub_review "$repo4"; with_origin "$repo4"
printf '%b' "$bad_script" > "$repo4/scripts/"$'caf\303\251 bad.sh'
GATE_ARGS=--quick run "$repo4"
check "a shell file with a non-ASCII name and a space is checked" "$([[ $status == 1 ]] && grep -q 'SC2086' "$repo4/out" && echo 0 || echo 1)"

# The scope is what the branch changes: a file with a finding that is already in origin/main's history, and a deleted one, are left alone.
repo5="$(new_repo quick-shellcheck-scope)"; stub_review "$repo5"
printf '%b' "$bad_script" > "$repo5/scripts/old-bad.sh"; printf '%b' "$bad_script" > "$repo5/scripts/deleted-bad.sh"
commit_all "$repo5" base; git -C "$repo5" update-ref refs/remotes/origin/main HEAD
git -C "$repo5" rm -q -f scripts/deleted-bad.sh; commit_all "$repo5" "branch: a deletion"
GATE_ARGS=--quick run "$repo5"
check "a finding in a file the branch did not change, and a deleted file, are not reported" "$([[ $status == 0 ]] && grep -q '0 changed shell file(s) checked' "$repo5/out" && echo 0 || echo 1)"
printf '%b' "$bad_script" >> "$repo5/scripts/old-bad.sh"
GATE_ARGS=--quick run "$repo5"
check "the same file fails once the branch changes it" "$([[ $status == 1 ]] && grep -q 'old-bad.sh' "$repo5/out" && echo 0 || echo 1)"

# A tracked symlink replaced by a regular script is a type change in git's diff: its new contents are checked.
repo5b="$(new_repo quick-shellcheck-typechange)"; stub_review "$repo5b"
printf 'echo ok\n' > "$repo5b/scripts/real.sh"; ln -s real.sh "$repo5b/scripts/link.sh"
commit_all "$repo5b" base; git -C "$repo5b" update-ref refs/remotes/origin/main HEAD
rm "$repo5b/scripts/link.sh"; printf '%b' "$bad_script" > "$repo5b/scripts/link.sh"
GATE_ARGS=--quick run "$repo5b"
check "a symlink replaced by a shell script with a finding is checked (a type change)" "$([[ $status == 1 ]] && grep -q 'SC2086' "$repo5b/out" && echo 0 || echo 1)"

# With no origin/main, or one that shares no history with HEAD, every shell file is checked.
repo6="$(new_repo quick-shellcheck-nomain)"; printf '%b' "$bad_script" > "$repo6/scripts/bad.sh"
GATE_ARGS=--quick run "$repo6"
check "with no origin/main every shell file is checked" "$([[ $status == 1 ]] && grep -q 'SC2086' "$repo6/out" && echo 0 || echo 1)"
repo7="$(new_repo quick-shellcheck-unrelated)"; printf '%b' "$bad_script" > "$repo7/scripts/bad.sh"; tcommit "$repo7" base
git -C "$repo7" update-ref refs/remotes/origin/main "$(git -C "$repo7" -c user.name=t -c user.email=t@example.com commit-tree -m other "$(git -C "$repo7" hash-object -t tree -w /dev/null)")"
GATE_ARGS=--quick run "$repo7"
check "with an origin/main that shares no history every shell file is checked" "$([[ $status == 1 ]] && grep -q 'SC2086' "$repo7/out" && echo 0 || echo 1)"

# A missing shellcheck fails the gate and says so.
repo8="$(new_repo quick-no-shellcheck)"; mkdir -p "$work/noshellcheck"
for tool in bash git python3 markdownlint-cli2 yamllint node pnpm env sh cat grep sed awk sort dirname basename mktemp rm cp mv ls tr head tail wc cmp mkdir touch uname date find; do
  tool_path="$(command -v "$tool" 2>/dev/null || true)"; [[ -z $tool_path ]] || ln -sf "$tool_path" "$work/noshellcheck/$tool"
done
status=0
(cd "$repo8" && env -u GATE_SKIP_REVIEW GATE_SKIP_REVIEW=1 GATE_SKIP_SELF_TEST=1 PATH="$work/noshellcheck" bash scripts/gate.sh --quick >"$repo8/out" 2>&1) || status=$?
check "a missing shellcheck fails the gate and says so" "$([[ $status == 1 ]] && grep -q 'shellcheck not found' "$repo8/out" && echo 0 || echo 1)"

# test.sh's pass is remembered for a clean tree: the same tree is not tested twice; a new commit, an uncommitted or untracked file, GATE_NO_CACHE=1 and a failure all run it.
repo9="$(new_repo cache)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repo9/.git/info/exclude"; commit_all "$repo9" base
run "$repo9"
check "test.sh runs on a clean tree it has not passed on" "$([[ $status == 0 && $ran == yes ]] && echo 0 || echo 1)"
run "$repo9"
check "and is skipped the second time, saying so, with the same exit status" "$([[ $status == 0 && $ran == no ]] && grep -q 'already passed on this tree' "$repo9/out" && echo 0 || echo 1)"
GATE_NO_CACHE=1 run "$repo9"
check "GATE_NO_CACHE=1 runs it again" "$([[ $status == 0 && $ran == yes ]] && echo 0 || echo 1)"
echo more > "$repo9/extra.txt"; run "$repo9"
check "an untracked file makes the tree dirty, so test.sh runs" "$([[ $ran == yes ]] && echo 0 || echo 1)"
rm "$repo9/extra.txt"; run "$repo9"
check "the pass of the clean tree is still remembered after that" "$([[ $ran == no ]] && echo 0 || echo 1)"
echo more > "$repo9/extra.txt"; commit_all "$repo9" "a new tree"
STUB_EXIT=3 run "$repo9"
check "a new commit's tree runs test.sh, and a failure is the gate's status" "$([[ $status == 3 && $ran == yes ]] && echo 0 || echo 1)"
run "$repo9"
check "a failure was not remembered: the next run runs test.sh again" "$([[ $status == 0 && $ran == yes ]] && echo 0 || echo 1)"
repo10="$(new_repo cache-nocommit)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repo10/.git/info/exclude"
run "$repo10"; run "$repo10"
check "a repo with no commit has no tree to remember, so test.sh runs every time" "$([[ $status == 0 && $ran == yes ]] && echo 0 || echo 1)"

# A bare git status --porcelain in an Owned script fails the full gate and --quick, before test.sh.
repo11="$(new_repo git-status)"; mkdir -p "$repo11/$skill/tests" "$repo11/$skill/template/owned/scripts"
cp -p "$here/$skill/tests/check-git-status.sh" "$repo11/$skill/tests/"
# shellcheck disable=SC2016 # the script's own text
printf '%s\n' '[[ -z "$(git status --porcelain)" ]] || exit 1' > "$repo11/$skill/template/owned/scripts/guard.sh"
GATE_ARGS=--quick run "$repo11"
check "--quick fails on a git status --porcelain without --untracked-files" "$([[ $status == 1 && $ran == no ]] && grep -q 'guard.sh:1:' "$repo11/out" && echo 0 || echo 1)"
run "$repo11"
check "the full gate fails on it too, before test.sh" "$([[ $status == 1 && $ran == no ]] && echo 0 || echo 1)"
rm "$repo11/$skill/tests/check-git-status.sh"
GATE_ARGS=--quick run "$repo11"
check "a missing checker fails the gate, not passes it" "$([[ $status == 1 ]] && echo 0 || echo 1)"

echo changed >> "$repo/scripts/check-pr-title.sh"
GATE_ARGS=--quick run "$repo"
check "--quick still fails a copy that drifted" "$([[ $status == 1 ]] && echo 0 || echo 1)"
GATE_ARGS=--fast run "$repo"
check "an unknown argument is refused" "$([[ $status == 2 && $ran == no ]] && echo 0 || echo 1)"
GATE_ARGS="--quick --quick" run "$repo"
check "a second argument is refused" "$([[ $status == 2 ]] && echo 0 || echo 1)"

# A bare #N in an Owned file fails the full gate and --quick, before test.sh.
repo="$(new_repo bare-ref)"
echo "See #5 for why." > "$repo/$skill/template/owned/NOTES.txt"
run "$repo"
check "a bare #N in an Owned file fails the gate" "$([[ $status == 1 && $ran == no ]] && grep -q 'NOTES.txt:1' "$repo/out" && echo 0 || echo 1)"
GATE_ARGS=--quick run "$repo"
check "--quick fails on a bare #N in an Owned file" "$([[ $status == 1 ]] && grep -q 'NOTES.txt:1' "$repo/out" && echo 0 || echo 1)"
echo "See mpaulosky/Application_Factory#5 for why." > "$repo/$skill/template/owned/NOTES.txt"
run "$repo"
check "a repo-qualified issue reference passes" "$([[ $status == 0 ]] && echo 0 || echo 1)"

# --sync copies the Template's file over a drifted, missing or non-executable copy, then stops without the rest of the gate.
repo="$(new_repo sync)"
echo "# local edit" >> "$repo/.github/hooks/pre-push"
rm "$repo/scripts/check-branch-name.sh"
chmod -x "$repo/.github/hooks/pre-commit"
GATE_ARGS=--sync run "$repo"
check "--sync exits 0 and doesn't run test.sh" "$([[ $status == 0 && $ran == no ]] && echo 0 || echo 1)"
check "--sync names what it synced" "$(grep -q 'synced .github/hooks/pre-push' "$repo/out" && grep -q 'synced scripts/check-branch-name.sh' "$repo/out" && grep -q 'synced .github/hooks/pre-commit' "$repo/out" && echo 0 || echo 1)"
check "--sync restores the content and the execute bit" "$(cmp -s "$repo/.github/hooks/pre-push" "$here/.github/hooks/pre-push" && [[ -x $repo/scripts/check-branch-name.sh && -x $repo/.github/hooks/pre-commit ]] && echo 0 || echo 1)"
GATE_ARGS=--sync run "$repo"
check "--sync on matching copies syncs nothing" "$([[ $status == 0 ]] && grep -q '^0 copies synced' "$repo/out" && echo 0 || echo 1)"
GATE_ARGS="--sync --quick" run "$repo"
check "--sync takes no other argument" "$([[ $status == 2 ]] && echo 0 || echo 1)"
repo="$(new_repo drift-message)"
echo "# local edit" >> "$repo/scripts/review.sh"
run "$repo"
check "the drift message points at --sync" "$([[ $status == 1 ]] && grep -q 'gate.sh --sync' "$repo/out" && echo 0 || echo 1)"
# The workflow the message describes: after --sync the next full gate passes.
GATE_ARGS=--sync run "$repo"
run "$repo"
check "the gate passes after --sync" "$([[ $status == 0 && $ran == yes ]] && echo 0 || echo 1)"
# A copy whose Template original lacks the execute bit is made non-executable too.
repo="$(new_repo sync-execbit)"
chmod -x "$repo/$skill/template/owned/.github/hooks/post-checkout"
GATE_ARGS=--sync run "$repo"
check "--sync drops the execute bit where the Template's original lacks it" "$([[ $status == 0 && ! -x $repo/.github/hooks/post-checkout ]] && echo 0 || echo 1)"
run "$repo"
check "the gate passes after that --sync" "$([[ $status == 0 && $ran == yes ]] && echo 0 || echo 1)"

# The issue-reference step fails closed: no python3, or a checker that can't check, fails the gate with its own message and doesn't read as a pass.
repo="$(new_repo no-python)"
mkdir -p "$work/nopy"
for tool in bash cat cmp dirname env git markdownlint-cli2 node pnpm rm yamllint; do
  tool_path="$(command -v "$tool" 2>/dev/null || true)"; [[ -z $tool_path ]] || ln -sf "$tool_path" "$work/nopy/$tool"
done
status=0
(cd "$repo" && env -u GATE_SKIP_REVIEW GATE_SKIP_REVIEW=1 GATE_SKIP_SELF_TEST=1 PATH="$work/nopy" bash scripts/gate.sh >"$repo/out" 2>&1) || status=$?
check "a missing python3 fails the gate and says so" "$([[ $status == 1 ]] && grep -q 'python3 not found' "$repo/out" && echo 0 || echo 1)"
repo="$(new_repo broken-checker)"
printf 'import sys\nsys.exit(2)\n' > "$repo/$skill/tests/check-issue-refs.py"
run "$repo"
check "a checker that fails without checking fails the gate with its own message" "$([[ $status == 1 && $ran == no ]] && grep -q 'failed (exit 2) without checking' "$repo/out" && echo 0 || echo 1)"

# What gates a push never passes --quick or --sync (--sync runs no check at all): the pre-push hook and the Factory's CI call the gate with no argument.
check "the pre-push hook runs the gate with no argument" "$(grep -E 'scripts/gate\.sh' "$here/.github/hooks/pre-push" | grep -vE '^#' | grep -qE 'gate\.sh"?$' && echo 0 || echo 1)"
# grep exits 1 for no match and 2 for an error (a moved folder): only 1 passes, so the guard can't go quiet when its paths move.
quick_rc=0
grep -rqE -e '--(quick|sync)' "$here/.github/hooks" "$here/.github/workflows" 2>/dev/null && quick_rc=0 || quick_rc=$?
check "no hook or workflow passes --quick or --sync" "$([[ -d $here/.github/hooks && -d $here/.github/workflows && $quick_rc == 1 ]] && echo 0 || echo 1)"
check "the Factory's CI runs the gate with no argument" "$(grep -E 'run: bash "scripts/gate\.sh"$' "$here/.github/workflows/template-tests.yml" >/dev/null && echo 0 || echo 1)"

# The local review check (ADR 0010). The stub review.sh stands for scripts/review.sh; GATE_SKIP_REVIEW=1 skips it, anything else doesn't.
repo="$(new_repo review-skipped)"; stub_review "$repo"; with_origin "$repo"
REVIEW_EXIT=1 run "$repo"
check "GATE_SKIP_REVIEW=1 skips the review check and passes" "$([[ $status == 0 && $ran == yes ]] && echo 0 || echo 1)"
check "GATE_SKIP_REVIEW=1 doesn't call review.sh" "$([[ ! -e $repo/review-args ]] && echo 0 || echo 1)"
check "GATE_SKIP_REVIEW=1 says the check was skipped" "$(grep -q 'Local review check skipped' "$repo/out" && echo 0 || echo 1)"

for skip in true 0 yes; do
  repo="$(new_repo "review-skip-$skip")"; stub_review "$repo"; with_origin "$repo"
  REVIEW_SKIP="$skip" REVIEW_EXIT=1 run "$repo"
  check "GATE_SKIP_REVIEW=$skip doesn't skip the review check" "$([[ $status == 1 && -e $repo/review-args ]] && echo 0 || echo 1)"
done

repo="$(new_repo review-passes)"; stub_review "$repo"; with_origin "$repo"
REVIEW_SKIP="" REVIEW_EXIT=0 run "$repo"
check "a passing review check lets the gate run test.sh" "$([[ $status == 0 && $ran == yes ]] && echo 0 || echo 1)"
check "review.sh is called as check <merge base with origin/main>" "$([[ "$(cat "$repo/review-args")" == "check $base" ]] && echo 0 || echo 1)"

# An origin/main that shares no history with HEAD (a shallow clone, or unrelated histories) is named as that, not as a missing origin/main.
repo="$(new_repo review-unrelated)"; stub_review "$repo"; tcommit "$repo" base
unrelated="$(git -C "$repo" -c user.name=t -c user.email=t@example.com commit-tree -m unrelated "$(git -C "$repo" hash-object -t tree -w /dev/null)")"
git -C "$repo" update-ref refs/remotes/origin/main "$unrelated"
REVIEW_SKIP="" run "$repo"
check "an origin/main with no shared history fails the gate" "$([[ $status == 1 && $ran == no ]] && echo 0 || echo 1)"
check "unrelated histories are named, not reported as a missing origin/main" "$(grep -q 'share no history' "$repo/out" && ! grep -q 'No origin/main' "$repo/out" && echo 0 || echo 1)"
check "unrelated histories don't call review.sh" "$([[ ! -e $repo/review-args ]] && echo 0 || echo 1)"

# The review's stand-alone tests need tsx: without it and without pnpm the gate says how to get it, before anything else.
repo="$(new_repo no-pnpm)"; mkdir -p "$repo/scripts/tests"; printf '#!/usr/bin/env bash\nexit 0\n' > "$repo/scripts/tests/gate.test.sh"
chmod +x "$repo/scripts/tests/gate.test.sh"
bare="$work/bare-bin"; mkdir -p "$bare"
for tool in git env bash dirname mkdir chmod; do ln -sf "$(command -v "$tool")" "$bare/$tool"; done
status=0
(cd "$repo" && env -u GATE_SKIP_SELF_TEST PATH="$bare" "$(command -v bash)" scripts/gate.sh >"$repo/out" 2>&1) || status=$?
check "a missing pnpm fails the gate" "$([[ $status == 1 ]] && echo 0 || echo 1)"
check "a missing pnpm says to run corepack enable pnpm" "$(grep -q 'corepack enable pnpm' "$repo/out" && echo 0 || echo 1)"

# A pnpm that installs nothing leaves no tsx: the gate says so instead of dying on a missing binary; one that installs it gets past this step.
repo="$(new_repo pnpm-no-tsx)"; mkdir -p "$repo/scripts/tests"; printf '#!/usr/bin/env bash\nexit 0\n' > "$repo/scripts/tests/gate.test.sh"
chmod +x "$repo/scripts/tests/gate.test.sh"
printf '#!/usr/bin/env bash\nexit 0\n' > "$bare/pnpm"; chmod +x "$bare/pnpm"
status=0
(cd "$repo" && env -u GATE_SKIP_SELF_TEST PATH="$bare" "$(command -v bash)" scripts/gate.sh >"$repo/out" 2>&1) || status=$?
check "a pnpm that installs no tsx fails the gate" "$([[ $status == 1 ]] && echo 0 || echo 1)"
check "a pnpm that installs no tsx says so" "$(grep -q 'pnpm install did not install node_modules/.bin/tsx' "$repo/out" && echo 0 || echo 1)"
repo="$(new_repo pnpm-with-tsx)"; mkdir -p "$repo/scripts/tests"; printf '#!/usr/bin/env bash\nexit 0\n' > "$repo/scripts/tests/gate.test.sh"
chmod +x "$repo/scripts/tests/gate.test.sh"
printf '#!/usr/bin/env bash\nmkdir -p node_modules/.bin && printf "#!/usr/bin/env bash\\nexit 0\\n" > node_modules/.bin/tsx && chmod +x node_modules/.bin/tsx\n' > "$bare/pnpm"
status=0
(cd "$repo" && env -u GATE_SKIP_SELF_TEST PATH="$bare" "$(command -v bash)" scripts/gate.sh >"$repo/out" 2>&1) || status=$?
check "a pnpm that installs tsx gets the gate past the install step" "$(! grep -qE 'did not install|pnpm isn.t installed' "$repo/out" && grep -q 'Copies of Template files' "$repo/out" && echo 0 || echo 1)"
rm -f "$bare/pnpm"

repo="$(new_repo review-fails)"; stub_review "$repo"; with_origin "$repo"
REVIEW_SKIP="" REVIEW_EXIT=1 run "$repo"
check "a failing review check fails the gate" "$([[ $status != 0 ]] && echo 0 || echo 1)"
check "a failing review check stops before test.sh" "$([[ $ran == no ]] && echo 0 || echo 1)"

repo="$(new_repo review-no-origin)"; stub_review "$repo"
tcommit "$repo" base
REVIEW_SKIP="" REVIEW_EXIT=0 run "$repo"
check "no origin/main fails the gate" "$([[ $status == 1 ]] && echo 0 || echo 1)"
check "no origin/main stops before test.sh" "$([[ $ran == no ]] && echo 0 || echo 1)"
check "no origin/main says to fetch it" "$(grep -q 'git fetch origin main' "$repo/out" && echo 0 || echo 1)"
check "no origin/main doesn't call review.sh" "$([[ ! -e $repo/review-args ]] && echo 0 || echo 1)"

# gate.sh runs the copied tests that stand alone (its self-test step): each copied *.test.mts is named there, or it is never run.
for f in "${copies[@]}"; do
  [[ "$f" == *.test.mts ]] || continue
  check "gate.sh runs $f" "$(grep -F 'tsx --test' "$here/scripts/gate.sh" | grep -Fq " $f" && echo 0 || echo 1)"
done

# A git that fails is not read as "nothing to check" or "a clean tree". wrapper_for <name> <git subcommand>: a folder with a git that fails on that subcommand.
real_git="$(command -v git)"
wrapper_for() {
  mkdir -p "$work/$1"
  # shellcheck disable=SC2016 # the wrapper script is text: $@ and $a must not expand here
  printf '#!/usr/bin/env bash\nfor a in "$@"; do [[ "$a" == %q ]] && exit 1; done\nexec %q "$@"\n' "$2" "$real_git" > "$work/$1/git"
  chmod +x "$work/$1/git"
}
repo12="$(new_repo git-fails-diff)"; stub_review "$repo12"; with_origin "$repo12"; wrapper_for gitdiff diff
PATH="$work/gitdiff:$PATH" GATE_ARGS=--quick run "$repo12"
check "a git that fails listing the changed files fails the gate, saying so" "$([[ $status == 1 ]] && grep -q 'could not list the changed files' "$repo12/out" && echo 0 || echo 1)"
repo13="$(new_repo git-fails-status)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repo13/.git/info/exclude"; commit_all "$repo13" base; wrapper_for gitstatus status
PATH="$work/gitstatus:$PATH" run "$repo13"
check "a git status that fails is not a clean tree: test.sh runs and no pass is stored" "$([[ $ran == yes && ! -e $repo13/.git/gate-cache ]] && echo 0 || echo 1)"
run "$repo13"
check "so the next run, with a working git, runs test.sh again" "$([[ $ran == yes ]] && echo 0 || echo 1)"
# the cache is shared by the worktrees of the clone, and a cache folder that can't be written does not fail a passing test.sh
repo14="$(new_repo cache-worktree)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repo14/.git/info/exclude"; commit_all "$repo14" base
run "$repo14"
git -C "$repo14" worktree add -q --detach "$work/cache-worktree-2" HEAD
printf 'out\nreview-args\n%s/ran\n' "$skill" >> "$repo14/.git/info/exclude"
run "$work/cache-worktree-2"
check "a linked worktree of the clone shares the remembered pass" "$([[ $status == 0 && $ran == no ]] && grep -q 'already passed on this tree' "$work/cache-worktree-2/out" && echo 0 || echo 1)"
repo15="$(new_repo cache-readonly)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repo15/.git/info/exclude"; commit_all "$repo15" base
: > "$repo15/.git/gate-cache"
run "$repo15"
check "a cache folder that can't be made leaves a passing test.sh passing, with a note" "$([[ $status == 0 && $ran == yes ]] && grep -q 'could not record the test.sh pass' "$repo15/out" && echo 0 || echo 1)"

# The Factory's own files under an Owned pattern are listed in .sandcastle/lib/repo-config.mts, so a branch named chore/reapply-baseline that
# changes only them still needs a review (the ledger treats a change to only Owned files as a re-Apply). Dropping one from the list shows here.
for f in .github/workflows/template-tests.yml .gitignore .markdownlint-cli2.jsonc .yamllint.yml scripts/gate.sh scripts/tests/gate.test.sh; do
  check "repo-config.mts lists $f as the Factory's own" "$(grep -Fxq "  \"$f\"," "$here/.sandcastle/lib/repo-config.mts" && echo 0 || echo 1)"
done

# package.json carries the review's packages (ADR 0010), so Dependabot has to watch them: its npm block and the lockfile's directory.
check "dependabot.yml has an npm block for the root package.json" "$(grep -Fxq '  - package-ecosystem: "npm"' "$here/.github/dependabot.yml" && grep -A1 -Fx '  - package-ecosystem: "npm"' "$here/.github/dependabot.yml" | grep -Fxq '    directory: "/"' && echo 0 || echo 1)"

# The list in gate.sh is the one the docs name: every copy the test builds is checked, so a file dropped from the list shows here.
for f in "${copies[@]}"; do
  check "gate.sh lists $f" "$(grep -q "^  $f\$" "$here/scripts/gate.sh" && echo 0 || echo 1)"
done

echo
echo "gate.test.sh: $pass passed, $failed failed"
[[ $failed == 0 ]]
