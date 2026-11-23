"""Checks that every gh command in the Template's Owned scripts and a repo's own scripts and skills is told its repository; the rules are in references/gotchas.md."""

import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

# A value that's never empty: literal (quoted or bare, with no expansion), or
# ${NAME:?...}, which stops the script when NAME is empty.
VALUE = (r"""("[^"$` \t]+"|'[^' \t]+'|[^ \t`"'$;&|,()<>]+"""
         r"""|"?\$\{[A-Za-z_][A-Za-z0-9_]*:\?[^}"'`]*\}"?)""")
EXPORTS = re.compile(r"^export GH_REPO=" + VALUE + r"[ \t]*(;[ \t]*)?(#.*)?$")
# gh or gh.exe as a word: not part of gh_, neigh or gh.executable, nor a folder in a path (~/.config/gh/hosts.yml).
GH_WORD = r"gh(?:\.exe)?(?![A-Za-z0-9_.-]|/)"
GH = re.compile(r"(?<![A-Za-z0-9_.-])" + GH_WORD)
# A line that says its gh isn't a command to run (command -v gh, echo "needs gh"):
# # gh-repo: ok in a script, <!-- gh-repo: ok --> in a skill.
OPT_OUT = re.compile(r"(#|<!--)[ \t]*gh-repo:[ \t]*ok(?![A-Za-z0-9_-])")
# Its subcommand, written out.
SUBCOMMAND = re.compile(r"[ \t]+(?=[a-z-])")
# A backslash continuation, or a word gh's subcommand could come from.
HIDDEN = re.compile(r"""[ \t]*(\\$|["'`$])""")
# A redirection, with its target: 2>/dev/null, >"$log", &>/dev/null, <<EOF.
REDIRECTION = re.compile(r"[ \t]*[0-9]*(&>>?|>>|>&|>\||<<<|<<-?|<&|<>|>|<)[ \t]*[^ \t;&|()]*")
# What ends a command after a bare gh.
ENDS = re.compile(r"[ \t]*([;&|)`]|$)")
# A bare gh whose line ends in || or &&: a lookup (command -v gh ||) that goes on to its own next command.
LOOKUP_LIST = re.compile(r"[ \t]*(\|\||&&)[ \t]*")
# A NAME=value word before a command, with the whitespace after it.
ASSIGNMENT_WORD = (r"""[A-Za-z_][A-Za-z0-9_]*=(?:"[^"]*"|'[^']*'|\$\{[A-Za-z_][A-Za-z0-9_]*:\?[^}"'`]*\}"""
                   r"""|[^ \t"';&|()]*)[ \t]+""")
# The run of such words right before gh.
PREFIX = re.compile(r"(?<![A-Za-z0-9_])(?:" + ASSIGNMENT_WORD + r")+$")
ASSIGNMENT = re.compile(r"(?<![A-Za-z0-9_])GH_REPO=")
PREFIX_OF_GH = re.compile(r"GH_REPO=" + VALUE + r"[ \t]+(?:" + ASSIGNMENT_WORD + r")*" + GH_WORD)
UNSET = re.compile(r"(?<![A-Za-z0-9_])unset[ \t][^;&|]*(?<![A-Za-z0-9_])GH_REPO(?![A-Za-z0-9_])")
REPO = re.compile(r"repo([^A-Za-z0-9_-]|$)")
NAMES_ITS_OWN = re.compile(r"repo[ \t]+(list|create|clone|gitignore|license)\b")
READS_GH_REPO = re.compile(r"repo[ \t]+(rename|deploy-key|autolink)\b")
# gh repo <subcommand>, then a value that isn't a flag: its repository when
# it's owner/repo or ${NAME:?}.
NAMED = re.compile(r"repo[ \t]+[a-z-]+[ \t]+(?!-)" + VALUE)


def prefix_repository(before):
    """The value of the last GH_REPO= among the NAME=value words right before gh, or None when there is none."""
    prefix = PREFIX.search(before)
    if prefix is None:
        return None
    repositories = [word.partition("=")[2].rstrip(" \t") for word in re.findall(ASSIGNMENT_WORD, prefix.group())
                    if word.startswith("GH_REPO=")]
    return repositories[-1] if repositories else None


# A program that runs a command without the caller's GH_REPO (sudo resets the environment, ssh and su start a new one).
WRAPPER = re.compile(r"(?<![A-Za-z0-9_.-])(sudo|doas|su|runuser|pkexec|ssh)(?![A-Za-z0-9_.-])")
ENV = re.compile(r"(?<![A-Za-z0-9_.-])env(?![A-Za-z0-9_.-])")


def command_words(before):
    """The part of before that belongs to gh's own command: what follows the last ; & or | outside quotes."""
    start = quote = 0
    index = 0
    while index < len(before):
        char = before[index]
        if char == "\\" and quote != "'":
            index += 1
        elif quote:
            quote = 0 if char == quote else quote
        elif char in "'\"":
            quote = char
        elif char in ";&|\n":
            start = index + 1
        index += 1
    return before[start:]


def env_drops_gh_repo(words):
    """Whether the options of the env command whose words follow (env -i, -u GH_REPO, -S) lose GH_REPO."""
    words = words.split()
    index = 0
    while index < len(words):
        word = words[index]
        index += 1
        if word == "--":
            return False
        if word in ("-", "--ignore-environment") or word.startswith(("--split-string", "--unset=GH_REPO")):
            return True
        if word == "--unset":
            if words[index:index + 1] == ["GH_REPO"]:
                return True
            index += 1
        elif word.startswith("--"):
            # A long option; --chdir takes the next word as its argument.
            if word == "--chdir":
                index += 1
        elif word.startswith("-"):
            for position, flag in enumerate(word[1:], 1):
                if flag in "iS":
                    return True
                if flag in "uC":
                    attached = word[position + 1:]
                    argument = attached or (words[index] if index < len(words) else "")
                    if flag == "u" and argument.strip("'\"") == "GH_REPO":
                        return True
                    if not attached:
                        index += 1
                    break
        elif not re.match(r"[A-Za-z_][A-Za-z0-9_]*=", word):
            return False
    return False


def wrapped(before):
    """Whether gh, after before on its line, runs behind a wrapper that drops the GH_REPO an earlier export set."""
    words = command_words(before)
    return WRAPPER.search(words) is not None or any(
        env_drops_gh_repo(words[env.end():]) for env in ENV.finditer(words))


