---
status: accepted
---
# Shell files are formatted with shfmt

The Factory and the Template installed shfmt only so the Template's gh check could read each script's syntax tree (the skill's
[ADR 0011](../../.claude/skills/repo-ci-baseline/docs/adr/0011-the-gh-repo-check-parses-shell-with-shfmt.md)); nothing checked how a shell file was formatted. Before this ADR, 44 of
the 56 shell files would have changed under shfmt, each in its own mix of one-line `if ...; then ...; fi` blocks, `case` arms with two commands, and both `>/dev/null` and `> /dev/null`.
Formatting is a choice a reviewer should not have to read or argue about. This ADR builds on ADR 0006 (the gate) and ADR 0012 (its parts). The Factory's edit-time lint
([ADR 0011](0011-markdown-and-yaml-are-linted-as-they-are-written.md)) lints Markdown and YAML only; running shfmt from it too is a later decision, not made here (the Update
below makes it).

## Decision: one shfmt style, written in the Template's .editorconfig, checked by the gate

- The style is `shfmt -i 2 -ci -bn -sr`: two-space indents, indented `case` arms, `&&`, `||` and `|` starting the continued line, and a space after a redirection (`> /dev/null`). It changed
  the fewest lines of the styles tried. Each was run as `shfmt -d <flags>` over the 56 shell files of `origin/main` before this change, counting the lines the diff removes:

  | Flags | Files changed | Lines removed |
  | --- | --- | --- |
  | `-i 2` | 42 | 2,186 |
  | `-i 2 -ci` | 42 | 2,054 |
  | `-i 2 -sr` | 45 | 1,901 |
  | `-i 2 -ci -sr` | 45 | 1,766 |
  | `-i 2 -ci -bn` | 42 | 1,989 |
  | `-i 2 -ci -bn -sr` (chosen) | 44 | 1,699 |

  Most of the change is shfmt splitting a one-line compound command over several lines, which no shfmt option avoids. `-sr` is the less common redirection style today (80 lines with
  `> /dev/null` against 298 with `>/dev/null`), but it changes fewer lines overall.
- The settings are in the Template's Owned `.editorconfig`, in a `[*.sh]` section and a `[.github/hooks/*]` section for the three hooks, which have no extension (a glob with a `/` is anchored to
  the file's folder, so the two can't be one section). shfmt reads `.editorconfig` when it is given no style flags, so the style is written down once, and an editor that reads `.editorconfig` formats
  the same way. The Factory carries a copy of the whole file (`scripts/gate.sh` lists it with the other copies, and `--sync` copies a file whole), its C# and .NET sections included, which match
  nothing in the Factory; its own scripts get the same style.
- Every tracked and untracked shell file was formatted with `shfmt -w`, and the gate's `static` part runs `shfmt -d` on all of them, not only the ones a branch changed: the check takes well
  under a second. A shell file is a `*.sh`, or a `pre-commit`, `pre-push` or `post-checkout` in the repo's `.github/hooks` or the Template's copy of it, the hooks folders an
  `.editorconfig` gives the style to; the shellcheck step uses the same rule.
- The gate needs shfmt v3.14 or a later v3, the releases `test.sh` accepts (the same regular expression, which the gate's test compares). That refuses v2, v4 and a v3 before v3.14. It doesn't
  make every accepted release format alike: a later v3 that formats differently would fail files that CI's pinned release passes, and then the files are reformatted or the pin moves.
- The gh check is unaffected: it runs shfmt in an empty folder of its own, so no `.editorconfig` reaches it (the skill's `references/gotchas.md`).

## Considered Options

- **Explicit flags in each caller.** The gate, CI and any later caller would each spell `-i 2 -ci -bn -sr`, and an editor would not know them. Rejected: `.editorconfig` is the place shfmt and
  editors already look.
- **A Factory-only `.editorconfig`.** The Template's `.editorconfig` is `root = true`, so its section is the one that reaches the Template's files under the Factory, and the one an Application gets.
  The style lives there, and the Factory copies it.
- **Check only the changed shell files, as shellcheck does.** Rejected: the whole repo is formatted now, and checking every file costs well under a second and keeps it that way.

## Consequences

- The Owned `.editorconfig` and the Owned scripts and hooks reach an Application at the next Rollout, together, with no change in what the scripts do. An Application's own shell files are
  not checked, but an editor that reads `.editorconfig`, and `shfmt -w` run with no flags, now format them in this style.
- Test fixtures that write shell files into a throwaway repo the gate checks must be written in the same style (`scripts/tests/gate.test.sh`'s stub `test.sh`).
- A test that reads a script's text must not depend on its old layout: the gate test's shard check counts a `step` whatever its indent, and the mutation spec for `cleanup.sh` and
  `work-issue.sh` matches the formatted text.
- shfmt lines up the trailing comments of consecutive lines, so a long line next to a commented one pushes that comment far right; and where it splits a commented one-liner, the comment
  stays on the last line. Put such a comment on its own line above the code, or a blank line between the two lines.

## Update

An Application's gate now checks format too, but only on the shell files a branch changes: the Owned `scripts/gate.sh` has a **Shell format** step, after Shell lint, that runs
`shfmt -d --` with no style flags on the changed `*.sh` files and git hooks (its `SHFMT_FILES` list). An Application's own scripts were never formatted, and reformatting all of them
in a Rollout would bury the Rollout's real changes, so each one takes the style when someone next touches it. An extensionless `scripts/` file, which the gate's shellcheck step
also checks, is in no `.editorconfig` section, so shfmt would want its own defaults there; it is left out. The step needs shfmt v3.14 or a later v3 release, which the gh check already
needs, and stops, naming it, without one. An Application's CI doesn't check format: its Lint Actions workflow runs shellcheck on every file, and checking only changed files there
would need the PR's base, so the gate, which the pre-push hook runs, is the check.

The edit-time lint ([ADR 0011](0011-markdown-and-yaml-are-linted-as-they-are-written.md), its Update) runs `shfmt -d` on the same files as each is written, in the Factory and in
Applications, so the format is fixed in the turn that broke it. The Factory's gate still checks every shell file.
