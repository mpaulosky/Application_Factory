"""Tests for the jobs that judge a PR's tree by its base branch's scripts (mpaulosky/dotfiles#344, R46/R61, R58 and R20):
ci.yml's gh-repo job, which runs the base's check_gh_repo.py, and owned-files.yml's job, which runs the base's
check_owned_files.py. No GitHub: the jobs' `run:` steps are read out of the YAML and run in bash against scratch git
repos, with a bare repo as `origin`, as the workflows run them. They need PyYAML (or uvx) and shfmt v3.14 or a later v3
on PATH; the gh-repo job's shfmt install itself is tested in the Application Factory.

Each job is found by the step that runs its script. A step's `if:` may use only github.head_ref, github.base_ref,
github.event_name, github.repository, github.event.pull_request.head.repo.full_name,
contains(github.event.pull_request.labels.*.name, '<label>'), string literals, ==, !=, &&, ||, ! and parentheses;
anything else can't be evaluated here, so keep the condition that simple, or test it in the script.
"""

import json
import os
import re
import subprocess
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
CI = HERE.parents[1] / "workflows" / "ci.yml"
OWNED_WORKFLOW = HERE.parents[1] / "workflows" / "owned-files.yml"
CHECKER = HERE.parent / "check_gh_repo.py"
CHECKER_PATH = ".github/scripts/check_gh_repo.py"
OWNED_CHECK = HERE.parent / "check_owned_files.py"
OWNED_CHECK_PATH = ".github/scripts/check_owned_files.py"

UNTOLD = "gh pr view 1\n"
TOLD = "export GH_REPO=o/r\ngh pr view 1\n"
GATE = "#!/usr/bin/env bash\necho gate\n"
EDITED_GATE = "#!/usr/bin/env bash\necho gate\nexit 0\n"
APPLY_BRANCHES = ["chore/standardize-baseline", "chore/reapply-baseline"]
APPLY_LABEL = "baseline:apply"
REPOSITORY = "acme/widget"
INSTALL_SHFMT = "Install shfmt"
INSTALLS_SHFMT = "sudo install -m 0755 shfmt /usr/local/bin/shfmt"


def load_workflow(path):
    try:
        import yaml
    except ImportError:
        # pytest may run through uvx (the Application Factory's test.sh does), which has no PyYAML: read the file with it in a one-off environment.
        script = "import json, sys, yaml; print(json.dumps(yaml.safe_load(open(sys.argv[1]))))"
        done = subprocess.run(["uvx", "--with", "pyyaml", "python", "-I", "-c", script, str(path)],
                              check=True, capture_output=True, text=True)
        return json.loads(done.stdout)
    return yaml.safe_load(path.read_text())


WORKFLOW = load_workflow(CI)
OWNED_FILES = load_workflow(OWNED_WORKFLOW)


def checker_jobs():
    return {name: job for name, job in WORKFLOW["jobs"].items()
            if any("check_gh_repo.py" in str(step.get("run", "")) for step in job.get("steps", []))}


def the_job():
    jobs = checker_jobs()
    assert len(jobs) == 1, f"ci.yml must have exactly one job that runs check_gh_repo.py, not {sorted(jobs)}"
    return next(iter(jobs.values()))


def owned_files_job():
    jobs = [job for job in OWNED_FILES["jobs"].values()
            if any("check_owned_files.py" in str(step.get("run", "")) for step in job.get("steps", []))]
    assert len(jobs) == 1, "owned-files.yml must have exactly one job that runs check_owned_files.py"
    return jobs[0]


def both_jobs():
    return [the_job(), owned_files_job()]


def owned_files_step():
    steps = [step for step in owned_files_job()["steps"] if "check_owned_files.py" in str(step.get("run", ""))]
    assert len(steps) == 1
    return steps[0]


def job_text(job):
    return json.dumps(job, sort_keys=True)


# The job and its trigger.

def test_ci_yml_has_a_job_that_runs_check_gh_repo_py():
    # Arrange, Act, Assert
    assert the_job()["steps"]


def test_the_job_runs_on_pull_requests_only():
    # Arrange
    job = the_job()

    # Act, Assert: ci.yml also runs on push to main and as a called workflow, where there is no base branch.
    assert re.search(r"github\.event_name\s*==\s*'pull_request'", str(job.get("if", ""))), \
        "the job needs an if: on github.event_name == 'pull_request'"


@pytest.mark.parametrize("workflow", [WORKFLOW, OWNED_FILES], ids=["ci.yml", "owned-files.yml"])
def test_the_pull_request_trigger_has_no_paths_filter(workflow):
    # Arrange
    trigger = workflow.get("on", workflow.get(True))["pull_request"] or {}

    # Act, Assert
    assert "paths-ignore" not in trigger
    assert "paths" not in trigger
    assert "branches" not in trigger


