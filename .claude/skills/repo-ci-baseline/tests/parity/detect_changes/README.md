# The detect_changes parity suite

The Template's `.github/scripts/detect_changes.py` through its command line: `tests/parity.test.sh detect_changes [command...]`. The runner's header says what a case holds.
`test.sh` runs it against the `.py`, its reference while the `.py` ships, and against `detect_changes.cs`, built once first and run with `dotnet run --no-build --file`,
as ci.yml's Detect Changes job runs it.

Each case but the usage errors (21 to 23, 25, 26, 30 and 31) has a `setup.sh` that builds a repo in the run's folder: a base commit with some code and some docs, tagged
`base`, and the case's change committed on top. It then removes the files, so the run's folder holds only `out`, the file each case's `--output` names, and
`expected.tree/out` is the bytes the run appended to it. The diff is between the two commits, which the removal doesn't change. No case makes a `gh` call, so each `gh/` holds
only `.gitkeep`.

The cases cover a docs-only change, a code change, a mixed one and an empty diff (which builds); a move of code into `docs/`, a rename to a `.md` name and a deletion
(`--no-renames` lists both paths); names with a space and a newline kept whole; a code path whose line breaks would start a workflow command, printed as `\n` and `\r`;
the output appended after what the file already held (case 10); a Dependabot GitHub Actions bump, and the same change by another author, on a NuGet branch, with
`global.json` too, and with no changed files; the first code path in git's order, which puts `a-b/x.cs` before `a/x.cs` (case 16, the reverse of Python's
`sorted()` of paths, which the script never calls); a name that isn't UTF-8, printed as its own bytes (case 17, Python's `surrogateescape`); `.MD` as code (case 18);
`--head` naming another commit (case 19); `docs/` only at the root (case 28); an unknown base and an output in a missing folder (exit 1, stderr matched by the
exception's name, and in case 29 after stdout's line); and argparse's usage errors (exit 2: a required option left out, an ambiguous prefix, a positional
argument, an unknown option, an option with no value) and forms (an abbreviated option, `--name=value`). A usage error's stderr is matched by its first and last lines:
argparse wraps the usage line, and the C# names itself `detect_changes.cs`.

Not covered, on purpose:

- `--help`'s text, which argparse wraps to the terminal's width.
- A `git` that can't be started, and an output file that can't be written for a reason other than a missing folder. Both are exit 1 with the error on stderr.
