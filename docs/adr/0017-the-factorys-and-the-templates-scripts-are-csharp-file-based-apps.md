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
  - The Template's Owned `global.json` pins `11.0.100` with `rollForward: latestPatch`, and it joins `OWNED`. The Seed Dockerfile installs the SDK `global.json` names.
  - The Factory owns the bump: its Dependabot gains a `dotnet-sdk` entry that lists both the Factory's root and the Template's `template/owned/`, with a group over both
    folders, as its `github-actions` entry has. So one PR bumps the Template's `global.json` and the Factory's copy together, and the gate's drift check holds. An update the
    group leaves out arrives as one PR per folder, and the Template's is merged after `scripts/gate.sh --sync` brings the copy with it.
  - **Update (mpaulosky/Application_Factory#103):** the pin landed in two steps. The `dotnet-sdk` entry lists only `template/owned/` until the Factory has its own
    `global.json`; the increment that brings it (mpaulosky/Application_Factory#117) adds the root and the group. The Seed Dockerfile can't read `global.json`:
    Sandcastle builds the image with `.sandcastle/` as its context. So it installs the pin's feature band (`DOTNET_CHANNEL=11.0.1xx`), whose newest patch
    `rollForward: latestPatch` accepts, and `test.sh` holds the channel to the pin's band. A Dependabot PR that moves the band fails `test.sh` until someone changes the
    channel in the same PR.
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
  exit code and the `gh` calls, and runs against the Python and the C# alike (`tests/status-parity.test.sh <command...>`). It needed no change to judge the trial's port.
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
- The white-box tests in xUnit v3: when each pytest suite is replaced, where the test projects live, and which package reads YAML for the tests that check workflows, since the BCL
  has none. The Factory's Python test helpers move with them: `tests/check-issue-refs.py` and `tests/check-git-status.py` (which the gate and `test.sh` run),
  `tests/mutations/check-spec.py` and `tests/render_oracle/`.
- The checkers' cut-over: how the workflow steps build them in a temp folder with the base's `global.json`, where the `OWNED` list lives once `check_owned_files` can't import
  `check_gh_repo`, which checker judges the PR that replaces the checker, and for how many Applies both names are honoured.
- `check_central_pins`. CI runs the PR's own copy, from the PR's checkout, in the job that then builds the PR. So it runs nothing that job doesn't already run, and needs no
  checker layout. But as C# it needs the SDK set up before it, where today it runs first, on the runner's `python3`, so a bad pin fails fast.
- The Python heredocs in `claude-review.yml`, and the `python3` calls inside the bash (`lint-edited-file.sh`, the test scripts, the Seed Dockerfile), likely `jq` where a hook can't
  pay a `dotnet` start.
- Mutation testing: `mutate.py` and its specs, which mutate bash too.
- Retiring Python from the Baseline: Apply never deletes a file, so how Apply or a Rollout removes retired Owned files, and how `OWNED` and the leftovers report treat them.

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
