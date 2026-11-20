#!/usr/bin/env python3
"""Roll a re-Apply round out across the Baseline repos, TicketManager first.

    rollout.sh --brings <prs> [--root dir] [--dry-run]

The Baseline repos are github/repos.txt's, in its order, without the
ones of another scope (the Factory, "factory"); TicketManager goes first as the live check
of the Template change. Each run:

1. runs reapply.sh on TicketManager;
2. holds the rest until TicketManager's newest re-Apply PR has merged: while
   it is open, land it (land.sh prints how), fix any finding Template first,
   then run rollout.sh again. A failed reapply, a PR closed without merging, or
   none found holds the round too;
3. once that PR has merged (and reapply.sh found nothing new to push), the
   rest get reapply.sh in order, one at a time, so one pre-push gate runs at a
   time. A repo that fails is reported and the round goes on.

Each repo's checkout is <root>/<Repo> (default ~/github). A repo in repos.txt
with no checkout stops the round before anything runs: clone it, or retire it
from repos.txt. It ends with the
land.sh command that watches the re-Apply PRs it left open, and exits 1 when
any reapply failed or the round is held for anything but an open PR.
--dry-run prints the reapply.sh calls it would make from the PRs as they are
now, and makes none: the rest's calls only follow when TicketManager's reapply
opens no new PR.

--auto drives the round to the end without a person between the steps (ADR 0015):

1. it checks the tools every repo's gate and reapply.sh need, and that each
   repo's global.json names an installed .NET SDK, and stops before anything
   runs when one is missing;
2. reapply.sh on each repo, TicketManager first. When the gate asks for a
   review (reapply.sh exits 3), it runs /pre-push-review headless, a
   `claude -p` session started in the repo's re-Apply worktree so the
   worktree's own reviewer agent and ledger are the ones used, then reapply.sh
   again, at most MAX_REVIEWS times per repo;
3. it lands TicketManager's re-Apply PR with land.sh before it starts the rest,
   and the rest's PRs together once each has been reapplied.

It stops for a person only where one must decide: review findings waiting for
a person (reapply.sh exits 4), a PR that doesn't land, or a reapply or review
session that fails. TicketManager stopping holds the rest; one of the rest
stopping is reported and the round goes on. Each stop prints the command that
decides it, and the round ends with the command that resumes it: rollout.sh
--auto again, which picks up where it stopped (a repo already reapplied finds
nothing new to push, and its open PR is landed).
"""

import argparse
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass

from settings import read_repo_list

LEAD = "mpaulosky/TicketManager"
BRANCH = "chore/reapply-baseline"
SKILL_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REAPPLY = os.path.join(SKILL_DIR, "reapply.sh")
LAND = os.path.join(SKILL_DIR, "land.sh")
OWNED = os.path.join(SKILL_DIR, "template", "owned")
WORKTREE = "chore-reapply-baseline"
# reapply.sh's exit codes for a push its gate's local review check refused (a test holds them equal).
REVIEW_NEEDED = 3
FINDINGS_WAIT = 4
# Review sessions per repo per run: a second covers a session that ended before it recorded; more would loop.
MAX_REVIEWS = 2
# What --auto needs on PATH before it starts: reapply.sh and land.sh (git, gh), the gate (python3, node, pnpm, shfmt)
# and the review session (claude). dotnet is checked per repo, only where a global.json names an SDK.
TOOLS = ("git", "gh", "python3", "node", "pnpm", "shfmt", "claude")
# check_gh_repo.py's SHFMT_WANTED: v3.14 or a later v3.
SHFMT_MINOR = 14
# The headless review's instructions after the skill's name. The session runs where no one answers, in a re-Apply
# worktree whose Apply-written files are Owned: the Template-first rule (CLAUDE.md) makes each finding in one a
# person's, and posting to GitHub or pushing stays with the round.
REVIEW_PROMPT = """/pre-push-review You are running unattended for rollout.sh --auto, in {worktree}, the re-Apply worktree of {repo}.
Nobody will answer a question: never ask one or wait for a reply; where the skill says to ask the person or show them text first, don't, and say so in your last message.
A file is Owned when the same path exists under {owned}. Never edit an Owned file here: the next Apply overwrites it, and its fix belongs in the Template.
A finding in an Owned file is needsPerson: record it open, with a fix that says what to change in the Factory's template/owned/.
Fix only findings in this repository's own files, as the skill says. Don't push, don't file the follow-up issue, and don't edit a PR: write the advisory body to the run folder and name its path.
End with the output of scripts/review.sh report."""


def short_name(repo):
    return repo.split("/")[-1]