def told(before, after, exported):
    """Whether the gh between before and after is told its repository.

    before is the text of gh's own command up to it (from the syntax tree, with its backslash continuations joined), where a
    wrapper or an assignment before gh is. after is, in a script, the rest of gh's line, cut by after_of() when it is longer
    than AFTER_CHARS; in a skill's snippet (untold_in_code), gh's own words up to the next gh or the end of its command, uncut.
    """
    exported = exported and not wrapped(before)
    # gh closing a quoted word: judge what follows the quote.
    if after[:1] in ("\"", "'"):
        after = after[1:]
    if HIDDEN.match(after):
        return False
    while redirection := REDIRECTION.match(after):
        after = after[redirection.end():]
    subcommand = SUBCOMMAND.match(after)
    if subcommand is None:
        return LOOKUP_LIST.fullmatch(after) is not None or (exported and ENDS.match(after) is not None)
    rest = after[subcommand.end():]
    if REPO.match(rest) and not READS_GH_REPO.match(rest):
        named = NAMED.match(rest)
        repository = named.group(1) if named else ""
        return bool(NAMES_ITS_OWN.match(rest)) or "/" in repository or "${" in repository
    prefixed = prefix_repository(before)
    return exported if prefixed is None else re.fullmatch(VALUE, prefixed) is not None


# The commands whose heredoc is data, never code: they print it, store it, or ignore it.
DATA_COMMANDS = ("cat", "tee", "echo", "printf", "read", "mail", "mailx", ":", "true")
# The files a data command may write to: text, not something a later command runs.
DATA_SUFFIXES = ("txt", "md", "json", "yml", "yaml", "log", "out", "csv", "tsv", "toml", "ini", "conf", "cfg", "html",
                 "xml", "env", "patch", "diff")
# The redirections that write to their target.
WRITES = ("<>", ">>", ">&", ">|", ">", "&>>", "&>")
# An if condition that never holds, and one that always does.
NEVER = re.compile(r"(false|! *true|\[ +0 +-ne +0 +\]|\[ +1 +-eq +0 +\])$")
ALWAYS = re.compile(r"(true|:)$")
# The shfmt that parses a script: a v3 release from v3.14 on, since this checker reads shfmt's syntax tree, not the script's text.
SHFMT = "shfmt"
SHFMT_RELEASE = re.compile(r"v3\.(0|[1-9][0-9]*)\.[0-9]+")
SHFMT_OLDEST_MINOR = 14
SHFMT_WANTED = "shfmt v3.14 or a later v3 release"
# Where shfmt comes from, for the message when it's missing: the release binaries, or the developer's package manager.
SHFMT_INSTALL = "https://github.com/mvdan/sh/releases, or your package manager"


class ShfmtError(Exception):
    """shfmt is missing, isn't a release this checker reads, failed in a way that isn't a syntax error, or printed a tree of
    another shape."""


class Line(str):
    """A line of a file as bash reads it, NULs removed; .original is the line as written."""

    original = ""


def read_lines(path):
    """The lines of path as bash sees them: bash drops a NUL byte, so the file is read with its NULs removed."""
    data = Path(path).read_bytes()
    # Not splitlines(), which also breaks at characters bash doesn't.
    lines = []
    for written in data.decode("utf-8", errors="surrogateescape").split("\n"):
        line = Line(written.replace("\0", "").removesuffix("\r"))
        line.original = written.removesuffix("\r")
        lines.append(line)
    return lines


def for_log(text):
    """The text with its control characters escaped (\\r, \\n, \\x1b...), tab kept.

    A line or a file name comes from the PR. A raw \\r or \\n in it printed to the Actions log would let the rest of it
    start a workflow command (::error::, ::stop-commands::).
    """
    return "".join(char if char == "\t" or char.isprintable() else char.encode("unicode_escape").decode("ascii")
                   for char in text)


def shown(line):
    # The line as written (a NUL at its ends is no use to the reader); surrogateescape keeps a byte that isn't UTF-8,
    # printed as the replacement character. Control characters are escaped, so the line can't start a log command.
    line = getattr(line, "original", line).strip("\0")
    return for_log(line.encode("utf-8", errors="surrogateescape").decode("utf-8", errors="replace"))


_checked_shfmt = []
# The empty folder shfmt runs in, made once per run: shfmt starts in no folder of the PR's, so no file the PR holds (an
# .editorconfig) reaches it.
_shfmt_folder = []


def shfmt_options():
    """The folder and the environment shfmt runs with: an empty folder of its own, and only the PATH that finds it."""
    if not _shfmt_folder:
        _shfmt_folder.append(tempfile.TemporaryDirectory(prefix="check_gh_repo-"))
    return {"cwd": _shfmt_folder[0].name, "env": {"PATH": os.environ.get("PATH", os.defpath)}}


def shfmt_version():
    """Stop with ShfmtError unless shfmt is on PATH and is v3.14 or a later v3 release; asked once per run."""
    if _checked_shfmt:
        return
    try:
        result = subprocess.run([SHFMT, "--version"], capture_output=True, text=True, timeout=60, check=False,
                                **shfmt_options())
    except subprocess.TimeoutExpired as error:
        raise ShfmtError(f"shfmt --version didn't answer in {error.timeout:g} s; check_gh_repo.py needs {SHFMT_WANTED}") from error
    except (OSError, subprocess.SubprocessError) as error:
        raise ShfmtError(f"can't run shfmt ({for_log(str(error))}): check_gh_repo.py needs {SHFMT_WANTED} on PATH "
                         f"({SHFMT_INSTALL})") from error
    printed = for_log(result.stdout.strip() or result.stderr.strip())
    if result.returncode:
        raise ShfmtError(f"shfmt --version failed with exit {result.returncode} ({printed!r}); check_gh_repo.py needs {SHFMT_WANTED}; "
                         "it runs shfmt with only PATH set, so a version manager's shim (asdf, mise) that needs more fails here: "
                         "put the release binary on PATH")
    release = SHFMT_RELEASE.fullmatch(result.stdout.strip())
    if release is None or int(release.group(1)) < SHFMT_OLDEST_MINOR:
        raise ShfmtError(f"check_gh_repo.py needs {SHFMT_WANTED}, found {printed!r} ({SHFMT_INSTALL})")
    _checked_shfmt.append(True)


