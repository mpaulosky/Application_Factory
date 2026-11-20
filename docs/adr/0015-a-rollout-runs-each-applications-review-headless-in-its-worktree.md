---
status: accepted
---
# A Rollout runs each Application's review headless in its worktree

A Rollout needed a person between every step. `rollout.sh` ran `reapply.sh`, whose push the Application's gate refused until `/pre-push-review` had recorded a review (ADR 0010);
someone started a session in the re-Apply worktree to run it, ran `rollout.sh` again, landed the PR with `land.sh`, and ran `rollout.sh` once more for the rest of the Fleet, where each
repo could stop at the review again. A missing tool, such as the .NET SDK a repo's `global.json` names, showed up only halfway through a gate (mpaulosky/Application_Factory#81).

## Decision

`rollout.sh --auto` drives a round to the end. It checks the tools the gates, `reapply.sh` and the review need, and each repo's `global.json` SDK, before anything runs. When the gate
asks for a review, `reapply.sh` exits 3 (and 4 when one is recorded but findings in it wait for a person), and the Rollout runs `/pre-push-review` headless: `claude -p` started in the
repo's re-Apply worktree with `--permission-mode auto`, so Claude Code loads that checkout's `pre-push-reviewer` agent and the skill runs that checkout's `scripts/review.sh`. Then it runs
`reapply.sh` again to push and open the PR, and lands TicketManager's PR with `land.sh` before it starts the rest. It stops only where a person must decide, and prints the command that
decides it and the one that resumes the round.

The session is told it runs unattended: it asks nothing, edits no Owned file (their fixes go into the Template first), records a finding in one open for a person, and doesn't push, post
or edit a PR. The gate, the ledger and the review's rules are unchanged: the push still needs a recorded review with no open Critical or Warning finding, and only the person accepts one.

## Considered Options

- **A script drives the round and runs the review skill headless** (chosen): the loop is deterministic, so `test.sh` tests it and a rerun resumes it; the judgment stays in
  `pre-push-review`, which runs as it does for a person.
- **A skill that runs the whole round**: a model would carry out steps that need no judgment, untested, and a session's context would hold every repo's gate and review output.
- **The Application's gate trusts the Factory's review of the Template**: it would save a review per repo, but it changes the gate, and an Application's review also reads its own files
  beside the Owned ones.
- **`--permission-mode bypassPermissions` or a fixed tool allowlist**: bypass gives an unattended session every tool unchecked; an allowlist breaks whenever the skill runs a new command.
  `auto` lets the classifier allow the review's routine commands and refuse a destructive one.

## Consequences

- A Rollout runs without a person until a decision is needed. A review session costs tokens for each repo whose re-Apply writes an Owned file that carries behaviour.
- Findings in Owned files still stop the round for a person: the Template-first fix is a Factory branch of its own. Making that fix automatically is a follow-up, not this decision.
- `reapply.sh` reads the gate's review refusal by the ledger's own text; a test holds the two equal, so rewording `checkReview`'s messages fails `test.sh` until both change.
- The session runs as the person's Claude Code login, with the permission mode's classifier as the guard. A session that ends without recording a review is run once more, then the
  round stops.
