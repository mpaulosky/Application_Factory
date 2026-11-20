"""Tests for github/auto_rollout.py (rollout.sh --auto), with reapply, the review session, land.sh, gh and git faked.
No GitHub, no Claude."""

import io
import os
import re
import stat
import subprocess
import sys
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
    # reapply.sh tells the two refusals apart by the text checkReview prints, under the gate's step header; a
    # reworded message or step would turn both into a plain failed push.
    ledger = (SKILL / "template/owned/.sandcastle/lib/review-ledger.mts").read_text()
    check = ledger[ledger.index("export function checkReview("):]
    check = check[:check.index("\n}\n")]
    assert f"message: `{reapply_constant('no_review_text')}" in check
    assert f"`${{blocking.length}} {reapply_constant('findings_wait_text')}" in check
    gate = (SKILL / "template/owned/scripts/gate.sh").read_text()
    assert f'step "{reapply_constant("review_step")}"' in gate
    assert 'step() { echo -e "\\n${CYAN}▶ $1${RESET}"; }' in gate, "reapply.sh's review_refusal reads this header form"


def test_shfmt_rule_is_check_gh_repos_own():
    checker = (SKILL / "template/owned/.github/scripts/check_gh_repo.py").read_text()
    assert f'SHFMT_RELEASE = re.compile(r"{auto.SHFMT_RELEASE.pattern}")' in checker
    assert f"SHFMT_OLDEST_MINOR = {auto.SHFMT_OLDEST_MINOR}" in checker


def test_the_paths_it_runs_exist():
    assert os.access(auto.LAND, os.X_OK)
    assert os.access(rollout.REAPPLY, os.X_OK)
    assert os.path.isdir(auto.OWNED)
    assert os.path.isfile(os.path.join(auto.FACTORY, "CLAUDE.md")), "FACTORY is the Factory's root"


def test_max_reviews_is_two():
    # The ADR says a session that ends without recording is run once more, then the round stops.
    assert auto.MAX_REVIEWS == 2


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


def test_preflight_checks_the_real_templates_global_json_by_default():
    assert auto.TEMPLATE_GLOBAL_JSON == os.path.join(auto.OWNED, "global.json")
    assert os.path.isfile(auto.TEMPLATE_GLOBAL_JSON)


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
    assert seen == [["git", "-C", "/w", "diff", "--name-only", "-z", "origin/main...HEAD"]]
    with pytest.raises(RuntimeError, match="git diff in /w failed: bad"):
        auto.owned_changes("/w", run=lambda command, **kwargs: (128, "", "bad"), owned=str(owned))


# ── The review session ──────────────────────────────────────────────────────

def review_once(tmp_path, monkeypatch, run=None, changes=None, worktree=None):
    seen = {}

    def fake_run(command, **kwargs):
        seen.update(command=command, **kwargs)
        return subprocess.CompletedProcess(command, 0)

    monkeypatch.setenv("GATE_SKIP_REVIEW", "1")
    monkeypatch.setattr(auto.tempfile, "tempdir", str(tmp_path))
    if worktree is None:
        worktree = tmp_path / "TicketManager-worktrees" / "chore-reapply-baseline"
        worktree.mkdir(parents=True)
    code, log = auto.run_review(TM, str(worktree), run=run or fake_run,
                                changes=changes or (lambda path: [".github/hooks/pre-push", "scripts/gate.sh"]))
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
    assert seen["timeout"] == auto.REVIEW_TIMEOUT_MINUTES * 60
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
    assert log.startswith(str(tmp_path)) and os.path.basename(log).startswith("review-TicketManager.") and log.endswith(".log")
    assert stat.S_IMODE(os.stat(log).st_mode) & 0o077 == 0


def test_a_review_session_past_its_time_is_stopped_and_noted(tmp_path, monkeypatch):
    def slow(command, **kwargs):
        raise subprocess.TimeoutExpired(command, kwargs["timeout"])

    code, log, _, _ = review_once(tmp_path, monkeypatch, run=slow)
    assert code == 124
    assert f"ran past {auto.REVIEW_TIMEOUT_MINUTES} minutes" in Path(log).read_text()


def test_a_claude_that_cannot_start_is_127_with_its_reason_in_the_log(tmp_path, monkeypatch):
    def missing(command, **kwargs):
        raise FileNotFoundError(2, "No such file", "claude")

    code, log, _, _ = review_once(tmp_path, monkeypatch, run=missing)
    assert code == 127 and "can't start claude" in Path(log).read_text()


def test_a_missing_worktree_is_named_and_nothing_starts(tmp_path, monkeypatch):
    started = []
    code, log, _, _ = review_once(tmp_path, monkeypatch, run=lambda *a, **k: started.append(a),
                                  worktree=tmp_path / "gone")
    assert code == 127 and started == []
    assert f"the re-Apply worktree {tmp_path / 'gone'} doesn't exist" in Path(log).read_text()


