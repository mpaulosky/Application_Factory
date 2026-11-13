"""Tests for check_gh_repo.py (mpaulosky/dotfiles#267, #321): every gh command in the
Template's Owned scripts, and in every repo's own scripts and skills, is told its repository, so a re-Apply can't fail a
Baseline repo's gate check for gh commands that reach gh's default repository
(a fork, in a fork's clone). No GitHub.
"""

import ast
import os
import re
import signal
import subprocess
import sys
import time
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
TEMPLATE_OWNED = HERE.parents[1] / "template" / "owned"
sys.path.insert(0, str(TEMPLATE_OWNED / ".github" / "scripts"))
import check_gh_repo  # noqa: E402

OWNED_SCRIPTS = TEMPLATE_OWNED / "scripts"


@pytest.fixture(autouse=True)
def stop_a_test_that_hangs():
    """Fail a test that has not finished after 120 s (the slowest takes a few seconds): a scanner loop that stopped advancing fails here, not at the runner's own timeout."""
    def stop(*_):
        raise TimeoutError("the test did not finish in 120 s (a hang in the scanner?)")
    previous = signal.signal(signal.SIGALRM, stop)
    signal.alarm(120)
    try:
        yield
    finally:
        signal.alarm(0)
        signal.signal(signal.SIGALRM, previous)


def lines_of(found):
    """The line numbers of found, whose entries are path:line: text."""
    return [int(re.match(r".*?:([0-9]+): ", entry).group(1)) for entry in found]


def assert_refused_at(found, text, line):
    """found is exactly one refusal, of the given line of text, with that line's text as its message."""
    assert lines_of(found) == [line]
    assert found[0].split(f":{line}: ", 1)[1] == text.split("\n")[line - 1]


def untold(tmp_path, text, name="script.sh"):
    path = tmp_path / name
    path.write_text(text)
    return check_gh_repo.untold(path)


@pytest.mark.parametrize("text, line", [
    ("#!/usr/bin/env bash\ngh pr view 1\n", 2),
    ("gh issue list\n", 1),
    ("gh api repos/{owner}/{repo}/pulls\n", 1),
    ("echo \"$(gh pr view --json number)\"\n", 1),
    ("gh pr view 1 --repo o/r\n", 1),
    ("gh pr view 1\nexport GH_REPO=o/r\n", 1),
    ("export GH_REPO=\nexport GH_REPO=''\ngh pr view 1\n", 3),
    ("GH_REPO= gh pr view 1\n", 1),
    ("GH_REPO=o/r echo; gh pr view 1\n", 1),
    ("export GH_REPO=o/r && gh pr view 1\n", 1),
    ("gh \\\n  pr view 1\n", 1),
    ("if command -v gh\n", 1),
    # The subcommand arrives through a variable or a wrapper.
    ("gh \"$@\"\n", 1),
    ("gh $sub view 1\n", 1),
    ("gh \"${args[@]}\"\n", 1),
    ("gh_() { (cd \"$primary\" && gh \"$@\"); }\ngh_ pr view 1\n", 1),
    # A variable that's empty where the script runs leaves gh its default.
    ("export GH_REPO=\"$GITHUB_REPOSITORY\"\ngh pr view 1\n", 2),
    ("export GH_REPO=$repo\ngh pr view 1\n", 2),
    ("GH_REPO=\"$repo\" gh api repos/{owner}/{repo}/pulls\n", 1),
    # The hidden subcommand can be gh repo view, which ignores GH_REPO.
    ("export GH_REPO=o/r\ngh_() { gh \"$@\"; }\ngh_ repo view --json name\n", 2),
    ("export GH_REPO=o/r\ngh $sub view\n", 2),
    # A character bash doesn't end a line on hides no export behind it.
    ("echo x\u2028export GH_REPO=o/r\ngh pr view 1\n", 2),
    ("echo x\x0cexport GH_REPO=o/r\ngh pr view 1\n", 2),
    # A redirection or a brace expansion before the subcommand hides it.
    ("export GH_REPO=o/r\ngh 2>/dev/null repo view --json name\n", 2),
    ("export GH_REPO=o/r\ngh >\"$log\" repo edit o\n", 2),
    ("export GH_REPO=o/r\ngh {repo,} view\n", 2),
    # Emptied or removed after the export.
    ("export GH_REPO=o/r\nGH_REPO= gh pr view 1\n", 2),
    ("export GH_REPO=o/r\nunset GH_REPO\ngh pr view 1\n", 3),
    ("export GH_REPO=o/r\nGH_REPO=\"$repo\"\ngh pr view 1\n", 3),
    # A list of gh's words, where no export reaches it.
    ("export GH_REPO=o/r\nsubprocess.run([\"gh\", \"pr\", \"view\"])\n", 2),
    # An export that runs only sometimes.
    ("if [[ -n \"${CI:-}\" ]]; then\n  export GH_REPO=o/r\nfi\ngh pr view 1\n", 4),
])
def test_untold_refuses_a_gh_command_not_told_its_repository(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "export GH_REPO=\"${GITHUB_REPOSITORY:?}\"\ngh pr view 1\n",
    "export GH_REPO=${GITHUB_REPOSITORY:?set it}\ngh pr view 1\n",
    "export GH_REPO=o/r  # the repository\ngh issue list\n",
    "GH_REPO=o/r gh pr view 1\n",
    "GH_REPO=\"${repo:?}\" gh api repos/{owner}/{repo}/pulls\n",
    "export GH_REPO=o/r\nif command -v gh\n",
    "echo \"needs gh.\"\n",
    # gh ending a quoted word, as a test's stub does.
    "export GH_REPO=o/r\ncat > \"$work/bin/gh\" <<EOF\n",
    "export GH_REPO=o/r\nchmod +x \"$work/bin/gh\"\n",
    "export GH_REPO=o/r\necho \"gate.sh: needs gh\"\n",
    "export GH_REPO=o/r\nfor t in \"gh\" jq; do\n",
    "export GH_REPO=o/r\ncommand -v gh >/dev/null || exit 1\n",
    "export GH_REPO=o/r\ncommand -v gh &>/dev/null\n",
    # A prefix that names a repository leaves the export standing.
    "export GH_REPO=o/r\nGH_REPO=o/s gh pr view 1\ngh pr view 2\n",
    "# gh pr view 1 is how you'd look\n",
    "echo hi\n",
    "neigh pr\nhigh api\n",
])
def test_untold_accepts_a_gh_command_told_its_repository(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text", [
    "gh repo view o/r\n",
    "gh repo view \"${repo:?}\"\n",
    "gh repo edit o/r --enable-auto-merge\n",
    "gh repo list mpaulosky\n",
    "gh repo gitignore list\n",
    "gh repo license view mit\n",
    # rename, deploy-key and autolink read GH_REPO, like gh pr.
    "export GH_REPO=o/r\ngh repo rename new-name\n",
    "GH_REPO=o/r gh repo deploy-key list\n",
    "gh repo create o/r --private\n",
    "gh repo clone o/r\n",
])
def test_untold_accepts_gh_repo_naming_its_repository(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text, line", [
    ("gh repo view\n", 1),
    ("gh repo view --json name\n", 1),
    ("gh repo\n", 1),
    # GH_REPO doesn't reach view, edit and most of gh repo's subcommands.
    ("export GH_REPO=o/r\ngh repo view\n", 2),
    ("GH_REPO=o/r gh repo view --json name\n", 1),
    # view and edit have no -R or --repo; gh refuses them as unknown flags.
    ("gh repo edit -R o/r --enable-auto-merge\n", 1),
    ("gh repo view --repo=o/r\n", 1),
    ("gh repo view \"$repo\"\n", 1),
    ("gh repo rename new-name\n", 1),
])
def test_untold_refuses_gh_repo_without_its_repository(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


def test_untold_names_the_file_and_line(tmp_path):
    # Arrange, Act
    found = untold(tmp_path, "echo\ngh pr view 1\n", name="gate.sh")

    # Assert
    assert found == [f"{tmp_path / 'gate.sh'}:2: gh pr view 1"]


@pytest.mark.parametrize("separator", ["\x0c", "\x0b", "\x1c", "\x85", "\u2028"])
def test_untold_counts_lines_as_bash_does(tmp_path, separator):
    # Arrange, Act
    found = untold(tmp_path, f"echo a{separator}b\ngh pr view 1\n", name="gate.sh")

    # Assert
    assert found == [f"{tmp_path / 'gate.sh'}:2: gh pr view 1"]


def test_untold_reads_crlf_line_endings(tmp_path):
    # Arrange, Act
    found = untold(tmp_path, "export GH_REPO=o/r\r\ngh pr view 1\r\ngh repo view\r\n", name="gate.sh")

    # Assert
    assert found == [f"{tmp_path / 'gate.sh'}:3: gh repo view"]


def test_untold_reads_a_script_that_isnt_utf8(tmp_path):
    # Arrange: a comment saved as cp1252.
    path = tmp_path / "gate.sh"
    path.write_bytes(b"# caf\xe9\ngh pr view 1\n")

    # Act
    found = check_gh_repo.untold(path)

    # Assert
    assert found == [f"{path}:2: gh pr view 1"]


def test_untold_reads_a_file_with_a_nul_byte_as_bash_does_with_the_nul_removed(tmp_path):
    # Arrange
    path = tmp_path / "blob"
    path.write_bytes(b"\x00\x01gh pr view\n")

    # Act
    found = check_gh_repo.untold(path)

    # Assert
    assert found == [f"{path}:1: \\x01gh pr view"]


def test_main_fails_when_a_script_under_the_root_isnt_told(tmp_path, capsys):
    # Arrange
    (tmp_path / "tests").mkdir()
    (tmp_path / "ok.sh").write_text("export GH_REPO=o/r\ngh pr view 1\n")
    (tmp_path / "tests" / "bad.test.sh").write_text("gh pr view 1\n")

    # Act
    code = check_gh_repo.main([str(tmp_path)])

    # Assert
    assert code == 1
    assert "bad.test.sh:1: gh pr view 1" in capsys.readouterr().out


def test_main_fails_when_a_root_isnt_a_directory(tmp_path, capsys):
    # Arrange, Act
    code = check_gh_repo.main([str(tmp_path / "missing")])

    # Assert
    assert code == 2
    assert "missing" in capsys.readouterr().err


def test_main_passes_the_templates_owned_scripts():
    # Arrange
    assert any(path.is_file() for path in OWNED_SCRIPTS.rglob("*"))

    # Act
    code = check_gh_repo.main([str(OWNED_SCRIPTS)])

    # Assert
    assert code == 0


# --- mpaulosky/dotfiles#321: the gate checks a repo's own scripts and skills ---

CHECKER = TEMPLATE_OWNED / ".github" / "scripts" / "check_gh_repo.py"


def make_repo(root, files):
    """Writes files (repo-relative path -> text or bytes) under root."""
    for name, content in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(content, bytes):
            path.write_bytes(content)
        else:
            path.write_text(content)
    return root


def repo_check(tmp_path, files):
    return check_gh_repo.check_repo(make_repo(tmp_path, files))


def untold_md(tmp_path, text, name="SKILL.md"):
    path = tmp_path / name
    path.write_text(text)
    return check_gh_repo.untold_markdown(path)


UNTOLD = "gh pr view 1\n"
TOLD = "export GH_REPO=o/r\ngh pr view 1\n"


def covered(relative):
    """Whether OWNED lists the repo-relative path, as itself or under a folder entry."""
    return any(relative == entry or (entry.endswith("/") and relative.startswith(entry))
               for entry in check_gh_repo.OWNED)


# Shell files, checked in --repo mode.

def test_check_repo_reports_an_untold_gh_command_in_a_script_by_its_relative_path(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {"scripts/x.sh": "echo\n" + UNTOLD})

    # Assert
    assert found == ["scripts/x.sh:2: gh pr view 1"]


def test_check_repo_passes_a_script_that_exports_gh_repo(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {"scripts/x.sh": TOLD})

    # Assert
    assert found == []


@pytest.mark.parametrize("name", ["scripts/x.sh", "scripts/x.bash", ".github/ci/gate-checks.sh",
                                  "scripts/tests/y.test.sh", ".claude/skills/mine/run.sh"])
def test_check_repo_checks_shell_files_in_scope(tmp_path, name):
    # Arrange, Act
    found = repo_check(tmp_path, {name: UNTOLD})

    # Assert
    assert found == [f"{name}:1: gh pr view 1"]


@pytest.mark.parametrize("shebang", ["#!/usr/bin/env bash", "#!/bin/bash", "#!/bin/sh", "#!/usr/bin/env sh"])
def test_check_repo_checks_an_extensionless_script_with_a_shell_shebang(tmp_path, shebang):
    # Arrange, Act
    found = repo_check(tmp_path, {"scripts/tool": f"{shebang}\n{UNTOLD}"})

    # Assert
    assert found == ["scripts/tool:2: gh pr view 1"]


@pytest.mark.parametrize("content", [
    "#!/usr/bin/env python3\n" + UNTOLD,
    "#!/usr/bin/env node\n" + UNTOLD,
    UNTOLD,
])
def test_check_repo_skips_an_extensionless_skill_file_that_isnt_a_shell_script(tmp_path, content):
    # Arrange, Act
    found = repo_check(tmp_path, {".claude/skills/mine/tool": content})

    # Assert
    assert found == []


@pytest.mark.parametrize("name", ["scripts/x.py", "scripts/x.mts", ".github/ci/list.txt", "scripts/x.json"])
def test_check_repo_skips_files_that_arent_shell_or_markdown(tmp_path, name):
    # Arrange, Act
    found = repo_check(tmp_path, {name: UNTOLD})

    # Assert
    assert found == []


def test_check_repo_checks_a_file_with_a_nul_byte_as_bash_reads_it(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {"scripts/blob.sh": b"\x00\x01gh pr view\n"})

    # Assert
    assert found == ["scripts/blob.sh:1: \\x01gh pr view"]


@pytest.mark.parametrize("name", ["src/x.sh", "docs/x.md", ".github/other/x.sh", ".github/workflows/x.sh", "x.sh",
                                  ".sandcastle/x.sh"])
def test_check_repo_only_walks_scripts_github_ci_and_skills(tmp_path, name):
    # Arrange, Act
    found = repo_check(tmp_path, {name: UNTOLD})

    # Assert
    assert found == []


@pytest.mark.parametrize("name", ["scripts/node_modules/p/x.sh", ".claude/skills/node_modules/p/SKILL.md",
                                  ".github/ci/node_modules/x.sh", "scripts/.git/x.sh"])
def test_check_repo_doesnt_walk_node_modules_or_git(tmp_path, name):
    # Arrange, Act
    found = repo_check(tmp_path, {name: "gh pr view 1\n"})

    # Assert
    assert found == []


def test_check_repo_doesnt_follow_a_symlink_out_of_the_repo(tmp_path):
    # Arrange
    outside = make_repo(tmp_path / "outside", {"x.sh": UNTOLD, "dir/y.sh": UNTOLD})
    repo = make_repo(tmp_path / "repo", {"scripts/ok.sh": TOLD})
    (repo / "scripts" / "link.sh").symlink_to(outside / "x.sh")
    (repo / "scripts" / "linked").symlink_to(outside / "dir", target_is_directory=True)

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert: refused (mpaulosky/dotfiles#344 R60), and what is outside the root is not read into the report.
    assert sorted(entry.split(":")[0] for entry in found) == ["scripts/link.sh", "scripts/linked"]
    assert not any("gh pr view" in entry for entry in found)


def test_check_repo_passes_a_repo_without_any_folder_in_scope(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {"README.md": "gh pr view 1\n"})

    # Assert
    assert found == []


def test_check_repo_reports_each_file_with_its_own_path(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {
        "scripts/a.sh": UNTOLD,
        "scripts/b.sh": TOLD,
        ".github/ci/gate-checks.sh": "echo\ngh issue list\n",
        ".claude/skills/mine/SKILL.md": "Run `gh pr list`.\n",
    })

    # Assert
    assert sorted(found) == [
        ".claude/skills/mine/SKILL.md:1: Run `gh pr list`.",
        ".github/ci/gate-checks.sh:2: gh issue list",
        "scripts/a.sh:1: gh pr view 1",
    ]


# The Owned Baseline files are skipped.

@pytest.mark.parametrize("name", [
    "scripts/gate.sh",
    "scripts/check-branch-name.sh",
    "scripts/check-pr-title.sh",
    "scripts/check-git-hooks.sh",
    "scripts/lint-edited-file.sh",
    "scripts/review.sh",
    "scripts/sync-copilot-review.sh",
    "scripts/tests/check-branch-name.test.sh",
    "scripts/tests/check-pr-title.test.sh",
    "scripts/tests/check-git-hooks.test.sh",
    "scripts/tests/lint-edited-file.test.sh",
    "scripts/tests/sync-copilot-review.test.sh",
    "scripts/cleanup.sh",
    "scripts/work-issue.sh",
    "scripts/tests/cleanup.test.sh",
    "scripts/tests/work-issue.test.sh",
    ".claude/skills/pre-push-review/SKILL.md",
    ".claude/skills/pre-push-review/references/more.md",
    ".claude/skills/hardening-review/SKILL.md",
    ".claude/skills/hardening-review/scripts/mutate.py",
])
def test_check_repo_skips_the_owned_baseline_files(tmp_path, name):
    # Arrange, Act
    found = repo_check(tmp_path, {name: "gh pr view 1\n`gh pr list`\n"})

    # Assert
    assert found == []


@pytest.mark.parametrize("name", [
    "scripts/other.sh",
    "scripts/tests/other.test.sh",
    ".claude/skills/mine/SKILL.md",
    ".claude/skills/pre-push-review-2/SKILL.md",
    "scripts/gate.sh.bak.sh",
])
def test_check_repo_checks_what_isnt_an_owned_baseline_file(tmp_path, name):
    # Arrange, Act
    found = repo_check(tmp_path, {name: "`gh pr view 1`\n" if name.endswith(".md") else "gh pr view 1\n"})

    # Assert
    assert [line.split(":")[0] for line in found] == [name]


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


def test_owned_names_files_and_folders_under_scripts_the_skills_readme_and_the_review_skills_only():
    # Arrange, Act, Assert
    assert check_gh_repo.OWNED
    assert all(entry.startswith(("scripts/", ".claude/skills/README.md", ".claude/skills/pre-push-review/", ".claude/skills/hardening-review/")) for entry in check_gh_repo.OWNED)


def test_scope_is_scripts_github_ci_and_skills():
    # Arrange, Act, Assert
    assert sorted(check_gh_repo.SCOPE) == [".claude/skills", ".github/ci", "scripts"]


@pytest.mark.parametrize("name, expected", [
    ("a.md", "markdown"), ("a.sh", "shell"), ("a.bash", "shell"), ("a.py", None), ("a.txt", None), ("a.mts", None),
])
def test_kind_tells_markdown_from_shell_by_extension(tmp_path, name, expected):
    # Arrange
    path = tmp_path / name
    path.write_text("x\n")

    # Act, Assert
    assert check_gh_repo.kind(path) == expected


def test_kind_reads_the_shebang_of_an_extensionless_file(tmp_path):
    # Arrange
    shell, python, plain = tmp_path / "shell", tmp_path / "python", tmp_path / "plain"
    shell.write_text("#!/usr/bin/env bash\n")
    python.write_text("#!/usr/bin/env python3\n")
    plain.write_text("echo\n")

    # Act, Assert
    assert [check_gh_repo.kind(path) for path in (shell, python, plain)] == ["shell", None, None]


# Skills (Markdown): only gh in code counts.

@pytest.mark.parametrize("text, line", [
    ("```bash\ngh pr view 1\n```\n", 2),
    ("```\ngh pr view 1\n```\n", 2),
    ("~~~\ngh pr view 1\n~~~\n", 2),
    ("   ```bash\ngh pr view 1\n   ```\n", 2),
    ("Run `gh pr view 1` to see it.\n", 1),
    ("- Run `gh pr list`\n", 1),
    ("1. Then ``gh pr list`` shows them.\n", 1),
    ("Use `gh pr view 1` or `gh pr view 2 --repo o/r`.\n", 1),
    # The repository comes from a variable gh would find empty, or from another command.
    ("```bash\nGH_REPO=\"$repo\" gh pr view 1\n```\n", 2),
    ("```bash\ngh pr view 1 --repo \"$repo\"\n```\n", 2),
    ("```bash\nexport GH_REPO=o/r\ngh pr view 1\n```\n", 3),
    ("```bash\nGH_REPO=o/r echo; gh pr view 1\n```\n", 2),
    ("```bash\nGH_REPO= gh pr view 1\n```\n", 2),
    # gh api has no --repo flag.
    ("```bash\ngh api repos/o/r/pulls --repo o/r\n```\n", 2),
    ("```bash\ngh api repos/{owner}/{repo}/pulls\n```\n", 2),
    # gh repo's subcommands take the repository first.
    ("```bash\ngh repo view\n```\n", 2),
    ("```bash\ngh repo view --json name\n```\n", 2),
    ("```bash\ngh repo view --repo o/r\n```\n", 2),
    ("```bash\nGH_REPO=o/r gh repo view\n```\n", 2),
    ("Run `gh repo edit --enable-auto-merge`.\n", 1),
    # A command split across lines, or with its subcommand hidden.
    ("```bash\ngh \\\n  pr view 1 --repo o/r\n```\n", 2),
    ("```bash\ngh pr view 1 \\\n  --repo o/r\n```\n", 2),
    ("```bash\ngh \"$@\"\n```\n", 2),
    ("```bash\ngh $sub view 1\n```\n", 2),
    ("```bash\ngh\n```\n", 2),
    # An unclosed fence is code to the end of the file; a longer fence isn't closed by a shorter one.
    ("```bash\necho\ngh pr view 1\n", 3),
    ("````\n```\ngh pr view 1\n```\n````\n", 3),
    ("~~~\n```\ngh pr view 1\n```\n~~~\n", 3),
    ("```bash\necho\n```\n```bash\ngh pr view 1\n```\n", 5),
    ("Text\n\n```bash\necho\n```\n\nMore `gh issue list`\n", 7),
    # A fence in a blockquote or a list item is still a fence.
    ("> ```bash\n> gh pr view 1\n> ```\n", 2),
    ("> [!NOTE]\n> ```bash\n> gh pr view 1\n> ```\n", 3),
    (">> ```bash\n>> gh pr view 1\n>> ```\n", 2),
    ("- ```bash\n  gh pr view 1\n  ```\n", 2),
    ("1. ```bash\n   gh pr view 1\n   ```\n", 2),
    ("* Step:\n  > ```bash\n  > gh pr view 1\n  > ```\n", 3),
    ("> ~~~\n> gh pr view 1\n> ~~~\n", 2),
    ("- ```bash\n  gh pr view 1\n", 2),
])
def test_untold_markdown_refuses_a_gh_command_in_code_not_told_its_repository(tmp_path, text, line):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "```bash\nGH_REPO=o/r gh pr view 1\n```\n",
    "```bash\nGH_REPO=\"${repo:?}\" gh pr view 1\n```\n",
    "```bash\nGH_REPO=\"${GITHUB_REPOSITORY:?}\" gh issue list\n```\n",
    "```bash\nGH_REPO=o/r gh api repos/{owner}/{repo}/pulls\n```\n",
    "```bash\ngh pr view 1 --repo o/r\n```\n",
    "```bash\ngh pr view 1 --repo=o/r\n```\n",
    "```bash\ngh pr view 1 -R o/r\n```\n",
    "```bash\ngh pr list --state open --repo \"${repo:?}\"\n```\n",
    "```bash\ngh pr list --json number -R o/r --limit 5\n```\n",
    "```bash\ngh repo view o/r\n```\n",
    "```bash\ngh repo view \"${repo:?}\" --json name\n```\n",
    "```bash\ngh repo edit o/r --enable-auto-merge\n```\n",
    "```bash\ngh repo list mpaulosky\n```\n",
    "```bash\ngh repo list\n```\n",
    "```bash\nGH_REPO=o/r gh repo rename new-name\n```\n",
    "Run `GH_REPO=o/r gh pr view 1`.\n",
    "Run `gh pr view 1 --repo o/r`.\n",
    "Run `gh pr view 1 -R o/r` or `GH_REPO=o/r gh pr list`.\n",
    "~~~\nGH_REPO=o/r gh pr view 1\n~~~\n",
    "> ```bash\n> gh pr view 1 --repo o/r\n> ```\n",
    "- ```bash\n  GH_REPO=o/r gh pr view 1\n  ```\n",
    # A quoted fence that closed: what follows is prose again.
    "> ```bash\n> echo hi\n> ```\n> gh pr view 1\n",
    "- ```bash\n  echo hi\n  ```\n  gh pr view 1\n",
    "- Run `gh repo view o/r`\n",
    # gh named in prose, not run.
    "Needs the gh CLI.\n",
    "The gh command reaches the default repository.\n",
    "Install gh first; `gh` is a program.\n",
    "Call `gh` to talk to GitHub.\n",
    "```bash\nneigh pr view 1\ngh_ pr view 1\n```\n",
    "Plain gh pr view 1 outside code.\n",
    "# gh pr view 1\n\n- gh issue list\n",
    # Code that closed: what follows is prose again.
    "```bash\necho hi\n```\ngh pr view 1\n",
    "```bash\necho hi\n```\n\nRun gh pr view 1 yourself.\n",
    "~~~\necho hi\n~~~\ngh pr list\n",
    "````\n```\necho\n```\n````\ngh pr list\n",
    # An unclosed backtick opens no span.
    "Use `gh pr view 1 for it.\n",
    "```bash\necho hi\n```\n",
    "",
])
def test_untold_markdown_accepts_what_is_told_or_not_a_command(tmp_path, text):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert found == []


