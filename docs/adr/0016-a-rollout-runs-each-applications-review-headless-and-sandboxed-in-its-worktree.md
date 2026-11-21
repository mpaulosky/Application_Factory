---
status: accepted
---
# A Rollout runs each Application's review headless, sandboxed, in its worktree

A Rollout needed a person between every step. `rollout.sh` ran `reapply.sh`, whose push the Application's gate refused until `/pre-push-review` had recorded a review (ADR 0010);
someone started a session in the re-Apply worktree to run it, ran `rollout.sh` again, landed the PR with `land.sh`, and ran `rollout.sh` once more for the rest of the Fleet, where each
repo could stop at the review again. A missing tool, such as the .NET SDK the Template's `global.json` names, showed up only halfway through a gate (mpaulosky/Application_Factory#81).

## Decision

`rollout.sh --auto` (`github/auto_rollout.py`) drives a round to the end. Before anything runs it checks the tools the gates, `reapply.sh` and the review need (on Linux, bubblewrap
and socat for the sandbox among them, and that bubblewrap can make one there), a `claude` that runs and is signed in with no settings file read, as the session runs (a
cloud's sign-in, which `claude auth status` doesn't report, isn't asked), an shfmt the gate's gh check accepts, ruff 0.17 or uv for
the gate's Python lint, `gh`'s sign-in and the SDK the Template's `global.json` names, and takes a lock so a second `--auto` run can't work the same worktrees (a person running
`reapply.sh` or the review by hand there meanwhile isn't kept out). Every child the round starts holds the lock too, so a session or gate still running after `rollout.sh` is killed
keeps the next run out; each child's process group is killed when it exits, and the gate's build is told to leave no build server running, so nothing a child leaves keeps the lock
after the round.
When the gate refuses the push for want of a review, `reapply.sh` exits 3 (and 4 when one is recorded but findings in it wait for a person).
Only a push whose gate stopped at its Local review step is asked about, and then the worktree's own
`scripts/review.sh check` says how it refuses: the refusal is never read from the push's log. Every other failure exits 1. On a 3 the Rollout runs the pre-push review headless
(`claude -p` started in the repo's re-Apply worktree), then `reapply.sh` again to push and open the PR, and lands TicketManager's PR with `land.sh` before it starts the rest. It reads
only the repo's own PRs (not a fork's of the same name), the one at the worktree's head when the branch has commits main lacks, since every round shares the branch's name; an OPEN PR
at another head is never this round's, and with nothing ahead a CLOSED one there is the last round's, so there is nothing to land. After `land.sh` it reads the PR by its number. It
stops only where a person must decide, and prints the command that decides it and the one that resumes.

The session runs unattended and reads text people without write access may have written, so it reviews only, and runs in Claude Code's sandbox, which the operating system
enforces (bubblewrap on Linux) on every Bash command and everything it starts:

- **Review only.** It runs one round over the whole branch, as the skill's last, review-only round, and stages each finding open with its suggested fix, or dismissed
  with the skill's reason when it isn't a security one; a security finding is never dismissed, a repeat of a fixed `.security` entry included. It edits, fixes and
  commits nothing and runs no gate, so nothing it wrote runs later as the person: the gate on the push and the Application's build and tests run only the code the
  re-Apply wrote. A Critical or Warning finding stops the round for the person, who fixes it with `/pre-push-review` in the worktree or accepts it.
- **The round records, not the session.** The session stages its findings (`scripts/review.sh stage`) in a run folder the round made for it, and doesn't record them. Once it
  ends, exited 0 and broke no rule, the round reads the staged file without following a link, copies it into a folder of its own and runs the worktree's
  `scripts/review.sh prepare` for the key and HEAD and `scripts/review.sh record` with them, outside the sandbox, as the gate on the push runs those scripts. So the ledgers,
  their entry maps and `.security` are written only by the record's own code, which checks each finding and its id as it does for a person. A HEAD other than the one
  read before the session means something committed there since, and nothing is recorded. A finding's `repeats` is dropped: it ties the finding to a `.security`
  entry, which the person's accept of the finding then accepts too, and the session could name any entry, an open one of another branch's; the record matches a
  repeat by its path and summary (case and spacing aside), and one worded otherwise gets an open entry of its own. Both commands run in process groups of their own,
  holding the lock, killed whole when they end, for at most ten minutes each; an interrupt stops `prepare`, and waits for `record`, which a kill could leave half written,
  so a signal can be held up to ten minutes; signals are held from the end of the session until what it staged is recorded or kept. That is kept beside the log
  with the HEAD it reviewed before either runs, and removed once it is recorded: a record that failed, a group either left behind or an interrupt prints the command
  that records it (one `sh -c`, so it runs the same in any shell), which records only at that HEAD and says why when it doesn't. A broken rule (HEAD moved, a
  changed file) keeps nothing, since the findings vouch for code that is no longer there. A group `record` left behind is a stop of its own: the review may be
  recorded, and `scripts/review.sh report` tells. The round refuses to record without `node_modules/.bin/tsx`, which `scripts/review.sh` would otherwise install
  into the worktree it has just checked.
- **The sandbox.** Its Bash reads none of the person's credentials (`~/.ssh`, `~/.config/gh`, `~/.git-credentials`, `~/.netrc`, `~/.gitconfig`, the shells' start-up
  files, .NET's user secrets, the keyrings, cloud and package-registry sign-ins, shell histories, all of `~/.claude` and Claude Code's `CLAUDE_CONFIG_DIR`, the other sessions'
  logs, and wherever a variable such as `KUBECONFIG` or `XDG_CONFIG_HOME` moves one). It writes only a temporary folder of its own, its run folder among it: not the
  worktree, which a sandbox otherwise lets its working folder be written, the clone's git folder, its review ledgers or the main checkout's `.security`. It reaches no
  host (it builds and installs nothing, and a `git fetch` has nowhere to write: `reapply.sh` has just fetched), can't leave the sandbox
  (`allowUnsandboxedCommands: false`), and the session refuses to start without it (`failIfUnavailable`).
