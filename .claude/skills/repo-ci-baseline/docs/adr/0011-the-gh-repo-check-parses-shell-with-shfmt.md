# The gh repository check parses shell with shfmt

**Status:** accepted (mpaulosky/Application_Factory#52). Implemented in `template/owned/.github/scripts/check_gh_repo.py` (`parse`, `parse_prefix`, `Shape`, `untold`), its tests in
`tests/gh_repo/`, the Owned `ci.yml`'s `gh-repo` and `hook-tests` jobs, the Seed `.sandcastle/Dockerfile` and the Factory's `.github/actions/template-tools/`
([gotchas.md](../../references/gotchas.md), "Tell every `gh` command its repository").

## Context

`check_gh_repo.py` refuses a `gh` command in a script that isn't told its repository. Some of its rules depend on how bash reads the script: an `export GH_REPO=...`
in a branch that never runs, or in a subshell, tells nothing; a comment, a heredoc fed to `cat` and a quoted string are no code; a backslash joins lines. It read them
with a line scanner written for it, about 700 lines that knew quotes, `$(...)`, heredocs, `if`/`fi` and continuations. Every review round of mpaulosky/Application_Factory#49
and mpaulosky/Application_Factory#51 found another way it misread bash, and `references/gotchas.md` listed the rest as known limits: a case pattern `fi)`, a `(` left open at the end of a line,
backticks, a `${...}` holding a quote, a `}` or a `)`, quotes nested past 50 levels, a `[[ ... ]]` over several lines, a line of a multi-line string or a heredoc body
read as code, and chains of more than 100 continued lines. Some of these misread in the fail-open direction: an export the scanner trusted that bash never runs.

The check runs in three places in every Application: `scripts/gate.sh` on the developer's machine, CI's `gh-repo` job (the base branch's copy, under `python3 -I`), and
Sandcastle's sandbox, which runs the gate.

## Considered options

- **Keep the scanner and fix each misreading (rejected):** each fix was another special case, and each review found more. A shell parser written for one check is
  the problem, not any one bug in it.
- **Write a full bash parser in Python, standard library only (rejected):** no new dependency, but it is another few hundred lines of hand-written bash parsing
  that would need the same rounds of review as the scanner did.
- **bashlex (rejected):** a Python bash parser, but GPL-3.0, so it can't be copied into every Application, and a pip install breaks the isolated `python3 -I` runs.
- **shfmt's syntax tree (chosen):** `shfmt --to-json` prints the full syntax tree of a bash script. shfmt is maintained, BSD-3-Clause, one static binary with
  release builds for Linux and macOS, and packaged by the common package managers. The checker walks the tree in Python.

## Decisions

- **The checker runs `shfmt -ln bash --to-json` on each shell file and judges the tree,** not lines. Comments, heredoc bodies, substitutions, strings, `if`/`elif`/`else`
  branches, subshells, pipes and continuations come from shfmt. The rules about a single `gh` command (its subcommand, its `GH_REPO=` prefix, a wrapper such as `sudo`
  before it) stay as they were, applied to the words of the command the tree puts the `gh` in.
- **shfmt is a requirement wherever the check runs,** v3.14 or a later v3 release: the checker was written and tested against v3.14's JSON,
  and a new major version may change its shape. CI's `gh-repo` job and its `hook-tests` job (whose `pre-push.test.sh` runs the gate), and the Seed Sandcastle
  `Dockerfile`, install the v3.14.1 release binary, checked against the SHA-256 GitHub publishes for it; `test.sh` checks that every one of them pins the same release.
  The Factory's `template-tools` action installs the same release only so `test.sh` and the Template's tests can run: the Factory's own gate runs no gh check, so
  this adds no Baseline piece to the Factory. A developer installs shfmt with their package manager or from the release binaries. Without shfmt, or with one
  older or newer, the checker fails with exit 2 and says which it found: the gate can't pass a script it didn't read. A tree of another shape than the checker reads
  fails the same way. A repo whose files in scope are all skills (Markdown) never needs it.
- **shfmt reads nothing of the PR's but the script:** it runs in an empty folder of the checker's own, with only `PATH` in its environment. A version
  manager's shim that needs more than `PATH` fails there; the release binary on `PATH` doesn't.
- **A script that isn't valid bash is read up to its syntax error.** bash runs a script one complete statement at a time and stops at a syntax error, so the
  statements before the error are judged on the tree, and their export counts. The lines from the error on are judged one line at a time, as code, with no export of their
  own trusted (an `unset` there still ends one, and a wrapper on an earlier line a backslash joins to the `gh`'s drops the export). The checker parses at
  most five times to find the statements before the error, the whole script first (`PREFIX_TRIES`).
- **The text read for one `gh` is bounded:** a `gh` with more than 4096 characters before it in its own command (`BEFORE_CHARS`), joined lines
  included, or more than 100 continued lines before it after a syntax error (`JOINED_LINES`), is refused, whatever tells it, and only the first 4096
  characters of the rest of its line are read. A long line of `gh` words, or a long command, then costs the checker time in proportion to its length.
- **A statement nested deeper than Python's recursion limit lets the checker walk** (a chain of about 190 `&&`, `||` or `|` links) is read the same way, from
  the script's first line: no export in the file is trusted. That is a limit, documented in `references/gotchas.md`, and it fails closed.
- **One known difference from bash is worked around:** shfmt v3.14 joins the line after a comment that ends in a backslash onto the comment, where bash ends the comment
  at the newline and runs the next line, so a valid script may even fail to parse. When the tree holds such a comment, or the script doesn't parse, the checker gives
  shfmt the trailing backslashes of every line with a `#` on it as spaces, which bash reads the same inside a comment, and parses again. A space the new tree puts in
  no comment gets its backslash back, as does one on the line right before a syntax error the blanks brought (a real continuation, whose next line starts
  with `|` or `&&`; a comment's blank put back this way that a later tree puts in a comment again leaves the syntax error where it was found). Each blank put
  back costs a parse. After 10 parses (`COMMENT_PASSES`) whose blanks keep changing, the whole script is read as one that isn't valid bash: a script with a
  comment that ends in a backslash and more than eight such continuations, for one, which fails closed.
- **The `# gh-repo: ok` marker counts only as a comment** in the lines the tree reads: one inside a string, or a heredoc's delimiter, no longer skips its line.
- **The checker starts no process but shfmt.** Its test watches every process it starts.

## Consequences

- The documented limits are gone; their cases are tests now. An export in a pipe (`echo a | export GH_REPO=...`) no longer counts, since bash runs it in a subshell.
- Rows of the old table tests that were not valid bash changed their expected result: what follows the syntax error no longer has its export trusted. They are marked in the tests.
- Every Application needs shfmt installed for its gate. The Rollout that brings this checker says so, and an Application whose Seed `.sandcastle/Dockerfile` was written
  before this change adds the shfmt download by hand (a Seed file isn't overwritten).
- The checker starts one shfmt per shell file, plus one `shfmt --version` per run, and more only for a file with a comment that ends in a backslash or a
  syntax error. On the developer machine this was measured on, the test of a repo of 100 scripts and 100 skills took 0.34 s.
- An `elif` or `else` after a plainly true condition, and the right side of `false &&` or `true ||`, now never run: an export there no longer counts.
- A new shfmt major version needs the checker read against its JSON first; the version check stops a v4 until then.
