# Central package pins

Template files (Owned): `.github/scripts/check_central_pins.py` and its tests, run by `scripts/gate.sh` right before the build and by `ci.yml`'s
`Build Solution` job right before restore. mpaulosky/Application_Factory#105.

## Why

Under central package management with `CentralPackageTransitivePinningEnabled`, a `PackageVersion` in `Directory.Packages.props` pins its package
even where no project references it: wherever the package comes in through another one, it gets the pinned version. Dependabot's `nuget` updater
doesn't raise such a pin when it bumps the package that needs it, so the bump fails restore with NU1109 (a downgrade the pin forces).
IssueManager's all-nuget PRs (mpaulosky/IssueManager#340 and mpaulosky/IssueManager#351) both failed this way: Radzen.Blazor needed a newer `Microsoft.AspNetCore.Components.Web`, and
`xunit.v3.mtp-v2` newer `Microsoft.Testing.*` packages, than pins no project referenced. Each was raised by hand, and would have fallen behind again.

The pins were already in the `all-nuget` group (`"*"`), so grouping them can't help, and a version range trades the failure for builds that resolve
differently from one restore to the next. Deleting the pin lets NuGet resolve the version the parent package asks for.

## What the check refuses

With transitive pinning on (any MSBuild file in the repo sets the property to `true`), a `PackageVersion` whose id no project, `.props` or `.targets` file
references in a `PackageReference` (`Include` or `Update`) or as an MSBuild SDK, and `global.json` doesn't name under `msbuild-sdks`. Ids are compared
ignoring case; an id built from an MSBuild property (`$(...)`) is left alone; `bin/`, `obj/`, `node_modules/` and dot-folders are skipped. With transitive
pinning off, an unreferenced pin pins nothing and the check passes.

## Keeping a pin on purpose

A pin that forces a package up, past a security advisory say, is kept with a comment right before it that starts with `Transitive pin:` and gives the reason:

```xml
<!-- Transitive pin: GHSA-7jgj-8wvc-jh57, fixed in 4.3.4; System.Net.Http comes in through Foo. -->
<PackageVersion Include="System.Net.Http" Version="4.3.4" />
```

The comment covers only the pin right after it. Delete the pin with its comment once the package that brings it in moves past the version.

## Rolling it out

`Directory.Packages.props` belongs to the Application, so a Rollout can't clean it. Before the Rollout that brings the check, each Application with
transitive pinning deletes its unreferenced pins, or marks the ones it keeps, in a PR of its own; run
`python3 -I <Factory>/.claude/skills/repo-ci-baseline/template/owned/.github/scripts/check_central_pins.py --root .` in its checkout to list them.