def test_untold_markdown_names_the_file_and_line_of_the_whole_line(tmp_path):
    # Arrange
    text = "# Title\n\n```bash\ngh repo view o/r\ngh pr view 1\n```\n\nTry `gh pr list` now.\n"

    # Act
    found = untold_md(tmp_path, text, name="SKILL.md")

    # Assert
    assert found == [f"{tmp_path / 'SKILL.md'}:5: gh pr view 1", f"{tmp_path / 'SKILL.md'}:8: Try `gh pr list` now."]


def test_untold_markdown_judges_each_command_on_its_own(tmp_path):
    # Arrange: the first command is told, the second is not; there is no export state in a skill.
    text = "```bash\nexport GH_REPO=o/r\nGH_REPO=o/r gh pr view 1\ngh pr list\n```\n"

    # Act
    found = untold_md(tmp_path, text)

    # Assert
    assert found == [f"{tmp_path / 'SKILL.md'}:4: gh pr list"]


def test_untold_markdown_reads_crlf_line_endings(tmp_path):
    # Arrange, Act
    found = untold_md(tmp_path, "```bash\r\ngh pr view 1 --repo o/r\r\ngh pr list\r\n```\r\n")

    # Assert
    assert found == [f"{tmp_path / 'SKILL.md'}:3: gh pr list"]


def test_untold_markdown_reads_a_file_that_isnt_utf8(tmp_path):
    # Arrange
    path = tmp_path / "SKILL.md"
    path.write_bytes(b"caf\xe9\n```bash\ngh pr view 1\n```\n")

    # Act
    found = check_gh_repo.untold_markdown(path)

    # Assert
    assert found == [f"{path}:3: gh pr view 1"]


def test_untold_markdown_reads_a_file_with_a_nul_byte_with_the_nul_removed(tmp_path):
    # Arrange
    path = tmp_path / "SKILL.md"
    path.write_bytes(b"\x00```\ngh pr view 1\n```\n")

    # Act
    found = check_gh_repo.untold_markdown(path)

    # Assert
    assert found == [f"{path}:2: gh pr view 1"]


def test_check_repo_checks_skills_line_by_line_but_scripts_with_their_export(tmp_path):
    # Arrange: the same text is told as a script and untold as a skill.
    text = "export GH_REPO=o/r\ngh pr view 1\n"

    # Act
    found = repo_check(tmp_path, {"scripts/x.sh": text, "scripts/x.md": f"```bash\n{text}```\n"})

    # Assert
    assert found == ["scripts/x.md:3: gh pr view 1"]


# The command line.

def test_main_repo_mode_prints_the_untold_commands_and_a_hint_and_fails(tmp_path, capsys):
    # Arrange
    make_repo(tmp_path, {"scripts/x.sh": UNTOLD})

    # Act
    code = check_gh_repo.main(["--repo", str(tmp_path)])

    # Assert
    captured = capsys.readouterr()
    assert code == 1
    assert captured.out.splitlines() == ["scripts/x.sh:1: gh pr view 1"]
    assert captured.err.startswith("check_gh_repo.py: export GH_REPO")
    assert "--repo" in captured.err
    assert "gh repo" in captured.err


def test_main_repo_mode_prints_nothing_when_every_command_is_told(tmp_path, capsys):
    # Arrange
    make_repo(tmp_path, {"scripts/x.sh": TOLD, ".claude/skills/mine/SKILL.md": "`gh pr list -R o/r`\n"})

    # Act
    code = check_gh_repo.main(["--repo", str(tmp_path)])

    # Assert
    captured = capsys.readouterr()
    assert code == 0
    assert captured.out == ""
    assert captured.err == ""


def test_main_repo_mode_passes_a_repo_without_any_folder_in_scope(tmp_path):
    # Arrange, Act
    code = check_gh_repo.main(["--repo", str(tmp_path)])

    # Assert
    assert code == 0


