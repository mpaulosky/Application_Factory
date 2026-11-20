"""Tests for github/rollout.py: the rollout order, and the round it drives with reapply and gh faked. No GitHub."""

import io
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import rollout  # noqa: E402

LISTED = {
    "mpaulosky/TicketManager": "full",
    "mpaulosky/IssueTracker": "full",
    "mpaulosky/Blazor-Server": "full",
    "mpaulosky/IssueManager": "full",
    "mpaulosky/Articles": "full",
    "mpaulosky/SuggestionApp": "full",
    "mpaulosky/Application_Factory": "factory",
}
REST = ("mpaulosky/IssueTracker", "mpaulosky/Blazor-Server", "mpaulosky/IssueManager", "mpaulosky/Articles",
        "mpaulosky/SuggestionApp")


# ── The order ───────────────────────────────────────────────────────────────

def test_order_is_ticketmanager_then_the_rest_in_the_skills_order_without_the_factory():
    assert rollout.rollout_order(LISTED) == ("mpaulosky/TicketManager", *REST)


def test_order_leaves_out_every_scope_but_full():
    listed = {"mpaulosky/TicketManager": "full", "mpaulosky/Application_Factory": "factory", "mpaulosky/old": "repo-settings-only"}
    assert rollout.rollout_order(listed) == ("mpaulosky/TicketManager",)


def test_order_puts_ticketmanager_first_wherever_it_is_listed():
    listed = {"mpaulosky/IssueTracker": "full", "mpaulosky/TicketManager": "full", "mpaulosky/Articles": "full"}
    assert rollout.rollout_order(listed) == ("mpaulosky/TicketManager", "mpaulosky/IssueTracker", "mpaulosky/Articles")


def test_a_round_starts_with_ticketmanager_alone():
    step = rollout.next_step(rollout.rollout_order(LISTED), None)
    assert step.run == ("mpaulosky/TicketManager",)
    assert step.held == REST


def test_the_rest_are_held_while_ticketmanagers_reapply_pr_is_open():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, 129, "OPEN"))
    assert step.run == ()
    assert step.held == REST
    assert step.reason == "waiting for TicketManager#129 to merge"


def test_the_rest_are_held_when_ticketmanagers_reapply_failed():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(False, 128, "MERGED"))
    assert step.run == ()
    assert step.held == REST
    assert step.reason == "reapply failed on TicketManager"


def test_the_rest_are_held_when_ticketmanagers_reapply_pr_was_closed_unmerged():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, 129, "CLOSED"))
    assert step.run == ()
    assert step.held == REST
    assert step.reason == "TicketManager#129 was closed without merging"


def test_the_rest_are_held_when_no_reapply_pr_of_ticketmanagers_is_found():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, None, None))
    assert step.run == ()
    assert step.reason == "no re-Apply PR of TicketManager's found to have merged"


def test_the_rest_run_in_order_once_ticketmanagers_reapply_pr_has_merged():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, 129, "MERGED"))
    assert step.run == REST
    assert step.held == ()


def test_an_order_without_ticketmanager_is_refused():
    with pytest.raises(ValueError, match="TicketManager"):
        rollout.rollout_order({"mpaulosky/IssueTracker": "full"})


# ── The round ───────────────────────────────────────────────────────────────

class Fake:
    """reapply.sh and the newest re-Apply PR lookup as the tests set them."""

    def __init__(self, latest=None, fail=(), opens=None, unreadable=()):
        # {repo: (number, state)}; every repo's last round merged unless set
        self.latest = {repo: (100, "MERGED") for repo in LISTED} | dict(latest or {})
        self.fail = set(fail)  # repos whose reapply exits 1
        self.opens = opens or {}  # {repo: PR number its reapply opens}
        self.unreadable = set(unreadable)  # repos whose lookup fails
        self.ran = []

    def reapply(self, brings, path):
        self.ran.append((brings, path))
        repo = "mpaulosky/" + Path(path).name
        if repo in self.fail:
            return 1
        if repo in self.opens:
            self.latest[repo] = (self.opens[repo], "OPEN")
        return 0

    def latest_pr(self, repo):
        if repo in self.unreadable:
            raise RuntimeError("HTTP 502")
        return self.latest.get(repo)


