# Editing Markdown and YAML

Write and edit Markdown and YAML files with the Write and Edit tools, never with a shell command (`cat <<EOF >`, `sed -i`, `tee`, `echo >>`, a `python`
or `node` one-liner, a script).

`scripts/lint-edited-file.sh` is a Claude Code `PostToolUse` hook on Write, Edit and MultiEdit, run from `.claude/settings.json` where the repository turns it on.
It lints the file the moment the tool writes it, so a violation is fixed in the same turn. A Bash command doesn't name the files it wrote, so the hook never sees
them, and the violation turns up later: at the commit for Markdown (the `pre-commit` hook lints no YAML), otherwise at the gate, in CI or in a review.

A script or test that writes Markdown or YAML as its own output is fine; the rule is about the files you are editing.
