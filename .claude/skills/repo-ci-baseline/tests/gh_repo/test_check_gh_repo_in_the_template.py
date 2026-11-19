"""The tests of check_gh_repo.py that need the Factory: the Template's own folders (OWNED lists every Owned script and skill
file, and names nothing the Template lacks; the Owned scripts pass), and references/gotchas.md, which holds the rules the
checker's docstring points at. The checker's own tests ship with it, in the Template's .github/scripts/tests/, so every
Application runs them from its own repo root; test.sh runs them there too.
"""

import re
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
BASELINE_SKILL = HERE.parents[1]
TEMPLATE_OWNED = BASELINE_SKILL / "template" / "owned"
sys.path.insert(0, str(TEMPLATE_OWNED / ".github" / "scripts"))
import check_gh_repo  # noqa: E402

OWNED_SCRIPTS = TEMPLATE_OWNED / "scripts"
GOTCHAS = BASELINE_SKILL / "references" / "gotchas.md"
SKILLS_README = TEMPLATE_OWNED / ".claude" / "skills" / "README.md"


def covered(relative):
    """Whether OWNED lists the repo-relative path, as itself or under a folder entry."""
    return any(relative == entry or (entry.endswith("/") and relative.startswith(entry))
               for entry in check_gh_repo.OWNED)


def test_main_passes_the_templates_owned_scripts():
    # Arrange
    assert any(path.is_file() for path in OWNED_SCRIPTS.rglob("*"))

    # Act
    code = check_gh_repo.main([str(OWNED_SCRIPTS)])

    # Assert
    assert code == 0


def test_owned_covers_every_owned_script_and_skill_file_of_the_template():
    # Arrange
    owned_files = [path.relative_to(TEMPLATE_OWNED).as_posix()
                   for folder in ("scripts", ".claude/skills")
                   for path in (TEMPLATE_OWNED / folder).rglob("*") if path.is_file()]
    assert owned_files

    # Act, Assert
    assert [name for name in owned_files if not covered(name)] == []


def test_every_owned_entry_exists_in_the_template():
    # Arrange
    assert check_gh_repo.OWNED

    # Act
    missing = [entry for entry in check_gh_repo.OWNED if not (TEMPLATE_OWNED / entry).exists()]

    # Assert
    assert missing == []


# R45 and R52: gotchas.md holds the rules; the docstring points at it.


def test_the_checkers_docstring_is_one_line_that_points_at_gotchas_md():
    # Arrange
    docstring = check_gh_repo.__doc__.strip()

    # Act, Assert
    assert len(docstring.splitlines()) == 1
    assert "gotchas.md" in docstring


def test_gotchas_md_says_a_placeholder_repository_in_a_skill_is_refused():
    # Arrange
    text = GOTCHAS.read_text()

    # Act, Assert
    assert "<owner>/<repo>" in text
    assert re.search(r"<owner>/<repo>[^.]*\brefused\b|\brefused\b[^.]*<owner>/<repo>", text)


def test_the_owned_skills_readme_says_a_placeholder_repository_in_a_skill_is_refused():
    # Arrange
    text = SKILLS_README.read_text()

    # Act, Assert
    assert "<owner>/<repo>" in text
    assert re.search(r"<owner>/<repo>[^.]*\brefused\b|\brefused\b[^.]*<owner>/<repo>", text)


@pytest.mark.parametrize("rule", [r"gh\.exe", r"heredoc", r"binary"])
def test_gotchas_md_holds_the_rules_the_docstring_used_to(rule):
    # Arrange
    text = GOTCHAS.read_text()

    # Act, Assert
    assert re.search(rule, text)
