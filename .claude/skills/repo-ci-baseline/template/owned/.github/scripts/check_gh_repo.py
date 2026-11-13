"""Checks that every gh command in the Template's Owned scripts and a repo's own scripts and skills is told its repository; the rules are in references/gotchas.md."""

import os
import re
import sys
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


def told(before, after, exported, earlier="", truncated=False):
    """Whether the gh between before and after is told its repository.

    earlier is the text of the backslash-continued lines before this one: a wrapper or an assignment may have
    started on them, and bash reads them as one line. truncated says the chain went on past what was read, so
    a wrapper that drops GH_REPO may have started before it: the export no longer tells this gh.
    """
    before = earlier + before
    exported = exported and not truncated and not wrapped(before)
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


# A shell operator, longest first: a redirection (with <<, <<-, <<< and &> before their shorter forms) or a separator.
OPERATOR = re.compile(r"<<<|<<-?|<&|<>|<|>>|>&|>\||>|&>>|&>|&&|\|\||;;|;|&|\|")
REDIRECTIONS = ("<<<", "<<", "<<-", "<&", "<>", "<", ">>", ">&", ">|", ">", "&>>", "&>")
# The redirections that write to their target.
WRITES = ("<>", ">>", ">&", ">|", ">", "&>>", "&>")
# The commands whose heredoc is data, never code: they print it, store it, or ignore it.
DATA_COMMANDS = ("cat", "tee", "echo", "printf", "read", "mail", "mailx", ":", "true")
# The files a data command may write to: text, not something a later command runs.
DATA_SUFFIXES = ("txt", "md", "json", "yml", "yaml", "log", "out", "csv", "tsv", "toml", "ini", "conf", "cfg", "html",
                 "xml", "env", "patch", "diff")
# Words before a command that aren't it.
BEFORE_COMMAND = ("{", "!", "if", "then", "else", "elif", "while", "until", "do", "time")
ASSIGNMENT_START = re.compile(r"[A-Za-z_][A-Za-z0-9_]*\+?=")


def tokens(text):
    """The words and operators of one logical line of shell as (kind, text, quoted) tuples, quotes and comments understood.

    kind is 'word' (text is the word without its quotes, quoted whether it had any), 'op' (a redirection or separator,
    or a parenthesis or backtick), 'open' for a $( and 'close' for the ) that ends it. A << inside a quote, a comment
    or (( )) is no operator. The word right before a redirection that is all digits (2>&1) is its file descriptor, dropped.
    """
    found = []
    word = None
    quoted = False
    double = False
    frames = []
    index = 0

    def flush():
        nonlocal word, quoted
        if word is not None:
            found.append(("word", "".join(word), quoted))
        word, quoted = None, False

    while index < len(text):
        char = text[index]
        if double:
            if char == "\\":
                word.append(text[index + 1:index + 2])
                index += 2
            elif char == '"':
                double = False
                index += 1
            elif text.startswith("$(", index) and not text.startswith("$((", index):
                flush()
                found.append(("open", "$(", False))
                frames.append(["double", 1])
                double = False
                index += 2
            else:
                word.append(char)
                index += 1
        elif char in " \t":
            flush()
            index += 1
        elif char == "\\":
            word = [] if word is None else word
            word.append(text[index + 1:index + 2])
            quoted = True
            index += 2
        elif char == "'":
            end = text.find("'", index + 1)
            end = len(text) if end < 0 else end
            word = [] if word is None else word
            word.append(text[index + 1:end])
            quoted = True
            index = end + 1
        elif char == '"':
            word = word or []
            quoted = double = True
            index += 1
        elif char == "#" and word is None:
            break
        elif text.startswith("((", index) or text.startswith("$((", index):
            # Arithmetic: its << is a shift, not a heredoc.
            depth, index = 0, text.index("(", index)
            while index < len(text):
                depth += (text[index] == "(") - (text[index] == ")")
                index += 1
                if depth == 0:
                    break
            word = [] if word is None else word
            word.append("0")
        elif text.startswith("$(", index):
            flush()
            found.append(("open", "$(", False))
            frames.append(["normal", 1])
            index += 2
        elif char in "()":
            flush()
            if char == ")" and frames and frames[-1][1] == 1:
                double = frames.pop()[0] == "double"
                found.append(("close", ")", False))
                word, quoted = ([], True) if double else (None, False)
            else:
                if frames:
                    frames[-1][1] += 1 if char == "(" else -1
                found.append(("op", char, False))
            index += 1
        elif char == "`":
            flush()
            found.append(("op", char, False))
            index += 1
        elif operator := OPERATOR.match(text, index):
            descriptor = word is not None and not quoted and "".join(word).isdigit() and operator.group()[0] in "<>"
            if descriptor:
                word = None
            flush()
            found.append(("op", operator.group(), False))
            index = operator.end()
        else:
            if word is None:
                word = []
            word.append(char)
            index += 1
    flush()
    return found


