# The rollout parity suite

`rollout.sh` through its command line: `tests/rollout-parity.test.sh [command...]`. The runner's header says what a case holds, and how each run gets a
temporary copy of the skill folder (rollout calls `reapply.sh` and `land.sh` beside itself, so those two are stubs there), the fixed `repos.txt` beside this
file, a stand-in Template (`template/`: the `global.json`, `pre-push-review` skill and `pre-push-reviewer` agent `--auto` reads), a HOME of its own, and a
PATH of fakes and the host's own commands only. One suite judges `rollout.py` and `auto_rollout.py`: `auto_rollout.py` has no command line of its own, and
`rollout.py` runs it for `--auto` and reads TicketManager's re-Apply PR and branch through it in plain mode too (ADR 0017; they are ported together).
The cases were recorded from `rollout.sh`, which runs `rollout.py`.

The cases cover plain mode: `--dry-run` with TicketManager's PR merged (the rest's calls follow) and with none found (held, exit 1), and `--root` with a space in
it (the calls quote it); a missing checkout, which stops the round before anything runs (exit 1); no `--brings` (exit 2); `reapply.sh` exiting 3 and 4 on
TicketManager, each with what to run first; a held round (TicketManager's PR open: `land.sh` for it, exit 0); a merged PR, after which the rest are reapplied in
`repos.txt`'s order with TicketManager moved first and the `factory` and `repo-settings-only` repos left out, one failing (exit 1) and one leaving a PR for
`land.sh`; TicketManager already matching the Template (no PR, and its re-Apply worktree has no commit main lacks), so the rest go on; a merged PR while the
worktree has commits main lacks (held); and an unreadable PR and an unreadable re-Apply branch (held). The stubs print on stdout and stderr, so the cases pin
rollout's own lines around its child's: each `── rollout:` line comes before the child's output.

The `--auto` cases (`*-auto-*`) cover:

- the dry run: the preflight's calls, then the plan, and nothing run;
- one per preflight problem, each stopping the round before anything runs (exit 1): a tool off PATH, a sandbox tool off PATH, neither ruff nor uv, a ruff that
  isn't 0.17, a Claude Code sign-in secret set, a proxy holding a URL with a sign-in, bubblewrap unable to make the sandbox, a `claude` that doesn't run, its
  sign-in status failing, saying nothing of `loggedIn`, and signed out, an shfmt older than v3.14, `gh` signed out, and no SDK matching the Template's
  `global.json`;
- the lock: held by another process (`setup.sh` holds it, `after.sh` ends that), unable to be taken (a file where its folder goes), and `reapply.sh` holding
  its descriptor (another `flock` of it is refused);
- signals, each sent to the wrapper's PID: SIGTERM (143) and SIGINT (130) while `reapply.sh` runs, each with the resume line, and `after.sh` checks the child's
  group is gone; SIGTERM while no child's group runs (the round's own handler, 143); and a SIGHUP ignored from the start, which stays ignored, in the round
  and its child, and the round goes on;
- `reapply.sh` exiting 3, a review session that stages nothing and is recorded (`scripts/review.sh` prepare, record and advisory), then 0, with TicketManager's
  open PR landed and the rest's after; exiting 4; and exiting 3 three times, past the cap of two review sessions;
- TicketManager already matching the Template (the last round's PR closed at another head), so the rest go on, one failing; and its PR closed at the worktree's
  head, which holds the rest.

`defaults/` answers what most `--auto` cases share, under each case's own `fakes/` and `gh/` (the runner's header says how): `bwrap` makes the sandbox,
`claude` runs, is signed in and, given `-p`, stages `[]` as a review session; `shfmt` is v3.14.0, `dotnet` has the stand-in Template's SDK, `node` names no
other path, and `gh auth status` is signed in. Its `git` is a checkout whose git folder is `<checkout>/.git` and its re-Apply worktree, at HEAD `2222…` with
nothing changed, ahead of main unless the case sets `FAKE_GIT_AHEAD=0`; its `review.sh` is the worktree's `scripts/review.sh`; and `make-worktree.sh`,
which a case's `reapply.sh` runs, makes that worktree in HOME. Anything else they're asked is logged as `UNMATCHED` and fails the case.

Not covered, on purpose:

- The default `github/repos.txt` and a `repos.txt` without TicketManager: every case runs on the copy's fixed one.
- `git` itself: it is a fake, so the cases pin rollout's calls to it, not what a real repository answers. A review session's after-session checks see an
  unchanged worktree only; the rules a session can break (a moved HEAD, a changed file, a finding staged fixed) are left to `github/tests/test_auto_rollout.py`.
- `claude` itself: the review session is a fake that stages `[]`, so the sandbox and permissions the round hands it are pinned only as its command line has them.
- A system other than Linux: the preflight reads the platform from Python itself, with no seam a case could set.
- `--auto`'s waits, which take that long in real time and have no seam a case could shorten: a review session's 180 minutes, the 10 seconds a killed session's
  process group and a PR lookup each get, and the 600 seconds a review record gets.

One limit: a review session's sandbox lists every folder on the way to HOME, `/tmp` among them, when the session starts and again when it ends. An entry made
in one of them meanwhile (the fraction of a second the fake session takes) is a warning in the log, and one whose name holds a `*?[]{}` character stops the
session, so either fails a review case on a host busy writing `/tmp`.