def test_main_repo_mode_fails_when_the_root_isnt_a_directory(tmp_path, capsys):
    # Arrange
    (tmp_path / "file").write_text("x\n")

    # Act
    codes = [check_gh_repo.main(["--repo", str(tmp_path / "missing")]), check_gh_repo.main(["--repo", str(tmp_path / "file")])]

    # Assert
    assert codes == [2, 2]
    assert capsys.readouterr().err.count("isn't a directory") == 2


@pytest.mark.parametrize("argv", [["--repo"], ["--repo", "a", "b"], ["--repo", "a", "--repo", "b"]])
def test_main_repo_mode_is_a_usage_error_without_exactly_one_root(tmp_path, capsys, argv):
    # Arrange
    (tmp_path / "a").mkdir()
    (tmp_path / "b").mkdir()
    argv = [str(tmp_path / arg) if arg in ("a", "b") else arg for arg in argv]

    # Act
    code = check_gh_repo.main(argv)

    # Assert
    assert code == 2
    assert "usage: check_gh_repo.py --repo <root> | <dir>..." in capsys.readouterr().err


def test_main_repo_mode_runs_as_a_script(tmp_path):
    # Arrange
    make_repo(tmp_path, {"scripts/x.sh": UNTOLD})

    # Act
    result = subprocess.run([sys.executable, "-I", str(CHECKER), "--repo", str(tmp_path)],
                            capture_output=True, text=True, check=False)

    # Assert
    assert result.returncode == 1
    assert result.stdout.splitlines() == ["scripts/x.sh:1: gh pr view 1"]
    assert "GH_REPO" in result.stderr


def test_main_directory_mode_still_checks_every_file_and_skips_nothing(tmp_path, capsys):
    # Arrange: the Owned names, a Markdown file and a .py file are all read as shell in this mode.
    make_repo(tmp_path, {"gate.sh": UNTOLD, "a.md": UNTOLD, "b.py": UNTOLD, "ok.sh": TOLD})

    # Act
    code = check_gh_repo.main([str(tmp_path)])

    # Assert
    assert code == 1
    assert sorted(line.split(":")[0].rsplit("/", 1)[1] for line in capsys.readouterr().out.splitlines()) == [
        "a.md", "b.py", "gate.sh"]


def test_main_directory_mode_prints_no_hint(tmp_path, capsys):
    # Arrange
    make_repo(tmp_path, {"x.sh": UNTOLD})

    # Act
    check_gh_repo.main([str(tmp_path)])

    # Assert
    assert capsys.readouterr().err == ""


# Cheap: it runs on every gate.

def test_the_check_imports_nothing_that_starts_a_process_or_reaches_the_network():
    # Arrange
    tree = ast.parse(CHECKER.read_text())
    imported = {alias.name.split(".")[0] for node in ast.walk(tree) if isinstance(node, ast.Import) for alias in node.names}
    imported |= {node.module.split(".")[0] for node in ast.walk(tree) if isinstance(node, ast.ImportFrom) and node.module}

    # Act, Assert
    assert imported
    assert imported.isdisjoint({"subprocess", "socket", "urllib", "http", "requests", "shutil", "asyncio"})


def test_check_repo_starts_no_process(tmp_path, monkeypatch):
    # Arrange
    make_repo(tmp_path, {"scripts/x.sh": UNTOLD, ".claude/skills/mine/SKILL.md": "`gh pr list`\n"})

    def refuse(*args, **kwargs):
        raise AssertionError("check_repo started a process")

    for name in ("run", "Popen", "call", "check_call", "check_output"):
        monkeypatch.setattr(subprocess, name, refuse)
    monkeypatch.setattr(os, "system", refuse)

    # Act
    found = check_gh_repo.check_repo(tmp_path)

    # Assert
    assert len(found) == 2


def test_check_repo_passes_a_repo_of_200_small_files(tmp_path):
    # Arrange
    make_repo(tmp_path, {f"scripts/s{i}.sh": TOLD for i in range(100)}
              | {f".claude/skills/s{i}/SKILL.md": f"# Skill {i}\n\nUse the gh CLI.\n" for i in range(100)})

    # Act
    found = check_gh_repo.check_repo(tmp_path)

    # Assert
    assert found == []


@pytest.mark.parametrize("text", [
    "command -v gh >/dev/null || exit 1 # gh-repo: ok\n",
    "echo \"needs gh\" #gh-repo: ok\n",
    "cat ~/.config/gh/hosts.yml\n",
    "ls /root/.config/gh/\n",
])
def test_untold_skips_a_line_marked_gh_repo_ok_and_a_folder_named_gh(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text, line", [
    ("command -v gh >/dev/null\n", 1),
    ("echo gh-repo: ok; gh pr view 1\n", 1),
    ("gh pr view 1 # gh-repo: okay\n", 1),
    ("# gh-repo: ok\ngh pr view 1\n", 2),
])
def test_untold_refuses_a_gh_command_without_the_marker_on_its_own_line(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "Needs `gh` <!-- gh-repo: ok -->, as in `gh auth status`.\n",
    "```bash\ncommand -v gh # gh-repo: ok\n```\n",
    "Keep the token in `~/.config/gh/hosts.yml`.\n",
])
def test_untold_markdown_skips_a_line_marked_gh_repo_ok_and_a_folder_named_gh(tmp_path, text):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert found == []


def test_untold_markdown_still_refuses_the_next_line_after_a_marked_one(tmp_path):
    # Arrange, Act
    found = untold_md(tmp_path, "`gh auth status` <!-- gh-repo: ok -->\n`gh pr view 1`\n")

    # Assert
    assert [line.split(":")[1] for line in found] == ["2"]


# --- mpaulosky/dotfiles#339: follow-ups from the review of #336 ---

# R50: an extensionless file with no shebang is skipped even when it holds a gh line (the empty file it replaces had none to find).
@pytest.mark.parametrize("content", ["\n" + UNTOLD, "# notes\n" + UNTOLD, " #!/bin/bash\n" + UNTOLD])
def test_check_repo_skips_an_extensionless_skill_file_without_a_shell_shebang_that_holds_a_gh_line(tmp_path, content):
    # Arrange, Act
    found = repo_check(tmp_path, {".claude/skills/mine/tool": content})

    # Assert
    assert found == []


def test_check_repo_checks_an_extensionless_file_whose_shebang_is_shell_and_whose_gh_line_follows_blank_lines(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {"scripts/tool": "#!/bin/bash\n\n" + UNTOLD})

    # Assert
    assert found == ["scripts/tool:3: gh pr view 1"]


# R55: a lookup line that ends in || or && goes on to its next command, not to a hidden subcommand.
@pytest.mark.parametrize("text", [
    "command -v gh >/dev/null 2>&1 ||\n  exit 1\nexport GH_REPO=o/r\ngh pr view 1\n",
    "command -v gh >/dev/null 2>&1 &&\n  echo found\nexport GH_REPO=o/r\ngh pr view 1\n",
    "command -v gh ||\n  exit 1\nexport GH_REPO=o/r\ngh pr view 1\n",
    "command -v gh &>/dev/null &&\n  echo found\nexport GH_REPO=o/r\ngh pr view 1\n",
    "command -v gh >/dev/null 2>&1 ||   \n  exit 1\nexport GH_REPO=o/r\ngh pr view 1\n",
])
def test_untold_accepts_a_lookup_line_ending_in_or_or_and_before_the_export(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text, line", [
    # A pipe or a backslash carries the line on to the next one, which may hold gh's subcommand.
    ("command -v gh |\n  cat\nexport GH_REPO=o/r\ngh pr view 1\n", 1),
    ("command -v gh >/dev/null 2>&1 |\n  cat\nexport GH_REPO=o/r\ngh pr view 1\n", 1),
    ("command -v gh \\\n  >/dev/null\nexport GH_REPO=o/r\ngh pr view 1\n", 1),
    ("command -v gh >/dev/null 2>&1 \\\n  || exit 1\nexport GH_REPO=o/r\ngh pr view 1\n", 1),
    # A gh with a subcommand is no lookup, whatever follows it.
    ("gh pr view 1 ||\n  exit 1\nexport GH_REPO=o/r\n", 1),
    ("gh pr view 1 &&\n  echo\nexport GH_REPO=o/r\n", 1),
    # The || does not tell the later gh anything: it is judged by the export before it.
    ("command -v gh ||\n  exit 1\ngh pr view 1\n", 3),
])
def test_untold_refuses_a_gh_line_that_goes_on_by_pipe_or_backslash_or_has_a_subcommand(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


# R56: the NAME=value words before gh are walked back over, and the last GH_REPO= among them is judged.
@pytest.mark.parametrize("text", [
    "GH_REPO=o/r GH_TOKEN=x gh pr view 1\n",
    "GH_TOKEN=x GH_REPO=o/r gh pr view 1\n",
    "GH_REPO=o/r GH_TOKEN=x GH_HOST=github.com gh pr list\n",
    "GH_TOKEN=x GH_REPO=o/r GH_HOST=github.com gh api repos/{owner}/{repo}/pulls\n",
    "GH_REPO=\"${repo:?}\" GH_TOKEN=\"$token\" gh pr view 1\n",
    "GH_REPO= GH_REPO=o/r gh pr view 1\n",
    "GH_REPO=o/r GH_TOKEN=x gh repo view o/r\n",
    "GH_REPO=o/r GH_TOKEN=x GH_REPO=o/s gh pr view 1\n",
    # A prefix that names a repository leaves the export standing, however many assignments it has.
    "export GH_REPO=o/r\nGH_REPO=o/s GH_TOKEN=x gh pr view 1\ngh pr view 2\n",
    # The export tells the gh behind a prefix with no GH_REPO= of its own.
    "export GH_REPO=o/r\nGH_TOKEN=x gh pr view 1\n",
])
def test_untold_accepts_a_gh_prefixed_by_assignments_and_a_told_gh_repo(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text, line", [
    ("GH_REPO=o/r GH_REPO= gh pr view 1\n", 1),
    ("GH_REPO=o/r GH_REPO=\"$repo\" gh pr view 1\n", 1),
    ("GH_REPO=\"$repo\" GH_TOKEN=x gh pr view 1\n", 1),
    ("GH_REPO= GH_TOKEN=x gh pr view 1\n", 1),
    ("GH_TOKEN=x GH_REPO= gh pr view 1\n", 1),
    ("export GH_REPO=o/r\nGH_REPO= GH_TOKEN=x gh pr view 1\n", 2),
    # GH_REPO does not reach gh repo view.
    ("GH_REPO=o/r GH_TOKEN=x gh repo view\n", 1),
    # A wrapper between the assignments and gh is not walked over.
    ("GH_REPO=o/r GH_TOKEN=x env gh pr view 1\n", 1),
    ("GH_REPO=o/r env gh pr view 1\n", 1),
    ("GH_REPO=o/r GH_TOKEN=x sudo gh pr view 1\n", 1),
    # The assignments are no prefix of a gh that comes after another command.
    ("GH_REPO=o/r GH_TOKEN=x echo; gh pr view 1\n", 1),
    ("GH_REPO=o/r GH_TOKEN=x echo && gh pr view 1\n", 1),
])
def test_untold_refuses_a_gh_whose_assignments_dont_name_a_repository_or_end_at_a_wrapper(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


def test_untold_ends_the_export_when_an_assignment_before_a_wrapper_empties_it(tmp_path):
    # Arrange
    text = "export GH_REPO=o/r\nGH_REPO= GH_TOKEN=x env true\ngh pr view 1\n"

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, 3)


# R57: a heredoc that feeds data to a program is no code, as an echo message is not; one fed to a shell is code.
@pytest.mark.parametrize("text", [
    "cat <<EOF\nusage: needs gh pr view <n>\nEOF\nexport GH_REPO=o/r\ngh pr list\n",
    "cat <<'EOF'\nusage: gh pr view <n>\nEOF\nexport GH_REPO=o/r\ngh pr list\n",
    "cat <<\"EOF\"\nusage: gh pr view <n>\nEOF\nexport GH_REPO=o/r\ngh pr list\n",
    "cat <<-EOF\n\tusage: gh pr view <n>\n\tEOF\nexport GH_REPO=o/r\ngh pr list\n",
    "cat > usage.txt <<END\nRun gh pr view 1 to see it.\nEND\nexport GH_REPO=o/r\ngh pr list\n",
    "cat <<EOF >&2\nneeds gh\nEOF\nexport GH_REPO=o/r\ngh pr list\n",
    "usage() {\n  cat <<EOF\nneeds gh\nEOF\n}\nexport GH_REPO=o/r\ngh pr list\n",
    # Several lines of prose, a blank line among them.
    "cat <<EOF\nline one\n\ngh pr view 1\ngh repo view\nEOF\nexport GH_REPO=o/r\ngh pr list\n",
    # Prose naming gh at the very top, before anything else.
    "#!/usr/bin/env bash\ncat <<'EOF'\ngh pr view 1\nEOF\n",
])
def test_untold_skips_the_body_of_a_data_only_heredoc(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text, line", [
    ("bash <<EOF\ngh pr view 1\nEOF\n", 2),
    ("sh <<EOF\ngh pr view 1\nEOF\n", 2),
    ("bash <<'EOF'\ngh pr view 1\nEOF\n", 2),
    ("bash -e <<EOF\ngh pr view 1\nEOF\n", 2),
    ("bash -s -- arg <<-EOF\n\tgh pr view 1\n\tEOF\n", 2),
    ("/bin/bash <<EOF\ngh pr view 1\nEOF\n", 2),
    ("export GH_REPO=o/r\nbash <<EOF\ngh repo view\nEOF\n", 3),
    ("export GH_REPO=o/r\nsh <<EOF\ngh \"$@\"\nEOF\n", 3),
])
def test_untold_still_reads_the_body_of_a_heredoc_fed_to_a_shell(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


# An unquoted delimiter lets bash run the $(...) and backticks in the body; eval, source and . run the body itself.
@pytest.mark.parametrize("text, line", [
    ("body=$(cat <<EOF\nOpen PRs: $(gh pr list --state open)\nEOF\n)\n", 2),
    ("cat <<EOF\nOpen PRs: `gh pr list`\nEOF\n", 2),
    ("cat <<-EOF\n\tOpen: $(gh pr list)\n\tEOF\n", 2),
    ("cat <<EOF\nNow: $(echo $(gh pr list))\nEOF\n", 2),
    ("eval \"$(cat <<EOF\ngh pr view 1\nEOF\n)\"\n", 2),
    ("source /dev/stdin <<EOF\ngh pr view 1\nEOF\n", 2),
    (". /dev/stdin <<EOF\ngh pr view 1\nEOF\n", 2),
])
def test_untold_reads_what_an_unquoted_heredoc_runs(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "cat <<'EOF'\nOpen PRs: $(gh pr list)\nEOF\n",
    "cat <<\"EOF\"\nOpen PRs: `gh pr list`\nEOF\n",
    "cat <<\\EOF\nOpen PRs: $(gh pr list)\nEOF\n",
    "cat <<EOF\nOpen PRs: \\$(gh pr list)\nEOF\n",
    "export GH_REPO=o/r\ncat <<EOF\nOpen PRs: $(gh pr list)\nEOF\n",
    "cat <<EOF\nOpen PRs: $(GH_REPO=o/r gh pr list)\nEOF\n",
    "cat <<EOF\nOpen PRs: $(gh pr list) # gh-repo: ok\nEOF\n",
])
def test_untold_accepts_a_heredoc_whose_substitutions_are_told_or_never_run(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


def test_untold_judges_the_line_after_a_heredoc_with_a_substitution(tmp_path):
    # Arrange
    text = "cat <<EOF\n$(date)\nEOF\ngh pr view 1\n"

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, 4)