def rollout_order(listed):
    """The Baseline repos in repos.txt's order, TicketManager first."""
    repos = [repo for repo, scope in listed.items() if scope == "full"]
    if LEAD not in repos:
        raise ValueError(f"{LEAD} isn't a Baseline repo in repos.txt; it goes first as the live check")
    return tuple(sorted(repos, key=lambda repo: repo != LEAD))


@dataclass(frozen=True)
class Lead:
    """How TicketManager stands after its reapply: whether reapply.sh went
    cleanly, and its newest re-Apply PR with its state (OPEN, MERGED or CLOSED),
    or None for both when there is none or it couldn't be read."""

    reapplied: bool
    pr: int | None
    state: str | None


@dataclass(frozen=True)
class Step:
    run: tuple  # the repos to reapply now, in order
    held: tuple  # the repos that wait
    reason: str = ""


def next_step(order, lead):
    """What runs next: TicketManager alone, then (once its PR has merged) the rest."""
    first, rest = order[0], tuple(order[1:])
    if lead is None:
        return Step((first,), rest, f"{short_name(first)} goes first")
    if not lead.reapplied:
        return Step((), rest, f"reapply failed on {short_name(first)}")
    if lead.state == "OPEN":
        return Step((), rest, f"waiting for {short_name(first)}#{lead.pr} to merge")
    if lead.state == "CLOSED":
        return Step((), rest, f"{short_name(first)}#{lead.pr} was closed without merging")
    if lead.state != "MERGED":
        return Step((), rest, f"no re-Apply PR of {short_name(first)}'s found to have merged")
    return Step(rest, ())


def review_worktree(path):
    """The re-Apply worktree reapply.sh makes for the checkout at path."""
    path = os.path.normpath(path)
    return os.path.join(os.path.dirname(path), f"{os.path.basename(path)}-worktrees", WORKTREE)


def shfmt_problem(version_text):
    """Why shfmt --version's answer won't do for the gate's gh check, or None."""
    match = re.match(r"v?3\.(\d+)(?:\.\d+)?\b", version_text.strip())
    if match is None or int(match.group(1)) < SHFMT_MINOR:
        return f"shfmt is {version_text.strip() or 'unknown'}; the gate needs v3.{SHFMT_MINOR} or a later v3"
    return None


def global_json_sdk(path):
    """The SDK version path's global.json names, or None when it has none (or names none)."""
    try:
        with open(os.path.join(path, "global.json"), encoding="utf-8") as handle:
            sdk = json.load(handle).get("sdk") or {}
    except FileNotFoundError:
        return None
    except (OSError, ValueError, AttributeError) as error:
        return f"unreadable ({error})"
    return sdk.get("version") if isinstance(sdk, dict) else None


def run_quiet(command, cwd=None):
    """(exit code, stdout and stderr) of a command; 127 when it can't start."""
    try:
        done = subprocess.run(command, cwd=cwd, capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired) as error:
        return 127, str(error)
    return done.returncode, done.stdout + done.stderr


def preflight(checkouts, which=shutil.which, run=run_quiet, sdk_of=global_json_sdk):
    """What would stop the round partway through, as a list of problems: a tool missing from PATH, an shfmt the gate
    refuses, gh signed out, or a repo whose global.json names an SDK that isn't installed. checkouts maps each repo to
    its checkout."""
    problems = [f"{tool} isn't on PATH" for tool in TOOLS if which(tool) is None]
    if which("shfmt") is not None:
        code, text = run(["shfmt", "--version"])
        problem = shfmt_problem(text) if code == 0 else f"shfmt --version failed: {text.strip()}"
        if problem:
            problems.append(problem)
    if which("gh") is not None:
        code, text = run(["gh", "auth", "status"])
        if code != 0:
            problems.append("gh isn't signed in: gh auth login")
    for repo, path in checkouts.items():
        sdk = sdk_of(path)
        if sdk is None:
            continue
        if which("dotnet") is None:
            problems.append(f"{short_name(repo)}: global.json names .NET SDK {sdk}, and dotnet isn't on PATH")
            continue
        code, text = run(["dotnet", "--version"], cwd=path)
        if code != 0:
            problems.append(f"{short_name(repo)}: no installed .NET SDK matches global.json's {sdk}: dotnetup sdk install {sdk}")
    return problems


