#!/usr/bin/env bash
# End the process setup.sh left holding the lock.
set -euo pipefail
kill "$(cat holder.pid)"
rm holder.pid
