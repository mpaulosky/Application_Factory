"""rollout.sh --auto: a re-Apply round driven to the end with no person between the steps (ADR 0016).

    rollout.sh --auto --brings <prs> [--root dir] [--dry-run]

1. It checks what would stop the round partway through: a tool missing from
   PATH, an shfmt the gate's gh check refuses, neither ruff 0.17 nor uv for the
   gate's Python lint, a claude that doesn't run or isn't signed in (as it is
   with no settings file; a cloud's sign-in isn't asked), a sandbox bubblewrap can't
   make (on Linux), gh signed out, and the .NET SDK the Template's global.json
   names (an Owned file, so Apply writes it into every re-Apply worktree and the
   gate builds with it), and a sign-in secret in the environment the review
   session would inherit. One problem stops the round before anything runs. A
   second --auto run while the lock is held stops too, since both would work
   the same worktrees and review ledgers; every child the round starts holds
   the lock as well, so one a SIGKILL of rollout.sh leaves running keeps it,
   and its process group is killed when it exits, with no build server left
   (NO_BUILD_SERVERS), so nothing it leaves keeps the lock after the round.
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
ADR 0016). It runs one review-only round and stages what it finds in a run
folder of its own, open, or dismissed when it isn't a security finding; once it
ends, the round records that with the worktree's scripts/review.sh record
(record_review), so the session writes no ledger or .security entry itself. It
edits, fixes and commits nothing, and runs no gate, so nothing it wrote runs
later as the person. Its Bash can't read the person's credentials
(CREDENTIAL_PATHS, ~/.claude among them, and where CREDENTIAL_VARS move them),
writes only a temporary folder of its own (session_writes: never the worktree,
its git folder, the ledgers or .security), reaches no host (SANDBOX_DOMAINS is
empty), and can't leave the sandbox; the session refuses to start without the
sandbox (bubblewrap and socat on Linux, which the preflight checks for). Its file tools, which run outside the sandbox, may read only the
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
remote branch moved (or can't be read), the clone's git config or hooks
changed (git_files_state), the worktree's HEAD, branch or status changed, the
Template changed, an Owned file is no longer the Template's, a file of the
ledgers or of .security was added, removed or changed, any path of the worktree
changed in content, mode or link target (tree_state: node_modules too, which
git status doesn't show; read again just before the record), the session's
group outlived its kill, or it staged a finding fixed or accepted, or a security
one dismissed (only the person decides those), stops the round for the person,
and nothing is recorded. A file it didn't read (a symlink, a FIFO) is compared
by what lstat says of it. A process that left the session's group (setsid) is
past what these checks cover, as is a filesystem a sandbox that didn't hold let
the session reach outside the worktree.
It runs in a process group of its own, killed whole however it ends (it exits,
or runs past its time); an interrupt, a SIGTERM or a SIGHUP while it runs is
held until the group is gone, then passed on, and a SIGKILL of rollout.sh can't
be caught, and leaves the session running (holding the lock until it ends).
reapply.sh and land.sh run in groups of their own too, held the same way, which
a SIGTERM, SIGHUP or interrupt of the round kills; one whose group is still
there after the kill stops the round (GroupLeft). The round's own
scripts/review.sh prepare and record run in groups of their own as well
(run_bounded), for at most RECORD_TIMEOUT_SECONDS each; an interrupt stops
prepare, and waits for record to end, and signals are held from the end of the
session until what it staged is recorded or kept. That is kept beside the log
(staged_copy) with the HEAD it reviewed: a record that failed, a group record
left, or an interrupt prints the command that records it, at that HEAD only. A
broken rule (HEAD moved, a changed file) keeps nothing, since the findings vouch
for code that is no longer there. Its log is kept, 0600, in a 0700 folder of the
user's own, ~/.cache/repo-ci-baseline/logs: it can hold the session's report of
security findings.

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
RUFF_INSTALL = (f"install uv (https://docs.astral.sh/uv/), or put a ruff 0.17 first on PATH (uv tool install"
                f" ruff@{RUFF_VERSION} once uv is there)")
# How the preflight asks whether claude is signed in: with no settings file, as the review session runs (this form,
# a root option before the subcommand, answers with its JSON in Claude Code 2.1.326).
CLAUDE_AUTH_STATUS = ("claude", "--setting-sources", "", "auth", "status", "--json")
# The preflight's try of the review session's sandbox, as the person can run it again.
BWRAP_PROBE = ("bwrap", "--unshare-all", "--ro-bind", "/", "/", "--dev", "/dev", "true")
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
    # Files a secret is often exported or kept in: the shells' start-up files, git's own config (the session reads an
    # empty one: GIT_CONFIG_GLOBAL), .NET's user secrets, and other tools' sign-ins.
    "~/.bashrc", "~/.bash_profile", "~/.bash_login", "~/.profile", "~/.zshrc", "~/.zshenv", "~/.zprofile",
    "~/.zlogin", "~/.config/fish", "~/.gitconfig", "~/.config/git", "~/.microsoft/usersecrets", "~/.aspnet",
    "~/.config/rclone", "~/.s3cfg", "~/.boto", "~/.oci", "~/.config/doctl", "~/.gradle/gradle.properties",
)
# The variables that move one of those credentials elsewhere: what each names is kept from the session too. A colon
# separated list (KUBECONFIG) names each of its files; XDG_CONFIG_HOME, where set, holds XDG_CONFIG_SUBFOLDERS.
CREDENTIAL_VARS = ("GH_CONFIG_DIR", "DOCKER_CONFIG", "KUBECONFIG", "AWS_SHARED_CREDENTIALS_FILE", "AWS_CONFIG_FILE",
                   "NPM_CONFIG_USERCONFIG", "npm_config_userconfig", "GNUPGHOME", "CLOUDSDK_CONFIG", "AZURE_CONFIG_DIR",
                   "PASSWORD_STORE_DIR", "GIT_CONFIG_GLOBAL")
XDG_CONFIG_SUBFOLDERS = ("gh", "glab-cli", "hub", "git", "pnpm", "pip", "uv", "NuGet", "op", "gcloud", "rclone",
                         "doctl", "fish")
# The hosts the session's Bash may reach: none. It builds and installs nothing (it runs no gate), and git can't fetch
# in it (the clone's git folder is read-only to it, so a fetch has nowhere to write); reapply.sh has just fetched.
SANDBOX_DOMAINS = ()
# What the sandbox runs on Linux (bubblewrap and socat); failIfUnavailable makes the session refuse to start without.
SANDBOX_TOOLS = ("bwrap", "socat") if sys.platform.startswith("linux") else ()
# The headless review's instructions, then the skill's own text from the Template. Nobody answers, the Owned files'
# fixes belong in the Template, and pushing, posting and deciding stay with the round and the person.
REVIEW_PROMPT = """You are running the pre-push-review skill, unattended, for rollout.sh --auto, in {worktree}, the re-Apply worktree of {repo}. The skill's instructions are below, from the Factory's Template; its base directory is {skill_dir}, the worktree's copy of the same files. Follow them, with these changes.
Nobody will answer a question: never ask one or wait for a reply; where the skill says to ask the person or show them text first, don't, and say so in your last message.
Review only: this session changes nothing in the repository. Don't edit, fix or commit anything, and don't run the gate (reapply.sh runs it on the push). Run one round over the whole branch, as the skill's last, review-only round: stage open every finding the skill's triage doesn't dismiss, with its suggested fix, and the person fixes or accepts them; a finding the triage dismisses, with the skill's reason, is staged dismissed only when it isn't a security one. Never dismiss a security finding (one the skill's triage marks security): stage it open, a repeat of a fixed .security entry too, with that entry's number in the summary. Leave out every finding's repeats field, whatever entry it would name (fixed, open or accepted): rollout.sh drops it, and the record matches a finding to an entry by its path and summary.
Your run folder is {run}, made for you: use it where the skill says to make one with mktemp -d, and write nothing anywhere else. The worktree, the clone's git folder, its review ledgers and the .security record are read-only to you.
Stage the round's findings with scripts/review.sh stage {run} <file> as the skill's step 5 says, as an empty array [] when it keeps none, and stop there: don't run scripts/review.sh record. rollout.sh records what you staged once the session ends, with the key and HEAD of this code.
These files the branch changes are Owned Baseline files, which the next Apply overwrites: {owned}. A finding in one says, in its fix, what to change in the Factory's template/owned/.
Don't push, don't file the follow-up issue, and don't edit a PR.
Your shell runs in a sandbox with no network and no GitHub sign-in: skip the skill's GitHub reads (the PR's threads, the issue) and its git fetch, and say you did. reapply.sh has just fetched origin, so go on with origin/main as it is.
End with a list of the findings you staged: each one's id, severity, path and line.

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
# A cloud variable that is a secret, or names a file of one (a certificate's or private key's path, a key file):
# refused by the preflight, left out by session_env.
SIGN_IN_SECRET = re.compile(
    r"TOKEN|SECRET|PASSW|API_?KEY|ACCESS_KEY|AUTH(?!OR)|CREDENTIAL|_KEY$|COOKIE|BEARER|CERT|PRIVATE|KEY_?FILE", re.I)
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
# run_review's codes for a session whose staged findings the round couldn't record (scripts/review.sh prepare or
# record refused, ran past RECORD_TIMEOUT_SECONDS or couldn't run), and for one that exited 0 having staged nothing,
# not even []: above every exit code, so a session's own exit can't read as either.
RECORD_FAILED = 256
NOTHING_STAGED = 257
# The round's record left a process group behind, so whether the review was recorded can't be told.
RECORD_LEFT = 258
# How long scripts/review.sh prepare and record may each run when the round records a review (tsx is installed by
# then: the round refuses to record without it, so neither installs packages into the worktree it has just checked).
RECORD_TIMEOUT_SECONDS = 600
# The most of a text the session wrote (an id, a status, a file name) that a message prints.
SHOWN_CHARS = 120
# How many of the worktree's changed paths a stop names (the log has them too, in the same message).
UNSEEN_SHOWN = 3
# The run folder's file the session stages its findings in (review-ledger.mts's STAGED_FILE; a test holds them equal).
STAGED_FILE = "findings.json"
# Beside the kept findings: the HEAD the session reviewed, which the command to record them checks.
KEPT_HEAD_FILE = "HEAD"
# The statuses a review-only round stages: a finding it keeps open, or one it dismissed that isn't a security one.
STAGED_STATUSES = ("open", "dismissed")
# The agent the session's reviewers run as (agent_definition), recorded as the review's reviewerAgent.
REVIEWER_AGENT = "pre-push-reviewer"
# The most the staged findings may hold, a hundred times a large review: the session writes them, so a bigger file
# isn't read.
RECORD_BYTES = 16 * 1024 * 1024
# What the commands run_in_group starts (reapply.sh, with the gate its push runs, and land.sh) are told, so the gate's
# build leaves no server running after it: one would keep the round's lock (its descriptor is passed on) after the
# round ends. The review session has an environment of its own (session_env): it builds nothing.
NO_BUILD_SERVERS = {"MSBUILDDISABLENODEREUSE": "1", "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "UseSharedCompilation": "false"}


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
        code, _, error = run(list(BWRAP_PROBE))
        if code != 0:
            problems.append(f"bubblewrap can't make the review session's sandbox here (exit {code}): {error.strip() or 'no answer'};"
                            f" try it with: {shlex.join(BWRAP_PROBE)}. The host may refuse unprivileged user namespaces"
                            " (Ubuntu's sysctl kernel.apparmor_restrict_unprivileged_userns, kernel.unprivileged_userns_clone,"
                            " a container without them); where it can't allow them, run rollout.sh without --auto and the"
                            " review by hand")
    if which("claude") is not None:
        code, _, error = run(["claude", "--version"])
        if code != 0:
            problems.append(f"claude --version failed (exit {code}): {error.strip() or 'no answer'}")
        else:
            problems += claude_sign_in_problems(run, environ)
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


def claude_sign_in_problems(run, environ):
    """Whether claude is signed in as the session will be: CLAUDE_AUTH_STATUS, which reads no settings file as the
    session reads none (so a sign-in only a settings file's apiKeyHelper gives doesn't count), must say loggedIn. Only
    its loggedIn is read: the rest of what it prints, the account, is never shown, nor is its stderr. With a cloud's
    flag on (kept_clouds), Claude Code signs in through the cloud's profile, which it doesn't report, so nothing is
    asked: the first session is where a cloud sign-in that fails shows."""
    if kept_clouds(environ):
        return []
    code, out, _ = run(list(CLAUDE_AUTH_STATUS))
    command = shlex.join(CLAUDE_AUTH_STATUS)
    if code != 0:
        return [f"{command} failed (exit {code}), so whether claude is signed in can't be told: run it to see why, and"
                " sign claude in with claude /login if it says you're signed out"]
    try:
        status = json.loads(out)
    except ValueError:
        status = None
    if not isinstance(status, dict) or "loggedIn" not in status:
        return [f"{command} doesn't say whether claude is signed in (no loggedIn in what it prints): update claude, or"
                " run rollout.sh without --auto"]
    if status["loggedIn"] is not True:
        return [f"claude isn't signed in, as {command} says: sign it in with claude /login (the review session reads no"
                " settings file, so an apiKeyHelper in one isn't used)"]
    return []


def ruff_problems(which, run):
    """Whether the gate's Python lint can run, as the Template's gate decides: an installed ruff 0.17 release, or uvx
    to run the pinned one. A re-Apply that writes an Owned Python file needs it."""
    if which("uvx") is not None:
        return []
    if which("ruff") is None:
        return [f"neither ruff 0.17 nor uv is installed, and the gate's Python lint needs one: {RUFF_INSTALL}"]
    code, out, error = run(["ruff", "--version"])
    found = (out + error).strip()
    if code == 0 and RUFF_RELEASE.fullmatch(found):
        return []
    return [f"ruff is '{found or 'silent'}' (exit {code}), not a 0.17 release, and uv isn't installed for the gate to run"
            f" the pinned one: {RUFF_INSTALL}"]


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
# or a gate running, doesn't let a second --auto run in beside it. Each child's group is killed when it exits
# (run_in_group, run_review), so what it leaves behind doesn't keep the lock after the round.
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
    """path with a leading ~ (alone, or before a /) replaced by home; ~user and anything else as it is."""
    return home + path[1:] if path == "~" or path.startswith("~/") else path


def session_writes(tmp):
    """All the session writes: its temporary folder, the run folder it stages its findings in among it. Not the
    worktree, the clone's git folder (its review ledgers among it) or the main checkout's .security record: the round
    records the review (record_review), not the session."""
    return [tmp]


def absolute(path, home):
    """path as an absolute path with no trailing slash: a leading ~ or ~/ is home, and a relative one is taken from
    rollout.sh's folder (a program in the session, whose folder is the worktree, would take it from there, which the
    session may read anyway). None for an empty or blank one."""
    path = (path or "").strip()
    if not path:
        return None
    return os.path.normpath(os.path.abspath(expand_home(path, home)))


def both_forms(path):
    """path and, when a symlink is on the way, where it leads: a rule naming one form only may miss the other."""
    return sorted({path, os.path.realpath(path)})


def claude_config_dir(environ, home):
    """Claude Code's config folder when CLAUDE_CONFIG_DIR moves it, as an absolute path, or None."""
    return absolute(environ.get("CLAUDE_CONFIG_DIR"), home)


def relocated_credentials(environ, home):
    """Where CREDENTIAL_VARS move a credential to, and XDG_CONFIG_HOME's credential folders, each as an absolute path."""
    found = []
    for name in CREDENTIAL_VARS:
        found += [absolute(part, home) for part in environ.get(name, "").split(os.pathsep)]
    config = absolute(environ.get("XDG_CONFIG_HOME"), home)
    if config:
        found += [os.path.join(config, folder) for folder in XDG_CONFIG_SUBFOLDERS]
    return [path for path in found if path]


def session_settings(worktree, common, gitdir, tmp, home=None, config_dir=None, environ=None):
    """The session's settings (--settings, passed as JSON so no file of them exists for it to change): dontAsk
    permissions that allow only SESSION_TOOLS, reads of the worktree, the main checkout's .security and tmp, and edits
    in tmp alone, and deny the credentials, the Factory, ~/.claude and every edit in the worktree; and a sandbox for its
    Bash that can't be turned off, refuses to start without its sandbox (bubblewrap on Linux), reads none of
    CREDENTIAL_PATHS (nor Claude Code's CLAUDE_CONFIG_DIR, nor where environ's CREDENTIAL_VARS move a credential), each
    as an absolute path and where its symlinks lead (a dotfiles repo's links), writes only session_writes (never the
    worktree, which a sandbox otherwise lets its working folder be written, nor its git folder), and reaches only SANDBOX_DOMAINS (none). The
    main checkout is the git common folder's parent, as reapply.sh has it."""
    home = home or os.path.expanduser("~")
    environ = os.environ if environ is None else environ
    config_dir = claude_config_dir(environ, home) if config_dir is None else absolute(config_dir, home)
    security = os.path.join(os.path.dirname(common), ".security")
    read_only = [worktree, gitdir]
    moved = ([config_dir] if config_dir else []) + relocated_credentials(environ, home)
    credentials = list(dict.fromkeys(form for path in [expand_home(path, home) for path in CREDENTIAL_PATHS] + moved
                                     for form in both_forms(path)))
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
                "allowWrite": session_writes(tmp),
                "denyWrite": read_only,
                "denyRead": credentials,
            },
            "network": {"allowedDomains": list(SANDBOX_DOMAINS)},
        },
    }


