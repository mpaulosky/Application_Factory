# The draft_release parity suite

The Template's `.github/scripts/draft_release.py` through its command line: `tests/parity.test.sh draft_release [command...]`. The runner's header says what a
case holds. The cases were recorded from the `.py` and pass against `draft_release.cs` too, which `test.sh` runs them against while the `.py` ships beside it.
Each case gives the draft's body on stdin and the release notes in its tree's `notes.txt`; the run must leave that file as it was.

The cases cover a marker on a line of its own (with spaces and tabs around it, or twice), no marker, an empty draft, a marker inside a longer line, a draft with
no final newline, notes that lose only their trailing newlines, a draft naming another PR (a longer number, or another PR beside this one; with and without
`--tag`), a marker whose number is this PR's only as a number (`#042`, or Arabic-Indic digits: not another PR, but not this one's marker either, so the notes are
added) and Arabic-Indic digits for another PR (exit 1), a draft whose bytes pass through untouched (astral characters, named entities, non-ASCII case, U+2028 and U+2029, and a byte that isn't UTF-8),
and the usage errors (exit 2). Case 21 gives `--note` for `--notes-file`: an abbreviated option is accepted, as argparse does. Cases 22 and 23 are drafts with
CRLF line endings, published with them: one with its marker on a CRLF line, kept as it is, and one with no marker, whose notes are added with LF after it.
The notes file is read with its line endings translated, unlike stdin: case 24's CRLF and lone CR come out as LF. Case 25 is an empty notes file, which still
adds the blank lines before it. Case 26 gives `--pr ' +0_42 '`, which Python's `int()` reads as 42. Case 27 is case 19 with astral digits (U+1D7D2 and
U+1D7D0, a surrogate pair each in .NET), case 28 a notes file starting with a byte order mark, which is kept, case 29 a marker whose number is past a 64-bit
integer, and case 31 `--pr -42`, which no marker can name. Case 30 gives `--pr "it's"`: the error quotes the value as Python's `repr()` does, in double quotes;
its usage lines name the script as `draft_release.py` or `draft_release.cs`.

Not covered, on purpose:

- A notes file that can't be read: the script ends in a Python traceback, which is no contract a port should copy.
