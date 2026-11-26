#:include Gh.cs
#:include DiscoverTests.cs
#:property PublishAot=false
// List the test projects under tests/ as the CI test matrix. DiscoverTests.cs holds the logic; Gh.cs the argument parser and Script.Run.
//
// Called by the "Discover Test Projects" job in .github/workflows/ci.yml, after a step that builds it
// (dotnet build .github/scripts/discover_tests.cs):
//
//     dotnet run --no-build --file .github/scripts/discover_tests.cs -- --output "$GITHUB_OUTPUT"
//
// It appends the matrix ({"include": [...]}, one entry per test project, as json.dumps wrote it for discover_tests.py) and
// has_tests=true|false to the output file. scripts/gate.sh builds it and runs it with --list instead, which prints each test project's path,
// one per line, so the pre-push gate tests the same projects CI does. --list prints nothing else on stdout: its messages go to stderr.
//
// A project is a test project when IsTestProject resolves to true, read the way MSBuild imports it: the .csproj's own value wins, then the
// nearest Directory.Build.props above it (atelier-store sets it once in tests/Directory.Build.props). MSBuild imports only that nearest
// file, so a parent one counts only when the nearest imports it (the usual GetPathOfFileAbove('Directory.Build.props', ...) line). Helper
// libraries such as IssueTracker's TestingSupport.Library set neither, so they're skipped.
//
// A Condition on IsTestProject or its PropertyGroup is evaluated against the project's name when it has one of the shapes the repos use,
// optionally negated with !: $(MSBuildProjectName.Contains('x')), .StartsWith('x'), .EndsWith('x'), or '$(MSBuildProjectName)' == 'x'
// (or !=). Blazor-Server sets it in its root Directory.Build.props only where $(MSBuildProjectName.Contains('.Tests')), so a helper library
// under tests/ stays out. Any other Condition is logged as a notice (on stderr with --list) and counts as holding.
//
// The projects are every name ending in .csproj under tests/, in pathlib's sorted order (by path part, so tests/a/x.csproj comes before
// tests/a-b/x.csproj); a symlinked folder is not searched. A repo with no projects under tests/ gets has_tests=false, so the test matrix
// is skipped while it's bootstrapped. Projects that are all skipped fail the job instead: "Test Suite" passes a skipped matrix, so a
// discovery miss would otherwise pass every PR with no tests run.
//
// Exit 0 once the matrix is appended or the paths printed; 1 when projects exist but none is a test project, or a project file isn't XML
// or the output can't be appended (the exception on stderr); 2 on a usage error (argparse's, as discover_tests.py gave: exactly one of
// --output and --list, an option may be given by a unique prefix of its name). The standard library only.
using Baseline;

return Script.Run((stdout, stderr) => DiscoverTests.Run(args, stdout, stderr));
