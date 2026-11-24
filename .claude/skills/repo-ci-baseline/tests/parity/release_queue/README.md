# The release_queue parity suite

The Template's `.github/scripts/release_queue.py` through its command line: `tests/parity.test.sh release_queue [command...]`. The runner's header says what a case holds.

Each case's `setup.sh` builds main's history in the run's folder, one empty commit per merge with the Release tags on theirs, and a local bare origin. Its commits
get the same hashes on every run, and the `gh` fixtures' `merge_commit_sha` names them. Cases 04 to 08 push one merge to origin after the checkout, so the queue
holds it only because the script fetches main.

The cases cover a first release (only the triggering PR, or nothing), Releases that are no cutoff (drafts, no marker, a tag that isn't `vX.Y.Z`), the merges
after the cutoff in main's order rather than by merge time, a run with and without `--pr`, a trigger the list doesn't hold (fetched) and one merged into another
branch (not queued), the PRs that aren't owed a release (a `[skip-release]` title, Dependabot, one another Release names, a merge inside the cutoff's tag), the
cutoff by version rather than list order, a second page, an empty page, a manual run for an older PR, and the usage errors (exit 2). stdout pins `json.dumps`
spacing: `[3, 2, 4]` and `[]`.

Case 16 keeps each rule apart: one PR is out only because the cutoff's tag holds its merge, one only because Dependabot opened it, one only because an older
Release names it, and one has `[skip-release]` and no merge commit. Cases 17 to 19 fetch a trigger the list doesn't hold that isn't owed: Dependabot's (its
login under `user`, not `author`), one with `[skip-release]`, and one never merged. Case 20 takes v1.10.0 over v1.9.0 as the cutoff, which string order would
reverse. Case 21 queues a merge in the cutoff's own second and goes on to the next page while a PR's update time equals the cutoff's merge time; case 22 stops
at a PR with no update time. Case 23 is a failed Release listing: exit 1, nothing on stdout, and stderr matched only as not empty, since the Python's is a
traceback. Case 24 gives `--rep` for `--repo`: an abbreviated option is accepted, as argparse does.

Not covered, on purpose:

- The other exits 1: a `git` failure, and a merge commit missing from the fetched main. The unit tests in the Template's `.github/scripts/tests/` cover the
  missing merge commit.
