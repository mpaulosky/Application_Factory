#:include Gh.cs
#:include DetectChanges.cs
#:property PublishAot=false
// Decide whether a pull request changes code, or only docs. DetectChanges.cs holds the logic; Gh.cs the argument parser and Script.Run.
//
// Called by the "Detect Changes" job in .github/workflows/ci.yml on a pull request, from a full-history checkout of the PR merged into its
// base, after a step that builds it (dotnet build .github/scripts/detect_changes.cs):
//
//     dotnet run --no-build --file .github/scripts/detect_changes.cs -- --base <base sha> --output "$GITHUB_OUTPUT"
//
// It appends "code=true" or "code=false" to the output file, and skip_reason: "docs-only", "actions-bump", or empty when the build runs. A
// PR is docs-only when every changed path is under docs/ or ends in .md; the build, test matrix and coverage are then skipped. Everything
// else counts as code, including workflows, scripts and an empty diff, so an unexpected path runs the suite.
//
// A Dependabot GitHub Actions bump skips them too: it only moves pinned action SHAs, which the build and tests never read, while every other
// check still runs. All three must hold: the PR's author is dependabot[bot] (--author), its branch is dependabot/github_actions/...
// (--head-ref), and every changed path is a workflow or a local action. A NuGet or SDK bump changes what's built, so it always builds.
//
// Diffing HEAD (--head) against the base the event names can also pick up commits that reached main since, which only ever adds paths, so it
// errs toward building. --no-renames lists a renamed file under both its paths, so moving code into docs/ or to a .md name still counts as a
// code change. It prints one line: the first code path (its line breaks written as \r and \n, so a name can't start a workflow command), or a
// notice of the skip with the number of changed files, or that there were none. A name that isn't UTF-8 is printed as its own bytes.
//
// Exit 0 once the output is appended; 2 on a usage error (argparse's, as detect_changes.py gave: --base and --output are required, an option
// may be given by a unique prefix of its name); 1 when git fails (an unknown base, say: the error names the command, its exit status and
// what git said) or the output can't be appended. The standard library only; git, found in PATH's absolute folders only, does the rest.
// detect_changes.py, beside it, is the same script in Python, kept for one Rollout so a revert is one workflow line.
using Baseline;

return Script.Run((stdout, stderr) => DetectChanges.Run(args, stdout, stderr));
