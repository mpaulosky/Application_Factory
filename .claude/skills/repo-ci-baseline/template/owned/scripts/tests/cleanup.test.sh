#!/usr/bin/env bash
# Tests for scripts/cleanup.sh, run in a throwaway clone with a bare origin and a fake `gh`.
# Usage: scripts/tests/cleanup.test.sh
#
# The fake gh answers `gh pr list --head <branch> ...` from the file $GH_FIX/<branch with / as _>: one JSON object per PR (state, headRefOid, baseRefName,
# headRepository), and runs the --jq text cleanup.sh passes it through the real jq, as gh does. It fails unless it is given --state all, --limit 100, a
# --head, exactly the four --json fields cleanup.sh needs, and GH_REPO. A file $GH_FIX/_fail makes it exit 1. A file $GH_FIX/<branch with / as _>.hook is
# run once, the first time that branch is asked about (a push that lands after the fetch). A git shim answers `git remote get-url origin` from
# $SHIM_ORIGIN_URL, to try github.com URL forms, fails `git ls-remote` for the branch in $SHIM_LSREMOTE_FAIL, and fails `git fetch` when SHIM_FETCH_FAIL is set (all: every fetch;
# closing: the first passes and the later ones fail, counted in the file $SHIM_FETCH_COUNT). The fake gh prints at most 100 rows, as --limit 100 does.
# Needs jq (a developer machine without it skips this file; CI fails), bash, git and chmod; the unsearchable-folder check is skipped as root, which can search any folder.
# shellcheck disable=SC2034 # out and o are read by the eval in check
set -uo pipefail

unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX
SCRIPTS="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'chmod -R u+rwx "$work/wt-np" 2>/dev/null; command rm -rf "$work"' EXIT
mkdir "$work/no-template"
export GIT_TEMPLATE_DIR="$work/no-template" GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@t

if ! command -v jq >/dev/null 2>&1; then # gh-repo: ok
  echo "jq is not installed, and the fake gh runs cleanup.sh's jq with it" # gh-repo: ok
  [[ -z "${CI:-}" ]] && { echo "skip"; exit 0; } # a developer machine may skip; CI must not
  exit 1
fi

PASSED=0; FAILED=0
ok() { PASSED=$((PASSED + 1)); echo "ok   - $1"; }
bad() { FAILED=$((FAILED + 1)); echo "FAIL - $1"; }
check() { if eval "$2"; then ok "$1"; else bad "$1"; fi; }

export GH_FIX="$work/fix"; mkdir "$GH_FIX"
mkdir "$work/bin"
cat > "$work/bin/gh" <<'GH' # gh-repo: ok
#!/usr/bin/env bash
args="$*"
[[ $# == 12 && "$1 $2 $3 $4 $5 $6 $7" == "pr list --state all --limit 100 --head" && -n "$8" && "$9 ${10} ${11}" == "--json state,headRefOid,baseRefName,headRepository --jq" \
  && "${GH_REPO:-}" == "${EXPECT_GH_REPO:-o/r}" ]] || { echo "fake gh: bad call: $args (GH_REPO=${GH_REPO:-})" >&2; exit 2; } # gh-repo: ok
[[ ! -f "$GH_FIX/_fail" ]] || { echo "fake gh: network down" >&2; exit 1; } # gh-repo: ok
while [[ $# -gt 0 ]]; do
  case "$1" in --head) b="$2" ;; --jq) expr="$2" ;; esac
  shift
done
f="$GH_FIX/${b//\//_}"
if [[ -f "$f.hook" ]]; then bash "$f.hook"; rm -f "$f.hook"; fi
[[ -f "$f" ]] || exit 0
jq -s . "$f" | jq -r "$expr" | head -n 100 # at most --limit rows
GH
chmod +x "$work/bin/gh" # gh-repo: ok
export PATH="$work/bin:$PATH"
export GITHUB_REPOSITORY=o/r

git init -q --bare -b main "$work/origin.git"
git clone -q "$work/origin.git" "$work/repo" 2>/dev/null
repo="$work/repo"
cd "$repo" || exit 1
git config fetch.prune false; git config remote.origin.prune false # a user's own config must not prune for the script: only its --prune may
git checkout -q -b main
mkdir scripts
cp "$SCRIPTS/check-branch-name.sh" "$SCRIPTS/cleanup.sh" scripts/
{ git add . && git commit -q -m init && git push -q origin main; } || { echo "setup failed"; exit 1; }
git remote set-head origin main
main_init=$(git rev-parse main)

# A second clone, to push to origin as someone else would.
git clone -q "$work/origin.git" "$work/other" 2>/dev/null
other_push() { # other_push <ref> <file>: a commit on top of origin's <ref>, pushed from the second clone
  git -C "$work/other" fetch -q origin && git -C "$work/other" checkout -q -B tmp "origin/$1" \
    && echo "$2" > "$work/other/$2" && git -C "$work/other" add "$2" && git -C "$work/other" commit -q -m "$2" \
    && git -C "$work/other" push -q origin "tmp:refs/heads/$1"
}

