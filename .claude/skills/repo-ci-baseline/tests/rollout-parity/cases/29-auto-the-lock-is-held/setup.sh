#!/usr/bin/env bash
# Another round holds the lock: a process that keeps its descriptor, ended by after.sh.
set -euo pipefail
mkdir -p .cache/repo-ci-baseline
exec 9>> .cache/repo-ci-baseline/rollout-auto.lock
flock -n 9
sleep 60 > /dev/null 2>&1 &
echo $! > holder.pid
