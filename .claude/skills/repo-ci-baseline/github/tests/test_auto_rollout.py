"""Tests for github/auto_rollout.py (rollout.sh --auto), with reapply, the review session, land.sh, gh and git faked.
No GitHub, no Claude."""

import io
import json
import re
import os
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
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
                   environ=None):
    answers = {"shfmt": (0, "v3.14.1\n", ""), "gh": (0, "", ""), "dotnet": (0, "10.0.404\n", ""), "bwrap": (0, "", ""),
               "claude": (0, "2.1.326 (Claude Code)\n", ""), "ruff": (0, "ruff 0.17.6\n", ""),
               "claude auth": (0, '{"loggedIn": true, "email": "someone@example.com"}\n', "")} | dict(answers or {})
    calls = []

    def run(command, cwd=None, env=None):
        seen = None
        if cwd is not None and os.path.isfile(os.path.join(cwd, "global.json")):
            seen = Path(cwd, "global.json").read_text()
        calls.append({"command": tuple(command), "cwd": cwd, "env": env, "global.json": seen})
        return answers["claude auth" if tuple(command[:2]) == ("claude", "auth") else command[0]]

    path = os.path.join(tmp_path, "global.json") if tmp_path else "/nonexistent/global.json"
    if global_json is not None and tmp_path:
        Path(path).write_text(global_json)
    problems = auto.preflight(which=lambda tool: f"/bin/{tool}" if tool in on_path else None, run=run,
                              template_global_json=path, environ=environ or {"PATH": "/bin"})
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


NOT_SIGNED_IN = ("as claude auth status says: sign it in with claude /login (the review session reads no settings file,"
                 " so an apiKeyHelper in one isn't used)")


@pytest.mark.parametrize("answer, problem", [
    ((0, '{"loggedIn": true, "email": "someone@example.com"}', ""), None),
    ((0, '{"loggedIn": false, "email": "someone@example.com"}', ""), f"claude isn't signed in, {NOT_SIGNED_IN}"),
    ((0, '{"loggedIn": "true"}', ""), f"claude isn't signed in, {NOT_SIGNED_IN}"),
    ((0, '{"email": "someone@example.com"}', ""), f"claude isn't signed in, {NOT_SIGNED_IN}"),
    ((0, "[]", ""), f"claude isn't signed in, {NOT_SIGNED_IN}"),
    ((0, "not json", ""), f"claude isn't signed in, {NOT_SIGNED_IN}"),
    ((1, '{"loggedIn": true}', "x"), f"claude isn't signed in (exit 1), {NOT_SIGNED_IN}"),
])
def test_preflight_wants_claude_signed_in_and_shows_nothing_of_the_account(answer, problem, tmp_path):
    problems, calls = fake_preflight(answers={"claude auth": answer}, tmp_path=tmp_path)
    assert problems == ([] if problem is None else [problem])
    assert ("claude", "auth", "status", "--json") in [call["command"] for call in calls]
    assert not any("someone@example.com" in text for text in problems), "the account isn't printed"


def test_a_claude_that_doesnt_run_isnt_asked_whether_it_is_signed_in(tmp_path):
    problems, calls = fake_preflight(answers={"claude": (127, "", "No such file")}, tmp_path=tmp_path)
    assert problems == ["claude --version failed (exit 127): No such file"]
    assert not any(call["command"][:2] == ("claude", "auth") for call in calls)


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
                      security=str(tmp_path / "clone" / ".security"), security_before={}, worktree_state=("h", "h", ""))


SKILL_TEXT = "# Pre-push review\n\nThe skill's own steps.\n"
AGENTS = {"pre-push-reviewer": {"description": "d", "prompt": "p", "tools": ["Read"]}}


