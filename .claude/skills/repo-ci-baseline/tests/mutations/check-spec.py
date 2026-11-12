"""Checks that each mutation spec next to this file (*.json, for the hardening-review skill's mutate.py) still fits the code it mutates.

A full mutate.py run takes tens of minutes and edits the tree, so test.sh runs this instead: each mutation's `old` occurs exactly once in its
file (mutate.py reports NOT FOUND or AMBIGUOUS otherwise, and only when somebody runs it), differs from `new`, has a reason, and leaves a shell
file that still parses (`bash -n`): a mutant that doesn't parse would be "caught" by every test. tests/gh_repo/test_mutation_spec.py does the same
for the scanner's Python spec.

    python3 -I tests/mutations/check-spec.py

Exit 0: every spec fits. Exit 1: one doesn't (printed). Exit 2: there is no spec to check.
"""

import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]


def problems(spec_path):
    spec = json.loads(spec_path.read_text())
    out = []
    if not (isinstance(spec.get("test"), list) and spec["test"] and spec.get("timeout", 0) > 0):
        out.append("needs a `test` argv and a positive `timeout`")
    seen = set()
    for i, m in enumerate(spec.get("mutations", [])):
        name = f"mutation {i + 1} ({m.get('why', '?')[:50]})"
        path = ROOT / m["path"]
        if not path.is_file():
            out.append(f"{name}: {m['path']} does not exist")
            continue
        text = path.read_text()
        if text.count(m["old"]) != 1:
            out.append(f"{name}: `old` occurs {text.count(m['old'])} times in {m['path']}, not once")
            continue
        if m["new"] == m["old"]:
            out.append(f"{name}: `new` equals `old`")
        if not m["why"].strip():
            out.append(f"{name}: no reason")
        if (m["path"], m["old"], m["new"]) in seen:
            out.append(f"{name}: repeated")
        seen.add((m["path"], m["old"], m["new"]))
        if path.suffix == ".sh":
            mutant = text.replace(m["old"], m["new"], 1)
            parsed = subprocess.run(["bash", "-n"], input=mutant, text=True, capture_output=True, check=False)
            if parsed.returncode != 0:
                out.append(f"{name}: the mutant does not parse: {parsed.stderr.strip()}")
    if not spec.get("mutations"):
        out.append("has no mutations")
    return out


def main():
    specs = sorted(HERE.glob("*.json"))
    if not specs:
        print("no mutation spec found next to check-spec.py", file=sys.stderr)
        return 2
    bad = 0
    for spec in specs:
        for problem in problems(spec):
            print(f"{spec.name}: {problem}")
            bad = 1
    return bad


if __name__ == "__main__":
    sys.exit(main())