def heredoc_header(text):
    """The heredocs of one logical line as (delimiter, tabs, quoted), and whether the line feeds its data to a program.

    A body is data only when every command on the line is one that treats its input as data (cat, tee, echo,
    printf, read, mail, :, true), each file they write is text, and the line doesn't end in a pipe, whose
    consumer comes after the body. Any other command, a shell or an interpreter or an assignment-prefixed . among
    them, may run the body, so its body is read as code.
    """
    heredocs = []
    data = True
    redirect = None
    segment = []
    segments = []
    found = tokens(text)
    for kind, value, quoted in found + [("op", ";", False)]:
        if kind == "word" and redirect:
            if redirect in ("<<", "<<-"):
                heredocs.append((value, redirect == "<<-", quoted))
            elif redirect in WRITES:
                data = data and (value.isdigit() or value == "-" or value.startswith("/dev/")
                                 or value.rsplit("/", 1)[-1].rpartition(".")[2].lower() in DATA_SUFFIXES)
            redirect = None
        elif kind == "word":
            segment.append(value)
        elif kind == "op" and value in REDIRECTIONS:
            redirect = value
        else:
            segments.append(segment)
            segment = []
    for words in segments:
        while words and (words[0] in BEFORE_COMMAND or ASSIGNMENT_START.match(words[0])):
            words = words[1:]
        if words:
            command = words[0].rsplit("/", 1)[-1]
            data = data and command in DATA_COMMANDS
            if command == "tee":
                data = data and all(word.startswith("-") or word.rsplit("/", 1)[-1].rpartition(".")[2].lower()
                                    in DATA_SUFFIXES or word.startswith("/dev/") for word in words[1:])
    return heredocs, data and found[-1:] != [("op", "|", False)]


def last_lines(lines):
    """Where each line of lines last occurs, as two dicts of text -> index: as written, and without its leading tabs."""
    whole, stripped = {}, {}
    for index, text in enumerate(lines):
        whole[str(text)] = index
        stripped[str(text).lstrip("\t")] = index
    return whole, stripped


def data_heredocs(header, lines, start, last=None):
    """The (delimiter, tabs, quoted) of each heredoc on the logical line header whose body is data, not code, in the order the bodies follow.

    An unquoted delimiter still lets bash run the $(...) and backticks in a data body. A heredoc whose delimiter never
    comes again in lines[start:] is no heredoc. last is last_lines(lines), which the caller builds once for a file with
    many headers (else a file of `cat <<EOF` lines with no terminator would be read to its end once per line).
    """
    if "<<" not in header:
        return []
    heredocs, data = heredoc_header(header)
    if not data:
        return []
    whole, stripped = last or last_lines(lines)
    return [(delimiter, tabs, quoted) for delimiter, tabs, quoted in heredocs
            if (stripped if tabs else whole).get(delimiter, -1) >= start]


def logical_line(lines, number):
    """The logical line that starts at lines[number - 1], with the continuations bash joins (a trailing backslash outside a comment), and how many physical lines it takes.

    A backslash at the end of a comment joins nothing, and a # inside a string that an earlier line of the chain opened is no comment, so a line with a # is judged on the
    joined text. That holds for the join after each of the first CONTINUATION_LIMIT lines of a chain; the join after a later line uses the backslash alone, as it did before the
    comment was looked at.
    """
    parts = [lines[number - 1]]
    # The trailing backslashes of the text so far: only the newest part decides, since a join drops one backslash and leaves an even run before it.
    tail = len(parts[0]) - len(parts[0].rstrip("\\"))
    count = 1
    while number - 1 + count < len(lines):
        last = lines[number - 2 + count]
        # An odd number of trailing backslashes continues the line; an even number is escaped backslashes.
        joins = tail % 2 == 1
        # Only a line with a # can start a comment (an earlier # was judged when it was joined), so only it is scanned: the whole chain so far, which the string an earlier
        # line opened decides. The guard saves cost and changes no result: a line with a # costs one scan of at most CONTINUATION_LIMIT lines, so a file costs at most that many
        # line scans per line (counted in the tests), not a linear amount. Past the limit nothing is scanned and nothing is cut, so the fail-closed side of a # in a
        # string an earlier line opened still holds.
        if joins and "#" in last and count <= CONTINUATION_LIMIT:
            joins = continues("".join(parts))
        if not joins:
            break
        parts[-1] = parts[-1][:-1]
        parts.append(lines[number - 1 + count])
        tail = len(parts[-1]) - len(parts[-1].rstrip("\\"))
        count += 1
    return "".join(parts), count