def test_untold_judges_the_line_after_a_data_heredocs_terminator(tmp_path):
    # Arrange
    text = "export GH_REPO=o/r\ncat <<EOF\ngh pr view 1\nEOF\ngh repo view\n"

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, 5)


def test_untold_takes_no_export_from_the_body_of_a_data_heredoc(tmp_path):
    # Arrange
    text = "cat <<EOF\nexport GH_REPO=o/r\nEOF\ngh pr view 1\n"

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, 4)


def test_untold_takes_no_unset_from_the_body_of_a_data_heredoc(tmp_path):
    # Arrange
    text = "export GH_REPO=o/r\ncat <<EOF\nunset GH_REPO\nEOF\ngh pr view 1\n"

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


# Only a command known to treat its input as data makes a body data; any other program may run it.
@pytest.mark.parametrize("text, line", [
    ("python3 - <<'EOF'\nsubprocess.run([\"gh\", \"pr\", \"list\"])\nEOF\n", 2),
    ("node - <<'EOF'\nexecSync(\"gh pr list\")\nEOF\n", 2),
    ("ssh host <<EOF\ngh pr view 1\nEOF\n", 2),
    ("cat > \"$work/run.sh\" <<'EOF'\ngh pr view 1\nEOF\nbash \"$work/run.sh\"\n", 2),
    ("cat > run.sh <<'EOF'\ngh pr view 1\nEOF\n", 2),
    ("tee run.sh <<'EOF'\ngh pr view 1\nEOF\n", 2),
    ("cat <<'EOF' > \"$out\"\ngh pr view 1\nEOF\n", 2),
    ("some-tool <<EOF\ngh pr view 1\nEOF\n", 2),
    ("echo ok; python3 <<'PY'\nimport subprocess\nsubprocess.run(['gh', 'pr', 'view', '1'])\nPY\n", 3),
])
def test_untold_reads_the_body_of_a_heredoc_fed_to_anything_but_a_data_command(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


# A shell run with assignments before it, a pipe or a continued pipe to a shell, and a pipe whose consumer follows.
@pytest.mark.parametrize("text, line", [
    ("GH_TOKEN=x . /dev/stdin <<'EOF'\ngh pr view 1\nEOF\n", 2),
    ("GH_TOKEN=x source /dev/stdin <<'EOF'\ngh pr view 1\nEOF\n", 2),
    ("A=1 B=2 bash <<'EOF'\ngh pr view 1\nEOF\n", 2),
    ("cat <<'EOF' | \\\nbash\ngh pr list\nEOF\n", 3),
    ("cat <<'EOF' \\\n  | sh\ngh pr list\nEOF\n", 3),
    ("cat <<'EOF' |\ngh pr list\nEOF\nbash\n", 2),
    ("cat <<'EOF' | tee log.txt | bash\ngh pr list\nEOF\n", 2),
])
def test_untold_reads_the_body_of_a_heredoc_that_reaches_a_shell_by_a_pipe_or_an_assignment(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "cat <<EOF # usage for bash\nRun gh pr list to inspect PRs.\nEOF\nexport GH_REPO=o/r\ngh pr list\n",
    "cat <<EOF # sh\nneeds gh\nEOF\n",
    "tee usage.txt <<EOF\nneeds gh\nEOF\n",
    "tee -a /dev/stderr <<EOF\nneeds gh\nEOF\n",
    ": <<'EOF'\nneeds gh\nEOF\n",
    "printf '%s\\n' <<EOF\nneeds gh\nEOF\n",
    "read -r -d '' usage <<'EOF'\nneeds gh\nEOF\n",
    "USAGE=1 cat <<'EOF'\nneeds gh\nEOF\n",
    "cat \\\n  <<'EOF'\nneeds gh\nEOF\n",
    "cat <<'EOF' > usage.md\nneeds gh\nEOF\n",
    "cat <<'EOF' 2>/dev/null\nneeds gh\nEOF\n",
    "echo \"$(cat <<'EOF'\nneeds gh\nEOF\n)\"\n",
])
def test_untold_skips_the_body_of_a_heredoc_fed_to_a_data_command(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text, line", [
    ("printf '%s\\n' 'Example: cat <<true'\ngh pr list\ntrue\n", 2),
    ("echo \"cat <<true\"\ngh pr list\ntrue\n", 2),
    ("echo cat \\<<true\ngh pr list\ntrue\n", 2),
    ("# cat <<true\ngh pr list\ntrue\n", 2),
    ("echo ok # cat <<true\ngh pr list\ntrue\n", 2),
    ("echo $((1 << 2))\ngh pr list\n2\n", 2),
    ("(( x <<true ))\ngh pr list\ntrue\n", 2),
])
def test_untold_takes_no_heredoc_from_a_quoted_or_commented_or_arithmetic_double_angle(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


# A $( or backtick that a heredoc body leaves open goes on in the next lines.
@pytest.mark.parametrize("text, line", [
    ("cat <<EOF\n$(\n  gh pr list --state open\n)\nEOF\n", 3),
    ("cat <<EOF\n`\n  gh pr list\n`\nEOF\n", 3),
    ("cat <<EOF\nOpen: $(echo hi;\ngh pr list)\nEOF\n", 3),
    ("cat <<EOF\n$(\n  echo $(\n    gh pr list\n  )\n)\nEOF\n", 4),
    ("cat <<EOF\n$(\n  date\n)\n  gh pr list\nEOF\n", None),
])
def test_untold_judges_the_lines_of_a_substitution_a_heredoc_body_spans(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    if line is None:
        assert found == []
    else:
        assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "cat <<EOF\n$(\n  GH_REPO=o/r gh pr list\n)\nEOF\n",
    "export GH_REPO=o/r\ncat <<EOF\n$(\n  gh pr list\n)\nEOF\n",
    "cat <<'EOF'\n$(\n  gh pr list\n)\nEOF\n",
    "cat <<EOF\n$(\n  gh pr list # gh-repo: ok\n)\nEOF\n",
])
def test_untold_accepts_a_multi_line_substitution_that_is_told_or_never_run(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


def test_untold_closes_a_multi_line_substitution_at_the_terminator(tmp_path):
    # Arrange: the first heredoc's open $( ends with it; the second body is plain text.
    text = "cat <<A\n$(\nA\ncat <<B\nneeds gh pr list\nB\n"

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text", [
    "GH_REPO=${repo:?set repo} gh pr view 1\n",
    "GH_REPO=${repo:?set repo} GH_TOKEN=x gh pr view 1\n",
    "GH_TOKEN=x GH_REPO=${repo:?set repo} gh pr view 1\n",
])
def test_untold_accepts_a_guarded_expansion_with_spaces_before_gh(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


def test_untold_reads_the_heredoc_of_a_shell_after_a_data_one(tmp_path):
    # Arrange
    text = "cat <<EOF\ngh pr view 1\nEOF\nbash <<EOF\ngh pr view 2\nEOF\n"

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, 5)


# R42: gh.exe is gh.
@pytest.mark.parametrize("text, line", [
    ("gh.exe pr view 1\n", 1),
    ("gh.exe issue list\n", 1),
    ("gh.exe repo view\n", 1),
    ("export GH_REPO=o/r\ngh.exe repo view\n", 2),
    ("gh.exe \"$@\"\n", 1),
    ("gh.exe $sub view 1\n", 1),
    ("export GH_REPO=o/r\nGH_REPO= gh.exe pr view 1\n", 2),
    ("gh.exe pr view 1\nexport GH_REPO=o/r\n", 1),
    ("echo \"$(gh.exe pr view 1)\"\n", 1),
    ("command -v gh.exe >/dev/null\n", 1),
    ("if command -v gh.exe\n", 1),
])
def test_untold_refuses_a_gh_exe_command_not_told_its_repository(tmp_path, text, line):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "export GH_REPO=o/r\ngh.exe pr view 1\n",
    "GH_REPO=o/r gh.exe pr view 1\n",
    "GH_REPO=o/r GH_TOKEN=x gh.exe pr view 1\n",
    "gh.exe repo view o/r\n",
    "export GH_REPO=o/r\ncommand -v gh.exe >/dev/null || exit 1\n",
    "command -v gh.exe >/dev/null # gh-repo: ok\n",
])
def test_untold_accepts_a_gh_exe_command_told_its_repository(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text", [
    "agh.exe pr view 1\n",
    "neigh.exe pr view 1\n",
    "gh.executable pr view 1\n",
    "gh.exe2 pr view 1\n",
    "gh_.exe pr view 1\n",
    "mygh.exe pr view 1\n",
    "cat ~/.config/gh.exe/hosts.yml\n",
])
def test_untold_doesnt_read_a_longer_word_that_contains_gh_exe_as_gh(tmp_path, text):
    # Arrange, Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text, line", [
    ("```bash\ngh.exe pr view 1\n```\n", 2),
    ("Run `gh.exe pr list`.\n", 1),
    ("```bash\ngh.exe repo view\n```\n", 2),
    ("```bash\ngh.exe pr view 1 --repo o/r\ngh.exe pr list\n```\n", 3),
])
def test_untold_markdown_refuses_a_gh_exe_command_in_code_not_told_its_repository(tmp_path, text, line):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "```bash\ngh.exe pr view 1 --repo o/r\n```\n",
    "```bash\nGH_REPO=o/r gh.exe pr view 1\n```\n",
    "Run `gh.exe pr view 1 -R o/r`.\n",
    "Needs the gh.exe CLI.\n",
    "Install `gh.exe` first.\n",
])
def test_untold_markdown_accepts_a_gh_exe_command_told_its_repository_or_named_in_prose(tmp_path, text):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert found == []


# R52: a skill's gh line is copy-pasteable, so a <owner>/<repo> placeholder doesn't tell it its repository.
@pytest.mark.parametrize("text, line", [
    ("```bash\ngh pr view 1 --repo <owner>/<repo>\n```\n", 2),
    ("```bash\ngh pr view 1 --repo=<owner>/<repo>\n```\n", 2),
    ("```bash\ngh pr view 1 -R <owner>/<repo>\n```\n", 2),
    ("```bash\nGH_REPO=<owner>/<repo> gh pr view 1\n```\n", 2),
    ("```bash\ngh repo view <owner>/<repo>\n```\n", 2),
    ("Run `gh pr list --repo <owner>/<repo>`.\n", 1),
    ("Run `gh pr list -R OWNER/REPO` or `gh pr list --repo <owner>/<repo>`.\n", 1),
])
def test_untold_markdown_refuses_a_placeholder_repository_after_repo(tmp_path, text, line):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


def test_check_repo_refuses_a_placeholder_repository_in_a_skill_by_its_relative_path(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {".claude/skills/mine/SKILL.md": "# Mine\n\n```bash\ngh issue list --repo <owner>/<repo>\n```\n"})

    # Assert
    assert found == [".claude/skills/mine/SKILL.md:4: gh issue list --repo <owner>/<repo>"]


# R45 and R52: gotchas.md holds the rules; the docstring points at it.
BASELINE_SKILL = HERE.parents[1]
GOTCHAS = BASELINE_SKILL / "references" / "gotchas.md"
SKILLS_README = BASELINE_SKILL / "template" / "owned" / ".claude" / "skills" / "README.md"


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


# --- mpaulosky/dotfiles#344: hardening of the checker ---

# R49: a wrapper that drops or unsets GH_REPO before gh runs hides gh from the export.
@pytest.mark.parametrize("command", [
    "sudo gh pr view 1",
    "su -c 'gh pr view 1' bob",
    "ssh host gh pr view 1",
    "env -i gh pr view 1",
    "env -u GH_REPO gh pr view 1",
    "env -S 'gh pr view 1'",
    "doas gh pr view 1",
    "runuser -l bob -c 'gh pr view 1'",
    "pkexec gh pr view 1",
    "env --ignore-environment gh pr view 1",
    "env --unset=GH_REPO gh pr view 1",
    "true && sudo gh pr view 1",
    "sudo -u bob gh pr view 1",
    "ssh -o BatchMode=yes host 'gh pr view 1'",
])
def test_untold_refuses_a_gh_behind_a_wrapper_that_drops_gh_repo_even_after_an_export(tmp_path, command):
    # Arrange
    text = f"export GH_REPO=o/r\n{command}\n"

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, 2)


@pytest.mark.parametrize("command", [
    "bash -c 'gh pr view 1'",
    "eval 'gh pr view 1'",
    "echo 'gh pr view 1' | sh",
    "xargs gh pr view 1",
    # Only GH_REPO and the whole environment are lost to the wrappers above.
    "env -u GH_TOKEN gh pr view 1",
    "env GH_TOKEN=x gh pr view 1",
    # A wrapper's own GH_REPO= right before gh tells it.
    "sudo GH_REPO=o/r gh pr view 1",
    "sudo -u bob GH_REPO=o/r gh pr view 1",
    # sudo that does not run gh, then a gh on its own line.
    "sudo true\ngh pr view 1",
])
def test_untold_accepts_a_gh_that_inherits_the_export_or_is_told_by_its_own_prefix(tmp_path, command):
    # Arrange, Act
    found = untold(tmp_path, f"export GH_REPO=o/r\n{command}\n")

    # Assert
    assert found == []


@pytest.mark.parametrize("command", ["sudo GH_REPO=o/r gh pr view 1", "su -c 'GH_REPO=o/r gh pr view 1' bob"])
def test_untold_accepts_a_wrapped_gh_told_by_its_own_gh_repo_prefix_without_an_export(tmp_path, command):
    # Arrange, Act
    found = untold(tmp_path, f"{command}\n")

    # Assert
    assert found == []


@pytest.mark.parametrize("command", ["sudo GH_REPO= gh pr view 1", "sudo GH_REPO=\"$repo\" gh pr view 1"])
def test_untold_refuses_a_wrapped_gh_whose_own_gh_repo_prefix_may_be_empty_even_after_an_export(tmp_path, command):
    # Arrange
    text = f"export GH_REPO=o/r\n{command}\n"

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, 2)


# R51: a NUL byte is dropped by bash, so a file with one is checked as bash reads it, not skipped.
def nul_script(tail):
    """A script whose NUL comes after its first 100 bytes, as bash needs to run it."""
    return b"#!/usr/bin/env bash\n" + b"# padding " * 12 + b"\n" + b"echo start\x00\n" + tail


