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
  Update for mpaulosky/Application_Factory#119; where the Template's own scripts' tests go is still open), and which package reads YAML for the tests that check workflows, since the BCL
  has none. The Factory's Python test helpers move with them: `tests/check-issue-refs.py` and `tests/check-git-status.py` (which the gate and `test.sh` run),
  `tests/mutations/check-spec.py` and `tests/render_oracle/`.
- The checkers' cut-over: how the workflow steps build them in a temp folder with the base's `global.json`, where the `OWNED` list lives once `check_owned_files` can't import
  `check_gh_repo`, which checker judges the PR that replaces the checker, and for how many Applies both names are honoured.
- `check_central_pins`. CI runs the PR's own copy, from the PR's checkout, in the job that then builds the PR. So it runs nothing that job doesn't already run, and needs no
  checker layout. But as C# it needs the SDK set up before it, where today it runs first, on the runner's `python3`, so a bad pin fails fast.
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
- The skill's [ADR 0003](../../.claude/skills/repo-ci-baseline/docs/adr/0003-github-settings-as-code.md) is unchanged: the settings stay data in the skill, read by `Settings.cs`
  once `settings.py` goes.
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
  (The Update for mpaulosky/Application_Factory#145 below adds NU1900 to `WarningsNotAsErrors`.)

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
