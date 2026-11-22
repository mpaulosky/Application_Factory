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
case. An id holding $(Name) is resolved from the values the repo's MSBuild files give that property; one that still holds $(, @( or
%( can't be read without evaluating MSBuild. A pin like that is left alone, and a reference like that could be any package, so the
pins found are then reported as warnings and the check passes. Transitive pinning is on when any of those files sets the property to
true.

Every file is read as bytes, so expat honours its byte-order mark and XML declaration (MSBuild reads UTF-16 project files too). A
DOCTYPE or entity declaration is refused: MSBuild files never need one, and entity expansion is how a small file grows huge. Only
regular files are read, not symlinks, and none over MAX_BYTES. A file that can't be read or parsed is skipped with a notice naming
it: skipping one can only hide a reference, so the check gets stricter, never more lenient, and restore fails on such a file anyway
when it is one the build reads. bin/, obj/, node_modules/ and dot-folders (.git, CI's .nuget package cache) aren't walked.

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
# The most ids one reference may resolve to (each $(Name) with several values multiplies them); past it the reference can't be read.
MAX_EXPANSIONS = 256
REASON_MARKER = "transitive pin:"
PROPERTY = re.compile(r"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)")
UNREADABLE = ("$(", "@(", "%(")


class Node:
    """An element (tag lowercased, without a prefix) or a comment (tag None), with its attributes, text and children."""

    def __init__(self, tag, attributes=None, text=""):
        self.tag = tag
        self.attributes = attributes or {}
        self.text = text
        self.children = []

    def iter(self):
        yield self
        for child in self.children:
            yield from child.iter()


class Refused(Exception):
    pass


def refuse(*_):
    # Entities can only be declared inside a DOCTYPE, so refusing it refuses them too.
    raise Refused("a DOCTYPE or entity declaration, which an MSBuild file never needs")


def parse(data):
    """The document node of data (bytes), or Refused or expat.ExpatError."""
    document = Node("#document")
    stack = [document]
    parser = expat.ParserCreate()

    def start(name, attributes):
        node = Node(name.rpartition(":")[2].lower(), attributes)
        stack[-1].children.append(node)
        stack.append(node)

    def end(_):
        stack.pop()

    def characters(text):
        stack[-1].text += text

    def comment(text):
        stack[-1].children.append(Node(None, text=text))

    parser.StartElementHandler = start
    parser.EndElementHandler = end
    parser.CharacterDataHandler = characters
    parser.CommentHandler = comment
    parser.StartDoctypeDeclHandler = refuse
    parser.Parse(data, True)
    return document


def printable(text):
    """text with every non-printable character as '?', so a name can't move the terminal or start a line."""
    return "".join(character if character.isprintable() else "?" for character in text)


def escape_data(text):
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(text):
    return escape_data(text).replace(":", "%3A").replace(",", "%2C")


def report(kind, where, message):
    """Print one problem: an annotation (kind error, warning or notice) under GitHub Actions, a plain line elsewhere."""
    where, message = printable(where), printable(message)
    if os.environ.get("GITHUB_ACTIONS") == "true":
        print(f"::{kind} file={escape_property(where)}::{escape_data(message)}")
    else:
        print(f"{ {'error': '❌', 'warning': '⚠️ ', 'notice': 'ℹ️ '}[kind]} {message}")


def read(path, root, skipped):
    """path's bytes, or None after noting in skipped why it wasn't read."""
    where = path.relative_to(root).as_posix()
    try:
        info = os.lstat(path)
        if stat.S_ISLNK(info.st_mode):
            skipped.append((where, "it is a symlink"))
            return None
        if not stat.S_ISREG(info.st_mode):
            skipped.append((where, "it is not a regular file"))
            return None
        if info.st_size > MAX_BYTES:
            skipped.append((where, f"it is over {MAX_BYTES} bytes"))
            return None
        # One byte past the cap, so a file that grew since lstat is still caught.
        with open(path, "rb") as handle:
            data = handle.read(MAX_BYTES + 1)
    except OSError as error:
        skipped.append((where, f"it can't be read ({error.strerror or error})"))
        return None
    if len(data) > MAX_BYTES:
        skipped.append((where, f"it is over {MAX_BYTES} bytes"))
        return None
    return data


def walk(root):
    """(MSBuild files, global.json files) under root, sorted, outside the skipped folders."""
    msbuild, global_json = [], []
    for folder, subfolders, files in os.walk(root):
        subfolders[:] = sorted(name for name in subfolders if name not in SKIPPED_FOLDERS and not name.startswith("."))
        for name in files:
            lowered = name.lower()
            if MSBUILD_FILE.search(lowered):
                msbuild.append(Path(folder) / name)
            elif lowered == "global.json":
                global_json.append(Path(folder) / name)
    return sorted(msbuild), sorted(global_json)


def tokens(text):
    """JSON text as (is a string, piece) runs, with // and /* */ comments outside strings dropped."""
    index, start = 0, 0
    while index < len(text):
        if text[index] == '"':
            yield False, text[start:index]
            end = index + 1
            while end < len(text) and text[end] != '"':
                end += 2 if text[end] == "\\" else 1
            yield True, text[index:end + 1]
            index = start = end + 1
        elif text.startswith("//", index) or text.startswith("/*", index):
            yield False, text[start:index]
            if text[index + 1] == "/":
                newline = text.find("\n", index)
                index = len(text) if newline == -1 else newline
            else:
                close = text.find("*/", index + 2)
                index = len(text) if close == -1 else close + 2
            start = index
        else:
            index += 1
    yield False, text[start:]


def without_comments(text):
    """JSON text without // and /* */ comments or trailing commas, as the .NET host reads global.json."""
    # Comments out first, then, between the strings of what is left, a comma before a closing bracket: a comment between the two
    # would otherwise split them into two runs.
    joined = "".join(piece for _, piece in tokens(text))
    return "".join(piece if is_string else re.sub(r",(\s*)(?=[}\]])", r"\1", piece) for is_string, piece in tokens(joined))


def global_json_sdks(data):
    """The lowercased ids under a global.json's msbuild-sdks, or None when it isn't JSON the host would read."""
    try:
        document = json.loads(without_comments(data.decode("utf-8-sig")))
    except (UnicodeDecodeError, ValueError):
        return None
    sdks = document.get("msbuild-sdks") if isinstance(document, dict) else None
    return {name.lower() for name in sdks} if isinstance(sdks, dict) else set()


def ids(value):
    """The ids in an Include, Update or Sdk value: split on ';', any '/version' dropped, blanks left out."""
    for part in (value or "").split(";"):
        name = part.strip().split("/")[0].strip()
        if name:
            yield name


def properties(document, found):
    """Add to found (lowercased name -> set of values) every property a PropertyGroup in document sets."""
    for node in document.iter():
        if node.tag == "propertygroup":
            for child in node.children:
                if child.tag:
                    found.setdefault(child.tag, set()).add(child.text.strip())


def resolve(name, values):
    """The lowercased ids name can be, with each $(Name) replaced by every value the repo gives it, or None when it can't be read."""
    keys = list(dict.fromkeys(key.lower() for key in PROPERTY.findall(name)))
    if not keys:
        return None if any(marker in name for marker in UNREADABLE) else {name.lower()}
    choices = [sorted(values.get(key, ())) for key in keys]
    count = 1
    for choice in choices:
        count *= len(choice)
    if count == 0 or count > MAX_EXPANSIONS:
        return None
    resolved = set()
    for combination in itertools.product(*choices):
        mapping = dict(zip(keys, combination, strict=True))
        text = PROPERTY.sub(lambda match, mapping=mapping: mapping[match.group(1).lower()], name)
        if any(marker in text for marker in UNREADABLE):
            return None
        resolved.update(part.lower() for part in ids(text))
    return resolved


def references(document):
    """The ids document references, as written."""
    for node in document.iter():
        if node.tag in ("packagereference", "globalpackagereference", "packagedownload"):
            for attribute in ("Include", "Update"):
                yield from ids(node.attributes.get(attribute))
        elif node.tag in ("project", "import"):
            yield from ids(node.attributes.get("Sdk"))
        elif node.tag == "sdk":
            yield from ids(node.attributes.get("Name"))


def pins(document):
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
                for name in ids(node.attributes.get("Include")):
                    yield name, reason
            before = None


def check(root):
    """(transitive pinning on, [(central file, id)] unreferenced, [unreadable references], [(file, why)] skipped)."""
    msbuild, global_json = walk(root)
    skipped, documents = [], []
    for path in msbuild:
        data = read(path, root, skipped)
        if data is None:
            continue
        try:
            documents.append((path, parse(data)))
        except (expat.ExpatError, Refused) as error:
            skipped.append((path.relative_to(root).as_posix(), f"it isn't well-formed MSBuild XML ({error})"))
    referenced = set()
    for path in global_json:
        data = read(path, root, skipped)
        if data is None:
            continue
        sdks = global_json_sdks(data)
        if sdks is None:
            skipped.append((path.relative_to(root).as_posix(), "it isn't JSON the .NET host reads"))
        else:
            referenced |= sdks
    values = {}
    for _, document in documents:
        properties(document, values)
    transitive = any(
        node.tag == "centralpackagetransitivepinningenabled" and node.text.strip().lower() == "true"
        for _, document in documents
        for node in document.iter()
    )
    unreadable = []
    for _, document in documents:
        for name in references(document):
            resolved = resolve(name, values)
            if resolved is None:
                unreadable.append(name)
            else:
                referenced |= resolved
    unreferenced = {}
    for path, document in documents:
        if path.name.lower() != "directory.packages.props":
            continue
        for name, reason in pins(document):
            resolved = resolve(name, values)
            if resolved is None or reason:
                continue
            for key in sorted(resolved - referenced):
                unreferenced.setdefault((path, key), name if len(resolved) == 1 else key)
    return transitive, [(path, name) for (path, _), name in unreferenced.items()], unreadable, skipped


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".", help="the repo root")
    args = parser.parse_args(argv)
    root = Path(args.root).resolve()

    transitive, unreferenced, unreadable, skipped = check(root)
    for where, why in skipped:
        report("notice", where, f"{where} was skipped: {why}. A reference in it wasn't seen.")
    if not transitive:
        print("Transitive pinning is off (or there is no central package management): no central pin can hold a package back.")
        return 0
    if not unreferenced:
        print("Every central pin is referenced by a project, or kept with a reason.")
        return 0
    kind = "warning" if unreadable else "error"
    for path, name in unreferenced:
        where = path.relative_to(root).as_posix()
        report(kind, where, f"{name} is pinned in {where} but no project references it")
    if unreadable:
        shown = ", ".join(sorted({printable(name) for name in unreadable}))
        print(f"Not failing: a reference this check can't read without evaluating MSBuild ({shown}) may name any of the pins above.")
        return 0
    print(
        "Under CentralPackageTransitivePinningEnabled such a pin holds the package at its version wherever it comes in through another one, "
        "and Dependabot doesn't raise it when it bumps that one, so restore fails with NU1109. Delete each pin above. "
        "To keep one on purpose (a security fix, or a package an SDK references itself), put a comment right before it: "
        "<!-- Transitive pin: <the reason> -->.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
