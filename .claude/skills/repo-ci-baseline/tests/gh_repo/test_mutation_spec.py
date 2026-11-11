"""mutations.json is the scanner's mutation spec for the hardening-review skill's mutate.py (run it by hand: see CLAUDE.md). A full run
takes about a minute and edits the tree, so the gate only checks that the spec still fits the code: each mutation's `old` occurs
exactly once in its file (the runner reports NOT FOUND or AMBIGUOUS otherwise), and changes something.
"""

import json
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
SPEC = json.loads((HERE / "mutations.json").read_text())


def test_the_spec_runs_this_directorys_tests_and_has_mutations():
    assert (ROOT / SPEC["test"][2]).is_file()
    assert SPEC["timeout"] > 0
    assert len(SPEC["mutations"]) >= 10


@pytest.mark.parametrize("mutation", SPEC["mutations"], ids=lambda m: m["why"])
def test_a_mutation_still_fits_the_code(mutation):
    text = (ROOT / mutation["path"]).read_text()
    assert text.count(mutation["old"]) == 1, "old must occur exactly once: update the spec to the code"
    assert mutation["new"] != mutation["old"]
    assert mutation["why"].strip()


def test_every_mutation_has_its_own_old_new_pair():
    pairs = [(m["path"], m["old"], m["new"]) for m in SPEC["mutations"]]
    assert len(set(pairs)) == len(pairs)
