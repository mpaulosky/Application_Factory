# The backfill_blog_posts parity suite

The Template's `.github/scripts/backfill_blog_posts.py` through its command line: `tests/parity.test.sh backfill_blog_posts [command...]`. The runner's header
says what a case holds. It writes its posts with `release_post.py`, whose own suite covers what a post holds; these cases cover which posts are written, and
the tables refreshed once after them.

The cases cover the missing posts written oldest first, every release with a post (the tables only), `--regenerate` (`" TRUE "` is true, `yes` isn't),
`--pr-numbers` with `#`, spaces and an empty part, an unmarked Release (skipped with a notice) and a draft (not listed), `--wait-for-prs` listed at once and on the
second listing (one 2-second wait, from the numbered fixtures `rest.<path>.1.json` and `.2.json`), an empty repo with the repo from `GITHUB_REPOSITORY`, no
Release at all with `--repo` over `REPOSITORY`, the hostile Markdown corpus of the `release_post` suite, and the usage errors (exit 2).

Cases 16 to 20 add:

- 16: the posts written in version order. v1.2.0, v1.9.0, then v1.10.0, which is neither the listing's order, the PRs' numbers nor the tags' string order.
- 17: CRLF and a UTF-8 BOM in the docs the tables rewrite. They come out with LF line endings and keep the BOM.
- 18 and 19: `REPOSITORY` over `GITHUB_REPOSITORY`, and an empty `REPOSITORY` falling back to it.
- 20: an `ANTHROPIC_API_KEY` and `ANTHROPIC_MODEL` of only blanks count as unset.

Not covered, on purpose:

- A wait that runs out: 15 listings 2 seconds apart take half a minute of real time. The unit tests cover it with the sleep stubbed.
- The AI summary and a PR with no merge time, for the reasons the `release_post` suite gives.
- A `gh` failure: the script ends in a Python traceback, which is no contract a port should copy.
- Option abbreviations (`--regen` for `--regenerate`): argparse accepts them, but no caller relies on them and a port's parser needn't copy them.
