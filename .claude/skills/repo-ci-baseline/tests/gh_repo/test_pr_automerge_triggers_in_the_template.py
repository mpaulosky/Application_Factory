"""The Factory's side of test_pr_automerge_triggers.py: its pin of the Template's workflow files skips outside the Template, so here,
where the Template is known, the guard must say so and the set must match the folder, whatever the guard decides."""

import importlib.util
from pathlib import Path

HERE = Path(__file__).resolve().parent
TEMPLATE_OWNED = HERE.parents[1] / "template" / "owned"
TRIGGERS = TEMPLATE_OWNED / ".github" / "scripts" / "tests" / "test_pr_automerge_triggers.py"


def load():
    spec = importlib.util.spec_from_file_location("test_pr_automerge_triggers_module", TRIGGERS)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_the_trigger_test_knows_it_is_in_the_template():
    assert load().in_the_template()


def test_its_workflow_set_is_the_templates_folder():
    workflows = {p.name for p in (TEMPLATE_OWNED / ".github" / "workflows").glob("*.yml")}
    assert load().TEMPLATE_WORKFLOWS == workflows