def test_an_owned_list_that_fails_stops_the_session_before_it_starts(tmp_path, monkeypatch):
    started = []

    def broken(path):
        raise RuntimeError("git diff failed")

    code, log, _, _ = review_once(tmp_path, monkeypatch, run=lambda *a, **k: started.append(a), changes=broken)
    assert code == 127 and started == [] and "git diff failed" in Path(log).read_text()


def test_a_log_that_cannot_be_made_is_127(tmp_path, monkeypatch):
    def full(*args, **kwargs):
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(auto.tempfile, "mkstemp", full)
    code, log = auto.run_review(TM, str(tmp_path))
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


class Fleet:
    """reapply, review, land, gh and git as the tests set them.

    codes: {repo: [reapply exit codes in order]}, 0 once they run out. A 0 pushes: the branch gets the repo's head
    beyond main, and opens (or keeps) the repo's PR from `opens`. prs: {repo: (number, state, head)} as gh reads
    them before the round. stale: {repo: reads that show the last round's PR first}. unlanded: PR refs land.sh leaves
    open. still_open: refs whose read stays OPEN after land.sh returned 0. unreadable: repos gh fails for.
    """

    def __init__(self, codes=None, opens=None, prs=None, reviews=None, unlanded=(), still_open=(), unreadable=(),
                 stale=None, problems=(), locked=False, raises=None):
        self.codes = {repo: list(seq) for repo, seq in (codes or {}).items()}
        self.opens = dict(opens or {})
        self.prs = {repo: (100, "MERGED", "0" * 40) for repo in LISTED} | dict(prs or {})
        self.ahead = {}  # repo: whether its branch has commits main lacks
        self.review_codes = dict(reviews or {})
        self.unlanded, self.still_open, self.unreadable = set(unlanded), set(still_open), set(unreadable)
        self.stale = dict(stale or {})
        self.problems, self.locked, self.raises = list(problems), locked, dict(raises or {})
        self.events, self.reviewed, self.landed, self.sleeps = [], [], [], []

    def repo_of(self, path):
        return "mpaulosky/" + Path(path).name.replace("-worktrees", "")

    def reapply(self, brings, path):
        repo = self.repo_of(path)
        self.events.append(("reapply", short(repo), brings, path))
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
        for ref in refs:
            repo = ref.split("#")[0]
            if ref not in self.unlanded:
                number, _, head = self.prs[repo]
                self.prs[repo] = (number, "MERGED", head)
        return 1 if self.unlanded & set(refs) else 0

    def pr_of(self, repo):
        if repo in self.unreadable:
            raise RuntimeError("HTTP 502")
        if self.stale.get(repo, 0) > 0:
            self.stale[repo] -= 1
            return 99, "MERGED", "f" * 40
        number, state, head = self.prs[repo]
        if state == "MERGED" and f"{repo}#{number}" in self.still_open:
            return number, "OPEN", head
        return self.prs[repo]

    def branch_of(self, worktree):
        repo = "mpaulosky/" + Path(worktree).parent.name.removesuffix("-worktrees")
        return head_of(repo), self.ahead.get(repo, False)

    def check(self):
        self.events.append(("check",))
        return self.problems

    def lock(self):
        return None if self.locked else object()


def run_auto(argv, fleet, root="/r"):
    out, err = io.StringIO(), io.StringIO()
    code = rollout.main(["--auto", "--root", root, *argv], listed=LISTED, reapply=fleet.reapply,
                        exists=lambda path: True, out=out, err=err,
                        auto={"review": fleet.review, "land": fleet.land, "pr_of": fleet.pr_of,
                              "branch_of": fleet.branch_of, "check": fleet.check, "lock": fleet.lock,
                              "sleep": fleet.sleeps.append, "default_root": "/r"})
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
    assert "resume with" not in out


def test_auto_runs_the_preflight_first_and_lands_ticketmanager_before_any_other_reapply():
    fleet = Fleet(opens={TM: 130, REST[0]: 200})
    run_auto(["--brings", "#82"], fleet)
    assert [event[0] for event in fleet.events[:4]] == ["check", "reapply", "land", "reapply"]
    assert fleet.events[3][1] == "IssueTracker"


def test_auto_stops_on_a_preflight_problem_with_nothing_run():
    fleet = Fleet(problems=["shfmt isn't on PATH", "gh isn't signed in: gh auth login"])
    code, out, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert fleet.events == [("check",)]
    assert "rollout.sh: shfmt isn't on PATH\nrollout.sh: gh isn't signed in: gh auth login\nrollout.sh: nothing run" in err


def test_auto_stops_when_another_run_holds_the_lock():
    fleet = Fleet(locked=True)
    code, _, err = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert reapplied(fleet) == []
    assert "another rollout.sh --auto holds" in err


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


def test_the_resume_and_retry_commands_carry_a_root_that_isnt_the_default():
    fleet = Fleet(codes={TM: [1]})
    code, out, _ = run_auto(["--brings", "#82"], fleet, root="/my repos")
    assert code == 1
    assert "retry it with: reapply.sh --brings '#82' '/my repos/TicketManager'" in out
    assert "resume with: rollout.sh --auto --brings '#82' --root '/my repos'" in out


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


