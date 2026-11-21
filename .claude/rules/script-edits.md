---
paths:
  - "**/*.sh"
  - "**/*.py"
  - "scripts/**"
  - ".github/hooks/**"
---

# Editing scripts

Write and edit shell scripts with the Write and Edit tools. `scripts/lint-edited-file.sh` is a Claude Code `PostToolUse` hook on Write, Edit and MultiEdit,
run from `.claude/settings.json` where the repository turns it on: it runs shellcheck and `shfmt -d` on a shell file the moment the tool writes it, so a finding is
fixed in the same turn. A Bash command doesn't name the files it wrote, so the hook never sees them. It lints no Python: for a Python script the checks below are
all there is before the gate. To write the format shfmt asks for, run `shfmt -w <file>` with no style flags, so it reads `.editorconfig`, on a `*.sh` or a git hook;
an extensionless file in `scripts/` is in no `.editorconfig` section, so shfmt would format it in its own style, not the repository's.

Reviewers keep finding the same gaps in script changes. Close them in the edit that opens them:

- **A test for every new branch.** Each new branch, guard, fallback or error path gets a test that fails without it. Remove or invert the line and see the test
  fail before you count it as covered: a test that passes either way doesn't test it.
- **The header or docstring in the same edit.** When a script's arguments, exit codes, environment variables, outputs or rules change, update the comment or
  docstring that describes them in the same edit, and every doc that repeats it.
- **The edge cases that keep coming back.** For each input, decide what happens, and test it, when there is:
  - a tool that is missing, or another release than the one the script needs;
  - an empty or relative path, one that starts with `-`, or one with a space, a newline or bytes that aren't valid UTF-8;
  - input with CRLF line endings or a byte-order mark;
  - a symlink where a file is expected;
  - a variable a caller left set (`GIT_DIR`, `CDPATH`, one of the script's own), or one that is unset under `set -u`.
- **The error contract.** Keep to the exit codes and outputs the script's header gives. A failure says on stderr what failed and names the file or tool; a check
  that checked nothing never passes. A failure inside `$(...)` or a process substitution doesn't stop `set -e`, so check its status yourself.
