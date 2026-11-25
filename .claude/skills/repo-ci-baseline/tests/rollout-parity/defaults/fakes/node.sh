#!/usr/bin/env bash
# The default node: asked where it really runs from, it says nothing, so the round takes the file PATH finds.
if [[ "$*" == "-p process.execPath" ]]; then
  exit 0
fi
echo "UNMATCHED node $* (no default answers it)" >> "$FAKE_GH_LOG"
exit 97