def review_once(tmp_path, monkeypatch, popen=None, changes=None, worktree=None, session=None, end=None, broke=(),
                clock=None, guard=None):
    seen = {}
    session = session or FakeSession()
    ended = seen.setdefault("ended", [])

    def fake_popen(command, **kwargs):
        seen.update(command=command, **kwargs)
        seen["settings"] = json.loads(command[command.index("--settings") + 1])
        seen["tmp"] = os.path.dirname(kwargs["env"]["TMPDIR"])
        seen["gh_config"] = sorted(os.listdir(kwargs["env"]["GH_CONFIG_DIR"]))
        seen["folders"] = {name: os.path.isdir(tmp_path / "clone" / name) and not os.path.islink(tmp_path / "clone" / name)
                           for name in (".git/review", ".security")}
        kwargs["stdout"].write("the session's own output\n")
        return session

    monkeypatch.setenv("GATE_SKIP_REVIEW", "1")
    if worktree is None:
        worktree = tmp_path / "TicketManager-worktrees" / "chore-reapply-baseline"
        worktree.mkdir(parents=True)
    code, log, _ = auto.run_review(TM, str(worktree), popen=popen or fake_popen,
                                   changes=changes or (lambda path: [".github/hooks/pre-push", "scripts/gate.sh"]),
                                   log_dir=str(tmp_path / "logs"), end=end or (lambda s: ended.append(s) or True),
                                   guard=guard or (lambda w: seen.setdefault("guarded", w) and fake_guard(tmp_path)),
                                   check=lambda g: list(broke), skill=lambda: SKILL_TEXT, agents=lambda: AGENTS,
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
    assert seen["settings"] == auto.session_settings(worktree, str(clone), str(clone / "worktrees" / "chore-reapply-baseline"),
                                                     seen["tmp"]), "the settings are built from the guard's folders"
    assert seen["tmp"].startswith(tempfile.gettempdir()) and os.path.basename(seen["tmp"]).startswith("rollout-session.")
    assert seen["folders"] == {".git/review": True, ".security": True}, \
        "the ledger folder and the .security record are made before the session, so the sandbox can let them be written"
    assert stat.S_IMODE((clone / "review").stat().st_mode) == stat.S_IMODE((clone.parent / ".security").stat().st_mode) == 0o700
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
                 "record every finding it keeps open, with its suggested fix", "The worktree is read-only to you",
                 "what to change in the Factory's template/owned/", "Don't push",
                 "don't file the follow-up issue", "don't edit a PR", "skip the skill's GitHub reads",
                 "and its git fetch", "go on with origin/main as it is", "no network", "End with the output of scripts/review.sh report"):
        assert rule in prompt, rule


def test_a_ledger_folder_and_record_already_there_are_kept_as_they_are(tmp_path, monkeypatch):
    (tmp_path / "clone" / ".git" / "review").mkdir(parents=True)
    (tmp_path / "clone" / ".git" / "review" / "l.json").write_text("{}")
    (tmp_path / "clone" / ".security").mkdir()
    code, _, seen, _ = review_once(tmp_path, monkeypatch)
    assert code == 0 and seen["folders"] == {".git/review": True, ".security": True}
    assert (tmp_path / "clone" / ".git" / "review" / "l.json").read_text() == "{}"


@pytest.mark.parametrize("name", [".git/review", ".security"])
@pytest.mark.parametrize("what", ["symlink", "file"])
def test_a_ledger_folder_or_record_that_isnt_a_real_folder_starts_no_session(tmp_path, monkeypatch, name, what):
    (tmp_path / "clone" / ".git").mkdir(parents=True)
    place = tmp_path / "clone" / name
    if what == "symlink":
        (tmp_path / "elsewhere").mkdir()
        place.symlink_to(tmp_path / "elsewhere")
    else:
        place.write_text("x")
    started = []
    code, log, _, _ = review_once(tmp_path, monkeypatch, popen=lambda *a, **k: started.append(a))
    assert code == 127 and started == []
    assert f"rollout.sh: can't set the session up: {place} isn't a real folder: inspect it and remove it" in Path(log).read_text()
    assert not os.listdir(tmp_path / "elsewhere") if what == "symlink" else place.read_text() == "x"


def test_the_sessions_folders_and_settings_are_gone_after_it(tmp_path, monkeypatch):
    _, _, seen, _ = review_once(tmp_path, monkeypatch)
    assert not os.path.exists(seen["env"]["GH_CONFIG_DIR"]) and not os.path.exists(seen["env"]["TMPDIR"])


