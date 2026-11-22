"""Tests for github/auto_rollout.py (rollout.sh --auto), with reapply, the review session, land.sh, gh and git faked.
No GitHub, no Claude."""

import dataclasses
import hashlib
import inspect
import io
import json
import os
import re
import shutil
import signal
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


def test_reapply_sh_reads_the_refusals_in_the_ledgers_and_the_gates_own_words():
    # reapply.sh tells the two refusals apart by the text checkReview prints at a line's start, on stderr; a reworded
    # message would turn both into a plain failed push.
    ledger = (SKILL / "template/owned/.sandcastle/lib/review-ledger.mts").read_text()
    check = ledger[ledger.index("export function checkReview("):]
    check = check[:check.index("\n}\n")]
    assert f"message: `{reapply_constant('no_review_text')}" in check
    assert f"`${{blocking.length}} {reapply_constant('findings_wait_text')}" in check
    assert "process.stderr.write(`${printable(text)}\\n`)" in ledger, "fail() writes to stderr, which reapply.sh reads"
    command = ledger[ledger.index('case "check": {'):]
    command = command[:command.index("\n      }\n")]
    assert "if (!result.ok) return fail(result.message);" in command, "the check's refusal goes through fail()"
    gate = (SKILL / "template/owned/scripts/gate.sh").read_text()
    assert f'step "{reapply_constant("review_step")}"' in gate, "reapply.sh asks only when the gate stopped at this step"
    assert 'step() { echo -e "\\n${CYAN}▶ $1${RESET}"; }' in gate, "reapply.sh reads this header form"
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


ALL_TOOLS = auto.TOOLS + auto.SANDBOX_TOOLS + ("dotnet", "uvx")


def fake_preflight(on_path=ALL_TOOLS, answers=None, global_json='{"sdk": {"version": "10.0.403"}}', tmp_path=None,
                   environ=None, platform="linux"):
    answers = {"shfmt": (0, "v3.14.1\n", ""), "gh": (0, "", ""), "dotnet": (0, "10.0.404\n", ""), "bwrap": (0, "", ""),
               "claude": (0, "2.1.326 (Claude Code)\n", ""), "ruff": (0, "ruff 0.17.6\n", ""),
               "claude auth": (0, '{"loggedIn": true, "email": "someone@example.com"}\n', "")} | dict(answers or {})
    calls = []

    def run(command, cwd=None, env=None):
        seen = None
        if cwd is not None and os.path.isfile(os.path.join(cwd, "global.json")):
            seen = Path(cwd, "global.json").read_text()
        calls.append({"command": tuple(command), "cwd": cwd, "env": env, "global.json": seen})
        return answers["claude auth" if "auth" in command and command[0] == "claude" else command[0]]

    path = os.path.join(tmp_path, "global.json") if tmp_path else "/nonexistent/global.json"
    if global_json is not None and tmp_path:
        Path(path).write_text(global_json)
    problems = auto.preflight(which=lambda tool: f"/bin/{tool}" if tool in on_path else None, run=run,
                              template_global_json=path, environ=environ or {"PATH": "/bin"}, platform=platform)
    return problems, calls


SIGN_IN = ("is set, and the review session's shell would see it: unset it for the round, and sign Claude Code in with its"
           " own credentials file (claude /login) or the cloud's profile files, which the sandbox keeps from the shell")


@pytest.mark.parametrize("environ, refused", [
    ({"ANTHROPIC_API_KEY": "k"}, ["ANTHROPIC_API_KEY"]),
    ({"CLAUDE_CODE_OAUTH_TOKEN": "t", "ANTHROPIC_AUTH_TOKEN": "t"}, ["ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN"]),
    ({"ANTHROPIC_FOUNDRY_API_KEY": "k", "ANTHROPIC_CUSTOM_HEADERS": "x"}, ["ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_FOUNDRY_API_KEY"]),
    ({"ANTHROPIC_API_KEY": ""}, []),
    ({"CLAUDE_CODE_MESSAGING_TOKEN": "t", "ANTHROPIC_MODEL": "m"}, []),
    ({"CLAUDE_CODE_USE_BEDROCK": "1", "AWS_SECRET_ACCESS_KEY": "s", "AWS_ACCESS_KEY_ID": "i", "AWS_SESSION_TOKEN": "t",
      "AWS_PROFILE": "p", "AWS_REGION": "r"}, ["AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN"]),
    ({"CLAUDE_CODE_USE_BEDROCK": "0", "AWS_SECRET_ACCESS_KEY": "s"}, []),
    ({"CLAUDE_CODE_USE_VERTEX": "true", "GOOGLE_APPLICATION_CREDENTIALS": "/f", "GOOGLE_CLOUD_PROJECT": "p"},
     ["GOOGLE_APPLICATION_CREDENTIALS"]),
    ({"CLAUDE_CODE_USE_FOUNDRY": "1", "AZURE_CLIENT_SECRET": "s", "AZURE_TENANT_ID": "t"}, ["AZURE_CLIENT_SECRET"]),
])
def test_preflight_refuses_a_sign_in_secret_the_session_would_inherit(environ, refused):
    # Not a sign-in the session needs in its environment: the messaging token is the caller's, dropped by session_env.
    problems, _ = fake_preflight(environ={"PATH": "/bin", **environ})
    assert problems == [f"{name} {SIGN_IN}" for name in refused]


@pytest.mark.parametrize("name, secret", [
    ("AWS_DB_PASSWORD", True), ("AWS_API_KEY", True), ("AWS_APIKEY", True), ("AWS_AUTH_X", True),
    ("AWS_SERVICE_KEY", True), ("AWS_COOKIE", True), ("AWS_BEARER_TOKEN_BEDROCK", True), ("AWS_BEARER_X", True),
    ("AWS_SECRET_ACCESS_KEY", True), ("AWS_ACCESS_KEY_ID", True), ("AWS_SESSION_TOKEN", True),
    ("AWS_SHARED_CREDENTIALS_FILE", True), ("AWS_CLIENT_CERTIFICATE_PATH", True), ("AWS_PRIVATE_KEY_PATH", True),
    ("AWS_KEYFILE", True), ("AWS_KEY_FILE", True), ("AWS_Secret_x", True),
    ("AWS_AUTHORITY_HOST", False), ("AWS_KEYBOARD", False), ("AWS_DEFAULT_REGION", False), ("AWS_PROFILE", False),
    ("AWS_Region", False), ("AWS_KEY_ID", False),
])
def test_a_cloud_secret_is_refused_and_dropped_and_the_rest_kept(name, secret, tmp_path):
    environ = {"PATH": "/bin", "CLAUDE_CODE_USE_BEDROCK": "1", name: "v"}
    problems, _ = fake_preflight(environ=environ)
    assert problems == ([f"{name} {SIGN_IN}"] if secret else [])
    assert (name in auto.session_env(environ, str(tmp_path))) is (not secret)


def test_preflight_runs_on_linux_only(monkeypatch):
    problems, _ = fake_preflight(platform="darwin")
    assert problems[0] == ("rollout.sh --auto runs on Linux only (darwin here): what its review session may read is"
                           " checked with bubblewrap's sandbox alone; run rollout.sh without --auto, and the review by hand")
    assert fake_preflight(platform="linux")[0] == []
    monkeypatch.setattr(auto.sys, "platform", "darwin")
    assert fake_preflight(platform=None)[0][0].startswith("rollout.sh --auto runs on Linux only (darwin here)"), \
        "the host's own platform by default"


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
    problems, calls = fake_preflight(on_path=tuple(t for t in ALL_TOOLS if t != tool), tmp_path=tmp_path)
    assert problems == [f"{tool} isn't on PATH"]
    assert not any(call["command"][0] == tool for call in calls)


def test_preflight_refuses_an_old_shfmt_and_a_gh_that_fails(tmp_path):
    problems, _ = fake_preflight(answers={"shfmt": (0, "v3.13.0\n", ""), "gh": (1, "", "not logged in\n")}, tmp_path=tmp_path)
    assert problems == ["shfmt is v3.13.0; the gate needs v3.14 or a later v3 release",
                        "gh auth status failed (exit 1): not logged in; if it says you're signed out: gh auth login"]
    problems, _ = fake_preflight(answers={"gh": (1, "", "")}, tmp_path=tmp_path)
    assert problems == ["gh auth status failed (exit 1): no answer; if it says you're signed out: gh auth login"]


def test_preflight_names_each_missing_sandbox_tool(tmp_path):
    if not auto.SANDBOX_TOOLS:
        pytest.skip("the sandbox needs no tools here")
    problems, calls = fake_preflight(on_path=auto.TOOLS + ("dotnet", "uvx"), tmp_path=tmp_path)
    assert problems == [f"{tool} isn't on PATH: the review session's sandbox needs it" for tool in auto.SANDBOX_TOOLS]
    assert not any(call["command"][0] == "bwrap" for call in calls)


def test_ruff_rule_is_the_template_gates_own():
    gate = (SKILL / "template/owned/scripts/gate.sh").read_text()
    assert f"RUFF_RELEASE='{auto.RUFF_RELEASE.pattern}'" in gate
    assert f'RUFF_VERSION="{auto.RUFF_VERSION}"' in gate
    assert "if have_uvx; then" in gate, "the gate falls back to uvx, so uvx alone will do"


@pytest.mark.parametrize("on_path, ruff, problem", [
    (("uvx",), None, None),
    (("uvx", "ruff"), (0, "ruff 0.16.0\n", ""), None),
    (("ruff",), (0, "ruff 0.17.0\n", ""), None),
    (("ruff",), (0, "ruff 0.17.12\n", ""), None),
    (("ruff",), (0, "", "ruff 0.17.6\n"), None),
    (("ruff",), (0, "ruff 0.16.9\n", ""), "ruff is 'ruff 0.16.9' (exit 0), not a 0.17 release"),
    (("ruff",), (0, "ruff 0.18.0\n", ""), "ruff is 'ruff 0.18.0' (exit 0), not a 0.17 release"),
    (("ruff",), (0, "ruff 0.17.6 extra\n", ""), "ruff is 'ruff 0.17.6 extra' (exit 0), not a 0.17 release"),
    (("ruff",), (2, "", ""), "ruff is 'silent' (exit 2), not a 0.17 release"),
    (("ruff",), (1, "ruff 0.17.6\n", ""), "ruff is 'ruff 0.17.6' (exit 1), not a 0.17 release"),
    ((), None, "neither ruff 0.17 nor uv is installed, and the gate's Python lint needs one: install uv"
               " (https://docs.astral.sh/uv/), or put a ruff 0.17 first on PATH (uv tool install ruff@0.17.6 once uv is"
               " there)"),
])
def test_preflight_wants_the_gates_python_lint(on_path, ruff, problem, tmp_path):
    answers = {"ruff": ruff} if ruff else {}
    problems, calls = fake_preflight(on_path=auto.TOOLS + auto.SANDBOX_TOOLS + ("dotnet", *on_path), answers=answers,
                                     tmp_path=tmp_path)
    if problem is None:
        assert problems == []
    else:
        assert len(problems) == 1 and problems[0].startswith(problem)
        if "release" in problem:
            assert problems[0].endswith("and uv isn't installed for the gate to run the pinned one: install uv"
                                        " (https://docs.astral.sh/uv/), or put a ruff 0.17 first on PATH (uv tool install"
                                        " ruff@0.17.6 once uv is there)")
        assert "uv tool install" not in problems[0].split("install uv")[0], "never a uv command before uv is there"
    if "uvx" in on_path:
        assert not any(call["command"][0] == "ruff" for call in calls), "with uvx there is no need to ask ruff"


def test_preflight_tries_the_sandbox_and_claude(tmp_path):
    problems, calls = fake_preflight(tmp_path=tmp_path)
    commands = [call["command"] for call in calls]
    assert ("claude", "--version") in commands
    if auto.SANDBOX_TOOLS:
        assert ("bwrap", "--unshare-all", "--ro-bind", "/", "/", "--dev", "/dev", "true") in commands
    problems, _ = fake_preflight(answers={"bwrap": (1, "", "bwrap: setting up uid map: Permission denied\n"),
                                          "claude": (127, "", "No such file")}, tmp_path=tmp_path)
    expected = ["claude --version failed (exit 127): No such file"]
    if auto.SANDBOX_TOOLS:
        expected.insert(0, "bubblewrap can't make the review session's sandbox here (exit 1): bwrap: setting up uid map:"
                           " Permission denied; try it with: bwrap --unshare-all --ro-bind / / --dev /dev true. The host"
                           " may refuse unprivileged user namespaces (Ubuntu's sysctl"
                           " kernel.apparmor_restrict_unprivileged_userns, kernel.unprivileged_userns_clone, a container"
                           " without them); where it can't allow them, run rollout.sh without --auto and the review by hand")
    assert problems == expected
    problems, _ = fake_preflight(answers={"bwrap": (1, "", ""), "claude": (1, "", "")}, tmp_path=tmp_path)
    assert all(problem.split(": ", 1)[1].startswith("no answer") for problem in problems)


AUTH = "claude --setting-sources '' auth status --json"
NOT_SIGNED_IN = (f"claude isn't signed in, as {AUTH} says: sign it in with claude /login (the review session reads no"
                 " settings file, so an apiKeyHelper in one isn't used)")
CANT_TELL = f"{AUTH} doesn't say whether claude is signed in (no loggedIn in what it prints): update claude, or run rollout.sh without --auto"


@pytest.mark.parametrize("answer, problem", [
    ((0, '{"loggedIn": true, "email": "someone@example.com"}', ""), None),
    ((0, '{"loggedIn": false, "email": "someone@example.com"}', ""), NOT_SIGNED_IN),
    ((0, '{"loggedIn": "true"}', ""), NOT_SIGNED_IN),
    ((0, '{"loggedIn": null}', ""), NOT_SIGNED_IN),
    ((0, '{"email": "someone@example.com"}', ""), CANT_TELL),
    ((0, "[]", ""), CANT_TELL),
    ((0, "not json", ""), CANT_TELL),
    ((0, "", ""), CANT_TELL),
    ((1, '{"loggedIn": true}', "someone@example.com x"),
     f"{AUTH} failed (exit 1), so whether claude is signed in can't be told: run it to see why, and sign claude in with"
     " claude /login if it says you're signed out"),
    ((124, "", "claude didn't answer in 120 s"),
     f"{AUTH} failed (exit 124), so whether claude is signed in can't be told: run it to see why, and sign claude in with"
     " claude /login if it says you're signed out"),
])
def test_preflight_wants_claude_signed_in_and_shows_nothing_of_the_account(answer, problem, tmp_path):
    problems, calls = fake_preflight(answers={"claude auth": answer}, tmp_path=tmp_path)
    assert problems == ([] if problem is None else [problem])
    assert auto.CLAUDE_AUTH_STATUS == ("claude", "--setting-sources", "", "auth", "status", "--json")
    assert auto.CLAUDE_AUTH_STATUS in [call["command"] for call in calls], "it reads no settings file, as the session"
    assert not any("someone@example.com" in text for text in problems), "neither the account nor stderr is printed"


@pytest.mark.parametrize("flag", sorted(auto.CLOUD_SIGN_IN))
def test_a_cloud_sign_in_isnt_asked_of_claude_auth_status(flag, tmp_path):
    problems, calls = fake_preflight(answers={"claude auth": (0, '{"loggedIn": false}', "")}, tmp_path=tmp_path,
                                     environ={"PATH": "/bin", flag: "1"})
    assert problems == []
    assert auto.CLAUDE_AUTH_STATUS not in [call["command"] for call in calls]


def test_a_cloud_flag_thats_off_still_asks_claude_auth_status(tmp_path):
    problems, _ = fake_preflight(answers={"claude auth": (0, '{"loggedIn": false}', "")}, tmp_path=tmp_path,
                                 environ={"PATH": "/bin", "CLAUDE_CODE_USE_BEDROCK": "0"})
    assert problems == [NOT_SIGNED_IN]


def test_a_claude_that_doesnt_run_isnt_asked_whether_it_is_signed_in(tmp_path):
    problems, calls = fake_preflight(answers={"claude": (127, "", "No such file")}, tmp_path=tmp_path)
    assert problems == ["claude --version failed (exit 127): No such file"]
    assert auto.CLAUDE_AUTH_STATUS not in [call["command"] for call in calls]


def test_preflight_names_an_sdk_it_cant_check(tmp_path, monkeypatch):
    def no_room(*args, **kwargs):
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(auto.tempfile, "TemporaryDirectory", no_room)
    problems, _ = fake_preflight(tmp_path=tmp_path)
    assert problems == ["can't check the .NET SDK (no temporary folder to check it in): [Errno 28] No space left on device"]


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
    assert fake_preflight(on_path=auto.TOOLS + auto.SANDBOX_TOOLS + ("uvx",), global_json=None, tmp_path=tmp_path)[0] == []
    problems, _ = fake_preflight(on_path=auto.TOOLS + auto.SANDBOX_TOOLS + ("uvx",), tmp_path=tmp_path)
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


def test_review_worktree_of_a_linked_worktree_is_beside_the_primary_checkout(tmp_path):
    git = git_repo(tmp_path / "TicketManager")
    git("worktree", "add", "-q", "-b", "elsewhere", str(tmp_path / "other" / "tm-feature"))
    expected = str(tmp_path / "TicketManager-worktrees" / "chore-reapply-baseline")
    assert auto.review_worktree(str(tmp_path / "other" / "tm-feature")) == expected, "as reapply.sh names it"
    assert auto.review_worktree(str(tmp_path / "TicketManager")) == expected
    assert auto.review_worktree("/c", read=lambda command: "/x/Repo/.git") == "/x/Repo-worktrees/chore-reapply-baseline"


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
    def __init__(self, code=0, raises=None, pid=None):
        self.code, self.raises, self.pid, self.waits = code, raises, pid or 999999, []

    def wait(self, timeout=None):
        self.waits.append(timeout)
        if self.raises is not None and timeout is not None:
            raise self.raises
        return self.code


def fake_guard(tmp_path):
    common = tmp_path / "clone" / ".git"
    common.mkdir(parents=True, exist_ok=True)
    return auto.Guard(worktree="w", remote_branch="", template={}, ledgers=str(common / "review"), ledgers_before={},
                      common=str(common), gitdir=str(common / "worktrees" / "chore-reapply-baseline"), link=b"gitdir: x\n",
                      security=str(tmp_path / "clone" / ".security"), security_before={}, worktree_state=("headsha", "branchname", ""),
                      files=auto.tree_state("w"), git_files=auto.git_files_state(str(common), str(common / "worktrees")))


SKILL_TEXT = "# Pre-push review\n\nThe skill's own steps.\n"
AGENTS = {"pre-push-reviewer": {"description": "d", "prompt": "p", "tools": ["Read"]}}


# The session's settings list no real folder in these tests: the real /, home and /tmp differ between hosts and change
# while they run. Nor do they read git's config.
NO_LISTING = {"listdir": lambda folder: [], "which": lambda name: None, "secrets": lambda worktree, common: []}


def settings_of(*args, **kwargs):
    """auto.session_settings with NO_LISTING's listdir and which unless a test gives its own."""
    return auto.session_settings(*args, **{"listdir": NO_LISTING["listdir"], "which": NO_LISTING["which"], **kwargs})


HELD = ["/dev/shm"]


def review_once(tmp_path, monkeypatch, popen=None, changes=None, worktree=None, session=None, end=None, broke=(),
                clock=None, guard=None, record=None, check=None, listing=None):
    seen = {}

    def fake_record(worktree, run, out, head, keep, got, unseen):
        seen["recorded"] = (worktree, run, os.path.isdir(run))
        seen["record_head"], seen["keep"], seen["got"], seen["unseen"] = head, keep, got, unseen()
        return 0, []

    session = session or FakeSession()
    ended = seen.setdefault("ended", [])

    def fake_popen(command, **kwargs):
        seen.update(command=command, **kwargs)
        seen["settings"] = json.loads(command[command.index("--settings") + 1])
        seen["tmp"] = os.path.dirname(kwargs["env"]["TMPDIR"])
        seen["gh_config"] = sorted(os.listdir(kwargs["env"]["GH_CONFIG_DIR"]))
        seen["folders"] = {name: os.path.lexists(tmp_path / "clone" / name) for name in (".git/review", ".security")}
        run = os.path.join(seen["tmp"], "run")
        seen["run"] = (run, os.path.isdir(run) and stat.S_IMODE(os.stat(run).st_mode))
        kwargs["stdout"].write("the session's own output\n")
        return session

    monkeypatch.setenv("GATE_SKIP_REVIEW", "1")
    if worktree is None:
        worktree = tmp_path / "TicketManager-worktrees" / "chore-reapply-baseline"
        worktree.mkdir(parents=True)
    code, log, seen["broke"] = auto.run_review(TM, str(worktree), popen=popen or fake_popen,
                                   changes=changes or (lambda path: [".github/hooks/pre-push", "scripts/gate.sh"]),
                                   log_dir=str(tmp_path / "logs"), session_dir=str(tmp_path / "sessions"),
                                   end=end or (lambda s: ended.append(s) or True),
                                   guard=guard or (lambda w: seen.setdefault("guarded", w) and fake_guard(tmp_path)),
                                   check=check or (lambda g: list(broke)), skill=lambda: SKILL_TEXT, agents=lambda: AGENTS,
                                   record=record or fake_record, **{**NO_LISTING, **(listing or {})},
                                   **({"clock": clock} if clock else {}))
    return code, log, seen, str(worktree)


def test_the_review_session_runs_sandboxed_in_the_worktree_with_the_templates_skill(tmp_path, monkeypatch):
    code, log, seen, worktree = review_once(tmp_path, monkeypatch)
    assert code == 0
    command = seen["command"]
    assert command[:2] == ["claude", "-p"]
    def flag(name):
        return command[command.index(name) + 1]

    assert flag("--permission-mode") == "dontAsk", "anything not allowed is refused"
    assert flag("--setting-sources") == "", "no settings file is loaded: neither the repository's nor the person's"
    assert flag("--tools") == ",".join(auto.SESSION_BUILTINS), "only these built-in tools exist in the session"
    assert "--strict-mcp-config" in command and "--mcp-config" not in command, "no MCP server"
    assert json.loads(flag("--agents")) == AGENTS
    assert "--add-dir" not in command and "--bare" not in command
    assert command[command.index("--disallowedTools") + 1:] == list(auto.DENIED_TOOLS), \
        "the deny rules are passed on the command line too, last, so the variadic flag takes nothing else"
    clone = tmp_path / "clone" / ".git"
    assert seen["settings"] == settings_of(worktree, str(clone), str(clone / "worktrees" / "chore-reapply-baseline"),
                                                     seen["tmp"]), "the settings are built from the guard's folders"
    assert os.path.dirname(seen["tmp"]) == str(tmp_path / "sessions"), "in home, not /tmp, which the sandbox denies"
    assert os.path.basename(seen["tmp"]).startswith("rollout-session.")
    assert stat.S_IMODE(os.stat(tmp_path / "sessions").st_mode) == 0o700
    assert seen["folders"] == {".git/review": False, ".security": False}, \
        "the session writes neither the ledgers nor .security, so neither is made for it"
    run = os.path.join(seen["tmp"], "run")
    assert seen["run"] == (run, 0o700), "its run folder is made for it, the person's own, in its temporary folder"
    assert seen["settings"]["sandbox"]["filesystem"]["allowWrite"] == [seen["tmp"]]
    assert seen["recorded"] == (worktree, run, True), "once it exits 0 having broken no rule, the round records its run"
    assert seen["pass_fds"] == (), "no lock held here, so none is passed on"
    assert seen["cwd"] == worktree and seen["start_new_session"] is True
    assert seen["stdin"] is subprocess.DEVNULL and seen["stderr"] is subprocess.STDOUT
    assert "the session's own output" in Path(log).read_text(), "the session's output is the log"
    assert len(seen["ended"]) == 1, "its group is ended when it exits normally"
    assert seen["guarded"] == worktree, "what the session may not change is read before it starts"
    env = seen["env"]
    assert "GATE_SKIP_REVIEW" not in env and env["PATH"] == os.environ["PATH"] and env["HOME"] == os.environ["HOME"]
    assert seen["gh_config"] == [], "gh gets an empty config: signed out"
    prompt = command[2]
    assert prompt.endswith(SKILL_TEXT), "the skill's text is the Template's, carried in the prompt"
    assert worktree in prompt and TM in prompt and ".github/hooks/pre-push, scripts/gate.sh" in prompt
    assert os.path.join(worktree, ".claude", "skills", "pre-push-review") in prompt
    for rule in ("never ask one", "Review only: this session changes nothing in the repository",
                 "Don't edit, fix or commit anything, and don't run the gate", "the skill's last, review-only round",
                 "stage open every finding the skill's triage doesn't dismiss, with its suggested fix", "Never dismiss a security finding",
                 "a repeat of a fixed .security entry too, with that entry's number in the summary. Leave out every finding's"
                 " repeats field, whatever entry it would name (fixed, open or accepted): rollout.sh drops it",
                 f"Your run folder is {run}, made for you", "write nothing anywhere else",
                 "its review ledgers and the .security record are read-only to you",
                 f"scripts/review.sh stage {run} <file>", "as an empty array [] when it keeps none",
                 "don't run scripts/review.sh record", "rollout.sh records what you staged once the session ends",
                 "what to change in the Factory's template/owned/", "Don't push",
                 "don't file the follow-up issue", "don't edit a PR", "skip the skill's GitHub reads",
                 "and its git fetch", "go on with origin/main as it is", "no network",
                 "End with a list of the findings you staged"):
        assert rule in prompt, rule


@pytest.mark.parametrize("session, broke", [(FakeSession(code=1), ()), (FakeSession(code=0), ("a rule",))])
def test_a_session_that_failed_or_broke_a_rule_is_never_recorded(tmp_path, monkeypatch, session, broke):
    _, _, seen, _ = review_once(tmp_path, monkeypatch, session=session, broke=broke)
    assert "recorded" not in seen


def test_a_session_whose_group_survived_is_never_recorded(tmp_path, monkeypatch):
    _, _, seen, _ = review_once(tmp_path, monkeypatch, end=lambda s: False)
    assert "recorded" not in seen


@pytest.mark.parametrize("outcome, code", [((auto.RECORD_FAILED, []), auto.RECORD_FAILED), ((0, []), 0),
                                           ((auto.SESSION_BROKE_A_RULE, ["R2 was staged fixed"]), auto.SESSION_BROKE_A_RULE)])
def test_the_records_outcome_is_the_reviews(tmp_path, monkeypatch, outcome, code):
    got, log, seen, _ = review_once(tmp_path, monkeypatch, record=lambda *args: outcome)
    assert got == code and seen["broke"] == outcome[1]
    for rule in outcome[1]:
        assert f"rollout.sh: the session broke a rule: {rule}" in Path(log).read_text()


def test_a_failed_record_naming_a_group_prepare_left_is_no_broken_rule(tmp_path, monkeypatch):
    left = ["what review.sh prepare left ..."]
    code, log, seen, _ = review_once(tmp_path, monkeypatch, record=lambda *args: (auto.RECORD_FAILED, left))
    assert code == auto.RECORD_FAILED and seen["broke"] == left
    assert "broke a rule" not in Path(log).read_text()


@pytest.mark.parametrize("error", [OSError("no space"), PermissionError(13, "Permission denied"),
                                   subprocess.SubprocessError("Exception occurred in preexec_fn.")])
def test_a_record_that_cant_run_is_record_failed_with_its_reason_in_the_log(tmp_path, monkeypatch, error):
    def record(*args):
        raise error

    code, log, seen, _ = review_once(tmp_path, monkeypatch, record=record)
    assert code == auto.RECORD_FAILED and seen["broke"] == []
    assert f"rollout.sh: can't record the review: {type(error).__name__}: {error}" in Path(log).read_text()


def test_the_record_is_given_the_head_the_guard_read_and_a_folder_beside_the_log(tmp_path, monkeypatch):
    _, log, seen, _ = review_once(tmp_path, monkeypatch)
    assert seen["record_head"] == "headsha", "the guard's HEAD (not its branch), read before the session"
    assert seen["keep"] == f"{log}.staged" == auto.staged_copy(log)
    assert seen["got"] == [], "the round's deferred signals, so a signal can stop prepare"
    assert seen["unseen"] == [], "the guard's own unseen_changes, asked again just before record runs"


def test_the_record_asks_the_guards_own_snapshot_again(tmp_path, monkeypatch):
    def guard(worktree):
        return dataclasses.replace(fake_guard(tmp_path), files={".": ("dir", 0o40755)})

    _, _, seen, worktree = review_once(tmp_path, monkeypatch, guard=guard)
    assert len(seen["unseen"]) == 1 and seen["unseen"][0].startswith("1 path(s) in w changed during the session")


