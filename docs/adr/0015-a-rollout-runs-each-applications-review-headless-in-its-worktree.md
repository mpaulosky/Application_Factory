---
status: accepted
---
# A Rollout runs each Application's review headless in its worktree

A Rollout needed a person between every step. `rollout.sh` ran `reapply.sh`, whose push the Application's gate refused until `/pre-push-review` had recorded a review (ADR 0010);
someone started a session in the re-Apply worktree to run it, ran `rollout.sh` again, landed the PR with `land.sh`, and ran `rollout.sh` once more for the rest of the Fleet, where each
repo could stop at the review again. A missing tool, such as the .NET SDK the Template's `global.json` names, showed up only halfway through a gate (mpaulosky/Application_Factory#81).

## Decision

`rollout.sh --auto` (`github/auto_rollout.py`) drives a round to the end. Before anything runs it checks the tools the gates, `reapply.sh` and the review need, an shfmt the gate's gh
check accepts, `gh`'s sign-in and the SDK the Template's `global.json` names, and takes a lock so a second `--auto` run can't work the same worktrees (a person running `reapply.sh`
or the review by hand there meanwhile isn't kept out). When the gate refuses the push for want of a review, `reapply.sh` exits 3 (and 4 when one is recorded but findings in it wait
for a person). Only a push whose gate stopped at its Local review step is asked about, and then the worktree's own `scripts/review.sh check` says how it refuses: the
refusal is never read from the push's log. Every other failure exits 1. On a 3
the Rollout runs `/pre-push-review` headless: `claude -p` started in the repo's re-Apply worktree, so Claude Code loads that checkout's `pre-push-reviewer` agent and the skill runs that
checkout's `scripts/review.sh`. Then it runs `reapply.sh` again to push and open the PR, and lands TicketManager's PR with `land.sh` before it starts the rest. It reads only the repo's own PRs
(not a fork's of the same name), the one at the worktree's head when the branch has commits main lacks, since every round shares the branch's name; an OPEN PR at another head is
never this round's, and with nothing ahead a CLOSED one there is the last round's, so there is nothing to land. After `land.sh` it reads the PR by its number. It stops only where a person
must decide, and prints the command that decides it and the one that resumes.

The session runs with `--permission-mode auto` and deny rules (`--disallowedTools`) for the common spellings of `git push`, `scripts/review.sh accept` and `accept-open`, a set of
`gh` writes (PRs, issues, labels, releases, repos, secrets, workflows), and the file tools' edits under the Factory checkout and `~/.claude`. It isn't given the Template's folder (no
`--add-dir`): its prompt names the Owned files the branch changes, says it runs unattended, and says a finding in an Owned file stays open for a person, since its fix goes into the
Template first. It runs in a process group of its own, which is killed whole however the session ends: it exits, runs past three hours, or `rollout.sh` gets an interrupt, a
SIGTERM or a SIGHUP (a SIGKILL can't be caught). Its log is kept, `0600`, in a `0700` folder of the user's own, `~/.cache/repo-ci-baseline/logs`, since it can hold the session's report of
security findings. The gate, the ledger and the review's rules are unchanged: the push still needs a recorded review with no open
Critical or Warning finding, and only the person accepts one.

## Considered Options

- **A script drives the round and runs the review skill headless** (chosen): the loop is deterministic, so `test.sh` tests it and a rerun resumes it; the judgment stays in
  `pre-push-review`, which runs as it does for a person.
- **A skill that runs the whole round**: a model would carry out steps that need no judgment, untested, and a session's context would hold every repo's gate and review output.
- **The Application's gate trusts the Factory's review of the Template**: it would save a review per repo, but it changes the gate, and an Application's review also reads its own files
  beside the Owned ones.
- **`acceptEdits` with a fixed allowlist of commands**: in `-p` mode a command off the list is refused, so the session fails closed, but the list breaks whenever the skill runs a
  new command. `auto` with deny rules keeps the commands that must never run out by rule, and lets the classifier judge the rest.
- **`bypassPermissions`**: an unattended session that reads text from people without write access would hold every tool unchecked.

## Consequences

- A Rollout runs without a person until a decision is needed. A review session costs Claude usage for each repo whose re-Apply writes an Owned file that carries behaviour, and its
  reads of PR threads and the issue spend the owner's `gh` allowance (`references/tokens.md`).
- Findings in Owned files still stop the round for a person: the Template-first fix is a Factory branch of its own. Making that fix automatically is a follow-up, not this decision.
- `reapply.sh` knows the gate stopped at its Local review step by the step's name, and how the check refuses by the ledger's own text on its stderr; a test holds them equal to
  `gate.sh`'s and `checkReview`'s, so rewording either fails `test.sh` until both change.
- The deny rules match command prefixes, and Edit and Write rules bind the file tools, not Bash: a command written another way (`git -C . push`, `GH_REPO=x gh pr edit`, `gh api`
  with a method, a Bash write, an edit to the ledger's JSON) is left to the classifier. The session runs as the person's Claude Code login and `gh` sign-in, with their environment;
  the rules and the classifier are the guard, not a sandbox. A session that ends without recording a review is run once more, then the round stops.