# The most physical lines continued_text reads back, and the most that logical_line scans whole for a # inside a string (the join after a later line uses the backslash alone);
# what a longer chain does to the export is in references/gotchas.md (the conditional-export bullet).
CONTINUATION_LIMIT = 100


def joined_lines(lines):
    """For each line of lines, whether bash joins the next line to it (a trailing backslash outside a comment): True, False, or None for a line that can't be judged.

    Each chain is judged whole by logical_line, from its first line, so a string that spans lines decides whether the # after it starts a comment: a line is not judged alone.
    Past CONTINUATION_LIMIT lines of a chain logical_line joins on the backslash alone (it never cuts a chain at a # it can't judge, so a heredoc header after a string isn't missed) and a line is not judged whole, so a line there that holds a #
    is None: it may be a comment or sit in a string an earlier line opened, and either way the read-back stops at it and reports the chain as cut (continued_text), so a
    GH_REPO= in it tells nothing and an export before it is not trusted. The line after it is not forced to start a command (it may be text of a string): untold keeps what track_branches
    returned, so a real comment on the unjudged line still ends the command there. A line without a # joins as logical_line says.
    """
    joins = [False] * len(lines)
    number = 1
    while number <= len(lines):
        _, count = logical_line(lines, number)
        for index in range(number - 1, number + count - 2):
            joins[index] = True if index - number + 2 <= CONTINUATION_LIMIT or "#" not in lines[index] else None
        number += count
    return joins


def continued_text(lines, number, joins):
    """The text of the physical lines right before lines[number - 1] that a trailing backslash joins to it, backslashes removed,
    and whether the chain goes on past what was read: past the CONTINUATION_LIMIT lines read (a file of many continued lines costs a bounded amount per line),
    or at a line that joined_lines can't judge (None). `joins` is joined_lines(lines), which the caller computes once for the file.
    """
    text = ""
    index = number - 2
    # `index >= 0` keeps the walk off joins[-1]: at the first line of the file, and for an empty file.
    while index >= 0 and number - 2 - index < CONTINUATION_LIMIT and joins[index]:
        text = lines[index][:-1] + text
        index -= 1
    return text, index >= 0 and joins[index] is not False


def close_parenthesis(line, start, depth):
    """The index in line at or after start where the substitution of the given depth ends, or len(line), and the depth left."""
    end = start
    while end < len(line) and depth:
        depth += (line[end] == "(") - (line[end] == ")")
        end += 1
    return (end - 1 if depth == 0 else end), depth


def substitutions(line, pending=None):
    """The commands bash runs in line's $(...) and backticks, as (texts, pending).

    A substitution still open at the line's end (pending: ('(', depth) or ('`',)) goes on in the next line: pass
    pending back in, and the start of that line up to the close is its text.
    """
    found = []
    position = 0
    if pending:
        if pending[0] == "(":
            end, depth = close_parenthesis(line, 0, pending[1])
            pending = ("(", depth) if depth else None
        else:
            end = 0
            while end < len(line) and line[end] != "`":
                end += 2 if line[end] == "\\" else 1
            pending = None if end < len(line) else pending
        found.append(line[:end])
        if pending:
            return found, pending
        position = end + 1
    while position < len(line):
        if line[position] == "\\":
            position += 2
        elif line.startswith("$(", position):
            end, depth = close_parenthesis(line, position + 2, 1)
            found.append(line[position + 2:end])
            pending = pending or (("(", depth) if depth else None)
            position += 2
        elif line[position] == "`":
            end = position + 1
            while end < len(line) and line[end] != "`":
                end += 2 if line[end] == "\\" else 1
            found.append(line[position + 1:end])
            if end >= len(line):
                pending = pending or ("`",)
            position = end + 1
        else:
            position += 1
    return found, pending


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


