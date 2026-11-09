---
status: superseded by ADR-0005
---
# The Factory calls repo-ci-baseline instead of absorbing it

`repo-ci-baseline` already owns the Baseline Template, `apply.sh`, `reapply.sh`, `rollout.sh`, `status.sh` and the Fleet registry (`repos.txt`). The Factory adds only the
Request-driven Creation flow (create from the Starter, then rename), and invokes the baseline scripts for everything else. This avoids two copies of the hooks and
workflows that could drift apart.

## Considered Options

- **Absorb the Baseline Template into the Factory and retire `repo-ci-baseline`**: one repo, but it means migrating tested scripts and re-pointing the existing Fleet.
- **Keep them separate with a manual baseline step**: simplest, but a Creation would no longer produce a ready-to-work repo.

## Consequences

The Factory depends on `repo-ci-baseline` being installed and on its script interfaces staying stable. Revisit if that coupling becomes painful.
