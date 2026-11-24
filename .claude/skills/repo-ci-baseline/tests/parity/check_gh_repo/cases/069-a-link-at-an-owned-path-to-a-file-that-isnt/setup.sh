#!/usr/bin/env bash
# A link at an Owned path to a file that isn't Owned: the file is judged by where it really is, and checked.
set -euo pipefail
mkdir -p .claude/skills/pre-push-review
ln -s ../../../notes/x.md .claude/skills/pre-push-review/x.md
