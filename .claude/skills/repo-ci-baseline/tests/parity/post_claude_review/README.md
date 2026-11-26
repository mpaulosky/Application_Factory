# The post_claude_review parity suite

The Template's `.github/scripts/post_claude_review.py` through its command line: `tests/parity.test.sh post_claude_review [command...]`. The runner's header
says what a case holds. The cases were recorded from the `.py`, the reference its C# port (`post_claude_review.cs`, mpaulosky/Application_Factory#129)
is held to. Each case gives Claude Review's answer in `FINDINGS` (and the review job's count in `REDACTED` where it has one), and the fake gh answers
the five calls the script makes: the PR's files, a file's contents for its length, the review's post, its comments read back and the edit that links them.
Each call's argv is pinned in `expected.calls`, the posted review's JSON among it.

The cases cover findings on the diff (threads linked from the body by the edit), none (no changes recommended), one off the diff (the body with the off-diff
marker), a line past the end of a changed file (on its nearest diff line), a length gh can't read and a file the diff doesn't touch (both in the body), the
structured answer with every section, credentials redacted and counted, suggestions vetted, thread links matched by path and body and then by order, a file
without a patch and a path that needs quoting, and the effort kept on one line. Then what a port is likely to get wrong:

- 16 and 36: an option given by a prefix. `--he=abc123` is ambiguous with `--help` and exits 2; 36's `--re`, `--p`, `--hea=` and `--ef` are accepted, as
  argparse does, and `--p ' +0_7 '` is PR 7, as Python's `int()` reads it. Python 3.14's error names the ambiguous option as given (`--he=abc123`),
  and the `Arguments` every port shares from `Gh.cs` names it without its value (`--he`), so 16's pattern takes either.
- 28: a line past a 64-bit integer, which stays in the body. 34: an astral digit in a hunk header (U+1D7D3, a 5 to Python's `\d` and `int()`, and a
  surrogate pair in .NET), and a file listed twice, whose lines from both listings take a thread. 31: a key repeated in the answer takes its last value.
- 33: a body past GitHub's 65,536 characters, cut to fit with the markers and off-diff findings kept. Its answer is under the kernel's 128 KB limit on one
  environment variable, so it reaches the limit with a long overview and advisory list rather than the 100,000-character fields the pytest uses.
- 17 to 24: a failed file listing or post (exit 1, with a traceback the pattern takes from either), and the linking step's failures (a comment read or edit
  that fails, a post with no id, one that isn't an object or is empty, comments that aren't JSON), each a warning with the first body kept.
- 09 to 15, 29, 32 and 35: the exits for findings that aren't JSON or name another severity, empty findings (a secret withheld), no head, a PR of 0 or not
  a number, an unknown option, a `REDACTED` that isn't a number, a positional argument and a malformed file listing.

The usage lines name the script as `post_claude_review.py` or `post_claude_review.cs`, and a traceback's last line may name Python's exception or the port's
(`CalledProcessError`, `ValueError` or `KeyError` as `PyError`): a crash's text is no contract a port should copy, only its exit code.

Not covered, on purpose:

- `-h`: argparse wraps its help text to the terminal's width, which the port doesn't copy.
- JSON that only Python reads (`NaN`, `Infinity`, a lone surrogate escape) and a float or a container as the posted review's id: GitHub sends none of them.
- An astral letter straight after a suggestion fence's info string, and characters whose Unicode class differs between Python's and .NET's Unicode versions.
