#!/usr/bin/env python3
"""Fail on a central NuGet pin that no project references, under transitive pinning.

Run by scripts/gate.sh before the build, and by the Build Solution job in .github/workflows/ci.yml right after the checkout:

    python3 -I .github/scripts/check_central_pins.py --root .

Under central package management with CentralPackageTransitivePinningEnabled, a PackageVersion in Directory.Packages.props that no
project references still pins the package wherever it comes in through another one. Dependabot doesn't raise such a pin when it bumps
the package that needs it, so restore fails with NU1109 until someone raises it by hand (mpaulosky/IssueManager#340 and
mpaulosky/IssueManager#351). Deleting the pin lets NuGet resolve the version the parent package asks for.

A pin kept on purpose, such as one that forces a package up past a security advisory, or one for a package an SDK references itself
(MSTest.Sdk's, say), is allowed when the XML comment right before it starts with "Transitive pin:" (any case) and gives the reason:

    <!-- Transitive pin: GHSA-xxxx-xxxx-xxxx, fixed in 4.3.1; System.Net.Http comes in through Foo. -->
    <PackageVersion Include="System.Net.Http" Version="4.3.4" />

What counts as a reference: an id in a PackageReference (Include or Update), GlobalPackageReference or PackageDownload, or an MSBuild
SDK (a Project Sdk attribute, an Sdk element, an Import's Sdk, or a global.json's msbuild-sdks), in any MSBuild file in the repo: a
file whose extension ends in "proj" (.csproj, .sqlproj, .wixproj, ...), or a .props or .targets file. NuGet ids are compared ignoring
case. An id holding $(Name) is resolved from the values the repo's MSBuild files give that property, wherever they set it. One that
still holds $(, @( or %(, that is over MAX_ID characters, or that would need more than MAX_EXPANSIONS combinations of values or grow
past MAX_VALUE characters with them substituted (any property value over MAX_VALUE does), can't be read without evaluating
MSBuild. A pin like that is left alone, and a reference like that could be any package, so the pins found are then reported as
warnings and the check passes. Transitive pinning is on when any of those files sets the property to true.

Every file is read as bytes, so expat honours its byte-order mark and XML declaration (MSBuild reads UTF-16 project files too). Only
regular files are read, not symlinks, and none over MAX_BYTES. A DOCTYPE is refused: MSBuild files never need one, and entity
expansion is how a small file grows huge. So are an element nested over MAX_DEPTH deep and an encoding Python can't decode. A file
that can't be read or parsed can't be checked:

- An MSBuild file that may hold pins or turn transitive pinning on (a Directory.Packages.props, one that names PackageVersion or
  CentralPackageTransitivePinningEnabled, or one whose bytes weren't read at all) fails the check, naming the file and why: skipping
  it could hide a pin or the setting. So does a folder that can't be listed, or a symlinked folder, which isn't walked. Under
  transitive pinning, so does any MSBuild file that couldn't be parsed when a central pin without a reason holds $( (is built from
  a property), since the file may give the property a value, and so a pin, the check never sees.
- Any other MSBuild file, and a global.json that can't be read (a symlink, a FIFO, one over MAX_GLOBAL_JSON_BYTES, or not JSON the
  .NET host reads), is skipped with a notice, naming it. It can hide references, which makes the check stricter. It can also hide
  the value of a property a reference is built from, which makes that reference unreadable: the pins found are then warnings and
  the check passes, naming the reference, as for any reference the check can't read.

Only the file itself is opened without following a symlink: a folder swapped for one between the walk and the read is followed, which
needs a process writing to the tree while the check runs, not a change in a pull request.

bin/, obj/, node_modules/ and dot-folders (.git, CI's .nuget package cache) aren't walked. The whole run reads at most MAX_FILES files,
MAX_TOTAL_BYTES, MAX_NODES elements, attributes and comments, MAX_IDS ids and MAX_TOTAL_EXPANSIONS combinations of property values;
past any of them, or more than MAX_GLOBAL_JSON_FILES global.json files, it fails, since it can't vouch for what it didn't read. Every
entry of an id list counts as an id, blank or not (an absent or empty attribute counts none); so does every entry of a name with its
properties substituted, once for each combination of values, and every SDK a global.json names. Each kind of line is printed at most
MAX_SHOWN times, and each name or path in it is cut after MAX_PRINTED characters, with a count of the rest.

A run with a file or folder it couldn't check always prints "Not a pass" as its last line on stdout, after any pins it found.

Under GitHub Actions each problem is an annotation, its text escaped as the runner reads workflow commands; elsewhere a plain line.
Every text a repo controls is printed with its non-printable characters as '?'.

Standard library only.
"""

