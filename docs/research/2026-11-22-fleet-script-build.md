# Building an Owned file-based script in every Fleet Application

Research note, 2026-11-22, for #101 (map #93). It decides nothing; an ADR does. It builds on the file-based apps note (#94) and the checker isolation note (#95),
on the branches `research/file-based-csharp` and `research/csharp-checker-isolation`, and does not repeat them.

**[V]** marks a fact verified here by experiment: Linux x64, SDK 11.0.100 and 10.0.404 (dotnetup), in detached worktrees of each Application's `origin/main` under `/tmp`
(so the `nuget.config` in `~/github`, above the checkouts, was not an ancestor). **[D]** marks a fact from source or documentation only, with the source named. **[I]** is inference.

## Answer

- **[V] Unshielded, the same script builds differently in each Application.** Of the seven Fleet Applications, three built and ran it as written, three failed, and Articles built it
  with warnings, on .NET 10, and wrote `bin/` and `obj/` into `.github/scripts/`.
- **[V] Three Owned files in `.github/scripts/` shield it completely:** `Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props`, each an empty `<Project>`.
  With them the script built with no warning in all seven and printed byte-identical output (same SHA-256), through `dotnet run --file` and through the shebang alike.
  Each file is needed for a different reason (below); none needs any content, and none may chain to the root with `GetPathOfFileAbove`.
- **[V] The shield doesn't touch the Application's own build.** Every project in every `.slnx` (71 in all) still imports the root `Directory.Build.props` and
  `Directory.Packages.props`, none has a `Compile` item under `.github/`, and Blazor-Server's and SuggestionApp's solutions built clean with the shield and the probe in place.