def run_shfmt(data):
    """shfmt's syntax tree of data (bytes) as JSON and None, or None and the line of a syntax error."""
    try:
        result = subprocess.run([SHFMT, "-ln", "bash", "--to-json"], input=data, capture_output=True, timeout=60, check=False,
                                **shfmt_options())
    except subprocess.TimeoutExpired as error:
        raise ShfmtError(f"shfmt didn't parse a script in {error.timeout:g} s") from error
    except (OSError, subprocess.SubprocessError) as error:
        raise ShfmtError(f"can't run shfmt to parse a script ({for_log(str(error))})") from error
    if result.returncode:
        # shfmt says <line>:<col>: <message> for a syntax error; anything else is shfmt failing.
        syntax = re.match(rb"(?:<standard input>:)?([0-9]+):[0-9]+: ", result.stderr)
        if syntax:
            return None, int(syntax.group(1))
        raise ShfmtError(f"shfmt failed with exit {result.returncode}: {for_log(result.stderr.decode('utf-8', 'replace').strip())!r}")
    try:
        tree = json.loads(result.stdout)
    except ValueError as error:
        raise ShfmtError(f"shfmt printed no syntax tree ({error})") from error
    if not isinstance(tree, dict):
        raise ShfmtError("shfmt printed no syntax tree (not a JSON object)")
    return tree, None


def comments_of(node, found):
    """The comments of the tree (shfmt's Comment nodes: a Hash and a Text)."""
    if isinstance(node, list):
        for item in node:
            comments_of(item, found)
    elif isinstance(node, dict):
        if "Hash" in node and "Text" in node:
            found.append(node)
        for value in node.values():
            if isinstance(value, (dict, list)):
                comments_of(value, found)
    return found


# How many times a script is parsed, at most, to read its comments that end in a backslash: the first parse, one with those
# backslashes blanked, and one more for each blank put back, because a tree put it in no comment or because it broke the line after it.
COMMENT_PASSES = 10


def backslash_runs(data):
    """{(start, end): line} of the backslashes that end each line with a # on it (lines counted from 1): the lines a comment may
    end in a backslash on. One pass over the lines, so a long line costs its length."""
    runs = {}
    offset = 0
    for number, line in enumerate(data.split(b"\n"), 1):
        stripped = line.rstrip(b"\\")
        if len(stripped) < len(line) and b"#" in stripped:
            runs[(offset + len(stripped), offset + len(line))] = number
        offset += len(line) + 1
    return runs


def parse(text):
    """shfmt's syntax tree of text as bash, with every offset in it counted in characters of text, and None; or None and the line of
    the syntax error when text isn't valid bash.

    A byte that isn't UTF-8 (read as a surrogate) is given to shfmt as a ?, one byte for one, so the offsets still line up.
    shfmt v3.14 joins the line after a comment that ends in a backslash onto the comment's line, where bash ends the comment
    at the newline and runs the next line, so a valid script can even fail to parse. When the tree holds such a comment, or
    the script doesn't parse, the backslashes that end each line with a # on it are given to shfmt as blanks, which bash
    reads the same inside a comment, and the script is parsed again. A blank that the new tree puts in no comment (a #
    in a string, then a real continuation) is put back, and so is a blank on the line right before a syntax error the blanks
    brought (a real continuation, whose next line then starts with | or &&); the script is parsed again each time, at most
    COMMENT_PASSES times in all.
    """
    shfmt_version()
    data = bytearray()
    starts = []
    for char in text:
        starts.append(len(data))
        data += b"?" if "\udc80" <= char <= "\udcff" else char.encode("utf-8")
    starts.append(len(data))
    original = bytes(data)
    runs = backslash_runs(original)
    blanked = set()
    restored = set()
    # For each blank put back because it seemed to break the line after it, the syntax error it was blamed for. If a later tree puts
    # that blank in a comment again, the blame was wrong: the error is a real one, and the statements before it count.
    broke = {}
    tree = error = None
    for _ in range(COMMENT_PASSES):
        tree, error = run_shfmt(bytes(data))
        if tree is None:
            # A blank that was a real continuation breaks the line after it: put that one back, and parse again.
            breaking = [run for run in blanked if runs[run] == error - 1]
            if breaking:
                for start, end in breaking:
                    data[start:end] = original[start:end]
                blanked -= set(breaking)
                restored |= set(breaking)
                broke.update((run, error) for run in breaking)
                continue
            fresh = [run for run in runs if run not in blanked and run not in restored]
            if not fresh:
                return None, error
        else:
            # The line each comment starts on ends in it: a blanked run there is a comment's.
            commented = {original.rfind(b"\n", 0, comment["Hash"]["Offset"]) for comment in comments_of(tree, [])}
            wrong = {run for run in blanked if original.rfind(b"\n", 0, run[0]) not in commented}
            if wrong:
                for start, end in wrong:
                    data[start:end] = original[start:end]
                blanked -= wrong
                restored |= wrong
                continue
            pending = {run for run in runs if run not in blanked and original.rfind(b"\n", 0, run[0]) in commented}
            if not pending:
                break
            if pending & restored:
                if all(run in broke for run in pending & restored):
                    # A comment's blank that only seemed to break the next line: that line holds a real syntax error.
                    return None, min(broke[run] for run in pending & restored)
                # A comment's backslashes that an earlier tree put in no comment: which tree is bash's is unknown.
                return None, 1
            fresh = [run for run in runs if run not in blanked and run not in restored]
        for start, end in fresh:
            data[start:end] = b" " * (end - start)
        blanked |= set(fresh)
    else:
        # Comments that end in a backslash keep turning up: read the whole file as one that isn't valid bash, line by line.
        return None, 1
    if len(data) != len(text):
        # The character each byte offset starts.
        character = {offset: index for index, offset in enumerate(starts)}

        def convert(node):
            if isinstance(node, dict):
                if is_position(node):
                    node["Offset"] = character.get(node["Offset"], node["Offset"])
                for value in node.values():
                    convert(value)
            elif isinstance(node, list):
                for value in node:
                    convert(value)
        convert(tree)
    return tree, None


# How many times a script that isn't valid bash is parsed, the whole script first, looking for the statements before its syntax error.
PREFIX_TRIES = 5