def test_the_owned_files_check_runs_on_pull_requests_only_and_again_when_a_label_changes():
    # Arrange
    triggers = OWNED_FILES.get("on", OWNED_FILES.get(True))

    # Act, Assert: adding or removing baseline:apply must start a new run, since a run reads the labels its event had.
    assert set(triggers) == {"pull_request"}
    assert set(triggers["pull_request"]["types"]) == {"opened", "synchronize", "reopened", "labeled", "unlabeled"}


def test_ci_yml_does_not_run_on_label_events():
    # Arrange: a label event would start the build and tests again and report skipped required checks on the head.
    types = (WORKFLOW.get("on", WORKFLOW.get(True))["pull_request"] or {}).get("types", [])

    # Act, Assert
    assert "labeled" not in types and "unlabeled" not in types


def test_the_job_takes_its_checker_from_the_base_branch_not_the_pr():
    # Arrange
    job = the_job()
    text = "\n".join(str(step.get("run", "")) for step in job["steps"])

    # Act, Assert
    assert re.search(r"git show[^\n]*BASE_SHA[^\n]*:\.github/scripts/check_gh_repo\.py", text), \
        "the checker must come from `git show $BASE_SHA:.github/scripts/check_gh_repo.py`"
    assert "github.event.pull_request.base.sha" in job_text(job), "BASE_SHA is the base the merge commit was built from"
    assert "origin/" not in text, "the base branch's moving tip must not be read"
    assert "mktemp -d" in text, "the base checker goes to a temp directory outside the checkout"


def test_the_job_runs_the_checker_isolated_against_the_workspace():
    # Arrange
    text = "\n".join(str(step.get("run", "")) for step in the_job()["steps"])

    # Act, Assert
    assert re.search(r'python3 -I "?\$\{?\w+\}?/check_gh_repo\.py"? --repo "?\$\{?GITHUB_WORKSPACE\}?"?', text)
    assert not re.search(r"python3?[^\n]*\s\.?/?\.github/scripts/check_gh_repo\.py", text), \
        "the PR's own copy of the checker must not be run"


@pytest.mark.parametrize("job", both_jobs(), ids=["gh-repo", "owned-files"])
def test_the_job_has_no_secrets_and_no_write_permission(job):
    # Act, Assert
    assert "secrets." not in job_text(job)
    assert job.get("permissions", {"contents": "read"}) in ({}, {"contents": "read"})


def test_the_owned_files_workflow_reads_only():
    # Act, Assert
    assert OWNED_FILES["permissions"] == {"contents": "read"}
    assert "secrets." not in json.dumps(OWNED_FILES)


@pytest.mark.parametrize("job", both_jobs(), ids=["gh-repo", "owned-files"])
def test_the_jobs_checkouts_keep_no_credentials(job):
    # Arrange
    checkouts = [step for step in job["steps"] if str(step.get("uses", "")).startswith("actions/checkout@")]

    # Act, Assert
    assert checkouts
    assert all(step.get("with", {}).get("persist-credentials") is False for step in checkouts)


# The job's steps, run.

