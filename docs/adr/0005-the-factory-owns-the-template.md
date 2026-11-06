---
status: accepted
---
# The Factory owns the Template, the scripts and the Fleet registry

The `repo-ci-baseline` skill (its Template, Apply, Reapply, Rollout, Status and Land scripts, `github/repos.txt`, tests and references) moves from `mpaulosky/dotfiles` into this repo, with its history, so the Factory is the single source of truth for every Application. `dotfiles` stops carrying Baseline copies and keeps only its own files. This repo becomes public, because Application workflows clone the source at run time. It supersedes ADR 0001.

## Considered Options

- **Leave the skill in `dotfiles` and have the Factory call it** (ADR 0001): no migration, but the source of truth for Applications stays in an unrelated repo that also consumes the Baseline, so its CI and the Template's tests are entangled.
- **Keep this repo private and give workflows a token**: avoids publishing, but adds a credential to every Application for no gain, since the content is already public in `dotfiles`.

## Consequences

- Cutover order: import with history and get the Template tests green here; repoint the `~/.claude/skills/repo-ci-baseline` symlink; remove the skill and its workflow from `dotfiles`; then one Fleet re-Apply rewrites the functional references (clone URLs, pins, repo slugs). Comments that cite historical `mpaulosky/dotfiles#NNN` PRs stay as they are.
- The skill keeps its name and its relative path (`.claude/skills/repo-ci-baseline/`); renaming it is a separate change.
- `dotfiles` needs its own CI checks reworked, since its "Template tests" required check goes away.
- This repo gains a real CI workflow for the Template tests, but not the full .NET Baseline.
