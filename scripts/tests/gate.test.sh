#!/usr/bin/env bash
# Tests scripts/gate.sh against throwaway repos with a stub test.sh and a stub review.sh: matching copies run the stub and pass its exit
# status on; a changed, missing or execute-bit-mismatched copy stops the gate before the stub runs; the local review check (ADR 0010)
# skips only on GATE_SKIP_REVIEW=1, fails without an origin/main or when origin/main shares no history with HEAD, and passes review.sh check's status on, ahead of test.sh. It also checks
# that gate.sh names each copied stand-alone test and lists every copy, that repo-config.mts lists the Factory's own files under Owned patterns,
# that test.sh's pass is remembered per clean tree (skipped the second time, shared by worktrees, trusted only while its token is in its start stamp, checked and stamped under a per-tree lock that a held lock or a signal leaves safe, never stored for a tree that changed), that --quick stops after the lint and --sync restores drifted, missing and non-executable copies and then stops, that a bare #N in an Owned file fails the gate
# and --quick (and a missing python3 or a broken checker fails closed), that dependabot.yml watches the root package.json, and the review tests' install step (a missing pnpm, a pnpm that leaves no tsx, a pnpm that installs it).
# It also checks that .claude/settings.json runs the executable scripts/lint-edited-file.sh after Edit, Write and MultiEdit (ADR 0011), and that the check fails on a dropped matcher, another event or hook type,
# a command without the quotes around CLAUDE_PROJECT_DIR or naming another script, a non-executable script and a file that is not JSON.
# It also checks GATE_PART (ADR 0012) and TEST_SH_SHARD (ADR 0013, only the template part runs a shard; test.sh's steps each sit in one shard; the workflow's matrix lists them all): each part runs alone, a bad value, or --quick or --sync with a part, is refused, the Sandcastle check runs in a full gate only when the branch changed
# a file it reads (and is remembered per clean tree), and the CI workflow has one job per part and a Template tests job that needs them all.
#
#   scripts/tests/gate.test.sh
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"
trap 'chmod -R u+w "$work" 2>/dev/null || true; rm -rf "$work"' EXIT # a case that makes a folder read-only is cleaned up even when it is interrupted
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX GATE_NO_CACHE STUB_TOUCH GATE_PART STUB_SANDCASTLE_EXIT RUN_PART TEST_SH_SHARD RUN_SHARD STUB_DURING MKDIR_LOG MKDIR_FAIL_ONCE MKDIR_RELEASE CAT_MARK # a caller's GATE_NO_CACHE=1 must not reach the stub gate runs; the tests set it themselves where they need it

pass=0; failed=0
check() { # check <description> <condition: 0 for ok>
  if [[ "$2" == 0 ]]; then echo "ok   - $1"; pass=$((pass + 1)); else echo "FAIL - $1"; failed=$((failed + 1)); fi
}

skill=".claude/skills/repo-ci-baseline"
copies=(.github/hooks/pre-commit .github/hooks/pre-push .github/hooks/post-checkout scripts/check-branch-name.sh .github/workflows/claude-review.yml .github/workflows/pr-automerge.yml .github/workflows/pr-review-submitted.yml .github/scripts/post_claude_review.py .github/ISSUE_TEMPLATE/bug_report.md .github/ISSUE_TEMPLATE/feature_request.md .github/ISSUE_TEMPLATE/config.yml .github/pull_request_template.md .github/workflows/pr-title.yml scripts/check-pr-title.sh .github/workflows/lint-markdown.yml .github/workflows/lint-yaml.yml .github/workflows/lint-actions.yml scripts/review.sh scripts/lint-edited-file.sh .sandcastle/lib/review-ledger.mts .sandcastle/lib/security-record.mts .sandcastle/lib/errors.mts .sandcastle/lib/host-limit.mts .sandcastle/lib/plain-text.mts .sandcastle/lib/review-ledger.test.mts .sandcastle/lib/security-record.test.mts .sandcastle/lib/plain-text.test.mts .claude/skills/pre-push-review/SKILL.md .claude/skills/pre-push-review/lenses.md .claude/skills/pre-push-review/review-bar.md .claude/agents/pre-push-reviewer.md .security/README.md)

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
printf '%s\n' "${TEST_SH_SHARD-unset}" >> "$(git rev-parse --git-common-dir)/test-sh-shards"
# STUB_DURING: a command run while this test.sh runs (once: the runs it starts don't see it), standing in for another gate run on the same tree.
if [[ -n "${STUB_DURING:-}" ]]; then during="$STUB_DURING"; (unset STUB_DURING; eval "$during") || true; fi
[[ -z "${STUB_TOUCH:-}" ]] || : > "$STUB_TOUCH"
exit "${STUB_EXIT:-0}"
STUB
  chmod +x "$repo/$skill/test.sh"
  # The Sandcastle check's stub marks that it ran inside .git, so it never makes the tree look changed (the cache cases need a clean tree).
  cat > "$repo/$skill/tests/sandcastle-check.test.sh" <<'STUB'
#!/usr/bin/env bash
touch "$(dirname "${BASH_SOURCE[0]}")/../../../../.git/ran-sandcastle"
exit "${STUB_SANDCASTLE_EXIT:-0}"
STUB
  chmod +x "$repo/$skill/tests/sandcastle-check.test.sh"
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
  rm -f "$1/$skill/ran" "$1/review-args" "$1/.git/ran-sandcastle"
  status=0
  local skip=(GATE_SKIP_REVIEW=1)
  if [[ -n "${REVIEW_SKIP+x}" ]]; then skip=(); [[ -z "$REVIEW_SKIP" ]] || skip=(GATE_SKIP_REVIEW="$REVIEW_SKIP"); fi
  # $GATE_ARGS is the gate's arguments, split on spaces.
  local gate_args=()
  [[ -z "${GATE_ARGS:-}" ]] || read -ra gate_args <<<"$GATE_ARGS"
  (cd "$1" && env -u GATE_SKIP_REVIEW "${skip[@]+"${skip[@]}"}" GATE_SKIP_SELF_TEST=1 STUB_EXIT="${STUB_EXIT:-0}" STUB_SANDCASTLE_EXIT="${STUB_SANDCASTLE_EXIT:-0}" REVIEW_EXIT="${REVIEW_EXIT:-0}" ${RUN_PART:+GATE_PART="$RUN_PART"} ${RUN_SHARD:+TEST_SH_SHARD="$RUN_SHARD"} bash scripts/gate.sh "${gate_args[@]+"${gate_args[@]}"}" >"$1/out" 2>&1) || status=$?
  [[ -e "$1/$skill/ran" ]] && ran=yes || ran=no
  [[ -e "$1/.git/ran-sandcastle" ]] && ran_sc=yes || ran_sc=no
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

# Every run that runs test.sh takes the trust from the remembered pass first (a new token in its stamp), so a failure (a forced run's, say) leaves nothing for the next ordinary run to trust.
repo16="$(new_repo cache-forced-failure)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repo16/.git/info/exclude"; commit_all "$repo16" base
run "$repo16"
GATE_NO_CACHE=1 STUB_EXIT=3 run "$repo16"
check "a forced run that fails is the gate's status" "$([[ $status == 3 && $ran == yes ]] && echo 0 || echo 1)"
run "$repo16"
check "and the next ordinary run tests again instead of trusting the old pass" "$([[ $status == 0 && $ran == yes ]] && echo 0 || echo 1)"
run "$repo16"
check "and that pass is remembered again" "$([[ $ran == no ]] && grep -q 'already passed on this tree' "$repo16/out" && echo 0 || echo 1)"
GATE_NO_CACHE=1 run "$repo16"
check "a forced run that passes keeps the pass remembered" "$([[ $ran == yes ]] && echo 0 || echo 1)"
run "$repo16"
check "so the ordinary run after it is skipped" "$([[ $ran == no ]] && echo 0 || echo 1)"

