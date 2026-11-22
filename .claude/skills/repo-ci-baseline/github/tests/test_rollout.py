"""Tests for github/rollout.py: the rollout order, and the round it drives with reapply and gh faked. No GitHub."""

import io
import os
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import auto_rollout  # noqa: E402
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


def test_the_rest_run_when_ticketmanager_pushed_nothing_and_has_no_reapply_pr():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, None, None, ahead=False))
    assert step.run == REST
    assert step.held == ()


def test_the_rest_run_when_ticketmanager_pushed_nothing_and_its_last_pr_was_closed():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, 129, "CLOSED", ahead=False))
    assert step.run == REST


def test_the_rest_are_held_when_ticketmanager_pushed_commits_no_pr_shows():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, None, None, ahead=True))
    assert step.run == ()
    assert step.reason == "no re-Apply PR of TicketManager's found to have merged"
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, 129, "CLOSED", ahead=True))
    assert step.reason == "TicketManager#129 was closed without merging"


def test_a_failed_reapply_holds_the_rest_even_with_nothing_pushed():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(False, None, None, ahead=False))
    assert step.run == ()
    assert step.reason == "reapply failed on TicketManager"


def test_nothing_pushed_doesnt_let_the_rest_past_an_open_pr():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, 129, "OPEN", ahead=False))
    assert step.run == ()
    assert step.reason == "waiting for TicketManager#129 to merge"


def test_a_merged_pr_with_nothing_pushed_lets_the_rest_run_as_before():
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, 129, "MERGED", ahead=False))
    assert step.run == REST
    assert step.reason == ""


def test_a_merged_pr_while_the_worktree_has_new_commits_is_an_earlier_rounds_and_holds():
    # reapply.sh pushed and opened a new PR, but GitHub's list still shows the last round's merged one.
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, 129, "MERGED", ahead=True))
    assert step.run == ()
    assert step.held == REST
    assert step.reason == ("TicketManager's re-Apply worktree has commits main lacks, but its newest PR shown, #129, has"
                           " merged: GitHub's list may not show the new one yet; run rollout.sh again")


@pytest.mark.parametrize("state", [None, "CLOSED", "MERGED"])
def test_an_unreadable_branch_holds_the_rest_whatever_the_pr(state):
    step = rollout.next_step(rollout.rollout_order(LISTED), rollout.Lead(True, 129, state, unreadable=True))
    assert step.run == ()
    assert step.reason == "can't tell whether TicketManager has anything to land: its re-Apply branch can't be read"


def test_a_ticketmanager_already_current_lets_the_rest_go_on_in_one_run():
    fake = Fake(latest={"mpaulosky/TicketManager": None})
    asked = []
    code, out, _ = run(["--brings", "#82", "--root", "/r"], fake, ahead=False, asked=asked)
    assert code == 0
    assert ran_repos(fake) == [Path(repo).name for repo in rollout.rollout_order(LISTED)]
    assert "TicketManager already matches the Template: nothing to land, so the rest go on" in out
    assert asked == ["TicketManager"]


def test_a_ticketmanager_with_commits_and_no_pr_still_holds_the_rest():
    fake = Fake(latest={"mpaulosky/TicketManager": None})
    code, out, _ = run(["--brings", "#82", "--root", "/r"], fake, ahead=True)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "held:" in out


def test_an_unreadable_branch_or_pr_holds_the_rest_and_says_why():
    fake = Fake(latest={"mpaulosky/TicketManager": None})
    code, out, err = run(["--brings", "#82", "--root", "/r"], fake, ahead=RuntimeError("not a git repository"))
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "can't read TicketManager's re-Apply branch: not a git repository" in err
    assert ("rollout: can't tell whether TicketManager has anything to land: its re-Apply branch can't be read; held:"
            in out)
    fake, asked = Fake(unreadable={"mpaulosky/TicketManager"}), []
    code, _, _ = run(["--brings", "#82", "--root", "/r"], fake, ahead=False, asked=asked)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert asked == []  # the PR is unknown, so nothing pushed doesn't decide it