def parse_prefix(lines):
    """The syntax tree of the longest run of lines from the top that is valid bash, and how many lines it has.

    bash runs a script one complete statement at a time and stops at a syntax error, so the statements before the error are
    what runs. shfmt names the line of the error; the lines before it are parsed in turn (an if or a heredoc that the error
    is in starts earlier), PREFIX_TRIES parses in all, and the run is empty when none parses.
    """
    count = len(lines)
    for _ in range(PREFIX_TRIES):
        tree, error = parse("\n".join(lines[:count]))
        if tree is not None:
            return tree, count
        count = min(count - 1, error - 1)
        if count <= 0:
            break
    return None, 0


def is_position(node):
    """Whether a dict of the tree is a position ({Offset, Line, Col}), not a node: a ${x:1} slice has an Offset that is a word."""
    return isinstance(node.get("Offset"), int) and "Line" in node


def is_statement(node):
    """Whether a dict of the tree is a statement (shfmt's Stmt), with a command or only redirections (> out, <<EOF).

    An elif or an else has a Position too, but no Type and a FiPos; an if, a function has a Type.
    """
    return node.get("Type") is None and "Position" in node and "FiPos" not in node


def span(node):
    """The [start, end) of a node of the tree, in characters."""
    return node["Pos"]["Offset"], node["End"]["Offset"]


def literal(word):
    """The text of a word that is only literal text and quotes (a quoted delimiter, a command name), or None when it holds an expansion."""
    text = []
    for part in word.get("Parts") or []:
        kind = part.get("Type")
        if kind == "Lit":
            text.append(part["Value"].replace("\\", ""))
        elif kind == "SglQuoted":
            text.append(part["Value"])
        elif kind == "DblQuoted":
            inner = literal(part)
            if inner is None:
                return None
            text.append(inner)
        else:
            return None
    return "".join(text)


def text_file(target):
    """Whether a file a data command writes is text, not something a later command runs."""
    return target.isdigit() or target == "-" or target.startswith("/dev/") or \
        target.rsplit("/", 1)[-1].rpartition(".")[2].lower() in DATA_SUFFIXES


def writes_text_only(statement):
    """Whether every file the statement's own redirections write to is text."""
    for redirect in statement.get("Redirs") or []:
        if redirect.get("Op") in WRITES:
            target = literal(redirect["Word"])
            if target is None or not text_file(target):
                return False
    return True


def command_name(call):
    """The name of the command a CallExpr runs, without its folder, or None when it's no literal word."""
    args = call.get("Args") or []
    name = literal(args[0]) if args else None
    return None if name is None else name.rsplit("/", 1)[-1]


def data_statement(statement):
    """Whether a statement is one data command (cat, tee, echo, printf, read, mail, :, true) that writes only text."""
    call = statement.get("Cmd") or {}
    if call.get("Type") != "CallExpr" or not writes_text_only(statement):
        return False
    name = command_name(call)
    if name not in DATA_COMMANDS:
        return False
    if name == "tee":
        for word in call["Args"][1:]:
            target = literal(word)
            if target is None or not (target.startswith("-") or text_file(target)):
                return False
    return True


def pipeline_statements(statement):
    """The statements of a pipeline that starts at statement (itself, when it is no pipe)."""
    call = statement.get("Cmd") or {}
    if call.get("Type") == "BinaryCmd" and call.get("Op") in ("|", "|&"):
        return pipeline_statements(call["X"]) + pipeline_statements(call["Y"])
    return [statement]


def output_is_data(chain):
    """Whether what the statement at the end of chain (a list of (node, key) from the root down) prints ends up as data.

    It does when nothing that may run it reads it: each pipe it goes into ends in data commands, each file a statement
    around it writes is text, and a $(...) or <(...) it is in goes to a data command or into a variable. A compound
    command passes its output on; a function's output is the script's.
    """
    for index in range(len(chain) - 1, 0, -1):
        node, key = chain[index]
        parent, _ = chain[index - 1]
        if is_statement(node) and not writes_text_only(node):
            return False
        kind = parent.get("Type")
        if kind == "BinaryCmd" and parent.get("Op") in ("|", "|&") and key == "X":
            if not all(data_statement(statement) for statement in pipeline_statements(parent["Y"])):
                return False
        elif kind in ("CmdSubst", "ProcSubst"):
            # What reads the substitution's text: the word it is in belongs to an Assign (stored) or a CallExpr's Args.
            for above in range(index - 2, -1, -1):
                owner, owner_key = chain[above]
                child_key = chain[above + 1][1]
                if "Name" in owner and owner.get("Type") is None and child_key == "Value":
                    # NAME=$(...): stored in a variable.
                    return True
                if owner.get("Type") == "CallExpr":
                    # An argument of a command: data only when that command is a data command whose own output is data.
                    return child_key == "Args" and data_statement(chain[above - 1][0]) and output_is_data(chain[:above])
                if is_statement(owner) or owner.get("Type"):
                    if owner.get("Type") in ("DblQuoted",):
                        continue
                    return False
            return False
    return True


def condition_text(text, statements):
    """The source of an if's condition, without the ; or blanks after it."""
    if not statements:
        return ""
    start, _ = span(statements[0])
    _, end = span(statements[-1])
    return text[start:end].replace("\\\n", "").strip(" \t\n").removesuffix(";").strip(" \t\n")


