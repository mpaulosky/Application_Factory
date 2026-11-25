"""Tests for check_owned_files.py, owned-files.yml's Owned files check: throwaway git repos, a base commit with a
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

    def check(self, tmp_path, base=None, base_ref="main", env=None):
        """Runs a copy of the script outside the repo, as the job does; returns (exit code, stdout, stderr)."""
        copy = tmp_path / "runner" / "check_owned_files.py"
        copy.parent.mkdir(exist_ok=True)
        shutil.copyfile(SCRIPT, copy)
        done = subprocess.run([sys.executable, "-I", str(copy), "--base", base or self.base, "--base-ref", base_ref,
                               "--repo", str(self.root)], capture_output=True, env=env)
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


def base_with_checker(tmp_path, checker):
    work = tmp_path / "work"
    work.mkdir()
    return Repo(work, {**BASE, CHECKER_PATH: checker})


@pytest.mark.parametrize("checker", [
    pytest.param("raise RuntimeError('boom')\n", id="raises"),
    pytest.param("import sys\nsys.exit(0)\n", id="exits"),
    pytest.param("OWNED = (\n", id="syntax-error"),
])
def test_a_base_checker_that_fails_to_load_is_a_usage_error(tmp_path, checker):
    # Arrange
    repo = base_with_checker(tmp_path, checker)
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")

    # Act
    code, out, err = repo.check(tmp_path)

    # Assert: one line naming the script, never a traceback or a pass.
    assert code == 2
    assert out == ""
    assert err.startswith("check_owned_files.py: the base's .github/scripts/check_gh_repo.py fails to load")
    assert "Traceback" not in err
    assert len(err.splitlines()) == 1


@pytest.mark.parametrize("checker", [
    pytest.param("NOT_OWNED = ('scripts/gate.sh',)\n", id="missing"),
    pytest.param("OWNED = None\n", id="none"),
    pytest.param("OWNED = 'scripts/gate.sh'\n", id="a-string"),
    pytest.param("OWNED = ()\n", id="empty"),
    pytest.param("OWNED = ('scripts/gate.sh', 3)\n", id="not-a-path"),
    pytest.param("OWNED = ('scripts/gate.sh', '')\n", id="empty-entry"),
])
def test_a_base_checker_without_an_owned_list_is_a_usage_error(tmp_path, checker):
    # Arrange
    repo = base_with_checker(tmp_path, checker)
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")

    # Act
    code, out, err = repo.check(tmp_path)

    # Assert
    assert code == 2
    assert out == ""
    assert err == "check_owned_files.py: the base's .github/scripts/check_gh_repo.py has no OWNED list of paths\n"


def test_git_that_cant_run_is_a_usage_error(repo, tmp_path):
    # Arrange: no git on PATH.
    empty = tmp_path / "no-git"
    empty.mkdir()

    # Act
    code, out, err = repo.check(tmp_path, env={"PATH": str(empty)})

    # Assert
    assert code == 2
    assert out == ""
    assert err.startswith("check_owned_files.py: can't run git: ")
    assert "Traceback" not in err


def test_a_file_the_check_cant_write_is_a_usage_error_not_blamed_on_git(repo, tmp_path):
    # Arrange: the temporary folder for the base checker's copy can't be made, as on a full disk.
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")
    copy = tmp_path / "runner" / "check_owned_files.py"
    copy.parent.mkdir(exist_ok=True)
    shutil.copyfile(SCRIPT, copy)
    program = (
        "import errno, runpy, sys, tempfile\n"
        "def full(*args, **kwargs):\n"
        "    raise OSError(errno.ENOSPC, 'No space left on device')\n"
        "tempfile.TemporaryDirectory = full\n"
        f"sys.argv = ['check_owned_files.py', '--base', {repo.base!r}, '--base-ref', 'main', '--repo', {str(repo.root)!r}]\n"
        f"runpy.run_path({str(copy)!r}, run_name='__main__')\n"
    )

    # Act
    done = subprocess.run([sys.executable, "-I", "-c", program], capture_output=True, text=True)

    # Assert
    assert done.returncode == 2
    assert done.stdout == ""
    assert done.stderr == ("check_owned_files.py: can't write or read the check's own files: "
                           "OSError: [Errno 28] No space left on device\n")


@pytest.mark.parametrize("owned", [
    '("scripts/gate.sh", "scripts/gate.sh")',
    '("scripts/", "scripts/gate.sh")',
], ids=["repeated-entry", "file-in-an-owned-folder"])
def test_a_path_two_owned_entries_cover_is_reported_once(tmp_path, owned):
    # Arrange: the base's OWNED names scripts/gate.sh twice, once by name and once by its folder or again by name.
    work = tmp_path / "work"
    work.mkdir()
    repo = Repo(work, {**BASE, CHECKER_PATH: f"OWNED = {owned}\n"})
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path)

    # Assert
    assert (code, out) == (1, differs("scripts/gate.sh"))


def test_a_git_call_that_fails_is_a_usage_error(repo, tmp_path):
    # Arrange: the base commit is readable, but the blob of its checker is gone, so git show fails.
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")
    blob = repo.git("rev-parse", f"{repo.base}:{CHECKER_PATH}")
    objects = repo.root / ".git" / "objects"
    (objects / blob[:2] / blob[2:]).unlink()

    # Act
    code, out, err = repo.check(tmp_path)

    # Assert
    assert code == 2
    assert out == ""
    assert err.startswith("check_owned_files.py: ")
    assert "Traceback" not in err
    assert len(err.splitlines()) == 1


def test_a_base_tree_git_cant_read_is_a_usage_error(repo, tmp_path):
    # Arrange: the base commit is readable, so cat-file -e passes, but its root tree is gone, so ls-tree fails.
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")
    tree = repo.git("rev-parse", f"{repo.base}^{{tree}}")
    loose = repo.root / ".git" / "objects" / tree[:2] / tree[2:]
    assert loose.is_file()
    loose.unlink()

    # Act
    code, out, err = repo.check(tmp_path)

    # Assert: one line naming the failed git call, never the first Apply's pass.
    assert code == 2
    assert out == ""
    assert err.startswith("check_owned_files.py: git ls-tree failed")
    assert len(err.splitlines()) == 1
    assert "first Apply" not in out + err


def test_a_line_break_in_the_base_ref_is_escaped(repo, tmp_path):
    # Arrange: a base branch name that would start a workflow command on its own line.
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path, base_ref="main\n::error::x")

    # Assert
    assert code == 1
    assert not any(line.startswith("::") for line in out.splitlines())
    assert out == "Owned file scripts/gate.sh differs from main\\n::error::x\n"


def test_a_line_break_in_the_base_ref_is_escaped_on_the_first_apply(tmp_path):
    # Arrange
    work = tmp_path / "work"
    work.mkdir()
    repo = Repo(work, {"README.md": "# hi\n"})
    repo.write(BASE)
    repo.commit("head")

    # Act
    code, out, _ = repo.check(tmp_path, base_ref="main\r\n::error::x")

    # Assert
    assert code == 0
    assert not any(line.startswith("::") for line in out.splitlines())
    assert out.startswith("main\\r\\n::error::x has no ")


def test_the_checkout_is_left_as_it_was(repo, tmp_path):
    # Arrange
    repo.write({"scripts/gate.sh": "edited\n"})
    repo.commit("head")

    # Act
    repo.check(tmp_path)

    # Assert: the base's checker was loaded from a temp folder, not beside the PR's files.
    assert repo.git("status", "--porcelain", "--ignored", "--untracked-files=all") == ""


def test_the_templates_owned_list_reports_a_bumped_global_json(tmp_path):
    # Arrange: the Template's own checker as the base's, and the root global.json bumped the way a Dependabot dotnet-sdk PR
    # would bump it. The SDK pin is Owned (mpaulosky/Application_Factory#103), so the bump comes through the Factory and a Rollout.
    real_checker = (SCRIPT.parent / "check_gh_repo.py").read_text()
    work = tmp_path / "work"
    work.mkdir()
    pinned = '{\n  "sdk": {\n    "version": "11.0.100",\n    "rollForward": "latestPatch"\n  }\n}\n'
    repo = Repo(work, {CHECKER_PATH: real_checker, "global.json": pinned})
    repo.write({"global.json": pinned.replace("11.0.100", "11.0.101")})
    repo.commit("bump the SDK")

    # Act
    code, out, err = repo.check(tmp_path)

    # Assert
    assert (code, out, err) == (1, differs("global.json"), "")


@pytest.mark.parametrize("shield", [".github/scripts/Directory.Build.props", ".github/scripts/Directory.Build.targets",
                                    ".github/scripts/Directory.Packages.props"])
@pytest.mark.parametrize("change", ["edited", "deleted"])
def test_the_templates_owned_list_reports_a_changed_shield(tmp_path, shield, change):
    # Arrange: the Template's own checker as the base's, and one of the empty shields beside the C# scripts edited to chain to the repo's own
    # Directory.* files, or deleted. They are Owned (mpaulosky/Application_Factory#125), so only a Rollout changes them.
    real_checker = (SCRIPT.parent / "check_gh_repo.py").read_text()
    work = tmp_path / "work"
    work.mkdir()
    repo = Repo(work, {CHECKER_PATH: real_checker, shield: "<Project />\n"})
    repo.write({shield: None if change == "deleted" else '<Project><Import Project="../../Directory.Build.props" /></Project>\n'})
    repo.commit(f"{change} a shield")

    # Act
    code, out, err = repo.check(tmp_path)

    # Assert
    assert (code, out, err) == (1, differs(shield), "")
