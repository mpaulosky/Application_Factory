"""Tests for github/auto_rollout.py (rollout.sh --auto), with reapply, the review session, land.sh, gh and git faked.
No GitHub, no Claude."""

import io
import json
import os
import shutil
import re
import stat
import subprocess
import sys
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import auto_rollout as auto  # noqa: E402
import rollout  # noqa: E402

SKILL = Path(__file__).resolve().parents[2]
LISTED = {
    "mpaulosky/TicketManager": "full",
    "mpaulosky/IssueTracker": "full",
    "mpaulosky/Blazor-Server": "full",
    "mpaulosky/IssueManager": "full",
    "mpaulosky/Articles": "full",
    "mpaulosky/SuggestionApp": "full",
    "mpaulosky/Application_Factory": "factory",
}
TM = "mpaulosky/TicketManager"
REST = ("mpaulosky/IssueTracker", "mpaulosky/Blazor-Server", "mpaulosky/IssueManager", "mpaulosky/Articles",
        "mpaulosky/SuggestionApp")
BLAZOR, ISSUEMANAGER, ARTICLES = REST[1], REST[2], REST[3]


# ── What the other side says ────────────────────────────────────────────────

def reapply_constant(name):
    script = (SKILL / "reapply.sh").read_text()
    return next(line.split("=", 1)[1].strip('"') for line in script.splitlines() if line.startswith(f"{name}="))


def test_the_exit_codes_and_worktree_agree_with_reapply_sh():
    assert reapply_constant("review_needed_exit") == str(auto.REVIEW_NEEDED)
    assert reapply_constant("findings_wait_exit") == str(auto.FINDINGS_WAIT)
    assert reapply_constant("worktree_name") == auto.WORKTREE
    assert reapply_constant("branch") == auto.BRANCH == rollout.BRANCH


def test_reapply_sh_reads_the_refusals_in_the_ledgers_own_words():
    # reapply.sh tells the two refusals apart by the text checkReview prints at a line's start, on stderr; a reworded
    # message would turn both into a plain failed push.
    ledger = (SKILL / "template/owned/.sandcastle/lib/review-ledger.mts").read_text()
    check = ledger[ledger.index("export function checkReview("):]
    check = check[:check.index("\n}\n")]
    assert f"message: `{reapply_constant('no_review_text')}" in check
    assert f"`${{blocking.length}} {reapply_constant('findings_wait_text')}" in check
    assert "process.stderr.write(`${printable(text)}\\n`)" in ledger, "a refusal goes to stderr, which reapply.sh reads"
    gate = (SKILL / "template/owned/scripts/gate.sh").read_text()
    assert 'scripts/review.sh check "$BASE"' in gate and 'BASE="$(git merge-base HEAD origin/main' in gate, \
        "reapply.sh asks the check as the gate does"


def test_shfmt_rule_is_check_gh_repos_own():
    checker = (SKILL / "template/owned/.github/scripts/check_gh_repo.py").read_text()
    assert f'SHFMT_RELEASE = re.compile(r"{auto.SHFMT_RELEASE.pattern}")' in checker
    assert f"SHFMT_OLDEST_MINOR = {auto.SHFMT_OLDEST_MINOR}" in checker


def test_the_paths_it_runs_exist():
    assert os.access(auto.LAND, os.X_OK)
    assert os.access(rollout.REAPPLY, os.X_OK)
    assert os.path.isdir(auto.OWNED)
    assert os.path.isfile(os.path.join(auto.FACTORY, "CLAUDE.md")), "FACTORY is the Factory's root"


def test_the_documented_limits():
    # The ADR: a session that ends without recording is run once more, then the round stops; a session stops after
    # three hours. LOOKUP_TRIES reads LOOKUP_WAIT_SECONDS apart: 40 s of waiting before a stop.
    assert auto.MAX_REVIEWS == 2
    assert auto.REVIEW_TIMEOUT_MINUTES == 180
    assert (auto.LOOKUP_TRIES, auto.LOOKUP_WAIT_SECONDS) == (5, 10)


def test_the_deny_rules_word_for_word():
    assert auto.DENIED_TOOLS == (
        "Bash(git push:*)",
        "Bash(scripts/review.sh accept:*)",
        "Bash(scripts/review.sh accept-open:*)",
        "Bash(gh pr create:*)", "Bash(gh pr edit:*)", "Bash(gh pr comment:*)", "Bash(gh pr review:*)",
        "Bash(gh pr merge:*)", "Bash(gh pr ready:*)", "Bash(gh pr close:*)",
        "Bash(gh issue create:*)", "Bash(gh issue comment:*)", "Bash(gh issue edit:*)", "Bash(gh issue close:*)",
        "Bash(gh label:*)", "Bash(gh release:*)", "Bash(gh repo:*)", "Bash(gh secret:*)", "Bash(gh workflow:*)",
        f"Edit(/{auto.FACTORY}/**)", f"Write(/{auto.FACTORY}/**)", "Edit(~/.claude/**)", "Write(~/.claude/**)",
    )


# ── The preflight ───────────────────────────────────────────────────────────

@pytest.mark.parametrize("text, ok", [
    ("v3.14.0", True), ("v3.14.1\n", True), ("v3.15.0", True), ("v3.100.2", True),
    ("v3.13.9", False), ("v3.9.0", False), ("v4.0.0", False), ("v2.20.0", False), ("3.14.0", False), ("v3.14", False),
    ("v3.14.0-rc1", False), ("v3.14.0 extra", False), ("v3.014.0", False), ("", False), ("(devel)", False),
])
def test_shfmt_needs_v3_14_or_a_later_v3_release(text, ok):
    assert (auto.shfmt_problem(text) is None) == ok


def test_an_empty_shfmt_answer_is_named():
    assert auto.shfmt_problem("") == "shfmt is silent; the gate needs v3.14 or a later v3 release"


@pytest.mark.parametrize("text, version", [
    ('{"sdk": {"version": "10.0.403", "rollForward": "latestMinor"}}', "10.0.403"),
    ('﻿// a comment\n{\n  /* sdk */ "sdk": { "rollForward": "latestMinor", "version": "10.0.100-rc.2" },\n}', "10.0.100-rc.2"),
    ('{"test": {"runner": "x"}}', None),
    ('{"sdk": {"version": 10}}', None),
    ('{"sdk": {"version": "$(rm -rf ~)"}}', None),
    ('{"sdk": {"version": "1' + "0" * 40 + '"}}', "1" + "0" * 40),
    ('{"sdk": {"version": "1' + "0" * 41 + '"}}', None),
    ('{"sdk": {"a": {}, "version": "1.0.0"}}', None),
    ('{"sdk": {}, "tool": {"version": "9.0.0"}}', None),
    ('{"sdk":\r\n {\r\n "version" :\t"10.0.100"\r\n }}', "10.0.100"),
    ('{"sdk": null}', None),
    ("[]", None),
    ("{not json", None),
    ("", None),
])
def test_sdk_hint_finds_a_plain_version_only(text, version):
    assert auto.sdk_hint(text) == version