def test_a_signal_during_the_checks_is_held_until_the_findings_are_kept_and_the_command_is_printed(
        tmp_path, monkeypatch, capsys):
    seen = {}

    def check(guard):
        os.kill(os.getpid(), signal.SIGINT)
        time.sleep(0.2)
        return []

    def record(worktree, run, out, head, keep, got, unseen):
        seen["got"] = list(got)
        auto.write_staged(keep, "[]", head)
        return auto.RECORD_FAILED, []

    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, check=check, record=record, guard=head_guard(tmp_path))
    assert seen["got"] == [signal.SIGINT], "the record is still run, told of the signal, so it keeps the findings"
    log = str(next((tmp_path / "logs").glob("*.log")))
    keep = auto.staged_copy(log)
    worktree = str(tmp_path / "TicketManager-worktrees" / "chore-reapply-baseline")
    assert (f"rollout.sh: what the session staged is kept in {keep} (see {log}); record it with:"
            f" {auto.record_command(worktree, keep, HEAD)}\n") in capsys.readouterr().err


def head_guard(tmp_path):
    def guard(worktree):
        return dataclasses.replace(fake_guard(tmp_path), worktree_state=(HEAD, "branchname", ""))
    return guard


def interrupt():
    os.kill(os.getpid(), signal.SIGINT)
    time.sleep(0.2)


def test_a_signal_between_the_session_and_the_checks_is_held_too(tmp_path, monkeypatch, capsys):
    real = auto.staged_copy

    def staged_copy(log):
        interrupt()  # the first thing after the session's group is gone
        return real(log)

    def record(worktree, run, out, head, keep, got, unseen):
        auto.write_staged(keep, "[]", head)
        return auto.RECORD_FAILED, []

    monkeypatch.setattr(auto, "staged_copy", staged_copy)
    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, record=record, guard=head_guard(tmp_path))
    assert "what the session staged is kept in" in capsys.readouterr().err, "held, so the findings were kept"


def test_a_signal_after_a_record_that_left_its_group_says_to_check_the_report_first(tmp_path, monkeypatch, capsys):
    def record(worktree, run, out, head, keep, got, unseen):
        auto.write_staged(keep, "[]", head)
        interrupt()
        raise auto.GroupLeft("what review.sh record left ...")

    worktree = tmp_path / "Ticket Manager" / "chore-reapply-baseline"
    worktree.mkdir(parents=True)
    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, record=record, guard=head_guard(tmp_path), worktree=worktree)
    err = capsys.readouterr().err
    assert err.count("rollout.sh: what review.sh record left ...\n") == 1, "said once, not again as a failed record's"
    assert (f"whether the review was recorded can't be told: cd '{worktree}' && scripts/review.sh report shows it;"
            " if it wasn't, record it with: ") in err


def test_a_signal_after_a_prepare_that_left_its_group_names_the_group(tmp_path, monkeypatch, capsys):
    def record(worktree, run, out, head, keep, got, unseen):
        auto.write_staged(keep, "[]", head)
        interrupt()
        return auto.RECORD_FAILED, ["what review.sh prepare left ...: see it with pgrep -l -g 7"]

    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, record=record, guard=head_guard(tmp_path))
    err = capsys.readouterr().err
    assert err.index("rollout.sh: what review.sh prepare left ...: see it with pgrep -l -g 7\n") \
        < err.index("what the session staged is kept in"), "the group, which holds the lock, comes first"


def test_a_signal_with_a_broken_rule_prints_no_rule_on_the_terminal(tmp_path, monkeypatch, capsys):
    def record(worktree, run, out, head, keep, got, unseen):
        interrupt()
        return auto.SESSION_BROKE_A_RULE, ["R2 was staged fixed"]

    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, record=record, guard=head_guard(tmp_path))
    assert "R2 was staged fixed" not in capsys.readouterr().err, "only a group a failed record names is written there"


def test_a_signal_with_another_reviews_findings_kept_prints_no_command(tmp_path, monkeypatch, capsys):
    def record(worktree, run, out, head, keep, got, unseen):
        auto.write_staged(keep, "[]", "f" * 40)
        interrupt()
        return auto.RECORD_FAILED, []

    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, record=record, guard=head_guard(tmp_path))
    assert "is kept in" not in capsys.readouterr().err


def test_a_signal_with_nothing_kept_prints_no_command(tmp_path, monkeypatch, capsys):
    def record(*args):
        os.kill(os.getpid(), signal.SIGINT)
        time.sleep(0.2)
        return 0, []

    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, record=record)
    assert "is kept in" not in capsys.readouterr().err


def test_the_record_start_is_said_on_the_terminal(tmp_path, monkeypatch, capsys):
    review_once(tmp_path, monkeypatch)
    assert capsys.readouterr().err == ("rollout.sh: recording the review (scripts/review.sh prepare, then record, up to"
                                       " 10 minutes each: an interrupt stops prepare, and waits for record to end)\n")


def test_a_group_the_record_left_is_its_own_stop_since_it_may_have_recorded(tmp_path, monkeypatch, capsys):
    def record(*args):
        raise auto.GroupLeft("what review.sh record left ...")

    code, log, seen, _ = review_once(tmp_path, monkeypatch, record=record)
    assert code == auto.RECORD_LEFT and seen["broke"] == ["what review.sh record left ..."]
    text = Path(log).read_text()
    assert "rollout.sh: what review.sh record left ...\n" in text and "broke a rule" not in text
    assert "what review.sh record left" not in capsys.readouterr().err, "no signal: the stop says it"

    def interrupted(*args):
        os.kill(os.getpid(), signal.SIGINT)
        time.sleep(0.2)
        raise auto.GroupLeft("what review.sh record left ...")

    (tmp_path / "again").mkdir()
    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path / "again", monkeypatch, record=interrupted)
    assert "rollout.sh: what review.sh record left ...\n" in capsys.readouterr().err, \
        "the signal wins, and the person still hears of the group"


def test_a_bug_while_recording_is_record_failed_with_its_traceback_and_the_signal_still_passed_on(tmp_path, monkeypatch):
    def record(*args):
        raise ValueError("a bug")

    code, log, seen, _ = review_once(tmp_path, monkeypatch, record=record)
    assert code == auto.RECORD_FAILED and seen["broke"] == []
    assert "rollout.sh: recording the review failed:\nTraceback" in Path(log).read_text()

    def interrupted(*args):
        os.kill(os.getpid(), signal.SIGINT)
        time.sleep(0.2)
        raise ValueError("a bug")

    (tmp_path / "again").mkdir()
    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path / "again", monkeypatch, record=interrupted)


def test_a_check_that_fails_is_a_broken_rule_not_a_crash(tmp_path, monkeypatch):
    def check(guard):
        raise ValueError("a bug")

    code, log, seen, _ = review_once(tmp_path, monkeypatch, check=check)
    assert code == auto.SESSION_BROKE_A_RULE and "recorded" not in seen
    assert seen["broke"] == ["the checks after the session failed (ValueError: a bug; the traceback is in the log), so"
                             " what it did can't be told"]
    assert "Traceback" in Path(log).read_text()


def test_the_record_runs_with_the_signals_held_and_passes_one_on_after(tmp_path, monkeypatch):
    def record(worktree, run, out, *rest):
        os.kill(os.getpid(), signal.SIGINT)
        time.sleep(0.2)
        out.write("recorded whole\n")
        return 0, []

    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, record=record)
    log = next((tmp_path / "logs").iterdir())
    assert "recorded whole" in log.read_text()


def test_the_sessions_folders_and_settings_are_gone_after_it(tmp_path, monkeypatch):
    _, _, seen, _ = review_once(tmp_path, monkeypatch)
    assert not os.path.exists(seen["env"]["GH_CONFIG_DIR"]) and not os.path.exists(seen["env"]["TMPDIR"])


ROOT = {"/": ["bin", "c", "etc", "h", "home", "opt", "t", "tmp", "usr", "w"], "/c": ["clone", "other"],
        "/c/clone": [".git", ".security", "src"], "/t": ["session", "y"], "/w": ["wt", "x"]}


def test_the_session_settings_sandbox_its_bash_and_fence_its_file_tools():
    home, common = "/h", "/c/clone/.git"
    gitdir = os.path.join(common, "worktrees", "wt")
    settings = settings_of("/w/wt", common, gitdir, "/t/session", home=home, config_dir="", environ={},
                           listdir=lambda folder: ROOT.get(folder, []))
    sandbox = settings["sandbox"]
    assert (sandbox["enabled"], sandbox["failIfUnavailable"], sandbox["allowUnsandboxedCommands"],
            sandbox["excludedCommands"]) == (True, True, False, [])
    security = "/c/clone/.security"
    credentials = [home + path[1:] for path in auto.CREDENTIAL_PATHS]
    assert sandbox["filesystem"] == {
        "allowWrite": ["/t/session"],
        "denyWrite": ["/w/wt", gitdir],
        "denyRead": credentials + ["/c/clone/src", "/c/other", home, "/home", "/opt", "/t/y", "/tmp", "/w/x", *HELD],
    }, ("it writes only its own folder: not the worktree, its working folder, its git folder, the ledgers or .security;"
        " of the person's data it reads only the worktree, the clone's git folder, .security and its own folder")
    assert sandbox["network"] == {"allowedDomains": list(auto.SANDBOX_DOMAINS)}
    permissions = settings["permissions"]
    assert permissions["allow"] == [*auto.SESSION_TOOLS, "Read(//w/wt/**)", f"Read(/{security}/**)", "Read(//t/session/**)",
                                    "Edit(//t/session/**)"], "it edits its own folder alone"
    assert not {"Read", "Glob", "Grep"} & set(permissions["allow"]), "reads are allowed by path only"
    deny = permissions["deny"]
    assert deny[:len(auto.DENIED_TOOLS)] == list(auto.DENIED_TOOLS)
    for path in credentials:
        assert f"Read(/{path})" in deny and f"Read(/{path}/**)" in deny
    for path in ("/w/wt", gitdir):
        assert f"Edit(/{path})" in deny and f"Edit(/{path}/**)" in deny
    assert not any(rule.startswith("Write(") for rule in permissions["allow"] + deny), "Edit rules cover every file-editing tool"


TREE = {"/": ["h", "usr"], "/h": ["github", ".cache", ".config", ".ssh", "notes.txt"],
        "/h/github": ["App", "App-worktrees", "Other"], "/h/github/App": [".git", ".security", "src"],
        "/h/github/App-worktrees": ["wt", "old"], "/h/.cache": ["repo-ci-baseline", "sessions"],
        "/h/.cache/sessions": ["mine", "theirs"]}


def fake_listdir(folder):
    if folder not in TREE:
        raise OSError(2, "No such file or directory", folder)
    return TREE[folder]


def test_denied_around_hides_everything_beside_the_way_to_what_is_allowed():
    allowed = ["/h/github/App-worktrees/wt", "/h/github/App/.git", "/h/github/App/.security"]
    assert auto.denied_around("/h", allowed, fake_listdir) == [
        "/h/.cache", "/h/.config", "/h/.ssh", "/h/github/App/src", "/h/github/App-worktrees/old", "/h/github/Other",
        "/h/notes.txt"]
    assert auto.denied_around("/tmp", allowed, fake_listdir) == ["/tmp"], "a root holding nothing allowed: whole"
    assert auto.denied_around("/h/github/App-worktrees/wt", allowed, fake_listdir) == [], "an allowed root is read"


def test_denied_around_stops_at_a_folder_on_the_way_it_cant_list():
    with pytest.raises(OSError, match=r"can't list /h/unlisted, which is on the way to the session's folders"):
        auto.denied_around("/h/unlisted", ["/h/unlisted/wt"], fake_listdir)
    assert auto.denied_around("/h", ["/h/github/App"], fake_listdir)[-1] == "/h/notes.txt", \
        "a folder holding nothing allowed is denied whole, unlisted"


def test_denied_around_takes_a_trailing_slash_and_a_name_that_only_starts_alike():
    assert auto.denied_around("/h/github/", ["/h/github/App/"], fake_listdir) == [
        "/h/github/App-worktrees", "/h/github/Other"], "App-worktrees isn't in App, and an allowed folder is read whole"
    assert auto.denied_around("/h/github", ["/h/github/App-worktrees/wt"], fake_listdir) == [
        "/h/github/App", "/h/github/App-worktrees/old", "/h/github/Other"], "App doesn't hold App-worktrees: denied whole"


@pytest.mark.parametrize("name", ["a[1].txt", "notes?.md", "b*", "{x}"])
def test_denied_around_refuses_an_entry_named_as_a_pattern(name):
    tree = {"/h": [name, "wt"]}
    with pytest.raises(ValueError, match="which the sandbox may read as a pattern: rename or move it"):
        auto.denied_around("/h", ["/h/wt"], tree.get)
    tree = {"/h": ["my[app]"], "/h/my[app]": ["wt"]}
    assert auto.denied_around("/h", ["/h/my[app]/wt"], tree.get) == [], "nothing beside the way: nothing to name"
    tree = {"/h": ["my[app]"], "/h/my[app]": ["wt", "other"]}
    with pytest.raises(ValueError, match=r"'/h/my\[app\]/other' must be hidden from the session, but the folder it is"
                                         r" in, '/h/my\[app\]', holds one of .*: rename that folder, or move the clone"
                                         r" out of it, then resume"):
        auto.denied_around("/h", ["/h/my[app]/wt"], tree.get)


@pytest.mark.parametrize("character", sorted("*?[]{}"))
def test_each_glob_character_is_refused_alone(character):
    assert auto.GLOB_CHARACTERS == frozenset("*?[]{}")
    with pytest.raises(ValueError, match="which the sandbox may read as a pattern"):
        auto.denied_around("/h", ["/h/wt"], {"/h": [f"a{character}b", "wt"]}.get)


def test_denied_around_refuses_a_name_that_isnt_utf_8():
    name = b"caf\xe9".decode("utf-8", "surrogateescape")
    with pytest.raises(ValueError, match="isn't UTF-8, so the sandbox can't name it"):
        auto.denied_around("/h", ["/h/wt"], {"/h": [name, "wt"]}.get)
    with pytest.raises(ValueError, match="but the folder it is in, '/h/caf.udce9', isn't UTF-8"):
        auto.denied_around("/h", [f"/h/{name}/wt"], {"/h": [name], f"/h/{name}": ["wt", "other"]}.get)


def test_denied_around_hides_both_sides_of_a_symlinked_folder(tmp_path):
    real = tmp_path / "data" / "github"
    for folder in ("App-worktrees/wt", "App/.git", "App/.security", "Other"):
        (real / folder).mkdir(parents=True)
    (real / "Other" / ".env").write_text("SECRET=1\n")
    (tmp_path / "home").mkdir()
    (tmp_path / "home" / "github").symlink_to(real)
    (tmp_path / "home" / "notes.txt").write_text("x")
    link = tmp_path / "home" / "github"
    reads = auto.session_reads(str(link / "App-worktrees" / "wt"), str(link / "App" / ".git"),
                               str(link / "App" / ".security"), str(tmp_path / "home" / "session"))
    (tmp_path / "home" / "session").mkdir()
    denied = auto.denied_around(str(tmp_path), reads, os.listdir)
    assert str(link / "Other") in denied and str(real / "Other") in denied, "by the link and where it leads"
    assert str(tmp_path / "home" / "notes.txt") in denied
    assert not [path for path in denied if path.startswith(str(real / "App-worktrees" / "wt"))]


def test_the_sessions_bash_reads_only_its_folders_of_home():
    settings = settings_of("/h/github/App-worktrees/wt", "/h/github/App/.git", "/h/github/App/.git/worktrees/wt",
                           "/h/.cache/sessions/mine", home="/h", environ={}, listdir=fake_listdir)
    denied = settings["sandbox"]["filesystem"]["denyRead"]
    for path in ("/h/.config", "/h/notes.txt", "/h/github/Other", "/h/github/App/src", "/h/github/App-worktrees/old",
                 "/h/.cache/sessions/theirs", *HELD):
        assert path in denied, path
    for path in ("/", "/h", "/h/github", "/h/github/App-worktrees/wt", "/h/github/App/.git", "/h/github/App/.security",
                 "/h/.cache/sessions/mine", "/usr"):
        assert path not in denied, path
    assert denied.count("/h/.ssh") == 1 and denied.count("/h/.cache/repo-ci-baseline") == 1, \
        "a listed entry that is a credential is denied once, as a credential"
    assert len(denied) == len(set(denied))
    assert "allowRead" not in settings["sandbox"]["filesystem"], "bubblewrap can't mask files in a re-opened worktree"


CLONE_OUTSIDE_HOME = {"/": ["h", "home", "opt", "run", "srv", "tmp", "usr"], "/home": ["someone"],
                      "/run": ["lock", "user"],
                      "/srv": ["code"], "/srv/code": ["App", "App-worktrees", "Other"],
                      "/srv/code/App": [".git", ".security"], "/srv/code/App-worktrees": ["wt"],
                      "/h": [".cache"], "/h/.cache": ["sessions"], "/h/.cache/sessions": ["mine"]}


def test_a_clone_outside_home_keeps_the_applications_beside_it_hidden():
    settings = settings_of("/srv/code/App-worktrees/wt", "/srv/code/App/.git", "/srv/code/App/.git/worktrees/wt",
                           "/h/.cache/sessions/mine", home="/h", environ={}, listdir=CLONE_OUTSIDE_HOME.get)
    denied = settings["sandbox"]["filesystem"]["denyRead"]
    for path in ("/srv/code/Other", "/home", "/opt", "/tmp", "/run/user", "/run/lock"):
        assert path in denied, path
    for path in ("/run", "/usr", "/srv/code/App-worktrees/wt", "/srv/code/App/.git"):
        assert path not in denied, path


def test_the_system_folders_are_read_and_the_rest_of_run_and_dev_shm_denied():
    assert auto.SYSTEM_READS == ("/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/etc", "/proc", "/dev",
                                 "/sys", "/nix", "/run/current-system", "/run/booted-system", "/run/wrappers")
    assert auto.SYSTEM_DENIED == ("/dev/shm",)
    names = ["bin", "dev", "etc", "h", "lib", "lib32", "lib64", "libx32", "nix", "proc", "run", "sbin", "sys", "usr"]
    tree = {"/": names, "/h": ["wt"],
            "/run": ["credentials", "current-system", "booted-system", "docker.sock", "media", "secrets", "user",
                     "wrappers"]}
    denied = settings_of("/h/wt", "/h/wt/.git", "/h/wt/.git", "/h/s", home="/h", environ={},
                         listdir=tree.get)["sandbox"]["filesystem"]["denyRead"]
    for path in ("/run/credentials", "/run/docker.sock", "/run/media", "/run/secrets", "/run/user", "/dev/shm"):
        assert path in denied, path
    for path in [f"/{name}" for name in names if name not in ("h", "run")] + [
            "/run/current-system", "/run/booted-system", "/run/wrappers", "/dev"]:
        assert path not in denied, path


def test_a_clone_on_a_mounted_drive_or_in_shared_memory_stays_readable():
    tree = {"/": ["dev", "h", "run"], "/run": ["media", "user"], "/run/media": ["u"], "/run/media/u": ["disk", "other"],
            "/run/media/u/disk": ["App", "App-worktrees"], "/run/media/u/disk/App": [".git", ".security"],
            "/run/media/u/disk/App-worktrees": ["wt"], "/h": ["s"]}
    denied = settings_of("/run/media/u/disk/App-worktrees/wt", "/run/media/u/disk/App/.git",
                         "/run/media/u/disk/App/.git/worktrees/wt", "/h/s", home="/h", environ={},
                         listdir=tree.get)["sandbox"]["filesystem"]["denyRead"]
    assert "/run/media/u/other" in denied and "/run/user" in denied
    assert not [path for path in denied if "/run/media/u/disk/App" in path or path in ("/run", "/run/media")]
    shm = {"/dev/shm": ["work", "theirs"], "/dev/shm/work": ["App"], "/dev/shm/work/App": [".git", "wt"]}
    reads = ["/dev/shm/work/App/wt", "/dev/shm/work/App/.git"]
    found = auto.read_denials(reads, [], "/h", listdir=lambda folder: shm.get(folder, []), which=lambda name: None)
    assert "/dev/shm/theirs" in found and "/dev/shm" not in found and "/dev/shm/work/App/wt" not in found


def test_the_sessions_bash_reads_its_folders_where_their_symlinks_lead_too(tmp_path):
    real = tmp_path / "real-worktree"
    real.mkdir()
    (tmp_path / "wt").symlink_to(real)
    assert auto.session_reads(str(tmp_path / "wt"), "/c/.git", "/c/.security", "/t") == [
        str(real), str(tmp_path / "wt"), "/c/.git", "/c/.security", "/t"]


def no_answer(command):
    return 127, "", "not run"


def test_a_tools_own_install_prefix_is_read_and_put_first_on_path():
    def which(name):
        return {"node": "/h/.nvm/versions/node/v24/bin/node"}.get(name)

    assert auto.tool_reads("/h", which, no_answer) == (
        ["/h/.nvm/versions/node/v24/bin/node", "/h/.nvm/versions/node/v24"], ["/h/.nvm/versions/node/v24/bin"])
    tree = {"/": ["h"], "/h": [".nvm", "wt"], "/h/.nvm": ["versions"], "/h/.nvm/versions": ["node"],
            "/h/.nvm/versions/node": ["v23", "v24"], "/h/.nvm/versions/node/v24": ["bin", "include", "lib"]}
    settings = settings_of("/h/wt", "/h/wt/.git", "/h/wt/.git", "/h/s", home="/h", environ={}, listdir=tree.get,
                           which=which)
    denied = settings["sandbox"]["filesystem"]["denyRead"]
    assert "/h/.nvm/versions/node/v23" in denied
    assert not [path for path in denied if path.startswith("/h/.nvm/versions/node/v24")]


def test_a_shim_is_asked_where_its_tool_really_is():
    answers = {"node": "/h/.volta/tools/image/node/24.1.0/bin/node\n", "--exec-path": "/usr/lib/git-core\n"}

    def run(command):
        return 0, answers["node" if command[1:] == ["-p", "process.execPath"] else command[-1]], ""

    reads, first = auto.tool_reads("/h", {"node": "/h/.volta/bin/node", "git": "/usr/bin/git"}.get, run)
    assert reads == ["/h/.volta/bin/node", "/h/.volta/tools/image/node/24.1.0/bin/node", "/h/.volta/tools/image/node/24.1.0",
                     "/usr/bin/git", "/usr/lib/git-core"], "the shim, the real binary and its prefix, git's helpers"
    assert first == ["/h/.volta/tools/image/node/24.1.0/bin"], "the real node runs, not the shim the sandbox can't read"
    assert auto.tool_reads("/h", {"node": "/h/.volta/bin/node"}.get, lambda command: (0, "relative/node\n", ""))[0] == [
        "/h/.volta/bin/node"], "an answer that isn't a path is no read, and the shim's own ~/.volta is no version folder"


def brew_parts(prefix):
    return [f"{form}/{part}" for form in auto.both_forms(prefix) for part in auto.HOMEBREW_PARTS]


def test_a_homebrew_install_reads_its_formulaes_folders_not_its_etc_or_var(tmp_path, monkeypatch):
    home = tmp_path / "me"
    cellar = home / ".linuxbrew" / "Cellar" / "git" / "2.50" / "bin"
    cellar.mkdir(parents=True)
    (cellar / "git").write_text("")
    (home / ".linuxbrew" / "bin").mkdir()
    (home / ".linuxbrew" / "bin" / "git").symlink_to(cellar / "git")
    reads, first = auto.tool_reads(str(home), lambda name: str(home / ".linuxbrew" / "bin" / name) if name == "git"
                                   else None, no_answer)
    assert reads == [str(home / ".linuxbrew" / "bin" / "git"), str(cellar / "git"), *brew_parts(str(home / ".linuxbrew"))], \
        "the formulae's libraries are in its opt and Cellar, git's system config in etc/gitconfig"
    assert first == [str(cellar)]
    assert auto.HOMEBREW_PREFIXES == ("/home/linuxbrew/.linuxbrew",)
    assert auto.HOMEBREW_PARTS == ("bin", "sbin", "Cellar", "opt", "lib", "libexec", "share", "include", "etc/gitconfig")
    # The literal paths below stand for a host whose /home is no link, whatever this one's is; a linked /home has
    # test_a_tools_install_is_found_through_a_linked_home_or_prefix.
    monkeypatch.setattr(auto.os.path, "realpath", lambda path, **kwargs: path)
    brew = "/home/linuxbrew/.linuxbrew/Cellar/git/2.50/bin/git"
    assert auto.tool_reads("/h", lambda name: brew if name == "git" else None, no_answer) == (
        [brew, *brew_parts("/home/linuxbrew/.linuxbrew")], ["/home/linuxbrew/.linuxbrew/Cellar/git/2.50/bin"])
    tree = {"/": ["h", "home"], "/h": ["wt"], "/home": ["linuxbrew"], "/home/linuxbrew": [".linuxbrew"],
            "/home/linuxbrew/.linuxbrew": ["Cellar", "etc", "opt", "var"],
            "/home/linuxbrew/.linuxbrew/etc": ["gitconfig", "redis.conf"]}
    denied = settings_of("/h/wt", "/h/wt/.git", "/h/wt/.git", "/h/s", home="/h", environ={}, listdir=tree.get,
                         which=lambda name: brew if name == "git" else None)["sandbox"]["filesystem"]["denyRead"]
    assert "/home/linuxbrew/.linuxbrew/etc/redis.conf" in denied and "/home/linuxbrew/.linuxbrew/var" in denied, \
        "a service's config and data are no formula's"
    assert not {"/home/linuxbrew/.linuxbrew/etc/gitconfig", "/home/linuxbrew/.linuxbrew/Cellar"} & set(denied)
    for found in ("/opt/Cellar/git/2.50/bin/git", "/mnt/data/projects/Cellar/x/1/bin/git",
                  "/home/linuxbrew/.linuxbrew/Cellar/git", "/home/linuxbrew/.linuxbrew/other/Cellar/git/2/bin/git"):
        assert auto.tool_reads("/h", lambda name, found=found: found if name == "git" else None, no_answer) == (
            [found], []), f"{found}: no Homebrew prefix, or not a formula's version folder, so the file alone"


@pytest.mark.parametrize("prefix", [".nvm/versions/node/v24", ".asdf/installs/nodejs/24.1.0",
                                    ".local/share/mise/installs/node/24", ".volta/tools/image/node/24.1.0",
                                    ".local/share/fnm/node-versions/v24/installation", ".fnm/node-versions/v24/installation",
                                    ".local/share/pnpm/nodejs/24.1.0"])
def test_a_version_managers_version_folder_is_read_whole(prefix):
    found = f"/h/{prefix}/bin/node"
    assert auto.tool_reads("/h", lambda name: found if name == "node" else None, no_answer) == (
        [found, f"/h/{prefix}"], [f"/h/{prefix}/bin"])
    assert auto.tool_reads("/home/u", lambda name: found if name == "node" else None, no_answer) == ([found], []), \
        "another user's home: the file alone"


@pytest.mark.parametrize("prefix", [
    ".nvm/versions/node", ".asdf/installs/nodejs", ".asdf/installs/a/b/c", ".local/share/mise/installs/node",
    ".local/share/mise/installs/a/b/c", ".volta/tools/image/node", ".volta/tools/image/a/b/c",
    ".local/share/fnm/node-versions/v24", ".local/share/fnm/node-versions/v24/installation/x",
    ".fnm/node-versions/v24/x", ".local/share/pnpm/nodejs", ".local/share/pnpm/nodejs/24/x",
    "x/.asdf/installs/nodejs/24", "x/.local/share/mise/installs/node/24", "x/.volta/tools/image/node/24",
    "x/.fnm/node-versions/v24/installation", "x/.local/share/pnpm/nodejs/24"])
def test_a_folder_beside_a_version_managers_layout_isnt_read_whole(prefix):
    found = f"/h/{prefix}/bin/node"
    assert auto.tool_reads("/h", lambda name: found if name == "node" else None, no_answer) == ([found], []), \
        "a level short, a level too deep, or not right under home: no version folder"


def test_a_tools_install_is_found_through_a_linked_home_or_prefix(tmp_path, monkeypatch):
    real = tmp_path / "var" / "home"
    shared_git = real / "linuxbrew" / ".linuxbrew" / "Cellar" / "git" / "2" / "bin"
    node = real / "me" / ".nvm" / "versions" / "node" / "v24" / "bin"
    moved = tmp_path / "disk" / "brew" / "Cellar" / "gh" / "1" / "bin"
    for folder in (shared_git, node, moved):
        folder.mkdir(parents=True)
    (shared_git / "git").write_text("")
    (node / "node").write_text("")
    (tmp_path / "home").symlink_to(real)
    link = tmp_path / "home"
    monkeypatch.setattr(auto, "HOMEBREW_PREFIXES", (str(link / "linuxbrew" / ".linuxbrew"),))
    found = {"git": str(link / "linuxbrew" / ".linuxbrew" / "Cellar" / "git" / "2" / "bin" / "git"),
             "node": str(link / "me" / ".nvm" / "versions" / "node" / "v24" / "bin" / "node")}
    reads, first = auto.tool_reads(str(link / "me"), found.get, no_answer)
    assert {f"{link}/linuxbrew/.linuxbrew/Cellar", f"{real}/linuxbrew/.linuxbrew/Cellar"} <= set(reads), \
        "/home a link (Fedora Atomic's /var/home): the prefix matched, and read, in both its forms"
    assert str(real / "me" / ".nvm" / "versions" / "node" / "v24") in reads, "a version folder in a linked home"
    assert {str(shared_git), str(node)} == set(first)
    (real / "me" / ".linuxbrew").symlink_to(tmp_path / "disk" / "brew")
    (moved / "gh").write_text("")
    monkeypatch.setattr(auto, "SESSION_COMMANDS", ("gh",))
    reads, _ = auto.tool_reads(str(real / "me"), lambda name: str(moved / "gh"), no_answer)
    assert f"{tmp_path}/disk/brew/Cellar" in reads and f"{real}/me/.linuxbrew/Cellar" in reads, \
        "a ~/.linuxbrew moved to another disk by a link"


