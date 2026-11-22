#!/usr/bin/env python3
"""Fail on a central NuGet pin that no project references, under transitive pinning.

Run by scripts/gate.sh before the build, and by the Build Solution job in .github/workflows/ci.yml before restore:

    python3 -I .github/scripts/check_central_pins.py --root .

Under central package management with CentralPackageTransitivePinningEnabled, a PackageVersion in Directory.Packages.props that no
project references still pins the package wherever it comes in through another one. Dependabot doesn't raise such a pin when it bumps
the package that needs it, so restore fails with NU1109 until someone raises it by hand (mpaulosky/IssueManager#340 and mpaulosky/IssueManager#351).
Deleting the pin lets NuGet resolve the version the parent package asks for.

A pin kept on purpose, such as one that forces a package up past a security advisory, is allowed when the XML comment right before
it starts with "Transitive pin:" and gives the reason:

    <!-- Transitive pin: GHSA-xxxx-xxxx-xxxx, fixed in 4.3.1; System.Net.Http comes in through Foo. -->
    <PackageVersion Include="System.Net.Http" Version="4.3.4" />

A package counts as referenced when any MSBuild file in the repo (a project, .props or .targets file) names it in a PackageReference
(Include or Update) or as an MSBuild SDK (a Project Sdk attribute, an Sdk element or an Import's Sdk), or global.json names it under
msbuild-sdks. NuGet ids are compared ignoring case. A name holding an MSBuild property ($(...)) can't be read without evaluating
MSBuild, so such a pin is left alone. Transitive pinning is on when any of those MSBuild files sets the property to true; with it off,
an unreferenced pin pins nothing and the check passes. bin/, obj/, node_modules/ and dot-folders (.git, .nuget's package cache) are
skipped.

Standard library only.
"""

import argparse
import json
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

MSBUILD_SUFFIXES = (".csproj", ".fsproj", ".vbproj", ".proj", ".props", ".targets")
SKIPPED_FOLDERS = {"bin", "obj", "node_modules"}
REASON_MARKER = "transitive pin:"


def msbuild_files(root):
    """Every MSBuild file under root, sorted, outside the skipped folders."""
    found = []
    for folder, subfolders, files in os.walk(root):
        subfolders[:] = [name for name in subfolders if name not in SKIPPED_FOLDERS and not name.startswith(".")]
        found.extend(Path(folder) / name for name in files if name.lower().endswith(MSBUILD_SUFFIXES))
    return sorted(found)


def local_name(element):
    """The element's tag without its namespace, lowercased; a comment has none."""
    return element.tag.rpartition("}")[2].lower() if isinstance(element.tag, str) else ""


def parse(path):
    """The file's root element, with its comments kept as elements."""
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    return ET.fromstring(path.read_text(encoding="utf-8-sig", errors="replace"), parser=parser)


def names(value):
    """The package ids in an Include, Update or Sdk value: split on ';', any '/version' dropped, lowercased."""
    for part in (value or "").split(";"):
        name = part.strip().split("/")[0].strip()
        if name:
            yield name.lower()


def references(element):
    """The package ids element and everything under it reference."""
    found = set()
    for node in element.iter():
        tag = local_name(node)
        if tag == "packagereference":
            for attribute in ("Include", "Update"):
                found.update(names(node.get(attribute)))
        elif tag == "project" or tag == "import":
            found.update(names(node.get("Sdk")))
        elif tag == "sdk":
            found.update(names(node.get("Name")))
    return found


def pins_transitively(element):
    """Whether element sets CentralPackageTransitivePinningEnabled to true anywhere."""
    return any(
        local_name(node) == "centralpackagetransitivepinningenabled" and (node.text or "").strip().lower() == "true"
        for node in element.iter()
    )


def pins(element):
    """(id as written, lowercased id, has a reason) for each PackageVersion directly in one of element's ItemGroups."""
    found = []
    for group in element:
        if local_name(group) != "itemgroup":
            continue
        before = None
        for node in group:
            if node.tag is ET.Comment:
                before = node
                continue
            if local_name(node) == "packageversion":
                text = (before.text or "").strip() if before is not None else ""
                reason = text.lower().startswith(REASON_MARKER) and bool(text[len(REASON_MARKER):].strip())
                for part in (node.get("Include") or "").split(";"):
                    if part.strip():
                        found.append((part.strip(), part.strip().lower(), reason))
            before = None
    return found


def global_json_sdks(root):
    """The lowercased MSBuild SDK ids global.json names under msbuild-sdks."""
    path = root / "global.json"
    if not path.is_file():
        return set()
    try:
        sdks = json.loads(path.read_text(encoding="utf-8-sig")).get("msbuild-sdks") or {}
    except (ValueError, AttributeError):
        return set()
    return {name.lower() for name in sdks} if isinstance(sdks, dict) else set()


def unreferenced_pins(root):
    """(whether transitive pinning is on, [(central file, id) for each unreferenced pin with no reason])."""
    referenced = global_json_sdks(root)
    transitive = False
    central = []
    for path in msbuild_files(root):
        try:
            element = parse(path)
        except ET.ParseError as error:
            raise SystemExit(f"❌ {path.relative_to(root)} is not well-formed XML ({error}): fix it first, as restore will fail on it too.") from None
        referenced |= references(element)
        transitive = transitive or pins_transitively(element)
        if path.name.lower() == "directory.packages.props":
            central.append((path, element))
    unreferenced = [
        (path, name)
        for path, element in central
        for name, key, reason in pins(element)
        if "$(" not in name and key not in referenced and not reason
    ]
    return transitive, unreferenced


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".", help="the repo root")
    args = parser.parse_args(argv)
    root = Path(args.root).resolve()

    transitive, unreferenced = unreferenced_pins(root)
    if not transitive:
        print("Transitive pinning is off (or there is no central package management): no central pin can hold a package back.")
        return 0
    if not unreferenced:
        print("Every central pin is referenced by a project, or kept with a reason.")
        return 0
    annotate = os.environ.get("GITHUB_ACTIONS") == "true"
    for path, name in unreferenced:
        where = path.relative_to(root).as_posix()
        message = f"{name} is pinned in {where} but no project references it"
        print(f"::error file={where}::{message}" if annotate else f"❌ {message}")
    print(
        "Under CentralPackageTransitivePinningEnabled such a pin holds the package at its version wherever it comes in through another one, "
        "and Dependabot doesn't raise it when it bumps that one, so restore fails with NU1109. Delete each pin above. "
        "To keep one on purpose (a security fix, say), put a comment right before it: <!-- Transitive pin: <the reason> -->.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
