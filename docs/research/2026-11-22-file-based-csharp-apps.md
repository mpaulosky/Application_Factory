# File-based C# apps as the Factory's scripts

Research note, 2026-11-22, for #94 (part of map #93). Nothing here is a decision; an ADR decides.

Facts marked **[V]** I checked by running them on this machine: Linux x86_64, 24 cores, SDK 11.0.100 and SDK 10.0.404 installed by dotnetup under
`~/.local/share/dotnetup/dotnet`, every experiment in a fresh `/tmp` directory outside the repo. Timings are wall-clock (`bash time`), a few runs each, rounded.
Facts marked **[D]** come from documentation or source code only, with the source named. **[I]** marks my inference.

## Summary

- **[V]** Warm start is about **45 ms** (`dotnet run app.cs`, `dotnet app.cs` and `./app.cs` alike), against about 15 ms for `python3`. The build output's own apphost starts in 20 ms.
- **[V]** A cold build of a directive-free file takes about **1.0 s** without a running compiler server and **0.25 s** with one. After an edit or a `touch`, the SDK skips MSBuild and
  calls `csc` alone.
- **[V] The biggest surprise:** file-based apps default to `PublishAot=true`, and that has two effects on a plain `dotnet run`:
  - Reflection-based `System.Text.Json` (`JsonSerializer.Serialize(obj)`) **throws at run time**, and the process dies with exit code 134 (SIGABRT), not 1.
    `JsonNode`/`JsonDocument` work; `JsonSerializer` needs source generation or `#:property PublishAot=false`.
  - On a machine with an empty NuGet cache (a fresh runner), the first build **restores about 340 MB** of ILLink, ILCompiler and runtime packs (5 to 6 s here) and **fails offline**
    (`NU1301`). With `#:property PublishAot=false` the same first run takes 1.0 to 1.8 s and needs no network at all.