# A pass is never stored for a tree that test.sh left changed.
# moved.txt, the file the stub adds, stays visible to git (it is not in the exclude file): that is what makes the tree changed.
repo17="$(new_repo cache-changed)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repo17/.git/info/exclude"; commit_all "$repo17" base
STUB_TOUCH="$repo17/moved.txt" run "$repo17"
check "a test.sh that leaves the tree changed passes the gate, saying its pass is not remembered" "$([[ $status == 0 && $ran == yes ]] && grep -q 'the tree changed while test.sh ran' "$repo17/out" && echo 0 || echo 1)"
command rm -f "$repo17/moved.txt"; run "$repo17"
check "so the next run on the clean tree tests again" "$([[ $ran == yes ]] && echo 0 || echo 1)"
# A remembered pass that can't be removed (a read-only cache folder) is still not trusted after a run that disproves it: the run's token in the stamp takes the trust away.
repo18="$(new_repo cache-unclearable)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repo18/.git/info/exclude"; commit_all "$repo18" base
run "$repo18"
if [[ "$(id -u)" != 0 ]]; then
  chmod 555 "$repo18/.git/gate-cache"
  GATE_NO_CACHE=1 STUB_EXIT=3 run "$repo18"
  check "a forced run in a read-only cache folder runs test.sh and fails with its status" "$([[ $status == 3 && $ran == yes ]] && ! grep -q "could not write or remove" "$repo18/out" && echo 0 || echo 1)"
  # The folder is writable again before the check, so the stamp's token, not a lock that can't be taken in a read-only folder, is what stops the old pass being trusted.
  chmod 755 "$repo18/.git/gate-cache"
  run "$repo18"
  check "a remembered pass that can't be removed is not trusted after a forced run that failed: test.sh runs" "$([[ $ran == yes ]] && ! grep -q "test.sh already passed" "$repo18/out" && echo 0 || echo 1)"
  # The same in a linked worktree of a clone whose path holds glob characters (there the cache folder's path is absolute): files are named literally, not read as patterns.
  repo18b="$(new_repo 'cache-[x]-unclearable')"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repo18b/.git/info/exclude"; commit_all "$repo18b" base
  wt18b="$work/cache-[x]-worktree"; git -C "$repo18b" worktree add -q --detach "$wt18b"
  run "$wt18b"; run "$wt18b"
  check "a pass is remembered and trusted in a worktree of a clone whose path holds [ and ]" "$([[ $ran == no ]] && grep -q "test.sh already passed" "$wt18b/out" && echo 0 || echo 1)"
  chmod 555 "$repo18b/.git/gate-cache"
  GATE_NO_CACHE=1 STUB_EXIT=3 run "$wt18b"
  check "and there too a forced run runs test.sh and fails with its status" "$([[ $status == 3 && $ran == yes ]] && echo 0 || echo 1)"
  chmod 755 "$repo18b/.git/gate-cache"
  run "$wt18b"
  check "and a pass that can't be removed there is not trusted after a forced run that failed" "$([[ $ran == yes ]] && ! grep -q "test.sh already passed" "$wt18b/out" && echo 0 || echo 1)"
else
  echo "skip - the unclearable-pass case needs a user other than root"
fi
check "a GATE_NO_CACHE or STUB_TOUCH from the caller does not reach the stub gate runs (the gate runs this test with the caller's environment)" "$([[ -z ${GATE_NO_CACHE+x} && -z ${STUB_TOUCH+x} && -z ${GATE_PART+x} ]] && echo 0 || echo 1)"

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

