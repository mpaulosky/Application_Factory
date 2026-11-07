---
status: accepted
---
# The Factory owns the shared skills and the starting points

The sibling skills (`build-repair`, `dotnet-add-testing`, `dotnet-inspect`, `dotnet-project-analysis`, `dotnet-tdd`, `dotnet-testing-strategy`, `dotnet-xunit`, `remove-squad`, `review-changes`)
and the optional app-stack files (`.aspire/`, `.github/instructions/`, `.github/agents/beast.agent.md`, `.github/skills/code-review/`) lived in `dotfiles` under `Support For New Projects/` beside
the Template. ADR 0005 moved only `repo-ci-baseline`. The Factory now owns the rest too: the skills are in `.claude/skills/` and the starting points are in `starting-points/`, which keeps GitHub
from treating the copied `.github/` files as this repo's own. They are copied without history. It builds on ADR 0005.

## Considered Options

- **Leave them in `dotfiles`**: no work, but the source of truth for Applications would stay split across two repos, and `dotfiles` is meant to hold machine configuration.
- **Move them to the `Blazor-Server` Starter**: the app-stack files fit there, but the skills are used on every .NET repo, not only a Starter's descendants.
- **Move them to the Factory** (chosen): one repo holds everything an Application is created from or kept aligned with.

## Consequences

- Edit these files here. The `dotfiles` copies are stale from now on and are deleted in a later change; the `~/.claude/skills` symlinks point here.
- The Template's Owned `sandcastle.yml` still clones `mpaulosky/dotfiles` and reads skills from `Support For New Projects/.claude/skills/`, at the commit each Application pins in
  `.github/ci/sandcastle-skills.txt`. Those pins stay valid while `dotfiles` keeps its history. Repointing the workflow at this repo changes an Owned file and every Application's pin, so it is a
  Rollout, and it comes before the `dotfiles` copies are deleted.
- The skills' Markdown predates the Factory's 200-character line limit, so `.markdownlint-cli2.jsonc` ignores them and the starting points rather than rewrap them.
