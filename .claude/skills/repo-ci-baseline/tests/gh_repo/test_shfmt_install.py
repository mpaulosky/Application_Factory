"""Tests for the shfmt installs check_gh_repo.py needs (repo-ci-baseline's ADR 0011): the Owned ci.yml's jobs that run the
checker, the Factory's template-tools action and the Seed Sandcastle Dockerfile. Each pins the same release and SHA-256, and
each install checks the download against it before installing it. No network: the install steps run with curl and sudo stubbed.
"""

import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
SKILL = HERE.parents[1]
CI = SKILL / "template" / "owned" / ".github" / "workflows" / "ci.yml"
DOCKERFILE = SKILL / "template" / "seed" / ".sandcastle" / "Dockerfile"
# The Factory's own action; it isn't part of the Template.
ACTION = SKILL.parents[2] / ".github" / "actions" / "template-tools" / "action.yml"
sys.path.insert(0, str(SKILL / "template" / "owned" / ".github" / "scripts"))
import check_gh_repo  # noqa: E402

# What a step runs to install shfmt.
INSTALLS = "sudo install -m 0755 shfmt /usr/local/bin/shfmt"
# What makes a job need shfmt: the checker itself, the gate that runs it, or the hook tests that run the gate.
NEEDS_SHFMT = re.compile(r"check_gh_repo\.py|scripts/gate\.sh|pre-push\.test\.sh")


def load_yaml(path):
    try:
        import yaml
    except ImportError:
        # test.sh may run pytest through uvx, which has no PyYAML: read the file with it in a one-off environment.
        script = "import json, sys, yaml; print(json.dumps(yaml.safe_load(open(sys.argv[1]))))"
        done = subprocess.run(["uvx", "--with", "pyyaml", "python", "-I", "-c", script, str(path)],
                              check=True, capture_output=True, text=True)
        return json.loads(done.stdout)
    return yaml.safe_load(path.read_text())


WORKFLOW = load_yaml(CI)
STEPS_OF_ACTION = load_yaml(ACTION)["runs"]["steps"]


def install_steps():
    """Every step that installs shfmt, by where it is: the Owned ci.yml's jobs and the template-tools action."""
    found = {f"ci.yml {name}": step for name, job in WORKFLOW["jobs"].items() for step in job.get("steps", [])
             if INSTALLS in str(step.get("run", ""))}
    found.update({"template-tools": step for step in STEPS_OF_ACTION if INSTALLS in str(step.get("run", ""))})
    return found


def dockerfile_pins():
    text = DOCKERFILE.read_text()
    version = re.search(r"^ARG SHFMT_VERSION=(\S+)$", text, re.M)
    arms = {arch: re.search(rf"^\s*{arch}\) \\\n(?:.*\\\n)*?.*shfmt_sha=([0-9a-f]+) ;; \\$", text, re.M)
            for arch in ("amd64", "arm64")}
    return text, version and version.group(1), {arch: arm and arm.group(1) for arch, arm in arms.items()}


def test_every_ci_job_that_runs_the_checker_installs_shfmt_before_it():
    # Arrange
    needing = {name: job for name, job in WORKFLOW["jobs"].items()
               if any(NEEDS_SHFMT.search(str(step.get("run", ""))) for step in job.get("steps", []))}

    # Act, Assert: the gh-repo job and the hook-tests job, and in each the install comes before the first step that needs it.
    assert {"gh-repo", "hook-tests"} <= set(needing)
    for name, job in needing.items():
        runs = [str(step.get("run", "")) for step in job["steps"]]
        first = next(index for index, run in enumerate(runs) if NEEDS_SHFMT.search(run))
        assert any(INSTALLS in run for run in runs[:first]), f"the {name} job runs {runs[first]!r} before installing shfmt"


def test_every_shfmt_install_pins_the_same_release_and_sha256():
    # Arrange
    steps = install_steps()
    text, version, shas = dockerfile_pins()

    # Act
    pins = {(step["env"]["SHFMT_VERSION"], step["env"]["SHFMT_SHA256"]) for step in steps.values()}

    # Assert: three steps (two ci.yml jobs and the action), one pin, the Dockerfile's amd64 arm the same and its arm64 arm a SHA-256.
    assert len(steps) == 3, sorted(steps)
    assert len(pins) == 1, pins
    (pinned_version, pinned_sha), = pins
    assert re.fullmatch(r"3\.[0-9]+\.[0-9]+", pinned_version)
    assert re.fullmatch(r"[0-9a-f]{64}", pinned_sha)
    assert version == pinned_version
    assert shas["amd64"] == pinned_sha
    assert shas["arm64"] and re.fullmatch(r"[0-9a-f]{64}", shas["arm64"]) and shas["arm64"] != pinned_sha


