using System.Numerics;
using System.Text.Json.Nodes;

namespace Baseline.Tests;

// The Template's .github/scripts/ReleasePost.cs: test_release_post.py's tests, one method each under its name (ADR 0017's rule), with the
// same cases and assertions, on throwaway folders. The pytest stays, judging release_post.py, until the cut-over (ADR 0017's library first).
// This file holds the module's helpers; the tests are in the partial class's other files, in the pytest's order. A pytest that patched
// urlopen hands RequestSummary a stand-in UrlOpen, and capsys is the log writer the methods take.
public sealed partial class ReleasePostTests : IDisposable
{
	const string Repo = "octo/demo";

	// pytest's tmp_path.
	readonly string tmp = Directory.CreateTempSubdirectory("release-post.").FullName;

	public void Dispose() => Directory.Delete(tmp, true);

	static JsonNode? J(string json) => JsonNode.Parse(json);

	static List<JsonNode?> Many(string json) => [.. J(json)!.AsArray().Select(node => node?.DeepClone())];

	// Stands in for the gh CLI wrapper with canned API data.
	sealed class FakeGitHub : IReleaseGitHub
	{
		public readonly Dictionary<long, JsonObject> Pulls = new()
		{
			[42] = new JsonObject
			{
				["number"] = 42,
				["title"] = "feat(ui): Add the \"dark\" theme",
				["body"] = "Adds a theme.",
				["html_url"] = $"https://github.com/{Repo}/pull/42",
				["merged_at"] = "2026-09-24T21:30:00Z",
			},
			[7] = new JsonObject { ["number"] = 7, ["title"] = "fix: Older change", ["body"] = "", ["html_url"] = "", ["merged_at"] = "" },
		};

		public readonly List<JsonNode?> ReleasesData;

		public FakeGitHub(List<JsonNode?>? releases = null) => ReleasesData = releases ?? Many("""
				[
				  {"tag_name": "v0.0.2", "name": "Release v0.0.2", "published_at": "2026-09-20T10:00:00Z", "body": "Source PR: #7\n"},
				  {"tag_name": "v0.0.1", "name": "Release v0.0.1", "published_at": "2026-09-19T10:00:00Z", "body": ""}
				]
				""");

		public JsonNode? Pull(long number) => Pulls[number];

		public string PullTitle(BigInteger number) =>
				number >= long.MinValue && number <= long.MaxValue && Pulls.TryGetValue((long)number, out var pull)
						? (string)pull["title"]!
						: throw new CalledProcessError(["gh"], 1, "");

		public List<JsonNode?> Commits(long number) => Many("""
				[
				  {"sha": "abcdef1234567", "commit": {"message": "feat(ui): Add theme tokens\n\nLong body."}},
				  {"sha": "1234567abcdef", "commit": {"message": "test(ui): Cover the theme switch"}}
				]
				""");

		public List<JsonNode?> Files(long number) => Many("""
				[
				  {"filename": "src/Web/Theme.cs", "additions": 10, "deletions": 2, "patch": "@@ -1 +1 @@\n-a\n+b"},
				  {"filename": "tests/Web.Tests/ThemeTests.cs", "additions": 5, "deletions": 0, "patch": "@@ +1 @@\n+t"},
				  {"filename": ".github/workflows/ci.yml", "additions": 1, "deletions": 1, "patch": "@@\n-x\n+y"},
				  {"filename": ".sandcastle/main.mts", "additions": 3, "deletions": 3, "patch": "@@\n-m\n+n"},
				  {"filename": "docs/adr/0001.md", "additions": 4, "deletions": 0, "patch": "@@\n+adr"},
				  {"filename": "package-lock.json", "additions": 900, "deletions": 800, "patch": "@@\n+lock"},
				  {"filename": "Directory.Packages.props", "additions": 1, "deletions": 0, "patch": "@@\n+pkg"}
				]
				""");

		public List<JsonNode?> Releases() => ReleasesData;
	}

	const string Readme = "# Demo\n\n## About\n\nText.\n\n## Releases\n\n<!-- RELEASES_START -->\nold\n<!-- RELEASES_END -->\n\n## License\n";

	static string Write(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
		return path;
	}

	static string Read(string path) => File.ReadAllText(path);

	static string MakeRepo(string root)
	{
		Directory.CreateDirectory(Path.Join(root, "docs", "blogs"));
		Write(Path.Join(root, "README.md"), Readme);
		// A repo that publishes its README as the Pages landing page.
		Write(Path.Join(root, "docs", "README.md"), Readme);
		Write(Path.Join(root, "docs", "index.html"),
				"<html>\n  <body>\n"
				+ "    <!-- RELEASES_HTML_START -->\n    old\n    <!-- RELEASES_HTML_END -->\n"
				+ "    <!-- BLOGS_HTML_START -->\n    <!-- BLOGS_HTML_END -->\n"
				+ "  </body>\n</html>\n");
		return root;
	}

	// run(tmp_path, gh, ...): what it logged, as capsys read it.
	static string Run(string root, FakeGitHub? gh = null, string? apiKey = null, string model = ReleasePost.DefaultModel, UrlOpen? urlopen = null)
	{
		StringWriter log = new();
		ReleasePost.Run(Repo, 42, "v0.0.3", gh ?? new FakeGitHub(), root, apiKey, model, urlopen, log);
		return log.ToString();
	}

	// Every file under root, by its path from root, and its text.
	static SortedDictionary<string, string> ReadAll(string root) =>
			new(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(root, path), Read), StringComparer.Ordinal);

	static int Count(string text, string part)
	{
		var count = 0;
		for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
			count++;
		return count;
	}
}