def test_a_tool_in_a_shared_folder_is_read_as_its_file_alone(tmp_path):
    for found in ("/h/.local/bin/git", "/h/bin/git", "/h/git"):
        assert auto.tool_reads("/h", lambda name, found=found: found if name == "git" else None, no_answer) == ([found], []), found
    for found in ("/opt/zz-none/bin/git", "/mnt/data/bin/git", "/home/other/bin/git", "/home/other/a/b/c/bin/git",
                  "/h/tools/bin/git", "/h/a/b/bin/git", "/h/work/clients/acme/bin/node", "/h/.nvm/versions/node/v24/git", "/h/.nvm/versions/node/v24/lib/git",
                  "/h/.nvm/versions/node/v24/x/bin/git", "/h/x/.nvm/versions/node/v24/bin/git"):
        assert auto.tool_reads("/h", lambda name, found=found: found if name == "git" else None, no_answer) == (
            [found], []), f"{found}: no layout known to be the tool's own, so the file alone"
    assert auto.tool_reads("/home/u", lambda name: "/home/bin/git" if name == "git" else None, no_answer) == (
        ["/home/bin/git"], []), "a folder above home: no layout, and the shared filter keeps its file"
    for found in ("/opt/bin/git", "/srv/bin/git"):
        assert auto.tool_reads("/h", lambda name, found=found: found if name == "git" else None, no_answer) == (
            [found], []), "nor a folder right under /, which holds other programs' too"
    tree = {"/": ["h", "opt"], "/h": ["wt"], "/opt": ["bin", "other-app"], "/opt/bin": ["git", "secret-tool"]}
    denied = settings_of("/h/wt", "/h/wt/.git", "/h/wt/.git", "/h/s", home="/h", environ={}, listdir=tree.get,
                         which=lambda name: "/opt/bin/git" if name == "git" else None)["sandbox"]["filesystem"]["denyRead"]
    assert "/opt/other-app" in denied and "/opt/bin/secret-tool" in denied and "/opt/bin/git" not in denied
    assert auto.tool_reads("/h", lambda name: "node_modules/.bin/node", no_answer) == ([], []), \
        "a relative PATH entry is rollout.sh's folder, not the session's"
    (tmp_path / "real").mkdir()
    (tmp_path / "home").symlink_to(tmp_path / "real")
    (tmp_path / "real" / "bin").mkdir()
    (tmp_path / "real" / "bin" / "git").write_text("")
    found = str(tmp_path / "home" / "bin" / "git")
    reads, first = auto.tool_reads(str(tmp_path / "home"), lambda name: found if name == "git" else None, no_answer)
    assert reads == [found, str(tmp_path / "real" / "bin" / "git")] and first == [], "a symlinked home is home too"


def test_the_sessions_path_starts_with_the_folders_its_tools_really_run_from(tmp_path):
    env = auto.session_env({"PATH": "/usr/bin:/bin"}, str(tmp_path), ["/h/.nvm/versions/node/v24/bin"])
    assert env["PATH"] == "/h/.nvm/versions/node/v24/bin:/usr/bin:/bin"
    assert auto.session_env({"PATH": "/usr/bin"}, str(tmp_path))["PATH"] == "/usr/bin"
    assert auto.session_env({}, str(tmp_path), ["/n/bin"])["PATH"] == "/n/bin" + os.pathsep + os.defpath, \
        "no PATH: the system's default after the tools' folders"


def test_a_review_runs_its_tools_from_where_they_really_are_and_asks_them_once(tmp_path, monkeypatch):
    monkeypatch.setenv("HOME", str(tmp_path / "me"))
    calls = []
    real = auto.tool_reads
    monkeypatch.setattr(auto, "tool_reads", lambda *args, **kwargs: calls.append(args) or real(*args, **kwargs))
    node = str(tmp_path / "me" / ".nvm" / "versions" / "node" / "v24" / "bin" / "node")
    _, _, seen, _ = review_once(tmp_path, monkeypatch, listing={"which": lambda name: node if name == "node" else None})
    assert seen["env"]["PATH"].split(os.pathsep)[0] == os.path.dirname(node)
    assert seen["env"]["PATH"].endswith(os.environ["PATH"])
    assert len(calls) == 1, "the settings, the PATH and the check after the session share one answer"


def test_settings_too_large_for_one_argument_are_refused(monkeypatch):
    assert auto.SETTINGS_LIMIT == 100_000, "one argument of claude's is at most 128 KiB on Linux"
    many = {"/": ["h"], "/h": [f"a-file-of-the-persons-own-{n:05}" for n in range(5000)] + ["wt"]}
    with pytest.raises(ValueError, match=r"more than 100000 \(one argument of claude's\): /h, on the way.*holds 5000"
                                         r" entries the sandbox must deny one by one"):
        settings_of("/h/wt", "/c/.git", "/c/.git/worktrees/wt", "/t", home="/h", environ={}, listdir=many.get)
    few = {"/": ["h"], "/h": ["notes", "wt"]}
    monkeypatch.setattr(auto, "SETTINGS_LIMIT", 10 ** 9)
    size = len(json.dumps(settings_of("/h/wt", "/c/.git", "/c/.git/worktrees/wt", "/t", home="/h", environ={},
                                      listdir=few.get)))
    monkeypatch.setattr(auto, "SETTINGS_LIMIT", size)
    settings_of("/h/wt", "/c/.git", "/c/.git/worktrees/wt", "/t", home="/h", environ={}, listdir=few.get)
    monkeypatch.setattr(auto, "SETTINGS_LIMIT", size - 1)
    with pytest.raises(ValueError, match=f"take {size} bytes, more than {size - 1}"):
        settings_of("/h/wt", "/c/.git", "/c/.git/worktrees/wt", "/t", home="/h", environ={}, listdir=few.get)


def root_of(path):
    return os.path.realpath(path).split("/")[1]


def test_settings_too_large_stop_the_round_before_the_session(tmp_path, monkeypatch):
    names = [f"an-entry-of-the-persons-own-{n:05}" for n in range(5000)]
    code, log, seen, _ = review_once(tmp_path, monkeypatch, listing={
        "listdir": lambda folder: [*names, root_of(tmp_path)] if folder == "/" else []})
    assert code == 127 and "command" not in seen, "no session"
    assert "rollout.sh: not started: can't set the session up: the session's settings take" in Path(log).read_text()


def test_the_session_folder_defaults_to_one_beside_the_cache_outside_tmp():
    assert auto.SESSION_DIR == os.path.expanduser("~/.cache/repo-ci-baseline-sessions")
    home = os.path.expanduser("~")
    for path in auto.CREDENTIAL_PATHS:
        path = auto.expand_home(path, home)
        assert auto.SESSION_DIR != path and not auto.SESSION_DIR.startswith(path + "/"), path
    assert not auto.SESSION_DIR.startswith(("/tmp/", "/var/tmp/"))


def test_a_session_folder_not_of_the_users_own_stops_before_the_session(tmp_path, monkeypatch):
    (tmp_path / "elsewhere").mkdir()
    (tmp_path / "sessions").symlink_to(tmp_path / "elsewhere")
    code, log, seen, _ = review_once(tmp_path, monkeypatch)
    assert code == 127 and "command" not in seen
    text = Path(log).read_text()
    assert "rollout.sh: not started: can't set the session up:" in text and "isn't a folder of the user's own" in text
    assert "a real folder of yours, not a symlink; remove or fix it, or run /pre-push-review in" in text


def test_a_session_folder_that_cant_be_made_stops_before_the_session(tmp_path, monkeypatch):
    def full(**kwargs):
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(auto.tempfile, "TemporaryDirectory", full)
    code, log, seen, _ = review_once(tmp_path, monkeypatch)
    assert code == 127 and "command" not in seen
    assert "rollout.sh: not started: can't set the session up: [Errno 28] No space left on device" in Path(log).read_text()


def test_what_killed_runs_left_in_the_session_folder_is_removed(tmp_path, monkeypatch):
    sessions = tmp_path / "sessions"
    (sessions / "rollout-session.old" / "run").mkdir(parents=True)
    (sessions / "rollout-session.old" / "run" / "findings.json").write_text("[]")
    (tmp_path / "elsewhere").mkdir()
    (tmp_path / "elsewhere" / "keep.txt").write_text("x")
    (sessions / "rollout-session.link").symlink_to(tmp_path / "elsewhere")
    (sessions / "other").mkdir()
    code, _, seen, _ = review_once(tmp_path, monkeypatch)
    assert code == 0
    assert not (sessions / "rollout-session.old").exists()
    assert (sessions / "rollout-session.link").is_symlink() and (tmp_path / "elsewhere" / "keep.txt").exists(), \
        "a symlink is left, and where it leads untouched"
    assert (sessions / "other").is_dir(), "only rollout-session.* folders"
    assert not os.path.exists(seen["tmp"]), "and the session's own is gone once it ends"


def test_an_entry_gone_while_the_session_folder_is_swept_is_skipped(tmp_path, monkeypatch):
    (tmp_path / "rollout-session.gone").mkdir()
    (tmp_path / "rollout-session.old").mkdir()
    real = auto.os.lstat

    def lstat(path, *args, **kwargs):
        if str(path).endswith(".gone"):
            raise FileNotFoundError(2, "gone", path)
        return real(path, *args, **kwargs)

    monkeypatch.setattr(auto.os, "lstat", lstat)
    auto.sweep_sessions(str(tmp_path))
    assert not (tmp_path / "rollout-session.old").exists()


def test_another_users_leftover_in_the_session_folder_is_left(tmp_path, monkeypatch):
    (tmp_path / "rollout-session.theirs").mkdir()
    monkeypatch.setattr(auto.os, "getuid", lambda: os.stat(tmp_path).st_uid + 1)
    auto.sweep_sessions(str(tmp_path))
    assert (tmp_path / "rollout-session.theirs").is_dir()


def test_the_session_folder_is_left_whole_when_it_cant_be_removed(tmp_path, monkeypatch):
    made = []
    real = auto.tempfile.TemporaryDirectory

    def recorded(**kwargs):
        made.append(kwargs)
        return real(**kwargs)

    monkeypatch.setattr(auto.tempfile, "TemporaryDirectory", recorded)
    code, _, _, _ = review_once(tmp_path, monkeypatch)
    assert code == 0 and made == [{"prefix": "rollout-session.", "dir": str(tmp_path / "sessions"),
                                   "ignore_cleanup_errors": True}], "a failed cleanup can't replace the round's result"


