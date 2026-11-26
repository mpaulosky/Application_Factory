#!/usr/bin/env bash
# Makes what a file in tree/ can't hold.
set -euo pipefail
ln -s outside/real.txt Linked.props
ln -s missing.props Dangling.props
ln -s outside/real.txt global.json