def run(argv, fake, exists=lambda path: True):
    out, err = io.StringIO(), io.StringIO()
    code = rollout.main(argv, listed=LISTED, reapply=fake.reapply, latest_pr=fake.latest_pr, exists=exists,
                        out=out, err=err)
    return code, out.getvalue(), err.getvalue()


def ran_repos(fake):
    return [Path(path).name for _, path in fake.ran]


def test_first_run_applies_ticketmanager_only_and_holds_the_rest():
    fake = Fake(opens={"mpaulosky/TicketManager": 130})
    code, out, _ = run(["--brings", "#82", "--root", "/r"], fake)
    assert code == 0
    assert fake.ran == [("#82", "/r/TicketManager")]
    assert "waiting for TicketManager#130 to merge" in out
    assert "held: IssueTracker, Blazor-Server, IssueManager, Articles, SuggestionApp" in out
    assert "land.sh mpaulosky/TicketManager#130" in out


def test_once_ticketmanager_merged_the_rest_follow_one_at_a_time_in_order():
    fake = Fake(opens={repo: n for n, repo in enumerate(REST, start=200)})
    code, out, _ = run(["--brings", "#82", "--root", "/r"], fake)
    assert code == 0
    assert ran_repos(fake) == ["TicketManager", "IssueTracker", "Blazor-Server", "IssueManager", "Articles",
                               "SuggestionApp"]
    assert ("land.sh mpaulosky/IssueTracker#200 mpaulosky/Blazor-Server#201 mpaulosky/IssueManager#202 "
            "mpaulosky/Articles#203 mpaulosky/SuggestionApp#204") in out


