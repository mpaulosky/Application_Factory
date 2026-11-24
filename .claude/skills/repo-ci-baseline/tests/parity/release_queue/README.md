# The release_queue parity suite

The Template's `.github/scripts/release_queue.py` through its command line: `tests/parity.test.sh release_queue [command...]`. The runner's header says what a case holds.

Each case's `setup` builds main's history in the run's folder, one empty commit per merge with the Release tags on theirs, and a local bare origin. Its commits
get the same hashes on every run, and the `gh` fixtures' `merge_commit_sha` names them. Cases 04 to 08 push one merge to origin after the checkout, so the queue
holds it only because the script fetches main.

The cases cover a first release (only the triggering PR, or nothing), Releases that are no cutoff (drafts, no marker, a tag that isn't `vX.Y.Z`), the merges
after the cutoff in main's order rather than by merge time, a run with and without `--pr`, a trigger the list doesn't hold (fetched) and one merged into another
branch (not queued), the PRs that aren't owed a release (a `[skip-release]` title, Dependabot, one another Release names, a merge inside the cutoff's tag), the
cutoff by version rather than list order, a second page, an empty page, a manual run for an older PR, and the usage errors (exit 2). stdout pins `json.dumps`
spacing: `[3, 2, 4]` and `[]`.

Not covered, on purpose:

- Exit 1: a `gh` or `git` failure, and a merge commit missing from the fetched main, end in a Python traceback, which is no contract a port should copy.
  The unit tests in the Template's `.github/scripts/tests/` cover the missing merge commit.
- Option abbreviations (`--rep` for `--repo`): argparse accepts them, but no caller relies on them and a port's parser needn't copy them.
