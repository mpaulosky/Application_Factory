#!/usr/bin/env python3
"""Roll a re-Apply round out across the Baseline repos, TicketManager first.

    rollout.sh --brings <prs> [--root dir] [--dry-run] [--auto]

The Baseline repos are github/repos.txt's, in its order, without the
ones of another scope (the Factory, "factory"); TicketManager goes first as the live check
of the Template change. Each run:

1. runs reapply.sh on TicketManager;
2. holds the rest until TicketManager's newest re-Apply PR has merged: while
   it is open, land it (land.sh prints how), fix any finding Template first,
   then run rollout.sh again. A failed reapply, a PR closed without merging, or
   none found holds the round too; a reapply stopped at the gate's review (exit
   3) or on findings that wait for a person (exit 4) says what to run first;
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

--auto runs the whole round in one run, with no person between the steps:
github/auto_rollout.py, whose docstring says how (ADR 0016). With --dry-run it
checks the tools and prints its plan, and runs nothing.
"""

import argparse
import os
import shlex
import subprocess
import sys
from dataclasses import dataclass

from settings import read_repo_list

LEAD = "mpaulosky/TicketManager"
BRANCH = "chore/reapply-baseline"
REAPPLY = os.path.join(os.path.dirname(os.path.dirname(os.path.realpath(__file__))), "reapply.sh")
DEFAULT_ROOT = os.path.expanduser("~/github")
# What reapply.sh's review refusals ask of the person (its exit 3 and 4, auto_rollout's REVIEW_NEEDED and
# FINDINGS_WAIT: a test holds them equal), said after "reapply failed on <repo>".
REAPPLY_STOPS = {3: ": the gate asks for a review: run /pre-push-review in its re-Apply worktree (or rollout.sh --auto), then"
                    " run this again",
                 4: ": review findings wait for you: fix them with /pre-push-review in its re-Apply worktree, or accept"
                    " them with scripts/review.sh accept-open there, then run this again"}


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


def main(argv=None, listed=None, reapply=run_reapply, latest_pr=gh_latest_pr, exists=os.path.isdir,
         out=sys.stdout, err=sys.stderr, auto=None):
    """auto: keyword arguments for auto_rollout.run (its outside world), for the tests."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--brings", required=True,
                        help="the Factory PRs this round brings in, as reapply.sh takes them: '#80,#81' (quoted) or mpaulosky/Application_Factory#80,#81")
    parser.add_argument("--root", default=DEFAULT_ROOT,
                        help="the directory holding each repo's checkout (default ~/github)")
    parser.add_argument("--dry-run", action="store_true",
                        help="print the reapply.sh calls instead of making them (with --auto: check the tools, print the plan)")
    parser.add_argument("--auto", action="store_true",
                        help="check the tools, then reapply, review and land each repo with no person between the steps (ADR 0016)")
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
            say(f"would run: reapply.sh --brings {shlex.quote(args.brings)} {shlex.quote(path)}")
            return True
        say(f"── rollout: {short_name(repo)}")
        code = reapply(args.brings, path)
        if code != 0:
            err.write(f"rollout.sh: reapply failed on {short_name(repo)}{REAPPLY_STOPS.get(code, '')}\n")
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
        import auto_rollout  # noqa: PLC0415 - only --auto needs it
        # Under --auto, reapply.sh runs in a process group of its own, which a SIGTERM or SIGHUP kills whole.
        reapply = auto_rollout.run_reapply if reapply is run_reapply else reapply
        return auto_rollout.run(order, args.brings, args.root, checkout, args.dry_run, reapply=reapply, say=say,
                                err=err, **({"default_root": DEFAULT_ROOT} | (auto or {})))

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
