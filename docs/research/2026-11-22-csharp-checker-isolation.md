# Running a C# checker over an untrusted PR tree

Research note, 2026-11-22, for issue #95 (map #93). It decides nothing; an ADR does.
**[V]** marks a fact verified here by experiment, with SDK 11.0.100 (and 10.0.404 where shown) on Linux x64.
**[D]** marks a fact taken from the primary docs only, with the source cited. **[I]** is my inference.

## The question

`check_owned_files.py` (`owned-files.yml`) and `check_gh_repo.py` (`ci.yml`, job `gh-repo`) are read from the base commit with `git show` into a
`mktemp -d` folder outside the checkout and run as `python3 -I "$tmp/<checker>.py" --repo "$GITHUB_WORKSPACE"`. The cwd is the checkout (the PR's merge tree),
no secrets are present and the token is read-only. `-I` makes Python ignore `PYTHON*` variables and the user site folder, and leaves the script's folder and
the cwd off `sys.path`, so no file in the PR's tree changes which code runs. The PR's files are only data the checker reads.

What would a C# file-based app (`dotnet run file.cs`) or a prebuilt one read from the PR's tree, its cwd or the environment, and what layout makes it just as immune?

## Answer

C# has no single `-I` switch. It can be made just as immune by layout, and two traps must be avoided:

1. **`global.json` in the cwd picks the SDK, and since .NET 10 it can point at an SDK inside the PR's tree.** Every SDK command (`dotnet run`, `dotnet build`,
   `dotnet file.cs`) looks for `global.json` from the **cwd** upward. A PR that adds `global.json` with `"sdk": {"paths": [".dotnet"]}` and a fake
   `.dotnet/sdk/<v>/dotnet.dll` runs its own code. **[V]** (below). Today's layout runs the checker with cwd = the checkout, so a straight port
   (`dotnet run "$tmp/check.cs"` from the workspace) is **arbitrary code execution**.
2. **`dotnet run file.cs` without `--file` runs a project in the cwd instead,** if the cwd has a `.csproj`, and passes the file as an argument. **[V]** [D]

What makes it immune, from strongest to weakest:

- **Build in one place, run the dll with `dotnet <dll>` (or `dotnet exec <dll>`).** App mode does not read `global.json` and does not engage the SDK, MSBuild
  or NuGet. **[V]** [D] The host loads only the app's folder (its `runtimeconfig.json` and `deps.json`) and the shared framework next to the `dotnet` on `PATH`; a
  host trace with the cwd in a hostile tree shows no probe of the cwd. **[V]** With the build done with cwd and source both outside the checkout (or in a job that
  never checks the PR out), this is as immune as `python3 -I`, except for environment variables (below).
- **`dotnet run --file "$tmp/check.cs"` with `cd "$tmp"` first** (the cwd outside the checkout). Then `global.json` is looked for in `$tmp`'s ancestors, and
  `Directory.Build.props`/`.targets`, `Directory.Packages.props` and `NuGet.config` in the `.cs` file's folder and its ancestors. None is in the PR's tree. **[V]**
  It still runs MSBuild and maybe a restore at check time, so it carries more moving parts than the dll.
- Never put the `.cs` file inside `$GITHUB_WORKSPACE`. A file there picks up the PR's `Directory.Build.props`, `.targets`, `Directory.Packages.props` and
  `NuGet.config`, each of which can run code at build time. **[V]**

Environment variables are the one place C# is weaker than `-I`. MSBuild turns every environment variable into a property; an `Exec` from
`CustomBeforeMicrosoftCommonTargets=<file>` ran in a build. **[V]** [D] The host honours `DOTNET_STARTUP_HOOKS`, `DOTNET_ADDITIONAL_DEPS` and similar, even
in app mode. [D] A `pull_request` job's environment comes from the workflow, not from the PR's files, so this matches today's trust (the workflow is already
trusted to call `python3`); `env -i PATH=... HOME=... dotnet <dll>` removes it if wanted. **[V]** (the dll ran under `env -i`)

## Recommended shape for the ported checkers

```bash
tmp=$(mktemp -d)                                        # outside $GITHUB_WORKSPACE, as today
git show "${BASE_SHA}:.github/scripts/check_gh_repo.cs" > "$tmp/check_gh_repo.cs"
( cd "$tmp" && dotnet build check_gh_repo.cs -o "$tmp/out" --disable-build-servers )   # cwd and source outside the tree
dotnet "$tmp/out/check_gh_repo.dll" --repo "$GITHUB_WORKSPACE"                          # app mode: no global.json, MSBuild or NuGet
```

