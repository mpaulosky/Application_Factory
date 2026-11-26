# The discover_tests parity suite

The Template's `.github/scripts/discover_tests.cs` through its command line: `tests/parity.test.sh discover_tests [command...]`. The runner's header says what a case holds.
The cases were recorded from `discover_tests.py`, which shipped beside the C# for one Rollout and has retired (mpaulosky/Application_Factory#126); the `.cs` is the
reference now, built once first and run with `dotnet run --no-build --file`, as ci.yml's Discover Test Projects job and the Template's gate run it. The first case
after that build is a cold first run, so its stdout shows the build left nothing there.

Each case's `tree/` is the repo the script reads: its `tests/` and any `Directory.Build.props` above them. The run's folder is the repo's root (`--root` is left out but in
cases 09, 33 to 36 and 47 to 49), so `expected.tree/` holds the tree as it was and `out`, the file `--output` names, with the bytes the run appended to it. No case makes a `gh`
call, so each `gh/` holds only `.gitkeep`.

The cases cover:

- **What a test project is.** `IsTestProject` in the `.csproj`, and a helper library that sets nothing (case 01); in `tests/Directory.Build.props` (02) and overridden by the
  `.csproj` (03); only the nearest `Directory.Build.props` read (04), unless it imports its parent (05, and 08 with `<import` in lower case), the nearer value winning (06); one
  at the root (07); the chain stopping at the root (08, and 09, where the props above `--root` would make it a test project) and at a nearer props whose `Import` names
  another file (10).
- **Conditions.** Each shape the script evaluates, each in a folder of its own with its own `Directory.Build.props`: `Contains`, `StartsWith`, `EndsWith`, `==` and `!=`
  ignoring case, `!` negation, blanks around it and `==` with no spaces (11); one on the property itself (12); one that doesn't hold leaving the earlier value (13); the last
  value that applies winning (14); an unknown one logged and counted as holding (15, with `--list`, so the notice, which names the file's absolute path, is on stderr, matched
  by its shape); and a false group Condition that skips the property's own, unknown one without a notice (16).
- **Reading the XML.** The MSBuild namespace, a byte order mark, an XML declaration and CRLF (17); property names and values in any case and with blanks, and a value that is
  neither `true` nor `false` ignored (18); a comment and CDATA in a value read as ElementTree reads them, and a child element before the text hiding it (19); only a
  `PropertyGroup` directly under the project, in any case, counted (20); a file that isn't XML (37, exit 1); bytes that aren't UTF-8 (39).
- **The matrix.** Each entry's settings: an E2E folder in either case, Playwright, `_` and `.` in `test_id` (21); no projects, and no `tests/` (22, 23: `has_tests=false`);
  projects but no test project (24: exit 1, nothing appended); the output appended after what the file held (25); an output in a missing folder (38: exit 1, after the notice);
  `json.dumps`'s escapes, a quote, a backslash, a tab, DEL, non-ASCII and an astral character as `\uXXXX` and a surrogate pair (30; a name with a line break, which
  `parity-runner.test.sh`'s line-ending check can't read from git's file list, is `DiscoverTestsTests`' instead).
- **Finding the projects.** Any depth, a `.csproj` in `tests/` itself and a hidden folder (26); none (27); none that is a test project (28: exit 1, stdout empty); pathlib's
  order (29: by path part, so `a/x/x.csproj` before `a-b/x.csproj`, and by code point, so U+FF21 before U+1F600, the reverse of an ordinal sort of UTF-16); only names
  ending in `.csproj`, in that case, `.csproj` alone among them (31); a symlinked folder not searched but a symlinked `.csproj` read (32).
- **The root.** `--root` naming the repo (33), empty (34: the current folder), missing (35: no projects), and through a symlink followed before its `..` (36).
- **argparse.** Neither `--output` nor `--list`, both in either order, `--list` given a value, an unknown option, a positional argument, `--output` with no value (40 to 46,
  exit 2), abbreviated options and the `--name=value` form (47, 48), and the missing-group error before an unrecognized argument (49). A usage error's stderr is matched line
  by line with the script's name as either `discover_tests.py` or `discover_tests.cs`.

Not covered, on purpose:

- `--help`'s text, which argparse wraps to the terminal's width.
- A name under `tests/` that isn't UTF-8: Python reads it as its own bytes, .NET as U+FFFD, so the C# can't open the file.
- Names whose lower case differs between Python's `str.lower()` and .NET's invariant lowering (a capital I with a dot, a final capital sigma), and an `Import` line spelled
  with letters only Unicode case folding matches.
- An XML file with a DTD, which ElementTree reads and .NET refuses, and a project file that is a folder or a dangling symlink (exit 1 either way).
- An unknown Condition with `--output`: its notice names the run's absolute path, which no case can fix.