def test_untold_refuses_an_untold_gh_in_a_file_with_a_nul_after_100_bytes(tmp_path):
    # Arrange
    path = tmp_path / "script.sh"
    path.write_bytes(nul_script(b"gh pr view 1\n"))
    assert path.read_bytes().index(b"\0") > 100

    # Act
    found = check_gh_repo.untold(path)

    # Assert
    assert found == [f"{path}:4: gh pr view 1"]


def test_untold_accepts_a_file_with_a_nul_after_100_bytes_whose_gh_lines_are_told(tmp_path):
    # Arrange
    path = tmp_path / "script.sh"
    path.write_bytes(nul_script(b"export GH_REPO=o/r\ngh pr view 1\n"))

    # Act
    found = check_gh_repo.untold(path)

    # Assert
    assert found == []


def test_untold_takes_an_export_a_nul_splits_as_bash_reads_it(tmp_path):
    # Arrange: bash reads the first line as an export of an empty GH_REPO, which tells nothing.
    path = tmp_path / "script.sh"
    path.write_bytes(b"exp\0ort GH_REPO=\ngh pr view 1\n")

    # Act
    found = check_gh_repo.untold(path)

    # Assert
    assert found == [f"{path}:2: gh pr view 1"]


def test_untold_finds_a_gh_whose_letters_a_nul_splits(tmp_path):
    # Arrange
    path = tmp_path / "script.sh"
    path.write_bytes(b"echo start\ng\0h pr view 1\n")

    # Act
    found = check_gh_repo.untold(path)

    # Assert
    assert found == [f"{path}:2: g\\x00h pr view 1"]


def test_check_repo_refuses_an_untold_gh_in_a_script_with_a_nul_after_100_bytes(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {"scripts/x.sh": nul_script(b"gh pr view 1\n")})

    # Assert
    assert found == ["scripts/x.sh:4: gh pr view 1"]


def test_check_repo_passes_a_script_with_a_nul_after_100_bytes_whose_gh_lines_are_told(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {"scripts/x.sh": nul_script(b"export GH_REPO=o/r\ngh pr view 1\n")})

    # Assert
    assert found == []


def test_check_repo_refuses_an_untold_gh_in_a_skill_with_a_nul_after_100_bytes(tmp_path):
    # Arrange
    skill = b"# Skill\n" + b"padding " * 14 + b"\x00\n```bash\ngh pr view 1\n```\n"

    # Act
    found = repo_check(tmp_path, {".claude/skills/mine/SKILL.md": skill})

    # Assert
    assert found == [".claude/skills/mine/SKILL.md:4: gh pr view 1"]


# R54: a Markdown code block is also indented, <pre>, or a <code> element; HTML comments, <script> and <style> aren't code.
@pytest.mark.parametrize("text, line", [
    ("Intro.\n\n    gh pr view 1\n", 3),
    ("Intro.\n\n\tgh pr view 1\n", 3),
    ("Intro.\n\n        gh pr view 1\n", 3),
    # Any line of the block, after blank lines inside it too.
    ("Intro.\n\n    echo hi\n    gh pr view 1\n", 4),
    ("Intro.\n\n    echo hi\n\n    gh pr view 1\n", 5),
    ("Intro.\n\n    gh pr view 1 --repo o/r\n    gh pr list\n", 4),
    ("# Title\n\n    GH_REPO=\"$repo\" gh pr view 1\n", 3),
    ("> Intro.\n\nText.\n\n    gh issue list\n", 5),
])
def test_untold_markdown_refuses_a_gh_command_in_an_indented_code_block(tmp_path, text, line):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "Intro.\n\n    gh pr view 1 --repo o/r\n",
    "Intro.\n\n\tgh pr view 1 --repo o/r\n",
    "Intro.\n\n    GH_REPO=o/r gh pr view 1\n",
    "Intro.\n\n    echo hi\n",
    # Prose, not code: fewer than 4 spaces, or no blank line before it.
    "Intro.\n\n   gh pr view 1\n",
    "Intro.\n    gh pr view 1\n",
    # A list item's continuation line is no indented block, with a blank line before it or not.
    "- Run it:\n    gh pr view 1\n",
    "- Run it\n\n    gh pr view 1 by hand\n",
    "1. Run it\n\n    gh pr view 1 by hand\n",
    "* Item\n  gh pr view 1\n",
    # The block ended at the first line that isn't indented.
    "Intro.\n\n    echo hi\n\nThen gh pr view 1 by hand.\n",
    # An opt-out on the flagged line.
    "Intro.\n\n    gh pr view 1 <!-- gh-repo: ok -->\n",
])
def test_untold_markdown_accepts_an_indented_block_that_is_told_and_what_is_no_such_block(tmp_path, text):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text, line", [
    ("<pre>\ngh pr view 1\n</pre>\n", 2),
    ("<pre>\necho hi\ngh pr view 1\n</pre>\n", 3),
    ("<pre class=\"sh\">\ngh pr view 1\n</pre>\n", 2),
    ("<pre><code>gh pr view 1\n</code></pre>\n", 1),
    ("<pre><code class=\"language-bash\">\ngh pr view 1\n</code></pre>\n", 2),
    ("<pre>gh pr view 1</pre>\n", 1),
    ("Text\n\n<pre>\n\ngh pr view 1\n\n</pre>\n", 5),
])
def test_untold_markdown_refuses_a_gh_command_in_a_pre_block(tmp_path, text, line):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "<pre>\ngh pr view 1 --repo o/r\n</pre>\n",
    "<pre><code>GH_REPO=o/r gh pr view 1\n</code></pre>\n",
    "<pre>gh pr view 1 --repo o/r</pre>\n",
    # After the closing tag it is prose again.
    "<pre>\necho hi\n</pre>\nRun gh pr view 1 yourself.\n",
    "<pre>echo hi</pre>\ngh pr view 1\n",
    # A tag that only starts with pre isn't <pre>.
    "<prefix>\ngh pr view 1\n</prefix>\n",
    "<pre>\ngh pr view 1 <!-- gh-repo: ok -->\n</pre>\n",
])
def test_untold_markdown_accepts_a_pre_block_that_is_told_or_ended_or_opted_out(tmp_path, text):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text, line", [
    ("Run <code>gh pr view 1</code> now.\n", 1),
    ("Text\n<code>gh issue list</code>\n", 2),
    ("Use <code>gh pr view 1 --repo o/r</code> or <code>gh pr list</code>.\n", 1),
    ("<code class=\"sh\">gh pr view 1</code>\n", 1),
])
def test_untold_markdown_refuses_a_gh_command_in_a_code_element(tmp_path, text, line):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("text", [
    "Run <code>gh pr view 1 --repo o/r</code> now.\n",
    "Run <code>GH_REPO=o/r gh pr view 1</code>.\n",
    # Like a bare `gh` span, <code>gh</code> names the tool.
    "Needs <code>gh</code> installed.\n",
    "Needs <code>gh.exe</code> installed.\n",
    "Run <code>gh pr view 1</code> now <!-- gh-repo: ok -->\n",
    # An element whose closing tag isn't on its line is no inline span.
    "Run <code>gh pr view 1\nnow</code>.\n",
])
def test_untold_markdown_accepts_a_code_element_that_is_told_or_names_the_tool_or_is_opted_out(tmp_path, text):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert found == []


@pytest.mark.parametrize("text", [
    "<!-- gh pr view 1 -->\n",
    "<!--\ngh pr view 1\n-->\n",
    "<script>\ngh pr view 1\n</script>\n",
    "<script>gh pr view 1</script>\n",
    "<style>\ngh pr view 1\n</style>\n",
    "<style>gh pr view 1</style>\n",
])
def test_untold_markdown_doesnt_read_html_comments_scripts_or_styles(tmp_path, text):
    # Arrange, Act
    found = untold_md(tmp_path, text)

    # Assert
    assert found == []


# R60: the repo walk reads a file by what it is, not by how its name is cased or spelled.
@pytest.mark.parametrize("name", ["scripts/up.SH", "scripts/up.Sh", "scripts/x.BASH", "scripts/z.zsh", "scripts/z.ZSH",
                                  ".github/ci/up.SH", ".github/ci/z.zsh"])
def test_check_repo_checks_a_shell_file_by_its_suffix_in_lower_case_and_zsh(tmp_path, name):
    # Arrange, Act
    found = repo_check(tmp_path, {name: UNTOLD})

    # Assert
    assert found == [f"{name}:1: gh pr view 1"]


def test_check_repo_checks_a_markdown_file_by_its_suffix_in_lower_case(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {".claude/skills/mine/SKILL.MD": "`gh pr view 1`\n"})

    # Assert
    assert found == [".claude/skills/mine/SKILL.MD:1: `gh pr view 1`"]


@pytest.mark.parametrize("name", ["scripts/noext", "scripts/tests/noext", ".github/ci/noext"])
@pytest.mark.parametrize("content", [UNTOLD, "\n" + UNTOLD, "#!/usr/bin/env python3\n" + UNTOLD, "# notes\n" + UNTOLD])
def test_check_repo_treats_every_extensionless_file_under_scripts_and_github_ci_as_shell(tmp_path, name, content):
    # Arrange, Act
    found = repo_check(tmp_path, {name: content})

    # Assert
    assert [entry.split(":")[0] for entry in found] == [name]


def test_check_repo_passes_an_extensionless_file_under_scripts_whose_gh_lines_are_told(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {"scripts/noext": TOLD, ".github/ci/noext": TOLD})

    # Assert
    assert found == []


def test_check_repo_keeps_the_shebang_test_for_an_extensionless_file_under_skills(tmp_path):
    # Arrange, Act
    found = repo_check(tmp_path, {".claude/skills/mine/plain": UNTOLD, ".claude/skills/mine/tool": "#!/bin/bash\n" + UNTOLD})

    # Assert
    assert found == [".claude/skills/mine/tool:2: gh pr view 1"]


def refused_names(found):
    return sorted(entry.split(":")[0] for entry in found)


def test_check_repo_refuses_a_scripts_folder_that_is_a_symlink_out_of_the_repo(tmp_path):
    # Arrange
    outside = make_repo(tmp_path / "elsewhere", {"x.sh": UNTOLD})
    repo = tmp_path / "repo"
    repo.mkdir()
    (repo / "scripts").symlink_to(outside, target_is_directory=True)

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert refused_names(found) == ["scripts"]


@pytest.mark.parametrize("folder", [".github/ci", ".claude/skills"])
def test_check_repo_refuses_a_scope_folder_that_is_a_symlink_out_of_the_repo(tmp_path, folder):
    # Arrange
    outside = make_repo(tmp_path / "elsewhere", {"x.sh": TOLD})
    repo = tmp_path / "repo"
    (repo / folder).parent.mkdir(parents=True)
    (repo / folder).symlink_to(outside, target_is_directory=True)

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert refused_names(found) == [folder]


def test_check_repo_refuses_a_symlinked_file_out_of_the_repo_even_when_its_target_is_told(tmp_path):
    # Arrange
    outside = make_repo(tmp_path / "elsewhere", {"x.sh": TOLD})
    repo = make_repo(tmp_path / "repo", {"scripts/ok.sh": TOLD})
    (repo / "scripts" / "link.sh").symlink_to(outside / "x.sh")

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert refused_names(found) == ["scripts/link.sh"]


def test_check_repo_refuses_a_symlinked_folder_out_of_the_repo_inside_a_scope_folder(tmp_path):
    # Arrange
    outside = make_repo(tmp_path / "elsewhere", {"y.sh": TOLD})
    repo = make_repo(tmp_path / "repo", {"scripts/ok.sh": TOLD})
    (repo / "scripts" / "linked").symlink_to(outside, target_is_directory=True)

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert refused_names(found) == ["scripts/linked"]


def test_check_repo_refuses_a_dangling_symlink(tmp_path):
    # Arrange
    repo = make_repo(tmp_path / "repo", {"scripts/ok.sh": TOLD})
    (repo / "scripts" / "dangling.sh").symlink_to(tmp_path / "repo" / "missing.sh")

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert refused_names(found) == ["scripts/dangling.sh"]


def test_check_repo_refuses_a_symlink_that_points_at_itself(tmp_path):
    # Arrange
    repo = make_repo(tmp_path / "repo", {"scripts/ok.sh": TOLD})
    (repo / "scripts" / "a").symlink_to("a")

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert refused_names(found) == ["scripts/a"]


def test_check_repo_refuses_two_symlinks_that_point_at_each_other_as_files_and_as_folders(tmp_path):
    # Arrange
    repo = make_repo(tmp_path / "repo", {"scripts/ok.sh": TOLD})
    (repo / "scripts" / "a").symlink_to("b")
    (repo / "scripts" / "b").symlink_to("a")
    (repo / "scripts" / "c").symlink_to("d", target_is_directory=True)
    (repo / "scripts" / "d").symlink_to("c", target_is_directory=True)

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert refused_names(found) == ["scripts/a", "scripts/b", "scripts/c", "scripts/d"]


def test_check_repo_refuses_a_symlink_whose_resolve_raises_a_loop_error(tmp_path, monkeypatch):
    # Arrange: Python 3.12's Path.resolve() raises RuntimeError for a loop; 3.13 doesn't, so make it raise here.
    repo = make_repo(tmp_path / "repo", {"scripts/ok.sh": TOLD})
    (repo / "scripts" / "a").symlink_to("a")
    real_resolve = Path.resolve

    def resolve(self, *args, **kwargs):
        if self.name == "a":
            raise RuntimeError(f"Symlink loop from {self}")
        return real_resolve(self, *args, **kwargs)

    monkeypatch.setattr(Path, "resolve", resolve)

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert refused_names(found) == ["scripts/a"]


def test_check_repo_refuses_a_dangling_symlinked_folder(tmp_path):
    # Arrange
    repo = tmp_path / "repo"
    repo.mkdir()
    (repo / "scripts").symlink_to(tmp_path / "missing", target_is_directory=True)

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert refused_names(found) == ["scripts"]


def test_check_repo_passes_a_symlink_to_a_file_inside_the_root(tmp_path):
    # Arrange
    repo = make_repo(tmp_path / "repo", {"scripts/real.sh": TOLD, "lib/real.sh": TOLD})
    (repo / "scripts" / "link.sh").symlink_to(repo / "scripts" / "real.sh")
    (repo / "scripts" / "relative.sh").symlink_to(Path("..") / "lib" / "real.sh")

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert found == []


def test_check_repo_passes_a_symlinked_folder_inside_the_root(tmp_path):
    # Arrange
    repo = make_repo(tmp_path / "repo", {"scripts/real/x.sh": TOLD})
    (repo / "scripts" / "linked").symlink_to(repo / "scripts" / "real", target_is_directory=True)

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert found == []


def test_check_repo_walks_the_target_of_a_symlink_inside_the_root_as_itself(tmp_path):
    # Arrange
    repo = make_repo(tmp_path / "repo", {"scripts/real.sh": UNTOLD})
    (repo / "scripts" / "link.sh").symlink_to(repo / "scripts" / "real.sh")

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert "scripts/real.sh:1: gh pr view 1" in found
    assert refused_names(found) in (["scripts/link.sh", "scripts/real.sh"], ["scripts/real.sh"])


