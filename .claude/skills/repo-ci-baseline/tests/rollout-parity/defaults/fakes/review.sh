#!/usr/bin/env bash
# The default scripts/review.sh of a re-Apply worktree (make-worktree.sh writes one that runs this): prepare prints the
# code's key and the HEAD the default git gives, record takes the review on stdin, and advisory finds none open.
case "${1:-}" in
  prepare) echo "key-2222 2222222222222222222222222222222222222222" ;;
  record)
    cat > /dev/null
    echo "review.sh: recorded the review"
    ;;
  advisory) echo "No open Advisory findings." ;;
  *)
    echo "UNMATCHED review.sh $* (no default answers it)" >> "$FAKE_GH_LOG"
    exit 97
    ;;
esac