- Keep the checker one file that uses only the base class library: no `#:package` (a restore at check time is network and supply chain), no `#:include` or
  `#:project` (they resolve next to the file, so every file must be extracted from the base too). **[I]**
- The build must not run with cwd in the checkout, or the PR's `global.json` chooses the SDK. **[V]** If the base's own pin matters, copy the base's
  `global.json` into `$tmp` with `git show` as well; it is trusted. **[I]**
- An alternative with a cleaner trust line: build in a first job that checks out only `base.sha`, upload the dll as an artifact, and run it in a second job
  that checks out the PR. **[I]** It costs an artifact round trip and a second runner.
- Publishing to Native AOT (the file-based default for `dotnet publish`) gives a native binary that needs no `dotnet` host at all. [D] Not tried here.

## What each input does, and whether it was verified

| Input | Read from | Effect | Status |
| --- | --- | --- | --- |
| `global.json` `sdk.version` | cwd, then ancestors (SDK commands only) | picks the SDK; a cwd pin of 10.0.404 made `dotnet run` build on .NET 10.0.15 | **[V]** [D global-json] |
| `global.json` `sdk.paths` (.NET 10+) | relative to that `global.json` | runs an SDK from the PR's tree: code execution | **[V]** [D global-json] |
| `global.json` in app mode | not read | `dotnet app.dll` and `dotnet exec app.dll` ignored a hostile one; no probe in the host trace | **[V]** [D global-json] |
| `.csproj` in the cwd | cwd | `dotnet run file.cs` ran the PR's project; `--file` did not | **[V]** [D file-based-apps] |
| `Directory.Build.props` / `.targets` | the `.cs` file's folder and ancestors | targets run at build; not read from the cwd | **[V]** [D file-based-apps] |
| `Directory.Packages.props` | the `.cs` file's folder and ancestors | its targets ran for an in-tree file; not read from the cwd | **[V]** [D file-based-apps] |
| `NuGet.config` | the `.cs` file's folder and ancestors | an in-tree file restored from the PR's feed; the cwd's was ignored for `#:package` | **[V]** [D file-based-apps] |
| `Directory.Build.rsp` | project's ancestors, for `dotnet build` | not observed to apply to a file-based build in-tree; docs say `dotnet build` reads it | not reproduced; [D rsp] |
| `.editorconfig` | the source file's ancestors | changes diagnostic severities, not code | [D, inferred] |
| `[app].run.json`, `Properties/launchSettings.json` | next to the `.cs` file | sets the app's env; `--no-launch-profile` skips it | [D file-based-apps] |
| MSBuild env-as-properties | environment | `CustomBeforeMicrosoftCommonTargets` imported a targets file that ran | **[V]** [D msbuild-env] |
| `DOTNET_*` host and runtime variables | environment | startup hooks, extra deps, roll-forward, tracing; apply in app mode too | [D env-vars] |
| `DOTNET_MSBUILD_SDK_RESOLVER_*`, `NUGET_PACKAGES`, `DOTNET_CLI_HOME` | environment | redirect SDK targets, packages, CLI state | [D env-vars] |
| Build cache | `~/.local/share/dotnet/runfile/<name>-<hash>/` on this machine | keyed on the file path and content; the docs say `<temp>/dotnet/runfile/` | **[V]** path; [D] keying |
| Build servers (`VBCSCompiler`, MSBuild nodes) | per-user | a server started earlier in the job is reused; `--disable-build-servers` | **[V]** servers seen running; [D] flag |

The docs on build caching say the cache key includes "implicit build file existence and content", and also that "changes to implicit build files don't
trigger rebuilds" in some cases. [D file-based-apps] On a fresh hosted runner the cache is empty, so this matters only on a self-hosted runner. **[I]**

## The experiments

All ran in a fresh `mktemp -d` folder under `/tmp`, outside the repo: a fake PR tree `pr/` and a checker `checker/checker.cs` that printed
`RuntimeInformation.FrameworkDescription`, the cwd and `AppContext.BaseDirectory`. Each hostile file's target ran `touch` on a marker file, so a marker meant code ran.

1. cwd `pr/` with `global.json` pinning `10.0.404` (`rollForward: disable`), `dotnet run ../checker/checker.cs`: `dotnet --version` printed 10.0.404 and the
   checker ran on `.NET 10.0.15`. The cwd's `global.json` chose the SDK.
