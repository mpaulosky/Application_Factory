# Central package pins

Template files (Owned): `.github/scripts/check_central_pins.py` and its tests, run by `scripts/gate.sh` right before the build and by `ci.yml`'s
`Build Solution` job right after the checkout, before Setup .NET. Its tests pin both places. mpaulosky/Application_Factory#105.

## Why

Under central package management with `CentralPackageTransitivePinningEnabled`, a `PackageVersion` in `Directory.Packages.props` pins its package
even where no project references it: wherever the package comes in through another one, it gets the pinned version. Dependabot's `nuget` updater
doesn't raise such a pin when it bumps the package that needs it, so the bump fails restore with NU1109 (a downgrade the pin forces).
IssueManager's all-nuget PRs (mpaulosky/IssueManager#340 and mpaulosky/IssueManager#351) both failed this way: Radzen.Blazor needed a newer `Microsoft.AspNetCore.Components.Web`, and
`xunit.v3.mtp-v2` newer `Microsoft.Testing.*` packages, than pins no project referenced. Each was raised by hand, and would have fallen behind again.

The pins were already in the `all-nuget` group (`"*"`), so grouping them can't help, and a version range trades the failure for builds that resolve
differently from one restore to the next. Deleting the pin lets NuGet resolve the version the parent package asks for.

## What the check refuses

With transitive pinning on (any MSBuild file in the repo sets the property to `true`, in any case), the check refuses a `PackageVersion` whose id
nothing references. A reference is an id in a `PackageReference` (`Include` or `Update`), `GlobalPackageReference` or `PackageDownload`, or an MSBuild SDK (a `Project`'s
`Sdk`, an `Sdk` element, an `Import`'s `Sdk`, or any `global.json`'s `msbuild-sdks`, which may hold comments and trailing commas as the .NET host allows),
in any MSBuild file: one whose extension ends in `proj` (`.csproj`, `.sqlproj`, `.wixproj` and the rest), or a `.props` or `.targets` file. Ids are
compared ignoring case, and a pin listed twice is reported once. With transitive pinning off, an unreferenced pin pins nothing and the check passes.

An id holding `$(Name)` is resolved from every value the repo's MSBuild files give that property, wherever they set it (a `Target`, a `Choose`). One
that still holds `$(`, `@(` or `%(`, that is over 256 characters (NuGet's own limit is 100), or that would need more than 256 combinations of property
values or grow past 1,024 characters with them substituted (as a property value that long makes it), can't be read without evaluating MSBuild: such a
pin is left alone, and such a reference
could name any package, so the pins found are printed as warnings and the check passes, naming the reference.

Files are read as bytes, so a byte-order mark or an XML declaration's encoding (UTF-16, say) is honoured. The check reads only a regular file, not a
symlink, of at most 4 MiB, holding well-formed XML without a `DOCTYPE` (which no MSBuild file needs, and entity expansion would), nested at most 256
elements deep, in an encoding Python decodes. What happens to a file it can't read depends on what the file could hide:

- An MSBuild file that may hold pins or turn transitive pinning on fails the check, naming the file and why: a `Directory.Packages.props`, one whose
  text names `PackageVersion` or `CentralPackageTransitivePinningEnabled`, or one whose bytes weren't read at all (a symlink, a FIFO, one over 4 MiB).
  Skipping it could hide a pin, or the setting that makes the check apply. So does a folder that can't be listed, or a symlinked folder, which isn't walked.
  Under transitive pinning, so does any MSBuild file that couldn't be parsed when a pin without a reason is built from a property (`$(Name)`), since
  the file may give that property a value, and so a pin, the check never sees.
- Any other MSBuild file (a malformed `dotnet new` template or test fixture, say), and a `global.json` that can't be read (a symlink, a FIFO, one over
  4 MiB, or not JSON the .NET host reads), is skipped with a notice. It can hide only references, which makes the check stricter, and the values of
  properties a reference is built from, which can make it unreadable and so turn the report into warnings.

A run with a file or folder it couldn't check never prints a line saying the pins are fine: it ends with `Not a pass: ...`, after any pins it
found. Only the file itself is opened without following a symlink: a folder swapped for one between the walk and the read is followed, which needs
a process writing to the tree while the check runs, not a change in a pull request.

`bin/`, `obj/`, `node_modules/` and dot-folders (`.git`, CI's `.nuget` package cache) aren't walked. The whole run reads at most 50,000 files, 64 MiB,
250,000 elements, attributes and comments, 250,000 ids and 100,000 combinations of property values; past any of them it fails, since it can't vouch for
what it didn't read. Every entry of an id list (`A;;B` is three) counts as an id, and so does every id a name resolves to and every SDK a
`global.json` names. Each kind of line is printed at most 100 times, then a count of the rest.

Under GitHub Actions each problem is an `::error`, `::warning` or `::notice` annotation on its file, escaped as the runner reads workflow commands;
elsewhere a plain line. A name or path from the repo is printed with its non-printable characters as `?`, so it can't start a workflow command or move
the terminal, and each is cut after 300 characters, with a count of the rest.

## Keeping a pin on purpose

A pin that forces a package up, past a security advisory say, or one for a package an SDK references itself (MSTest.Sdk's packages, say, which no
project names), is kept with a comment right before it that starts with `Transitive pin:` (in any case) and gives the reason:

```xml
<!-- Transitive pin: GHSA-7jgj-8wvc-jh57, fixed in 4.3.4; System.Net.Http comes in through Foo. -->
<PackageVersion Include="System.Net.Http" Version="4.3.4" />
```

The comment covers only the pin right after it: another comment or element between them takes the reason away. Delete the pin with its comment once
the package that brings it in moves past the version.

## Rolling it out

`Directory.Packages.props` belongs to the Application, so a Rollout can't clean it. Before the Rollout that brings the check, each Application with
transitive pinning deletes its unreferenced pins, or marks the ones it keeps, in a PR of its own; run
`python3 -I <Factory>/.claude/skills/repo-ci-baseline/template/owned/.github/scripts/check_central_pins.py --root .` in its checkout to list them.
An Application the Rollout reaches first gets a red `Build Solution` on its Rollout PR, and on every PR after it: the step's annotations name each pin to
delete or mark. The same command lists any MSBuild file or folder the check can't read (a symlinked `.props` or folder, a malformed file that names
`PackageVersion`); there is no comment that exempts one, so the Application fixes, moves or deletes it in the same PR.

The Seed `.sandcastle/lib/repo-config.mts` lists the script in `CHECK_PATHS`, so a Sandcastle branch that changes it says so in its PR body. A Seed file is
written only when missing, so an Application with Sandcastle adds `".github/scripts/check_central_pins.py"` to its own `CHECK_PATHS` by hand
([sandcastle.md](sandcastle.md), step 9).