class Shape:
    """What the syntax tree says about a script: its comments, its heredocs, its commands and the lines that export GH_REPO."""

    def __init__(self, text, tree):
        self.text = text
        self.comments = []
        # (start, end, heredoc): end is the start of the delimiter's line.
        self.heredocs = []
        # [start, end) of each simple command and statement, to find the one a gh is in.
        self.commands = []
        self.statements = []
        # The 0-based index of each line that starts an export of GH_REPO, and of each whose export runs in the script's own shell, in a branch that can run.
        self.export_statements = set()
        self.export_lines = set()
        self._walk(tree, [(tree, None)], dead=False, here=True)

    def _walk(self, node, chain, dead, here):
        if isinstance(node, list):
            for item in node:
                self._walk(item, chain, dead, here)
            return
        if not isinstance(node, dict) or is_position(node):
            return
        kind = node.get("Type")
        if "Hash" in node and "Text" in node:
            self.comments.append(span(node))
            return
        if is_statement(node):
            self._statement(node, chain, dead, here)
            return
        if kind in ("CallExpr", "DeclClause"):
            self.commands.append(span(node))
        if node.get("Op") in ("<<", "<<-") and "Word" in node:
            self._heredoc(node, chain)
        if kind == "IfClause":
            self._if(node, chain, dead, here)
            return
        # A subshell, a substitution, a pipe's command, a coprocess and a condition don't run in the script's own shell.
        inner_here = here and kind not in ("Subshell", "CmdSubst", "ProcSubst", "TestClause", "ArithmCmd", "CoprocClause") and not (
            kind == "BinaryCmd" and node.get("Op") in ("|", "|&"))
        # The right side of false && ... or true || ... never runs.
        dead_right = kind == "BinaryCmd" and (
            node.get("Op") == "&&" and NEVER.match(condition_text(self.text, [node["X"]])) is not None
            or node.get("Op") == "||" and ALWAYS.match(condition_text(self.text, [node["X"]])) is not None)
        for key, value in node.items():
            if key in ("Pos", "End") or not isinstance(value, (dict, list)):
                continue
            here_for_key = inner_here and key != "Cond"
            self._walk_child(value, chain, key, dead or (dead_right and key == "Y"), here_for_key)

    def _walk_child(self, value, chain, key, dead, here):
        if isinstance(value, list):
            for item in value:
                self._walk_child(item, chain, key, dead, here)
        elif isinstance(value, dict):
            self._walk(value, chain + [(value, key)], dead, here)

    def _statement(self, statement, chain, dead, here):
        self.statements.append(span(statement))
        call = statement.get("Cmd") or {}
        # A command run in the background runs in a child shell. (A line EXPORTS matches is one export alone, never sent to
        # the background or given an empty value itself: what can stop its export is around it.)
        here = here and not statement.get("Background") and not statement.get("Coprocess")
        if call.get("Type") == "DeclClause" and (call.get("Variant") or {}).get("Value") == "export":
            args = call.get("Args") or []
            if len(args) == 1 and (args[0].get("Name") or {}).get("Value") == "GH_REPO":
                line = statement["Pos"]["Line"] - 1
                self.export_statements.add(line)
                if here and not dead:
                    self.export_lines.add(line)
        for key, value in statement.items():
            if key in ("Pos", "End") or not isinstance(value, (dict, list)):
                continue
            self._walk_child(value, chain, key, dead, here)

    def _if(self, clause, chain, dead, here):
        """An if or an elif: its then branch is dead when its condition never holds; a plain else when that condition always does."""
        condition = condition_text(self.text, clause.get("Cond") or [])
        for key, value in clause.items():
            if key in ("Pos", "End", "Else") or not isinstance(value, (dict, list)):
                continue
            if key == "Cond":
                self._walk_child(value, chain, key, dead, False)
            elif key == "Then":
                self._walk_child(value, chain, key, dead or NEVER.match(condition) is not None, here)
            else:
                self._walk_child(value, chain, key, dead, here)
        branch = clause.get("Else")
        if not branch:
            return
        inner = chain + [(branch, "Else")]
        if branch.get("Cond"):
            # An elif after a condition that always holds never runs, nor does anything after it.
            self._if(branch, inner, dead or ALWAYS.match(condition) is not None, here)
            return
        for key, value in branch.items():
            if key in ("Pos", "End") or not isinstance(value, (dict, list)):
                continue
            self._walk_child(value, inner, key, dead or (key == "Then" and ALWAYS.match(condition) is not None), here)

    def _heredoc(self, redirect, chain):
        never_runs = {"data": True, "quoted": True, "substitutions": []}
        # The delimiter word after << runs nothing, and is no comment either: a gh-repo: ok in it marks nothing. (The line that
        # closes an empty body is in no statement: untold reads it as one that runs nothing.)
        self.heredocs.append((*span(redirect["Word"]), {**never_runs, "delimiter": True}))
        body = redirect.get("Hdoc")
        if not body:
            return
        start, last = span(body)
        # The body ends where the delimiter's line starts; that line closes it and runs nothing.
        end = max(start, self.text.rfind("\n", start, last) + 1) if last > start else start
        if last > end:
            self.heredocs.append((end, last, never_runs))
        delimiter = self.text[slice(*span(redirect["Word"]))]
        quoted = any(char in delimiter for char in "'\"\\")
        # The statement the redirection belongs to is the last statement in the chain: every redirection is a statement's,
        # even one with no command (<<EOF alone), which is no data command, so its body is read as text some program may run.
        statement_index = max(index for index, (node, _) in enumerate(chain) if is_statement(node))
        statement = chain[statement_index][0]
        data = data_statement(statement) and output_is_data(chain[:statement_index + 1])
        substitutions = []
        if not quoted:
            self._substitutions(body, substitutions)
        self.heredocs.append((start, end, {"data": data, "quoted": quoted, "substitutions": substitutions}))

    def _substitutions(self, node, found):
        if isinstance(node, list):
            for item in node:
                self._substitutions(item, found)
        elif isinstance(node, dict) and not is_position(node):
            if node.get("Type") in ("CmdSubst", "ProcSubst"):
                found.append(span(node))
            for value in node.values():
                if isinstance(value, (dict, list)):
                    self._substitutions(value, found)


def innermost(spans, position):
    """The smallest [start, end) of spans that holds position, or None (for the few substitutions of one heredoc)."""
    best = None
    for start, end in spans:
        if start <= position < end and (best is None or end - start < best[1] - best[0]):
            best = (start, end)
    return best


class Index:
    """The innermost span around each of the given positions, found in one pass over both, sorted: the syntax tree's spans nest."""

    def __init__(self, spans, positions):
        self.found = {}
        spans = sorted(spans, key=lambda item: (item[0], -item[1]))
        stack = []
        index = 0
        for position in sorted(set(positions)):
            while index < len(spans) and spans[index][0] <= position:
                while stack and stack[-1][1] <= spans[index][0]:
                    stack.pop()
                stack.append(spans[index])
                index += 1
            while stack and stack[-1][1] <= position:
                stack.pop()
            self.found[position] = stack[-1] if stack else None

    def around(self, position):
        return self.found[position]


def clears(line):
    """Whether a line ends an export: an unset of GH_REPO, or a GH_REPO= that is no told prefix of a gh command."""
    return UNSET.search(line) is not None or any(
        not PREFIX_OF_GH.match(line, assignment.start()) for assignment in ASSIGNMENT.finditer(line))


# How many characters of a gh's own command before it are read (joined lines included, without the backslash and newline that
# join them), and how many continued lines before it after a syntax error, and, in a command the tree reads, twice BEFORE_CHARS
# of its command before it with the continuations: past any of them, the gh is refused. After the gh, at most AFTER_CHARS of the
# rest of its line are read (see after_of), so the text read for one gh stays bounded however long the line or the command is.
BEFORE_CHARS = 4096
AFTER_CHARS = 4096
JOINED_LINES = 100


