---
status: accepted
---
# The Factory's and the Template's scripts are C# file-based apps

The Factory and the Template hold 21 Python scripts:

- **Factory-only:** six in the skill's `github/` folder (`status.py`, `landing.py`, `settings.py`, `land.py`, `rollout.py`, `auto_rollout.py`) and the bench, in `bench/`.
- **Owned, in `.github/scripts/`:** eleven, among them `check_owned_files.py` and `check_gh_repo.py`, the two that run over an untrusted PR tree, and `check_central_pins.py`.
- **Owned, in `.claude/skills/hardening-review/scripts/`:** two.
- **The Factory's copy of an Owned script:** `post_claude_review.py`.

Every Application is a .NET solution. The Python brings a toolchain of its own into every Application's CI and Seed image (`setup-python`, pytest, a hashed `requirements-tests.txt`
and ruff) to run scripts that a reader of a .NET Application otherwise has no reason to read in another language. This ADR moves the scripts to .NET 11 / C# 15 file-based apps
(`dotnet run --file script.cs`), in increments. It was worked out on the map mpaulosky/Application_Factory#93, and each decision below names the ticket that holds its detail. The
evidence is in four research notes:

- [`2026-11-22-file-based-csharp-apps.md`](../research/2026-11-22-file-based-csharp-apps.md): how a file-based app starts, builds, restores and finds its SDK
  (mpaulosky/Application_Factory#94).
- [`2026-11-22-csharp-checker-isolation.md`](../research/2026-11-22-csharp-checker-isolation.md): running a C# checker over an untrusted PR tree as safely as `python3 -I`
  (mpaulosky/Application_Factory#95).
- [`2026-11-22-fleet-script-build.md`](../research/2026-11-22-fleet-script-build.md): building one Owned script the same way in all seven Fleet Applications
  (mpaulosky/Application_Factory#101).
- [`2026-11-22-status-cs-trial.md`](../research/2026-11-22-status-cs-trial.md): `status.py` and all of `landing.py` ported, and judged on speed, parity, CI and size
  (mpaulosky/Application_Factory#97).

## Decision: port the scripts, one increment at a time, until the Baseline has no Python

The trial (mpaulosky/Application_Factory#97) met every threshold set before it ran:

- a warm start at most 72 ms behind `status.py` (the limit was 100);
- a cold first build of 1.2 s with an empty NuGet cache and no network (the limit was 5 s);
- 19 of 19 parity cases against the port;
- the CI job green on SDK 11.0.100;
- 0.72 times the Python's code lines (the limit was 1.5).

So the scripts move, on these rules:

- **The SDK is 11.0.100 everywhere** (mpaulosky/Application_Factory#100).
  - The Template's Owned `global.json` pins `11.0.100` with `rollForward: latestPatch`, and it joins `OWNED`. The Seed Dockerfile installs the SDK band the pin names (see the Update for mpaulosky/Application_Factory#103).
  - The Factory owns the bump: its Dependabot gains a `dotnet-sdk` entry that lists both the Factory's root and the Template's `template/owned/`, with a group over both
    folders, as its `github-actions` entry has. So one PR bumps the Template's `global.json` and the Factory's copy together, and the gate's drift check holds. An update the
    group leaves out arrives as one PR per folder, and the Template's is merged after `scripts/gate.sh --sync` brings the copy with it.
  - The Seed `dependabot.yml`'s `dotnet-sdk` entry leaves the Template, and since a Seed file is written once, each existing Application removes its own when its Rollout note
    says so.
  - Target frameworks stay `net10.0`; only the SDK moves.
  - The Factory gets its own `global.json`, a copy of the Template's that `scripts/gate.sh` lists with its other copies and `--sync` keeps equal, in the first increment that adds
    C# to it.
- **One script is one file-based `.cs`** (mpaulosky/Application_Factory#98).
  - The entry file keeps the script's name and sits beside its `.py` (`github/status.cs`, `.github/scripts/check_gh_repo.cs`), so a caller changes only the extension.
  - It is thin: its `#:` lines, then one call into a class (`return Script.Run(() => Status.Run(args, ...))`).
  - The logic lives in classes in files it pulls in with `#:include`, named in PascalCase in the same folder (`Landing.cs`, `StatusCore.cs`; not `Status.cs`, which a filesystem that
    ignores case can't hold beside `status.cs`).
  - Code is shared only within a folder. Each folder has one `Gh.cs` with the `gh` runner (`Graphql`, `Rest`, `NotFound`), the `JsonNode` helpers, the argument parser and
    `Script.Run`, which turns an uncaught exception into exit 1 with a message rather than .NET's abort (exit 134).
  - There is no `#:project` library and no `tools` project.
- **The two untrusted-tree checkers are single-file and BCL-only** (mpaulosky/Application_Factory#95, mpaulosky/Application_Factory#98).
  - They include nothing, and carry their own copy of what they need from `Gh.cs`.
  - They are built with the cwd and the `.cs` both in a `mktemp -d` folder, never in the PR's tree, and run as `dotnet <dll>`, which reads no `global.json` and runs no MSBuild.
  - For them, no SDK command runs with its cwd in the PR's tree. Every `dotnet run`, theirs or any script's, names its file with `--file`.
- **BCL only** (mpaulosky/Application_Factory#98). A `#:package` needs nuget.org on its first restore, which a cold start offline can't do, and the checkers can't take one at all.
  A script that needs a package needs its own ADR first.
- **Every script folder is shielded** (mpaulosky/Application_Factory#101, mpaulosky/Application_Factory#98).
  - Three empty files sit beside the scripts: `Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props`, each `<Project />`, never chaining to the root.
  - Unshielded, the same script built three ways across the Fleet. Shielded, it built byte-identical in all seven, and no project of an Application's own changed.
  - The shields in `.github/scripts/` and in `hardening-review/scripts/` are Owned, and join `OWNED` and the Owned-files check. Those in the Factory-only `github/` and `bench/` are not.
- **Every entry file carries `#:property PublishAot=false`, and no shebang** (mpaulosky/Application_Factory#94, mpaulosky/Application_Factory#97).
  - A file-based app defaults to `PublishAot=true`, whose restore fetches the ILCompiler and runtime packs (about 340 MB) and fails offline with NU1301.
  - The property is in the file, not the shield, so a checker copied alone into a temp folder keeps it.
  - Under the Template's `.editorconfig`, `dotnet format whitespace` rewrites `#!` as `# !`, which no longer builds (CS9378). Callers run `dotnet run --file`, so nothing needs
    the shebang.
  - Without one, an entry file that uses `#:include` gets analyzer warning CA2266 ("File-based program entry point should start with '#!'"). This ADR doesn't suppress it,
    so a build that treats warnings as errors fails on such a file.
- **How a script runs** (mpaulosky/Application_Factory#94, mpaulosky/Application_Factory#98).
  - A wrapper or a workflow step runs `dotnet run --file <path> -- <args>`.
  - A wrapper first changes to the script's own folder, so the `global.json` read is the one beside the scripts and not one in the caller's folder, and it passes paths as absolute
    paths. A workflow step runs from the Application's root, whose Owned `global.json` pins 11.0.100.
  - A job, gate part or test that runs a script more than once builds it first (`dotnet build <path>.cs`) and runs it with `--no-build`. A script run in parallel while it needs a
    build fails, and a fresh runner's first build took 12.3 s.
  - The cold build of about 1 s in each new worktree is accepted; there is no shared build cache.
- **A script ports with everything that imports it** (mpaulosky/Application_Factory#98). Python can't import C#, so while a script's importer is still Python, the imported `.py`
  stays beside its `.cs`, kept in step by that script's parity suite. `status`, `landing` and `land` move as one increment; `settings.py` stays until `rollout.py` is ported;
  `release_post` moves with `backfill_blog_posts` and `release_queue`.
- **Each port is judged by a parity suite** (mpaulosky/Application_Factory#96). It is a black-box suite at the command line, with a fake `gh` on `PATH`, that asserts stdout, the
  exit code and the `gh` calls, and runs against the Python and the C# alike (then `tests/status-parity.test.sh <command...>`; one runner now serves every suite,
  `tests/parity.test.sh <suite> <command...>`, mpaulosky/Application_Factory#118). It needed no change to judge the trial's port.
- **When each `.py` goes.**
  - A Factory-only script's `.py` is deleted in the PR that ports it, once its parity suite passes against the C#; a revert rolls it back.
  - An Owned script ships as C#, the file the workflows run, keeps its `.py` beside it for one Rollout, and loses it in the next; changing one workflow line rolls it back.
  - Each increment's hand-off says which applies.
- **The gate's cache key** gains `dotnet --version` and the SDK `global.json` names, so an SDK change runs `test.sh` again. This lands with the first increment that adds C# to the gate.
- **The Factory carries the .NET SDK.** `CLAUDE.md` says the Factory adds no more of the Baseline without an ADR; this is that ADR for the SDK, its `global.json` copy and the C# the
  ported scripts need. `CLAUDE.md` changes when the first increment brings them.

The increments, in order, are the map's last ticket. Factory-only scripts go first, with no Rollout. Then come the Template's scripts, with the Factory's copies of Owned files moving
with them through `gate.sh --sync`. Last come the Python heredocs in `claude-review.yml` and the `python3` calls inside the bash. The bash itself, the git hooks and the Sandcastle
TypeScript stay as they are.

## Still open

Each of these is decided in its increment, and recorded as an Update below:

- What replaces ruff in the edit-time lint, the gate and CI. `dotnet format whitespace --verify-no-changes` takes 1 to 2 s a run (mpaulosky/Application_Factory#97), too slow
  for a hook that runs on every edit, and the replacement must run no code from the tree.
- The white-box tests in xUnit v3: when each pytest suite is replaced, where the test projects live (both decided for the skill's `github/` scripts in the
  Update for mpaulosky/Application_Factory#119; for the Template's own scripts in the Update for mpaulosky/Application_Factory#125), and which package
  reads YAML for the tests that check workflows, since the BCL has none (YamlDotNet for the bench and `Scripts.Tests`, Update for mpaulosky/Application_Factory#161;
  a test the Template carries is still open).
  The Factory's Python test helpers move with them: `tests/check-issue-refs.py` and `tests/check-git-status.py` (which the gate and `test.sh` run; both C#
  now, Update for mpaulosky/Application_Factory#123),
  `tests/mutations/check-spec.py` and `tests/render_oracle/` (which stays a pytest suite over cmark-gfm and kramdown, and judges the C# too: Update for
  mpaulosky/Application_Factory#164; the C# alone since the Update for mpaulosky/Application_Factory#128).
- The checkers' cut-over: how the workflow steps build them in a temp folder with the base's `global.json` (the post job's steps, Update for
  mpaulosky/Application_Factory#129, are one way), where the `OWNED` list lives once `check_owned_files` can't import
  `check_gh_repo`, which checker judges the PR that replaces the checker, and for how many Applies both names are honoured.
- `check_central_pins`. CI runs the PR's own copy, from the PR's checkout, in the job that then builds the PR. So it runs nothing that job doesn't already run, and needs no
  checker layout. But as C# it needs the SDK set up before it, where today it runs first, on the runner's `python3`, so a bad pin fails fast. (Decided in the Update for
  mpaulosky/Application_Factory#126: it runs after Setup .NET.)
- The Python heredocs in `claude-review.yml`, and the `python3` calls inside the bash (`lint-edited-file.sh`, the test scripts, the Seed Dockerfile), likely `jq` where a hook can't
  pay a `dotnet` start.
- Mutation testing: `mutate.py` and its specs, which mutate bash too.
- Retiring Python from the Baseline: which files retire when, now that Apply can remove a retired Owned file (Update: retired Owned files, below).

## Considered Options

- **Stay on Python.** It works, and it starts faster: the port ran about 15 ms behind on a two-call run and about 70 ms behind on a five-call one, from first-use JIT along each
  new code path (mpaulosky/Application_Factory#97). Rejected: the Python is a second toolchain in every Application, with its own CI job, pins and lint, and the trial showed the C#
  is shorter.
- **A `tools` project with a subcommand per script.** One build, and a dll that starts about 35 ms faster. Rejected: it is a project in every Application, which the Application's
  own `Directory.Build.*` and central package management reach, and the two checkers can't be part of it.
- **File-based scripts sharing a library through `#:project`.** Rejected: the library is a real `.csproj` that needs its own shielding, and every script's first run builds it too.
  `#:include` within a folder shares the code with neither cost.
- **`System.CommandLine` (or another package) for arguments.** Rejected for now (BCL only, above): the scripts take positional arguments and a few flags, which a small parser in
  `Gh.cs` covers, `argparse`'s exit 2 for an unknown option included.
- **`PublishAot=false` in the shield's `Directory.Build.props`.** Rejected: the shields stay empty (mpaulosky/Application_Factory#101), and a checker built alone in a temp folder has
  no shield.

## Consequences

This ADR supersedes no ADR. It updates ADRs 0011, 0012 and 0013, each of which now points here. ADRs 0006 and 0016 and the skill's ADR 0011 decide nothing different, but
the increments change what their text describes, and the increment that does adds the Update there. The rest are unchanged:

- [ADR 0005](0005-the-factory-owns-the-template.md) is unchanged: the C# scripts and the shields are Owned files like any other, and reach the Fleet by a Rollout.
- [ADR 0006](0006-the-factory-carries-the-git-hooks-and-a-gate.md) is unchanged in what it decides. The gate it gave the Factory gains the `global.json` copy in its list of copies,
  the cache key above, and a build of the scripts before it runs them; the first increment that adds C# to the gate adds the Update.
- [ADR 0011](0011-markdown-and-yaml-are-linted-as-they-are-written.md)'s Update: Python stays in force while any Python is left. The increment that retires Python marks it
  superseded, naming what replaces ruff.
- [ADR 0012](0012-the-template-tests-workflow-runs-the-gate-in-parallel-parts.md) and [ADR 0013](0013-ci-runs-test-sh-in-shards-and-the-review-tests-as-their-own-part.md): the
  parts and shards stay as they are. `.github/actions/template-tools/` gains `setup-dotnet`, reading the SDK from `global.json`, for the parts that build C#. Each part builds
  its scripts once before it runs them.
- [ADR 0016](0016-a-rollout-runs-each-applications-review-headless-and-sandboxed-in-its-worktree.md): its preflight's check for ruff 0.17 or uv goes with ruff, and the increment
  that retires ruff adds the Update. It already checks for the SDK `global.json` names.
- The skill's [ADR 0003](../../.claude/skills/repo-ci-baseline/docs/adr/0003-github-settings-as-code.md) is unchanged: the settings stay data in the skill, read by `SettingsCore.cs`
  (`settings.cs`) since mpaulosky/Application_Factory#120.
- The skill's [ADR 0011](../../.claude/skills/repo-ci-baseline/docs/adr/0011-the-gh-repo-check-parses-shell-with-shfmt.md) is unchanged in what it decides. The C# `check_gh_repo`
  still reads each script's syntax tree from `shfmt -ln bash --to-json`, run in an empty folder. Its text names `check_gh_repo.py`, `python3 -I` and Python's recursion limit, so
  the increment that ports the checker adds an Update there for those.
- An Application's CI loses `setup-python`, pytest, `requirements-tests.txt` and ruff when the last Python goes. Every job that runs a script but builds no .NET
  today gains a `setup-dotnet` step and a first build of its script, about 12 s on a fresh runner: among them the change detection, the test discovery, the gh and Owned-files
  checks, Claude Review, the release jobs and the blog-post backfill. The Seed image already carries the SDK.
- The trial's reading of the extra start-up cost is first-use JIT along each new code path, an inference its note marks as such (mpaulosky/Application_Factory#97). On that reading,
  a script that makes many `gh` calls in one process pays it once for each new code path, not again for each repeat, and `land.py`, which polls in a loop inside one process,
  pays it mostly on its first poll. No loop of polls was measured. A wrapper that starts a script many times pays it each time, and no wrapper today starts one often enough
  for that to matter.

## Update (mpaulosky/Application_Factory#103): the SDK pin lands in two steps

Until the Factory has its own `global.json`, the `dotnet-sdk` entry lists only `template/owned/`, with its group already in place; the increment that brings the copy
(mpaulosky/Application_Factory#117) adds the root to it. The Seed Dockerfile can't read `global.json`, since Sandcastle builds the image with `.sandcastle/` as its context,
so it installs the pin's feature band (`DOTNET_CHANNEL=11.0.1xx`), whose newest patch `rollForward: latestPatch` accepts, and `test.sh` holds the channel to the pin's
band. A Dependabot PR that moves the band fails `test.sh` until a commit on its branch changes the channel.

## Update (mpaulosky/Application_Factory#117): the Factory carries the SDK, and the gate lints the C# files

The first increment brings the .NET SDK into the Factory, with no script ported yet:

- **The SDK.** The Factory's `global.json` is a copy of the Template's, in `scripts/gate.sh`'s list of copies, so `--sync` keeps it equal and the drift check fails it otherwise.
  The `dotnet-sdk` Dependabot entry lists `/` beside `template/owned/`, under its one group. `.github/actions/template-tools/` runs `setup-dotnet` with `global-json-file: global.json`,
  so every part of the gate has the SDK. The gate's cache key is the tree and then the SDK: the release `dotnet --version` answers from the root and the version `global.json` names.
- **The tests.** `tests/Scripts.Tests/` is an xUnit v3 project (`net11.0`, `xunit.v3.mtp-v2`, warnings as errors), shielded by its own three empty files, as is the skill's `github/`.
  It targets `net11.0`, not the Applications' `net10.0`: a file-based script builds for the SDK's own framework, so the tests run on the runtime the scripts do, and the pinned SDK
  alone runs them, with no .NET 10 runtime installed beside it.
  `test.sh` runs it in the `scripts` shard, built into a temp folder so the skill's tree stays clean. Until it holds a test it is only built: Microsoft Testing Platform fails
  a run in which no test ran (exit 8). (The Update for mpaulosky/Application_Factory#145 below replaces the build-only run with `dotnet test`.)
- **What lints C#: the gate and CI, not the edit-time hook** (Still open: what replaces ruff). The `static` part, and so `--quick` and CI, runs
  `dotnet format whitespace . --folder --verify-no-changes` over every C# file, in folder mode, which reads no project and runs no MSBuild; then it builds every script entry file
  with warnings as errors. An entry file is a `.cs` in no project's folder that no `.cs` in its folder pulls in with `#:include`. A project's own files are built by the test that
  runs it. The hook doesn't lint `.cs` files: a `dotnet format` run takes 1 to 2 s, too slow for every edit ([ADR 0011](0011-markdown-and-yaml-are-linted-as-they-are-written.md),
  Update: C# files). With no `.cs` file the step checks nothing and needs no SDK, which `scripts/tests/gate.test.sh` checks on a fixture.
- **CA2266: a warning, not an error.** The gate builds each script with `-p:TreatWarningsAsErrors=true -p:WarningsNotAsErrors=CA2266`, so every other warning fails the build
  and CA2266 stays visible as a warning. Checked on SDK 11.0.100: `TreatWarningsAsErrors` alone makes CA2266 an error (exit 1), with `WarningsNotAsErrors=CA2266` the build
  exits 0, and `dotnet build app.cs -warnaserror` isn't accepted for a file-based app (MSB4025). The other two ways were turned down: a shebang fails
  `dotnet format whitespace --verify-no-changes` (exit 2, the `# !` rewrite above), and an entry file with no `#:include` would undo the thin-entry-file rule.
  (The Updates for mpaulosky/Application_Factory#145 and mpaulosky/Application_Factory#161 below add NU1900 and NU1905 to `WarningsNotAsErrors`.)

## Update (mpaulosky/Application_Factory#145): the C# step and the test project are hardened

- **NU1900 stays a warning too.** The gate builds each script with `-p:WarningsNotAsErrors=CA2266%3BNU1900` (`%3B` is MSBuild's escaped `;`), and `Scripts.Tests.csproj`
  adds NU1900 to its `WarningsNotAsErrors`. NU1900 says the vulnerability data couldn't be fetched, a feed outage, which says nothing of a package, so it no longer fails
  every run while nuget.org is unreachable. The test project names its audit source, nuget.org alone (its `NuGet.config` clears `auditSources`, so a config above it
  can't point the audit at a feed that reports nothing), and then an outage is also NU1905, "the audit source gave no data", which it allows for the same reason.
  NuGetAudit's NU1901 to NU1904, a vulnerable package, stay errors.
- **Each build is its own.** The gate builds each entry file with `--no-incremental`, so a build cached without these properties can't hide a warning; into a `gate-cs.*`
  folder of the run's own (`--artifacts-path`), which the EXIT trap removes however the gate ends; and with `--disable-build-servers`. A TERM mid-build ends the build (a TERM,
  then a KILL after 5 s) and waits for it before the folder is removed; while it ends the build it ignores INT, TERM and HUP, so a second signal (a cancel sent twice,
  Ctrl-C pressed twice) can't cut the KILL, the wait or the removal short. TMPDIR is made absolute first: dotnet fails on a relative one.
- **`test.sh` runs the test project with no build server** (`MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`, `-p:UseSharedCompilation=false`: `dotnet test`
  passes `--disable-build-servers` on to the test app). It runs `dotnet test` whether or not the project holds a test, and accepts exit 8, "no test ran", only while the project
  holds no `.cs` source. It checks that the project targets `net<major>.0` of the SDK `global.json` names, that each of the six shield files is `<Project />` (the target
  framework and shield checks, the reading of the major from `global.json` and the `auditSources` check below, which ignores XML comments, are each also tried on files they
  must pass and refuse), and that a copy
  passes, NU1900 and NU1905 warnings, with its feed unreachable and hostile `Directory.*` files above it. It hands dotnet an absolute output folder and TMPDIR, since the run
  starts in the project's folder, and sets no build server, no node reuse and no first-run setup whatever the caller set; a stub dotnet checks both. `test.sh`'s
  own temp folders are made absolute by prefixing the working folder, not by `cd`, which a caller's `CDPATH` makes print the folder, and a run under a relative
  TMPDIR with `CDPATH=.` checks that its trap leaves nothing behind. The trap is set before the first `mktemp`, over empty names, and a run whose second `mktemp` is a stub that
  fails checks that the first folder is still removed. `test.sh` finds its own folder with `CDPATH` cleared and stops (exit 2) when that folder holds no `test.sh`; a run by a
  relative path under `CDPATH=.` must reach the shard check, and a run from a process substitution must stop at that guard.
- **The audit reads nuget.org alone.** The project's `NuGet.config` clears `auditSources` and adds nuget.org, which `test.sh` checks.
- **The test project's restore is pinned.** Its own `NuGet.config` clears every source and mapping and restores from nuget.org alone, and `packages.lock.json` pins
  every package by hash; `test.sh` restores in locked mode, so a version moved without the lock file fails with NU1004, and checks both on a copy under a `NuGet.config`
  that adds another feed. Dependabot's NuGet updater restores after a bump, so it rewrites the lock file with the version.
- **The template-tools action's `setup-dotnet` pin** is the Template ci.yml's, which `test.sh` checks, and Dependabot's `github-actions` entry lists the action's folder.
- **dotnet runs with telemetry off and no first-run setup**, as in the Template's ci.yml, **and no logo** (`DOTNET_NOLOGO`, the Factory's own addition) in
  the gate, `test.sh` and the workflow.

## Update (mpaulosky/Application_Factory#124): retired Owned files

Apply can now remove an Owned file the Template retired, so the increments that replace a `.py` with a `.cs` can take the `.py` out of every Application:

- **Where the list lives.** `retired.txt`, beside `apply.sh` in the skill, holds one `<sha256>  <path>` line for every version of a retired file the Template shipped, as
  `sha256sum` prints it. It is a data file, not a tuple beside `OWNED` in `check_gh_repo.py`, which mpaulosky/Application_Factory#131 ports, and it holds hashes because a retired file is no longer in
  `template/owned/` to compare with. A retired path still in the Template stops Apply and fails `test.sh`.
- **What Apply removes.** A regular file, in a folder that resolves inside the repo, identical to one of those versions. A changed copy, a symlink, a folder or a file reached
  through a symlinked folder is kept and reported once, in its own section of the report and never as a leftover too; a copy already gone isn't mentioned; `--dry-run` says
  "would be removed". `reapply.sh` commits the deletion with the rest of Apply's output.
- **The review.** A re-Apply that deletes a retired file, where `main` had a regular file, passes the gate's review check unreviewed, as one that writes an Owned file does:
  the retirement was reviewed in the Template's PR. The ledger's `RETIRED_PATHS` names the paths, since the Application has no `retired.txt`, and `test.sh` holds the two
  equal. `rollout.sh --auto` lists a deleted retired file in its review's prompt with the Owned files the branch changes.
- **The Factory's copies.** A retired file the Factory copies leaves `scripts/gate.sh`'s copies list, and the Factory's copy is deleted, in the PR that retires it; `--sync`
  doesn't delete a copy.
- **Status.** `status.sh` prints no per-file Drift, so it gains no retired line: a retired file still present shows in the next Apply's report.

## Update (mpaulosky/Application_Factory#119): status, landing and land are C#, and where a replaced pytest suite's tests go

The first ported scripts, Factory-only, so the `.py` files are deleted in the PR that ports them:

- **The files.** `github/status.cs` and `github/land.cs` are the entry files. They pull in `Landing.cs` (the landing decision), `StatusCore.cs` (the status
  line and the GitHub reads `land.cs` shares), `LandCore.cs` (the watch loop), `Repos.cs` (the `repos.txt` reader, here rather than with `settings.py`'s port,
  mpaulosky/Application_Factory#120) and `Gh.cs`. `status.py`, `landing.py` and `land.py` are gone, and both parity suites pass against the C#.
  `settings.py` stays for `rollout.py`, so an xUnit test holds `Repos.cs` to its `read_repo_list()`, on `repos.txt` and on files with the edge cases either could read
  differently.
- **`Gh.cs`'s argument parser is held to argparse.** An xUnit test runs Python's argparse and the parser on the same arguments, land's among them. It keeps argparse's
  limits as well as its forms: a long option by any unique prefix, `--name=value`, `float()`'s spellings and argparse's exit 2, and one run of positional
  arguments, so `land.sh o/app --once o/lib#7` is refused, as `land.py` refused it. `--help` counts among the options a prefix can name, a float option's
  value is read when the option is met, and an argument that names no option is positional when it looks like a negative number (argparse's `-\.?\d`) or
  holds a space. `-h` with more after it is help unless a `=` or `-` follows it, and `float()` and `\d` read any Unicode decimal digit, astral ones too.
  Where Python 3.12 and 3.14 differ (a negative number with more after it, `-5x`), the parser reads as 3.14 does, and a test holds that reading.
- **What the port changes on purpose.** The pre-push review of the port found weaknesses the Python had too, and the C# closes them rather than carry them
  over: `gh` is found in `PATH`'s absolute folders only (.NET's own lookup tries the program's folder and the current one first); a release-blog branch
  counts only on a PR from the repo itself (`isCrossRepository` false), so a fork's PR on `docs/release-notes` isn't armed; a PR's title and a check's name
  or status context are made printable before they are printed; `status.sh` and `land.sh` refuse a repo that isn't `owner/repo` before any call, the whole argument (a trailing line break too), in
  GitHub's ASCII characters, with neither side `.` or `..` (a usage error for an argument, exit 1 for a `repos.txt` line); a permission answer that isn't an
  object is unread rather than no write access; and `land.sh` refuses a PR number past a 64-bit integer whatever came before it, and a `--interval`
  `time.sleep` refuses (negative, NaN, or from 2^63 nanoseconds, about 292 years, up, infinity among them) ends it with exit 1 when it first waits, as `time.sleep` did,
  rather than hang. The owner's Copilot ask record is matched ignoring case, as GitHub matches logins, so `land.sh` finds its own record for an owner typed
  or listed in another case rather than ask Copilot again on every run (`github-actions` is still matched exactly). `Landing.Epoch` reads only the
  extended ISO-8601 form `datetime.fromisoformat()` read, and takes a time with no offset as UTC where the Python took the machine's zone (GitHub's times
  carry `Z`). A list the Python indexed (`["nodes"]`) that's missing, null, or a non-empty object or string still fails the read, as it did
  (at its first item), and so does an empty object or string, which the Python read as an empty list, and a null commits list, which it read
  as no commits. `land.cs` reads a PR's state and Copilot's request from `gh pr view`'s stdout, where the Python read its stderr first and took a warning
  gh printed on a call that succeeded for the answer, and only MERGED or CLOSED ends a PR's watch. The parity suites' fixtures carry `isCrossRepository` where a case needs a release-blog PR.
- **The wrappers.** `status.sh` and `land.sh` change to `github/` and run `dotnet run --file` with the script's absolute path, so the Factory's `global.json` picks the SDK.
  The build leaves no MSBuild node, MSBuild server or compiler server behind, and a missing `dotnet` exits 127 with a message.
  `test.sh` runs the status suite against `status.sh`, and `land.sh --help`, under a `global.json` that pins an SDK nobody has installed.
  `rollout.sh --auto` lists `dotnet` among the tools it wants, since `land.sh` runs on it.
- **The parity suites' reference.** A suite's reference is `github/<suite>.py`, else `github/<suite>.cs`, else the Template's `.py`. The runner builds a `.cs`
  reference once, from its own folder, before the first case, and runs each case from that folder too, since dotnet picks the SDK by the folder it runs in;
  record mode records from it, since the Python it was recorded from is gone. A `.py` beside its `.cs` stays the reference until its port is judged.
- **When a pytest suite is replaced.** With the `.py` it tests: in the PR that deletes a Factory-only `.py`, and for an Owned script in the Rollout that drops its
  `.py`, since until then the `.py` is what a revert runs. Its tests move to xUnit as one test method each, under the pytest name without `test_` and with
  its first letter capitalised, with the same cases and assertions, so the count can be checked against the pytest file's. A test that compared two
  implementations by text (`reapply.sh`'s ask comment against `landing.py`'s, which `reapply.test.sh` checked) moves to xUnit too.
- **Where its xUnit tests live.** The tests of a script in the skill's `github/` folder go in `tests/Scripts.Tests/`, one folder per script or class (`Status/`,
  `Landing/`, `Land/`, `Repos/`, `Gh/`). The project compiles every `.cs` in `github/` but the entry files, which hold top-level statements, and finds the
  files its tests read (`repos.txt`, the Template's workflows, the references) through the skill folder in its `SkillDir` assembly metadata. So `test.sh`'s
  copies of the project, for the locked restore and the unreachable feed, carry the skill beside them. Where the Template's own scripts' tests go is still
  open, decided with the first Owned script's port.
- **The Owned files.** The comments in the Template's `pr-automerge.yml` and `pr-automerge.test.mjs` that named `github/landing.py` name `github/Landing.cs`.
  The change is comments only, so it rides the next Rollout rather than calling one of its own.

## Update (mpaulosky/Application_Factory#121): the rollout suite has a runner of its own

`rollout.py` calls `reapply.sh` and `land.sh` by absolute path beside itself, and keeps its lock, logs and sessions under `~`, which `tests/parity.test.sh`
has no way to give a run. So its suite, which judges `auto_rollout.py` too (it has no command line of its own), has a second runner,
`tests/rollout-parity.test.sh [command...]`: each run is of a temporary copy of the skill folder, with stubs for those two and a fixed `repos.txt`, and a HOME
of its own, and `git` and `claude` are fakes on `PATH` beside the fake `gh`. A command word that names a file in the skill folder names the copy's, so a port
finds the stubs as the reference does. The two runners share the fake `gh` and the stderr match (`tests/parity/stderr.sh`), not their case loops.
The plain-mode cases come first; `--auto`'s follow in mpaulosky/Application_Factory#156.

## Update (mpaulosky/Application_Factory#156): the rollout suite's `--auto` cases

The `--auto` cases needed more of the runner than plain mode. `--auto` checks its tools on `PATH`, so the run's `PATH` holds only the fakes (each tool the
preflight looks for) and links to the host's own commands; a case's `missing` takes a fake off it, and a host's own `pnpm` can't stand in. The copy of the skill
folder sits where it does in the Factory, so the Factory checkout `--auto` derives is the same each run, and it holds a stand-in Template whose skill text the
review session's prompt carries. Suite-wide defaults answer the preflight and a review session. A case's `setup.sh` and `after.sh` hold the lock and check that
a child's group is gone, its `ignore` starts the run with a signal ignored, and fixtures signal the wrapper's PID. The random names `--auto` makes are written
`XXXXXXXX`, and a `.git` in HOME is left out. The 27 cases pass against `rollout.py` and `auto_rollout.py`, with no change to either; the port in
mpaulosky/Application_Factory#122 runs against the same cases.

## Update (mpaulosky/Application_Factory#123): the gate's two checkers are C#

`tests/check-issue-refs.py` and `tests/check-git-status.py`, the Factory's test helpers that the gate and `test.sh` run, are C# file-based apps, and the `.py` files and the
pytest (`tests/issue_refs/`) are gone. They are Factory-only, so there is no Rollout. The bench and its YAML package moved to mpaulosky/Application_Factory#161.

- **The files.** The entry files `tests/check-issue-refs.cs` and `tests/check-git-status.cs` pull in `IssueRefs.cs` and `GitStatusGuards.cs`, which hold the rules,
  and `Checker.cs`, which both share: `Checker.Run`, and Python's text rules (below). `tests/` holds the three empty shields, which `test.sh` checks with the others.
  The Owned-files checkers' rules don't apply: these two read the Template's own `owned/` folder, not a PR's tree.
- **A crash is exit 2.** `Checker.Run` turns an uncaught exception into exit 2, "nothing was fully checked", where `Gh.cs`'s `Script.Run` gives 1: for a checker 1 means
  "found". `check-git-status.py` already did this; `check-issue-refs.py` exited 1 with a traceback, which the port changes on purpose.
- **Python's rules are kept where .NET's differ**, each pinned by a parity case that fails without it: a line ends where `str.splitlines()` ends one (`\v`, `\f`, `\x1c` to
  `\x1e`, U+0085, U+2028, U+2029, a lone `\r`) for the bare-`#N` check, and as Python's text mode read it (`\r\n` and `\r` as `\n`, a byte that isn't UTF-8 as U+FFFD) for the
  guard check; space is `str.isspace()` (`\x1c` to `\x1f` too); `\w` is a letter, a number or `_` (not .NET's marks and connector punctuation), and `\d` and `\w` read an
  astral character as Python does (.NET's regex reads UTF-16 code units, so each is folded to one BMP character of its class before matching); paths sort as `sorted()`
  sorted pathlib paths, a segment at a time by code point, so `a/x` comes before `a-b/x` and an astral name after U+FFFD.
- **A FIFO, a dangling link and a loop.** The bare-`#N` check skips what isn't a regular file and a name that resolves to nothing (ENOENT, ENOTDIR, ELOOP), and fails on any other
  error, as `os.stat` let the Python. The BCL tells neither a FIFO from a regular file nor ELOOP from ENOENT, so it calls the runtime's own stat, `SystemNative_Stat` in
  `libSystem.Native`, which `System.IO` calls, reading only the mode: its second `int32`, whose file-type bits are the same on every platform. A runtime without it fails the
  check with exit 2, not a pass. The guard check still blocks on a FIFO named `*.sh`, as the Python did.
- **The wrappers.** `tests/check-issue-refs.sh` is new and `tests/check-git-status.sh` now runs the C#; each changes to `tests/`, makes a relative folder absolute (so the guard
  check prints it absolute), and exits 2 with a message when `dotnet` isn't on `PATH`. `test.sh` checks both from under a `global.json` that pins an SDK nobody has installed.
- **The parity suites.** `tests/check-issue-refs.test.sh` is new, and `tests/check-git-status.test.sh` now takes the implementation as its command. Both passed against the
  Python and the C# before the `.py` files went. With no command each builds the C# once, into its own temp folder, and runs `dotnet <dll>`, which starts in about a tenth of the
  time `dotnet run --no-build` takes (a 105-case suite ran in 6 s rather than 54).
- **The xUnit tests.** The pytest's 13 tests are `tests/Scripts.Tests/IssueRefs/`, one method each; `Scripts.Tests` compiles every `.cs` in `tests/` but the two entry
  files. The guard check's crash case, which `check-git-status.test.sh` ran through Python, is in `Checker/`.

## Update (mpaulosky/Application_Factory#120): settings is C#, and settings.py stays for rollout.py

A Factory-only port, as the mpaulosky/Application_Factory#119 and mpaulosky/Application_Factory#123 ones are. `rollout` isn't part of it: `rollout.py`
imports `auto_rollout` in plain mode too, and `test_auto_rollout.py` imports `rollout`, so under the import rule above they port together
(mpaulosky/Application_Factory#122).

- **The files.** `github/settings.cs` is the entry file; it pulls in `SettingsCore.cs` (the logic; a `Settings.cs` would clash with
  `settings.cs` where case is ignored), `Repos.cs` and `Gh.cs`. `github-settings.sh` changes to `github/` and runs it, as `status.sh` does. `Gh.Process` gains an overload
  that writes a body to gh's stdin, and the `owner/repo` pattern moves from `StatusCore.cs` to `Repos.cs` (`Repos.Name`), so `settings.cs` needs no `StatusCore.cs`.
- **settings.py stays.** `rollout.py` imports its `read_repo_list`, so it is deleted with `rollout.py`; nothing runs it as a command now. While it is left it is the
  settings parity suite's reference (the runner's rule above), `test.sh` runs the suite against `github-settings.sh` too, and `SettingsPortTests.cs` checks the
  Python readings the C# re-implements against it, run by `python3`.
- **Its output is Python's, byte for byte.** A report prints settings as `repr()` did (`True`, `None`, `'PR_TITLE'`), ruleset parts as `json.dumps()` did
  (`", "` and `": "`, every character past ASCII escaped), and `--json` as `json.dump(indent=2)` did, so `SettingsCore.cs` carries those readings (`Py`), with
  Python's `==` (a bool is an int), `sorted()` (by code point; None against a number fails), `str.splitlines()`, `str.strip()` and `b64decode()`.
- **The parity suite runs on a copy of `github/`.** The suite holds a `github/` of its own (a small standard, a `repos.txt` with each scope and two per-repo files),
  which the runner lays over a copy of the skill's `github/`, beside copies of the skill's wrappers and the `global.json` above it; the reference and any command word
  naming a skill file the copy holds run from the copy. So its cases don't change when the Fleet's standard or registry does, and `--all` and the scopes are covered.
  The fake `gh` answers `gh api -X <method> <path> [-H <header>]... [--input -]`, keys a write by its method (`rest.PATCH.<path>`), and logs each call's argv and its
  body as canonical JSON (`jq -cS`), so two serializations of one body log the same line.
- **What the port changes on purpose.** A repo that isn't `owner/repo` is refused before any call: a usage error (exit 2) for an argument, exit 1 for a `repos.txt`
  line, as `status.sh` refuses one. The Python sent it to `gh` as it was, where `..` would step out of the REST path a write goes to. The names GitHub answers with
  that go into a path or a query (a workflow's path, the default branch, a ruleset's id) are percent-encoded, a segment at a time with the slashes kept
  (`Settings.Segment` and `Settings.Segments`), and an empty, `.` or `..` segment is refused (exit 1, the error on stderr), so a `?`, `#` or `%` in one can't
  change the call; the Python spliced them in as they were. Plain
  names are sent as before, so the parity cases are unchanged. `Gh.Process` ignores a broken pipe on gh's stdin, as Python's `communicate()` did, so a `gh` that
  fails before it reads its body still reports its own exit code and error.
- **The wrapper's build leaves CA2266 out.** `dotnet run` prints its build's warnings on stdout, so the first run after a change would start its report, or its
  `--json`, with the CA2266 line. `github-settings.sh` passes `-p:NoWarn=CA2266` to that build alone; the gate's build of every script still shows it.
  `status.sh` and `land.sh` print it too on a first run, which this Update leaves for their own change.
- **The pytest suite.** `github/tests/test_settings.py` is deleted, its 34 tests moved to `tests/Scripts.Tests/Settings/SettingsTests.cs` under the rule above, a
  `Settings/` folder beside the ones the mpaulosky/Application_Factory#119 Update lists. It held no `read_repo_list` test: `ReposTests.cs` already holds `Repos.cs` to it.

## Update (mpaulosky/Application_Factory#157): library-first ports, and how rollout's C# holds the lock, runs groups and keeps signals

Decisions only; nothing is ported here. The evidence is the research note
[`2026-11-25-rollout-csharp-processes.md`](../research/2026-11-25-rollout-csharp-processes.md), whose spikes ran on Linux and were then thrown away.

### Library first

This updates the rule above that a script ports with everything that imports it. `auto_rollout.py` is too large for one worker run to port, so a module's C# logic may land ahead
of its cut-over:

- **As a library that nothing runs yet**, beside its `.py`, tested by xUnit copies of its pytest tests, one method each under the naming rule of the
  mpaulosky/Application_Factory#119 Update, with the same cases and assertions. The `.py`, its pytest and the wrapper don't change: the pytest keeps running, and the `.py` is
  still what runs and what a parity suite judges against.
- **Kept in step until the cut-over.** A PR that changes the `.py`'s logic in the meantime changes the library and its xUnit copy too, so the cut-over doesn't find the
  difference late.
- **The entry file lands with the first library.** The gate builds every `.cs` that no file in its folder includes as a script on its own, and a file with no top-level statements
  fails that build (CS5001, which the note shows). So the first library PR adds the script's entry file (for rollout, `github/rollout.cs`), which `#:include`s each library file as
  it lands, and the gate builds them through it with warnings as errors. Until the cut-over its top-level statements only write to stderr that the wrapper still runs the
  Python, and exit 2; nothing calls it. `Scripts.Tests` leaves it out of its `Compile` items, as it does the other entry files.
- **The cut-over PR** fills in the entry file, switches the wrapper and the callers, runs the parity suite against the C#, and deletes the `.py` files and their pytest, as
  the rule above says (for rollout, `settings.py` with them).
- **Who uses it:** F3a to F3c, mpaulosky/Application_Factory#122's split of `auto_rollout`'s logic into libraries, and T3b, mpaulosky/Application_Factory#128's split of the
  release scripts. A T3b library is part of the Template, so where it sits is T3b's; its tests go where the Template's own scripts' tests go (the Update for
  mpaulosky/Application_Factory#125).

### How rollout's C# runs its children

This updates ADR 0016's "Its processes" and its lock (its own Update for mpaulosky/Application_Factory#157 points here). What the round does is unchanged; this is how the C# does
it, and each part is shown in the note.

- **The lock is a `FileStream` with `FileShare.None`**, on the same file (`~/.cache/repo-ci-baseline/rollout-auto.lock`). On Linux that is `flock(LOCK_EX | LOCK_NB)`, the call
  Python's `fcntl.flock` makes, so a C# round and a Python round keep each other out while both exist; an `IOException` from the open is the lock held. `flock(1)` around the
  wrapper is rejected: every child the round starts holds the lock then, `gh` and `git` among them, as it is when the descriptor's `FD_CLOEXEC` is cleared, and plain mode and
  `--dry-run`, which take no lock today, would take it too.
- **Each group's command is started with `posix_spawnp`**, called through `DllImport` from libc, not `Process.Start`:
  - `POSIX_SPAWN_SETSID` gives it a session and group of its own, with its pid as the group's id, as `start_new_session=True` does.
  - `posix_spawn_file_actions_adddup2(fd, fd)` clears `FD_CLOEXEC` on the lock's descriptor in that child alone, as `pass_fds` does; the same file actions put the log
    on its stdout and stderr where `auto_rollout.py` gives `stdout=`.
  - Its signal mask is emptied and SIGPIPE set back to its default (`POSIX_SPAWN_SETSIGMASK`, `POSIX_SPAWN_SETSIGDEF`), as Python's `restore_signals` does: .NET ignores SIGPIPE,
    and a `Process.Start` child starts with it ignored.
  - The program is the absolute path the round resolved, from `PATH`'s absolute folders only, as `Gh.cs` finds `gh`.

  `Process.Start` stays for the short calls whose output the round reads (`gh`, `git`, `claude auth status`), none of which gets the lock.
- **The leader is left unreaped until its group is killed.** .NET reaps every child `Process.Start` started, the `setsid` program's too, the moment it exits, so a group whose
  leader came from `Process.Start` keeps its id only while a member is left; it reaps none it didn't start. So the round waits on a spawned leader with
  `waitid(P_PID, pid, WEXITED | WNOHANG | WNOWAIT)`, polling as `wait_for` does, kills the group with `kill(-pgid, SIGKILL)`, reaps the leader with `waitid` after that, and
  reads what is left of the group from `/proc` as `group_alive` does. `setsid` through `Process.Start` is rejected for that reason.
- **Signals are held with `PosixSignalRegistration`**, whose handler records the first SIGINT, SIGTERM or SIGHUP and sets `Cancel`, the round acting on it as
  `deferred_signals` and `round_signals` do now. An ignored signal stays ignored:
  - SIGHUP and SIGINT. The runtime leaves an ignored one ignored, even with a registration, and a child inherits it; the round reads `SigIgn` from `/proc/self/status` as
    it starts and registers nothing for one the caller ignores, as `held` does.
  - SIGTERM. The runtime takes it over before the first line runs, so `rollout.sh` passes on the `SigIgn` it reads from `/proc/$$/status` before its `exec`, and when SIGTERM was
    ignored the round puts `SIG_IGN` back (`signal(SIGTERM, SIG_IGN)` through `DllImport`) and holds nothing for it. Its children inherit that, as Python's do.
- **`rollout.sh` runs the built dll, not `dotnet run --file`.** It builds with `dotnet build <github/rollout.cs> -t:Build -getProperty:TargetPath`, which builds and prints only
  the dll's path (in the file-based app's own artifacts folder, where `dotnet run` builds it), with the build environment `status.sh` uses, and then runs `exec dotnet <dll>`.
  So the round is the wrapper's pid, and a SIGTERM or SIGHUP sent to it reaches the round's handler. Through `dotnet run --file` the app is the CLI's child: in the note's runs
  a SIGTERM or SIGHUP to the wrapper's pid ended the CLI at once and left the round running, its handler never called, and a SIGKILL left it running as well, where a SIGKILL
  of `rollout.sh` ends the round today. Once built, the build step took about 1.5 s for the spike's small app; the round runs for minutes to hours.
- **`--auto` is Linux only, as ADR 0016 already says.** The struct sizes and offsets the `DllImport` calls use are glibc's on 64-bit Linux, where the note's spikes ran.
  Plain mode starts no group and takes no lock, as today, so it calls none of them.

What this leaves to F3a to F3c and the cut-over: the exact names, the tests that hold each part (a test of the lock against `flock -n`, a spawned child that holds it after
the round's SIGKILL, a leader that stays a zombie until the kill, SIGHUP under `nohup`), and the parity cases for `--auto` (mpaulosky/Application_Factory#156).

## Update (mpaulosky/Application_Factory#161): the bench is C#, and YamlDotNet reads its YAML

A Factory-only port, as the ones above are, so `bench/claude-review-bench.py` and its pytest are deleted in the PR that ports it, and there is no Rollout.

- **The files.** `bench/claude-review-bench.cs` is the entry file. It pulls in `ReviewBench.cs` (the logic), `Yaml.cs` (the YAML reader), and `Gh.cs` and `Py.cs` from
  `github/` by relative path. `Py`, Python's readings of a JSON value, moves out of `SettingsCore.cs` into `github/Py.cs`, which `settings.cs` now includes, so the bench
  takes it without the rest of settings. `Arguments` gains an int option and an option that takes one value or more (argparse's `nargs="+"`, for `--rescore`), held to
  argparse by a test. `bench/` holds the three empty shields, which `test.sh` checks with the others. The bench has no wrapper: run it as
  `dotnet run --file bench/claude-review-bench.cs -- <args>` from anywhere in the Factory, whose `global.json` picks the SDK. Its first run after a change prints CA2266
  on stdout, as `status.sh` and `land.sh` did before their wrappers built them first under a lock (mpaulosky/Application_Factory#192); the bench's output is a report a person
  reads, not one a program parses, so it has no wrapper to do that.
- **The YAML package is YamlDotNet.** The rule above says a script that needs a package needs its own ADR first; this Update is that decision, for the bench and for
  `Scripts.Tests`, whose tests read the Template's `claude-review.yml` too. The options:
  - **YamlDotNet** (MIT, no dependencies, the most used YAML library for .NET). Chosen. Its parser gives the events, and `Yaml.cs` builds from them the
    `JsonNode` tree PyYAML's `safe_load()` gave, with the types PyYAML's YAML 1.1 resolvers gave a plain scalar (`on` and `yes` are true, `1.0` is a float, `1e3` is a
    string), so the bench and its tests read the workflow as the Python did. What it reads differently is named in `Yaml.cs` and tested: a key given twice is refused
    (GitHub refuses one in a workflow too) where PyYAML kept the last value, and an int in base 2, 8, 16 or 60, `.inf`, `.nan`, a timestamp and a merge key stay strings.
  - **A YAML reader of our own, BCL only.** Rejected: the workflow has block scalars, flow collections, quoting and comments, and the reader would be a second YAML
    implementation to keep right.
  - **Regular expressions over the workflow's lines**, as `SettingsCore.cs` finds a workflow's jobs. Rejected: the bench needs the prompt, the arguments and the heredoc
    whole, as YAML reads a block scalar, not a line at a time.
  - **PyYAML or `yq` run as a program.** Rejected: it keeps the Python, or adds a tool, that the port removes.
- **Its restore is pinned as the test project's is.** The entry file pins `#:package YamlDotNet@18.1.0` and sets `RestorePackagesWithLockFile` and `RestoreLockedMode`,
  so `bench/packages.lock.json` pins the package by hash and a version moved without it fails with NU1004. `bench/NuGet.config` restores from nuget.org alone, as the
  test project's does; `test.sh` checks it on a copy under a folder whose `NuGet.config` adds another feed. `Scripts.Tests` takes the same version, in its own lock file.
  Dependabot's nuget block bumps the test project's, not a file-based app's, so `BenchPortTests` fails until the two versions and the two lock files' hashes match: a
  bump's PR moves the bench's by hand (the version in the entry file, then `dotnet build bench/claude-review-bench.cs -p:RestoreLockedMode=false` rewrites its lock file).
- **Offline.** A `#:package` needs nuget.org on its first restore, which a cold start offline can't do. The bench needs the network anyway (it fetches the actions a case
  pins, and runs Claude), but the gate builds every script, so its first build of the bench on a machine needs nuget.org once; after that the package comes from the
  NuGet cache. CI has the network. With the package cached but nuget.org unreachable, the restore can't fetch the vulnerability data (NU1900), and since
  `bench/NuGet.config` names nuget.org as its audit source, that source gave none (NU1905): the gate builds every script with both as warnings, as
  `Scripts.Tests.csproj` already allows them, so an outage doesn't fail the gate. `test.sh` builds a copy of the bench, with the gate's properties, whose feed
  can't be reached.
- **The heredoc stays Python.** The bench runs the workflow's "Write the PR's diff" heredoc with `python3 -I`, as the runner does, until the heredocs in `claude-review.yml`
  are C# (Still open, above).
- **Its output is Python's, byte for byte.** `--rescore` prints and writes back what the Python did: `repr()` and `str()` of a value, `json.dump(indent=2)` with every
  character past ASCII escaped, and `str.split()` and slicing by code point. `bench/tests/rescore.test.sh` is its parity suite, which `test.sh` runs: it passed against the
  Python and the C# before the `.py` went, and on the day of the port `--rescore` over a copy of every file in `bench/results/` gave the same stdout and bytes from both.
  The one exception is the report's escaping of control and format characters, below.
  Its fixtures were recorded from the Python; when the cases file changes they are recorded again from the C# (`RESCORE_RECORD=1`), the reference now.
- **What the port changes on purpose.** An action a case's head pins whose owner, repo or ref is `.` or `..`, or starts with `-`, is skipped, where the Python joined it
  into the folder it copied the action to and passed it to `git` as it was. The programs the bench runs (`git`, `tar`, `python3`, `claude`) are found in `PATH`'s absolute
  folders only, as `Gh.Which` finds `gh`. A linked `.claude`, `.mcp.json`, `CLAUDE.md` or `CLAUDE.local.md` in the head is removed itself, where the Python's `rmtree`
  failed on a linked folder and left a dangling link in place. A failed call's error names the command and its exit status, as Python's `CalledProcessError` did, in
  words of its own. A program in `PATH` that can't be run fails its case, as the Python's `OSError` did, rather than the run. A case's head is read through
  `Yaml.Load` too, so it refuses a text past fixed limits (`MaxText`, 1 Mi characters; `MaxDepth`, 100 levels; and, once its aliases are read in, `MaxNodes`,
  100,000 nodes, and `MaxScalarText`, 4 Mi characters of scalars), and a head's workflow past one pins no action instead of ending the run. It builds the tree
  from YamlDotNet's parser events with a stack of its own, not through YamlStream, whose load recurses once per level (200,000 `[` overflowed the call stack,
  which no catch stops), and checks an alias against the depth and charges it the whole size of the node it names before copying it, where PyYAML shared the one
  object, so an alias bomb is refused before it grows. An int is read as its digits, not through `BigInteger`, and an anchor names a node in its own
  document only, as in PyYAML. A float past a double's range, such as `1.0e+999`, is read as its string, where PyYAML read `inf`: no pinned action is a float.
  Each case's report, in a run and in `--rescore`, shows a control character (but a tab or a newline), a format or line or paragraph separator
  character and a lone surrogate in its error, its seconds, turns and cost, a finding's path, line and body, and the bad lines as `\xNN`, `\uNNNN` or
  `\UNNNNNNNN`, read by code point, where the Python
  printed it raw to the terminal. A call that exits within its timeout but leaves a child holding its pipes ends in a timeout, and the child is killed with
  the call's process group (mpaulosky/Application_Factory#196, below).
  Claude's `stream-json` output is split at `\n` alone, where the Python's
  `splitlines()` also broke at the U+2028, U+2029 and U+0085 that JSON writes raw inside a string and so lost such a review. A `--prompt-file` reads `\r\n` and
  `\r` as `\n`, as `read_text()` did. The run mode, which runs Claude on the cases' clones, has no parity suite: `Scripts.Tests` tests its parts (`BenchRunTests.cs`:
  each outcome a finished call gives, the `--max-turns` cap, the timeout, the YAML limits), and a run on the maintainer's machine the whole. Since
  mpaulosky/Application_Factory#196, `BenchCaseTests.cs` runs the run mode whole too, with a fake `claude` on `PATH` over fixture clones (below).
- **The pytest suite.** `bench/tests/test_claude_review_bench.py` is deleted, its 26 tests moved to `tests/Scripts.Tests/Bench/BenchTests.cs` under the rule above, a
  `Bench/` folder beside the others; `BenchPortTests.cs` holds the port's own. `Scripts.Tests` compiles every `.cs` in `bench/` but the entry file.
- **Hardened after the port (mpaulosky/Application_Factory#196).** Gaps the Python had too, left out of the port's PR:
  - **The run mode is tested.** `BenchCaseTests.cs` runs `Bench.Run` with a fake `claude` on `PATH`, which records its argv, its folder and the config it could
    load, over fixture clones with a base and a head commit: the results file and its fields, claude's argv and folder, a timed-out case's and a missing clone's
    error with the run going on to the next case, each case's worktree and temporary folder removed, `--only` and `--set`, and the message when `claude` isn't on `PATH`.
  - **The action clones are bounded.** `CopyActions` runs each `git` and `tar` call through `Exec` with a time limit (600 s), with `GIT_TERMINAL_PROMPT=0` and an empty
    `GIT_ASKPASS`, so a repo that wants credentials fails its case rather than waiting at a prompt. `Exec` takes the most bytes of stdout a call may give and stops it
    there: 4 MiB of the head's workflow (four bytes for each character `Yaml.Load` takes; past it the head pins no action) and 128 MiB of an action's archive (past it
    the case fails). A call past its time is the case's error. A clone goes to a folder of its own beside the cache and is moved into place whole, so one stopped
    part way never reads as cached, and a run whose clone fails never removes the cache another run filled meanwhile.
  - **A head's nested Claude Code config is removed.** Every `.claude` folder, `CLAUDE.md` and `CLAUDE.local.md` below the root of a case's tree, in any case, is
    removed before `claude` runs, where they came from the head before. The root's stay the base's; `.git` is skipped, and a link is removed itself, never followed.
  - **A call's leftover children end.** Each call starts in a session of its own (`ProcessStartInfo.StartDetached`, `setsid()`), so its process group holds what it
    starts after the call has exited. A call past its time, or one that exits but leaves a child holding its pipes when its time is up, has its group killed, and so
    does every call as it returns, so a child that let go of the pipes ends with it too, where the child ran on before (the Python waited for it instead). The
    terminal's Ctrl-C no longer reaches a call in its own session, so the bench kills every live group on SIGINT, SIGTERM, SIGHUP and SIGQUIT before it ends as the
    signal would end it.

## Update (mpaulosky/Application_Factory#158): the first AutoRollout library, the preflight, sign-in and lock

F3a, the first library under the library-first rule above. Nothing runs it: `rollout.sh` still runs `rollout.py`, and `auto_rollout.py` and its pytest are unchanged.

- **The files.** `github/rollout.cs` is the entry file; until the cut-over it writes to stderr that `rollout.sh` still runs the Python, and exits 2. It includes `Gh.cs`,
  `AutoRollout.cs` (where the round's files are, as a `Folders` record of the skill folder; `short_name`, `review_worktree`, `run_quiet`, the lock, `retired_paths`,
  `owned_changes`, `front_matter`, `read_skill`, and the Python readings they need) and `AutoRolloutPreflight.cs` (the preflight and the sign-in checks).
  `Scripts.Tests` leaves `rollout.cs` out of its `Compile` items, and its copies of the pytest tests are in `tests/Scripts.Tests/AutoRollout/`. A copy whose original also
  checks `session_env`, which mpaulosky/Application_Factory#159 ports, keeps the preflight's assertions; the rest joins it with that port.
- **The lock** is as the Update above decides. Held is an `IOException` of that exact type whose errno is EWOULDBLOCK (11 on Linux); any other error is "can't take it"
  and propagates. Tests hold it against `flock -n` and `auto_rollout.py`'s own `take_lock`, both ways.
- **What the port changes on purpose.** The preflight's `which` and `run_quiet` find a program in `PATH`'s absolute folders only, as `Gh.cs` finds `gh` (`shutil.which`
  and `subprocess` took a relative folder too). `TakeLock` refuses to run when .NET's file locking is turned off (`DOTNET_SYSTEM_IO_DISABLEFILELOCKING`,
  `System.IO.DisableFileLocking`), where a `FileStream` would take no lock and a second round would run beside the first.
- **What it keeps of the Python that .NET doesn't give for free.** `Folders.Default` resolves the entry file's folder through its symlinks first, as `SKILL_DIR` reads
  `os.path.realpath(__file__)`, so a run through `~/.claude/skills/repo-ci-baseline` still finds the Factory's checkout. `RunQuiet` holds one deadline over the exit and
  both pipes' reads, as `subprocess.run(timeout=)` does, so a command that exits but leaves a child holding its output still ends in 124 at the limit.

## Update (mpaulosky/Application_Factory#160): the session guard and the review record are a library

F3c, the third library under the rule above, after F3a's and F3b's. `auto_rollout.py`'s `run_text` to `record_review` is C#, run by nothing; the `.py`, its pytest and `rollout.sh` are unchanged.

- **The files.** `github/rollout.cs`, F3a's entry file, also includes `Repos.cs`, `Py.cs` (`Py`: `repr()`, `json.dumps()`, `str.strip()` and the code point sort) and
  two more libraries. `AutoRolloutGuard.cs` holds the `Guard`, `SessionGuard`
  (`session_guard`, `broken_rules` and the file and tree snapshots they compare), `PyText` (`ascii()`, a bytes `repr()`, `shlex.quote()` and `shlex.join()`) and `Posix`.
  `AutoRolloutRecord.cs` holds `ReviewRecord` (`staged_findings` to `record_review`, with `run_bounded` and `wait_for`) and `GroupLeft`. Each library's classes have
  names of their own, not a shared `partial` class, so F3a and F3b can land beside them in any order.
- **The file reads go through libc.** The BCL has no `lstat` that gives the inode and the mtime in nanoseconds, no open that follows no symlink and never waits on a FIFO,
  and no `mkdir` that fails on a folder already there. So `Posix` calls `statx`, whose layout is the same on every Linux architecture, and `open`, `read`, `close`, `mkdir`
  and `access` through `DllImport`. `O_NOFOLLOW` is the one flag whose value differs between x64 and arm64. `--auto` is Linux only, so this is too.
- **The process group is the cut-over's.** `run_bounded` starts its command with `posix_spawnp` in a group of its own, and that spawner lands with the rest of the
  groups and signals. So `RunBounded` takes the spawner (it returns an `IGroupChild`: its pid, and its exit code once it exits, unreaped) and the group's end as
  parameters, and `RecordReview` takes its bounded runner as one. Their two pytest tests that run a real process group move to xUnit with the cut-over. The other 68
  tests of these functions are in `tests/Scripts.Tests/AutoRollout/`, under the naming rule.
- **What it reads as Python reads it.** A staged findings file is strict UTF-8 JSON, nested at most 1,000 deep. An object's repeated key keeps its first place and its last
  value, as a `dict` does. Python's `NaN` and `Infinity` literals, which `json.loads()` takes, are refused, so a file holding one breaks a Rule, and so is a lone surrogate
  (`\ud800`) in a string or a name, which a .NET string can't hold. The findings are written again
  with `Py.Dumps`, so the kept copy is byte for byte what `json.dumps()` writes. `mkdir`'s error reads as Python's `OSError` does (`[Errno 17] File exists: '<path>'`).
- **One deadline over the exit and the reads.** `SessionGuard.RunText`, as F3a's `RunQuiet`, counts its 120 s over the command's exit and both pipes' reads, so a
  child left holding its output can't stall the guard past the limit.
- **A name that isn't UTF-8 fails closed.** .NET lists such a name with U+FFFD, and its path then names nothing, where `os.walk` kept the bytes. So `TreeState`,
  `FolderState` and `TreeHashes` read a name they can't reach as different every time: the session guard breaks a Rule on a tree that holds one, rather than missing a
  change made under it. A name listed twice (one that isn't UTF-8 decoded to the same text as one that is) reads the same way.
- **`FileHash` never waits or reads without end.** It hashes a regular file of at most `RecordLimit` bytes, opened without waiting, a symlink followed; a FIFO, a device
  or a bigger file gives null, no Template hash, where Python's `open().read()` would have hung or filled memory.

## Update (mpaulosky/Application_Factory#122): rollout is C#, and the skill's github/ holds no Python

F3d, the cut-over under the library-first rule above. `rollout.sh` runs the C#; `rollout.py`, `auto_rollout.py`, `settings.py` and their pytest are deleted.

- **The files.** `github/rollout.cs` runs `RolloutCore.cs` (`rollout.py`: the plain mode, its argument parser held to argparse's words) and the round's three
  last libraries: `AutoRolloutGroups.cs` (`held` to `run_reapply`: `Signals`, the spawner, `EndGroup`, `GroupAlive`, `RunInGroup`), `AutoRolloutReview.cs`
  (`settings_path` to `stop_line`: the review session) and `AutoRolloutRound.cs` (`gh_pr` to `run`). `auto_rollout.py`'s docstring, the round's contract, is
  `AutoRolloutRound.cs`'s header, its names in their C# forms, and a test reads it as the pytest read the docstring.
- **The tests.** Every one of `test_auto_rollout.py`'s 362 tests and `test_rollout.py`'s 44 has its xUnit method under the naming rule, in
  `tests/Scripts.Tests/AutoRollout/` and `tests/Scripts.Tests/Rollout/`. The ones that hold or send signals, start process groups or set the lock are one
  collection that runs alone, since the signal handlers and the lock's descriptor are the process's. A real SIGINT to the test host also reaches the xUnit
  runner's Ctrl+C handler, which cancels the run, so those tests send SIGHUP or SIGTERM, which the round holds the same way, and the SIGINT case of the
  deferred signals is skipped; parity case 33 holds SIGINT end to end. The tests that compared the C# with `settings.py` (`read_repo_list`, the workflow and
  branch readers) hold what it read, recorded from it before it was deleted, and the lock's test holds `flock(1)` alone. The `settings` parity suite's
  reference is `github/settings.cs` now, under the reference rule of the mpaulosky/Application_Factory#119 Update.
- **The parity suite passes unchanged.** Its 40 cases, recorded from the Python, pass against `rollout.sh` running the C#. `rollout.sh` builds and runs it with
  the `dotnet` on PATH, which in a run is the fake, so the runner builds the copy's `github/rollout.cs` once before the first case, and the fake `dotnet` hands
  those two calls alone to the caller's `dotnet`, logging neither; the build gets the caller's HOME, so it writes nothing in the case's.
- **The wrapper.** `rollout.sh` builds with `dotnet build github/rollout.cs -t:Build -getProperty:TargetPath`, from `github/` (the Factory's `global.json`
  picks the SDK) under `flock` on `github/` as `status.sh` does, its output shown only when it fails (exit 1, nothing run), then `exec dotnet <dll>` from the
  caller's folder. Only the build changes folder, so `--root` is passed on as given and a relative one names what it named. It passes the caller's ignored
  signals on in `ROLLOUT_SIGIGN`, the `SigIgn` mask of `/proc/$$/status`; `Signals.Init` reads it first, drops it from the environment, and puts SIGTERM back
  to `SIG_IGN` when its bit is set. `test.sh` checks the wrapper as it checks `status.sh`'s: from under a `global.json` that pins another SDK, six runs at once on a
  fresh build, no `dotnet` on PATH (127), and with a stand-in `dotnet` the dll it runs, its arguments, the mask, and a build that fails.
- **How the children run.** `Groups.Spawn` calls `posix_spawn` on the absolute path the program resolves to in the absolute folders of the `PATH` the child
  gets (the review session's own `PATH` first), as decided above; the folder is set with `posix_spawn_file_actions_addchdir_np` (glibc 2.29), `/dev/null` with
  `addopen`. A spawn that fails is a `SpawnError` worded as Python's `OSError` (`[Errno 2] No such file or directory: 'claude'`). `SpawnedChild.Exit` polls
  `waitid(WNOWAIT)` and leaves the leader a zombie; `Reap`, after the group's kill, waits it. `Process.Start` stays for `gh`, `git` and the preflight's calls.
- **How the signals are held.** One `PosixSignalRegistration` per signal while any handler is installed, and a stack of handlers over it, so a hold replaces the
  round's handler and the round's is back once the hold is disposed, as `signal.signal` has it. Inside a hold the first signal is recorded and passed on, once
  the group is gone, as an `Interrupted` exception that `Run` turns into the resume line and 128 + the signal. Outside one the round's handler prints the same
  line and ends the process with that code at once (`Environment.Exit`), as Python's `SystemExit` ended it: a `gh` or `git` call then running is left to
  finish by itself (at most its 120 s), where `subprocess.run` killed it, and holds no lock.
- **What the port changes on purpose.** The review log is opened with `O_APPEND`, so what the round writes there lands after what the session, sharing the file,
  wrote (.NET writes at a position of its own); a crash's line names the .NET exception (`InvalidOperationException`) where Python named its own, while a
  spawn's error keeps Python's name (`FileNotFoundError`); a set-up error of the session that .NET throws as `InvalidDataException` (an agent or skill file that
  doesn't read, Python's `ValueError`) stops it as Python's did, rather than crashing the round.

## Update (mpaulosky/Application_Factory#125): the Template's first C#, detect_changes

T1a, the first Owned script ported. `ci.yml`'s Detect Changes job runs the C#, and `detect_changes.py` ships beside it for this one Rollout (the rule above for an Owned
script), so a revert is one workflow line; T2 adds it to `retired.txt`.

- **The files.** `.github/scripts/detect_changes.cs` is the entry file; it pulls in `DetectChanges.cs` (the logic) and `.github/scripts/Gh.cs`, created here: the argument
  parser (`Arguments`, the Factory's held to argparse, with `Required` for `required=True` and a parser that takes no positional argument, so an extra one and a `--` are
  refused as argparse refuses them), `Script.Run`, `Script.Which` (a program found in `PATH`'s absolute folders only, since the script runs from the PR's checkout, where a
  `git` at the root is the PR's), `Script.Capture` and `CheckOutput` (`subprocess.run(capture_output=True, check=True)`, a failure a `CalledProcessError` naming the command,
  its exit status and git's stderr), and `SurrogateEscape`, Python's `surrogateescape`: a name that isn't UTF-8 is read as lone surrogates and printed back as its own
  bytes. The gh runner joins `Gh.cs` with the first script there that calls `gh`. The Template's classes are in the namespace `Baseline`, so `Scripts.Tests` compiles them
  beside the Factory's `github/Gh.cs`, whose classes have the same names; the entry file says `using Baseline;`.
- **The shields.** `.github/scripts/` holds the three empty `Directory.*` files. They are Owned and in `check_gh_repo.py`'s `OWNED`, so the Owned files check reports a PR
  that edits or deletes one, and `test.sh` checks that every `Directory.*` file there is in `OWNED`, that each `.github/scripts/` entry of `OWNED` is a file the Template
  ships, and that each shield is `<Project />`.
- **The workflow.** The Detect Changes job runs `setup-dotnet` from `global.json`, builds the script in a step of its own (`dotnet build .github/scripts/detect_changes.cs`),
  and runs it with `dotnet run --no-build --file`, so its log holds only what the script prints. Both are on `pull_request` only, as the checkout is, so every PR, a
  docs-only one too, pays the setup and a first build, about 12 s on a fresh runner. The Hook tests job gains `setup-dotnet` too.
- **The Template gate.** A C# scripts step after the Python lint: when the branch changes a `.cs` in `.github/scripts/` (and without an `origin/main`), `dotnet format
  whitespace .github/scripts --folder --verify-no-changes`, then a build of each script there with `--no-incremental --disable-build-servers`, into a folder the gate
  removes, warnings as errors but CA2266 and NU1900, as the Factory's gate builds its own. `pre-push.test.sh`'s `dotnet` stub hands those two calls to the real `dotnet` when
  it runs the SDK the Template's `global.json` names, and its "stops before the build" cases now match the solution's build alone (`dotnet build *--configuration Release*`),
  so a script build before it doesn't fail them. `discover_tests` and `load_test_env`, and the gate's run of `discover_tests` as C#, moved to mpaulosky/Application_Factory#162.
- **Where the Template's own scripts' tests go** (Still open, above, until now): in the Factory's `tests/Scripts.Tests/Template/`, one folder per script or class (`DetectChanges/`,
  `Gh/`), in the namespace `Baseline.Tests`, so a simple name finds the Template's class first. The project compiles every `.cs` in `template/owned/.github/scripts/` but the
  entry files. An Application carries no test of its C#: the Factory's parity suite and xUnit tests judge it before a Rollout brings it.
- **The pytest file retires now.** `test_detect_changes.py`'s 16 tests are `DetectChangesTests.cs`, one method each, and the file is deleted and in `retired.txt`, all three
  versions the Factory's history holds (the skill's history in dotfiles, before it moved here, isn't, so a copy from then is kept and reported), and in the ledger's
  `RETIRED_PATHS`. This departs from the rule above that an Owned script's pytest goes with its `.py`: the issue decided it, since the parity suite, run against both, holds
  the `.py` to the `.cs` while it ships.
- **The parity suite.** `tests/parity/detect_changes/`, 31 cases over real git repos that each `setup.sh` builds, comparing stdout, the exit code and the bytes appended to
  `--output`. Recorded from the `.py`, they pass against it and against the C#, and `test.sh` runs every Owned entry file in `.github/scripts/` against its suite
  (built once from the Template's folder, then `dotnet run --no-build --file`), failing an entry file with no suite. They pin git's path order (`a-b/x` before `a/x`),
  a name that isn't UTF-8, and argparse's errors and forms.
- **What the port changes on purpose.** `git` is found in `PATH`'s absolute folders only (above), and a failed `git` call's message carries what git said, where the
  Python's traceback named only its exit status. The usage line names `detect_changes.cs`.

## Update (mpaulosky/Application_Factory#162): discover_tests and load_test_env

T1b, on what T1a brought. `ci.yml`'s Discover Test Projects job and each test job run the C#, and `discover_tests.py` and `load_test_env.py` ship beside it for this
one Rollout; T2 (mpaulosky/Application_Factory#126) adds them to `retired.txt`.

- **The files.** `discover_tests.cs` pulls in `DiscoverTests.cs`, and `load_test_env.cs` `LoadTestEnv.cs`, each with `Gh.cs`. `Gh.cs`'s `Arguments` gains `OneOf`,
  argparse's required mutually exclusive group (`--output` or `--list`: a second is "not allowed with" the first, none is "one of the arguments ... is required"), and a
  `Py` class with `str.strip()`, `str.splitlines()` and `str.lower()`, as the Factory's `Py.cs` has them. The four files and `Gh.cs` join `OWNED`, as the issue asked:
  `load_test_env.cs` runs with the `TEST_ENV` secret, and the Owned files check now reports a PR that edits one of them. `detect_changes.cs` and `DetectChanges.cs` aren't
  in it yet.
- **The workflow and the gate.** Each script is built in a step of its own, then run with `dotnet run --no-build --file`, so the log holds only what it prints: the
  Discover Test Projects job gains `setup-dotnet`, and the test job, which has it, builds `load_test_env.cs` in a step without `TEST_ENV`, so the secret reaches only the
  run. The Template gate's Tests step builds `discover_tests.cs` (its output on stderr) and runs it with `--no-build --list`, so the paths it reads are a first run's
  stdout alone. `pre-push.test.sh`'s `dotnet` stub hands that run to the real SDK, and without one answers it from the `.py`; its cases check the build comes first, that
  only the printed paths are tested, and that a failed build or run refuses the push. A Factory xUnit test holds `ci.yml` to the build-first rule.
- **The pytest files retire now,** as `test_detect_changes.py` did: `test_discover_tests.py` and `test_load_test_env.py` are `DiscoverTestsTests.cs` and
  `LoadTestEnvTests.cs` in `tests/Scripts.Tests/Template/`, one method each, with tests of the port's own beside them, and every version the Factory's history holds is in
  `retired.txt` and `RETIRED_PATHS`.
- **The parity suites.** `tests/parity/discover_tests/` (49 cases, each a repo in `tree/`) and `tests/parity/load_test_env/` (37 cases) compare stdout, the exit code and
  the bytes appended to `--output`. They pin pathlib's sort by path part and code point, `json.dumps`'s spacing and `\uXXXX` escapes, `str.splitlines()`'s separators and
  `str.strip()`'s blanks, ElementTree's reading of a value, `Path.resolve()` through a symlink, argparse's group errors, and that a malformed `TEST_ENV` prints no value.
  `TEST_ENV` holds line breaks one `env` line can't, so the runner gains `env.d/`: a file per variable whose bytes are its value.
- **What the port leaves different, on purpose.** A name under `tests/` that isn't UTF-8 (.NET can't open it), names whose lower case Python's full case mapping gives
  differently, and an XML file with a DTD; each suite's README lists them. The usage lines name the `.cs`.

## Update (mpaulosky/Application_Factory#126): check_central_pins, and T1's Python retires

T2. `ci.yml`'s Build Solution job and the Template gate run `check_central_pins` as C#, and `check_central_pins.py` ships beside it for this one Rollout; T3 adds it
to `retired.txt`. T1's three `.py` files retire now.

- **Where CI runs it** (Still open, above, until now): after Setup .NET, not first. The job's order is the checkout, Setup .NET, a step that builds the check, the
  check (`dotnet run --no-build --file`), then the NuGet cache, the restore and the build. It runs the PR's own copy in the job that builds the PR, so it needs no
  checker layout; it loses only the seconds by which a bad pin failed before the SDK was installed. The skill's ADR 0012, whose rule this is, has the same Update.
  The gate builds it (its output on stderr) and runs it with `--no-build` before the solution's build, as it does the test discovery.
- **The files.** `check_central_pins.cs` includes `CheckCentralPins.cs` (the logic) and `Gh.cs`, which gains `PyPath` (`Path.resolve()` and pathlib's sort, moved
  from `DiscoverTests.cs`) and `Py.IsSpace`, `Py.Len`, `Py.Head`, `Py.CompareCodePoints` and `Py.Order`, so a length, a cut and a sort count code points as Python's do.
  The caps are a `Caps` record, and the reads go through a `Files` class, so a test lowers a cap or stands in for a failing call as the pytest patched a constant or
  `os.lstat`, `os.open`, `os.fstat` and `os.scandir`. `GITHUB_ACTIONS` is read once, by the constructor, so tests running in parallel can each set it.
- **How it reads what Python read.** expat becomes `XmlTextReader` with namespaces off (a prefix needs no declaration, as without expat's namespace processing),
  normalization on and undeclared entities an error; a DOCTYPE is parsed only to be refused, before any entity it declares is used, and nothing is fetched.
  `global.json`'s comments and trailing commas go in a linear two-pass scanner that does what the two possessive regular expressions did (.NET has no possessive
  repeats), and `System.Text.Json` reads the rest, nested at most 1,000 deep. Files are opened through libc's `open` with `O_NOFOLLOW`, `O_NONBLOCK` and
  `O_CLOEXEC`, since the BCL has none of them and opening a FIFO with it waits for a writer; `SafeFileHandle.Type` (.NET 11) is the `fstat` check after it. The
  reason marker and the setting's `true` are compared in ASCII, the only letters Python's `lower()` turns into theirs, where .NET's invariant casing would match a
  dotless i. A path that isn't UTF-8 can't be opened, as for `discover_tests`.
- **The tests.** `test_check_central_pins.py`'s 125 tests are `CheckCentralPinsTests.cs` in `tests/Scripts.Tests/Template/CheckCentralPins/`, one method each, but
  `test_an_older_python_is_refused_with_a_message`, whose check went with the Python, and six of the port's own (os.walk's order of problems, a file gone since the
  walk, the errno a .NET exception gives, the real `open` on a symlink and a FIFO, the ASCII reason marker, code point lengths). The step-order test is inverted:
  `Setup .NET`, the build, the check, then `Restore dependencies`. The pytest file is deleted and in `retired.txt` and `RETIRED_PATHS`, as T1's were.
- **The parity suite.** `tests/parity/check_central_pins/`, 80 cases, each a repo in `tree/` (a `setup.sh` makes the symlinks and FIFOs). Recorded from the `.py`,
  they pass against it and the `.cs`. Every stdout line is equal; the README lists what no case holds because the two word it apart: expat's and `XmlReader`'s
  reasons a file isn't well-formed, the encodings each decodes, and a `strerror` .NET reports with an exception of its own. The runner unsets `GITHUB_ACTIONS`, so a
  case's `env` sets it and the plain lines are checked in CI too, and drops a FIFO from the run's folder before it compares or records, since git can't store one.
- **T1's Python retires.** `detect_changes.py`, `discover_tests.py` and `load_test_env.py` are deleted, and every version the Factory's history holds is in
  `retired.txt` and `RETIRED_PATHS`. Their suites' reference is the Template's `.cs` now: the runner builds it once from the Template's folder and runs it from each
  case's, as ci.yml runs it from the Application's checkout, so those cases keep their `tree/` and `setup.sh`; `test.sh` no longer runs an entry file against its suite
  a second time once its `.py` is gone. Without the SDK, `pre-push.test.sh`'s `dotnet` stub answers the discovery with the fixture's one test project and skips the two
  cases that read what it found; its ruff case lints `check_gh_repo.py`.
- **The Seed's `CHECK_PATHS`** names `check_central_pins.cs`, `CheckCentralPins.cs` and `Gh.cs` where it named the `.py`; each Sandcastle Application makes that edit by
  hand, a Seed file being written only when missing.

## Update (mpaulosky/Application_Factory#163): draft_release, and T2's Python retires

T3a, split from mpaulosky/Application_Factory#128 so one run can finish it. `release.yml`'s release job runs `draft_release` as C#, and `draft_release.py` ships beside
it for this one Rollout; T4a (mpaulosky/Application_Factory#129) adds it to `retired.txt`. `check_central_pins.py` retires now.

- **The files.** `draft_release.cs` includes `DraftRelease.cs` (the logic) and `Gh.cs`. Both join `OWNED`, as the release job runs them with its tokens. `Gh.cs`'s
  `Arguments` gains `IntOption`, argparse's `type=int`: a value `int()` refuses is a usage error the moment it is read, quoted as `repr()` quotes it, so before a missing
  or unknown option is reported. `Py` gains `Int`, `int()` of a str (blanks around, a sign, any Unicode decimal digit, an underscore only between two, any size up to
  Python's 4,300 digits), and `Repr`. A marker's number is any run of Unicode decimal digits, an astral one too, read by its value, as Python's `\d` and `int()` read it,
  so the markers are found by a scan rather than .NET's `\d`, which sees an astral digit as two code units, and the numbers are `BigInteger`s.
- **The workflow.** The release job gains `setup-dotnet` and a step that builds the script, both on the condition Create release has, so only a PR it releases pays
  for them. Create release runs `dotnet run --no-build --file`, so `BODY=$(...)` is the body alone on a cold build. `CiScriptStepsTests` holds `release.yml` to the
  build-first rule too, and to no step running `draft_release.py`.
- **The tests.** `test_draft_release.py`'s 10 tests (17 with parameters) are `DraftReleaseTests.cs` in `tests/Scripts.Tests/Template/DraftRelease/`, one method each,
  with tests of the port's own. The two that hold the markers to `release_post.source_pr_of()` ask the Template's `release_post.py`, which is still Python. The pytest
  file is deleted and in `retired.txt` and `RETIRED_PATHS`. `TemplateGhTests` holds `IntOption` to argparse, `Py.Int` to `int()` and `Py.Repr` to `repr()`.
- **The parity suite.** `tests/parity/draft_release/` had 26 cases, recorded from the `.py`; five more (astral digits, a byte order mark in the notes, a number past
  64 bits, a quoted `--pr`, a negative `--pr`) make 31, which pass against both.
- **T2's Python retires.** `check_central_pins.py` is deleted, and both versions the Factory's history holds are in `retired.txt` and `RETIRED_PATHS`. Its suite's
  reference is the `.cs` now. Without the SDK, `pre-push.test.sh`'s `dotnet` stub answers the check with a pass and skips the two cases that read what it found.

## Update (mpaulosky/Application_Factory#164): release_post's logic is a library, and the render oracle stays on cmark-gfm and kramdown

T3b, split from mpaulosky/Application_Factory#128 under the library-first rule above. Nothing in the workflows changes: `release.yml` and `backfill-blog-posts.yml` run
`release_post.py`, `release_queue.py` and `backfill_blog_posts.py` until T3c, the cut-over (mpaulosky/Application_Factory#128).

- **The files.** `.github/scripts/ReleasePost.cs` holds `release_post.py`'s logic, a method for each function and of the same name, with `HtmlEntities.cs` beside it:
  the 2,231 HTML5 names `html.unescape()` decodes, which .NET's `WebUtility.HtmlDecode` (HTML4's, and only with their `;`) doesn't. `release_post.cs` is the entry
  file: it includes both and `Gh.cs`, writes to stderr that the `.py` still runs, and exits 2. `test.sh`'s `library_first` list holds it to exactly that instead of running
  it against the `release_post` parity suite, and fails once the list names an entry file with no `.py` beside it; T3c takes it off. None of the three is in `OWNED`: Apply
  copies them, but nothing in an Application runs them until T3c, which adds them.
- **How it reads text as the Python did.** Lengths, cuts and `ord()` count code points; `\s`, `\S` and `\w` are Python's (`str.isspace()` counts `\x1c` to `\x1f`, and
  `isalnum()` the letter and number categories); a tab expands by code point columns, starting again after a CR as `expandtabs()` does; `str.lower()` lowers U+0130 to
  two code points; `re.IGNORECASE` matches `i` to U+0130 and U+0131, `s` to U+017F and `k` to the Kelvin sign where the pattern isn't `re.ASCII`, and only ASCII cases
  where it is; `pattern.match(text, pos, endpos)` is an anchored match on the range; `date.fromisoformat()` reads Python 3.12's six forms and lengths; a file is read as
  strict UTF-8 with universal newlines and its BOM kept, as `Path.read_text()` reads it; `Path.glob()` lists hidden entries and folders too. An astral letter or digit
  is a word character to Python's `\w` and `\d` but not to .NET's, which matches UTF-16 units: in a pattern that only shapes an excerpt or a release number, that
  difference is left. A differential run of 25,000 random Markdown documents through both found no output apart, and the oracle below checks its grid each run.
- **The tests.** `test_release_post.py`'s 132 tests are copied, one method each under their names and in their order, with the same 454 cases, into
  `tests/Scripts.Tests/Template/ReleasePost/`, with seven of the port's own after them. The pytest stays until T3c, judging the `.py`; a PR that changes the `.py`'s logic
  meanwhile changes the library and its copy too, as the rule above says. (Since then, the two that time the scanning are in their own class, `ReleasePostTimingTests`,
  a collection that runs alone after the parallel tests, since the suite's other tests made their timings flaky.)
- **The render oracle** (Still open: the tests, until now). Its renderers stay what publishes the output: cmark-gfm through the `cmarkgfm` package (GitHub, the README)
  and kramdown with kramdown-parser-gfm through Ruby, a local one with the pinned gems or else Docker (GitHub Pages, the posts), at the github-pages gem's versions. No .NET
  library renders as either: Markdig is a third CommonMark reading, and a grid passing it would say nothing about GitHub or Pages. Its runner stays pytest, in the skill's
  `tests/` and never in the Template: the renderers need Python and Ruby whatever runs them, so a C# runner would add a layer and remove no toolchain, and the Baseline still
  drops Python at T4. What changes is what it judges: `tests/render_oracle/release_post.cs` includes the library and prints `RenderCommits` and `RebaseReadmeLinks` for the
  grid as JSON, so each grid case runs against both implementations, and a test holds the C#'s output to the Python's case for case. At T3c the Python leaves the grid and
  the oracle judges the C# alone. `test.sh`'s oracle step needs `dotnet` now.
- **Considered for the oracle.** Markdig in xUnit (rejected, above); binding cmark-gfm's C library from .NET (rejected: a native build the Factory would keep, and kramdown
  would still need Ruby); dropping the oracle with `release_post.py` (rejected: run against two earlier versions of it, it failed 110 and 130 cases the unit tests
  passed, as the skill's `references/release-pipeline.md` records).
- **Rollout.** None of its own: the three files reach each Application with the next Apply and run nowhere there. T3c's Rollout brings the cut-over.

## Update (mpaulosky/Application_Factory#128): the release scripts' cut-over

T3c. `release.yml`'s plan and docs jobs and `backfill-blog-posts.yml` run `release_queue`, `backfill_blog_posts` and, through it, `release_post` as C#.
`release_queue.py`, `release_post.py` and `backfill_blog_posts.py` ship beside them for this one Rollout, as `draft_release.py` does; T4 adds the four to
`retired.txt`. Their pytest files retire now.

- **The files.** `release_post.cs` is `release_post.py`'s command line on `ReleasePost.cs` (`ReleasePost.Command`): it no longer exits 2, and `test.sh`'s
  `library_first` list is empty. `release_queue.cs` includes `ReleaseQueue.cs`, and `backfill_blog_posts.cs` `BackfillBlogPosts.cs`; each also includes
  `ReleasePost.cs`, `HtmlEntities.cs` and `Gh.cs`, since both read the Releases with `ReleasePost.cs`'s gh calls and `source_pr_of()`, and the backfill writes its
  posts and tables with it. `ReleaseQueue.cs`'s `QueueGitHub` adds the closed-PR listing to those calls, with the page call virtual, so a test pages over canned
  data as the pytest overrode `_closed_pulls_page`. Each entry file reads the environment through a lookup it is handed, so a test sets a variable without
  touching the process's. The seven files join `OWNED` and `owned-paths.test.mts`, as the workflows run them with their tokens.
- **A digit is Python's again where a number is read.** The Update for mpaulosky/Application_Factory#164 left .NET's `\d` in the patterns that read a release
  number. Once the planner reads the markers with `ReleasePost.SourcePrOf`, that is no longer safe: a `Source PR: #𝟒𝟐` marker named PR 42 to `draft_release.cs`
  and to Python's `source_pr_of()`, and no PR to the C#. `SourcePrOf`, `VersionKey` and release_queue's `vX.Y.Z` test now read a run of digits as `DigitsAt`
  does (any Unicode decimal digit, astral ones too) and its value with `Py.Int`. The gap stays only where an astral letter or digit shapes an excerpt.
- **The workflows.** The plan job and the docs job each gain `setup-dotnet` and a step that builds their script, which then runs with `dotnet run --no-build --file`,
  so `PRS=$(...)` is the JSON alone on a cold build; `backfill-blog-posts.yml` does the same. A file-based app's build output is under the temporary folder, so
  the docs jobs' `git status` still sees only `README.md` and `docs/`; `test.sh` fails a build of an Owned entry file that writes anything under the Template's
  `.github/`. `CiScriptStepsTests` holds both workflows to the build-first rule, and to no step running the three `.py` files. Each release run gains about 12 s in
  each of the two jobs.
- **The tests.** `test_release_queue.py`'s 20 tests and `test_backfill_blog_posts.py`'s 18 (22 with parameters) are `ReleaseQueueTests.cs` and
  `BackfillBlogPostsTests.cs` in `tests/Scripts.Tests/Template/`, one method each, with tests of the port's own after them (the queue's stdout and stderr, the
  cutoff's tag and ties, argparse's errors; `str.isdigit()` against `int()` in `--pr-numbers`, the wrapped usage, the wait's messages).
  `ReleasePostTests.Command.cs` covers `ReleasePost.Command`, which the pytest never called. `DraftReleaseTests` asks `ReleasePost.SourcePrOf` where it ran
  `release_post.py` through `python3`. The three pytest files are deleted, and every version the Factory's history holds (`git log --full-history`, so the
  versions before the skill moved under `.claude/skills/` count) is in `retired.txt` and `RETIRED_PATHS`.
- **The parity suites.** The `release_post` (28 cases), `release_queue` (25) and `backfill_blog_posts` (25) suites pass against both the `.py` and the `.cs`,
  and `test.sh` runs each entry file against its suite while its `.py` ships.
- **The render oracle** judges the C# alone, as the Update for mpaulosky/Application_Factory#164 said it would: its grid no longer imports `release_post.py`,
  and the test that held the C#'s output to the Python's goes with it.
- **Rollout.** Re-Apply each Application: the two workflows, the seven C# files and `check_gh_repo.py` with its longer `OWNED` arrive, and Apply removes an unchanged
  copy of each retired pytest file. Before the Rollout, a `regenerate` backfill dispatch in one Application should give an empty diff. After it, the next release's
  queue and blog post are the first written by C#.

## Update (mpaulosky/Application_Factory#129): post_claude_review is C#, built from the base in the checker layout

T4a. `claude-review.yml`'s post job runs `post_claude_review.cs`. The job holds a token that can post a review under the marker, so it runs the base's copy,
never the PR's (the skill's ADR 0004, which has the same Update), and the file takes the layout this ADR gave only to the two untrusted-tree checkers.

- **The file is a checker in its layout.** `post_claude_review.cs` is one file that includes nothing and uses the BCL only, with its own copies of `Gh.cs`'s
  classes in the namespace `Baseline.PostReview` (a Factory test holds them equal to `Gh.cs`'s). `test.sh` builds it alone in an empty folder, refuses it
  unless `#:` appears in it once, as its first line `#:property PublishAot=false` (so no `#:include`, `#:package`, `#:project`, `#:sdk` or other
  `#:property`, nor a `#:` in a comment or a string), and runs its parity suite against the built dll.
- **How the job builds it** (Still open: the checkers' cut-over, for this file). A step writes the base's `global.json` to `$RUNNER_TEMP/base-sdk/` when the
  base has the `.cs`, and Setup .NET installs the SDK it pins. The post step copies the base's `.cs` and `global.json` into a `mktemp -d` folder, runs
  `dotnet build` there with that folder as the cwd, and runs `dotnet <dll>`, so no SDK command reads the PR's tree. A base `.cs` without a `global.json`, or
  one that doesn't build, fails the job. The build runs without the job's token and the answer (`GH_TOKEN`, `FINDINGS`, `REDACTED` unset): only the dll
  that posts gets them. The job's timeout went from 5 to 10 minutes for the SDK install and a cold build. The two checkers' own cut-over can take the
  same steps; where their `OWNED` list lives is still open.
- **The PR that replaces the script is posted by the one it replaces.** While a base has no `.cs`, the job runs the base's `post_claude_review.py` with
  `python3 -I`, as before. So each Application's re-Apply PR in this Rollout, and this Factory PR, is posted by the `.py`, and the PR after it is the first
  that C# posts. The `.py` ships beside the `.cs` for this one Rollout; T5 retires it and the job's fallback together.
- **The tests.** The pytest file's tests of the script are `PostClaudeReviewTests` in `tests/Scripts.Tests/Template/PostClaudeReview/`, one method each, with
  the port's own in `PostClaudeReviewTests.Port.cs`. The `post_claude_review` parity suite (78 cases) passes against the `.py`, against `dotnet run` of the
  `.cs` and against the dll built alone. The post job's steps are tested against a real build in `Bench/BenchPostStepTests.cs`. The pytest file stays while
  the `.py` ships: its post-step tests keep the `.py` path, and the heredoc tests move with the heredocs (mpaulosky/Application_Factory#165,
  mpaulosky/Application_Factory#166).
- **A test's bash reads no startup file.** bash reads `~/.bashrc` even for `bash -c` when its stdin is a socket, as a remote shell daemon's is, and a test
  host's stdin can be one. The tests that run a workflow step give it an empty stdin, so a maintainer's own `~/.bashrc` can't fail the step.
- **The release scripts' `.py` files**, which the Update for mpaulosky/Application_Factory#128 left for T4 to add to `retired.txt`, retire in a pull request of
  their own after this one: with them, this one would pass the size line (about 2,400 lines of them).
- **Rollout.** Re-Apply each Application: `claude-review.yml`, `pr-automerge.yml` (a comment), `post_claude_review.cs`, and the comments and a test of
  `post_claude_review.py`, `check_gh_repo.py` and `test_post_claude_review.py` arrive. The
  re-Apply PR's review is posted by the base's `.py`; check that the next PR's review is posted, the first by C#. The post job gains Setup .NET and a build.
  The Factory's copies (`claude-review.yml`, `pr-automerge.yml`, and `post_claude_review.cs`, a new copy) arrive with `scripts/gate.sh --sync`.

## Update (mpaulosky/Application_Factory#220): a new Factory script's logic is C#, and a library a bash script sources stays bash

The step timings add the first script this ADR didn't port: one that is new. Its two parts take the two sides of this ADR's line.

- **The summary is C#.** `scripts/gate-timings.cs`, with its logic and contract in `scripts/GateTimings.cs` (library first, as the Update for
  mpaulosky/Application_Factory#157 has it), reads the log and writes its table: medians, the halves' change, the counts of skipped steps and malformed
  lines. That is the logic the ported scripts held, and it was first an awk pipeline. `scripts/gate-timings.sh` runs it from `scripts/`, beside three empty
  shield files, as `status.sh` runs `status.cs`, and passes the caller's folder in `GATE_TIMINGS_CALLER_DIR`, so a relative `--log` and the default log
  are the caller's. `tests/Scripts.Tests/GateTimings/` tests `GateTimings.cs`, which the test project compiles from the Factory's `scripts/` (so
  `test.sh`'s copies of the project carry it); `scripts/tests/timings.test.sh` runs the wrapper end to end.
- **The library stays bash.** `scripts/lib/timings.sh` is sourced by `scripts/gate.sh` and `test.sh` and marks each step from inside them: a C# app can't, and
  a process per step boundary would add to the time it measures. It is "the bash itself" this ADR leaves as it is.

A new Factory script whose logic outgrows a few lines of shell is C#, then, and shell that has to run inside a bash script stays shell.
