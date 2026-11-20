"""rollout.sh --auto: a re-Apply round driven to the end with no person between the steps (ADR 0015).

    rollout.sh --auto --brings <prs> [--root dir] [--dry-run]

1. It checks what would stop the round partway through: a tool missing from
   PATH, an shfmt the gate's gh check refuses, gh signed out, and the .NET SDK
   the Template's global.json names (an Owned file, so Apply writes it into
   every re-Apply worktree and the gate builds with it). One problem stops the
   round before anything runs. A second --auto run while one holds the lock
   stops too, since both would work the same worktrees and review ledgers.
   The lock keeps --auto runs apart only: while a round runs, don't run
   reapply.sh or /pre-push-review by hand in a re-Apply worktree.
2. reapply.sh on each repo, TicketManager first. When the gate asks for a
   review (reapply.sh exits 3), it runs /pre-push-review headless: `claude -p`
   started in the repo's re-Apply worktree, so Claude Code loads that
   checkout's pre-push-reviewer agent and the skill runs that checkout's
   scripts/review.sh. Then reapply.sh again, with at most MAX_REVIEWS sessions
   per repo, each one at most REVIEW_TIMEOUT_MINUTES long.
3. It lands TicketManager's re-Apply PR with land.sh before it starts the rest,
   and the rest's PRs together once each has been reapplied.

The session runs unattended with the owner's sign-ins, reading text people
without write access may have written. The prompt states the skill's Rules,
--disallowedTools denies the common spellings of git push, review.sh accept and
accept-open and a set of gh writes, and the file tools' edits under the Factory
checkout and ~/.claude; the auto permission mode's classifier is the guard for
the rest (other spellings, Bash writes, gh api). It isn't given the Template's
folder (no --add-dir): the prompt names the Owned files the branch changes. It
runs in a process group of its own, which is killed whole however the session
ends (it exits, it runs past its time, or rollout.sh gets an interrupt, a
SIGTERM or a SIGHUP; a SIGKILL of rollout.sh can't be caught, and leaves it
running). Its log is kept, 0600, in a 0700 folder of the user's own,
~/.cache/repo-ci-baseline/logs: it can hold the session's report of security
findings.

A PR is the repo's own (never a fork's of the same branch name), and when the
worktree's branch has commits main lacks, it is the one at the worktree's head:
the name is the same every round, so the newest PR by name may be the last
round's. An OPEN PR at another head is never this round's either, and with
nothing ahead a CLOSED one at another head is the last round's: nothing to land. A read that doesn't show it is tried again LOOKUP_TRIES times. Once
land.sh has run, the PR is read by its number, since land.sh may update its
branch.

It stops for a person only where one must decide: review findings that wait for
a person (reapply.sh exits 4), a PR that doesn't land, a reapply or review
session that fails, a PR it can't read. TicketManager stopping holds the rest;
one of the rest stopping is reported and the round goes on. Each stop prints
the command that decides it, and the round ends with the command that resumes
it: rollout.sh --auto again, which picks up where it stopped (a repo already
reapplied finds nothing new to push, and its open PR is landed).
"""

import contextlib
import fcntl
import os
import re
import shlex
import shutil
import signal
import stat
import subprocess
import tempfile
import time
import traceback
from dataclasses import dataclass

SKILL_DIR = os.path.dirname(os.path.dirname(os.path.realpath(__file__)))
FACTORY = os.path.dirname(os.path.dirname(os.path.dirname(SKILL_DIR)))
LAND = os.path.join(SKILL_DIR, "land.sh")
OWNED = os.path.join(SKILL_DIR, "template", "owned")
TEMPLATE_GLOBAL_JSON = os.path.join(OWNED, "global.json")
LOCK = os.path.expanduser("~/.cache/repo-ci-baseline/rollout-auto.lock")
LOG_DIR = os.path.expanduser("~/.cache/repo-ci-baseline/logs")
# How long a killed session's process group may take to be gone before the log says it didn't.
GROUP_WAIT_SECONDS = 10
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
# CLAUDE.md). Deny rules hold in every permission mode, but they match command prefixes, and Edit and Write rules bind
# the file tools only: the auto mode's classifier covers what they can't name (ADR 0015).
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
        done = subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True, errors="replace",
                              timeout=120, stdin=subprocess.DEVNULL)
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
    if os.path.lexists(template_global_json):
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
    """An exclusive lock for this --auto run, held while the returned file stays open, or None when another run holds
    it. Raises OSError when it can't be taken at all (an unwritable ~/.cache, a filesystem without locks)."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    handle = open(path, "a")  # noqa: SIM115 - held until the caller closes it (run's finally)
    try:
        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        handle.close()
        return None
    return handle


def owned_changes(worktree, run=run_quiet, owned=OWNED):
    """The Owned files the worktree's branch changes against origin/main, for the review's prompt."""
    # --no-renames, as checkReview's own diff: a renamed Owned file is a change to the Owned path too.
    code, out, error = run(["git", "-C", worktree, "diff", "--no-renames", "--name-only", "-z", "origin/main...HEAD"])
    if code != 0:
        raise RuntimeError(f"git diff in {worktree} failed: {error.strip()}")
    return sorted(path for path in out.split("\0") if path and os.path.isfile(os.path.join(owned, path)))


def review_command(repo, worktree, owned):
    """The headless review's argv."""
    prompt = REVIEW_PROMPT.format(worktree=worktree, repo=repo, owned=", ".join(owned) or "none")
    return ["claude", "-p", prompt, "--permission-mode", "auto", "--disallowedTools", *DENIED_TOOLS]


