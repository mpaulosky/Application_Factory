#!/usr/bin/env python3
"""Fail a pull request that changes, deletes or adds an Owned Baseline file.

    python3 -I check_owned_files.py --base <sha> --base-ref <branch> [--repo <checkout>]

CI's gh-repo job runs the base commit's copy of this script, written outside the checkout, so a PR can't weaken its own
check. It reads the base's OWNED list (files, and folders with a trailing /) out of the base's
.github/scripts/check_gh_repo.py, then compares the checkout's HEAD (the PR's merge commit) with the base:

- every path under an Owned entry in the base's tree: its tree entry ("<mode> <type> <sha>", a tab, the path) must be
  the same at HEAD, so a changed blob, a changed mode (chmod -x, a symlink swap), a deleted or a renamed file differs;
- every file under an Owned entry at HEAD must be in the base's tree too, so a file the PR adds to an Owned folder, or
  an Owned file the base lacks, is reported: the gh check skips Owned files, so nothing else would read it.

Each difference is one line, "Owned file <path> differs from <branch>", with \\r and \\n in the path written as those
two characters, since a path is the PR's and a line break in it could start a workflow command in the log.

Exit 0 when nothing differs, and when the base has no check_gh_repo.py (the first Apply: there is nothing to compare
with); 1 when something differs; 2 on a usage error or a base commit git can't read.
"""

import argparse
import importlib.util
import subprocess
import sys
import tempfile
from pathlib import Path

CHECKER = ".github/scripts/check_gh_repo.py"


def for_log(text):
    """text (bytes) with \\r and \\n escaped, so it stays on its own log line."""
    return text.replace(b"\r", b"\\r").replace(b"\n", b"\\n")


def git(repo, *args, check=True):
    return subprocess.run(["git", "-C", str(repo), *args], capture_output=True, check=check)


def records(output):
    """The NUL-terminated records of a `git ls-tree -z` output."""
    return [record for record in output.split(b"\0") if record]


def owned_entries(repo, base):
    """The base checker's OWNED list, or None when the base has no checker."""
    if git(repo, "cat-file", "-e", f"{base}:{CHECKER}", check=False).returncode != 0:
        return None
    with tempfile.TemporaryDirectory(prefix="check_owned_files-") as folder:
        path = Path(folder) / "check_gh_repo.py"
        path.write_bytes(git(repo, "show", f"{base}:{CHECKER}").stdout)
        spec = importlib.util.spec_from_file_location("check_gh_repo", path)
        module = importlib.util.module_from_spec(spec)
        sys.dont_write_bytecode = True
        spec.loader.exec_module(module)
        return list(module.OWNED)


def differing(repo, base, entries):
    """The paths (bytes) under the Owned entries that differ between base and HEAD, each once, in the order found."""
    found = []
    for entry in entries:
        base_records = records(git(repo, "ls-tree", "-r", "-z", base, "--", entry).stdout)
        base_paths = set()
        for base_record in base_records:
            path = base_record.split(b"\t", 1)[1]
            base_paths.add(path)
            if records(git(repo, "ls-tree", "-z", "HEAD", "--", path).stdout) != [base_record]:
                found.append(path)
        for head_record in records(git(repo, "ls-tree", "-r", "-z", "HEAD", "--", entry).stdout):
            path = head_record.split(b"\t", 1)[1]
            if path not in base_paths and git(repo, "cat-file", "-e", b"%s:%s" % (base.encode(), path), check=False).returncode != 0:
                found.append(path)
    return list(dict.fromkeys(found))


def main(argv=None):
    parser = argparse.ArgumentParser(prog="check_owned_files.py", description="Fail a PR that changes an Owned Baseline file.")
    parser.add_argument("--base", required=True, help="the commit the PR's merge commit was built from")
    parser.add_argument("--base-ref", required=True, help="the base branch's name, for the messages")
    parser.add_argument("--repo", default=".", help="the checkout, at the PR's merge commit (default: .)")
    args = parser.parse_args(argv)
    out = sys.stdout.buffer
    base_ref = for_log(args.base_ref.encode())

    if git(args.repo, "cat-file", "-e", f"{args.base}^{{commit}}", check=False).returncode != 0:
        print(f"check_owned_files.py: can't read the base commit {args.base!r} in {args.repo}", file=sys.stderr)
        return 2
    entries = owned_entries(args.repo, args.base)
    if entries is None:
        out.write(base_ref + b" has no " + CHECKER.encode() + b": no Owned files to compare (the first Apply).\n")
        return 0
    paths = differing(args.repo, args.base, entries)
    for path in paths:
        out.write(b"Owned file " + for_log(path) + b" differs from " + base_ref + b"\n")
    out.flush()
    return 1 if paths else 0


if __name__ == "__main__":
    sys.exit(main())
