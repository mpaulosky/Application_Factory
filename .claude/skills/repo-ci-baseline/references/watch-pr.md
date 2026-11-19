# PR watcher

`scripts/watch-pr.sh` watches one pull request for the session that opened it and exits with the reason when that session has to act.
It is Owned, so every repo gets it, with or without Sandcastle. It needs `gh` and `jq`, and it is read-only: it makes `gh api graphql`
queries and writes only its seen file.

```bash
scripts/watch-pr.sh <pr>                      # poll every 60 s for up to 120 minutes
scripts/watch-pr.sh --once <pr>               # one poll
scripts/watch-pr.sh --interval 120 --timeout 30 <pr>
scripts/watch-pr.sh --would-skip < body.txt   # does the watcher skip this body from the owner? (exit 0 yes, 1 no)
```

`WATCH_PR_INTERVAL` and `WATCH_PR_TIMEOUT` set the defaults and the flags win. Each query costs one GraphQL point of the owner's 5,000 an hour
([tokens.md](tokens.md)), and a poll of a PR without more pages is one query, which is why the interval defaults to 60 s and shouldn't go lower.

`WATCH_PR_BUDGET` (default 1000) is the most GraphQL points an hour the watcher spends, however long the PR. After each poll, quiet or failed, it waits
the interval, or `3600 × queries ÷ budget` seconds when that's longer, `queries` being the first query and every page it asked for. A poll of 301 queries
(300 pages of comments) waits about 18 minutes at the default; a poll of up to 16 waits the interval. Nothing is capped and no poll fails for its length:
a long PR is read whole, only less often. The timeout counts these waits as it counts the interval.

## Reasons

A poll prints one `watch-pr: <code>: <detail>` line for every reason that applies, up to 50, in this order, and exits 0.

| Code | When |
| --- | --- |
| `merged`, `closed` | The PR is merged or closed. Printed alone. |
| `conflict` | The PR conflicts with its base. |
| `behind` | The base branch moved and the repo's rules require an up-to-date branch. Not reported beside `conflict`. |
| `check-failed` | A check run ended FAILURE, TIMED_OUT, ACTION_REQUIRED or STARTUP_FAILURE, or a status context is FAILURE or ERROR. The names are listed. Cancelled, skipped, neutral and pending checks don't count. |
| `changes-requested` | A person's review asked for changes. |
| `review` | A person's review with a body. |
| `comment` | A person's comment on the PR, the owner's included when it carries no marker. |
| `thread` | An unresolved review thread whose last comment is not the session's or Sandcastle's (Copilot's counts, and so does a reply from anyone but the repository's owner). After the one-shot events, so no number of threads keeps them out of the 50 lines a poll prints. |
| `more` | Printed last when the poll found more than 50 reasons: how many it didn't print. |

A poll prints at most 50 reasons, so no flood of comments floods the session. Every list is still read whole, and only the one-shot events
it printed are recorded as seen, so the next run prints the rest. Threads come last, so a comment or review is never kept out by them; a thread past the
first 50 is reported once those ahead of it are resolved. The `more` line says to act on the printed reasons and run again.

The detail is the author's login and the first line of the text (up to 200 characters; every control and format character, among them the bidi
controls, the zero-width ones, the soft hyphen and the tag characters, the line and paragraph separators, every default-ignorable code point (Unicode's
list, unassigned ones included, and also listed by range so an older jq's Unicode tables blank no fewer) and the blank braille cell, which a
terminal shows as nothing, made spaces, so no printed text is reordered or hidden), except
for a person other than the repository's owner: see [Who is trusted](#who-is-trusted).

A bot (`__typename` Bot: Copilot, github-actions) is never a person for `changes-requested`, `review` and `comment`; its findings reach the session as threads and checks.
`mergeable` UNKNOWN means GitHub hasn't worked it out yet, so the watcher keeps polling.