class Job:
    """A PR to run the job on: a bare origin with the base branch, and a clone on the PR's head branch."""

    def __init__(self, tmp_path, base_files, head_files, head_ref="feature/344-x", base_ref="main", labels=(),
                 head_repo=REPOSITORY):
        self.tmp_path = tmp_path
        self.base_ref, self.head_ref = base_ref, head_ref
        self.labels, self.head_repo = list(labels), head_repo
        self.origin, self.work = tmp_path / "origin.git", tmp_path / "work"
        self.git(tmp_path, "init", "-q", "--bare", "-b", base_ref, str(self.origin))
        self.work.mkdir()
        self.git(self.work, "init", "-q", "-b", base_ref)
        self.write(base_files)
        self.commit("base")
        self.git(self.work, "remote", "add", "origin", str(self.origin))
        self.git(self.work, "push", "-q", "origin", base_ref)
        self.git(self.work, "checkout", "-q", "-b", head_ref)
        self.base_sha = self.rev("HEAD")
        self.write(head_files)
        self.commit("head")

    def rev(self, name):
        return subprocess.run(["git", "rev-parse", name], cwd=self.work, check=True, capture_output=True,
                              text=True).stdout.strip()

    def advance_base(self, files):
        """Lands a commit on the base branch after the head branch was cut: the PR's base.sha stays where it was."""
        self.git(self.work, "checkout", "-q", self.base_ref)
        self.write(files)
        self.commit("base moves on")
        self.git(self.work, "push", "-q", "origin", self.base_ref)
        self.git(self.work, "checkout", "-q", self.head_ref)

    @staticmethod
    def git(cwd, *args):
        subprocess.run(["git", "-c", "user.name=T", "-c", "user.email=t@example.test", "-c", "protocol.file.allow=always",
                        *args], cwd=cwd, check=True, capture_output=True)

    def write(self, files):
        for name, content in files.items():
            path = self.work / name
            if content is None:
                path.unlink()
                continue
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content)

    def commit(self, message):
        self.git(self.work, "add", "-A")
        self.git(self.work, "commit", "-q", "--allow-empty", "-m", message)

    def expression(self, text):
        values = {"github.base_ref": self.base_ref, "github.head_ref": self.head_ref,
                  "github.workspace": str(self.work), "github.event_name": "pull_request",
                  "github.event.pull_request.base.ref": self.base_ref,
                  "github.event.pull_request.base.sha": self.base_sha,
                  "github.event.pull_request.head.ref": self.head_ref, "github.repository": REPOSITORY,
                  "github.event.pull_request.head.repo.full_name": self.head_repo}

        def value(match):
            name = match.group(1)
            if name not in values:
                pytest.fail(f"the job uses ${{{{ {name} }}}}, which this test can't give: use one of {sorted(values)}")
            return values[name]

        return re.sub(r"\$\{\{\s*([\w.]+)\s*\}\}", value, text)

    def runs(self, step, failed):
        condition = step.get("if")
        always = condition is not None and re.search(r"always\(\)|failure\(\)", str(condition)) is not None
        if failed and not always:
            return False
        if condition is None:
            return True
        text = re.sub(r"^\$\{\{(.*)\}\}$", r"\1", str(condition).strip(), flags=re.S).strip()
        text = re.sub(r"always\(\)|failure\(\)|success\(\)", "true", text)
        text = re.sub(r"contains\(\s*github\.event\.pull_request\.labels\.\*\.name\s*,\s*'([^']*)'\s*\)",
                      lambda match: "true" if match.group(1) in self.labels else "false", text)
        text = re.sub(r"github\.(head_ref|base_ref|event_name|repository|event\.pull_request\.head\.repo\.full_name)\b",
                      lambda match: repr(self.expression("${{ github." + match.group(1) + " }}")), text)
        residue = re.sub(r"'[^']*'|==|!=|&&|\|\||[!() ]|\btrue\b|\bfalse\b", "", text)
        if residue:
            pytest.fail(f"can't evaluate the step's if: {condition!r}; see this file's docstring")
        python = text.replace("&&", " and ").replace("||", " or ").replace("true", "True").replace("false", "False")
        python = re.sub(r"!(?!=)", " not ", python)
        return bool(eval(python, {"__builtins__": {}}))  # noqa: S307 - the expression was checked above

    def run(self):
        """Runs both jobs' run: steps in order, each job on its own; returns (exit code of the first failing step or 0, all output)."""
        runner = self.tmp_path / "runner"
        runner.mkdir()
        output, code = [], 0
        skipped = []
        # Each job's own result, by its index in both_jobs(): the exit code of its first failing step, or 0, and its output.
        self.job_codes, self.job_outputs = {}, {}
        steps = [(job_index, step) for job_index, job in enumerate(both_jobs()) for step in job["steps"]]
        failed = set()
        for index, (job_index, step) in enumerate(steps):
            if "run" not in step or not self.runs(step, job_index in failed):
                continue
            # It downloads shfmt and installs it with sudo; the test's PATH has shfmt already (the checker needs it), and
            # the Application Factory's test_shfmt_install.py runs the step itself. Told by what it runs, so a rename can't make the harness run it.
            if INSTALLS_SHFMT in str(step["run"]):
                skipped.append(index)
                continue
            script = self.tmp_path / f"step{index}.sh"
            script.write_text(self.expression(str(step["run"])))
            env = {"PATH": os.environ["PATH"], "HOME": str(self.tmp_path / "home"), "GITHUB_WORKSPACE": str(self.work),
                   "RUNNER_TEMP": str(runner), "GITHUB_BASE_REF": self.base_ref, "GITHUB_HEAD_REF": self.head_ref,
                   "GITHUB_EVENT_NAME": "pull_request", "GITHUB_OUTPUT": str(self.tmp_path / "out"),
                   "GITHUB_STEP_SUMMARY": str(self.tmp_path / "summary"), "GITHUB_ENV": str(self.tmp_path / "env")}
            env.update({name: self.expression(str(value)) for name, value in step.get("env", {}).items()})
            options = "-eo pipefail" if step.get("shell") == "bash" else "-e"
            done = subprocess.run(["bash", "--noprofile", "--norc", *options.split(), str(script)], cwd=self.work,
                                  env=env, capture_output=True, text=True)
            output.append(done.stdout + done.stderr)
            self.job_outputs[job_index] = self.job_outputs.get(job_index, "") + done.stdout + done.stderr
            self.job_codes.setdefault(job_index, 0)
            if done.returncode != 0:
                failed.add(job_index)
                self.job_codes[job_index] = self.job_codes[job_index] or done.returncode
                code = code or done.returncode
        assert len(skipped) == 1, f"the harness skipped {len(skipped)} steps, not the one shfmt install"
        return code, "".join(output)


BASE = {CHECKER_PATH: CHECKER.read_text(), OWNED_CHECK_PATH: OWNED_CHECK.read_text(), "scripts/gate.sh": GATE, "scripts/ok.sh": TOLD}