import argparse
import itertools
import json
import os
import re
import stat
import sys
from pathlib import Path
from xml.parsers import expat

MSBUILD_FILE = re.compile(r"\.([a-z0-9]*proj|props|targets)$")
SKIPPED_FOLDERS = {"bin", "obj", "node_modules"}
MAX_BYTES = 4 * 1024 * 1024
MAX_TOTAL_BYTES = 64 * 1024 * 1024
# A global.json is a few hundred bytes; a larger one is skipped, and a repo with more than MAX_GLOBAL_JSON_FILES fails the run.
MAX_GLOBAL_JSON_BYTES = 64 * 1024
MAX_GLOBAL_JSON_FILES = 100
MAX_DEPTH = 256
MAX_NODES = 250_000
# The most combinations of property values one reference may resolve through (each $(Name) with several values multiplies them).
MAX_EXPANSIONS = 256
MAX_TOTAL_EXPANSIONS = 100_000
# A name with its properties substituted is read up to MAX_VALUE characters; an id up to MAX_ID (NuGet's own limit is 100).
MAX_VALUE = 1024
MAX_ID = 256
MAX_IDS = 250_000
MAX_FILES = 50_000
MAX_SHOWN = 100
MAX_PRINTED = 300
REASON_MARKER = "transitive pin:"
PROPERTY = re.compile(r"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)")
UNREADABLE = ("$(", "@(", "%(")
# What a file that may hold pins or turn transitive pinning on names: lowercased bytes, any NUL dropped so UTF-16 matches too.
CENTRAL_WORDS = (b"packageversion", b"centralpackagetransitivepinningenabled")


class Node:
    """An element (tag lowercased, without a prefix) or a comment (tag None), with its attributes, text and children."""

    def __init__(self, tag, attributes=None, text=""):
        self.tag = tag
        self.attributes = attributes or {}
        self.text = text
        self.children = []

    def iter(self):
        """This node and every one under it, in document order, without recursion."""
        pending = [self]
        while pending:
            node = pending.pop()
            yield node
            pending.extend(reversed(node.children))


class Refused(Exception):
    """A file this check won't parse."""


class Exhausted(Exception):
    """The run needs more than its budget."""


class Budget:
    """What the whole run may still read."""

    def __init__(self):
        self.totals = {
            "files": MAX_FILES,
            "global.json files": MAX_GLOBAL_JSON_FILES,
            "bytes": MAX_TOTAL_BYTES,
            "elements, attributes and comments": MAX_NODES,
            "ids": MAX_IDS,
            "combinations of property values": MAX_TOTAL_EXPANSIONS,
        }
        self.left = dict(self.totals)

    def spend(self, kind, amount):
        if self.left[kind] < amount:
            raise Exhausted(f"the repo needs more than the {self.totals[kind]} {kind} this check reads")
        self.left[kind] -= amount


def refuse(*_):
    # Entities can only be declared inside a DOCTYPE, so refusing it refuses them too.
    raise Refused("a DOCTYPE or entity declaration, which an MSBuild file never needs")


