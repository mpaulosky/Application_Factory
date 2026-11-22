# Editing Markdown and YAML

Write and edit Markdown and YAML files with the Write and Edit tools, never with a shell command (`cat <<EOF >`, `sed -i`, `tee`, `echo >>`, a `python`
or `node` one-liner, a one-off script you write to change them).

`scripts/lint-edited-file.sh` is a Claude Code `PostToolUse` hook on Write, Edit and MultiEdit, run from `.claude/settings.json` where the repository turns it on.
It lints the file the moment the tool writes it, so a violation is fixed in the same turn. A Bash command doesn't name the files it wrote, so the hook never sees
them, and the violation turns up later: at the commit for Markdown (the `pre-commit` hook lints no YAML), otherwise at the gate, in CI or in a review.

Where `.claude/settings.json` has no such entry (a `PostToolUse` hook whose command is `scripts/lint-edited-file.sh`), nothing lints the file as you write it:
lint each file you edit yourself, `markdownlint-cli2 <file>` for Markdown and `yamllint -c .yamllint.yml <file>` for YAML,
with `actionlint <file>` and `zizmor --offline <file>` too for a workflow and `zizmor --offline <file>` for `.github/dependabot.yml`, before you commit it. The repository
turns the hook on by adding the entry the script's header gives to `.claude/settings.json`; that is the repository's choice, not an edit to make unasked.

The rule is about the files you are editing. Running one of the repository's own scripts or tests that writes Markdown or YAML as its output (a report, a
fixture, a record) is fine.
