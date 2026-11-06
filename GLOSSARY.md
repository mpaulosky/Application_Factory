# Application Factory

A personal system that creates new .NET application repositories on GitHub and keeps them aligned with a shared Baseline, driven through issues, pull requests, skills and hooks.

## Language

**Factory**:
The Application Factory itself: the source of truth for every Application. It holds the Template, the Fleet registry, the skills and scripts, and the rename pass that produces Applications from a Starter.
_Avoid_: Generator, scaffolder, platform

**Application**:
A .NET solution that lives in its own GitHub repository and was produced by the Factory.
_Avoid_: Project, app, service, repo (when meaning the product rather than its hosting)

**Creation**:
One Factory run that turns a Request into a single ready-to-work Application repository: create from the Starter, rename, apply the Baseline.
_Avoid_: Scaffolding run, generation, provisioning

**Request**:
A GitHub issue asking the Factory to create an Application.
_Avoid_: Ticket, order

**Application type**:
The kind of solution a Request asks for, which decides the Starter a Creation uses. Only Blazor Server (with Aspire) exists today.
_Avoid_: Flavor, kind, archetype

**Feature slice**:
One folder under an Application's UI project holding everything a single use case needs, with no dependency on any other slice.
_Avoid_: Module, feature folder, vertical

**Template**:
The Factory's source files for the Baseline, split into Owned files (overwritten on every Apply) and Seed files (written only if the Application has none).
_Avoid_: Scaffold, skeleton, boilerplate

**Baseline**:
The shared set of CI, hooks, workflows and process files that every Application carries, as produced by Applying the Template.
_Avoid_: Standard, boilerplate

**Starter**:
The GitHub template repository an Application is created from (today, `mpaulosky/Blazor-Server`).
_Avoid_: Template, scaffold, skeleton, boilerplate

**Apply**:
Copying the Baseline into an Application's repository.
_Avoid_: Install, deploy, sync

**Fleet**:
The set of Applications the Factory manages, listed in the Factory's registry.
_Avoid_: Portfolio, estate

**Drift**:
The difference between an Application's Baseline files and what the current Baseline would produce.
_Avoid_: Divergence, staleness