def parse(data, budget):
    """The document node of data (bytes). Raises Refused, expat.ExpatError, LookupError or ValueError when it can't be read."""
    document = Node("#document")
    stack = [document]
    # Text comes in many pieces (a line, an entity), kept in a list and joined once: adding each to a string would copy it every time.
    texts = [[]]
    parser = expat.ParserCreate()

    def start(name, attributes):
        if len(stack) > MAX_DEPTH:
            raise Refused(f"an element nested over {MAX_DEPTH} deep")
        budget.spend("elements, attributes and comments", 1 + len(attributes))
        node = Node(name.rpartition(":")[2].lower(), attributes)
        stack[-1].children.append(node)
        stack.append(node)
        texts.append([])

    def end(_):
        stack.pop().text = "".join(texts.pop())

    def characters(text):
        texts[-1].append(text)

    def comment(text):
        budget.spend("elements, attributes and comments", 1)
        stack[-1].children.append(Node(None, text=text))

    parser.StartElementHandler = start
    parser.EndElementHandler = end
    parser.CharacterDataHandler = characters
    parser.CommentHandler = comment
    parser.StartDoctypeDeclHandler = refuse
    parser.Parse(data, True)
    return document


def printable(text):
    """text with every non-printable character as '?', so a name can't move the terminal or start a line, cut after MAX_PRINTED."""
    if len(text) > MAX_PRINTED:
        text = f"{text[:MAX_PRINTED]}... ({len(text) - MAX_PRINTED} more)"
    return "".join(character if character.isprintable() else "?" for character in text)


def escape_data(text):
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(text):
    return escape_data(text).replace(":", "%3A").replace(",", "%2C")


def report(kind, where, message):
    """Print one problem: an annotation (kind error, warning or notice) under GitHub Actions, a plain line elsewhere.

    message must be built from texts already through printable() (report_all does that), so each is cut on its own."""
    where = printable(where)
    if os.environ.get("GITHUB_ACTIONS") == "true":
        print(f"::{kind} file={escape_property(where)}::{escape_data(message)}")
    else:
        print(f"{ {'error': '❌', 'warning': '⚠️ ', 'notice': 'ℹ️ '}[kind]} {message}")


def report_all(kind, items, message):
    """report() the first MAX_SHOWN (where, detail) items, each text through printable(), then say how many more there were."""
    for where, detail in items[:MAX_SHOWN]:
        report(kind, where, message(printable(where), printable(detail)))
    if len(items) > MAX_SHOWN:
        print(f"... and {len(items) - MAX_SHOWN} more like the {MAX_SHOWN} above.")


def read(path, root, problems, budget, cap=MAX_BYTES):
    """path's bytes, at most cap of them, or None after adding (where, why, fails the check) to problems."""
    where = path.relative_to(root).as_posix()
    # An MSBuild file whose bytes weren't read may hold anything; a global.json only SDK references.
    blocks = path.name.lower() != "global.json"
    try:
        info = os.lstat(path)
        if stat.S_ISLNK(info.st_mode):
            problems.append((where, "it is a symlink", blocks))
            return None
        if not stat.S_ISREG(info.st_mode):
            problems.append((where, "it is not a regular file", blocks))
            return None
        if info.st_size > cap:
            problems.append((where, f"it is over {cap} bytes", blocks))
            return None
        # The path may have changed since lstat: opened without following a symlink or waiting on a FIFO, and checked again.
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0) | getattr(os, "O_CLOEXEC", 0)
        descriptor = os.open(path, flags)
        try:
            if not stat.S_ISREG(os.fstat(descriptor).st_mode):
                problems.append((where, "it is not a regular file", blocks))
                return None
            with os.fdopen(descriptor, "rb") as handle:
                descriptor = None
                # One byte past the cap, so a file that grew since lstat is still caught.
                data = handle.read(cap + 1)
        finally:
            if descriptor is not None:
                os.close(descriptor)
    except OSError as error:
        problems.append((where, f"it can't be read ({error.strerror or error})", blocks))
        return None
    if len(data) > cap:
        problems.append((where, f"it is over {cap} bytes", blocks))
        return None
    budget.spend("bytes", len(data))
    return data


