# The draft_release parity suite

The Template's `.github/scripts/draft_release.py` through its command line: `tests/parity.test.sh draft_release [command...]`. The runner's header says what a
case holds. Each case gives the draft's body on stdin and the release notes in its tree's `notes.txt`; the run must leave that file as it was.

The cases cover a marker on a line of its own (with spaces and tabs around it, or twice), no marker, an empty draft, a marker inside a longer line, a draft with
no final newline, notes that lose only their trailing newlines, a draft naming another PR (a longer number, or another PR beside this one; with and without
`--tag`), a marker whose number is this PR's only as a number (`#042`, or Arabic-Indic digits: not another PR, but not this one's marker either, so the notes are
added) and Arabic-Indic digits for another PR (exit 1), a draft whose bytes pass through untouched (astral characters, named entities, non-ASCII case, U+2028 and U+2029, and a byte that isn't UTF-8),
and the usage errors (exit 2).

Not covered, on purpose:

- A notes file that can't be read: the script ends in a Python traceback, which is no contract a port should copy.
- A draft with CRLF line endings: its body would be published with them, and the runner refuses a carriage return in `expected.stdout`. The unit tests cover
  the marker on a CRLF line.
- Option abbreviations (`--note` for `--notes-file`): argparse accepts them, but no caller relies on them and a port's parser needn't copy them.
