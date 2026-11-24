#!/usr/bin/env bash
# main's history as one empty commit per merge, the Release tags on theirs, and a local bare origin.
set -euo pipefail
git init -q -b main .
commit() { git commit -q --allow-empty -m "$1"; }
commit "Initial commit"
commit "Merge 5"
git init -q --bare -b main "$PARITY_SCRATCH/origin.git"
git remote add origin "$PARITY_SCRATCH/origin.git"
git push -q origin main --tags
