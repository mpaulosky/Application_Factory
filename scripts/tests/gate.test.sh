#!/usr/bin/env bash
# Tests scripts/gate.sh against throwaway repos with a stub test.sh and a stub review.sh: matching copies run the stub and pass its exit
# status on; a changed, missing or execute-bit-mismatched copy stops the gate before the stub runs; the local review check (ADR 0010)
# skips only on GATE_SKIP_REVIEW=1, fails without an origin/main, and passes review.sh check's status on, ahead of test.sh. It also checks
# that gate.sh names each copied stand-alone test, that repo-config.mts lists the Factory's own files under Owned patterns, and that
# dependabot.yml watches the root package.json.
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
  (cd "$1" && env -u GATE_SKIP_REVIEW "${skip[@]+"${skip[@]}"}" GATE_SKIP_SELF_TEST=1 STUB_EXIT="${STUB_EXIT:-0}" REVIEW_EXIT="${REVIEW_EXIT:-0}" bash scripts/gate.sh >"$1/out" 2>&1) || status=$?
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
check "a pnpm that installs tsx gets the gate past the install step" "$(! grep -q 'did not install\|pnpm isn.t installed' "$repo/out" && grep -q 'Copies of Template files\|copies match' "$repo/out" && echo 0 || echo 1)"
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