def test_the_job_installs_a_pinned_and_checked_shfmt_before_the_checker_runs():
    # Arrange: the checker reads each script's syntax tree from shfmt.
    steps = the_job()["steps"]
    names = [step.get("name") for step in steps]
    checker = next(index for index, step in enumerate(steps) if "check_gh_repo.py\" --repo" in str(step.get("run", "")))

    # Act
    install = steps[names.index(INSTALL_SHFMT)]

    # Assert: before the checker, a version and the release's SHA-256 pinned, the download checked against it.
    assert names.index(INSTALL_SHFMT) < checker
    assert re.fullmatch(r"3\.[0-9]+\.[0-9]+", install["env"]["SHFMT_VERSION"])
    assert re.fullmatch(r"[0-9a-f]{64}", install["env"]["SHFMT_SHA256"])
    assert "sha256sum -c -" in install["run"]
    assert "github.com/mvdan/sh/releases/download/" in install["run"]


def run_job(tmp_path, head_files=None, base_files=None, **kwargs):
    return Job(tmp_path, BASE if base_files is None else base_files, head_files or {}, **kwargs).run()


def differs_lines(output):
    return [line for line in output.splitlines() if "differs from" in line]


# R46/R61: the base branch's checker judges the PR's tree.

def test_the_job_fails_a_pr_with_an_untold_gh_line(tmp_path):
    # Arrange, Act
    code, output = run_job(tmp_path, {"scripts/new.sh": "echo hi\n" + UNTOLD})

    # Assert
    assert code != 0
    assert "scripts/new.sh:2: gh pr view 1" in output


def test_the_job_fails_a_pr_that_edits_the_checker_to_accept_everything_on_the_same_line(tmp_path):
    # Arrange
    files = {CHECKER_PATH: "import sys\nsys.exit(0)\n", "scripts/new.sh": UNTOLD}

    # Act
    code, output = run_job(tmp_path, files)

    # Assert
    assert code != 0
    assert "scripts/new.sh:1: gh pr view 1" in output


def test_the_job_fails_a_pr_that_deletes_the_checker_and_adds_an_untold_gh_line(tmp_path):
    # Arrange
    files = {CHECKER_PATH: None, "scripts/new.sh": UNTOLD}

    # Act
    code, output = run_job(tmp_path, files)

    # Assert
    assert code != 0
    assert "scripts/new.sh:1: gh pr view 1" in output


def test_the_job_passes_a_pr_with_only_told_gh_lines(tmp_path):
    # Arrange, Act
    code, output = run_job(tmp_path, {"scripts/new.sh": TOLD, ".claude/skills/mine/SKILL.md": "`gh pr view 1 --repo o/r`\n"})

    # Assert
    assert code == 0, output


def test_the_job_judges_a_pr_that_changes_the_checker_by_the_old_one(tmp_path):
    # Arrange: the PR's checker would refuse everything, the base's refuses only what it should.
    files = {CHECKER_PATH: "import sys\nsys.exit(1)\n", "scripts/new.sh": TOLD}

    # Act
    code, output = run_job(tmp_path, files)

    # Assert
    assert code == 0, output


def test_the_job_leaves_the_checkout_as_it_found_it(tmp_path):
    # Arrange
    job = Job(tmp_path, BASE, {"scripts/new.sh": TOLD})

    # Act
    code, output = job.run()
    status = subprocess.run(["git", "status", "--porcelain", "--ignored"], cwd=job.work, check=True,
                            capture_output=True, text=True).stdout

    # Assert: the base checker was written outside the checkout, not beside the PR's files.
    assert code == 0, output
    assert status == ""


def test_the_job_names_the_base_branch_it_takes_the_checker_from(tmp_path):
    # Arrange: develop's checker refuses an untold line, and the head branch's own checker accepts it.
    files = {CHECKER_PATH: "import sys\nsys.exit(0)\n", "scripts/new.sh": UNTOLD}

    # Act
    code, output = run_job(tmp_path, files, base_ref="develop")

    # Assert
    assert code != 0
    assert "scripts/new.sh:1: gh pr view 1" in output


# R58: an Owned file is the base's, unless the PR is an Apply PR.

def test_the_job_fails_a_feature_pr_that_edits_an_owned_file(tmp_path):
    # Arrange, Act
    code, output = run_job(tmp_path, {"scripts/gate.sh": EDITED_GATE})

    # Assert
    assert code != 0
    assert "Owned file scripts/gate.sh differs from main" in output


def test_the_job_names_the_base_branch_in_the_owned_file_message(tmp_path):
    # Arrange, Act
    code, output = run_job(tmp_path, {"scripts/gate.sh": EDITED_GATE}, base_ref="develop")

    # Assert
    assert code != 0
    assert "Owned file scripts/gate.sh differs from develop" in output


@pytest.mark.parametrize("head_ref", ["feature/344-x", "fix/1-y", "hotfix/2-z", "chore/other", "chore/reapply-baseline-2",
                                      "feature/chore/reapply-baseline", "chore/standardize"])
def test_the_job_checks_owned_files_for_every_head_branch_but_the_two_apply_branches(tmp_path, head_ref):
    # Arrange, Act: even with the label.
    code, output = run_job(tmp_path, {"scripts/gate.sh": EDITED_GATE}, head_ref=head_ref, labels=[APPLY_LABEL])

    # Assert
    assert code != 0
    assert "Owned file scripts/gate.sh differs from main" in output


