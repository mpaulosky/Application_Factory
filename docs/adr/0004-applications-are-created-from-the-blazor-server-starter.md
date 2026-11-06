---
status: accepted
---
# Applications are created from the Blazor-Server Starter

`mpaulosky/Blazor-Server` is a GitHub template repository, and `auth0-management-app` was generated from it and then renamed. A Creation uses `gh repo create --template mpaulosky/Blazor-Server` and then a scripted rename pass, instead of the Factory keeping its own scaffolding. The Starter's stack (Blazor Web App, Aspire, Tailwind, Auth0, Theme and Palette) is kept whole, so Auth0 is always on.

## Considered Options

- **A Factory-owned scaffold**: full control, but it would duplicate a maintained repo and drift from it.
- **A minimal UI without Tailwind or Auth0**: simpler Applications, but not what the Starter or the existing Applications are.

## Consequences

- Changes to the stack happen in `Blazor-Server`, not here, and reach only Applications created afterwards.
- The rename pass must cover the solution name, file headers, the Auth0 role-claim namespace, `UserSecretsId`, and the README and `CLAUDE.md` text that describes the repo as the Starter.
- Only one Application type exists until a second Starter does.