2. cwd `pr2/` with `global.json` `{"sdk": {"version": "99.0.100", "paths": [".dotnet", "$host$"]}}`, a console app built as `dotnet.dll` in
   `pr2/.dotnet/sdk/99.0.100/`, and `pr2/.dotnet/shared` a symlink to the real `shared/` (a symlink git can commit). `dotnet run ../checker/checker.cs`
   printed `EVIL SDK RAN: run .../checker.cs -- --repo x` and wrote the marker. Without the symlink it failed only because no runtime was next to it.
3. The same `pr2/` cwd, `dotnet ../out/checker.dll` and `dotnet exec ../out/checker.dll`: both ran the checker on .NET 11.0.0, no marker. With
   `DOTNET_HOST_TRACE=1`, app mode logged no `global.json` probe; `dotnet run --file` from `checker/` logged probes of `checker/global.json` up to `/global.json`.
4. cwd `pr/` with `evil.csproj` at its root: `dotnet run ../checker/checker.cs` warned that the file "appears to be a file-based app but was passed as an
   argument to the project" and ran the PR's project, whose `Directory.Build.props`, `.targets` and `Directory.Packages.props` targets all ran.
   `dotnet run --file ../checker/checker.cs --no-cache` ran the checker and no marker appeared.
5. The checker copied into `pr/.github/scripts/` and run with `--file`: the PR's props, targets and CPM targets ran, and restore used the PR's `NuGet.config`
   feed (`NU1900 ... evil.invalid`).
6. A checker with `#:package Humanizer.Core@2.14.1` and an empty `NUGET_PACKAGES`, outside the tree, cwd `pr/`: restored from nuget.org and ran; the cwd's
   `NuGet.config` was ignored. The same file inside `pr/sub/` failed to restore against the PR's feed.
7. `CustomBeforeMicrosoftCommonTargets=<hook.targets> dotnet build checker.cs` with nothing hostile on disk: the hook's `Exec` ran. An `AssemblyName`
   variable had no effect, because the virtual project sets it itself.
8. `env -i PATH=/usr/bin:/bin HOME=$HOME dotnet out/checker.dll` from `/tmp`: ran normally.

Timing on this machine with a warm compiler server: `dotnet run --file` of a changed file 0.3 s, a cached one 0.06 s, `dotnet <dll>` 0.02 s. A fresh CI runner
pays the first-run cost of the SDK; the first build here with SDK 10.0.404 took 4.5 s. **[V]** for these numbers only.

## Not covered

- Native AOT publish of a file-based app: docs only.
- `Directory.Build.rsp`: my test file may have been malformed, so "not observed" is weak; the safe reading is that it applies, which the layout above avoids anyway.
- The `pull_request` workflow file itself comes from the PR's merge ref, so the steps above are only as trusted as the workflow is today. That is the same for
  the Python checkers and is outside this question.

## Sources

- [D global-json] <https://learn.microsoft.com/en-us/dotnet/core/tools/global-json>: the muxer "starts from the current working directory"; `paths` "Available
  since: .NET 10 SDK"; it "does NOT affect scenarios such as ... running with `dotnet app.dll`, or running with `dotnet exec app.dll`".
- [D file-based-apps] <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>: "When a project file exists in the current working directory,
  `dotnet run file.cs` without the `--file` option runs that project"; "Implicit build files": `Directory.Build.props`, `.targets`, `Directory.Packages.props`,
  `nuget.config` and `global.json` "in the same directory or parent directories"; "Build caching"; launch profiles.
- [D env-vars] <https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables>: `DOTNET_STARTUP_HOOKS`, `DOTNET_ADDITIONAL_DEPS`,
  `DOTNET_SHARED_STORE`, `DOTNET_ROLL_FORWARD`, `DOTNET_MSBUILD_SDK_RESOLVER_*`, `NUGET_PACKAGES`, `DOTNET_CLI_HOME`, `DOTNET_HOST_TRACE`.
- [D msbuild-env] <https://learn.microsoft.com/en-us/visualstudio/msbuild/how-to-use-environment-variables-in-a-build>: "MSBuild treats environment variables
  as trusted inputs. They can cause MSBuild to load build logic such as tasks, `.targets`, or `.props` files from custom locations".
- [D rsp] <https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-response-files>: "MSBuild searches parent directories of the project for a file named
  Directory.Build.rsp"; processed by `dotnet build`; `-noAutoResponse` disables it.