def after_of(line, end):
    """The rest of line after a gh that ends at end, as told() reads it: whole when it is at most AFTER_CHARS long.

    told() decides from the first words of the gh's own command (a redirection, the subcommand, repo and its repository) and,
    for a bare gh, the operator that ends it, so a longer rest (a long --jq filter) is cut back to the last blank in its first
    AFTER_CHARS characters and ended with "(". Each word left is whole, and "(" is nothing told() reads as the end of the
    command, a redirection's target, a subcommand or a repository: what told() needs past the cut makes it refuse the gh.
    """
    after = line[end:end + AFTER_CHARS + 1]
    if len(after) <= AFTER_CHARS:
        return after
    after = after[:AFTER_CHARS]
    return after[:max(after.rfind(" "), after.rfind("\t")) + 1] + "("


def read_shape(lines):
    """The Shape of the longest run of lines from the top that is valid bash, and how many lines it has (0 when none is).

    A script nested deeper than Python's recursion limit lets the checker walk (a chain of a few hundred && or | links) is
    read as one that isn't valid bash from its first line: no export in it is trusted. A tree of a shape the checker doesn't
    read (a shfmt whose JSON changed) is a ShfmtError.
    """
    try:
        tree, count = parse_prefix(lines)
        text = "\n".join(lines[:count])
        return Shape(text, tree if tree is not None else {}), count
    except RecursionError:
        return Shape("", {}), 0
    except (KeyError, TypeError, AttributeError, IndexError, ValueError) as error:
        raise ShfmtError(f"shfmt's syntax tree isn't the shape check_gh_repo.py reads ({type(error).__name__}: "
                         f"{for_log(str(error))}); it needs {SHFMT_WANTED}") from error


def untold(path):
    """Each line of path with a gh command not told its repository, as file:line: text.

    The lines shfmt parses (all of them, for a script that is valid bash) are judged on the syntax tree. The lines from a syntax
    error on are judged as text, each one as code: bash stops at the error, but which lines would run is unknown, so a gh there
    is still judged, with the export the parsed lines left and no export of their own.
    """
    lines = read_lines(path)
    shape, count = read_shape(lines)
    text = shape.text
    starts = [0]
    for line in lines[:-1]:
        starts.append(starts[-1] + len(line) + 1)
    # The positions asked about: the first character of each line, each gh, and each gh-repo: ok marker.
    firsts = [starts[number] + len(line) - len(line.lstrip(" \t")) for number, line in enumerate(lines)]
    markers = [[match.start() for match in OPT_OUT.finditer(line)] for line in lines]
    positions = firsts + [starts[number] + match.start() for number, line in enumerate(lines) for match in GH.finditer(line)]
    number_of = {starts[number] + column: number for number, columns in enumerate(markers) for column in columns}
    positions += list(number_of)
    comments = Index(shape.comments, positions)
    commands = Index(shape.commands, positions)
    statements = Index(shape.statements, positions)
    heredoc_index = Index([(start, end) for start, end, _ in shape.heredocs], positions)
    by_span = {(start, end): heredoc for start, end, heredoc in shape.heredocs}

    def where(number, position):
        """'comment', 'data' (never runs), 'text' (a heredoc body some program reads, or a line after a syntax error), or 'code'."""
        if number >= count:
            return "comment" if re.match(r"[ \t]*#", lines[number]) else "text"
        if comments.around(position):
            return "comment"
        body = heredoc_index.around(position)
        if body is None:
            # Outside every statement and every comment is only the line that closes a heredoc whose body is empty.
            return "code" if statements.around(position) else "data"
        heredoc = by_span[body]
        if heredoc["data"]:
            if heredoc["quoted"] or innermost(heredoc["substitutions"], position) is None:
                return "data"
            return "code"
        return "text"

    # The export state each line is judged with: an export alone on its line that runs in the script's own shell,
    # in a branch that can run, tells the lines after it; an unset or another GH_REPO= in code ends it.
    exported_lines = []
    exported = False
    for number, line in enumerate(lines):
        place = where(number, firsts[number]) if line.strip(" \t") else "comment"
        if place in ("comment", "data"):
            exported_lines.append(exported)
            continue
        if number < count and number in shape.export_statements and EXPORTS.match(line):
            # An export in a branch that never runs, or in a subshell, leaves GH_REPO as it was.
            exported = exported or number in shape.export_lines
        elif clears(line):
            exported = False
        exported_lines.append(exported)

    # The lines a backslash joins to each line after the syntax error, which bash reads as one: a wrapper there drops the export.
    joined_from = list(range(len(lines)))
    for number in range(max(count, 1), len(lines)):
        if re.search(r"(?<!\\)(\\\\)*\\$", lines[number - 1]) and number - 1 >= count:
            joined_from[number] = joined_from[number - 1]
    # The joined text of the line being judged, made once for all its gh words: (index, text).
    joined = [None, None]

    def earlier_of(index):
        """The text of the lines a backslash joins to line index, or None when there are more than JOINED_LINES of them or it is
        longer than BEFORE_CHARS (each joined line without the backslash that joins it). judge() checks BEFORE_CHARS again with
        the gh's own line; this check only keeps the joined text it builds small."""
        if joined[0] != index:
            first = joined_from[index]
            text_of = None
            if index - first <= JOINED_LINES and sum(len(lines[other]) - 1 for other in range(first, index)) <= BEFORE_CHARS:
                text_of = "".join(lines[other][:-1] for other in range(first, index))
            joined[:] = [index, text_of]
        return joined[1]

    def judge(number, match, exported):
        position = starts[number - 1] + match.start()
        line = lines[number - 1]
        place = where(number - 1, position)
        if place in ("comment", "data"):
            return None
        after = after_of(line, match.end())
        if place == "text":
            earlier = earlier_of(number - 1)
            if earlier is None or len(earlier) + match.start() > BEFORE_CHARS:
                return False
            return told(line[:match.start()], after, exported and not wrapped(earlier + line[:match.start()]))
        around = commands.around(position) or statements.around(position)
        start = around[0] if around else starts[number - 1]
        # Counted, as on a line after a syntax error, without the backslash and newline of each continuation. A command more than
        # twice BEFORE_CHARS long before the gh, continuations included, is refused before it is joined, so the work stays bounded
        # (one that is mostly bare continuations too, which fails closed).
        if position - start > 2 * BEFORE_CHARS:
            return False
        before = re.sub(r"\\\n", "", text[start:position])
        if len(before) > BEFORE_CHARS:
            return False
        return told(before, after, exported)

    def marks(position):
        """Whether a gh-repo: ok marker is no code: in code, one the syntax tree reads as a comment, and never a heredoc's delimiter."""
        body = heredoc_index.around(position)
        return where(number_of[position], position) != "code" and not (body and by_span[body].get("delimiter"))

    def opted_out(number):
        """Whether the line holds a gh-repo: ok marker that marks it."""
        return any(marks(starts[number] + column) for column in markers[number])

    found = []
    for number, line in enumerate(lines, 1):
        if opted_out(number - 1):
            continue
        if any(judge(number, match, exported_lines[number - 1]) is False for match in GH.finditer(line)):
            found.append(f"{for_log(str(path))}:{number}: {shown(line)}")
    return found