def test_main_repo_mode_fails_for_a_symlink_out_of_the_repo_and_names_it(tmp_path, capsys):
    # Arrange
    outside = make_repo(tmp_path / "elsewhere", {"x.sh": UNTOLD})
    repo = make_repo(tmp_path / "repo", {"scripts/ok.sh": TOLD})
    (repo / "scripts" / "link.sh").symlink_to(outside / "x.sh")

    # Act
    code = check_gh_repo.main(["--repo", str(repo)])

    # Assert
    assert code == 1
    assert "scripts/link.sh" in capsys.readouterr().out


# A wrapper split from its gh by a backslash continuation still drops the export.
@pytest.mark.parametrize("command", [
    "sudo -u bob \\\n  gh pr view 1",
    "env -i \\\n  gh pr view 1",
    "ssh host \\\n  gh pr view 1",
    "su -c \\\n  'gh pr view 1' bob",
    "sudo \\\n  -u bob \\\n  gh pr view 1",
])
def test_untold_refuses_a_gh_on_the_continuation_of_a_wrapper_line_even_after_an_export(tmp_path, command):
    # Arrange
    text = f"export GH_REPO=o/r\n{command}\n"
    line = text.count("\n")

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert_refused_at(found, text, line)


@pytest.mark.parametrize("command", [
    "bash -c \\\n  'gh pr view 1'",
    "xargs \\\n  gh pr view 1",
    "sudo GH_REPO=o/r \\\n  gh pr view 1",
    "echo sudo \\\n  ; gh pr view 1",
    "sudo true\ngh pr view 1",
])
def test_untold_accepts_a_continued_gh_that_inherits_the_export_or_follows_no_wrapper(tmp_path, command):
    # Arrange
    text = f"export GH_REPO=o/r\n{command}\n"

    # Act
    found = untold(tmp_path, text)

    # Assert
    assert found == []


# A symlink must not hide the folder it points at, nor make a file Owned that isn't.

def test_check_repo_checks_the_real_folder_a_symlink_sorting_before_it_points_at(tmp_path):
    # Arrange: hardening-review sorts before zzz, and its link path is Owned.
    repo = make_repo(tmp_path / "repo", {".claude/skills/zzz/run.sh": UNTOLD})
    (repo / ".claude" / "skills" / "hardening-review").symlink_to("zzz", target_is_directory=True)

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert: reported once, by its real path.
    assert found == [".claude/skills/zzz/run.sh:1: gh pr view 1"]


def test_check_repo_checks_a_folder_outside_the_scope_that_an_owned_symlink_points_at(tmp_path):
    # Arrange
    repo = make_repo(tmp_path / "repo", {"docs/x/run.sh": UNTOLD, "scripts/ok.sh": TOLD})
    (repo / ".claude" / "skills").mkdir(parents=True)
    (repo / ".claude" / "skills" / "pre-push-review").symlink_to("../../docs/x", target_is_directory=True)

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert: the link's path is Owned, but the folder it points at is not, and it is checked as itself.
    assert found == ["docs/x/run.sh:1: gh pr view 1"]


def test_check_repo_reports_a_file_reached_by_two_paths_once(tmp_path):
    # Arrange
    repo = make_repo(tmp_path / "repo", {"scripts/real/run.sh": UNTOLD})
    (repo / "scripts" / "alias").symlink_to("real", target_is_directory=True)
    (repo / "scripts" / "run2.sh").symlink_to("real/run.sh")

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert found == ["scripts/real/run.sh:1: gh pr view 1"]


# Text that comes from the PR is escaped before it reaches the log.

def test_untold_escapes_a_carriage_return_inside_a_line(tmp_path):
    # Arrange, Act
    found = untold(tmp_path, "gh pr view 1\r::error::x\n")

    # Assert
    assert len(found) == 1
    assert "\r" not in found[0]
    assert found[0].endswith(": gh pr view 1\\r::error::x")


@pytest.mark.parametrize("char, escaped", [("\x0b", "\\x0b"), ("\x0c", "\\x0c"), ("\x1b", "\\x1b"),
                                           (" ", "\\u2028"), ("\x85", "\\x85")])
def test_untold_escapes_other_control_characters(tmp_path, char, escaped):
    # Arrange, Act
    found = untold(tmp_path, f"gh pr view 1{char}x\n")

    # Assert
    assert found[0].endswith(f"gh pr view 1{escaped}x")


def test_untold_keeps_a_tab_and_non_ascii_text(tmp_path):
    # Arrange, Act
    found = untold(tmp_path, "gh pr view 1\té\n")

    # Assert
    assert found[0].endswith("gh pr view 1\té")


def test_untold_markdown_escapes_a_carriage_return_inside_a_line(tmp_path):
    # Arrange, Act
    found = untold_md(tmp_path, "`gh pr view 1`\r::error::x\n")

    # Assert
    assert len(found) == 1
    assert "\r" not in found[0]
    assert found[0].endswith("\\r::error::x")


def test_check_repo_escapes_line_breaks_in_a_file_name(tmp_path):
    # Arrange
    repo = make_repo(tmp_path / "repo", {"scripts/a\n::error::b.sh": UNTOLD})

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert found == ["scripts/a\\n::error::b.sh:1: gh pr view 1"]


def test_check_repo_escapes_line_breaks_in_the_name_of_a_symlink_it_refuses(tmp_path):
    # Arrange
    repo = make_repo(tmp_path / "repo", {"scripts/ok.sh": TOLD})
    (repo / "scripts" / "a\r::error::b").symlink_to(tmp_path / "nowhere")

    # Act
    found = check_gh_repo.check_repo(repo)

    # Assert
    assert found == ["scripts/a\\r::error::b: a symlink to nothing"]


