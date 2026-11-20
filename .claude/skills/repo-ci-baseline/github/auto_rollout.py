"""rollout.sh --auto: a re-Apply round driven to the end with no person between the steps (ADR 0015).

    rollout.sh --auto --brings <prs> [--root dir] [--dry-run]

1. It checks what would stop the round partway through: a tool missing from
   PATH, an shfmt the gate's gh check refuses, gh signed out, and the .NET SDK
   the Template's global.json names (an Owned file, so Apply writes it into
   every re-Apply worktree and the gate builds with it). One problem stops the
   round before anything runs. A second --auto run while one holds the lock
   stops too, since both would work the same worktrees and review ledgers.
2. reapply.sh on each repo, TicketManager first. When the gate asks for a
   review (reapply.sh exits 3), it runs /pre-push-review headless: `claude -p`
   started in the repo's re-Apply worktree, so Claude Code loads that
   checkout's pre-push-reviewer agent and the skill runs that checkout's
   scripts/review.sh. Then reapply.sh again, with at most MAX_REVIEWS sessions
   per repo, each one at most REVIEW_TIMEOUT_MINUTES long.
3. It lands TicketManager's re-Apply PR with land.sh before it starts the rest,
   and the rest's PRs together once each has been reapplied.

The session runs unattended with the owner's sign-ins, reading text people
without write access may have written, so the skill's Rules don't rest on the
prompt alone: --disallowedTools denies pushing, deciding findings, writing to
GitHub and editing the Factory or ~/.claude, and the auto permission mode's
classifier stands behind that. It gets no access to the Template: the prompt
names the Owned files the branch changes instead.

A PR is read for the head the worktree has, never only by the branch's name:
the name is the same every round, so the newest PR by name may be the last
round's. A read that doesn't show the head is tried again LOOKUP_TRIES times.

It stops for a person only where one must decide: review findings that wait for
a person (reapply.sh exits 4), a PR that doesn't land, a reapply or review
session that fails, a PR it can't read. TicketManager stopping holds the rest;
one of the rest stopping is reported and the round goes on. Each stop prints
the command that decides it, and the round ends with the command that resumes
it: rollout.sh --auto again, which picks up where it stopped (a repo already
reapplied finds nothing new to push, and its open PR is landed).
"""

import fcntl
import os
import re
import shlex
import shutil
import subprocess
import tempfile
import time
from dataclasses import dataclass

SKILL_DIR = os.path.dirname(os.path.dirname(os.path.realpath(__file__)))
FACTORY = os.path.dirname(os.path.dirname(os.path.dirname(SKILL_DIR)))
LAND = os.path.join(SKILL_DIR, "land.sh")
OWNED = os.path.join(SKILL_DIR, "template", "owned")
TEMPLATE_GLOBAL_JSON = os.path.join(OWNED, "global.json")
LOCK = os.path.expanduser("~/.cache/repo-ci-baseline/rollout-auto.lock")
BRANCH = "chore/reapply-baseline"
WORKTREE = "chore-reapply-baseline"
# reapply.sh's exit codes for a push its gate's local review check refused (a test holds them equal).
REVIEW_NEEDED = 3
FINDINGS_WAIT = 4
# Review sessions per repo per run: a second covers a session that ended before it recorded; more would loop.
MAX_REVIEWS = 2
# A review session's wall clock: a large re-Apply takes several rounds of parallel reviewers and fixes.
REVIEW_TIMEOUT_MINUTES = 180
# Reads of a PR that doesn't yet show the worktree's head, and the seconds between them.
LOOKUP_TRIES = 5
LOOKUP_WAIT_SECONDS = 10
# What --auto needs on PATH: reapply.sh and land.sh (git, gh), the gate (python3, node, pnpm, shfmt) and the review
# session (claude). dotnet is needed only when the Template carries a global.json.
TOOLS = ("git", "gh", "python3", "node", "pnpm", "shfmt", "claude")
# check_gh_repo.py's SHFMT_RELEASE and SHFMT_OLDEST_MINOR (a test holds them equal): a v3 release from v3.14 on.
SHFMT_RELEASE = re.compile(r"v3\.(0|[1-9][0-9]*)\.[0-9]+")
SHFMT_OLDEST_MINOR = 14
# What the session may never do, whatever text it reads: the skill's Rules, and the Template-first rule (the Factory's
# CLAUDE.md). Deny rules hold in every permission mode; the auto mode's classifier covers what a prefix can't name.
DENIED_TOOLS = (
    "Bash(git push:*)",
    "Bash(scripts/review.sh accept:*)",
    "Bash(scripts/review.sh accept-open:*)",
    "Bash(gh pr create:*)", "Bash(gh pr edit:*)", "Bash(gh pr comment:*)", "Bash(gh pr review:*)",
    "Bash(gh pr merge:*)", "Bash(gh pr ready:*)", "Bash(gh pr close:*)",
    "Bash(gh issue create:*)", "Bash(gh issue comment:*)", "Bash(gh issue edit:*)", "Bash(gh issue close:*)",
    "Bash(gh label:*)", "Bash(gh release:*)", "Bash(gh repo:*)", "Bash(gh secret:*)", "Bash(gh workflow:*)",
    f"Edit(/{FACTORY}/**)", f"Write(/{FACTORY}/**)", "Edit(~/.claude/**)", "Write(~/.claude/**)",
)
# The headless review's instructions after the skill's name. Nobody answers, the Owned files' fixes belong in the
# Template, and pushing, posting and deciding stay with the round and the person.
REVIEW_PROMPT = """/pre-push-review You are running unattended for rollout.sh --auto, in {worktree}, the re-Apply worktree of {repo}.
Nobody will answer a question: never ask one or wait for a reply; where the skill says to ask the person or show them text first, don't, and say so in your last message.
These files the branch changes are Owned Baseline files, which the next Apply overwrites: {owned}.
Never edit an Owned file here. A finding in one is needsPerson: record it open, with a fix that says what to change in the Factory's template/owned/.
Fix only findings in this repository's own files, as the skill says. Don't push, don't file the follow-up issue, and don't edit a PR: write the advisory body to the run folder and name its path.
End with the output of scripts/review.sh report."""