def git_config_repo(tmp_path, *settings):
    env = {**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"}
    subprocess.run(["git", "init", "-q", str(tmp_path)], check=True, env=env)
    for key, value in settings:
        subprocess.run(["git", "-C", str(tmp_path), "config", "--add", key, value], check=True, env=env)


@pytest.mark.parametrize("key, value, found", [
    ("remote.origin.url", "git@github.com:o/r.git", []),
    ("remote.origin.url", "https://github.com/o/r.git", []),
    ("remote.origin.url", "ssh://git@github.com/o/r.git", []),
    ("remote.origin.url", "https://github.com/o/a@b.git", []),
    ("remote.origin.url", "https://ghp_abc@github.com/o/r.git", ["remote.origin.url"]),
    ("remote.origin.pushurl", "HTTPS://ghp_abc@github.com/o/r.git", ["remote.origin.pushurl"]),
    ("remote.origin.url", "ssh://u:pw@host/r.git", ["remote.origin.url"]),
    ("remote.origin.url", "ftp://u@host/r.git", ["remote.origin.url"]),
    ("remote.origin.url", "ftps://u@host/r.git", ["remote.origin.url"]),
    ("url.https://x-access-token:t0k@github.com/.insteadOf", "https://github.com/", ["url.https://***@github.com/.insteadof"]),
    ("url.https://t0k@github.com/.pushInsteadOf", "https://github.com/", ["url.https://***@github.com/.pushinsteadof"]),
    ("http.https://github.com/.extraheader", "AUTHORIZATION: basic eHg=", ["http.https://github.com/.extraheader"]),
    ("http.proxy", "http://u:pw@proxy:3128", ["http.proxy"]),
    ("remote.origin.proxy", "http://u:pw@proxy:3128", ["remote.origin.proxy"]),
    ("submodule.lib.url", "https://ghp_abc@github.com/o/lib.git", ["submodule.lib.url"]),
    ("lfs.url", "https://ghp_abc@lfs.example/o/r", ["lfs.url"]),
    ("credential.helper", "!f() { echo password=hunter2; }; f", ["credential.helper"]),
    ("credential.https://github.com.helper", "store --file .git/creds", ["credential.https://github.com.helper"]),
    ("credential.helper", "!git credential-store --file .git/creds", ["credential.helper"]),
    ("credential.helper", "!f() { echo PASSWORD=x; }; f", ["credential.helper"]),
    ("credential.helper", "!f() { echo password = x; }; f", ["credential.helper"]),
    ("credential.helper", "!my-helper --password x", ["credential.helper"]),
    ("credential.helper", "!my-helper --pass x", ["credential.helper"]),
    ("credential.helper", "!pass-git-helper -m ~/.password-store", []),
    ("credential.helper", '!f() { echo username=x; echo "password=$(pass show github)"; }; f', []),
    ("credential.helper", "!f() { echo password=`pass show github`; }; f", []),
    ("credential.helper", "!my-helper --pass-store x", []),
    ("credential.helper", "!my-helper --password-store=gnome", []),
    ("credential.helper", "!my-helper --password $PW", []),
    ("credential.helper", "!my-helper --password=x", ["credential.helper"]),
    ("credential.helper", "!my-helper --file-mode 600", []),
    ("credential.helper", "store --file=.git/creds", ["credential.helper"]),
    ("credential.helper", "!my-helper --passthrough", []),
    ("credential.helper", "!my-helper --filesystem", []),
    ("credential.helper", '!f() { echo password="hunter2"; }; f', ["credential.helper"]),
    ("credential.helper", "!f() { echo password='hunter2'; }; f", ["credential.helper"]),
    ("credential.helper", "!f() { echo password='$uper-secret'; }; f", ["credential.helper"]),
    ("credential.helper", '!f() { echo password="$(echo hunter2)"; }; f', ["credential.helper"]),
    ("credential.helper", '!f() { echo password="$PW"; }; f', []),
    ("credential.helper", "!f() { echo password=${PW}; }; f", []),
    ("credential.helper", "!f() { echo password=$1; }; f", []),
    ("credential.helper", '!f() { echo password=""; }; f', []),
    ("credential.helper", "!f() { echo password=; }; f", []),
    ("credential.helper", '!f() { echo "password=$(op read op://v/gh/token)"; }; f', []),
    ("credential.helper", '!my-helper --password "$PW"', []),
    ("credential.helper", "!my-helper --password=$PW", []),
    ("credential.helper", "!my-helper --password '$uper'", ["credential.helper"]),
    ("credential.helper", '!my-helper --password "$(echo hunter2)"', ["credential.helper"]),
    ("sendemail.smtppass", "hunter2", ["sendemail.smtppass"]),
    ("imap.pass", "hunter2", ["imap.pass"]),
    ("tool.api-token", "abc", ["tool.api-token"]),
    ("tool.api-token", "", []),
    ("tool.token", "   ", []),
    ("x.passwd", "a", ["x.passwd"]),
    ("x.password", "a", ["x.password"]),
    ("x.smtppassword", "a", ["x.smtppassword"]),
    ("x.passphrase", "a", ["x.passphrase"]),
    ("x.secret", "a", ["x.secret"]),
    ("x.clientsecret", "a", ["x.clientsecret"]),
    ("x.apikey", "a", ["x.apikey"]),
    ("x.api-key", "a", ["x.api-key"]),
    ("x.access-key", "a", ["x.access-key"]),
    ("x.secretkey", "a", ["x.secretkey"]),
    ("x.smtp-pass", "a", ["x.smtp-pass"]),
    ("hub.oauthtoken", "a", ["hub.oauthtoken"]),
    ("core.askpass", "/usr/bin/ssh-askpass", []),
    ("ssh.askpass", "/usr/lib/ssh/ksshaskpass", []),
    ("alias.bypass", "commit --no-verify", []),
    ("user.signingkey", "~/.ssh/id_ed25519.pub", []),
    ("http.sslkey", "/etc/ssl/me.key", []),
    ("x.passthrough", "a", []),
    ("x.tokens", "a", []),
    ("x.secrets-dir", "a", []),
    ("alias.pr", '!curl -H "Authorization: token abc" https://api.github.com', ["alias.pr"]),
    ("alias.pr", "!curl -H 'bearer abcdefgh1234' https://api.github.com", ["alias.pr"]),
    ("alias.pr", "!curl --oauth2-bearer abc https://api.github.com", []),
    ("alias.pr", "!curl --oauth2-bearer abcdefghijkl https://api.github.com", ["alias.pr"]),
    ("alias.pr", '!curl --oauth2-bearer "$GITHUB_TOKEN" https://api.github.com', []),
    ("alias.pr", '!curl -H "Authorization: Bearer $GITHUB_TOKEN" https://api.github.com', []),
    ("alias.pr", '!curl -H "Authorization: token $TOKEN" https://api.github.com', []),
    ("alias.pr", "!curl -H 'Authorization: `cat t`' https://api.github.com", []),
    ("alias.pr", "!curl -H 'Authorization:' https://api.github.com", []),
    ("alias.pr", "!echo bearer abcdefgh", ["alias.pr"]),
    ("alias.pr", "!echo bearer abcdefg", []),
    ("alias.pr", "!echo debearer abcdefghijkl", []),
    ("alias.pr", "!curl -H 'Authorization: token \"$T\"' https://x", []),
    ("alias.pr", "!curl -H 'Authorization: Bearer $GITHUB_TOKEN' https://x", []),
    ("alias.pr", '!curl -H "Authorization: Basic $(echo -n me:hunter2 | base64)" https://x', ["alias.pr"]),
    ("alias.pr", "!curl --oauth2-bearer `cat t` x", []),
    ("alias.pr", '!curl --oauth2-bearer "$(printf abcdefghij)" x', ["alias.pr"]),
    ("core.sshcommand", 'sshpass -p "$PW" ssh', []),
    ("core.sshcommand", "sshpass -p$PW ssh", []),
    ("core.sshcommand", "sshpass -p `cat t` ssh", []),
    ("core.sshcommand", 'sshpass -p "$(echo hunter2)" ssh', ["core.sshcommand"]),
    ("core.sshcommand", "sshpass -p '$uper-secret' ssh", ["core.sshcommand"]),
    ("core.sshcommand", 'sshpass -p "hunter2" ssh', ["core.sshcommand"]),
    ("core.sshcommand", "sshpass -p $PW ssh", []),
    ("core.sshcommand", "sshpass -e ssh", []),
    ("core.sshcommand", "sshpass -p hunter2 ssh", ["core.sshcommand"]),
    ("alias.x", "!echo ghp_" + "a" * 36, ["alias.x"]),
    ("alias.x", "!echo gho_" + "a" * 36, ["alias.x"]),
    ("alias.x", "!echo ghp_short", []),
    ("alias.x", "!echo ghp_" + "a" * 20, ["alias.x"]),
    ("alias.x", "!echo ghp_" + "a" * 19, []),
    ("alias.x", "!echo xghp_" + "a" * 36, []),
    ("alias.x", "!echo ghu_" + "a" * 20, ["alias.x"]),
    ("alias.x", "!echo ghs_" + "a" * 20, ["alias.x"]),
    ("alias.x", "!echo ghr_" + "a" * 20, ["alias.x"]),
    ("alias.x", "!echo github_pat_" + "a" * 20, ["alias.x"]),
    ("alias.x", "!echo github_pat_" + "a" * 19, []),
    ("alias.x", "!echo glpat-" + "a" * 19, []),
    ("alias.x", "!echo xoxb-" + "1" * 10, ["alias.x"]),
    ("alias.x", "!echo xoxb-" + "1" * 9, []),
    ("alias.x", "!echo xoxa-" + "1" * 10, ["alias.x"]),
    ("alias.x", "!echo xoxp-" + "1" * 10, ["alias.x"]),
    ("alias.x", "!echo xoxr-" + "1" * 10, ["alias.x"]),
    ("alias.x", "!echo xoxs-" + "1" * 10, ["alias.x"]),
    ("alias.x", "!echo sk-" + "a" * 20, ["alias.x"]),
    ("alias.x", "!echo sk-" + "a" * 19, []),
    ("alias.x", "!echo AKIA" + "A" * 15, []),
    ("alias.x", "!echo AKIA" + "A" * 15 + "!", []),
    ("alias.x", "!echo github_pat_" + "a" * 30, ["alias.x"]),
    ("alias.x", "!echo glpat-" + "a" * 20, ["alias.x"]),
    ("alias.x", "!echo xoxb-" + "1" * 12, ["alias.x"]),
    ("alias.x", "!echo sk-" + "a" * 24, ["alias.x"]),
    ("alias.x", "!echo task-" + "a" * 24, []),
    ("alias.x", "!echo AKIA" + "A" * 16, ["alias.x"]),
    ("alias.x", "!echo akia" + "a" * 16, []),
    ("core.sshcommand", "ssh -i key", []),
    ("credential.usehttppath", "true", []),
    ("credential.helper", "store", []),
    ("credential.helper", "cache --timeout 300", []),
])
def test_a_sign_in_secret_in_the_clones_git_config_is_found_by_its_key(tmp_path, key, value, found):
    git_config_repo(tmp_path, (key, value))
    secrets = auto.config_secrets(str(tmp_path), str(tmp_path / ".git"))
    assert secrets == [f"{key} in {tmp_path / '.git' / 'config'}" for key in found]
    assert not [name for name in secrets if "t0k" in name or "ghp_" in name or "hunter2" in name], \
        "a key is named, never its token"


@pytest.mark.parametrize("store", ["pass", "gopass", "op", "bw", "lpass", "gpg", "secret-tool", "security", "vault",
                                   "keyring", "cat"])
def test_a_secret_fetched_from_a_store_or_a_file_holds_none(store):
    for value in (f"echo password=$({store} show x)", f"echo password=`{store} show x`",
                  f"echo password=$( {store} show x)", f'echo password="$({store} show x)"',
                  f"--password $({store} show x)"):
        assert not auto.HELPER_SECRET.search(value), value
    for value in (f'Authorization: Bearer "$({store} show x)"', f"bearer `{store} show x`",
                  f"sshpass -p $({store} show x) ssh"):
        assert not auto.TOKEN_SHAPE.search(value), value
    for value in (f"echo password=$({store}x show y)", f"echo password=`{store}x y`"):
        assert auto.HELPER_SECRET.search(value), f"{value}: another program than {store}, which may hold it itself"
    assert auto.TOKEN_SHAPE.search(f"sshpass -p $({store}ed x) ssh")


@pytest.mark.parametrize("value", ["echo password=$-x", "echo password=$ x", "echo password=$$"])
def test_a_dollar_that_starts_no_name_is_read_as_a_password(value):
    assert auto.HELPER_SECRET.search(value)


def test_a_secret_in_a_file_the_config_includes_or_a_credential_store_is_found(tmp_path):
    git_config_repo(tmp_path, ("include.path", "secrets.inc"))
    (tmp_path / ".git" / "secrets.inc").write_text("[remote \"origin\"]\n\turl = https://ghp_abc@github.com/o/r\n")
    common = tmp_path / ".git"
    assert auto.config_secrets(str(tmp_path), str(common)) == [f"remote.origin.url in {common / 'secrets.inc'}"], \
        "named by the file that holds it, the one to unset it in"
    (common / "credentials").write_text("https://u:ghp_abc@github.com\n")
    assert auto.config_secrets(str(tmp_path), str(common))[0] == f"{common / 'credentials'} (a file named for credentials)"
    (common / "credentials").unlink()
    (common / "git-credential-cache").write_text("")
    assert auto.config_secrets(str(tmp_path), str(common))[0] == \
        f"{common / 'git-credential-cache'} (a file named for credentials)", "credential, not only credentials"


def test_a_submodules_git_config_is_read_too(tmp_path):
    git_config_repo(tmp_path, ("core.bare", "false"))
    common = tmp_path / ".git"
    assert auto.config_secrets(str(tmp_path), str(common)) == []
    lib = common / "modules" / "lib"
    inner = lib / "modules" / "deep" / "inner"
    for folder, url in ((lib, "https://ghp_lib@github.com/o/lib"), (inner, "https://ghp_in@github.com/o/inner")):
        folder.mkdir(parents=True)
        (folder / "HEAD").write_text("ref: refs/heads/main\n")
        (folder / "config").write_text(f"[remote \"origin\"]\n\turl = {url}\n")
    (lib / "worktrees" / "w").mkdir(parents=True)
    (lib / "worktrees" / "w" / "HEAD").write_text("ref: refs/heads/w\n")
    (lib / "worktrees" / "w" / "config.worktree").write_text("[http]\n\tproxy = http://u:pw@p:3128\n")
    (lib / "refs" / "remotes" / "origin").mkdir(parents=True)
    (lib / "refs" / "remotes" / "origin" / "HEAD").write_text("ref: refs/remotes/origin/main\n")
    (lib / "refs" / "remotes" / "origin" / "config").write_text("a branch named config, not a git config\n")
    (lib / "objects").mkdir()
    (lib / "objects" / "config").write_text("[remote \"x\"]\n\turl = https://ghp_obj@github.com/o/x\n")
    assert auto.config_secrets(str(tmp_path), str(common)) == [
        f"remote.origin.url in {lib / 'config'}", f"http.proxy in {lib / 'worktrees' / 'w' / 'config.worktree'}",
        f"remote.origin.url in {inner / 'config'}"], "a submodule's, its worktrees', a nested one's; not its refs"


def test_a_linked_worktrees_submodules_and_sibling_submodules_are_read_in_order(tmp_path, monkeypatch):
    real = os.scandir

    class Reversed:
        """os.scandir listing in reverse, so the order the filesystem happens to list in can't pass for sorted."""

        def __init__(self, path):
            with real(path) as handle:
                self.entries = iter(sorted(handle, key=lambda entry: entry.name, reverse=True))

        def __iter__(self):
            return self

        def __next__(self):
            return next(self.entries)

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

        def close(self):
            pass

    monkeypatch.setattr(os, "scandir", Reversed)
    git_config_repo(tmp_path, ("core.bare", "false"))
    common = tmp_path / ".git"
    wt = common / "worktrees" / "w"
    wt.mkdir(parents=True)
    (wt / "HEAD").write_text("ref: refs/heads/w\n")
    for folder in (common / "modules" / "b", common / "modules" / "a", wt / "modules" / "sub"):
        folder.mkdir(parents=True)
        (folder / "HEAD").write_text("ref: refs/heads/main\n")
        (folder / "config").write_text("[remote \"origin\"]\n\turl = https://ghp_x@github.com/o/r\n")
    assert auto.config_files(str(common)) == [
        str(common / "config"), str(wt / "modules" / "sub" / "config"), str(common / "modules" / "a" / "config"),
        str(common / "modules" / "b" / "config")], "a linked worktree's submodules (git keeps them there), sorted"


def test_an_entry_whose_origin_isnt_a_file_is_named_by_the_file_read(tmp_path):
    (tmp_path / "config").write_text("")
    out = "command line:\0remote.a.url\nhttps://t@h/a\0file:/inc\0remote.b.url\nhttps://t@h/b\0"
    assert auto.config_secrets("/w", str(tmp_path), run=lambda command: (0, out, "")) == [
        f"remote.a.url in {tmp_path / 'config'}", "remote.b.url in /inc"]


def test_a_modules_folder_that_cant_be_listed_stops_the_check(tmp_path, monkeypatch):
    git_config_repo(tmp_path, ("core.bare", "false"))
    (tmp_path / ".git" / "modules" / "lib").mkdir(parents=True)

    def walk(top, onerror=None):
        onerror(PermissionError(13, "Permission denied", str(tmp_path / ".git" / "modules" / "lib")))
        return iter(())

    monkeypatch.setattr(auto.os, "walk", walk)
    with pytest.raises(RuntimeError, match=r"can't list .*modules/lib, so what of the clone's git config the session"
                                           r" could read can't be told \(Permission denied\): make it listable"):
        auto.config_files(str(tmp_path / ".git"))


def test_a_git_folder_removed_while_it_is_walked_is_skipped(tmp_path, monkeypatch):
    git_config_repo(tmp_path, ("core.bare", "false"))
    common = tmp_path / ".git"
    walk = os.walk

    def removed_meanwhile(top, onerror=None):
        onerror(FileNotFoundError(2, "No such file or directory", str(common / "worktrees" / "gone")))
        onerror(NotADirectoryError(20, "Not a directory", str(common / "modules" / "x")))
        return walk(top, onerror=onerror)

    monkeypatch.setattr(auto.os, "walk", removed_meanwhile)
    assert auto.config_files(str(common)) == [str(common / "config")], "a worktree pruned as it was walked"


def test_a_config_file_removed_before_git_reads_it_is_skipped(tmp_path):
    git_config_repo(tmp_path, ("core.bare", "false"))
    common = tmp_path / ".git"
    (common / "worktrees" / "w").mkdir(parents=True)
    (common / "worktrees" / "w" / "HEAD").write_text("ref: refs/heads/w\n")
    target = common / "worktrees" / "w" / "config.worktree"
    target.write_text("")

    def pruned(command):
        if command[5] == str(target):
            target.unlink()
            return 128, "", "fatal: unable to read config file"
        return 0, "", ""

    assert auto.config_secrets("/w", str(common), run=pruned) == []
    target.write_text("")
    with pytest.raises(RuntimeError, match=r"git can't read .*config.worktree \(fatal: bad config\)"):
        auto.config_secrets("/w", str(common), run=lambda command: (128, "", "fatal: bad config")
                            if command[5] == str(target) else (0, "", ""))


def test_a_git_folder_that_cant_be_listed_stops_the_round():
    with pytest.raises(RuntimeError, match=r"can't list the clone's git folder /nowhere/.git, so whether it holds a"
                                           r" sign-in secret can't be told"):
        auto.config_secrets("/w", "/nowhere/.git", run=lambda command: (0, "", ""))


@pytest.mark.skipif(os.geteuid() == 0, reason="root lists a folder whatever its mode")
def test_a_worktrees_folder_that_cant_be_listed_stops_the_round(tmp_path):
    (tmp_path / "worktrees").mkdir()
    (tmp_path / "worktrees").chmod(0)
    try:
        with pytest.raises(RuntimeError, match=r"can't list .*/worktrees, so what of the clone's git config the"
                                               r" session could read can't be told \(Permission denied\)"):
            auto.config_secrets("/w", str(tmp_path), run=lambda command: (0, "", ""))
    finally:
        (tmp_path / "worktrees").chmod(0o700)


def test_redacted_hides_every_urls_sign_in():
    assert auto.redacted("url.https://x:t0k@a/.insteadof ssh://u:p@b") == "url.https://***@a/.insteadof ssh://***@b"


@pytest.mark.parametrize("error, why", [("fatal: bad config https://t0k@host\n", "fatal: bad config https://***@host"),
                                        ("", "exit 128")])
def test_a_git_config_that_cant_be_read_is_an_error(tmp_path, error, why):
    (tmp_path / "config").write_text("")
    with pytest.raises(RuntimeError) as raised:
        auto.config_secrets("/w", str(tmp_path), run=lambda command: (128, "", error))
    config = tmp_path / "config"
    assert str(raised.value) == (f"git can't read {config} ({why}): repair it (git config --file {config} --list shows"
                                 " the error)")


def test_a_linked_worktrees_own_config_is_read_only_with_the_extension(tmp_path):
    env = {**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"}

    def git(*args):
        subprocess.run(["git", *args], check=True, env=env, capture_output=True)

    clone, worktree = tmp_path / "clone", tmp_path / "wt"
    git("init", "-q", str(clone))
    git("-C", str(clone), "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "one")
    git("-C", str(clone), "worktree", "add", "-q", str(worktree))
    common = clone / ".git"
    git("-C", str(clone), "config", "extensions.worktreeConfig", "true")
    assert auto.config_secrets(str(worktree), str(common)) == [], "the extension on and no config.worktree yet"
    git("-C", str(worktree), "config", "--worktree", "remote.origin.url", "https://tok@github.com/o/r")
    own = next((common / "worktrees").iterdir()) / "config.worktree"
    assert auto.config_secrets(str(worktree), str(common)) == [f"remote.origin.url in {own}"], "its own config.worktree"
    other = tmp_path / "other"
    git("-C", str(clone), "worktree", "add", "-q", str(other))
    git("-C", str(other), "config", "--worktree", "http.proxy", "http://u:p@proxy")
    git("-C", str(clone), "config", "--worktree", "lfs.url", "https://tok@lfs.example")
    found = auto.config_secrets(str(worktree), str(common))
    assert f"lfs.url in {common / 'config.worktree'}" in found, "the main checkout's, which the session reads too"
    assert [line for line in found if line.startswith("http.proxy in ") and "/worktrees/" in line], "another worktree's"


def test_a_clone_with_a_sign_in_secret_isnt_reviewed(tmp_path, monkeypatch):
    asked = []
    code, log, seen, worktree = review_once(tmp_path, monkeypatch, listing={
        "secrets": lambda w, c: asked.append((w, c)) or ["remote.origin.url"]})
    assert code == 127 and "command" not in seen
    assert len(asked) == 1 and asked[0][0] == worktree, "asked once, before the session"
    text = Path(log).read_text()
    assert "rollout.sh: not started: the clone's git folder holds a sign-in secret (remote.origin.url)" in text
    assert ("A key is named '<key> in <file>', the file that holds it: git config --file <file> --list shows it whole"
            " (a *** in one stands for its sign-in), and git config --file <file> --unset-all <key> removes it; a file"
            " named for credentials is named by its path: remove it. Keep the token in a credential helper, then"
            " resume") in text


def test_a_secret_added_to_the_clones_git_folder_while_the_session_ran_is_named(tmp_path, monkeypatch, capsys):
    calls = []

    def secrets(worktree, common):
        calls.append(worktree)
        return ["remote.origin.url"] if len(calls) > 1 else []

    code, log, _, _ = review_once(tmp_path, monkeypatch, listing={"secrets": secrets})
    assert code == 0 and len(calls) == 2, "asked before the session and after it"
    said = ("rollout.sh: added to the clone's git folder while the session ran, so it could read them:"
            " remote.origin.url")
    assert said in Path(log).read_text() and said in capsys.readouterr().err
    assert auto.added_secrets(["a"], ["a", "b"]) == [
        "added to the clone's git folder while the session ran, so it could read them: b"]
    assert auto.added_secrets(["a"], ["a"]) == []


def test_what_appears_beside_the_sessions_folders_while_it_runs_is_named(tmp_path, monkeypatch, capsys):
    calls = []

    def listdir(folder):
        if folder != "/":
            return []
        calls.append(folder)
        return [root_of(tmp_path), *(["new-project"] if len(calls) > 1 else [])]

    code, log, _, _ = review_once(tmp_path, monkeypatch, listing={"listdir": listdir})
    assert code == 0, "a warning, not a broken rule"
    said = ("rollout.sh: made while the session ran, after its sandbox was set, so it could read them (check them):"
            " /new-project")
    assert said in Path(log).read_text() and said in capsys.readouterr().err


@pytest.mark.parametrize("count, shown", [(20, ""), (21, " (and 1 more)"), (25, " (and 5 more)")])
def test_what_appeared_is_named_twenty_at_most(count, shown):
    names = [f"n{index:02}" for index in range(count)]
    settings = {"sandbox": {"filesystem": {"denyRead": HELD}}}
    said = auto.appeared(settings, (["/h/wt"], [], "/h"), listdir={"/": names}.get, which=lambda name: None)
    assert said == ["made while the session ran, after its sandbox was set, so it could read them (check them): "
                    + ", ".join(f"/{name}" for name in names[:20]) + shown]


def test_a_name_that_cant_be_denied_makes_what_appeared_untold():
    settings = {"sandbox": {"filesystem": {"denyRead": HELD}}}
    said = auto.appeared(settings, (["/h/wt"], [], "/h"), listdir={"/": ["a*b"]}.get, which=lambda name: None)
    assert said[0].startswith("what was made beside the session's folders while it ran can't be told ('/a*b' must be")
    assert said[0].endswith("rename or move it)"), "no 'then resume': nothing stopped"


def test_unlisted_since_names_only_whats_new():
    tree = {"/": ["h", "new", "old"], "/h": ["wt"]}
    assert auto.unlisted_since(["/old", *HELD], ["/h/wt"], [], "/h", tree.get, lambda name: None) == ["/new"]


def test_a_failing_warning_or_a_closed_stderr_loses_no_review(tmp_path, monkeypatch):
    out = io.StringIO()

    def broken():
        raise KeyError("x")

    auto.after_session(out, "the thing", broken)
    assert out.getvalue().startswith("Traceback (most recent call last):"), "the traceback in the log"
    assert out.getvalue().endswith("rollout.sh: checking the thing failed (KeyError: 'x'); check it yourself\n")

    class Closed:
        def write(self, text):
            raise ValueError("I/O operation on closed file")

    monkeypatch.setattr(auto.sys, "stderr", Closed())
    out = io.StringIO()
    auto.after_session(out, "the thing", lambda: ["one", "two"])
    assert out.getvalue() == "rollout.sh: one\nrollout.sh: two\n"

    class Full:
        def write(self, text):
            raise OSError(28, "No space left on device")

    monkeypatch.setattr(auto.sys, "stderr", io.StringIO())
    auto.after_session(Full(), "the thing", broken)
    auto.after_session(Full(), "the thing", lambda: ["one"])
    assert auto.sys.stderr.getvalue().endswith("rollout.sh: one\n"), "a log that can't be written loses no review"
    assert auto.without_resume(ValueError("rename it, then resume; more")) == "rename it"


def test_a_failing_check_after_the_session_neither_stops_the_record_nor_hides_the_other(tmp_path, monkeypatch, capsys):
    calls = []

    def listdir(folder):
        if folder != "/":
            return []
        calls.append(folder)
        return [root_of(tmp_path), *(["new-project"] if len(calls) > 1 else [])]

    asked = []

    def secrets(worktree, common):
        asked.append(worktree)
        if len(asked) > 1:
            raise RuntimeError("git can't read /c/config (bad), then resume")
        return []

    code, log, seen, _ = review_once(tmp_path, monkeypatch, listing={"listdir": listdir, "secrets": secrets})
    assert code == 0 and "recorded" in seen, "recorded all the same"
    text = Path(log).read_text()
    assert "rollout.sh: made while the session ran, after its sandbox was set, so it could read them (check them):" \
           " /new-project" in text, "the other check's warning is kept"
    assert ("rollout.sh: checking the clone's git folder for sign-in secrets added failed (RuntimeError: git can't"
            " read /c/config (bad)); check it yourself") in text


def test_a_warning_that_raises_past_its_guard_still_lets_the_review_be_recorded(tmp_path, monkeypatch):
    monkeypatch.setattr(auto, "appeared", lambda *args, **kwargs: (_ for _ in ()).throw(KeyError("x")))
    code, log, seen, _ = review_once(tmp_path, monkeypatch)
    assert code == 0 and "recorded" in seen
    assert "rollout.sh: checking what was made beside the session's folders failed (KeyError: 'x')" in Path(log).read_text()


def test_what_appeared_during_the_session_that_cant_be_told_is_said(tmp_path, monkeypatch, capsys):
    calls = []

    def listdir(folder):
        calls.append(folder)
        if folder == "/" and calls.count("/") > 1:
            raise OSError(13, "Permission denied")
        return [root_of(tmp_path)] if folder == "/" else []

    code, log, _, _ = review_once(tmp_path, monkeypatch, listing={"listdir": listdir})
    assert code == 0
    said = "rollout.sh: what was made beside the session's folders while it ran can't be told (can't list /"
    assert said in Path(log).read_text() and said in capsys.readouterr().err


def test_nothing_new_beside_the_sessions_folders_says_nothing(tmp_path, monkeypatch, capsys):
    code, log, _, _ = review_once(tmp_path, monkeypatch)
    assert code == 0 and "made while the session ran" not in Path(log).read_text()


def test_the_session_settings_read_the_real_environment_by_default(monkeypatch):
    # run_review passes no environ: what the person's environment moves is denied all the same.
    monkeypatch.setenv("CLAUDE_CONFIG_DIR", "/cfg/claude")
    monkeypatch.setenv("KUBECONFIG", "/k/config")
    monkeypatch.setenv("XDG_CONFIG_HOME", "/x")
    deny = settings_of("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home="/h")
    for path in ("/cfg/claude", "/k/config", "/x/gh", "/x/git"):
        assert path in deny["sandbox"]["filesystem"]["denyRead"] and f"Read(/{path}/**)" in deny["permissions"]["deny"]


def test_a_listed_credential_that_is_a_symlink_is_denied_where_it_leads(tmp_path):
    # ~/.bashrc and ~/.gitconfig are often links into a dotfiles repo.
    home = tmp_path / "home"
    (home / ".config").mkdir(parents=True)
    (tmp_path / "dotfiles" / "git").mkdir(parents=True)
    (tmp_path / "dotfiles" / "bashrc").write_text("export TOKEN=x\n")
    (home / ".bashrc").symlink_to(tmp_path / "dotfiles" / "bashrc")
    (home / ".config" / "git").symlink_to(tmp_path / "dotfiles" / "git")
    deny = settings_of("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home=str(home), environ={})
    found = deny["sandbox"]["filesystem"]["denyRead"]
    for path in (home / ".bashrc", tmp_path / "dotfiles" / "bashrc", home / ".config" / "git", tmp_path / "dotfiles" / "git"):
        assert str(path) in found and f"Read(/{path})" in deny["permissions"]["deny"]
    assert len(found) == len(set(found))


def test_a_credential_named_twice_is_denied_once():
    environ = {"GIT_CONFIG_GLOBAL": "~/.gitconfig", "GH_CONFIG_DIR": "~/.config/gh", "KUBECONFIG": "/k:/k"}
    deny = settings_of("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home="/h", environ=environ)
    found = deny["sandbox"]["filesystem"]["denyRead"]
    assert len(found) == len(set(found)) and found.count("/h/.gitconfig") == 1 and found.count("/k") == 1
    assert denied_beyond_the_list(environ) == ["/k"]


@pytest.mark.parametrize("path, expanded", [("~", "/h"), ("~/x", "/h/x"), ("~user/x", "~user/x"), ("a~/x", "a~/x"),
                                            ("/abs", "/abs")])
def test_expand_home_takes_only_a_tilde_of_its_own(path, expanded):
    assert auto.expand_home(path, "/h") == expanded


def denied_beyond_the_list(environ, home="/h"):
    deny = settings_of("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home=home, environ=environ)
    listed = [home + path[1:] for path in auto.CREDENTIAL_PATHS]
    found = deny["sandbox"]["filesystem"]["denyRead"]
    assert found[:len(listed)] == listed and found[-len(HELD):] == HELD, "the credentials, then the person's data"
    found = found[:-len(HELD)]
    for path in found:
        assert f"Read(/{path})" in deny["permissions"]["deny"] and f"Read(/{path}/**)" in deny["permissions"]["deny"]
    return found[len(listed):]


def test_the_session_settings_keep_claude_codes_own_config_folder_from_it(tmp_path, monkeypatch):
    monkeypatch.setenv("CLAUDE_CONFIG_DIR", "/cfg/claude")
    settings = settings_of("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home="/h", environ=dict(os.environ))
    assert "/cfg/claude" in settings["sandbox"]["filesystem"]["denyRead"], "read from the environment by default"
    assert denied_beyond_the_list({"CLAUDE_CONFIG_DIR": "/cfg/claude"}) == ["/cfg/claude"]
    assert denied_beyond_the_list({}) == []


@pytest.mark.parametrize("value, denied", [
    ("/cfg/claude/", ["/cfg/claude"]),
    ("/cfg//claude", ["/cfg/claude"]),
    ("~/cfg", ["/h/cfg"]),
    ("  /cfg/claude  ", ["/cfg/claude"]),
    ("", []),
    ("   ", []),
])
def test_claude_codes_config_folder_is_denied_as_an_absolute_path(value, denied):
    assert denied_beyond_the_list({"CLAUDE_CONFIG_DIR": value}) == denied


def test_a_relative_or_symlinked_config_folder_is_denied_where_it_really_is(tmp_path, monkeypatch):
    (tmp_path / "real").mkdir()
    (tmp_path / "link").symlink_to(tmp_path / "real")
    monkeypatch.chdir(tmp_path)
    assert denied_beyond_the_list({"CLAUDE_CONFIG_DIR": "link"}) == [str(tmp_path / "link"), str(tmp_path / "real")]
    env = auto.session_env({"CLAUDE_CONFIG_DIR": "link/"}, str(tmp_path / "t"))
    assert env["CLAUDE_CONFIG_DIR"] == str(tmp_path / "link"), "the session reads the folder the settings deny"


def test_where_the_environment_moves_a_credential_is_denied_too():
    environ = {"GH_CONFIG_DIR": "/gh", "DOCKER_CONFIG": "~/dock", "KUBECONFIG": "/k/a:/k/b", "AWS_SHARED_CREDENTIALS_FILE":
               "/aws/c", "AWS_CONFIG_FILE": "/aws/f", "NPM_CONFIG_USERCONFIG": "/n", "npm_config_userconfig": "/n2",
               "GNUPGHOME": "/g", "CLOUDSDK_CONFIG": "/gc", "AZURE_CONFIG_DIR": "/az", "PASSWORD_STORE_DIR": "/ps",
               "GIT_CONFIG_GLOBAL": "/gitc", "XDG_CONFIG_HOME": "/x", "KUBE_OTHER": "/no"}
    expected = ["/gh", "/h/dock", "/k/a", "/k/b", "/aws/c", "/aws/f", "/n", "/n2", "/g", "/gc", "/az", "/ps", "/gitc",
                *(f"/x/{folder}" for folder in auto.XDG_CONFIG_SUBFOLDERS)]
    assert denied_beyond_the_list(environ) == expected
    assert denied_beyond_the_list({"KUBECONFIG": ":", "XDG_CONFIG_HOME": " "}) == [], "an empty part names nothing"


def test_the_session_settings_expand_the_real_home_and_take_the_main_checkout_from_git(tmp_path, monkeypatch):
    monkeypatch.setenv("HOME", str(tmp_path / "me"))
    monkeypatch.delenv("CLAUDE_CONFIG_DIR", raising=False)
    settings = settings_of("/w/wt", "/x/Repo.git", "/x/Repo.git/worktrees/wt", "/t", environ={})
    assert settings["sandbox"]["filesystem"]["denyRead"][0] == str(tmp_path / "me" / ".ssh")
    assert not any("~" in path for path in settings["sandbox"]["filesystem"]["denyRead"])
    assert "Read(//x/.security/**)" in settings["permissions"]["allow"], \
        "the .security record is beside the git folder whatever it is called, as reapply.sh finds the main checkout"


def test_expand_home_replaces_a_leading_tilde_only():
    assert auto.expand_home("~/.ssh", "/h") == "/h/.ssh"
    assert auto.expand_home("/a/~b", "/h") == "/a/~b"


def test_the_sandbox_and_session_lists_word_for_word():
    assert auto.SESSION_BUILTINS == ("Read", "Write", "Edit", "Glob", "Grep", "Bash", "Agent", "TodoWrite", "SendMessage")
    assert auto.SESSION_TOOLS == ("Bash", "Agent", "Task", "TodoWrite", "SendMessage")
    assert auto.CREDENTIAL_PATHS == (
        "~/.ssh", "~/.config/gh", "~/.config/glab-cli", "~/.config/hub", "~/.git-credentials", "~/.config/git/credentials",
        "~/.netrc", "~/.gnupg", "~/.docker", "~/.aws", "~/.azure", "~/.config/gcloud", "~/.kube", "~/.terraform.d",
        "~/.npmrc", "~/.yarnrc", "~/.yarnrc.yml", "~/.config/pnpm", "~/.nuget/NuGet", "~/.config/NuGet", "~/.pypirc",
        "~/.config/pip", "~/.config/uv", "~/.cargo/credentials", "~/.cargo/credentials.toml", "~/.gem/credentials",
        "~/.m2/settings.xml", "~/.local/share/keyrings", "~/.password-store", "~/.vault-token", "~/.config/op",
        "~/.pgpass", "~/.bash_history", "~/.zsh_history", "~/.local/share/fish/fish_history", "~/.claude.json",
        "~/.claude", "~/.cache/repo-ci-baseline",
        "~/.bashrc", "~/.bash_profile", "~/.bash_login", "~/.profile", "~/.zshrc", "~/.zshenv", "~/.zprofile",
        "~/.zlogin", "~/.config/fish", "~/.gitconfig", "~/.config/git", "~/.microsoft/usersecrets", "~/.aspnet",
        "~/.config/rclone", "~/.s3cfg", "~/.boto", "~/.oci", "~/.config/doctl", "~/.gradle/gradle.properties")
    assert auto.CREDENTIAL_VARS == (
        "GH_CONFIG_DIR", "DOCKER_CONFIG", "KUBECONFIG", "AWS_SHARED_CREDENTIALS_FILE", "AWS_CONFIG_FILE",
        "NPM_CONFIG_USERCONFIG", "npm_config_userconfig", "GNUPGHOME", "CLOUDSDK_CONFIG", "AZURE_CONFIG_DIR",
        "PASSWORD_STORE_DIR", "GIT_CONFIG_GLOBAL")
    assert auto.XDG_CONFIG_SUBFOLDERS == ("gh", "glab-cli", "hub", "git", "pnpm", "pip", "uv", "NuGet", "op", "gcloud",
                                          "rclone", "doctl", "fish")
    assert auto.SANDBOX_DOMAINS == (), "it builds, installs and fetches nothing, so it reaches no host"
    assert auto.NO_BUILD_SERVERS == {"MSBUILDDISABLENODEREUSE": "1", "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
                                     "UseSharedCompilation": "false"}
    assert (auto.RECORD_BYTES, auto.STAGED_STATUSES) == (16 * 1024 * 1024, ("open", "dismissed"))
    assert (auto.RECORD_FAILED, auto.NOTHING_STAGED, auto.RECORD_LEFT, auto.RECORD_TIMEOUT_SECONDS, auto.SHOWN_CHARS,
            auto.UNSEEN_SHOWN, auto.KEPT_HEAD_FILE) == (256, 257, 258, 600, 120, 3, "HEAD")
    assert min(auto.RECORD_FAILED, auto.NOTHING_STAGED, auto.RECORD_LEFT) > 255, \
        "above every exit code, so a session's can't read as one"
    assert inspect.signature(auto.run_bounded).parameters["timeout"].default == auto.RECORD_TIMEOUT_SECONDS
    assert auto.SANDBOX_TOOLS == (("bwrap", "socat") if sys.platform.startswith("linux") else ())
    assert auto.TOOLS == ("git", "gh", "python3", "node", "pnpm", "shfmt", "claude")
    assert (auto.GROUP_WAIT_SECONDS, auto.SESSION_BROKE_A_RULE) == (10, 126)
    assert auto.NO_PUSH == "https://push.invalid/"
    assert auto.SESSION_GIT_CONFIG == (
        ("credential.helper", ""),
        ("credential.https://github.com.helper", ""),
        ("url.https://push.invalid/.pushInsteadOf", "https://github.com/"),
        ("url.https://push.invalid/.pushInsteadOf", "git@github.com:"),
        ("url.https://push.invalid/.pushInsteadOf", "ssh://git@github.com/"),
        ("gc.auto", "0"),
    )
    assert auto.FLAG_ON == ("1", "true", "yes", "on")
    assert auto.CLAUDE_KEPT == (
        "ANTHROPIC_BASE_URL", "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL",
        "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_VERTEX_PROJECT_ID",
        "ANTHROPIC_FOUNDRY_RESOURCE", "ANTHROPIC_BEDROCK_BASE_URL", "ANTHROPIC_VERTEX_BASE_URL", "ANTHROPIC_FOUNDRY_BASE_URL",
        "CLAUDE_CONFIG_DIR", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY")
    assert auto.CLAUDE_SECRETS == ("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN",
                                   "ANTHROPIC_FOUNDRY_API_KEY", "ANTHROPIC_CUSTOM_HEADERS")


def test_the_agent_comes_from_the_templates_file():
    agents = auto.agent_definition()
    assert set(agents) == {"pre-push-reviewer"}
    agent = agents["pre-push-reviewer"]
    assert agent["tools"] == ["Read", "Grep", "Glob"] and agent["model"] == "sonnet"
    assert agent["description"].startswith("A read-only reviewer for the pre-push-review skill") and "\n" not in agent["description"]
    assert agent["prompt"].startswith("# Pre-push reviewer")
    assert auto.AGENT_FILE == os.path.join(auto.OWNED, ".claude", "agents", "pre-push-reviewer.md")


@pytest.mark.parametrize("text, problem", [
    ("# no front matter\n", "has no front matter"),
    ("---\nname: x\ndescription: y\n---\nbody\n", "has no tools"),
    ("---\nname: x\ntools: Read\n---\nbody\n", "has no description"),
    ("---\ndescription: y\ntools: Read\n---\nbody\n", "has no name"),
    ("---\nname: x\ndescription: y\ntools: Read\nbody without a close\n", "front matter is never closed"),
    ("----\nname: x\n---\nbody\n", "has no front matter"),
])
def test_an_agent_file_that_doesnt_read_is_refused(tmp_path, text, problem):
    (tmp_path / "a.md").write_text(text)
    with pytest.raises(ValueError, match=problem):
        auto.agent_definition(str(tmp_path / "a.md"))


@pytest.mark.parametrize("marker", [">-", ">", "|"])
def test_an_agent_files_folded_fields_and_body_are_read_whole(tmp_path, marker):
    (tmp_path / "a.md").write_text(f"---\nname: x\ndescription: {marker}\n  one\n\ttwo\ntools: Read, Grep ,\n---\n\n- a list"
                                   " first\n---\nmore\n")
    agent = auto.agent_definition(str(tmp_path / "a.md"))["x"]
    assert agent == {"description": "one two", "prompt": "- a list first\n---\nmore\n", "tools": ["Read", "Grep"]}, \
        "no model: none is passed; a body that opens with a dash or holds a rule is kept whole"


@pytest.mark.parametrize("text, parts", [
    ("", None),
    ("body\n", None),
    ("---\nname: a\n---\nbody", ("name: a\n", "body")),
    ("---\r\nname: a\r\n---\r\nbody", ("name: a\r\n", "body")),
    ("---\n---\n", ("", "")),
    ("--- \nname: a\n---\nbody", None),
    ("---x\nname: a\n---\nbody", None),
    ("---\nname: a\n----\n---\nbody", ("name: a\n----\n", "body")),
])
def test_front_matter_opens_and_closes_on_a_line_that_is_dashes_alone(text, parts):
    assert auto.front_matter(text, "f.md") == parts


@pytest.mark.parametrize("text", ["---\n", "---\nname: a\n", "---\nname: a\n--- \nbody"])
def test_front_matter_never_closed_is_refused(text):
    with pytest.raises(ValueError, match="f.md's front matter is never closed"):
        auto.front_matter(text, "f.md")


def test_read_skill_of_an_empty_file_is_empty(tmp_path):
    (tmp_path / "s.md").write_text("")
    assert auto.read_skill(str(tmp_path / "s.md")) == ""


def test_the_skill_comes_from_the_templates_file_without_its_front_matter():
    text = auto.read_skill()
    assert not text.startswith("---") and text.startswith("# Pre-push review")
    assert auto.SKILL_FILE == os.path.join(auto.OWNED, ".claude", "skills", "pre-push-review", "SKILL.md")


@pytest.mark.parametrize("text, body", [
    ("# no front matter\n---\nx\n", "# no front matter\n---\nx\n"),
    ("---\nname: a\ndescription: b --- c\n---\n\n# Body\n", "# Body\n"),
    ("---\nname: a\n---\n# Body\n---\nafter\n", "# Body\n---\nafter\n"),
])
def test_read_skill_leaves_out_the_front_matter_alone(tmp_path, text, body):
    (tmp_path / "s.md").write_text(text)
    assert auto.read_skill(str(tmp_path / "s.md")) == body


def test_the_deny_rules_word_for_word():
    assert auto.DENIED_TOOLS == (
        "Bash(git push:*)",
        "Bash(scripts/review.sh accept:*)",
        "Bash(scripts/review.sh accept-open:*)",
        "Bash(gh pr create:*)", "Bash(gh pr edit:*)", "Bash(gh pr comment:*)", "Bash(gh pr review:*)",
        "Bash(gh pr merge:*)", "Bash(gh pr ready:*)", "Bash(gh pr close:*)",
        "Bash(gh issue create:*)", "Bash(gh issue comment:*)", "Bash(gh issue edit:*)", "Bash(gh issue close:*)",
        "Bash(gh label:*)", "Bash(gh release:*)", "Bash(gh repo:*)", "Bash(gh secret:*)", "Bash(gh workflow:*)",
        f"Edit(/{auto.FACTORY}/**)", "Edit(~/.claude/**)",
    )


def test_the_review_log_is_the_owners_alone(tmp_path, monkeypatch):
    _, log, _, _ = review_once(tmp_path, monkeypatch)
    assert log.startswith(str(tmp_path / "logs")) and os.path.basename(log).startswith("review-TicketManager.") and log.endswith(".log")
    assert stat.S_IMODE(os.stat(log).st_mode) & 0o077 == 0
    assert stat.S_IMODE(os.stat(tmp_path / "logs").st_mode) == 0o700
    assert auto.LOG_DIR == os.path.expanduser("~/.cache/repo-ci-baseline/logs")


def test_an_existing_log_folder_is_set_to_0700_and_a_symlink_is_refused(tmp_path, monkeypatch):
    (tmp_path / "logs").mkdir(mode=0o755)
    os.chmod(tmp_path / "logs", 0o755)
    review_once(tmp_path, monkeypatch)
    assert stat.S_IMODE(os.stat(tmp_path / "logs").st_mode) == 0o700
    (tmp_path / "elsewhere").mkdir()
    link = tmp_path / "linked"
    link.symlink_to(tmp_path / "elsewhere")
    code, log, _ = auto.run_review(TM, str(tmp_path), log_dir=str(link), session_dir=str(tmp_path / "sessions"),
                                   **NO_LISTING)
    assert code == 127 and "isn't a folder of the user's own" in log
    monkeypatch.setattr(auto.os, "getuid", lambda: os.stat(tmp_path / "logs").st_uid + 1)
    code, log, _ = auto.run_review(TM, str(tmp_path), log_dir=str(tmp_path / "logs"),
                                   session_dir=str(tmp_path / "sessions"))
    assert code == 127 and "isn't a folder of the user's own" in log, "another user's folder is refused too"


def test_the_prompt_says_none_when_no_owned_file_changes():
    prompt = auto.review_command(TM, "/w", [], {}, AGENTS, SKILL_TEXT, "/t/run")[2]
    assert "These files the branch changes are Owned Baseline files, which the next Apply overwrites: none." in prompt


def test_a_review_session_past_its_time_is_stopped_and_noted(tmp_path, monkeypatch):
    session = FakeSession(raises=subprocess.TimeoutExpired("claude", 1))
    now = [0.0]

    def clock():
        now[0] += 60  # a minute a look
        return now[0]

    code, log, seen, _ = review_once(tmp_path, monkeypatch, session=session, clock=clock)
    assert code == 124
    assert now[0] >= auto.REVIEW_TIMEOUT_MINUTES * 60 > now[0] - 3 * 60, "it polls until its time is spent, and stops"
    assert len(session.waits) > 1 and max(session.waits) <= 0.5
    assert seen["ended"] == [session]
    assert f"ran past {auto.REVIEW_TIMEOUT_MINUTES} minutes" in Path(log).read_text()


def test_a_broken_rule_outranks_a_timeout(tmp_path, monkeypatch):
    now = [0.0]
    code, log, _, _ = review_once(tmp_path, monkeypatch, session=FakeSession(raises=subprocess.TimeoutExpired("c", 1)),
                                  clock=lambda: now.__setitem__(0, now[0] + 3600) or now[0], broke=["x"])
    assert code == auto.SESSION_BROKE_A_RULE
    text = Path(log).read_text()
    assert "ran past" in text and "the session broke a rule: x" in text


def test_an_interrupted_review_session_ends_its_group_and_passes_the_interrupt_on(tmp_path, monkeypatch):
    session = FakeSession(raises=KeyboardInterrupt())
    ended = []
    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, session=session, end=lambda s: ended.append(s) or True)
    assert ended == [session]


def test_a_group_that_does_not_go_away_is_a_broken_rule(tmp_path, monkeypatch):
    # What is left of it can still write after the check read the ledgers.
    code, log, seen, _ = review_once(tmp_path, monkeypatch, end=lambda s: False)
    assert code == auto.SESSION_BROKE_A_RULE
    rule = (f"the session's process group was still there {auto.GROUP_WAIT_SECONDS} s after it was killed, and can still"
            " write: see it with pgrep -l -g 999999, and end it with pkill -KILL -g 999999, before you resume")
    assert seen["broke"] == [rule]
    assert f"rollout.sh: the session broke a rule: {rule}" in Path(log).read_text()


def wait_gone(pid):
    """Whether pid ends (gone, or a zombie until its new parent reaps it) within five seconds."""
    for _ in range(100):
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return True
        try:
            state = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[0]
        except (FileNotFoundError, IndexError):
            return True
        if state == "Z":
            return True
        time.sleep(0.05)
    return False


def read_pid(path):
    for _ in range(200):
        if path.exists() and path.read_text().strip():
            return int(path.read_text())
        time.sleep(0.025)
    raise AssertionError(f"{path} was never written")


needs_proc = pytest.mark.skipif(not os.path.isdir("/proc"), reason="needs /proc to see a process end")


def real_session(tmp_path, script, timeout, signal_after_start=None):
    pid_file = tmp_path / "child.pid"
    worktree = tmp_path / "w"
    worktree.mkdir(exist_ok=True)

    def popen(command, **kwargs):
        child = subprocess.Popen(["bash", "-c", script.format(pid_file=pid_file)], **kwargs)
        if signal_after_start is not None:
            read_pid(pid_file)
            os.kill(os.getpid(), signal_after_start)
        return child

    code, log, _ = auto.run_review(TM, str(worktree), **NO_LISTING, popen=popen, changes=lambda path: [],
                                   timeout=timeout,
                                   log_dir=str(tmp_path / "logs"), session_dir=str(tmp_path / "sessions"),
                                   guard=lambda w: fake_guard(tmp_path),
                                   check=lambda g: [], skill=lambda: SKILL_TEXT, agents=lambda: AGENTS,
                                   record=lambda *args: (0, []))
    return code, log, read_pid(pid_file)


@needs_proc
def test_a_timed_out_session_takes_what_it_started_with_it(tmp_path):
    code, _, child = real_session(tmp_path, "sleep 60 & echo $! > {pid_file}; wait", timeout=2)
    assert code == 124
    assert wait_gone(child)


@needs_proc
def test_a_session_that_exits_takes_what_it_left_running_with_it(tmp_path):
    code, log, child = real_session(tmp_path, "sleep 60 & echo $! > {pid_file}; echo the session said this; exit 0", timeout=60)
    assert code == 0
    assert wait_gone(child)
    assert "the session said this" in Path(log).read_text()


@needs_proc
def test_a_sigterm_during_a_session_ends_its_group_and_becomes_an_exit(tmp_path):
    previous = signal.getsignal(signal.SIGTERM)
    with pytest.raises(SystemExit) as stop:
        real_session(tmp_path, "sleep 60 & echo $! > {pid_file}; wait", timeout=60, signal_after_start=signal.SIGTERM)
    assert stop.value.code == 128 + signal.SIGTERM
    assert wait_gone(read_pid(tmp_path / "child.pid"))
    assert signal.getsignal(signal.SIGTERM) is previous, "the handler is put back"


def test_a_signal_while_the_session_starts_still_ends_its_group(tmp_path, monkeypatch):
    session = FakeSession(code=0)

    def popen(command, **kwargs):
        os.kill(os.getpid(), signal.SIGHUP)  # lands between starting the session and holding it
        return session

    ended = []
    with pytest.raises(SystemExit) as stop:
        review_once(tmp_path, monkeypatch, popen=popen, end=lambda s: ended.append(s) or True)
    assert ended == [session] and stop.value.code == 128 + signal.SIGHUP


def test_a_signal_while_claude_fails_to_start_is_still_passed_on(tmp_path, monkeypatch):
    def popen(command, **kwargs):
        os.kill(os.getpid(), signal.SIGTERM)
        raise FileNotFoundError(2, "No such file", "claude")

    with pytest.raises(SystemExit) as stop:
        review_once(tmp_path, monkeypatch, popen=popen)
    assert stop.value.code == 128 + signal.SIGTERM


def test_a_second_signal_during_the_cleanup_neither_stops_it_nor_replaces_the_first(tmp_path, monkeypatch):
    killed = []

    def end(session):
        os.kill(os.getpid(), signal.SIGTERM)
        killed.append(session)  # the kill goes on past the second signal
        return True

    session = FakeSession(code=0)

    def popen(command, **kwargs):
        os.kill(os.getpid(), signal.SIGINT)
        return session

    with pytest.raises(KeyboardInterrupt):
        review_once(tmp_path, monkeypatch, popen=popen, end=end)
    assert killed == [session]


def test_a_signal_during_a_session_is_not_called_a_timeout(tmp_path, monkeypatch):
    session = FakeSession(raises=subprocess.TimeoutExpired("claude", 1))

    def popen(command, **kwargs):
        os.kill(os.getpid(), signal.SIGTERM)
        return session

    with pytest.raises(SystemExit):
        review_once(tmp_path, monkeypatch, popen=popen)
    (log,) = (tmp_path / "logs").iterdir()
    assert "ran past" not in log.read_text()


@pytest.mark.parametrize("signum, raised", [(signal.SIGINT, KeyboardInterrupt), (signal.SIGTERM, SystemExit),
                                            (signal.SIGHUP, SystemExit)])
def test_deferred_signals_hold_each_signal_and_restore_the_handlers(signum, raised):
    before = signal.getsignal(signum)
    with auto.deferred_signals() as got:
        os.kill(os.getpid(), signum)
        os.kill(os.getpid(), signal.SIGTERM if signum != signal.SIGTERM else signal.SIGHUP)
        time.sleep(0.05)
    assert got == [signum], "the first signal is the one held"
    assert signal.getsignal(signum) is before
    assert isinstance(auto.passed_on(signum), raised)
    if raised is SystemExit:
        assert auto.passed_on(signum).code == 128 + signum


@pytest.mark.parametrize("hold", [auto.deferred_signals, auto.round_signals])
def test_an_ignored_signal_stays_ignored(hold):
    previous = signal.signal(signal.SIGHUP, signal.SIG_IGN)  # nohup's
    try:
        with hold():
            assert signal.getsignal(signal.SIGHUP) is signal.SIG_IGN
            os.kill(os.getpid(), signal.SIGHUP)
            time.sleep(0.05)
        assert signal.getsignal(signal.SIGHUP) is signal.SIG_IGN
    finally:
        signal.signal(signal.SIGHUP, previous)


@pytest.mark.parametrize("signum", [signal.SIGTERM, signal.SIGHUP])
def test_round_signals_turn_sigterm_and_sighup_into_an_exit(signum):
    before = signal.getsignal(signum)
    with pytest.raises(SystemExit) as stop, auto.round_signals():
        os.kill(os.getpid(), signum)
        time.sleep(1)
    assert stop.value.code == 128 + signum
    assert signal.getsignal(signum) is before


def test_run_in_group_starts_its_own_group_and_ends_it_on_an_interrupt():
    seen, ended = {}, []

    def popen(command, **kwargs):
        seen.update(command=command, **kwargs)
        return FakeSession(code=3)

    assert auto.run_in_group(["x"], popen=popen, end=lambda child: ended.append(child) or True) == 3
    assert seen == {"command": ["x"], "start_new_session": True, "pass_fds": (),
                    "env": {**os.environ, **auto.NO_BUILD_SERVERS}}, "no build server is left to outlive it"
    assert len(ended) == 1, "its group is ended when it exits, too"
    with pytest.raises(SystemExit):
        auto.run_in_group(["x"], popen=lambda c, **k: _Raising(), end=lambda child: ended.append(child) or True)
    assert len(ended) == 2


def test_run_in_group_holds_a_second_signal_and_passes_the_first_on():
    ended = []

    class Signalled(FakeSession):
        def wait(self, timeout=None):
            os.kill(os.getpid(), signal.SIGTERM)
            os.kill(os.getpid(), signal.SIGINT)
            time.sleep(0.05)
            raise subprocess.TimeoutExpired("x", timeout)

    with pytest.raises(SystemExit) as stop:
        auto.run_in_group(["x"], popen=lambda c, **k: Signalled(), end=lambda child: ended.append(child) or True)
    assert stop.value.code == 128 + signal.SIGTERM and len(ended) == 1


def test_run_in_group_that_cant_start_passes_a_signal_on_before_its_error(capsys):
    def popen(command, **kwargs):
        os.kill(os.getpid(), signal.SIGHUP)
        time.sleep(0.05)
        raise FileNotFoundError(2, "No such file", "x")

    with pytest.raises(SystemExit) as stop:
        auto.run_in_group(["x"], popen=popen)
    assert stop.value.code == 128 + signal.SIGHUP
    assert capsys.readouterr().err == "", "only a group left behind is written as the signal wins"
    with pytest.raises(FileNotFoundError):
        auto.run_in_group(["/nonexistent/x"])


def test_run_in_group_passes_the_lock_on_to_the_command(monkeypatch):
    seen = {}
    monkeypatch.setattr(auto, "LOCK_FDS", [7])
    auto.run_in_group(["x"], popen=lambda c, **k: seen.update(k) or FakeSession())
    assert seen["pass_fds"] == (7,)


@needs_proc
def test_what_a_command_leaves_in_its_group_is_ended_and_lets_go_of_the_lock(tmp_path, monkeypatch):
    # A build server the gate leaves behind would hold the lock it was passed, and the resume would be refused.
    lock = str(tmp_path / "lock")
    handle = auto.take_lock(lock)
    monkeypatch.setattr(auto, "LOCK_FDS", [handle.fileno()])
    pid_file = tmp_path / "left.pid"
    assert auto.run_in_group(["bash", "-c", f"sleep 60 & echo $! > {pid_file}"]) == 0
    handle.close()
    assert wait_gone(read_pid(pid_file))
    again = auto.take_lock(lock)
    assert again is not None, "nothing the command left holds the lock"
    again.close()


def test_child_env_adds_the_build_server_switches_to_the_environment():
    assert auto.child_env({"PATH": "/bin", "UseSharedCompilation": "true"}) == {"PATH": "/bin", **auto.NO_BUILD_SERVERS}


class _Raising(FakeSession):
    def wait(self, timeout=None):
        raise SystemExit(143)


@needs_proc
def test_a_sigterm_of_the_round_ends_reapply_and_what_it_started(tmp_path):
    pid_file = tmp_path / "child.pid"

    def popen(command, **kwargs):
        child = subprocess.Popen(["bash", "-c", f"sleep 60 & echo $! > {pid_file}; wait"], **kwargs)
        read_pid(pid_file)
        os.kill(os.getpid(), signal.SIGTERM)
        return child

    with pytest.raises(SystemExit) as stop, auto.round_signals():
        auto.run_in_group(["reapply.sh"], popen=popen)
    assert stop.value.code == 143
    assert wait_gone(read_pid(pid_file))


def test_run_land_and_run_reapply_run_in_groups_of_their_own(monkeypatch):
    seen = []
    monkeypatch.setattr(auto, "run_in_group", lambda command: seen.append(command) or 5)
    assert auto.run_land([f"{TM}#130", f"{REST[0]}#5"]) == 5
    assert auto.run_reapply("#82", "/r/TicketManager") == 5
    assert seen == [[auto.LAND, f"{TM}#130", f"{REST[0]}#5"], [auto.REAPPLY, "--brings", "#82", "/r/TicketManager"]]
    assert auto.REAPPLY == rollout.REAPPLY


def test_rollout_hands_auto_the_reapply_that_runs_in_a_group(monkeypatch):
    seen = {}
    monkeypatch.setattr(auto, "run", lambda *a, **k: seen.update(k) or 0)
    rollout.main(["--auto", "--brings", "#82", "--root", "/r"], listed=LISTED, exists=lambda path: True,
                 out=io.StringIO(), err=io.StringIO())
    assert seen["reapply"] is auto.run_reapply


def test_wait_for_stops_at_a_signal_or_the_deadline():
    session = FakeSession(raises=subprocess.TimeoutExpired("claude", 1))
    assert auto.wait_for(session, 3600, [signal.SIGTERM]) is None and len(session.waits) == 1
    now = [0.0]
    session = FakeSession(raises=subprocess.TimeoutExpired("claude", 1))
    assert auto.wait_for(session, 2, [], clock=lambda: now.__setitem__(0, now[0] + 0.5) or now[0]) is None
    assert session.waits and max(session.waits) <= 0.5
    assert auto.wait_for(FakeSession(code=7), 2, []) == 7


def test_wait_for_stops_at_the_deadline_itself():
    # The clock reads exactly the deadline on the look after a poll: that is past its time.
    ticks = iter([0.0, 1.0, 2.0])
    session = FakeSession(raises=subprocess.TimeoutExpired("claude", 1))
    assert auto.wait_for(session, 2, [], clock=lambda: next(ticks)) is None


def test_a_session_that_broke_a_rule_is_its_own_code_and_says_what(tmp_path, monkeypatch):
    code, log, _, _ = review_once(tmp_path, monkeypatch, broke=["the remote chore/reapply-baseline moved"])
    assert code == auto.SESSION_BROKE_A_RULE
    assert "rollout.sh: the session broke a rule: the remote chore/reapply-baseline moved" in Path(log).read_text()


DROPPED = ("GH_TOKEN", "GITHUB_TOKEN", "gh_token", "RELEASE_PR_PAT", "AWS_PROFILE", "MY_API_KEY", "NPM_TOKEN",
           "DB_PASSWORD", "SSH_AUTH_SOCK", "SSH_AGENT_PID", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0",
           "GIT_CONFIG_KEY_9", "GIT_CONFIG_PARAMETERS", "GATE_SKIP_REVIEW", "CLIENT_SECRET", "GOOGLE_APPLICATION_CREDENTIALS",
           "AZURE_CLIENT_ID", "NPM_CONFIG__AUTH", "DOCKER_AUTH_CONFIG", "STRIPE_APIKEY", "API_KEY_PROD", "DATABASE_URL",
           "SENTRY_DSN", "GIT_ASKPASS", "SSH_ASKPASS", "GIT_SSH_COMMAND", "GIT_SSH", "DBUS_SESSION_BUS_ADDRESS",
           "GPG_PASSPHRASE", "SESSION_COOKIE", "MY_BEARER", "DOCKER_CREDENTIAL", "XSSH_AUTH_SOCK", "XAUTHORITY",
           "OPENAI_KEY", "STRIPE_KEY", "GOOGLE_CLOUD_PROJECT", "AZURE_TENANT", "AWS_REGION", "XCLAUDE_TOKEN", "MY_PAT",
           "SENTRY_AUTH", "GIT_SSH_VARIANT", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_AUTH_TOKEN",
           "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SSE_PORT",
           "CLAUDE_PID", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_ANYTHING")
KEPT = ("PATH", "HOME", "LANG", "LD_LIBRARY_PATH", "MONKEY", "KEYBOARD", "MYAWS_X",
        "GIT_CONFIG_NOSYSTEM", "GIT_AUTHOR_NAME", "XSSH_X", *(name for name in auto.CLAUDE_KEPT if name != "CLAUDE_CONFIG_DIR"))


def test_the_sessions_environment_has_no_secrets_but_claudes_sign_in(tmp_path):
    base = {name: "x" for name in DROPPED + KEPT + ("GIT_CONFIG_GLOBAL", "CLAUDE_CONFIG_DIR")}
    env = auto.session_env(base, str(tmp_path))
    assert {name for name in base if name in env and env[name] == "x"} == set(KEPT)
    assert env["CLAUDE_CONFIG_DIR"] == os.path.abspath("x"), "kept, as an absolute path"
    assert env["GIT_CONFIG_GLOBAL"] == str(tmp_path / "gitconfig") and (tmp_path / "gitconfig").read_text() == "", \
        "git reads an empty global config, not the person's"
    pairs = [(env[f"GIT_CONFIG_KEY_{n}"], env[f"GIT_CONFIG_VALUE_{n}"]) for n in range(int(env["GIT_CONFIG_COUNT"]))]
    assert pairs == list(auto.SESSION_GIT_CONFIG), "the caller's GIT_CONFIG_ entries are replaced, not kept"
    assert "GIT_CONFIG_KEY_9" not in env
    assert env["GIT_OPTIONAL_LOCKS"] == "0", "git status doesn't write the read-only worktree's index"


def test_the_sessions_caches_and_temporary_folders_are_its_own(tmp_path):
    env = auto.session_env({"PATH": "/bin"}, str(tmp_path))
    for name, folder in (("GH_CONFIG_DIR", "gh"), ("TMPDIR", "tmp"), ("NUGET_PACKAGES", "nuget-packages"),
                         ("NUGET_HTTP_CACHE_PATH", "nuget-http"), ("npm_config_store_dir", "pnpm-store"),
                         ("pnpm_config_store_dir", "pnpm-store"), ("XDG_CACHE_HOME", "cache"), ("DOTNET_CLI_HOME", "dotnet-home")):
        assert env[name] == str(tmp_path / folder) and (tmp_path / folder).is_dir(), name
    assert env["DOTNET_NOLOGO"] == env["DOTNET_CLI_TELEMETRY_OPTOUT"] == "1"


@pytest.mark.parametrize("flag, prefix", list(auto.CLOUD_SIGN_IN.items()))
def test_a_cloud_sign_in_keeps_its_own_variables(tmp_path, flag, prefix):
    others = [p for p in auto.CLOUD_SIGN_IN.values() if p != prefix]
    base = {flag: "1", f"{prefix}PROFILE": "p", "GH_TOKEN": "t", f"{prefix}SECRET_ACCESS_KEY": "s",
            **{f"{p}PROFILE": "q" for p in others}}
    env = auto.session_env(base, str(tmp_path))
    assert env[f"{prefix}PROFILE"] == "p" and "GH_TOKEN" not in env
    assert f"{prefix}SECRET_ACCESS_KEY" not in env, "even the cloud in use's secrets are left out"
    assert not any(f"{p}PROFILE" in env for p in others), "only the cloud in use keeps its variables"


@pytest.mark.parametrize("value, kept", [("1", True), ("true", True), (" Yes ", True), ("ON", True), ("0", False),
                                         ("false", False), ("", False), ("no", False), ("off", False), ("2", False)])
def test_a_cloud_flag_is_on_only_for_a_value_that_turns_it_on(tmp_path, value, kept):
    env = auto.session_env({"CLAUDE_CODE_USE_BEDROCK": value, "AWS_PROFILE": "p"}, str(tmp_path))
    assert ("AWS_PROFILE" in env) is kept


def test_git_in_the_sessions_environment_cannot_push_to_github(tmp_path):
    def git(*args, env=None):
        return subprocess.run(["git", "-C", str(tmp_path / "r"), *args], capture_output=True, text=True,
                              env={**(env or os.environ), "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"})

    (tmp_path / "r").mkdir()
    git("init", "-q", "-b", "main")
    env = auto.session_env(dict(os.environ), str(tmp_path / "s"))
    for url in ("https://github.com/acme/widget.git", "git@github.com:acme/widget.git", "ssh://git@github.com/acme/widget"):
        git("remote", "remove", "origin")
        git("remote", "add", "origin", url)
        push = git("remote", "get-url", "--push", "origin", env=env).stdout.strip()
        fetch = git("remote", "get-url", "origin", env=env).stdout.strip()
        assert push.startswith(auto.NO_PUSH) and fetch == url
    helpers = git("config", "--get-all", "credential.helper", env=env).stdout.splitlines()
    assert helpers == [""], "the credential helpers are emptied"


def git_repo(path):
    def git(*args):
        return subprocess.run(["git", "-C", str(path), *args], check=True, capture_output=True, text=True,
                              env={**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1",
                                   "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t", "GIT_COMMITTER_NAME": "t",
                                   "GIT_COMMITTER_EMAIL": "t@t"}).stdout.strip()
    path.mkdir(parents=True, exist_ok=True)
    git("init", "-q", "-b", "main")
    git("commit", "-q", "--allow-empty", "-m", "one")
    return git


class Guarded:
    """A real linked worktree of a primary checkout, with an Owned file equal to the Template's, the clone's ledger
    folder and the main checkout's .security; the remote read is faked (and recorded), the rest is real git."""

    def __init__(self, tmp_path):
        self.primary, self.worktree, self.owned = tmp_path / "main", tmp_path / "w", tmp_path / "owned"
        self.git = git_repo(self.primary)
        self.git("worktree", "add", "-q", "-b", "chore/reapply-baseline", str(self.worktree))
        (self.owned / "scripts").mkdir(parents=True)
        (self.owned / "scripts" / "gate.sh").write_text("gate\n")
        (self.worktree / "scripts").mkdir()
        (self.worktree / "scripts" / "gate.sh").write_text("gate\n")
        self.ledgers = self.primary / ".git" / "review"
        self.ledgers.mkdir()
        self.security = self.primary / ".security"
        self.security.mkdir()
        self.remote = {"sha": "abc\trefs/heads/chore/reapply-baseline"}
        self.changed = ["scripts/gate.sh"]
        self.reads = []

    def read(self, command, cwd=None):
        self.reads.append(command)
        if "ls-remote" in command:
            return self.remote["sha"]
        return auto.run_text(command, cwd)

    def guard(self):
        self.taken = auto.session_guard(str(self.worktree), owned=str(self.owned), read=self.read)
        return self.taken

    def check(self, **kwargs):
        changes = kwargs.pop("changes", lambda w: list(self.changed))
        return auto.broken_rules(self.taken, owned=str(self.owned), read=self.read, changes=changes, **kwargs)

    def ledger(self, name, findings, branch="chore/reapply-baseline", head="0" * 40, raw=None):
        path = self.ledgers / name
        path.write_text(raw if raw is not None else json.dumps({"branch": branch, "head": head, "findings": findings}))
        return path

    def entry(self, name, status):
        path = self.security / name
        path.write_text(f"# {name}\n\n- Status: {status}\n- Lens: Security\n\n## Weakness\n\nthe weakness\n")
        return path


def test_a_session_that_kept_the_rules_broke_none(tmp_path):
    g = Guarded(tmp_path)
    g.entry("0001-a.md", "open")
    g.guard()
    assert g.check() == []
    assert g.taken.ledgers == str(g.ledgers)
    assert g.taken.common == str(g.primary / ".git")
    assert g.taken.gitdir == str(g.primary / ".git" / "worktrees" / "w")
    assert g.taken.security == str(g.security)
    assert g.taken.link == f"gitdir: {g.primary / '.git' / 'worktrees' / 'w'}\n".encode()


CHANGED = "{} changed during the session (its HEAD, its branch or its files): the session reviews only, so it commits and edits nothing"
UNSEEN = ("{} path(s) in {} changed during the session, which git status may not show (an ignored file, such as"
          " node_modules): {}. The round's record and the gate on the push run the worktree's files as you, so nothing"
          " is run or recorded")


@pytest.mark.parametrize("how", ["commit", "edit", "new file", "branch moved", "checkout"])
def test_a_session_that_commits_or_edits_in_the_worktree_is_caught(tmp_path, how):
    g = Guarded(tmp_path)
    g.guard()
    wt = ["-C", str(g.worktree)]
    if how == "commit":
        g.git(*wt, "commit", "-q", "--allow-empty", "-m", "the session's")
    elif how == "edit":
        (g.worktree / "README.md").write_text("x")
        g.git(*wt, "add", "README.md")
    elif how == "new file":
        (g.worktree / "src.cs").write_text("x")
    elif how == "branch moved":
        g.git("commit", "-q", "--allow-empty", "-m", "two")
        g.git("update-ref", "refs/heads/chore/reapply-baseline", "main")
    else:
        g.git(*wt, "checkout", "-q", "--detach")
        g.git(*wt, "commit", "-q", "--allow-empty", "-m", "elsewhere")
    named = {"edit": "'README.md'", "new file": "'src.cs'"}.get(how)
    assert g.check(changes=lambda w: []) == [CHANGED.format(g.worktree)] + (
        [UNSEEN.format(1, g.worktree, named)] if named else []), "the snapshot names a file the status counts too"


@pytest.mark.parametrize("failing, hint", [
    (0, "rev-parse HEAD"),
    (1, "rev-parse --verify refs/heads/chore/reapply-baseline"),
    (2, "status --porcelain=v1 -z --untracked-files=all"),
])
def test_the_worktree_read_that_fails_is_the_one_the_hint_names(tmp_path, failing, hint):
    # The branch read's --quiet would keep its failure silent: the hint runs it without.
    g = Guarded(tmp_path)
    read, commands = g.read, auto.worktree_state_commands(str(g.worktree))
    g.read = lambda command, cwd=None: None if command == commands[failing] else read(command, cwd)
    with pytest.raises(auto.GuardUnreadable) as refused:
        g.guard()
    assert str(refused.value) == (f"can't read the HEAD, the branch chore/reapply-baseline or the status of {g.worktree};"
                                  f" see why with: git -C {g.worktree} {hint}")


def test_a_worktree_state_that_cant_be_read_is_refused_before_and_broken_after(tmp_path):
    g = Guarded(tmp_path)
    read = g.read
    g.read = lambda command, cwd=None: None if "status" in command else read(command, cwd)
    with pytest.raises(auto.GuardUnreadable) as refused:
        g.guard()
    assert str(refused.value) == (f"can't read the HEAD, the branch chore/reapply-baseline or the status of {g.worktree};"
                                  f" see why with: git -C {g.worktree} status --porcelain=v1 -z --untracked-files=all")
    g.read = read
    g.guard()
    g.read = lambda command, cwd=None: None if "HEAD" in command else read(command, cwd)
    assert g.check(changes=lambda w: []) == [f"can't read the HEAD, the branch chore/reapply-baseline or the status of {g.worktree}"
                                             " after the session"]


def test_the_worktree_state_reads_word_for_word():
    assert auto.worktree_state_commands("/w") == [
        ["git", "-C", "/w", "rev-parse", "HEAD"],
        ["git", "-C", "/w", "rev-parse", "--verify", "--quiet", "refs/heads/chore/reapply-baseline"],
        ["git", "-C", "/w", "status", "--porcelain=v1", "-z", "--untracked-files=all"]]


def test_the_guard_reads_the_remote_branch_by_its_full_name_before_and_after(tmp_path):
    g = Guarded(tmp_path)
    g.guard()
    g.check()
    remote = ["git", "-C", str(g.worktree), "ls-remote", "origin", "refs/heads/chore/reapply-baseline"]
    assert [command for command in g.reads if "ls-remote" in command] == [remote, remote]


def test_a_guard_that_cant_read_the_link_the_remote_or_the_git_folders_starts_no_session(tmp_path):
    g = Guarded(tmp_path)
    g.remote["sha"] = None
    with pytest.raises(auto.GuardUnreadable) as refused:
        g.guard()
    assert str(refused.value) == ("can't read the remote chore/reapply-baseline before the session (the network, or git);"
                                  f" see why with: git -C {g.worktree} ls-remote origin refs/heads/chore/reapply-baseline")
    with pytest.raises(auto.GuardUnreadable) as refused:
        auto.session_guard(str(tmp_path / "not-a-repo"), owned=str(g.owned), read=lambda c, cwd=None: "")
    assert str(refused.value) == (f"{tmp_path / 'not-a-repo'}/.git isn't the plain file a linked worktree has (see: ls -la"
                                  f" {tmp_path / 'not-a-repo' / '.git'})")
    with pytest.raises(auto.GuardUnreadable, match=r"\.git isn't the plain file"):
        auto.session_guard(str(g.primary), owned=str(g.owned), read=lambda c, cwd=None: "")
    for missing in ("--git-common-dir", "--git-dir"):
        with pytest.raises(auto.GuardUnreadable) as refused:
            auto.session_guard(str(g.worktree), owned=str(g.owned),
                               read=lambda c, cwd=None, missing=missing: None if missing in c else "" if "ls-remote" in c else "/x")
        assert str(refused.value) == (f"can't find the git folders of {g.worktree} (git 2.31 or later reads them); see why"
                                      f" with: git -C {g.worktree} rev-parse --path-format=absolute {missing}"), "the read that failed"


def test_a_remote_branch_that_doesnt_exist_yet_is_a_reading_not_a_failure(tmp_path):
    g = Guarded(tmp_path)
    g.remote["sha"] = ""
    g.guard()
    assert g.check() == []
    g.remote["sha"] = "abc\trefs/heads/chore/reapply-baseline"
    assert g.check() == ["the remote chore/reapply-baseline moved during the session (the session, or anyone who pushed to it meanwhile)"]


def test_a_session_after_which_the_remote_moved_or_cant_be_read_is_a_stop(tmp_path):
    g = Guarded(tmp_path)
    g.guard()
    g.remote["sha"] = "def\trefs/heads/chore/reapply-baseline"
    assert g.check() == ["the remote chore/reapply-baseline moved during the session (the session, or anyone who pushed to it meanwhile)"]
    g.remote["sha"] = None
    assert g.check() == ["can't read the remote chore/reapply-baseline after the session, so whether it was pushed can't be told"]


@pytest.mark.parametrize("how", ["rewritten", "removed", "symlink", "folder"])
def test_a_changed_git_link_is_a_stop_and_no_git_runs_in_the_worktree(tmp_path, how):
    g = Guarded(tmp_path)
    g.guard()
    link = g.worktree / ".git"
    link.unlink()
    if how == "rewritten":
        link.write_text(f"gitdir: {tmp_path / 'planted'}\n")
    elif how == "symlink":
        link.symlink_to(g.primary / ".git" / "worktrees" / "w")
    elif how == "folder":
        link.mkdir()
    g.reads.clear()
    listed = []
    broke = g.check(changes=lambda w: listed.append(w) or [])
    assert len(broke) == 1 and broke[0].startswith(f"{link} changed during the session")
    assert g.reads == [] and listed == [], "neither the remote read nor the Owned list runs git there"


def test_a_template_that_changed_during_the_session_is_a_stop(tmp_path):
    g = Guarded(tmp_path)
    g.guard()
    (g.owned / "new.md").write_text("x")
    assert g.check() == ["the Template changed during the session (the session, or your own edit in the Factory checkout)"]


def test_an_owned_file_edited_or_deleted_by_the_session_is_caught(tmp_path):
    g = Guarded(tmp_path)
    g.guard()
    owned = "scripts/gate.sh is an Owned file and no longer the Template's: edit the Template, not the copy"
    (g.worktree / "scripts" / "gate.sh").write_text("gate, edited\n")
    assert g.check() == [owned, UNSEEN.format(1, g.worktree, "'scripts/gate.sh'")], (
        "an ignored file edited: the status reads the same, the snapshot and the Owned check catch it")
    (g.worktree / "scripts" / "gate.sh").unlink()
    assert g.check() == [CHANGED.format(g.worktree), owned, UNSEEN.format(1, g.worktree, "'scripts/gate.sh'")]


def test_an_ignored_file_the_session_changed_is_caught(tmp_path):
    # git status shows no ignored file, and the round runs the worktree's scripts/review.sh (node_modules/.bin/tsx)
    # after the session, outside the sandbox.
    g = Guarded(tmp_path)
    (g.worktree / ".gitignore").write_text("node_modules/\n")
    (g.worktree / "node_modules" / ".bin").mkdir(parents=True)
    for name in ("tsx", "a", "b", "c", "d"):
        (g.worktree / "node_modules" / ".bin" / name).write_text(f"{name}\n")
    g.guard()
    assert g.check() == []
    (g.worktree / "node_modules" / ".bin" / "tsx").write_text("planted\n")
    assert g.check() == [UNSEEN.format(1, g.worktree, "'node_modules/.bin/tsx'")]
    for name in ("d", "c"):
        (g.worktree / "node_modules" / ".bin" / name).write_text("planted\n")
    assert g.check() == [UNSEEN.format(3, g.worktree, "'node_modules/.bin/c', 'node_modules/.bin/d',"
                                       " 'node_modules/.bin/tsx'")], "three are named whole"
    (g.worktree / "node_modules" / ".bin" / "b").write_text("planted\n")
    assert g.check() == [UNSEEN.format(4, g.worktree, "'node_modules/.bin/b', 'node_modules/.bin/c',"
                                       " 'node_modules/.bin/d' and 1 more")], "the first three, sorted, and how many more"
    (g.worktree / "node_modules" / ".bin" / "a").write_text("planted\n")
    assert g.check() == [UNSEEN.format(5, g.worktree, "'node_modules/.bin/a', 'node_modules/.bin/b',"
                                       " 'node_modules/.bin/c' and 2 more")]


def test_a_hard_link_made_elsewhere_changes_nothing(tmp_path):
    # pnpm hard-links node_modules files from its store: another project's install changes their link count and
    # ctime, never their bytes.
    root = tmp_path / "w"
    (root / "node_modules").mkdir(parents=True)
    (root / "node_modules" / "f").write_text("x")
    before = auto.tree_state(str(root))
    os.link(root / "node_modules" / "f", tmp_path / "elsewhere")
    assert auto.tree_changes(before, auto.tree_state(str(root))) == []
    (tmp_path / "elsewhere").unlink()
    assert auto.tree_changes(before, auto.tree_state(str(root))) == []


@pytest.mark.parametrize("how", ["write", "same size", "chmod", "new file", "new folder", "remove", "rename", "symlink",
                                 "relink", "fifo", "fifo mode", "nested git", "root mode"])
def test_tree_state_catches_every_kind_of_change_under_the_root(tmp_path, how):
    root = tmp_path / "w"
    (root / "a" / "b").mkdir(parents=True)
    f = root / "a" / "b" / "f"
    f.write_text("x")
    (root / "a" / "l").symlink_to("b")
    (root / ".git").write_text("gitdir: x\n")
    (root / "a" / ".git").write_text("a package's own\n")
    os.mkfifo(root / "a" / "p", 0o600)
    before = auto.tree_state(str(root))
    assert auto.tree_changes(before, auto.tree_state(str(root))) == []
    expected = {"write": ["a/b/f"], "same size": ["a/b/f"], "chmod": ["a/b/f"], "new file": ["a/b/g"],
                "new folder": ["a/c"], "remove": ["a/b/f"], "rename": ["a/b/f", "a/b/h"], "symlink": ["a/b/l"],
                "relink": ["a/l"], "fifo": ["a/b/f"], "fifo mode": ["a/p"], "nested git": ["a/.git"],
                "root mode": ["."]}[how]
    if how == "write":
        f.write_text("yy")
    elif how == "same size":
        f.write_text("y")
    elif how == "chmod":
        f.chmod(0o700)
    elif how == "new file":
        (root / "a" / "b" / "g").write_text("y")
    elif how == "new folder":
        (root / "a" / "c").mkdir()
    elif how == "remove":
        f.unlink()
    elif how == "rename":
        f.rename(root / "a" / "b" / "h")
    elif how == "symlink":
        (root / "a" / "b" / "l").symlink_to("/etc/passwd")
    elif how == "relink":
        (root / "a" / "l").unlink()
        (root / "a" / "l").symlink_to("/etc")
    elif how == "fifo":
        f.unlink()
        os.mkfifo(f)
    elif how == "fifo mode":
        (root / "a" / "p").chmod(0o640)
    elif how == "nested git":
        (root / "a" / ".git").write_text("planted\n")
    else:
        root.chmod(0o755 if stat.S_IMODE(root.stat().st_mode) != 0o755 else 0o750)
    assert auto.tree_changes(before, auto.tree_state(str(root))) == expected, how


def test_tree_state_leaves_the_git_link_to_its_own_check_and_never_follows_a_symlink(tmp_path):
    root, outside = tmp_path / "w", tmp_path / "outside"
    root.mkdir()
    outside.mkdir()
    (outside / "f").write_text("x")
    (root / "out").symlink_to(outside)
    (root / ".git").write_text("gitdir: x\n")
    before = auto.tree_state(str(root))
    assert ".git" not in before and "out/f" not in before
    (root / ".git").write_text("gitdir: y\n")
    (outside / "f").write_text("y")
    assert auto.tree_changes(before, auto.tree_state(str(root))) == [], "the .git link is broken_rules' own check"
    assert auto.tree_state(str(tmp_path / "none")) == {".": None}


@pytest.mark.skipif(os.geteuid() == 0, reason="root reads a folder whatever its mode")
def test_a_folder_that_cant_be_read_is_read_the_same_each_time_and_its_mode_still_counts(tmp_path):
    root = tmp_path / "w"
    (root / "locked").mkdir(parents=True)
    (root / "locked" / "f").write_text("x")
    (root / "secret").write_text("x")
    (root / "locked").chmod(0)
    (root / "secret").chmod(0)
    try:
        before = auto.tree_state(str(root))
        assert before["locked"] == ("dir", stat.S_IFDIR) and before["secret"][0] == "unread"
        assert auto.tree_changes(before, auto.tree_state(str(root))) == [], "unreadable, but no change: no stop"
        (root / "locked").chmod(0o700)
        assert auto.tree_changes(before, auto.tree_state(str(root))) == ["locked", "locked/f"]
    finally:
        (root / "locked").chmod(0o700)
        (root / "secret").chmod(0o600)


@pytest.mark.parametrize("what", ["config", "config.worktree", "hooks", "info", "worktree config.worktree"])
def test_a_change_to_the_clones_git_config_or_hooks_is_caught_and_no_git_runs(tmp_path, what):
    g = Guarded(tmp_path)
    (g.primary / ".git" / "hooks").mkdir(exist_ok=True)
    g.guard()
    assert g.check() == []
    common, gitdir = g.primary / ".git", g.primary / ".git" / "worktrees" / "w"
    if what == "config":
        with open(common / "config", "a") as handle:
            handle.write("[core]\n\tfsmonitor = /tmp/planted\n")
    elif what == "config.worktree":
        (common / "config.worktree").write_text("[core]\n\thooksPath = /tmp\n")
    elif what == "hooks":
        (common / "hooks" / "post-index-change").write_text("#!/bin/sh\n")
    elif what == "info":
        (common / "info").mkdir(exist_ok=True)
        (common / "info" / "attributes").write_text("* diff=planted\n")
    else:
        (gitdir / "config.worktree").write_text("[core]\n\tfsmonitor = /tmp/planted\n")
    g.reads.clear()
    listed = []
    broke = g.check(changes=lambda w: listed.append(w) or [])
    assert broke[0].startswith(f"the clone's git {what} changed during the session (in {common}): git can run a"
                               " command they name, so it wasn't run there after the session"), broke
    assert g.reads == [] and listed == [], "neither the remote read nor the Owned list runs git there"


@pytest.mark.skipif(os.geteuid() == 0, reason="root reads a file whatever its mode")
def test_a_file_that_cant_be_read_swapped_for_one_of_its_size_is_a_change(tmp_path):
    root = tmp_path / "w"
    root.mkdir()
    (root / "tool").write_text("one")
    (root / "tool").chmod(0o111)
    try:
        before = auto.tree_state(str(root))
        assert before["tool"][0] == "unread"
        old = os.stat(root / "tool")
        (root / "new").write_text("two")
        (root / "new").chmod(0o111)
        os.utime(root / "new", ns=(old.st_atime_ns, old.st_mtime_ns))
        os.replace(root / "new", root / "tool")
        assert auto.tree_state(str(root))["tool"][4] == old.st_mtime_ns, "only the inode tells this swap"
        assert auto.tree_changes(before, auto.tree_state(str(root))) == ["tool"]
        before = auto.tree_state(str(root))
        inode = os.stat(root / "tool").st_ino
        (root / "tool").chmod(0o600)
        (root / "tool").write_text("six")
        (root / "tool").chmod(0o111)
        os.utime(root / "tool", ns=(old.st_atime_ns, old.st_mtime_ns + 10**9))
        assert os.stat(root / "tool").st_ino == inode, "only the mtime tells this rewrite"
        assert auto.tree_changes(before, auto.tree_state(str(root))) == ["tool"]
        before = auto.tree_state(str(root))
        kept = os.stat(root / "tool")
        (root / "tool").chmod(0o600)
        (root / "tool").write_text("longer")
        (root / "tool").chmod(0o111)
        os.utime(root / "tool", ns=(kept.st_atime_ns, kept.st_mtime_ns))
        now = os.stat(root / "tool")
        assert (now.st_ino, now.st_mtime_ns, now.st_mode) == (kept.st_ino, kept.st_mtime_ns, kept.st_mode), \
            "only the size tells this rewrite"
        assert auto.tree_changes(before, auto.tree_state(str(root))) == ["tool"]
    finally:
        for name in ("tool", "new"):
            if (root / name).exists():
                (root / name).chmod(0o600)


def test_the_guards_snapshots_have_no_default_so_a_guard_without_them_cant_be_made():
    fields = {field.name: field for field in dataclasses.fields(auto.Guard)}
    for name in ("files", "git_files"):
        assert fields[name].default is dataclasses.MISSING and fields[name].default_factory is dataclasses.MISSING


def test_entry_state_reads_a_file_whole_in_chunks(tmp_path):
    big = tmp_path / "big"
    big.write_bytes(b"a" * (3 << 20) + b"b")
    assert auto.entry_state(str(big))[2] == hashlib.sha256(b"a" * (3 << 20) + b"b").hexdigest()


def test_an_owned_list_that_fails_after_the_session_is_a_stop(tmp_path):
    g = Guarded(tmp_path)
    g.guard()

    def broken(worktree):
        raise RuntimeError("git diff failed")

    assert g.check(changes=broken) == ["can't list the Owned files the branch changes: git diff failed"]


FOLDER_CHANGED = ("{!a} in {} was {} during the session, which can't write {}: your own review or record on another"
                  " branch meanwhile, or a sandbox that didn't hold")


@pytest.mark.parametrize("where, what", [("ledgers", "the review ledgers"), ("security", "the .security record")])
@pytest.mark.parametrize("how", ["added", "removed", "changed"])
@pytest.mark.parametrize("name", ["l.json", "l.entries", ".lock", "0001-a.md", "README.md", "x.tmp"])
def test_any_file_added_removed_or_changed_in_the_ledgers_or_the_security_record_is_caught(tmp_path, where, what, how,
                                                                                            name):
    # The round records, not the session, so the session writes neither folder: whatever a file is called, a lock or a
    # temporary file included, a change is a stop.
    g = Guarded(tmp_path)
    folder = getattr(g, where)
    if how != "added":
        (folder / name).write_text("before")
    g.guard()
    if how == "removed":
        (folder / name).unlink()
    else:
        (folder / name).write_text("after")
    assert g.check() == [FOLDER_CHANGED.format(name, folder, how, what)]


def test_files_there_before_and_unchanged_are_no_stop_whatever_they_hold(tmp_path):
    g = Guarded(tmp_path)
    g.ledger("old-bad.json", [], raw="{")
    g.ledger("l.json", [{"id": "R1", "status": "accepted"}])
    g.entry("0001-a.md", "fixed")
    (g.ledgers / ".lock").write_text("1 abc\n")
    g.guard()
    assert g.check() == []


def test_a_folder_that_isnt_there_reads_as_empty_and_one_made_with_files_is_caught(tmp_path):
    assert auto.folder_state(str(tmp_path / "none")) == {}
    g = Guarded(tmp_path)
    g.ledgers.rmdir()
    g.security.rmdir()
    g.guard()
    assert g.check() == []
    g.security.mkdir()
    g.entry("0001-a.md", "open")
    assert g.check() == [FOLDER_CHANGED.format("0001-a.md", g.security, "added", "the .security record")]


@pytest.mark.parametrize("where", ["ledgers", "security"])
def test_a_file_that_didnt_read_before_and_was_swapped_for_another_is_caught(tmp_path, where):
    g = Guarded(tmp_path)
    folder = getattr(g, where)
    (tmp_path / "one").write_text("x")
    (tmp_path / "two").write_text("x")
    (folder / "f").symlink_to(tmp_path / "one")
    g.guard()
    assert g.check() == []
    (folder / "f").unlink()
    (folder / "f").symlink_to(tmp_path / "two")
    assert len(g.check()) == 1 and g.check()[0].startswith(f"'f' in {folder} was changed during the session")


@needs_proc
@pytest.mark.parametrize("what", ["fifo", "zero", "dangling", "oversized"])
def test_a_file_that_isnt_a_small_regular_one_is_never_read_and_is_caught(tmp_path, what, monkeypatch):
    # Nothing in either folder is read in a way that hangs or fills memory (record_bytes).
    g = Guarded(tmp_path)
    g.guard()
    path = g.ledgers / "x.json"
    if what == "fifo":
        os.mkfifo(path)
    elif what == "zero":
        path.symlink_to("/dev/zero")
    elif what == "dangling":
        path.symlink_to(tmp_path / "nowhere.json")
    else:
        monkeypatch.setattr(auto, "RECORD_BYTES", 10)
        path.write_text(json.dumps({"findings": []}))
    assert g.check() == [FOLDER_CHANGED.format(path.name, path.parent, "added", "the review ledgers")]


def test_a_file_name_is_printed_with_its_control_characters_escaped(tmp_path):
    g = Guarded(tmp_path)
    g.guard()
    (g.ledgers / "a\x1b[2J\nrollout.sh: all is well\u202e.json").write_text("x")
    [rule] = g.check()
    assert "\x1b" not in rule and "\n" not in rule and "\u202e" not in rule
    assert rule.startswith(f"'a\\x1b[2J\\nrollout.sh: all is well\\u202e.json' in {g.ledgers} was added during the session")


def test_shown_quotes_and_escapes_what_the_session_wrote():
    assert auto.shown("R1") == "'R1'"
    assert auto.shown("R1\x1b]0;x\x07\n") == "'R1\\x1b]0;x\\x07\\n'"
    assert auto.shown(None) == "None" and auto.shown(["x"]) == "['x']"


def test_an_unread_files_identity_is_what_lstat_says_and_where_it_leads(tmp_path):
    (tmp_path / "l").symlink_to("/somewhere")
    info = os.lstat(tmp_path / "l")
    assert auto.identity(str(tmp_path / "l"), None) == (f"unread {info.st_mode:o} {info.st_ino} {info.st_size}"
                                                        f" {info.st_mtime_ns} /somewhere")
    assert auto.identity(str(tmp_path / "missing"), None) == "gone"
    assert auto.identity("x", b"abc") == hashlib.sha256(b"abc").hexdigest()


def test_record_bytes_reads_a_regular_file_up_to_its_limit_only(tmp_path):
    path = tmp_path / "f"
    path.write_bytes(b"x" * 10)
    assert auto.record_bytes(str(path), limit=10) == b"x" * 10
    assert auto.record_bytes(str(path), limit=9) is None
    assert auto.record_bytes(str(path), limit=11) == b"x" * 10
    assert auto.record_bytes(str(tmp_path / "missing")) is None
    assert auto.record_bytes(str(tmp_path)) is None, "a folder isn't read"


OPEN = {"id": "R1", "lens": "Bar", "path": "a.py", "line": 1, "summary": "s", "severity": "critical", "security": False,
        "status": "open", "test": None, "reason": None, "fix": "f"}
NOT_SECURITY = {"severity": "critical", "security": False, "lens": "Tests"}


def stage(tmp_path, findings=None, raw=None):
    run = tmp_path / "run"
    run.mkdir(exist_ok=True)
    (run / auto.STAGED_FILE).write_text(raw if raw is not None else json.dumps(findings))
    return str(run)


def test_staged_findings_kept_open_or_dismissed_break_nothing(tmp_path):
    findings = [OPEN, {**OPEN, "id": "R2", "severity": "warning", "security": True, "lens": "Security"},
                {**OPEN, "id": "R3", **NOT_SECURITY, "status": "dismissed", "reason": "r"}]
    assert auto.staged_findings(stage(tmp_path, findings)) == (findings, [])
    assert auto.staged_findings(stage(tmp_path, [])) == ([], []), "a round that keeps nothing stages []"


def test_nothing_staged_is_no_findings_and_no_broken_rule(tmp_path):
    assert auto.staged_findings(str(tmp_path)) == (None, [])


@pytest.mark.parametrize("status", ["fixed", "accepted", None, 5, "Open"])
def test_a_finding_staged_fixed_accepted_or_otherwise_breaks_a_rule(tmp_path, status):
    findings, broke = auto.staged_findings(stage(tmp_path, [OPEN, {**OPEN, "id": "R2", "status": status}]))
    assert findings is None
    assert broke == [f"'R2' was staged {status!r}: a review-only session stages a finding open or dismissed, since it"
                     " fixes nothing and only the person accepts"]


@pytest.mark.parametrize("finding, security", [
    ({"severity": "warning", "security": False, "lens": "Tests"}, True),
    ({"severity": "critical", "security": True, "lens": "Tests"}, True),
    ({"severity": "critical", "lens": "Tests"}, True),
    ({"severity": "critical", "security": False, "lens": "Bar/Security"}, True),
    ({"severity": "critical", "security": False, "lens": "SECURITY"}, True),
    ({"severity": "critical", "security": False, "lens": "Sec\u200burity"}, True),
    ({"severity": "critical", "security": False, "lens": "Sec\u00adurity"}, True),
    ({"severity": "critical", "security": False, "lens": "Sec urity"}, True),
    ({"severity": "critical", "security": False}, True),
    ({"severity": "critical", "security": False, "lens": None}, True),
    ({"severity": "critical", "security": False, "lens": 5}, True),
    (NOT_SECURITY, False),
    ({"severity": "advisory", "security": False, "lens": "Docs match the code"}, False),
])
def test_a_security_finding_staged_dismissed_breaks_a_rule_read_wider_than_the_ledger(tmp_path, finding, security):
    # The ledger reads the lens with its control and invisible characters stripped: read here with all but its
    # letters dropped, a lens can't hide security from the check and show it to the ledger.
    dismissed = {**{key: value for key, value in OPEN.items() if key not in ("security", "lens")}, **finding,
                 "status": "dismissed", "reason": "r"}
    findings, broke = auto.staged_findings(stage(tmp_path, [dismissed]))
    assert auto.is_security(dismissed) is security
    assert broke == (["'R1' was staged dismissed, and may be a security finding: only the person decides one, so the"
                      " session records it open"] if security else [])
    assert findings == (None if security else [dismissed])


@pytest.mark.parametrize("raw", ["{", "{}", '{"findings": []}', "5", '["x"]', "[[]]", "\xff", "[" * 100000])
def test_staged_findings_that_arent_a_json_array_of_objects_break_a_rule(tmp_path, raw):
    run = tmp_path / "run"
    run.mkdir()
    (run / auto.STAGED_FILE).write_bytes(raw.encode("latin-1"))
    assert auto.staged_findings(str(run)) == (None, [f"{run / auto.STAGED_FILE} isn't the JSON array of findings"
                                                     " scripts/review.sh stage writes"])


@needs_proc
@pytest.mark.parametrize("what", ["fifo", "symlink", "dangling", "folder", "oversized"])
def test_staged_findings_that_arent_a_small_regular_file_are_never_read(tmp_path, what, monkeypatch):
    run = tmp_path / "run"
    run.mkdir()
    path = run / auto.STAGED_FILE
    if what == "fifo":
        os.mkfifo(path)
    elif what == "symlink":
        (tmp_path / "secret").write_text(json.dumps([OPEN]))
        path.symlink_to(tmp_path / "secret")
    elif what == "dangling":
        path.symlink_to(tmp_path / "nowhere")  # not "nothing staged": a link is there
    elif what == "folder":
        path.mkdir()
    else:
        monkeypatch.setattr(auto, "RECORD_BYTES", 10)
        path.write_text(json.dumps([OPEN]))
    assert auto.staged_findings(str(run)) == (None, [f"{path} isn't the JSON array of findings scripts/review.sh stage"
                                                     " writes"])


def test_shown_cuts_a_text_just_past_its_length():
    assert auto.shown("x" * 119) == "'" + "x" * 119 + "... (121 characters)"


def test_shown_cuts_a_long_text_so_it_cant_flood_the_terminal():
    assert auto.shown("R1") == "'R1'"
    assert auto.shown("x" * 118) == "'" + "x" * 118 + "'", "120 characters, quotes included, are shown whole"
    long = auto.shown("x" * 10_000)
    assert long == "'" + "x" * 119 + "... (10002 characters)"


def test_a_staged_id_and_status_are_printed_escaped(tmp_path):
    _, broke = auto.staged_findings(stage(tmp_path, [{**OPEN, "id": "R1\x1b[2J\nrollout.sh: ok", "status": "fixed\x07"}]))
    assert broke == ["'R1\\x1b[2J\\nrollout.sh: ok' was staged 'fixed\\x07': a review-only session stages a finding"
                     " open or dismissed, since it fixes nothing and only the person accepts"]


HEAD = "c" * 40
KEY_AND_HEAD = "k" * 64 + "\n" + HEAD + "\n"


class Recorder:
    """run_bounded as record_review runs it with scripts/review.sh: prepare prints the key and HEAD on its stdout and a
    note on its stderr, record reads its input and prints. A code of None is a command that ran past its time."""

    def __init__(self, prepare=(0, KEY_AND_HEAD), record=0):
        self.prepare, self.record, self.calls = prepare, record, []

    def __call__(self, command, **kwargs):
        self.calls.append((command, kwargs))
        if command[1] == "prepare":
            assert os.path.isabs(command[2]) and not os.path.exists(command[2]), "prepare makes the folder it is given"
            kwargs["stdout"].write(self.prepare[1])
            kwargs["stderr"].write("prepare's stderr\n")
            return self.prepare[0]
        staged = command[4]
        self.staged = (json.loads(Path(staged, auto.STAGED_FILE).read_text()),
                       stat.S_IMODE(os.stat(staged).st_mode), stat.S_IMODE(os.stat(Path(staged, auto.STAGED_FILE)).st_mode))
        self.input = json.loads(kwargs["stdin"].read())
        kwargs["stdout"].write("Review recorded.\n")
        return self.record


def recorded(tmp_path, findings, recorder, head=HEAD, out=None):
    out = out if out is not None else io.StringIO()
    run = stage(tmp_path, findings) if findings is not None else str(tmp_path)
    return auto.record_review("/w", run, out, head, str(tmp_path / "log.staged"), bounded=recorder,
                              installed=lambda w: True), out


def kept(tmp_path):
    folder = tmp_path / "log.staged"
    return (json.loads((folder / auto.STAGED_FILE).read_text()), stat.S_IMODE(folder.stat().st_mode),
            stat.S_IMODE((folder / auto.STAGED_FILE).stat().st_mode), auto.kept_head(str(folder)))


def test_the_round_records_what_the_session_staged_with_the_worktrees_own_review_script(tmp_path):
    recorder = Recorder()
    (result, out) = recorded(tmp_path, [OPEN], recorder)
    assert result == (0, [])
    (prepare, prepare_kwargs), (record, record_kwargs) = recorder.calls
    assert prepare[:2] == ["/w/scripts/review.sh", "prepare"] and prepare_kwargs["cwd"] == "/w"
    assert prepare_kwargs["stdin"] is subprocess.DEVNULL
    assert prepare_kwargs["stderr"] is out, "prepare's notes go to the log"
    assert prepare_kwargs["stdout"] is not out, "its key and HEAD are read, not logged"
    assert record[:4] == ["/w/scripts/review.sh", "record", "-", "--staged"] and record_kwargs["cwd"] == "/w"
    assert record_kwargs["stdout"] is out and record_kwargs["stderr"] is subprocess.STDOUT, \
        "record's .security notes, on its stderr, go to the log too"
    assert not record[4].startswith(str(tmp_path)), "record reads a copy of the round's own, not the session's folder"
    assert recorder.input == {"reviewedKey": "k" * 64, "reviewedHead": HEAD, "rounds": 1,
                              "reviewerAgent": "pre-push-reviewer"}
    assert recorder.staged == ([OPEN], 0o700, 0o600), "the copy is the person's own, as record wants it"
    assert prepare_kwargs["got"] == () and "got" not in record_kwargs, "a signal can stop prepare, never record"
    assert not os.path.exists(record[4]), "the copy is gone after the record"
    assert "rollout.sh: recording the 1 finding(s) the session staged\nReview recorded.\n" in out.getvalue()
    assert not (tmp_path / "log.staged").exists(), "nothing is kept once it is recorded"


def test_the_round_records_an_empty_review(tmp_path):
    recorder = Recorder()
    assert recorded(tmp_path, [], recorder)[0] == (0, [])
    assert recorder.staged[0] == []


def test_nothing_staged_is_its_own_stop_and_records_nothing(tmp_path):
    recorder = Recorder()
    result, out = recorded(tmp_path, None, recorder)
    assert result == (auto.NOTHING_STAGED, []) and recorder.calls == []
    assert out.getvalue() == f"rollout.sh: the session staged no findings in {tmp_path}, not even [], so no review was recorded\n"


def test_staged_findings_that_break_a_rule_are_never_recorded(tmp_path):
    recorder = Recorder()
    (code, broke), _ = recorded(tmp_path, [{**OPEN, "status": "accepted"}], recorder)
    assert code == auto.SESSION_BROKE_A_RULE and len(broke) == 1 and recorder.calls == []


def test_a_findings_repeats_is_dropped_and_said(tmp_path):
    # The session could tie a finding to any .security entry, an open one of another branch's, which the person's
    # accept of the finding would then accept: the record matches a repeat by its path and summary (case and spacing
    # aside).
    recorder = Recorder()
    (result, out) = recorded(tmp_path, [{**OPEN, "repeats": "0007"}, {**OPEN, "id": "R2"}], recorder)
    assert result == (0, [])
    assert recorder.staged[0] == [OPEN, {**OPEN, "id": "R2"}]
    assert "rollout.sh: 'R1''s repeats '0007' was dropped: the record matches the finding to a .security entry by its" \
           " path and summary (case and spacing aside)\n" in out.getvalue()


@pytest.mark.parametrize("error", [RecursionError("maximum recursion depth exceeded while encoding a JSON object"),
                                   ValueError("Exceeds the limit (4300 digits) for integer string conversion")])
def test_findings_that_cant_be_written_again_break_a_rule(tmp_path, monkeypatch, error):
    def too_deep(value):
        raise error

    run = stage(tmp_path, [OPEN])
    monkeypatch.setattr(auto.json, "dumps", too_deep)
    recorder = Recorder()
    code, broke = auto.record_review("/w", run, io.StringIO(), HEAD, str(tmp_path / "log.staged"), bounded=recorder,
                              installed=lambda w: True)
    assert code == auto.SESSION_BROKE_A_RULE and recorder.calls == []
    assert broke == [f"{str(tmp_path / 'run' / auto.STAGED_FILE)!a} nests too deep to be written again"]


def failed_and_kept(tmp_path, out, why):
    assert out.getvalue().endswith(
        f"rollout.sh: {why}, so the review wasn't recorded\n"
        f"rollout.sh: what the session staged is kept in {tmp_path / 'log.staged'}; once what refused it is put right,"
        f" record it with:\n  {auto.record_command('/w', str(tmp_path / 'log.staged'), HEAD)}\n")
    assert kept(tmp_path) == ([OPEN], 0o700, 0o600, HEAD)


@pytest.mark.parametrize("prepare, lines", [((1, ""), 0), ((0, "only-one-line\n"), 1), ((0, "a\nb\nc\n"), 3),
                                            ((3, KEY_AND_HEAD), 2)])
def test_a_prepare_that_fails_or_doesnt_print_two_lines_records_nothing_and_keeps_the_findings(tmp_path, prepare, lines):
    recorder = Recorder(prepare=prepare)
    result, out = recorded(tmp_path, [OPEN], recorder)
    assert result == (auto.RECORD_FAILED, []) and len(recorder.calls) == 1
    assert "prepare's stderr\n" in out.getvalue()
    failed_and_kept(tmp_path, out, f"scripts/review.sh prepare exited {prepare[0]} and printed {lines} line(s), not its"
                                   " key and HEAD")


def test_a_prepare_that_runs_past_its_time_records_nothing(tmp_path):
    result, out = recorded(tmp_path, [OPEN], Recorder(prepare=(None, "")))
    assert result == (auto.RECORD_FAILED, [])
    failed_and_kept(tmp_path, out, "scripts/review.sh prepare ran past 600 s and was stopped")


@pytest.mark.parametrize("code, why", [(1, "scripts/review.sh record exited 1"),
                                       (None, "scripts/review.sh record ran past 600 s and was stopped")])
def test_a_record_that_refuses_or_runs_past_its_time_is_record_failed_and_keeps_the_findings(tmp_path, code, why):
    result, out = recorded(tmp_path, [OPEN], Recorder(record=code))
    assert result == (auto.RECORD_FAILED, [])
    failed_and_kept(tmp_path, out, why)


def test_findings_that_cant_be_kept_are_said_to_be_gone_and_no_command_is_given(tmp_path):
    (tmp_path / "log.staged").mkdir()
    result, out = recorded(tmp_path, [OPEN], Recorder(record=1))
    assert result == (auto.RECORD_FAILED, [])
    assert f"rollout.sh: what the session staged couldn't be kept in {tmp_path / 'log.staged'} ([Errno 17] File exists" \
           in out.getvalue()
    assert out.getvalue().endswith("rollout.sh: scripts/review.sh record exited 1, so the review wasn't recorded\n")


def test_a_keep_holding_another_reviews_findings_gives_no_command(tmp_path):
    auto.write_staged(str(tmp_path / "log.staged"), "[]", "a" * 40)
    result, out = recorded(tmp_path, [OPEN], Recorder(record=1))
    assert result == (auto.RECORD_FAILED, [])
    assert out.getvalue().endswith("rollout.sh: scripts/review.sh record exited 1, so the review wasn't recorded\n")


@pytest.mark.parametrize("error", [OSError(8, "Exec format error"), auto.GroupLeft("left"), ValueError("a bug")])
def test_findings_are_kept_whatever_stops_the_record(tmp_path, error):
    def bounded(command, **kwargs):
        if command[1] == "prepare":
            kwargs["stdout"].write(KEY_AND_HEAD)
            return 0
        raise error

    with pytest.raises(type(error)):
        recorded(tmp_path, [OPEN], bounded)
    assert kept(tmp_path) == ([OPEN], 0o700, 0o600, HEAD)


def test_a_group_prepare_left_is_a_failed_record_that_keeps_the_findings(tmp_path):
    def bounded(command, **kwargs):
        raise auto.GroupLeft("what review.sh prepare left in its process group was still there")

    result, out = recorded(tmp_path, [OPEN], bounded)
    assert result == (auto.RECORD_FAILED, ["what review.sh prepare left in its process group was still there"]), \
        "prepare writes only the round's folder: nothing was recorded, and the stop names the group that holds the lock"
    failed_and_kept(tmp_path, out, "what review.sh prepare left in its process group was still there")


def test_without_tsx_nothing_runs_and_the_findings_are_kept(tmp_path):
    recorder = Recorder()
    out = io.StringIO()
    result = auto.record_review("/w", stage(tmp_path, [OPEN]), out, HEAD, str(tmp_path / "log.staged"),
                                bounded=recorder, installed=lambda w: False)
    assert result == (auto.RECORD_FAILED, []) and recorder.calls == []
    failed_and_kept(tmp_path, out, "/w/node_modules/.bin/tsx isn't installed (pnpm install in the worktree):"
                                   " scripts/review.sh would install it, changing the worktree the round has just checked")


def test_tsx_installed_wants_an_executable(tmp_path):
    assert auto.tsx_installed(str(tmp_path)) is False
    tsx = tmp_path / "node_modules" / ".bin" / "tsx"
    tsx.parent.mkdir(parents=True)
    tsx.write_text("#!/bin/sh\n")
    assert auto.tsx_installed(str(tmp_path)) is False
    tsx.chmod(0o755)
    assert auto.tsx_installed(str(tmp_path)) is True


def test_a_signal_before_prepare_runs_nothing_and_keeps_the_findings(tmp_path):
    recorder = Recorder()
    out = io.StringIO()
    result = auto.record_review("/w", stage(tmp_path, [OPEN]), out, HEAD, str(tmp_path / "log.staged"),
                                got=[signal.SIGINT], bounded=recorder, installed=lambda w: True)
    assert result == (auto.RECORD_FAILED, []) and recorder.calls == []
    failed_and_kept(tmp_path, out, "a signal came before scripts/review.sh prepare ran")


def test_a_bounded_command_stops_at_a_deferred_signal(tmp_path):
    session = FakeSession(code=0, raises=subprocess.TimeoutExpired("prepare", 0.5))
    ticks = iter([0] * 10 + [10**9] * 100)
    code = auto.run_bounded(["/w/scripts/review.sh", "prepare"], cwd="/w", stdin=None, stdout=None, stderr=None,
                            timeout=1000, got=[signal.SIGINT], popen=lambda c, **k: session, end=lambda child: True,
                            clock=lambda: next(ticks))
    assert code is None and len(session.waits) == 1, "it stops at the first poll once a signal is there"


def test_a_signal_during_prepare_stops_it_and_keeps_the_findings(tmp_path):
    def bounded(command, **kwargs):
        kwargs["got"].append(signal.SIGINT)
        return None

    got = []
    out = io.StringIO()
    result = auto.record_review("/w", stage(tmp_path, [OPEN]), out, HEAD, str(tmp_path / "log.staged"), got=got,
                                installed=lambda w: True,
                                bounded=bounded)
    assert result == (auto.RECORD_FAILED, [])
    failed_and_kept(tmp_path, out, "scripts/review.sh prepare was stopped by a signal")


def test_a_change_the_unseen_check_finds_before_record_records_nothing(tmp_path):
    recorder = Recorder()
    out = io.StringIO()
    result = auto.record_review("/w", stage(tmp_path, [OPEN]), out, HEAD, str(tmp_path / "log.staged"),
                                installed=lambda w: True,
                                unseen=lambda: ["node_modules changed"], bounded=recorder)
    assert result == (auto.SESSION_BROKE_A_RULE, ["node_modules changed"])
    assert [command[1] for command, _ in recorder.calls] == ["prepare"], "record never ran"
    assert not (tmp_path / "log.staged").exists(), "findings for code that changed are kept for nothing"


@pytest.mark.parametrize("text, head", [
    ("a" * 40 + "\n", "a" * 40), ("0123456789abcdef" * 4, "0123456789abcdef" * 4), ("a" * 39, None), ("a" * 41, None),
    ("a" * 63, None), ("a" * 65, None), ("A" * 40, None), ("a; rm -rf ~", None), ("a" * 40 + "\n" + "b" * 40, None),
    (" " * 70 + "a" * 40, None), ("a" * 40 + " " * 59 + "x", None),
])
def test_kept_head_reads_only_an_object_id(tmp_path, text, head):
    assert auto.kept_head(str(tmp_path / "none")) is None
    auto.write_staged(str(tmp_path / "k"), "[]", text)
    assert auto.kept_head(str(tmp_path / "k")) == head
    assert stat.S_IMODE((tmp_path / "k" / auto.KEPT_HEAD_FILE).stat().st_mode) == 0o600


def test_a_head_that_moved_since_the_guard_read_it_is_a_broken_rule_and_records_nothing(tmp_path):
    recorder = Recorder()
    (code, broke), _ = recorded(tmp_path, [OPEN], recorder, head="a" * 40)
    assert code == auto.SESSION_BROKE_A_RULE and len(recorder.calls) == 1, "record never ran"
    assert not (tmp_path / "log.staged").exists(), "findings for code that moved are kept for nothing"
    assert broke == [f"the worktree's HEAD was {HEAD!a} by the time the review was recorded, not {'a' * 40} as the"
                     " session reviewed it: something committed there since, so nothing was recorded"]


STUB = r"""#!/bin/sh
case "$1" in
prepare) echo "prepare's note" >&2; pwd > "$CALLS/prepare.pwd"; printf '%s\n%s\n' "$KEY" "$HEAD"
  exit "${PREPARE_EXIT:-0}" ;;
record)
  cat > "$CALLS/input.json"
  printf '%s\n' "$@" > "$CALLS/argv"
  cp "$4/findings.json" "$CALLS/staged.json"
  if [ -n "${LEAVE:-}" ]; then sleep 60 & echo $! > "$CALLS/left.pid"; fi
  echo "Review recorded."
  echo ".security: R1 is a new entry, 0001" >&2
  exit "${RECORD_EXIT:-0}" ;;
esac
"""


@needs_proc
def test_the_record_runs_a_real_review_script_in_a_group_of_its_own_and_logs_both_its_streams(tmp_path, monkeypatch):
    worktree, calls = tmp_path / "w", tmp_path / "calls"
    (worktree / "scripts").mkdir(parents=True)
    (worktree / "node_modules" / ".bin").mkdir(parents=True)
    (worktree / "node_modules" / ".bin" / "tsx").write_text("#!/bin/sh\n")
    (worktree / "node_modules" / ".bin" / "tsx").chmod(0o755)
    calls.mkdir()
    script = worktree / "scripts" / "review.sh"
    script.write_text(STUB)
    script.chmod(0o755)
    monkeypatch.setenv("KEY", "k" * 64)
    monkeypatch.setenv("HEAD", HEAD)
    monkeypatch.setenv("CALLS", str(calls))
    monkeypatch.setenv("LEAVE", "1")
    log = tmp_path / "review.log"
    with open(log, "w") as out:
        out.write("the session's output\n")
        result = auto.record_review(str(worktree), stage(tmp_path, [OPEN]), out, HEAD, str(tmp_path / "log.staged"))
    assert result == (0, [])
    text = log.read_text()
    assert text.index("the session's output") < text.index("prepare's note") < text.index("recording the 1 finding(s)") \
        < text.index("Review recorded."), "the log keeps the order things happened in"
    assert ".security: R1 is a new entry, 0001" in text, "record's stderr is logged"
    assert json.loads((calls / "input.json").read_text())["reviewedHead"] == HEAD
    assert json.loads((calls / "staged.json").read_text()) == [OPEN]
    assert wait_gone(read_pid(calls / "left.pid")), "what record left in its group is killed with it"
    assert (calls / "prepare.pwd").read_text().strip() == str(worktree), "prepare runs in the worktree"


def test_the_kept_findings_record_with_the_command_given_only_at_the_head_reviewed(tmp_path, monkeypatch):
    worktree, calls = tmp_path / "w t", tmp_path / "calls"
    (worktree / "scripts").mkdir(parents=True)
    calls.mkdir()
    script = worktree / "scripts" / "review.sh"
    script.write_text(STUB)
    script.chmod(0o755)
    monkeypatch.setenv("KEY", "k" * 64)
    monkeypatch.setenv("HEAD", HEAD)
    monkeypatch.setenv("CALLS", str(calls))
    monkeypatch.setenv("TMPDIR", str(tmp_path))
    keep = tmp_path / "kept it"
    auto.write_staged(str(keep), json.dumps([OPEN]))
    moved = subprocess.run(["sh", "-c", auto.record_command(str(worktree), str(keep), "a" * 40)], capture_output=True,
                           text=True)
    assert moved.returncode != 0 and not (calls / "input.json").exists(), "a HEAD other than the reviewed one records nothing"
    assert moved.stderr.endswith(f"the worktree's HEAD is not {'a' * 40}, the one the session reviewed: nothing"
                                 " recorded\n")
    monkeypatch.setenv("PREPARE_EXIT", "3")
    failing = subprocess.run(["sh", "-c", auto.record_command(str(worktree), str(keep), HEAD)], capture_output=True,
                             text=True)
    assert failing.returncode != 0 and failing.stderr.endswith("scripts/review.sh prepare failed: nothing recorded\n")
    monkeypatch.delenv("PREPARE_EXIT")
    monkeypatch.setenv("HEAD", f"{HEAD} extra")
    words = subprocess.run(["sh", "-c", auto.record_command(str(worktree), str(keep), HEAD)], capture_output=True,
                           text=True)
    assert words.returncode != 0 and not (calls / "input.json").exists()
    assert words.stderr.endswith("scripts/review.sh prepare printed 3 word(s), not its key and HEAD: nothing recorded\n")
    monkeypatch.setenv("HEAD", HEAD)
    gone = subprocess.run(["sh", "-c", auto.record_command(str(tmp_path / "gone"), str(keep), HEAD)], cwd=worktree,
                          capture_output=True, text=True)
    assert gone.returncode != 0 and not (calls / "input.json").exists(), "a missing worktree runs nothing where it was pasted"
    shells = [name for name in ("bash", "zsh", "fish") if shutil.which(name)]
    assert "bash" in shells
    for shell in shells:
        (calls / "input.json").unlink(missing_ok=True)
        subprocess.run([shell, "-c", auto.record_command(str(worktree), str(keep), HEAD)], check=True, capture_output=True)
        assert json.loads((calls / "input.json").read_text()) == {"reviewedKey": "k" * 64, "reviewedHead": HEAD,
                                                                 "rounds": 1, "reviewerAgent": "pre-push-reviewer"}, shell
    assert (calls / "argv").read_text().split("\n")[:4] == ["record", "-", "--staged", str(keep)]
    assert (calls / "prepare.pwd").read_text().strip() == str(worktree), "it runs in the worktree"


@needs_proc
def test_a_bounded_command_past_its_time_is_stopped_with_its_group(tmp_path):
    pid_file = tmp_path / "pid"
    code = auto.run_bounded(["sh", "-c", f"sleep 60 & echo $! > {pid_file}; wait", "x"], cwd=str(tmp_path),
                            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=1)
    assert code is None
    assert wait_gone(read_pid(pid_file))


def test_a_bounded_command_runs_in_its_own_group_holding_the_lock_and_a_group_it_leaves_is_group_left(monkeypatch):
    seen = {}

    def popen(command, **kwargs):
        seen.update(kwargs)
        return FakeSession(code=0)

    monkeypatch.setattr(auto, "LOCK_FDS", [42])
    with pytest.raises(auto.GroupLeft) as left:
        auto.run_bounded(["/w/scripts/review.sh", "record"], cwd="/w", stdin="in", stdout="out", stderr="err",
                         popen=popen, end=lambda child: False)
    assert seen == {"cwd": "/w", "stdin": "in", "stdout": "out", "stderr": "err", "start_new_session": True,
                    "pass_fds": (42,)}
    assert str(left.value) == ("what review.sh record left in its process group was still there 10 s after it was"
                               " killed, and may still write the review ledgers and .security: see it with pgrep -l -g"
                               f" {FakeSession().pid}, and end it with pkill -KILL -g {FakeSession().pid}, before you"
                               " resume")
    assert auto.run_bounded(["/w/scripts/review.sh", "prepare"], cwd="/w", stdin=None, stdout=None, stderr=None,
                            popen=popen, end=lambda child: True) == 0


def test_the_staged_file_and_record_form_are_the_ledgers_own():
    source = (SKILL / "template" / "owned" / ".sandcastle" / "lib" / "review-ledger.mts").read_text()
    assert f'const STAGED_FILE = "{auto.STAGED_FILE}";' in source
    assert 'const flag = rest.indexOf("--staged");' in source and "flag !== 1" in source, \
        "record takes its input, then --staged and the folder, as record_review passes them"
    for field in ("reviewedKey", "reviewedHead", "rounds", "reviewerAgent"):
        assert f"{field}:" in source, field
    agents = auto.agent_definition()
    assert list(agents) == [auto.REVIEWER_AGENT], "the review is recorded under the agent the session ran"


def test_link_text_reads_only_a_plain_file(tmp_path):
    assert auto.link_text(str(tmp_path)) is None
    (tmp_path / ".git").write_bytes(b"gitdir: /x\n")
    assert auto.link_text(str(tmp_path)) == b"gitdir: /x\n"


def test_run_text_strips_trailing_newlines_only_and_gives_none_for_a_failure(tmp_path):
    assert auto.run_text(["printf", "  a\\n\\n"]) == "  a"
    assert auto.run_text(["false"]) is None
    assert auto.run_text(["/nonexistent/tool"]) is None
    assert auto.run_text(["pwd"], cwd=str(tmp_path)) == str(tmp_path)


needs_waitid = pytest.mark.skipif(not hasattr(os, "waitid") or not hasattr(os, "WNOWAIT"),
                                  reason="no waitid or WNOWAIT here (macOS)")


@needs_proc
@needs_waitid
def test_a_leader_that_exited_stays_unreaped_until_its_group_is_ended():
    # Its pid is the group's id: reaped before the group is signalled, it could be another process's by then.
    session = subprocess.Popen(["sh", "-c", "exit 3"], start_new_session=True)
    assert auto.wait_for(session, 10, []) == 3
    assert Path(f"/proc/{session.pid}/stat").read_text().rsplit(")", 1)[1].split()[0] == "Z", "still a zombie"
    assert session.returncode is None
    assert auto.end_group(session) is True and session.returncode == 3
    with pytest.raises(ChildProcessError):
        os.waitid(os.P_PID, session.pid, os.WEXITED | os.WNOHANG)


@needs_waitid
def test_exit_unreaped_gives_a_signal_as_popen_does_and_times_out_as_it_does():
    killed = subprocess.Popen(["sh", "-c", "kill -9 $$"], start_new_session=True)
    assert auto.exit_unreaped(killed, 10) == -signal.SIGKILL
    killed.wait()
    running = subprocess.Popen(["sleep", "30"], start_new_session=True)
    try:
        with pytest.raises(subprocess.TimeoutExpired):
            auto.exit_unreaped(running, 0.1)
    finally:
        running.kill()
        running.wait()
    assert auto.exit_unreaped(FakeSession(code=5), 1) == 5, "not this process's child: waited as Popen waits"


@pytest.mark.parametrize("missing", ["waitid", "WNOWAIT"])
def test_without_waitid_a_session_is_waited_as_popen_waits(monkeypatch, missing):
    monkeypatch.delattr(os, missing, raising=False)  # macOS has neither: the test runs there too
    session = FakeSession(code=5)
    assert auto.exit_unreaped(session, 1) == 5 and session.waits == [1]
    assert auto.wait_for(FakeSession(code=6), 10, []) == 6
    timing_out = FakeSession(raises=subprocess.TimeoutExpired("x", 1))
    with pytest.raises(subprocess.TimeoutExpired):
        auto.exit_unreaped(timing_out, 1)


def test_a_group_a_command_left_that_wont_go_is_a_stop_after_its_code(capsys):
    with pytest.raises(auto.GroupLeft) as left:
        auto.run_in_group(["/bin/true"], end=lambda child: False)
    assert re.fullmatch(r"what true left in its process group was still there 10 s after it was killed, and holds the"
                        r" round's lock and may still work in its worktree: see it with pgrep -l -g (\d+), and end it"
                        r" with pkill -KILL -g \1, before you resume", str(left.value))
    assert capsys.readouterr().err == ""


def test_a_signal_while_a_command_runs_still_says_what_it_left_in_its_group(capsys):
    def popen(command, **kwargs):
        os.kill(os.getpid(), signal.SIGINT)
        return FakeSession(code=0)

    with pytest.raises(KeyboardInterrupt):
        auto.run_in_group(["/x/land.sh"], popen=popen, end=lambda child: False)
    assert capsys.readouterr().err.startswith("rollout.sh: what land.sh left in its process group was still there")


def test_a_group_that_went_away_says_nothing(capsys):
    assert auto.run_in_group(["/bin/true"], end=lambda child: True) == 0
    assert capsys.readouterr().err == ""


def test_the_group_hint_uses_commands_procps_and_the_bsds_share():
    assert auto.group_hint(42) == "see it with pgrep -l -g 42, and end it with pkill -KILL -g 42"


def test_end_group_of_a_group_already_gone_returns_at_once():
    session = subprocess.Popen(["true"], start_new_session=True)
    session.wait()
    assert auto.end_group(session) is True


def test_end_group_waits_for_a_live_member_and_gives_up_in_time(monkeypatch):
    calls, now = [], [0.0]
    monkeypatch.setattr(auto.os, "killpg", lambda pid, sig: calls.append(sig))
    session = FakeSession()
    gone = auto.end_group(session, wait_seconds=1, sleep=lambda s: now.__setitem__(0, now[0] + s), clock=lambda: now[0],
                          alive=lambda pgid: True)
    assert gone is False and calls == [signal.SIGKILL]
    assert session.waits == [1]
    assert 1 <= now[0] < 1.2, "gave up once its time was spent"


def test_end_group_gives_up_on_a_leader_that_wont_die(monkeypatch):
    monkeypatch.setattr(auto.os, "killpg", lambda pid, sig: None)
    session = FakeSession(raises=subprocess.TimeoutExpired("claude", 10))
    assert auto.end_group(session, wait_seconds=10) is False and session.waits == [10]


def test_end_group_needs_no_kill_for_a_group_already_gone(monkeypatch):
    def gone(pid, sig):
        raise ProcessLookupError

    monkeypatch.setattr(auto.os, "killpg", gone)
    assert auto.end_group(FakeSession(), alive=lambda pgid: False) is True


def test_group_alive_counts_live_members_only(tmp_path):
    proc = tmp_path / "proc"

    def member(pid, state, pgrp):
        (proc / str(pid)).mkdir(parents=True)
        (proc / str(pid) / "stat").write_text(f"{pid} (some (odd) name) {state} 1 {pgrp} {pgrp} 0 -1\n")

    member(10, "Z", 77)
    member(11, "S", 88)
    (proc / "self").mkdir()
    assert auto.group_alive(77, proc=str(proc)) is False, "a zombie alone is gone"
    member(12, "R", 77)
    assert auto.group_alive(77, proc=str(proc)) is True


def test_group_alive_reads_the_group_field_not_the_session_or_parent(tmp_path):
    proc = tmp_path / "proc"
    (proc / "20").mkdir(parents=True)
    (proc / "20" / "stat").write_text("20 (x) S 77 88 77 0 -1\n")  # parent and session 77, group 88
    (proc / "21").mkdir()
    (proc / "21" / "stat").write_text("21 (x) S\n")  # too short to have a group
    (proc / "22").mkdir()  # no stat: gone while listed
    assert auto.group_alive(77, proc=str(proc)) is False
    assert auto.group_alive(88, proc=str(proc)) is True


def test_group_alive_without_proc_asks_the_kernel(tmp_path, monkeypatch):
    seen = []
    monkeypatch.setattr(auto.os, "killpg", lambda pid, sig: seen.append((pid, sig)))
    assert auto.group_alive(77, proc=str(tmp_path / "no-proc")) is True and seen == [(77, 0)]

    def gone(pid, sig):
        raise ProcessLookupError

    monkeypatch.setattr(auto.os, "killpg", gone)
    assert auto.group_alive(77, proc=str(tmp_path / "no-proc")) is False


@pytest.mark.parametrize("which, error", [("skill", ValueError("x.md: front matter is never closed")),
                                          ("agents", OSError(2, "No such file")), ("agents", ValueError("has no tools"))])
def test_a_skill_or_agent_that_doesnt_read_starts_no_session(tmp_path, monkeypatch, which, error):
    def broken():
        raise error

    monkeypatch.setenv("GATE_SKIP_REVIEW", "1")
    worktree = tmp_path / "w"
    worktree.mkdir()
    started = []
    code, log, broke = auto.run_review(TM, str(worktree), **NO_LISTING, popen=lambda c, **k: started.append(c),
                                       changes=lambda w: [], log_dir=str(tmp_path / "logs"),
                                       session_dir=str(tmp_path / "sessions"),
                                       guard=lambda w: fake_guard(tmp_path), check=lambda g: [],
                                       **{"skill": lambda: SKILL_TEXT, "agents": lambda: AGENTS, which: broken})
    assert (code, broke, started) == (127, [], [])
    assert f"rollout.sh: not started: can't set the session up: {error}" in Path(log).read_text()


def test_a_claude_that_cannot_start_is_127_with_its_reason_in_the_log(tmp_path, monkeypatch):
    def missing(command, **kwargs):
        raise FileNotFoundError(2, "No such file", "claude")

    code, log, _, _ = review_once(tmp_path, monkeypatch, popen=missing)
    assert code == 127 and Path(log).read_text().startswith(f"{auto.NOT_STARTED}can't start claude"), \
        "no session ran, so the round's reason is the log's first line, not after SESSION_STARTS"
    assert auto.stop_line(log).startswith("couldn't be started: can't start claude")


def test_a_session_that_prints_a_not_started_line_first_isnt_taken_for_the_round(tmp_path, monkeypatch):
    def forging(command, **kwargs):
        kwargs["stdout"].write(f"{auto.NOT_STARTED}\x1b[2Ka forged reason\n")
        kwargs["stdout"].flush()
        return FakeSession(code=127)

    code, log, _, _ = review_once(tmp_path, monkeypatch, popen=forging)
    assert code == 127
    assert Path(log).read_text().startswith(auto.SESSION_STARTS + auto.NOT_STARTED), "the round's line comes first"
    assert auto.stop_line(log) is None, "the session's first line is the log's second, never read"


def test_a_review_session_exit_code_is_passed_on(tmp_path, monkeypatch):
    code, _, _, _ = review_once(tmp_path, monkeypatch, session=FakeSession(code=3))
    assert code == 3


def test_a_missing_worktree_is_named_and_nothing_starts(tmp_path, monkeypatch):
    started = []
    code, log, _, _ = review_once(tmp_path, monkeypatch, popen=lambda *a, **k: started.append(a),
                                  worktree=tmp_path / "gone")
    assert code == 127 and started == []
    assert (f"{auto.NOT_STARTED}{auto.WORKTREE_GONE}{tmp_path / 'gone'} was removed since reapply.sh made it; resume,"
            " and reapply.sh makes it again") in Path(log).read_text()


def test_a_removed_worktree_isnt_where_the_stop_sends_the_person(tmp_path):
    log = tmp_path / "review.log"
    log.write_text(f"{auto.NOT_STARTED}{auto.WORKTREE_GONE}/r/w was removed since reapply.sh made it; resume, and"
                   " reapply.sh makes it again\n")
    outcome = auto.drive(TM, "/r/TicketManager", "#82", lambda brings, path: auto.REVIEW_NEEDED,
                         lambda repo, worktree: (127, str(log), []), lambda line: None)
    assert outcome == auto.Outcome("failed", f"the review session couldn't be started: {auto.WORKTREE_GONE}/r/w was"
                                   " removed since reapply.sh made it; resume, and reapply.sh makes it again (see"
                                   f" {log}).")
    log.write_text(f"{auto.NOT_STARTED}can't set the session up: no room\n")
    other = auto.drive(TM, "/r/TicketManager", "#82", lambda brings, path: auto.REVIEW_NEEDED,
                       lambda repo, worktree: (127, str(log), []), lambda line: None)
    assert other.detail == (f"the review session couldn't be started: can't set the session up: no room (see {log});"
                            " run /pre-push-review in /r/TicketManager-worktrees/chore-reapply-baseline, then resume.")


@pytest.mark.parametrize("where", ["changes", "guard"])
def test_a_session_that_cant_be_set_up_never_starts(tmp_path, monkeypatch, where):
    started = []

    def broken(path):
        raise (auto.GuardUnreadable("can't read the remote") if where == "guard" else RuntimeError("git diff failed"))

    code, log, _, _ = review_once(tmp_path, monkeypatch, popen=lambda *a, **k: started.append(a),
                                  **{where: broken})
    assert code == 127 and started == []
    assert ("rollout.sh: not started: can't read the remote" if where == "guard" else "git diff failed") in Path(log).read_text()


def test_a_log_that_cannot_be_made_is_127(tmp_path, monkeypatch):
    def full(*args, **kwargs):
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(auto.tempfile, "mkstemp", full)
    code, log, _ = auto.run_review(TM, str(tmp_path), log_dir=str(tmp_path / "logs"),
                                   session_dir=str(tmp_path / "sessions"))
    assert code == 127 and "No space left on device" in log


def head_of(repo):
    return f"{short(repo).lower():0<40}"[:40]


def short(repo):
    return repo.split("/")[-1]


class Lock:
    def __init__(self):
        self.closed = False

    def fileno(self):
        return 42

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
                 land_raises=None, lock_raises=None, moves=(), unlisted=None, state_failures=None, land_code=None,
                 broke=None):
        self.codes = {repo: list(seq) for repo, seq in (codes or {}).items()}
        self.opens = dict(opens or {})
        self.prs = {repo: (100, "MERGED", "0" * 40) for repo in LISTED} | dict(prs or {})
        self.ahead = {}
        self.review_codes = dict(reviews or {})
        self.unlanded, self.still_open, self.unreadable = set(unlanded), set(still_open), set(unreadable)
        self.stale, self.failures = dict(stale or {}), dict(failures or {})
        self.closes, self.branch_fails, self.moves = set(closes), set(branch_fails), set(moves)
        self.problems, self.locked, self.raises = list(problems), locked, dict(raises or {})
        self.land_raises, self.lock_raises, self.land_code = land_raises, lock_raises, land_code
        self.unlisted, self.state_failures = dict(unlisted or {}), dict(state_failures or {})
        self.broke = dict(broke or {})
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
        return self.review_codes.get(repo, 0), f"/tmp/review-{short(repo)}.log", list(self.broke.get(repo, []))

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
        if self.land_code is not None:
            return self.land_code
        return 1 if self.unlanded & set(refs) else 0

    def pr_of(self, repo):
        if repo in self.unreadable:
            raise RuntimeError("HTTP 502")
        if self.failures.get(repo, 0) > 0:
            self.failures[repo] -= 1
            raise RuntimeError("HTTP 502")
        if self.ahead.get(repo) and self.unlisted.get(repo, 0) > 0:
            self.unlisted[repo] -= 1
            return None  # pushed, and not listed yet
        if self.stale.get(repo, 0) > 0:
            self.stale[repo] -= 1
            return 99, "MERGED", "f" * 40
        return self.prs[repo]

    def pr_state(self, repo, number):
        if repo in self.unreadable:
            raise RuntimeError("HTTP 502")
        if self.state_failures.get(repo, 0) > 0:
            self.state_failures[repo] -= 1
            raise RuntimeError("HTTP 503")
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
    assert (f"rollout.sh: another rollout.sh --auto holds {auto.LOCK}, or a review session or gate an earlier one started"
            f" is still running; nothing run. See what holds it with: fuser -v {auto.LOCK} (or lsof {auto.LOCK}), and end"
            " it only when no round is running\n") == err


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
    worktree = "/r/TicketManager-worktrees/chore-reapply-baseline"
    assert (f"TicketManager: review findings wait for you (cd {worktree} && scripts/review.sh report): fix them with"
            " /pre-push-review there (one in an Owned file goes to the Factory's template/owned/ first: add that PR to"
            f" --brings), or accept them: cd {worktree} && scripts/review.sh accept-open") in out
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
    assert "moved since the last one (see /tmp/review-TicketManager.log); run /pre-push-review in" in out


def test_auto_reviews_a_second_time_when_the_first_session_left_no_record():
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED] * 2}, opens={TM: 130})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert len(fleet.reviewed) == 2


@pytest.mark.parametrize("exit_code, why", [
    (124, "ran past 180 minutes and was stopped"),
    (127, "couldn't be started: no log could be made"),
    (1, "exited 1"),
    (auto.RECORD_FAILED, "staged findings the round couldn't record (scripts/review.sh prepare or record failed, or the"
                         " round did; the log says why)"),
    (auto.NOTHING_STAGED, "exited 0 having staged no findings, not even [], so no review was recorded"),
    (125, "exited 125"),
    (3, "exited 3"),
])
def test_auto_stops_when_a_review_session_fails_and_says_what_to_do(exit_code, why):
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED]}, reviews={TM: exit_code})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert len(reapplied(fleet)) == 1
    assert (f"TicketManager: the review session {why} (see /tmp/review-TicketManager.log); run /pre-push-review in"
            " /r/TicketManager-worktrees/chore-reapply-baseline, then resume.") in out
    assert "What the session staged is kept" not in out, "nothing is kept: no command"


def test_a_session_that_couldnt_start_says_why_on_the_terminal(tmp_path):
    log = tmp_path / "review.log"
    log.write_text("rollout.sh: not started: the clone's git folder holds a secret, then resume\n")
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED]})
    fleet.review = lambda repo, worktree: (127, str(log), [])
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert f"the review session couldn't be started: the clone's git folder holds a secret (see {log})" in out
    assert auto.stop_line(str(tmp_path / "missing.log")) is None
    (tmp_path / "quiet.log").write_text("nothing of rollout.sh's\n")
    assert auto.stop_line(str(tmp_path / "quiet.log")) is None
    (tmp_path / "started.log").write_text("rollout.sh: made while the session ran, ... (check them): /x\n")
    assert auto.stop_line(str(tmp_path / "started.log")) is None, "a claude that started and exited 127 wasn't stopped"
    (tmp_path / "two.log").write_text(f"{auto.NOT_STARTED}the round's own\n{auto.NOT_STARTED}printed after\n")
    assert auto.stop_line(str(tmp_path / "two.log")) == "couldn't be started: the round's own", \
        "the first, written before anything else could print one"
    (tmp_path / "forged.log").write_text(f"claude's own output\n{auto.NOT_STARTED}\x1b[2Ka forged reason\n")
    assert auto.stop_line(str(tmp_path / "forged.log")) is None, "a session's line, however it looks, isn't the round's"
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED]})
    fleet.review = lambda repo, worktree: (127, str(tmp_path / "quiet.log"), [])
    _, out, _ = run_auto(["--brings", "#82"], fleet)
    assert ("the review session exited 127 and the log doesn't begin with a 'rollout.sh: not started:' line, so claude"
            f" started and exited 127 itself: the log says why (see {tmp_path / 'quiet.log'})") in out


