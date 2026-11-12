#!/usr/bin/env bash
# Tests for scripts/work-issue.sh, in a throwaway clone with a bare origin and a fake `gh`.
# Usage: scripts/tests/work-issue.test.sh
# Needs jq (a developer machine without it skips this file; CI fails), bash, git and chmod; the unsearchable-folder check is skipped as root, which can search any folder.
#
# The fake gh answers `gh issue view <n> --json title|labels --jq <expr>` from the file $GH_FIX/<n>.json ({"title", "labels": [{"name"}]}), and runs the --jq text work-issue.sh passes
# it through the real jq, as gh does. It fails unless it is given exactly those seven arguments, --json title or labels, and GH_REPO (o/r, or $EXPECT_GH_REPO); a missing file is an
# unknown issue (exit 1), and a file $GH_FIX/_fail makes it exit 1. A git shim answers `git remote get-url origin` from $SHIM_ORIGIN_URL, to try URL forms, and fails
# `git -C <dir> fetch` when SHIM_FETCH_FAIL is set, to try an origin that can't be fetched.
# shellcheck disable=SC2034 # o is read by the eval in check
set -uo pipefail
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX
SCRIPTS="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)" || exit 1
work="$(cd "$work" && pwd -P)" || exit 1 # the real path: git reports real paths, and /tmp or /var may be a symlink
[[ -n "$work" && -d "$work" ]] || exit 1
trap 'chmod -R u+rwx "$work/np" 2>/dev/null; command rm -rf "$work"' EXIT
mkdir "$work/no-template"
export GIT_TEMPLATE_DIR="$work/no-template" GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@t
export HOME="$work" GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 # a developer's own git config (signing, hooks, insteadOf) must not reach the throwaway repos
PASSED=0; FAILED=0
check() { if eval "$2"; then PASSED=$((PASSED + 1)); echo "ok   - $1"; else FAILED=$((FAILED + 1)); echo "FAIL - $1"; fi; }
# A command the suite calls that does not exist (a helper called before it is defined, say) would print one line and let the check go on: it fails the suite. A subshell inherits this
# function, so the name goes to a file; a child bash does not, which is what the scenarios that run the script with a bare PATH rely on.
not_found="$work/.command-not-found"
command_not_found_handle() { echo "FAIL - command not found: $1" >&2; echo "$1" >> "$not_found"; return 127; }

if ! command -v jq >/dev/null 2>&1; then # gh-repo: ok
  echo "jq is not installed, and the fake gh runs work-issue.sh's jq with it" # gh-repo: ok
  [[ -z "${CI:-}" ]] && { echo "skip"; exit 0; } # a developer machine may skip; CI must not
  exit 1