# Repo-relative paths --repo skips (the Owned Baseline files); a trailing / is a folder. check_owned_files.py reads the
# same list, so an entry outside SCOPE (global.json, the SDK pin the Factory moves: mpaulosky/Application_Factory#103)
# is there only for the Owned-files check.
OWNED = (
    "global.json",
    "scripts/gate.sh",
    "scripts/check-branch-name.sh",
    "scripts/check-pr-title.sh",
    "scripts/check-git-hooks.sh",
    "scripts/lint-edited-file.sh",
    "scripts/review.sh",
    "scripts/sync-copilot-review.sh",
    "scripts/watch-pr.sh",
    "scripts/cleanup.sh",
    "scripts/work-issue.sh",
    "scripts/tests/check-branch-name.test.sh",
    "scripts/tests/check-pr-title.test.sh",
    "scripts/tests/check-git-hooks.test.sh",
    "scripts/tests/lint-edited-file.test.sh",
    "scripts/tests/sync-copilot-review.test.sh",
    "scripts/tests/watch-pr.test.sh",
    "scripts/tests/cleanup.test.sh",
    "scripts/tests/work-issue.test.sh",
    ".claude/skills/README.md",
    ".claude/skills/pre-push-review/",
    ".claude/skills/hardening-review/",
)
# The folders --repo walks.
SCOPE = ("scripts", ".github/ci", ".claude/skills")
SKIPPED_PARTS = ("node_modules", ".git")


def is_owned(relative):
    """Whether the repo-relative path is an Owned Baseline file --repo skips, as itself or under a folder entry."""
    return any(relative == entry or (entry.endswith("/") and relative.startswith(entry)) for entry in OWNED)

# A Markdown fence: three or more backticks or tildes (a backtick fence's info
# string has no backtick, or it's an inline span), then the rest of the line.
FENCE = re.compile(r"[ \t]*(`{3,}|~{3,})(.*)$")
# What can come before a fence on its line: blockquote markers and list markers, in any order.
CONTAINER = re.compile(r"(?:[ \t]*(?:>|(?:[-*+]|[0-9]{1,9}[.)])(?=[ \t])))+")
# What a fence's first line says when it's the shell: #!/bin/sh, #!/usr/bin/env bash.
SHEBANG = re.compile(rb"#![ \t]*(\S*/)?(env[ \t]+(-\S+[ \t]+)*)?(ba)?sh([ \t]|$)")
# A skill's gh command is told by a flag on its own line, unless it's gh api or gh repo.
FLAG = re.compile(r"(^|[ \t])(--repo[ \t]+|--repo=|-R[ \t]+)" + VALUE)
# What ends the words that belong to one gh command on a line.
COMMAND_END = re.compile(r";|&&|\|\|")
HAS_NO_FLAG = re.compile(r"(api|repo)([^A-Za-z0-9_-]|$)")


def code_spans(line):
    """The inline code spans of line: text between backtick runs of the same length."""
    position = 0
    while position < len(line):
        if line[position] != "`":
            position += 1
            continue
        opening = position
        while position < len(line) and line[position] == "`":
            position += 1
        length = position - opening
        search = position
        while search < len(line):
            if line[search] != "`":
                search += 1
                continue
            closing = search
            while search < len(line) and line[search] == "`":
                search += 1
            if search - closing == length:
                yield line[position:closing]
                position = search
                break


def untold_in_code(code):
    """Whether a piece of code has a gh command not told its repository."""
    for match in GH.finditer(code):
        after = code[match.end():]
        # Only this command's own words: up to the next gh or the end of its command.
        own = after[:min((end.start() for end in (GH.search(after), COMMAND_END.search(after)) if end),
                         default=len(after))]
        if told(code[:match.start()], own, False):
            continue
        subcommand = SUBCOMMAND.match(own)
        if subcommand and not HAS_NO_FLAG.match(own[subcommand.end():]) and FLAG.search(own):
            continue
        return True
    return False


# An indented code block's line: four spaces or a tab. A list item's marker line, which its indented lines belong to.
INDENTED = re.compile(r" {4}|\t")
LIST_ITEM = re.compile(r"[ \t]{0,3}(?:[-*+]|[0-9]{1,9}[.)])(?:[ \t]|$)")
# A <pre> block opens at the start of a line (not <prefix>) and ends at the line with </pre>; <code> is one line's span.
PRE_OPEN = re.compile(r"[ \t]*<pre(?=[ \t>/]|$)", re.IGNORECASE)
PRE_CLOSE = re.compile(r"</pre\s*>", re.IGNORECASE)
CODE_ELEMENT = re.compile(r"<code(?:[ \t][^>]*)?>(.*?)</code\s*>", re.IGNORECASE)
HTML_TAG = re.compile(r"</?[A-Za-z][^>]*>")


def code_in(line):
    """The code of a line that holds some inside prose: its inline code spans and <code> elements, bare gh named out."""
    spans = list(code_spans(line)) + [match.group(1) for match in CODE_ELEMENT.finditer(line)]
    # A span that is only gh names the tool in prose.
    return [span for span in spans if span.strip() not in ("gh", "gh.exe")]