@pytest.mark.parametrize("error, left", [
    ("rename it, then resume", "rename it"),
    ("rename it; then resume", "rename it"),
    ("rename it: then resume", "rename it"),
    ("rename it then resume", "rename it"),
    ("rename it, then resume; more\nlines", "rename it"),
    ("rename it, then resumed it", "rename it, then resumed it"),
    ("rename it", "rename it"),
])
def test_then_resume_is_dropped_where_nothing_stopped(error, left):
    assert auto.without_resume(ValueError(error)) == left


def drive_once(tmp_path, code, broke=(), head=HEAD):
    log = tmp_path / "review.log"
    if head is not None:
        auto.write_staged(auto.staged_copy(str(log)), "[]", head)
    said = []
    outcome = auto.drive(TM, "/r/TicketManager", "#82", lambda brings, path: auto.REVIEW_NEEDED,
                         lambda repo, worktree: (code, str(log), list(broke)), said.append)
    return outcome, auto.staged_copy(str(log))


def test_a_failed_record_whose_prepare_left_a_group_names_it_on_the_stop(tmp_path):
    outcome, _ = drive_once(tmp_path, auto.RECORD_FAILED, ["what review.sh prepare left ...: see it with pgrep -l -g 7"])
    assert outcome.status == "failed", "a failed record, not a broken rule"
    assert ("staged findings the round couldn't record (scripts/review.sh prepare or record failed, or the round did;"
            " the log says why): what review.sh prepare left ...: see it with pgrep -l -g 7 (see") in outcome.detail