def fake_preflight(on_path=auto.TOOLS + ("dotnet",), answers=None, global_json='{"sdk": {"version": "10.0.403"}}',
                   tmp_path=None):
    answers = {"shfmt": (0, "v3.14.1\n", ""), "gh": (0, "", ""), "dotnet": (0, "10.0.404\n", "")} | dict(answers or {})
    calls = []

    def run(command, cwd=None, env=None):
        seen = None
        if cwd is not None and os.path.isfile(os.path.join(cwd, "global.json")):
            seen = Path(cwd, "global.json").read_text()
        calls.append({"command": tuple(command), "cwd": cwd, "env": env, "global.json": seen})
        return answers[command[0]]

    path = os.path.join(tmp_path, "global.json") if tmp_path else "/nonexistent/global.json"
    if global_json is not None and tmp_path:
        Path(path).write_text(global_json)
    problems = auto.preflight(which=lambda tool: f"/bin/{tool}" if tool in on_path else None, run=run,
                              template_global_json=path)
    return problems, calls


def test_preflight_passes_with_every_tool_and_the_templates_sdk(tmp_path):
    problems, calls = fake_preflight(tmp_path=tmp_path)
    assert problems == []
    dotnet = [call for call in calls if call["command"] == ("dotnet", "--version")]
    assert len(dotnet) == 1
    assert dotnet[0]["cwd"] != str(tmp_path), "dotnet runs in a folder of its own, holding a copy"
    assert dotnet[0]["global.json"] == '{"sdk": {"version": "10.0.403"}}'


def test_preflight_runs_shfmt_with_path_alone_as_the_gh_check_does(tmp_path, monkeypatch):
    monkeypatch.setenv("SHFMT_SHIM_HOME", "x")
    _, calls = fake_preflight(tmp_path=tmp_path)
    shfmt = next(call for call in calls if call["command"] == ("shfmt", "--version"))
    assert shfmt["env"] == {"PATH": os.environ["PATH"]}


@pytest.mark.parametrize("tool", auto.TOOLS)
def test_preflight_names_each_missing_tool_and_runs_nothing_of_it(tool, tmp_path):
    problems, calls = fake_preflight(on_path=tuple(t for t in auto.TOOLS + ("dotnet",) if t != tool), tmp_path=tmp_path)
    assert problems == [f"{tool} isn't on PATH"]
    assert not any(call["command"][0] == tool for call in calls)


def test_preflight_refuses_an_old_shfmt_and_a_signed_out_gh(tmp_path):
    problems, _ = fake_preflight(answers={"shfmt": (0, "v3.13.0\n", ""), "gh": (1, "", "not logged in")}, tmp_path=tmp_path)
    assert problems == ["shfmt is v3.13.0; the gate needs v3.14 or a later v3 release", "gh isn't signed in: gh auth login"]


def test_preflight_reads_shfmts_version_from_stdout_alone(tmp_path):
    problems, _ = fake_preflight(answers={"shfmt": (0, "v3.14.1\n", "a shim's warning\n")}, tmp_path=tmp_path)
    assert problems == []


def test_preflight_names_a_failing_shfmt(tmp_path):
    problems, _ = fake_preflight(answers={"shfmt": (2, "", "bad flag")}, tmp_path=tmp_path)
    assert problems == ["shfmt --version failed (exit 2): bad flag"]


@pytest.mark.parametrize("code", [124, 127])
def test_a_gh_that_does_not_answer_is_not_called_signed_out(code, tmp_path):
    problems, _ = fake_preflight(answers={"gh": (code, "", "gh didn't answer in 120 s")}, tmp_path=tmp_path)
    assert problems == ["gh auth status didn't run: gh didn't answer in 120 s"]


def test_preflight_names_the_templates_sdk_and_how_to_install_it(tmp_path):
    problems, _ = fake_preflight(answers={"dotnet": (155, "", "A compatible .NET SDK was not found.")}, tmp_path=tmp_path)
    assert problems == ["no installed .NET SDK matches the Template's global.json (.NET SDK 10.0.403): dotnetup sdk install 10.0.403"]


def test_preflight_never_builds_an_install_command_from_an_unreadable_version(tmp_path):
    problems, _ = fake_preflight(answers={"dotnet": (1, "", "")}, global_json='{"sdk": {"version": "$(x)"}}', tmp_path=tmp_path)
    assert problems == ["no installed .NET SDK matches the Template's global.json (the .NET SDK it names)"]


def test_preflight_wants_dotnet_only_when_the_template_has_a_global_json(tmp_path):
    assert fake_preflight(on_path=auto.TOOLS, global_json=None, tmp_path=tmp_path)[0] == []
    problems, _ = fake_preflight(on_path=auto.TOOLS, tmp_path=tmp_path)
    assert problems == ["the Template's global.json names .NET SDK 10.0.403, and dotnet isn't on PATH"]


def test_preflight_names_an_unreadable_global_json_and_runs_no_dotnet(tmp_path):
    (tmp_path / "global.json").mkdir()
    problems, calls = fake_preflight(global_json=None, tmp_path=tmp_path)
    assert len(problems) == 1 and problems[0].startswith("can't read the Template's global.json: ")
    assert not any(call["command"][0] == "dotnet" for call in calls)


@pytest.mark.parametrize("code, error", [(127, "No such file"), (124, "dotnet didn't answer in 120 s")])
def test_preflight_says_why_dotnet_did_not_answer(code, error, tmp_path):
    problems, _ = fake_preflight(answers={"dotnet": (code, "", error)}, tmp_path=tmp_path)
    assert problems == [f"no installed .NET SDK matches the Template's global.json (.NET SDK 10.0.403):"
                        f" dotnetup sdk install 10.0.403 ({error})"]


def test_preflight_checks_the_real_templates_global_json_by_default():
    assert auto.TEMPLATE_GLOBAL_JSON == os.path.join(auto.OWNED, "global.json")
    assert os.path.isfile(auto.TEMPLATE_GLOBAL_JSON)


