"""rollout.sh --auto: a re-Apply round driven to the end with no person between the steps (ADR 0016).

    rollout.sh --auto --brings <prs> [--root dir] [--dry-run]

1. It checks what would stop the round partway through: a tool missing from
   PATH, an shfmt the gate's gh check refuses, neither ruff 0.17 nor uv for the
   gate's Python lint, a claude that doesn't run, a sandbox bubblewrap can't
   make (on Linux), gh signed out, and the .NET SDK the Template's global.json
   names (an Owned file, so Apply writes it into every re-Apply worktree and the
   gate builds with it), and a sign-in secret in the environment the review
   session would inherit. One problem stops the round before anything runs. A
   second --auto run while the lock is held stops too, since both would work
   the same worktrees and review ledgers; every child the round starts holds
   the lock as well, so one a SIGKILL of rollout.sh leaves running keeps it.
   The lock keeps --auto runs apart only: while a round runs, don't run
   reapply.sh or /pre-push-review by hand in a re-Apply worktree.
2. reapply.sh on each repo, TicketManager first. When the gate asks for a
   review (reapply.sh exits 3), it runs /pre-push-review headless: `claude -p`
   started in the repo's re-Apply worktree, with the skill's text and its
   pre-push-reviewer agent from the Template, so the skill runs that
   worktree's scripts/review.sh and ledger. Then reapply.sh again, with at most
   MAX_REVIEWS sessions per repo, each one at most REVIEW_TIMEOUT_MINUTES long.
3. It lands TicketManager's re-Apply PR with land.sh before it starts the rest,
   and the rest's PRs together once each has been reapplied.

The session runs unattended, reading text people without write access may have
written, so it reviews only, and runs in Claude Code's sandbox (session_settings;
ADR 0016). It runs one review-only round and records what it finds, open: it
edits, fixes and commits nothing, and runs no gate, so nothing it wrote runs
later as the person. Its Bash can't read the person's credentials
(CREDENTIAL_PATHS, ~/.claude among them), writes only the clone's review
ledgers, the main checkout's .security and a temporary folder of its own
(session_writes: never the worktree or the rest of its git folder), reaches
only SANDBOX_DOMAINS, and can't leave the sandbox; the session refuses to start
without the sandbox (bubblewrap and socat on Linux, which the preflight checks
for). Its file tools, which run outside the sandbox, may read only the
worktree, the .security record and that folder, and edit only that folder
(dontAsk: anything not allowed is refused). It loads no settings file (neither
the repository's nor the person's), no MCP server and only SESSION_BUILTINS,
gets its settings as JSON on its command line, and the skill and its reviewer
agent from the Template, so no hook, permission rule or plugin but these
applies. Its environment has no sign-in secret (gh points at an empty config;
names that look like secrets are dropped, and of Claude Code's own only
CLAUDE_KEPT is kept, while the preflight refuses a round with a Claude Code or
cloud sign-in secret set, since the session would inherit it) and git's push is
sent nowhere: layers behind the sandbox. After the session the round checks what
it still could do: a session after which the worktree's .git link changed, the
remote branch moved (or can't be read), the worktree's HEAD, branch or status
changed, the Template changed, an Owned file is no longer the Template's, any of
the clone's ledgers gained an accepted finding, or the .security record lost or
changed an entry (record marking an open one fixed aside), stops the round for
the person.
It runs in a process group of its own, killed whole however it ends (it exits,
or runs past its time); an interrupt, a SIGTERM or a SIGHUP while it runs is
held until the group is gone, then passed on, and a SIGKILL of rollout.sh can't
be caught, and leaves the session running (holding the lock until it ends).
reapply.sh and land.sh run in groups of their own too, held the same way, which
a SIGTERM, SIGHUP or interrupt of the round kills. Its log is kept, 0600, in a 0700 folder of the user's own,
~/.cache/repo-ci-baseline/logs: it can hold the session's report of security
findings.

A PR is the repo's own (never a fork's of the same branch name), and when the
worktree's branch has commits main lacks, it is the one at the worktree's head:
the name is the same every round, so the newest PR by name may be the last
round's. An OPEN PR at another head is never this round's either, and with
nothing ahead a CLOSED one at another head is the last round's: nothing to
land. A read that doesn't show the PR at the worktree's head is tried again
LOOKUP_TRIES times. Once land.sh has run, the PR is read by its number, since
land.sh may update its branch.

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
import hashlib
import json
import math
import os
import re
import shlex
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
import traceback
from dataclasses import dataclass

SKILL_DIR = os.path.dirname(os.path.dirname(os.path.realpath(__file__)))
FACTORY = os.path.dirname(os.path.dirname(os.path.dirname(SKILL_DIR)))
LAND = os.path.join(SKILL_DIR, "land.sh")
REAPPLY = os.path.join(SKILL_DIR, "reapply.sh")
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
# A review session's wall clock: one round of parallel reviewers over a large re-Apply, with room to spare.
REVIEW_TIMEOUT_MINUTES = 180
# Reads of a PR that doesn't yet show the worktree's head, and the seconds between them.
LOOKUP_TRIES = 5
LOOKUP_WAIT_SECONDS = 10
# What --auto needs on PATH: reapply.sh and land.sh (git, gh), the gate (python3, node, pnpm, shfmt) and the review
# session (claude). dotnet is needed only when the Template carries a global.json.
TOOLS = ("git", "gh", "python3", "node", "pnpm", "shfmt", "claude")
# (SANDBOX_TOOLS, below, are checked too, and the gate's Python lint: ruff 0.17 or uv.)
# check_gh_repo.py's SHFMT_RELEASE and SHFMT_OLDEST_MINOR (a test holds them equal): a v3 release from v3.14 on.
SHFMT_RELEASE = re.compile(r"v3\.(0|[1-9][0-9]*)\.[0-9]+")
SHFMT_OLDEST_MINOR = 14
# The Template gate's RUFF_RELEASE and RUFF_VERSION (a test holds them equal): its Python lint uses an installed ruff
# only when it is a 0.17 release, and otherwise runs the pinned one through uvx.
RUFF_RELEASE = re.compile(r"^ruff 0\.17\.[0-9]+$")
RUFF_VERSION = "0.17.6"
# The Template's own copies of the skill the session runs and of its reviewer agent. The session gets both from here,
# never from the repository it reviews: it loads no settings file at all (--setting-sources ""), so the repository's
# hooks and permission rules, and the person's own, don't apply to it either.
SKILL_FILE = os.path.join(OWNED, ".claude", "skills", "pre-push-review", "SKILL.md")
AGENT_FILE = os.path.join(OWNED, ".claude", "agents", "pre-push-reviewer.md")
# What the session may never do, whatever text it reads: the skill's Rules, and the Template-first rule (the Factory's
# CLAUDE.md). Deny rules hold in every permission mode; they match command prefixes, and Edit rules bind the file
# tools only. The sandbox is what holds against the rest (ADR 0016).
DENIED_TOOLS = (
    "Bash(git push:*)",
    "Bash(scripts/review.sh accept:*)",
    "Bash(scripts/review.sh accept-open:*)",
    "Bash(gh pr create:*)", "Bash(gh pr edit:*)", "Bash(gh pr comment:*)", "Bash(gh pr review:*)",
    "Bash(gh pr merge:*)", "Bash(gh pr ready:*)", "Bash(gh pr close:*)",
    "Bash(gh issue create:*)", "Bash(gh issue comment:*)", "Bash(gh issue edit:*)", "Bash(gh issue close:*)",
    "Bash(gh label:*)", "Bash(gh release:*)", "Bash(gh repo:*)", "Bash(gh secret:*)", "Bash(gh workflow:*)",
    f"Edit(/{FACTORY}/**)", "Edit(~/.claude/**)",
)
# The built-in tools the session has at all (--tools): no WebFetch, WebSearch or notebook tool exists in it, whatever
# an allow rule says.
SESSION_BUILTINS = ("Read", "Write", "Edit", "Glob", "Grep", "Bash", "Agent", "TodoWrite", "SendMessage")
# The tools the session may use without a path, in dontAsk mode, where anything not allowed is refused: the review's
# subagents and messages to them, its task list, and Bash, which runs inside the sandbox. Reads (Read, Glob, Grep) and
# edits are allowed by path only (session_settings adds them).
SESSION_TOOLS = ("Bash", "Agent", "Task", "TodoWrite", "SendMessage")
# What neither the sandbox's Bash nor the file tools may read: the person's credentials of every kind (git, SSH, GPG,
# GitHub and GitLab, clouds, containers, package registries, secret stores, databases), shell histories, Claude Code's
# own sign-in and the other sessions' logs. A list of what to hide, not of what to show: a tool keeping a credential
# elsewhere needs its path added. The sandbox masks each of these paths; the Read rules cover the file tools, which
# run outside it and are also allowed to read only the worktree, the .security record and the session's own folder.
CREDENTIAL_PATHS = (
    "~/.ssh", "~/.config/gh", "~/.config/glab-cli", "~/.config/hub", "~/.git-credentials", "~/.config/git/credentials",
    "~/.netrc", "~/.gnupg", "~/.docker", "~/.aws", "~/.azure", "~/.config/gcloud", "~/.kube", "~/.terraform.d",
    "~/.npmrc", "~/.yarnrc", "~/.yarnrc.yml", "~/.config/pnpm", "~/.nuget/NuGet", "~/.config/NuGet", "~/.pypirc",
    "~/.config/pip", "~/.config/uv", "~/.cargo/credentials", "~/.cargo/credentials.toml", "~/.gem/credentials",
    "~/.m2/settings.xml", "~/.local/share/keyrings", "~/.password-store", "~/.vault-token", "~/.config/op",
    "~/.pgpass", "~/.bash_history", "~/.zsh_history", "~/.local/share/fish/fish_history", "~/.claude.json",
    "~/.claude", "~/.cache/repo-ci-baseline",
)
# The hosts the session's Bash may reach: GitHub alone, for the skill's git fetch. It builds and installs nothing (it
# runs no gate), so no package registry; and there is nothing to push with: no credential is readable and none is in
# its environment.
SANDBOX_DOMAINS = ("github.com",)
# What the sandbox runs on Linux (bubblewrap and socat); failIfUnavailable makes the session refuse to start without.
SANDBOX_TOOLS = ("bwrap", "socat") if sys.platform.startswith("linux") else ()
# The headless review's instructions, then the skill's own text from the Template. Nobody answers, the Owned files'
# fixes belong in the Template, and pushing, posting and deciding stay with the round and the person.
REVIEW_PROMPT = """You are running the pre-push-review skill, unattended, for rollout.sh --auto, in {worktree}, the re-Apply worktree of {repo}. The skill's instructions are below, from the Factory's Template; its base directory is {skill_dir}, the worktree's copy of the same files. Follow them, with these changes.
Nobody will answer a question: never ask one or wait for a reply; where the skill says to ask the person or show them text first, don't, and say so in your last message.
Review only: this session changes nothing in the repository. Don't edit, fix or commit anything, and don't run the gate (reapply.sh runs it on the push). Run one round over the whole branch, as the skill's last, review-only round: record every finding it keeps open, with its suggested fix, and the person fixes or accepts them. The worktree is read-only to you; write only to the run folder.
These files the branch changes are Owned Baseline files, which the next Apply overwrites: {owned}. A finding in one says, in its fix, what to change in the Factory's template/owned/.
Don't push, don't file the follow-up issue, and don't edit a PR: write the advisory body to the run folder and name its path.
Your shell runs in a sandbox with no GitHub sign-in, and git can't push: skip the skill's GitHub reads (the PR's threads, the issue) and say you did. reapply.sh has just fetched origin, so if git fetch fails, go on with origin/main as it is.
End with the output of scripts/review.sh report.

{skill}"""
# A name the session's environment leaves out: a token, secret, password, passphrase, key, credential, auth value or
# cookie of any tool, the helpers that hand one over (askpass, SSH), the keyring's bus, the X server's cookie, and the
# caller's own GIT_CONFIG_ entries, which session_env replaces. Of Claude Code's own names only CLAUDE_KEPT is kept,
# and of the cloud in use (CLAUDE_CODE_USE_*) only the names SIGN_IN_SECRET doesn't match. A layer only: the sandbox,
# which reads none of the credential files, is what holds.
SECRET_NAME = re.compile(
    r"TOKEN|SECRET|PASSW|PASSPHRASE|CREDENTIAL|API_?KEY|AUTH(?!OR)|COOKIE|BEARER|_PAT$|_KEY$|_DSN$|^DATABASE_URL$|ASKPASS"
    r"|^GIT_SSH|^SSH_|^DBUS_SESSION_BUS_ADDRESS$|^XAUTHORITY$|^GIT_CONFIG_(COUNT|PARAMETERS|KEY_[0-9]+|VALUE_[0-9]+)$"
    r"|^AWS_|^AZURE_|^GOOGLE_", re.I)
CLAUDE_SIGN_IN = re.compile(r"^(ANTHROPIC_|CLAUDE_)")
# Claude Code's names the session keeps: where its config is and which model, endpoint and cloud it uses, none a
# secret. Every other ANTHROPIC_ or CLAUDE_ name is left out: a key or token, and the calling session's own (its id,
# its messaging socket and token, its IDE's port), which the session's shell would otherwise see.
CLAUDE_KEPT = (
    "ANTHROPIC_BASE_URL", "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL",
    "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_VERTEX_PROJECT_ID",
    "ANTHROPIC_FOUNDRY_RESOURCE", "ANTHROPIC_BEDROCK_BASE_URL", "ANTHROPIC_VERTEX_BASE_URL", "ANTHROPIC_FOUNDRY_BASE_URL",
    "CLAUDE_CONFIG_DIR", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
)
# Claude Code's sign-in secrets: the preflight refuses a round with one set, since the session's own claude would
# need it in the environment its shell inherits. Claude Code signs in from its credentials file (claude /login), or
# a cloud's profile files, which the sandbox keeps from the shell.
CLAUDE_SECRETS = ("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_FOUNDRY_API_KEY",
                  "ANTHROPIC_CUSTOM_HEADERS")
# A cloud variable that is a secret, or names a file of one: refused by the preflight, left out by session_env.
SIGN_IN_SECRET = re.compile(r"TOKEN|SECRET|PASSW|API_?KEY|ACCESS_KEY|AUTH(?!OR)|CREDENTIAL|_KEY$|COOKIE|BEARER", re.I)
CLOUD_SIGN_IN = {"CLAUDE_CODE_USE_BEDROCK": "AWS_", "CLAUDE_CODE_USE_VERTEX": "GOOGLE_", "CLAUDE_CODE_USE_FOUNDRY": "AZURE_"}
# The values of a CLAUDE_CODE_USE_* flag that turn its cloud on; any other (0, false, empty) leaves it off.
FLAG_ON = ("1", "true", "yes", "on")
# git settings for the session, through GIT_CONFIG_COUNT: no credential helper, every push to GitHub sent to an
# address that doesn't resolve, and no automatic gc, which would rewrite packed-refs outside what the sandbox lets it
# write. Depth only: the sandbox and the missing credentials are what stop a push.
NO_PUSH = "https://push.invalid/"
SESSION_GIT_CONFIG = (
    ("credential.helper", ""),
    ("credential.https://github.com.helper", ""),
    (f"url.{NO_PUSH}.pushInsteadOf", "https://github.com/"),
    (f"url.{NO_PUSH}.pushInsteadOf", "git@github.com:"),
    (f"url.{NO_PUSH}.pushInsteadOf", "ssh://git@github.com/"),
    ("gc.auto", "0"),
)
# run_review's code for a session that broke one of the Rules the round checks after it.
SESSION_BROKE_A_RULE = 126
# A .security entry's status line, as security-record.mts reads it.
SECURITY_STATUS = re.compile(r"^- Status: (fixed|accepted|open)\s*$", re.M)


def short_name(repo):
    return repo.split("/")[-1]


def review_worktree(path, read=None):
    """The re-Apply worktree reapply.sh makes for the checkout at path, as reapply.sh names it: beside the primary
    checkout, which git's common folder names even when path is one of its linked worktrees (symlinks resolved). Where
    git can't say, the path itself is taken as the primary checkout."""
    common = (read or run_text)(["git", "-C", path, "rev-parse", "--path-format=absolute", "--git-common-dir"])
    primary = os.path.dirname(os.path.realpath(common)) if common else os.path.realpath(path)
    return os.path.join(os.path.dirname(primary), f"{os.path.basename(primary)}-worktrees", WORKTREE)


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


def preflight(which=shutil.which, run=run_quiet, template_global_json=TEMPLATE_GLOBAL_JSON, environ=None):
    """What would stop the round partway through, as a list of problems (empty when there's none)."""
    environ = os.environ if environ is None else environ
    problems = [f"{tool} isn't on PATH" for tool in TOOLS if which(tool) is None]
    problems += [f"{tool} isn't on PATH: the review session's sandbox needs it" for tool in SANDBOX_TOOLS if which(tool) is None]
    problems += ruff_problems(which, run)
    problems += sign_in_problems(environ)
    if SANDBOX_TOOLS and which("bwrap") is not None:
        # The sandbox needs unprivileged user namespaces, which a host can refuse (AppArmor's restriction on Ubuntu
        # 24.04, some containers): then the binaries are there and the first review session still can't start.
        code, _, error = run(["bwrap", "--unshare-all", "--ro-bind", "/", "/", "--dev", "/dev", "true"])
        if code != 0:
            problems.append(f"bubblewrap can't make the review session's sandbox here (exit {code}): {error.strip() or 'no answer'};"
                            " the host may refuse unprivileged user namespaces")
    if which("claude") is not None:
        code, _, error = run(["claude", "--version"])
        if code != 0:
            problems.append(f"claude --version failed (exit {code}): {error.strip() or 'no answer'}")
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
            problems.append(f"gh auth status failed (exit {code}): {error.strip() or 'no answer'}; if it says you're signed out:"
                            " gh auth login")
    if os.path.lexists(template_global_json):
        problems += sdk_problems(template_global_json, which, run)
    return problems


def kept_clouds(base):
    """The prefixes of the clouds Claude Code signs in through (CLAUDE_CODE_USE_* on), whose variables it needs."""
    return tuple(prefix for flag, prefix in CLOUD_SIGN_IN.items() if base.get(flag, "").strip().lower() in FLAG_ON)


def sign_in_problems(environ):
    """A sign-in secret in the environment the session would inherit: one of CLAUDE_SECRETS, or a secret of the cloud
    in use. Its shell would see it, so the round doesn't start."""
    clouds = kept_clouds(environ)
    found = sorted(name for name, value in environ.items() if value and (
        name in CLAUDE_SECRETS or (clouds and name.startswith(clouds) and SIGN_IN_SECRET.search(name))))
    return [f"{name} is set, and the review session's shell would see it: unset it for the round, and sign Claude Code in"
            " with its own credentials file (claude /login) or the cloud's profile files, which the sandbox keeps from"
            " the shell" for name in found]


def ruff_problems(which, run):
    """Whether the gate's Python lint can run, as the Template's gate decides: an installed ruff 0.17 release, or uvx
    to run the pinned one. A re-Apply that writes an Owned Python file needs it."""
    if which("uvx") is not None:
        return []
    if which("ruff") is None:
        return [f"neither ruff 0.17 nor uv is installed, and the gate's Python lint needs one: uv tool install ruff@{RUFF_VERSION}"]
    code, out, error = run(["ruff", "--version"])
    found = (out + error).strip()
    if code == 0 and RUFF_RELEASE.fullmatch(found):
        return []
    return [f"ruff is '{found or 'silent'}' (exit {code}), not a 0.17 release, and uv isn't installed for the gate to run"
            f" the pinned one: uv tool install ruff@{RUFF_VERSION}"]


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
    try:
        with tempfile.TemporaryDirectory(prefix="rollout-sdk.") as folder:
            shutil.copyfile(global_json, os.path.join(folder, "global.json"))
            code, _, error = run(["dotnet", "--version"], cwd=folder)
    except OSError as error:
        return [f"can't check the .NET SDK (no temporary folder to check it in): {error}"]
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


# The descriptor of the lock the running round holds (run sets it), which every child it starts is given: the lock is
# on the open file, so it is held until the last of them is gone, and a SIGKILL of rollout.sh, which leaves a session
# or a gate running, doesn't let a second --auto run in beside it.
LOCK_FDS = []


def held_lock_fds():
    """The lock's descriptors, for a child's pass_fds."""
    return tuple(LOCK_FDS)


def owned_changes(worktree, run=run_quiet, owned=OWNED):
    """The Owned files the worktree's branch changes against origin/main, for the review's prompt."""
    # --no-renames, as checkReview's own diff: a renamed Owned file is a change to the Owned path too.
    code, out, error = run(["git", "-C", worktree, "diff", "--no-renames", "--name-only", "-z", "origin/main...HEAD"])
    if code != 0:
        raise RuntimeError(f"git diff in {worktree} failed: {error.strip()}")
    return sorted(path for path in out.split("\0") if path and os.path.isfile(os.path.join(owned, path)))


def front_matter(text, path):
    """(front matter, body) of a file that opens with a `---` line and closes it with the next line that is `---`
    alone, or None when it opens with none. Raises ValueError when it is never closed."""
    lines = text.splitlines(keepends=True)
    if not lines or lines[0].rstrip("\r\n") != "---":
        return None
    for index, line in enumerate(lines[1:], start=1):
        if line.rstrip("\r\n") == "---":
            return "".join(lines[1:index]), "".join(lines[index + 1:])
    raise ValueError(f"{path}'s front matter is never closed")


def read_skill(path=SKILL_FILE):
    """The skill's instructions, its front matter left out."""
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    parts = front_matter(text, path)
    return parts[1].lstrip("\n") if parts else text


def agent_definition(path=AGENT_FILE):
    """The reviewer agent, for --agents: {name: {description, prompt, tools, model}} from its file's front matter (a
    flat `key: value` list, with `>-`, `>` or `|` folding the lines indented under it) and body."""
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    parts = front_matter(text, path)
    if parts is None:
        raise ValueError(f"{path} has no front matter")
    head, body = parts
    fields, key = {}, None
    for line in head.strip("\n").splitlines():
        if line.startswith((" ", "\t")) and key:
            fields[key] = (fields[key] + " " + line.strip()).strip()
        elif ":" in line:
            key, value = (part.strip() for part in line.split(":", 1))
            fields[key] = "" if value in (">-", ">", "|") else value
    for needed in ("name", "description", "tools"):
        if not fields.get(needed):
            raise ValueError(f"{path} has no {needed}")
    definition = {"description": fields["description"], "prompt": body.lstrip("\n"),
                  "tools": [tool.strip() for tool in fields["tools"].split(",") if tool.strip()]}
    if fields.get("model"):
        definition["model"] = fields["model"]
    return {fields["name"]: definition}


def expand_home(path, home):
    """path with a leading ~ replaced by home."""
    return home + path[1:] if path.startswith("~") else path


def session_writes(common, security, tmp):
    """All the session writes: the clone's review ledgers, the main checkout's .security record (scripts/review.sh
    record writes both) and its temporary folder. Not the worktree or anything else of the clone's git folder."""
    return [os.path.join(common, "review"), security, tmp]


def session_settings(worktree, common, gitdir, tmp, home=None, config_dir=None):
    """The session's settings (--settings, passed as JSON so no file of them exists for it to change): dontAsk
    permissions that allow only SESSION_TOOLS, reads of the worktree, the main checkout's .security and tmp, and edits
    in tmp alone, and deny the credentials, the Factory, ~/.claude and every edit in the worktree; and a sandbox for its
    Bash that can't be turned off, refuses to start without its sandbox (bubblewrap on Linux), reads none of
    CREDENTIAL_PATHS (nor Claude Code's CLAUDE_CONFIG_DIR, when one is set), writes only session_writes (never the
    worktree, which a sandbox otherwise lets its working folder be written, nor its git folder), and reaches only
    SANDBOX_DOMAINS. The main checkout is the git common folder's parent, as reapply.sh has it."""
    home = home or os.path.expanduser("~")
    config_dir = os.environ.get("CLAUDE_CONFIG_DIR") if config_dir is None else config_dir
    security = os.path.join(os.path.dirname(common), ".security")
    read_only = [worktree, gitdir]
    credentials = [expand_home(path, home) for path in CREDENTIAL_PATHS] + ([config_dir] if config_dir else [])
    return {
        "permissions": {
            "allow": [*SESSION_TOOLS, f"Read(/{worktree}/**)", f"Read(/{security}/**)", f"Read(/{tmp}/**)", f"Edit(/{tmp}/**)"],
            "deny": [*DENIED_TOOLS,
                     *(f"Read(/{path})" for path in credentials), *(f"Read(/{path}/**)" for path in credentials),
                     *(f"Edit(/{path})" for path in read_only), *(f"Edit(/{path}/**)" for path in read_only)],
        },
        "sandbox": {
            "enabled": True,
            "failIfUnavailable": True,
            "allowUnsandboxedCommands": False,
            "excludedCommands": [],
            "filesystem": {
                "allowWrite": session_writes(common, security, tmp),
                "denyWrite": read_only,
                "denyRead": credentials,
            },
            "network": {"allowedDomains": list(SANDBOX_DOMAINS)},
        },
    }


def review_command(repo, worktree, owned, settings, agents, skill):
    """The headless review's argv: its prompt carries the skill; it loads no settings file (--setting-sources ""), no
    MCP server and only SESSION_BUILTINS; and it gets the sandbox and permissions of settings, as JSON on the command
    line, and the reviewer agent from agents."""
    prompt = REVIEW_PROMPT.format(worktree=worktree, repo=repo, owned=", ".join(owned) or "none", skill=skill,
                                  skill_dir=os.path.join(worktree, ".claude", "skills", "pre-push-review"))
    return ["claude", "-p", prompt, "--permission-mode", "dontAsk", "--settings", json.dumps(settings),
            "--setting-sources", "", "--strict-mcp-config", "--tools", ",".join(SESSION_BUILTINS),
            "--agents", json.dumps(agents), "--disallowedTools", *DENIED_TOOLS]


def own_log_dir(log_dir):
    """Make the log folder, or check the one there: a real folder of the user's own, which is then set to 0700. Raises
    OSError naming what is wrong (a symlink, another owner's folder), since makedirs leaves an existing folder as it is."""
    os.makedirs(log_dir, mode=0o700, exist_ok=True)
    found = os.lstat(log_dir)
    if not stat.S_ISDIR(found.st_mode) or found.st_uid != os.getuid():
        raise OSError(f"{log_dir} isn't a folder of the user's own")
    os.chmod(log_dir, 0o700)


def session_env(base, tmp):
    """The review session's environment: base without GATE_SKIP_REVIEW (the gate's review check must run in it) or the
    names SECRET_NAME matches, Claude Code's own but CLAUDE_KEPT, and the cloud in use's secrets; gh pointed at an
    empty config; GIT_OPTIONAL_LOCKS off;
    SESSION_GIT_CONFIG; and every cache and temporary folder a build or install writes (NuGet, pnpm under both its
    old npm_config_ and its pnpm 12 pnpm_config_ name, XDG, dotnet's home, TMPDIR) moved into tmp, the one folder
    outside the worktree the sandbox lets it write."""
    clouds = kept_clouds(base)

    def kept(name):
        if name == "GATE_SKIP_REVIEW":
            return False
        if CLAUDE_SIGN_IN.match(name):
            return name in CLAUDE_KEPT
        if clouds and name.startswith(clouds):
            return not SIGN_IN_SECRET.search(name)
        return not SECRET_NAME.search(name)

    env = {name: value for name, value in base.items() if kept(name)}
    folders = {"GH_CONFIG_DIR": "gh", "TMPDIR": "tmp", "NUGET_PACKAGES": "nuget-packages",
               "NUGET_HTTP_CACHE_PATH": "nuget-http", "npm_config_store_dir": "pnpm-store",
               "pnpm_config_store_dir": "pnpm-store", "XDG_CACHE_HOME": "cache",
               "DOTNET_CLI_HOME": "dotnet-home"}
    for name, folder in folders.items():
        env[name] = os.path.join(tmp, folder)
        os.makedirs(env[name], exist_ok=True)
    env["DOTNET_NOLOGO"] = env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["GIT_OPTIONAL_LOCKS"] = "0"  # git status reads the index without writing it back: the worktree is read-only
    env["GIT_CONFIG_COUNT"] = str(len(SESSION_GIT_CONFIG))
    for index, (key, value) in enumerate(SESSION_GIT_CONFIG):
        env[f"GIT_CONFIG_KEY_{index}"], env[f"GIT_CONFIG_VALUE_{index}"] = key, value
    return env


def run_text(command, cwd=None):
    """A command's stdout, stripped, or None when it fails or can't run."""
    code, out, _ = run_quiet(command, cwd=cwd)
    return out.rstrip("\n") if code == 0 else None


def file_hash(path):
    """The sha256 of a file's bytes, or None when it can't be read."""
    try:
        with open(path, "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def tree_hashes(folder):
    """{path relative to folder: sha256} for every file under folder."""
    hashes = {}
    for root, _, files in os.walk(folder):
        for name in files:
            path = os.path.join(root, name)
            hashes[os.path.relpath(path, folder)] = file_hash(path)
    return hashes


def ledger_state(folder):
    """{ledger file name: (sha256, its accepted findings)} for each *.json in the review ledger folder, each accepted
    finding as (its id, the whole finding as canonical JSON), so one put in place under an id already accepted counts
    as new; a file that doesn't read as a ledger has (sha256, None)."""
    state = {}
    try:
        names = sorted(os.listdir(folder))
    except OSError:
        return state
    for name in names:
        if not name.endswith(".json"):
            continue
        path = os.path.join(folder, name)
        digest = file_hash(path)
        try:
            with open(path, encoding="utf-8") as handle:
                ledger = json.load(handle)
            findings = ledger.get("findings") if isinstance(ledger, dict) else None
            if not isinstance(findings, list):
                raise ValueError("no list of findings")
            accepted = frozenset((str(finding.get("id")), json.dumps(finding, sort_keys=True)) for finding in findings
                                 if isinstance(finding, dict) and finding.get("status") == "accepted")
            state[name] = (digest, accepted)
        except (OSError, ValueError, AttributeError, TypeError):
            state[name] = (digest, None)
    return state


def security_state(folder):
    """{file name: (sha256, its status or None)} for each file in the .security record."""
    state = {}
    try:
        names = sorted(os.listdir(folder))
    except OSError:
        return state
    for name in names:
        path = os.path.join(folder, name)
        if not os.path.isfile(path):
            continue
        try:
            with open(path, encoding="utf-8", errors="replace") as handle:
                found = SECURITY_STATUS.search(handle.read())
        except OSError:
            found = None
        state[name] = (file_hash(path), found.group(1) if found else None)
    return state


def link_text(worktree):
    """The bytes of the worktree's .git link (the file a linked worktree has in place of a git folder), or None when
    it isn't a plain file: a symlink, a folder or nothing is never the link git wrote."""
    path = os.path.join(worktree, ".git")
    try:
        if not stat.S_ISREG(os.lstat(path).st_mode):
            return None
        with open(path, "rb") as handle:
            return handle.read()
    except OSError:
        return None


class GuardUnreadable(Exception):
    """What a session may not change couldn't be read before it starts, so it isn't started."""


@dataclass(frozen=True)
class Guard:
    """What a review session may not change, read before it starts (session_guard), checked after (broken_rules)."""

    worktree: str
    remote_branch: str
    template: dict
    ledgers: str
    ledgers_before: dict
    common: str
    gitdir: str
    link: bytes
    security: str
    security_before: dict
    worktree_state: tuple


def remote_branch_command(worktree):
    """The read of the remote branch, before the session and after it: whether the session pushed."""
    return ["git", "-C", worktree, "ls-remote", "origin", f"refs/heads/{BRANCH}"]


def worktree_state_commands(worktree):
    """The reads of what the session leaves of the worktree: its HEAD, its branch and its status (untracked files
    included). The session reviews only, so all three are as they were before it."""
    return [["git", "-C", worktree, "rev-parse", "HEAD"],
            ["git", "-C", worktree, "rev-parse", "--verify", "--quiet", f"refs/heads/{BRANCH}"],
            ["git", "-C", worktree, "status", "--porcelain=v1", "-z", "--untracked-files=all"]]


def worktree_state(worktree, read):
    """(HEAD, branch, status) of the worktree, a None in place of a read that failed."""
    return tuple(read(command) for command in worktree_state_commands(worktree))


def session_guard(worktree, owned=OWNED, read=run_text):
    """Read what the session may not change: the worktree's .git link (git obeys the folder it names), the remote
    branch (the session doesn't push), the Template (its fixes go Template first, by a person), the clone's review
    ledgers (only the person accepts) and the main checkout's .security record. Raises GuardUnreadable when the link,
    the remote or the clone's git folders can't be read, since a check against nothing would pass whatever happened."""
    link = link_text(worktree)
    if link is None:
        raise GuardUnreadable(f"{worktree}/.git isn't the plain file a linked worktree has")
    remote = read(remote_branch_command(worktree))
    if remote is None:
        raise GuardUnreadable(f"can't read the remote {BRANCH} before the session (the network, or git)")
    common = read(["git", "-C", worktree, "rev-parse", "--path-format=absolute", "--git-common-dir"])
    gitdir = read(["git", "-C", worktree, "rev-parse", "--path-format=absolute", "--git-dir"])
    if not common or not gitdir:
        raise GuardUnreadable(f"can't find the git folders of {worktree} (git 2.31 or later reads them)")
    state = worktree_state(worktree, read)
    if None in state:
        raise GuardUnreadable(f"can't read the HEAD, the branch {BRANCH} or the status of {worktree}")
    ledgers = os.path.join(common, "review")
    security = os.path.join(os.path.dirname(common), ".security")
    return Guard(worktree_state=state, worktree=worktree, remote_branch=remote, template=tree_hashes(owned), ledgers=ledgers,
                 ledgers_before=ledger_state(ledgers), common=common, gitdir=gitdir, link=link, security=security,
                 security_before=security_state(security))


def broken_rules(guard, owned=OWNED, read=run_text, changes=owned_changes):
    """The Rules a finished session broke, as a list of what happened (empty when it broke none): the worktree's .git
    link changed (then no git command runs in the worktree, since git would obey whatever folder it now names), the
    remote branch moved or can't be read, the worktree's HEAD, branch or status changed, the Template changed, an Owned file the branch changes is no longer the
    Template's as it was when the session started, any ledger of the clone gained an accepted finding or stopped
    reading as one, or the .security record lost an entry, or one changed other than from open to fixed (which
    record writes), or gained one that is accepted. Every ledger counts, whatever branch or head it names, since the
    session writes those too: a person accepting something on another branch meanwhile stops the round as well."""
    broke = []
    link_kept = link_text(guard.worktree) == guard.link
    if not link_kept:
        broke.append(f"{guard.worktree}/.git changed during the session: git wasn't run there after it, and the link"
                     f" must be put back ({guard.link!r}) before it is")
    else:
        remote = read(remote_branch_command(guard.worktree))
        if remote is None:
            broke.append(f"can't read the remote {BRANCH} after the session, so whether it was pushed can't be told")
        elif remote != guard.remote_branch:
            broke.append(f"the remote {BRANCH} moved during the session (the session, or anyone who pushed to it meanwhile)")
        state = worktree_state(guard.worktree, read)
        if None in state:
            broke.append(f"can't read the HEAD, the branch {BRANCH} or the status of {guard.worktree} after the session")
        elif state != guard.worktree_state:
            broke.append(f"{guard.worktree} changed during the session (its HEAD, its branch or its files): the session"
                         " reviews only, so it commits and edits nothing")
    if tree_hashes(owned) != guard.template:
        broke.append("the Template changed during the session (the session, or your own edit in the Factory checkout)")
    if link_kept:
        try:
            changed = changes(guard.worktree)
        except (OSError, RuntimeError) as error:
            broke.append(f"can't list the Owned files the branch changes: {error}")
            changed = []
        for path in changed:
            if file_hash(os.path.join(guard.worktree, path)) != guard.template.get(path):
                broke.append(f"{path} is an Owned file and no longer the Template's: edit the Template, not the copy")
    for name, (digest, accepted) in ledger_state(guard.ledgers).items():
        before = guard.ledgers_before.get(name)
        if before is not None and before[0] == digest:
            continue  # unchanged since the session started
        path = os.path.join(guard.ledgers, name)
        if accepted is None:
            broke.append(f"{path} changed during the session and doesn't read as a ledger")
            continue
        gained = sorted({finding_id for finding_id, _ in accepted - ((before[1] or frozenset()) if before else frozenset())})
        if gained:
            broke.append(f"{path} gained {', '.join(gained)} accepted during the session: only the person accepts")
    broke += security_changes(guard.security, guard.security_before)
    return broke


def security_changes(folder, before):
    """What happened to the .security record that only a person may do: an entry removed, one changed other than from
    open to fixed, or a new one that is accepted."""
    after, broke = security_state(folder), []
    for name, (digest, status) in before.items():
        now = after.get(name)
        if now is None:
            broke.append(f"{os.path.join(folder, name)} was removed during the session")
        elif now[0] != digest and not (status == "open" and now[1] == "fixed"):
            broke.append(f"{os.path.join(folder, name)} changed during the session ({status} to {now[1]}): only a"
                         " review's record marks an open entry fixed")
    for name, (_, status) in after.items():
        if name not in before and status == "accepted":
            broke.append(f"{os.path.join(folder, name)} appeared during the session as accepted: only the person accepts")
    return broke


def held(signum):
    """Whether a signal is to be handled here: not one the caller ignores (nohup's SIGHUP, a background job's SIGINT)."""
    return signal.getsignal(signum) is not signal.SIG_IGN


@contextlib.contextmanager
def handling(signums, handler):
    """Install handler for each of signums the caller doesn't ignore, and put the old ones back after."""
    old = {signum: signal.signal(signum, handler) for signum in signums if held(signum)}
    try:
        yield
    finally:
        for signum, previous in old.items():
            signal.signal(signum, signal.SIG_DFL if previous is None else previous)


@contextlib.contextmanager
def round_signals():
    """For the whole round, SIGTERM and SIGHUP raise SystemExit (128 + the signal), as SIGINT raises an interrupt, so
    run_in_group kills reapply.sh's or land.sh's group and run() prints the resume command: by default either ends
    Python at once. An ignored one stays ignored."""
    def handler(signum, frame):
        raise SystemExit(128 + signum)

    with handling((signal.SIGTERM, signal.SIGHUP), handler):
        yield


@contextlib.contextmanager
def deferred_signals(signums=(signal.SIGINT, signal.SIGTERM, signal.SIGHUP)):
    """While the block runs, SIGINT, SIGTERM and SIGHUP are only recorded (the first one, in the list it yields), never
    raised: a signal can't land between starting the session and holding it, or in the middle of ending its group.
    The caller acts on it once the group is gone. An ignored one stays ignored."""
    got = []

    def handler(signum, frame):
        if not got:
            got.append(signum)

    with handling(signums, handler):
        yield got


def passed_on(signum):
    """The exception that passes a deferred signal on: an interrupt for SIGINT, an exit (128 + the signal) otherwise."""
    return KeyboardInterrupt() if signum == signal.SIGINT else SystemExit(128 + signum)


def group_alive(pgid, proc="/proc"):
    """Whether a process other than a zombie is left in the process group. A killed member stays a zombie until it is
    reaped (by init, once its parent is gone), which where nothing reaps orphans is never: it is gone all the same.
    Without /proc, whether the group exists at all."""
    if not os.path.isdir(proc):
        try:
            os.killpg(pgid, 0)
        except ProcessLookupError:
            return False
        return True
    for entry in os.listdir(proc):
        if not entry.isdigit():
            continue
        try:
            with open(os.path.join(proc, entry, "stat"), encoding="utf-8", errors="replace") as handle:
                fields = handle.read().rsplit(")", 1)[1].split()
        except (OSError, IndexError):
            continue
        if len(fields) > 2 and fields[2] == str(pgid) and fields[0] != "Z":
            return True
    return False


def end_group(session, wait_seconds=GROUP_WAIT_SECONDS, sleep=time.sleep, clock=time.monotonic, alive=group_alive):
    """Kill the session's whole process group, reap its leader, and wait until no live process is left in the group,
    for at most wait_seconds in all: whether it is gone. The group's id is the leader's pid, which can't be another
    group's while a member of this one is left, so a signal to it after the leader is reaped reaches only its members."""
    deadline = clock() + wait_seconds
    try:
        os.killpg(session.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass  # nothing is left in the group
    try:
        session.wait(timeout=wait_seconds)
    except subprocess.TimeoutExpired:
        return False  # the leader didn't die: stuck in the kernel
    while alive(session.pid):
        if clock() >= deadline:
            return False
        sleep(0.1)
    return True


def run_review(repo, worktree, popen=subprocess.Popen, changes=owned_changes, timeout=REVIEW_TIMEOUT_MINUTES * 60,
               log_dir=LOG_DIR, end=end_group, clock=time.monotonic, guard=session_guard, check=broken_rules,
               skill=read_skill, agents=agent_definition):
    """/pre-push-review headless in worktree: (exit code, the session's log, the Rules it broke). 124 when it runs past
    its time, 127 when it can't start, SESSION_BROKE_A_RULE when it broke one (they are listed, and in the log too).
    The session runs in Claude Code's sandbox (session_settings) with the skill and its agent from the Template, no
    settings file, no GitHub sign-in and a temporary folder of its own; it leads a process group of its own, which
    is killed whole whenever it ends; a SIGINT, SIGTERM or SIGHUP while it runs is held until the group is gone, then
    passed on. The log is 0600 (mkstemp) in a 0700 folder of the user's own."""
    try:
        own_log_dir(log_dir)
        handle, log = tempfile.mkstemp(prefix=f"review-{short_name(repo)}.", suffix=".log", dir=log_dir)
    except OSError as error:
        return 127, f"(no log: {error})", []
    with os.fdopen(handle, "w") as out, tempfile.TemporaryDirectory(prefix="rollout-session.") as tmp:
        def stop(text):
            out.write(f"rollout.sh: {text}\n")
            return 127, log, []

        if not os.path.isdir(worktree):
            return stop(f"the re-Apply worktree {worktree} doesn't exist")
        try:
            owned = changes(worktree)
            before = guard(worktree)
            os.makedirs(os.path.join(before.common, "review"), exist_ok=True)  # the sandbox lets it write only what exists
            settings = session_settings(worktree, before.common, before.gitdir, tmp)
            command = review_command(repo, worktree, owned, settings, agents(), skill())
            env = session_env(os.environ, tmp)
        except GuardUnreadable as error:
            return stop(f"{error}; the session wasn't started")
        except (OSError, RuntimeError, ValueError) as error:
            return stop(f"can't set the session up: {error}")
        out.flush()
        code, started = None, True
        with deferred_signals() as got:
            try:
                session = popen(command, cwd=worktree, env=env, stdin=subprocess.DEVNULL, stdout=out,
                                stderr=subprocess.STDOUT, start_new_session=True, pass_fds=held_lock_fds())
            except OSError as error:
                out.write(f"rollout.sh: can't start claude: {error}\n")
                started = False
            if started:
                try:
                    code = wait_for(session, timeout, got, clock)
                    if code is None and not got:
                        out.write(f"\nrollout.sh: the review session ran past {timeout // 60} minutes and was stopped\n")
                        code = 124
                finally:
                    if not end(session):
                        out.write(f"\nrollout.sh: the session's process group was still there {GROUP_WAIT_SECONDS} s after"
                                  " it was killed\n")
        if got:
            raise passed_on(got[0])
        if not started:
            return 127, log, []
        broke = check(before)
        for rule in broke:
            out.write(f"rollout.sh: the session broke a rule: {rule}\n")
        return (SESSION_BROKE_A_RULE if broke else code), log, broke


def wait_for(session, timeout, got, clock=time.monotonic):
    """The session's exit code, or None once timeout seconds pass or a deferred signal arrives (got holds it). It polls,
    since with the signals deferred nothing interrupts a wait."""
    deadline = clock() + timeout
    while True:
        try:
            return session.wait(timeout=max(0.0, min(0.5, deadline - clock())))
        except subprocess.TimeoutExpired:
            if got or clock() >= deadline:
                return None


def run_in_group(command, popen=subprocess.Popen, end=None, clock=time.monotonic):
    """A command, its output straight through, in a process group of its own: its exit code. A SIGTERM or SIGHUP that
    ends the round (round_signals) or an interrupt kills the whole group, so neither it nor anything it started (the
    gate a push runs) goes on working in a worktree after rollout.sh is gone. The signals are held from before it
    starts until its group is gone (a second one meanwhile is dropped), then the first is passed on, as for a review
    session; a command that can't start raises its OSError, after a signal that came meanwhile."""
    code, failed = None, None
    with deferred_signals() as got:
        try:
            child = popen(command, start_new_session=True, pass_fds=held_lock_fds())
        except OSError as error:
            failed = error
        else:
            try:
                code = wait_for(child, math.inf, got, clock)
            finally:
                if code is None:
                    (end or end_group)(child)
    if got:
        raise passed_on(got[0])
    if failed is not None:
        raise failed
    return code


def run_land(refs):
    """land.sh on the PRs; its exit code (0 once each has merged or closed)."""
    return run_in_group([LAND, *refs])


def run_reapply(brings, path):
    """reapply.sh on one checkout; its exit code."""
    return run_in_group([REAPPLY, "--brings", brings, path])


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
            return Outcome("person", f"review findings wait for you (cd {shlex.quote(worktree)} && scripts/review.sh report):"
                           " fix them with /pre-push-review there (one in an Owned file goes to the Factory's template/owned/"
                           f" first: add that PR to --brings), or accept them: cd {shlex.quote(worktree)} &&"
                           " scripts/review.sh accept-open")
        if code != REVIEW_NEEDED:
            return Outcome("failed", f"reapply.sh exited {code}; retry it with: {retry}")
        if reviews == MAX_REVIEWS:
            return Outcome("failed", f"the gate still asks for a review after {reviews} review session(s): main or the Template may have"
                           f" moved since the last one, or the session stopped short in its sandbox (no GitHub sign-in, few hosts;"
                           f" see {log}); run /pre-push-review in {shlex.quote(worktree)}, then resume")
        reviews += 1
        say(f"── rollout: {short_name(repo)}: the gate asks for a review; running /pre-push-review headless in {worktree}")
        code, log, broke = review(repo, worktree)
        say(f"── rollout: {short_name(repo)}: review session exited {code}; log: {log}")
        if broke:
            return Outcome("person", f"the review session broke a rule: {broke[0]}"
                           + (f" (and {len(broke) - 1} more)" if len(broke) > 1 else "") + f"; see {log}, put it right in"
                           f" {shlex.quote(worktree)}, then resume")
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
        """A crash still ends with a stop: the traceback goes to stderr, so a bug can be told from the world. It names
        the repo's reapply.sh to run by hand, where its checkout can be told."""
        err.write(f"rollout.sh: {short_name(repo)} crashed:\n{traceback.format_exc()}")
        try:
            retry = f"; run it by hand with: reapply.sh --brings {shlex.quote(brings)} {shlex.quote(checkout(repo))}"
        except Exception:  # noqa: BLE001 - the stop is still printed, without the command
            retry = ""
        return f"{short_name(repo)}: {type(error).__name__}: {error} (traceback on stderr){retry}"

    def not_merged(repo, number, state):
        """The stop for a PR that reads as neither OPEN nor MERGED (closed unmerged): how to land it, or start afresh."""
        return (f"{short_name(repo)}#{number} is {state}, not merged: reopen it (gh pr reopen {number} -R {repo}) and land it"
                f" with land.sh {repo}#{number}, or delete its branch and resume for a new PR")

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
            return held(not_merged(first, number, state))
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
            stops.append(not_merged(repo, number, state))
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
                stops.append(not_merged(repo, number, state))
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
        err.write(f"rollout.sh: another rollout.sh --auto holds {LOCK}, or a review session or gate an earlier one"
                  " started is still running; nothing run\n")
        return 1
    try:
        LOCK_FDS[:] = [handle.fileno()]
        with round_signals():
            return auto_round(order, brings, resume, checkout, reapply, review, land, pr_of, pr_state, branch_of, say,
                              err, sleep)
    except (KeyboardInterrupt, SystemExit) as stop:
        # An interrupt, or a SIGTERM or SIGHUP held through a review session (deferred_signals): its group is gone.
        code = stop.code if isinstance(stop, SystemExit) and isinstance(stop.code, int) else 130
        err.write(f"rollout.sh: interrupted; resume with: {resume}\n")
        return code
    finally:
        LOCK_FDS.clear()
        handle.close()  # this process's hold on the lock ends here, and not before