@pytest.mark.parametrize("text, refused", [
    ("if false; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [4]),
    ("if false\nthen\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if true; then\n  :\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if [ -n \"$x\" ]; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if true; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\n  :\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if ! true; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [4]),
    ("if [ 0 -ne 0 ]; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [4]),
    ("if [ 1 -eq 0 ]; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [4]),
    ("if :; then\n:\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if false; then # note\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [4]),
    # near misses still run
    ("if falsey; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false && x; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if ! false; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # an elif branch is assumed to run, whatever the if's condition
    ("if false; then\n:\nelif x; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # nesting
    ("if false; then\nif true; then\nexport GH_REPO=o/r\nfi\nfi\ngh pr view 1\n", [6]),
    ("if true; then\nif false; then\n:\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\nif x; then\n:\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    # a one-line if opens and closes on its line, however its fi is followed
    ("if false; then\nif x; then y; fi # note\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if x; then y; fi && z\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if false; then :; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("(if x; then y; fi)\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if x; then y; fi; if z; then w; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    # the word fi that is no keyword
    ("if grep -q fi f; then\n:\nfi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if x; then echo fi; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if false; then\necho fi\nfi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if false; then\n:\nelse\nif y; then\nexport GH_REPO=o/r\nfi\nfi\ngh pr view 1\n", []),
    # what the scanner tells apart by reading the keywords in source order
    ("if false; then\nif x; then y; fi # note\nfi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if false; then\nif grep -q fi; then\n:\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    ("  if false; then\nexport GH_REPO=o/r\n  fi\ngh pr view 1\n", [4]),
    ("if false; then\nwhile x; do if y; then z; fi; done\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\nx; { if y; then z; fi; }\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\nif y; then z; else if w; then v; fi; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # a line of a multi-line string that starts like fi but is glued to a quote is not a keyword, so the dead branch stays dead
    ("if false; then\necho \"\nfi\"\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    # a quoted or commented keyword is not one: it neither pops the dead branch nor opens a phantom one
    ("if false; then\nprintf '%s\\n' '; fi'\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho \"; fi\"\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho x # ; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho x \\; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("echo '; if false; then'\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("echo x # ; if false; then\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    # keywords on one line count in source order: the fi closes the old block before the new if opens
    ("if false; then\n:\nfi; if true; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\n:\nfi; if x; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if true; then\nif false; then\n:\nfi; if false; then\n:\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # an if alone on its line, or glued to ( , still opens a block that its fi closes
    ("if false; then\nif\n  x\nthen\n  :\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [9]),
    ("if false; then\nif(true); then :; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # a word after > or < is an argument, never a keyword
    ("if false; then\necho x > fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\ncat < fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # the if of a case arm is one
    ("if false; then\ncase $x in\n a) if y; then z; fi ;;\nesac\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    # a backslash escapes a quote inside "..." and $'...', not inside '...'
    ("if false; then\necho \"a\\\" ; fi \"\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho 'a\\'; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\necho $'\\' ; fi '\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n'a\\' ; fi $\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # ${...} is one word, closed or not; a word after it is an argument
    ("if false; then\necho ${x:-a; fi}\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho ${x; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho ${x} fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # {fi} is one word; a keyword is one only at the start of a command
    ("if false; then\n{fi}\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho then if x\nfi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    # then, !, while and time keep the next word at the start of a command
    ("if false; then\nif x; then if y; then z; fi; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n! if x; then y; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\nwhile if x; then y; fi; do :; done\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\ntime if x; then y; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # a line a trailing backslash continues is not the start of a command
    ("if false; then\necho x \\\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    # a ) that closes an expansion, however the parentheses nest, ends no command; one that closes a subshell does
    ("if false; then\necho $(true) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho $((1+2)) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\na=(x y) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\ncat <(true) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n(true) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\necho $(if x; then y; fi)\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # a redirect at the start of a command is followed by an argument; {if is one word, { if starts a block
    ("if false; then\n> fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n{if x; then y; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\n{\tif y; then z; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # a ; or & right after ${...} starts a command
    ("if false; then\necho ${x}; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # a line after one that ends mid-command is no command start; after ; && then or a blank, a comment, or an even run of backslashes it is
    ("if false; then\n:; \\\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\nif x; then \\\nif y; then z; fi; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if true; then \\\nif false; then\nexport GH_REPO=o/r\nfi\nfi\ngh pr view 1\n", [6]),
    ("foo && \\\nif false; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("# note \\\nif false; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    (" \\\nif false; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho x \\\\\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\n:\necho x \\\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    ("if false; then\n:\necho x \\\nelif y; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    # a comment ends the command, even one that ends in a backslash
    ("if false; then\necho x # c \\\nif y; then\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    ("if false; then\necho x # note \\\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\n:\necho x # c \\\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\necho x \\\n# note\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # arithmetic and array elements are not commands
    ("if false; then\n(( fi ))\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho $(( fi ))\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\nx=$(( if ))\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\na=(fi)\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n((if x; then y; fi))\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho $((true) fi)\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\ncat >(true) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho $(a $(b) fi)\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\nx=$(if a; then b; fi) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n(echo $(a)) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\n(true) fi $\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # parentheses nested in arithmetic or an array are skipped too; the contents end at the matching )
    ("if false; then\nx=$(( (fi) ))\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n(( (if) ))\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\na=( (if) )\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho $(( (1+2) * 3 )) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n(( (a) * 2 )) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\nx=(a (b)) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho $(( (1) )) fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n((x)); fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\na=(x); fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\na=(x; fi)\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n(if x; then y; fi)\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # a quoted, escaped or ${...} parenthesis in an array or arithmetic neither closes nor opens it
    ("if false; then\narr=( \"a)\" ); if true; then\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if false; then\narr=( \"(\" ); if true; then\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if false; then\narr=( ')' ); if true; then\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if false; then\narr=( \\) ); if true; then\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if false; then\narr=( ${x:-)} ); if true; then\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if false; then\narr=( \"a\\\"b)\" ); if true; then\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if false; then\narr=( \\) ; fi )\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\na=( \\\nfi )\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if false; then\nx=( $(fi) )\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # an escaped $ and the second $ of $$ open no ANSI-C quote; a live $ does, and a backslash then escapes the quote
    ("if false; then\necho \\$'a\\' ; if x; then\n:\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    ("if false; then\necho $$'a\\' ; if x; then\n:\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    ("if false; then\n$'\\' ; fi '\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # a "..." that holds a $( holds commands with quotes of their own
    ("if false; then\necho \"$(echo \"; fi \")\"\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # else and elif are keywords wherever a command starts, not only at the start of a line
    ("if true; then :; else\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [4]),
    ("if false; then :; else\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if x; then :; elif y; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # an extglob group holds a pattern, not a command
    ("if false; then\n[[ $x == +(fi) ]]\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\ncase $x in @(if|fi)) :;; esac\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # a ( that follows a space inside a $( or a subshell is a subshell, and its fi counts
    ("if false; then\n( (fi) )\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\n$( (fi) )\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\na=( $( (fi) ) )\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\ncase x in (a) if y; then z; fi;; esac\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # ${...} in an array or arithmetic is stepped over, closed or not; a block that is open at the end of the line goes on in the next
    ("if false; then\narr=( ${x:-)} ; fi )\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\narr=( ${x ) ; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n(if x; then\n:\nfi)\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    ("if false; then\nx=$(if y; then\n:\nfi)\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    # a dead export doesn't cancel an earlier one; | and & end a word
    ("export GH_REPO=o/r\nif false; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\necho x|if y; then z; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho x&if y; then z; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # an escaped $ before a $ run, and the second $ of $$, decide what a ' or a ( after them opens
    ("if false; then\necho \\$$'a\\' ; fi '\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho \"$$(\" ; if y; then\n:\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [7]),
    # a dollar-paren inside '...' or $'...' is no substitution (only "..." follows one)
    ("if false; then\necho '$(' ; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if false; then\necho $'$(' ; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    # parenthesis_end steps over a backslash, quotes holding ) and parentheses of their own
    ("if false; then\necho \"$(echo \\\")\" ; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if false; then\necho \"$(echo \")\")\" ; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if false; then\necho \"$(echo '\"')\" ; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if false; then\necho \"$(echo (x) \")\")\" ; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    # extglob groups with the prefixes ?, * and ! (@ and + are tested above)
    ("if false; then\n[[ $x == ?(fi) ]]\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n[[ $x == *(fi) ]]\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n[[ $x == !(fi) ]]\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # a condition is judged only on an if that is the first keyword of its line
    ("if false; then :; fi; if y; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if true; then :; fi; if y; then :; else\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # an elif runs whatever the if before it was, and keeps no always flag for an else after it
    ("if false; then :; elif y; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if true; then\n:\nelif x; then\n:\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # a stray fi is ignored
    ("fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    # a stray elif before any if is ignored
    ("elif x; then\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    # a { after an argument is an argument; a keyword follows a pipe; an odd run of three backslashes continues the line
    ("if false; then\necho { fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\necho x | if y; then z; fi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n:; \\\\\\\nfi\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    # an elif leaves its branch running for the else after it; else and elif are whole words; a stray else is ignored
    ("if false; then\n:\nelif x; then\n:\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if true; then\nelsewhere\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false; then\neliffy\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("else\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    # the else of a condition that is not plainly true runs
    ("if ! true; then\n:\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if grep -q :; then\n:\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
])
def test_untold_takes_an_export_only_from_a_branch_that_can_run(tmp_path, text, refused):
    assert lines_of(untold(tmp_path, text)) == refused


def test_tokens_takes_linear_time_for_a_long_unquoted_word():
    start = time.perf_counter()
    found = check_gh_repo.tokens("a" * 200_000)
    assert found == [("word", "a" * 200_000, False)]
    assert time.perf_counter() - start < 10


def test_data_heredocs_does_not_tokenize_a_line_without_a_heredoc(monkeypatch):
    monkeypatch.setattr(check_gh_repo, "tokens", lambda text: pytest.fail("tokenized"))
    assert check_gh_repo.data_heredocs("echo hi && ls", ["x"], 0) == []


def test_untold_reads_a_long_file_of_short_lines_in_linear_time(tmp_path):
    start = time.perf_counter()
    found = untold(tmp_path, "export GH_REPO=o/r\n" + "echo hi\n" * 40_000 + "gh pr view 1\n")
    assert found == []
    assert time.perf_counter() - start < 30


@pytest.mark.parametrize("header", [
    "if cat <<EOF", "elif cat <<EOF", "while cat <<EOF", "until cat <<EOF", "! cat <<EOF", "{ cat <<EOF", "time cat <<EOF",
    "then cat <<EOF", "else cat <<EOF", "do cat <<EOF",
])
def test_data_heredocs_sees_through_the_words_before_a_command(header):
    assert check_gh_repo.data_heredocs(header, ["gh pr view 1", "EOF"], 0) == [("EOF", False, False)]


def test_untold_reads_an_if_followed_by_a_long_run_of_blanks_without_hanging(tmp_path):
    assert untold(tmp_path, "if x" + " " * 5000 + "y\nfi\n") == []


class NoSlice(list):
    """A list that fails when sliced, as a copy of the rest of a long file would."""

    def __getitem__(self, index):
        assert not isinstance(index, slice), "the lines after a header were sliced"
        return super().__getitem__(index)


def test_data_heredocs_reads_the_lines_after_the_header_without_slicing_them():
    assert check_gh_repo.data_heredocs("cat <<EOF", NoSlice(["cat <<EOF", "gh pr view 1", "EOF"]), 1) == [("EOF", False, False)]


def test_untold_never_slices_the_lines_of_a_file_with_a_heredoc(tmp_path, monkeypatch):
    path = tmp_path / "script.sh"
    path.write_text("cat <<EOF\nhello\nEOF\ngh pr view 1\n")
    monkeypatch.setattr(check_gh_repo, "read_lines", lambda _: NoSlice(["cat <<EOF", "hello", "EOF", "gh pr view 1"]))
    assert lines_of(check_gh_repo.untold(path)) == [4]


@pytest.mark.parametrize("char", sorted(check_gh_repo.WORD_END) + ["{", "}", "\\", "'", '"', "$", "#"])
def test_keywords_takes_every_character_that_ends_a_word(char):
    # A character that ends a word but that no branch of the scan takes would never advance it: fail after 5 s instead of hanging.
    def stop(*_):
        raise TimeoutError(f"keywords() did not finish on {char!r}")
    previous = signal.signal(signal.SIGALRM, stop)
    signal.alarm(5)
    try:
        assert check_gh_repo.keywords(char * 3)[0] == []
    finally:
        signal.alarm(0)
        signal.signal(signal.SIGALRM, previous)


@pytest.mark.parametrize("text, expected", [
    ("$'a'", True), ("a$'a'", True), ("'a'", False), ("\\$'a'", False), ("$$'a'", False), ("$$$'a'", True), ("\\\\$'a'", True),
    ("\\$$'a'", True), ("\\\\$$'a'", False), ("\\$$$'a'", False), ("\"$'a'\"", True),
    # a line whose last character is a $ or a backslash: without the bound checks in live_dollar the walk to the left of index 0 wraps to the end of the line (mutations.json)
    ("$'a'$", True), ("$'a'\\", True), ("$$$'a'\\", True),
])
def test_live_dollar_takes_a_dollar_that_is_live_before_the_quote(text, expected):
    assert check_gh_repo.live_dollar(text, text.index("'") - 1) is expected


def test_live_dollar_is_false_outside_the_line():
    assert not check_gh_repo.live_dollar("$", -1)
    assert not check_gh_repo.live_dollar("a", 0)


@pytest.mark.parametrize("depth", [49, 50, 51, 2000])
def test_keywords_reads_deeply_nested_quotes_without_failing(depth):
    assert check_gh_repo.keywords('"$(' * depth + "x")[0] == []


def test_quoted_end_follows_nested_quotes_exactly_fifty_deep(monkeypatch):
    seen = []
    real = check_gh_repo.parenthesis_end
    monkeypatch.setattr(check_gh_repo, "parenthesis_end", lambda line, position, depth: (seen.append(depth), real(line, position, depth))[1])
    check_gh_repo.keywords('"$(' * 200 + "x")
    assert max(seen) == 50 == check_gh_repo.NESTING_LIMIT


def test_untold_reads_a_file_of_heredoc_headers_without_a_terminator_in_linear_time(tmp_path):
    start = time.perf_counter()
    found = untold(tmp_path, "cat <<EOF\n" * 30_000 + "gh pr view 1\n")
    assert lines_of(found) == [30_001]
    assert time.perf_counter() - start < 20


def test_untold_reads_a_file_of_continued_lines_in_linear_time(tmp_path):
    start = time.perf_counter()
    found = untold(tmp_path, "x \\\n" * 20_000 + "gh pr view 1\n")
    assert lines_of(found) == [20_001]
    assert time.perf_counter() - start < 20


@pytest.mark.parametrize("chain", [check_gh_repo.CONTINUATION_LIMIT - 1, check_gh_repo.CONTINUATION_LIMIT, check_gh_repo.CONTINUATION_LIMIT + 1])
def test_continued_text_reads_back_at_most_the_limit(chain):
    lines = [f"{index}\\" for index in range(chain)] + ["end"]
    wanted = "".join(str(index) for index in range(max(0, chain - check_gh_repo.CONTINUATION_LIMIT), chain))
    assert check_gh_repo.continued_text(lines, len(lines), check_gh_repo.joined_lines(lines)) == (wanted, chain > check_gh_repo.CONTINUATION_LIMIT)


@pytest.mark.parametrize("text", ["\\a" * 100_000, "'a'" * 100_000, "$((1))" * 100_000, "a'b'" * 100_000])
def test_tokens_takes_linear_time_for_a_long_word_of_every_kind(text):
    start = time.perf_counter()
    assert len(check_gh_repo.tokens(text)) == 1
    assert time.perf_counter() - start < 10


@pytest.mark.parametrize("lines, start, expected", [
    (["cat <<EOF", "EOF"], 1, [("EOF", False, False)]),
    (["cat <<EOF", "EOF"], 2, []),
    (["cat <<EOF", "x", "EOF"], 1, [("EOF", False, False)]),
    (["cat <<-EOF", "\tEOF"], 1, [("EOF", True, False)]),
    (["cat <<EOF", "\tEOF"], 1, []),
])
def test_data_heredocs_finds_a_delimiter_from_the_line_after_the_header(lines, start, expected):
    assert check_gh_repo.data_heredocs(lines[0], lines, start) == expected


@pytest.mark.parametrize("chain, refused", [(check_gh_repo.CONTINUATION_LIMIT - 1, False), (check_gh_repo.CONTINUATION_LIMIT, False),
                                            (check_gh_repo.CONTINUATION_LIMIT + 1, True)])
def test_untold_refuses_a_gh_command_after_a_continued_chain_longer_than_the_limit(tmp_path, chain, refused):
    # The start of a chain this long may hold a sudo that drops GH_REPO, so the export no longer tells the gh.
    found = untold(tmp_path, "export GH_REPO=o/r\n" + "\\\n" * chain + "gh pr view 1\n")
    assert lines_of(found) == ([chain + 2] if refused else [])


def test_untold_accepts_a_gh_command_told_by_its_own_assignment_after_a_long_chain(tmp_path):
    chain = check_gh_repo.CONTINUATION_LIMIT + 5
    assert untold(tmp_path, "\\\n" * chain + "GH_REPO=o/r gh pr view 1\n") == []


def test_data_heredocs_takes_the_last_occurrence_of_a_delimiter():
    lines = ["EOF", "cat <<EOF", "EOF"]
    assert check_gh_repo.data_heredocs(lines[1], lines, 2) == [("EOF", False, False)]
    assert check_gh_repo.data_heredocs(lines[1], lines, 3) == []
    tabbed = ["\tEOF", "cat <<-EOF", "\tEOF"]
    assert check_gh_repo.data_heredocs(tabbed[1], tabbed, 2) == [("EOF", True, False)]


def test_continued_text_stops_at_the_first_line_of_the_file():
    both = ["a\\", "b\\"]
    assert check_gh_repo.continued_text(both, 2, [True, False]) == ("a", False)
    assert check_gh_repo.continued_text(both, 1, [True, False]) == ("", False)
    # an empty file: the guard on the index keeps the walk off joins[-1]
    assert check_gh_repo.continued_text([], 1, []) == ("", False)
    # the joins given are the ones read, not the backslashes of the lines
    assert check_gh_repo.continued_text(both, 2, [False, False]) == ("", False)


@pytest.mark.parametrize("line, start", [
    ("# note", 0), ("echo x # note", 7), ("echo x#y", -1), ("echo '#' x", -1), ("echo \"# x\"", -1), ("echo ${#x}", -1), ("echo $#", -1),
    ("echo x;# c", 7), ("a\\# b", -1), ("echo \"$(echo '#')\" # c", 19), ("", -1),
    ("echo 'a #b' # c", 12), ("echo \"a #b\" # c", 12), ("echo ${x:- #y} # c", 15), ("echo ${x:- #y}", -1),
    # each character that ends a word lets a # after it start a comment
    ("echo x\t# c", 7), ("echo x&# c", 7), ("echo x|# c", 7), ("echo (# c", 6), ("echo x)# c", 7), ("echo x<# c", 7), ("echo x># c", 7),
    # an escaped quote opens no string; a ${ that never closes holds the rest of the line
    ("echo \\'x # c", 9), ("echo ${x #y", -1),
    # a backslash skips the next character and no more (the second space is the word boundary), and at least it (the first space is part of the word)
    ("echo a\\  # c", 9), ("echo a\\ # c", -1),
])
def test_comment_start_finds_a_hash_that_begins_a_word_outside_quotes(line, start):
    assert check_gh_repo.comment_start(line) == start


@pytest.mark.parametrize("line, joins", [
    ("x \\", True), ("x \\\\", False), ("x \\\\\\", True), ("# x \\", False), ("echo x # y \\", False), ("echo '#' \\", True), ("", False),
    ("echo ${x \\", True), ("echo ${x # y \\", True),
])
def test_continues_counts_a_trailing_backslash_outside_a_comment(line, joins):
    assert check_gh_repo.continues(line) is joins


@pytest.mark.parametrize("text, refused", [
    ("export GH_REPO=o/r\n# env -i \\\ngh pr view 1\n", []),
    ("export GH_REPO=o/r\n# unset GH_REPO \\\ngh pr view 1\n", []),
    ("# GH_REPO=o/r \\\ngh pr view 1\n", [2]),
    ("echo # GH_REPO=o/r \\\ngh pr view 1\n", [2]),
    ("export GH_REPO=o/r\n# sudo \\\ngh pr view 1\n", []),
    ("export GH_REPO=o/r\nsudo env \"FOO=a \\\n; \\\n" + "\\\n" * 101 + "\" gh pr view 1\n", [105]),
    ("export GH_REPO=o/r\n" + "x \\\n" * 100 + "gh pr view 1\n", []),
    ("export GH_REPO=o/r\n" + "x \\\n" * 101 + "gh pr view 1\n", [103]),
    ("export GH_REPO=o/r\n" + "echo hi; \\\n" * 100 + "gh pr view 1\n", []),
    ("export GH_REPO=o/r\n" + "echo hi; \\\n" * 101 + "gh pr view 1\n", [103]),
])
def test_untold_reads_a_chain_as_bash_does_and_fails_closed_when_it_is_cut(tmp_path, text, refused):
    assert lines_of(untold(tmp_path, text)) == refused


@pytest.mark.parametrize("text, refused", [
    # A comment that ends in a backslash joins no line, so the heredoc after it is read as data (a regression guard: the refusal held before joined_lines too).
    ("echo hi # c \\\ncat <<EOF\nexport GH_REPO=o/r\nEOF\ngh pr view 1\n", [5]),
    # The same for a heredoc header that carries the comment.
    ("cat <<EOF # c \\\nexport GH_REPO=o/r\nEOF\ngh pr view 1\n", [4]),
])
def test_untold_reads_a_heredoc_next_to_a_comment_that_ends_in_a_backslash_as_data(tmp_path, text, refused):
    assert lines_of(untold(tmp_path, text)) == refused


@pytest.mark.parametrize("text, refused", [
    # A # inside a string that spans lines is no comment: the chain goes on to the heredoc header, whose body is data (the gh is refused).
    ("echo \"a \\\n # b \\\nc\" ; cat <<EOF\nexport GH_REPO=o/r\nEOF\ngh pr view 1\n", [6]),
    # The same with a CRLF file.
    ("echo \"a \\\r\n # b \\\r\nc\" ; cat <<EOF\r\nexport GH_REPO=o/r\r\nEOF\r\ngh pr view 1\r\n", [6]),
])
def test_untold_joins_a_line_through_a_hash_inside_a_string_that_spans_lines(tmp_path, text, refused):
    path = tmp_path / "script.sh"
    path.write_bytes(text.encode())
    assert lines_of(check_gh_repo.untold(path)) == refused


@pytest.mark.parametrize("text, refused", [
    # A string that spans lines closes before a comment ending in a backslash: the comment joins nothing, so its GH_REPO= tells no later gh (Issue #51).
    ("echo \"x \\\ny\" # GH_REPO=o/r \\\ngh pr view 1\n", [3]),
    # The same with a CRLF file.
    ("echo \"x \\\r\ny\" # GH_REPO=o/r \\\r\ngh pr view 1\r\n", [3]),
    # The same over three lines of string.
    ("echo \"x \\\ny \\\nz\" # GH_REPO=o/r \\\ngh pr view 1\n", [4]),
    # A comment ending in a backslash after a string on one line still joins nothing (the chain stops at the comment on that line).
    ("echo \"x\" # GH_REPO=o/r \\\ngh pr view 1\n", [2]),
    # The line after that comment starts a command: an export in an if that never runs leaves GH_REPO unset.
    ("echo \"x \\\ny\" # c \\\nif false; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    # A real continuation after the closed string still tells the gh its own GH_REPO= on the joined line.
    ("echo \"x \\\ny\" ; GH_REPO=o/r \\\ngh pr view 1\n", []),
])
def test_untold_judges_a_chain_by_the_string_an_earlier_line_opened(tmp_path, text, refused):
    path = tmp_path / "script.sh"
    path.write_bytes(text.encode())
    assert lines_of(check_gh_repo.untold(path)) == refused


@pytest.mark.parametrize("lines, expected", [
    ([], []),
    (["a"], [False]),
    (["a \\"], [False]),
    (["a \\", "b"], [True, False]),
    # a string that spans lines closes before a comment ending in a backslash: that line joins nothing
    (["echo \"x \\", "y\" # c \\", "z"], [True, False, False]),
    # a comment on its own line ends the chain
    (["a \\", "# c \\", "d"], [True, False, False]),
    # two chains one after the other
    (["a \\", "b", "c \\", "d"], [True, False, True, False]),
])
def test_joined_lines_says_which_lines_join_the_next(lines, expected):
    assert check_gh_repo.joined_lines(lines) == expected


@pytest.mark.parametrize("hash_line, joins", [
    # A real comment ending in a backslash: at the limit and before it the chain ends there (False); past it logical_line joins on the backslash alone and the line can't be judged (None).
    (check_gh_repo.CONTINUATION_LIMIT - 2, False), (check_gh_repo.CONTINUATION_LIMIT - 1, False),
    (check_gh_repo.CONTINUATION_LIMIT, None), (check_gh_repo.CONTINUATION_LIMIT + 1, None),
])
def test_joined_lines_cuts_at_a_real_comment_and_marks_one_past_the_limit_as_unjudged(hash_line, joins):
    lines = ["echo a \\"] + ["x \\"] * (hash_line - 1) + [" # b \\", "tail"]
    found = check_gh_repo.joined_lines(lines)
    assert found[hash_line] is joins
    assert found[:hash_line] == [True] * hash_line


def test_joined_lines_judges_the_line_at_the_limit_on_the_chain_whole():
    # A # inside a string the first line opened, on the chain's CONTINUATION_LIMIT-th line, is scanned with the chain (no comment): that line still joins the next.
    limit = check_gh_repo.CONTINUATION_LIMIT
    lines = ["echo \"a \\"] + ["x \\"] * (limit - 2) + [" # b \\", "c\" ; d"]
    assert len(lines) == limit + 1
    assert check_gh_repo.joined_lines(lines) == [True] * limit + [False]


@pytest.mark.parametrize("hash_line", [
    check_gh_repo.CONTINUATION_LIMIT - 2, check_gh_repo.CONTINUATION_LIMIT - 1, check_gh_repo.CONTINUATION_LIMIT, check_gh_repo.CONTINUATION_LIMIT + 1, check_gh_repo.CONTINUATION_LIMIT + 2,
])
def test_untold_refuses_a_gh_after_a_comment_with_a_gh_repo_at_any_place_in_a_long_chain(tmp_path, hash_line):
    # The comment ends its line in bash, so its GH_REPO= tells nothing: the gh on the line after it is refused, wherever in the chain the comment is.
    lines = ["echo a \\"] + ["x \\"] * (hash_line - 1) + ["# GH_REPO=o/r \\", "gh pr view 1"]
    path = tmp_path / "script.sh"
    path.write_text("\n".join(lines) + "\n")
    assert lines_of(check_gh_repo.untold(path)) == [len(lines)]


@pytest.mark.parametrize("lines", [
    # A string opened on the first line closes past the limit on a line that ends in a comment: the line is unjudged (None), the read-back stops at it, and its GH_REPO= tells no later gh.
    ["echo \"a \\"] + ["x \\"] * check_gh_repo.CONTINUATION_LIMIT + ["y\" # GH_REPO=o/r \\", "gh pr view 1"],
    # A # inside a string opened on the first line, past the limit, is no comment: the chain goes on to a gh that runs under a sudo that drops the export.
    ["export GH_REPO=o/r", "sudo env \"a \\"] + ["x \\"] * check_gh_repo.CONTINUATION_LIMIT + [" # b \\", "\" gh pr view 1"],
])
def test_untold_refuses_a_gh_after_a_past_limit_line_whose_hash_it_cannot_judge(tmp_path, lines):
    path = tmp_path / "script.sh"
    path.write_text("\n".join(lines) + "\n")
    assert lines_of(check_gh_repo.untold(path)) == [len(lines)]


def test_untold_does_not_join_the_line_after_a_heredoc_delimiter_that_ends_in_a_backslash(tmp_path):
    # A quoted delimiter can be `GH_REPO=o/r \\`: the line that closes the body joins nothing, so its text is no assignment for the gh after it.
    lines = ["cat <<'GH_REPO=o/r \\'", "x", "GH_REPO=o/r \\", "gh pr view 1"]
    path = tmp_path / "script.sh"
    path.write_text("\n".join(lines) + "\n")
    assert lines_of(check_gh_repo.untold(path)) == [4]


def test_untold_reads_a_very_long_chain_in_linear_time(tmp_path):
    # Building the logical line must not copy the text so far on every line: 400,000 lines of one chain take seconds, not minutes.
    start = time.perf_counter()
    found = untold(tmp_path, "x \\\n" * 400_000 + "gh pr view 1\n")
    assert lines_of(found) == [400_001]
    assert time.perf_counter() - start < 15


def test_untold_does_not_read_the_line_after_an_unjudged_line_as_a_command_start(tmp_path):
    # A line past the limit that holds a # (None) may sit inside a string, so the next line may be string text: a `fi` in it must not pop the `if false` branch, or the export after it counts.
    lines = ["if false; then", "echo \"a \\"] + ["x \\"] * check_gh_repo.CONTINUATION_LIMIT + ["y '#' \\", "fi ok\"", "export GH_REPO=o/r", "fi", "gh pr view 1"]
    path = tmp_path / "script.sh"
    path.write_text("\n".join(lines) + "\n")
    assert lines_of(check_gh_repo.untold(path)) == [len(lines)]


def test_untold_scans_a_chain_for_a_comment_a_bounded_number_of_times_per_line(tmp_path, monkeypatch):
    # Each line of a chain that holds a # is judged on the chain so far once per pass over the file: counting the scans (not timing them) fails when a chain is walked from every line.
    calls = []
    real = check_gh_repo.continues
    monkeypatch.setattr(check_gh_repo, "continues", lambda text: calls.append(1) or real(text))
    text = ("echo '#' \\\n" * 99 + "x\n") * 200 + "gh pr view 1\n"
    found = untold(tmp_path, text)
    assert lines_of(found) == [20_001]
    assert len(calls) <= 2 * 20_000



@pytest.mark.parametrize("lines, number, expected", [
    # the last line of the file ends in a backslash: nothing follows it to join
    (["a \\"], 1, ("a \\", 1)),
    (["x", "a \\"], 2, ("a \\", 1)),
    # a chain that ends at the last line
    (["a \\", "b \\"], 1, ("a b \\", 2)),
    (["a \\", "b"], 1, ("a b", 2)),
    # a comment in the middle of a chain ends it; an even run of backslashes does not continue
    (["a \\", "# c \\", "d"], 1, ("a # c \\", 2)),
    (["a \\\\", "b"], 1, ("a \\\\", 1)),
    # a # inside a string opened on an earlier line is no comment
    (["echo \"a \\", " # b \\", "c\" ; d"], 1, ("echo \"a  # b c\" ; d", 3)),
])
def test_logical_line_joins_what_continues_and_stops_at_the_end_of_the_file(lines, number, expected):
    assert check_gh_repo.logical_line(lines, number) == expected


@pytest.mark.parametrize("hash_line, scanned", [
    (check_gh_repo.CONTINUATION_LIMIT - 2, True), (check_gh_repo.CONTINUATION_LIMIT - 1, True),
    (check_gh_repo.CONTINUATION_LIMIT, False), (check_gh_repo.CONTINUATION_LIMIT + 1, False),
])
def test_logical_line_scans_the_chain_for_a_comment_only_up_to_the_continuation_limit(hash_line, scanned):
    # The # line (index hash_line) is a real comment ending in a backslash: a scan of the chain cuts it there, and past the limit the chain joins on the backslash alone.
    lines = ["echo a \\"] + ["x \\"] * (hash_line - 1) + [" # b \\", "tail"]
    text, count = check_gh_repo.logical_line(lines, 1)
    assert count == (hash_line + 1 if scanned else hash_line + 2)
    assert text.endswith("tail") is not scanned


@pytest.mark.parametrize("extra", [-1, 0, 1, 100])
def test_untold_refuses_a_gh_after_a_long_chain_with_a_string_and_a_hash(tmp_path, extra):
    # A chain past the limit that opens a string on its first line must not be cut at a later #: the heredoc after it stays data and the gh is refused.
    chain = check_gh_repo.CONTINUATION_LIMIT + extra
    lines = ['echo "a \\'] + ["x \\"] * chain + [" # b \\", 'c" ; cat <<EOF', "export GH_REPO=o/r", "EOF", "gh pr view 1"]
    assert lines_of(untold(tmp_path, "\n".join(lines) + "\n")) == [len(lines)]


def test_untold_reads_a_long_chain_of_lines_with_a_quoted_hash_in_linear_time(tmp_path):
    start = time.perf_counter()
    found = untold(tmp_path, "echo '#' \\\n" * 20_000 + "gh pr view 1\n")
    assert lines_of(found) == [20_001]
    assert time.perf_counter() - start < 20


@pytest.mark.parametrize("line", ["echo ${x #y", "echo ${x # y \\", "echo \\'x # c", "echo a\\  # c", "echo x\t# c"])
def test_comment_start_and_continues_end_on_an_unfinished_construct(line):
    # A branch that stopped advancing would hang: fail after 5 s instead.
    def stop(*_):
        raise TimeoutError(f"comment_start() did not finish on {line!r}")
    previous = signal.signal(signal.SIGALRM, stop)
    signal.alarm(5)
    try:
        assert isinstance(check_gh_repo.comment_start(line), int)
        assert isinstance(check_gh_repo.continues(line), bool)
    finally:
        signal.alarm(0)
        signal.signal(signal.SIGALRM, previous)


@pytest.mark.parametrize("text, lines", [
    # An operand inside [[ ... ]] is never a keyword: it can't close or open a branch.
    ("if false; then\n[[ value == other || fi == value ]]\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n[[ value =~ (fi) ]]\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n[[ a == b && fi ]]\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n[[ a == b ; fi ]]\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    ("if false; then\n[[ x ]]; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if false; then\necho [[; fi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    # An escaped space before # is no word boundary, so the comment doesn't hide the wrapper.
    ("export GH_REPO=o/r\nsudo -p foo\\ # \\\ngh pr view 1\n", [3]),
    # A then that is an argument doesn't end the condition.
    ("if true then && false; then\n:\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # A condition of several lines is judged at its real then.
    ("if false\ntrue\nthen\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false\n# note\nthen\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
])
def test_untold_reads_conditions_tests_and_comments_as_bash_does(tmp_path, text, lines):
    assert lines_of(untold(tmp_path, text)) == lines


@pytest.mark.parametrize("line, start", [("sudo -p foo\\ # x", -1), ("echo a\\;# x", -1), ("echo a ;# x", 8)])
def test_comment_start_ignores_an_escaped_word_boundary(line, start):
    assert check_gh_repo.comment_start(line) == start


@pytest.mark.parametrize("text, lines", [
    # fi, else and elif arrive while the condition is pending
    ("if x\nfi\nexport GH_REPO=o/r\ngh pr view 1\n", []),
    ("if false\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # a comment on the first or a later line of a condition
    ("if\nfalse # note\nthen\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [6]),
    ("if true &&\ntrue &&\nfalse\nthen\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if false \\\n; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # the then line holds an else: the condition is judged before the else flips
    ("if true\nthen :; else\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # an if that doesn't start its line is no condition to judge
    ("x; if foo ||\nfalse\nthen\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("{ if false\nthen\nexport GH_REPO=o/r\n}\nfi\ngh pr view 1\n", []),
    ("echo x \\\nif false; if x; then\n:\nelse\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    # a tab after the if, and a comment on the if line, are read as a blank and a comment
    ("if\tfalse; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [4]),
    ("if false # note\nthen\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", [5]),
    # if( is an if followed by a subshell, never a condition
    ("if(false)\nthen\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if(false); then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
    ("if<f false; then\nexport GH_REPO=o/r\nfi\ngh pr view 1\n", []),
])
def test_untold_judges_only_an_if_that_starts_its_line_and_at_its_real_then(tmp_path, text, lines):
    assert lines_of(untold(tmp_path, text)) == lines


@pytest.mark.parametrize("text, condition, ended", [
    ("false; then", "false", True), ("false;then", "false", True), ("false # x", "false # x", False), ("false", "false", False),
    ("echo then; then", "echo then", True), ("true then && false; then", "true then && false", True),
    ("false thenx; then", "false thenx", True), ("[[ a == then ]]; then", "[[ a == then ]]", True),
    ("true; false; then", "true; false", True), ("", "", False), ("false;", "false", False),
])
def test_split_condition_cuts_at_the_real_then_and_the_semicolon_before_it(text, condition, ended):
    assert check_gh_repo.split_condition(text) == (condition, ended)


@pytest.mark.parametrize("line, kept", [("false # note", "false "), ("false \\", "false "), ("false \\\\", "false \\\\"), ("# note", "")])
def test_condition_text_drops_the_comment_and_a_joining_backslash(line, kept):
    assert check_gh_repo.condition_text(line) == kept


@pytest.mark.parametrize("keyword, expected", [
    ("[[ a == b && if ]]", []), ("[[ a == b ; if ]]", []), ("[[ a == b || if ]]", []),
    ("[[ a ]] && fi", ["fi"]), ("echo [[ ; fi", ["fi"]), ("[[ (fi) ]]", []),
])
def test_keywords_reads_a_test_expression_as_operands(keyword, expected):
    assert check_gh_repo.keywords(keyword)[0] == expected


def test_untold_reads_a_file_with_an_if_that_never_has_a_then_in_linear_time(tmp_path):
    # A regression guard for a pending condition kept as a string that each line was added to; the cap itself is pinned below.
    text = "if x\n" + "echo hi\n" * 50_000 + "gh pr view 1\n"
    started = time.monotonic()
    found = untold(tmp_path, text)
    assert time.monotonic() - started < 5
    assert lines_of(found) == [50_002]


@pytest.mark.parametrize("blank_lines, runs", [
    (check_gh_repo.PENDING_LIMIT - 2, False),
    (check_gh_repo.PENDING_LIMIT - 1, False),
    (check_gh_repo.PENDING_LIMIT, True),
])
def test_a_pending_condition_is_judged_up_to_the_limit_and_runs_past_it(blank_lines, runs):
    # `if false`, then blank lines of the condition, then its then: the limit is on the lines read before the then.
    branches = []
    for line in ["if false"] + [""] * blank_lines + ["then"]:
        check_gh_repo.track_branches(line, branches)
    assert branches[-1][0] is runs
