#!/usr/bin/env bash
# The parity suite for the bench's --rescore (bench/claude-review-bench.cs, ADR 0017's Update for mpaulosky/Application_Factory#161): what it prints
# and the bytes it writes back into each results file, through its command line, so the same cases judge any implementation of it. The fixtures in
# rescore/ were recorded from claude-review-bench.py, the Python it was ported from, deleted once this passed against the C#; rescore/README.md says
# what they cover.
#
#   bash bench/tests/rescore.test.sh                     the C#: built once, then run as dotnet <its dll>
#   bash bench/tests/rescore.test.sh <command> [arg]...  another implementation; a word that names a file is made absolute
#   RESCORE_RECORD=1 bash bench/tests/rescore.test.sh    record rescore/expected.stdout and rescore/expected/ from the C#, after the cases file
#                                                        changes; review the diff before committing
#
# Each run is from a folder holding a copy of rescore/input/, with --root naming a folder that holds one clone, fixture/, built here (two commits),
# so a finding's line is checked against a real head; a case whose repo isn't there gets bad_lines null. The cases file is the bench's own.
set -uo pipefail

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
bench="$(dirname -- "$here")"
work="$(mktemp -d)" || exit 2
[[ -n "$work" && -d "$work" ]] || exit 2
[[ "$work" == /* ]] || work="$PWD/$work"
trap 'command rm -rf -- "$work"' EXIT
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
if (($#)); then
  [[ -z "${RESCORE_RECORD:-}" ]] || {
    echo "rescore.test.sh: RESCORE_RECORD records from the C# only, so an implementation under test never records its own oracle" >&2
    exit 2
  }
  bench_cmd=()
  for word in "$@"; do
    [[ "$word" == /* || ! -e "$word" ]] || word="$PWD/$word"
    bench_cmd+=("$word")
  done
else
  # Built once, from the bench's folder, into this run's own folder (ADR 0017, How a script runs); then run as dotnet <dll>.
  dll="$work/build/bin/claude-review-bench.cs/debug/claude-review-bench.dll"
  (cd -- "$bench" && dotnet build claude-review-bench.cs --artifacts-path "$work/build" -p:UseSharedCompilation=false > "$work/build.log" 2>&1) && [[ -f "$dll" ]] || {
    cat "$work/build.log" >&2
    echo "rescore.test.sh: the build of claude-review-bench.cs failed, or left no $dll" >&2
    exit 2
  }
  bench_cmd=(dotnet "$dll")
fi
PASSED=0
FAILED=0
check() { if eval "$2"; then
  PASSED=$((PASSED + 1))
  echo "ok   - $1"
else
  FAILED=$((FAILED + 1))
  echo "FAIL - $1"
fi; }

# The clone: release_post.py of three lines at HEAD, claude-review.yml of two with no line break at its end, and HEAD~1 before them.
git_fixed=(env -u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1
  GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@example.com GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@example.com
  GIT_AUTHOR_DATE=2026-01-01T00:00:00Z GIT_COMMITTER_DATE=2026-01-01T00:00:00Z git -c core.hooksPath=/dev/null -c commit.gpgsign=false)
clone="$work/root/fixture"
mkdir -p "$clone"
"${git_fixed[@]}" -C "$clone" init -q -b main
printf 'first\n' > "$clone/README.md"
"${git_fixed[@]}" -C "$clone" add -A
"${git_fixed[@]}" -C "$clone" commit -q -m base
printf 'a\nb\nc\n' > "$clone/release_post.py"
printf 'on: push\njobs: {}' > "$clone/claude-review.yml"
"${git_fixed[@]}" -C "$clone" add -A
"${git_fixed[@]}" -C "$clone" commit -q -m head
[[ "$("${git_fixed[@]}" -C "$clone" rev-list --count HEAD)" == 2 ]] || {
  echo "rescore.test.sh: couldn't build the fixture clone in $clone" >&2
  exit 2
}

mkdir -p "$work/run"
cp "$here/rescore/input/"*.json "$work/run/"
names=()
for f in "$here/rescore/input/"*.json; do names+=("$(basename -- "$f")"); done
st=0
(cd -- "$work/run" && "${bench_cmd[@]}" --root "$work/root" --rescore "${names[@]}" > "$work/stdout" 2> "$work/stderr" < /dev/null) || st=$?

if [[ -n "${RESCORE_RECORD:-}" ]]; then
  ((st == 0)) || {
    cat "$work/stderr" >&2
    echo "rescore.test.sh: the C# exited $st, so nothing was recorded" >&2
    exit 1
  }
  cp "$work/stdout" "$here/rescore/expected.stdout"
  rm -rf "$here/rescore/expected"
  mkdir -p "$here/rescore/expected"
  cp "$work/run/"*.json "$here/rescore/expected/"
  echo "recorded rescore/expected.stdout and rescore/expected/ (${#names[@]} file(s))"
  exit 0
fi

check "--rescore exits 0 with nothing on stderr" "[[ \$st == 0 && ! -s '$work/stderr' ]]"
check "it prints what the Python printed, byte for byte" "cmp -s '$work/stdout' '$here/rescore/expected.stdout'"
for name in "${names[@]}"; do
  check "it writes $name back as the Python did, byte for byte" "cmp -s '$work/run/$name' '$here/rescore/expected/$name'"
done
cmp -s "$work/stdout" "$here/rescore/expected.stdout" || diff "$here/rescore/expected.stdout" "$work/stdout" | head -20

# A usage error is exit 2, and a results file that isn't there fails the run, as the Python's argparse and traceback did.
for bad in "--rescore" "--timeout x --rescore a.json" "--bogus" "stray"; do
  read -ra words <<< "$bad"
  st=0
  (cd -- "$work/run" && "${bench_cmd[@]}" "${words[@]}" > /dev/null 2> "$work/stderr" < /dev/null) || st=$?
  check "$bad is a usage error (exit 2)" "[[ \$st == 2 ]] && grep -q '^usage: ' '$work/stderr'"
done
st=0
(cd -- "$work/run" && "${bench_cmd[@]}" --root "$work/root" --rescore missing.json > /dev/null 2>&1 < /dev/null) || st=$?
check "a results file that isn't there is exit 1" "[[ \$st == 1 ]]"

echo "rescore.test.sh: $PASSED passed, $FAILED failed"
((FAILED == 0))