def test_a_failed_repo_is_reported_and_the_rest_still_run():
    fake = Fake(fail={"mpaulosky/Blazor-Server"})
    code, out, err = run(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert ran_repos(fake)[-3:] == ["IssueManager", "Articles", "SuggestionApp"]
    assert "failed: Blazor-Server" in out
    assert "Blazor-Server" in err


def test_a_failed_ticketmanager_holds_the_round_and_exits_1():
    fake = Fake(fail={"mpaulosky/TicketManager"})
    code, out, _ = run(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "reapply failed on TicketManager" in out


def test_a_missing_checkout_stops_the_round_before_anything_runs():
    fake = Fake()
    missing = {"/r/IssueManager", "/r/Articles"}
    code, _, err = run(["--brings", "#82", "--root", "/r"], fake, exists=lambda path: path not in missing)
    assert code == 1
    assert fake.ran == []
    assert "no checkout at /r/IssueManager for mpaulosky/IssueManager" in err
    assert "no checkout at /r/Articles for mpaulosky/Articles" in err
    assert "retire it from github/repos.txt" in err


def test_a_missing_checkout_stops_a_dry_run_too():
    fake = Fake()
    code, out, err = run(["--brings", "#82", "--root", "/r", "--dry-run"], fake,
                         exists=lambda path: path != "/r/Articles")
    assert code == 1
    assert "would run" not in out
    assert "no checkout at /r/Articles" in err


def test_a_repo_settings_only_repo_needs_no_checkout():
    fake = Fake()
    code, _, err = run(["--brings", "#82", "--root", "/r", "--dry-run"], fake,
                       exists=lambda path: path != "/r/Application_Factory")
    assert code == 0
    assert "Application_Factory" not in err


def test_dry_run_runs_nothing_and_shows_the_plan_from_the_open_pr():
    fake = Fake(latest={"mpaulosky/TicketManager": (129, "OPEN")})
    code, out, _ = run(["--brings", "#82", "--root", "/r", "--dry-run"], fake)
    assert code == 0
    assert fake.ran == []
    assert "would run: reapply.sh --brings '#82' /r/TicketManager" in out
    assert "waiting for TicketManager#129 to merge" in out


def test_dry_run_with_ticketmanager_landed_shows_every_repo():
    fake = Fake()
    code, out, _ = run(["--brings", "#82", "--root", "/r", "--dry-run"], fake)
    assert code == 0
    assert fake.ran == []
    assert out.count("would run: reapply.sh") == 6
    assert "then, if that leaves TicketManager with no new re-Apply PR (its Apply is already on main):" in out
    assert out.index("/r/TicketManager") < out.index("then, if") < out.index("/r/IssueTracker")


def test_a_failed_lookup_on_ticketmanager_is_reported_as_one_and_holds_the_round():
    fake = Fake(unreadable={"mpaulosky/TicketManager"})
    code, out, err = run(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "can't read mpaulosky/TicketManager's re-Apply PR: HTTP 502" in err
    assert "reapply failed" not in out + err


def test_a_closed_unmerged_reapply_pr_holds_the_round_on_rerun():
    fake = Fake(latest={"mpaulosky/TicketManager": (129, "CLOSED")})
    code, out, _ = run(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "TicketManager#129 was closed without merging" in out


def test_a_merged_reapply_pr_on_the_rest_is_not_offered_to_land():
    fake = Fake(opens={"mpaulosky/Articles": 300})
    code, out, _ = run(["--brings", "#82", "--root", "/r"], fake)
    assert code == 0
    assert "land them with: land.sh mpaulosky/Articles#300\n" in out


# ── The branch name ─────────────────────────────────────────────────────────


def test_every_copy_of_the_reapply_branch_name_agrees():
    # rollout.py finds the round's PRs by reapply.sh's branch, so a rename has
    # to reach both copies.
    skill = Path(__file__).resolve().parents[2]
    script = (skill / "reapply.sh").read_text()
    assert f'branch="{rollout.BRANCH}"' in script


def test_the_reapply_worktree_and_review_exit_codes_agree_with_reapply_sh():
    # --auto finds the worktree to review in and reads reapply.sh's exit codes, so both sides must change together.
    skill = Path(__file__).resolve().parents[2]
    script = (skill / "reapply.sh").read_text()
    assert f'worktree_name="{rollout.WORKTREE}"' in script
    assert f"review_needed_exit={rollout.REVIEW_NEEDED}" in script
    assert f"findings_wait_exit={rollout.FINDINGS_WAIT}" in script


def test_reapply_sh_matches_the_gates_review_check_in_the_ledgers_own_words():
    # reapply.sh tells the two review refusals apart by the text checkReview prints; a reworded message there
    # would turn both into a plain failed push.
    skill = Path(__file__).resolve().parents[2]
    script = (skill / "reapply.sh").read_text()
    ledger = (skill / "template/owned/.sandcastle/lib/review-ledger.mts").read_text()
    check = ledger[ledger.index("export function checkReview("):]
    check = check[:check.index("\n}\n")]
    for name in ("no_review_text", "findings_wait_text"):
        text = next(line.split('"', 2)[1] for line in script.splitlines() if line.startswith(f"{name}="))
        assert text in check, f"{name} ({text!r}) isn't in checkReview's messages"


# ── --auto ──────────────────────────────────────────────────────────────────

def test_review_worktree_is_beside_the_checkout():
    assert rollout.review_worktree("/r/TicketManager") == "/r/TicketManager-worktrees/chore-reapply-baseline"
    assert rollout.review_worktree("/r/TicketManager/") == "/r/TicketManager-worktrees/chore-reapply-baseline"


@pytest.mark.parametrize("text, ok", [
    ("v3.14.0", True), ("v3.14.1\n", True), ("v3.15.0", True), ("3.14.0", True),
    ("v3.13.9", False), ("v4.0.0", False), ("v2.20.0", False), ("", False), ("(devel)", False),
])
def test_shfmt_needs_v3_14_or_a_later_v3(text, ok):
    assert (rollout.shfmt_problem(text) is None) == ok


def test_global_json_sdk_reads_the_version(tmp_path):
    assert rollout.global_json_sdk(str(tmp_path)) is None
    (tmp_path / "global.json").write_text('{"sdk": {"version": "10.0.403", "rollForward": "latestMinor"}}')
    assert rollout.global_json_sdk(str(tmp_path)) == "10.0.403"
    (tmp_path / "global.json").write_text('{"test": {"runner": "x"}}')
    assert rollout.global_json_sdk(str(tmp_path)) is None
    (tmp_path / "global.json").write_text("{not json")
    assert rollout.global_json_sdk(str(tmp_path)).startswith("unreadable")


def preflight(on_path=rollout.TOOLS + ("dotnet",), answers=None, sdks=None):
    answers = {"shfmt": (0, "v3.14.1\n"), "gh": (0, ""), "dotnet": (0, "10.0.404\n")} | dict(answers or {})
    calls = []

    def run(command, cwd=None):
        calls.append((tuple(command), cwd))
        return answers[command[0]] if not isinstance(answers[command[0]], dict) else answers[command[0]][cwd]

    sdks = sdks or {}
    problems = rollout.preflight({"mpaulosky/TicketManager": "/r/TicketManager", "mpaulosky/Articles": "/r/Articles"},
                                 which=lambda tool: f"/bin/{tool}" if tool in on_path else None, run=run,
                                 sdk_of=lambda path: sdks.get(path))
    return problems, calls


def test_preflight_passes_with_every_tool_and_sdk():
    problems, calls = preflight(sdks={"/r/TicketManager": "10.0.403"})
    assert problems == []
    assert (("dotnet", "--version"), "/r/TicketManager") in calls
    assert not any(cwd == "/r/Articles" for _, cwd in calls), "a repo without global.json needs no dotnet"


@pytest.mark.parametrize("tool", rollout.TOOLS)
def test_preflight_names_each_missing_tool(tool):
    problems, _ = preflight(on_path=tuple(t for t in rollout.TOOLS if t != tool))
    assert f"{tool} isn't on PATH" in problems


def test_preflight_refuses_an_old_shfmt_and_a_signed_out_gh():
    problems, _ = preflight(answers={"shfmt": (0, "v3.13.0"), "gh": (1, "not logged in")})
    assert any("shfmt is v3.13.0" in problem for problem in problems)
    assert "gh isn't signed in: gh auth login" in problems


def test_preflight_names_the_sdk_a_repo_needs_and_how_to_install_it():
    problems, _ = preflight(answers={"dotnet": {"/r/TicketManager": (155, "A compatible .NET SDK was not found.")}},
                            sdks={"/r/TicketManager": "10.0.403"})
    assert problems == ["TicketManager: no installed .NET SDK matches global.json's 10.0.403: dotnetup sdk install 10.0.403"]


def test_preflight_wants_dotnet_only_where_a_global_json_names_an_sdk():
    assert preflight(on_path=rollout.TOOLS)[0] == []
    problems, _ = preflight(on_path=rollout.TOOLS, sdks={"/r/Articles": "10.0.100"})
    assert problems == ["Articles: global.json names .NET SDK 10.0.100, and dotnet isn't on PATH"]


class AutoFake(Fake):
    """--auto's reapply, review, land and preflight. codes: {repo: [reapply exit codes, in order]} (0 once they run
    out); opens: the PR a 0 opens; reviews: {repo: review exit code}; unlanded: PRs land.sh leaves open."""

    def __init__(self, codes=None, reviews=None, unlanded=(), problems=(), **kwargs):
        super().__init__(**kwargs)
        self.codes = {repo: list(seq) for repo, seq in (codes or {}).items()}
        self.review_codes = dict(reviews or {})
        self.unlanded = set(unlanded)
        self.problems = list(problems)
        self.reviewed, self.landed, self.checked = [], [], []

    def reapply(self, brings, path):
        repo = "mpaulosky/" + Path(path).name
        seq = self.codes.get(repo)
        if seq:
            self.ran.append((brings, path))
            return seq.pop(0)
        return super().reapply(brings, path)

    def review(self, repo, worktree):
        self.reviewed.append((repo, worktree))
        return self.review_codes.get(repo, 0), f"/tmp/review-{rollout.short_name(repo)}.log"

    def land(self, refs):
        self.landed.append(tuple(refs))
        for ref in refs:
            repo, number = ref.split("#")
            if ref not in self.unlanded:
                self.latest[repo] = (int(number), "MERGED")
        return 1 if self.unlanded & set(refs) else 0

    def check(self, checkouts):
        self.checked.append(dict(checkouts))
        return self.problems


def run_auto(argv, fake):
    out, err = io.StringIO(), io.StringIO()
    code = rollout.main(["--auto", *argv], listed=LISTED, reapply=fake.reapply, latest_pr=fake.latest_pr,
                        exists=lambda path: True, out=out, err=err, review=fake.review, land=fake.land, check=fake.check)
    return code, out.getvalue(), err.getvalue()


TM = "mpaulosky/TicketManager"


def test_auto_reviews_reapplies_and_lands_ticketmanager_then_the_rest():
    fake = AutoFake(codes={TM: [rollout.REVIEW_NEEDED]}, opens={TM: 130, **{repo: n for n, repo in enumerate(REST, start=200)}})
    code, out, _ = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 0, out
    assert fake.reviewed == [(TM, "/r/TicketManager-worktrees/chore-reapply-baseline")]
    assert ran_repos(fake) == ["TicketManager", "TicketManager", *(Path(repo).name for repo in REST)]
    assert fake.landed[0] == (f"{TM}#130",)
    assert fake.landed[1] == tuple(f"{repo}#{n}" for n, repo in enumerate(REST, start=200))
    assert "TicketManager and 5 of 5 other repo(s) done" in out


def test_auto_lands_ticketmanager_before_any_other_repo_is_reapplied():
    order = []
    fake = AutoFake(opens={TM: 130, REST[0]: 200})
    reapply, land = fake.reapply, fake.land
    fake.reapply = lambda brings, path: order.append(("reapply", Path(path).name)) or reapply(brings, path)
    fake.land = lambda refs: order.append(("land", refs[0])) or land(refs)
    run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert order[:3] == [("reapply", "TicketManager"), ("land", f"{TM}#130"), ("reapply", "IssueTracker")]


def test_auto_runs_the_preflight_on_every_checkout_and_stops_on_a_problem():
    fake = AutoFake(problems=["shfmt isn't on PATH"])
    code, out, err = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert fake.ran == [] and fake.reviewed == [] and fake.landed == []
    assert "rollout.sh: shfmt isn't on PATH" in err
    assert set(fake.checked[0]) == {TM, *REST}
    assert fake.checked[0][TM] == "/r/TicketManager"


def test_auto_stops_for_a_person_when_findings_wait_and_holds_the_rest():
    fake = AutoFake(codes={TM: [rollout.REVIEW_NEEDED, rollout.FINDINGS_WAIT]})
    code, out, _ = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager", "TicketManager"]
    assert "cd /r/TicketManager-worktrees/chore-reapply-baseline && scripts/review.sh accept-open" in out
    assert "held: IssueTracker, Blazor-Server, IssueManager, Articles, SuggestionApp" in out
    assert "resume with: rollout.sh --auto --brings '#82'" in out
    assert fake.landed == []


def test_auto_gives_up_after_max_reviews_when_the_gate_still_finds_none():
    fake = AutoFake(codes={TM: [rollout.REVIEW_NEEDED] * (rollout.MAX_REVIEWS + 1)})
    code, out, _ = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert len(fake.reviewed) == rollout.MAX_REVIEWS
    assert len(fake.ran) == rollout.MAX_REVIEWS + 1
    assert f"still finds no review after {rollout.MAX_REVIEWS} review session(s)" in out


def test_auto_reviews_a_second_time_when_the_first_session_left_no_record():
    fake = AutoFake(codes={TM: [rollout.REVIEW_NEEDED] * rollout.MAX_REVIEWS}, opens={TM: 130})
    code, out, _ = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 0, out
    assert len(fake.reviewed) == rollout.MAX_REVIEWS


def test_auto_stops_when_a_review_session_fails():
    fake = AutoFake(codes={TM: [rollout.REVIEW_NEEDED]}, reviews={TM: 1})
    code, out, _ = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert len(fake.ran) == 1
    assert "the review session failed; log: /tmp/review-TicketManager.log" in out


def test_auto_holds_the_rest_when_ticketmanagers_pr_does_not_land():
    fake = AutoFake(opens={TM: 130}, unlanded={f"{TM}#130"})
    code, out, _ = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "TicketManager#130 didn't land" in out


def test_auto_holds_the_rest_when_ticketmanagers_reapply_fails():
    fake = AutoFake(fail={TM})
    code, out, _ = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "reapply.sh exited 1" in out


def test_auto_reports_a_stopped_repo_among_the_rest_and_goes_on():
    blazor, articles = REST[1], REST[3]
    fake = AutoFake(codes={blazor: [rollout.FINDINGS_WAIT]}, fail={articles}, opens={repo: 300 for repo in REST},
                    unlanded={f"{REST[0]}#300"})
    code, out, _ = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager", *(Path(repo).name for repo in REST)]
    assert f"{blazor}#300" not in fake.landed[0] and f"{REST[2]}#300" in fake.landed[0]
    assert "Blazor-Server: review findings wait for you" in out
    assert "Articles: reapply.sh exited 1" in out
    assert "IssueTracker#300 is OPEN, not merged: land.sh mpaulosky/IssueTracker#300" in out
    assert "TicketManager and 2 of 5 other repo(s) done" in out


def test_auto_resumes_with_ticketmanager_already_merged():
    fake = AutoFake()  # every repo's last round merged, nothing new to push
    code, out, _ = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 0
    assert fake.landed == [] and fake.reviewed == []
    assert ran_repos(fake) == ["TicketManager", *(Path(repo).name for repo in REST)]


def test_auto_holds_the_rest_when_ticketmanagers_pr_was_closed():
    fake = AutoFake(latest={TM: (129, "CLOSED")})
    code, out, _ = run_auto(["--brings", "#82", "--root", "/r"], fake)
    assert code == 1
    assert "TicketManager#129 is CLOSED, not merged" in out


def test_auto_dry_run_checks_the_tools_and_runs_nothing():
    fake = AutoFake()
    code, out, _ = run_auto(["--dry-run", "--brings", "#82", "--root", "/r"], fake)
    assert code == 0
    assert fake.checked and fake.ran == [] and fake.reviewed == [] and fake.landed == []
    assert "/r/TicketManager-worktrees/chore-reapply-baseline" in out


def test_the_review_session_starts_in_the_worktree_unattended_without_gate_skip_review(monkeypatch, tmp_path):
    seen = {}

    def fake_run(command, **kwargs):
        seen.update(command=command, **kwargs)
        return type("Done", (), {"returncode": 0})()

    monkeypatch.setenv("GATE_SKIP_REVIEW", "1")
    monkeypatch.setenv("TMPDIR", str(tmp_path))
    monkeypatch.setattr(rollout.subprocess, "run", fake_run)
    tempfile_dir = rollout.tempfile.tempdir
    rollout.tempfile.tempdir = None
    try:
        code, log = rollout.run_review(TM, "/r/TicketManager-worktrees/chore-reapply-baseline")
    finally:
        rollout.tempfile.tempdir = tempfile_dir
    assert code == 0 and log.startswith(str(tmp_path)) and "review-TicketManager." in log
    assert seen["cwd"] == "/r/TicketManager-worktrees/chore-reapply-baseline"
    assert seen["command"][:2] == ["claude", "-p"]
    assert seen["command"][2].startswith("/pre-push-review ")
    assert "--permission-mode" in seen["command"] and seen["command"][seen["command"].index("--permission-mode") + 1] == "auto"
    assert "GATE_SKIP_REVIEW" not in seen["env"]
    prompt = seen["command"][2]
    assert rollout.OWNED in prompt and "Never edit an Owned file" in prompt and "Don't push" in prompt
