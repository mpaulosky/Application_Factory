#!/usr/bin/env bash
# The base (some code and some docs, tagged base), then this case's change committed on top. The files are then removed, so the
# run's folder holds only what the script writes (and the case's out, when it has one); the diff is between the two commits.
set -euo pipefail
git init -q -b main .
mkdir -p src docs
printf 'class App {}\n' > src/App.cs
printf '# Guide\n' > docs/guide.md
printf '# Readme\n' > README.md
git add -A
git commit -q -m base
git tag base
printf 'x\n' > README.MD
printf 'x\n' > docs/notes.MD
git add -A
git commit -q --allow-empty -m change
find . -mindepth 1 -maxdepth 1 ! -name .git ! -name out -exec rm -rf -- {} +