def may_be_central(path, data):
    """Whether an MSBuild file that couldn't be parsed may hold pins or turn transitive pinning on."""
    folded = data.replace(b"\x00", b"").lower()
    return path.name.lower() == "directory.packages.props" or any(word in folded for word in CENTRAL_WORDS)


def walk(root, problems, budget):
    """(MSBuild files, global.json files) under root, sorted, outside the skipped folders. A folder it can't walk goes in problems."""
    msbuild, global_json = [], []

    def where(folder):
        relative = Path(folder).relative_to(root).as_posix()
        return "." if relative == "." else relative + "/"

    def failed(error):
        problems.append((where(error.filename), f"it can't be listed ({error.strerror or error})", True))

    for folder, subfolders, files in os.walk(root, onerror=failed):
        kept = []
        for name in sorted(subfolders):
            if name in SKIPPED_FOLDERS or name.startswith("."):
                continue
            if os.path.islink(os.path.join(folder, name)):
                problems.append((where(os.path.join(folder, name)), "it is a symlinked folder, which isn't walked", True))
                continue
            kept.append(name)
        subfolders[:] = kept
        for name in files:
            lowered = name.lower()
            if MSBUILD_FILE.search(lowered):
                budget.spend("files", 1)
                msbuild.append(Path(folder) / name)
            elif lowered == "global.json":
                budget.spend("files", 1)
                budget.spend("global.json files", 1)
                global_json.append(Path(folder) / name)
    return sorted(msbuild), sorted(global_json)


# A JSON string (group 1; to the end of the text when it isn't closed), a // comment, or a /* */ comment (to the end when it isn't
# closed). Each pass replaces a match with its groups, so a string stays and a comment goes; the second also drops a trailing comma
# (group 2 keeps the space after it). The string's repeats are possessive (Python 3.11 or later, as the Baseline's 3.12 is), so the
# engine keeps no backtracking state per character: time is linear and memory small, in a file of at most MAX_GLOBAL_JSON_BYTES.
JSON_STRING_OR_COMMENT = r'("(?:[^"\\]++|\\.)*+"?)|//[^\n]*|/\*.*?(?:\*/|\Z)'
WITHOUT_COMMENTS = re.compile(JSON_STRING_OR_COMMENT, re.S)
WITHOUT_TRAILING_COMMAS = re.compile(JSON_STRING_OR_COMMENT + r"|,(\s*)(?=[}\]])", re.S)


def without_comments(text):
    """JSON text without // and /* */ comments or trailing commas, as the .NET host reads global.json."""
    # Comments out first, then, outside strings, a comma before a closing bracket: a comment between the two would otherwise keep it.
    return WITHOUT_TRAILING_COMMAS.sub(r"\1\2", WITHOUT_COMMENTS.sub(r"\1", text))


def global_json_sdks(data, budget):
    """The lowercased ids under a global.json's msbuild-sdks, or None when it isn't JSON the host would read.

    Each counts as an id; one over MAX_ID characters can't be a pin this check reads, so it is left out."""
    try:
        document = json.loads(without_comments(data.decode("utf-8-sig")))
    except (UnicodeDecodeError, ValueError, RecursionError):
        return None
    sdks = document.get("msbuild-sdks") if isinstance(document, dict) else None
    if not isinstance(sdks, dict):
        return set()
    budget.spend("ids", len(sdks))
    return {name.lower() for name in sdks if len(name) <= MAX_ID}


def ids(value, budget):
    """The ids in an Include, Update or Sdk value: split on ';', any '/version' dropped, blanks left out.

    Every entry counts as an id, blank or not, before the value is split; an absent or empty value counts as none."""
    if not value:
        return
    budget.spend("ids", value.count(";") + 1)
    for part in value.split(";"):
        name = part.strip().split("/")[0].strip()
        if name:
            yield name


def properties(document, found):
    """Add to found (lowercased name -> set of values) every property a PropertyGroup in document sets, wherever the group is."""
    for node in document.iter():
        if node.tag == "propertygroup":
            for child in node.children:
                if child.tag:
                    found.setdefault(child.tag, set()).add(child.text.strip())


