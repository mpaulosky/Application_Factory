# The release_post parity suite

The Template's `.github/scripts/release_post.py` through its command line: `tests/parity.test.sh release_post [command...]`. The runner's header says what a
case holds. A case's tree is the repo's docs before the run, and its `expected.tree/` every file after it.

The cases cover a first post into an empty repo, a full repo (the README's table between its markers, `docs/README.md` with its links rebased, the Pages site's
table and cards, the blog index dropping a row for a deleted post, a draft Release left out), a re-run for a renamed PR (its old post removed), a README with no
markers (with an `## About` section and a legacy Dev Blog block, or no sections and no final newline), an `index.html` with one pair of markers, the ten newest
of thirteen Releases (one with no marker and no name, one whose title can't be read), a Dependabot PR with no title, commits, files or description and a padded
upper-case tag, the repo from `GITHUB_REPOSITORY` or from `--repo` over the environment, a long diff and lock files, and the usage errors (exit 2).

Case 08 is the hostile corpus: a title and body with astral characters, named, numeric and unknown entities, non-ASCII case (`ſ`, `K`, `İ`), U+2028 and
U+2029, Liquid, HTML, `javascript:` links, reference definitions, a CR, and commit subjects and file names that would start a definition or break a code span.
The file list pins the order of `tests/a-b/x.cs` before `tests/a/x.cs`, a sort by the name as a string.

Not covered, on purpose:

- The AI summary: with `ANTHROPIC_API_KEY` set, the script calls `ANTHROPIC_URL`, which is fixed, so a case would call the real API.
  No case sets the key, and the unit tests cover the request and its failures.
- A PR with no merge time: the post is dated today, and the clock isn't fixed.
- A `gh` failure other than a title's: the script ends in a Python traceback, which is no contract a port should copy.
- Option abbreviations (`--ta` for `--tag`): argparse accepts them, but no caller relies on them and a port's parser needn't copy them.
