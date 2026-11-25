# The bench's `--rescore` parity suite

`../rescore.test.sh` runs `--rescore` over a copy of `input/` and holds what it prints to `expected.stdout` and each file it writes back to
`expected/`, byte for byte. Both were recorded from `claude-review-bench.py`, the Python the bench was ported from, before it was deleted
([ADR 0017](../../../../../../docs/adr/0017-the-factorys-and-the-templates-scripts-are-csharp-file-based-apps.md), Update for
mpaulosky/Application_Factory#161). On the day of the port, `--rescore` over a copy of every file in `bench/results/` gave the same stdout and
bytes from both as well.

- `input/20261007T213720-baseline.json`: a real run, whose heads the cases file knows, so their repo, PR and expected findings come from it.
- `input/edge-cases.json`: what a real run seldom holds.
  - Text past ASCII, written back as `\u` escapes, an astral character as a surrogate pair, and a finding's body cut at 300 code points,
    not UTF-16 units, after its space runs (a tab, U+2028 and U+001C among them) are made single spaces.
  - Floats as Python's `repr()` writes them (`1e-05`, `1.2345678901234568e+16`, `-0.0`, `2.0`), a bool where a number goes, and None.
  - A line that's a float, a bool (an int to `isinstance`), a string or missing; a line past the file's end, on a file the head doesn't have,
    and on a file with no line break at its end; a body that isn't a string.
  - A case whose head the cases file knows, which takes its repo and expected findings from it; one whose repo isn't cloned (bad_lines null);
    an error outcome; a review with no findings key; an expected finding named twice, which counts twice; `action_rejects`; `reads` empty and null.
  - Keys written back where they were (`bad_lines`, `caught`) or at the end (`score`, which this file lacks).

The cases file is the bench's own, so a change to `claude-review-cases.json` changes the expected output: record it again with
`RESCORE_RECORD=1 bash bench/tests/rescore.test.sh`, which records from the C#, and read the diff before committing it.