def untold_markdown(path):
    """Each line of the Markdown file path with a gh command in code not told its repository.

    Code is a fenced block, an indented block (after a blank line, outside a list item), a <pre> block, an inline
    span and a <code> element on one line.
    """
    lines = read_lines(path)
    found = []
    fence = None
    pre = indented = in_list = False
    previous_blank = True
    for number, line in enumerate(lines, 1):
        # A fence in a blockquote or a list item: judge it, and its lines, without the markers.
        container = CONTAINER.match(line)
        inside = line[container.end():] if container else line
        opening = FENCE.match(inside)
        blank = not line.strip()
        after_blank, previous_blank = previous_blank, blank
        if fence:
            if opening and opening.group(1)[0] == fence[0] and len(opening.group(1)) >= len(fence) \
                    and not opening.group(2).strip():
                fence = None
                continue
            code = [inside]
        elif pre:
            end = PRE_CLOSE.search(line)
            pre = end is None
            code = [HTML_TAG.sub("", line[:end.start()] if end else line)]
        elif indented and (blank or INDENTED.match(line)):
            code = [line]
        elif not blank and INDENTED.match(line) and after_blank and not in_list:
            indented = True
            code = [line]
        else:
            indented = False
            if not blank and LIST_ITEM.match(line):
                in_list = True
            elif not blank and not line[:1].isspace():
                in_list = False
            if opening and not (opening.group(1)[0] == "`" and "`" in opening.group(2)):
                fence = opening.group(1)
                continue
            if PRE_OPEN.match(inside):
                end = PRE_CLOSE.search(inside)
                pre = end is None
                code = [HTML_TAG.sub("", inside[:end.start()] if end else inside)]
            else:
                code = code_in(line)
        if not OPT_OUT.search(line) and any(untold_in_code(piece) for piece in code):
            found.append(f"{for_log(str(path))}:{number}: {shown(line)}")
    return found


def kind(path, extensionless_is_shell=False):
    """'markdown', 'shell' or None for a file --repo doesn't check.

    An extensionless file is shell when it has a shell shebang, or always when extensionless_is_shell (under scripts/
    and .github/ci/, where `bash name` runs it whatever its first line says).
    """
    path = Path(path)
    suffix = path.suffix.lower()
    if suffix == ".md":
        return "markdown"
    if suffix in (".sh", ".bash", ".zsh"):
        return "shell"
    if suffix:
        return None
    if extensionless_is_shell:
        return "shell"
    with path.open("rb") as file:
        return "shell" if SHEBANG.match(file.readline(256)) else None


def walk(directory, root, seen):
    """The files under directory as (path, refusal): a symlink out of root or dangling is refused, not read.

    A symlink whose target is inside root is walked as what it points at. seen holds the folders already walked.
    """
    resolved = directory.resolve()
    if resolved in seen:
        return
    seen.add(resolved)
    for entry in sorted(directory.iterdir()):
        if entry.name in SKIPPED_PARTS:
            continue
        if entry.is_symlink():
            try:
                target = entry.resolve()
            except (OSError, RuntimeError):
                # Python 3.12 raises RuntimeError for a link that points at itself or at a cycle of links.
                yield entry, "a symlink to nothing"
                continue
            if not target.exists():
                yield entry, "a symlink to nothing"
                continue
            if not target.is_relative_to(root):
                yield entry, "a symlink to a target outside the repo"
                continue
        if entry.is_dir():
            yield from walk(entry, root, seen)
        elif entry.is_file():
            yield entry, None


def check_repo(root):
    """Each untold gh command in the repo at root, as relative-path:line: text, and each symlink it can't read."""
    root = Path(root)
    base = root.resolve()
    found = []
    done = set()
    for folder in SCOPE:
        top = root / folder
        if top.is_symlink() and not top.exists():
            found.append(f"{folder}: a symlink to nothing")
            continue
        if top.is_symlink() and not top.resolve().is_relative_to(base):
            found.append(f"{folder}: a symlink to a target outside the repo")
            continue
        if not top.is_dir():
            continue
        for path, refusal in walk(top, base, set()):
            if refusal:
                found.append(f"{for_log(path.relative_to(root).as_posix())}: {refusal}")
                continue
            # A file is judged by where it really is: a symlink reaching it first (or a folder walked once, through
            # a link) doesn't hide it, and a link's own path doesn't make it Owned.
            real = path.resolve().relative_to(base).as_posix()
            if is_owned(path.relative_to(root).as_posix()) and is_owned(real):
                continue
            checked = kind(path, folder != ".claude/skills")
            if checked is None or (real, checked) in done:
                continue
            done.add((real, checked))
            lines = untold_markdown(path) if checked == "markdown" else untold(path)
            shown_path = for_log(str(path))
            found.extend(for_log(real) + line[len(shown_path):] for line in lines)
    return found


HINT = ("check_gh_repo.py: export GH_REPO before the first gh command in a script; in a skill write "
        "GH_REPO=owner/repo (or \"${repo:?}\") before each gh command, or give --repo; gh repo's subcommands "
        "take the repository first. See references/gotchas.md in mpaulosky/dotfiles' repo-ci-baseline skill.")


def main(argv):
    """Exit 0 when every gh command is told its repository, 1 when one isn't (and prints it), 2 when the check can't run: a bad usage,
    no usable shfmt, or a file or folder it can't read or decode, which must never read as 1."""
    try:
        return run(argv)
    except ShfmtError as error:
        print(f"check_gh_repo.py: {error}", file=sys.stderr)
        return 2
    except (OSError, UnicodeError) as error:
        # Only an error that names its file says which one it couldn't read; any other (a UnicodeError never names one) says what failed.
        where = getattr(error, "filename", None)
        said = f"can't read {for_log(str(where))}" if where else "failed"
        print(f"check_gh_repo.py: {said}: {for_log(str(error))}", file=sys.stderr)
        return 2


def run(argv):
    if argv[:1] == ["--repo"]:
        if len(argv) != 2 or argv[1] == "--repo":
            print("usage: check_gh_repo.py --repo <root> | <dir>...", file=sys.stderr)
            return 2
        if not Path(argv[1]).is_dir():
            print(f"check_gh_repo.py: {argv[1]} isn't a directory", file=sys.stderr)
            return 2
        found = check_repo(argv[1])
        if found:
            print("\n".join(found))
            print(HINT, file=sys.stderr)
        return 1 if found else 0
    found = []
    for root in argv:
        if not Path(root).is_dir():
            print(f"check_gh_repo.py: {root} isn't a directory", file=sys.stderr)
            return 2
        for path in sorted(p for p in Path(root).rglob("*") if p.is_file()):
            found.extend(untold(path))
    if found:
        print("\n".join(found))
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
