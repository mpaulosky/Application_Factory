# The check_central_pins parity suite

The Template's `.github/scripts/check_central_pins.py` through its command line: `tests/parity.test.sh check_central_pins [command...]`. The runner's header says what a
case holds. `test.sh` runs it against the `.py`, its reference while the `.py` ships, and against `check_central_pins.cs`, built once first and run with
`dotnet run --no-build --file`, as ci.yml's Build Solution job and the Template's gate run it.

Each case's `tree/` is the repo the script reads, and the run's folder is its root (`--root` is left out but in cases 72 to 78), so `expected.tree/` holds the tree as it
was: the script writes nothing. A `setup.sh` makes what git can't store: a symlink (52 to 54, 74) or a FIFO (55). No case makes a `gh` call, so each `gh/` holds only
`.gitkeep`. The runner unsets `GITHUB_ACTIONS`, so the plain lines are checked whatever the caller's environment, and a case's `env` sets it (61 to 68).

The cases cover:

- **What is reported, and what passes.** An unreferenced pin under transitive pinning (01: exit 1, the advice on stderr), every pin referenced (02), pinning off (03), no
  central package management (04), an empty repo (05).
- **The setting.** `true` in any case and with blanks, another word off (06); set in `Directory.Build.props` (07); built from a property that is true (08) or false (09);
  one that can't be read counting as on and named once, however often it is set (10), and passing when every pin is referenced (11); one resolving to true settling an
  unreadable one, a value mixing two properties (12); past `MaxExpansions` values (13); a literal past `MaxId` compared as written (14); one resolving to nothing (15).
- **Pins.** Listed twice in either case (16); an `Include` holding several ids, blanks and a `/version` (17); under `Choose` and a conditioned `ItemGroup` (18); a nested
  `directory.packages.props` (19); pathlib's order, `a/x` before `a-b/x` (20), and by code point, U+FF21 before U+1F600 (21).
- **The reason comment** (22): right before the pin in any case, covering only that pin; separated by an element; the last of two comments; a heading; no reason after the
  marker; no colon; the marker not first; a dotless i, which Python's lowering doesn't turn into `i`.
- **References.** Ids ignoring case (23); `Update`, `GlobalPackageReference`, `PackageDownload` and a lower-case tag (24); every MSBuild extension and five names that
  aren't one (25); SDKs in a `Project`, an `Sdk` element, an `Import` and `global.json` (26); the legacy namespace, a prefix and an undeclared one (27); a byte order mark,
  UTF-16, ISO-8859-1 and CRLF inside a tag (28); `bin/`, `obj/`, `node_modules/` and dot-folders not walked (29); a folder named like a project walked (56).
- **Properties.** A reference built from one (30), one with several values (31), a pin built from one (32) and resolving to several ids, reported in order (33); references
  that can't be read making the pins warnings, listed once and sorted (34); a pin that can't be read left alone (35); `MaxExpansions` combinations, one past them (36) and
  at them, two properties multiplied (37); a name past `MaxValue` once substituted (38); an id past `MaxId`, written or resolved (39), counted in code points (40); a value
  with blanks, a character reference, a comment, CDATA, in a `Target` or a `Choose` (41); a value holding a list (42).
- **Files the check won't parse,** in the check's own words: a DOCTYPE skipped (43) and failing a central file (44); nesting at `MaxDepth` read and past it skipped (45);
  a skipped file failing the check when a pin is built from a property (46), and staying a notice for a pin no file can make readable (47), a kept pin (48) or with
  pinning off (49); a file that can't be checked failing with warnings (50) and never a pass (51).
- **Symlinks and FIFOs.** A symlinked, a dangling and a symlinked `global.json` (52); a symlinked central file (53); a symlinked folder, a dot-named one aside (54); a FIFO
  project and a FIFO `global.json`, reported within the timeout, not waited on (55: the FIFO `global.json` is in a subfolder, since `dotnet run` itself reads the one in the
  folder it runs from).
- **global.json.** Comments, trailing commas, a byte order mark and comment markers inside strings, and a repeated key (57); no `msbuild-sdks` object, a repeated
  `msbuild-sdks` whose last value wins (58); text the host can't read, an unclosed comment after the object read (59); one over its cap, and one at it (60).
- **GitHub Actions and text a repo controls.** An error annotation (61), `GITHUB_ACTIONS` other than `true` (62), warnings and notices (63), a file that can't be checked
  (64); an id holding line breaks, `%`, C1 and format characters, a private-use one, a line separator, spaces other than the space, and an astral one, under GitHub Actions
  (65) and in a plain line (66); an unreadable reference and setting holding line breaks (67); a path holding `,`, `:` and `%` (68); a name and a path cut at `MaxPrinted`
  (69); `MaxShown` pins and references, with the count of the rest (70), and exactly `MaxShown` (71).
- **The root and argparse.** `--root` naming the repo (72), empty (73: the current folder), through a symlink followed before its `..` (74), abbreviated with `=` (75); not
  a folder (76), missing, its name printed with `?` for a control character (77), with no value (78), a positional argument (79), an unknown option (80), each exit 2. A
  usage error's stderr is matched line by line with the script's name as either `check_central_pins.py` or `check_central_pins.cs`, and the stderr line a run that can't
  check a file ends with names the decoder as either Python or .NET.

Not covered, on purpose, each in `CheckCentralPinsTests` instead where it can be:

- **Messages each language words its own way.** The reason a file isn't well-formed XML is expat's in the Python (`no element found: line 1, column 9`) and the .NET
  `XmlReader`'s in the C# (`Root element is missing`), so no case holds a malformed file but those the check refuses in its own words (a DOCTYPE, nesting past
  `MaxDepth`). An encoding: Python decodes any 8-bit codec it has (Windows-1252, KOI8-R), .NET without a code page provider only UTF-8, UTF-16, UTF-32, ASCII and
  ISO-8859-1, so a file in another one is skipped by the C# where the Python reads it (and UTF-32 the reverse). A `strerror` text comes from the C library either way, but
  .NET reports a folder it can't list with an exception of its own, from which the C# takes the errno it can (`Permission denied`, `No such file or directory`).
- **What a case can't make.** A file over `MaxBytes` (4 MiB), past the run's budgets, or past `MaxGlobalJsonFiles`; a folder that can't be listed (a run as root lists it
  anyway); a file swapped between the lstat and the open; a path holding a line break (the runner's checks read git's file list by line); 101 notices.
- **A name that isn't UTF-8**, which Python reads as its own bytes and .NET as U+FFFD, so the C# can't open the file; and names whose lower case Python's full case mapping
  gives differently from .NET's invariant lowering (a capital I with a dot).
- **JSON at the edges**: nesting past 1,000 deep (the C#'s cap; the Python's is its recursion limit, near 10,000 on 3.12), `NaN` and `Infinity`, which `json.loads` reads
  and the host doesn't, and a key escaping a lone surrogate.
- `--help`'s text, which argparse wraps to the terminal's width.
