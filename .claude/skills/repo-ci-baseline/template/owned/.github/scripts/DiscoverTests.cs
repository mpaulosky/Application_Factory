// The test projects under tests/ as the CI test matrix: discover_tests.cs's logic, a port of discover_tests.py (ADR 0017 in the
// Application Factory). The contract is discover_tests.cs's header.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Baseline;

// One test project's matrix entry, its fields in the order the matrix's JSON gives them.
sealed record MatrixEntry(string ProjectPath, string TestName, string TestId, bool BuildBeforeTest, bool NoBuild, bool RequiresPlaywright, string Verbosity);

static class DiscoverTests
{
	// The usage line argparse printed for discover_tests.py, the options in its order.
	const string Usage = "[-h] [--root ROOT] (--output OUTPUT | --list)";

	// An Import of the next Directory.Build.props up, such as
	// $([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../')).
	static readonly Regex ImportsParentProps = new(@"<Import\b[^>]*Directory\.Build\.props", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	static readonly Regex NameMethod = new(@"\A(!?)\$\(MSBuildProjectName\.(Contains|StartsWith|EndsWith)\('([^']*)'\)\)\z", RegexOptions.CultureInvariant);
	static readonly Regex NameComparison = new(@"\A'\$\(MSBuildProjectName\)'\s*(==|!=)\s*'([^']*)'\z", RegexOptions.CultureInvariant);

	// Whether an MSBuild Condition holds for project name, or null for a shape this doesn't evaluate.
	public static bool? ConditionHolds(string condition, string name)
	{
		condition = Py.Strip(condition);
		if (NameMethod.Match(condition) is { Success: true } method)
		{
			var text = method.Groups[3].Value;
			var holds = method.Groups[2].Value switch
			{
				"Contains" => name.Contains(text, StringComparison.Ordinal),
				"StartsWith" => name.StartsWith(text, StringComparison.Ordinal),
				_ => name.EndsWith(text, StringComparison.Ordinal),
			};
			return holds != (method.Groups[1].Value == "!");
		}
		// MSBuild's == and != compare strings case-insensitively.
		if (NameComparison.Match(condition) is { Success: true } comparison)
			return (Py.Lower(name) == Py.Lower(comparison.Groups[2].Value)) == (comparison.Groups[1].Value == "==");
		return null;
	}

	// The element's tag without its namespace, lowercased: MSBuild property names ignore case.
	static string LocalName(XElement element) => Py.Lower(element.Name.LocalName);

	// ElementTree's element.text: the character data before the element's first child, the comments and processing instructions in it
	// dropped, as its parser drops them.
	static string Text(XElement element) =>
			string.Concat(element.Nodes().TakeWhile(node => node is not XElement).OfType<XText>().Select(text => text.Value));

	// Whether element's Condition, if any, holds for project name; one this can't evaluate counts as holding, and is logged.
	static bool Applies(XElement element, string name, string path, TextWriter log)
	{
		if (element.Attribute("Condition")?.Value is not { } condition)
			return true;
		if (ConditionHolds(condition, name) is { } holds)
			return holds;
		Script.Write(log, $"::notice::{name}: can't evaluate Condition \"{condition}\" on IsTestProject in {path}; counting it as holding\n");
		return true;
	}

	// A file's text as UTF-8, a byte that isn't UTF-8 read as U+FFFD, and with sig a leading byte order mark dropped (Python's utf-8-sig).
	static string ReadText(string path, bool sig = false)
	{
		var bytes = File.ReadAllBytes(path).AsSpan();
		if (sig && bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
			bytes = bytes[3..];
		return new UTF8Encoding(false, false).GetString(bytes);
	}

	// The IsTestProject value a file sets for project name (its last one that applies), or null if it sets none.
	public static bool? DeclaredIsTestProject(string path, string name, TextWriter log)
	{
		var project = XElement.Parse(ReadText(path, sig: true), LoadOptions.PreserveWhitespace);
		bool? value = null;
		foreach (var group in project.Elements())
		{
			if (LocalName(group) != "propertygroup")
				continue;
			foreach (var element in group.Elements())
			{
				var text = Py.Lower(Py.Strip(Text(element)));
				if (LocalName(element) != "istestproject" || text is not ("true" or "false"))
					continue;
				if (Applies(group, name, path, log) && Applies(element, name, path, log))
					value = text == "true";
			}
		}
		return value;
	}

	// The nearest Directory.Build.props in folder or above it, up to root, or null.
	static string? NearestProps(string folder, string root)
	{
		while (true)
		{
			var props = Path.Join(folder, "Directory.Build.props");
			if (File.Exists(props))
				return props;
			if (folder == root || Path.GetDirectoryName(folder) is not { } parent)
				return null;
			folder = parent;
		}
	}

	// pathlib's .stem of a name ending in .csproj: the name without it, or the whole name when that is all it is.
	static string Stem(string name) => name == ".csproj" ? name : name[..^".csproj".Length];

	// Whether IsTestProject resolves to true for this project, as discover_tests.cs's header describes.
	public static bool IsTestProject(string csproj, string root, TextWriter log)
	{
		var name = Stem(Path.GetFileName(csproj));
		if (DeclaredIsTestProject(csproj, name, log) is { } own)
			return own;
		var props = NearestProps(Path.GetDirectoryName(csproj)!, root);
		while (props is not null)
		{
			if (DeclaredIsTestProject(props, name, log) is { } value)
				return value;
			var folder = Path.GetDirectoryName(props)!;
			if (folder == root || !ImportsParentProps.IsMatch(ReadText(props)))
				return false;
			props = NearestProps(Path.GetDirectoryName(folder) ?? folder, root);
		}
		return false;
	}

	// The matrix entry for one test project.
	public static MatrixEntry Entry(string csproj, string root)
	{
		var folder = Path.GetFileName(Path.GetDirectoryName(csproj)!);
		var lowered = Py.Lower(folder);
		var isE2e = lowered.Contains("e2e", StringComparison.Ordinal);
		var usesPlaywright = ReadText(csproj).Contains("Microsoft.Playwright", StringComparison.Ordinal);
		// Playwright projects must be built before browsers can be installed.
		var prebuild = isE2e || usesPlaywright;
		return new MatrixEntry(csproj[(root.EndsWith('/') ? root.Length : root.Length + 1)..], folder,
				lowered.Replace('.', '-').Replace('_', '-'), prebuild, prebuild, usesPlaywright, isE2e ? "normal" : "minimal");
	}

	// pathlib's folder.glob("**/*.csproj"): every entry whose name ends in .csproj in folder and each folder under it, a symlink to a
	// folder searched in as a name but never descended into, and a folder that can't be listed skipped.
	static IEnumerable<string> Csprojs(string folder)
	{
		FileSystemInfo[] entries;
		try
		{
			entries = new DirectoryInfo(folder).GetFileSystemInfos();
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			yield break;
		}
		foreach (var entry in entries)
		{
			if (entry.Name.EndsWith(".csproj", StringComparison.Ordinal))
				yield return Path.Join(folder, entry.Name);
		}
		foreach (var entry in entries)
		{
			if (entry is DirectoryInfo && entry.LinkTarget is null)
			{
				foreach (var csproj in Csprojs(Path.Join(folder, entry.Name)))
					yield return csproj;
			}
		}
	}

	// (every .csproj under tests/, the matrix entries of the test projects among them), the projects in pathlib's sorted order.
	public static (List<string> Projects, List<MatrixEntry> Entries) Discover(string root, TextWriter log)
	{
		root = PyPath.Resolve(root);
		var tests = Path.Join(root, "tests");
		List<string> projects = Directory.Exists(tests) ? [.. Csprojs(tests).Order(PyPath.Order)] : [];
		return (projects, [.. projects.Where(csproj => IsTestProject(csproj, root, log)).Select(csproj => Entry(csproj, root))]);
	}

	// json.dumps({"include": entries}): its ", " and ": " separators, and every character outside printable ASCII escaped as \uXXXX.
	public static string Matrix(IEnumerable<MatrixEntry> entries)
	{
		static string Str(string text)
		{
			var json = new StringBuilder("\"");
			foreach (var c in text)
			{
				json.Append(c switch
				{
					'"' => "\\\"",
					'\\' => @"\\",
					'\n' => @"\n",
					'\r' => @"\r",
					'\t' => @"\t",
					'\b' => @"\b",
					'\f' => @"\f",
					>= ' ' and <= '~' => c.ToString(),
					_ => $@"\u{((int)c).ToString("x4", CultureInfo.InvariantCulture)}",
				});
			}
			return json.Append('"').ToString();
		}
		static string Bool(bool value) => value ? "true" : "false";
		var items = entries.Select(entry => $"{{\"project_path\": {Str(entry.ProjectPath)}, \"test_name\": {Str(entry.TestName)}, \"test_id\": {Str(entry.TestId)}, " +
				$"\"build_before_test\": {Bool(entry.BuildBeforeTest)}, \"no_build\": {Bool(entry.NoBuild)}, \"requires_playwright\": {Bool(entry.RequiresPlaywright)}, " +
				$"\"verbosity\": {Str(entry.Verbosity)}}}");
		return $"{{\"include\": [{string.Join(", ", items)}]}}";
	}

	// The script.
	public static int Run(IEnumerable<string> args, TextWriter stdout, TextWriter stderr)
	{
		var parser = new Arguments("discover_tests.cs", Usage, "List the test projects under tests/ as the CI test matrix.")
				.Option("--root", "the repo root")
				.Option("--output", "the file to append matrix and has_tests to")
				.Flag("--list", "print each test project's path, one per line")
				.OneOf("--output", "--list");
		if (parser.Parse(args, stdout, stderr) is int exit)
			return exit;
		// pathlib reads an empty path as the current folder.
		var root = parser.Value("--root", ".") is { Length: > 0 } given ? given : ".";
		const string NoneMarked = "projects under tests/ resolves IsTestProject to true. Set it in each test .csproj or in a Directory.Build.props above them.";

		if (parser.Has("--list"))
		{
			// Only the paths go to stdout, so the gate can read them; messages go to stderr.
			var (all, found) = Discover(root, stderr);
			if (all.Count > 0 && found.Count == 0)
			{
				Script.Write(stderr, $"None of the {all.Count} {NoneMarked}\n");
				return 1;
			}
			foreach (var entry in found)
				Script.Write(stdout, $"{entry.ProjectPath}\n");
			return 0;
		}

		var (projects, entries) = Discover(root, stdout);
		if (projects.Count > 0 && entries.Count == 0)
		{
			Script.Write(stdout, $"::error::None of the {projects.Count} {NoneMarked}\n");
			return 1;
		}
		// No entries is expected while the repo is being bootstrapped.
		Script.Write(stdout, entries.Count > 0 ? $"::notice::Discovered {entries.Count} test projects under tests/\n" : "::warning::No test projects found under tests/ — skipping tests.\n");

		// The matrix is one line, so it can't end the heredoc early.
		File.AppendAllText(parser.Value("--output", ""), $"matrix<<EOF\n{Matrix(entries)}\nEOF\nhas_tests={(entries.Count > 0 ? "true" : "false")}\n");
		return 0;
	}
}