def own_log_dir(log_dir):
    """Make the log folder, or check the one there: a real folder of the user's own, which is then set to 0700. Raises
    OSError naming what is wrong (a symlink, another owner's folder), since makedirs leaves an existing folder as it is."""
    os.makedirs(log_dir, mode=0o700, exist_ok=True)
    found = os.lstat(log_dir)
    if not stat.S_ISDIR(found.st_mode) or found.st_uid != os.getuid():
        raise OSError(f"{log_dir} isn't a folder of the user's own")
    os.chmod(log_dir, 0o700)


@contextlib.contextmanager
def signals_raise(signums=(signal.SIGTERM, signal.SIGHUP)):
    """While the block runs, SIGTERM and SIGHUP raise SystemExit (128 + the signal), so the cleanup around a session
    runs: by default either ends Python at once, and a session in its own process group gets neither."""
    def handler(signum, frame):
        raise SystemExit(128 + signum)

    old = {signum: signal.signal(signum, handler) for signum in signums}
    try:
        yield
    finally:
        for signum, previous in old.items():
            signal.signal(signum, previous)


def end_group(session, wait_seconds=GROUP_WAIT_SECONDS, sleep=time.sleep, clock=time.monotonic):
    """Kill the session's whole process group, reap its leader, and wait until no process is left in the group, for at
    most wait_seconds: whether it is gone. The group's id is the leader's pid, which can't be another group's while a
    member of this one is left, so a signal to it after the leader is reaped reaches only this group's members."""
    try:
        os.killpg(session.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass  # nothing is left in the group
    session.wait()
    deadline = clock() + wait_seconds
    while True:
        try:
            os.killpg(session.pid, 0)
        except ProcessLookupError:
            return True
        if clock() >= deadline:
            return False
        sleep(0.1)


def run_review(repo, worktree, popen=subprocess.Popen, changes=owned_changes, timeout=REVIEW_TIMEOUT_MINUTES * 60,
               log_dir=LOG_DIR, end=end_group):
    """/pre-push-review headless in worktree: (exit code, the session's log). 124 when it runs past its time, 127 when
    it can't start. The session leads a process group of its own, which is killed whole whenever the session ends, so
    nothing it started (its gate, its reviewers' tools, a process it left behind) outlives it. An interrupt, a SIGTERM
    or a SIGHUP is passed on after the kill. The log is 0600 (mkstemp) in a 0700 folder of the user's own."""
    try:
        own_log_dir(log_dir)
        handle, log = tempfile.mkstemp(prefix=f"review-{short_name(repo)}.", suffix=".log", dir=log_dir)
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
        with signals_raise():
            try:
                session = popen(command, cwd=worktree, env=env, stdin=subprocess.DEVNULL, stdout=out,
                                stderr=subprocess.STDOUT, start_new_session=True)
            except OSError as error:
                out.write(f"rollout.sh: can't start claude: {error}\n")
                return 127, log
            try:
                code = session.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                out.write(f"\nrollout.sh: the review session ran past {timeout // 60} minutes and was stopped\n")
                code = 124
            finally:
                if not end(session):
                    out.write(f"\nrollout.sh: the session's process group was still there {GROUP_WAIT_SECONDS} s after it was killed\n")
        return code, log


def run_land(refs):
    """land.sh on the PRs, its output straight through; its exit code (0 once each has merged or closed)."""
    return subprocess.run([LAND, *refs]).returncode


def gh_pr(repo):
    """(number, state, head commit) of repo's newest re-Apply PR from repo itself, or None; raises when gh fails.
    --head matches the branch name only, so a fork's PR of the same name is left out, as reapply.sh leaves it out."""
    done = subprocess.run(["gh", "pr", "list", "-R", repo, "--head", BRANCH, "--state", "all", "--limit", "100",
                           "--json", "number,state,headRefOid,isCrossRepository", "--jq", PR_JQ],
                          capture_output=True, text=True, errors="replace", timeout=120)
    if done.returncode != 0:
        raise RuntimeError(done.stderr.strip() or f"gh pr list exited {done.returncode}")
    if not done.stdout.strip():
        return None
    number, state, head = done.stdout.split()
    return int(number), state, head


# gh pr list's newest first: the first PR from the repo itself, as "<number> <state> <head>".
PR_JQ = '[.[] | select(.isCrossRepository == false)][0] // empty | "\\(.number) \\(.state) \\(.headRefOid)"'


def gh_pr_state(repo, number):
    """The state of repo's PR number (OPEN, MERGED or CLOSED); raises when gh fails."""
    done = subprocess.run(["gh", "pr", "view", str(number), "-R", repo, "--json", "state", "--jq", ".state"],
                          capture_output=True, text=True, errors="replace", timeout=120)
    if done.returncode != 0 or not done.stdout.strip():
        raise RuntimeError(done.stderr.strip() or f"gh pr view exited {done.returncode}")
    return done.stdout.strip()


def git_branch(worktree):
    """(HEAD, whether the branch has commits main lacks) of the re-Apply worktree; raises with git's own words."""
    def git(*args):
        done = subprocess.run(["git", "-C", worktree, *args], capture_output=True, text=True, errors="replace",
                              timeout=120)
        return done.returncode, done.stdout.strip(), done.stderr.strip()

    code, head, error = git("rev-parse", "HEAD")
    if code != 0:
        raise RuntimeError(f"git rev-parse HEAD in {worktree} exited {code}: {error}")
    code, _, error = git("merge-base", "--is-ancestor", "HEAD", "origin/main")
    if code not in (0, 1):
        raise RuntimeError(f"git merge-base in {worktree} exited {code}: {error}")
    return head, code == 1


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


def auto_round(order, brings, resume, checkout, reapply, review, land, pr_of, pr_state, branch_of, say, err,
               sleep=time.sleep):
    """The round after the preflight; its exit code. resume is the command that resumes it."""
    first, rest = order[0], order[1:]

    def find(repo):
        """(number, state) of the repo's re-Apply PR for its worktree, or None when nothing was pushed and it has
        none. Raises PrUnknown when gh keeps failing, or no PR shows the head the worktree pushed."""
        worktree = review_worktree(checkout(repo))
        try:
            head, ahead = branch_of(worktree)
        except Exception as error:  # noqa: BLE001 - reported as the stop
            raise PrUnknown(f"can't read {worktree}'s branch: {error}") from error
        last = "no re-Apply PR found"
        for attempt in range(LOOKUP_TRIES):
            if attempt:
                sleep(LOOKUP_WAIT_SECONDS)
            try:
                found = pr_of(repo)
            except Exception as error:  # noqa: BLE001 - tried again, then reported
                last = f"gh failed: {error}"
                continue
            if found is None:
                if not ahead:
                    return None  # nothing pushed, so no list can lag: there is no PR
                last = f"no re-Apply PR shows head {head[:7]}"
                continue
            number, state, pr_head = found
            if pr_head != head and not ahead and state == "CLOSED":
                return None  # the last round's PR, closed unmerged: nothing was pushed, so nothing to land
            if pr_head != head and (ahead or state == "OPEN"):
                # The last round's PR while the list lags the push, or a PR whose branch moved since.
                last = f"#{number} is at {pr_head[:7]}, the worktree at {head[:7]}"
                continue
            return number, state
        raise PrUnknown(last)

    def settle(repo, number):
        """The state of a PR land.sh has finished with, read by its number (land.sh may have updated its branch), once
        it no longer reads as OPEN. Raises PrUnknown."""
        last = f"#{number} still reads as OPEN"
        for attempt in range(LOOKUP_TRIES):
            if attempt:
                sleep(LOOKUP_WAIT_SECONDS)
            try:
                state = pr_state(repo, number)
            except Exception as error:  # noqa: BLE001 - tried again, then reported
                last = f"gh failed: {error}"
                continue
            if state != "OPEN":
                return state
            last = f"#{number} still reads as OPEN"  # a read that answers replaces an earlier gh failure
        raise PrUnknown(last)

    def crashed(repo, error):
        """A crash still ends with a stop: the traceback goes to stderr, so a bug can be told from the world."""
        err.write(f"rollout.sh: {short_name(repo)} crashed:\n{traceback.format_exc()}")
        return f"{short_name(repo)}: {type(error).__name__}: {error} (traceback on stderr)"

    def held(reason):
        say(f"rollout: {reason}; held: {', '.join(short_name(repo) for repo in rest) or 'none'}")
        say(f"  resume with: {resume}")
        return 1

    try:
        lead = drive(first, checkout(first), brings, reapply, review, say)
        if lead.status != "ok":
            return held(f"{short_name(first)}: {lead.detail}")
        found = find(first)
        if found is None:
            return held(f"{short_name(first)}: no merged re-Apply PR found, and nothing to push")
        number, state = found
        if state == "OPEN":
            say(f"── rollout: landing {short_name(first)}#{number}")
            if land([f"{first}#{number}"]) != 0:
                return held(f"{short_name(first)}#{number} didn't land (see land.sh's output above): land.sh {first}#{number}")
            state = settle(first, number)
        if state != "MERGED":
            return held(f"{short_name(first)}#{number} is {state}, not merged")
    except PrUnknown as error:
        return held(f"{short_name(first)}: can't tell its re-Apply PR ({error}): land it with land.sh {first}")
    except Exception as error:  # noqa: BLE001 - a crash still ends with the stop and the resume command
        return held(crashed(first, error))

    stops, landing = [], []
    for repo in rest:
        try:
            outcome = drive(repo, checkout(repo), brings, reapply, review, say)
            if outcome.status != "ok":
                stops.append(f"{short_name(repo)}: {outcome.detail}")
                continue
            found = find(repo)
        except PrUnknown as error:
            stops.append(f"{short_name(repo)}: can't tell its re-Apply PR ({error}): land it with land.sh {repo}")
            continue
        except Exception as error:  # noqa: BLE001 - reported, and the round goes on
            stops.append(crashed(repo, error))
            continue
        if found is None:
            continue  # nothing to push and never a re-Apply PR: nothing to land
        number, state = found
        if state == "OPEN":
            landing.append((repo, number))
        elif state != "MERGED":
            stops.append(f"{short_name(repo)}#{number} is {state}, not merged")
    if landing:
        refs = [f"{repo}#{number}" for repo, number in landing]
        say(f"── rollout: landing {' '.join(refs)}")
        try:
            landed = land(refs)
        except Exception as error:  # noqa: BLE001 - each PR's state below says what landed
            say(f"rollout.sh: land.sh failed: {type(error).__name__}: {error}")
        else:
            if landed != 0:
                say(f"rollout.sh: land.sh exited {landed} (see its output above)")
        for repo, number in landing:
            try:
                state = settle(repo, number)
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


def run(order, brings, root, checkout, dry_run, reapply, say, err, default_root, review=run_review, land=run_land,
        pr_of=gh_pr, pr_state=gh_pr_state, branch_of=git_branch, check=preflight, lock=take_lock, sleep=time.sleep):
    """rollout.sh --auto, after rollout.py has read the arguments and found every checkout. default_root is
    rollout.py's DEFAULT_ROOT: the printed commands carry --root only when the round runs with another."""
    root_arg = "" if os.path.normpath(root) == os.path.normpath(default_root) else f" --root {shlex.quote(root)}"
    resume = f"rollout.sh --auto --brings {shlex.quote(brings)}{root_arg}"
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
    try:
        handle = lock()
    except OSError as error:
        err.write(f"rollout.sh: can't take the lock {LOCK}: {error}; nothing run\n")
        return 1
    if handle is None:
        err.write(f"rollout.sh: another rollout.sh --auto holds {LOCK}; nothing run\n")
        return 1
    try:
        return auto_round(order, brings, resume, checkout, reapply, review, land, pr_of, pr_state, branch_of, say, err,
                          sleep)
    except (KeyboardInterrupt, SystemExit) as stop:
        # An interrupt, or a SIGTERM or SIGHUP during a review session (signals_raise): the session's group is gone.
        code = stop.code if isinstance(stop, SystemExit) and isinstance(stop.code, int) else 130
        err.write(f"rollout.sh: interrupted; resume with: {resume}\n")
        return code
    finally:
        handle.close()  # the lock is released here, and not before