def test_a_stop_with_findings_kept_prints_the_command_that_records_them(tmp_path):
    outcome, keep = drive_once(tmp_path, auto.RECORD_FAILED)
    command = auto.record_command("/r/TicketManager-worktrees/chore-reapply-baseline", keep, HEAD)
    assert outcome.status == "failed" and outcome.detail.endswith(
        f"then resume. What the session staged is kept in {keep}; record it, once that is put right, with: {command}")
    (tmp_path / "none").mkdir()
    outcome, _ = drive_once(tmp_path / "none", auto.RECORD_FAILED, head=None)
    assert outcome.detail.endswith("then resume."), "nothing kept, so no command"


def test_a_group_the_record_left_stops_for_the_person_saying_it_may_have_recorded(tmp_path):
    outcome, keep = drive_once(tmp_path, auto.RECORD_LEFT, broke=["what review.sh record left in its process group"])
    here = "/r/TicketManager-worktrees/chore-reapply-baseline"
    assert outcome.status == "person"
    assert outcome.detail.startswith("the round's record of the review didn't end cleanly: what review.sh record left in"
                                     " its process group. Whether the review was recorded can't be told: cd"
                                     f" {here} && scripts/review.sh report shows it")
    assert f"record it, once that is put right, with: {auto.record_command(here, keep, HEAD)}" in outcome.detail


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
    assert code == 1 and (f"TicketManager#130 is CLOSED, not merged: reopen it (gh pr reopen 130 -R {TM}) and land it with"
                          f" land.sh {TM}#130, or delete its branch and resume for a new PR") in out


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
    fleet = Fleet(prs={TM: (129, "CLOSED", head_of(TM))})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert ("rollout: TicketManager#129 is CLOSED, not merged: reopen it (gh pr reopen 129 -R mpaulosky/TicketManager)"
            " and land it with land.sh mpaulosky/TicketManager#129, or delete its branch and resume for a new PR; held:") in out
    # The last round's PR, closed at another head, with nothing pushed: no merged PR to go on from.
    fleet = Fleet(prs={TM: (129, "CLOSED", "0" * 40)})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and "TicketManager: no merged re-Apply PR found, and nothing to push" in out


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
    assert "TicketManager: no merged re-Apply PR found, and nothing to push" in out


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
    assert ("TicketManager: PermissionError: [Errno 13] Permission denied (traceback on stderr); run it by hand with:"
            " reapply.sh --brings '#82' /r/TicketManager") in out and "resume with:" in out
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