- **[V] The shield doesn't pick the SDK.** `global.json` comes from the cwd (#94). Under IssueManager's 10.0.403 pin the shielded script failed on C# 15 syntax, so the shield works
  only once every Application is on 11.0.100. On `origin/main` today **two** Applications still pin 10.0.403: IssueManager and auth0-management-app. The Template's
  Owned `global.json` pins 10.0.403 too (#103 fixes that).
- **[V] `#:property` lines are a weaker shield.** They override what the root `Directory.Build.props` sets, but not what a root `Directory.Build.targets` sets, nor properties
  read before the file's body (`BaseIntermediateOutputPath`, `ImportDirectoryBuildProps`). They also cover only the properties you thought to list.
- **[V] With the shield, a versioned `#:package` works in every Application** (no NU1008), and a bare one fails with NU1015 in every Application. The same file serves them all,
  which removes the reason #94 gave for avoiding `#:package` in the Template (it still holds for the checkers that run over an untrusted PR tree, #95).

## What each Application's files do to an unshielded script

The probe, `.github/scripts/probe.cs`, run from the repository root as `dotnet run --file .github/scripts/probe.cs`:

```csharp
#!/usr/bin/env dotnet
#:property PublishAot=false
#:include probe_lib.cs
using System.Text.Json.Nodes;

List<string> argList = [with(capacity: args.Length + 1), .. args, "end"];        // C# 15: collection expression arguments
var input = JsonNode.Parse("""{"count":"41","name":"Probe"}""")!;
var count = int.Parse(input["count"]!.GetValue<string>());                        // CA1305 under AnalysisMode All
var report = new JsonObject
{
    ["tfm"] = AppContext.TargetFrameworkName,
    ["runtime"] = Environment.Version.ToString(),
    ["sum"] = ProbeLib.Sum([1, 2, 3]),                                             // from the #:include'd probe_lib.cs
    ["next"] = count + 1,
    ["name"] = input["name"]!.GetValue<string>().ToLower(),                       // CA1304, CA1311
    ["args"] = new JsonArray([.. argList.Select(a => (JsonNode?)JsonValue.Create(a))]),
};
Console.WriteLine(report.ToJsonString());
return args.Contains("fail") ? 3 : 0;
```

`probe_lib.cs` holds `internal static class ProbeLib { public static int Sum(IEnumerable<int> values) => values.Sum(); }`. Outside any repository it printed
`{"tfm":".NETCoreApp,Version=v11.0","runtime":"11.0.0",...}` and exited 0 (3 with `fail`). **[V]** Union types (`public union Pet(Cat, Dog);`) also compile on SDK 11.0.100. **[V]**

Evaluated properties (`dotnet build <file> -getProperty:...`) and results, **[V]** for every row:

| Application (`origin/main`) | SDK picked | Root files | LangVersion | TFM | TreatWarningsAsErrors, AnalysisMode | Result |
| --- | --- | --- | --- | --- | --- | --- |
| TicketManager | 11.0.100 | `Directory.Packages.props` | 15.0 | net11.0 | false, default | builds, runs on 11.0.0 |
| IssueTracker | 11.0.100 | `Directory.Packages.props` | 15.0 | net11.0 | false, default | builds, runs on 11.0.0 |
| SuggestionApp | 11.0.100 | none | 15.0 | net11.0 | false, default | builds, runs on 11.0.0 |
| Blazor-Server | 11.0.100 | props, CPM | 14.0 | net10.0 | true, All | fails: CS9327 (C# 14), CA1304, CA1305, CA1311 as errors |
| Articles | 11.0.100 | props, CPM | latest (15) | net10.0 | true, all, but `CodeAnalysisTreatWarningsAsErrors=false` | builds with 3 CA warnings, runs on **10.0.15**, writes `.github/scripts/bin/`, `obj/` |
| IssueManager, own pin 10.0.403 | 10.0.404 | props, CPM | latest (14) | net10.0 | true, none | fails: CS8652 (preview feature in C# 14) |
| IssueManager, pinned to 11.0.100 | 11.0.100 | props, CPM | latest (15) | net10.0 | true, none | builds, runs on **10.0.15** |
| auth0-management-app, own pin 10.0.403 | 10.0.404 | props, CPM | 14.0 | net10.0 | true, All | fails: CS8652, CA1304, CA1305, CA1311 |
| auth0-management-app, pinned to 11.0.100 | 11.0.100 | props, CPM | 14.0 | net10.0 | true, All | fails: CS9327, CA1304, CA1305, CA1311 |

Why, from the SDK source **[D]** (`src/Cli/Microsoft.DotNet.FileBasedPrograms/VirtualProjectBuilder.cs`, dotnet/sdk `release/11.0.1xx`), confirmed by the rows above **[V]**:

- The virtual project writes its defaults (`OutputType`, `TargetFramework=net11.0`, `ImplicitUsings`, `Nullable`, `PublishAot=true`, `PackAsTool`) **before** importing `Sdk.props`:
  "Write default properties before importing SDKs so they can be overridden by SDKs (and implicit build files which are imported by the default .NET SDK)". So the root
  `Directory.Build.props`'s `<TargetFramework>net10.0</TargetFramework>` wins, and with it the C# default for net10.0 (14) unless `LangVersion` is set.
- `#:property` values are written **after** `Sdk.props` (so after `Directory.Build.props` and `Directory.Packages.props`) and before `Sdk.targets` (so before `Directory.Build.targets`).
- Articles sets `BaseIntermediateOutputPath` and `BaseOutputPath` to `$(MSBuildProjectDirectory)/obj/` and `/bin/`; for a file-based app `MSBuildProjectDirectory` is the `.cs` file's
  folder, so the build output lands in `.github/scripts/`. Articles' `.gitignore` ignores both, so the tree stays clean, but the runfile cache isn't used. **[V]**
- An unused C# 15 feature under SDK 10 is CS8652 ("currently in Preview"); under SDK 11 with C# 14 it is CS9327. **[V]**

The `.editorconfig` is the same file in all seven (it is Owned; the Fleet's copies predate the shfmt section). Its only `:warning` rules are code-style rules
(`csharp_prefer_static_local_function`, `dotnet_style_readonly_field`), which a build enforces only with `EnforceCodeStyleInBuild`. **[V]** (the files and their hashes)
**[I]** So it varies by Template version, not by Application; unshielded, Blazor-Server and auth0-management-app would also fail a script on those two rules.

## The shield, file by file

All **[V]**, each in the copies named:

- **`Directory.Build.props` alone** (Blazor-Server, Articles, TicketManager): LangVersion 15.0, net11.0, no TreatWarningsAsErrors or AnalysisMode, output in the runfile cache. The probe
  built and ran. CPM stayed on, from the root `Directory.Packages.props`: a versioned `#:package` failed with NU1008 (and in Articles, CPM is also switched on by the root props).
- **`Directory.Packages.props`** (Articles, TicketManager, SuggestionApp): with all three files, `#:package Humanizer.Core@2.14.1` built in all three; a bare `#:package Humanizer.Core`
  failed with NU1015 in all three. Without the shield, versioned failed with NU1008 and bare with NU1010 (not in the central list) under CPM, and SuggestionApp (no CPM) took the
  versioned one only. An empty file is enough: CPM is on only when `ManagePackageVersionsCentrally` is true, and the root props that set it is no longer imported.
- **`Directory.Build.targets`**: no Fleet Application has a root one today. With a root one added to the Blazor-Server copy setting `LangVersion 14.0` and `TreatWarningsAsErrors`,
  the probe failed with CS9327 under the props and packages shield and under the `#:property` shield, and built under all three files. It guards against a file the Fleet may add later.
- **No chaining.** MSBuild imports only the nearest `Directory.Build.props`; a shield that adds
  `<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />` brought back everything from the root (C# 14, net10.0,
  AnalysisMode All, the four errors). The shield must stay empty, or set only the Template's own values.
- **The Application's build:** with the three files and the probe in `.github/scripts/`, every project listed by `dotnet sln <slnx> list` in all seven copies still had
  `DirectoryBuildPropsPath` and `DirectoryPackagesPropsPath` at the root (or empty where there is none), and none had a `Compile` item under `.github/`. No Application has a
  `.csproj` at its root or under `.github/`, which is what would glob the scripts in. `dotnet build` of Blazor-Server's (10 projects) and SuggestionApp's solutions passed.

## Simpler shields, and why they fall short

- **`#:property` lines** (Blazor-Server, Articles, IssueManager pinned to 11, TicketManager): `TargetFramework=net11.0`, `LangVersion=15.0`, `TreatWarningsAsErrors=false`,
  `AnalysisMode=Default`, `EnforceCodeStyleInBuild=false` and `ManagePackageVersionsCentrally=false` all took effect, and the probe built with a versioned `#:package`. **[V]** But:
  - a root `Directory.Build.targets` overrides them (above); **[V]**
  - Articles still wrote `bin/` and `obj/` into `.github/scripts/`, because `BaseIntermediateOutputPath` is read before the file's properties; **[V]**
  - every script must repeat the list, and anything not on it leaks through (`WarningsAsErrors`, `NoWarn`, `DefineConstants`, an `ItemGroup` with a `PackageReference` or
    `GlobalPackageReference` analyzer). **[I]**
- **`#:property ImportDirectoryBuildProps=false`** (and `ImportDirectoryPackagesProps`, `ImportDirectoryBuildTargets`): no effect; the root props, CPM and all four errors stayed. **[V]**
  They are read in `Sdk.props`, before the file's properties are written. **[D]** (VirtualProjectBuilder.cs, above)
- **The same as global properties works:** `dotnet build <file> -p:ImportDirectoryBuildProps=false -p:ImportDirectoryPackagesProps=false`, or `ImportDirectoryBuildProps=false` in the
  environment (MSBuild reads environment variables as properties), shielded it. **[V]** But every caller must pass them, including every workflow step, every bash caller and every
  shebang run, which cannot pass `-p`. **[I]**
- **A `global.json` in `.github/scripts/`** chose SDK 11.0.100 under IssueManager's 10.0.403 pin, but only with the cwd in `.github/scripts/`. **[V]** Callers run from the root. **[I]**

## Not covered, or docs only

- **`NuGet.config`** is not shielded by the three files: NuGet merges every `NuGet.config` from the file's folder up, plus the user's. **[D]** (#95's note; learn.microsoft.com
  "Common NuGet configurations".) Every Fleet `NuGet.config` lists only nuget.org with a `*` source mapping, so a `#:package` restores from the same feed everywhere. **[V]** (the files)
  An Owned `.github/scripts/NuGet.config` with `<clear/>` would make that explicit; not tested.
- **Running on .NET 10:** Articles and IssueManager ran the unshielded probe on runtime 10.0.15 because the root props set net10.0. A machine with only the .NET 11 runtime would fail
  to run it. **[I]** Not tested; the shield makes it net11.0 anyway.
- **A full solution build** was run for two of the seven; the other five were checked by evaluation only.
- **Dependabot's NuGet updater** may find the empty `.github/scripts/Directory.Packages.props`. **[I]** Not checked; with no `PackageVersion` items there is nothing to update.

## Sources

- dotnet/sdk `src/Cli/Microsoft.DotNet.FileBasedPrograms/VirtualProjectBuilder.cs` at `release/11.0.1xx`: `GetDefaultProperties`, and the comments "Write default properties before importing
  SDKs so they can be overridden by SDKs (and implicit build files ...)" and "Write custom properties".
- <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>: implicit build files.
- <https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory>: MSBuild imports the first `Directory.Build.props` found walking up, and `GetPathOfFileAbove` to chain.
- <https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management>: CPM is on when `ManagePackageVersionsCentrally` is true; NU1008, NU1010, NU1015.
- Local: each Application's `origin/main` (TicketManager `6cb89ac`, IssueTracker `98a7fef`, Blazor-Server `335bf85`, IssueManager `0f83914`, Articles `bc1dca0`,
  SuggestionApp `6b926af`, auth0-management-app `b67223a`); `dotnet build -getProperty`/`-getItem`; `dotnet sln list`.
