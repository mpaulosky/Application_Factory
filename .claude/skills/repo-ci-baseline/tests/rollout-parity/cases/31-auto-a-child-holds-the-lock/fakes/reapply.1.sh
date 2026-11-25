#!/usr/bin/env bash
lock="$HOME/.cache/repo-ci-baseline/rollout-auto.lock"
for fd in "/proc/$$/fd"/*; do
  [[ "$(readlink "$fd")" != "$lock" ]] || echo "reapply.sh holds the lock's descriptor"
done
flock -n "$lock" true || echo "reapply.sh: another flock of the lock is refused"
exit 1
