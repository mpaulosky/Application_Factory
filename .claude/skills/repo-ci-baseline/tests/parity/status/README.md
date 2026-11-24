# The status parity suite

`github/status.cs` through its command line: `tests/parity.test.sh status [command...]`. The runner's header says what a case holds.
The cases were recorded from the Python script, which the C# replaced once it passed every one (ADR 0017); the C# is the reference now.

Not covered, on purpose:

- The run with no repo arguments: it reads the Fleet's `github/repos.txt`, and a flag to point it elsewhere would change `status.cs` for a test.
  `status.cs` reads `repos.txt` on every run (the cases' repos aren't in it, so nothing it holds changes their output today).
- A Copilot ask record younger than the 30 seconds it takes to settle: the clock isn't fixed, and every record in the fixtures is long settled.
- A repo argument that isn't `owner/repo`: the Python failed with a traceback, and `status.cs` refuses it as a usage error (exit 2) before any call (ADR 0017's
  Update), which `StatusPortTests.cs` holds; argparse's usage text isn't a contract the suite should hold either.