def test_run_quiet_reads_bytes_that_arent_utf_8():
    assert auto.run_quiet(["printf", "a\\377b"]) == (0, "a\ufffdb", "")


def test_run_quiet_passes_its_limits_and_the_env(monkeypatch):
    seen = {}
    monkeypatch.setattr(auto.subprocess, "run", lambda command, **kwargs: seen.update(kwargs) or subprocess.CompletedProcess(command, 0, "", ""))
    auto.run_quiet(["x"], cwd="/c", env={"PATH": "/p"})
    assert (seen["timeout"], seen["stdin"], seen["env"], seen["cwd"]) == (120, subprocess.DEVNULL, {"PATH": "/p"}, "/c")


def test_run_quiet_turns_a_missing_command_and_a_timeout_into_codes(monkeypatch):
    assert auto.run_quiet(["/nonexistent/tool"])[0] == 127

    def slow(*args, **kwargs):
        raise subprocess.TimeoutExpired(args[0], 120)

    monkeypatch.setattr(auto.subprocess, "run", slow)
    assert auto.run_quiet(["gh", "auth", "status"]) == (124, "", "gh didn't answer in 120 s")


# ── The worktree, the lock, the Owned files ─────────────────────────────────

def test_review_worktree_is_beside_the_checkout_with_symlinks_resolved(tmp_path):
    (tmp_path / "real" / "TicketManager").mkdir(parents=True)
    (tmp_path / "link").symlink_to(tmp_path / "real")
    expected = str(tmp_path / "real" / "TicketManager-worktrees" / "chore-reapply-baseline")
    assert auto.review_worktree(str(tmp_path / "link" / "TicketManager")) == expected
    assert auto.review_worktree(str(tmp_path / "real" / "TicketManager") + "/") == expected


def test_a_lock_that_cannot_be_taken_raises(tmp_path):
    (tmp_path / "file").write_text("")
    with pytest.raises(OSError):
        auto.take_lock(str(tmp_path / "file" / "lock"))


def test_a_second_run_cannot_take_the_lock(tmp_path):
    path = str(tmp_path / "sub" / "lock")
    first = auto.take_lock(path)
    assert first is not None
    script = f"import sys; sys.path.insert(0, {str(SKILL / 'github')!r}); import auto_rollout as a; print(a.take_lock({path!r}) is None)"
    assert subprocess.run([sys.executable, "-c", script], capture_output=True, text=True).stdout.strip() == "True"
    first.close()
    assert subprocess.run([sys.executable, "-c", script], capture_output=True, text=True).stdout.strip() == "False"


def test_owned_changes_lists_only_the_owned_files_the_branch_changes(tmp_path):
    owned = tmp_path / "owned"
    (owned / "scripts").mkdir(parents=True)
    (owned / "scripts" / "gate.sh").write_text("")
    (owned / "a b.md").write_text("")
    seen = []

    def run(command, **kwargs):
        seen.append(command)
        return 0, "src/App.cs\0scripts/gate.sh\0a b.md\0scripts\0", ""

    assert auto.owned_changes("/w", run=run, owned=str(owned)) == ["a b.md", "scripts/gate.sh"]
    assert seen == [["git", "-C", "/w", "diff", "--no-renames", "--name-only", "-z", "origin/main...HEAD"]]
    with pytest.raises(RuntimeError, match="git diff in /w failed: bad"):
        auto.owned_changes("/w", run=lambda command, **kwargs: (128, "", "bad"), owned=str(owned))


# ── The review session ──────────────────────────────────────────────────────

class FakeSession:
    def __init__(self, code=0, raises=None):
        self.code, self.raises, self.pid, self.waits = code, raises, os.getpid(), []

    def wait(self, timeout=None):
        self.waits.append(timeout)
        if self.raises is not None and timeout is not None:
            raise self.raises
        return self.code


def review_once(tmp_path, monkeypatch, popen=None, changes=None, worktree=None, session=None):
    seen = {}
    session = session or FakeSession()

    def fake_popen(command, **kwargs):
        seen.update(command=command, **kwargs)
        return session

    monkeypatch.setenv("GATE_SKIP_REVIEW", "1")
    if worktree is None:
        worktree = tmp_path / "TicketManager-worktrees" / "chore-reapply-baseline"
        worktree.mkdir(parents=True)
    code, log = auto.run_review(TM, str(worktree), popen=popen or fake_popen,
                                changes=changes or (lambda path: [".github/hooks/pre-push", "scripts/gate.sh"]),
                                log_dir=str(tmp_path / "logs"))
    return code, log, seen, str(worktree)


def test_the_review_session_starts_in_the_worktree_unattended(tmp_path, monkeypatch):
    code, log, seen, worktree = review_once(tmp_path, monkeypatch)
    assert code == 0
    command = seen["command"]
    assert command[:2] == ["claude", "-p"]
    assert command[command.index("--permission-mode") + 1] == "auto"
    assert seen["cwd"] == worktree
    assert seen["stdin"] is subprocess.DEVNULL
    assert seen["stderr"] is subprocess.STDOUT
    assert seen["start_new_session"] is True
    assert "GATE_SKIP_REVIEW" not in seen["env"]
    assert seen["env"]["PATH"] == os.environ["PATH"] and seen["env"]["HOME"] == os.environ["HOME"]
    prompt = command[2]
    assert prompt.startswith("/pre-push-review ")
    assert worktree in prompt and TM in prompt
    assert ".github/hooks/pre-push, scripts/gate.sh" in prompt
    assert "Never edit an Owned file" in prompt and "Don't push" in prompt and "never ask one" in prompt


def test_the_review_session_gets_no_access_to_the_template_and_every_deny_rule(tmp_path, monkeypatch):
    _, _, seen, _ = review_once(tmp_path, monkeypatch)
    command = seen["command"]
    assert "--add-dir" not in command and auto.OWNED not in " ".join(command[3:])
    denied = command[command.index("--disallowedTools") + 1:]
    assert tuple(denied) == auto.DENIED_TOOLS
    for rule in ("Bash(git push:*)", "Bash(scripts/review.sh accept:*)", "Bash(scripts/review.sh accept-open:*)",
                 "Bash(gh pr create:*)", "Bash(gh pr edit:*)", "Bash(gh pr comment:*)", "Bash(gh pr merge:*)",
                 "Bash(gh issue create:*)", "Bash(gh issue comment:*)", f"Edit(/{auto.FACTORY}/**)",
                 f"Write(/{auto.FACTORY}/**)", "Edit(~/.claude/**)", "Write(~/.claude/**)"):
        assert rule in denied
    assert auto.FACTORY.startswith("/") and f"Edit(//{auto.FACTORY.lstrip('/')}/**)" in denied, "an absolute path rule is //path"