def resolve(name, values, budget, cache):
    """The lowercased ids name can be, with each $(Name) replaced by every value the repo gives it, or None when it can't be read.

    values is (lowercased name -> set of values, lowercased name -> its longest value's length)."""
    if len(name) > MAX_ID:
        return None
    if not any(marker in name for marker in UNREADABLE):
        return {name.lower()}
    if name not in cache:
        cache[name] = expand(name, values, budget)
    return cache[name]


def expand(name, values, budget):
    found, widest = values
    # name is at most MAX_ID characters, so it holds at most MAX_ID / 4 placeholders.
    placeholders = PROPERTY.findall(name)
    keys = list(dict.fromkeys(key.lower() for key in placeholders))
    count = 1
    for key in keys:
        count *= len(found.get(key, ()))
    if count == 0 or count > MAX_EXPANSIONS:
        return None
    # The longest the substituted name can be, from the longest value of each placeholder: past MAX_VALUE it isn't built.
    longest = len(PROPERTY.sub("", name)) + sum(widest[key.lower()] for key in placeholders)
    if longest > MAX_VALUE:
        return None
    budget.spend("combinations of property values", count)
    choices = [sorted(found[key]) for key in keys]
    resolved = set()
    for combination in itertools.product(*choices):
        mapping = dict(zip(keys, combination, strict=True))
        text = PROPERTY.sub(lambda match, mapping=mapping: mapping[match.group(1).lower()], name)
        if any(marker in text for marker in UNREADABLE):
            return None
        for part in ids(text, budget):
            if len(part) > MAX_ID:
                return None
            resolved.add(part.lower())
    return resolved


def references(document, budget):
    """The ids document references, as written."""
    for node in document.iter():
        if node.tag in ("packagereference", "globalpackagereference", "packagedownload"):
            for attribute in ("Include", "Update"):
                yield from ids(node.attributes.get(attribute), budget)
        elif node.tag in ("project", "import"):
            yield from ids(node.attributes.get("Sdk"), budget)
        elif node.tag == "sdk":
            yield from ids(node.attributes.get("Name"), budget)


def pins(document, budget):
    """(id as written, has a reason) for each PackageVersion in any ItemGroup of document."""
    for group in document.iter():
        if group.tag != "itemgroup":
            continue
        before = None
        for node in group.children:
            if node.tag is None:
                before = node
                continue
            if node.tag == "packageversion":
                text = before.text.strip() if before is not None else ""
                reason = text.lower().startswith(REASON_MARKER) and bool(text[len(REASON_MARKER):].strip())
                for name in ids(node.attributes.get("Include"), budget):
                    yield name, reason
            before = None


