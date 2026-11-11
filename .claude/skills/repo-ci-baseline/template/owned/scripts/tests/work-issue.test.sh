#!/usr/bin/env bash
# Tests for scripts/work-issue.sh, in a throwaway clone with a bare origin and a fake `gh`.
set -uo pipefail
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX
SCRIPTS="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'command rm -rf "$work"' EXIT
mkdir "$work/no-template"
export GIT_TEMPLATE_DIR="$work/no-template" GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@t
PASSED=0; FAILED=0
check() { if eval "$2"; then PASSED=$((PASSED + 1)); echo "ok   - $1"; else FAILED=$((FAILED + 1)); echo "FAIL - $1"; fi; }

mkdir "$work/bin"
cat > "$work/bin/gh" <<'GH'
#!/usr/bin/env bash
# gh issue view <n> --json title|labels --jq ...
n="$3"; what="$5"
case "$n:$what" in
  54:title) echo "[Feature] Start work in a worktree, review once!" ;;
  55:title) echo "Null children crash the   grid" ;;
  55:labels) echo bug ;;
  *:title) echo "" ;;
esac
exit 0
GH
chmod +x "$work/bin/gh"; export PATH="$work/bin:$PATH"
export GITHUB_REPOSITORY=o/r

git init -q --bare "$work/origin.git"
git clone -q "$work/origin.git" "$work/Repo" 2>/dev/null
cd "$work/Repo" || exit 1
git checkout -q -b main; mkdir scripts
cp "$SCRIPTS/check-branch-name.sh" "$SCRIPTS/work-issue.sh" scripts/
git add . && git commit -q -m init && git push -q origin main

p=$(bash scripts/work-issue.sh 54 2>/dev/null); st=$?
check "makes the worktree and prints its path" "[[ $st == 0 && '$p' == '$work/Repo-worktrees/feature-54-start-work-in-a-worktree-review' && -d '$p' ]]"
check "checks the branch out in it" "[[ \$(git -C '$p' branch --show-current) == feature/54-start-work-in-a-worktree-review ]]"
check "branches from origin/main" "[[ \$(git -C '$p' rev-parse HEAD) == \$(git rev-parse origin/main) ]]"
p2=$(cd "$p" && bash "$work/Repo/scripts/work-issue.sh" 54 2>/dev/null)
check "reuses it, also when run from the worktree" "[[ '$p2' == '$p' ]]"
p3=$(bash scripts/work-issue.sh 55 2>/dev/null)
check "a bug issue gets fix/ and a squeezed slug" "[[ '$p3' == '$work/Repo-worktrees/fix-55-null-children-crash-the-grid' ]]"
bash scripts/work-issue.sh 99 >/dev/null 2>&1; check "refuses an issue with no title" "[[ \$? != 0 ]]"
bash scripts/work-issue.sh abc >/dev/null 2>&1; check "refuses a non-number" "[[ \$? != 0 ]]"

echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