def test_a_failed_reapply_reads_no_branch():
    fake, asked = Fake(latest={"mpaulosky/TicketManager": None}, fail={"mpaulosky/TicketManager"}), []
    code, out, err = run(["--brings", "#82", "--root", "/r"], fake, ahead=RuntimeError("no worktree"), asked=asked)
    assert code == 1
    assert asked == []
    assert "can't read" not in out + err
    assert "reapply failed on TicketManager" in out


def test_a_dry_run_reads_no_branch_and_holds_as_before():
    # A dry run reapplies nothing, so the worktree says nothing of what the reapply would push.
    fake, asked = Fake(latest={"mpaulosky/TicketManager": None}), []
    code, out, _ = run(["--brings", "#82", "--root", "/r", "--dry-run"], fake, ahead=False, asked=asked)
    assert code == 1
    assert asked == []
    assert "already matches" not in out
    assert "held:" in out


def test_the_branch_is_read_unless_the_pr_is_open():
    asked = []
    run(["--brings", "#82", "--root", "/r"], Fake(opens={"mpaulosky/TicketManager": 130}), ahead=False, asked=asked)
    assert asked == []
    run(["--brings", "#82", "--root", "/r"], Fake(), ahead=False, asked=asked)
    assert asked == ["TicketManager"]


def test_a_merged_pr_shown_while_the_worktree_has_new_commits_holds_the_round():
    fake = Fake()  # the list still shows TicketManager's last round, merged
    code, out, _ = run(["--brings", "#82", "--root", "/r"], fake, ahead=True)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "GitHub's list may not show the new one yet; run rollout.sh again; held: IssueTracker" in out


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

    def __init__(self, latest=None, fail=(), opens=None, unreadable=(), code=1):
        # {repo: (number, state)}; every repo's last round merged unless set
        self.latest = {repo: (100, "MERGED") for repo in LISTED} | dict(latest or {})
        self.fail, self.code = set(fail), code  # repos whose reapply exits code
        self.opens = opens or {}  # {repo: PR number its reapply opens}
        self.unreadable = set(unreadable)  # repos whose lookup fails
        self.ran = []

    def reapply(self, brings, path):
        self.ran.append((brings, path))
        repo = "mpaulosky/" + Path(path).name
        if repo in self.fail:
            return self.code
        if repo in self.opens:
            self.latest[repo] = (self.opens[repo], "OPEN")
        return 0

    def latest_pr(self, repo):
        if repo in self.unreadable:
            raise RuntimeError("HTTP 502")
        return self.latest.get(repo)


def run(argv, fake, exists=lambda path: True, ahead=None, asked=None, real_branch=False):
    """rollout.main with fake's reapply and PR lookup. ahead: what the branch read answers (or raises); the checkout
    paths it is asked for go into asked. real_branch: the real read, git on the re-Apply worktree, instead."""
    out, err = io.StringIO(), io.StringIO()

    def ahead_of(path):
        if asked is not None:
            asked.append(Path(path).name)
        if isinstance(ahead, Exception):
            raise ahead
        return ahead

    code = rollout.main(argv, listed=LISTED, reapply=fake.reapply, latest_pr=fake.latest_pr, exists=exists,
                        out=out, err=err, ahead=None if real_branch else ahead_of)
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


@pytest.mark.parametrize("code, said", [
    (3, "reapply failed on TicketManager: the gate asks for a review: run /pre-push-review in its re-Apply worktree"
        " (or rollout.sh --auto), then run this again"),
    (4, "reapply failed on TicketManager: review findings wait for you: fix them with /pre-push-review in its re-Apply"
        " worktree, or accept them with scripts/review.sh accept-open there, then run this again"),
    (1, "reapply failed on TicketManager\n")])
def test_a_review_refusal_says_what_reapply_asks_of_the_person(code, said):
    code_, out, err = run(["--brings", "#82", "--root", "/r"], Fake(fail={"mpaulosky/TicketManager"}, code=code))
    assert code_ == 1 and said in out + err


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