- **[V]** C# 15 needs SDK 11: SDK 11.0.100 compiles at language version 15.0, SDK 10.0.404 at 14.0.
- **[V]** `global.json` is found from the **current working directory**, not from the `.cs` file's folder, and that holds for a shebang run too. Under the Template's pin (`10.0.403`,
  `rollForward: latestMinor`) the SDK picked is the highest installed 10.0.x (10.0.404 here), never 11. So any C# script run from inside an Application builds with SDK 10 and C# 14
  until the Baseline moves to .NET 11 (#100).
- **[V]** `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props` and `NuGet.config` in the `.cs` file's folder or above it all apply, and a change to them causes
  a rebuild. Under central package management, `#:package X@1.2.3` is an error (`NU1008`); without it, `#:package X` with no version is an error (`NU1015`).
- **[V]** A `Directory.Build.targets` in the folder above the script runs arbitrary commands at build time. A script kept inside an untrusted PR tree executes that tree's code.
  Copied out of the tree it does not, but the cwd's `global.json` still picks the SDK.
- **[V]** Running one file-based app **concurrently while it needs a build** fails (2 or 3 of 8 parallel runs, "file ... being used by another process"). Warm parallel runs are fine.
  Build first (`dotnet build app.cs`) and then run with `--no-build`.
- **[V]** `#:include`, `#:project`, `#:package`, `#:sdk` and `#:property` all work on SDK 11. `dotnet project convert`, `dotnet format app.cs` and `dotnet clean file-based-apps` exist.
- **[V]** An xUnit v3 project can test a file-based app two ways: compile its logic file in with `<Compile Include="../../scripts/Logic.cs" />`, and run the app as a process.
- **[V]** `ubuntu-latest` (Ubuntu 24.04, image 20261116) already carries SDK 11.0.100, and also 10.0.404. `actions/setup-dotnet` v6.1.0 (the Template's pin) installs `11.0.x`.
  Its `cache` input caches only the NuGet folder and needs a `packages.lock.json`.

## Start time

All measured **[V]**, with this file (no directives, `System.Text.Json.Nodes`, prints a JSON line):

```csharp
#!/usr/bin/env dotnet
using System.Text.Json.Nodes;
var o = new JsonObject { ["lang"] = "C#", ["args"] = args.Length, ["ver"] = Environment.Version.ToString() };
Console.WriteLine(o.ToJsonString());
return args.Length > 0 && args[0] == "fail" ? 3 : 0;
```

| Case | Time |
| --- | --- |
| Fresh `HOME`, empty NuGet cache, default `PublishAot=true` (restores the AOT packs) | 5.3 to 6.4 s |
| Same, offline | fails after 17.6 s, `NU1301` |
| Fresh `HOME`, empty NuGet cache, `#:property PublishAot=false`, online or offline | 1.0 to 1.8 s |
| Cache folder removed, no compiler server running | 0.95 to 1.05 s |
| Cache folder removed, compiler server (`VBCSCompiler`) already running | 0.23 to 0.26 s |
| File edited or `touch`ed, server running | 0.22 to 0.24 s |
| File `touch`ed, no server | 1.03 s |
| A `Directory.Build.props` above it touched, or a `Directory.Build.targets` added | 0.83 to 0.85 s (MSBuild runs again) |
| `--no-cache` (forces MSBuild) | 1.06 s |
| Warm: `dotnet run app.cs`, `dotnet app.cs`, `./app.cs` (shebang) | 0.04 to 0.06 s |
| The cached apphost `bin/debug/<name>` run directly | 0.02 s |
| `python3 -c 'import json; ...'`, for comparison | 0.015 s |
| With `#:package Humanizer.Core@2.14.1`: first build online, then warm | 1.6 s, then 0.05 s |
| Same, NuGet cached, build cache removed, offline | 1.1 s |

The very first run after installing SDK 11 took 6.5 s (first-use setup plus the AOT restore). **[V]** With `DOTNET_CLI_CONTEXT_VERBOSE=true` the SDK says why it builds:
`Compiling because entry point file is modified`, `Skipping MSBuild and using CSC only`, `Compiler server processed compilation`, or
`Using MSBuild because NuGet package file does not exist: .../microsoft.net.illink.tasks/11.0.0/...` on a fresh cache.

**[I]** A gate that runs a C# script once pays about 1 s of compile per changed script on a cold machine and 45 ms after that. That is acceptable for the Factory's `status`, `rollout`
and `land` scripts. It is too slow for anything a git hook runs on every commit, which matches the map's choice to keep the hooks in bash.

## Where the build cache lives and what it keys on

- **[V]** On Linux the cache is `$XDG_DATA_HOME/dotnet/runfile/<name>-<sha256>/`, falling back to `~/.local/share/dotnet/runfile/`. The docs say "the system's temporary directory under
  `<temp>/dotnet/runfile/`", but that holds on Windows only.
  **[D]** `VirtualProjectBuilder.GetTempSubdirectory` in dotnet/sdk uses `Path.GetTempPath()` on Windows and `Environment.SpecialFolder.LocalApplicationData` elsewhere,
  "a location where permissions are expected to be restricted to the current user".
  The folder name is the file name plus `Sha256Hasher.HashWithNormalizedCasing(entryPointFileFullPath)`, so it is keyed on the **full path**. A moved or re-cloned file builds again.
- **[V]** Each folder holds `bin/debug/` (dll, apphost, runtimeconfig), `obj/`, `csc.rsp` (the compiler arguments reused for the csc-only path), `build-start.cache` and
  `build-success.cache`. The last is JSON with `SdkVersion`, `RuntimeVersion`, `Directives` and `ImplicitBuildFiles`, the list of `Directory.Build.*`/`global.json` files that were read.
- **[V]** The up-to-date check is by **timestamp**: `touch app.cs` with no change rebuilds (via csc only). **[I]** So restoring this folder from `actions/cache` after a fresh
  `actions/checkout`, which sets new mtimes, saves little; caching the NuGet folder is what pays.
- **[D]** dotnet/sdk `documentation/general/dotnet-run-file.md`: "If only `.cs` files change, it is enough to invoke `csc.exe`"; "If no inputs change, it is enough to start the target
  executable without invoking the build at all". The check "is not 100% precise": files imported *through* an implicit build file are not tracked.
  `--no-cache`, or `#:property FileBasedProgramCanSkipMSBuild=false`, opts out.
- **[D]** learn.microsoft.com `file-based-apps` (ms.date 2026-11-10) lists the cache inputs as source content, directives, SDK version, and implicit build file existence and content.
  It warns that "Moving files to different directories doesn't invalidate cache".
- **[D]** Cleanup: a background task started by `dotnet run` removes artifacts unused for 30 days, at most every 2 days. `DOTNET_CLI_DISABLE_FILE_BASED_APP_ARTIFACTS_AUTOMATIC_CLEANUP=true`
  turns it off. `dotnet clean file-based-apps [--days N]` cleans by hand. **[V]** The folder held a `dotnet-run-file-artifacts-metadata.json` with `LastAutomaticCleanupUtc`.
- **[V]** Concurrency: 8 parallel `dotnet run noaot.cs` right after an edit, 3 rounds: 2, 3 and 3 runs failed with `The process cannot access the file '.../bin/debug/noaot.dll'` (or
  `build-success.cache`) `because it is being used by another process`. 16 parallel warm runs all passed. **[D]** The docs give the same warning and the fix:
  `dotnet build file.cs`, then `dotnet run file.cs --no-build`. A parity suite that runs cases in parallel must build first.

## Directives

- **[D]** Learn lists `#:include`, `#:package`, `#:project`, `#:property` and `#:sdk`. The dotnet/sdk design doc also names `#:ref` and `#:exclude`, and says other `#:` kinds are errors.
- **[V]** `#:package Humanizer.Core@2.14.1` restores and runs. **[D]** `@*` takes the latest version, and a bare name works only under central package management.
- **[V]** `#:project ../lib/Lib.csproj` builds and references a class library.
- **[V]** `#:sdk Microsoft.NET.Sdk.Web` gives `WebApplication`.
- **[V]** `#:property PublishAot=false` works, and is needed for reflection JSON and for an offline first build (see Summary). **[D]** Property values can use MSBuild functions, for example
  `$([MSBuild]::ValueOrDefault('$(LOG_LEVEL)', 'Information'))`.
- **[V]** `#:include helper.cs` compiles a second file into the app (SDK 11; **[D]** also SDK 10.0.300 and later). An included file can't hold top-level statements, and a glob in
  `#:include` turns build caching off. **[V]** With an `#:include`, the entry point gets analyzer warning
  **CA2266** "File-based program entry point should start with '#!'". It goes once a shebang is the first line; under `TreatWarningsAsErrors` it would fail the build.
  **[I]** This is the way to share `landing.py`-style modules between scripts: `status.py` imports `landing` and `settings`, so a C# `status.cs` would `#:include` those files.
- **[V]** Offline once cached: with the package in `~/.nuget/packages`, a build with the network cut (`unshare -rn`) succeeds both after the build cache is removed and after an edit.

## SDK selection

- **[V]** `global.json` is resolved from the **cwd** (the `dotnet` muxer's usual rule). Same file, `global.json` (10.0.403, `latestMinor`) in `pinned/`:
  - `cd pinned && dotnet run ver.cs`: SDK 10.0.404, runtime 10.0.15.
  - `cd other && dotnet run ../pinned/ver.cs`, and `../pinned/ver.cs` via the shebang: SDK 11.0.100, runtime 11.0.0.
- **[V]** Under the pin, `latestMinor` rolls forward within major 10 only, never to 11. With only SDK 11 installed the pin would fail to resolve **[I]**; the `dotnet` muxer has always
  behaved that way.
- **[V]** `#error version` prints `Language version: 14.0` under SDK 10.0.404 (compiler 5.9.0) and `Language version: 15.0` under SDK 11.0.100 (compiler 5.12.0).
- **[I]** The Factory has no `global.json` at its root, so Factory-only scripts run from the Factory get SDK 11. `status.sh` is run from wherever the user stands, though, and from
  inside an Application's checkout it would pick SDK 10 and fail on any C# 15 syntax.
  Options: a wrapper that `cd`s to the Factory before `dotnet run` (and turns relative path arguments into absolute ones); a Factory `global.json` plus that `cd`; or C# 14 syntax only until #100.
- **[D]** setup-dotnet: with `global-json-file` the step's output is the `global.json` version, and `rollForward` is honoured.

## What it picks up from the folders above

- **[V]** `Directory.Build.props` two levels up applied (a `DefineConstants` from it was set). Its `TreatWarningsAsErrors=true` made an unused variable (CS0219) fail the build.
- **[V]** `Directory.Packages.props` turns on central package management. `#:package Humanizer.Core` (no version) restored from it, and `#:package Humanizer.Core@2.14.1` then failed with `NU1008`.
  Without central package management a bare name fails with `NU1015`. **[I]** Many Applications use central package management, so a script inside one must leave out versions, while a
  Factory script must give them. The same file can't serve both.
- **[V]** `NuGet.config` in the file's folder applies: a bogus `<clear/>` source made restore fail with `NU1301` on a fresh cache.
- **[V]** A `.csproj` in the cwd wins: `dotnet run a.cs` ran `x.csproj` (and failed, because `a.cs` was globbed into it), while `dotnet run --file a.cs`, `dotnet a.cs` and `./a.cs` ran the
  file. **[D]** Learn says the same, for backwards compatibility, and advises against putting file-based apps inside a project's folder tree.
- **[V]** Untrusted trees: a `Directory.Build.targets` with `<Exec Command="echo PWNED > ...">` ran when the script was inside that tree. With the script copied to a clean folder and
  run from inside the tree with `--file`, the target did not run, but the tree's `global.json` still chose SDK 10. **[I]** The C# equivalent of `cp` plus `python3 -I` is to copy the
  `.cs` files to a clean temp folder with no `Directory.Build.*`, `Directory.Packages.props` or `NuGet.config` above it, and run them from a cwd outside the tree.
  Better still, build them before the untrusted tree is checked out. I did not test whether the cwd's `NuGet.config` matters when a restore is needed; the package was already cached.

## CI

- **[V]** runner-images `images/ubuntu/Ubuntu2404-Readme.md` (image 20261116.376.1): ".NET Core SDK: ... 10.0.404, 11.0.100". `ubuntu-latest` is Ubuntu 24.04 per the runner-images README.
  **[I]** So SDK 11 is on the runner without setup-dotnet; pinning it with setup-dotnet keeps a job stable when the image changes.
- **[V]** The Template pins `actions/setup-dotnet@0f4a354...` (v6.1.0, released 2026-11-03), the latest release.
- **[D]** setup-dotnet README: `dotnet-version` takes `11.0.x`, `11.0`, `11.0.1xx` (feature band) or an exact version. `global-json-file` reads a `global.json` and installs both when
  `dotnet-version` is also set. On Ubuntu it installs to `/usr/share/dotnet` (or `DOTNET_INSTALL_DIR`).
- **[D]** setup-dotnet `cache: true` caches only the NuGet global-packages folder and keys on `packages.lock.json`; "If lock file does not exist, this action throws error". A
  file-based app has no lock file. **[I]** Use `actions/cache` on `~/.nuget/packages` with a key that hashes the `.cs` files' `#:package` lines, or avoid packages and set
  `PublishAot=false` so nothing is restored at all. That is cheaper than any cache.
- **[I]** Caching `~/.local/share/dotnet/runfile` saves little (see the timestamp check). The cost on a fresh runner is the 1 s compile per script, plus the AOT restore unless
  `PublishAot=false`.

## Converting to a project, and testing from xUnit

- **[V]** `dotnet project convert inc.cs -o conv` wrote `conv/inc.csproj`, `conv/inc.cs` (directives stripped) and the `#:include`d `helper.cs`, and the result ran with `dotnet run`.
  The generated project sets `PublishAot=true`, `PackAsTool=true` and a `UserSecretsId`, making those hidden defaults visible. Options: `--dry-run`, `--force`, `--delete-source`.
- **[V]** `dotnet build app.cs` writes to the cache folder. `dotnet format whitespace --verify-no-changes app.cs` accepts a single file and exits 2 on a violation. **[D]** Learn:
  "`dotnet format` also accepts a file-based app". **[I]** That is a candidate for the edit-time and gate lint that replaces ruff.
- **[V]** An xUnit v3 test project (`xunit.v3.mtp-v2` 3.2.2, Microsoft Testing Platform, `net11.0`) passed 2 tests, `dotnet test` in 2.9 s:
  - white-box: `<Compile Include="../../scripts/Logic.cs" Link="Logic.cs" />` compiles the script's logic file (the one the entry point `#:include`s) into the test assembly;
  - black-box: `Process.Start("dotnet", ["run", "--file", tool, "--", "2", "3"])`, asserting stdout and exit code. That is the parity-suite shape #93 describes.
  - The test file needed `using Xunit;` explicitly.
- **[D]** I found no Microsoft page on testing a file-based app from xUnit. A search of learn.microsoft.com and dotnet/docs turned up only the general xUnit tutorials, which reference a
  class library. **[I]** The pattern above (thin entry point, logic in an `#:include`d file shared with the test project) is the one I'd recommend.

## Sources

- <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps> (ms.date 2026-11-10; dotnet/docs `docs/core/sdk/file-based-apps.md` at `ffe823c`)
- <https://github.com/dotnet/sdk/blob/main/documentation/general/dotnet-run-file.md>
- <https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.FileBasedPrograms/VirtualProjectBuilder.cs> (`GetTempSubdirectory`, `GetArtifactsPath`)
- <https://github.com/actions/setup-dotnet/blob/main/README.md>, releases (v6.1.0, 2026-11-03)
- <https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md> and the runner-images README
- Local: `dotnet --list-sdks`, `dotnet run --help`, `dotnet project convert --help`, `dotnet clean file-based-apps --help`, `DOTNET_CLI_CONTEXT_VERBOSE=true` output
