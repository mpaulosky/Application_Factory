# The release_post parity suite

The Template's `.github/scripts/release_post.py` through its command line: `tests/parity.test.sh release_post [command...]`. The runner's header says what a
case holds. A case's tree is the repo's docs before the run, and its `expected.tree/` every file after it.

The cases cover a first post into an empty repo, a full repo (the README's table between its markers, `docs/README.md` with its links rebased, the Pages site's
table and cards, the blog index dropping a row for a deleted post, a draft Release left out), a re-run for a renamed PR (its old post removed), a README with no
markers (with an `## About` section and a legacy Dev Blog block, or no sections and no final newline), an `index.html` with one pair of markers, the ten newest
of thirteen Releases (one with no marker and no name, one whose title can't be read), a Dependabot PR with no title, commits, files or description and a padded
upper-case tag, the repo from `GITHUB_REPOSITORY` or from `--repo` over the environment, a lock file and a 5,000-line file listed with their counts
(the diff itself only feeds the AI summary, which no case asks for), and the usage errors (exit 2).

Case 08 is the hostile corpus: a title and body with astral characters, named, numeric and unknown entities, non-ASCII case (`ſ`, `K`, `İ`), U+2028 and
U+2029, Liquid, HTML, `javascript:` links, reference definitions, a CR, and commit subjects and file names that would start a definition or break a code span.
The file list pins the order of `tests/a-b/x.cs` before `tests/a/x.cs`, a sort by the name as a string.

Cases 17 to 24 pin what a port is likely to get wrong:

- 17: `\v`, `\f`, U+0085 and U+001C to U+001E in a title, a commit subject and the PR description stay in the text: Markdown breaks lines only at CR and LF.
  The card's excerpt, read back with `splitlines()`, does split there, so the list markers after each separator are dropped.
- 18: card excerpts of 199 and 200 characters are kept whole. Excerpts of 201 are cut to 199 characters at the last space, with trailing `,` trimmed, and get a `…`.
  An excerpt with no space keeps all 199. One with an astral character at the cut keeps it whole: lengths count code points.
- 19: 15 posts give the ten newest as cards. Three posts on one day go in version order, v1.10.0 before v1.9.0 before v1.2.0.
- 20: the file list in code point order: `é`, U+FF5E and U+FFFD before an astral name, which a UTF-16 order would put first.
- 21: CRLF and a UTF-8 BOM in the README, `docs/README.md`, `docs/blogs/README.md` and `index.html`. Each file the run rewrites has LF, and keeps the BOM.
  A post whose BOM comes before its front matter is read without it: its card shows the file name, the date from the file name and no tag.
- 22: `REPOSITORY` wins over `GITHUB_REPOSITORY`. 23: an empty `REPOSITORY` falls back to `GITHUB_REPOSITORY`.
- 24: an `ANTHROPIC_API_KEY` and `ANTHROPIC_MODEL` of only blanks count as unset: no API call, and the post says no AI summary.

Not covered, on purpose:

- The AI summary: with `ANTHROPIC_API_KEY` set, the script calls `ANTHROPIC_URL`, which is fixed, so a case would call the real API.
  No case sets a key other than blanks (case 24), and the unit tests cover the request and its failures. So the model a blank
  `ANTHROPIC_MODEL` falls back to is never seen in a case.
- A PR with no merge time: the post is dated today, and the clock isn't fixed.
- A `gh` failure other than a title's: the script ends in a Python traceback, which is no contract a port should copy.
- Option abbreviations (`--ta` for `--tag`): argparse accepts them, but no caller relies on them and a port's parser needn't copy them.
