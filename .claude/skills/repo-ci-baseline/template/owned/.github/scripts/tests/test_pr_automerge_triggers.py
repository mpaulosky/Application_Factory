"""PR Auto-Merge reads a PR again when a workflow that can leave a check on it completes (its workflow_run list).

GitHub sends no check_suite events for suites Actions creates, so a workflow that runs on a PR's pushes or labels and is missing from the list
leaves a PR whose last check it was to wait for the scheduled sweep, often an hour or more. Every Template workflow that runs on
`pull_request` for anything but `closed` must be named there.
"""

import json
import subprocess
from pathlib import Path

WORKFLOWS = Path(__file__).resolve().parents[2] / "workflows"


def load_all():
    """Every workflow file, as {file name: parsed YAML}, read in one go."""
    paths = sorted(WORKFLOWS.glob("*.yml"))
    try:
        import yaml
    except ImportError:
        # pytest may run through uvx, which has no PyYAML: read the files with it in a one-off environment.
        script = ("import json, sys, yaml; print(json.dumps({p: yaml.safe_load(open(p)) for p in sys.argv[1:]}, default=str))")
        done = subprocess.run(["uvx", "--with", "pyyaml", "python", "-I", "-c", script, *map(str, paths)],
                              check=True, capture_output=True, text=True)
        return {Path(p).name: data for p, data in json.loads(done.stdout).items()}
    return {p.name: yaml.safe_load(p.read_text()) for p in paths}


def triggers(workflow):
    # YAML 1.1 reads an unquoted `on` key as true.
    on = workflow.get("on", workflow.get(True, workflow.get("true")))
    if isinstance(on, str):
        return {on: None}
    if isinstance(on, list):
        return {name: None for name in on}
    return on or {}


def leaves_a_check_on_an_open_pr(workflow):
    on = triggers(workflow)
    if "pull_request" not in on:
        return False
    types = (on["pull_request"] or {}).get("types")
    return types is None or any(kind != "closed" for kind in types)


def test_every_workflow_that_checks_an_open_pr_wakes_pr_automerge():
    workflows = load_all()
    listed = set(triggers(workflows["pr-automerge.yml"])["workflow_run"]["workflows"])
    checking = {data["name"] for name, data in workflows.items() if leaves_a_check_on_an_open_pr(data)}
    assert checking - listed == set()


def test_the_owned_files_check_is_one_of_them():
    # It left ci.yml for a workflow of its own; a label event re-runs it, and its completion must reach PR Auto-Merge.
    workflows = load_all()
    assert leaves_a_check_on_an_open_pr(workflows["owned-files.yml"])
    assert "Owned files" in triggers(workflows["pr-automerge.yml"])["workflow_run"]["workflows"]


def test_a_workflow_only_on_closed_prs_is_not_required():
    assert not leaves_a_check_on_an_open_pr({"on": {"pull_request": {"types": ["closed"]}}})
    assert leaves_a_check_on_an_open_pr({"on": {"pull_request": None}})
    assert leaves_a_check_on_an_open_pr({True: ["pull_request"]})
    assert leaves_a_check_on_an_open_pr({"on": {"pull_request": {"types": ["closed", "labeled"]}}})