# branch <name> <pushed:yes|no>: a worktree beside the clone with one commit; prints its tip
branch() {
  local dir="$work/wt/${1//\//-}"
  git worktree add -q -b "$1" "$dir" main >&2 || exit 1
  echo x > "$dir/f" && git -C "$dir" add f && git -C "$dir" commit -q -m "work on $1"
  if [[ "$2" == yes ]]; then git -C "$dir" push -q origin "$1" 2>/dev/null || exit 1; fi
  git -C "$dir" config "branch.$1.remote" origin; git -C "$dir" config "branch.$1.merge" "refs/heads/$1" # the branch's own config section, which deleting the branch must take with it
  git -C "$dir" rev-parse HEAD
}
pr() { # pr <branch> <STATE> <oid> [base] [head repository, owner/name; - for none, as for a deleted fork]
  jq -nc --arg s "$2" --arg o "$3" --arg b "${4:-main}" --arg r "${5:-o/r}" \
    '{state: $s, headRefOid: $o, baseRefName: $b, headRepository: (if $r == "-" then null else {name: ($r | split("/")[1]), nameWithOwner: $r} end)}' >> "$GH_FIX/${1//\//_}"
}
remote_only() { git worktree remove "$work/wt/${1//\//-}" && git branch -q -D "$1"; }

