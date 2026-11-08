---
status: accepted
---
# The Factory owns the shared skills and the starting points

The sibling skills (`build-repair`, `dotnet-add-testing`, `dotnet-inspect`, `dotnet-project-analysis`, `dotnet-tdd`, `dotnet-testing-strategy`, `dotnet-xunit`, `remove-squad`, `review-changes`)
and the optional app-stack files (`.aspire/`, `.github/instructions/`, `.github/agents/beast.agent.md`, `.claude/skills/code-review/`) lived in `dotfiles` under `Support For New Projects/` beside
the Template. ADR 0005 moved only `repo-ci-baseline`. The Factory now owns the rest too: the skills are in `.claude/skills/` and the starting points are in `starting-points/`, which keeps GitHub
from treating the copied `.github/` files as this repo's own. They are copied without history. It builds on ADR 0005.

## Considered Options

- **Leave them in `dotfiles`**: no work, but the source of truth for Applications would stay split across two repos, and `dotfiles` is meant to hold machine configuration.
- **Move them to the `Blazor-Server` Starter**: the app-stack files fit there, but the skills are used on every .NET repo, not only a Starter's descendants.
- **Move them to the Factory** (chosen): one repo holds everything an Application is created from or kept aligned with.

## Consequences

- Edit these files here. The `dotfiles` copies are stale from now on and are deleted in a later change; the `~/.claude/skills` symlinks point here.
- **Update:** the Template's Owned `sandcastle.yml` now clones this repo and reads skills from `.claude/skills/`, at the Factory commit each Application pins in
  `.github/ci/sandcastle-skills.txt` (the key is still called `dotfiles-sha`). The `dotfiles` copies of the skills and starting points were removed, `dotfiles` is archived, and `repos.txt`,
  the settings check and the `--brings` PR-link default no longer name it. Nothing in the Factory depends on `dotfiles`; a guard test (`tests/no-dotfiles-dependency.test.sh`) keeps it that way.
- The skills' Markdown predates the Factory's 200-character line limit, so `.markdownlint-cli2.jsonc` ignores them and the starting points rather than rewrap them.
