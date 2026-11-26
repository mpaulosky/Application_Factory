#!/usr/bin/env bash
# Makes what a file in tree/ can't hold.
set -euo pipefail
mkfifo Pipe.props sub/global.json