def test_the_review_log_is_the_owners_alone(tmp_path, monkeypatch):
    _, log, _, _ = review_once(tmp_path, monkeypatch)
    assert log.startswith(str(tmp_path / "logs")) and os.path.basename(log).startswith("review-TicketManager.") and log.endswith(".log")
    assert stat.S_IMODE(os.stat(log).st_mode) & 0o077 == 0
    assert stat.S_IMODE(os.stat(tmp_path / "logs").st_mode) == 0o700
    assert auto.LOG_DIR == os.path.expanduser("~/.cache/repo-ci-baseline/logs")


def test_the_prompt_says_none_when_no_owned_file_changes():
    assert "These files the branch changes are Owned Baseline files, which the next Apply overwrites: none." in \
        auto.review_command(TM, "/w", [])[2]


def test_a_review_session_past_its_time_is_stopped_and_noted(tmp_path, monkeypatch):
    killed = []
    monkeypatch.setattr(auto.os, "killpg", lambda pid, sig: killed.append((pid, sig)))
    session = FakeSession(raises=subprocess.TimeoutExpired("claude", 1))
    code, log, _, _ = review_once(tmp_path, monkeypatch, session=session)
    assert code == 124
    assert session.waits == [auto.REVIEW_TIMEOUT_MINUTES * 60, None], "waited its time, then reaped the killed group"
    assert killed == [(session.pid, auto.signal.SIGKILL)]
    assert f"ran past {auto.REVIEW_TIMEOUT_MINUTES} minutes" in Path(log).read_text()


def test_an_interrupted_review_session_kills_its_group_and_passes_the_interrupt_on(tmp_path, monkeypatch):
    killed = []
    monkeypatch.setattr(auto.os, "killpg", lambda pid, sig: killed.append(pid))
    session = FakeSession(raises=KeyboardInterrupt())
    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, session=session)
    assert killed == [session.pid]


def test_a_timed_out_session_takes_what_it_started_with_it(tmp_path):
    # A real process group: a shell that starts a child and waits. The timeout must kill the child too.
    pid_file = tmp_path / "child.pid"
    worktree = tmp_path / "w"
    worktree.mkdir()

    def popen(command, **kwargs):
        return subprocess.Popen(["bash", "-c", f"sleep 60 & echo $! > {pid_file}; wait"], **kwargs)

    code, _ = auto.run_review(TM, str(worktree), popen=popen, changes=lambda path: [], timeout=1, log_dir=str(tmp_path / "logs"))
    assert code == 124
    child = int(pid_file.read_text())
    for _ in range(50):  # killed, it is gone, or a zombie until its new parent reaps it
        try:
            state = Path(f"/proc/{child}/stat").read_text().rsplit(")", 1)[1].split()[0]
        except FileNotFoundError:
            break
        if state == "Z":
            break
        time.sleep(0.05)
    else:
        pytest.fail(f"the session's child {child} is still running ({state})")


def test_a_claude_that_cannot_start_is_127_with_its_reason_in_the_log(tmp_path, monkeypatch):
    def missing(command, **kwargs):
        raise FileNotFoundError(2, "No such file", "claude")

    code, log, _, _ = review_once(tmp_path, monkeypatch, popen=missing)
    assert code == 127 and "can't start claude" in Path(log).read_text()


def test_a_review_session_exit_code_is_passed_on(tmp_path, monkeypatch):
    code, _, _, _ = review_once(tmp_path, monkeypatch, session=FakeSession(code=3))
    assert code == 3


def test_a_missing_worktree_is_named_and_nothing_starts(tmp_path, monkeypatch):
    started = []
    code, log, _, _ = review_once(tmp_path, monkeypatch, popen=lambda *a, **k: started.append(a),
                                  worktree=tmp_path / "gone")
    assert code == 127 and started == []
    assert f"the re-Apply worktree {tmp_path / 'gone'} doesn't exist" in Path(log).read_text()


def test_an_owned_list_that_fails_stops_the_session_before_it_starts(tmp_path, monkeypatch):
    started = []

    def broken(path):
        raise RuntimeError("git diff failed")

    code, log, _, _ = review_once(tmp_path, monkeypatch, popen=lambda *a, **k: started.append(a), changes=broken)
    assert code == 127 and started == [] and "git diff failed" in Path(log).read_text()


def test_a_log_that_cannot_be_made_is_127(tmp_path, monkeypatch):
    def full(*args, **kwargs):
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(auto.tempfile, "mkstemp", full)
    code, log = auto.run_review(TM, str(tmp_path), log_dir=str(tmp_path / "logs"))
    assert code == 127 and "No space left on device" in log


def test_run_land_runs_land_sh_on_the_prs(monkeypatch):
    seen = []
    monkeypatch.setattr(auto.subprocess, "run", lambda command: seen.append(command) or subprocess.CompletedProcess(command, 3))
    assert auto.run_land([f"{TM}#130", f"{REST[0]}#5"]) == 3
    assert seen == [[auto.LAND, f"{TM}#130", f"{REST[0]}#5"]]


# ── The round ───────────────────────────────────────────────────────────────

def head_of(repo):
    return f"{short(repo).lower():0<40}"[:40]


def short(repo):
    return repo.split("/")[-1]


class Lock:
    def __init__(self):
        self.closed = False

    def close(self):
        self.closed = True