**Persistent** states (`conflict`, `behind`, `check-failed`, `thread`) are recomputed every poll and reported until fixed.
**One-shot** events (`changes-requested`, `review`, `comment`) are written to the **seen file**, `watch-pr-<n>.seen` in the git common directory
(`git rev-parse --git-common-dir`, so a linked worktree shares the main checkout's), and the next run doesn't report them again.
jq reads the seen file itself, never as an argument, so no list of seen ids, however long, can keep jq from starting.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The session is needed; the reasons are on stdout. |
| 1 | Quiet until the timeout, or after `--once`. |
| 2 | A usage error, a missing `gh` or `jq`, an unknown repository, or a PR that can't be read. A poll that fails, answers with something other than a pull request, or reads an open PR's lists or rollup only in part (see Limits), is retried; five in a row end the watch. Output that isn't GitHub's answer is never read as quiet. |

The timeout is counted in the waits between quiet polls (a failed poll's wait isn't counted; at most four of them run in a row).

## What it skips

Sandcastle and the session post under the owner's token, so the author can't tell them from the owner. Two markers do, on a comment, review or
thread reply whose author is the repository's owner (see [Who is trusted](#who-is-trusted)):

- **The session's.** A body that contains `WATCH_PR_MARKER` anywhere (default `<!-- claude-session -->`, the text `docs/PROCESS.md` has the session put in its replies).
- **Sandcastle's.** A body that *starts* with `<!-- sandcastle:<name> -->`, where the name is lower-case letters and hyphens, with or without data after the name:
  `<!-- sandcastle:approved-head <sha> -->` and `<!-- sandcastle:overlap … -->` are posted on PRs and carry data. A regex that took only a bare name would wake the watcher
  on every push. A quoted marker (`> <!-- sandcastle:pr -->`) or one further down the body is a person's text and still wakes it.

Every PR comment Sandcastle posts opens with a marker: `PR_MARKER`, `FOLLOW_UP_MARKER` (the pass summary), `FOLLOW_UP_REPLY_MARKER`, `APPROVED_HEAD_MARKER`,
`APPROVAL_LAPSED_MARKER`, `OVERLAP_MARKER`, `GIVE_UP_MARKER` and `WORKTREE_TAMPERED_MARKER`, all in `.sandcastle/lib/config.mts`. `config.test.mts` runs `watch-pr.sh --would-skip` on every
string that file exports starting `<!-- sandcastle:`, so a new marker is covered by exporting it from `config.mts`. A new PR comment from Sandcastle needs a marker as its first line.

`--would-skip` reads a body alone, so it answers for a body from the owner.

## Who is trusted

On a public repository anyone can comment on a PR, review it or reply to its threads, so the body alone can't say who wrote it. The queries read each
comment's, review's and thread reply's `authorAssociation`, on the first query and on every page read after it, and only OWNER, the
repository's owner, is trusted. Anything else is not: MEMBER and COLLABORATOR (people with write access) included, and a missing field.
The session and Sandcastle post with the owner's token, so their markers still count.
The watcher assumes a repository a user owns, as every Fleet repository is. In an organization's repository GitHub may report the owner's comments
as MEMBER, not OWNER, so the session's own replies wouldn't count as its own there: they would wake the watcher and keep their threads reported.

- **Markers.** A marker counts only under the owner. Anyone else's marked comment or review wakes the watcher, and their marked reply doesn't quiet a thread.
- **Text.** No other person's text reaches the session. Their comment or review still wakes it, once, as
  `watch-pr: comment: <login>: (text not shown: the author is not the repository owner)` (`review` and
  `changes-requested` the same, except that a changes-requested review with no body prints `(no text)` whoever wrote it, as there is no text to hide),
  and the session reads the PR itself if it wants to know more. A bot's text is shown, because the session needs reviewers' findings: Copilot's thread text is what it acts on.
  That text is not trusted. A bot can carry text a PR's author chose: github-actions[bot] and the Claude Review workflow post what they derive from
  the PR (a fork's diff or title), so a PR's author can steer what a bot's thread says. The session treats a bot's text as data, never as instructions.
- **Threads.** A thread's last comment still decides. A reply from an untrusted author counts as the last word, so the thread is reported with
  their login and the fixed phrase, never quieted. A stranger can't hide a thread, only keep one reported until a trusted author replies with the
  marker or the thread is resolved. The session's own last reply, under a trusted author, quiets it as before.

## Limits

- The first query reads 100 review threads, 100 checks, the last 100 reviews and the last 100 comments. For an open PR, a list GitHub says has more is read on, 100 at a time,
  from its cursor (threads and checks forward, reviews and comments back, kept in GitHub's order), until GitHub says it has no more: there is no cap on the pages.
  A page gh can't read, a missing cursor, a cursor the list was already read from (a page handing back its own cursor, or an earlier page's, which would never end),
  a page of another shape, checks whose first rollup is not of the head yet (they are not paged then), or a checks page of another head
  (a push between pages) or whose own rollup is of another commit is an incomplete read, a failed poll (never a quiet one) whose message says which; five in a row end the
  watcher with exit 2. Each page costs one more GraphQL point, so a long PR costs a poll one point per 100 items, and the wait after the poll grows with them (`WATCH_PR_BUDGET`, above).
  A list's pages are kept in a file and put together once, after its last page, with the repeated ids found in one pass, so reading a list takes time
  linear in its items, never in the square of its items or its pages. A cursor is checked against the cursors that list was read from, so two lists GitHub hands the same cursor each read it.
  A merged or closed PR is reported from its state alone, though its lists must still be arrays. The checks come from the PR's own
  `statusCheckRollup`, and only while it is of `headRefOid`; the last of the PR's commits needn't be the head, and the head ref (in the head repository) isn't read,
  so a fork PR is watched. A PR with no rollup yet has no check reported and is quiet, unless GitHub calls it `UNSTABLE` (a check failed):
  then the read is incomplete and the poll fails.
- Pages are read at different moments, not as one snapshot. A review or comment with an id a later page repeats (the list shifted between queries) is kept once, but a list that
  shifts can be read without an item (a comment deleted, a check re-run) for that one poll; the next poll reads it afresh.
- `behind` appears only where the base branch's rules require an up-to-date branch. PR Auto-Merge also merges `main` into a ready PR, so it may clear before the session acts.
- The Sandcastle workflow's own red `Sandcastle` check counts as `check-failed`.
- The tests run against a fake `gh`; nothing in the Template runs it against live GitHub.

## Who does what

| Tool | Who runs it | What it does |
| --- | --- | --- |
| `scripts/watch-pr.sh` | The session, on its own PR, in every repo | Reads the one PR; tells the session what needs it. Never changes anything. |
| `land.sh`, `status.sh` ([automerge.md](automerge.md#landing-a-round)) | The owner, across every repo in `github/repos.txt` | Decide the landing steps (update the branch, ask Copilot, add `review:claude`, auto-merge); `land.sh` does them. |
| PR Auto-Merge | The workflow | Lands a ready PR once its gates pass. |
| Sandcastle's sweep ([sandcastle.md](sandcastle.md)) | The host | Answers threads and red checks on its own PRs and those the owner handed over. |

They share no code. `land.sh` and `status.sh` are the skill's Python and don't ship to repos; the watcher is an Owned script that does, and
it reads only what the session needs (behind, checks, threads, people's words), not Copilot, `review:claude` or the landing decision.
A test holds that it makes no call but `gh api graphql` and no write.

## Adapting

When to run it is each repo's own rule, so it isn't in the Template. Add a line to the repo's `CLAUDE.md`, for example: after opening a PR, run
`scripts/watch-pr.sh <pr>` in the background and act on what it prints. dotfiles' own line is under its Commands.
