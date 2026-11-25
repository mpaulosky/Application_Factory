#!/usr/bin/env bash
echo "reapply.sh: working on ${3##*/}"
bash "$FAKE_TOOL_DIR/make-worktree.sh" "$3"
echo "gate: no review recorded for this diff" >&2
exit 3