def short_name(repo):
    return repo.split("/")[-1]


def review_worktree(path):
    """The re-Apply worktree reapply.sh makes for the checkout at path, as git names it (symlinks resolved)."""
    path = os.path.realpath(path)
    return os.path.join(os.path.dirname(path), f"{os.path.basename(path)}-worktrees", WORKTREE)


# ── The preflight ───────────────────────────────────────────────────────────

def shfmt_problem(version_text):
    """Why shfmt --version's answer won't do for the gate's gh check, or None: check_gh_repo.py's own rule."""
    match = SHFMT_RELEASE.fullmatch(version_text.strip())
    if match is None or int(match.group(1)) < SHFMT_OLDEST_MINOR:
        return f"shfmt is {version_text.strip() or 'silent'}; the gate needs v3.{SHFMT_OLDEST_MINOR} or a later v3 release"
    return None


def sdk_hint(text):
    """The SDK version a global.json's text names, for the install hint only, or None. dotnet decides what the file
    means (it allows comments and a byte-order mark, which a JSON parser refuses); this only finds a plain version."""
    match = re.search(r'"sdk"\s*:\s*\{[^{}]*?"version"\s*:\s*"([0-9][0-9A-Za-z.+-]{0,40})"', text)
    return match.group(1) if match else None


def run_quiet(command, cwd=None, env=None):
    """(exit code, stdout, stderr) of a command; 127 when it can't start, 124 when it runs past two minutes."""
    try:
        done = subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True, timeout=120,
                              stdin=subprocess.DEVNULL)
    except subprocess.TimeoutExpired:
        return 124, "", f"{command[0]} didn't answer in 120 s"
    except OSError as error:
        return 127, "", str(error)
    return done.returncode, done.stdout, done.stderr


def preflight(which=shutil.which, run=run_quiet, template_global_json=TEMPLATE_GLOBAL_JSON):
    """What would stop the round partway through, as a list of problems (empty when there's none)."""
    problems = [f"{tool} isn't on PATH" for tool in TOOLS if which(tool) is None]
    if which("shfmt") is not None:
        # As check_gh_repo.py runs it: PATH alone, stdout alone.
        code, out, error = run(["shfmt", "--version"], env={"PATH": os.environ.get("PATH", "")})
        problem = shfmt_problem(out) if code == 0 else f"shfmt --version failed (exit {code}): {error.strip()}"
        if problem:
            problems.append(problem)
    if which("gh") is not None:
        code, _, error = run(["gh", "auth", "status"])
        if code in (124, 127):
            problems.append(f"gh auth status didn't run: {error.strip()}")
        elif code != 0:
            problems.append("gh isn't signed in: gh auth login")
    if os.path.isfile(template_global_json):
        problems += sdk_problems(template_global_json, which, run)
    return problems