@pytest.mark.parametrize("head_ref", APPLY_BRANCHES)
def test_the_job_passes_the_same_owned_file_edit_from_a_labelled_apply_branch_of_this_repo(tmp_path, head_ref):
    # Arrange, Act
    code, output = run_job(tmp_path, {"scripts/gate.sh": EDITED_GATE}, head_ref=head_ref, labels=["review:claude", APPLY_LABEL])

    # Assert
    assert code == 0, output
    assert differs_lines(output) == []


@pytest.mark.parametrize("head_ref", APPLY_BRANCHES)
def test_the_job_still_runs_the_checker_on_an_apply_branch(tmp_path, head_ref):
    # Arrange, Act
    code, output = run_job(tmp_path, {"scripts/gate.sh": EDITED_GATE, "scripts/new.sh": UNTOLD}, head_ref=head_ref,
                           labels=[APPLY_LABEL])

    # Assert
    assert code != 0
    assert "scripts/new.sh:1: gh pr view 1" in output


def test_the_job_passes_a_pr_that_leaves_every_owned_file_alone(tmp_path):
    # Arrange, Act
    code, output = run_job(tmp_path, {"README.md": "# hi\n", "scripts/ok.sh": TOLD + "echo more\n"})

    # Assert
    assert code == 0, output
    assert differs_lines(output) == []


def test_the_job_fails_a_pr_that_adds_an_owned_file_the_base_lacks(tmp_path):
    # Arrange: only an Apply branch, or a base with no checker, may add an Owned file.
    base = {CHECKER_PATH: CHECKER.read_text(), OWNED_CHECK_PATH: OWNED_CHECK.read_text(), "scripts/ok.sh": TOLD}

    # Act
    code, output = run_job(tmp_path, {"scripts/gate.sh": GATE, "scripts/review.sh": GATE}, base_files=base)

    # Assert
    assert code != 0
    assert "Owned file scripts/gate.sh differs from main" in output
    assert "Owned file scripts/review.sh differs from main" in output


def test_the_job_names_each_edited_owned_file_once(tmp_path):
    # Arrange
    base = {**BASE, "scripts/review.sh": GATE}

    # Act
    code, output = run_job(tmp_path, {"scripts/gate.sh": EDITED_GATE, "scripts/review.sh": EDITED_GATE}, base_files=base)

    # Assert
    assert code != 0
    assert len(differs_lines(output)) == 2
    assert sum("scripts/gate.sh" in line for line in differs_lines(output)) == 1
    assert sum("scripts/review.sh" in line for line in differs_lines(output)) == 1


def test_the_job_compares_a_file_in_an_owned_folder(tmp_path):
    # Arrange
    skill = ".claude/skills/pre-push-review/SKILL.md"
    base = {**BASE, skill: "# Review\n"}

    # Act
    code, output = run_job(tmp_path, {skill: "# Review\nSkip it.\n"}, base_files=base)

    # Assert
    assert code != 0
    assert f"Owned file {skill} differs from main" in output


def test_the_job_compares_owned_files_byte_for_byte(tmp_path):
    # Arrange: only a trailing newline differs.
    code, output = run_job(tmp_path, {"scripts/gate.sh": GATE.rstrip("\n")})

    # Assert
    assert code != 0
    assert "Owned file scripts/gate.sh differs from main" in output


def test_the_job_fails_a_pr_that_deletes_an_owned_file(tmp_path):
    # Arrange, Act
    code, output = run_job(tmp_path, {"scripts/gate.sh": None})

    # Assert
    assert code != 0
    assert "Owned file scripts/gate.sh differs from main" in output


def test_the_job_fails_a_pr_that_deletes_a_file_in_an_owned_folder(tmp_path):
    # Arrange
    skill = ".claude/skills/pre-push-review/SKILL.md"
    base = {**BASE, skill: "# Review\n"}

    # Act
    code, output = run_job(tmp_path, {skill: None}, base_files=base)

    # Assert
    assert code != 0
    assert f"Owned file {skill} differs from main" in output


def test_the_job_fails_a_pr_that_renames_an_owned_file(tmp_path):
    # Arrange
    job = Job(tmp_path, BASE, {})
    job.git(job.work, "mv", "scripts/gate.sh", "scripts/gate2.sh")
    job.commit("rename")

    # Act
    code, output = job.run()

    # Assert
    assert code != 0
    assert "Owned file scripts/gate.sh differs from main" in output


def test_the_job_fails_a_pr_that_chmods_an_owned_file(tmp_path):
    # Arrange: the content is the base's, only the mode changes.
    job = Job(tmp_path, BASE, {})
    (job.work / "scripts" / "gate.sh").chmod(0o755)
    job.commit("chmod +x")

    # Act
    code, output = job.run()

    # Assert
    assert code != 0
    assert "Owned file scripts/gate.sh differs from main" in output


