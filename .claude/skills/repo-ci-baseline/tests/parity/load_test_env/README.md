# The load_test_env parity suite

The Template's `.github/scripts/load_test_env.py` through its command line: `tests/parity.test.sh load_test_env [command...]`. The runner's header says what a case holds.
`test.sh` runs it against the `.py`, its reference while the `.py` ships, and against `load_test_env.cs`, built once first and run with `dotnet run --no-build --file`,
as ci.yml's test jobs run it.

A case's `TEST_ENV` is its `env.d/TEST_ENV`, byte for byte, so it can hold the line breaks a one-line `env` can't; cases 11, 27 and 31 set it in `env` instead, and case 10
leaves it unset. `out`, the file `--output` names, is in the run's folder, so `expected.tree/out` is the bytes the run appended to it; a case with no `expected.tree/`
left no file at all. No case makes a `gh` call, so each `gh/` holds only `.gitkeep`.

The cases cover:

- **Reading the secret.** Name and value lines, a comment and a blank line skipped (case 01); CRLF and a lone CR (02, 03); every line break `str.splitlines()` splits on
  (04), and the line numbers it gives (05); blanks `str.strip()` strips, Unicode ones and `\x1f` among them, around names and values (06); `=` and `#` kept in a value (07).
- **The masks.** A `%` escaped as the runner decodes it (08); an empty or blank value not masked but exported (09); non-ASCII values masked and written as UTF-8 (29); a name
  given twice exported twice (30).
- **Nothing to export.** No `TEST_ENV`, an empty one, and one of only comments and blanks (10 to 12): one line, and no output file made.
- **A malformed secret.** A line with no `=`, no name, a name starting with a digit or holding a space, a hyphen or a non-ASCII letter, and `=` alone (13 to 19); a `GITHUB_`
  and a `RUNNER_` name (20, 21), but not their lower case (22); `NODE_OPTIONS` in any case (23, 24). Each exits 1 naming the line, and its stdout, compared byte for byte,
  holds no value: the lines before the bad one hold `hunter2`, and case 13's bad line `hunter3`.
- **The output file.** Appended after what it held (25), left as it was by a malformed secret and by no secret (26, 27), and in a missing folder (28: exit 1, after the
  masks).
- **argparse.** No `--output`, `--output` with no value, an unknown option, a positional argument (32 to 35, exit 2), an abbreviated option and the `--name=value` form
  (36, 37). A usage error's stderr is matched line by line with the script's name as either `load_test_env.py` or `load_test_env.cs`.

Not covered, on purpose:

- `--help`'s text, which argparse wraps to the terminal's width.
- A `TEST_ENV` holding bytes that aren't UTF-8: Python fails writing them to the output, .NET reads them as U+FFFD. A GitHub secret is UTF-8 text.