def sdk_problems(global_json, which, run):
    """Whether an installed .NET SDK satisfies the Template's global.json, asked of dotnet in a folder holding a copy."""
    try:
        with open(global_json, encoding="utf-8", errors="replace") as handle:
            version = sdk_hint(handle.read())
    except OSError as error:
        return [f"can't read the Template's global.json: {error}"]
    named = f".NET SDK {version}" if version else "the .NET SDK it names"
    if which("dotnet") is None:
        return [f"the Template's global.json names {named}, and dotnet isn't on PATH"]
    with tempfile.TemporaryDirectory(prefix="rollout-sdk.") as folder:
        shutil.copyfile(global_json, os.path.join(folder, "global.json"))
        code, _, error = run(["dotnet", "--version"], cwd=folder)
    if code == 0:
        return []
    install = f": dotnetup sdk install {version}" if version else ""
    why = f" ({error.strip()})" if code in (124, 127) else ""
    return [f"no installed .NET SDK matches the Template's global.json ({named}){install}{why}"]


# ── The outside world ───────────────────────────────────────────────────────

def take_lock(path=LOCK):
    """An exclusive lock for this --auto run, held until the process ends, or None when another run holds it."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    handle = open(path, "a")  # noqa: SIM115 - held for the process's life
    try:
        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        handle.close()
        return None
    return handle


def owned_changes(worktree, run=run_quiet, owned=OWNED):
    """The Owned files the worktree's branch changes against origin/main, for the review's prompt."""
    code, out, error = run(["git", "-C", worktree, "diff", "--name-only", "-z", "origin/main...HEAD"])
    if code != 0:
        raise RuntimeError(f"git diff in {worktree} failed: {error.strip()}")
    return sorted(path for path in out.split("\0") if path and os.path.isfile(os.path.join(owned, path)))


def review_command(repo, worktree, owned):
    """The headless review's argv."""
    prompt = REVIEW_PROMPT.format(worktree=worktree, repo=repo, owned=", ".join(owned) or "none")
    return ["claude", "-p", prompt, "--permission-mode", "auto", "--disallowedTools", *DENIED_TOOLS]


def run_review(repo, worktree, run=subprocess.run, changes=owned_changes, timeout=REVIEW_TIMEOUT_MINUTES * 60):
    """/pre-push-review headless in worktree: (exit code, the session's log). 124 when it runs past its time, 127 when
    it can't start. The log is the caller's own (mkstemp makes it 0600): it holds what the session printed."""
    try:
        handle, log = tempfile.mkstemp(prefix=f"review-{short_name(repo)}.", suffix=".log")
    except OSError as error:
        return 127, f"(no log: {error})"
    with os.fdopen(handle, "w") as out:
        if not os.path.isdir(worktree):
            out.write(f"rollout.sh: the re-Apply worktree {worktree} doesn't exist\n")
            return 127, log
        try:
            command = review_command(repo, worktree, changes(worktree))
        except (OSError, RuntimeError) as error:
            out.write(f"rollout.sh: can't list the Owned files the branch changes: {error}\n")
            return 127, log
        # The gate's review check must run in the session, so a GATE_SKIP_REVIEW from the caller's shell is dropped.
        env = {name: value for name, value in os.environ.items() if name != "GATE_SKIP_REVIEW"}
        out.flush()
        try:
            return run(command, cwd=worktree, env=env, stdin=subprocess.DEVNULL, stdout=out,
                       stderr=subprocess.STDOUT, timeout=timeout).returncode, log
        except subprocess.TimeoutExpired:
            out.write(f"\nrollout.sh: the review session ran past {timeout // 60} minutes and was stopped\n")
            return 124, log
        except OSError as error:
            out.write(f"rollout.sh: can't start claude: {error}\n")
            return 127, log


def run_land(refs):
    """land.sh on the PRs, its output straight through; its exit code (0 once each has merged or closed)."""
    return subprocess.run([LAND, *refs]).returncode


