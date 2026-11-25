#!/usr/bin/env bash
# The process reapply.sh left in its group must be gone (or a zombie) once the round has ended.
set -euo pipefail
pid="$(cat sleeper.pid)"
rm sleeper.pid
for ((i = 0; i < 50; i++)); do
  stat="$(cat "/proc/$pid/stat" 2> /dev/null)" || exit 0
  stat="${stat##*) }"
  [[ "${stat%% *}" != Z ]] || exit 0
  sleep 0.1
done
kill -KILL "$pid"
echo "reapply.sh's group outlived the round: $pid was still running"
exit 1