def test_the_session_settings_sandbox_its_bash_and_fence_its_file_tools(tmp_path):
    home = str(tmp_path / "home")
    common = str(tmp_path / "clone" / ".git")
    gitdir = os.path.join(common, "worktrees", "wt")
    settings = auto.session_settings("/w/wt", common, gitdir, "/t/session", home=home, config_dir="", environ={})
    sandbox = settings["sandbox"]
    assert (sandbox["enabled"], sandbox["failIfUnavailable"], sandbox["allowUnsandboxedCommands"],
            sandbox["excludedCommands"]) == (True, True, False, [])
    security = str(tmp_path / "clone" / ".security")
    credentials = [home + path[1:] for path in auto.CREDENTIAL_PATHS]
    assert sandbox["filesystem"] == {
        "allowWrite": [os.path.join(common, "review"), security, "/t/session"],
        "denyWrite": ["/w/wt", gitdir],
        "denyRead": credentials,
    }, "it writes the ledgers, .security and its own folder: not the worktree, its working folder, nor its git folder"
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


def denied_beyond_the_list(environ, home="/h"):
    deny = auto.session_settings("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home=home, environ=environ)
    listed = [home + path[1:] for path in auto.CREDENTIAL_PATHS]
    found = deny["sandbox"]["filesystem"]["denyRead"]
    assert found[:len(listed)] == listed
    for path in found:
        assert f"Read(/{path})" in deny["permissions"]["deny"] and f"Read(/{path}/**)" in deny["permissions"]["deny"]
    return found[len(listed):]


def test_the_session_settings_keep_claude_codes_own_config_folder_from_it(tmp_path, monkeypatch):
    monkeypatch.setenv("CLAUDE_CONFIG_DIR", "/cfg/claude")
    settings = auto.session_settings("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home="/h", environ=dict(os.environ))
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
    settings = auto.session_settings("/w/wt", "/x/Repo.git", "/x/Repo.git/worktrees/wt", "/t", environ={})
    assert settings["sandbox"]["filesystem"]["denyRead"][0] == str(tmp_path / "me" / ".ssh")
    assert not any("~" in path for path in settings["sandbox"]["filesystem"]["denyRead"])
    assert "/x/.security" in settings["sandbox"]["filesystem"]["allowWrite"], \
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
    assert (auto.RECORD_BYTES, auto.LEDGER_FILE.pattern) == (16 * 1024 * 1024, r"\.(json|entries)$")
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
    code, log, _ = auto.run_review(TM, str(tmp_path), log_dir=str(link))
    assert code == 127 and "isn't a folder of the user's own" in log
    monkeypatch.setattr(auto.os, "getuid", lambda: os.stat(tmp_path / "logs").st_uid + 1)
    code, log, _ = auto.run_review(TM, str(tmp_path), log_dir=str(tmp_path / "logs"))
    assert code == 127 and "isn't a folder of the user's own" in log, "another user's folder is refused too"


def test_the_prompt_says_none_when_no_owned_file_changes():
    prompt = auto.review_command(TM, "/w", [], {}, AGENTS, SKILL_TEXT)[2]
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


def test_a_group_that_does_not_go_away_is_noted_in_the_log(tmp_path, monkeypatch):
    code, log, _, _ = review_once(tmp_path, monkeypatch, end=lambda s: False)
    assert code == 0
    assert f"process group was still there {auto.GROUP_WAIT_SECONDS} s after it was killed" in Path(log).read_text()


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

    code, log, _ = auto.run_review(TM, str(worktree), popen=popen, changes=lambda path: [], timeout=timeout,
                                   log_dir=str(tmp_path / "logs"), guard=lambda w: fake_guard(tmp_path),
                                   check=lambda g: [], skill=lambda: SKILL_TEXT, agents=lambda: AGENTS)
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

    assert auto.run_in_group(["x"], popen=popen, end=ended.append) == 3
    assert seen == {"command": ["x"], "start_new_session": True, "pass_fds": (),
                    "env": {**os.environ, **auto.NO_BUILD_SERVERS}}, "no build server is left to outlive it"
    assert len(ended) == 1, "its group is ended when it exits, too"
    with pytest.raises(SystemExit):
        auto.run_in_group(["x"], popen=lambda c, **k: _Raising(), end=ended.append)
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
        auto.run_in_group(["x"], popen=lambda c, **k: Signalled(), end=ended.append)
    assert stop.value.code == 128 + signal.SIGTERM and len(ended) == 1


def test_run_in_group_that_cant_start_passes_a_signal_on_before_its_error():
    def popen(command, **kwargs):
        os.kill(os.getpid(), signal.SIGHUP)
        time.sleep(0.05)
        raise FileNotFoundError(2, "No such file", "x")

    with pytest.raises(SystemExit) as stop:
        auto.run_in_group(["x"], popen=popen)
    assert stop.value.code == 128 + signal.SIGHUP
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

    def entry(self, name, status, text="the weakness"):
        path = self.security / name
        path.write_text(f"# {name}\n\n- Status: {status}\n- Lens: Security\n\n## Weakness\n\n{text}\n")
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
    assert g.check(changes=lambda w: []) == [CHANGED.format(g.worktree)]


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
                                      f" with: git -C {g.worktree} rev-parse --path-format=absolute --git-common-dir")


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
    assert g.check() == [owned], "an untracked file edited: the status reads the same, the Owned check catches it"
    (g.worktree / "scripts" / "gate.sh").unlink()
    assert g.check() == [CHANGED.format(g.worktree), owned]


def test_an_owned_list_that_fails_after_the_session_is_a_stop(tmp_path):
    g = Guarded(tmp_path)
    g.guard()

    def broken(worktree):
        raise RuntimeError("git diff failed")

    assert g.check(changes=broken) == ["can't list the Owned files the branch changes: git diff failed"]


LEDGER_CHANGED = ("{} was {} during the session (the session, or your own review on another branch meanwhile): a"
                  " review-only session writes only a new ledger")
NEW_DECIDED = "{} appeared during the session with {}: a review-only session fixes nothing, and only the person accepts"


def test_a_new_ledger_and_its_entry_map_with_open_and_dismissed_findings_break_nothing(tmp_path):
    g = Guarded(tmp_path)
    g.ledger("old.json", [{"id": "R1", "status": "accepted"}])
    (g.ledgers / "old.entries").write_text("{}\n")
    g.guard()
    g.ledger("new.json", [{"id": "R1", "status": "open"}, {"id": "R2", "status": "dismissed"}, "R3"])
    (g.ledgers / "new.entries").write_text('{"R1": "0001"}\n')
    (g.ledgers / ".lock").write_text("pid")
    (g.ledgers / "new.json.123.tmp").write_text("{")
    assert g.check() == [], "record's lock and a write in progress aren't the ledgers"


@pytest.mark.parametrize("findings, decided", [
    ([{"id": "R2", "status": "fixed"}, {"id": "R3", "status": "accepted"}, {"id": "R4", "status": "open"}],
     "R2 fixed, R3 accepted"),
    ([{"id": "R9", "status": "accepted"}], "R9 accepted"),
    ([{"id": "R9", "status": "fixed"}], "R9 fixed"),
])
def test_a_new_ledger_with_a_finding_fixed_or_accepted_is_caught_whatever_its_mtime(tmp_path, findings, decided):
    g = Guarded(tmp_path)
    g.guard()
    path = g.ledger("new.json", findings)
    os.utime(path, (0, 0))  # backdated: still caught, by content
    assert g.check() == [NEW_DECIDED.format(path, decided)]


@pytest.mark.parametrize("branch, head", [("feature/7-x", "1" * 40), ("something-else", None), ("chore/reapply-baseline", None)])
@pytest.mark.parametrize("name", ["l.json", "l.entries"])
def test_a_ledger_or_entry_map_already_there_that_changes_is_caught_whatever_it_names(tmp_path, branch, head, name):
    # The session can write those fields too, so a ledger can't vouch for itself; flipping a finding to fixed or
    # dismissed, or rewriting the entry map an accept goes by, is as much a decision as accepting it.
    g = Guarded(tmp_path)
    g.ledger(name, [{"id": "R1", "status": "open"}], branch=branch, head=head)
    g.guard()
    path = g.ledger(name, [{"id": "R1", "status": "dismissed", "reason": "x"}], branch=branch, head=head)
    assert g.check() == [LEDGER_CHANGED.format(path, "changed")]


@pytest.mark.parametrize("name", ["l.json", "l.entries", "bad.json"])
def test_a_ledger_or_entry_map_removed_during_the_session_is_caught(tmp_path, name):
    g = Guarded(tmp_path)
    g.ledger(name, [{"id": "R1", "status": "open"}], raw="{" if name == "bad.json" else None)
    g.guard()
    (g.ledgers / name).unlink()
    assert g.check() == [LEDGER_CHANGED.format(g.ledgers / name, "removed")]


@pytest.mark.parametrize("raw", ['{"findings": 5}', '{"findings": "x"}', "[]", "{", '{"findings": null}', "\xff"])
def test_a_new_ledger_that_doesnt_read_is_caught(tmp_path, raw):
    g = Guarded(tmp_path)
    g.guard()
    path = g.ledgers / "bad.json"
    path.write_bytes(raw.encode("latin-1"))
    assert g.check() == [f"{path} appeared during the session and doesn't read as a ledger"]


def test_a_ledger_that_didnt_read_before_and_is_unchanged_is_not_the_sessions(tmp_path):
    g = Guarded(tmp_path)
    g.ledger("old-bad.json", [], raw="{")
    g.guard()
    assert g.check() == []


@needs_proc
@pytest.mark.parametrize("what", ["fifo", "zero", "dangling", "link to a ledger", "oversized"])
def test_a_ledger_that_isnt_a_small_regular_file_is_never_read_and_is_caught(tmp_path, what, monkeypatch):
    # The session can make anything in the ledger folder: the round reads none of it in a way that hangs or fills
    # memory (record_bytes), and it doesn't read as a ledger.
    g = Guarded(tmp_path)
    g.guard()
    path = g.ledgers / "x.json"
    if what == "fifo":
        os.mkfifo(path)
    elif what == "zero":
        path.symlink_to("/dev/zero")
    elif what == "dangling":
        path.symlink_to(tmp_path / "nowhere.json")
    elif what == "link to a ledger":
        (tmp_path / "real.json").write_text(json.dumps({"findings": []}))
        path.symlink_to(tmp_path / "real.json")
    else:
        monkeypatch.setattr(auto, "RECORD_BYTES", 10)
        path.write_text(json.dumps({"findings": []}))
    assert g.check() == [f"{path} appeared during the session and doesn't read as a ledger"]


def test_record_bytes_reads_a_regular_file_up_to_its_limit_only(tmp_path):
    path = tmp_path / "f"
    path.write_bytes(b"x" * 10)
    assert auto.record_bytes(str(path), limit=10) == b"x" * 10
    assert auto.record_bytes(str(path), limit=9) is None
    assert auto.record_bytes(str(path), limit=11) == b"x" * 10
    assert auto.record_bytes(str(tmp_path / "missing")) is None
    assert auto.record_bytes(str(tmp_path)) is None, "a folder isn't read"


def test_the_security_record_may_gain_open_entries(tmp_path):
    g = Guarded(tmp_path)
    g.entry("0001-a.md", "open")
    g.entry("0002-b.md", "fixed")
    (g.security / "README.md").write_text("# The record\n")
    g.guard()
    g.entry("0003-c.md", "open")
    (g.security / "README.md").write_text("# The record, as the person edited it\n")
    (g.security / "0004-d.md.123.ab.tmp").write_text("- Status: accepted\n")
    assert g.check() == [], "the README and a write in progress aren't entries"


def test_a_temporary_file_there_before_and_gone_after_is_no_removed_entry(tmp_path):
    g = Guarded(tmp_path)
    (g.security / "0001-a.md.99.ff.tmp").write_text("- Status: open\n")
    g.guard()
    (g.security / "0001-a.md.99.ff.tmp").unlink()
    assert g.check() == []


@pytest.mark.parametrize("before, after", [
    ("accepted", None),
    ("open", "accepted"),
    ("open", "fixed"),
    ("fixed", "open"),
    ("fixed", "fixed"),
    ("accepted", "fixed"),
    ("open", "open"),
])
def test_the_security_record_losing_or_changing_an_entry_is_caught(tmp_path, before, after):
    # A review-only session records no finding fixed, so its record marks no open entry fixed either.
    g = Guarded(tmp_path)
    path = g.entry("0001-a.md", before)
    g.guard()
    if after is None:
        path.unlink()
        assert g.check() == [f"{path} was removed during the session"]
    else:
        g.entry("0001-a.md", after, text="another weakness")
        assert g.check() == [f"{path} changed during the session ({before} to {after}): a review-only session's"
                             " record changes no entry"]


@pytest.mark.parametrize("status, shown", [("accepted", "accepted"), ("fixed", "fixed"), ("maybe", "unreadable")])
def test_a_new_security_entry_that_isnt_open_is_caught(tmp_path, status, shown):
    g = Guarded(tmp_path)
    g.guard()
    path = g.entry("0009-z.md", status)
    assert g.check() == [f"{path} appeared during the session as {shown}: a review-only session's record writes only"
                         " open entries"]


@pytest.mark.parametrize("what", ["fifo", "symlink"])
def test_a_security_entry_that_isnt_a_regular_file_is_never_read(tmp_path, what):
    g = Guarded(tmp_path)
    g.guard()
    path = g.security / "0009-z.md"
    if what == "fifo":
        os.mkfifo(path)
    else:
        (tmp_path / "e.md").write_text("- Status: open\n")
        path.symlink_to(tmp_path / "e.md")
    assert g.check() == [f"{path} appeared during the session as unreadable: a review-only session's record writes only"
                         " open entries"]


def test_a_missing_security_record_reads_as_no_entries(tmp_path):
    assert auto.security_state(str(tmp_path / "none")) == {}
    (tmp_path / "rec").mkdir()
    (tmp_path / "rec" / "0001-sub.md").mkdir()  # a folder named as an entry isn't read
    (tmp_path / "rec" / "0002-x.md").write_text("- Status: maybe\n")
    (tmp_path / "rec" / "x.md").write_text("- Status: open\n")
    state = auto.security_state(str(tmp_path / "rec"))
    assert list(state) == ["0001-sub.md", "0002-x.md"] and state["0001-sub.md"] == (None, None)
    assert state["0002-x.md"][1] is None


@pytest.mark.parametrize("text, status", [
    ("- Status: open\n", "open"),
    ("- Status: fixed  \n", "fixed"),
    ("- Status: accepted", "accepted"),
    ("intro\n- Status: open\n", "open"),
    ("- Status: open\n\n- Status: accepted\n", "open"),
    ("- Status: openx\n", None),
    ("- Status: Open\n", None),
    ("  - Status: open\n", None),
    ("Status: open\n", None),
    ("x- Status: open\n", None),
    ("- Status: open x\n", None),
])
def test_an_entrys_status_line_reads_as_the_record_reads_it(tmp_path, text, status):
    (tmp_path / "0001-a.md").write_text(text)
    assert auto.security_state(str(tmp_path))["0001-a.md"][1] == status


@pytest.mark.parametrize("name, entry", [("0001-a.md", True), ("12-b.md", True), ("0001-.md", True), ("0001a.md", False),
                                         ("a-0001.md", False), ("0001-a.md.1.ff.tmp", False), ("README.md", False),
                                         ("0001-a.mdx", False), ("\u0661-a.md", False)])
def test_an_entry_is_named_as_the_record_names_it(name, entry):
    assert bool(auto.SECURITY_ENTRY.search(name)) is entry


def test_the_entry_name_and_status_patterns_are_the_records_own():
    record = (SKILL / "template/owned/.sandcastle/lib/security-record.mts").read_text()
    assert f"/{auto.SECURITY_ENTRY.pattern}/.exec(file)" in record
    assert f"/{auto.SECURITY_STATUS.pattern}/m.exec(text)" in record
    assert auto.SECURITY_STATUS.flags & re.M and auto.SECURITY_ENTRY.flags & re.A


def test_link_text_reads_only_a_plain_file(tmp_path):
    assert auto.link_text(str(tmp_path)) is None
    (tmp_path / ".git").write_bytes(b"gitdir: /x\n")
    assert auto.link_text(str(tmp_path)) == b"gitdir: /x\n"


def test_run_text_strips_trailing_newlines_only_and_gives_none_for_a_failure(tmp_path):
    assert auto.run_text(["printf", "  a\\n\\n"]) == "  a"
    assert auto.run_text(["false"]) is None
    assert auto.run_text(["/nonexistent/tool"]) is None
    assert auto.run_text(["pwd"], cwd=str(tmp_path)) == str(tmp_path)


def test_a_missing_ledger_folder_reads_as_no_ledgers(tmp_path):
    assert auto.ledger_state(str(tmp_path / "none")) == {}


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
    code, log, broke = auto.run_review(TM, str(worktree), popen=lambda c, **k: started.append(c),
                                       changes=lambda w: [], log_dir=str(tmp_path / "logs"),
                                       guard=lambda w: fake_guard(tmp_path), check=lambda g: [],
                                       **{"skill": lambda: SKILL_TEXT, "agents": lambda: AGENTS, which: broken})
    assert (code, broke, started) == (127, [], [])
    assert f"rollout.sh: can't set the session up: {error}" in Path(log).read_text()


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


@pytest.mark.parametrize("where", ["changes", "guard"])
def test_a_session_that_cant_be_set_up_never_starts(tmp_path, monkeypatch, where):
    started = []

    def broken(path):
        raise (auto.GuardUnreadable("can't read the remote") if where == "guard" else RuntimeError("git diff failed"))

    code, log, _, _ = review_once(tmp_path, monkeypatch, popen=lambda *a, **k: started.append(a),
                                  **{where: broken})
    assert code == 127 and started == []
    assert ("can't read the remote; the session wasn't started" if where == "guard" else "git diff failed") in Path(log).read_text()


def test_a_log_that_cannot_be_made_is_127(tmp_path, monkeypatch):
    def full(*args, **kwargs):
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(auto.tempfile, "mkstemp", full)
    code, log, _ = auto.run_review(TM, str(tmp_path), log_dir=str(tmp_path / "logs"))
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
    assert "stopped short in its sandbox (no GitHub sign-in, few hosts; see /tmp/review-TicketManager.log)" in out


def test_auto_reviews_a_second_time_when_the_first_session_left_no_record():
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED] * 2}, opens={TM: 130})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert len(fleet.reviewed) == 2