def test_a_rest_repo_whose_last_rounds_pr_was_closed_with_nothing_to_push_is_done():
    fleet = Fleet(prs={REST[0]: (77, "CLOSED", "0" * 40)})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert fleet.sleeps == [] and fleet.landed == []


def test_a_rest_repo_whose_pr_was_closed_is_a_stop():
    fleet = Fleet(prs={REST[0]: (77, "CLOSED", head_of(REST[0]))})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert ("  IssueTracker#77 is CLOSED, not merged: reopen it (gh pr reopen 77 -R mpaulosky/IssueTracker) and land it"
            " with land.sh mpaulosky/IssueTracker#77, or delete its branch and resume for a new PR\n") in out
    assert "4 of 5 other repo(s) done" in out


def test_a_rest_pr_land_sh_closes_is_a_stop():
    fleet = Fleet(opens={REST[0]: 200}, closes={f"{REST[0]}#200"})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert ("  IssueTracker#200 is CLOSED, not merged: reopen it (gh pr reopen 200 -R mpaulosky/IssueTracker) and land"
            " it with land.sh mpaulosky/IssueTracker#200, or delete its branch and resume for a new PR\n") in out


def test_a_land_sh_that_crashes_for_the_rest_still_reads_and_reports_each_pr():
    fleet = Fleet(opens={REST[0]: 200, REST[1]: 201}, land_raises=OSError(8, "Exec format error"))
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "rollout.sh: land.sh failed: OSError: [Errno 8] Exec format error" in out
    assert "  IssueTracker#200 didn't land (#200 still reads as OPEN): land.sh mpaulosky/IssueTracker#200" in out
    assert "  Blazor-Server#201 didn't land (#201 still reads as OPEN)" in out
    assert out.rstrip().endswith("resume with: rollout.sh --auto --brings '#82'")


