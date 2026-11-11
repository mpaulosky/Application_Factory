"""Tests for ../check-issue-refs.py: what it flags, what it lets through, and that a bad folder is never a pass."""
import os
import pathlib
import subprocess
import sys

SCRIPT = pathlib.Path(__file__).resolve().parent.parent / "check-issue-refs.py"


def run(root, *extra):
    args = [str(root)] if root is not None else []
    return subprocess.run([sys.executable, "-I", str(SCRIPT), *args, *extra], capture_output=True, text=True)


def tree(tmp_path, files):
    for name, text in files.items():
        path = tmp_path / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    return tmp_path


def test_a_bare_reference_in_prose_is_reported_with_its_line(tmp_path):
    result = run(tree(tmp_path, {"docs/a.md": "fine\nSee #5 for why.\n"}))
    assert result.returncode == 1
    assert "docs/a.md:2: See #5 for why." in result.stdout


def test_a_repo_qualified_reference_passes(tmp_path):
    assert run(tree(tmp_path, {"a.md": "See mpaulosky/dotfiles#5 and Application_Factory#6.\n"})).returncode == 0


def test_a_parenthesised_citation_is_reported_even_under_tests(tmp_path):
    result = run(tree(tmp_path, {"scripts/tests/t.sh": "# a fix (#5)\n"}))
    assert result.returncode == 1 and "scripts/tests/t.sh:1" in result.stdout


def test_a_bare_reference_under_tests_outside_a_citation_passes(tmp_path):
    assert run(tree(tmp_path, {"scripts/tests/t.sh": "echo '#5 is fixture data'\n"})).returncode == 0


def test_a_sandcastle_test_checks_only_comment_lines(tmp_path):
    assert run(tree(tmp_path, {"x.test.mts": 'const s = "see #5";\n'})).returncode == 0
    assert run(tree(tmp_path, {"y.test.mts": "// see #5\n"})).returncode == 1


def test_the_listed_examples_and_html_entities_pass(tmp_path):
    assert run(tree(tmp_path, {"a.md": "Write `Fixes #123` and &#9; here.\n"})).returncode == 0


def test_an_example_does_not_hide_a_longer_number(tmp_path):
    assert run(tree(tmp_path, {"a.md": "Fixes #421\n"})).returncode == 1


def test_a_missing_folder_or_no_argument_is_not_a_pass(tmp_path):
    assert run(tmp_path / "nope").returncode == 2
    assert run(None).returncode == 2
    assert run(tmp_path, "extra").returncode == 2


def test_an_unreadable_file_is_not_a_pass_and_not_a_finding(tmp_path):
    root = tree(tmp_path, {"a.md": "See #5\n", "b.md": "fine\n"})
    (root / "b.md").chmod(0)
    try:
        if os.access(root / "b.md", os.R_OK):
            return  # root reads anything
        result = run(root)
        assert result.returncode == 2 and "b.md" in result.stderr
    finally:
        (root / "b.md").chmod(0o644)