@pytest.mark.parametrize("exit_code, why", [
    (124, "ran past 180 minutes and was stopped"),
    (127, "couldn't be started (the log's rollout.sh: line says why)"),
    (1, "exited 1"),
    (125, "exited 125"),
])
def test_auto_stops_when_a_review_session_fails_and_says_what_to_do(exit_code, why):
    fleet = Fleet(codes={TM: [auto.REVIEW_NEEDED]}, reviews={TM: exit_code})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert len(reapplied(fleet)) == 1
    assert (f"TicketManager: the review session {why} (see /tmp/review-TicketManager.log); run /pre-push-review in"
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
    assert "it edits, fixes and commits nothing, and runs no gate, so nothing it wrote runs later as the person" in doc
    assert "the worktree's HEAD, branch or status changed" in doc
    assert "can't leave the sandbox; the session refuses to start without the sandbox (bubblewrap and socat on Linux" in doc
    assert "never the worktree or the rest of its git folder" in doc
    assert "the worktree's .git link changed" in doc
    assert "git's push is sent nowhere: layers behind the sandbox" in doc
    assert "a SIGKILL of rollout.sh can't be caught, and leaves the session running (holding the lock until it ends)" in doc
    assert "every child the round starts holds the lock as well" in doc


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
    assert ("TicketManager: the review session broke a rule: the remote chore/reapply-baseline moved: the session isn't to"
            " push (and 1 more); see /tmp/review-TicketManager.log. Put it right before you resume, since a resume starts"
            " from what it finds: git -C /r/TicketManager-worktrees/chore-reapply-baseline status shows the worktree, and"
            " cd /r/TicketManager-worktrees/chore-reapply-baseline && scripts/review.sh report (the ledgers) and"
            " scripts/review.sh security (the .security record) show the review's") in out


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
    assert "the review session broke a rule: just this; see " in out and "(and" not in out


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
    assert broke == [LEDGER_CHANGED.format(primary / ".git" / "review" / "l.json", "changed")]


def test_the_adr_says_what_the_sandbox_doesnt_cover():
    adr = next((SKILL.parents[2] / "docs" / "adr").glob("0016-*.md")).read_text()
    text = " ".join(adr.split())
    assert "The sandbox holds only as far as Claude Code enforces it" in text
    assert "its reads are a list of what to hide, so a secret in a file the list doesn't name" in text
    assert "with no host to reach, what the session reads can leave only through what it writes" in text
    assert "It reaches no host" in text and "reachable for anything" not in text, "no host is on the allowed list"
    assert "A SIGKILL of `rollout.sh` can't be caught" in text
    assert "however it is spelled" not in text and "this is not a sandbox" not in text, "the earlier overclaims are gone"