tip=$(branch feature/1-merged yes) || exit 1;       pr feature/1-merged MERGED "$tip"
tip=$(branch fix/2-dirty yes) || exit 1;            pr fix/2-dirty MERGED "$tip"; echo dirt > "$work/wt/fix-2-dirty/f"
tip=$(branch chore/open-one yes) || exit 1;         pr chore/open-one OPEN "$tip"
tip=$(branch fix/3-unpushed yes) || exit 1;         pr fix/3-unpushed MERGED "$tip"
echo more > "$work/wt/fix-3-unpushed/g"; git -C "$work/wt/fix-3-unpushed" add g; git -C "$work/wt/fix-3-unpushed" commit -q -m more
tip=$(branch feature/4-closed yes) || exit 1;       pr feature/4-closed CLOSED "$tip"
tip=$(branch chore/nopr no)
tip=$(branch chore/remote-only yes) || exit 1;      pr chore/remote-only MERGED "$tip"; remote_only chore/remote-only
tip=$(branch chore/forked yes) || exit 1;           pr chore/forked MERGED "$tip" main someone/r; remote_only chore/forked
tip=$(branch chore/stacked yes) || exit 1;          pr chore/stacked MERGED "$tip" feature/9-base; remote_only chore/stacked
tip=$(branch chore/open-and-merged yes) || exit 1;  pr chore/open-and-merged MERGED "$tip"; pr chore/open-and-merged OPEN "$tip"
tip=$(branch chore/closed-then-merged yes) || exit 1; pr chore/closed-then-merged CLOSED "$tip"; pr chore/closed-then-merged MERGED "$tip"
tip=$(branch chore/locked yes) || exit 1;           pr chore/locked MERGED "$tip"; git worktree lock "$work/wt/chore-locked"
tip=$(branch chore/owner-case yes) || exit 1;       pr chore/owner-case MERGED "$tip" main Acme/R
tip=$(branch chore/stale yes) || exit 1;            pr chore/stale MERGED "$tip"; command rm -rf "$work/wt/chore-stale"
tip=$(branch chore/unreadable yes) || exit 1;       pr chore/unreadable MERGED "$tip"; echo garbage > "$repo/.git/worktrees/chore-unreadable/index"
tip=$(branch chore/delfail yes) || exit 1;          pr chore/delfail MERGED "$tip"; git worktree remove "$work/wt/chore-delfail"; touch "$repo/.git/refs/heads/chore/delfail.lock"
tip=$(branch chore/pre yes) || exit 1;              pr chore/pre OPEN "$tip"
tip=$(branch chore/pre-fix yes) || exit 1;          pr chore/pre-fix MERGED "$tip"; remote_only chore/pre-fix
tip=$(branch chore/gone-race yes) || exit 1;        pr chore/gone-race MERGED "$tip"; remote_only chore/gone-race
tip=$(branch chore/deleted-fork yes) || exit 1;     pr chore/deleted-fork MERGED "$tip" main -; remote_only chore/deleted-fork
tip=$(branch chore/stale-locked yes) || exit 1;     pr chore/stale-locked MERGED "$tip"; git worktree lock "$work/wt/chore-stale-locked"; command rm -rf "$work/wt/chore-stale-locked"
tip=$(branch chore/net-fail yes) || exit 1;         pr chore/net-fail MERGED "$tip"; remote_only chore/net-fail
# A worktree whose gitdir file holds a relative path (what git 2.48+ writes with worktree.useRelativePaths), written by hand so the test does not depend on git's version
tip=$(branch chore/relative-path yes) || exit 1; pr chore/relative-path MERGED "$tip"
printf '%s\n' "../../../../wt/chore-relative-path/.git" > "$repo/.git/worktrees/chore-relative-path/gitdir"
command rm -rf "$work/wt/chore-relative-path"
rel_ok=false; git worktree list --porcelain | grep -qx "worktree $work/wt/chore-relative-path" && rel_ok=true
git worktree add -q --detach "$work/wt/detached" main
tip=$(branch chore/lease-race yes) || exit 1;       pr chore/lease-race MERGED "$tip"; remote_only chore/lease-race
git push -q origin main:refs/heads/Bad_Name
pr Bad_Name MERGED "$main_init"; git branch -q Bad_Local main; pr Bad_Local MERGED "$main_init" # non-standard names, with a merged PR at their tip: still kept
tip=$(branch fix/10-untracked yes) || exit 1;       pr fix/10-untracked MERGED "$tip"; echo u > "$work/wt/fix-10-untracked/untracked"
tip=$(branch chore/ignored no) || exit 1
echo ignored.txt > "$work/wt/chore-ignored/.gitignore"; git -C "$work/wt/chore-ignored" add .gitignore; git -C "$work/wt/chore-ignored" commit -q -m ignore
git -C "$work/wt/chore-ignored" push -q origin chore/ignored 2>/dev/null || exit 1
pr chore/ignored MERGED "$(git -C "$work/wt/chore-ignored" rev-parse HEAD)"; echo secret > "$work/wt/chore-ignored/ignored.txt"
sp="$work/wt sp/ré d" # spaces and a non-ASCII letter in the worktree's path
mkdir -p "$work/wt sp"
{ git worktree add -q -b chore/spaced "$sp" main && echo x > "$sp/f" && git -C "$sp" add f && git -C "$sp" commit -q -m spaced && git -C "$sp" push -q origin chore/spaced 2>/dev/null; } || exit 1
pr chore/spaced MERGED "$(git -C "$sp" rev-parse HEAD)"
np="$work/wt-np/w" # a worktree whose parent folder can't be searched is out of reach, not deleted
{ git worktree add -q -b chore/noperm "$np" main && echo x > "$np/f" && git -C "$np" add f && git -C "$np" commit -q -m noperm && git -C "$np" push -q origin chore/noperm 2>/dev/null; } || exit 1
pr chore/noperm MERGED "$(git -C "$np" rev-parse HEAD)"
noperm_ok=false; if [[ "$(id -u)" != 0 ]]; then chmod 000 "$work/wt-np"; [[ ! -x "$work/wt-np" ]] && noperm_ok=true; fi
tip=$(branch chore/symlinked yes) || exit 1;        pr chore/symlinked MERGED "$tip" # git's entry for it is a symlink to a folder elsewhere: never followed
command rm -rf "$work/wt/chore-symlinked"; mv "$repo/.git/worktrees/chore-symlinked" "$work/elsewhere-entry"; ln -s "$work/elsewhere-entry" "$repo/.git/worktrees/chore-symlinked"
tip=$(branch chore/local-race yes) || exit 1;       pr chore/local-race MERGED "$tip"
wt_at() { # wt_at <branch> <dir>: a pushed worktree at <dir>, with a merged PR at its tip
  { git worktree add -q -b "$1" "$2" main && echo x > "$2/f" && git -C "$2" add f && git -C "$2" commit -q -m "$1" && git -C "$2" push -q origin "$1" 2>/dev/null; } || exit 1
  pr "$1" MERGED "$(git -C "$2" rev-parse HEAD)"
}
# A folder whose parent is empty or gone can't be told from an unmounted drive: kept, with the hint to prune
wt_at chore/only-child "$work/wt-oc/oc"; command rm -rf "$work/wt-oc/oc"
wt_at chore/parent-gone "$work/wt-pg/pg"; command rm -rf "$work/wt-pg"
# Four merged branches under one parent: clean ones before and after the hand-deleted ones. Removing the clean ones empties the parent, but the answer was decided before the run.
wt_at chore/ord-a-clean "$work/wt-ord/a"
wt_at chore/ord-b-stale "$work/wt-ord/b"; command rm -rf "$work/wt-ord/b"
wt_at chore/ord-c-clean "$work/wt-ord/c"
wt_at chore/ord-d-stale "$work/wt-ord/d"; command rm -rf "$work/wt-ord/d"
# A hand-deleted worktree whose folder name ends in a space is still found (the path is read whole, not trimmed), and its parent is not empty
mkdir -p "$work/wt-ts"; touch "$work/wt-ts/other"
wt_at chore/trail-space "$work/wt-ts/t "; command rm -rf "$work/wt-ts/t "
wt_at chore/parent-file "$work/wt-pf/pf"; command rm -rf "$work/wt-pf"; touch "$work/wt-pf"; chmod 755 "$work/wt-pf" # the parent is now an executable file, not a folder
# A branch a worktree is rebasing shows as detached in the worktree list: git branch -D refuses it
wt_at chore/rebasing "$work/wt/chore-rebasing"
(cd "$work/wt/chore-rebasing" && GIT_SEQUENCE_EDITOR="sed -i.bak 's/^pick/edit/'" git rebase -q -i -f main >/dev/null 2>&1)
[[ -d "$repo/.git/worktrees/chore-rebasing/rebase-merge" ]] || { echo "setup failed: the rebase did not stop"; exit 1; }
# The verdict, beyond the single-PR cases
tip=$(branch chore/v-other-main yes) || exit 1;     pr chore/v-other-main MERGED "$tip" feature/9-base; pr chore/v-other-main MERGED "$tip"; remote_only chore/v-other-main
tip=$(branch chore/v-closed-other yes) || exit 1;   pr chore/v-closed-other CLOSED "$tip"; pr chore/v-closed-other MERGED "$tip" develop; remote_only chore/v-closed-other
tip=$(branch chore/v-closed-old yes) || exit 1;     pr chore/v-closed-old CLOSED "$main_init"; remote_only chore/v-closed-old
tip=$(branch chore/v-open-old yes) || exit 1;       pr chore/v-open-old OPEN "$main_init"; pr chore/v-open-old MERGED "$tip"; remote_only chore/v-open-old
# gh is asked for 100 PRs at most: 99 rows are all of them, 100 may have cut the branch's own PR off
tip=$(branch chore/cap99 yes) || exit 1;            pr chore/cap99 MERGED "$tip"; for _ in $(seq 98); do pr chore/cap99 CLOSED "$main_init" main x/y; done; remote_only chore/cap99
tip=$(branch chore/cap100 yes) || exit 1;           pr chore/cap100 MERGED "$tip"; for _ in $(seq 99); do pr chore/cap100 CLOSED "$main_init" main x/y; done; remote_only chore/cap100

