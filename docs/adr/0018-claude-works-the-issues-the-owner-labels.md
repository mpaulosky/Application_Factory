---
status: accepted
---
# Claude works the issues the owner labels

The Factory's open issues wait for the owner to start a Claude session for each one by hand. The Factory now carries `.github/workflows/claude-issues.yml`, which runs
`anthropics/claude-code-action` (pinned to the version Claude Review uses) on an issue the owner hands over: Claude reads the issue, makes the change on a `fix/{issue}-{slug}` or
`feature/{issue}-{slug}` branch, runs `scripts/gate.sh --quick`, and opens a draft pull request that the owner reviews and marks ready. The workflow is the Factory's own, not a copy of a
Template file; it doesn't reach the Fleet.

## Decision

- The `claude` label is the queue. The owner adds it, or comments `@claude` on the issue, which adds it. A comment from anyone else, or on a pull request, starts nothing, and a `claude` label
  someone else added is taken off with a comment.
- One issue is worked at a time: the job has the concurrency group `claude-issues`. Each run takes the oldest queued issue that isn't blocked and starts the next run when it ends, so a batch of
  labels is worked in turn rather than all at once on branches that conflict. An hourly schedule picks up an issue whose blocker closed since.
- An issue is blocked while GitHub's issue dependencies list an open issue it is blocked by (the way the Factory's issues record their order), or while it carries the `blocked` label. A blocked
  issue keeps `claude` and gets `claude:blocked` and one comment naming its blockers, and starts by itself once they close.
- `claude:working` marks the issue being worked. A run that ends without a pull request labels the issue `claude:needs-human` and says why on the issue, or, when Claude left no comment,
  where its last message is in the run's log.
- Claude runs with background tasks off (`CLAUDE_CODE_DISABLE_BACKGROUND_TASKS`), and a command may run for 20 minutes when it names no timeout (`BASH_DEFAULT_TIMEOUT_MS`) and up to
  60 when it does (`BASH_MAX_TIMEOUT_MS`). A headless run ends when Claude ends its turn, and nothing wakes it when a background command finishes: runs on issues 124, 130 and 136
  started the gate in the background, ended their turn to wait for it, and stopped with no branch, pull request or comment.
- Claude authenticates with `CLAUDE_CODE_OAUTH_TOKEN` (the secret Claude Review already uses, made by `claude setup-token` from the owner's subscription) or `ANTHROPIC_API_KEY`. Without either, a
  run does nothing. Its GitHub token is the Claude GitHub App's, from the action's OIDC exchange, so its pull request starts the required checks; one opened with the job's own token would start
  none. The exchange accepts only a workflow file that matches the default branch's, so the workflow first runs once merged.
- The pull request is a draft, which PR Auto-Merge (ADR 0008) skips: code Claude wrote unattended merges only once the owner has read it and marked it ready.

## Considered Options

- **Start a session by hand for each issue**: what the owner did; it waits on the owner for every issue.
- **Copilot's coding agent**: it spends the Copilot budget that Copilot's review (ADR 0014) already draws on, and works outside the Factory's Claude setup and rules.
- **The action's tag mode on each label** (`label_trigger`): it pushes a `claude/issue-…` branch that the branch standard refuses, leaves the pull request for a person to open, and a burst of
  labels starts every run at once. A concurrency group alone keeps only one run waiting and cancels the rest.
- **A queue worked one issue at a time, in automation mode** (chosen).

## Consequences

- Issues are worked one after another, so a long queue takes hours. Raising the limit would need branches that don't touch the same files, which the Factory's issues can't promise.
- The repository is public, and an issue's author can edit it after it is queued, so only issues the owner opened are worked. Claude reads the issue from a file the run writes with its
  title, body and the owner's comments only, and its pull request is a draft for the owner to read. Its shell isn't sandboxed: an instruction planted somewhere it reads anyway could still
  reach it.
- Claude runs with `--permission-mode bypassPermissions`. Claude Code asks before any write under `.claude/`, and a headless run refuses every ask, so without it Claude can't edit the
  Template (`.claude/skills/repo-ci-baseline/`), where most of the Factory's issues land. The first run, on issue 107, ended that way. Deny rules (`--disallowedTools`) still hold.
- Each run after the first is started by the one before it with the job's token, so its actor is `github-actions[bot]`, which the action refuses unless `allowed_bots` names it.
- Each pull request still gets the Factory's review: Copilot's where the ruleset asks for it and its budget lasts, Claude Review where it doesn't (ADR 0008, ADR 0014).
- A run's Claude usage counts against the owner's subscription, or the API key's account.
