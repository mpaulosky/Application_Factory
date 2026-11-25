#!/usr/bin/env bash
echo "git: reading HEAD" >&2
kill -TERM "$(cat "$FAKE_PID_FILE")"
sleep 5