mkdir "$work/shim"
cat > "$work/shim/git" <<SHIM
#!/usr/bin/env bash
if [[ "\$1 \$2 \$3" == "remote get-url origin" && -n "\${SHIM_ORIGIN_URL:-}" ]]; then echo "\$SHIM_ORIGIN_URL"; exit 0; fi
if [[ "\$1" == fetch && -n "\${SHIM_FETCH_FAIL:-}" ]]; then # all: every fetch fails; closing: the first passes and the later ones fail
  n=\$(( \$(cat "\$SHIM_FETCH_COUNT" 2>/dev/null || echo 0) + 1 )); echo "\$n" > "\$SHIM_FETCH_COUNT"
  if [[ "\$SHIM_FETCH_FAIL" == all || \$n -ge 2 ]]; then echo "shim: fetch failed" >&2; exit 128; fi
fi
if [[ "\$1" == ls-remote && -n "\${SHIM_LSREMOTE_FAIL:-}" && "\$*" == *"refs/heads/\$SHIM_LSREMOTE_FAIL"* ]]; then echo "shim: ls-remote failed" >&2; exit 128; fi
exec "$(command -v git)" "\$@"
SHIM
chmod +x "$work/shim/git"
has_local() { git show-ref --verify --quiet "refs/heads/$1"; }
has_remote() { git ls-remote --exit-code --heads origin "refs/heads/$1" >/dev/null 2>&1; }

main_before=$(git rev-parse main)
other_push main from-someone-else

# gh failing is an error, and nothing is removed. A branch deleted on origin since the last fetch is pruned before gh is asked (the first fetch --prune).
git push -q origin main:refs/heads/chore/preprune && git fetch -q origin && git -C "$work/other" push -q origin --delete chore/preprune
touch "$GH_FIX/_fail"
out=$(bash scripts/cleanup.sh 2>&1); st=$?
rm -f "$GH_FIX/_fail"
git reset -q --hard "$main_before"
check "a gh failure fails the run" "[[ $st != 0 ]] && grep -q 'gh pr list failed' <<<\"\$out\"" # gh-repo: ok
check "the first fetch prunes a branch deleted on origin" "! git show-ref --verify --quiet refs/remotes/origin/chore/preprune"
check "a gh failure removes nothing" "has_local feature/1-merged && [[ -d $work/wt/feature-1-merged ]] && has_remote chore/remote-only" # gh-repo: ok

# --dry-run
out=$(bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "dry run succeeds" "[[ $st == 0 ]]"
check "the dry run's summary says so" "grep -q '^Done: [0-9]* removed, [0-9]* kept (dry run)\\.\$' <<<\"\$out\""
check "dry run changes nothing" "has_local feature/1-merged && [[ -d $work/wt/feature-1-merged ]] && has_remote chore/remote-only && has_remote feature/1-merged && [[ \$(git rev-parse main) == $main_before ]]"
check "dry run lists every step" "grep -q 'would: git worktree remove .*feature-1-merged' <<<\"\$out\" && grep -q 'would: git branch -q -D feature/1-merged' <<<\"\$out\" && grep -q 'would: git push .*feature/1-merged' <<<\"\$out\" && grep -q 'would: git merge --ff-only origin/main' <<<\"\$out\""

check "a dry run keeps a locked worktree with a missing folder, as the real run does" "grep -q 'keep   chore/stale-locked: its worktree is locked and its folder is missing' <<<\"\$out\" && ! grep -q 'remove chore/stale-locked' <<<\"\$out\" && ! grep -q 'would: git branch -q -D chore/stale-locked' <<<\"\$out\""
if [[ "$rel_ok" == true ]]; then check "a dry run finds the entry of a worktree git stored with a relative path" "grep -q 'would: forget the missing worktree .*chore-relative-path' <<<\"\$out\""
elif [[ -n "${CI:-}" ]]; then bad "this git can't read a worktree stored with a relative path, and CI must test it"; else echo "skip - this git can't read a relative worktree path"; fi
check "a dry run says it would forget both hand-deleted worktrees, whichever side of the clean ones they sort" "grep -q 'would: forget the missing worktree .*wt-ord/b' <<<\"\$out\" && grep -q 'would: forget the missing worktree .*wt-ord/d' <<<\"\$out\" && ! grep -q 'keep   chore/ord-' <<<\"\$out\""
check "a dry run forgets no worktree entry, but says it would" "grep -q '^remove chore/stale\$' <<<\"\$out\" && grep -q 'would: forget the missing worktree .*chore-stale\$' <<<\"\$out\" && grep -q 'would: git branch -q -D chore/stale' <<<\"\$out\" && [[ -d $repo/.git/worktrees/chore-stale ]] && has_local chore/stale"

# The owner of origin: github.com URL forms (through the git shim), without regard to case; a PR from "Acme/R" counts for acme/r, not for o/r
check "a PR from a repository of another name is not counted by default" "! grep -q 'remove chore/owner-case' <<<\"\$out\" && grep -q 'keep   chore/owner-case: no PR from origin' <<<\"\$out\""
for url in git@github.com:acme/r.git https://github.com/Acme/R https://tok@github.com/acme/r.git ssh://git@github.com/acme/r https://github.com/acme/r/; do
  o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL="$url" bash scripts/cleanup.sh --dry-run 2>&1)
  check "origin $url: a PR from Acme/R counts" "grep -q 'remove chore/owner-case' <<<\"\$o\""