WORD_END = " \t;&|()<>"
NEVER = re.compile(r"(false|! *true|\[ +0 +-ne +0 +\]|\[ +1 +-eq +0 +\])$")
ALWAYS = re.compile(r"(true|:)$")


# How deep quoted_end follows a "..." that holds a $( that holds a "...": past it, the text is read as plain characters.
NESTING_LIMIT = 50


def live_dollar(line, index):
    """Whether line[index] is a $ that starts an expansion.

    A run of $ pairs up into process ids ($$), so only an odd run ends in a live $; a backslash before the run makes
    its first $ a literal.
    """
    if index < 0 or line[index] != "$":
        return False
    dollars = 1
    while index - dollars >= 0 and line[index - dollars] == "$":
        dollars += 1
    slashes = 0
    while index - dollars - slashes >= 0 and line[index - dollars - slashes] == "\\":
        slashes += 1
    if slashes % 2:
        dollars -= 1
    return dollars % 2 == 1


def quoted_end(line, position, depth=0):
    """The index right after the quoted string that starts at line[position], a ' or a " (past the end of line when it never closes).

    Inside "..." and $'...' (a ' after a live $, see live_dollar) a backslash escapes the next character; inside '...'
    it doesn't. A live $( inside "..." (a doubled dollar before a paren is a process id and a paren) holds commands
    with quotes of their own, so the string goes on to the ) that closes it, down to NESTING_LIMIT levels.
    """
    quote = line[position]
    escapes = quote == '"' or (quote == "'" and live_dollar(line, position - 1))
    position += 1
    while position < len(line) and line[position] != quote:
        if escapes and line[position] == "\\":
            position += 2
        elif quote == '"' and line.startswith("$(", position) and live_dollar(line, position) and depth < NESTING_LIMIT:
            position = parenthesis_end(line, position + 2, depth + 1)
        else:
            position += 1
    return position + 1


def parenthesis_end(line, position, depth):
    """The index right after the ) that closes a $( whose text starts at line[position] (past the end of line when it never closes)."""
    open_count = 1
    while position < len(line):
        char = line[position]
        if char == "\\":
            position += 2
        elif char in "'\"":
            position = quoted_end(line, position, depth)
        elif char in "()":
            open_count += 1 if char == "(" else -1
            position += 1
            if not open_count:
                return position
        else:
            position += 1
    return position


