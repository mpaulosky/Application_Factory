---
status: accepted
---
# Shell files are formatted with shfmt

The Factory and the Template installed shfmt only so the Template's gh check could read each script's syntax tree (the skill's
[ADR 0011](../../.claude/skills/repo-ci-baseline/docs/adr/0011-the-gh-repo-check-parses-shell-with-shfmt.md)); nothing checked how a shell file was formatted. Before this ADR, 44 of
the 56 shell files would have changed under shfmt, each in its own mix of one-line `if ...; then ...; fi` blocks, `case` arms with two commands, and both `>/dev/null` and `> /dev/null`.
Formatting is a choice a reviewer should not have to read or argue about. This ADR builds on ADR 0006 (the gate) and ADR 0011 (the edit-time lint, which is to run shfmt next).

## Decision: one shfmt style, written in the Template's .editorconfig, checked by the gate

- The style is `shfmt -i 2 -ci -bn -sr`: two-space indents, indented `case` arms, `&&`, `||` and `|` starting the continued line, and a space after a redirection (`> /dev/null`). Of the styles
  tried, it changed the fewest lines (1,699 removed, against 1,989 without `-sr`). Most of the change is shfmt splitting a one-line compound command over several lines, which no shfmt
  option avoids.
- The settings are in the Template's Owned `.editorconfig`, in a `[*.sh]` section and a `[.github/hooks/*]` section for the three hooks, which have no extension (a glob with a `/` is anchored to
  the file's folder, so the two can't be one section). shfmt reads `.editorconfig` when it is given no style flags, so the style is written down once, and an editor that reads `.editorconfig` formats
  the same way. The Factory carries a copy of the file (`scripts/gate.sh` lists it with the other copies), so its own scripts get the same style.
- Every tracked and untracked shell file (a `*.sh` or one of the three hooks) was formatted with `shfmt -w`, and the gate's `static` part runs `shfmt -d` on all of them, not only the ones a
  branch changed: the check takes about a second. It needs shfmt v3.14 or a later v3, the version `test.sh` already needs, so a different shfmt's output can't fail a file the pinned one passes.
- The gh check is unaffected: it runs shfmt in an empty folder of its own, so no `.editorconfig` reaches it (the skill's `references/gotchas.md`).

## Considered Options

- **Explicit flags in each caller.** The gate, CI and the edit-time hook would each spell `-i 2 -ci -bn -sr`, and an editor would not know them. Rejected: `.editorconfig` is the place shfmt and
  editors already look.
- **A Factory-only `.editorconfig`.** The Template's `.editorconfig` is `root = true`, so its section is the one that reaches the Template's files under the Factory, and the one an Application gets.
  The style lives there, and the Factory copies it.
- **Check only the changed shell files, as shellcheck does.** Rejected: the whole repo is formatted now, and checking every file costs about a second and keeps it that way.

## Consequences

- An Application's Owned scripts change shape at the next Rollout, with no change in what they do. An Application's own shell files are not checked: its `.editorconfig` gives them the
  style, and checking them is a separate decision.
- Test fixtures that write shell files into a throwaway repo the gate checks must be written in the same style (`scripts/tests/gate.test.sh`'s stub `test.sh`).
- A test that reads a script's text must not depend on its old layout: the gate test's shard check allows a `step` indented inside its shard block, and the mutation spec for `cleanup.sh`
  and `work-issue.sh` matches the formatted text.
- shfmt lines up the trailing comments of consecutive lines, so a long line next to a commented one pushes that comment far right. Put such a comment on its own line, or a blank line
  between the two.