done
for url in ssh://git@github.com:22/acme/r.git https://github.com:443/Acme/R.git/; do
  o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL="$url" bash scripts/cleanup.sh --dry-run 2>&1)
  check "origin $url: a PR from Acme/R counts" "grep -q 'remove chore/owner-case' <<<\"\$o\""
done
for url in https://evil.com/github.com/acme/r https://github.com.evil/acme/r https://x:y@evil.com/github.com/acme/r; do
  o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL="$url" bash scripts/cleanup.sh --dry-run 2>&1)
  check "origin $url is not read as acme/r" "! grep -q 'remove chore/owner-case' <<<\"\$o\""
done
o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://github.com/acme/other bash scripts/cleanup.sh --dry-run 2>&1)
check "a PR from another repository of the same owner does not count" "! grep -q 'remove chore/owner-case' <<<\"\$o\" && grep -q 'keep   chore/owner-case: no PR from origin' <<<\"\$o\""
o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://git.example.com/acme/r bash scripts/cleanup.sh --dry-run 2>&1)
check "a non-GitHub origin falls back to GH_REPO's repository" "! grep -q 'remove chore/owner-case' <<<\"\$o\" && grep -q 'keep   chore/owner-case: no PR from origin' <<<\"\$o\""
o=$(EXPECT_GH_REPO=up/r GITHUB_REPOSITORY=up/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=git@github.com:acme/r.git bash scripts/cleanup.sh --dry-run 2>&1)
check "a fork workflow (GH_REPO the upstream, origin the fork) counts the fork's own PR" "grep -q 'remove chore/owner-case' <<<\"\$o\""

