#!/usr/bin/env bash
# Tests for scripts/work-issue.sh, in a throwaway clone with a bare origin and a fake `gh`.
# Usage: scripts/tests/work-issue.test.sh
#
# The fake gh answers `gh issue view <n> --json title|labels` from the table in its case statement; GH_REPO must be o/r.
# shellcheck disable=SC2034,SC2015 # out and o are read by the eval in check; the setup lines are meant as "or stop"
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
cat > "$work/bin/gh" <<'GH' # gh-repo: ok
#!/usr/bin/env bash
# gh issue view <n> --json title|labels --jq ...
[[ "${GH_REPO:-}" == "o/r" ]] || { echo "fake gh: GH_REPO=${GH_REPO:-}" >&2; exit 2; }
n="$3"; what="$5"
case "$n:$what" in
  54:title) echo "[Feature] Start work in a worktree, review once!" ;;
  55:title) echo "Null children crash the   grid" ;;
  55:labels) printf 'enhancement\nBug\n' ;;
  56:title) echo "bugfix" ;;
  56:labels) echo bugfix ;;
  57:title) echo "[Feature]" ;;
  58:title) echo "Café au lait" ;;
  59:title) echo "Один два три" ;;
  60:title) echo "$(printf 'a%.0s' $(seq 1 300)) tail" ;;
  61:title) echo "one two three four five" ;;
  62:title) echo "one two three four five six" ;;
  63:title) echo "one two three four five six seven" ;;
  64:title) echo "Reuse me" ;;
  65:title) echo "Started on another machine" ;;
  66:title) echo "Plain directory in the way" ;;
  *:title) echo "" ;;
esac
exit 0
GH
chmod +x "$work/bin/gh" # gh-repo: ok
export PATH="$work/bin:$PATH"
export GITHUB_REPOSITORY=o/r

git init -q --bare "$work/origin.git"
git clone -q "$work/origin.git" "$work/Repo" 2>/dev/null
cd "$work/Repo" || exit 1
git checkout -q -b main; mkdir scripts
cp "$SCRIPTS/check-branch-name.sh" "$SCRIPTS/work-issue.sh" scripts/
git add . && git commit -q -m init && git push -q origin main || { echo "setup failed"; exit 1; }
wt="$work/Repo-worktrees"

p=$(bash scripts/work-issue.sh 54 2>/dev/null); st=$?
check "makes the worktree and prints its path" "[[ $st == 0 && '$p' == '$wt/feature-54-start-work-in-a-worktree-review' && -d '$p' ]]"
check "checks the branch out in it" "[[ \$(git -C '$p' branch --show-current) == feature/54-start-work-in-a-worktree-review ]]"
check "branches from origin/main" "[[ \$(git -C '$p' rev-parse HEAD) == \$(git rev-parse origin/main) ]]"
p2=$(cd "$p" && bash "$work/Repo/scripts/work-issue.sh" 54 2>/dev/null)
check "reuses it, also when run from the worktree" "[[ '$p2' == '$p' ]]"
p3=$(bash scripts/work-issue.sh 55 2>/dev/null)
check "a Bug label (any case) gets fix/ and a squeezed slug" "[[ '$p3' == '$wt/fix-55-null-children-crash-the-grid' ]]"
p4=$(bash scripts/work-issue.sh 056 2>/dev/null)
check "a label that only contains bug is not bug" "[[ '$p4' == '$wt/feature-56-bugfix' ]]"
check "a leading-zero number is the same number" "[[ \$(bash scripts/work-issue.sh 054 2>/dev/null) == '$p' ]]"
check "a title that is only a tag gets the fallback slug" "[[ \$(bash scripts/work-issue.sh 57 2>/dev/null) == '$wt/feature-57-issue' ]]"
check "non-ASCII letters are word breaks" "[[ \$(bash scripts/work-issue.sh 58 2>/dev/null) == '$wt/feature-58-caf-au-lait' ]]"
check "a title with no ASCII letters gets the fallback slug" "[[ \$(bash scripts/work-issue.sh 59 2>/dev/null) == '$wt/feature-59-issue' ]]"
p60=$(bash scripts/work-issue.sh 60 2>/dev/null)
check "a very long word is cut to 50 characters" "[[ -d '$p60' && \$(basename '$p60' | sed 's/^feature-60-//' | wc -c) -le 51 ]]"
check "5 words keep all 5" "[[ \$(bash scripts/work-issue.sh 61 2>/dev/null) == '$wt/feature-61-one-two-three-four-five' ]]"
check "6 words keep all 6" "[[ \$(bash scripts/work-issue.sh 62 2>/dev/null) == '$wt/feature-62-one-two-three-four-five-six' ]]"
check "7 words keep 6" "[[ \$(bash scripts/work-issue.sh 63 2>/dev/null) == '$wt/feature-63-one-two-three-four-five-six' ]]"

