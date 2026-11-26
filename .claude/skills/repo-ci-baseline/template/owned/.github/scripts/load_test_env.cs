#:include Gh.cs
#:include LoadTestEnv.cs
#:property PublishAot=false
// Export the repo's optional TEST_ENV secret to the test steps. LoadTestEnv.cs holds the logic; Gh.cs the argument parser and Script.Run.
//
// Called by each "Tests: <name>" job in .github/workflows/ci.yml, after a step without the secret that builds it
// (dotnet build .github/scripts/load_test_env.cs), with the secret in the TEST_ENV environment variable:
//
//     dotnet run --no-build --file .github/scripts/load_test_env.cs -- --output "$GITHUB_ENV"
//
// TEST_ENV holds one NAME=value line per variable the test fixtures need, such as MongoDB__ConnectionString=mongodb+srv://... Lines are
// split as Python's str.splitlines() splits them, and each is stripped of whitespace, as are its name and value. Blank lines and lines
// starting with "#" are skipped. Each non-empty value is masked in the log (::add-mask::, escaped as the runner decodes it) before it's
// appended to the output file, so later steps (prepare.sh and the test run) see it and the log never does.
//
// ci.yml is Owned by the Template, so it can't name a repo's own secrets. One fixed secret holding all of them keeps per-repo values out
// of it, and unlike secrets[<dynamic name>] it doesn't send every repo secret to the runner. An unset secret (any repo without one, and
// every Dependabot run) exports nothing, and the output file isn't touched.
//
// Values are single-line: GITHUB_ENV's NAME=value form can't hold a newline. A malformed line (no =, a name that isn't
// [A-Za-z_][A-Za-z0-9_]*, a GITHUB_ or RUNNER_ name, or NODE_OPTIONS in any case) fails the job naming its line number, without printing
// any value.
//
// Exit 0 once the lines are appended, or with nothing to export; 1 on a malformed secret, or an output that can't be appended (the
// exception on stderr); 2 on a usage error (argparse's, as load_test_env.py gave: --output is required). The standard library only.
using Baseline;

return Script.Run((stdout, stderr) => LoadTestEnv.Run(args, stdout, stderr));
