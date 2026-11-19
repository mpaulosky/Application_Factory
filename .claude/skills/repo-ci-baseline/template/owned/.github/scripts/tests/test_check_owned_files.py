"""Tests for check_owned_files.py, the gh-repo job's Owned files check: throwaway git repos, a base commit with a
check_gh_repo.py that lists OWNED and a head commit on top, and the script run as the job runs it (python3 -I, from a
copy outside the checkout). No GitHub.
"""

import shutil
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[1] / "check_owned_files.py"
CHECKER_PATH = ".github/scripts/check_gh_repo.py"
# A stand-in for the base's checker: only its OWNED list is read.
CHECKER = 'OWNED = ("scripts/gate.sh", "scripts/review.sh", ".claude/skills/pre-push-review/")\n'
GATE = "#!/usr/bin/env bash\necho gate\n"
SKILL = ".claude/skills/pre-push-review/SKILL.md"
BASE = {CHECKER_PATH: CHECKER, "scripts/gate.sh": GATE, SKILL: "# Review\n", "README.md": "# hi\n"}


class Repo:
    def __init__(self, root, base_files):
        self.root = root
        self.git("init", "-q", "-b", "main")
        self.write(base_files)
        self.base = self.commit("base")

    def git(self, *args):
        return subprocess.run(["git", "-c", "user.name=T", "-c", "user.email=t@example.test", *args], cwd=self.root,
                              check=True, capture_output=True, text=True).stdout.strip()

    def write(self, files):
        for name, content in files.items():
            path = self.root / name
            if content is None:
                path.unlink()
                continue
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content)

    def commit(self, message):
        self.git("add", "-A")
        self.git("commit", "-q", "--allow-empty", "-m", message)
        return self.git("rev-parse", "HEAD")

    def check(self, tmp_path, base=None, base_ref="main"):
        """Runs a copy of the script outside the repo, as the job does; returns (exit code, stdout, stderr)."""
        copy = tmp_path / "runner" / "check_owned_files.py"
        copy.parent.mkdir(exist_ok=True)
        shutil.copyfile(SCRIPT, copy)
        done = subprocess.run([sys.executable, "-I", str(copy), "--base", base or self.base, "--base-ref", base_ref,
                               "--repo", str(self.root)], capture_output=True)
        return done.returncode, done.stdout.decode("utf-8", "surrogateescape"), done.stderr.decode()


@pytest.fixture
def repo(tmp_path):
    work = tmp_path / "work"
    work.mkdir()
    return Repo(work, BASE)


def differs(path, base_ref="main"):
    return f"Owned file {path} differs from {base_ref}\n"


def test_a_clean_tree_passes_and_prints_nothing(repo, tmp_path):
    # Arrange: a change outside OWNED only.
    repo.write({"README.md": "# changed\n", "scripts/mine.sh": "echo mine\n"})
    repo.commit("head")

    # Act
    code, out, err = repo.check(tmp_path)

    # Assert
    assert (code, out, err) == (0, "", "")


def test_a_changed_blob_is_reported(repo, tmp_path):
    # Arrange
    repo.write({"scripts/gate.sh": GATE + "exit 0\n"})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (1, differs("scripts/gate.sh"))


def test_a_changed_mode_is_reported(repo, tmp_path):
    # Arrange: the same content, executable.
    (repo.root / "scripts" / "gate.sh").chmod(0o755)
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (1, differs("scripts/gate.sh"))


def test_a_deleted_owned_file_is_reported(repo, tmp_path):
    # Arrange
    repo.write({SKILL: None})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (1, differs(SKILL))


def test_a_renamed_owned_file_is_reported_by_its_old_name(repo, tmp_path):
    # Arrange
    repo.git("mv", "scripts/gate.sh", "scripts/gate2.sh")
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (1, differs("scripts/gate.sh"))


def test_a_file_added_to_an_owned_folder_is_reported(repo, tmp_path):
    # Arrange
    repo.write({".claude/skills/pre-push-review/helper.sh": "gh pr view 1\n"})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (1, differs(".claude/skills/pre-push-review/helper.sh"))


def test_an_added_single_file_owned_entry_is_reported(repo, tmp_path):
    # Arrange: scripts/review.sh is in OWNED and the base lacks it.
    repo.write({"scripts/review.sh": "#!/usr/bin/env bash\ntrue\n"})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (1, differs("scripts/review.sh"))


def test_each_differing_file_is_reported_once_in_order(repo, tmp_path):
    # Arrange
    repo.write({"scripts/gate.sh": None, SKILL: "# Changed\n", ".claude/skills/pre-push-review/new.md": "# New\n"})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert code == 1
    assert out == differs("scripts/gate.sh") + differs(SKILL) + differs(".claude/skills/pre-push-review/new.md")


@pytest.mark.parametrize("name, logged", [
    (".claude/skills/pre-push-review/x\n::error::spoofed.md", ".claude/skills/pre-push-review/x\\n::error::spoofed.md"),
    (".claude/skills/pre-push-review/x\r::error::spoofed.md", ".claude/skills/pre-push-review/x\\r::error::spoofed.md"),
])
def test_a_line_break_in_a_reported_path_is_escaped(repo, tmp_path, name, logged):
    # Arrange: a name that would start a workflow command on its own log line.
    repo.write({name: "# x\n"})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (1, differs(logged))
    assert "\r" not in out
    assert not any(line.startswith("::") for line in out.splitlines())


def test_a_base_with_no_checker_passes_and_says_so(tmp_path):
    # Arrange: the first Apply, onto a base without the Baseline.
    work = tmp_path / "work"
    work.mkdir()
    repo = Repo(work, {"README.md": "# hi\n"})
    repo.write(BASE)
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (0, "main has no .github/scripts/check_gh_repo.py: no Owned files to compare (the first Apply).\n")


def test_the_base_checkers_owned_list_is_read_not_the_heads(repo, tmp_path):
    # Arrange: the PR drops scripts/gate.sh from its own OWNED, and edits it.
    repo.write({CHECKER_PATH: 'OWNED = ()\n', "scripts/gate.sh": "edited\n"})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (1, differs("scripts/gate.sh"))


def test_a_later_commit_on_the_base_branch_is_not_the_base_compared_with(repo, tmp_path):
    # Arrange: the head branch leaves gate.sh alone; main edits it after the head was cut.
    repo.git("checkout", "-q", "-b", "head")
    repo.write({"README.md": "# head\n"})
    repo.commit("head")
    repo.git("checkout", "-q", "main")
    repo.write({"scripts/gate.sh": "edited on main\n"})
    repo.commit("main moves on")
    repo.git("checkout", "-q", "head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (0, "")


def test_the_base_branch_is_named_in_the_message(repo, tmp_path):
    # Arrange
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path, base_ref="develop")

    # Assert
    assert (code, out) == (1, differs("scripts/gate.sh", "develop"))


def test_a_base_commit_git_cant_read_is_a_usage_error(repo, tmp_path):
    # Arrange, Act
    code, out, err = repo.check(tmp_path, base="0" * 40)

    # Assert
    assert code == 2
    assert out == ""
    assert "can't read the base commit" in err


def test_the_checkout_is_left_as_it_was(repo, tmp_path):
    # Arrange
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")

    # Act
    repo.check(tmp_path)

    # Assert: the base's checker was loaded from a temp folder, not beside the PR's files.
    assert repo.git("status", "--porcelain", "--ignored", "--untracked-files=all") == ""
