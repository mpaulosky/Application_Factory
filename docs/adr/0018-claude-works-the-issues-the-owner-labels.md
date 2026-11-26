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
  labels is worked in turn rather than all at once on branches that conflict. An hourly schedule picks up an issue whose blocker closed since. (The Update for up to three issues at
  once, below, replaces this, and the Update on `claude:blocked` starts a pick when the owner closes an issue.)
- An issue is blocked while GitHub's issue dependencies list an open issue it is blocked by (the way the Factory's issues record their order), or while it carries the `blocked` label. A blocked
  issue keeps `claude` and gets `claude:blocked` and one comment naming its blockers, and starts by itself once they close.
- `claude:working` marks the issue being worked. A run that ends without a pull request labels the issue `claude:needs-human` and says why on the issue, or, when Claude left no comment,
  where its last message is in the run's log.
- Claude authenticates with `CLAUDE_CODE_OAUTH_TOKEN` (the secret Claude Review already uses, made by `claude setup-token` from the owner's subscription) or `ANTHROPIC_API_KEY`. Without either, a
  run does nothing. Its GitHub token is the Claude GitHub App's, from the action's OIDC exchange, so its pull request starts the required checks; one opened with the job's own token would start
  none. The exchange accepts only a workflow file that matches the default branch's, so the workflow first runs once merged.
- The pull request is a draft, which PR Auto-Merge (ADR 0008) skips: code Claude wrote unattended merges only once the owner has read it and marked it ready.

## Considered Options

- **Start a session by hand for each issue**: what the owner did; it waits on the owner for every issue.
- **Copilot's coding agent**: it spends the Copilot budget that Copilot's review (ADR 0014) already draws on, and works outside the Factory's Claude setup and rules.
- **The action's tag mode on each label** (`label_trigger`): it pushes a `claude/issue-…` branch that the branch standard refuses, leaves the pull request for a person to open, and a burst of
  labels starts every run at once. A concurrency group alone keeps only one run waiting and cancels the rest.
- **A queue worked one issue at a time, in automation mode** (chosen; the Update for up to three issues at once, below, works the same queue three at a time).

## Consequences

- Issues are worked one after another, so a long queue takes hours. Raising the limit would need branches that don't touch the same files, which the Factory's issues can't promise.
  (The Update for up to three issues at once, below, replaces this: it raises the limit and notes on each pull request the others that change the same files.)
- The repository is public, and an issue's author can edit it after it is queued, so only issues the owner opened are worked. Claude reads the issue from a file the run writes with its
  title, body and the owner's comments only, and its pull request is a draft for the owner to read. Its shell isn't sandboxed: an instruction planted somewhere it reads anyway could still
  reach it.
- Claude runs with `--permission-mode bypassPermissions`. Claude Code asks before any write under `.claude/`, and a headless run refuses every ask, so without it Claude can't edit the
  Template (`.claude/skills/repo-ci-baseline/`), where most of the Factory's issues land. The first run, on issue 107, ended that way. Deny rules (`--disallowedTools`) still hold.
- A headless run ends at the first turn Claude ends, so a command it leaves running in the background never reports back. Issue 124's first two runs ended that way, waiting on
  `test.sh`. The run turns background tasks off (`CLAUDE_CODE_DISABLE_BACKGROUND_TASKS`), denies `Monitor` and `ScheduleWakeup`, and the prompt says to run every command in the foreground.
- Each run after the first is started by the one before it with the job's token, so its actor is `github-actions[bot]`, which the action refuses unless `allowed_bots` names it.
- Each pull request still gets the Factory's review: Copilot's where the ruleset asks for it and its budget lasts, Claude Review where it doesn't (ADR 0008, ADR 0014).
- A run's Claude usage counts against the owner's subscription, or the API key's account.

## Update (mpaulosky/Application_Factory#119): long runs

Issue 119's run, a port of five scripts and 286 tests, reached the work step's 75-minute timeout with nothing pushed. The work step now has 300 minutes and 500 turns, and the job 330
minutes, under the hosted runner's 360. One issue can therefore hold the queue for about five and a half hours, and a stuck run can spend that much of the owner's usage. (Since the
Update for up to three issues at once, below, it holds one of the three slots for that long.)

The action exchanges the OIDC token for the Claude GitHub App's once, when the work step starts, and an App installation token expires after an hour, so Claude no longer pushes or opens
the pull request itself. It commits as it goes and writes the pull request's title and body to files. The "Publish the branch" step then commits anything left uncommitted, exchanges a
fresh token, pushes the branch, also after a timeout, and opens the draft pull request when the title and body are there. A later run on the same issue continues from the branch it finds.
The runner and its commits are gone when the job ends, so each of the step's network calls is retried, and work it doesn't push (a commit that fails, commits on a branch not named for
the issue, a failed exchange, or a push refused because a token without the workflows permission won't take a branch that edits `.github/workflows/`) fails the step and is uploaded as
the run's `claude-branch` artifact (`claude-branch-<issue>` since the Update for up to three issues at once), which the Finish step's comment names. Claude can change the
checkout's git config and hooks and the global git and gh config, so the fresh token is used only in a new repository fetched from a bundle of the branch, with no global, system or hook
config, and by `gh` run outside the checkout with an empty config directory. The exchange from a plain step is the one the action makes, so the first long run after this change is the one to watch.

## Update: up to three issues at once

The Factory's issues are now split small, with "blocked by" links that keep dependent ones apart, so several are unblocked at once and the queue worked one by one took a day. The owner
chose to work up to three at once (`CLAUDE_ISSUES_CAP` in the pick step). This replaces the Decision's "One issue is worked at a time", the chosen option's one at a time, and the
Consequence that issues are worked one after another.

- A short pick job holds the `claude-issues` concurrency group, so two runs never pick the same issue. It counts the issues labelled `claude:working`, open or closed (the closed ones
  since the Update on `claude:blocked`, below), with `claude` or without it, whose run, the one the workflow's latest "Claude started" comment names, is still going, picks the oldest
  unblocked issues up to the free slots, and labels and comments on each before it ends; one it can't label or comment on is left for the next run. A `claude:working` label
  left by a run that has ended or is gone, or with no "Claude started" comment, is taken off, and the issue is queued again if it still carries `claude`; one whose comments
  or run can't be read keeps its slot. The Finish step takes `claude` off before `claude:working`, so a finished issue isn't left queued with its slot freed, where another run's pick could take it again.
- The work job is a matrix over the picks, each on its own runner and checkout of `main`, so the issues worked at once share nothing but the repository. Each one starts the next run as it
  ends, which fills the slot it freed, and keeps work it couldn't push as its own `claude-branch-<issue>` artifact, since an artifact's name is unique in a run. The job's token stays
  read-only for pull requests: the merge order comment goes through the issues API, which `issues: write` covers.
- Issues worked at once can still change the same files. Each run comments on its pull request naming the open pull requests from this repository's branches that change the same
  files (at most five plainly named files each, or "files with other names" when none is plain), and the order to merge them: the oldest first, merging `main` into each
  before it lands. A fork's pull request is left out, since a fork names its branch as it likes. The comment is written when the run ends, so an older pull request's
  comment doesn't learn of one opened later, and two runs ending together may each miss the other's. The pull requests stay drafts, so the owner still reads and lands each one on its own.
- Three runs of up to five and a half hours each draw on the owner's Claude usage at once, so a usage limit can end a run early, as `claude:needs-human`. Runner minutes are free on the
  public repository, and each run makes a few dozen GitHub API calls.

## Update: `claude:blocked` comes off with its blockers

The pick step took `claude:blocked` off an issue only when its loop reached it with a slot free, so with every slot taken the label stayed on an issue nothing blocked any more, and
the "Start the next run" step, which skips `claude:blocked` issues, started no run for it. The owner asked for `claude:blocked` to come off whenever an issue's blocker does
(2026-11-25).

- After it counts the slots taken and before it reads the queue, every pick reads the open issues labelled `claude:blocked` and takes the label off each one that has no open
  "blocked by" issue and no `blocked` label, whether or not a slot is free. A list or an issue's dependencies that can't be read, or a label that can't be taken off, leave the label
  on for the next run, with a warning, and the pick goes on.
- The `blocked` label counts in any case (`Blocked` as well as `blocked`).
- The owner closing an issue (it can be the last blocker of another) or taking a `blocked` label off starts a pick, so an unblocked issue usually doesn't wait for the hourly
  schedule. This replaces the Decision's "An hourly schedule picks up an issue whose blocker closed since" for those two. The schedule still catches the rest: a "blocked by" link
  taken off, which starts no workflow; a close by a workflow's own token (a pull request merged without a personal token), which starts none either; a dependency listing that
  hadn't caught up with the close yet; and a close or an unlabel by anyone but the owner. That last one starts nothing on purpose: anyone can close an issue they opened, and the
  run's actor would be theirs, joining the concurrency group and working the owner's queue.
- An issue labelled `claude:working` holds its slot while its run is going even once it is closed, whoever closed it: closing an issue doesn't stop its run, and the owner's
  close now starts a pick at once. The pick counts closed issues with the label as well as open ones.
