# The check_gh_repo parity suite

`template/owned/.github/scripts/check_gh_repo.py` through its command line: a tree of scripts and skills in, the untold `gh` commands and the exit code out. The runner's header
(`tests/parity.test.sh`) says what a case holds. It stands in for the white-box pytest tests (`template/owned/.github/scripts/tests/test_check_gh_repo.py`), which a port can't run.

Run it, against the Python or a port, and record it (`PARITY_RECORD=1`) with the shim first on `PATH`, under the name `shfmt`, as `test.sh` does; without it the
cases that stand for a shfmt the checker refuses (`013` to `015` and `061` to `065`) fail, the ones that stand for a release it accepts (`016`, `060`) pass without
testing anything, and nothing checks how the checker runs shfmt:

```bash
shim="$(mktemp -d)" && ln -s "$PWD/.claude/skills/repo-ci-baseline/tests/parity/check_gh_repo/shfmt-shim.sh" "$shim/shfmt"
PATH="$shim:$PATH" .claude/skills/repo-ci-baseline/tests/parity.test.sh check_gh_repo [command...]
```

`shfmt-shim.sh` hands each call to the real shfmt (v3.14 or a later v3, the next one on `PATH`), after checking that the checker runs it from an empty folder with nothing but
`PATH` in its environment. A case whose folder holds `.shfmt-version` gets that file's first line as shfmt's `--version`, so the suite can stand for an older or malformed
release (or `.shfmt-version-exit`, whose first line `--version` exits with), and one whose folder holds `.shfmt-parse` gets every other call answered from it (its first line the exit code, the rest the output), so it can stand for a shfmt that
fails or prints no syntax tree. The shim reads the checker's folder through `/proc`, so those cases need Linux; `tests/shfmt-shim.test.sh` tests the shim itself.

## Where the cases come from

- `001` to `018`, written by hand: both modes and every exit, the usage errors, the hint `--repo` prints, the order of `a/` and `a-b/` (Python's `sorted` on paths compares by
  segment, the reverse of an ordinal string sort), shfmt releases, a skill file named `.sh` (Python's `Path.suffix` is empty there, unlike C#'s `GetExtension`), and symlinks.
- `019` to `057`, the inputs the white-box tests give the checker, captured from a run of them by a pytest plugin, not kept, that logged each `untold`, `untold_markdown` and
  `check_repo` input: the fewest that between them tell every mutation in
  `tests/gh_repo/mutations.json` that any captured input tells from the original, each as `scripts/check.bash` (each case is named for the test it came from).
  A script is a `*.bash` here, never a `*.sh`, so the gate's shellcheck and shfmt, which take every `*.sh`, don't lint a fixture written to fail; the checker reads both as shell.
- `058`: every Markdown input the white-box tests give the checker but one holding a NUL byte, one skill file each: fences of backticks and tildes, in quotes and lists, and inline code.
- `059`: scripts that aren't UTF-8, and control bytes.
- `060` to `063`, written by hand after the review: shfmt v3.14.0, the oldest release accepted, and a shfmt that fails to parse, prints no syntax tree, or prints JSON
  that isn't an object (exit 2).
- `064` to `069`, written by hand after a second review: a shfmt whose `--version` fails, a syntax tree of a shape the checker doesn't read (exit 2), links to
  themselves and to each other, `scripts/` itself a link out of the repo or to nothing, and a link at an Owned path to a file that isn't Owned.

## Decided

- **The name in messages.** stderr is matched by its shape, and its prefix is `check_gh_repo` with any extension, so a port's messages may name the port's own file. The
  hint's text after its first sentence is free too: it still names `mpaulosky/dotfiles`, where the skill lived, and a fix to it shouldn't fail the suite.
- **No `.py` changes.** The suite records the Python as it is.

## Not covered, on purpose

- A file or folder the checker can't read (exit 2): the runner runs as whoever runs `test.sh`, root in some containers, who reads every file.
- shfmt missing from `PATH`, or hanging: the runner looks `python3` up on the same `PATH`, so a case can't take shfmt away alone. The shim's own test covers the shim.
- A script or skill file holding a NUL byte: the local review can't read a fixture git shows as binary, so `032` stands for the one such script the selection
  picked, and `058` leaves out the one such skill file.
- The Owned files `--repo` skips: their names end in `.sh`, which the gate would lint as fixtures.
- Four of the 67 mutations in `tests/gh_repo/mutations.json`, which no command line tells from the original: none of the 964 captured inputs does, run in-process against
  each mutant. The pytest suite catches them only from inside:
  - "an elif or an else is read as a statement" and "a backslash on a line with no # is blanked too": by calling `is_statement` and `backslash_runs` on their own;
  - "a run both a comment's and no comment's is blanked again and again" and "a blank put back for being in no comment is read as one put back for a break": by
    replacing shfmt with trees that change from one parse of the same text to the next, which a real shfmt doesn't give.
