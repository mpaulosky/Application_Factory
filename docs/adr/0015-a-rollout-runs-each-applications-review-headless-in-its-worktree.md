---
status: accepted
---
# A Rollout runs each Application's review headless, sandboxed, in its worktree

A Rollout needed a person between every step. `rollout.sh` ran `reapply.sh`, whose push the Application's gate refused until `/pre-push-review` had recorded a review (ADR 0010);
someone started a session in the re-Apply worktree to run it, ran `rollout.sh` again, landed the PR with `land.sh`, and ran `rollout.sh` once more for the rest of the Fleet, where each
repo could stop at the review again. A missing tool, such as the .NET SDK the Template's `global.json` names, showed up only halfway through a gate (mpaulosky/Application_Factory#81).

## Decision

`rollout.sh --auto` (`github/auto_rollout.py`) drives a round to the end. Before anything runs it checks the tools the gates, `reapply.sh` and the review need (bubblewrap and socat
for the sandbox among them), an shfmt the gate's gh check accepts, `gh`'s sign-in and the SDK the Template's `global.json` names, and takes a lock so a second `--auto` run can't work
the same worktrees (a person running `reapply.sh` or the review by hand there meanwhile isn't kept out). When the gate refuses the push for want of a review, `reapply.sh` exits 3
(and 4 when one is recorded but findings in it wait for a person). Only a push whose gate stopped at its Local review step is asked about, and then the worktree's own
`scripts/review.sh check` says how it refuses: the refusal is never read from the push's log. Every other failure exits 1. On a 3 the Rollout runs the pre-push review headless
(`claude -p` started in the repo's re-Apply worktree), then `reapply.sh` again to push and open the PR, and lands TicketManager's PR with `land.sh` before it starts the rest. It reads
only the repo's own PRs (not a fork's of the same name), the one at the worktree's head when the branch has commits main lacks, since every round shares the branch's name; an OPEN PR
at another head is never this round's, and with nothing ahead a CLOSED one there is the last round's, so there is nothing to land. After `land.sh` it reads the PR by its number. It
stops only where a person must decide, and prints the command that decides it and the one that resumes.

The session runs unattended and reads text people without write access may have written, so it runs in Claude Code's sandbox, which the operating system enforces (bubblewrap on
Linux) on every Bash command and everything it starts:

- **The sandbox.** Its Bash reads none of the person's credentials (`~/.ssh`, `~/.config/gh`, `~/.git-credentials`, `~/.netrc`, the keyrings, cloud and package-registry
  sign-ins, Claude Code's own credentials file, the other sessions' logs), writes only the worktree, the clone's git folder (not its config, hooks or info), the main checkout's
  `.security` and a temporary folder of its own, and reaches only the package registries and GitHub. It can't leave the sandbox (`allowUnsandboxedCommands: false`), and the session
  refuses to start without it (`failIfUnavailable`). Builds and installs keep their caches in the temporary folder.
- **The file tools**, which run outside the sandbox, follow `dontAsk` permissions: anything not allowed is refused. They may read anywhere but the same credentials, edit only the
  worktree and the temporary folder, start the reviewer agent, and run Bash, which is sandboxed.
- **Nothing of the repository's own applies to it.** It loads only the person's user settings (`--setting-sources user`) and no MCP server, and takes the skill's text and its
  reviewer agent from the Template, not the worktree: the repository's hooks, permission rules and settings never run or widen it.
- **Layers behind the sandbox.** Its environment has no GitHub sign-in (`gh` points at an empty config; names that look like secrets are dropped, Claude Code's own sign-in aside),
  git's push to GitHub is sent nowhere, and deny rules name the commands that must never run. The prompt names the Owned files the branch changes, says it runs unattended, and says a
  finding in an Owned file stays open for a person, since its fix goes into the Template first.
- **Checks after it.** A session after which the remote branch moved (or can't be read), the Template changed, an Owned file the branch changes is no longer the Template's, or one of
  this branch's review ledgers gained an accepted finding stops the round for the person. The ledgers are compared by content with what they held before the session, and a ledger is
  this branch's when it names the branch or its head is one of the worktree's commits, so the person's own accept on another branch meanwhile isn't the session's.
- **Its processes.** It runs in a process group of its own, killed whole however it ends (it exits, or runs past three hours); an interrupt, a SIGTERM or a SIGHUP while it runs is
  held until the group is gone, then passed on, and one the caller ignores (`nohup`) stays ignored. `reapply.sh` and `land.sh` run in groups of their own too, which a SIGTERM or SIGHUP
  of the round kills. Its log is kept, `0600`, in a `0700` folder of the person's own, `~/.cache/repo-ci-baseline/logs`, since it can hold the session's report of security findings.

The gate, the ledger and the review's rules are unchanged: the push still needs a recorded review with no open Critical or Warning finding, and only the person accepts one.

## Considered Options

- **A script drives the round and runs the review skill headless, in Claude Code's sandbox** (chosen): the loop is deterministic, so `test.sh` tests it and a rerun resumes it; the
  judgment stays in `pre-push-review`; the operating system, not the model, keeps the session off the person's credentials and out of their files.
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
  `gh` sign-in, so it spends none of the owner's GitHub allowance (`references/tokens.md`), and its builds download their packages afresh into its temporary folder.
- A Critical or Warning finding in an Owned file stops the round for a person (the gate refuses the push): the Template-first fix is a Factory branch of its own. An Advisory one
  stays open for the follow-up issue, whose body the session writes to its run folder. Making the fix automatically is a follow-up, not this decision.
- `reapply.sh` knows the gate stopped at its Local review step by the step's name, and how the check refuses by the ledger's own text on its stderr; a test holds them equal to
  `gate.sh`'s and `checkReview`'s, so rewording either fails `test.sh` until both change.
- The sandbox holds only as far as Claude Code enforces it: a host on the allowed list (GitHub, the registries) is reachable for anything, and the person's own user settings and
  plugins still load. The session reads no PR threads or issue, which a re-Apply reviewed before its first push hasn't got, and a session that ends without recording a review is run
  once more, then the round stops. A SIGKILL of `rollout.sh` can't be caught, and leaves a running session to finish on its own.
