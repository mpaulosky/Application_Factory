#!/usr/bin/env bash
# Links git can't be trusted to keep as a fixture: one inside the repo, one out of it, one to nothing.
set -euo pipefail
ln -s real scripts/inside
ln -s /etc scripts/outside
ln -s missing scripts/dangling
ln -s ../scripts/real/a.bash scripts/again.bash
