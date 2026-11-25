#!/usr/bin/env bash
# The default dotnet: an SDK that matches the stand-in Template's global.json.
if [[ "$*" == --version ]]; then
  echo 11.0.100
  exit 0
fi
echo "UNMATCHED dotnet $* (no default answers it)" >> "$FAKE_GH_LOG"
exit 97