def review_command(repo, worktree, owned, settings, agents, skill, run):
    """The headless review's argv: its prompt carries the skill and names run, the folder it stages in; it loads no settings file (--setting-sources ""), no
    MCP server and only SESSION_BUILTINS; and it gets the sandbox and permissions of settings, as JSON on the command
    line, and the reviewer agent from agents."""
    prompt = REVIEW_PROMPT.format(worktree=worktree, repo=repo, owned=", ".join(owned) or "none", skill=skill, run=run,
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
    names SECRET_NAME matches, Claude Code's own but CLAUDE_KEPT (CLAUDE_CONFIG_DIR made absolute, as the settings
    deny it), and the cloud in use's secrets; gh pointed at an empty config and git at an empty global one; and
    SESSION_GIT_CONFIG. The worktree and the clone's git folder are read-only to it, so GIT_OPTIONAL_LOCKS is off (git
    status writes no index back) and gc.auto is 0. The session builds and installs nothing, but the caches and
    temporary folders the tools it runs may write (git, gh, node, Python; NuGet, pnpm under both its old npm_config_
    and its pnpm 12 pnpm_config_ name, XDG, dotnet's home, TMPDIR) are moved into tmp, its own folder, so none of them
    writes the person's home."""
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
    config_dir = claude_config_dir(base, os.path.expanduser("~"))
    if config_dir:
        env["CLAUDE_CONFIG_DIR"] = config_dir
    env["GIT_CONFIG_GLOBAL"] = os.path.join(tmp, "gitconfig")  # the person's ~/.gitconfig is kept from the session
    with open(env["GIT_CONFIG_GLOBAL"], "w", encoding="utf-8"):
        pass
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


def record_bytes(path, limit=None):
    """The bytes of a file the session may have written, or None unless it is a regular file of at most limit bytes
    (RECORD_BYTES by default): it is opened without following a symlink and without waiting (a FIFO), and nothing
    else (a device, a folder) is read."""
    limit = RECORD_BYTES if limit is None else limit
    try:
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    except OSError:
        return None
    try:
        if not stat.S_ISREG(os.fstat(fd).st_mode):
            return None
        chunks, size = [], 0
        while size <= limit:
            chunk = os.read(fd, limit + 1 - size)
            if not chunk:
                break
            chunks.append(chunk)
            size += len(chunk)
    except OSError:
        return None
    finally:
        os.close(fd)
    return b"".join(chunks) if size <= limit else None


def identity(path, data):
    """What a file is, to tell whether it changed: the sha256 of the bytes record_bytes read, or, for one it didn't
    read (a symlink, a FIFO, one too big), what lstat says of it and where a symlink leads, so swapping one such file
    for another still reads as a change."""
    if data is not None:
        return hashlib.sha256(data).hexdigest()
    try:
        info = os.lstat(path)
        target = os.readlink(path) if stat.S_ISLNK(info.st_mode) else ""
    except OSError:
        return "gone"
    return f"unread {info.st_mode:o} {info.st_ino} {info.st_size} {info.st_mtime_ns} {target}"


def folder_state(folder):
    """{name: identity} for each file in a folder the session may not write (the clone's review ledgers, the main
    checkout's .security record), each read by record_bytes; empty when the folder isn't there or can't be listed."""
    try:
        names = sorted(os.listdir(folder))
    except OSError:
        return {}
    state = {}
    for name in names:
        path = os.path.join(folder, name)
        state[name] = identity(path, record_bytes(path))
    return state


def shown(text):
    """Text the session wrote (a finding's id or status, a file name), as a message may print it: quoted, with every
    control character and anything not ASCII escaped, so it can't move the cursor or fake a line of its own, and cut to
    SHOWN_CHARS, so it can't flood the terminal."""
    text = ascii(text)
    return text if len(text) <= SHOWN_CHARS else f"{text[:SHOWN_CHARS]}... ({len(text)} characters)"


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
    files: dict
    git_files: dict


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
    branch (the session doesn't push), the Template (its fixes go Template first, by a person), and the clone's review
    ledgers and the main checkout's .security record (folder_state: the round records, not the session), every path
    of the worktree (tree_state: content, mode and link target, ignored files included) and the clone's git config,
    config.worktree, hooks and info (git_files_state). broken_rules compares them after the session, and record_review
    reads the worktree again just before record. Raises GuardUnreadable when the link, the remote or the clone's git
    folders can't be read, since a check against nothing would pass whatever happened."""
    link = link_text(worktree)
    if link is None:
        raise GuardUnreadable(f"{worktree}/.git isn't the plain file a linked worktree has (see: ls -la"
                              f" {shlex.quote(os.path.join(worktree, '.git'))})")
    remote = read(remote_branch_command(worktree))
    if remote is None:
        raise GuardUnreadable(f"can't read the remote {BRANCH} before the session (the network, or git); see why with:"
                              f" {shlex.join(remote_branch_command(worktree))}")
    folders = [["git", "-C", worktree, "rev-parse", "--path-format=absolute", which] for which in ("--git-common-dir", "--git-dir")]
    common, gitdir = (read(command) for command in folders)
    if not common or not gitdir:
        failed = folders[0] if not common else folders[1]
        raise GuardUnreadable(f"can't find the git folders of {worktree} (git 2.31 or later reads them); see why with:"
                              f" {shlex.join(failed)}")
    state = worktree_state(worktree, read)
    if None in state:
        failed = next(command for command, value in zip(worktree_state_commands(worktree), state, strict=True)
                      if value is None)
        # --quiet keeps the read's failure silent; the hint runs it without, so it says why.
        raise GuardUnreadable(f"can't read the HEAD, the branch {BRANCH} or the status of {worktree}; see why with:"
                              f" {shlex.join(part for part in failed if part != '--quiet')}")
    ledgers = os.path.join(common, "review")
    security = os.path.join(os.path.dirname(common), ".security")
    return Guard(worktree_state=state, worktree=worktree, remote_branch=remote, template=tree_hashes(owned), ledgers=ledgers,
                 ledgers_before=folder_state(ledgers), common=common, gitdir=gitdir, link=link, security=security,
                 security_before=folder_state(security), files=tree_state(worktree),
                 git_files=git_files_state(common, gitdir))


def broken_rules(guard, owned=OWNED, read=run_text, changes=owned_changes):
    """The Rules a finished session broke, as a list of what happened (empty when it broke none): the worktree's .git
    link changed (then no git command runs in the worktree, since git would obey whatever folder it now names), the
    remote branch moved or can't be read, the worktree's HEAD, branch or status changed, the Template changed, an Owned file the branch changes is no longer the
    Template's as it was when the session started, or a file of the ledgers or of the .security record was added,
    removed or changed (folder_changes): the session writes neither, so a person's own review on another branch
    meanwhile stops the round as well. A change in the clone's git config or hooks (git_files_state) breaks one, and
    then no git runs; and last, any path of the worktree whose content, mode or link target changed (tree_state: an
    ignored file git status doesn't show, such as node_modules/.bin/tsx, which the record runs) breaks one too."""
    broke = []
    link_kept = link_text(guard.worktree) == guard.link
    git_now = git_files_state(guard.common, guard.gitdir)
    # git_files_state reads the same names each time; one the guard lacks counts as changed.
    git_changed = [name for name in git_now if git_now[name] != guard.git_files.get(name)]
    if not link_kept:
        broke.append(f"{guard.worktree}/.git changed during the session: git wasn't run there after it, and the link"
                     f" must be put back ({guard.link!r}) before it is")
    elif git_changed:
        broke.append(f"the clone's git {', '.join(git_changed)} changed during the session (in {guard.common}): git can"
                     " run a command they name, so it wasn't run there after the session; see what changed with"
                     f" ls -la {shlex.quote(os.path.join(guard.common, 'hooks'))} and cat"
                     f" {shlex.quote(os.path.join(guard.common, 'config'))}")
        link_kept = False
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
    broke += folder_changes(guard.ledgers, guard.ledgers_before, "the review ledgers")
    broke += folder_changes(guard.security, guard.security_before, "the .security record")
    # Last, so the files are read as near as can be to the record, which runs them (record_review reads them again).
    broke += unseen_changes(guard)
    return broke


def unseen_changes(guard):
    """The Rule broken when a path of the worktree changed since the guard read it (tree_state: content, mode or link
    target, so an ignored file git status doesn't show, such as node_modules/.bin/tsx, counts): one rule naming the
    first UNSEEN_SHOWN of them, relative to the worktree, and how many there are; [] when none did."""
    touched = tree_changes(guard.files, tree_state(guard.worktree))
    if not touched:
        return []
    named = ", ".join(shown(path) for path in touched[:UNSEEN_SHOWN])
    more = f" and {len(touched) - UNSEEN_SHOWN} more" if len(touched) > UNSEEN_SHOWN else ""
    return [f"{len(touched)} path(s) in {guard.worktree} changed during the session, which git status may not show"
            f" (an ignored file, such as node_modules): {named}{more}. The round's record and the gate on the push run"
            " the worktree's files as you, so nothing is run or recorded"]


def folder_changes(folder, before, what):
    """What changed in a folder the session may not write (folder_state before it, and now): every file added,
    removed or changed. The session's sandbox writes only its own temporary folder, so a change is the person's own
    review or record on another branch meanwhile, or a sandbox that didn't hold."""
    after, broke = folder_state(folder), []
    for name in sorted(set(before) | set(after)):
        if before.get(name) != after.get(name):
            how = "added" if name not in before else "removed" if name not in after else "changed"
            broke.append(f"{shown(name)} in {folder} was {how} during the session, which can't write {what}: your own"
                         " review or record on another branch meanwhile, or a sandbox that didn't hold")
    return broke


def entry_state(path):
    """What one path is, read without following a symlink, to tell whether it changed: a folder's mode, a regular
    file's mode and the sha256 of its bytes (read without waiting on it), a symlink's target, and of anything else (a
    FIFO, a socket) or a file that can't be read, what lstat says of its kind and mode. Times and link counts are
    left out, so another project's pnpm install, which hard-links the same store files, changes nothing here; None
    when the path isn't there."""
    try:
        info = os.lstat(path)
    except OSError:
        return None
    if stat.S_ISLNK(info.st_mode):
        try:
            return ("link", os.readlink(path))
        except OSError:
            return ("link", None)
    if stat.S_ISDIR(info.st_mode):
        return ("dir", info.st_mode)
    if stat.S_ISREG(info.st_mode):
        try:
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        except OSError:
            return unread(info)
        try:
            digest = hashlib.sha256()
            while chunk := os.read(fd, 1 << 20):
                digest.update(chunk)
            return ("file", info.st_mode, digest.hexdigest())
        except OSError:
            return unread(info)
        finally:
            os.close(fd)
    return ("other", info.st_mode)


def unread(info):
    """What entry_state says of a regular file it can't read (one only its owner may run, mode 0111, say): its mode,
    size, inode and mtime, so a same-size file swapped in, which the round's scripts could still run, counts as a
    change. Only these files are judged by times, so pnpm's hard links elsewhere still change nothing."""
    return ("unread", info.st_mode, info.st_size, info.st_ino, info.st_mtime_ns)


def tree_state(root, skip=(".git",)):
    """{path relative to root: entry_state} for root and everything under it, symlinks not followed, the names in
    skip at its top left out (a worktree's .git link has a check of its own). A folder that can't be listed is there
    with its mode, as it was before, and what is in it can't be read by the round's scripts either."""
    state = {".": entry_state(root)}
    for folder, folders, files in os.walk(root):
        for name in sorted(folders + files):
            path = os.path.join(folder, name)
            if folder == root and name in skip:
                continue
            state[os.path.relpath(path, root)] = entry_state(path)
    return state


def git_files_state(common, gitdir):
    """What git reads in the clone's git folders that could run a command (core.fsmonitor, core.hooksPath, a
    diff.external or filter driver, the hooks): the shared config and config.worktree, the worktree's own
    config.worktree, and the hooks and info folders, each as tree_state reads it."""
    paths = {"config": os.path.join(common, "config"), "config.worktree": os.path.join(common, "config.worktree"),
             "worktree config.worktree": os.path.join(gitdir, "config.worktree"),
             "hooks": os.path.join(common, "hooks"), "info": os.path.join(common, "info")}
    return {name: tree_state(path, skip=()) if os.path.isdir(path) else entry_state(path)
            for name, path in paths.items()}


def tree_changes(before, after):
    """The paths whose state differs between two tree_state reads, sorted."""
    return sorted(path for path in set(before) | set(after) if before.get(path) != after.get(path))


def is_security(finding):
    """Whether a staged finding may be a security one, read wider than the ledger's isSecurity, never narrower: graded
    Warning, not marked security false, or a lens that isn't text or names security once all but its letters are
    dropped (the ledger strips control and invisible characters from a lens before it reads it)."""
    lens = finding.get("lens")
    return (finding.get("severity") == "warning" or finding.get("security") is not False or not isinstance(lens, str)
            or "security" in re.sub(r"[^a-z]", "", lens.lower()))


def staged_findings(run):
    """(the findings the session staged in run, the Rules they break): (None, []) when it staged none, and (None,
    [the rule]) when the file isn't a JSON array of objects that record_bytes reads. A review-only round stages each
    finding open, or dismissed when it isn't a security one (only the person decides one): fixed or accepted breaks a
    rule, and so does a security finding dismissed. A finding's repeats is left to record_review, which drops it.
    Everything else (a field, an id taken) is record's to refuse."""
    path = os.path.join(run, STAGED_FILE)
    if not os.path.lexists(path):
        return None, []
    data = record_bytes(path)
    try:
        findings = json.loads(data.decode("utf-8"))
    except (AttributeError, ValueError, RecursionError):  # unread, not UTF-8 or JSON, or nested past the parser
        findings = None
    if not isinstance(findings, list) or not all(isinstance(finding, dict) for finding in findings):
        return None, [f"{path} isn't the JSON array of findings scripts/review.sh stage writes"]
    broke = []
    for finding in findings:
        status = finding.get("status")
        if status not in STAGED_STATUSES:
            broke.append(f"{shown(finding.get('id'))} was staged {shown(status)}: a review-only session stages a finding"
                         " open or dismissed, since it fixes nothing and only the person accepts")
        elif status == "dismissed" and is_security(finding):
            broke.append(f"{shown(finding.get('id'))} was staged dismissed, and may be a security finding: only the"
                         " person decides one, so the session records it open")
    return (None if broke else findings), broke


def staged_copy(log):
    """Where the findings a session staged are kept when the round can't record them: beside its log, in a 0700
    folder whose findings.json is 0600, as scripts/review.sh record --staged reads them."""
    return f"{log}.staged"


def record_command(worktree, staged, head):
    """The command the person runs to record findings kept in staged themselves, once what refused them is put right:
    the key and HEAD of the worktree's code now, from scripts/review.sh prepare, given to scripts/review.sh record,
    only while that HEAD is head, the one the session reviewed (a commit since would have the findings vouch for code
    the session never read); otherwise it says why and records nothing. It is one sh -c, so it runs the same pasted
    into zsh or fish as into bash."""
    script = (f"cd {shlex.quote(worktree)} || exit 1\n"
              'out=$(scripts/review.sh prepare "$(mktemp -d)") || { echo "scripts/review.sh prepare failed: nothing'
              ' recorded" >&2; exit 1; }\n'
              'set -- $out\n'
              f'if [ "$#" -ne 2 ] || [ "$2" != {shlex.quote(head)} ]; then echo "the worktree\'s HEAD is not {head},'
              ' the one the session reviewed: nothing recorded" >&2; exit 1; fi\n'
              f"printf '{{\"reviewedKey\":\"%s\",\"reviewedHead\":\"%s\",\"rounds\":1,\"reviewerAgent\":"
              f"\"{REVIEWER_AGENT}\"}}' \"$1\" \"$2\" | scripts/review.sh record - --staged {shlex.quote(staged)}\n")
    return f"sh -c {shlex.quote(script)}"


def write_staged(folder, text, head=None):
    """Make folder 0700 and write text to its findings.json, 0600, never over a file already there; with head, the
    HEAD the session reviewed, to its HEAD file as well (kept_head reads it)."""
    os.mkdir(folder, 0o700)
    for name, content in ((STAGED_FILE, text), (KEPT_HEAD_FILE, head)):
        if content is None:
            continue
        fd = os.open(os.path.join(folder, name), os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            handle.write(content)


def kept_head(keep):
    """The HEAD the session reviewed, as record_review kept it beside the findings in keep; None when none is kept.
    The round writes it, outside the sandbox, so unlike the log the session can't have written it."""
    try:
        with open(os.path.join(keep, KEPT_HEAD_FILE), encoding="utf-8") as handle:
            head = handle.read(100).strip()
    except OSError:
        return None
    return head if re.fullmatch(r"[0-9a-f]{40}([0-9a-f]{24})?", head) else None


def run_bounded(command, cwd, stdin, stdout, stderr, timeout=RECORD_TIMEOUT_SECONDS, got=(), popen=subprocess.Popen,
                end=None, clock=time.monotonic):
    """A command the round runs to record a review (scripts/review.sh prepare or record), in a process group of its
    own that holds the round's lock: its exit code, or None when it ran past timeout seconds or a deferred signal
    came (got, run_review's list, holds it: given for prepare, which writes only a folder of its own, and not for
    record, which a kill could leave half-written). Its group is killed whole however it ends, so nothing it started
    (tsx's node) goes on writing the ledgers or holding their lock, and a terminal's interrupt
    reaches only the round. Raises GroupLeft when the group is still there after the kill."""
    child = popen(command, cwd=cwd, stdin=stdin, stdout=stdout, stderr=stderr, start_new_session=True,
                  pass_fds=held_lock_fds())
    code = None
    try:
        code = wait_for(child, timeout, got, clock)
    finally:
        if not (end or end_group)(child):
            raise GroupLeft(f"what {os.path.basename(command[0])} {command[1]} left in its process group was still"
                            f" there {GROUP_WAIT_SECONDS} s after it was killed, and may still write the review ledgers"
                            f" and .security: {group_hint(child.pid)}, before you resume")
    return code


def tsx_installed(worktree):
    """Whether the worktree's node_modules/.bin/tsx is there for scripts/review.sh to run, so it installs nothing."""
    return os.access(os.path.join(worktree, "node_modules", ".bin", "tsx"), os.X_OK)


def record_review(worktree, run, out, head, keep, got=(), unseen=lambda: [], bounded=run_bounded,
                  installed=tsx_installed):
    """Record the findings the session staged in run, as the review's record: scripts/review.sh prepare in a folder of
    the round's own gives the key and HEAD of the code, and scripts/review.sh record takes them, with the staged
    findings copied there 0600 (record reads only a file of the person's own). Both are the worktree's, its Owned
    copies the Template's, and run as the gate on the push runs them, outside the sandbox (run_bounded); unseen() is
    asked once more just before record runs (unseen_changes). head is the HEAD the guard read before the session: a
    prepare at another one means something committed there since, and nothing is recorded. A finding's repeats is
    dropped (and said in the log): it would tie the finding to a .security entry, which the person's accept of the
    finding then accepts too, and the session could name any entry, an open one of another branch's; the record then
    matches it by its path and summary, and one worded otherwise gets an entry of its own, open. What the session staged
    is kept in keep (staged_copy) before anything runs, and removed once it is recorded or a Rule is found broken (the
    code changed since the session read it, so it vouches for none of it); any other failure, a GroupLeft or a bug
    too, leaves it there. tsx not installed is a failure before prepare runs, since scripts/review.sh would install
    it into the worktree just checked. (exit code, the Rules broken): 0 when it recorded; NOTHING_STAGED when the
    session staged nothing, not even []; RECORD_FAILED when prepare or record refused, ran past its time, was stopped
    by a signal (got), can't be told to have printed the key and HEAD, or prepare's group was left (it writes only the
    round's own folder, so nothing was recorded), and the log says why and how to record the kept findings;
    SESSION_BROKE_A_RULE with the Rules when the staged findings break one (staged_findings), HEAD moved, or unseen()
    names one; a GroupLeft from record is raised. prepare's stdout is read for its key and HEAD; everything else
    either prints goes to out, the log."""
    findings, broke = staged_findings(run)
    if broke:
        return SESSION_BROKE_A_RULE, broke
    if findings is None:
        out.write(f"rollout.sh: the session staged no findings in {run}, not even [], so no review was recorded\n")
        return NOTHING_STAGED, []
    for finding in findings:
        if "repeats" in finding:
            out.write(f"rollout.sh: {shown(finding.get('id'))}'s repeats {shown(finding['repeats'])} was dropped:"
                      " the record matches the finding to a .security entry by its path and summary (case and spacing"
                      " aside)\n")
            del finding["repeats"]
    try:
        text = json.dumps(findings)
    except (RecursionError, ValueError):
        return SESSION_BROKE_A_RULE, [f"{shown(os.path.join(run, STAGED_FILE))} nests too deep to be written again"]
    review = os.path.join(worktree, "scripts", "review.sh")
    try:
        write_staged(keep, text, head)
    except OSError as error:
        out.write(f"rollout.sh: what the session staged couldn't be kept in {keep} ({error}): if the record fails,"
                  " it is gone\n")

    def failed(why):
        out.write(f"rollout.sh: {why}, so the review wasn't recorded\n")
        if os.path.isfile(os.path.join(keep, STAGED_FILE)) and kept_head(keep) == head:
            out.write(f"rollout.sh: what the session staged is kept in {keep}; once what refused it is put right,"
                      f" record it with:\n  {record_command(worktree, keep, head)}\n")
        return RECORD_FAILED, []

    def broke_rule(rules):
        # The code changed since the session read it: what it staged vouches for none of it, so nothing is kept.
        shutil.rmtree(keep, ignore_errors=True)
        return SESSION_BROKE_A_RULE, rules

    if got:
        return failed("a signal came before scripts/review.sh prepare ran")
    if not installed(worktree):
        return failed(f"{os.path.join(worktree, 'node_modules', '.bin', 'tsx')} isn't installed (pnpm install in the"
                      " worktree): scripts/review.sh would install it, changing the worktree the round has just checked")
    with tempfile.TemporaryDirectory(prefix="rollout-record.") as own:
        staged = os.path.join(own, "staged")
        write_staged(staged, text)
        printed = os.path.join(own, "prepared")
        out.flush()
        with open(printed, "w+", encoding="utf-8") as key_and_head:
            try:
                code = bounded([review, "prepare", os.path.join(own, "prepare")], cwd=worktree,
                               stdin=subprocess.DEVNULL, stdout=key_and_head, stderr=out, got=got)
            except GroupLeft as error:
                # prepare writes only the round's own folder, so nothing was recorded; the group still holds the lock.
                return failed(str(error))
            key_and_head.seek(0)
            lines = key_and_head.read(4096).split()
        if code is None and got:
            return failed("scripts/review.sh prepare was stopped by a signal")
        if code is None:
            return failed(f"scripts/review.sh prepare ran past {RECORD_TIMEOUT_SECONDS} s and was stopped")
        if code != 0 or len(lines) != 2:
            return failed(f"scripts/review.sh prepare exited {code} and printed {len(lines)} line(s), not its key and"
                          " HEAD")
        key, prepared_head = lines
        if prepared_head != head:
            return broke_rule([f"the worktree's HEAD was {shown(prepared_head)} by the time the review was"
                                          f" recorded, not {head} as the session reviewed it: something committed there"
                                          " since, so nothing was recorded"])
        broke = unseen()
        if broke:
            return broke_rule(broke)
        given = os.path.join(own, "review.json")
        with open(given, "w", encoding="utf-8") as handle:
            json.dump({"reviewedKey": key, "reviewedHead": prepared_head, "rounds": 1, "reviewerAgent": REVIEWER_AGENT},
                      handle)
        out.write(f"rollout.sh: recording the {len(findings)} finding(s) the session staged\n")
        out.flush()
        with open(given, encoding="utf-8") as review_input:
            code = bounded([review, "record", "-", "--staged", staged], cwd=worktree, stdin=review_input, stdout=out,
                           stderr=subprocess.STDOUT)
        out.flush()
        if code is None:
            return failed(f"scripts/review.sh record ran past {RECORD_TIMEOUT_SECONDS} s and was stopped")
        if code != 0:
            return failed(f"scripts/review.sh record exited {code}")
    shutil.rmtree(keep, ignore_errors=True)
    return 0, []


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
    group's while the leader is unreaped (wait_for leaves it a zombie) or a member is left, so the signal reaches only
    this group: the leader is reaped only after it."""
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
               skill=read_skill, agents=agent_definition, record=record_review):
    """/pre-push-review headless in worktree: (exit code, the session's log, the Rules it broke). 124 when it runs past
    its time, 127 when it can't start, SESSION_BROKE_A_RULE when it broke one (they are listed, and in the log too),
    RECORD_FAILED when what it staged couldn't be recorded (it is kept, staged_copy), RECORD_LEFT (with the group in
    the list) when the record left a process group the kill didn't end, so whether it recorded can't be told,
    NOTHING_STAGED when it exited 0 having staged nothing. The session runs in Claude Code's sandbox
    (session_settings) with the skill and its agent from the Template, no settings file, no GitHub sign-in and a
    temporary folder of its own, with the run folder it stages its findings in; it leads a process group of its own,
    which is killed whole whenever it ends; a SIGINT, SIGTERM or SIGHUP while it runs is held until the group is gone,
    then passed on. Once it has exited 0 and broken no Rule, the round records what it staged (record_review). The
    log is 0600 (mkstemp) in a 0700 folder of the user's own."""
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
            run = os.path.join(tmp, "run")
            os.mkdir(run, 0o700)
            settings = session_settings(worktree, before.common, before.gitdir, tmp)
            command = review_command(repo, worktree, owned, settings, agents(), skill(), run)
            env = session_env(os.environ, tmp)
        except GuardUnreadable as error:
            return stop(f"{error}; the session wasn't started")
        except (OSError, RuntimeError, ValueError) as error:
            return stop(f"can't set the session up: {error}")
        out.flush()
        code, started, survived = None, True, False
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
                        survived = True
                        out.write(f"\nrollout.sh: the session's process group was still there {GROUP_WAIT_SECONDS} s after"
                                  " it was killed\n")
        if got:
            raise passed_on(got[0])
        if not started:
            return 127, log, []
        keep, head = staged_copy(log), before.worktree_state[0]
        # Held from here until what the session staged is recorded or kept: a signal while the checks run (they read
        # every file of the worktree) would otherwise lose the run folder, and the session's findings with it.
        with deferred_signals() as got:
            try:
                broke = check(before)
            except Exception as error:  # noqa: BLE001 - a check that fails can't vouch for the session: a stop, not a crash
                out.write(f"rollout.sh: the checks after the session failed:\n{traceback.format_exc()}")
                broke = [f"the checks after the session failed ({type(error).__name__}: {error}; the traceback is in"
                         " the log), so what it did can't be told"]
            if survived:
                # What is left of it can still write after the check read the ledgers.
                broke.append(f"the session's process group was still there {GROUP_WAIT_SECONDS} s after it was killed,"
                             f" and can still write: {group_hint(session.pid)}, before you resume")
            if not broke and code == 0:
                sys.stderr.write(f"rollout.sh: recording the review (scripts/review.sh prepare, then record, up to"
                                 f" {RECORD_TIMEOUT_SECONDS // 60} minutes each: an interrupt stops prepare, and waits"
                                 " for record to end)\n")
                try:
                    code, broke = record(worktree, run, out, head, keep, got, lambda: unseen_changes(before))
                except GroupLeft as error:
                    # record may have written the ledgers before it left the group, so whether it recorded is unknown.
                    out.write(f"rollout.sh: {error}\n")
                    code, broke = RECORD_LEFT, [str(error)]
                    if got:
                        sys.stderr.write(f"rollout.sh: {error}\n")  # the signal wins, but the group must be ended
                except (OSError, subprocess.SubprocessError) as error:
                    out.write(f"rollout.sh: can't record the review: {type(error).__name__}: {error}\n")
                    code = RECORD_FAILED
                except Exception:  # noqa: BLE001 - a bug while recording is a stop that keeps the signal, not a crash
                    out.write(f"rollout.sh: recording the review failed:\n{traceback.format_exc()}")
                    code = RECORD_FAILED
        if got:
            if kept_head(keep) == head:
                # The stop that would print it isn't reached: the signal ends the round here.
                sys.stderr.write(f"rollout.sh: what the session staged is kept in {keep} (see {log}); record it with:"
                                 f" {record_command(worktree, keep, head)}\n")
            raise passed_on(got[0])
        if code == RECORD_LEFT:
            return code, log, broke
        for rule in broke:
            out.write(f"rollout.sh: the session broke a rule: {rule}\n")
        return (SESSION_BROKE_A_RULE if broke else code), log, broke


def wait_for(session, timeout, got, clock=time.monotonic):
    """The session's exit code, or None once timeout seconds pass or a deferred signal arrives (got holds it). It polls,
    since with the signals deferred nothing interrupts a wait, and leaves the leader unreaped (exit_unreaped)."""
    deadline = clock() + timeout
    while True:
        try:
            return exit_unreaped(session, max(0.0, min(0.5, deadline - clock())))
        except subprocess.TimeoutExpired:
            if got or clock() >= deadline:
                return None


def exit_unreaped(session, seconds, sleep=time.sleep, clock=time.monotonic):
    """As session.wait(timeout=seconds), but a leader that exited stays a zombie, unreaped (waitid's WNOWAIT): its pid,
    the group's id, then can't be another process's until end_group has signalled the group and reaped it. A session
    that isn't this process's child, or was reaped already, is waited as Popen waits, and so is every session where
    Python has no waitid or WNOWAIT (macOS): there the leader is reaped first, and the group's id could in principle be
    another's by the time end_group signals it, which is the best that platform allows."""
    if not hasattr(os, "waitid") or not hasattr(os, "WNOWAIT"):
        return session.wait(timeout=seconds)
    deadline = clock() + seconds
    while True:
        try:
            info = os.waitid(os.P_PID, session.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT)
        except ChildProcessError:
            return session.wait(timeout=max(0.0, deadline - clock()))
        if info is not None and info.si_pid == session.pid:
            return info.si_status if info.si_code == os.CLD_EXITED else -info.si_status
        if clock() >= deadline:
            raise subprocess.TimeoutExpired(getattr(session, "args", "session"), seconds)
        sleep(min(0.05, max(0.0, deadline - clock())))


class GroupLeft(Exception):
    """What a command left in its process group was still there after the group was killed."""


def group_hint(pgid):
    """How the person sees and ends what is left in a process group, with commands both procps and the BSDs have."""
    return f"see it with pgrep -l -g {pgid}, and end it with pkill -KILL -g {pgid}"


def run_in_group(command, popen=subprocess.Popen, end=None, clock=time.monotonic):
    """A command, its output straight through, in a process group of its own, with child_env: its exit code. Its group
    is killed whole however it ends: after it exits, so nothing it left there (a build server) keeps the round's
    lock, which it is passed; and on a SIGTERM or SIGHUP that ends the round (round_signals) or an interrupt, so
    neither it nor anything it started (the gate a push runs) goes on working in a worktree after rollout.sh is gone. The signals are held from before it
    starts until its group is gone (a second one meanwhile is dropped), then the first is passed on, as for a review
    session; a command that can't start raises its OSError, and one whose group is still there after the kill raises
    GroupLeft (a stop: it can still work in the worktree, and holds the lock), each after a signal that came
    meanwhile, which is passed on in their place (a GroupLeft is then written to stderr, so the person still hears of
    the group)."""
    code, failed = None, None
    with deferred_signals() as got:
        try:
            child = popen(command, start_new_session=True, pass_fds=held_lock_fds(), env=child_env(os.environ))
        except OSError as error:
            failed = error
        else:
            try:
                code = wait_for(child, math.inf, got, clock)
            finally:
                # However it ends: what it left running in its group (a build server) holds the round's lock.
                if not (end or end_group)(child):
                    failed = GroupLeft(f"what {os.path.basename(command[0])} left in its process group was still there"
                                       f" {GROUP_WAIT_SECONDS} s after it was killed, and holds the round's lock and"
                                       f" may still work in its worktree: {group_hint(child.pid)}, before you resume")
    if got:
        if isinstance(failed, GroupLeft):
            sys.stderr.write(f"rollout.sh: {failed}\n")  # the signal wins, but the person still has to end the group
        raise passed_on(got[0])
    if failed is not None:
        raise failed
    return code


def child_env(base):
    """The environment of the commands run_in_group starts: base with NO_BUILD_SERVERS."""
    return {**base, **NO_BUILD_SERVERS}


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
                           f" moved since the last one, or the session stopped short in its sandbox (no GitHub sign-in, no network;"
                           f" see {log}); run /pre-push-review in {shlex.quote(worktree)}, then resume")
        reviews += 1
        say(f"── rollout: {short_name(repo)}: the gate asks for a review; running /pre-push-review headless in {worktree}")
        code, log, broke = review(repo, worktree)
        say(f"── rollout: {short_name(repo)}: review session exited {code}; log: {log}")
        here, keep = shlex.quote(worktree), staged_copy(log)
        head = kept_head(keep)
        kept = (f" What the session staged is kept in {keep}; record it, once that is put right, with:"
                f" {record_command(worktree, keep, head)}") if head is not None else ""
        if code == RECORD_LEFT:
            return Outcome("person", f"the round's record of the review didn't end cleanly: {broke[0]}. Whether the"
                           f" review was recorded can't be told: cd {here} && scripts/review.sh report shows it (see"
                           f" {log}).{kept}")
        if broke:
            return Outcome("person", f"the review session broke a rule, so nothing was recorded: {broke[0]}"
                           + (f" (and {len(broke) - 1} more)" if len(broke) > 1 else "") + f"; see {log}. Put it right"
                           " before you resume, since a resume starts from what it finds:"
                           f" git -C {here} status --ignored shows the worktree, cd {here} && scripts/review.sh report"
                           f" shows the review ledgers, and cd {here} && scripts/review.sh security shows the .security"
                           " record")
        if code != 0:
            why = {124: f"ran past {REVIEW_TIMEOUT_MINUTES} minutes and was stopped",
                   127: "couldn't be started (the log's rollout.sh: line says why)",
                   NOTHING_STAGED: "exited 0 having staged no findings, not even [], so no review was recorded",
                   RECORD_FAILED: "staged findings the round couldn't record (scripts/review.sh prepare or record"
                                  " failed, or the round did; the log says why)"}.get(code, f"exited {code}")
            return Outcome("failed", f"the review session {why} (see {log}); run /pre-push-review in"
                           f" {here}, then resume.{kept}")


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

    def crashed(repo, error, retry=None):
        """A crash still ends with a stop: the traceback goes to stderr, so a bug can be told from the world. It names
        retry, the command to run by hand, or else the repo's reapply.sh, where its checkout can be told."""
        err.write(f"rollout.sh: {short_name(repo)} crashed:\n{traceback.format_exc()}")
        if retry is None:
            try:
                retry = f"reapply.sh --brings {shlex.quote(brings)} {shlex.quote(checkout(repo))}"
            except Exception:  # noqa: BLE001 - the stop is still printed, without the command
                retry = ""
        return f"{short_name(repo)}: {type(error).__name__}: {error} (traceback on stderr)" + (
            f"; run it by hand with: {retry}" if retry else "")

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
            try:
                landed = land([f"{first}#{number}"])
            except GroupLeft as error:
                return held(f"{short_name(first)}#{number}: {error}; then land it with land.sh {first}#{number}")
            except OSError as error:  # land.sh couldn't start: the stop names land.sh, not the reapply.sh a crash would
                return held(f"{short_name(first)}#{number}: land.sh failed: {type(error).__name__}: {error}; land it with"
                            f" land.sh {first}#{number}")
            except Exception as error:  # noqa: BLE001 - a bug while landing: its traceback, and land.sh to run
                return held(crashed(first, error, f"land.sh {first}#{number}"))
            if landed != 0:
                return held(f"{short_name(first)}#{number} didn't land (see land.sh's output above): land.sh {first}#{number}")
            state = settle(first, number)
        if state != "MERGED":
            return held(not_merged(first, number, state))
    except PrUnknown as error:
        return held(f"{short_name(first)}: can't tell its re-Apply PR ({error}): land it with land.sh {first}")
    except GroupLeft as error:
        return held(f"{short_name(first)}: {error}")
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
        except GroupLeft as error:
            stops.append(f"{short_name(repo)}: {error}")
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
        except GroupLeft as error:
            stops.append(f"land.sh: {error}")
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
                  f" started is still running; nothing run. See what holds it with: fuser -v {shlex.quote(LOCK)} (or lsof"
                  f" {shlex.quote(LOCK)}), and end it only when no round is running\n")
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