def test_auto_waits_for_a_landed_pr_to_read_as_merged():
    fleet = Fleet(opens={TM: 130})
    still = {"n": 2}
    pr_of = fleet.pr_of

    def lagging(repo):
        number, state, head = pr_of(repo)
        if repo == TM and state == "MERGED" and still["n"]:
            still["n"] -= 1
            return number, "OPEN", head
        return number, state, head

    fleet.pr_of = lagging
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert fleet.sleeps == [auto.LOOKUP_WAIT_SECONDS] * 2


def test_auto_holds_when_a_landed_pr_never_reads_as_merged():
    fleet = Fleet(opens={TM: 130}, still_open={f"{TM}#130"})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "TicketManager: can't tell its re-Apply PR (#130 still reads as OPEN)" in out
    assert len(fleet.sleeps) == auto.LOOKUP_TRIES - 1
    assert reapplied(fleet) == ["TicketManager"]


def test_a_stale_read_of_the_last_rounds_merged_pr_never_releases_the_rest():
    # The newest PR by the branch's name is the last round's merged one until the list shows the new PR.
    fleet = Fleet(opens={TM: 130}, stale={TM: 2})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert fleet.landed[0] == (f"{TM}#130",), "the new PR is landed, not skipped as merged"
    fleet = Fleet(opens={TM: 130}, stale={TM: auto.LOOKUP_TRIES})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1 and fleet.landed == [] and reapplied(fleet) == ["TicketManager"]
    assert "no re-Apply PR shows head ticketm)" in out


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


def test_auto_holds_when_ticketmanager_has_no_reapply_pr():
    fleet = Fleet()
    fleet.prs[TM] = None
    pr_of = fleet.pr_of
    fleet.pr_of = lambda repo: None if repo == TM else pr_of(repo)
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "TicketManager: can't tell its re-Apply PR (no re-Apply PR found)" in out


def test_a_crash_on_ticketmanager_still_ends_with_the_resume_command():
    fleet = Fleet(raises={TM: PermissionError(13, "Permission denied")})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert "TicketManager: PermissionError: [Errno 13] Permission denied" in out and "resume with:" in out


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


def test_a_crash_on_one_of_the_rest_is_reported_and_the_round_goes_on():
    fleet = Fleet(raises={ARTICLES: OSError(8, "Exec format error")})
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 1
    assert reapplied(fleet) == ["TicketManager", *map(short, REST)]
    assert "  Articles: OSError: [Errno 8] Exec format error" in out


def test_auto_resumes_with_everything_already_merged():
    fleet = Fleet()  # every repo's last round merged, nothing new to push
    code, out, _ = run_auto(["--brings", "#82"], fleet)
    assert code == 0, out
    assert fleet.landed == [] and fleet.reviewed == [] and fleet.sleeps == []
    assert reapplied(fleet) == ["TicketManager", *map(short, REST)]


def test_auto_dry_run_checks_the_tools_prints_every_step_and_runs_nothing():
    fleet = Fleet()
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


def test_the_header_says_what_the_round_denies():
    doc = auto.__doc__
    assert re.search(r"--disallowedTools denies pushing, deciding findings, writing to\s+GitHub and editing the Factory", doc)


def test_git_branch_says_whether_the_branch_has_commits_main_lacks(tmp_path):
    def git(*args):
        return subprocess.run(["git", "-C", str(tmp_path), *args], check=True, capture_output=True, text=True,
                              env={**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"}).stdout.strip()

    git("init", "-q", "-b", "main")
    git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "one")
    git("update-ref", "refs/remotes/origin/main", "HEAD")
    assert auto.git_branch(str(tmp_path)) == (git("rev-parse", "HEAD"), False)
    git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "two")
    assert auto.git_branch(str(tmp_path)) == (git("rev-parse", "HEAD"), True)
    git("update-ref", "-d", "refs/remotes/origin/main")
    with pytest.raises(RuntimeError, match="merge-base"):
        auto.git_branch(str(tmp_path))


def test_gh_pr_reads_number_state_and_head_and_raises_on_a_gh_failure(monkeypatch):
    answers = iter([subprocess.CompletedProcess([], 0, "130 OPEN abc123\n", ""), subprocess.CompletedProcess([], 0, "", ""),
                    subprocess.CompletedProcess([], 1, "", "HTTP 502")])
    seen = []
    monkeypatch.setattr(auto.subprocess, "run", lambda command, **kwargs: seen.append(command) or next(answers))
    assert auto.gh_pr(TM) == (130, "OPEN", "abc123")
    assert seen[0][:7] == ["gh", "pr", "list", "-R", TM, "--head", auto.BRANCH]
    assert "number,state,headRefOid" in seen[0]
    assert auto.gh_pr(TM) is None
    with pytest.raises(RuntimeError, match="HTTP 502"):
        auto.gh_pr(TM)
