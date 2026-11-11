#!/usr/bin/env python3
"""A bare #N in an Owned file: print each one and exit 1.

    python3 -I check-issue-refs.py <the Template's owned/ folder>

Exit 0: none found. Exit 1: found, printed. Exit 2: nothing was (fully) checked: the folder is missing, the arguments are wrong, or a file or directory is unreadable. (An uncaught Python error also exits 1, with its traceback.)
test.sh runs it, and so does the Factory's scripts/gate.sh (also with --quick), so the rule fails before test.sh does.
The rule, with its reasons, is the comment in test.sh's "Issue references in Owned files name their repo" step.
"""
import os, pathlib, re, sys

CITATION = re.compile(r"\((?:[^()]*, )?#\d+\)")
BARE = re.compile(r"(?<![\w/&])#\d+\b")
# Examples, not references: the exact text each one sits in.
EXAMPLES = ("`Fixes #123`", "`Refs #456`", "Fixes #42", '"1, #3"', '("Fixes #16.")', "so PR #1 doesn't", "release for #10.")
if len(sys.argv) != 2 or not pathlib.Path(sys.argv[1]).is_dir():
    # A folder that isn't there has no files to find a bare #N in, which must not read as a pass.
    print("usage: check-issue-refs.py <the Template's owned/ folder, which must exist>", file=sys.stderr)
    sys.exit(2)
root = pathlib.Path(sys.argv[1])
found = []


def unlistable(error):
    # A directory it can't list is a set of files it didn't check, which must not read as a pass (os.walk skips it without this).
    print(f"{error}", file=sys.stderr)
    sys.exit(2)


files = sorted(pathlib.Path(folder, name) for folder, _, names in os.walk(root, onerror=unlistable) for name in names)
for path in files:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except UnicodeDecodeError:
        continue
    except (FileNotFoundError, IsADirectoryError):
        continue  # a dangling symlink, or a name that is no longer a file: nothing to check
    except OSError as error:
        # Any other error (is_file() would have hidden it, e.g. a file in a directory that lists but can't be searched) is a file it didn't check, which must not read as a pass or as a bare #N (exit 1).
        print(f"{path}: {error}", file=sys.stderr)
        sys.exit(2)
    in_tests = "tests" in path.relative_to(root).parts
    is_ts_test = path.name.endswith(".test.mts")
    for number, line in enumerate(lines, 1):
        text = line
        for example in EXAMPLES:
            # Not followed by a digit, so "Fixes #42" leaves "Fixes #421" whole.
            text = re.sub(re.escape(example) + r"(?!\d)", "", text)
        if CITATION.search(line) or (
            BARE.search(text) and not in_tests and (not is_ts_test or line.lstrip().startswith("//"))
        ):
            found.append(f"{path.relative_to(root)}:{number}: {line.strip()}")
if found:
    print("\n".join(found))
sys.exit(1 if found else 0)