def run_review(repo, worktree):
    """/pre-push-review headless in worktree: (exit code, the session's log). The session starts in the worktree, so
    Claude Code loads that checkout's pre-push-reviewer agent and the skill runs its scripts/review.sh."""
    handle, log = tempfile.mkstemp(prefix=f"review-{short_name(repo)}.", suffix=".log")
    prompt = REVIEW_PROMPT.format(worktree=worktree, repo=repo, owned=OWNED)
    env = {name: value for name, value in os.environ.items() if name != "GATE_SKIP_REVIEW"}
    with os.fdopen(handle, "w") as out:
        try:
            code = subprocess.run(["claude", "-p", prompt, "--permission-mode", "auto", "--add-dir", OWNED],
                                  cwd=worktree, env=env, stdin=subprocess.DEVNULL, stdout=out,
                                  stderr=subprocess.STDOUT).returncode
        except OSError as error:
            out.write(f"rollout.sh: can't start claude: {error}\n")
            code = 127
    return code, log


def run_land(refs):
    """land.sh on the PRs, its output straight through; its exit code (0 once each has merged or closed)."""
    return subprocess.run([LAND, *refs]).returncode


def run_reapply(brings, path):
    """reapply.sh on one checkout, its output straight through; its exit code."""
    return subprocess.run([REAPPLY, "--brings", brings, path]).returncode


def gh_latest_pr(repo):
    """(number, state) of repo's newest re-Apply PR, or None."""
    run = subprocess.run(["gh", "pr", "list", "-R", repo, "--head", BRANCH, "--state", "all", "--limit", "1",
                          "--json", "number,state", "--jq", '.[0] // empty | "\\(.number) \\(.state)"'],
                         capture_output=True, text=True)
    if run.returncode != 0:
        raise RuntimeError(run.stderr.strip() or f"gh pr list exited {run.returncode}")
    if not run.stdout.strip():
        return None
    number, state = run.stdout.split()
    return int(number), state


@dataclass(frozen=True)
class Outcome:
    """How one repo's reapply under --auto ended: "ok" (pushed, or nothing new), "person" (a decision waits for a
    person) or "failed", with what to do next."""

    status: str
    detail: str = ""


def drive(repo, path, brings, reapply, review, say):
    """reapply.sh on one repo under --auto, with a headless review each time the gate asks for one."""
    worktree = review_worktree(path)
    reviews = 0
    while True:
        say(f"── rollout: {short_name(repo)}")
        code = reapply(brings, path)
        if code == 0:
            return Outcome("ok")
        if code == FINDINGS_WAIT:
            return Outcome("person", f"review findings wait for you: cd {worktree} && scripts/review.sh accept-open")
        if code != REVIEW_NEEDED:
            return Outcome("failed", f"reapply.sh exited {code}; retry it with: reapply.sh --brings {shlex.quote(brings)} {path}")
        if reviews == MAX_REVIEWS:
            return Outcome("failed", f"the gate still finds no review after {MAX_REVIEWS} review session(s); run /pre-push-review in {worktree}")
        reviews += 1
        say(f"── rollout: {short_name(repo)}: the gate asks for a review; running /pre-push-review headless in {worktree}")
        code, log = review(repo, worktree)
        say(f"── rollout: {short_name(repo)}: review session exited {code}; log: {log}")
        if code != 0:
            return Outcome("failed", f"the review session failed; log: {log}")


def auto_round(order, brings, checkout, reapply, review, land, lookup, say):
    """The --auto round (the docstring's --auto steps 2 and 3); its exit code."""
    resume = f"rollout.sh --auto --brings {shlex.quote(brings)}"
    first, rest = order[0], order[1:]

    def held(reason):
        say(f"rollout: {reason}; held: {', '.join(short_name(repo) for repo in rest) or 'none'}")
        say(f"  resume with: {resume}")
        return 1

    lead = drive(first, checkout(first), brings, reapply, review, say)
    if lead.status != "ok":
        return held(f"{short_name(first)}: {lead.detail}")
    number, state = lookup(first)
    if state == "OPEN":
        say(f"── rollout: landing {short_name(first)}#{number}")
        if land([f"{first}#{number}"]) != 0:
            return held(f"{short_name(first)}#{number} didn't land; see land.sh's output above")
        number, state = lookup(first)
    if state != "MERGED":
        return held(f"{short_name(first)}#{number} is {state or 'not found'}, not merged")

    stops, landing = [], []
    for repo in rest:
        outcome = drive(repo, checkout(repo), brings, reapply, review, say)
        if outcome.status != "ok":
            stops.append(f"{short_name(repo)}: {outcome.detail}")
            continue
        number, state = lookup(repo)
        if state == "OPEN":
            landing.append(f"{repo}#{number}")
    if landing:
        say(f"── rollout: landing {' '.join(landing)}")
        land(landing)
        for ref in landing:
            repo, number = ref.split("#")
            state = lookup(repo)[1]
            if state != "MERGED":
                stops.append(f"{short_name(repo)}#{number} is {state or 'not found'}, not merged: land.sh {ref}")
    say(f"rollout: {short_name(first)} and {len(rest) - len(stops)} of {len(rest)} other repo(s) done"
        + ("" if not stops else "; waiting for you:"))
    for stop in stops:
        say(f"  {stop}")
    if stops:
        say(f"  resume with: {resume}")
    return 1 if stops else 0


