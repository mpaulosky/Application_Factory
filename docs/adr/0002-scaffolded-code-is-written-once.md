---
status: accepted
---
# Scaffolded code is written once and not managed

Creation copies the Starter's code (`.slnx`, projects, props files, `CLAUDE.md`) once and renames it, after which it belongs to the Application. Drift detection and
Reapply cover only Baseline files, as `repo-ci-baseline` does today. Applications change their scaffolding immediately, so managing it would produce constant false Drift
and risk overwriting real work.

## Consequences

A fix to the Starter reaches only Applications created afterwards. Existing Applications must be updated by hand.