class Fleet:
    """reapply, review, land, gh and git as the tests set them.

    codes: {repo: [reapply exit codes in order]}, 0 once they run out. A 0 for a repo in `opens` pushes: its branch
    gets the repo's head beyond main, and its PR (from `opens`) is OPEN at that head. prs: {repo: (number, state, head)
    or None} as gh reads them before the round (default: the last round's, merged). stale: {repo: reads that still show
    the last round's PR}. unlanded: refs land.sh leaves open. still_open: refs that read OPEN after land.sh returned 0.
    unreadable: repos gh fails for (failures: {repo: reads that fail before gh answers}). closes: refs land.sh closes.
    """

    def __init__(self, codes=None, opens=None, prs=None, reviews=None, unlanded=(), still_open=(), unreadable=(),
                 stale=None, problems=(), locked=False, raises=None, failures=None, closes=(), branch_fails=(),
                 land_raises=None, lock_raises=None, moves=()):
        self.codes = {repo: list(seq) for repo, seq in (codes or {}).items()}
        self.opens = dict(opens or {})
        self.prs = {repo: (100, "MERGED", "0" * 40) for repo in LISTED} | dict(prs or {})
        self.ahead = {}
        self.review_codes = dict(reviews or {})
        self.unlanded, self.still_open, self.unreadable = set(unlanded), set(still_open), set(unreadable)
        self.stale, self.failures = dict(stale or {}), dict(failures or {})
        self.closes, self.branch_fails, self.moves = set(closes), set(branch_fails), set(moves)
        self.problems, self.locked, self.raises = list(problems), locked, dict(raises or {})
        self.land_raises, self.lock_raises = land_raises, lock_raises
        self.events, self.reviewed, self.landed, self.sleeps, self.locks = [], [], [], [], []

    def repo_of(self, path):
        return "mpaulosky/" + Path(path).name

    def reapply(self, brings, path):
        repo = self.repo_of(path)
        self.events.append(("reapply", short(repo), brings, path, [lock.closed for lock in self.locks]))
        if repo in self.raises:
            raise self.raises[repo]
        seq = self.codes.get(repo)
        code = seq.pop(0) if seq else 0
        if code == 0 and repo in self.opens:
            self.ahead[repo] = True
            self.prs[repo] = (self.opens[repo], "OPEN", head_of(repo))
        return code

    def review(self, repo, worktree):
        self.events.append(("review", short(repo)))
        self.reviewed.append((repo, worktree))
        return self.review_codes.get(repo, 0), f"/tmp/review-{short(repo)}.log"

    def land(self, refs):
        self.events.append(("land", tuple(refs)))
        self.landed.append(tuple(refs))
        if self.land_raises is not None:
            raise self.land_raises
        for ref in refs:
            repo = ref.split("#")[0]
            number, _, head = self.prs[repo]
            if ref in self.moves:
                head = "e" * 40  # land.sh updated the branch before it merged
            if ref in self.closes:
                self.prs[repo] = (number, "CLOSED", head)
            elif ref not in self.unlanded:
                self.prs[repo] = (number, "MERGED", head)
        return 1 if self.unlanded & set(refs) else 0

    def pr_of(self, repo):
        if repo in self.unreadable:
            raise RuntimeError("HTTP 502")
        if self.failures.get(repo, 0) > 0:
            self.failures[repo] -= 1
            raise RuntimeError("HTTP 502")
        if self.stale.get(repo, 0) > 0:
            self.stale[repo] -= 1
            return 99, "MERGED", "f" * 40
        return self.prs[repo]

    def pr_state(self, repo, number):
        if repo in self.unreadable:
            raise RuntimeError("HTTP 502")
        _, state, _ = self.prs[repo]
        if state == "MERGED" and f"{repo}#{number}" in self.still_open:
            return "OPEN"
        return state

    def branch_of(self, worktree):
        repo = "mpaulosky/" + Path(worktree).parent.name.removesuffix("-worktrees")
        if repo in self.branch_fails:
            raise RuntimeError("git rev-parse HEAD exited 128: not a git repository")
        return head_of(repo), self.ahead.get(repo, False)

    def check(self):
        self.events.append(("check",))
        return self.problems

    def lock(self):
        if self.lock_raises is not None:
            raise self.lock_raises
        if self.locked:
            return None
        self.locks.append(Lock())
        return self.locks[-1]


def run_auto(argv, fleet, root="/r", default_root="/r"):
    out, err = io.StringIO(), io.StringIO()
    hooks = {"review": fleet.review, "land": fleet.land, "pr_of": fleet.pr_of, "pr_state": fleet.pr_state,
             "branch_of": fleet.branch_of, "check": fleet.check, "lock": fleet.lock, "sleep": fleet.sleeps.append}
    if default_root is not None:
        hooks["default_root"] = default_root
    code = rollout.main(["--auto", "--root", root, *argv], listed=LISTED, reapply=fleet.reapply,
                        exists=lambda path: True, out=out, err=err, auto=hooks)
    return code, out.getvalue(), err.getvalue()


def reapplied(fleet):
    return [event[1] for event in fleet.events if event[0] == "reapply"]


ALL_OPEN = {TM: 130, **{repo: n for n, repo in enumerate(REST, start=200)}}


def test_auto_reviews_reapplies_and_lands_ticketmanager_then_the_rest():
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED]}, opens=ALL_OPEN)
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out + err
    assert fleet.reviewed == [(TM, "/r/TicketManager-worktrees/chore-reapply-baseline")]
    assert reapplied(fleet) == ["TicketManager", "TicketManager", *map(short, REST)]
    assert fleet.landed == [(f"{TM}#130",), tuple(f"{repo}#{n}" for n, repo in enumerate(REST, start=200))]
    assert "rollout: TicketManager and 5 of 5 other repo(s) done\n" in out
    assert "resume with" not in out and err == ""


def test_the_lock_is_held_for_the_whole_round_and_released_after():
    fleet = Fleet(opens=ALL_OPEN)
    run_auto(["--brings", "#82"], fleet)
    assert len(fleet.locks) == 1
    assert all(event[4] == [False] for event in fleet.events if event[0] == "reapply"), "held while each repo runs"
    assert fleet.locks[0].closed


def test_the_lock_is_released_when_the_round_crashes():
    fleet = Fleet(raises={TM: KeyboardInterrupt()})
    with pytest.raises(KeyboardInterrupt):
        run_auto(["--brings", "#82"], fleet)
    assert fleet.locks[0].closed


def test_auto_runs_the_preflight_first_and_lands_ticketmanager_before_any_other_reapply():
    fleet = Fleet(opens={TM: 130, REST[0]: 200})
    run_auto(["--brings", "#82"], fleet)
    assert [event[0] for event in fleet.events[:4]] == ["check", "reapply", "land", "reapply"]
    assert fleet.events[3][1] == "IssueTracker"


def test_auto_stops_on_a_preflight_problem_with_nothing_run_and_no_lock():
    fleet = Fleet(problems=["shfmt isn't on PATH", "gh isn't signed in: gh auth login"])
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert fleet.events == [("check",)] and fleet.locks == []
    assert "rollout.sh: shfmt isn't on PATH\nrollout.sh: gh isn't signed in: gh auth login\nrollout.sh: nothing run" in err


def test_auto_stops_when_another_run_holds_the_lock():
    fleet = Fleet(locked=True)
    code, _, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert reapplied(fleet) == []
    assert "another rollout.sh --auto holds" in err


