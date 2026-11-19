"""PR Auto-Merge reads a PR again when a workflow that can leave a check on it completes (its workflow_run list).

GitHub sends no check_suite events for suites Actions creates, so a workflow that runs on a PR's pushes or labels and is missing from the list
leaves a PR whose last check it was to wait for the scheduled sweep, often an hour or more. Every Template workflow that runs on
`pull_request` for anything but `closed` must be named there. Only the Template's own workflow files are read: an Application's own
workflows are left alone, since it can't change the Owned list (a required check of its own goes into the Template first).
"""

import json
import subprocess
from pathlib import Path

import pytest

WORKFLOWS = Path(__file__).resolve().parents[2] / "workflows"

# The workflow files the Template ships. In the Template, test_the_template_ships_these_workflows pins this to the folder.
TEMPLATE_WORKFLOWS = {
    "backfill-blog-posts.yml", "ci.yml", "claude-review.yml", "code-metrics.yml", "codeql-analysis.yml", "copilot-review-sync.yml",
    "dependabot-auto-merge.yml", "lint-actions.yml", "lint-markdown.yml", "lint-yaml.yml", "owned-files.yml", "pr-automerge.yml",
    "pr-review-submitted.yml", "pr-title.yml", "release.yml", "sandcastle.yml",
}


def in_the_template():
    return WORKFLOWS.parent.parent.name == "owned" and WORKFLOWS.parent.parent.parent.name == "template"


def load(names):
    """The named workflow files that exist, as {file name: parsed YAML}, read in one go."""
    paths = [WORKFLOWS / name for name in sorted(names) if (WORKFLOWS / name).is_file()]
    try:
        import yaml
    except ImportError:
        # pytest may run through uvx, which has no PyYAML: read the files with it in a one-off environment.
        script = "import json, sys, yaml; print(json.dumps({p: yaml.safe_load(open(p)) for p in sys.argv[1:]}, default=str))"
        done = subprocess.run(["uvx", "--with", "pyyaml", "python", "-I", "-c", script, *map(str, paths)],
                              check=True, capture_output=True, text=True)
        return {Path(p).name: data for p, data in json.loads(done.stdout).items()}
    return {p.name: yaml.safe_load(p.read_text()) for p in paths}


def triggers(workflow):
    if not isinstance(workflow, dict):
        return {}
    # YAML 1.1 reads an unquoted `on` key as true.
    on = workflow.get("on", workflow.get(True, workflow.get("true")))
    if isinstance(on, str):
        return {on: None}
    if isinstance(on, list):
        return {name: None for name in on}
    return on if isinstance(on, dict) else {}


def leaves_a_check_on_an_open_pr(workflow):
    on = triggers(workflow)
    if "pull_request" not in on:
        return False
    types = (on["pull_request"] or {}).get("types")
    return types is None or any(kind != "closed" for kind in types)


def display_name(file_name, workflow):
    # GitHub names a workflow without `name:` by its path.
    named = workflow.get("name") if isinstance(workflow, dict) else None
    return named or f".github/workflows/{file_name}"


def listed(workflows):
    assert "pr-automerge.yml" in workflows, f"no pr-automerge.yml in {WORKFLOWS}"
    run = triggers(workflows["pr-automerge.yml"]).get("workflow_run")
    assert isinstance(run, dict) and isinstance(run.get("workflows"), list), "pr-automerge.yml has no workflow_run list of workflows"
    return set(run["workflows"])


def test_every_template_workflow_that_checks_an_open_pr_wakes_pr_automerge():
    workflows = load(TEMPLATE_WORKFLOWS)
    missing = sorted(display_name(name, data) for name, data in workflows.items()
                     if leaves_a_check_on_an_open_pr(data) and display_name(name, data) not in listed(workflows))
    assert missing == [], f"these run on pull_request but PR Auto-Merge's workflow_run doesn't list them: {missing}"


def test_the_owned_files_check_is_one_of_them():
    # It left ci.yml for a workflow of its own; a label event re-runs it, and its completion must reach PR Auto-Merge.
    workflows = load({"owned-files.yml", "pr-automerge.yml"})
    assert leaves_a_check_on_an_open_pr(workflows["owned-files.yml"])
    assert "Owned files" in listed(workflows)


@pytest.mark.skipif(not in_the_template(), reason="an Application may have workflows of its own")
def test_the_template_ships_these_workflows():
    assert {p.name for p in WORKFLOWS.glob("*.yml")} == TEMPLATE_WORKFLOWS


def test_a_workflow_only_on_closed_prs_is_not_required():
    assert not leaves_a_check_on_an_open_pr({"on": {"pull_request": {"types": ["closed"]}}})
    assert leaves_a_check_on_an_open_pr({"on": {"pull_request": None}})
    assert leaves_a_check_on_an_open_pr({True: ["pull_request"]})
    assert leaves_a_check_on_an_open_pr({"on": {"pull_request": {"types": ["closed", "labeled"]}}})


def test_an_empty_or_unnamed_workflow_is_read_not_a_crash():
    assert not leaves_a_check_on_an_open_pr(None)
    assert not leaves_a_check_on_an_open_pr("just text")
    assert display_name("docs.yml", {"on": "pull_request"}) == ".github/workflows/docs.yml"
    assert display_name("docs.yml", None) == ".github/workflows/docs.yml"