def test_the_job_fails_a_pr_that_swaps_an_owned_file_for_a_symlink_with_the_same_content(tmp_path):
    # Arrange
    job = Job(tmp_path, BASE, {})
    (job.work / "scripts" / "gate.sh").unlink()
    (job.work / "scripts" / "gate.sh").symlink_to("../scripts/gate.sh.real")
    (job.work / "scripts" / "gate.sh.real").write_text(GATE)
    job.commit("symlink")

    # Act
    code, output = job.run()

    # Assert
    assert code != 0
    assert "Owned file scripts/gate.sh differs from main" in output


def test_the_job_fails_a_pr_that_adds_a_file_to_an_owned_folder(tmp_path):
    # Arrange: the checker skips the folder and the base's tree has no such file to differ from.
    added = ".claude/skills/pre-push-review/helper.sh"
    base = {**BASE, ".claude/skills/pre-push-review/SKILL.md": "# Review\n"}

    # Act
    code, output = run_job(tmp_path, {added: UNTOLD}, base_files=base)

    # Assert
    assert code != 0
    assert f"Owned file {added} differs from main" in output


def test_the_job_fails_a_pr_that_adds_a_file_to_the_other_owned_folder(tmp_path):
    # Arrange
    added = ".claude/skills/hardening-review/references/new.md"

    # Act
    code, output = run_job(tmp_path, {added: "`gh pr view 1`\n"})

    # Assert
    assert code != 0
    assert f"Owned file {added} differs from main" in output


def test_the_job_passes_an_apply_branch_that_adds_a_file_to_an_owned_folder(tmp_path):
    # Arrange, Act
    code, output = run_job(tmp_path, {".claude/skills/pre-push-review/new.md": "# New\n"},
                           head_ref="chore/reapply-baseline", labels=[APPLY_LABEL])

    # Assert
    assert code == 0, output


def test_the_job_names_an_added_owned_folder_file_once_among_the_edited(tmp_path):
    # Arrange
    skill = ".claude/skills/pre-push-review/SKILL.md"
    added = ".claude/skills/pre-push-review/new.md"
    base = {**BASE, skill: "# Review\n"}

    # Act
    code, output = run_job(tmp_path, {skill: "# Changed\n", added: "# New\n"}, base_files=base)

    # Assert
    assert code != 0
    assert sorted(differs_lines(output)) == sorted([f"Owned file {skill} differs from main",
                                                    f"Owned file {added} differs from main"])


def test_the_job_escapes_line_breaks_in_an_owned_file_name_it_reports(tmp_path):
    # Arrange: a name that would start a workflow command on its own log line.
    name = ".claude/skills/pre-push-review/x\n::error::spoofed.sh"

    # Act
    code, output = run_job(tmp_path, {name: "# x\n"})

    # Assert
    assert code != 0
    assert "Owned file .claude/skills/pre-push-review/x\\n::error::spoofed.sh differs from main" in output
    assert not any(line.startswith("::") for line in output.splitlines())


def test_the_job_escapes_a_carriage_return_in_a_reported_line_and_file_name(tmp_path):
    # Arrange
    files = {"scripts/new.sh": "gh pr view 1\r::error::spoofed\n", "scripts/a\r::error::b.sh": UNTOLD}

    # Act
    code, output = run_job(tmp_path, files)

    # Assert
    assert code != 0
    assert "\r" not in output
    assert not any(line.startswith("::") for line in output.splitlines())
    assert "scripts/new.sh:1: gh pr view 1\\r::error::spoofed" in output
    assert "scripts/a\\r::error::b.sh:1: gh pr view 1" in output


# A re-run is judged against the base the merge commit was built from, not the base branch's moving tip.

def test_the_job_ignores_an_owned_file_the_base_changed_after_the_head_was_cut(tmp_path):
    # Arrange: an Apply PR lands on main after this PR's merge commit was made.
    job = Job(tmp_path, BASE, {"README.md": "# hi\n"})
    job.advance_base({"scripts/gate.sh": EDITED_GATE})

    # Act
    code, output = job.run()

    # Assert
    assert code == 0, output
    assert differs_lines(output) == []


def test_the_job_ignores_a_checker_the_base_changed_after_the_head_was_cut(tmp_path):
    # Arrange: the base's newer checker would refuse everything.
    job = Job(tmp_path, BASE, {"scripts/new.sh": TOLD})
    job.advance_base({CHECKER_PATH: "import sys\nsys.exit(1)\n"})

    # Act
    code, output = job.run()

    # Assert
    assert code == 0, output


def test_the_job_ignores_a_file_the_base_added_to_an_owned_folder_after_the_head_was_cut(tmp_path):
    # Arrange
    job = Job(tmp_path, BASE, {})
    job.advance_base({".claude/skills/pre-push-review/new.md": "# New\n"})

    # Act
    code, output = job.run()

    # Assert
    assert code == 0, output


def test_the_job_passes_an_apply_branch_that_adds_a_single_file_owned_entry(tmp_path):
    # Arrange, Act
    code, output = run_job(tmp_path, {"scripts/review.sh": "#!/usr/bin/env bash\ntrue\n"}, head_ref="chore/reapply-baseline",
                           labels=[APPLY_LABEL])

    # Assert
    assert code == 0, output


