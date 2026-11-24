#!/usr/bin/env bash
# The links, made here so tree/ holds only regular files: one inside the repo, one out of it, one to nothing, one to a file.
# expected.tree/ holds them too, so the run is held to leaving them as they were.
set -euo pipefail
ln -s real scripts/inside
ln -s /etc scripts/outside
ln -s missing scripts/dangling
ln -s ../scripts/real/a.bash scripts/again.bash