def test_the_dockerfile_checks_shfmt_against_its_sha256_before_installing_it():
    text, _, _ = dockerfile_pins()
    check = re.search(r'printf .*"\$shfmt_sha" shfmt \| sha256sum -c -;', text)
    install = text.find("install -m 0755 shfmt /usr/local/bin/shfmt")
    download = text.find('curl -fsSLo shfmt "https://github.com/mvdan/sh/releases/download/v${SHFMT_VERSION}/')
    assert check and download >= 0 and install >= 0
    assert download < check.start() < install


def run_install(tmp_path, step, sha):
    """Runs an install step with curl writing a fixed file, sudo logging its call, and the real sha256sum."""
    stubs = tmp_path / "stubs"
    stubs.mkdir()
    log = tmp_path / "log"
    (stubs / "curl").write_text(
        '#!/bin/sh\nwhile [ $# -gt 0 ]; do if [ "$1" = -o ]; then shift; printf fake-shfmt > "$1"; fi; shift; done\n')
    (stubs / "sudo").write_text(f'#!/bin/sh\necho "sudo $*" >> {log}\n')
    (stubs / "shfmt").write_text("#!/bin/sh\necho v3.14.1\n")
    for stub in stubs.iterdir():
        stub.chmod(0o755)
    runner = tmp_path / "runner"
    runner.mkdir()
    env = {"PATH": f"{stubs}:{os.environ['PATH']}", "RUNNER_TEMP": str(runner), **step["env"], "SHFMT_SHA256": sha}
    # A ci.yml step with no shell runs under bash -e, an action's shell: bash step under bash -eo pipefail.
    options = ["-eo", "pipefail"] if step.get("shell") == "bash" else ["-e"]
    done = subprocess.run(["bash", "--noprofile", "--norc", *options, "-c", step["run"]], env=env, capture_output=True, text=True)
    return done.returncode, log.read_text() if log.exists() else ""


@pytest.mark.parametrize("where", sorted(install_steps()))
def test_an_install_step_installs_a_download_that_matches_its_sha256(tmp_path, where):
    code, log = run_install(tmp_path, install_steps()[where], hashlib.sha256(b"fake-shfmt").hexdigest())
    assert code == 0
    assert log == "sudo install -m 0755 shfmt /usr/local/bin/shfmt\n"


@pytest.mark.parametrize("where", sorted(install_steps()))
def test_an_install_step_fails_and_installs_nothing_when_the_download_doesnt_match(tmp_path, where):
    code, log = run_install(tmp_path, install_steps()[where], hashlib.sha256(b"another file").hexdigest())
    assert code != 0
    assert log == ""


@pytest.mark.parametrize("where", sorted(install_steps()))
def test_an_install_step_retries_a_failed_download(where):
    assert re.search(r"curl [^\n]*--retry [1-9][^\n]*--retry-all-errors[^\n]* -o shfmt ", install_steps()[where]["run"])


def test_sh_version_check():
    """test.sh's shfmt version check, the two lines that start with [[ "$(shfmt --version)"."""
    lines = (SKILL / "test.sh").read_text().splitlines()
    first = next(index for index, line in enumerate(lines) if line.startswith('[[ "$(shfmt --version)" =~'))
    return "\n".join(lines[first:first + 2]) + "\n"


@pytest.mark.parametrize("version", [
    "v3.13.9", "v3.14.0", "v3.14.1", "v3.20.0", "v3.99.0", "v3.100.0", "v2.6.4", "v4.0.0", "v3.14", "(devel)", "v3.014.0",
])
def test_test_sh_and_the_checker_accept_the_same_shfmt_releases(tmp_path, version):
    # Arrange: a shfmt on PATH that prints the version, and the checker's own rule for it.
    stubs = tmp_path / "stubs"
    stubs.mkdir()
    (stubs / "shfmt").write_text(f"#!/bin/sh\necho '{version}'\n")
    (stubs / "shfmt").chmod(0o755)
    found = check_gh_repo.SHFMT_RELEASE.fullmatch(version)
    accepted = bool(found) and int(found.group(1)) >= check_gh_repo.SHFMT_OLDEST_MINOR

    # Act
    done = subprocess.run(["bash", "--noprofile", "--norc", "-c", test_sh_version_check()], capture_output=True, text=True,
                          env={"PATH": f"{stubs}:{os.environ['PATH']}"})

    # Assert: the same answer, and test.sh's refusal is exit 2 and names the release it found.
    assert (done.returncode == 0) == accepted, (version, done.returncode)
    if not accepted:
        assert done.returncode == 2
        assert f"shfmt v3.14 or a later v3 release is required, not '{version}'" in done.stderr