def gh_pr(repo):
    """(number, state, head commit) of repo's newest re-Apply PR, or None; raises when gh fails."""
    done = subprocess.run(["gh", "pr", "list", "-R", repo, "--head", BRANCH, "--state", "all", "--limit", "1",
                           "--json", "number,state,headRefOid",
                           "--jq", '.[0] // empty | "\\(.number) \\(.state) \\(.headRefOid)"'],
                          capture_output=True, text=True, timeout=120)
    if done.returncode != 0:
        raise RuntimeError(done.stderr.strip() or f"gh pr list exited {done.returncode}")
    if not done.stdout.strip():
        return None
    number, state, head = done.stdout.split()
    return int(number), state, head


def git_branch(worktree):
    """(HEAD, whether the branch has commits main lacks) of the re-Apply worktree; raises when git fails."""
    head = subprocess.run(["git", "-C", worktree, "rev-parse", "HEAD"], capture_output=True, text=True, check=True,
                          timeout=120).stdout.strip()
    on_main = subprocess.run(["git", "-C", worktree, "merge-base", "--is-ancestor", "HEAD", "origin/main"],
                             capture_output=True, timeout=120).returncode
    if on_main not in (0, 1):
        raise RuntimeError(f"git merge-base in {worktree} exited {on_main}")
    return head, on_main == 1


# ── The round ───────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class Outcome:
    """How one repo's reapply ended: "ok" (pushed, or nothing new), "person" (a decision waits for a person) or
    "failed", with what to do next."""

    status: str
    detail: str = ""


def drive(repo, path, brings, reapply, review, say):
    """reapply.sh on one repo, with a headless review each time the gate asks for one."""
    worktree = review_worktree(path)
    retry = f"reapply.sh --brings {shlex.quote(brings)} {shlex.quote(path)}"
    reviews, log = 0, None
    while True:
        say(f"── rollout: {short_name(repo)}")
        code = reapply(brings, path)
        if code == 0:
            return Outcome("ok")
        if code == FINDINGS_WAIT:
            return Outcome("person", f"review findings wait for you: cd {shlex.quote(worktree)} && scripts/review.sh accept-open")
        if code != REVIEW_NEEDED:
            return Outcome("failed", f"reapply.sh exited {code}; retry it with: {retry}")
        if reviews == MAX_REVIEWS:
            return Outcome("failed", f"the gate still asks for a review after {reviews} review session(s): main or the Template may have"
                           f" moved since the last one (see {log}); run /pre-push-review in {shlex.quote(worktree)}, then resume")
        reviews += 1
        say(f"── rollout: {short_name(repo)}: the gate asks for a review; running /pre-push-review headless in {worktree}")
        code, log = review(repo, worktree)
        say(f"── rollout: {short_name(repo)}: review session exited {code}; log: {log}")
        if code != 0:
            return Outcome("failed", f"the review session exited {code} (see {log}); run /pre-push-review in"
                           f" {shlex.quote(worktree)}, then resume")


class PrUnknown(Exception):
    """The re-Apply PR for a worktree's head couldn't be read or found."""


