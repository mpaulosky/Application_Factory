# The rollout parity suite

`rollout.sh` through its command line: `tests/rollout-parity.test.sh [command...]`. The runner's header says what a case holds, and how each run gets a
temporary copy of the skill folder (rollout calls `reapply.sh` and `land.sh` beside itself, so those two are stubs there), the fixed `repos.txt` beside this
file, and a HOME of its own. One suite judges `rollout.py` and `auto_rollout.py`: `auto_rollout.py` has no command line of its own, and `rollout.py` runs it
for `--auto` and reads TicketManager's re-Apply PR and branch through it in plain mode too (ADR 0017; they are ported together).
The cases were recorded from `rollout.sh`, which runs `rollout.py`.

The cases cover plain mode: `--dry-run` with TicketManager's PR merged (the rest's calls follow) and with none found (held, exit 1), and `--root` with a space in
it (the calls quote it); a missing checkout, which stops the round before anything runs (exit 1); no `--brings` (exit 2); `reapply.sh` exiting 3 and 4 on
TicketManager, each with what to run first; a held round (TicketManager's PR open: `land.sh` for it, exit 0); a merged PR, after which the rest are reapplied in
`repos.txt`'s order with TicketManager moved first and the `factory` and `repo-settings-only` repos left out, one failing (exit 1) and one leaving a PR for
`land.sh`; TicketManager already matching the Template (no PR, and its re-Apply worktree has no commit main lacks), so the rest go on; a merged PR while the
worktree has commits main lacks (held); and an unreadable PR and an unreadable re-Apply branch (held). The stubs print on stdout and stderr, so the cases pin
rollout's own lines around its child's: each `── rollout:` line comes before the child's output.

Not covered, on purpose:

- `--auto`: its cases are mpaulosky/Application_Factory#156's, on this runner.
- The default `github/repos.txt` and a `repos.txt` without TicketManager: every case runs on the copy's fixed one.
- `git` itself: it is a fake, so the cases pin rollout's calls to it, not what a real repository answers.
- `--auto`'s waits, which take that long in real time and have no seam a case could shorten: a review session's 180 minutes, the 10 seconds a killed session's
  process group and a PR lookup each get, and the 600 seconds a review record gets.