def test_auto_stops_when_the_lock_cannot_be_taken():
    fleet = Fleet(lock_raises=PermissionError(13, "Permission denied"))
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and reapplied(fleet) == []
    assert f"rollout.sh: can't take the lock {auto.LOCK}: [Errno 13] Permission denied; nothing run" in err


def test_auto_a_missing_checkout_stops_before_the_preflight():
    fleet = Fleet()
    out, err = io.StringIO(), io.StringIO()
    code = rollout.main(["--auto", "--brings", "#82", "--root", "/r"], listed=LISTED, reapply=fleet.reapply,
                        exists=lambda path: not path.endswith("Articles"), out=out, err=err,
                        auto={"check": fleet.check, "lock": fleet.lock})
    assert code == 1 and fleet.events == []
    assert "no checkout at /r/Articles" in err.getvalue()


def test_auto_stops_for_a_person_when_findings_wait_and_holds_the_rest():
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED, auto.FINDINGS_WAIT]})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert reapplied(fleet) == ["TicketManager", "TicketManager"]
    assert "TicketManager: review findings wait for you: cd /r/TicketManager-worktrees/chore-reapply-baseline && scripts/review.sh accept-open" in out
    assert "held: IssueTracker, Blazor-Server, IssueManager, Articles, SuggestionApp" in out
    assert "resume with: rollout.sh --auto --brings '#82'\n" in out
    assert fleet.landed == []


def test_the_printed_commands_quote_a_root_with_a_space_and_carry_it_when_not_the_default():
    fleet = Fleet(codes={TM: [1]})
    code, out, _ = run_auto(["--brings", "#82"], fleet, root="/my repos")
    assert code == 1
    assert "retry it with: reapply.sh --brings '#82' '/my repos/TicketManager'" in out
    assert "resume with: rollout.sh --auto --brings '#82' --root '/my repos'" in out
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED, auto.FINDINGS_WAIT]})
    _, out, _ = run_auto(["--brings", "#82"], fleet, root="/my repos")
    assert "cd '/my repos/TicketManager-worktrees/chore-reapply-baseline' && scripts/review.sh accept-open" in out
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED]}, reviews={TM: 1})
    _, out, _ = run_auto(["--brings", "#82"], fleet, root="/my repos")
    assert "run /pre-push-review in '/my repos/TicketManager-worktrees/chore-reapply-baseline', then resume" in out


@pytest.mark.parametrize("root", ["/r/", "/r/../r", "/r"])
def test_a_root_equal_to_the_default_is_left_out(root):
    fleet = Fleet(codes={TM: [1]})
    _, out, _ = run_auto(["--brings", "#82"], fleet, root=root)
    assert "resume with: rollout.sh --auto --brings '#82'\n" in out


def test_the_default_root_is_rollouts_own():
    fleet = Fleet(codes={TM: [1]})
    _, out, _ = run_auto(["--brings", "#82"], fleet, root=os.path.expanduser("~/github"), default_root=None)
    assert "resume with: rollout.sh --auto --brings '#82'\n" in out
    assert rollout.DEFAULT_ROOT == os.path.expanduser("~/github")


def test_auto_gives_up_after_two_review_sessions_when_the_gate_still_asks():
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED] * 3})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert len(fleet.reviewed) == 2 and len(reapplied(fleet)) == 3
    assert "the gate still asks for a review after 2 review session(s): main or the Template may have moved" in out
    assert "(see /tmp/review-TicketManager.log)" in out


def test_auto_reviews_a_second_time_when_the_first_session_left_no_record():
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED] * 2}, opens={TM: 130})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert len(fleet.reviewed) == 2


def test_auto_stops_when_a_review_session_fails_and_says_what_to_do():
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED]}, reviews={TM: 124})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert len(reapplied(fleet)) == 1
    assert ("the review session exited 124 (see /tmp/review-TicketManager.log); run /pre-push-review in"
            " /r/TicketManager-worktrees/chore-reapply-baseline, then resume") in out


def test_auto_holds_the_rest_when_ticketmanagers_pr_does_not_land():
    fleet = Fleet(opens={TM: 130}, unlanded={f"{TM}#130"})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert reapplied(fleet) == ["TicketManager"]
    assert "TicketManager#130 didn't land (see land.sh's output above): land.sh mpaulosky/TicketManager#130" in out


def test_a_landed_pr_is_read_by_its_number_even_after_land_sh_updated_its_branch():
    fleet = Fleet(opens={TM: 130, REST[0]: 200}, moves={f"{TM}#130", f"{REST[0]}#200"})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert fleet.sleeps == []


def test_auto_waits_for_a_landed_pr_to_read_as_merged():
    fleet = Fleet(opens={TM: 130})
    still = {"n": 2}
    pr_state = fleet.pr_state

    def lagging(repo, number):
        state = pr_state(repo, number)
        if repo == TM and state == "MERGED" and still["n"]:
            still["n"] -= 1
            return "OPEN"
        return state

    fleet.pr_state = lagging
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert fleet.sleeps == [10, 10]


def test_auto_holds_when_a_landed_pr_never_reads_as_merged():
    fleet = Fleet(opens={TM: 130}, still_open={f"{TM}#130"})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "TicketManager: can't tell its re-Apply PR (#130 still reads as OPEN)" in out
    assert fleet.sleeps == [10] * 4
    assert reapplied(fleet) == ["TicketManager"]


def test_a_landed_pr_that_was_closed_holds_the_round():
    fleet = Fleet(opens={TM: 130}, closes={f"{TM}#130"})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and "TicketManager#130 is CLOSED, not merged" in out


@pytest.mark.parametrize("reads, lands", [(auto.LOOKUP_TRIES - 1, True), (auto.LOOKUP_TRIES, False)])
def test_a_stale_read_of_the_last_rounds_merged_pr_never_releases_the_rest(reads, lands):
    # The newest PR by the branch's name is the last round's merged one until the list shows the new PR.
    fleet = Fleet(opens={TM: 130}, stale={TM: reads})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    if lands:
        assert code == 0, out
        assert fleet.landed[0] == (f"{TM}#130",), "the new PR is landed, not skipped as merged"
        assert fleet.sleeps == [10] * reads
    else:
        assert code == 1 and fleet.landed == [] and reapplied(fleet) == ["TicketManager"]
        assert "#99 is at fffffff, the worktree at ticketm" in out


def test_an_open_pr_at_another_head_is_not_this_rounds():
    # Nothing ahead of main, and an OPEN PR at another head (its branch moved): not landed.
    fleet = Fleet(prs={TM: (131, "OPEN", "a" * 40)})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and fleet.landed == []
    assert "TicketManager: can't tell its re-Apply PR (#131 is at aaaaaaa, the worktree at ticketm)" in out


