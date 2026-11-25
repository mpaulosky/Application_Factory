#!/usr/bin/env bash
echo "reapply.sh: working on ${3##*/}"
sleep 30 > /dev/null 2>&1 &
echo $! > "$HOME/sleeper.pid"
kill -TERM "$(cat "$FAKE_PID_FILE")"
wait