def auto_round(order, brings, root_arg, checkout, reapply, review, land, pr_of, branch_of, say, sleep=time.sleep):
    """The round after the preflight; its exit code."""
    resume = f"rollout.sh --auto --brings {shlex.quote(brings)}{root_arg}"
    first, rest = order[0], order[1:]

    def read(repo, landed=False):
        """(number, state) of the PR for the worktree's head. With landed, it waits for the read to leave OPEN, since
        land.sh reads a PR directly and a list can lag it. Raises PrUnknown."""
        worktree = review_worktree(checkout(repo))
        try:
            head, ahead = branch_of(worktree)
        except Exception as error:  # noqa: BLE001 - reported as the stop
            raise PrUnknown(f"can't read {worktree}'s branch: {error}") from error
        for attempt in range(LOOKUP_TRIES):
            if attempt:
                sleep(LOOKUP_WAIT_SECONDS)
            try:
                found = pr_of(repo)
            except Exception as error:  # noqa: BLE001 - tried again, then reported
                found, last = None, f"gh failed: {error}"
            else:
                last = f"no re-Apply PR shows head {head[:7]}" if ahead else "no re-Apply PR found"
            if found is None:
                continue
            number, state, pr_head = found
            if ahead and pr_head != head:
                continue  # the last round's PR, or a list that lags the push
            if landed and state == "OPEN":
                last = f"#{number} still reads as OPEN"
                continue
            return number, state
        raise PrUnknown(last)

    def held(reason):
        say(f"rollout: {reason}; held: {', '.join(short_name(repo) for repo in rest) or 'none'}")
        say(f"  resume with: {resume}")
        return 1

    try:
        lead = drive(first, checkout(first), brings, reapply, review, say)
        if lead.status != "ok":
            return held(f"{short_name(first)}: {lead.detail}")
        number, state = read(first)
        if state == "OPEN":
            say(f"── rollout: landing {short_name(first)}#{number}")
            if land([f"{first}#{number}"]) != 0:
                return held(f"{short_name(first)}#{number} didn't land (see land.sh's output above): land.sh {first}#{number}")
            number, state = read(first, landed=True)
        if state != "MERGED":
            return held(f"{short_name(first)}#{number} is {state}, not merged")
    except PrUnknown as error:
        return held(f"{short_name(first)}: can't tell its re-Apply PR ({error}): land it with land.sh {first}")
    except Exception as error:  # noqa: BLE001 - a crash still ends with the stop and the resume command
        return held(f"{short_name(first)}: {type(error).__name__}: {error}")

    stops, landing = [], []
    for repo in rest:
        try:
            outcome = drive(repo, checkout(repo), brings, reapply, review, say)
            if outcome.status != "ok":
                stops.append(f"{short_name(repo)}: {outcome.detail}")
                continue
            number, state = read(repo)
        except PrUnknown as error:
            stops.append(f"{short_name(repo)}: can't tell its re-Apply PR ({error}): land it with land.sh {repo}")
            continue
        except Exception as error:  # noqa: BLE001 - reported, and the round goes on
            stops.append(f"{short_name(repo)}: {type(error).__name__}: {error}")
            continue
        if state == "OPEN":
            landing.append((repo, number))
    if landing:
        refs = [f"{repo}#{number}" for repo, number in landing]
        say(f"── rollout: landing {' '.join(refs)}")
        try:
            land(refs)
        except Exception as error:  # noqa: BLE001 - each PR's state below says what landed
            say(f"rollout.sh: land.sh failed: {error}")
        for repo, number in landing:
            try:
                state = read(repo, landed=True)[1]
            except PrUnknown as error:
                stops.append(f"{short_name(repo)}#{number} didn't land ({error}): land.sh {repo}#{number}")
                continue
            if state != "MERGED":
                stops.append(f"{short_name(repo)}#{number} is {state}, not merged")
    say(f"rollout: {short_name(first)} and {len(rest) - len(stops)} of {len(rest)} other repo(s) done"
        + ("" if not stops else "; waiting for you:"))
    for stop in stops:
        say(f"  {stop}")
    if stops:
        say(f"  resume with: {resume}")
    return 1 if stops else 0


def run(order, brings, root, checkout, dry_run, reapply, say, err, review=run_review, land=run_land, pr_of=gh_pr,
        branch_of=git_branch, check=preflight, lock=take_lock, sleep=time.sleep, default_root=None):
    """rollout.sh --auto, after rollout.py has read the arguments and found every checkout."""
    if default_root is None:
        default_root = os.path.expanduser("~/github")
    root_arg = "" if os.path.normpath(root) == os.path.normpath(default_root) else f" --root {shlex.quote(root)}"
    problems = check()
    for problem in problems:
        err.write(f"rollout.sh: {problem}\n")
    if problems:
        err.write("rollout.sh: nothing run; fix those first\n")
        return 1
    if dry_run:
        say("tools: all there")
        for repo in order:
            say(f"would run: reapply.sh --brings {shlex.quote(brings)} {shlex.quote(checkout(repo))}, with /pre-push-review"
                f" headless in {review_worktree(checkout(repo))} when its gate asks for one")
        say(f"then land {short_name(order[0])}'s re-Apply PR with land.sh before the rest start, and the rest's together")
        return 0
    handle = lock()  # held while the round runs
    if handle is None:
        err.write(f"rollout.sh: another rollout.sh --auto holds {LOCK}; nothing run\n")
        return 1
    return auto_round(order, brings, root_arg, checkout, reapply, review, land, pr_of, branch_of, say, sleep)
