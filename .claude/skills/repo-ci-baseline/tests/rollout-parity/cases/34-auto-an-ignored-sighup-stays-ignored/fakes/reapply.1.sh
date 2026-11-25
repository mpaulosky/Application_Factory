#!/usr/bin/env bash
echo "reapply.sh: working on ${3##*/}"
kill -HUP "$(cat "$FAKE_PID_FILE")"
mask="$(sed -n "s/^SigIgn:[[:space:]]*//p" "/proc/$$/status")"
if ((0x$mask & 1)); then echo "reapply.sh: SIGHUP is ignored here too"; fi
sleep 0.5
