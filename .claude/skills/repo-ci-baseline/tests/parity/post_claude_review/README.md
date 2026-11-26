# The post_claude_review parity suite

The Template's `.github/scripts/post_claude_review.py` through its command line: `tests/parity.test.sh post_claude_review [command...]`. The runner's header
says what a case holds. The cases were recorded from the `.py`, the reference its C# port (`post_claude_review.cs`, mpaulosky/Application_Factory#129)
is held to, and `test.sh` runs them against the `.py` (and the `.cs` too, once it ships beside it). Each case gives Claude Review's answer in `FINDINGS`
(and the review job's count in `REDACTED` where it has one), and the fake gh answers the five calls the script makes: the PR's files, a file's contents
for its length, the review's post, its comments read back and the edit that links them. Each call's argv is pinned in `expected.calls`, the posted
review's JSON among it.

The cases cover findings on the diff (threads linked from the body by the edit), none (no changes recommended), one off the diff (the body with the off-diff
marker), a line past the end of a changed file and one of 0 (04, on its nearest diff line), a length gh can't read and a file the diff doesn't touch (both
in the body), the structured answer with every section, credentials redacted and counted, suggestions vetted, thread links matched by path and body or left
unlinked (26) and taken by order (04, whose bodies don't match), a file without a patch and a path that needs quoting, and the effort kept on one line.
Then what a port is likely to get wrong:

- 16 and 36: an option given by a prefix. `--he=abc123` is ambiguous with `--help` and exits 2; 36's `--re`, `--p`, `--hea=` and `--ef` are accepted, as
  argparse does, and `--p ' +0_7 '` is PR 7, as Python's `int()` reads it. Python 3.14's error names the ambiguous option as given (`--he=abc123`),
  and the `Arguments` every port shares from `Gh.cs` names it without its value (`--he`), so 16's pattern takes either.
- 27: a hunk whose lines hold a form feed, U+2028 and U+0085, which split a line for .NET's and Python's `splitlines()` but not for the script.
- 28: a line past a 64-bit integer, which stays in the body. 34: an astral digit in a hunk header (U+1D7D3, a 5 to Python's `\d` and `int()`, and a
  surrogate pair in .NET), and a file listed twice, whose lines from both listings take a thread. 31: a key repeated in the answer takes its last value.
- 33 and 49 to 52: a body past GitHub's 65,536 characters, cut to fit with the markers and off-diff findings kept: What changed first (33), then
  Advisory and Not reviewed (49), then the open issues past what fits, counted (50, in both the first body and the linked one). 51's body is exactly
  65,536 characters and is kept; 52's is one more and is cut. Each answer is under the kernel's 128 KB limit on one environment variable.
- 17 and 18: a failed file listing or post (exit 1, with a traceback the pattern takes from either). 19 to 24: the linking step's failures (a comment
  read or edit that fails, a post with no id, one that isn't an object or is empty, comments that aren't JSON), each a warning with the first body kept.
- 09 to 15, 29, 32, 35, 37 to 48, 69 and 70: the exits for findings that aren't JSON or name another severity (null among them), each other way an
  answer is malformed (one that isn't an object, no summary or overview, a summary that isn't a string with no overview, a field of the wrong type, a file
  with no description, a line of `true`, `2.0` or `"3"`, a finding with no body after a good one), empty findings (a secret withheld), no head, a PR of 0
  or not a number, an unknown option, a `REDACTED` that isn't a number, a positional argument and a malformed file listing.
- 53 and 54: a file's length is its newlines counted in bytes (an empty file, a last line with no newline, CRLF, a lone CR and bytes that aren't UTF-8),
  a line equal to it stays in the body and one past it moves, as does a negative line; a tie for the nearest line takes the lower.
- 55: every shape of credential, the 35 and 36 characters of a classic token, a run of 128 and 129 characters with a credential's start and one of 129
  without, a path of 1,024 and 1,025 characters, and a credential in every field of the answer and in a suggestion, which is then defused.
- 56: every rule a suggestion is vetted by, kept (a `~~~` fence, spaces after the info string, a tab) or defused (the legacy form, empty, blank, a blank
  filler or a combining mark alone, a line that closes the fence, a format, separator or control character, `[redacted]`, two fences).
- 57: comments whose URL isn't `https` (`javascript:`, a number) or that aren't objects (`null`) leave the body unedited. 58: identical comments take
  their URLs in the order listed, past a `null` and a string among them.
- 59: a `\ No newline at end of file` row isn't a line, a null patch has none, a header that isn't a hunk's starts none and a `+++` row in a hunk is one.
- 60 to 62: an effort of 200 characters is kept, one of 201 is cut and a blank one is none; titles of 200 and 201 characters, one cut after a space,
  one of `**` markers alone and an empty body. 63: a path's characters quoted in the contents URL. 64 to 68: `--pr -3` sent as it is, an option with
  no value, an empty `REDACTED` (0), findings with a byte-order mark (not JSON) and unset findings (withheld).

The usage lines name the script as `post_claude_review.py` or `post_claude_review.cs`, and a traceback's last line may name Python's exception or the port's
(`CalledProcessError`, `ValueError` or `KeyError` as `PyError`): a crash's text is no contract a port should copy, only its exit code, so 29 and 35 pin
its type and no more, and 17 and 18 its type and that the command exited 1.

Not covered, on purpose:

- `-h`: argparse wraps its help text to the terminal's width, which the port doesn't copy.
- JSON that only Python reads (`NaN`, `Infinity`, a lone surrogate escape) and a float or a container as the posted review's id: GitHub sends none of them.
- An astral letter straight after a suggestion fence's info string, and characters whose Unicode class differs between Python's and .NET's Unicode versions.
- A `REDACTED` of 0 or below and an empty `--repo`: the review job writes a count, and the workflow passes the repository.