def test_the_job_ignores_a_single_file_owned_entry_the_base_added_after_the_head_was_cut(tmp_path):
    # Arrange
    job = Job(tmp_path, BASE, {})
    job.advance_base({"scripts/review.sh": "#!/usr/bin/env bash\ntrue\n"})

    # Act
    code, output = job.run()

    # Assert
    assert code == 0, output


def test_the_job_fails_a_pr_that_adds_an_absent_single_file_owned_entry(tmp_path):
    # Arrange: scripts/review.sh is an Owned entry the base lacks, so there is no base file to differ from.
    added = "scripts/review.sh"
    assert added not in BASE

    # Act
    code, output = run_job(tmp_path, {added: "#!/usr/bin/env bash\ntrue\n"})

    # Assert
    assert code != 0
    assert f"Owned file {added} differs from main" in output


# R19: the Owned files check is the base's check_owned_files.py, as the gh check is the base's check_gh_repo.py.

def test_the_job_takes_the_owned_files_check_from_the_base_branch_not_the_pr():
    # Arrange
    text = "\n".join(str(step.get("run", "")) for step in owned_files_job()["steps"])

    # Act, Assert
    assert re.search(r'git show "\$\{BASE_SHA\}:\.github/scripts/check_owned_files\.py" > "\$tmp/check_owned_files\.py"', text)
    assert re.search(r'python3 -I "\$tmp/check_owned_files\.py" --base "\$BASE_SHA" --base-ref "\$BASE_REF" --repo "\$GITHUB_WORKSPACE"', text)
    assert not re.search(r"python3?[^\n]*\s\.?/?\.github/scripts/check_owned_files\.py", text), \
        "the PR's own copy of the Owned files check must not be run"


def test_the_job_judges_a_pr_that_edits_the_owned_files_check_by_the_old_one(tmp_path):
    # Arrange: the PR's copy would pass anything.
    files = {OWNED_CHECK_PATH: "import sys\nsys.exit(0)\n", "scripts/gate.sh": EDITED_GATE}

    # Act
    code, output = run_job(tmp_path, files)

    # Assert
    assert code != 0
    assert "Owned file scripts/gate.sh differs from main" in output


def test_the_job_passes_and_says_so_when_the_base_has_no_checker(tmp_path):
    # Arrange: the first Apply, onto a base with none of the Baseline.
    base = {"README.md": "# hi\n"}

    # Act
    code, output = run_job(tmp_path, {**BASE, "scripts/gate.sh": EDITED_GATE}, base_files=base)

    # Assert
    assert code == 0, output
    assert "main has no .github/scripts/check_gh_repo.py: nothing to check against (the first Apply)." in output
    assert "main has no .github/scripts/check_gh_repo.py: no Owned files to compare (the first Apply)." in output


def test_the_job_fails_when_the_base_has_the_checker_but_not_the_owned_files_check(tmp_path):
    # Arrange: a base from before check_owned_files.py; the PR brings it, so its copy must not judge the PR.
    base = {key: value for key, value in BASE.items() if key != OWNED_CHECK_PATH}

    # Act
    code, output = run_job(tmp_path, {OWNED_CHECK_PATH: OWNED_CHECK.read_text()}, base_files=base)

    # Assert
    assert code != 0
    assert ("::error::main has .github/scripts/check_gh_repo.py but no .github/scripts/check_owned_files.py, so its Owned files "
            "can't be compared by the base's own rules") in output
    assert differs_lines(output) == []


# Only a readable base whose tree has no checker is the first Apply: a base git can't read fails both jobs.

JOB_NAMES = ["gh-repo", "owned-files"]


def test_a_base_commit_git_cant_read_fails_both_jobs(tmp_path):
    # Arrange: base.sha names a commit the checkout doesn't have.
    job = Job(tmp_path, BASE, {"scripts/gate.sh": EDITED_GATE})
    job.base_sha = "1" * 40

    # Act
    code, output = job.run()

    # Assert: each job fails on its own with an error annotation, and neither passes as the first Apply.
    assert code != 0
    for index, name in enumerate(JOB_NAMES):
        assert job.job_codes.get(index, 0) != 0, f"{name} passed an unreadable base: {job.job_outputs.get(index)}"
        assert f"::error::can't read the base commit {'1' * 40} of main" in job.job_outputs[index], name
    assert "first Apply" not in output


def test_a_base_whose_checker_git_cant_read_fails_both_jobs(tmp_path):
    # Arrange: the base's tree names check_gh_repo.py, but its blob is gone, so it isn't the first Apply.
    job = Job(tmp_path, BASE, {"scripts/gate.sh": EDITED_GATE, CHECKER_PATH: CHECKER.read_text() + "# head\n"})
    blob = subprocess.run(["git", "rev-parse", f"{job.base_sha}:{CHECKER_PATH}"], cwd=job.work, check=True,
                          capture_output=True, text=True).stdout.strip()
    (job.work / ".git" / "objects" / blob[:2] / blob[2:]).unlink()

    # Act
    code, output = job.run()

    # Assert
    assert code != 0
    for index, name in enumerate(JOB_NAMES):
        assert job.job_codes.get(index, 0) != 0, f"{name} passed an unreadable checker: {job.job_outputs.get(index)}"
    assert "::error::can't read main's .github/scripts/check_gh_repo.py at " in job.job_outputs[0]
    assert "check_owned_files.py: git show failed" in job.job_outputs[1]
    assert "first Apply" not in output