def check(root):
    """(transitive pinning on, [(central file, id)] unreferenced, [unreadable references], [(where, why, fails the check)] problems).

    Raises Exhausted when the repo needs more than the run's budget."""
    budget = Budget()
    problems = []
    msbuild, global_json = walk(root, problems, budget)
    documents = []
    for path in msbuild:
        data = read(path, root, problems, budget)
        if data is None:
            continue
        try:
            documents.append((path, parse(data, budget)))
        except (expat.ExpatError, Refused, LookupError, ValueError) as error:
            problems.append((path.relative_to(root).as_posix(), f"it isn't well-formed MSBuild XML ({error})", may_be_central(path, data)))
    referenced = set()
    for path in global_json:
        data = read(path, root, problems, budget, MAX_GLOBAL_JSON_BYTES)
        if data is None:
            continue
        sdks = global_json_sdks(data, budget)
        if sdks is None:
            problems.append((path.relative_to(root).as_posix(), "it isn't JSON the .NET host reads", False))
        else:
            referenced |= sdks
    found = {}
    for _, document in documents:
        properties(document, found)
    values = (found, {key: max(map(len, choices)) for key, choices in found.items()})
    transitive = any(
        node.tag == "centralpackagetransitivepinningenabled" and node.text.strip().lower() == "true"
        for _, document in documents
        for node in document.iter()
    )
    cache = {}
    unreadable = {}
    for _, document in documents:
        for name in references(document, budget):
            resolved = resolve(name, values, budget, cache)
            if resolved is None:
                unreadable.setdefault(name)
            else:
                referenced |= resolved
    unreferenced = {}
    built_pin = False
    for path, document in documents:
        if path.name.lower() != "directory.packages.props":
            continue
        for name, reason in pins(document, budget):
            if reason:
                continue
            # Only a property can take a value from a file the check couldn't read; @( and %( can't be read whatever the files say.
            built_pin = built_pin or "$(" in name
            resolved = resolve(name, values, budget, cache)
            if resolved is None:
                continue
            for key in sorted(resolved - referenced):
                unreferenced.setdefault((path, key), name if len(resolved) == 1 else key)
    if transitive and built_pin:
        # A skipped MSBuild file may set a value of the property such a pin is built from, so it could hide a pin. With transitive
        # pinning off no pin is reported, and a skipped file whose bytes name the setting (in ASCII, or UTF-16 or UTF-32 with the
        # NULs dropped) already fails; one in an encoding that spells it otherwise is the gap may_be_central() leaves.
        problems = [(where, why, blocks or not where.lower().endswith("global.json")) for where, why, blocks in problems]
    return transitive, [(path, name) for (path, _), name in unreferenced.items()], list(unreadable), problems


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".", help="the repo root")
    args = parser.parse_args(argv)
    root = Path(args.root).resolve()
    if not root.is_dir():
        parser.error(f"--root {printable(args.root)} is not a folder")

    try:
        transitive, unreferenced, unreadable, problems = check(root)
    except Exhausted as error:
        report("error", ".", f"Not checked: {error}.")
        return 1
    skipped = [(where, why) for where, why, blocks in problems if not blocks]
    blocking = [(where, why) for where, why, blocks in problems if blocks]
    report_all("notice", skipped, lambda where, why: f"{where} was skipped: {why}. A reference or property in it wasn't seen.")
    report_all("error", blocking, lambda where, why: f"{where} can't be checked: {why}. It may hold central pins, turn transitive pinning on or set a property a pin is built from.")
    status = 1 if blocking else 0
    if not transitive:
        if not blocking:
            print("Transitive pinning is off in every MSBuild file read (or there is no central package management): no pin found can hold a package back.")
    elif not unreferenced:
        if not blocking:
            print("Every central pin is referenced by a project, or kept with a reason.")
    else:
        found = [(path.relative_to(root).as_posix(), name) for path, name in unreferenced]
        report_all("warning" if unreadable else "error", found, lambda where, name: f"{name} is pinned in {where} but no project references it")
        if unreadable:
            names = sorted(printable(name) for name in unreadable)
            shown = ", ".join(names[:MAX_SHOWN]) + (f" and {len(names) - MAX_SHOWN} more" if len(names) > MAX_SHOWN else "")
            print(f"Reported as warnings: a reference this check can't read without evaluating MSBuild ({shown}) may name any of the pins above.")
        else:
            status = 1
            print(
                "Under CentralPackageTransitivePinningEnabled such a pin holds the package at its version wherever it comes in through another one, "
                "and Dependabot doesn't raise it when it bumps that one, so restore fails with NU1109. Delete each pin above. "
                "To keep one on purpose (a security fix, or a package an SDK references itself), put a comment right before it: "
                "<!-- Transitive pin: <the reason> -->.",
                file=sys.stderr,
            )
    if blocking:
        print("Not a pass: the files and folders above couldn't be checked, and may hold what the check looks for.")
        print(
            "Each file or folder that can't be checked must be readable for the check to pass: a regular file (not a symlink) "
            f"of at most {MAX_BYTES} bytes holding well-formed XML without a DOCTYPE, nested at most {MAX_DEPTH} deep, in an encoding "
            "Python decodes; or a folder that isn't a symlink and can be listed.",
            file=sys.stderr,
        )
    return status


if __name__ == "__main__":
    sys.exit(main())
