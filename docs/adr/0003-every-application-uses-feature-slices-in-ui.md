---
status: accepted
---
# Every Application uses feature slices inside the UI project

Each Application's features live in the UI project, one folder per use case under `Components/Features/<Feature>/`, with `Domain` as the Shared Kernel and
`ServiceDefaults` for cross-cutting Aspire wiring. Every Starter has this layout, and its `Architecture.Tests` enforce it: no slice depends on another, `UI` references
only `Domain` and `ServiceDefaults`, and `Domain` depends on neither. This follows the decision in the `Blazor-Server` Starter (its ADR 0005). Standardizing it means
every Application reads the same way, so a person or an agent moving between repos finds features in the same place.

## Considered Options

- **A `Core` class library holding the slices**: the compiler would keep slices off Blazor, but each slice would be split across two projects. Rejected for the reasons in
  `auth0-management-app` ADR 0005.
- **Make the layout optional per Request**: more flexible, but then the architecture tests and the Starter would need variants, and Applications would stop looking alike.

## Consequences

- No project boundary separates slices, so the slice rule is only as strong as the architecture test.
- Handlers and validators live in an executable project, so `UI` turns off CA1515 in its `.editorconfig`.
- Changing the layout later means changing every Application's tests as well as the Starter.