def delete_object(job, name):
    """Deletes the loose object `name` names in the work repo; the fixture's objects are loose (nothing packs them)."""
    sha = subprocess.run(["git", "rev-parse", name], cwd=job.work, check=True, capture_output=True, text=True).stdout.strip()
    path = job.work / ".git" / "objects" / sha[:2] / sha[2:]
    assert path.is_file(), f"{name} ({sha}) isn't a loose object, so it can't be deleted"
    path.unlink()


def test_a_base_whose_tree_git_cant_read_fails_both_jobs(tmp_path):
    # Arrange: the base commit is there, so cat-file -e passes, but its root tree is gone, so ls-tree fails.
    job = Job(tmp_path, BASE, {"scripts/gate.sh": EDITED_GATE})
    delete_object(job, f"{job.base_sha}^{{tree}}")
    subprocess.run(["git", "cat-file", "-e", f"{job.base_sha}^{{commit}}"], cwd=job.work, check=True)

    # Act
    code, output = job.run()

    # Assert
    assert code != 0
    for index, name in enumerate(JOB_NAMES):
        assert job.job_codes.get(index, 0) != 0, f"{name} passed an unreadable tree: {job.job_outputs.get(index)}"
        assert f"::error::can't read the tree of main's commit {job.base_sha}" in job.job_outputs[index]
    assert "first Apply" not in output


def test_a_base_whose_owned_files_checker_git_cant_read_fails_the_owned_files_job(tmp_path):
    # Arrange: the base's tree names check_owned_files.py, but its blob is gone; the head's copy differs, so it isn't the head's blob too.
    job = Job(tmp_path, BASE, {"scripts/gate.sh": EDITED_GATE, OWNED_CHECK_PATH: OWNED_CHECK.read_text() + "# head\n"})
    delete_object(job, f"{job.base_sha}:{OWNED_CHECK_PATH}")

    # Act
    code, output = job.run()

    # Assert
    assert code != 0
    assert job.job_codes.get(1, 0) != 0, f"{JOB_NAMES[1]} passed an unreadable checker: {job.job_outputs.get(1)}"
    assert f"::error::can't read main's .github/scripts/check_owned_files.py at {job.base_sha}." in job.job_outputs[1]
    # Not the message for a base that predates check_owned_files.py.
    assert "but no .github/scripts/check_owned_files.py" not in job.job_outputs[1]
    assert "first Apply" not in output


# R20: an Apply PR is exempt only with the maintainer-only label, from a branch of this repo.

@pytest.mark.parametrize("head_ref", APPLY_BRANCHES)
def test_an_apply_branch_without_the_label_is_checked(tmp_path, head_ref):
    # Arrange, Act: anyone who can push a branch can name it so; the name alone exempts nothing.
    code, output = run_job(tmp_path, {"scripts/gate.sh": EDITED_GATE}, head_ref=head_ref, labels=["review:claude"])

    # Assert
    assert code != 0
    assert differs_lines(output) == ["Owned file scripts/gate.sh differs from main"]


@pytest.mark.parametrize("head_ref", APPLY_BRANCHES)
def test_a_labelled_apply_branch_from_a_fork_is_checked(tmp_path, head_ref):
    # Arrange, Act: a fork's later pushes must not ride a label a maintainer set.
    code, output = run_job(tmp_path, {"scripts/gate.sh": EDITED_GATE}, head_ref=head_ref, labels=[APPLY_LABEL],
                           head_repo="someone/widget")

    # Assert
    assert code != 0
    assert differs_lines(output) == ["Owned file scripts/gate.sh differs from main"]


def test_a_label_of_a_similar_name_exempts_nothing(tmp_path):
    # Arrange, Act
    code, output = run_job(tmp_path, {"scripts/gate.sh": EDITED_GATE}, head_ref="chore/reapply-baseline",
                           labels=["baseline:apply-2", "baseline"])

    # Assert
    assert code != 0
    assert differs_lines(output) == ["Owned file scripts/gate.sh differs from main"]


def test_the_exemption_needs_the_branch_the_label_and_this_repo():
    # Arrange: the step's condition, read as written.
    condition = re.sub(r"\s+", " ", str(owned_files_step()["if"]))

    # Act, Assert
    assert f"contains(github.event.pull_request.labels.*.name, '{APPLY_LABEL}')" in condition
    assert "github.event.pull_request.head.repo.full_name == github.repository" in condition
    assert all(f"github.head_ref == '{branch}'" in condition for branch in APPLY_BRANCHES)