# The repository when GITHUB_REPOSITORY is not set (the usual case on a developer machine): upstream's URL, else origin's
o=$(env -u GITHUB_REPOSITORY EXPECT_GH_REPO=acme/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=git@github.com:acme/r.git bash scripts/cleanup.sh --dry-run 2>&1)
check "with no GITHUB_REPOSITORY, the repository is read from origin's URL" "grep -q 'remove chore/owner-case' <<<\"\$o\""
git remote add upstream https://github.com/up/r.git
o=$(env -u GITHUB_REPOSITORY EXPECT_GH_REPO=up/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=git@github.com:acme/r.git bash scripts/cleanup.sh --dry-run 2>&1)
git remote remove upstream
check "an upstream remote, not origin, names the repository gh is asked about" "grep -q 'remove chore/owner-case' <<<\"\$o\"" # gh-repo: ok
o=$(env -u GITHUB_REPOSITORY PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://git.example.com/acme/r bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "a non-GitHub origin with no GITHUB_REPOSITORY fails, saying why" "[[ $st != 0 ]] && grep -q \"can't tell which repository\" <<<\"\$o\""

# Refusals (main is behind origin/main here, as it is for the real run below)
o=$( cd "$work/wt/feature-1-merged" && bash "$repo/scripts/cleanup.sh" 2>&1 ); st=$?
check "refuses in a linked worktree" "[[ $st != 0 ]] && grep -q 'primary checkout' <<<\"\$o\" && [[ \$(git rev-parse main) == $main_before ]]"
git checkout -q -b chore/elsewhere; o=$(bash scripts/cleanup.sh 2>&1); st=$?; git checkout -q main; git branch -q -D chore/elsewhere
check "refuses off main" "[[ $st != 0 ]] && grep -q 'run this on main' <<<\"\$o\" && has_local feature/1-merged && [[ \$(git rev-parse main) == $main_before ]]"
echo dirt >> scripts/check-branch-name.sh; o=$(bash scripts/cleanup.sh 2>&1); st=$?; git checkout -q -- scripts
check "refuses a dirty main" "[[ $st != 0 ]] && grep -q 'uncommitted changes' <<<\"\$o\" && has_local feature/1-merged"
o=$(bash scripts/cleanup.sh --bogus 2>&1); st=$?
check "refuses an unknown flag" "[[ $st != 0 ]] && grep -q '^usage:' <<<\"\$o\" && has_local feature/1-merged && [[ \$(git rev-parse main) == $main_before ]]"
mv scripts/check-branch-name.sh "$work/cbn.sh"; o=$(bash scripts/cleanup.sh 2>&1); st=$?; mv "$work/cbn.sh" scripts/check-branch-name.sh
check "refuses without check-branch-name.sh" "[[ $st != 0 ]] && grep -q 'check-branch-name.sh is missing' <<<\"\$o\" && has_local feature/1-merged && [[ \$(git rev-parse main) == $main_before ]]"
mkdir "$work/nogh"; ln -s "$(command -v git)" "$work/nogh/git"
o=$(PATH="$work/nogh" "$(command -v bash)" scripts/cleanup.sh 2>&1); st=$?
check "refuses without gh" "[[ $st != 0 ]] && grep -q 'gh is needed' <<<\"\$o\" && has_local feature/1-merged" # gh-repo: ok
echo local > local-main && git add local-main && git commit -q -m "local main commit"
o=$(bash scripts/cleanup.sh 2>&1); st=$?; git reset -q --hard "$main_before"
check "refuses when main can't fast-forward" "[[ $st != 0 ]] && grep -q \"can't fast-forward\" <<<\"\$o\" && has_local feature/1-merged"

cat > "$GH_FIX/chore_lease-race.hook" <<HOOK
git -C "$work/other" fetch -q origin && git -C "$work/other" checkout -q -B tmp origin/chore/lease-race && echo late > "$work/other/late" \
  && git -C "$work/other" add late && git -C "$work/other" commit -q -m late && git -C "$work/other" push -q origin tmp:refs/heads/chore/lease-race
HOOK
echo "git -C \"$work/other\" push -q origin --delete chore/gone-race" > "$GH_FIX/chore_gone-race.hook"
cat > "$GH_FIX/chore_net-fail.hook" <<HOOK
git -C "$work/other" fetch -q origin && git -C "$work/other" checkout -q -B tmp origin/chore/net-fail && echo late > "$work/other/late2" \
  && git -C "$work/other" add late2 && git -C "$work/other" commit -q -m late2 && git -C "$work/other" push -q origin tmp:refs/heads/chore/net-fail
HOOK
echo "git -C \"$work/wt/chore-local-race\" commit -q --allow-empty -m late-local" > "$GH_FIX/chore_local-race.hook"
git fetch -q origin; lease_tip_before=$(git rev-parse origin/chore/lease-race)
# The real run: several removals are refused (a locked worktree, a ref lock, a moved origin tip, a failed ls-remote), so it exits 1 after doing the rest
local_before=$(git for-each-ref refs/heads | wc -l | tr -d ' '); origin_before=$(git ls-remote --heads origin | wc -l | tr -d ' ')
out=$(PATH="$work/shim:$PATH" SHIM_LSREMOTE_FAIL=chore/net-fail bash scripts/cleanup.sh 2>&1); st=$?
check "exits 1 when git refused a removal, after doing the rest" "[[ $st == 1 ]]"
local_after=$(git for-each-ref refs/heads | wc -l | tr -d ' '); origin_after=$(git ls-remote --heads origin | wc -l | tr -d ' ')
removed_n=$(( local_before - local_after + origin_before - origin_after )); kept_n=$(grep -c '^keep ' <<<"$out")
check "the summary counts what was removed and what was kept" "grep -qx 'Done: $removed_n removed, $kept_n kept.' <<<\"\$out\" && [[ $removed_n -gt 5 && $kept_n -gt 5 ]]"
check "the closing fetch prunes the tracking ref of a branch deleted on origin during the run (a push's own delete does not)" "! git show-ref --verify --quiet refs/remotes/origin/chore/gone-race && ! git show-ref --verify --quiet refs/remotes/origin/feature/1-merged"
check "a worktree folder deleted by hand is forgotten and its branch removed in the same run" "! has_local chore/stale && ! has_remote chore/stale && [[ ! -d $repo/.git/worktrees/chore-stale ]]"
check "only that worktree's entry is forgotten: a locked one with a missing folder is kept, with a hint" "has_local chore/stale-locked && has_remote chore/stale-locked && [[ -d $repo/.git/worktrees/chore-stale-locked ]] && grep -q 'keep   chore/stale-locked: its worktree is locked and its folder is missing' <<<\"\$out\""
if [[ "$rel_ok" == true ]]; then check "a worktree stored with a relative path, its folder deleted, is forgotten and its branch removed" "! has_local chore/relative-path && ! has_remote chore/relative-path && [[ ! -d $repo/.git/worktrees/chore-relative-path ]]"; fi
check "a worktree on a detached HEAD is never touched" "[[ -d $work/wt/detached ]]"
check "a deleted fork's PR (no head repository) does not count" "has_remote chore/deleted-fork && grep -q 'keep   origin/chore/deleted-fork: no PR from origin' <<<\"\$out\""
check "a failed push whose ls-remote check also fails is kept and fails the run" "has_remote chore/net-fail && grep -q 'keep   origin/chore/net-fail: git could not delete it, and origin could not be checked' <<<\"\$out\""
check "each hook ran once, so the races really happened" "[[ ! -f $GH_FIX/chore_gone-race.hook && ! -f $GH_FIX/chore_lease-race.hook && ! -f $GH_FIX/chore_net-fail.hook ]]"
check "keeps a branch whose worktree can't be read, and its origin copy" "has_local chore/unreadable && has_remote chore/unreadable && grep -q \"keep   chore/unreadable: its worktree can't be read\" <<<\"\$out\""
check "keeps a branch git could not delete, with its origin copy and its config" "has_local chore/delfail && has_remote chore/delfail && grep -q 'keep   chore/delfail: git could not delete the branch' <<<\"\$out\" && [[ -n \"\$(git config --get-regexp '^branch\\.chore/delfail\\.')\" ]]"
check "a branch that is a prefix of another does not keep the other's origin copy" "has_local chore/pre && has_remote chore/pre && grep -q 'keep   origin/chore/pre: ' <<<\"\$out\" && ! has_remote chore/pre-fix"
check "fast-forwards main to origin/main" "[[ \$(git rev-parse main) == \$(git rev-parse origin/main) && -f from-someone-else ]]"
check "a removed branch takes its branch.<name> config section with it" "[[ -z \"\$(git config --get-regexp '^branch\\.feature/1-merged\\.')\" ]] && [[ -n \"\$(git config --get-regexp '^branch\\.fix/2-dirty\\.')\" ]]"
check "removes a merged branch, its worktree and its origin branch" "! has_local feature/1-merged && [[ ! -d $work/wt/feature-1-merged ]] && ! has_remote feature/1-merged"
check "removes a merged branch that is only on origin" "! has_remote chore/remote-only"
check "keeps a merged branch whose worktree is dirty, and its origin branch" "has_local fix/2-dirty && [[ -d $work/wt/fix-2-dirty ]] && has_remote fix/2-dirty"
check "keeps a branch with an open PR" "has_local chore/open-one && has_remote chore/open-one"
check "keeps a branch with a commit the PR never had, and its origin branch" "has_local fix/3-unpushed && has_remote fix/3-unpushed"
check "keeps a closed-unmerged branch by default" "has_local feature/4-closed && has_remote feature/4-closed"
check "keeps a branch with no PR" "has_local chore/nopr"
check "a non-standard name is kept, local and origin, whatever its PR says" "has_local Bad_Local && has_remote Bad_Name && grep -q 'keep   Bad_Local: not a standard branch name' <<<\"\$out\" && grep -q 'keep   origin/Bad_Name: not a standard branch name' <<<\"\$out\""
check "a worktree with only an untracked file is dirty" "has_local fix/10-untracked && has_remote fix/10-untracked && grep -q 'keep   fix/10-untracked: its worktree has uncommitted changes' <<<\"\$out\""
check "a worktree holding an ignored file is removed with it, as documented" "! has_local chore/ignored && ! has_remote chore/ignored && [[ ! -e $work/wt/chore-ignored ]]"
check "a worktree whose path has spaces and a non-ASCII letter is removed" "! has_local chore/spaced && ! has_remote chore/spaced && [[ ! -e '$sp' ]]"
if [[ "$noperm_ok" == true ]]; then check "a worktree whose parent folder can't be searched is kept as out of reach, not forgotten" "has_local chore/noperm && has_remote chore/noperm && grep -q \"keep   chore/noperm: its worktree's folder can't be reached\" <<<\"\$out\" && [[ -d $repo/.git/worktrees/w ]]"; else echo "skip - running as root, or chmod 000 did not make the folder unsearchable, so that arm can't be tried here (the empty and missing parents are tried below)"; fi
check "a worktree entry that is a symlink is never followed or removed" "has_local chore/symlinked && has_remote chore/symlinked && [[ -f $work/elsewhere-entry/gitdir ]] && grep -q 'keep   chore/symlinked: its worktree' <<<\"\$out\""
check "a commit made after the branch was judged keeps the branch, and its origin copy" "has_local chore/local-race && has_remote chore/local-race && [[ \$(git log -1 --format=%s chore/local-race) == late-local ]] && grep -q 'keep   chore/local-race: it has a commit that was made after it was judged' <<<\"\$out\" && grep -q 'keep   origin/chore/local-race: its local branch was kept' <<<\"\$out\" && [[ -n \"\$(git config --get-regexp '^branch\\.chore/local-race\\.')\" ]]"
check "a branch a worktree is rebasing is kept, with its origin copy" "has_local chore/rebasing && has_remote chore/rebasing && grep -q 'keep   chore/rebasing: git could not delete the branch' <<<\"\$out\" && [[ -d $work/wt/chore-rebasing ]]"
check "a worktree whose parent folder is empty is kept as maybe out of reach, with the prune hint, and its entry kept" "has_local chore/only-child && has_remote chore/only-child && [[ -d $repo/.git/worktrees/oc ]] && grep -q \"keep   chore/only-child: its worktree's folder can't be reached\" <<<\"\$out\" && grep -q 'git worktree prune' <<<\"\$out\""
check "hand-deleted worktrees are forgotten the same in the real run, whichever side of the clean ones they sort" "! has_local chore/ord-b-stale && ! has_remote chore/ord-b-stale && ! has_local chore/ord-d-stale && ! has_remote chore/ord-d-stale && ! has_local chore/ord-a-clean && ! has_local chore/ord-c-clean"
check "a hand-deleted worktree whose folder name ends in a space is forgotten, not kept as out of reach" "! has_local chore/trail-space && ! has_remote chore/trail-space"
check "a worktree whose parent is a file, not a folder, is kept the same way" "has_local chore/parent-file && has_remote chore/parent-file && [[ -d $repo/.git/worktrees/pf ]] && grep -q \"keep   chore/parent-file: its worktree's folder can't be reached\" <<<\"\$out\""
check "a worktree whose parent folder is gone is kept the same way" "has_local chore/parent-gone && has_remote chore/parent-gone && [[ -d $repo/.git/worktrees/pg ]] && grep -q \"keep   chore/parent-gone: its worktree's folder can't be reached\" <<<\"\$out\""
check "a PR merged into another base, then one into main, at the same tip: merged" "! has_remote chore/v-other-main"
check "a closed PR, then one merged into another base: kept, as merged elsewhere" "has_remote chore/v-closed-other && grep -q 'keep   origin/chore/v-closed-other: its PR merged into develop, not main' <<<\"\$out\""
check "a closed PR at an older tip is not this branch's PR" "has_remote chore/v-closed-old"
check "an open PR keeps the branch whatever tip it has" "has_remote chore/v-open-old && grep -q 'keep   origin/chore/v-open-old: its PR is open' <<<\"\$out\""
check "99 PRs of that branch name are all of them: the merged one counts" "! has_remote chore/cap99"
check "100 PRs of that branch name may hide its own: kept" "has_remote chore/cap100 && grep -q 'keep   origin/chore/cap100: 100 or more PRs' <<<\"\$out\""
check "does not mistake origin/HEAD for a branch" "! grep -q 'origin/origin' <<<\"\$out\""
check "says why it kept one" "grep -q 'keep   fix/2-dirty: its worktree has uncommitted changes' <<<\"\$out\" && grep -q 'keep   origin/fix/2-dirty: its local branch was kept' <<<\"\$out\""
check "ignores a PR from a fork with the same branch name" "has_remote chore/forked && grep -q 'keep   origin/chore/forked: no PR from origin' <<<\"\$out\""
check "keeps a branch whose PR merged into another branch" "has_remote chore/stacked && grep -q 'merged into feature/9-base, not main' <<<\"\$out\""
check "an open PR beside a merged one keeps the branch" "has_local chore/open-and-merged && has_remote chore/open-and-merged"
check "a closed PR beside a merged one at the same tip counts as merged" "! has_local chore/closed-then-merged && ! has_remote chore/closed-then-merged"
check "keeps a locked worktree's branch and says git refused" "has_local chore/locked && has_remote chore/locked && grep -q 'keep   chore/locked: git could not remove its worktree' <<<\"\$out\""
check "an origin branch deleted by GitHub since the fetch counts as removed, not as a failure" "! has_remote chore/gone-race && ! grep -q 'keep   origin/chore/gone-race' <<<\"\$out\""
check "keeps an origin branch that moved after the fetch" "has_remote chore/lease-race && [[ \$(git rev-parse origin/chore/lease-race) != $lease_tip_before ]] && grep -q 'keep   origin/chore/lease-race: git could not delete it' <<<\"\$out\""

# A later run, once what git refused is out of the way, finishes the job and exits 0
chmod 755 "$work/wt-np" 2>/dev/null; git -C "$work/wt/chore-rebasing" rebase --abort >/dev/null 2>&1; git worktree prune; git worktree unlock "$work/wt/chore-locked"; command rm -f "$repo/.git/worktrees/chore-unreadable/index"; git -C "$work/wt/chore-unreadable" read-tree HEAD; git worktree unlock "$work/wt/chore-stale-locked"; command rm -f "$repo/.git/refs/heads/chore/delfail.lock"
bash scripts/cleanup.sh --include-closed >/dev/null 2>&1; st=$?
check "a later run with nothing refused exits 0" "[[ $st == 0 ]]"
check "--include-closed removes a closed-unmerged branch" "! has_local feature/4-closed && ! has_remote feature/4-closed"
check "an unlocked worktree with a missing folder is forgotten by the later run, and its branch goes" "! has_local chore/stale-locked && ! has_remote chore/stale-locked && [[ ! -d $repo/.git/worktrees/chore-stale-locked ]]"
check "a worktree out of reach is removed once it is reachable again" "! has_local chore/noperm && ! has_remote chore/noperm"
check "after git worktree prune, the branches kept for an empty or missing parent are removed" "! has_local chore/only-child && ! has_remote chore/only-child && ! has_local chore/parent-gone && ! has_remote chore/parent-gone && ! has_local chore/parent-file && ! has_remote chore/parent-file"
check "a branch whose rebase was aborted is removed by the later run" "! has_local chore/rebasing && ! has_remote chore/rebasing"
check "the branches git had refused are removed by the later run" "! has_local chore/locked && ! has_remote chore/locked && ! has_local chore/delfail && ! has_remote chore/delfail && ! has_local chore/unreadable && ! has_remote chore/unreadable"

# The fetch of origin failing: the opening one stops the run, the closing one fails it after the rest was done
tip=$(branch chore/closing yes) || exit 1; pr chore/closing MERGED "$tip"
o=$(PATH="$work/shim:$PATH" SHIM_FETCH_FAIL=all SHIM_FETCH_COUNT="$work/fc1" bash scripts/cleanup.sh 2>&1); st=$?
check "refuses when origin can't be fetched, and removes nothing" "[[ $st != 0 ]] && grep -q \"can't fetch origin\" <<<\"\$o\" && has_local chore/closing && has_remote chore/closing"
o=$(PATH="$work/shim:$PATH" SHIM_FETCH_FAIL=closing SHIM_FETCH_COUNT="$work/fc2" bash scripts/cleanup.sh 2>&1); st=$?
check "a failed closing fetch is reported and fails the run, after the rest was done" "[[ $st == 1 ]] && grep -q 'the closing fetch of origin failed' <<<\"\$o\" && ! has_local chore/closing && ! has_remote chore/closing && grep -q '^Done: ' <<<\"\$o\""

echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