# A local branch with no worktree gets one, on that branch
git branch feature/64-reuse-me origin/main
p64=$(bash scripts/work-issue.sh 64 2>/dev/null)
check "a local branch with no worktree is checked out in a new one" "[[ '$p64' == '$wt/feature-64-reuse-me' && \$(git -C '$p64' branch --show-current) == feature/64-reuse-me ]]"

# A branch already on origin is built from it, not from main
other="$work/other"; git clone -q "$work/origin.git" "$other" 2>/dev/null
git -C "$other" checkout -q -b feature/65-started-on-another-machine origin/main
echo remote > "$other/r" && git -C "$other" add r && git -C "$other" commit -q -m remote && git -C "$other" push -q origin feature/65-started-on-another-machine 2>/dev/null
p65=$(bash scripts/work-issue.sh 65 2>/dev/null)
check "a branch on origin is built from origin/<branch>" "[[ -f '$p65/r' && \$(git -C '$p65' rev-parse HEAD) == \$(git rev-parse origin/feature/65-started-on-another-machine) ]]"

# A directory in the way is an error and is left alone
mkdir -p "$wt/feature-66-plain-directory-in-the-way"; touch "$wt/feature-66-plain-directory-in-the-way/keep"
o=$(bash scripts/work-issue.sh 66 2>&1 >/dev/null); st=$?
check "a plain directory in the way is refused, and left alone" "[[ $st != 0 && -f '$wt/feature-66-plain-directory-in-the-way/keep' ]] && grep -q 'is not a worktree of' <<<\"\$o\" && ! git show-ref --verify --quiet refs/heads/feature/66-plain-directory-in-the-way"

o=$(bash scripts/work-issue.sh 99 2>&1 >/dev/null); st=$?
check "refuses an issue with no title" "[[ $st != 0 ]] && grep -q 'has no title' <<<\"\$o\""
o=$(bash scripts/work-issue.sh abc 2>&1 >/dev/null); st=$?
check "refuses a non-number" "[[ $st != 0 ]] && grep -q '^usage:' <<<\"\$o\""
o=$(bash scripts/work-issue.sh 1234567890 2>&1 >/dev/null); st=$?
check "refuses a number over 9 digits" "[[ $st != 0 ]] && grep -q '^usage:' <<<\"\$o\""
o=$(GITHUB_REPOSITORY=nope bash scripts/work-issue.sh 54 2>&1 >/dev/null); st=$?
check "refuses when it can't tell the repository" "[[ $st != 0 ]] && grep -q \"can't tell which repository\" <<<\"\$o\""
mv scripts/check-branch-name.sh "$work/cbn.sh"; o=$(bash scripts/work-issue.sh 54 2>&1 >/dev/null); st=$?; mv "$work/cbn.sh" scripts/check-branch-name.sh
check "refuses without check-branch-name.sh" "[[ $st != 0 ]] && grep -q 'check-branch-name.sh is missing' <<<\"\$o\""

echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
