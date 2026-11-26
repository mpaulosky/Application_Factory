// Export the repo's optional TEST_ENV secret to the test steps: load_test_env.cs's logic, a port of load_test_env.py (ADR 0017 in the
// Application Factory). The contract is load_test_env.cs's header.
using System.Text.RegularExpressions;

namespace Baseline;

static class LoadTestEnv
{
	// The usage line argparse printed for load_test_env.py.
	const string Usage = "[-h] --output OUTPUT";

	static readonly Regex Name = new(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

	// The (name, value) pairs in TEST_ENV's text; a FormatException names the bad line's number, never its text.
	public static List<(string Name, string Value)> Parse(string text)
	{
		List<(string, string)> pairs = [];
		var number = 0;
		foreach (var raw in Py.SplitLines(text))
		{
			number++;
			var line = Py.Strip(raw);
			if (line.Length == 0 || line.StartsWith('#'))
				continue;
			var separator = line.IndexOf('=');
			var name = Py.Strip(separator < 0 ? line : line[..separator]);
			if (separator < 0 || !Name.IsMatch(name))
				throw new FormatException($"line {number} isn't NAME=value");
			if (name.StartsWith("GITHUB_", StringComparison.Ordinal) || name.StartsWith("RUNNER_", StringComparison.Ordinal))
				throw new FormatException($"line {number} sets a reserved {name.Split('_')[0]}_ variable");
			// The runner's SetEnvFileCommand drops this one name, in any case, from $GITHUB_ENV with only an error in the log, so refuse it
			// here. The name is ASCII, so an ordinal comparison ignoring case is Python's name.upper() == "NODE_OPTIONS".
			if (name.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase))
				throw new FormatException($"line {number} sets NODE_OPTIONS, which the runner won't export; set it in a step's env instead");
			pairs.Add((name, Py.Strip(line[(separator + 1)..])));
		}
		return pairs;
	}

	// A value escaped for a workflow command's data, as the runner decodes it. The runner percent-decodes the data of "::add-mask::", so a
	// literal "%25" in a secret would otherwise register "%" and leave the real value unmasked.
	public static string EscapeData(string value) =>
			value.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

	// The script, reading TEST_ENV from environ (a test hands in its own), else the process's environment.
	public static int Run(IEnumerable<string> args, TextWriter stdout, TextWriter stderr, Func<string, string?>? environ = null)
	{
		var parser = new Arguments("load_test_env.cs", Usage, "Export the repo's optional TEST_ENV secret to the test steps.")
				.Required("--output", "the file to append NAME=value lines to ($GITHUB_ENV)");
		if (parser.Parse(args, stdout, stderr) is int exit)
			return exit;

		List<(string Name, string Value)> pairs;
		try
		{
			pairs = Parse((environ ?? Environment.GetEnvironmentVariable)("TEST_ENV") ?? "");
		}
		catch (FormatException error)
		{
			Script.Write(stdout, $"::error::The TEST_ENV secret is malformed: {error.Message}.\n");
			return 1;
		}
		if (pairs.Count == 0)
		{
			Script.Write(stdout, "No TEST_ENV secret; no test secrets exported.\n");
			return 0;
		}

		// "::add-mask::" with nothing after it would be a malformed command, so an empty value isn't masked.
		foreach (var (_, value) in pairs.Where(pair => pair.Value.Length > 0))
			Script.Write(stdout, $"::add-mask::{EscapeData(value)}\n");
		File.AppendAllText(parser.Value("--output", ""), string.Concat(pairs.Select(pair => $"{pair.Name}={pair.Value}\n")));
		Script.Write(stdout, $"Exported {pairs.Count} test secrets: {string.Join(", ", pairs.Select(pair => pair.Name))}\n");
		return 0;
	}
}
