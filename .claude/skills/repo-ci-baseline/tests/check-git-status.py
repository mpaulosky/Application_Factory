"""A guard that reads `git status --porcelain` in an Owned shell script names its untracked-files setting: print each call that doesn't and exit 1.

    python3 -I check-git-status.py <the Template's owned/ folder>      (tests/check-git-status.sh runs this)

Exit 0: none found. Exit 1: found, printed as file:line:text. Exit 2: the folder is missing, or the folder or a file in it could not be read (nothing was fully checked).
Without --untracked-files=<mode> or -u[<mode>] (=all, or =no when ignoring them is the point), a user's status.showUntrackedFiles=no hides an untracked file from the
guard, so a "clean tree" check passes on a dirty one. A script's tests, Markdown and Sandcastle's TypeScript are not scanned: a test sets its own repo's config, and
Sandcastle runs `git status` in a fresh sandbox clone. Scanned: *.sh and the three git hooks, in any folder below the one given except a `tests` folder.

A logical line is a line plus the lines a trailing backslash joins to it (a comment line is not joined: bash does not continue a comment). A call is the words after `git`
up to the first unquoted `|`, `;`, `&` or `#`, or the `)` that closes the context the call is in (a substitution inside the call, `$(...)`, `(...)` or backticks, quoted or not, is part of it but blanked: its words are not the call's), so a `-u` of `sort -u` or `git add -u` after it, or text in a trailing comment, is not the call's setting. The call is
checked when one of its words is `status` and one starts with `--porcelain`; git's global options (-C <dir>, --no-pager, --git-dir=<dir>) come between and are skipped over
as words, quoted values with spaces included, since the call is cut by quote parity.
"""

import os
import re
import sys

HOOKS = {"pre-commit", "pre-push", "post-checkout"}
SETTING = re.compile(r"^(--untracked-files(=.*)?|-u(all|no|normal)?)$")
GIT = re.compile(r"(?<![\w-])git(?=\s)")


def logical_lines(text):
    """Yield (number of the first line, joined text)."""
    buf, start = "", 0
    for number, line in enumerate(text.split("\n"), 1):
        if not buf:
            start = number
        if line.rstrip().endswith("\\") and not (not buf and line.lstrip().startswith("#")):
            buf += line.rstrip()[:-1] + " "
            continue
        yield start, buf + line
        buf = ""
    if buf:
        yield start, buf


def call_after(line, pos):
    """The call's own text from `pos`: up to the first unquoted | ; & or # outside any substitution, or to the ) that closes the context the call is in. What is inside a
    substitution ($( ... ), ( ... ) or backticks, quoted or not) is blanked out: it is another command's words, never the call's `status`, `--porcelain` or setting.
    A stack of contexts (single quote, double quote, substitution, backtick) says where a character is; a substitution inside double quotes opens a code context again."""
    stack = []
    kept = []
    i = pos
    while i < len(line):
        c = line[i]
        nxt = line[i + 1] if i + 1 < len(line) else ""
        top = stack[-1] if stack else None
        hidden = "sub" in stack or "bt" in stack
        width = 1
        if top == "sq":
            if c == "'":
                stack.pop()
        elif top == "dq":
            if c == "\\":
                width = 2
            elif c == '"':
                stack.pop()
            elif c == "$" and nxt == "(":
                stack.append("sub")
                width = 2
            elif c == "`":
                stack.append("bt")
        elif top == "bt":
            if c == "\\":
                width = 2
            elif c == "`":
                stack.pop()
            elif c in "\"'":
                stack.append("dq" if c == '"' else "sq")
        else:  # a code context: the call itself (no stack) or the inside of a substitution
            if c == "\\":
                width = 2
            elif c == "'":
                stack.append("sq")
            elif c == '"':
                stack.append("dq")
            elif c == "`":
                stack.append("bt")
            elif c == "$" and nxt == "(":
                stack.append("sub")
                width = 2
            elif c == "(":
                stack.append("sub")
            elif c == ")":
                if top != "sub":
                    break
                stack.pop()
            elif not stack and (c in "|;&" or (c == "#" and (i == pos or line[i - 1].isspace()))):
                break
        blank = hidden or "sub" in stack or "bt" in stack
        kept.append(" " * width if blank else line[i : i + width])
        i += width
    return "".join(kept)


def unguarded(line):
    for match in GIT.finditer(line):
        words = call_after(line, match.end()).split()
        if "status" in words and any(w.startswith("--porcelain") for w in words) and not any(SETTING.match(w) for w in words):
            return True
    return False


def scan(root):
    """Return (problems, error): the file:line:text of each unguarded call, or an error message."""
    found = []
    failure = []
    for folder, dirs, files in os.walk(root, onerror=lambda e: failure.append(f"could not list {e.filename}")):
        dirs[:] = sorted(d for d in dirs if d != "tests")
        for name in sorted(files):
            if not (name.endswith(".sh") or name in HOOKS):
                continue
            path = os.path.join(folder, name)
            try:
                with open(path, encoding="utf-8", errors="replace") as handle:
                    text = handle.read()
            except OSError:
                return found, f"could not read {path}"
            for number, line in logical_lines(text):
                if not line.lstrip().startswith("#") and unguarded(line):
                    found.append(f"{path}:{number}:{line.strip()}")
    return found, (failure[0] if failure else None)


def main(argv):
    if len(argv) != 2 or not os.path.isdir(argv[1]):
        print("usage: check-git-status.py <the Template's owned/ folder, which must exist>", file=sys.stderr)
        return 2
    found, error = scan(argv[1])
    if error:
        print(error, file=sys.stderr)
        return 2
    for line in found:
        print(line)
    if found:
        print("Give each git status --porcelain above --untracked-files=all (or =no on purpose), so status.showUntrackedFiles can't weaken the check.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