@pytest.mark.parametrize("fails, ok", [(auto.LOOKUP_TRIES - 1, True), (auto.LOOKUP_TRIES, False)])
def test_a_gh_that_fails_and_recovers_is_tried_again(fails, ok):
    fleet = Fleet(opens={TM: 130}, failures={TM: fails})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert (code == 0) == ok, out
    if not ok:
        assert "TicketManager: can't tell its re-Apply PR (gh failed: HTTP 502)" in out


def test_auto_holds_the_rest_when_ticketmanagers_reapply_fails():
    fleet = Fleet(codes={TM: [1]})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and reapplied(fleet) == ["TicketManager"]
    assert "TicketManager: reapply.sh exited 1" in out


def test_auto_holds_the_rest_when_ticketmanagers_pr_was_closed():
    fleet = Fleet(prs={TM: (129, "CLOSED", "0" * 40)})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "TicketManager#129 is CLOSED, not merged" in out


def test_auto_holds_with_the_gh_error_when_ticketmanagers_pr_cannot_be_read():
    fleet = Fleet(unreadable={TM})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and reapplied(fleet) == ["TicketManager"]
    assert "TicketManager: can't tell its re-Apply PR (gh failed: HTTP 502): land it with land.sh mpaulosky/TicketManager" in out
    assert "#None" not in out


def test_auto_holds_when_ticketmanager_has_no_reapply_pr_and_nothing_to_push():
    fleet = Fleet(prs={TM: None})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and fleet.sleeps == []
    assert "TicketManager: no re-Apply PR found, and nothing to push" in out


def test_a_worktree_whose_branch_cannot_be_read_is_a_stop():
    fleet = Fleet(branch_fails={TM})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "TicketManager: can't tell its re-Apply PR (can't read /r/TicketManager-worktrees/chore-reapply-baseline's branch:" \
           " git rev-parse HEAD exited 128: not a git repository)" in out
    fleet = Fleet(branch_fails={ARTICLES})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and reapplied(fleet)[-1] == "SuggestionApp"
    assert "  Articles: can't tell its re-Apply PR (can't read " in out


def test_a_crash_on_ticketmanager_still_ends_with_the_resume_command_and_a_traceback():
    fleet = Fleet(raises={TM: PermissionError(13, "Permission denied")})
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "TicketManager: PermissionError: [Errno 13] Permission denied (traceback on stderr)" in out and "resume with:" in out
    assert "rollout.sh: TicketManager crashed:\nTraceback (most recent call last):" in err


def test_auto_reports_each_stopped_repo_among_the_rest_lands_none_of_theirs_and_goes_on():
    # Blazor-Server waits for a person and Articles fails, each with an open PR from an earlier run: neither is landed.
    fleet = Fleet(codes={BLAZOR: [auto.FINDINGS_WAIT], ARTICLES: [1]}, opens={repo: 300 for repo in REST},
                  prs={BLAZOR: (250, "OPEN", "0" * 40), ARTICLES: (251, "OPEN", "0" * 40)},
                  unlanded={f"{REST[0]}#300"})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert reapplied(fleet) == ["TicketManager", *map(short, REST)]
    landed = {ref for refs in fleet.landed for ref in refs}
    assert not any(ref.startswith((BLAZOR, ARTICLES)) for ref in landed)
    assert f"{ISSUEMANAGER}#300" in landed
    assert "  Blazor-Server: review findings wait for you" in out
    assert "  Articles: reapply.sh exited 1" in out
    assert "  IssueTracker#300 didn't land (#300 still reads as OPEN): land.sh mpaulosky/IssueTracker#300" in out
    assert "rollout: TicketManager and 2 of 5 other repo(s) done; waiting for you:" in out
    assert out.rstrip().endswith("resume with: rollout.sh --auto --brings '#82'")


def test_a_rest_repo_whose_pr_cannot_be_read_is_a_stop_not_done():
    fleet = Fleet(opens={TM: 130, REST[0]: 200}, unreadable={REST[0]})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "IssueTracker: can't tell its re-Apply PR (gh failed: HTTP 502): land it with land.sh mpaulosky/IssueTracker" in out
    assert "TicketManager and 4 of 5 other repo(s) done" in out


def test_a_rest_repo_with_nothing_to_push_and_no_pr_is_done_at_once():
    fleet = Fleet(prs={REST[0]: None})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert fleet.sleeps == [] and fleet.landed == []


def test_a_rest_repo_whose_pr_was_closed_is_a_stop():
    fleet = Fleet(prs={REST[0]: (77, "CLOSED", "0" * 40)})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "  IssueTracker#77 is CLOSED, not merged" in out and "4 of 5 other repo(s) done" in out


def test_a_rest_pr_land_sh_closes_is_a_stop():
    fleet = Fleet(opens={REST[0]: 200}, closes={f"{REST[0]}#200"})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and "  IssueTracker#200 is CLOSED, not merged" in out


def test_a_land_sh_that_crashes_for_the_rest_still_reads_and_reports_each_pr():
    fleet = Fleet(opens={REST[0]: 200, REST[1]: 201}, land_raises=OSError(8, "Exec format error"))
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "rollout.sh: land.sh failed: OSError: [Errno 8] Exec format error" in out
    assert "  IssueTracker#200 didn't land (#200 still reads as OPEN): land.sh mpaulosky/IssueTracker#200" in out
    assert "  Blazor-Server#201 didn't land (#201 still reads as OPEN)" in out
    assert out.rstrip().endswith("resume with: rollout.sh --auto --brings '#82'")


def test_a_crash_on_one_of_the_rest_is_reported_and_the_round_goes_on():
    fleet = Fleet(raises={ARTICLES: OSError(8, "Exec format error")})
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert reapplied(fleet) == ["TicketManager", *map(short, REST)]
    assert "  Articles: OSError: [Errno 8] Exec format error (traceback on stderr)" in out
    assert "rollout.sh: Articles crashed:\nTraceback" in err


def test_auto_resumes_with_everything_already_merged():
    fleet = Fleet()  # every repo's last round merged, nothing new to push
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert fleet.landed == [] and fleet.reviewed == [] and fleet.sleeps == []
    assert reapplied(fleet) == ["TicketManager", *map(short, REST)]