- **The file tools**, which run outside the sandbox, follow `dontAsk` permissions: anything not allowed is refused. They may read only the worktree, `.security` and the temporary
  folder, edit only the temporary folder (so never a ledger or a `.security` entry), start the reviewer agent, and run Bash, which is sandboxed.
  Only the built-in tools the review needs exist in the session (`--tools`).
- **Nothing of the repository's own or the person's applies to it.** It loads no settings file at all (`--setting-sources ""`; its settings are passed on the command line, so the
  session can't rewrite them) and no MCP server, and takes the skill's text and its reviewer agent from the Template, not the worktree: neither the repository's nor the person's
  hooks, permission rules, plugins or settings run or widen it.
- **No secret in its environment.** It has no GitHub sign-in (`gh` points at an empty config), names that look like secrets are dropped, and of Claude Code's own names only a
  listed few that aren't secrets (its config folder, model, endpoint and cloud) are kept, so the calling session's id, messaging token and IDE port don't reach it either. Claude Code
  signs in from its credentials file or a cloud's profile files, which the sandbox keeps from the shell: the preflight refuses a round with a sign-in secret in the environment
  (`ANTHROPIC_API_KEY`, `CLAUDE_CODE_OAUTH_TOKEN`, a cloud's key), since the session's shell would inherit it.
- **Layers behind the sandbox.** git's push to GitHub is sent nowhere, and deny rules name the commands that must never run. The prompt names the Owned files the branch changes,
  says it runs unattended and review-only, and says a finding in an Owned file is fixed in the Template first.
- **Checks after it.** A session after which the worktree's `.git` link changed, or the clone's git `config`, a `config.worktree`, `hooks` or `info` (in either case no git
  runs there after it, since git runs what they name; the whole file is compared, not the keys that run a command, so the person's own `git push -u` or
  `git remote set-url` in the clone meanwhile stops it too, as a review they record does), the remote branch moved (or can't be read), the worktree's HEAD, branch or status changed, the Template
  changed, an Owned file the branch changes is no longer the Template's, any file of the ledgers or `.security` was added, removed or changed (the session writes
  neither, so a review the person records on another branch meanwhile stops it as well), any path of the worktree changed in content, mode or link target (a
  snapshot of every file's bytes taken before the session, so an ignored one git status doesn't show, such as `node_modules/.bin/tsx`, which the record runs,
  counts; times and link counts are left out, so another project's pnpm install changes nothing, except for a file the check can't read, compared by its
  mode, size, inode and mtime; read last, and again just before `record`), or it staged a
  finding other than open or dismissed, or a security one dismissed, stops the round for the person, and nothing is recorded. A dismissed
  finding counts as a security one when the ledger could read it as one, read wider: graded Warning, not marked `security: false`, or a lens that names security
  once all but its letters are dropped. Files are read without following a symlink, waiting on a FIFO or reading past a size; one that can't be read is compared by
  what `lstat` says of it. What the session wrote is printed escaped and cut short. A check that fails is a stop too, not a crash, and so is a session that exited 0
  having staged nothing, not even `[]`.
- **Its processes.** It runs in a process group of its own, killed whole however it ends (it exits, or runs past three hours), while its exited leader is still unreaped, so
  the group's id can't be another's (where Python has no `waitid`, macOS, the leader is reaped first: the best that platform allows); a group still there after the kill stops
  the round, since it could still write. An interrupt, a SIGTERM or a SIGHUP while it runs is held until the group is gone, then passed on, and one the caller ignores
  (`nohup`) stays ignored. `reapply.sh` and `land.sh` run in groups of their own too, which a SIGTERM or SIGHUP of the round kills; one still there after the kill stops
  that repo too, since it holds the lock and may still work in the worktree, and the stop names `pgrep -l -g` and `pkill -KILL -g` to see and end it. Its log is kept, `0600`,
  in a `0700` folder of the person's own, `~/.cache/repo-ci-baseline/logs`, since it can hold the session's report of security findings.

The gate, the ledger and the review's rules are unchanged: the push still needs a recorded review with no open Critical or Warning finding, and only the person accepts one.

## Considered Options

- **A script drives the round and runs the review skill headless and review-only, in Claude Code's sandbox** (chosen): the loop is deterministic, so `test.sh` tests it and a rerun
  resumes it; the judgment stays in `pre-push-review`; the operating system, not the model, keeps the session off the person's credentials and out of their files.
- **The same, with the session fixing what it finds**: reviewed and set aside, since what it commits runs later as the person, outside the sandbox (the gate on the push, the
  Application's build and tests), and its gate's installs can't run in the sandbox.
- **The same, guarded by environment and deny rules alone**: built first, and reviewed to be deterrence, not isolation. The session runs as the person, so from Bash it could read
  their credential files or the parent's environment, unset the variables meant to sign it out, and write `.git/config`, hooks or `~/.claude`, which then run later with the person's
  credentials.
- **Sandcastle's host review**, whose reviewers run in Docker with no shell or token and whose host records: the strongest, but every Application would need Docker and Sandcastle
  set up for it.
- **No unattended review**: `--auto` would stop for the person whenever a review is needed. Safe and simple, but a person at every repo whose re-Apply needs one.
- **A skill that runs the whole round**: a model would carry out steps that need no judgment, untested, and a session's context would hold every repo's gate and review output.
- **The Application's gate trusts the Factory's review of the Template**: it would save a review per repo, but it changes the gate, and an Application's review also reads its own files
  beside the Owned ones.

## Consequences

- A Rollout runs without a person until a decision is needed. A review session costs Claude usage for each repo whose re-Apply writes an Owned file that carries behaviour; it has no
  `gh` sign-in, so it spends none of the owner's GitHub allowance (`references/tokens.md`).
- Any Critical or Warning finding stops the round for a person (the gate refuses the push), in an Owned file or in the Application's own: a fix in an Owned file is a Factory
  branch of its own, the Template-first rule. An Advisory one stays open in the recorded review, and the person files the follow-up issue. A session that fixes as it
  reviews would stop less often, but what it wrote would run as the person outside the sandbox, and its gate's installs can't run inside it; that is a follow-up, not this decision.
- `reapply.sh` knows the gate stopped at its Local review step by the step's name, and how the check refuses by the ledger's own text on its stderr; a test holds them equal to
  `gate.sh`'s and `checkReview`'s, so rewording either fails `test.sh` until both change.
- The sandbox holds only as far as Claude Code enforces it: what it shows of `/proc` and of Unix sockets is Claude Code's, and its reads are a list of what to hide, so a
  secret in a file the list doesn't name (another checkout's `.env`, say) is readable; with no host to reach, what the session reads can leave only through what it writes
  (its findings, its folder), which a person reads before publishing. `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` isn't used: in Claude Code 2.1.326 it forces the
  permission mode back to `default`, and the refused sign-in
  secrets make it unneeded. The session reads no PR threads or issue, which a re-Apply reviewed before its first push hasn't got; a session whose recorded review the
  gate still refuses (main moved meanwhile, say) is run once more, then the round stops. The record runs the worktree's own scripts (`scripts/review.sh` and its
  `node_modules`) as the person, as the gate on the push does; the snapshot of the worktree is what stands between a sandbox that didn't hold and that. A process
  that left the session's group (`setsid`) is past what it covers, since it can write after the last read; so is a sandbox that let the session write outside the
  worktree. A SIGKILL of `rollout.sh` can't be caught, and leaves a running session to finish on its own, holding the lock.
- What the session hands on is its staged findings, which the round records with the record's own code, never its own copy of the record's rules: an earlier version let
  the session record and checked the ledgers and `.security` after it by reading them as the record does, and every round of review found another way that reading and the
  record's differed. The round's checks can't tell a review's judgment from a steered one that raised less: that is the review's own trust, as when a person runs it.
  A security finding the skill would dismiss as a repeat of a fixed entry is recorded open instead, so the person decides it.
