# The land parity suite

`github/land.py` through its command line: `tests/parity.test.sh land [command...]`. The runner's header says what a case holds.

The cases cover each action `commands()` carries out, live and under `--dry-run`, `--ready`, a failed call, the Copilot request's comment, request and read-back
(a failed comment stops it; a dropped, refused or unreadable request), PRs that merged or closed, `--blog-wait 0` and a release-blog PR that ends a
`--blog-wait 0.5`, `--timeout 0`, a run of polls (`--interval 0`) that prints an action and a repo's error once each, an unreadable repo under `--once`,
a bad target and a bad `--interval` (exit 2), and the PR URL form.
A looping case answers each poll from its own fixtures (`repo.<owner>.<name>.<n>.json`, see `tests/parity/fake-gh.sh`), so it ends after a fixed number of polls.

Not covered, on purpose:

- The run with no arguments: it reads the Fleet's `github/repos.txt`, and a flag to point it elsewhere would change `land.py` for a test.
- The `factory` scope, and every scope but the default `full`: a scope comes only from `repos.txt` (the cases' repos aren't in it), so a merge in a case's
  repo always starts the wait for its release-blog PR.
- A Copilot ask record younger than the 30 seconds it takes to settle: the clock isn't fixed, and every record in the fixtures is long settled.
- Option abbreviations (`--onc` for `--once`): argparse accepts them, but no caller relies on them and a port's parser needn't copy them.
- A wait that runs out: a `--blog-wait` above 0 with no release-blog PR, or a `--timeout` above 0 that is reached, takes that long in real time.
  Only the 0-minute limits are covered, and the 0.5-minute wait a release-blog PR ends.