def keywords(line, command=True, names=("if", "fi", "else", "elif"), where=None):
    """The if, fi, else and elif keywords of line (or the reserved words in names), in source order, and whether the line ends where a command may start.

    where, a list, gets the index each keyword starts at. Inside [[ ... ]] a word, ||, &&, ; and ( ) are operands, never keywords.

    A keyword is a reserved word as bash reads one: an unquoted word where a command starts, never an argument
    (grep -q fi), never inside quotes ('; fi'), a ${...} or a comment (# fi). A command starts at the start of the line
    (command: False for the line after one that bash joins to it, as joined_lines says), after ; & | ( and after a
    BEFORE_COMMAND word (then, else, do, if, {, !...). A comment ends the command, so the next line starts one.
    A ( says what its ) ends: a ) that closes $(...), <(...) or >(...) ends no command, so a word after it is an
    argument; one that closes a subshell, or a case pattern (a) if y; then z; fi ;;), does. What a ((...)), $((...)),
    name=(...) or extglob group (@( ) *( ) +( ) ?( ) !( )) holds (a (( is arithmetic, never two subshells) is arithmetic,
    array elements or a pattern, not commands, so it is skipped, and so is any ( nested in it except a $( (quotes,
    backslashes and ${...} in it still hide a parenthesis). Known limits: a case pattern that reads fi), a ( left open
    at the end of a line (a $( or (( or name=( spanning lines: the next line is read as code), a backtick
    substitution, a ${...} that holds a quote, a } or a ) (it ends at the first }, and quoted_end and
    parenthesis_end don't see it), quotes nested deeper than NESTING_LIMIT, a [[ ... ]] test that spans lines (the next line is read as code), and a line of a multi-line string or a
    heredoc body read as code that starts like a keyword.
    """
    found = []
    position = 0
    # What each open ( is: "expansion" ($( <( >( ), "skipped" (( ), name=( ), an extglob group and what nests in them) or "subshell".
    parentheses = []
    in_test = False
    while position < len(line):
        char = line[position]
        if parentheses and parentheses[-1] == "skipped" and char not in "()":
            if char == "\\":
                position += 2
            elif char in "'\"":
                position = quoted_end(line, position)
            elif line.startswith("${", position):
                end = line.find("}", position)
                position = len(line) if end < 0 else end + 1
            else:
                position += 1
        elif char in " \t":
            position += 1
        elif char == "#":
            command = True
            break
        elif char == "\\" and position == len(line) - 1:
            position += 1
        elif char == "(":
            before = line[position - 1] if position else ""
            if before == "$":
                kind = "expansion"
            elif line.startswith("((", position) or (parentheses and parentheses[-1] == "skipped") or in_test or (before and before in "=(@?*+!"):
                kind = "skipped"
            else:
                kind = "expansion" if before and before in "<>" else "subshell"
            parentheses.append(kind)
            command = kind != "skipped"
            position += 1
        elif char == ")":
            command = (parentheses.pop() if parentheses else "subshell") == "subshell"
            position += 1
        elif char in ";&|":
            command = command if in_test else True
            position += 1
        elif char in "<>":
            command = False
            position += 1
        else:
            start = position
            while position < len(line) and line[position] not in WORD_END:
                if line[position] == "\\":
                    position += 2
                elif line[position] in "'\"":
                    position = quoted_end(line, position)
                elif line.startswith("${", position):
                    end = line.find("}", position)
                    position = len(line) if end < 0 else end + 1
                else:
                    position += 1
            word = line[start:position]
            if command and word in names:
                found.append(word)
                if where is not None:
                    where.append(start)
            if command and word == "[[":
                in_test = True
            elif word == "]]":
                in_test = False
            command = command and word in BEFORE_COMMAND
    return found, command


def split_condition(text):
    """text cut at its real then (a then where a command starts, never an argument: echo then), without the ; before it, and whether it has one."""
    where = []
    keywords(text, True, ("then",), where)
    if where:
        return text[:where[0]].strip(" \t").removesuffix(";").strip(" \t"), True
    return text.strip(" \t").removesuffix(";").strip(" \t"), False


def without_comment(line):
    """line up to its comment."""
    start = comment_start(line)
    return line if start < 0 else line[:start]


def condition_text(text):
    """text up to its comment, without a backslash that joins the next line to it."""
    text = without_comment(text)
    return text[:-1] if (len(text) - len(text.rstrip("\\"))) % 2 else text


# How many lines a condition may go on over before its if is read as one that may run: a bound on the work a file with an if and no then costs.
PENDING_LIMIT = 20
UNKNOWN = (True, False, None)


def judged(condition):
    """The [runs, always, pending] of a branch whose whole condition is known."""
    condition = condition.strip()
    return [NEVER.match(condition) is None, ALWAYS.match(condition) is not None, None]


def opened(line, start):
    """The branch of the if keyword that starts at line[start]: judged now when its condition ends in a then on this line,
    pending (the condition so far, in a list) when the condition goes on, and one that may run when the if doesn't start the line."""
    if line[:start].strip(" \t"):
        return list(UNKNOWN)
    condition, ended = split_condition(condition_text(line[start + 2:]))
    return judged(condition) if ended else [True, False, [condition]]


