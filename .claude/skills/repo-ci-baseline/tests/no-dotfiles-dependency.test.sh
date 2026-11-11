#!/usr/bin/env bash
# Tests for mpaulosky/Application_Factory#13: the Template does not depend on the mpaulosky/dotfiles repo, which is being archived.
#
# A mention of `mpaulosky/dotfiles` in the Template (template/owned and template/seed) fails, except a reference to one of its
# issues or pull requests (`mpaulosky/dotfiles#58`): those are history in comments, and stay readable while the repo is archived.
#
# KNOWN lists the mentions that still exist and the number in each file. It can only shrink: a file not on it that mentions the repo
# fails (a new dependency), and so does a listed file whose count differs (a mention removed without updating the list, or added to
# a listed file). Ticket #14 removes them and empties the list.
# Usage: tests/no-dotfiles-dependency.test.sh
set -uo pipefail

SKILL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

python3 -I - "$SKILL_DIR" <<'PY'
import re
import sys
import tempfile
from pathlib import Path

skill = Path(sys.argv[1])
# `mpaulosky/dotfiles` not followed by `#` and a digit. A longer name ("mpaulosky/dotfiles-x") is also a mention. GitHub owner and
# repository names are case-insensitive, so `MPaulosky/Dotfiles` is the same repo.
MENTION = re.compile(r"mpaulosky/dotfiles(?!#[0-9])", re.IGNORECASE)

KNOWN = {
    "template/owned/.github/scripts/check_gh_repo.py": 1,
    "template/owned/.github/workflows/claude-review.yml": 1,
    "template/owned/.sandcastle/lib/review-ledger.test.mts": 3,
}

passed = failed = 0


def check(name, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
        print(f"ok   - {name}")
    else:
        failed += 1
        print(f"FAIL - {name}" + (f": {detail}" if detail else ""))


def read(path):
    # Undecodable bytes (a Windows-1252 character in a comment) must not hide a mention: keep them as surrogates, not skip the file.
    return path.read_bytes().decode("utf-8", errors="surrogateescape")


def count_in(text):
    return len(MENTION.findall(text))


# The pattern itself: what counts as a mention.
check("a bare mention counts", count_in("clone mpaulosky/dotfiles now") == 1)
check("the clone URL counts", count_in("https://github.com/mpaulosky/dotfiles.git") == 1)
check("a mention at the end of a line counts", count_in("see mpaulosky/dotfiles") == 1)
check("an issue reference does not count", count_in("fixed in mpaulosky/dotfiles#58") == 0)
check("a pull request reference with many digits does not count", count_in("mpaulosky/dotfiles#1234") == 0)
check("a '#' that is not followed by a digit counts", count_in("mpaulosky/dotfiles#") == 1 and count_in("mpaulosky/dotfiles#x") == 1)
check("a longer repo name counts", count_in("mpaulosky/dotfiles-old") == 1)
check("each mention on a line counts", count_in("mpaulosky/dotfiles and mpaulosky/dotfiles#1 and mpaulosky/dotfiles") == 2)
check("a mixed-case clone URL counts", count_in("https://github.com/mpaulosky/Dotfiles.git") == 1 and count_in("MPaulosky/DOTFILES") == 1)
check("a mixed-case issue reference does not count", count_in("MPaulosky/Dotfiles#58") == 0)
check("another repo does not count", count_in("mpaulosky/Application_Factory and other/dotfiles") == 0)

# A file with bytes that are not UTF-8 is still scanned: the function the walk uses reads it.
with tempfile.TemporaryDirectory() as tmp:
    bad = Path(tmp) / "x.sh"
    bad.write_bytes(b"# \xa9 2022\ngit clone https://github.com/mpaulosky/dotfiles.git\n")
    check("undecodable bytes do not hide a mention", count_in(read(bad)) == 1)

# The Template.
found = {}
files = 0
for root in ("template/owned", "template/seed"):
    for path in sorted((skill / root).rglob("*")):
        if not path.is_file() or ".git" in path.relative_to(skill).parts:
            continue
        text = read(path)
        files += 1
        n = count_in(text)
        if n:
            found[str(path.relative_to(skill))] = n

check("the walk read the Template's files", files > 100, f"read {files}")
for rel in sorted(set(found) | set(KNOWN)):
    check(
        f"{rel} mentions mpaulosky/dotfiles as often as the known list says",
        found.get(rel, 0) == KNOWN.get(rel, 0),
        f"found {found.get(rel, 0)}, known {KNOWN.get(rel, 0)}",
    )

print(f"\n{passed} passed, {failed} failed")
sys.exit(1 if failed else 0)
PY