fi
export GH_FIX="$work/fix"; mkdir "$GH_FIX"
mkdir "$work/bin"
cat > "$work/bin/gh" <<'GH' # gh-repo: ok
#!/usr/bin/env bash
[[ $# == 7 && "${GH_REPO:-}" == "${EXPECT_GH_REPO:-o/r}" && "$1 $2" == "issue view" && "$4" == "--json" && ( "$5" == title || "$5" == labels ) && "$6" == "--jq" ]] \
  || { echo "fake gh: bad call: $* (GH_REPO=${GH_REPO:-})" >&2; exit 2; } # gh-repo: ok
[[ ! -f "$GH_FIX/_fail" ]] || { echo "fake gh: network down" >&2; exit 1; } # gh-repo: ok
[[ -f "$GH_FIX/$3.json" ]] || { echo "fake gh: no issue $3" >&2; exit 1; } # gh-repo: ok
jq -c "{$5: .$5}" "$GH_FIX/$3.json" | jq -r "$7"
GH
chmod +x "$work/bin/gh" # gh-repo: ok
export PATH="$work/bin:$PATH"
export GITHUB_REPOSITORY=o/r
issue() { local n="$1" t="$2"; shift 2; jq -nc --arg t "$t" '$ARGS.positional as $l | {title: $t, labels: [$l[] | {name: .}]}' --args "$@" > "$GH_FIX/$n.json"; } # issue <n> <title> [label...]
issue 54 "[Feature] Start work in a worktree, review once!"
issue 55 "Null children crash the   grid" enhancement Bug
issue 56 "bugfix" bugfix
issue 57 "[Feature]"
issue 58 "Café au lait"
issue 59 "Один два три"
issue 60 "$(printf 'a%.0s' $(seq 1 300)) tail"
issue 61 "one two three four five"
issue 62 "one two three four five six"
issue 63 "one two three four five six seven"
issue 64 "Reuse me"
issue 65 "Started on another machine"
issue 66 "Plain directory in the way"
issue 67 "Elsewhere"
issue 68 "$(printf 'a%.0s' $(seq 1 49)) bbb"
issue 69 "$(printf 'a%.0s' $(seq 1 50))"
issue 70 "$(printf 'a%.0s' $(seq 1 51))"
issue 71 "Locked and gone"
issue 73 "Original name"
issue 74 "Relative path"
issue 76 "Real title"
issue 77 "Extra arguments"
issue 78 "Fresh title"
issue 79 "Fresh title two"
issue 130 "New title"
issue 131 "New title"
issue 132 "Unrelated words"
issue 133 "Unrelated words"
issue 134 "Unrelated words"
issue 135 "Zeros title"
issue 136 "Origin tracking"
issue 137 "Always tracks"
issue 99 ""
issue 123456789 "Nine digits"

git init -q --bare -b main "$work/origin.git"
git clone -q "$work/origin.git" "$work/Repo" 2>/dev/null
cd "$work/Repo" || exit 1
git config fetch.prune false; git config remote.origin.prune false # a user's own config must not prune for the script: only its --prune may
git checkout -q -b main; mkdir scripts
cp "$SCRIPTS/check-branch-name.sh" "$SCRIPTS/work-issue.sh" scripts/
{ git add . && git commit -q -m init && git push -q origin main; } || { echo "setup failed"; exit 1; }
wt="$work/Repo-worktrees"
# A second clone, to push to origin as someone else would, without fetching in Repo
other="$work/other"; git clone -q "$work/origin.git" "$other" 2>/dev/null
other_push() { # other_push <branch> <file> [<start>]: a commit on <branch> (new from <start> or origin/main), pushed from the second clone
  git -C "$other" fetch -q origin && git -C "$other" checkout -q -B tmp "${3:-origin/$1}" && echo "$2" > "$other/$2" && git -C "$other" add "$2" \
    && git -C "$other" commit -q -m "$2" && git -C "$other" push -q origin "tmp:refs/heads/$1"
}

p=$(bash scripts/work-issue.sh 54 2>/dev/null); st=$?
check "makes the worktree and prints its path" "[[ $st == 0 && '$p' == '$wt/feature-54-start-work-in-a-worktree-review' && -d '$p' ]]"
check "checks the branch out in it" "[[ \$(git -C '$p' branch --show-current) == feature/54-start-work-in-a-worktree-review ]]"
check "branches from origin/main" "[[ \$(git -C '$p' rev-parse HEAD) == \$(git rev-parse origin/main) ]]"
p2=$(cd "$p" && bash "$work/Repo/scripts/work-issue.sh" 54 2>/dev/null)
check "reuses it, also when run from the worktree" "[[ '$p2' == '$p' ]]"
p3=$(bash scripts/work-issue.sh 55 2>/dev/null)
check "a Bug label (any case) gets fix/ and a squeezed slug" "[[ '$p3' == '$wt/fix-55-null-children-crash-the-grid' ]]"
# A worktree folder recreated or replaced after the worktree was deleted is not reused: one fixture for each way worktree_ok refuses
refused() { local o st; o=$(bash scripts/work-issue.sh 54 2>&1 >/dev/null); st=$?; check "$1" "[[ $st == 1 ]] && grep -q 'is not one now' <<<\"\$o\""; }
mv "$p/.git" "$work/saved-git"
refused "a registered worktree folder that lost its .git file is refused, not reused"
git init -q "$p" >/dev/null 2>&1; git -C "$p" symbolic-ref HEAD refs/heads/feature/54-start-work-in-a-worktree-review
refused "a folder replaced by another repository with the branch's name checked out is refused (only the common-dir check can)"
command rm -rf "$p/.git"; cp "$p3/.git" "$p/.git"
refused "a folder whose .git file points at another worktree of this clone is refused (only the HEAD check can)"
command rm -rf "$p/.git"; mv "$work/saved-git" "$p/.git"
p2=$(bash scripts/work-issue.sh 54 2>/dev/null)
check "once the folder is the worktree again it is reused" "[[ '$p2' == '$p' ]]"
p4=$(bash scripts/work-issue.sh 056 2>/dev/null)
check "a label that only contains bug is not bug" "[[ '$p4' == '$wt/feature-56-bugfix' ]]"
check "a leading-zero number is the same number" "[[ \$(bash scripts/work-issue.sh 054 2>/dev/null) == '$p' ]]"
check "a title that is only a tag gets the fallback slug" "[[ \$(bash scripts/work-issue.sh 57 2>/dev/null) == '$wt/feature-57-issue' ]]"
check "non-ASCII letters are word breaks" "[[ \$(bash scripts/work-issue.sh 58 2>/dev/null) == '$wt/feature-58-caf-au-lait' ]]"
check "a title with no ASCII letters gets the fallback slug" "[[ \$(bash scripts/work-issue.sh 59 2>/dev/null) == '$wt/feature-59-issue' ]]"
a50=$(printf 'a%.0s' $(seq 1 50)); a49=$(printf 'a%.0s' $(seq 1 49))
check "a very long word is cut to exactly 50 characters" "[[ \$(bash scripts/work-issue.sh 60 2>/dev/null) == '$wt/feature-60-$a50' ]]"
check "a cut that lands on a hyphen drops the hyphen" "[[ \$(bash scripts/work-issue.sh 68 2>/dev/null) == '$wt/feature-68-$a49' ]]"
check "a slug of exactly 50 characters is kept whole" "[[ \$(bash scripts/work-issue.sh 69 2>/dev/null) == '$wt/feature-69-$a50' ]]"
check "a slug of 51 characters is cut to 50" "[[ \$(bash scripts/work-issue.sh 70 2>/dev/null) == '$wt/feature-70-$a50' ]]"
check "a 9-digit number is accepted" "[[ \$(bash scripts/work-issue.sh 123456789 2>/dev/null) == '$wt/feature-123456789-nine-digits' ]]"
check "5 words keep all 5" "[[ \$(bash scripts/work-issue.sh 61 2>/dev/null) == '$wt/feature-61-one-two-three-four-five' ]]"
check "6 words keep all 6" "[[ \$(bash scripts/work-issue.sh 62 2>/dev/null) == '$wt/feature-62-one-two-three-four-five-six' ]]"
check "7 words keep 6" "[[ \$(bash scripts/work-issue.sh 63 2>/dev/null) == '$wt/feature-63-one-two-three-four-five-six' ]]"

# A local branch with no worktree gets one, on that branch
git branch feature/64-reuse-me origin/main
p64=$(bash scripts/work-issue.sh 64 2>/dev/null)
check "a local branch with no worktree is checked out in a new one" "[[ '$p64' == '$wt/feature-64-reuse-me' && \$(git -C '$p64' branch --show-current) == feature/64-reuse-me ]]"

# A worktree the branch already has anywhere is reused, and a folder deleted by hand is made again
git worktree add -q -b feature/67-elsewhere "$work/elsewhere" origin/main
check "a worktree at another path is reused, and nothing is made" "[[ \$(bash scripts/work-issue.sh 67 2>/dev/null) == '$work/elsewhere' && ! -e '$wt/feature-67-elsewhere' ]]"
command rm -rf "$p"
p54=$(bash scripts/work-issue.sh 54 2>/dev/null); st=$?
check "a worktree folder deleted by hand is made again" "[[ $st == 0 && '$p54' == '$p' && -d '$p' && \$(git -C '$p' branch --show-current) == feature/54-start-work-in-a-worktree-review ]]"

# Many worktrees: git writes its list in several chunks, and finding the primary checkout must not stop reading it, or git gets SIGPIPE under pipefail
# (a race, but the 120 worktrees make the old `awk ... exit` form fail on every run tried)
for i in $(seq 1 120); do git worktree add -q --detach "$work/many-worktrees-with-a-long-path-so-the-list-is-big/w$i" origin/main; done
check "finds the primary checkout among many worktrees" "[[ \$(bash scripts/work-issue.sh 54 2>/dev/null) == '$p' ]]"

# A missing folder whose worktree is locked is an error with a hint, and only its own entry is forgotten
git worktree add -q -b feature/71-locked-and-gone "$work/locked-and-gone" origin/main
git worktree add -q -b feature/72-other-stale "$work/other-stale" origin/main; command rm -rf "$work/other-stale"
git worktree lock "$work/locked-and-gone"; command rm -rf "$work/locked-and-gone"
o=$(bash scripts/work-issue.sh 71 2>&1 >/dev/null); st=$?
check "a missing but locked worktree is refused with a hint" "[[ $st != 0 ]] && grep -q 'missing but locked' <<<\"\$o\""
command rm -rf "$p"; bash scripts/work-issue.sh 54 >/dev/null 2>&1
check "forgetting one stale entry leaves the other stale entries alone" "[[ -d '$p' && -d '$work/Repo/.git/worktrees/other-stale' && -d '$work/Repo/.git/worktrees/locked-and-gone' ]]"

# A renamed issue keeps its branch
p73=$(bash scripts/work-issue.sh 73 2>/dev/null); issue 73 "A completely different title"
check "an issue renamed since its branch was made keeps the branch and its worktree" "[[ '$p73' == '$wt/feature-73-original-name' && \$(bash scripts/work-issue.sh 73 2>/dev/null) == '$p73' && ! -e '$wt/feature-73-a-completely-different-title' ]]"

# A worktree whose gitdir file holds a relative path (what git 2.48+ writes with worktree.useRelativePaths), written by hand so the test does not depend on git's version;
# its folder deleted by hand, it is forgotten and made again
git worktree add -q -b feature/74-relative-path "$work/relative-path" origin/main
printf '%s\n' "../../../../relative-path/.git" > "$work/Repo/.git/worktrees/relative-path/gitdir"
command rm -rf "$work/relative-path"
wlist="$(git worktree list --porcelain)" # no pipe: grep -q may close it early and give git SIGPIPE
if grep -qx "worktree $work/relative-path" <<<"$wlist"; then
  check "a missing worktree stored with a relative path is found, forgotten and made again" "[[ \$(bash scripts/work-issue.sh 74 2>/dev/null) == '$wt/feature-74-relative-path' && -d '$wt/feature-74-relative-path' ]]"
elif [[ -n "${CI:-}" ]]; then check "this git can't read a worktree stored with a relative path, and CI must test it" "false"
else echo "skip - this git can't read a relative worktree path"; fi

# A branch already on origin is built from it, not from main
other_push feature/65-started-on-another-machine r origin/main 2>/dev/null
p65=$(bash scripts/work-issue.sh 65 2>/dev/null)
check "a branch on origin is built from origin/<branch>" "[[ -f '$p65/r' && \$(git -C '$p65' rev-parse HEAD) == \$(git rev-parse origin/feature/65-started-on-another-machine) ]]"
check "a branch made from origin/main has no upstream (it would track main, and a plain git push would then refuse)" "[[ -z \"\$(git config --get branch.feature/54-start-work-in-a-worktree-review.merge)\" && -z \"\$(git config --get branch.feature/54-start-work-in-a-worktree-review.remote)\" ]]"
check "a branch made from origin/<branch> tracks that branch" "[[ \"\$(git config --get branch.feature/65-started-on-another-machine.merge)\" == refs/heads/feature/65-started-on-another-machine ]]"

# A branch for this number outside the branch standard (or under another prefix) is not the issue's branch: it is skipped, local or on origin
git branch feature/76-WIP origin/main; git branch squad/76-legacy origin/main; git branch dependabot/76-bump origin/main; other_push fix/76-a/b n76 origin/main 2>/dev/null
check "the strays exist (the push of the nested one worked)" "git show-ref --verify --quiet refs/heads/feature/76-WIP && git ls-remote --exit-code origin refs/heads/fix/76-a/b >/dev/null"
o=$(bash scripts/work-issue.sh 76 2>/dev/null); st=$?
check "branches for the number that break the standard or use another prefix are skipped" "[[ $st == 0 && '$o' == '$wt/feature-76-real-title' && \$(git -C '$o' branch --show-current) == feature/76-real-title ]]"

# The upstream is set explicitly, whatever branch.autoSetupMerge says
other_push feature/136-origin-tracking n136 origin/main; git config branch.autoSetupMerge false
p136=$(bash scripts/work-issue.sh 136 2>/dev/null); git config --unset branch.autoSetupMerge
check "a branch built from origin/<branch> tracks it even when branch.autoSetupMerge is false" "[[ \"\$(git config --get branch.feature/136-origin-tracking.merge)\" == refs/heads/feature/136-origin-tracking && -f '$p136/n136' ]]"
git config branch.autoSetupMerge always
bash scripts/work-issue.sh 137 >/dev/null 2>&1; git config --unset branch.autoSetupMerge
check "a branch made from origin/main has no upstream even when branch.autoSetupMerge is always" "[[ -z \"\$(git config --get branch.feature/137-always-tracks.merge)\" && -d '$wt/feature-137-always-tracks' ]]"

# A stray before a valid branch is passed over to it; an origin-only stray alone is skipped too
git branch feature/78-WIP origin/main; git branch feature/78-old-title origin/main
check "the search goes on past a stray branch to a valid one that sorts after it" "[[ \$(bash scripts/work-issue.sh 78 2>/dev/null) == '$wt/feature-78-old-title' ]]"
other_push fix/79-a/b n79 origin/main
check "the origin-only stray exists" "git ls-remote --exit-code origin refs/heads/fix/79-a/b >/dev/null"
check "an origin-only branch outside the standard is skipped, and the title's own branch is made" "[[ \$(bash scripts/work-issue.sh 79 2>/dev/null) == '$wt/feature-79-fresh-title-two' ]]"
# Two valid branches for one number: the one the title gives wins, wherever it sorts
git branch feature/130-aaa-old origin/main; git branch feature/130-new-title origin/main
check "when the branch the title gives exists, it wins over another valid branch for the number" "[[ \$(bash scripts/work-issue.sh 130 2>/dev/null) == '$wt/feature-130-new-title' ]]"

# The title's branch on origin only still wins over a valid local branch that sorts before it
git branch feature/131-aaa-old origin/main; other_push feature/131-new-title n131 origin/main
p131=$(bash scripts/work-issue.sh 131 2>/dev/null)
check "the title's branch that exists only on origin wins over a valid local branch for the number, and is built from origin" "[[ '$p131' == '$wt/feature-131-new-title' && -f '$p131/n131' ]]"
# With no branch of the title's name: the first valid branch in name order, local before origin
git branch feature/132-b-second origin/main; git branch feature/132-a-first origin/main
check "of two valid branches for one number, neither the title's, the first in name order is taken" "[[ \$(bash scripts/work-issue.sh 132 2>/dev/null) == '$wt/feature-132-a-first' ]]"
git branch fix/133-local origin/main; other_push feature/133-origin n133 origin/main
check "a local branch is taken before an origin one, whatever their names" "[[ \$(bash scripts/work-issue.sh 133 2>/dev/null) == '$wt/fix-133-local' ]]"
git branch fix/134-x origin/main; git branch feature/134-x origin/main
check "of local branches for one number, feature/ is taken before fix/ (name order)" "[[ \$(bash scripts/work-issue.sh 134 2>/dev/null) == '$wt/feature-134-x' ]]"
git branch feature/0135-zeros origin/main
check "a branch whose number has leading zeros is not the issue's: the title's own branch is made" "[[ \$(bash scripts/work-issue.sh 135 2>/dev/null) == '$wt/feature-135-zeros-title' ]]"

# A directory in the way is an error and is left alone
mkdir -p "$wt/feature-66-plain-directory-in-the-way"; touch "$wt/feature-66-plain-directory-in-the-way/keep"
o=$(bash scripts/work-issue.sh 66 2>&1 >/dev/null); st=$?
check "a plain directory in the way is refused, and left alone" "[[ $st != 0 && -f '$wt/feature-66-plain-directory-in-the-way/keep' ]] && grep -q 'is not a worktree of' <<<\"\$o\" && ! git show-ref --verify --quiet refs/heads/feature/66-plain-directory-in-the-way"

# Origin is fetched first (and pruned): the base is its main as it is now, and a branch pushed from another machine, under an older title, is seen
other_push main fresh-main origin/main
check "a new branch starts from origin/main as it is now, not as it was at the last fetch" "[[ \$(git -C \"\$(bash scripts/work-issue.sh 54 2>/dev/null >/dev/null; issue 80 'Fresh base'; bash scripts/work-issue.sh 80 2>/dev/null)\" rev-parse HEAD) == \$(git ls-remote origin refs/heads/main | cut -f1) ]]"
other_push feature/75-old-name n75 origin/main; issue 75 "New name"
check "an issue renamed on another machine keeps the branch pushed from there (feature/)" "[[ \$(bash scripts/work-issue.sh 75 2>/dev/null) == '$wt/feature-75-old-name' && ! -e '$wt/feature-75-new-name' && -f '$wt/feature-75-old-name/n75' ]]"
other_push fix/81-old-fix n81 origin/main; issue 81 "Totally new words" Bug
other_push hotfix/82-old-hot n82 origin/main; issue 82 "Other new words"
check "a renamed issue keeps a fix/ branch" "[[ \$(bash scripts/work-issue.sh 81 2>/dev/null) == '$wt/fix-81-old-fix' ]]"
check "a renamed issue keeps a hotfix/ branch" "[[ \$(bash scripts/work-issue.sh 82 2>/dev/null) == '$wt/hotfix-82-old-hot' ]]"
issue 5 "Five"
check "issue 5 does not pick up issue 54's branch" "[[ \$(bash scripts/work-issue.sh 5 2>/dev/null) == '$wt/feature-5-five' ]]"
git branch fix/90-local-name origin/main; other_push feature/90-origin-name n90 origin/main; issue 90 "Anything"
check "with a local and an origin branch for one number, the local one wins (it sorts after the origin one by short name, so it is local-first, not name order alone)" "[[ \$(bash scripts/work-issue.sh 90 2>/dev/null) == '$wt/fix-90-local-name' ]]"
other_push feature/91-gone n91 origin/main; git fetch -q origin; git -C "$other" push -q origin --delete feature/91-gone; issue 91 "Fresh start"
check "an origin branch deleted since the last fetch is pruned, not reused" "[[ \$(bash scripts/work-issue.sh 91 2>/dev/null) == '$wt/feature-91-fresh-start' && \$(git -C '$wt/feature-91-fresh-start' rev-parse HEAD) == \$(git rev-parse origin/main) ]]"
issue 83 "From a worktree"
check "makes a new worktree when run from inside another one" "[[ \$(cd '$p65' && bash '$work/Repo/scripts/work-issue.sh' 83 2>/dev/null) == '$wt/feature-83-from-a-worktree' ]]"

# The repository when GITHUB_REPOSITORY is not set (the usual case on a developer machine): upstream's URL, else origin's
mkdir "$work/shim"
cat > "$work/shim/git" <<SHIM
#!/usr/bin/env bash
if [[ "\$1 \$2 \$3" == "remote get-url origin" && -n "\${SHIM_ORIGIN_URL:-}" ]]; then echo "\$SHIM_ORIGIN_URL"; exit 0; fi
if [[ "\$1" == -C && "\$3" == fetch && -n "\${SHIM_FETCH_FAIL:-}" ]]; then echo "shim: fetch failed" >&2; exit 128; fi
exec "$(command -v git)" "\$@"
SHIM
chmod +x "$work/shim/git"
issue 84 "From url"; issue 85 "From upstream"; issue 86 "No repository"
check "with no GITHUB_REPOSITORY, the repository is read from origin's URL" "[[ \$(env -u GITHUB_REPOSITORY EXPECT_GH_REPO=acme/r PATH='$work/shim:'\"\$PATH\" SHIM_ORIGIN_URL=git@github.com:acme/r.git bash scripts/work-issue.sh 84 2>/dev/null) == '$wt/feature-84-from-url' ]]"
git remote add upstream https://github.com/up/r.git
check "an upstream remote, not origin, names the repository gh is asked about" "[[ \$(env -u GITHUB_REPOSITORY EXPECT_GH_REPO=up/r PATH='$work/shim:'\"\$PATH\" SHIM_ORIGIN_URL=git@github.com:acme/r.git bash scripts/work-issue.sh 85 2>/dev/null) == '$wt/feature-85-from-upstream' ]]" # gh-repo: ok
git remote remove upstream
o=$(env -u GITHUB_REPOSITORY PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://git.example.com/acme/r bash scripts/work-issue.sh 86 2>&1 >/dev/null); st=$?
check "a non-GitHub origin with no GITHUB_REPOSITORY fails, saying why" "[[ $st != 0 ]] && grep -q \"can't tell which repository\" <<<\"\$o\" && [[ ! -e '$wt/feature-86-no-repository' ]]"

# slug_of, the same function in work-issue.sh and cleanup.sh: read from each script and run on its own, so a wrong group index, a loosened regex or a lost case-insensitivity in either copy fails here
slug_fn() { awk '/^slug_of\(\) \{/{p=1} p{print} p&&/^}/{exit}' "$1"; }
check "slug_of is the same function in work-issue.sh and cleanup.sh" "[[ -n \"\$(slug_fn $SCRIPTS/work-issue.sh)\" && \"\$(slug_fn $SCRIPTS/work-issue.sh)\" == \"\$(slug_fn $SCRIPTS/cleanup.sh)\" ]]"
for loc in C C.utf8 en_US.utf8; do
  if [[ "$loc" != C ]] && [[ "$(LC_ALL=$loc locale charmap 2>/dev/null)" != UTF-8 ]]; then
    if [[ -n "${CI:-}" && "$loc" == C.utf8 ]]; then FAILED=$((FAILED + 1)); echo "FAIL - locale C.utf8 is missing, and CI must test slug_of in a UTF-8 locale"; else echo "skip - locale $loc is not installed"; fi; continue
  fi
  SLUG_LOCALE=$loc
  for script in work-issue cleanup; do
    # shellcheck disable=SC2016 # the bash -c text is expanded by the inner bash, on purpose
    slug_of_run() { env -u LC_ALL -u LC_CTYPE -u LC_COLLATE -u LC_NUMERIC -u LC_MESSAGES LANG="$SLUG_LOCALE" bash -c 'set -euo pipefail; eval "$1"; slug_of "$2"' _ "$(slug_fn "$SCRIPTS/$script.sh")" "$1"; }
    for pair in "https://GitHub.com/Acme/r|Acme/r" "HTTPS://github.com/acme/r.git/|acme/r" "ssh://git@SSH.GitHub.com:443/acme/r|acme/r" "https://www.github.com/acme/r/|acme/r" "https://x:y@github.com/acme/r.git/|acme/r" \
      "https://us%40er:p~w@github.com/ac-me/r_1.x|ac-me/r_1.x" "git://github.com/acme/r|acme/r" "git@github.com:Acme/R.git|Acme/R" "github.com:acme/r|acme/r" "www.github.com:acme/r|acme/r" "x@ssh.github.com:acme/r|acme/r"; do
      check "$script [$loc]: slug_of reads ${pair%%|*} as ${pair##*|}" "[[ \"\$(slug_of_run '${pair%%|*}')\" == '${pair##*|}' ]]"
    done
    check "$script [$loc]: slug_of exits 0 on a URL it does not read (the scripts run under set -e)" "slug_of_run https://gitlab.com/acme/r >/dev/null"
    for bad in "https://evil.com#@github.com/acme/r" "https://evil.com?@github.com/acme/r" "evil.com:x@github.com:acme/r" "x:@github.com:acme/r" "https://github.com:/acme/r" "https://github.com/foo/acme/r" "https://github.com/acme/r/extra" "https://github.com/acme/r?x=1" \
      "https://github.com/" "https://ssh.github.com.evil.com/acme/r" "https://evil.com/github.com/acme/r" "file:///github.com/acme/r" "https://[::1]/acme/r" "git@github.com:/acme/r" "git@github.com:acme" "https://github.com/acme /r" "https://github.com/acme/r x" "https://github.com:22x/acme/r" "https://gitlab.com/acme/r" "/srv/acme/r" "https://gİthub.com/acme/r" "https://github.com/acmé/r" ""; do
      check "$script [$loc]: slug_of reads nothing from '$bad'" "[[ -z \"\$(slug_of_run '$bad')\" ]]"
    done
  done
done

# The C-locale lines are pinned in the text too, so a runner without a UTF-8 locale still fails when one is dropped
for script in work-issue cleanup; do
  check "$script: slug_of declares local LC_ALL=C (the regex ranges are ASCII only)" "slug_fn $SCRIPTS/$script.sh | grep -q 'local LC_ALL=C '"
  check "$script: slug_of's tr is started as LC_ALL=C tr" "slug_fn $SCRIPTS/$script.sh | grep -q 'LC_ALL=C tr '"
done
# slug_of's tr runs with LC_ALL=C whatever locale the caller has: a tr shim logs the LC_ALL it is started with (GNU tr is byte-wise, so only the environment can be pinned)
real_tr="$(command -v tr)"; mkdir "$work/trshim"
# shellcheck disable=SC2016 # the shim's own text: ${LC_ALL-unset} is expanded when the shim runs
printf '#!/usr/bin/env bash\necho "${LC_ALL-unset}" >> "$TR_LOG"\nexec %q "$@"\n' "$real_tr" > "$work/trshim/tr"; chmod +x "$work/trshim/tr"
for script in work-issue cleanup; do
  for loc in unset C.utf8; do
    : > "$work/tr.log"
    lc_env=(-u LC_ALL); [[ "$loc" == unset ]] || lc_env=(LC_ALL=C.utf8)
    # shellcheck disable=SC2016 # the bash -c text is expanded by the inner bash, on purpose
    env -u LC_CTYPE -u LC_COLLATE -u LC_NUMERIC -u LC_MESSAGES "${lc_env[@]}" TR_LOG="$work/tr.log" PATH="$work/trshim:$PATH" LANG=C.utf8 bash -c 'eval "$1"; slug_of https://GitHub.com/Acme/r >/dev/null' _ "$(slug_fn "$SCRIPTS/$script.sh")"
    check "$script: slug_of starts tr with LC_ALL=C (the caller's LC_ALL: $loc)" "[[ -s '$work/tr.log' && \"\$(sort -u '$work/tr.log')\" == C ]]"
  done
done

# The branch checked out in the primary checkout is not a place to work
issue 92 "In primary"; git checkout -q -b feature/92-in-primary
o=$(bash scripts/work-issue.sh 92 2>&1 >/dev/null); st=$?; git checkout -q main
check "refuses a branch that is checked out in the primary checkout" "[[ $st != 0 ]] && grep -q 'checked out in the primary checkout' <<<\"\$o\" && [[ ! -e '$wt/feature-92-in-primary' ]]"

# A folder that can't be reached is not a folder deleted by hand; an entry that is a symlink is not followed
issue 93 "Unreachable"; mkdir "$work/np"; git worktree add -q -b feature/93-unreachable "$work/np/w" origin/main
if [[ "$(id -u)" != 0 ]]; then
  chmod 000 "$work/np"; o=$(bash scripts/work-issue.sh 93 2>&1 >/dev/null); st=$?; chmod 755 "$work/np"
  check "a worktree whose parent folder can't be searched is refused, and its entry kept" "[[ $st != 0 ]] && grep -q \"can't be reached\" <<<\"\$o\" && [[ -d '$work/Repo/.git/worktrees/w' && -d '$work/np/w' ]]"
else echo "skip - running as root, so no folder can be made unsearchable (the empty and missing parents are tried below)"; fi
issue 94 "Symlinked"; git worktree add -q -b feature/94-symlinked "$work/sl" origin/main; command rm -rf "$work/sl"
mv "$work/Repo/.git/worktrees/sl" "$work/elsewhere-entry"; ln -s "$work/elsewhere-entry" "$work/Repo/.git/worktrees/sl"
o=$(bash scripts/work-issue.sh 94 2>&1 >/dev/null); st=$?
check "a worktree entry that is a symlink is never followed or removed" "[[ $st != 0 && -f '$work/elsewhere-entry/gitdir' ]] && grep -q 'was not found' <<<\"\$o\""

# A parent folder that is empty or gone can't be told from an unmounted drive: refused with the prune hint, and made again after git worktree prune
issue 101 "Only child"; git worktree add -q -b feature/101-only-child "$work/oc/oc" origin/main; command rm -rf "$work/oc/oc"
issue 102 "Parent gone"; git worktree add -q -b feature/102-parent-gone "$work/pg/pg" origin/main; command rm -rf "$work/pg"
issue 104 "Parent file"; git worktree add -q -b feature/104-parent-file "$work/pf/pf" origin/main; command rm -rf "$work/pf"; touch "$work/pf"; chmod 755 "$work/pf" # an executable file, not a folder
o=$(bash scripts/work-issue.sh 101 2>&1 >/dev/null); st=$?
check "a worktree whose parent folder is empty is refused, with the prune hint, and its entry kept" "[[ $st != 0 ]] && grep -q 'git worktree prune' <<<\"\$o\" && [[ -d '$work/Repo/.git/worktrees/oc' ]]"
o=$(bash scripts/work-issue.sh 102 2>&1 >/dev/null); st=$?
check "a worktree whose parent folder is gone is refused the same way" "[[ $st != 0 ]] && grep -q 'git worktree prune' <<<\"\$o\" && [[ -d '$work/Repo/.git/worktrees/pg' ]]"
o=$(bash scripts/work-issue.sh 104 2>&1 >/dev/null); st=$?
check "a worktree whose parent is a file, not a folder, is refused the same way" "[[ $st != 0 ]] && grep -q 'git worktree prune' <<<\"\$o\" && [[ -d '$work/Repo/.git/worktrees/pf' ]]"
git worktree prune
check "after git worktree prune, all three are made again" "[[ \$(bash scripts/work-issue.sh 101 2>/dev/null) == '$wt/feature-101-only-child' && \$(bash scripts/work-issue.sh 102 2>/dev/null) == '$wt/feature-102-parent-gone' && \$(bash scripts/work-issue.sh 104 2>/dev/null) == '$wt/feature-104-parent-file' ]]"

# A local branch is checked out as it is, even when origin's copy of it is ahead
issue 100 "Behind"; git branch feature/100-behind origin/main; other_push feature/100-behind n100 origin/main; git fetch -q origin
p100=$(bash scripts/work-issue.sh 100 2>/dev/null)
check "a local branch is checked out as it is, even when origin's copy is ahead" "[[ '$p100' == '$wt/feature-100-behind' && \$(git -C '$p100' rev-parse HEAD) == \$(git rev-parse feature/100-behind) && \$(git rev-parse feature/100-behind) != \$(git rev-parse origin/feature/100-behind) ]]"

# Origin can't be fetched: a new worktree is refused, one the branch already has is still printed (with a warning)
issue 103 "Offline"
o=$(PATH="$work/shim:$PATH" SHIM_FETCH_FAIL=1 bash scripts/work-issue.sh 103 2>&1 >/dev/null); st=$?
check "origin can't be fetched: a new worktree is refused, and nothing is made" "[[ $st != 0 ]] && grep -q \"can't fetch origin\" <<<\"\$o\" && [[ ! -e '$wt/feature-103-offline' ]] && ! git show-ref --verify --quiet refs/heads/feature/103-offline"
out54=$(PATH="$work/shim:$PATH" SHIM_FETCH_FAIL=1 bash scripts/work-issue.sh 54 2>"$work/err"); st=$?
check "origin can't be fetched: a worktree the branch already has is still printed, with a warning" "[[ $st == 0 && '$out54' == '$p' ]] && grep -q \"can't fetch origin: using what was fetched last\" '$work/err'"

# The fetch failing before git's entry for a hand-deleted worktree is forgotten: the entry stays, and a later run (fetch working) makes the worktree again
issue 105 "Stale offline"; git worktree add -q -b feature/105-stale-offline "$wt/feature-105-stale-offline" origin/main; command rm -rf "$wt/feature-105-stale-offline"
o=$(PATH="$work/shim:$PATH" SHIM_FETCH_FAIL=1 bash scripts/work-issue.sh 105 2>&1 >/dev/null); st=$?
check "a failed fetch stops before git's entry for a hand-deleted worktree is forgotten" "[[ $st != 0 ]] && grep -q \"can't fetch origin\" <<<\"\$o\" && [[ -d '$work/Repo/.git/worktrees/feature-105-stale-offline' && ! -e '$wt/feature-105-stale-offline' ]]"
check "a later run makes that worktree again" "[[ \$(bash scripts/work-issue.sh 105 2>/dev/null) == '$wt/feature-105-stale-offline' && -d '$wt/feature-105-stale-offline' ]]"

# gh failing, an unknown issue, no gh, a check-branch-name.sh that refuses, a path with spaces and a non-ASCII letter
before="$(git branch | wc -l)$(find "$wt" -mindepth 1 -maxdepth 1 | wc -l)"
touch "$GH_FIX/_fail"; o=$(bash scripts/work-issue.sh 54 2>&1 >/dev/null); st=$?; command rm -f "$GH_FIX/_fail"
check "a gh failure fails the run and makes nothing" "[[ $st != 0 ]] && grep -q 'network down' <<<\"\$o\" && [[ \"\$(git branch | wc -l)\$(find '$wt' -mindepth 1 -maxdepth 1 | wc -l)\" == '$before' ]]" # gh-repo: ok
o=$(bash scripts/work-issue.sh 98 2>&1 >/dev/null); st=$?
check "an unknown issue fails the run and makes nothing" "[[ $st != 0 ]] && grep -q 'no issue 98' <<<\"\$o\" && [[ \"\$(git branch | wc -l)\$(find '$wt' -mindepth 1 -maxdepth 1 | wc -l)\" == '$before' ]]"
mkdir "$work/nogh"; ln -s "$(command -v git)" "$work/nogh/git"
o=$(PATH="$work/nogh" "$(command -v bash)" scripts/work-issue.sh 54 2>&1 >/dev/null); st=$?
check "refuses without gh" "[[ $st != 0 ]] && grep -q 'gh is needed' <<<\"\$o\"" # gh-repo: ok
issue 95 "Guard"; cp scripts/check-branch-name.sh "$work/cbn-real.sh"; printf '#!/usr/bin/env bash\nexit 1\n' > scripts/check-branch-name.sh
o=$(bash scripts/work-issue.sh 95 2>&1 >/dev/null); st=$?; cp "$work/cbn-real.sh" scripts/check-branch-name.sh
check "a branch name the standard refuses is not made" "[[ $st != 0 ]] && grep -q 'is not an allowed branch name' <<<\"\$o\" && [[ ! -e '$wt/feature-95-guard' ]]"
sp="$work/Réo x"; git clone -q "$work/origin.git" "$sp" 2>/dev/null
check "a primary checkout whose path has spaces and a non-ASCII letter works" "[[ \$(cd '$sp' && bash scripts/work-issue.sh 55 2>/dev/null) == '$work/Réo x-worktrees/fix-55-null-children-crash-the-grid' && -d '$work/Réo x-worktrees/fix-55-null-children-crash-the-grid' ]]"

o=$(bash scripts/work-issue.sh 99 2>&1 >/dev/null); st=$?
check "refuses an issue with no title" "[[ $st != 0 ]] && grep -q 'has no title' <<<\"\$o\""
o=$(bash scripts/work-issue.sh abc 2>&1 >/dev/null); st=$?
check "refuses a non-number" "[[ $st != 0 ]] && grep -q '^usage:' <<<\"\$o\""
o=$(bash scripts/work-issue.sh 77 78 2>&1 >/dev/null); st=$?
check "refuses a second argument, and makes nothing" "[[ $st != 0 ]] && grep -q '^usage:' <<<\"\$o\" && [[ ! -e '$wt/feature-77-extra-arguments' ]]"
o=$(bash scripts/work-issue.sh 77 --dry-run 2>&1 >/dev/null); st=$?
check "refuses a flag, and makes nothing" "[[ $st != 0 ]] && grep -q '^usage:' <<<\"\$o\" && [[ ! -e '$wt/feature-77-extra-arguments' ]]"
o=$(bash scripts/work-issue.sh 2>&1 >/dev/null); st=$?
check "refuses no argument" "[[ $st != 0 ]] && grep -q '^usage:' <<<\"\$o\""
o=$(bash scripts/work-issue.sh 1234567890 2>&1 >/dev/null); st=$?
check "refuses a number over 9 digits" "[[ $st != 0 ]] && grep -q '^usage:' <<<\"\$o\""
o=$(GITHUB_REPOSITORY=nope bash scripts/work-issue.sh 54 2>&1 >/dev/null); st=$?
check "refuses when it can't tell the repository" "[[ $st != 0 ]] && grep -q \"can't tell which repository\" <<<\"\$o\""
mv scripts/check-branch-name.sh "$work/cbn.sh"; o=$(bash scripts/work-issue.sh 54 2>&1 >/dev/null); st=$?; mv "$work/cbn.sh" scripts/check-branch-name.sh
check "refuses without check-branch-name.sh" "[[ $st != 0 ]] && grep -q 'check-branch-name.sh is missing' <<<\"\$o\""

if [[ -s "$not_found" ]]; then FAILED=$((FAILED + 1)); echo "FAIL - the suite called commands that do not exist: $(sort -u "$not_found" | tr '\n' ' ')"; fi
echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
