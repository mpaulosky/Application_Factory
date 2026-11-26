# An unreferenced central pin fails the gate and Build Solution

**Status:** accepted (mpaulosky/Application_Factory#109). Decided in mpaulosky/Application_Factory#105, implemented in mpaulosky/Application_Factory#110 and
mpaulosky/Application_Factory#114: the Owned `.github/scripts/check_central_pins.*` and its tests, run by the Owned `scripts/gate.sh` before the build and by
`ci.yml`'s `Build Solution` job right after the checkout ([central-pins.md](../../references/central-pins.md)).

## Context

Under central package management with `CentralPackageTransitivePinningEnabled`, a `PackageVersion` in `Directory.Packages.props` pins its package even
where no project references it: wherever the package comes in through another one, it gets the pinned version. Dependabot's `nuget` updater doesn't
raise such a pin when it bumps the package that needs it, so the bump fails restore with NU1109. Two all-nuget PRs in a row in IssueManager
(mpaulosky/IssueManager#340, mpaulosky/IssueManager#351) failed this way, and each pin was raised by hand.

`Directory.Packages.props` belongs to the Application, not the Template, so the Factory can't fix the pins itself. It can only refuse them, and the
check that does turns the required `Build Solution` job red.

## Considered options

- **Raise each pin by hand when a bump fails (rejected):** what the Fleet did. The pin falls behind again at the next bump, and every failure costs a
  person a restore, a diagnosis and a PR.
- **Group the pins with the packages that need them in Dependabot (rejected):** they were already in the `all-nuget` group (`"*"`); grouping doesn't make
  the updater raise a pin no project references.
- **Pin a version range (rejected):** trades the failure for builds that resolve differently from one restore to the next.
- **Turn transitive pinning off (rejected):** that is the Application's choice, and it takes away the pin that forces a package past an advisory.
- **Delete an unreferenced pin, and keep one on purpose only with a reason, enforced by a check (chosen):** with the pin gone, NuGet resolves the
  version the parent package asks for, and Dependabot's bump of the parent carries it.

## Decisions

- **Under transitive pinning, a `PackageVersion` whose id nothing in the repo references fails the check,** unless the comment right before it starts
  with `Transitive pin:` and gives the reason (a security advisory, a package an SDK references itself). A reference is a `PackageReference`,
  `GlobalPackageReference`, `PackageDownload` or MSBuild SDK, in any MSBuild file or `global.json`. With transitive pinning off, the check passes.
- **The check runs in two places in every Application:** the gate, before the build, and `Build Solution`, first, so a clone without hooks and a
  Dependabot PR are both checked. A PR that skips the build skips it too.
- **It reads the files, it doesn't evaluate MSBuild.** `$(Name)` is resolved from every value the repo's MSBuild files give the property, within fixed
  limits; past them, a value can't be read. Every read is bounded, so a PR can't make it run long, and a run past a bound fails.
- **It fails in two directions, on purpose:**
  - **Closed, where skipping could hide a pin or the setting:** a file it can't read that may hold pins or turn transitive pinning on, a folder it can't
    list, and a transitive setting it can't read (which counts as on) fail the check, or make it apply. Under such a setting an unreferenced pin
    either holds a package back or does nothing, so deleting it, or keeping it with a reason, is safe either way.
  - **Open, where a reference can't be read:** a reference it can't read could name any package, so the pins it found are printed as warnings and the
    check passes, naming the reference. Any other file it can't read is skipped with a notice; that can hide a reference, which makes the check
    stricter (a pin reported that a reference in that file uses), or the value a reference is built from, which makes the check pass with warnings.
- **There is no exemption for a file:** a file or folder the check refuses is fixed, moved or deleted in the Application's PR.

## Consequences

- A Dependabot bump no longer fails restore on a pin no project references; the pins that remain carry their reason in the file.
- The Template enforces a rule on a file the Application owns, so a Rollout can't make an Application pass: each Application with transitive pinning
  deletes or marks its pins in a PR of its own before the Rollout that brings the check, or its Rollout PR and every PR after it that builds is red.
  The cleanup PRs for the Fleet merged with mpaulosky/Application_Factory#114.
- A pin kept with a reason is never checked again: deleting it once the package that brings it in moves past the version is a person's job.
- A pin may be reported though a reference names it, when that reference is in a file the check skipped; the notice names the file. A pin may pass
  with a warning though nothing references it, when a reference can't be read; the warning names the reference.

## Update (mpaulosky/Application_Factory#126): the check is C#, and runs after Setup .NET

The check is `.github/scripts/check_central_pins.cs` now, a C# file-based app (the Factory's ADR 0017), with `check_central_pins.py` beside it for one Rollout.
The rule, the two places it runs and the two directions it fails in are unchanged; where `Build Solution` runs it is not.

- **`Build Solution` runs it after Setup .NET, not first.** As C# it needs the SDK, so the job's order is the checkout, Setup .NET, a step that builds the
  check, the check (`dotnet run --no-build --file`), then the cache, the restore and the build. It still runs before the restore, which would fail with
  NU1109 on the pin it reports, and still on the PR's own copy, which runs nothing the job doesn't build and run anyway: the job builds the PR. What it
  loses is the few seconds by which a bad pin failed before the SDK was installed.
- **The gate builds it, then runs it**, before the solution's build, as it does the test discovery.