def round_with_checkout(fleet, checkout):
    out, err = io.StringIO(), io.StringIO()
    code = auto.auto_round((TM, *REST), "#82", "rollout.sh --auto --brings '#82'", checkout, fleet.reapply, fleet.review,
                           fleet.land, fleet.pr_of, fleet.pr_state, fleet.branch_of, lambda text: out.write(text + "\n"),
                           err, fleet.sleeps.append)
    return code, out.getvalue(), err.getvalue()


@pytest.mark.parametrize("failing", [TM, REST[0]])
def test_a_crash_whose_checkout_cant_be_told_still_ends_with_its_stop_and_no_command(failing):
    def checkout(repo):
        if repo == failing:
            raise LookupError("no checkout")
        return f"/r/{short(repo)}"

    code, out, err = round_with_checkout(Fleet(), checkout)
    assert code == 1
    if failing == TM:
        assert "rollout: TicketManager: LookupError: no checkout (traceback on stderr); held: IssueTracker," in out
    else:
        assert "  IssueTracker: LookupError: no checkout (traceback on stderr)\n" in out
    assert "run it by hand" not in out and f"rollout.sh: {short(failing)} crashed:\nTraceback" in err


def test_a_land_sh_that_cant_start_for_ticketmanager_names_land_sh_not_reapply_sh():
    fleet = Fleet(opens={TM: 130}, land_raises=OSError(8, "Exec format error"))
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and reapplied(fleet) == ["TicketManager"]
    assert ("rollout: TicketManager#130: land.sh failed: OSError: [Errno 8] Exec format error; land it with land.sh"
            " mpaulosky/TicketManager#130; held:") in out
    assert "run it by hand" not in out and "crashed" not in err


def test_a_bug_while_landing_ticketmanager_is_a_crash_with_its_traceback():
    fleet = Fleet(opens={TM: 130}, land_raises=TypeError("a bug"))
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "land.sh failed" not in out and "TypeError: a bug" in err and "Traceback" in err
    assert ("rollout: TicketManager: TypeError: a bug (traceback on stderr); run it by hand with: land.sh"
            " mpaulosky/TicketManager#130; held:") in out and "reapply.sh" not in out.split("held:")[0]


LEFT = auto.GroupLeft("what reapply.sh left in its process group was still there 10 s after it was killed")


def test_a_group_ticketmanagers_reapply_left_holds_the_round():
    fleet = Fleet(raises={TM: LEFT})
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and reapplied(fleet) == ["TicketManager"] and err == ""
    assert f"rollout: TicketManager: {LEFT}; held: IssueTracker," in out


def test_a_group_one_of_the_rests_reapply_left_is_a_stop_and_the_round_goes_on():
    fleet = Fleet(raises={ARTICLES: LEFT})
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and reapplied(fleet) == ["TicketManager", *map(short, REST)] and "crashed" not in err
    assert f"  Articles: {LEFT}\n" in out


def test_a_group_ticketmanagers_land_left_holds_the_round_and_names_land_sh():
    fleet = Fleet(opens={TM: 130}, land_raises=LEFT)
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and "crashed" not in err
    assert f"rollout: TicketManager#130: {LEFT}; then land it with land.sh mpaulosky/TicketManager#130; held:" in out


def test_a_group_the_rests_land_left_is_a_stop_and_each_pr_is_still_read():
    fleet = Fleet(opens={REST[0]: 200}, land_raises=LEFT)
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert f"  land.sh: {LEFT}\n" in out
    assert "  IssueTracker#200 didn't land (#200 still reads as OPEN): land.sh mpaulosky/IssueTracker#200" in out


def test_a_crash_on_one_of_the_rest_is_reported_and_the_round_goes_on():
    fleet = Fleet(raises={ARTICLES: OSError(8, "Exec format error")})
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert reapplied(fleet) == ["TicketManager", *map(short, REST)]
    assert "  Articles: OSError: [Errno 8] Exec format error (traceback on stderr); run it by hand with: reapply.sh --brings '#82' /r/Articles" in out
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
    for line, repo in zip(lines[1:7], (TM, *REST), strict=True):
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


def test_the_header_says_what_holds_and_what_only_backs_it_up():
    doc = " ".join(auto.__doc__.split())
    assert "so it reviews only, and runs in Claude Code's sandbox (session_settings; ADR 0016)" in doc
    assert "It edits, fixes and commits nothing, and runs no gate, so nothing it wrote runs later as the person" in doc
    assert "the worktree's HEAD, branch or status changed" in doc
    assert "can't leave the sandbox; the session refuses to start without the sandbox (bubblewrap and socat, which the preflight checks for)" in doc
    assert "writes only a temporary folder of its own (session_writes: never the worktree, its git folder, the ledgers or .security)" in doc
    assert "the round records that with the worktree's scripts/review.sh record (record_review)" in doc
    assert "the worktree's .git link changed" in doc
    assert "git's push is sent nowhere: layers behind the sandbox" in doc
    assert "a SIGKILL of rollout.sh can't be caught, and leaves the session running (holding the lock until it ends)" in doc
    assert "every child the round starts holds the lock as well" in doc
    assert "and of their data only the worktree, the clone's git folder, .security and its own folder" in doc
    assert "as listed when the session starts, so an entry made after that is readable" in doc


def test_git_branch_says_whether_the_branch_has_commits_main_lacks(tmp_path, monkeypatch):
    monkeypatch.setenv("LC_ALL", "C")
    monkeypatch.setenv("GIT_CEILING_DIRECTORIES", str(tmp_path.parent))
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


@pytest.mark.parametrize("reads, ok", [(auto.LOOKUP_TRIES - 1, True), (auto.LOOKUP_TRIES, False)])
@pytest.mark.parametrize("repo", [TM, REST[0]])
def test_a_pushed_pr_the_list_doesnt_show_yet_is_waited_for(repo, reads, ok):
    fleet = Fleet(opens={repo: 300}, unlisted={repo: reads})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert (code == 0) == ok, out
    if ok:
        assert any(f"{repo}#300" in refs for refs in fleet.landed)
    else:
        assert f"{short(repo)}: can't tell its re-Apply PR (no re-Apply PR shows head {head_of(repo)[:7]})" in out
        assert not any(f"{repo}#300" in refs for refs in fleet.landed), "never counted as nothing to land"


def test_an_open_pr_at_the_worktrees_own_head_with_nothing_ahead_is_landed():
    # A resume: the push went out in an earlier run, so the branch is level with what the PR shows.
    fleet = Fleet(prs={TM: (131, "OPEN", head_of(TM))})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert fleet.landed[0] == (f"{TM}#131",)


@pytest.mark.parametrize("fails, ok", [(auto.LOOKUP_TRIES - 1, True), (auto.LOOKUP_TRIES, False)])
@pytest.mark.parametrize("repo", [TM, REST[0]])
def test_a_landed_pr_read_that_fails_is_tried_again(repo, fails, ok):
    fleet = Fleet(opens={repo: 300}, state_failures={repo: fails})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert (code == 0) == ok, out
    if not ok:
        assert "(gh failed: HTTP 503)" in out


def test_a_land_sh_that_exits_non_zero_for_the_rest_says_so():
    fleet = Fleet(opens={REST[0]: 200}, land_code=1)
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert "rollout.sh: land.sh exited 1 (see its output above)" in out


def test_an_interrupted_round_prints_the_resume_command_and_releases_the_lock():
    for stop, code in ((KeyboardInterrupt(), 130), (SystemExit(143), 143)):
        fleet = Fleet(raises={TM: stop})
        result, out, err = run_auto(["--brings", "#82"], fleet, root="/my repos")
        assert result == code
        assert "rollout.sh: interrupted; resume with: rollout.sh --auto --brings '#82' --root '/my repos'" in err
        assert fleet.locks[0].closed


@pytest.mark.parametrize("call", ["gh_pr", "gh_pr_state", "git_branch"])
def test_gh_and_git_output_that_isnt_utf_8_is_read(call, monkeypatch):
    seen = []

    def fake(command, **kwargs):
        seen.append(kwargs)
        return subprocess.CompletedProcess(command, 1, "", "bad \ufffd byte")

    monkeypatch.setattr(auto.subprocess, "run", fake)
    with pytest.raises(RuntimeError, match="bad \ufffd byte"):
        {"gh_pr": lambda: auto.gh_pr(TM), "gh_pr_state": lambda: auto.gh_pr_state(TM, 1),
         "git_branch": lambda: auto.git_branch("/w")}[call]()
    assert all(kwargs.get("errors") == "replace" and kwargs.get("text") is True for kwargs in seen)


def test_a_read_that_answers_after_a_gh_failure_is_what_the_stop_says():
    fleet = Fleet(opens={TM: 130}, state_failures={TM: 1}, still_open={f"{TM}#130"})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "TicketManager: can't tell its re-Apply PR (#130 still reads as OPEN)" in out


def test_a_session_that_broke_a_rule_stops_for_the_person_without_reapplying():
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED]}, reviews={TM: auto.SESSION_BROKE_A_RULE},
                  broke={TM: ["the remote chore/reapply-baseline moved: the session isn't to push", "x"]})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and len(reapplied(fleet)) == 1
    here = "/r/TicketManager-worktrees/chore-reapply-baseline"
    assert ("TicketManager: the review session broke a rule, so nothing was recorded: the remote chore/reapply-baseline"
            " moved: the session isn't to push (and 1 more); see /tmp/review-TicketManager.log. Put it right before you"
            f" resume, since a resume starts from what it finds: git -C {here} status --ignored shows the worktree,"
            f" cd {here} && scripts/review.sh report shows the review ledgers, and cd {here} && scripts/review.sh"
            " security shows the .security record") in out


def test_rollouts_reapply_path_has_its_symlinks_resolved(tmp_path):
    link = tmp_path / "skill"
    link.symlink_to(SKILL)
    script = f"import runpy, sys; sys.path.insert(0, {str(link / 'github')!r}); print(runpy.run_path({str(link / 'github' / 'rollout.py')!r})['REAPPLY'])"
    out = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True, check=True).stdout.strip()
    assert out == os.path.realpath(SKILL / "reapply.sh")


def test_a_round_with_ticketmanager_alone_holds_none():
    out = io.StringIO()
    code = auto.auto_round((TM,), "#82", "rollout.sh --auto --brings '#82'", lambda repo: f"/r/{short(repo)}",
                           lambda brings, path: 1, None, None, None, None, None, lambda text: out.write(text + "\n"),
                           io.StringIO())
    assert code == 1 and "held: none" in out.getvalue()


def test_git_branch_gives_up_on_a_git_that_hangs(monkeypatch):
    seen = []
    monkeypatch.setattr(auto.subprocess, "run", lambda command, **kwargs: seen.append(kwargs) or subprocess.CompletedProcess(command, 0, "abc\n", ""))
    auto.git_branch("/w")
    assert all(kwargs.get("timeout") == 120 for kwargs in seen) and len(seen) == 2


def test_a_sigterm_while_a_repo_is_reapplied_ends_the_round_with_its_resume_command():
    fleet = Fleet()
    reapply = fleet.reapply

    def terminated(brings, path):
        os.kill(os.getpid(), signal.SIGTERM)
        time.sleep(1)
        return reapply(brings, path)

    fleet.reapply = terminated
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 128 + signal.SIGTERM
    assert "rollout.sh: interrupted; resume with: rollout.sh --auto --brings '#82'" in err
    assert fleet.locks[0].closed


def test_the_round_passes_its_lock_to_what_it_runs_and_lets_go_after():
    fleet = Fleet()
    reapply, held = fleet.reapply, []
    fleet.reapply = lambda brings, path: held.append(auto.held_lock_fds()) or reapply(brings, path)
    run_auto(["--brings", "#82"], fleet)
    assert held and all(fds == (42,) for fds in held), "a child a SIGKILL of rollout.sh leaves running keeps the lock"
    assert auto.held_lock_fds() == ()


def test_a_session_that_broke_one_rule_names_it_alone():
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED]}, reviews={TM: auto.SESSION_BROKE_A_RULE}, broke={TM: ["just this"]})
    _, out, _ = run_auto(["--brings", "#82"], fleet)
    assert "the review session broke a rule, so nothing was recorded: just this; see " in out and "(and" not in out


def test_the_guard_of_a_linked_worktree_reads_the_primary_checkouts_ledgers(tmp_path):
    primary = tmp_path / "Repo"
    git = git_repo(primary)
    worktree = tmp_path / "Repo-worktrees" / "chore-reapply-baseline"
    git("worktree", "add", "-q", "-b", "chore/reapply-baseline", str(worktree))
    (primary / ".git" / "review").mkdir()
    (primary / ".git" / "review" / "l.json").write_text(json.dumps({"branch": "chore/reapply-baseline", "findings": []}))
    owned = tmp_path / "owned"
    owned.mkdir()
    guard = auto.session_guard(str(worktree), owned=str(owned),
                               read=lambda c, cwd=None: "" if "ls-remote" in c else auto.run_text(c, cwd))
    assert guard.ledgers == str(primary / ".git" / "review")
    assert set(guard.ledgers_before) == {"l.json"}
    assert guard.security == str(primary / ".security")
    (primary / ".git" / "review" / "l.json").write_text(json.dumps({"branch": "x", "findings": [{"id": "R1", "status": "accepted"}]}))
    broke = auto.broken_rules(guard, owned=str(owned), read=lambda c, cwd=None: "" if "ls-remote" in c else auto.run_text(c, cwd),
                              changes=lambda w: [])
    assert broke == [FOLDER_CHANGED.format("l.json", primary / ".git" / "review", "changed", "the review ledgers")]


def test_the_adr_says_what_the_sandbox_doesnt_cover():
    adr = next((SKILL.parents[2] / "docs" / "adr").glob("0016-*.md")).read_text()
    text = " ".join(adr.split())
    assert "The sandbox holds only as far as Claude Code enforces it" in text
    assert ("its reads are denials listed as the session starts, so what is made beside its folders while it runs"
            " (another checkout's `.env`, say) is readable until it ends, and is named then") in text
    assert "a list of what to hide" not in text, "the walk from / replaced the list"
    assert "`rollout.sh --auto` runs on Linux only" in text
    assert "with no host to reach, what the session reads can leave only through what it writes" in text
    assert "It reaches no host" in text and "reachable for anything" not in text, "no host is on the allowed list"
    assert "A SIGKILL of `rollout.sh` can't be caught" in text
    assert "however it is spelled" not in text and "this is not a sandbox" not in text, "the earlier overclaims are gone"
