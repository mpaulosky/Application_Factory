#!/usr/bin/env bash
# Tests for scripts/cleanup.sh, run in a throwaway clone with a bare origin and a fake `gh`.
# Usage: scripts/tests/cleanup.test.sh
#
# The fake gh answers `gh pr list --head <branch> ...` from the file $GH_FIX/<branch with / as _>: one JSON object per PR (state, headRefOid, baseRefName,
# headRepository), and runs the --jq text cleanup.sh passes it through the real jq, as gh does. It fails unless it is given --state all, --limit 100, a
# --head, exactly the four --json fields cleanup.sh needs, and GH_REPO. A file $GH_FIX/_fail makes it exit 1. A file $GH_FIX/<branch with / as _>.hook is
# run once, the first time that branch is asked about (a push that lands after the fetch). A git shim answers `git remote get-url origin` from
# $SHIM_ORIGIN_URL, to try github.com URL forms, fails `git ls-remote` for the branch in $SHIM_LSREMOTE_FAIL, and fails `git fetch` when SHIM_FETCH_FAIL is set (all: every fetch;
# closing: the first passes and the later ones fail, counted in the file $SHIM_FETCH_COUNT), answers `git remote get-url --push --all origin` from $SHIM_PUSH_URLS (; between URLs) or else $SHIM_ORIGIN_URL, and
# lands a commit on the branch named in $SHIM_ADVANCE (or deletes the branch named in $SHIM_VANISH, leaving an alias to nowhere when SHIM_DANGLE is set) just before `git update-ref --no-deref -d` deletes it, and fails
# `git remote get-url --push` when SHIM_PUSH_FAIL is set, and prints no push URLs when SHIM_PUSH_EMPTY is set. The fake gh prints at most 100 rows, as --limit 100 does.
# It also answers the lookup of open PRs based on a branch, `gh pr list --state open --limit 100 --base <branch> --json number --jq length`, which it requires in exactly that form: with the number
# in $GH_FIX/_base_<branch with / as _> (0 when there is no file), with GH_REPO, compared without regard to case, equal to $EXPECT_GH_BASE_REPO (written in lowercase; o/r when it is not set); $GH_FIX/_fail fails it too, and
# $GH_FIX/_failbase fails only it.
# Needs jq (a developer machine without it skips this file; CI fails), bash, git and chmod; the unsearchable-folder check is skipped as root, which can search any folder.
# shellcheck disable=SC2034 # out and o are read by the eval in check
set -uo pipefail

unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX
SCRIPTS="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)" || exit 1
work="$(cd "$work" && pwd -P)" || exit 1 # the real path: git reports real paths, and /tmp or /var may be a symlink
[[ -n "$work" && -d "$work" ]] || exit 1
trap 'chmod -R u+rwx "$work/wt-np" 2>/dev/null; command rm -rf "$work"' EXIT
mkdir "$work/no-template"
export GIT_TEMPLATE_DIR="$work/no-template" GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@t
export HOME="$work" GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 # a developer's own git config (signing, hooks, insteadOf) must not reach the throwaway repos

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
if [[ $# == 12 && "$1 $2 $3 $4 $5 $6 $7" == "pr list --state open --limit 100 --base" && -n "$8" && "$9 ${10} ${11} ${12}" == "--json number --jq length" ]]; then
  # the base lookup asks the repository that holds the branches (origin's), which can differ from GH_REPO for the PRs; o/r unless EXPECT_GH_BASE_REPO says otherwise
  [[ "$(printf %s "${GH_REPO:-}" | tr '[:upper:]' '[:lower:]')" == "${EXPECT_GH_BASE_REPO:-o/r}" ]] || { echo "fake gh: bad base GH_REPO: ${GH_REPO:-}" >&2; exit 2; } # gh-repo: ok
  [[ ! -f "$GH_FIX/_fail" && ! -f "$GH_FIX/_failbase" ]] || { echo "fake gh: network down" >&2; exit 1; } # gh-repo: ok
  if [[ -f "$GH_FIX/_base_${8//\//_}" ]]; then cat "$GH_FIX/_base_${8//\//_}"; else echo 0; fi # the count of open PRs based on that branch
  exit 0
fi
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
rel_ok=false; wlist="$(git worktree list --porcelain)"; grep -qx "worktree $work/wt/chore-relative-path" <<<"$wlist" && rel_ok=true # no pipe: grep -q may close it early
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
# A branch a worktree is rebasing with the apply backend (rebase-apply/head-name), and one whose name starts like the interactive one's (the match is exact)
wt_at chore/apply-busy "$work/wt/chore-apply-busy"
( cd "$work/wt/chore-apply-busy" && git checkout -q --detach main && echo other > f && git add f && git commit -q -m other && o=$(git rev-parse HEAD) && git checkout -q chore/apply-busy && git rebase --apply "$o" >/dev/null 2>&1 )
[[ -d "$repo/.git/worktrees/chore-apply-busy/rebase-apply" ]] || { echo "setup failed: the apply rebase did not stop"; exit 1; }
wt_at chore/rebasing-2 "$work/wt/chore-rebasing-2"; wt_at chore/apply "$work/wt/chore-apply"; wt_at chore/bisect "$work/wt/chore-bisect" # clean branches whose names are a prefix of (or extend) a busy one's
# A bisect that moved HEAD off the branch: the worktree list shows it detached, BISECT_START still holds the branch's name
{ git worktree add -q -b chore/bisecting2 "$work/wt/chore-bisecting2" main && for i in 1 2 3; do echo "$i" > "$work/wt/chore-bisecting2/b$i" && git -C "$work/wt/chore-bisecting2" add . && git -C "$work/wt/chore-bisecting2" commit -q -m "c$i"; done \
  && git -C "$work/wt/chore-bisecting2" push -q origin chore/bisecting2 2>/dev/null; } || exit 1
pr chore/bisecting2 MERGED "$(git -C "$work/wt/chore-bisecting2" rev-parse HEAD)"
git -C "$work/wt/chore-bisecting2" bisect start HEAD HEAD~2 >/dev/null 2>&1
[[ -z "$(git -C "$work/wt/chore-bisecting2" branch --show-current)" ]] || { echo "setup failed: the bisect did not detach HEAD"; exit 1; }
# A hand-deleted worktree whose branch moves while gh is asked: git's entry for it must stay
wt_at chore/stale-race "$work/wt/stale-race"; command rm -rf "$work/wt/stale-race"
# A branch that is an alias (a symbolic ref) of a branch that is kept: only the alias may go
git symbolic-ref refs/heads/chore/sym refs/heads/fix/2-dirty; pr chore/sym MERGED "$(git rev-parse fix/2-dirty)"
# A branch a worktree is bisecting keeps its name in BISECT_START; the worktree list shows nothing for it
wt_at chore/bisecting "$work/wt/chore-bisecting"; git -C "$work/wt/chore-bisecting" bisect start >/dev/null 2>&1
[[ -f "$repo/.git/worktrees/chore-bisecting/BISECT_START" ]] || { echo "setup failed: the bisect did not start"; exit 1; }
# A branch that moves between cleanup's last check and its delete (the shim lands a commit just before update-ref -d)
tip=$(branch chore/race2 yes) || exit 1; pr chore/race2 MERGED "$tip"; race2_tip="$tip"
# The verdict, beyond the single-PR cases
tip=$(branch chore/v-other-main yes) || exit 1;     pr chore/v-other-main MERGED "$tip" feature/9-base; pr chore/v-other-main MERGED "$tip"; remote_only chore/v-other-main
tip=$(branch chore/v-closed-other yes) || exit 1;   pr chore/v-closed-other CLOSED "$tip"; pr chore/v-closed-other MERGED "$tip" develop; remote_only chore/v-closed-other
tip=$(branch chore/v-closed-old yes) || exit 1;     pr chore/v-closed-old CLOSED "$main_init"; remote_only chore/v-closed-old
tip=$(branch chore/v-open-old yes) || exit 1;       pr chore/v-open-old OPEN "$main_init"; pr chore/v-open-old MERGED "$tip"; remote_only chore/v-open-old
# gh is asked for 100 PRs at most: 99 rows are all of them, 100 may have cut the branch's own PR off
tip=$(branch chore/cap99 yes) || exit 1;            pr chore/cap99 MERGED "$tip"; for _ in $(seq 98); do pr chore/cap99 CLOSED "$main_init" main x/y; done; remote_only chore/cap99
tip=$(branch chore/cap100 yes) || exit 1;           pr chore/cap100 MERGED "$tip"; for _ in $(seq 99); do pr chore/cap100 CLOSED "$main_init" main x/y; done; remote_only chore/cap100
# The verdict with its rows the other way round (gh lists the newest PR first): the answer must not depend on the order
tip=$(branch chore/rev-merged-closed yes) || exit 1; pr chore/rev-merged-closed MERGED "$tip"; pr chore/rev-merged-closed CLOSED "$tip"; remote_only chore/rev-merged-closed
tip=$(branch chore/rev-main-other yes) || exit 1;    pr chore/rev-main-other MERGED "$tip"; pr chore/rev-main-other MERGED "$tip" develop; remote_only chore/rev-main-other
tip=$(branch chore/rev-other-closed yes) || exit 1;  pr chore/rev-other-closed MERGED "$tip" develop; pr chore/rev-other-closed CLOSED "$tip"; remote_only chore/rev-other-closed
# A merged branch an open PR is based on (a stacked PR) is kept, local and origin copies alike
tip=$(branch chore/stack-base yes) || exit 1;        pr chore/stack-base MERGED "$tip"; echo 1 > "$GH_FIX/_base_chore_stack-base"
tip=$(branch chore/stack-base-remote yes) || exit 1; pr chore/stack-base-remote MERGED "$tip"; echo 2 > "$GH_FIX/_base_chore_stack-base-remote"; remote_only chore/stack-base-remote

mkdir "$work/shim"
cat > "$work/shim/git" <<SHIM
#!/usr/bin/env bash
if [[ "\$1 \$2 \$3" == "remote get-url origin" && -n "\${SHIM_ORIGIN_URL:-}" ]]; then echo "\$SHIM_ORIGIN_URL"; exit 0; fi
if [[ "\$1" == fetch && -n "\${SHIM_FETCH_FAIL:-}" ]]; then # all: every fetch fails; closing: the first passes and the later ones fail
  n=\$(( \$(cat "\$SHIM_FETCH_COUNT" 2>/dev/null || echo 0) + 1 )); echo "\$n" > "\$SHIM_FETCH_COUNT"
  if [[ "\$SHIM_FETCH_FAIL" == all || \$n -ge 2 ]]; then echo "shim: fetch failed" >&2; exit 128; fi
fi
if [[ "\$1 \$2 \$3 \$4 \$5" == "remote get-url --push --all origin" && -n "\${SHIM_PUSH_EMPTY:-}" ]]; then exit 0; fi
if [[ "\$1 \$2 \$3 \$4 \$5" == "remote get-url --push --all origin" && ( -n "\${SHIM_PUSH_URLS:-}" || -n "\${SHIM_ORIGIN_URL:-}" ) ]]; then printf '%s\n' "\${SHIM_PUSH_URLS:-\$SHIM_ORIGIN_URL}" | tr ';' '\n'; exit 0; fi
if [[ "\$1 \$2 \$3" == "remote get-url --push" && -n "\${SHIM_PUSH_FAIL:-}" ]]; then echo "shim: get-url failed" >&2; exit 128; fi
if [[ "\$1 \$2 \$3" == "update-ref --no-deref -d" && -n "\${SHIM_ADVANCE:-}" && "\$4" == "refs/heads/\$SHIM_ADVANCE" ]]; then # a commit lands on the branch between cleanup's check and its delete
  new=\$("$(command -v git)" commit-tree "\$4^{tree}" -p "\$4" -m late) && "$(command -v git)" update-ref "\$4" "\$new"
fi
if [[ "\$1 \$2 \$3" == "update-ref --no-deref -d" && -n "\${SHIM_VANISH:-}" && "\$4" == "refs/heads/\$SHIM_VANISH" ]]; then "$(command -v git)" update-ref -d "\$4"; [[ -z "\${SHIM_DANGLE:-}" ]] || "$(command -v git)" symbolic-ref "\$4" refs/heads/nowhere; fi # the branch is deleted by someone else first (SHIM_DANGLE: and an alias that points nowhere left in its place)
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
touch "$GH_FIX/_failbase"
out2=$(bash scripts/cleanup.sh 2>&1); st2=$?
rm -f "$GH_FIX/_failbase"
git reset -q --hard "$main_before"
check "a failing lookup of the PRs based on a branch fails the run and removes nothing" "[[ $st2 == 1 ]] && grep -q 'gh pr list --base failed for' <<<\"\$out2\" && has_local feature/1-merged && [[ -d $work/wt/feature-1-merged ]] && has_remote chore/remote-only" # gh-repo: ok
check "a gh failure removes nothing" "has_local feature/1-merged && [[ -d $work/wt/feature-1-merged ]] && has_remote chore/remote-only" # gh-repo: ok

# --dry-run
out=$(bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "dry run succeeds" "[[ $st == 0 ]]"
check "the dry run's summary says so" "grep -q '^Done: [0-9]* removed, [0-9]* kept (dry run)\\.\$' <<<\"\$out\""
o2=$(bash scripts/cleanup.sh --dry-run --include-closed 2>&1)
check "a dry run with --include-closed says it would remove a closed-unmerged branch, and without it keeps it" "grep -q '^remove feature/4-closed ' <<<\"\$o2\" && grep -q 'keep   feature/4-closed: its PR closed unmerged' <<<\"\$out\""
check "dry run changes nothing" "has_local feature/1-merged && [[ -d $work/wt/feature-1-merged ]] && has_remote chore/remote-only && has_remote feature/1-merged && [[ \$(git rev-parse main) == $main_before ]]"
check "a dry run keeps the busy branches as the real run does" "grep -q 'keep   chore/rebasing: a worktree is rebasing or bisecting it' <<<\"\$out\" && grep -q 'keep   chore/apply-busy: a worktree is rebasing' <<<\"\$out\" && grep -q 'keep   chore/bisecting2: a worktree is rebasing' <<<\"\$out\" && ! grep -q '^remove chore/rebasing ' <<<\"\$out\" && grep -q '^remove chore/bisect ' <<<\"\$out\""
check "dry run lists every step" "grep -q 'would: git worktree remove .*feature-1-merged' <<<\"\$out\" && grep -q 'would: git update-ref --no-deref -d refs/heads/feature/1-merged' <<<\"\$out\" && grep -q 'would: git push .*feature/1-merged' <<<\"\$out\" && grep -q 'would: git merge --ff-only origin/main' <<<\"\$out\""

check "a dry run keeps a locked worktree with a missing folder, as the real run does" "grep -q 'keep   chore/stale-locked: its worktree is locked and its folder is missing' <<<\"\$out\" && ! grep -q 'remove chore/stale-locked' <<<\"\$out\" && ! grep -q 'would: git update-ref --no-deref -d refs/heads/chore/stale-locked' <<<\"\$out\""
if [[ "$rel_ok" == true ]]; then check "a dry run finds the entry of a worktree git stored with a relative path" "grep -q 'would: forget the missing worktree .*chore-relative-path' <<<\"\$out\""
elif [[ -n "${CI:-}" ]]; then bad "this git can't read a worktree stored with a relative path, and CI must test it"; else echo "skip - this git can't read a relative worktree path"; fi
check "a dry run says it would forget both hand-deleted worktrees, whichever side of the clean ones they sort" "grep -q 'would: forget the missing worktree .*wt-ord/b' <<<\"\$out\" && grep -q 'would: forget the missing worktree .*wt-ord/d' <<<\"\$out\" && ! grep -q 'keep   chore/ord-' <<<\"\$out\""
check "a dry run forgets no worktree entry, but says it would" "grep -q '^remove chore/stale\$' <<<\"\$out\" && grep -q 'would: forget the missing worktree .*chore-stale\$' <<<\"\$out\" && grep -q 'would: git update-ref --no-deref -d refs/heads/chore/stale ' <<<\"\$out\" && [[ -d $repo/.git/worktrees/chore-stale ]] && has_local chore/stale"

# The owner of origin: github.com URL forms (through the git shim), without regard to case; a PR from "Acme/R" counts for acme/r, not for o/r
check "a PR from a repository of another name is not counted by default" "! grep -q 'remove chore/owner-case' <<<\"\$out\" && grep -q 'keep   chore/owner-case: no PR from origin' <<<\"\$out\""
for url in git@github.com:acme/r.git https://github.com/Acme/R https://tok@github.com/acme/r.git ssh://git@github.com/acme/r https://github.com/acme/r/; do
  o=$(EXPECT_GH_BASE_REPO=acme/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL="$url" bash scripts/cleanup.sh --dry-run 2>&1)
  check "origin $url: a PR from Acme/R counts" "grep -q 'remove chore/owner-case' <<<\"\$o\""
done
for url in ssh://git@github.com:22/acme/r.git https://github.com:443/Acme/R.git/; do
  o=$(EXPECT_GH_BASE_REPO=acme/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL="$url" bash scripts/cleanup.sh --dry-run 2>&1)
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
o=$(EXPECT_GH_BASE_REPO=acme/r EXPECT_GH_REPO=up/r GITHUB_REPOSITORY=up/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=git@github.com:acme/r.git bash scripts/cleanup.sh --dry-run 2>&1)
check "a fork workflow (GH_REPO the upstream, origin the fork) counts the fork's own PR" "grep -q 'remove chore/owner-case' <<<\"\$o\""
echo 1 > "$GH_FIX/_base_chore_owner-case"
o=$(EXPECT_GH_BASE_REPO=acme/r EXPECT_GH_REPO=up/r GITHUB_REPOSITORY=up/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=git@github.com:acme/r.git bash scripts/cleanup.sh --dry-run 2>&1)
command rm -f "$GH_FIX/_base_chore_owner-case"
check "a fork workflow asks origin's repository, not the upstream, for the PRs based on a branch" "grep -q 'keep   chore/owner-case: an open PR is based on it' <<<\"\$o\""

# The repository when GITHUB_REPOSITORY is not set (the usual case on a developer machine): upstream's URL, else origin's
o=$(env -u GITHUB_REPOSITORY EXPECT_GH_REPO=acme/r EXPECT_GH_BASE_REPO=acme/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=git@github.com:acme/r.git bash scripts/cleanup.sh --dry-run 2>&1)
check "with no GITHUB_REPOSITORY, the repository is read from origin's URL" "grep -q 'remove chore/owner-case' <<<\"\$o\""
git remote add upstream https://github.com/up/r.git
o=$(env -u GITHUB_REPOSITORY EXPECT_GH_REPO=up/r EXPECT_GH_BASE_REPO=acme/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=git@github.com:acme/r.git bash scripts/cleanup.sh --dry-run 2>&1)
git remote remove upstream
check "an upstream remote, not origin, names the repository gh is asked about" "grep -q 'remove chore/owner-case' <<<\"\$o\"" # gh-repo: ok
o=$(env -u GITHUB_REPOSITORY PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://git.example.com/acme/r bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "a non-GitHub origin with no GITHUB_REPOSITORY fails, saying why" "[[ $st != 0 ]] && grep -q \"can't tell which repository\" <<<\"\$o\""

# origin's push URLs: a different repository than the fetch URL's is refused before anything is read or removed; the same repository in another URL form is fine
git config remote.origin.pushurl https://github.com/evil/fork.git
o=$(bash scripts/cleanup.sh --dry-run 2>&1); st=$?; git config --unset remote.origin.pushurl
check "a push URL of another repository is refused, saying so" "[[ $st == 1 ]] && grep -q \"origin's push URL (https://github.com/evil/fork.git)\" <<<\"\$o\" && ! grep -q '^remove ' <<<\"\$o\""
git config remote.origin.pushurl "$work/origin.git"; git config --add remote.origin.pushurl https://github.com/evil/fork.git
o=$(bash scripts/cleanup.sh --dry-run 2>&1); st=$?; git config --unset-all remote.origin.pushurl
check "one of several push URLs naming another repository is refused" "[[ $st == 1 ]] && grep -q 'push URL (https://github.com/evil/fork.git)' <<<\"\$o\""
o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://github.com/Acme/R SHIM_PUSH_URLS="git@github.com:acme/r.git;ssh://git@github.com/ACME/r.git" EXPECT_GH_BASE_REPO=acme/r bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "push URLs of the same repository in other forms (and case) are accepted" "[[ $st == 0 ]] && grep -q 'remove chore/owner-case' <<<\"\$o\""
o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://git.example.com/acme/r SHIM_PUSH_URLS="https://git.example.com/other/r" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "push URLs that differ for a non-GitHub origin are refused" "[[ $st == 1 ]] && grep -q 'push URL (https://git.example.com/other/r)' <<<\"\$o\""
for pu in git@github.com:evil/r.git https://github.com/acme/other ssh://git@github.com/acme.git; do
  o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://github.com/acme/r SHIM_PUSH_URLS="$pu" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
  check "a github.com push URL ($pu) of another owner or name than the fetch URL's is refused" "[[ $st == 1 ]] && grep -qF \"push URL ($pu)\" <<<\"\$o\""
done
o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://ghe.example.com/acme/r SHIM_PUSH_URLS="git@ghe.example.com:acme/r.git" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "an ssh push URL for an https fetch URL of a non-github.com host is refused (only the same text is shown to be the same)" "[[ $st == 1 ]] && grep -q 'CLEANUP_PUSH_URL_OK=1' <<<\"\$o\""
o=$(PATH="$work/shim:$PATH" CLEANUP_PUSH_URL_OK=1 SHIM_ORIGIN_URL=https://ghe.example.com/acme/r SHIM_PUSH_URLS="git@ghe.example.com:acme/r.git" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "CLEANUP_PUSH_URL_OK=1 accepts it" "[[ $st == 0 ]]"
o=$(PATH="$work/shim:$PATH" CLEANUP_PUSH_URL_OK=0 SHIM_ORIGIN_URL=https://ghe.example.com/acme/r SHIM_PUSH_URLS="git@ghe.example.com:acme/r.git" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "only CLEANUP_PUSH_URL_OK=1 does: 0 does not" "[[ $st == 1 ]]"
o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://ghe.example.com/acme/r SHIM_PUSH_URLS="https://ghe.example.com/acme/r" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "a push URL with the fetch URL's own text is accepted on any host" "[[ $st == 0 ]]"
for pair in "https://git.example.com/a/team/app|https://git.example.com/b/team/app" "file:///a/acme/r|file:///b/acme/r" "https://[::1]/a/b|https://[::2]/a/b" "/srv/a.git|/srv/b.git" "C:/a/acme/r|C:/b/acme/r"; do
  o=$(PATH="$work/shim:$PATH" GITHUB_REPOSITORY=acme/r SHIM_ORIGIN_URL="${pair%%|*}" SHIM_PUSH_URLS="${pair##*|}" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
  check "different URLs a parser can't read as github.com (${pair%%|*} vs ${pair##*|}) are refused" "[[ $st == 1 ]] && grep -qF \"push URL (${pair##*|})\" <<<\"\$o\""
done
o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://ghe.example.com/acme/r SHIM_PUSH_URLS="git@other.example.com:acme/r.git" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "the same owner/name on another host is refused" "[[ $st == 1 ]] && grep -q 'push URL (git@other.example.com:acme/r.git)' <<<\"\$o\""
o=$(EXPECT_GH_BASE_REPO=acme/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://github.com/acme/r SHIM_PUSH_URLS="ssh://git@ssh.github.com:443/acme/r.git" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "github.com's ssh-over-443 host counts as github.com" "[[ $st == 0 ]]"
for pu in https://github.com/acme/r/ https://www.github.com/acme/r.git https://GitHub.com/ACME/r HTTPS://github.com/acme/r http://github.com/acme/r github.com:acme/r "https://x:y@github.com/acme/r.git/" ssh://git@SSH.GitHub.com:443/acme/r; do
  o=$(EXPECT_GH_BASE_REPO=acme/r PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://github.com/acme/r SHIM_PUSH_URLS="$pu" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
  check "the push URL $pu is read as github.com's acme/r" "[[ $st == 0 ]]"
done
for pu in "https://evil.com#@github.com/acme/r" "https://github.com:/acme/r" "https://github.com/foo/acme/r" "https://github.com/acme/r/extra" "https://github.com/acme/r?x=1" "https://github.com/" "https://ssh.github.com.evil.com/acme/r" "https://user@evil.com/@github.com/acme/r" "file:///github.com/acme/r" "https://[::1]/acme/r" "git@github.com:/acme/r" "https://gitlab.com/a/team/app"; do
  o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://github.com/acme/r SHIM_PUSH_URLS="$pu" bash scripts/cleanup.sh --dry-run 2>&1); st=$?
  check "the push URL $pu is not read as github.com's acme/r" "[[ $st == 1 ]] && grep -qF \"push URL ($pu)\" <<<\"\$o\""
done
o=$(PATH="$work/shim:$PATH" SHIM_PUSH_EMPTY=1 bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "push URLs that print nothing refuse the run too" "[[ $st == 1 ]] && grep -q \"can't read origin's push URLs\" <<<\"\$o\""
o=$(PATH="$work/shim:$PATH" SHIM_PUSH_FAIL=1 bash scripts/cleanup.sh --dry-run 2>&1); st=$?
check "push URLs that can't be read refuse the run" "[[ $st == 1 ]] && grep -q \"can't read origin's push URLs\" <<<\"\$o\""
o=$(PATH="$work/shim:$PATH" SHIM_ORIGIN_URL=https://github.com/acme/r SHIM_PUSH_URLS="git@github.com:evil/r.git" SHIM_FETCH_FAIL=all SHIM_FETCH_COUNT="$work/fc0" bash scripts/cleanup.sh 2>&1); st=$?
check "a refused push URL stops the real run before it fetches or merges anything" "[[ $st == 1 ]] && grep -q 'push URL' <<<\"\$o\" && [[ ! -e $work/fc0 ]] && [[ \$(git rev-parse main) == $main_before ]]"

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
echo "git -C \"$repo\" update-ref refs/heads/chore/stale-race \"\$(git -C \"$repo\" commit-tree 'chore/stale-race^{tree}' -p chore/stale-race -m late)\"" > "$GH_FIX/chore_stale-race.hook"
git fetch -q origin; lease_tip_before=$(git rev-parse origin/chore/lease-race)
# The real run: several removals are refused (a locked worktree, a ref lock, a moved origin tip, a failed ls-remote), so it exits 1 after doing the rest
local_before=$(git for-each-ref refs/heads | wc -l | tr -d ' '); origin_before=$(git ls-remote --heads origin | wc -l | tr -d ' ')
out=$(PATH="$work/shim:$PATH" SHIM_LSREMOTE_FAIL=chore/net-fail SHIM_ADVANCE=chore/race2 bash scripts/cleanup.sh 2>&1); st=$?
check "exits 1 when git refused a removal, after doing the rest" "[[ $st == 1 ]]"
local_after=$(git for-each-ref refs/heads | wc -l | tr -d ' '); origin_after=$(git ls-remote --heads origin | wc -l | tr -d ' ')
removed_n=$(( local_before - local_after + origin_before - origin_after )); kept_n=$(grep -c '^keep ' <<<"$out")
check "the summary counts what was removed and what was kept" "grep -qx 'Done: $removed_n removed, $kept_n kept.' <<<\"\$out\" && [[ $removed_n -gt 5 && $kept_n -gt 5 ]]"
check "the closing fetch prunes the tracking ref of a branch deleted on origin during the run" "! git show-ref --verify --quiet refs/remotes/origin/chore/gone-race"
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
check "keeps a closed-unmerged branch by default, and says how to remove it" "has_local feature/4-closed && has_remote feature/4-closed && grep -q 'keep   feature/4-closed: its PR closed unmerged (use --include-closed)' <<<\"\$out\" && grep -q 'keep   origin/feature/4-closed: its local branch was kept' <<<\"\$out\""
check "keeps a branch with no PR" "has_local chore/nopr"
check "a non-standard name is kept, local and origin, whatever its PR says" "has_local Bad_Local && has_remote Bad_Name && grep -q 'keep   Bad_Local: not a standard branch name' <<<\"\$out\" && grep -q 'keep   origin/Bad_Name: not a standard branch name' <<<\"\$out\""
check "a worktree with only an untracked file is dirty" "has_local fix/10-untracked && has_remote fix/10-untracked && grep -q 'keep   fix/10-untracked: its worktree has uncommitted changes' <<<\"\$out\""
check "a worktree holding an ignored file is removed with it, as documented" "! has_local chore/ignored && ! has_remote chore/ignored && [[ ! -e $work/wt/chore-ignored ]]"
check "a worktree whose path has spaces and a non-ASCII letter is removed" "! has_local chore/spaced && ! has_remote chore/spaced && [[ ! -e '$sp' ]]"
if [[ "$noperm_ok" == true ]]; then check "a worktree whose parent folder can't be searched is kept as out of reach, not forgotten" "has_local chore/noperm && has_remote chore/noperm && grep -q \"keep   chore/noperm: its worktree's folder can't be reached\" <<<\"\$out\" && [[ -d $repo/.git/worktrees/w ]]"; else echo "skip - running as root, or chmod 000 did not make the folder unsearchable, so that arm can't be tried here (the empty and missing parents are tried below)"; fi
check "a worktree entry that is a symlink is never followed or removed" "has_local chore/symlinked && has_remote chore/symlinked && [[ -f $work/elsewhere-entry/gitdir ]] && grep -q 'keep   chore/symlinked: its worktree' <<<\"\$out\""
check "a commit made after the branch was judged keeps the branch, and its origin copy" "has_local chore/local-race && has_remote chore/local-race && [[ \$(git log -1 --format=%s chore/local-race) == late-local ]] && [[ -d $work/wt/chore-local-race ]] && grep -q 'keep   chore/local-race: it has a commit that was made after it was judged' <<<\"\$out\" && grep -q 'keep   origin/chore/local-race: its local branch was kept' <<<\"\$out\" && [[ -n \"\$(git config --get-regexp '^branch\\.chore/local-race\\.')\" ]]"
check "a branch that moves between the last check and the delete is kept with its new commit, and its origin copy" "has_local chore/race2 && has_remote chore/race2 && [[ \$(git rev-parse chore/race2) != $race2_tip ]] && [[ \$(git log -1 --format=%s chore/race2) == late ]] && grep -q 'keep   chore/race2: it has a commit that was made after it was judged' <<<\"\$out\" && grep -q 'keep   origin/chore/race2: its local branch was kept' <<<\"\$out\""
check "a hand-deleted worktree's branch that moves while gh is asked keeps git's entry for the worktree, the branch and its origin copy" "[[ -d $repo/.git/worktrees/stale-race ]] && has_local chore/stale-race && has_remote chore/stale-race && grep -q 'keep   chore/stale-race: it has a commit that was made after it was judged' <<<\"\$out\"" # gh-repo: ok
check "an alias branch (a symbolic ref) is deleted itself, and the branch it points at is left alone" "! has_local chore/sym && has_local fix/2-dirty && grep -q '^remove chore/sym' <<<\"\$out\""
check "a branch a worktree is rebasing with the apply backend is kept, with its origin copy" "has_local chore/apply-busy && has_remote chore/apply-busy && grep -q 'keep   chore/apply-busy: a worktree is rebasing or bisecting it' <<<\"\$out\" && [[ -d $work/wt/chore-apply-busy ]]"
check "a branch whose name only starts like a busy branch's, or is a prefix of one, is removed (the match is exact)" "! has_local chore/rebasing-2 && ! has_remote chore/rebasing-2 && ! has_local chore/apply && ! has_remote chore/apply && ! has_local chore/bisect && ! has_remote chore/bisect"
check "a branch a worktree is bisecting with HEAD off the branch is kept, with its origin copy" "has_local chore/bisecting2 && has_remote chore/bisecting2 && grep -q 'keep   chore/bisecting2: a worktree is rebasing or bisecting it' <<<\"\$out\""
check "a branch a worktree is bisecting is kept, with its origin copy" "has_local chore/bisecting && has_remote chore/bisecting && grep -q 'keep   chore/bisecting: a worktree is rebasing or bisecting it' <<<\"\$out\" && [[ -d $work/wt/chore-bisecting ]]"
check "a branch a worktree is rebasing is kept, with its origin copy" "has_local chore/rebasing && has_remote chore/rebasing && grep -q 'keep   chore/rebasing: a worktree is rebasing or bisecting it' <<<\"\$out\" && [[ -d $work/wt/chore-rebasing ]]"
check "a worktree whose parent folder is empty is kept as maybe out of reach, with the prune hint, and its entry kept" "has_local chore/only-child && has_remote chore/only-child && [[ -d $repo/.git/worktrees/oc ]] && grep -q \"keep   chore/only-child: its worktree's folder can't be reached\" <<<\"\$out\" && grep -q 'git worktree prune' <<<\"\$out\""
check "hand-deleted worktrees are forgotten the same in the real run, whichever side of the clean ones they sort" "! has_local chore/ord-b-stale && ! has_remote chore/ord-b-stale && ! has_local chore/ord-d-stale && ! has_remote chore/ord-d-stale && ! has_local chore/ord-a-clean && ! has_local chore/ord-c-clean"
check "a hand-deleted worktree whose folder name ends in a space is forgotten, not kept as out of reach" "! has_local chore/trail-space && ! has_remote chore/trail-space"
check "a worktree whose parent is a file, not a folder, is kept the same way" "has_local chore/parent-file && has_remote chore/parent-file && [[ -d $repo/.git/worktrees/pf ]] && grep -q \"keep   chore/parent-file: its worktree's folder can't be reached\" <<<\"\$out\""
check "a worktree whose parent folder is gone is kept the same way" "has_local chore/parent-gone && has_remote chore/parent-gone && [[ -d $repo/.git/worktrees/pg ]] && grep -q \"keep   chore/parent-gone: its worktree's folder can't be reached\" <<<\"\$out\""
check "a PR merged into another base, then one into main, at the same tip: merged" "! has_remote chore/v-other-main"
check "a closed PR, then one merged into another base: kept, as merged elsewhere" "has_remote chore/v-closed-other && grep -q 'keep   origin/chore/v-closed-other: its PR merged into develop, not main' <<<\"\$out\""
check "a closed PR at an older tip is not this branch's PR" "has_remote chore/v-closed-old"
check "MERGED then CLOSED at one tip (newest first): merged, removed without --include-closed" "! has_remote chore/rev-merged-closed"
check "MERGED into main then MERGED into develop at one tip: merged, removed" "! has_remote chore/rev-main-other"
check "MERGED into develop then CLOSED at one tip: kept as merged elsewhere" "has_remote chore/rev-other-closed && grep -q 'keep   origin/chore/rev-other-closed: its PR merged into develop, not main' <<<\"\$out\""
check "a branch an open PR is based on is kept, and so is its origin copy" "has_local chore/stack-base && has_remote chore/stack-base && grep -q 'keep   chore/stack-base: an open PR is based on it' <<<\"\$out\" && grep -q 'keep   origin/chore/stack-base: its local branch was kept' <<<\"\$out\""
check "an origin-only branch an open PR is based on is kept" "has_remote chore/stack-base-remote && grep -q 'keep   origin/chore/stack-base-remote: an open PR is based on it' <<<\"\$out\""
check "an open PR keeps the branch whatever tip it has" "has_remote chore/v-open-old && grep -q 'keep   origin/chore/v-open-old: its PR is open' <<<\"\$out\""
check "99 PRs of that branch name are all of them: the merged one counts" "! has_remote chore/cap99"
check "100 PRs of that branch name may hide its own: kept" "has_remote chore/cap100 && grep -q 'keep   origin/chore/cap100: 100 or more PRs' <<<\"\$out\""
check "does not mistake origin/HEAD or main for a branch to judge" "[[ -f $repo/.git/refs/remotes/origin/HEAD ]] && ! grep -q 'origin/HEAD' <<<\"\$out\" && ! grep -q 'origin/main' <<<\"\$out\" && ! grep -q 'keep   main' <<<\"\$out\""
check "says why it kept one" "grep -q 'keep   fix/2-dirty: its worktree has uncommitted changes' <<<\"\$out\" && grep -q 'keep   origin/fix/2-dirty: its local branch was kept' <<<\"\$out\""
check "ignores a PR from a fork with the same branch name" "has_remote chore/forked && grep -q 'keep   origin/chore/forked: no PR from origin' <<<\"\$out\""
check "keeps a branch whose PR merged into another branch" "has_remote chore/stacked && grep -q 'merged into feature/9-base, not main' <<<\"\$out\""
check "an open PR beside a merged one keeps the branch" "has_local chore/open-and-merged && has_remote chore/open-and-merged"
check "a closed PR beside a merged one at the same tip counts as merged" "! has_local chore/closed-then-merged && ! has_remote chore/closed-then-merged"
check "keeps a locked worktree's branch and says git refused" "has_local chore/locked && has_remote chore/locked && grep -q 'keep   chore/locked: git could not remove its worktree' <<<\"\$out\""
check "an origin branch deleted by GitHub since the fetch counts as removed, not as a failure" "! has_remote chore/gone-race && ! grep -q 'keep   origin/chore/gone-race' <<<\"\$out\""
check "keeps an origin branch that moved after the fetch" "has_remote chore/lease-race && [[ \$(git rev-parse origin/chore/lease-race) != $lease_tip_before ]] && grep -q 'keep   origin/chore/lease-race: git could not delete it' <<<\"\$out\""

# A later run, once what git refused is out of the way, finishes the job and exits 0
chmod 755 "$work/wt-np" 2>/dev/null; git -C "$work/wt/chore-rebasing" rebase --abort >/dev/null 2>&1; git -C "$work/wt/chore-bisecting" bisect reset >/dev/null 2>&1; git -C "$work/wt/chore-bisecting2" bisect reset >/dev/null 2>&1; git -C "$work/wt/chore-apply-busy" rebase --abort >/dev/null 2>&1; git worktree prune; git worktree unlock "$work/wt/chore-locked"; command rm -f "$repo/.git/worktrees/chore-unreadable/index"; git -C "$work/wt/chore-unreadable" read-tree HEAD; git worktree unlock "$work/wt/chore-stale-locked"; command rm -f "$repo/.git/refs/heads/chore/delfail.lock"
bash scripts/cleanup.sh --include-closed >/dev/null 2>&1; st=$?
check "a later run with nothing refused exits 0" "[[ $st == 0 ]]"
check "--include-closed removes a closed-unmerged branch" "! has_local feature/4-closed && ! has_remote feature/4-closed"
check "an unlocked worktree with a missing folder is forgotten by the later run, and its branch goes" "! has_local chore/stale-locked && ! has_remote chore/stale-locked && [[ ! -d $repo/.git/worktrees/chore-stale-locked ]]"
check "a worktree out of reach is removed once it is reachable again" "! has_local chore/noperm && ! has_remote chore/noperm"
check "after git worktree prune, the branches kept for an empty or missing parent are removed" "! has_local chore/only-child && ! has_remote chore/only-child && ! has_local chore/parent-gone && ! has_remote chore/parent-gone && ! has_local chore/parent-file && ! has_remote chore/parent-file"
check "a branch whose rebase was aborted is removed by the later run" "! has_local chore/rebasing && ! has_remote chore/rebasing"
check "branches whose bisect was reset or rebase aborted are removed by the later run" "! has_local chore/bisecting && ! has_remote chore/bisecting && ! has_local chore/bisecting2 && ! has_remote chore/bisecting2 && ! has_local chore/apply-busy && ! has_remote chore/apply-busy"
check "a branch that moved keeps its new commit for good: no PR has that tip, so the later run keeps it too" "has_local chore/race2 && has_remote chore/race2"
check "the branches git had refused are removed by the later run" "! has_local chore/locked && ! has_remote chore/locked && ! has_local chore/delfail && ! has_remote chore/delfail && ! has_local chore/unreadable && ! has_remote chore/unreadable"

check "--include-closed still keeps a branch merged elsewhere, and one an open PR is based on" "has_remote chore/rev-other-closed && has_local chore/stack-base && has_remote chore/stack-base && has_remote chore/stack-base-remote"

# Each documented cause of exit 1, alone: the run exits 1, and still removes a clean merged branch beside it
scrub() { # scrub <branch>: undo whatever a scenario left, so the next one starts clean
  local d="$work/wt/${1//\//-}"
  git worktree unlock "$d" 2>/dev/null; git worktree remove -f -f "$d" 2>/dev/null; command rm -f "$repo/.git/refs/heads/$1.lock"
  git branch -q -D "$1" 2>/dev/null; git -C "$work/other" push -q origin --delete "$1" 2>/dev/null; git fetch -q --prune origin
}
alone() { # alone <label> <the keep line it must print> [<branch ls-remote fails for>]: run once with only that cause, with a clean sibling beside it
  tip=$(branch chore/x-ok yes) || exit 1; pr chore/x-ok MERGED "$tip"; remote_only chore/x-ok
  o=$(PATH="$work/shim:$PATH" SHIM_LSREMOTE_FAIL="${3:-}" bash scripts/cleanup.sh 2>&1); st=$?
  check "$1: exit 1 for that cause, and the clean branch beside it is still removed" "[[ $st == 1 ]] && grep -qF -- '$2' <<<\"\$o\" && ! has_remote chore/x-ok"
}
tip=$(branch chore/x-locked yes) || exit 1; pr chore/x-locked MERGED "$tip"; git worktree lock "$work/wt/chore-x-locked"
alone "a worktree git won't remove" 'keep   chore/x-locked: git could not remove its worktree'; scrub chore/x-locked
tip=$(branch chore/x-lock yes) || exit 1; pr chore/x-lock MERGED "$tip"; git worktree remove "$work/wt/chore-x-lock"; touch "$repo/.git/refs/heads/chore/x-lock.lock"
alone "a branch git won't delete" 'keep   chore/x-lock: git could not delete the branch'; scrub chore/x-lock
tip=$(branch chore/x-late yes) || exit 1; pr chore/x-late MERGED "$tip"; echo "git -C \"$work/wt/chore-x-late\" commit -q --allow-empty -m late" > "$GH_FIX/chore_x-late.hook"
alone "a commit made after the branch was judged" 'keep   chore/x-late: it has a commit that was made after it was judged'; scrub chore/x-late
tip=$(branch chore/x-moved yes) || exit 1; pr chore/x-moved MERGED "$tip"; remote_only chore/x-moved
cat > "$GH_FIX/chore_x-moved.hook" <<HOOK
git -C "$work/other" fetch -q origin && git -C "$work/other" checkout -q -B tmp origin/chore/x-moved && echo m > "$work/other/m" \
  && git -C "$work/other" add m && git -C "$work/other" commit -q -m m && git -C "$work/other" push -q origin tmp:refs/heads/chore/x-moved
HOOK
alone "an origin branch that moved after the fetch" 'keep   origin/chore/x-moved: git could not delete it (it may have moved'; scrub chore/x-moved
tip=$(branch chore/x-ls yes) || exit 1; pr chore/x-ls MERGED "$tip"; remote_only chore/x-ls
cat > "$GH_FIX/chore_x-ls.hook" <<HOOK
git -C "$work/other" fetch -q origin && git -C "$work/other" checkout -q -B tmp origin/chore/x-ls && echo l > "$work/other/l" \
  && git -C "$work/other" add l && git -C "$work/other" commit -q -m l && git -C "$work/other" push -q origin tmp:refs/heads/chore/x-ls
HOOK
alone "an origin check that failed" 'keep   origin/chore/x-ls: git could not delete it, and origin could not be checked' chore/x-ls; scrub chore/x-ls
tip=$(branch chore/x-busy yes) || exit 1; pr chore/x-busy MERGED "$tip"
( cd "$work/wt/chore-x-busy" && GIT_SEQUENCE_EDITOR="sed -i.bak 's/^pick/edit/'" git rebase -q -i -f main >/dev/null 2>&1 )
alone "a branch a worktree is rebasing" 'keep   chore/x-busy: a worktree is rebasing or bisecting it'; scrub chore/x-busy
o=$(bash scripts/cleanup.sh 2>&1); st=$?
check "once those are scrubbed, the run exits 0 again" "[[ $st == 0 ]]"

# A branch someone else deleted between cleanup's last check and its own delete is gone, which is what was wanted
tip=$(branch chore/x-gone yes) || exit 1; pr chore/x-gone MERGED "$tip"
o=$(PATH="$work/shim:$PATH" SHIM_VANISH=chore/x-gone bash scripts/cleanup.sh 2>&1); st=$?
check "a branch already deleted by someone else counts as removed, not as a late commit" "[[ $st == 0 ]] && ! has_local chore/x-gone && ! has_remote chore/x-gone && grep -q 'chore/x-gone is already gone' <<<\"\$o\" && ! grep -q 'made after it was judged' <<<\"\$o\" && ! git config --get-regexp '^branch\\.chore/x-gone\\.' >/dev/null"
# ...but an alias left in its place is not "gone": the branch is still there
tip=$(branch chore/x-dangle yes) || exit 1; pr chore/x-dangle MERGED "$tip"
o=$(PATH="$work/shim:$PATH" SHIM_VANISH=chore/x-dangle SHIM_DANGLE=1 bash scripts/cleanup.sh 2>&1); st=$?
check "an alias that points nowhere in a deleted branch's place is kept, not counted as gone" "[[ $st == 1 ]] && grep -q 'keep   chore/x-dangle: git could not delete the branch' <<<\"\$o\" && ! grep -q 'x-dangle is already gone' <<<\"\$o\""
git update-ref --no-deref -d refs/heads/chore/x-dangle; scrub chore/x-dangle
# A branch deleted by someone else while gh was being asked is kept, with its worktree
wt_at chore/x-deleted "$work/wt/x-deleted"; command rm -rf "$work/wt/x-deleted" # a hand-deleted worktree: only git's entry is left
echo "git -C \"$repo\" update-ref -d refs/heads/chore/x-deleted" > "$GH_FIX/chore_x-deleted.hook"
o=$(bash scripts/cleanup.sh 2>&1); st=$?
check "a branch deleted while it was being judged is kept with git's entry for its worktree, saying so, and fails the run" "[[ $st == 1 ]] && grep -q 'keep   chore/x-deleted: it was deleted after it was judged' <<<\"\$o\" && [[ -d $repo/.git/worktrees/x-deleted ]]"
scrub chore/x-deleted

# The fetch of origin failing: the opening one stops the run, the closing one fails it after the rest was done
tip=$(branch chore/closing yes) || exit 1; pr chore/closing MERGED "$tip"
o=$(PATH="$work/shim:$PATH" SHIM_FETCH_FAIL=all SHIM_FETCH_COUNT="$work/fc1" bash scripts/cleanup.sh 2>&1); st=$?
check "refuses when origin can't be fetched, and removes nothing" "[[ $st != 0 ]] && grep -q \"can't fetch origin\" <<<\"\$o\" && has_local chore/closing && has_remote chore/closing"
o=$(PATH="$work/shim:$PATH" SHIM_FETCH_FAIL=closing SHIM_FETCH_COUNT="$work/fc2" bash scripts/cleanup.sh 2>&1); st=$?
check "a failed closing fetch is reported and fails the run, after the rest was done" "[[ $st == 1 ]] && grep -q 'the closing fetch of origin failed' <<<\"\$o\" && ! has_local chore/closing && ! has_remote chore/closing && grep -q '^Done: ' <<<\"\$o\""

echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
