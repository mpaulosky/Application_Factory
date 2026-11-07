# Application Factory

Creates new .NET application repositories on GitHub and keeps them aligned with a shared Baseline, driven through issues, pull requests, skills and hooks.

The Factory is the source of truth for every Application. It holds the Template (the CI, release and git-hook files each Application carries),
the Fleet registry, and the scripts that Apply the Baseline, find Drift and roll changes out. The terms are defined in [`GLOSSARY.md`](GLOSSARY.md).

## What's here

| Path | What it is |
| --- | --- |
| `.claude/skills/repo-ci-baseline/` | The skill: the Template (`template/owned/`, `template/seed/`), the scripts, the references and their tests |
| `.claude/skills/repo-ci-baseline/github/repos.txt` | The Fleet registry: the Applications the Factory manages |
| `docs/adr/` | The decisions behind the Factory |
| `docs/Instructions.md` | Setting up a new Application, or standardizing an existing one |
| `.github/workflows/template-tests.yml` | CI: runs the Template's tests on every PR |

## Using it

The skill is available to Claude Code as `repo-ci-baseline` (link `.claude/skills/repo-ci-baseline` into `~/.claude/skills/`). Its scripts:

```bash
BASELINE="$HOME/github/Application_Factory/.claude/skills/repo-ci-baseline"

"$BASELINE/apply.sh" --dry-run <repo>   # what Apply would write; changes nothing
"$BASELINE/apply.sh" <repo>             # copy the Baseline into an Application
"$BASELINE/status.sh"                   # open PRs across the Fleet, each with its next landing action (read-only)
"$BASELINE/github-settings.sh" --fix <owner/repo>   # merge settings, ruleset, required checks, labels
```

[`docs/Instructions.md`](docs/Instructions.md) walks through the whole sequence, and the skill's [`SKILL.md`](.claude/skills/repo-ci-baseline/SKILL.md)
is the reference. `reapply.sh --brings <prs> <repo>` re-Applies the Baseline to one Application in its re-Apply PR,
`rollout.sh` runs that across the Fleet (TicketManager first), and `land.sh` watches a round's PRs until they land.

## Working on the Factory

Edit the Template, not an Application's copy of an Owned file. Turn the git hooks on once per clone with `git config core.hooksPath .github/hooks`, and commit in a worktree. The pre-push hook
runs `scripts/gate.sh`; to run the tests by hand:

```bash
.claude/skills/repo-ci-baseline/test.sh
```

It needs `actionlint`, `zizmor`, `yamllint`, `shellcheck`, `markdownlint-cli2`, `node` and `git`. [`CLAUDE.md`](CLAUDE.md) has the layout, the commands and the git workflow.

## Status

This repo is public, because Application workflows clone the Template from it at run time ([ADR 0005](docs/adr/0005-the-factory-owns-the-template.md)).
Applications are created from the [`Blazor-Server`](https://github.com/mpaulosky/Blazor-Server) Starter ([ADR 0004](docs/adr/0004-applications-are-created-from-the-blazor-server-starter.md)).
