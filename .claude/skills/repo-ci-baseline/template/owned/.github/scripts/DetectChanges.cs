// Whether a pull request changes code, or only docs: detect_changes.cs's logic, a port of detect_changes.py (ADR 0017 in the Application
// Factory). The contract is detect_changes.cs's header.
namespace Baseline;

static class DetectChanges
{
	// The usage line argparse printed for detect_changes.py, the options in its order.
	const string Usage = "[-h] --base BASE [--head HEAD] --output OUTPUT [--author AUTHOR] [--head-ref HEAD_REF]";

	// Whether a changed path is documentation the build and tests don't read.
	public static bool IsDoc(string path) => path.StartsWith("docs/", StringComparison.Ordinal) || path.EndsWith(".md", StringComparison.Ordinal);

	// Every path that differs between base and head, a rename under both its paths, in git's order. Read as bytes and decoded as Python's
	// surrogateescape decodes them, so a "\r" in a name stays one and a name that isn't UTF-8 is printed back as its own bytes.
	public static List<string> ChangedPaths(string @base, string head = "HEAD", string? cwd = null)
	{
		var output = SurrogateEscape.Decode(Script.CheckOutput(["git", "diff", "--name-only", "--no-renames", "-z", @base, head], cwd));
		return [.. output.Split('\0').Where(path => path.Length > 0)];
	}

	// The first path that isn't docs, or null when every path is.
	public static string? FirstCodePath(IEnumerable<string> paths) => paths.FirstOrDefault(path => !IsDoc(path));

	// The path with its line breaks escaped. Git allows them in file names, and a raw one would let the rest of the name start a workflow
	// command on the next log line (such as "::error::").
	public static string ForLog(string path) => path.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

	// Whether the build and tests must run: any code path, or no paths at all.
	public static bool IsCodeChange(IReadOnlyCollection<string> paths) => paths.Count == 0 || FirstCodePath(paths) is not null;

	// Whether this is a Dependabot GitHub Actions bump that changes only workflows and actions.
	public static bool IsActionsBump(IReadOnlyCollection<string> paths, string author, string headRef) =>
			author == "dependabot[bot]"
			&& headRef.StartsWith("dependabot/github_actions/", StringComparison.Ordinal)
			&& paths.Count > 0
			&& paths.All(path => path.StartsWith(".github/workflows/", StringComparison.Ordinal) || path.StartsWith(".github/actions/", StringComparison.Ordinal));

	public static int Run(IEnumerable<string> args, TextWriter stdout, TextWriter stderr)
	{
		var parser = new Arguments("detect_changes.cs", Usage, "Decide whether a pull request changes code, or only docs.")
				.Required("--base", "the PR's base commit")
				.Option("--head", "the PR merged into its base")
				.Required("--output", "the file to append code=true|false to")
				.Option("--author", "the PR author's login")
				.Option("--head-ref", "the PR's head branch");
		if (parser.Parse(args, stdout, stderr) is int exit)
			return exit;

		var paths = ChangedPaths(parser.Value("--base", ""), parser.Value("--head", "HEAD"));
		var code = IsCodeChange(paths);
		var skipReason = code ? "" : "docs-only";
		if (code && IsActionsBump(paths, parser.Value("--author", ""), parser.Value("--head-ref", "")))
		{
			(code, skipReason) = (false, "actions-bump");
			Script.Write(stdout, $"::notice::Dependabot GitHub Actions bump ({paths.Count} files); the build, tests and coverage are skipped.\n");
		}
		else if (code && paths.Count > 0)
			Script.Write(stdout, $"Code change: {ForLog(FirstCodePath(paths)!)}\n");
		else if (paths.Count == 0)
			Script.Write(stdout, "No changed files; running the build and tests.\n");
		else
			Script.Write(stdout, $"::notice::Docs-only change ({paths.Count} files); the build, tests and coverage are skipped.\n");

		// skip_reason tells the summary which skip it was; code alone can't.
		File.AppendAllText(parser.Value("--output", ""), $"code={(code ? "true" : "false")}\nskip_reason={skipReason}\n");
		return 0;
	}
}
