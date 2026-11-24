#!/usr/bin/env bash
# The parity suite for the bare-#N check (tests/check-issue-refs.cs, ADR 0017): its exit codes, stdout and stderr through its command line,
# on folders of fixtures, so the same cases judge any implementation of it.
#
#   bash tests/check-issue-refs.test.sh                     the C#: built once, then run as dotnet <its dll> from this folder
#   bash tests/check-issue-refs.test.sh <command> [arg]...  another implementation (the Python it was ported from was
#                                                           python3 -I tests/check-issue-refs.py); a word that names a file is made absolute
#
# Each run gets the folder as its one argument (absolute), and stdin from /dev/null, with 20 seconds before it counts as hung (a FIFO it opens
# would block), and a KILL 5 seconds after the TERM, which a .NET app blocked in open() outlives. The Python ran the same rule; the C# exits 2 where an uncaught Python error exited 1, which Scripts.Tests checks, since no input
# here crashes either. The unreadable file and folder cases need a user other than root. A file name that isn't UTF-8 isn't covered.
# shellcheck disable=SC2016,SC2034 # the fixtures are text that must not expand; $out, $err and $st are read inside the checks' eval strings
set -uo pipefail

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)" || exit 2
[[ -n "$work" && -d "$work" ]] || exit 2
[[ "$work" == /* ]] || work="$PWD/$work"
trap 'chmod -R u+rwx "$work" 2> /dev/null; command rm -rf -- "$work"' EXIT
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
if (($#)); then
  checker=()
  for word in "$@"; do
    [[ "$word" == /* || ! -e "$word" ]] || word="$PWD/$word"
    checker+=("$word")
  done
else
  # Built once, from this folder, into this run's own folder, so no case times its build and two runs can't build into one folder (ADR 0017,
  # How a script runs); then run as dotnet <dll>, which starts in about a tenth of the time dotnet run takes.
  dll="$work/build/bin/check-issue-refs.cs/debug/check-issue-refs.dll"
  (cd -- "$here" && dotnet build check-issue-refs.cs --artifacts-path "$work/build" -p:UseSharedCompilation=false > "$work/build.log" 2>&1) && [[ -f "$dll" ]] || {
    cat "$work/build.log" >&2
    echo "check-issue-refs.test.sh: the build of check-issue-refs.cs failed, or left no $dll" >&2
    exit 2
  }
  checker=(dotnet "$dll")
fi
PASSED=0
FAILED=0
check() { if eval "$2"; then
  PASSED=$((PASSED + 1))
  echo "ok   - $1"
else
  FAILED=$((FAILED + 1))
  echo "FAIL - $1"
  printf '       exit %s, stdout: %q, stderr: %q\n' "$st" "$out" "$err"
fi; }

# file <name> <relative file> <printf format>: the file $work/<name>/<relative file>, its bytes printf's of the format
file() {
  mkdir -p "$work/$1/$(dirname -- "$2")"
  # shellcheck disable=SC2059 # the format is the fixture, escapes and all
  printf "$3" > "$work/$1/$2"
}
# run_checker [arg]...: exit status in $st, stdout in $out, stderr in $err, from this folder (so dotnet picks the Factory's SDK)
run_checker() {
  out="$(cd -- "$here" && timeout -k 5 20 "${checker[@]}" "$@" 2> "$work/stderr" < /dev/null)"
  st=$?
  err="$(cat "$work/stderr")"
}
nl=$'\n'

file prose docs/a.md 'fine\nSee #5 for why.\n'
run_checker "$work/prose"
check "a bare reference in prose is exit 1, printed with its path and line" "[[ \$st == 1 && \$out == 'docs/a.md:2: See #5 for why.' && -z \$err ]]"
file qualified a.md 'See mpaulosky/dotfiles#5 and Application_Factory#6.\n'
run_checker "$work/qualified"
check "a repo-qualified reference passes, printing nothing" "[[ \$st == 0 && -z \$out && -z \$err ]]"
file citation scripts/tests/t.sh '# a fix (#5)\n'
run_checker "$work/citation"
check "a parenthesised citation is found even under tests" "[[ \$st == 1 && \$out == 'scripts/tests/t.sh:1: # a fix (#5)' ]]"
file citation-list a.md 'Fixed (mpaulosky/dotfiles#4, #5).\n'
run_checker "$work/citation-list"
check "a citation that lists a bare #N after a comma is found" "[[ \$st == 1 ]]"
file fixture-data scripts/tests/t.sh "echo '#5 is fixture data'\n"
run_checker "$work/fixture-data"
check "a bare reference under tests outside a citation passes" "[[ \$st == 0 && -z \$out ]]"
file tests-file sub/tests 'See #5\n'
run_checker "$work/tests-file"
check "a file named tests counts as under tests" "[[ \$st == 0 ]]"
file ts-string x.test.mts 'const s = "see #5";\n'
run_checker "$work/ts-string"
check "a Sandcastle test's string passes" "[[ \$st == 0 ]]"
file ts-comment y.test.mts '  // see #5\n'
run_checker "$work/ts-comment"
check "a Sandcastle test's // comment line is checked, and printed stripped" "[[ \$st == 1 && \$out == 'y.test.mts:1: // see #5' ]]"
file examples a.md 'Write `Fixes #123` and &#9; here, "1, #3" and so PR #1 doesn'"'"'t.\n'
run_checker "$work/examples"
check "the listed examples and HTML entities pass" "[[ \$st == 0 ]]"
file longer a.md 'Fixes #421\n'
run_checker "$work/longer"
check "an example does not hide a longer number" "[[ \$st == 1 ]]"
file word a.md 'a#5 x/#5 _#5 #5a #5_\n'
run_checker "$work/word"
check "a #N after a word character, / or &, or followed by one, is not a reference" "[[ \$st == 0 ]]"
file several a.md 'one #1\ntwo\n'
file several b/c.md 'three #3\n'
run_checker "$work/several"
check "every finding is printed, one per line, in path order" "[[ \$st == 1 && \$out == 'a.md:1: one #1${nl}b/c.md:1: three #3' ]]"

# exit 2: no folder, no argument, more than one
run_checker "$work/nope"
check "a missing folder is exit 2, with a usage line and no stdout" "[[ \$st == 2 && -z \$out && \$err == usage:* ]]"
run_checker
check "no argument is exit 2" "[[ \$st == 2 && -z \$out && \$err == usage:* ]]"
run_checker "$work/prose" extra
check "a second argument is exit 2" "[[ \$st == 2 && -z \$out && \$err == usage:* ]]"
run_checker "$work/prose/docs/a.md"
check "a file, not a folder, is exit 2" "[[ \$st == 2 && -z \$out ]]"
run_checker "$here/../template/owned"
check "the Template's own Owned files pass" "[[ \$st == 0 && -z \$out && -z \$err ]]"

# the order: sorted() on paths compares a segment at a time, so a/x comes before a-b/x, the reverse of a plain string sort
file order a-b/x.md 'See #2\n'
file order a/x.md 'See #1\n'
file order a.md 'See #0\n'
file order B.md 'See #3\n'
run_checker "$work/order"
check "paths are in pathlib's order: by segment (a, then a-b, then a.md), by code point" "[[ \$st == 1 && \$out == 'B.md:1: See #3${nl}a/x.md:1: See #1${nl}a-b/x.md:1: See #2${nl}a.md:1: See #0' ]]"
file order-astral $'\U0001F600.md' 'See #1\n'
file order-astral $'�.md' 'See #2\n'
run_checker "$work/order-astral"
check "an astral name sorts after U+FFFD, by code point not UTF-16" "[[ \$st == 1 && \$out == $'�.md:1: See #2\n\U0001F600.md:1: See #1' ]]"

# lines: str.splitlines() ends a line at more than \n, and str.strip() takes more than ASCII space
file lines a.md 'a\vb\fc\x1cd\x1de\x1ef\xc2\x85g\xe2\x80\xa8h\xe2\x80\xa9i\r\nj\rSee #5\n'
run_checker "$work/lines"
check "a line ends at \\v \\f \\x1c-\\x1e U+0085 U+2028 U+2029 \\r\\n and \\r, as splitlines() ends one" "[[ \$st == 1 && \$out == 'a.md:11: See #5' ]]"
file strip a.md '\t\x1f\xc2\xa0See #5\xe3\x80\x80\x1f \n'
run_checker "$work/strip"
check "a line is printed stripped of what str.isspace() calls space (\\x1f, NBSP, U+3000)" "[[ \$st == 1 && \$out == 'a.md:1: See #5' ]]"
file bom a.md '\xef\xbb\xbf#5\n'
run_checker "$work/bom"
check "a BOM is part of the first line, and no word character" "[[ \$st == 1 && \$out == $'a.md:1: ﻿#5' ]]"
file mark a.md 'e\xcc\x81#5\n'
run_checker "$work/mark"
check "a combining mark is no word character, so the #N after it is found" "[[ \$st == 1 ]]"
file astral-letter a.md '\xf0\x9d\x90\x80#5\n'
run_checker "$work/astral-letter"
check "an astral letter is a word character, so the #N after it is not a reference" "[[ \$st == 0 ]]"
file astral-digit a.md 'See #\xf0\x9d\x9f\x93.\n'
run_checker "$work/astral-digit"
check "an astral decimal digit is a digit, so #<it> is a reference" "[[ \$st == 1 ]]"
file astral-example a.md 'Fixes #42\xf0\x9d\x9f\x93\n'
run_checker "$work/astral-example"
check "an example followed by an astral digit is not the example" "[[ \$st == 1 ]]"
file arabic a.md 'See #\xd9\xa5\n'
run_checker "$work/arabic"
check "a non-ASCII decimal digit is a digit" "[[ \$st == 1 ]]"

# what can't be read as text, or resolves to nothing, is skipped without hiding a real finding
file utf8 bad.md 'See #5 \xff\n'
file utf8 good.md 'See #6\n'
run_checker "$work/utf8"
check "a file that isn't UTF-8 is skipped, and the others still checked" "[[ \$st == 1 && \$out == 'good.md:1: See #6' && -z \$err ]]"
file surrogate a.md 'See #5 \xed\xa0\x80\n'
run_checker "$work/surrogate"
check "UTF-8 for a surrogate code point is not UTF-8" "[[ \$st == 0 ]]"
file links a.md 'fine\n'
file links real.md 'See #5\n'
ln -s "$work/links/missing.md" "$work/links/dangling.md"
ln -s loop.md "$work/links/loop.md"
ln -s "$work/links/a.md/x" "$work/links/through.md"
ln -s real.md "$work/links/to-real.md"
run_checker "$work/links"
check "a dangling, looping or through-a-file link is skipped, a link to a file is read" "[[ \$st == 1 && \$out == 'real.md:1: See #5${nl}to-real.md:1: See #5' && -z \$err ]]"
file dir-link elsewhere/a.md 'See #5\n'
mkdir -p "$work/dir-link/owned"
ln -s ../elsewhere "$work/dir-link/owned/linked"
run_checker "$work/dir-link/owned"
check "a link to a folder is not followed" "[[ \$st == 0 && -z \$out ]]"
file fifo a.md 'fine\n'
mkfifo "$work/fifo/pipe.md"
run_checker "$work/fifo"
check "a FIFO is skipped, not opened (which would block)" "[[ \$st == 0 && -z \$out && -z \$err ]]"
file empty a.md ''
run_checker "$work/empty"
check "an empty file passes" "[[ \$st == 0 ]]"

if [[ "$(id -u)" != 0 ]]; then
  file unreadable a.md 'See #5\n'
  file unreadable b.md 'fine\n'
  chmod 000 "$work/unreadable/b.md"
  run_checker "$work/unreadable"
  check "an unreadable file is exit 2, named, and hides the findings found before it" "[[ \$st == 2 && -z \$out && \$err == *b.md* ]]"
  file unlistable sub/b.md 'See #5\n'
  file unlistable a.md 'fine\n'
  chmod 000 "$work/unlistable/sub"
  run_checker "$work/unlistable"
  check "a folder that can't be listed is exit 2 and named, not skipped" "[[ \$st == 2 && -z \$out && \$err == *\"\$work/unlistable/sub\"* ]]"
  file unlistable-root a.md 'See #5\n'
  chmod 000 "$work/unlistable-root"
  run_checker "$work/unlistable-root"
  check "a folder given that can't be listed is exit 2" "[[ \$st == 2 && -z \$out && \$err == *\"\$work/unlistable-root\"* ]]"
  file unsearchable sub/b.md 'See #5\n'
  file unsearchable a.md 'fine\n'
  chmod 600 "$work/unsearchable/sub"
  run_checker "$work/unsearchable"
  check "a folder that lists but can't be searched is exit 2, naming the file" "[[ \$st == 2 && -z \$out && \$err == *b.md* ]]"
else
  echo "skip - the unreadable file and folder cases need a user other than root"
fi
echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
