#!/usr/bin/env bash
# A link to itself and two links to each other: each is a symlink to nothing, not a hang or a crash.
set -euo pipefail
ln -s self scripts/self
ln -s b scripts/a
ln -s a scripts/b