# --- GATE_PART (ADR 0012): CI runs one part of the gate per job; a full gate runs them all -------------------------------------------------------------------
# The step headers of each part. A part prints its own and no other part's, so a step that moves to another part, or is dropped, is found here.
headers_static="Copies of Template files|Issue references in Owned files|git status guards in Owned scripts|markdownlint (whole repo|yamllint (whole repo|shellcheck (changed shell files)"
headers_template="Local review (scripts/review.sh check)|▶ test.sh"
headers_sandcastle="▶ Sandcastle check"
repoP="$(new_repo parts)"; stub_review "$repoP"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repoP/.git/info/exclude"
mkdir -p "$repoP/$skill/references" "$repoP/$skill/template/owned"
echo "# sandcastle" > "$repoP/$skill/references/sandcastle.md"
commit_all "$repoP" files; with_origin "$repoP"
tipP="$(git -C "$repoP" rev-parse HEAD)"
touch_path() { # touch_path <path>: adds a line to the file (made if missing), valid for the linters the gate runs over it
  mkdir -p "$repoP/$(dirname "$1")"
  [[ "$1" != *.sh || -s "$repoP/$1" ]] || printf '#!/usr/bin/env bash\n' > "$repoP/$1"
  [[ "$1" != *.md || -s "$repoP/$1" ]] || printf '# T\n' > "$repoP/$1"
  case "$1" in *.md) printf '\n%s\n' "text for $1" >> "$repoP/$1" ;; *.sh) echo "# $1" >> "$repoP/$1" ;; *) echo "$1" >> "$repoP/$1" ;; esac
}
reset_parts() { git -C "$repoP" reset -q --hard "$tipP"; git -C "$repoP" clean -fdq; }
part_check() { # part_check <part> <test.sh ran> <sandcastle ran>: GATE_PART=<part> passes, runs its own steps and no other part's
  local part="$1" group headers h problems=()
  GATE_NO_CACHE=1 REVIEW_SKIP="" RUN_PART="$part" run "$repoP"
  [[ $status == 0 && $ran == "$2" && $ran_sc == "$3" ]] || problems+=("status $status, test.sh ran: $ran, Sandcastle ran: $ran_sc")
  for group in static template sandcastle; do
    case "$group" in static) headers="$headers_static" ;; template) headers="$headers_template" ;; sandcastle) headers="$headers_sandcastle" ;; esac
    local IFS='|'
    for h in $headers; do
      if [[ "$group" == "$part" ]]; then grep -qF "$h" "$repoP/out" || problems+=("part $part did not print '$h'")
      else ! grep -qF "$h" "$repoP/out" || problems+=("part $part printed '$h', a step of the $group part"); fi
    done
  done
  if [[ "$part" == template ]]; then [[ "$(cat "$repoP/review-args" 2>/dev/null)" == "check $base" ]] || problems+=("the template part did not call review.sh check <merge base>")
  else [[ ! -e "$repoP/review-args" ]] || problems+=("part $part called review.sh"); fi
  ((${#problems[@]} == 0)) || printf '       | %s\n' "${problems[@]}"
  check "GATE_PART=$part runs exactly the $part steps" "$((${#problems[@]} == 0 ? 0 : 1))"
}
part_check static no no
part_check template yes no
part_check sandcastle no yes
part_check self no no
part_check review no no
RUN_PART=bogus run "$repoP"
check "an unknown GATE_PART is refused with exit 2 and runs nothing" "$([[ $status == 2 && $ran == no && $ran_sc == no ]] && grep -q "GATE_PART must be" "$repoP/out" && echo 0 || echo 1)"
for arg in --quick --sync; do
  RUN_PART=static GATE_ARGS=$arg run "$repoP"
  check "$arg with a GATE_PART is refused with exit 2" "$([[ $status == 2 ]] && grep -q "run no single part" "$repoP/out" && echo 0 || echo 1)"
done
GATE_NO_CACHE=1 STUB_SANDCASTLE_EXIT=1 RUN_PART=sandcastle run "$repoP"
check "a failing Sandcastle check fails the sandcastle part" "$([[ $status == 1 && $ran_sc == yes ]] && echo 0 || echo 1)"
GATE_NO_CACHE=1 STUB_EXIT=1 RUN_PART=template run "$repoP"
check "a failing test.sh fails the template part" "$([[ $status == 1 && $ran == yes ]] && echo 0 || echo 1)"

# The self part runs the gate's own test and the review's tests, which the other cases skip with GATE_SKIP_SELF_TEST=1.
repoS="$(new_repo parts-self)"; mkdir -p "$repoS/scripts/tests" "$repoS/node_modules/.bin"
printf '#!/usr/bin/env bash\ntouch "%s/.git/ran-self-test"\n' "$repoS" > "$repoS/scripts/tests/gate.test.sh"
printf '#!/usr/bin/env bash\necho "tsx $*" >> "%s/.git/tsx-args"\n' "$repoS" > "$repoS/node_modules/.bin/tsx"
chmod +x "$repoS/scripts/tests/gate.test.sh" "$repoS/node_modules/.bin/tsx"
status=0
(cd "$repoS" && env -u GATE_SKIP_SELF_TEST GATE_PART=self bash scripts/gate.sh >"$repoS/out" 2>&1) || status=$?
check "GATE_PART=self runs the gate's own test, and not the review's tests or any other part" "$([[ $status == 0 && -e $repoS/.git/ran-self-test && ! -e $repoS/.git/tsx-args ]] && ! grep -q "Copies of Template files" "$repoS/out" && [[ ! -e $repoS/$skill/ran && ! -e $repoS/.git/ran-sandcastle ]] && echo 0 || echo 1)"
rm -f "$repoS/.git/ran-self-test"
status=0
(cd "$repoS" && env -u GATE_SKIP_SELF_TEST GATE_PART=review bash scripts/gate.sh >"$repoS/out" 2>&1) || status=$?
check "GATE_PART=review runs the review's tests, and not the gate's own test or any other part" "$([[ $status == 0 && ! -e $repoS/.git/ran-self-test ]] && grep -q "review-ledger.test.mts" "$repoS/.git/tsx-args" && ! grep -q "Copies of Template files" "$repoS/out" && [[ ! -e $repoS/$skill/ran && ! -e $repoS/.git/ran-sandcastle ]] && echo 0 || echo 1)"
rm -f "$repoS/.git/tsx-args"
status=0
(cd "$repoS" && env -u GATE_SKIP_SELF_TEST GATE_SKIP_REVIEW=1 bash scripts/gate.sh --quick >"$repoS/out" 2>&1) || status=$?
check "a quick gate still runs both the gate's own test and the review's tests" "$([[ -e $repoS/.git/ran-self-test ]] && grep -q "review-ledger.test.mts" "$repoS/.git/tsx-args" 2>/dev/null && echo 0 || echo 1)"

# TEST_SH_SHARD (ADR 0013): only the template part, run by name, runs one shard of test.sh; a full gate runs all of it. Each shard's pass is remembered on its own.
shards_seen() { tail -n 1 "$repoP/.git/test-sh-shards" 2>/dev/null; }
reset_parts; rm -rf "$repoP/.git/gate-cache" "$repoP/.git/test-sh-shards"
RUN_PART=template RUN_SHARD=hooks run "$repoP"
check "GATE_PART=template with TEST_SH_SHARD runs that shard of test.sh" "$([[ $status == 0 && $ran == yes && "$(shards_seen)" == hooks ]] && grep -qF "test.sh (hooks shard)" "$repoP/out" && echo 0 || echo 1)"
RUN_PART=template RUN_SHARD=hooks run "$repoP"
check "a shard's pass is remembered for the clean tree" "$(grep -q "test.sh (hooks shard) already passed on this tree" "$repoP/out" && [[ "$(wc -l < "$repoP/.git/test-sh-shards")" == 1 ]] && echo 0 || echo 1)"
RUN_PART=template RUN_SHARD=scripts run "$repoP"
check "another shard is not skipped by the first shard's pass" "$([[ $status == 0 && "$(shards_seen)" == scripts ]] && echo 0 || echo 1)"
RUN_SHARD=hooks run "$repoP"
check "a full gate ignores TEST_SH_SHARD and runs all of test.sh, not skipped by a shard's pass" "$([[ $status == 0 && "$(shards_seen)" == unset ]] && ! grep -q "shard" "$repoP/out" && echo 0 || echo 1)"
RUN_PART=template run "$repoP"
check "the template part with no TEST_SH_SHARD shares the whole run's remembered pass, not a shard's" "$([[ $status == 0 ]] && grep -q "test.sh already passed on this tree" "$repoP/out" && echo 0 || echo 1)"
rm -f "$repoP/.git/test-sh-shards" "$repoP/$skill/ran"
GATE_NO_CACHE=1 RUN_PART=template run "$repoP"
check "the template part with no TEST_SH_SHARD runs all of test.sh" "$([[ $status == 0 && $ran == yes && "$(cat "$repoP/.git/test-sh-shards" 2>/dev/null)" == unset ]] && ! grep -q "shard" "$repoP/out" && echo 0 || echo 1)"
rm -rf "$repoP/.git/gate-cache"
STUB_EXIT=1 RUN_PART=template RUN_SHARD=checks run "$repoP"
check "a failing shard fails the template part" "$([[ $status == 1 && "$(shards_seen)" == checks ]] && echo 0 || echo 1)"

# A run of test.sh takes the trust from the passes it overlaps for the tree: a whole run from every shard's, a shard from the whole run's (Copilot's review of PR 65).
# Shards don't overlap each other, so one shard's failure leaves another's pass.
rm -rf "$repoP/.git/gate-cache" "$repoP/.git/test-sh-shards"
run "$repoP"; RUN_PART=template RUN_SHARD=scripts run "$repoP"
GATE_NO_CACHE=1 STUB_EXIT=1 RUN_PART=template RUN_SHARD=hooks run "$repoP"
check "a forced shard that fails fails the template part" "$([[ $status == 1 && "$(shards_seen)" == hooks ]] && echo 0 || echo 1)"
RUN_PART=template RUN_SHARD=scripts run "$repoP"
check "another shard's pass still stands: one shard's failure says nothing about it" "$([[ $status == 0 && "$(shards_seen)" == hooks ]] && grep -q "test.sh (scripts shard) already passed" "$repoP/out" && echo 0 || echo 1)"
rm -f "$repoP/$skill/ran"; run "$repoP"
check "and the next full gate runs all of test.sh instead of trusting the whole run's old pass" "$([[ $status == 0 && $ran == yes && "$(shards_seen)" == unset ]] && echo 0 || echo 1)"
RUN_PART=template RUN_SHARD=hooks run "$repoP"
GATE_NO_CACHE=1 STUB_EXIT=1 run "$repoP"
check "a forced full gate whose test.sh fails fails the gate" "$([[ $status == 1 && "$(shards_seen)" == unset ]] && echo 0 || echo 1)"
RUN_PART=template RUN_SHARD=hooks run "$repoP"
check "and the next run of a shard tests again instead of trusting its old pass" "$([[ $status == 0 && "$(shards_seen)" == hooks ]] && ! grep -q "already passed" "$repoP/out" && echo 0 || echo 1)"

# Two runs on one tree that overlap in time (Copilot's review of PR 65): a whole run that passes while a shard run, started after it, fails stores a pass that is not trusted.
rm -rf "$repoP/.git/gate-cache" "$repoP/.git/test-sh-shards"
# shellcheck disable=SC2016 # the command is run later by the stub, in the repo: its \$(...) must not expand here
overlap='GATE_PART=template TEST_SH_SHARD=hooks STUB_EXIT=1 GATE_NO_CACHE=1 bash scripts/gate.sh > "$(git rev-parse --git-common-dir)/overlap-out" 2>&1'
STUB_DURING="$overlap" run "$repoP"
check "a whole run that a failing shard run overlapped passes, saying its pass will not be trusted" "$([[ $status == 0 && "$(sed -n 2p "$repoP/.git/test-sh-shards")" == hooks ]] && grep -q "test.sh (hooks shard)" "$repoP/.git/overlap-out" && grep -q "another run of test.sh, or of a part it overlaps, started on this tree" "$repoP/out" && echo 0 || echo 1)"
rm -f "$repoP/$skill/ran"; run "$repoP"
check "and the next full gate runs test.sh instead of trusting that pass" "$([[ $status == 0 && $ran == yes && "$(shards_seen)" == unset ]] && ! grep -q "already passed" "$repoP/out" && echo 0 || echo 1)"
RUN_PART=template RUN_SHARD=hooks run "$repoP"
STUB_DURING="${overlap/STUB_EXIT=1/STUB_EXIT=0}" RUN_PART=template RUN_SHARD=scripts run "$repoP"
RUN_PART=template RUN_SHARD=scripts run "$repoP"
check "a shard run that another shard run overlapped in time keeps its pass: shards don't overlap" "$(grep -q "test.sh (scripts shard) already passed" "$repoP/out" && echo 0 || echo 1)"
if [[ "$(id -u)" != 0 ]]; then
  # A later run that can't write the whole's start stamp removes it, so the earlier run's pass is stored but not trusted, and it says so.
  rm -rf "$repoP/.git/gate-cache"
  # shellcheck disable=SC2016 # run later by the stub, in the repo
  STUB_DURING='chmod 444 "$(git rev-parse --git-common-dir)/gate-cache/"started.test-sh-*; '"$overlap" run "$repoP"
  check "a whole run overlapped by a failing shard run that could not write the whole's stamp says its pass will not be trusted" "$([[ $status == 0 ]] && grep -q "this pass will not be trusted" "$repoP/out" && echo 0 || echo 1)"
  rm -f "$repoP/$skill/ran"; run "$repoP"
  check "and the next full gate runs test.sh" "$([[ $ran == yes ]] && ! grep -q "test.sh already passed" "$repoP/out" && echo 0 || echo 1)"
else
  echo "skip - the unwritable-stamp case needs a user other than root"
fi
# A pass is trusted only while its token is still in its own stamp. A later run that overlaps it writes its own token there as it starts, so the pass stops being trusted
# at once, even if that run is then killed before it fails, and even for a gate that reads the pass before anything removes it.
repoT="$(new_repo cache-token)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repoT/.git/info/exclude"; commit_all "$repoT" base
treeT="$(git -C "$repoT" rev-parse 'HEAD^{tree}')"
run "$repoT"; run "$repoT"
check "a pass holding the token in its stamp is trusted" "$([[ $ran == no ]] && grep -q "test.sh already passed" "$repoT/out" && echo 0 || echo 1)"
echo later > "$repoT/.git/gate-cache/started.test-sh-$treeT"
run "$repoT"
check "a pass whose stamp a later run has taken (then killed, say) is not trusted" "$([[ $ran == yes ]] && ! grep -q "test.sh already passed" "$repoT/out" && echo 0 || echo 1)"
command rm -f "$repoT/.git/gate-cache/started.test-sh-$treeT"
run "$repoT"
check "a pass whose stamp is gone is not trusted" "$([[ $ran == yes ]] && echo 0 || echo 1)"
: > "$repoT/.git/gate-cache/test-sh-$treeT"
run "$repoT"
check "an empty pass file (the format before tokens) is not trusted" "$([[ $ran == yes ]] && echo 0 || echo 1)"
command rm -f "$repoT/.git/gate-cache/started.test-sh-$treeT"; : > "$repoT/.git/gate-cache/test-sh-$treeT"
run "$repoT"
check "an empty pass file with no stamp either is not trusted (empty is never a token)" "$([[ $ran == yes ]] && ! grep -q "test.sh already passed" "$repoT/out" && echo 0 || echo 1)"
if [[ "$(id -u)" != 0 ]]; then
  # A run that can't write its own stamp (a read-only stamp in a writable folder) removes it and stores no pass, saying so.
  echo earlier > "$repoT/.git/gate-cache/started.test-sh-$treeT"; chmod 444 "$repoT/.git/gate-cache/started.test-sh-$treeT"; command rm -f "$repoT/.git/gate-cache/test-sh-$treeT"
  run "$repoT"
  check "a run that can't write its own stamp stores no pass, saying it couldn't record it" "$([[ $status == 0 && $ran == yes && ! -e $repoT/.git/gate-cache/test-sh-$treeT && ! -e $repoT/.git/gate-cache/started.test-sh-$treeT ]] && grep -q "could not record the test.sh pass" "$repoT/out" && ! grep -q "will not be trusted" "$repoT/out" && echo 0 || echo 1)"
fi
# The whole run writes its own stamp before it lists the shard stamps: listed first, a shard run stamping in between would be missed, and the whole run's stamp, written
# after the shard's, would leave both passes trusted though each run may fail.
own_first="$(awk '/^run_cached\(\) \{/ { f = 1 } f && /stamp_write "\$label" "\$dir\/started\.\$name-\$tree"/ && !o { o = NR } f && /for s in "\$dir\/started\.\$whole\."\*/ && !g { g = NR } f && /^\}/ { f = 0 } END { print (o && g && o < g) ? 0 : 1 }' "$here/scripts/gate.sh")"
check "run_cached writes a run's own stamp before it lists the shard stamps" "$own_first"
RUN_PART=template RUN_SHARD=hooks run "$repoT"; run "$repoT"
RUN_PART=template RUN_SHARD=hooks run "$repoT"
check "a whole run takes the trust from a shard's pass" "$([[ $ran == yes ]] && ! grep -q "test.sh already passed" "$repoT/out" && echo 0 || echo 1)"
run "$repoT"; RUN_PART=template RUN_SHARD=scripts run "$repoT"; run "$repoT"
check "a shard run takes the trust from the whole run's pass" "$([[ $ran == yes ]] && ! grep -q "test.sh already passed" "$repoT/out" && echo 0 || echo 1)"
if [[ "$(id -u)" != 0 ]]; then
  # The same when the old pass files can't be removed (a read-only cache folder), so only the tokens decide.
  RUN_PART=template RUN_SHARD=hooks run "$repoT"
  chmod 555 "$repoT/.git/gate-cache"
  GATE_NO_CACHE=1 STUB_EXIT=1 run "$repoT"
  check "a forced whole run in a read-only cache folder holding a shard pass runs test.sh and fails with its status" "$([[ $status == 1 && $ran == yes && "$(tail -n 1 "$repoT/.git/test-sh-shards")" == unset ]] && ! grep -q "could not" "$repoT/out" && echo 0 || echo 1)"
  chmod 755 "$repoT/.git/gate-cache"
  RUN_PART=template RUN_SHARD=hooks run "$repoT"
  check "a forced whole run that failed takes the trust from a shard's pass it could not remove" "$([[ $ran == yes ]] && ! grep -q "already passed" "$repoT/out" && echo 0 || echo 1)"
  run "$repoT"
  chmod 555 "$repoT/.git/gate-cache"
  GATE_NO_CACHE=1 STUB_EXIT=1 RUN_PART=template RUN_SHARD=scripts run "$repoT"
  check "a forced shard run in a read-only cache folder holding the whole run's pass runs test.sh and fails with its status" "$([[ $status == 1 && $ran == yes && "$(tail -n 1 "$repoT/.git/test-sh-shards")" == scripts ]] && ! grep -q "could not" "$repoT/out" && echo 0 || echo 1)"
  chmod 755 "$repoT/.git/gate-cache"
  run "$repoT"
  check "a forced shard run that failed takes the trust from the whole run's pass it could not remove" "$([[ $ran == yes ]] && ! grep -q "test.sh already passed" "$repoT/out" && echo 0 || echo 1)"
fi
# The cache lock: the hit check and a run's stamp writes hold it, so no check reads a run's own stamp written and the stamps it overlaps not yet. lock_held <repo>: the
# messages a run gives when it can't take the lock (each such run waits about 2 seconds for it first).
repoL="$(new_repo cache-lock)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repoL/.git/info/exclude"; commit_all "$repoL" base
treeL="$(git -C "$repoL" rev-parse 'HEAD^{tree}')"; cacheL="$repoL/.git/gate-cache"
lock_held() { grep -q "the cache lock .*lock-$treeL has been held for over 2 seconds" "$1/out" && grep -q "could not record the test.sh\( (hooks shard)\)\? pass" "$1/out"; }
run "$repoL"; run "$repoL"
check "a run and a cache hit leave no cache lock behind" "$([[ $ran == no && ! -e $cacheL/lock-$treeL ]] && echo 0 || echo 1)"
# A shard run paused after writing its own stamp, before the whole run's: the whole pass's token is still in its stamp, so only the lock it holds keeps the pass untrusted.
echo shard-token > "$cacheL/started.test-sh.hooks-$treeL"
run "$repoL"
check "(a shard's own stamp alone leaves the whole pass trusted: the window the lock closes)" "$([[ $ran == no ]] && echo 0 || echo 1)"
mkdir "$cacheL/lock-$treeL"; run "$repoL"
check "a whole run doesn't trust its pass while a shard run holds the lock between its stamp writes: test.sh runs, stores no pass, and leaves the lock" "$([[ $status == 0 && $ran == yes && -d $cacheL/lock-$treeL ]] && lock_held "$repoL" && ! grep -q "test.sh already passed" "$repoL/out" && echo 0 || echo 1)"
rmdir "$cacheL/lock-$treeL"
# A whole run paused after writing its own stamp, before the shards': the shard pass's token is still in its stamp.
run "$repoL"; RUN_PART=template RUN_SHARD=hooks run "$repoL"
echo whole-token > "$cacheL/started.test-sh-$treeL"
RUN_PART=template RUN_SHARD=hooks run "$repoL"
check "(the whole run's own stamp alone leaves a shard pass trusted)" "$([[ $ran == no ]] && echo 0 || echo 1)"
mkdir "$cacheL/lock-$treeL"; RUN_PART=template RUN_SHARD=hooks run "$repoL"
check "a shard run doesn't trust its pass while a whole run holds the lock between its stamp writes: test.sh runs and stores no pass" "$([[ $status == 0 && $ran == yes && -d $cacheL/lock-$treeL ]] && lock_held "$repoL" && ! grep -q "test.sh (hooks shard) already passed" "$repoL/out" && echo 0 || echo 1)"
rmdir "$cacheL/lock-$treeL"
# The lock's tries, counted by a mkdir on PATH: it logs each try on a lock folder to $MKDIR_LOG and, with $MKDIR_FAIL_ONCE naming a file that doesn't exist yet, fails the
# first try without making the folder (a lock released just after a failed try); with $MKDIR_RELEASE naming a file that doesn't exist yet, it
# removes the lock folder 0.5 seconds after the first try (a holder that releases the lock while the run waits).
mkdir_shim="$work/mkdir-shim"; mkdir -p "$mkdir_shim"; real_mkdir="$(command -v mkdir)"
cat > "$mkdir_shim/mkdir" <<EOF
#!/usr/bin/env bash
for a in "\$@"; do
  if [[ "\$a" == */lock-* ]]; then
    [[ -z "\${MKDIR_LOG:-}" ]] || echo try >> "\$MKDIR_LOG"
    if [[ -n "\${MKDIR_FAIL_ONCE:-}" && ! -e "\$MKDIR_FAIL_ONCE" ]]; then : > "\$MKDIR_FAIL_ONCE"; exit 1; fi
    if [[ -n "\${MKDIR_RELEASE:-}" && ! -e "\$MKDIR_RELEASE" ]]; then : > "\$MKDIR_RELEASE"; (sleep 0.5; rmdir "\$a") >/dev/null 2>&1 & fi
  fi
done
exec "$real_mkdir" "\$@"
EOF
chmod +x "$mkdir_shim/mkdir"
# Each counted run is the template part alone: a full gate takes the lock again for the Sandcastle check.
tries() { if [[ -f "$work/mkdir-tries" ]]; then wc -l < "$work/mkdir-tries" | tr -d ' '; else echo 0; fi; }
run "$repoL"; run "$repoL"
command rm -f "$work/mkdir-tries" "$work/mkdir-failed"
RUN_PART=template PATH="$mkdir_shim:$PATH" MKDIR_LOG="$work/mkdir-tries" MKDIR_FAIL_ONCE="$work/mkdir-failed" run "$repoL"
check "a lock released just after a failed try is tried again and taken: the pass is trusted" "$([[ $status == 0 && $ran == no && "$(tries)" == 2 && ! -e $cacheL/lock-$treeL ]] && grep -q "test.sh already passed" "$repoL/out" && ! grep -q "the cache lock" "$repoL/out" && echo 0 || echo 1)"
mkdir "$cacheL/lock-$treeL"; command rm -f "$work/mkdir-released"
RUN_PART=template PATH="$mkdir_shim:$PATH" MKDIR_RELEASE="$work/mkdir-released" run "$repoL"
check "a lock released while a run waits for it is taken: the pass is trusted and no lock is left" "$([[ $status == 0 && $ran == no && ! -e $cacheL/lock-$treeL ]] && grep -q "test.sh already passed" "$repoL/out" && ! grep -q "the cache lock" "$repoL/out" && echo 0 || echo 1)"
mkdir "$cacheL/lock-$treeL"; command rm -f "$work/mkdir-tries"
RUN_PART=template PATH="$mkdir_shim:$PATH" MKDIR_LOG="$work/mkdir-tries" run "$repoL"
check "a lock that stays held is tried 20 times before the run goes on without it" "$([[ $status == 0 && $ran == yes && "$(tries)" == 20 ]] && lock_held "$repoL" && echo 0 || echo 1)"
rmdir "$cacheL/lock-$treeL"
if [[ "$(id -u)" != 0 ]]; then
  # A cache folder that can't hold a lock gives up after one try, saying nothing about a held lock, even with an old lock left in it (it didn't wait for one).
  run "$repoL"; chmod 555 "$cacheL"; command rm -f "$work/mkdir-tries"
  RUN_PART=template PATH="$mkdir_shim:$PATH" MKDIR_LOG="$work/mkdir-tries" run "$repoL"
  check "a lock that can't be made in a read-only cache folder is given up at once: test.sh runs, saying nothing of a held lock" "$([[ $status == 0 && $ran == yes && "$(tries)" == 1 ]] && ! grep -q "the cache lock" "$repoL/out" && echo 0 || echo 1)"
  chmod 755 "$cacheL"; mkdir "$cacheL/lock-$treeL"; chmod 555 "$cacheL"; command rm -f "$work/mkdir-tries"
  RUN_PART=template PATH="$mkdir_shim:$PATH" MKDIR_LOG="$work/mkdir-tries" run "$repoL"
  check "and one holding an old lock gives up at once too, without saying it waited for it" "$([[ $status == 0 && $ran == yes && "$(tries)" == 1 ]] && ! grep -q "the cache lock" "$repoL/out" && echo 0 || echo 1)"
  chmod 755 "$cacheL"; rmdir "$cacheL/lock-$treeL"
  # A pass file that can't be written after the lock and stamps were: the run passes and says the pass isn't recorded, and the next run tests again.
  # shellcheck disable=SC2016 # run later by the stub, in the repo
  STUB_DURING='chmod 555 "$(git rev-parse --git-common-dir)/gate-cache"' run "$repoL"
  chmod 755 "$cacheL"
  check "a pass file that can't be written leaves the run passing, saying its pass isn't recorded" "$([[ $status == 0 && $ran == yes && ! -e $cacheL/test-sh-$treeL ]] && grep -q "could not record the test.sh pass" "$repoL/out" && ! grep -q "the cache lock" "$repoL/out" && echo 0 || echo 1)"
  run "$repoL"
  check "so the next run tests again" "$([[ $ran == yes ]] && echo 0 || echo 1)"
fi
# A cache folder path that is a file, not a folder, can't hold a lock either: one try.
mv "$cacheL" "$cacheL.aside"; : > "$cacheL"; command rm -f "$work/mkdir-tries"
RUN_PART=template PATH="$mkdir_shim:$PATH" MKDIR_LOG="$work/mkdir-tries" run "$repoL"
check "a cache folder path that is a file gives the lock up at once" "$([[ $status == 0 && $ran == yes && "$(tries)" == 1 ]] && ! grep -q "the cache lock" "$repoL/out" && echo 0 || echo 1)"
command rm -f "$cacheL"; mv "$cacheL.aside" "$cacheL"
# A gate given INT, TERM or HUP while it holds the lock releases it and ends by that signal; one given any of them after it released the lock ends by it too, at once.
# signal_gate <repo> <signal> <marker> [VAR=value...]: runs the template part in <repo> with those variables, sends <signal> once <marker> exists, and prints how the gate
# ended: "signal N" or "exit N", then "prompt" or "late" (1.5 seconds or more after the signal), or "hung" when it hadn't ended 20 seconds later. Python starts it in its
# own process group with the three signals at their default (a shell's background job would ignore INT, which bash then can't trap), sends the signal to the whole group
# as a terminal's Ctrl-C does (bash waiting on a child goes on after an INT the child didn't die of), and tells an end by a signal from an exit.
signal_gate() {
  python3 -I - "$@" <<'PY'
import os, signal, subprocess, sys, time
repo, sig, marker, *pairs = sys.argv[1:]
env = dict(os.environ)
env.update(pair.split("=", 1) for pair in pairs)
def defaults():
    for s in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(s, signal.SIG_DFL)
with open(os.path.join(repo, "out"), "w") as out:
    p = subprocess.Popen(["bash", "scripts/gate.sh"], cwd=repo, env=env, stdout=out, stderr=subprocess.STDOUT, preexec_fn=defaults, start_new_session=True)
    for _ in range(400):
        if os.path.exists(marker) or p.poll() is not None:
            break
        time.sleep(0.05)
    sent = time.monotonic()
    if p.poll() is None:
        os.killpg(p.pid, getattr(signal, "SIG" + sig))
    try:
        rc = p.wait(timeout=20)
    except subprocess.TimeoutExpired:
        os.killpg(p.pid, signal.SIGKILL)
        p.wait()
        print("hung")
        sys.exit(0)
print((f"signal {-rc}" if rc < 0 else f"exit {rc}") + (" late" if time.monotonic() - sent >= 1.5 else " prompt"))
PY
}
# A cat on PATH stands in for the hit check's read of the pass: it writes to its mark whether the lock is held as it runs, and waits, so the signal comes while it is.
cat_shim="$work/cat-shim"; mkdir -p "$cat_shim"; real_cat="$(command -v cat)"
cat > "$cat_shim/cat" <<EOF
#!/usr/bin/env bash
last="\${!#}"
if [[ "\$last" == */gate-cache/test-sh-* && -n "\${CAT_MARK:-}" ]]; then
  lock="\${last%/*}/lock-\${last##*-}"
  if [[ -d "\$lock" ]]; then echo held > "\$CAT_MARK"; else echo free > "\$CAT_MARK"; fi
  sleep 2
fi
exec "$real_cat" "\$@"
EOF
chmod +x "$cat_shim/cat"
run "$repoL"
for sig in INT TERM HUP; do
  command rm -f "$work/cat-marked"
  ended="$(signal_gate "$repoL" "$sig" "$work/cat-marked" PATH="$cat_shim:$PATH" CAT_MARK="$work/cat-marked" GATE_SKIP_REVIEW=1 GATE_SKIP_SELF_TEST=1 GATE_PART=template)"
  check "a gate given $sig while it holds the cache lock releases it and ends by that signal" "$([[ "$(cat "$work/cat-marked" 2>/dev/null)" == held && ${ended% *} == "signal $(kill -l "$sig")" && ! -e $cacheL/lock-$treeL ]] && echo 0 || echo 1)"
done
# The stub test.sh runs after the lock is released: it marks that it runs and waits 3 seconds, so the signal comes then. A trap left set would hold the gate until the
# stub ended.
for sig in INT TERM HUP; do
  command rm -f "$work/stub-running"
  ended="$(signal_gate "$repoL" "$sig" "$work/stub-running" STUB_DURING=": > '$work/stub-running'; sleep 3" GATE_NO_CACHE=1 GATE_SKIP_REVIEW=1 GATE_SKIP_SELF_TEST=1 GATE_PART=template)"
  check "a gate given $sig while test.sh runs, after it released the lock, ends by that signal at once" "$([[ -e $work/stub-running && $ended == "signal $(kill -l "$sig") prompt" && ! -e $cacheL/lock-$treeL ]] && echo 0 || echo 1)"
done
# A stamp stuck as a folder can be neither written nor removed, even in a writable cache folder: the gate fails, and releases the lock it took.
command rm -f "$cacheL/started.test-sh-$treeL"; mkdir -p "$cacheL/started.test-sh-$treeL/stuck"
RUN_PART=template run "$repoL"
check "a run whose start stamp is stuck fails the gate and leaves no cache lock" "$([[ $status == 1 && $ran == no && ! -e $cacheL/lock-$treeL ]] && grep -q "could not write or remove the test.sh start stamp" "$repoL/out" && echo 0 || echo 1)"
command rm -rf "$cacheL/started.test-sh-$treeL"
locked_order="$(awk '/^run_cached\(\) \{/ { f = 1 } f && /cache_lock "\$dir\/lock-\$tree"/ && !l { l = NR } f && /\[\[ -n "\$cache_locked" && -f "\$cache"/ && !h { h = NR } f && /stamp_write "\$label"/ { w = NR } f && /^    cache_unlock$/ && !u { u = NR } f && /^  "\$@"$/ && !r { r = NR } f && /^\}/ { f = 0 } END { print (l && h && w && u && r && l < h && h < w && w < u && u < r) ? 0 : 1 }' "$here/scripts/gate.sh")"
check "run_cached takes the lock before the hit check, which needs it, and releases it after the last stamp write and before the command runs" "$locked_order"
if [[ "$(id -u)" != 0 ]]; then
  # A start stamp that can be neither written nor removed fails the gate before test.sh: an earlier run could otherwise keep its token and a pass this run disproves.
  repoW="$(new_repo cache-stamp-stuck)"; printf 'out\nreview-args\n%s/ran\n' "$skill" > "$repoW/.git/info/exclude"; commit_all "$repoW" base
  mkdir -p "$repoW/.git/gate-cache"; echo earlier > "$repoW/.git/gate-cache/started.test-sh-$(git -C "$repoW" rev-parse 'HEAD^{tree}')"
  chmod 444 "$repoW/.git/gate-cache/"started.*; chmod 555 "$repoW/.git/gate-cache"
  RUN_PART=template run "$repoW"
  check "a start stamp that can be neither written nor removed fails the gate before test.sh, saying so" "$([[ $status == 1 && $ran == no ]] && grep -q "could not write or remove the test.sh start stamp" "$repoW/out" && echo 0 || echo 1)"
  chmod 755 "$repoW/.git/gate-cache"
else
  echo "skip - the stuck-stamp case needs a user other than root"
fi

# test.sh's shards: every step sits in exactly one shard block, the blocks are the declared shards in order, and a bad shard is refused.
testsh_shards_ok() { # testsh_shards_ok <test.sh>: 0 when its steps are split into the shards it declares, each block once, nothing outside
  python3 -I - "$1" <<'PY'
import re, sys
lines = open(sys.argv[1]).read().split("\n")
declared = [m.group(1).split() for l in lines if (m := re.fullmatch(r"shards=\(([a-z ]+)\)", l))]
if len(declared) != 1:
    sys.exit(1)
order, current, steps = [], None, 0
for l in lines:
    m = re.fullmatch(r"if in_shard ([a-z]+); then", l)
    if m:
        if current is not None:
            sys.exit(1)
        current = m.group(1)
        order.append(current)
        continue
    m = re.fullmatch(r"fi # the ([a-z]+) shard", l)
    if m:
        if m.group(1) != current:
            sys.exit(1)
        current = None
        continue
    if l.startswith("step "):
        if current is None:
            sys.exit(1)
        steps += 1
if current is not None or order != declared[0] or steps == 0:
    sys.exit(1)
PY
}
testsh="$here/$skill/test.sh"
check "test.sh's steps are split into the shards it declares, each once, in order, none outside" "$(testsh_shards_ok "$testsh" && echo 0 || echo 1)"
mkdir -p "$work/testsh"
awk '{ print } /^fi # the checks shard$/ { print "step \"stray\"" }' "$testsh" > "$work/testsh/outside.sh"
awk '{ print } /^fi # the hooks shard$/ { print "step \"between\"" }' "$testsh" > "$work/testsh/between.sh"
sed '/^fi # the checks shard$/d' "$testsh" > "$work/testsh/open.sh"
sed '/^fi # the hooks shard$/d' "$testsh" > "$work/testsh/nested.sh"
sed 's/^fi # the hooks shard$/fi # the scripts shard/' "$testsh" > "$work/testsh/label.sh"
sed 's/^if in_shard checks; then$/if in_shard scripts; then/; s/^fi # the checks shard$/fi # the scripts shard/' "$testsh" > "$work/testsh/twice.sh"
sed 's/^shards=(hooks scripts checks)$/shards=(hooks scripts)/' "$testsh" > "$work/testsh/undeclared.sh"
for m in outside between open nested label twice undeclared; do
  check "the shard check fails for a test.sh where $m (the mutant differs from test.sh)" "$(! cmp -s "$testsh" "$work/testsh/$m.sh" && ! testsh_shards_ok "$work/testsh/$m.sh" && echo 0 || echo 1)"
done

# in_shard, taken out of test.sh: with no shard every block runs; with one, only its own.
inshard_rc() { # inshard_rc <shard> <block>: in_shard's status for <block> when TEST_SH_SHARD is <shard>
  bash -euo pipefail -c 'eval "$(grep -E "^in_shard\(\) " "$1")"; shard="$2"; in_shard "$3"' _ "$testsh" "$1" "$2" > /dev/null 2>&1 && echo 0 || echo 1
}
inshard_problems=()
for block in hooks scripts checks; do
  [[ "$(inshard_rc "" "$block")" == 0 ]] || inshard_problems+=("no shard skips $block")
  for s in hooks scripts checks; do
    want=1; [[ "$s" != "$block" ]] || want=0
    [[ "$(inshard_rc "$s" "$block")" == "$want" ]] || inshard_problems+=("shard $s on block $block")
  done
done
((${#inshard_problems[@]} == 0)) || printf '       | %s\n' "${inshard_problems[@]}"
check "in_shard runs every block with no shard, and only the named shard's block with one" "$((${#inshard_problems[@]} == 0 ? 0 : 1))"
mkdir -p "$work/testsh-bin"; for tool in dirname mktemp rm; do ln -sf "$(command -v "$tool")" "$work/testsh-bin/$tool"; done
for value in bogus "hooks scripts" "scripts checks" "hooks scripts checks" " hooks" "hooks " "*"; do
  status=0
  (PATH="$work/testsh-bin" TEST_SH_SHARD="$value" "$(command -v bash)" "$testsh") > "$work/testsh/bogus.out" 2>&1 || status=$?
  check "test.sh refuses TEST_SH_SHARD='$value' with exit 2, before checking its tools, and runs no step" "$([[ $status == 2 ]] && grep -q "TEST_SH_SHARD must be" "$work/testsh/bogus.out" && ! grep -q "^── " "$work/testsh/bogus.out" && echo 0 || echo 1)"
done

# A full gate runs the Sandcastle check only when the branch changed a file it reads (everything it applies and tests: the Template, the skill's references and ADRs, its rule).
sc_paths_yes=(
  "$skill/template/owned/.sandcastle/lib/x.mts" "$skill/template/owned/.github/workflows/x.yml" "$skill/template/seed/package.json" "$skill/template/seed/pnpm-lock.yaml"
  "$skill/apply.sh" "$skill/SKILL.md" "$skill/CONTEXT.md" "$skill/references/other.md" "$skill/docs/adr/0010-x.md" "$skill/tests/sandcastle/x.test.mts"
  "$skill/tests/sandcastle-check.test.sh" ".claude/rules/sandcastle.md"
)
sc_paths_no=("$skill/test.sh" "$skill/tests/other.sh" "$skill/github/repos.txt" "$skill/rollout.sh" ".claude/rules/other.md" "docs/other.md" "scripts/x.sh" "README.md")
reset_parts
GATE_NO_CACHE=1 run "$repoP"
check "a full gate on a branch that changed nothing skips the Sandcastle check, saying so" "$([[ $status == 0 && $ran == yes && $ran_sc == no ]] && grep -q "changed nothing Sandcastle's check reads" "$repoP/out" && echo 0 || echo 1)"
for changed in "${sc_paths_yes[@]}"; do
  reset_parts; touch_path "$changed"; commit_all "$repoP" "touch $changed"
  GATE_NO_CACHE=1 run "$repoP"
  check "a full gate runs the Sandcastle check when the branch changed ${changed#"$skill"/}" "$([[ $status == 0 && $ran_sc == yes ]] && echo 0 || echo 1)"
done
for changed in "${sc_paths_no[@]}"; do
  reset_parts; touch_path "$changed"; commit_all "$repoP" "touch $changed"
  GATE_NO_CACHE=1 run "$repoP"
  check "a full gate skips the Sandcastle check when the branch changed only ${changed#"$skill"/}, which it does not read" "$([[ $status == 0 && $ran_sc == no ]] && echo 0 || echo 1)"
done
# Changes git sees in other ways than a commit on the branch.
reset_parts; touch_path "$skill/references/sandcastle.md"
GATE_NO_CACHE=1 run "$repoP"
check "an uncommitted edit of a tracked file the check reads makes a full gate run it" "$([[ $status == 0 && $ran_sc == yes ]] && echo 0 || echo 1)"
reset_parts; echo "# new" > "$repoP/$skill/template/owned/new.txt"
GATE_NO_CACHE=1 run "$repoP"
check "an untracked file the check reads makes a full gate run it" "$([[ $status == 0 && $ran_sc == yes ]] && echo 0 || echo 1)"
reset_parts; mkdir -p "$repoP/docs"; git -C "$repoP" mv "$skill/references/sandcastle.md" docs/moved.md; commit_all "$repoP" "move it out"
GATE_NO_CACHE=1 run "$repoP"
check "a file moved out of a folder the check reads makes a full gate run it (the old path counts)" "$([[ $status == 0 && $ran_sc == yes ]] && echo 0 || echo 1)"
reset_parts; printf '# t\n' > "$repoP/$skill/references/é ü.md"; commit_all "$repoP" "non-ASCII name"
GATE_NO_CACHE=1 run "$repoP"
check "a file with non-ASCII characters in its name under a folder the check reads makes a full gate run it" "$([[ $status == 0 && $ran_sc == yes ]] && echo 0 || echo 1)"
reset_parts; touch_path "$skill/SKILL.md"; commit_all "$repoP" "skill"
rm -rf "$repoP/.git/gate-cache"
run "$repoP"; run "$repoP"
check "and the Sandcastle pass is remembered for the clean tree, as test.sh's is" "$([[ $ran_sc == no ]] && grep -q "Sandcastle check already passed on this tree" "$repoP/out" && echo 0 || echo 1)"
GATE_NO_CACHE=1 STUB_SANDCASTLE_EXIT=1 run "$repoP"
check "a failing Sandcastle check fails the full gate when it runs" "$([[ $status == 1 ]] && echo 0 || echo 1)"
repoQ="$(new_repo parts-noorigin)"; stub_review "$repoQ"; run "$repoQ"
check "with no origin/main to tell what changed, a full gate runs the Sandcastle check" "$([[ $status == 0 && $ran_sc == yes ]] && echo 0 || echo 1)"

# sandcastle_changed on its own: when it can't tell (a git that fails on diff or ls-files, no merge base) it says "changed", so the check runs.
sc_changed_rc() { # sc_changed_rc <repo> <PATH>: the function's status, taken out of gate.sh, run in <repo>
  (cd "$1" && PATH="$2" bash -euo pipefail -c 'source <(sed -n "/^sandcastle_changed() {/,/^}/p" "$1"); sandcastle_changed' _ "$here/scripts/gate.sh" > /dev/null 2>&1; echo $?)
}
reset_parts
check "sandcastle_changed says unchanged for a branch with no change in a watched path" "$([[ "$(sc_changed_rc "$repoP" "$PATH")" == 1 ]] && echo 0 || echo 1)"
wrapper_for sc-diff diff; wrapper_for sc-lsfiles ls-files; wrapper_for sc-mergebase merge-base
for pair in "sc-diff:git diff" "sc-lsfiles:git ls-files" "sc-mergebase:git merge-base"; do
  check "sandcastle_changed says changed when ${pair#*:} fails, so the check is not skipped on a guess" "$([[ "$(sc_changed_rc "$repoP" "$work/${pair%%:*}:$PATH")" == 0 ]] && echo 0 || echo 1)"
done

mkdir -p "$work/sc-grep"; printf '#!/usr/bin/env bash\nexit 2\n' > "$work/sc-grep/grep"; chmod +x "$work/sc-grep/grep"
check "sandcastle_changed says changed when grep itself fails (exit 2), so the check is not skipped on a guess" "$([[ "$(sc_changed_rc "$repoP" "$work/sc-grep:$PATH")" == 0 ]] && echo 0 || echo 1)"

# The workflow checks read YAML. PyYAML may be only in a user site or in yamllint's own environment, which python3 -I does not see, so pick an
# interpreter the way test.sh does: python3 -I when it imports yaml, else uvx with pyyaml.
yaml_python() { # yaml_python: prints the command that runs Python with PyYAML, one word per line; fails when there is none
  if python3 -I -c 'import yaml' &> /dev/null; then
    printf '%s\n' python3 -I
  elif command -v uvx > /dev/null; then
    printf '%s\n' uvx --with pyyaml python -I
  else
    echo "the workflow checks need PyYAML for python3, or uvx" >&2
    return 1
  fi
}
mkdir -p "$work/noyaml"
printf '#!/usr/bin/env bash\nexit 1\n' > "$work/noyaml/python3"
printf '#!/usr/bin/env bash\necho "uvx $*"\n' > "$work/noyaml/uvx"
chmod +x "$work/noyaml/python3" "$work/noyaml/uvx"
check "the workflow checks use uvx with pyyaml when python3 cannot import yaml" "$([[ "$(PATH="$work/noyaml:$PATH" yaml_python | tr '\n' ' ')" == "uvx --with pyyaml python -I " ]] && echo 0 || echo 1)"
rm "$work/noyaml/uvx"
check "the workflow checks fail, not pass, when there is no Python with PyYAML and no uvx" "$(PATH="$work/noyaml" yaml_python 2> /dev/null && echo 1 || echo 0)"
mapfile -t yaml_py < <(yaml_python)
[[ ${#yaml_py[@]} -gt 0 ]] || { echo "gate.test.sh: no Python with PyYAML and no uvx for the workflow checks" >&2; exit 1; }

# The CI workflow: one job per part, and the job the ruleset requires needs them all, so a part cannot go missing from CI without this test failing.
workflow_ok() { # workflow_ok <workflow file> <gate.sh> <test.sh>: 0 when its jobs run each GATE_PART once, through scripts/gate.sh, the template job every test.sh shard once,
  # and "Template tests" always runs and needs every one of them
  "${yaml_py[@]}" - "$1" "$2" "$3" <<'PY'
import re, sys, yaml
wf = yaml.safe_load(open(sys.argv[1]))
gate = open(sys.argv[2]).read()
shards = re.search(r'^shards=\(([a-z ]+)\)$', open(sys.argv[3]).read(), re.M)
if not shards:
    sys.exit(1)
shards = sorted(shards.group(1).split())
declared = re.search(r'"" \| ([a-z |]+)\) ;;', gate)
if not declared:
    sys.exit(1)
parts = sorted(declared.group(1).replace("|", " ").split())
jobs = wf["jobs"]
finals = [(k, j) for k, j in jobs.items() if j.get("name") == "Template tests"]
if len(finals) != 1:
    sys.exit(1)
final_key, final = finals[0]
used, part_jobs = [], []
for key, job in jobs.items():
    if key == final_key:
        continue
    if "if" in job or job.get("continue-on-error"):
        sys.exit(1)
    for step in job.get("steps", []):
        part = (step.get("env") or {}).get("GATE_PART")
        if part:
            if "scripts/gate.sh" not in str(step.get("run", "")) or "if" in step or step.get("continue-on-error"):
                sys.exit(1)
            used.append(part)
            part_jobs.append(key)
            env = step.get("env") or {}
            strategy = job.get("strategy") or {}
            if part == "template":
                matrix = strategy.get("matrix") or {}
                if set(matrix) != {"shard"} or not isinstance(matrix["shard"], list) or sorted(matrix["shard"]) != shards:
                    sys.exit(1)
                if str(env.get("TEST_SH_SHARD", "")).replace(" ", "") != "${{matrix.shard}}":
                    sys.exit(1)
                if strategy.get("fail-fast") is not False:
                    sys.exit(1)
            elif strategy or "TEST_SH_SHARD" in env:
                sys.exit(1)
if sorted(used) != parts or sorted(part_jobs) != sorted(set(part_jobs)):
    sys.exit(1)
needs = final.get("needs")
if not isinstance(needs, list) or sorted(needs) != sorted(part_jobs):
    sys.exit(1)
if str(final.get("if", "")).strip() != "${{ always() }}" or final.get("continue-on-error"):
    sys.exit(1)
PY
}
aggregator_ok() { # aggregator_ok <workflow file>: 0 when the Template tests step passes for five successes and fails for a failure, a cancelled or a skipped job, a missing job and nothing
  local script ok=0 case_json result job
  command -v jq > /dev/null || { echo "aggregator_ok needs jq" >&2; return 1; }
  script="$("${yaml_py[@]}" -c 'import sys, yaml; j = yaml.safe_load(open(sys.argv[1]))["jobs"]; print([x for x in j.values() if x.get("name") == "Template tests"][0]["steps"][-1]["run"])' "$1")" || return 1
  agg() { NEEDS="$1" bash -e -c "$script" > /dev/null 2>&1; }
  local all='{"self":{"result":"success"},"review":{"result":"success"},"static":{"result":"success"},"template":{"result":"success"},"sandcastle":{"result":"success"}}'
  agg "$all" || ok=1
  for result in failure cancelled skipped; do
    for job in self review static template sandcastle; do
      case_json="$(python3 -I -c 'import json, sys; d = json.loads(sys.argv[3]); d[sys.argv[1]]["result"] = sys.argv[2]; print(json.dumps(d))' "$job" "$result" "$all")"
      ! agg "$case_json" || ok=1
    done
  done
  for job in self review static template sandcastle; do
    case_json="$(python3 -I -c 'import json, sys; d = json.loads(sys.argv[1]); del d[sys.argv[2]]; print(json.dumps(d))' "$all" "$job")"
    ! agg "$case_json" || ok=1
  done
  ! agg '{}' || ok=1
  return "$ok"
}
wf="$here/.github/workflows/template-tests.yml"
check "the CI workflow has one job per GATE_PART, run through gate.sh, and a Template tests job that always runs and needs them all" "$(workflow_ok "$wf" "$here/scripts/gate.sh" "$here/$skill/test.sh" && echo 0 || echo 1)"
check "the Template tests step passes for five successes and fails for a failed, cancelled or skipped part, a missing part and no parts" "$(aggregator_ok "$wf" && echo 0 || echo 1)"
mkdir -p "$work/wf"
mutate_wf() { # mutate_wf <name> <python statement on the loaded workflow `wf`>: writes $work/wf/<name>.yml
  "${yaml_py[@]}" - "$wf" "$work/wf/$1.yml" "$2" <<'PY'
import sys, yaml
wf = yaml.safe_load(open(sys.argv[1]))
exec(sys.argv[3])
yaml.safe_dump(wf, open(sys.argv[2], "w"), sort_keys=False)
PY
}
mutate_wf same 'pass'
mutate_wf dup 'wf["jobs"]["sandcastle"]["steps"][-1]["env"]["GATE_PART"] = "static"'
mutate_wf needs 'wf["jobs"]["test"]["needs"].remove("sandcastle")'
# shellcheck disable=SC2016 # the Python source is text: the ${{ }} is the workflow's expression
mutate_wf always 'wf["jobs"]["test"]["if"] = "${{ success() }}"'
mutate_wf twice 'wf["jobs"]["self"]["steps"].append(dict(wf["jobs"]["self"]["steps"][-1]))'
mutate_wf coe 'wf["jobs"]["static"]["continue-on-error"] = True'
mutate_wf disabled 'wf["jobs"]["template"]["if"] = "false"'
mutate_wf norun 'wf["jobs"]["static"]["steps"][-1]["run"] = "true"'
mutate_wf twotests 'wf["jobs"]["self"]["name"] = "Template tests"'
mutate_wf noshard 'wf["jobs"]["template"]["strategy"]["matrix"]["shard"].remove("checks")'
mutate_wf dupshard 'wf["jobs"]["template"]["strategy"]["matrix"]["shard"].append("hooks")'
mutate_wf fixedshard 'wf["jobs"]["template"]["steps"][-1]["env"]["TEST_SH_SHARD"] = "hooks"'
mutate_wf include 'wf["jobs"]["template"]["strategy"]["matrix"]["include"] = [{"shard": "hooks"}]'
mutate_wf othershard 'wf["jobs"]["static"]["steps"][-1]["env"]["TEST_SH_SHARD"] = "hooks"'
mutate_wf nomatrix 'del wf["jobs"]["template"]["strategy"]'
mutate_wf noreview 'del wf["jobs"]["review"]; wf["jobs"]["test"]["needs"].remove("review")'
mutate_wf failfast 'del wf["jobs"]["template"]["strategy"]["fail-fast"]'
mutate_wf shardmap 'wf["jobs"]["template"]["strategy"]["matrix"]["shard"] = {"hooks": 1, "scripts": 1, "checks": 1}'
check "the workflow and aggregator checks pass for the workflow written back unchanged (the control for the mutants)" "$(workflow_ok "$work/wf/same.yml" "$here/scripts/gate.sh" "$here/$skill/test.sh" && aggregator_ok "$work/wf/same.yml" && echo 0 || echo 1)"
for m in dup needs always twice coe disabled norun twotests noshard dupshard fixedshard include othershard nomatrix noreview failfast shardmap; do
  check "the workflow check fails for a workflow where $m" "$(workflow_ok "$work/wf/$m.yml" "$here/scripts/gate.sh" "$here/$skill/test.sh" && echo 1 || echo 0)"
done
mutate_wf noe 'wf["jobs"]["test"]["steps"][-1]["run"] = wf["jobs"]["test"]["steps"][-1]["run"].replace("jq -e", "jq")'
mutate_wf any 'wf["jobs"]["test"]["steps"][-1]["run"] = wf["jobs"]["test"]["steps"][-1]["run"].replace("all(", "any(")'
mutate_wf skipped 'wf["jobs"]["test"]["steps"][-1]["run"] = wf["jobs"]["test"]["steps"][-1]["run"].replace(".result == \"success\"", ".result != \"failure\"")'
mutate_wf length 'wf["jobs"]["test"]["steps"][-1]["run"] = wf["jobs"]["test"]["steps"][-1]["run"].replace("length == 5", "length >= 0")'
for m in noe any skipped length; do
  check "the aggregator check fails for a Template tests step where $m" "$(aggregator_ok "$work/wf/$m.yml" && echo 1 || echo 0)"
done

# The edit-time lint (ADR 0011) is wired only by .claude/settings.json, and a typo in its matcher or command path disables the hook without a word.
settings_ok() { # settings_ok <repo root>: 0 when a PostToolUse hook for Edit|Write|MultiEdit runs the repo's executable scripts/lint-edited-file.sh
  # shellcheck disable=SC2016 # the Python source is text: $CLAUDE_PROJECT_DIR must not expand here
  python3 -I -c '
import json, os, sys
root = sys.argv[1]
try:
    settings = json.load(open(os.path.join(root, ".claude", "settings.json")))
    entries = settings["hooks"]["PostToolUse"]
except (OSError, ValueError, KeyError, TypeError):
    sys.exit(1)
want = "\"$CLAUDE_PROJECT_DIR\"/scripts/lint-edited-file.sh"
for entry in entries:
    if isinstance(entry, dict) and entry.get("matcher") == "Edit|Write|MultiEdit":
        for hook in entry.get("hooks", []):
            if isinstance(hook, dict) and hook.get("type") == "command" and hook.get("command") == want:
                sys.exit(0 if os.access(os.path.join(root, "scripts", "lint-edited-file.sh"), os.X_OK) else 1)
sys.exit(1)
' "$1"
}
check ".claude/settings.json runs scripts/lint-edited-file.sh after Edit, Write and MultiEdit" "$(settings_ok "$here" && echo 0 || echo 1)"
settings_root="$work/settings"; mkdir -p "$settings_root/.claude" "$settings_root/scripts"; touch "$settings_root/scripts/lint-edited-file.sh"; chmod +x "$settings_root/scripts/lint-edited-file.sh"
cp "$here/.claude/settings.json" "$settings_root/.claude/settings.json"
check "the settings check passes on a good file" "$(settings_ok "$settings_root" && echo 0 || echo 1)"
sed 's/Edit|Write|MultiEdit/Edit|Write/' "$here/.claude/settings.json" > "$settings_root/.claude/settings.json"
check "the settings check fails on a matcher that drops MultiEdit" "$(settings_ok "$settings_root" && echo 1 || echo 0)"
sed 's/lint-edited-file.sh/lint-edit.sh/' "$here/.claude/settings.json" > "$settings_root/.claude/settings.json"
check "the settings check fails on a command that names another script" "$(settings_ok "$settings_root" && echo 1 || echo 0)"
cp "$here/.claude/settings.json" "$settings_root/.claude/settings.json"; chmod -x "$settings_root/scripts/lint-edited-file.sh"
check "the settings check fails when the script is not executable" "$(settings_ok "$settings_root" && echo 1 || echo 0)"
chmod +x "$settings_root/scripts/lint-edited-file.sh" # restored, so every mutant below fails for its own reason
check "the settings check passes again once the script is executable" "$(settings_ok "$settings_root" && echo 0 || echo 1)"
python3 -I -c '
import json, sys
s = json.load(open(sys.argv[1]))
s["hooks"]["PostToolUse"].insert(0, {"matcher": "Bash", "hooks": [{"type": "command", "command": "true"}]})
json.dump(s, open(sys.argv[1], "w"))
' "$settings_root/.claude/settings.json"
check "the settings check passes when an unrelated PostToolUse entry comes first" "$(settings_ok "$settings_root" && echo 0 || echo 1)"
sed 's/PostToolUse/PreToolUse/' "$here/.claude/settings.json" > "$settings_root/.claude/settings.json"
check "the settings check fails on a hook registered for PreToolUse" "$(settings_ok "$settings_root" && echo 1 || echo 0)"
sed 's/"type": "command"/"type": "prompt"/' "$here/.claude/settings.json" > "$settings_root/.claude/settings.json"
check "the settings check fails on a hook that is not a command" "$(settings_ok "$settings_root" && echo 1 || echo 0)"
# shellcheck disable=SC2016 # sed's pattern is text: the $ must not expand
sed 's/\\"\$CLAUDE_PROJECT_DIR\\"/$CLAUDE_PROJECT_DIR/' "$here/.claude/settings.json" > "$settings_root/.claude/settings.json"
check "the settings check fails on a command without the quotes around CLAUDE_PROJECT_DIR" "$(settings_ok "$settings_root" && echo 1 || echo 0)"
printf '{' > "$settings_root/.claude/settings.json"
check "the settings check fails on a file that is not JSON" "$(settings_ok "$settings_root" && echo 1 || echo 0)"

echo
echo "gate.test.sh: $pass passed, $failed failed"
[[ $failed == 0 ]]
