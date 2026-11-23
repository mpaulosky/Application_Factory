# Trial: status.py ported to a C# file-based app

Research note, 2026-11-22, for [Trial: port status.py to a C# file-based app](https://github.com/mpaulosky/Application_Factory/issues/97).
It decides nothing: the ADR on map #93 does. Facts marked **[V]** are verified, by a command run here or in CI. Statements marked **[I]** are inference.

The trial is on the throwaway branch `research/status-cs-trial`, which is never merged:

- `.claude/skills/repo-ci-baseline/github/status.cs` ports `status.py`, with `settings.read_repo_list`.
- `github/Landing.cs` ports all of `landing.py`, and `status.cs` pulls it in with `#:include`.
- `github/` holds the three empty shield files (#101).
- `tests/status-cs-trial-measure.sh` does the timing.
- A job in `template-tests.yml` runs only on `workflow_dispatch`.

The Python is untouched. `status.sh` and `land.py` still run it, so `land.py` keeps working during the trial.

## Verdict against the criteria

| Criterion | Threshold (agreed on the ticket) | Result |
| --- | --- | --- |
| Speed, warm | at most 100 ms slower than `status.py` | **[V]** It passes. With 2 `gh` calls: 78 ms against 61 ms locally, and 69 against 66 in CI. With 5 calls: 144 against 72 locally, and 135 against 82 in CI. |
| Speed, cold | under 5 s, with an empty NuGet cache and no network | **[V]** It passes locally, at 1.2 to 1.3 s, but only with `#:property PublishAot=false` (see the first finding). A fresh CI runner's first build took 12.3 s. |
| Parity | the parity suite passes against both | **[V]** It passes: 19 of 19 cases, locally and in CI, against the port and against `status.py`. |
| CI | a Factory job with `setup-dotnet` runs it green | **[V]** It passes: the run is [45119013394](https://github.com/mpaulosky/Application_Factory/actions/runs/45119013394), job `Trial - status.cs`, on SDK 11.0.100. |
| Size | at most 1.5 times the Python's code lines | **[V]** It passes: 684 lines (`status.cs` 435, `Landing.cs` 249) against 955 lines of Python, which is 0.72 times. |

The size counts non-blank, non-comment lines. On the C# side it leaves out a line that holds only braces or punctuation. The Python's 955 lines are `status.py` (474) plus the
part of `landing.py` that `status.py` uses (481 lines across 50 names). All of `landing.py` is 552.

## Findings

1. **[V]** `#:property PublishAot=false` is required, not optional.
   - A file-based app defaults to `PublishAot=true`, so its restore fetches `microsoft.dotnet.ilcompiler`, `microsoft.net.illink.tasks` and the runtime packs.
   - With an empty `NUGET_PACKAGES` and no network (`unshare -rn`), the build fails with NU1301 "Network is unreachable".
   - With the property, the same cold build takes 1.2 to 1.3 s and fetches nothing.
   - Map #93's Q7 chose `JsonNode` to avoid needing the property. That holds for JSON, but the restore needs the property anyway.
2. **[V]** The ported scripts carry no shebang.
   - Under the repository's `.editorconfig`, `dotnet format whitespace` rewrites `#!` to `# !` ("Insert '\s'" at line 1, column 2). The file then fails to build with CS9378.
   - Without the repository's `.editorconfig`, the same file isn't flagged.
   - Wrappers such as `status.sh` call `dotnet run --file`, so a shebang isn't needed.
3. **[V]** The port's warm start grows with each `gh` call.
   - It runs about 15 ms behind Python with 2 calls, and about 70 ms behind with 5.
   - A C# loop of six `Process.Start` calls spends about 12 ms on the first, then under 1 ms on each (Python: under 1 ms on each). So the first spawn is only part of the gap.
   - **[I]** The rest is first-use JIT along each new code path (regex, `JsonNode`, the GraphQL shapes).
   - A Release build, `InvariantGlobalization` and the tiering settings didn't change it.
   - A script that makes many calls, like `land.py`'s polling loop, would feel it more. Each poll pays it once per process.
4. **[V]** Running the dll directly with `dotnet <dll>` saves about 35 ms over `dotnet run --file`: 41 to 46 ms against 69 to 78 ms on the 2-call case.
5. **[V]** `dotnet format whitespace --verify-no-changes` takes:
   - about 1.0 s locally with `--folder .`, and 2.2 s in CI;
   - 1.7 s locally on one file through the file-based app.

   An empty NuGet cache doesn't change it. That's too slow for a per-edit hook (`lint-edited-file.sh` runs on every Write or Edit), which bears on the ruff replacement.
6. **[V]** A fresh CI runner's first `dotnet build` of the port took 12.3 s. That figure includes the SDK's first use on that machine.
   - **[I]** Expect about that once per job, before any parallel run.
   - Building once and then running with `--no-build` (#94) still holds.
7. **[V]** One run straight after an `unshare -rn` run took about 330 ms with `--no-build`. The next steady-state runs were back to 78 ms.
   - **[I]** It's probably a build-cache check that runs again after a run under a different user namespace. It doesn't affect CI.
8. **[V]** The parity suite needed no change to judge the port.
   - The only command given was `tests/status-parity.test.sh dotnet run --no-build --file github/status.cs --`.
   - The fake `gh` answered every one of the port's calls.

## How it was measured

- **Warm start:** `tests/status-cs-trial-measure.sh <skill dir> <case> 21` gives the median of 21 runs after one warm-up run. Both implementations run in an empty folder, with the parity suite's fake
  `gh` on `PATH`, TZ=UTC, and the case's args.
- **Cold run:** an empty `NUGET_PACKAGES` folder, the file's build cache under `~/.local/share/dotnet/runfile/` removed, and `unshare -rn dotnet run --file github/status.cs -- --bogus` from `/tmp`, three
  times.
- **Size:** a count of the non-blank, non-comment lines in each file. On the C# side, lines that hold only braces or punctuation are left out.
- **Machine:** CachyOS Linux, SDK 11.0.100, Python 3.14.10. CI runs on `ubuntu-latest` with `setup-dotnet` 11.0.100.
