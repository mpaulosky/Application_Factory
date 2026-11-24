# The status parity suite

`github/status.py` through its command line: `tests/parity.test.sh status [command...]`. The runner's header says what a case holds.

Not covered, on purpose:

- The run with no repo arguments: it reads the Fleet's `github/repos.txt`, and a flag to point it elsewhere would change `status.py` for a test.
  `status.py` reads `repos.txt` on every run (the cases' repos aren't in it, so nothing it holds changes their output today).
- A Copilot ask record younger than the 30 seconds it takes to settle: the clock isn't fixed, and every record in the fixtures is long settled.
- A repo argument with no `/`: `status.py` fails with a Python traceback, which is no contract a port should copy.