def main(argv=None, listed=None, reapply=run_reapply, latest_pr=gh_latest_pr, exists=os.path.isdir,
         out=sys.stdout, err=sys.stderr, review=run_review, land=run_land, check=preflight):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--brings", required=True,
                        help="the Factory PRs this round brings in, as reapply.sh takes them: '#80,#81' (quoted) or mpaulosky/Application_Factory#80,#81")
    parser.add_argument("--root", default=os.path.expanduser("~/github"),
                        help="the directory holding each repo's checkout (default ~/github)")
    parser.add_argument("--dry-run", action="store_true", help="print the reapply.sh calls instead of making them")
    parser.add_argument("--auto", action="store_true",
                        help="check the tools, then review, reapply and land each repo with no person between the steps (ADR 0015)")
    args = parser.parse_args(argv)
    try:
        order = rollout_order(read_repo_list() if listed is None else listed)
    except ValueError as error:
        parser.error(str(error))

    def say(text):
        out.write(text + "\n")
        out.flush()

    def checkout(repo):
        return os.path.join(args.root, short_name(repo))

    missing = [repo for repo in order if not exists(checkout(repo))]
    if missing:
        for repo in missing:
            err.write(f"rollout.sh: no checkout at {checkout(repo)} for {repo}\n")
        err.write("rollout.sh: nothing run; clone each one, or retire it from github/repos.txt\n")
        return 1

    def apply(repo):
        """reapply.sh on one repo (or what it would run); whether it went cleanly."""
        path = checkout(repo)
        if args.dry_run:
            say(f"would run: reapply.sh --brings {shlex.quote(args.brings)} {path}")
            return True
        say(f"── rollout: {short_name(repo)}")
        if reapply(args.brings, path) != 0:
            err.write(f"rollout.sh: reapply failed on {short_name(repo)}\n")
            return False
        return True

    def lookup(repo):
        """(number, state) of repo's newest re-Apply PR, or (None, None) when there is none or it can't be read."""
        try:
            return latest_pr(repo) or (None, None)
        except Exception as error:  # noqa: BLE001 - reported; the repo's PR is then unknown
            err.write(f"rollout.sh: can't read {repo}'s re-Apply PR: {error}\n")
            return None, None

    if args.auto:
        problems = check({repo: checkout(repo) for repo in order})
        for problem in problems:
            err.write(f"rollout.sh: {problem}\n")
        if problems:
            err.write("rollout.sh: nothing run; fix those first\n")
            return 1
        if args.dry_run:
            say("tools: all there")
            for repo in order:
                say(f"would run: reapply.sh --brings {shlex.quote(args.brings)} {checkout(repo)}, with /pre-push-review headless"
                    f" in {review_worktree(checkout(repo))} when the gate asks for it")
            say(f"and land each re-Apply PR with land.sh, {short_name(order[0])}'s before the rest start")
            return 0
        return auto_round(order, args.brings, checkout, reapply, review, land, lookup, say)

    first = order[0]
    lead = Lead(apply(first), *lookup(first))
    step = next_step(order, lead)
    if not step.run:
        say(f"rollout: {step.reason}; held: {', '.join(short_name(repo) for repo in step.held)}")
        if lead.reapplied and lead.state == "OPEN":
            say(f"  land it with: land.sh {first}#{lead.pr}, then run rollout.sh again")
            return 0
        return 1

    if args.dry_run:
        say(f"then, if that leaves {short_name(first)} with no new re-Apply PR (its Apply is already on main):")
    failed, landing = [], []
    for repo in step.run:
        if not apply(repo):
            failed.append(short_name(repo))
            continue
        if args.dry_run:
            continue
        number, state = lookup(repo)
        if state == "OPEN":
            landing.append(f"{repo}#{number}")
    if not args.dry_run:
        say(f"rollout: reapplied {len(step.run) - len(failed)} of {len(step.run)} repo(s) after {short_name(first)}"
            + (f"; failed: {', '.join(failed)}" if failed else ""))
        if landing:
            say(f"  land them with: land.sh {' '.join(landing)}")
    elif failed:
        say(f"rollout: failed: {', '.join(failed)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