def track_branches(line, branches, command=True):
    """Keep branches, one [runs, always, pending] per open if, up to date for this line, and return whether the line ends where a command may start.

    runs is False while the branch being read can't run. Only a condition that is plainly false (or plainly true,
    for its else) is judged, and only on an if that starts its line; any other branch may run. A condition that
    goes on past its line is pending (the lines so far, in a list) and is judged at its real then, from all its
    lines; one that has no then within PENDING_LIMIT lines may run. Keywords
    count in source order, so a fi closes the block it ends before an if later on the line opens the next, and an
    else flips the branch whether it starts the line or follows a ;. A quoted or commented keyword is not one, and
    neither is a keyword where no command starts, such as the line after `echo x \\`: command says whether the line
    starts one. A line inside a multi-line string or a non-data heredoc body that looks like a fi or an else can
    misjudge a branch, either way: it can pop a branch that never runs, so an export in it counts again, as it did
    before branches were tracked, or flip one.
    """
    if branches and branches[-1][2] is not None:
        part, ended = split_condition(condition_text(line))
        branches[-1][2].append(part)
        if ended:
            branches[-1] = judged("\n".join(branches[-1][2]))
        elif len(branches[-1][2]) > PENDING_LIMIT:
            branches[-1] = list(UNKNOWN)
    where = []
    found, command = keywords(line, command, where=where)
    for index, word in enumerate(found):
        if word == "if":
            # Only the first keyword of a line can be an if that starts it.
            branches.append(opened(line, where[index]) if index == 0 else list(UNKNOWN))
        elif word == "fi":
            if branches:
                branches.pop()
        elif branches:
            branches[-1] = [not branches[-1][1], branches[-1][1], None] if word == "else" else list(UNKNOWN)
    return command


def comment_start(line):
    """The index of the # that starts a comment in line, a # that begins a word outside quotes and ${...}, or -1.

    A space, tab or ; & | ( ) < > before the # ends the word only when no backslash escapes it (foo\\ # is one word).
    A ${ that never closes holds the rest of the line.
    """
    position = 0
    boundary = True
    while position < len(line):
        char = line[position]
        if char == "\\":
            position += 2
            boundary = False
        elif char in "'\"":
            position = quoted_end(line, position)
            boundary = False
        elif line.startswith("${", position):
            end = line.find("}", position)
            position = len(line) if end < 0 else end + 1
            boundary = False
        elif char == "#" and boundary:
            return position
        else:
            boundary = char in " \t;&|()<>"
            position += 1
    return -1


def continues(line):
    """True when line ends in an odd number of backslashes outside a comment, so bash joins the next line to it."""
    return (len(line) - len(line.rstrip("\\"))) % 2 == 1 and comment_start(line) < 0


def untold(path):
    """Each line of path with a gh command not told its repository, as file:line: text."""
    lines = read_lines(path)
    joins = joined_lines(lines)
    found = []
    exported = False
    bodies = []
    # The first line of the bodies, the last line of the command that opens them, and a substitution a body leaves open.
    body_start = header_end = 0
    pending = None
    branches = []
    last = None
    # Whether the line being read starts a command: not the line after one that bash joins to it (joined_lines is True); the line after an unjudged line (None) keeps what track_branches returned.
    starts_command = True
    for number, line in enumerate(lines, 1):
        if bodies and number >= body_start:
            delimiter, tabs, quoted = bodies[0]
            if (line.lstrip("\t") if tabs else line) == delimiter:
                bodies.pop(0)
                pending = None
                # The line that closes a body joins nothing, even when a quoted delimiter ends in a backslash.
                joins[number - 1] = False
            else:
                codes, pending = substitutions(line, pending) if not quoted else ([], None)
                if not OPT_OUT.search(line) and any(
                        not told(code[:match.start()], code[match.end():], exported)
                        for code in codes for match in GH.finditer(code)):
                    found.append(f"{for_log(str(path))}:{number}: {shown(line)}")
            continue
        if re.match(r"[ \t]*#", line):
            starts_command = True
            continue
        if not bodies and number > header_end:
            header, count = logical_line(lines, number)
            header_end = number + count - 1
            body_start = header_end + 1
            if last is None and "<<" in header:
                last = last_lines(lines)
            bodies = data_heredocs(header, lines, header_end, last)
        starts_command = track_branches(line, branches, starts_command) or joins[number - 1] is False
        if EXPORTS.match(line):
            # An export in a branch that never runs leaves GH_REPO unset.
            exported = exported or all(runs for runs, _, _ in branches)
        elif UNSET.search(line) or any(
                not PREFIX_OF_GH.match(line, assignment.start()) for assignment in ASSIGNMENT.finditer(line)):
            exported = False
        if OPT_OUT.search(line):
            continue
        earlier, truncated = continued_text(lines, number, joins)
        if any(not told(line[:match.start()], line[match.end():], exported, earlier, truncated) for match in GH.finditer(line)):
            found.append(f"{for_log(str(path))}:{number}: {shown(line)}")
    return found


# Repo-relative paths --repo skips (the Owned Baseline files); a trailing / is a folder.
OWNED = (
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