def test_a_dry_runs_commands_quote_the_checkout_so_they_run_as_printed():
    code, out, _ = run(["--brings", "#82", "--root", "/my repos", "--dry-run"], Fake())
    assert code == 0
    assert "would run: reapply.sh --brings '#82' '/my repos/TicketManager'\n" in out
    assert "/my repos/TicketManager\n" not in out.replace("'/my repos/TicketManager'\n", "")


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


# ── The real branch read and PR lookup ──────────────────────────────────────

GIT_ENV = {"GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1", "GIT_AUTHOR_NAME": "t",
           "GIT_AUTHOR_EMAIL": "t@t", "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t"}


@pytest.fixture
def ticketmanager(tmp_path, monkeypatch):
    """A real TicketManager checkout under tmp_path with the re-Apply worktree reapply.sh makes beside it, level with
    origin/main; a git that runs in it."""
    for name in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_PREFIX"):
        monkeypatch.delenv(name, raising=False)
    for name, value in GIT_ENV.items():
        monkeypatch.setenv(name, value)
    primary = tmp_path / "TicketManager"
    worktree = tmp_path / "TicketManager-worktrees" / "chore-reapply-baseline"

    def git(where, *args):
        return subprocess.run(["git", "-C", str(where), *args], check=True, capture_output=True, text=True).stdout

    primary.mkdir()
    git(primary, "init", "-q", "-b", "main")
    git(primary, "commit", "-q", "--allow-empty", "-m", "one")
    git(primary, "update-ref", "refs/remotes/origin/main", "HEAD")
    git(primary, "worktree", "add", "-q", "--no-track", "-b", rollout.BRANCH, str(worktree), "origin/main")
    return tmp_path, worktree, git


def test_the_real_branch_read_lets_the_rest_go_on_when_the_worktree_is_level_with_main(ticketmanager):
    root, _, _ = ticketmanager
    fake = Fake(latest={"mpaulosky/TicketManager": None})
    code, out, err = run(["--brings", "#82", "--root", str(root)], fake, real_branch=True)
    assert (code, err) == (0, "")
    assert "TicketManager already matches the Template: nothing to land, so the rest go on" in out
    assert ran_repos(fake) == [Path(repo).name for repo in rollout.rollout_order(LISTED)]


def test_the_real_branch_read_holds_the_rest_when_the_worktree_has_commits_main_lacks(ticketmanager):
    root, worktree, git = ticketmanager
    git(worktree, "commit", "-q", "--allow-empty", "-m", "re-Apply")
    fake = Fake(latest={"mpaulosky/TicketManager": None})
    code, out, _ = run(["--brings", "#82", "--root", str(root)], fake, real_branch=True)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "no re-Apply PR of TicketManager's found to have merged; held:" in out
    fake = Fake()  # the last round's merged PR, shown while the list lags this push
    code, out, _ = run(["--brings", "#82", "--root", str(root)], fake, real_branch=True)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "GitHub's list may not show the new one yet" in out


def test_the_real_branch_read_holds_the_rest_and_says_why_when_git_cant_answer(ticketmanager):
    root, _, git = ticketmanager
    git(root / "TicketManager", "update-ref", "-d", "refs/remotes/origin/main")
    fake = Fake(latest={"mpaulosky/TicketManager": None})
    code, out, err = run(["--brings", "#82", "--root", str(root)], fake, real_branch=True)
    assert code == 1
    assert ran_repos(fake) == ["TicketManager"]
    assert "can't read TicketManager's re-Apply branch: git merge-base in" in err
    assert "can't tell whether TicketManager has anything to land" in out


def test_the_plain_modes_lookup_is_autos_without_the_head(monkeypatch):
    answers = iter([(130, "OPEN", "abc123"), None])
    seen = []
    monkeypatch.setattr(auto_rollout, "gh_pr", lambda repo: seen.append(repo) or next(answers))
    assert rollout.gh_latest_pr("mpaulosky/TicketManager") == (130, "OPEN")
    assert rollout.gh_latest_pr("mpaulosky/TicketManager") is None
    assert seen == ["mpaulosky/TicketManager"] * 2