def test_auto_dry_run_checks_the_tools_prints_every_step_and_runs_nothing():
    fleet = Fleet(locked=True)  # a plan is not a round: it takes no lock
    code, out, _ = run_auto(["--dry-run", "--brings", "#82,#83"], fleet)
    assert code == 0
    assert fleet.events == [("check",)] and fleet.landed == [] and fleet.reviewed == []
    lines = out.splitlines()
    assert lines[0] == "tools: all there"
    for line, repo in zip(lines[1:7], (TM, *REST)):
        name = short(repo)
        assert line == (f"would run: reapply.sh --brings '#82,#83' /r/{name}, with /pre-push-review headless in"
                        f" /r/{name}-worktrees/chore-reapply-baseline when its gate asks for one")
    assert lines[7] == "then land TicketManager's re-Apply PR with land.sh before the rest start, and the rest's together"
    assert len(lines) == 8


def test_auto_dry_run_with_a_problem_prints_no_plan():
    fleet = Fleet(problems=["claude isn't on PATH"])
    code, out, err = run_auto(["--dry-run", "--brings", "#82"], fleet)
    assert code == 1 and out == ""
    assert "rollout.sh: claude isn't on PATH" in err


def test_the_round_without_auto_is_unchanged():
    fleet = Fleet()
    out, err = io.StringIO(), io.StringIO()
    code = rollout.main(["--brings", "#82", "--root", "/r"], listed=LISTED, reapply=fleet.reapply,
                        latest_pr=lambda repo: (100, "MERGED"), exists=lambda path: True, out=out, err=err,
                        auto={"check": fleet.check})
    assert code == 0 and ("check",) not in fleet.events


def test_the_header_says_what_the_rules_cover_and_what_they_dont():
    doc = " ".join(auto.__doc__.split())
    assert "denies the common spellings of git push, review.sh accept and accept-open and a set of gh writes" in doc
    assert "the auto permission mode's classifier is the guard for the rest" in doc


def test_git_branch_says_whether_the_branch_has_commits_main_lacks(tmp_path):
    def git(*args):
        return subprocess.run(["git", "-C", str(tmp_path), *args], check=True, capture_output=True, text=True,
                              env={**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"}).stdout.strip()

    with pytest.raises(RuntimeError, match=r"git rev-parse HEAD in .* exited 128: fatal: not a git repository"):
        auto.git_branch(str(tmp_path))
    git("init", "-q", "-b", "main")
    git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "one")
    git("update-ref", "refs/remotes/origin/main", "HEAD")
    assert auto.git_branch(str(tmp_path)) == (git("rev-parse", "HEAD"), False)
    git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "two")
    assert auto.git_branch(str(tmp_path)) == (git("rev-parse", "HEAD"), True)
    git("update-ref", "-d", "refs/remotes/origin/main")
    with pytest.raises(RuntimeError, match="git merge-base in .* exited 128: fatal: Not a valid object name origin/main"):
        auto.git_branch(str(tmp_path))


def test_gh_pr_asks_for_every_state_and_the_repos_own_prs(monkeypatch):
    answers = iter([subprocess.CompletedProcess([], 0, "130 OPEN abc123\n", ""), subprocess.CompletedProcess([], 0, "", ""),
                    subprocess.CompletedProcess([], 1, "", "HTTP 502")])
    seen = []
    monkeypatch.setattr(auto.subprocess, "run", lambda command, **kwargs: seen.append((command, kwargs)) or next(answers))
    assert auto.gh_pr(TM) == (130, "OPEN", "abc123")
    command, kwargs = seen[0]
    assert command == ["gh", "pr", "list", "-R", TM, "--head", auto.BRANCH, "--state", "all", "--limit", "100",
                       "--json", "number,state,headRefOid,isCrossRepository", "--jq", auto.PR_JQ]
    assert kwargs["timeout"] == 120
    assert auto.gh_pr(TM) is None
    with pytest.raises(RuntimeError, match="HTTP 502"):
        auto.gh_pr(TM)


@pytest.mark.parametrize("prs, line", [
    ([], ""),
    ([{"number": 130, "state": "OPEN", "headRefOid": "abc", "isCrossRepository": False}], "130 OPEN abc"),
    ([{"number": 140, "state": "OPEN", "headRefOid": "f0f", "isCrossRepository": True},
      {"number": 130, "state": "MERGED", "headRefOid": "abc", "isCrossRepository": False}], "130 MERGED abc"),
    ([{"number": 140, "state": "OPEN", "headRefOid": "f0f", "isCrossRepository": True}], ""),
])
def test_the_pr_jq_takes_the_newest_pr_from_the_repo_itself(prs, line):
    if shutil.which("jq") is None:
        pytest.skip("jq isn't installed")
    done = subprocess.run(["jq", "-r", auto.PR_JQ], input=json.dumps(prs), capture_output=True, text=True, check=True)
    assert done.stdout.strip() == line


def test_gh_pr_state_reads_one_pr_by_number(monkeypatch):
    answers = iter([subprocess.CompletedProcess([], 0, "MERGED\n", ""), subprocess.CompletedProcess([], 1, "", "HTTP 404"),
                    subprocess.CompletedProcess([], 0, "\n", "")])
    seen = []
    monkeypatch.setattr(auto.subprocess, "run", lambda command, **kwargs: seen.append((command, kwargs)) or next(answers))
    assert auto.gh_pr_state(TM, 130) == "MERGED"
    assert seen[0][0] == ["gh", "pr", "view", "130", "-R", TM, "--json", "state", "--jq", ".state"]
    assert seen[0][1]["timeout"] == 120
    with pytest.raises(RuntimeError, match="HTTP 404"):
        auto.gh_pr_state(TM, 130)
    with pytest.raises(RuntimeError, match="gh pr view exited 0"):
        auto.gh_pr_state(TM, 130)


def test_owned_changes_reads_a_name_that_isnt_utf_8(tmp_path):
    def git(*args):
        subprocess.run(["git", "-C", str(tmp_path), *args], check=True, capture_output=True,
                       env={**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"})

    git("init", "-q", "-b", "main")
    git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "one")
    git("update-ref", "refs/remotes/origin/main", "HEAD")
    (tmp_path / "scripts").mkdir()
    (tmp_path / "scripts" / "gate.sh").write_text("x")
    os.close(os.open(os.path.join(os.fsencode(tmp_path), b"bad\xffname"), os.O_CREAT | os.O_WRONLY))
    git("add", "-A")
    git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "two")
    owned = tmp_path / "owned" / "scripts"
    owned.mkdir(parents=True)
    (owned / "gate.sh").write_text("")
    assert auto.owned_changes(str(tmp_path), owned=str(tmp_path / "owned")) == ["scripts/gate.sh"]
