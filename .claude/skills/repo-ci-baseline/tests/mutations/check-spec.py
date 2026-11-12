"""Checks that each mutation spec next to this file (*.json, for the hardening-review skill's mutate.py) still fits the code it mutates.

A full mutate.py run edits the tree one mutation at a time and can take tens of minutes, so test.sh runs this instead: each mutation's `old` occurs exactly once in its
file (mutate.py reports NOT FOUND or AMBIGUOUS otherwise, and only when somebody runs it), differs from `new`, has a reason, and leaves a shell
file that still parses (`bash -n`): a mutant that doesn't parse would be "caught" by every test. tests/gh_repo/test_mutation_spec.py does the same
for the scanner's Python spec.

    python3 -I tests/mutations/check-spec.py [<repo root> <folder of specs>]

The two arguments (the repository the specs' paths are relative to, and the folder to read *.json from) default to this checkout and this file's folder; tests/mutations/check-spec.test.sh
gives them a throwaway tree. A spec needs a `test` argv (a non-empty list of non-empty strings), a `timeout` above 0 and at most mutate.py's cap, and at least one mutation, each with path, old, new and a reason (and `equivalent`, when given, a reason); text has no NUL byte; no mutation repeats another. These are the checks of mutate.py's load_spec that a spec can fail on its own.
Exit 0: every spec fits. Exit 1: one doesn't (printed). Exit 2: there is no spec to check, or the arguments are not none or both of <repo root> and <folder of specs>.
"""

import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
FIELDS = ("path", "old", "new", "why")
MAX_TIMEOUT = 86400  # the same as mutate.py's; check-spec.test.sh fails when they differ


def plain(value):
    """Text mutate.py accepts: no NUL byte, and it encodes (a lone surrogate from a \\ud800 escape does not)."""
    try:
        value.encode("utf-8")
    except UnicodeError:
        return False
    return "\x00" not in value


def problems(spec_path, root):
    try:
        spec = json.loads(spec_path.read_text())
    except ValueError as error:
        return [f"is not valid JSON: {error}"]
    if not isinstance(spec, dict):
        return ["is not a JSON object"]
    out = []
    timeout = spec.get("timeout")
    test = spec.get("test")
    if not (isinstance(test, list) and test and all(isinstance(a, str) and a and plain(a) for a in test)):
        out.append("needs a `test` argv: a list of non-empty strings without a NUL byte")
    if not (isinstance(timeout, (int, float)) and not isinstance(timeout, bool) and 0 < timeout <= MAX_TIMEOUT):
        out.append(f"needs a `timeout` above 0 and at most {MAX_TIMEOUT} seconds")
    mutations = spec.get("mutations")
    if not isinstance(mutations, list):
        return out + ["needs `mutations` as a list"]
    seen = set()
    for i, m in enumerate(mutations):
        if not isinstance(m, dict):
            out.append(f"mutation {i + 1}: is not an object")
            continue
        name = f"mutation {i + 1} ({str(m.get('why', '?'))[:50]})"
        missing = [f for f in FIELDS if not isinstance(m.get(f), str)]
        if missing:
            out.append(f"{name}: needs {', '.join(missing)} as text")
            continue
        if not all(plain(m[f]) for f in FIELDS) or not m["path"] or not m["old"]:
            out.append(f"{name}: path, old, new and why must be text without a NUL byte, and path and old not empty")
            continue
        if m.get("equivalent") is not None and not (isinstance(m["equivalent"], str) and m["equivalent"].strip()):
            out.append(f"{name}: `equivalent` must be a reason")
        path = root / m["path"]
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
    if not mutations:
        out.append("has no mutations")
    return out


def main(argv):
    if len(argv) not in (1, 3):
        print("usage: check-spec.py [<repo root> <folder of specs>]", file=sys.stderr)
        return 2
    root, folder = (Path(argv[1]), Path(argv[2])) if len(argv) == 3 else (ROOT, HERE)
    specs = sorted(folder.glob("*.json"))
    if not specs:
        print(f"no mutation spec found in {folder}", file=sys.stderr)
        return 2
    bad = 0
    for spec in specs:
        for problem in problems(spec, root):
            print(f"{spec.name}: {problem}".encode("utf-8", "backslashreplace").decode("utf-8"))  # a lone surrogate in a spec must not crash the report
            bad = 1
    return bad


if __name__ == "__main__":
    sys.exit(main(sys.argv))
