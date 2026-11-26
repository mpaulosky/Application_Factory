using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Baseline.Tests;

// The Template's .github/scripts/BackfillBlogPosts.cs: test_backfill_blog_posts.py's tests, one method each under its name (ADR 0017's
// rule), with the same cases and assertions, on throwaway folders, then tests of the port's own. The pytest file retired with the cut-over
// (mpaulosky/Application_Factory#128). A pytest that patched urlopen hands Run a stand-in UrlOpen, one that patched run or sleep hands
// Command or Run a stand-in, and capsys is the log writer Run takes.
public sealed class BackfillBlogPostsTests : IDisposable
{
	const string Repo = "octo/demo";

	// pytest's tmp_path.
	readonly string tmp = Directory.CreateTempSubdirectory("backfill-blog-posts.").FullName;

	public void Dispose() => Directory.Delete(tmp, true);

	static JsonObject Release(string tag, long? pr, string published = "2026-09-20T10:00:00Z") => new()
	{
		["tag_name"] = tag,
		["name"] = $"Release {tag}",
		["published_at"] = published,
		["body"] = pr is not null ? $"Source PR: #{pr}\nSource PR URL: https://github.com/{Repo}/pull/{pr}\n" : "Notes.\n",
	};

	// Three releases for PRs 1, 2 (Dependabot) and 4, and one without a source PR.
	class FakeGitHub : IReleaseGitHub
	{
		public readonly Dictionary<long, JsonObject> Pulls = new()
		{
			[1] = new() { ["number"] = 1, ["title"] = "chore: First", ["body"] = "One.", ["merged_at"] = "2026-09-18T10:00:00Z" },
			[2] = new()
			{
				["number"] = 2,
				["title"] = "Bump codeql-action",
				["body"] = "Bumps.",
				["merged_at"] = "2026-09-19T10:00:00Z",
				["user"] = new JsonObject { ["login"] = "dependabot[bot]" },
			},
			[4] = new() { ["number"] = 4, ["title"] = "feat: Fourth", ["body"] = "Four.", ["merged_at"] = "2026-09-20T10:00:00Z" },
		};

		public readonly List<JsonObject> ReleasesData =
		[
			Release("v0.0.4", 4, "2026-09-20T11:00:00Z"),
			Release("v0.0.2", 2, "2026-09-19T11:00:00Z"),
			Release("v0.0.1", 1, "2026-09-18T11:00:00Z"),
			Release("v0.0.0", null, "2026-09-17T11:00:00Z"),
		];

		public readonly List<long> Pulled = [];

		public FakeGitHub()
		{
			foreach (var (number, pr) in Pulls)
				pr["html_url"] = $"https://github.com/{Repo}/pull/{number}";
		}

		public JsonNode? Pull(long number)
		{
			Pulled.Add(number);
			return Pulls[number].DeepClone();
		}

		public string PullTitle(BigInteger number) => (string)Pulls[(long)number]["title"]!;

		public List<JsonNode?> Commits(long number) =>
				[new JsonObject { ["sha"] = string.Concat(Enumerable.Repeat(number.ToString(System.Globalization.CultureInfo.InvariantCulture), 7)), ["commit"] = new JsonObject { ["message"] = $"change {number}" } }];

		public List<JsonNode?> Files(long number) =>
				[new JsonObject { ["filename"] = $"src/{number}.cs", ["additions"] = 1, ["deletions"] = 0, ["patch"] = "+x" }];

		public virtual List<JsonNode?> Releases() => [.. ReleasesData.Select(release => release.DeepClone())];
	}

	static void Write(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
	}

	static string Read(string path) => File.ReadAllText(path);

	static string MakeRepo(string root)
	{
		Directory.CreateDirectory(Path.Join(root, "docs", "blogs"));
		Write(Path.Join(root, "README.md"), "# Demo\n\n## Releases\n\n<!-- RELEASES_START -->\nold\n<!-- RELEASES_END -->\n");
		Write(Path.Join(root, "docs", "README.md"), "# Demo\n");
		Write(Path.Join(root, "docs", "index.html"),
				"<!-- RELEASES_HTML_START -->\n<!-- RELEASES_HTML_END -->\n<!-- BLOGS_HTML_START -->\n<!-- BLOGS_HTML_END -->\n");
		return root;
	}

	static SortedDictionary<string, string> ReadAll(string root) =>
			new(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(root, path), Read), StringComparer.Ordinal);

	static List<string> Posts(string root) =>
			[.. Directory.EnumerateFiles(Path.Join(root, "docs", "blogs"), "*-pr-*.md").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

	static string BlogDir(string root) => Path.Join(root, "docs", "blogs");

	static HashSet<BigInteger> Set(params long[] numbers) => [.. numbers.Select(n => (BigInteger)n)];

	static List<BigInteger> List(params long[] numbers) => [.. numbers.Select(n => (BigInteger)n)];

	static List<(BigInteger, string)> Selected(params (long Pr, string Tag)[] pairs) => [.. pairs.Select(pair => ((BigInteger)pair.Pr, pair.Tag))];

	static List<(BigInteger, string)> Select(string blogDir, bool regenerate = false, ISet<BigInteger>? only = null) =>
			[.. BackfillBlogPosts.SelectReleases(new FakeGitHub().Releases(), blogDir, TextWriter.Null, regenerate, only).Select(pair => (pair.Pr, pair.Tag))];

	static List<BigInteger> Run(string root, IReleaseGitHub gh, bool regenerate = false, string? apiKey = null, UrlOpen? urlopen = null, ISet<BigInteger>? waitFor = null,
			Action<int>? sleep = null, TextWriter? log = null) =>
			BackfillBlogPosts.Run(Repo, gh, root, regenerate, apiKey: apiKey, urlopen: urlopen, waitFor: waitFor, sleep: sleep ?? (_ => { }), log: log ?? TextWriter.Null);

	public static TheoryData<string?, long[]?> PrNumbers => new()
	{
		{ "", null },
		{ "  ", null },
		{ null, null },
		{ "1", [1] },
		{ "1, 3,#4 ,", [1, 3, 4] },
	};

	[Theory]
	[MemberData(nameof(PrNumbers))]
	public void test_parse_pr_numbers(string? text, long[]? expected)
	{
		var parsed = BackfillBlogPosts.ParsePrNumbers(text);

		if (expected is null)
			Assert.Null(parsed);
		else
			Assert.Equal(Set(expected).Order(), parsed!.Order());
	}

	[Fact]
	public void test_parse_pr_numbers_rejects_non_numbers()
	{
		Assert.Throws<PrNumbersError>(() => BackfillBlogPosts.ParsePrNumbers("1, two"));
	}

	[Fact]
	public void test_selects_releases_without_a_post_oldest_first()
	{
		var blogDir = BlogDir(MakeRepo(tmp));
		Write(Path.Join(blogDir, "2026-09-19-pr-2-bump.md"), "post\n");
		var selected = Select(blogDir);
		Assert.Equal(Selected((1, "v0.0.1"), (4, "v0.0.4")), selected);
	}

	[Fact]
	public void test_regenerate_selects_every_release_with_a_source_pr()
	{
		var blogDir = BlogDir(MakeRepo(tmp));
		Write(Path.Join(blogDir, "2026-09-19-pr-2-bump.md"), "post\n");
		var selected = Select(blogDir, regenerate: true);
		Assert.Equal(Selected((1, "v0.0.1"), (2, "v0.0.2"), (4, "v0.0.4")), selected);
	}

	[Fact]
	public void test_pr_numbers_limit_the_selection()
	{
		var blogDir = BlogDir(MakeRepo(tmp));
		var selected = Select(blogDir, regenerate: true, only: Set(2, 99));
		Assert.Equal(Selected((2, "v0.0.2")), selected);
	}

	[Fact]
	public void test_a_post_for_pr_1_does_not_count_for_pr_10()
	{
		var blogDir = BlogDir(MakeRepo(tmp));
		Write(Path.Join(blogDir, "2026-09-18-pr-10-other.md"), "post\n");
		Assert.Contains(((BigInteger)1, "v0.0.1"), Select(blogDir));
	}

	[Fact]
	public void test_run_writes_missing_posts_with_the_existing_tags()
	{
		MakeRepo(tmp);
		var written = Run(tmp, new FakeGitHub());

		Assert.Equal(List(1, 2, 4), written);
		Assert.Equal(["2026-09-18-pr-1-chore-first.md", "2026-09-19-pr-2-bump-codeql-action.md", "2026-09-20-pr-4-feat-fourth.md"], Posts(tmp));
		var fourth = Read(Path.Join(BlogDir(tmp), "2026-09-20-pr-4-feat-fourth.md"));
		Assert.Contains("- **Release tag:** `v0.0.4`", fourth);
		Assert.Contains("## Commits", fourth);
		Assert.Contains("## Files changed", fourth);

		var index = Read(Path.Join(BlogDir(tmp), "README.md"));
		Assert.True(index.IndexOf("pr-4-", StringComparison.Ordinal) is var four and >= 0 && four < index.IndexOf("pr-2-", StringComparison.Ordinal));
		Assert.True(index.IndexOf("pr-2-", StringComparison.Ordinal) < index.IndexOf("pr-1-", StringComparison.Ordinal));

		var readme = Read(Path.Join(tmp, "README.md"));
		Assert.DoesNotContain("old", readme);
		Assert.Contains($"| [v0.0.4](https://github.com/{Repo}/releases/tag/v0.0.4) | 2026-09-20 | feat: Fourth | [Post](", readme);
		Assert.Contains("| [v0.0.0](https://github.com/octo/demo/releases/tag/v0.0.0) | 2026-09-17 | Release v0.0.0 | — |", readme);
		Assert.Equal(readme, Read(Path.Join(tmp, "docs", "README.md")));

		var html = Read(Path.Join(tmp, "docs", "index.html"));
		Assert.Equal(3, html.Split("\">Post</a>").Length - 1);
		Assert.Contains($"<a href=\"https://github.com/{Repo}/pull/2\">PR #2</a>", html);
	}

	[Fact]
	public void test_dependabot_post_has_no_summary_but_others_do()
	{
		MakeRepo(tmp);

		static byte[] Urlopen(SummaryRequest request, int timeout) => Encoding.UTF8.GetBytes("""{"content": [{"type": "text", "text": "A summary."}]}""");

		Run(tmp, new FakeGitHub(), apiKey: "sk-test", urlopen: Urlopen);
		Assert.DoesNotContain("## Summary", Py.SplitLines(Read(Path.Join(BlogDir(tmp), "2026-09-19-pr-2-bump-codeql-action.md"))));
		Assert.Contains("## Summary", Py.SplitLines(Read(Path.Join(BlogDir(tmp), "2026-09-18-pr-1-chore-first.md"))));
	}

	[Fact]
	public void test_second_run_without_regenerate_changes_nothing()
	{
		MakeRepo(tmp);
		Run(tmp, new FakeGitHub());
		var first = ReadAll(tmp);

		FakeGitHub gh = new();
		Assert.Empty(Run(tmp, gh));
		Assert.Empty(gh.Pulled);
		Assert.Equal(first, ReadAll(tmp));
	}

	[Fact]
	public void test_run_without_missing_posts_still_copies_an_edited_readme_to_docs()
	{
		// A README-only PR merged with [skip-release] releases nothing, so every Release already has its post; docs/README.md must still
		// follow README.md.
		MakeRepo(tmp);
		Run(tmp, new FakeGitHub());
		var readme = Path.Join(tmp, "README.md");
		Write(readme, Read(readme) + "\n## Usage\n\nSee [the guide](docs/guide.md).\n");

		FakeGitHub gh = new();
		Assert.Empty(Run(tmp, gh));
		Assert.Empty(gh.Pulled);
		Assert.Contains("See [the guide](guide.md).", Read(Path.Join(tmp, "docs", "README.md")));

		// A repeated run changes nothing, so the docs job opens no PR.
		var first = ReadAll(tmp);
		Run(tmp, new FakeGitHub());
		Assert.Equal(first, ReadAll(tmp));
	}

	[Fact]
	public void test_regenerate_replaces_old_format_posts()
	{
		var blogDir = BlogDir(MakeRepo(tmp));
		const string old = "2026-09-17-pr-1-chore-first.md";
		Write(Path.Join(blogDir, old), "---\npost_title: \"chore: First\"\n---\n## chore: First\n");

		Assert.Equal(List(1, 2, 4), Run(tmp, new FakeGitHub(), regenerate: true));
		Assert.DoesNotContain(old, Posts(tmp));
		Assert.Contains("2026-09-18-pr-1-chore-first.md", Posts(tmp));
		Assert.DoesNotContain(old, Read(Path.Join(blogDir, "README.md")));
	}

	[Fact]
	public void test_regenerate_twice_changes_nothing()
	{
		MakeRepo(tmp);
		Run(tmp, new FakeGitHub(), regenerate: true);
		var first = ReadAll(tmp);
		Run(tmp, new FakeGitHub(), regenerate: true);
		Assert.Equal(first, ReadAll(tmp));
	}

	// main(argv) with run patched, in an environment of the given variables alone, as the pytest's monkeypatch set them.
	static BackfillBlogPosts.Inputs CallMain(string[] argv, Dictionary<string, string>? environment = null)
	{
		BackfillBlogPosts.Inputs? seen = null;
		var code = BackfillBlogPosts.Command(argv, TextWriter.Null, TextWriter.Null, inputs => seen = inputs, name => environment?.GetValueOrDefault(name));
		Assert.Equal(0, code);
		return seen!;
	}

	[Fact]
	public void test_main_passes_the_inputs()
	{
		var calls = CallMain(["--repo", Repo, "--regenerate", "true", "--pr-numbers", "3, 1"], new() { ["ANTHROPIC_API_KEY"] = " sk-test " });

		Assert.Equal(Repo, calls.Repository);
		Assert.True(calls.Regenerate);
		Assert.Equal(Set(1, 3).Order(), calls.Only!.Order());
		Assert.Equal("sk-test", calls.ApiKey);
		Assert.Equal(ReleasePost.DefaultModel, calls.Model);
		Assert.Null(calls.WaitFor);
	}

	// Lists v0.0.4 (PR 4) only from the given call on, like a just-published Release.
	sealed class LaggingGitHub(int listedFromCall) : FakeGitHub
	{
		public int Calls;

		public override List<JsonNode?> Releases()
		{
			Calls++;
			var listed = base.Releases();
			return Calls < listedFromCall ? [.. listed.Where(release => (string?)release!["tag_name"] != "v0.0.4")] : listed;
		}
	}

	[Fact]
	public void test_run_lists_again_until_a_just_published_release_appears()
	{
		MakeRepo(tmp);
		LaggingGitHub gh = new(3);
		List<int> slept = [];

		var written = Run(tmp, gh, waitFor: Set(4), sleep: slept.Add);

		Assert.Equal(List(1, 2, 4), written);
		Assert.Equal([BackfillBlogPosts.WaitDelay, BackfillBlogPosts.WaitDelay], slept);
		Assert.Contains("[v0.0.4]", Read(Path.Join(tmp, "README.md")));
	}

	[Fact]
	public void test_the_tables_use_the_listing_the_posts_came_from()
	{
		MakeRepo(tmp);
		LaggingGitHub gh = new(2);

		Run(tmp, gh, waitFor: Set(4), sleep: _ => { });

		// One listing that lacked v0.0.4, one that had it; the tables reuse the second.
		Assert.Equal(2, gh.Calls);
		Assert.Contains("[v0.0.4]", Read(Path.Join(tmp, "README.md")));
	}

	[Fact]
	public void test_run_writes_what_is_listed_when_a_release_never_appears()
	{
		MakeRepo(tmp);
		LaggingGitHub gh = new(99);
		List<int> slept = [];
		StringWriter log = new();

		var written = Run(tmp, gh, waitFor: Set(4), sleep: slept.Add, log: log);

		Assert.Equal(List(1, 2), written);
		Assert.Equal(BackfillBlogPosts.WaitAttempts, gh.Calls);
		Assert.Equal(BackfillBlogPosts.WaitAttempts - 1, slept.Count);
		Assert.Contains("::warning::No Release is listed yet for PR #4", log.ToString());
	}

	[Fact]
	public void test_run_lists_once_without_wait_for()
	{
		MakeRepo(tmp);
		LaggingGitHub gh = new(99);

		var written = Run(tmp, gh, sleep: _ => Assert.Fail("slept"));

		Assert.Equal(List(1, 2), written);
		Assert.Equal(1, gh.Calls);
	}

	[Fact]
	public void test_main_passes_wait_for_prs()
	{
		var seen = CallMain(["--repo", Repo, "--wait-for-prs", "187, #188"]);

		Assert.Equal(Set(187, 188).Order(), seen.WaitFor!.Order());
	}

	// The port's own.

	[Theory]
	[InlineData("#٣, 001", new long[] { 3, 1 })]
	[InlineData("#, ,", null)]
	[InlineData("𝟓", new long[] { 5 })]
	public void Pr_numbers_are_read_as_isdigit_and_int_read_them(string text, long[]? expected)
	{
		// Arabic-Indic and astral decimal digits are digits to both, and leading zeros are int()'s.
		var parsed = BackfillBlogPosts.ParsePrNumbers(text);

		if (expected is null)
			Assert.Null(parsed);
		else
			Assert.Equal(Set(expected).Order(), parsed!.Order());
	}

	[Theory]
	[InlineData("²", "invalid literal for int() with base 10: '²'")]
	[InlineData("1, two", "pr_numbers must be comma-separated PR numbers, got '1, two'")]
	[InlineData("1;2", "pr_numbers must be comma-separated PR numbers, got '1;2'")]
	[InlineData("# 3", "pr_numbers must be comma-separated PR numbers, got '# 3'")]
	public void Pr_numbers_that_arent_are_python_s_error(string text, string message)
	{
		// ² is a digit to str.isdigit() but not to int(), so its error is int()'s.
		var error = Assert.Throws<PrNumbersError>(() => BackfillBlogPosts.ParsePrNumbers(text));

		Assert.Equal(message, error.Message);
	}

	[Theory]
	[InlineData(" TRUE ", true)]
	[InlineData("yes", false)]
	[InlineData("false", false)]
	public void Regenerate_is_true_only_for_true_in_any_case_and_blanks(string value, bool regenerate)
	{
		Assert.Equal(regenerate, CallMain(["--repo", Repo, "--regenerate", value]).Regenerate);
	}

	[Fact]
	public void The_repository_comes_from_repository_then_github_repository()
	{
		Assert.Equal("o/one", CallMain([], new() { ["REPOSITORY"] = "o/one", ["GITHUB_REPOSITORY"] = "o/two" }).Repository);
		Assert.Equal("o/two", CallMain([], new() { ["REPOSITORY"] = "", ["GITHUB_REPOSITORY"] = "o/two" }).Repository);
		Assert.Equal("o/three", CallMain(["--repo", "o/three"], new() { ["REPOSITORY"] = "o/one" }).Repository);
	}

	[Theory]
	[InlineData(new string[0], "--repo is required when REPOSITORY and GITHUB_REPOSITORY are unset")]
	[InlineData(new[] { "--repo", "" }, "--repo is required when REPOSITORY and GITHUB_REPOSITORY are unset")]
	[InlineData(new[] { "--repo", "o/app", "--wait-for-prs", "1;2" }, "pr_numbers must be comma-separated PR numbers, got '1;2'")]
	public void A_usage_error_exits_2_with_the_wrapped_usage(string[] argv, string message)
	{
		StringWriter stdout = new(), stderr = new();

		var code = BackfillBlogPosts.Command(argv, stdout, stderr, _ => Assert.Fail("ran"), _ => null);

		Assert.Equal(2, code);
		Assert.Equal("", stdout.ToString());
		Assert.Equal("usage: backfill_blog_posts.cs [-h] [--regenerate REGENERATE]\n                              [--pr-numbers PR_NUMBERS]\n"
				+ $"                              [--wait-for-prs WAIT_FOR_PRS] [--repo REPO]\nbackfill_blog_posts.cs: error: {message}\n", stderr.ToString());
	}

	[Fact]
	public void An_unmarked_release_is_skipped_with_a_notice()
	{
		StringWriter log = new();

		BackfillBlogPosts.SelectReleases(new FakeGitHub().Releases(), BlogDir(MakeRepo(tmp)), log);

		Assert.Equal("::notice::v0.0.0 has no 'Source PR: #n' line; skipping it.\n", log.ToString());
	}

	[Fact]
	public void A_run_with_every_post_written_says_it_refreshes_the_tables_only()
	{
		MakeRepo(tmp);
		Run(tmp, new FakeGitHub());
		StringWriter log = new();

		Run(tmp, new FakeGitHub(), log: log);

		Assert.Contains("Every selected release already has a blog post; refreshing the tables only.\n", log.ToString());
	}

	[Fact]
	public void A_wait_names_the_missing_prs_in_order_and_the_delay()
	{
		LaggingGitHub gh = new(2);
		StringWriter log = new();

		BackfillBlogPosts.ListedReleasesOf(gh, Set(9, 4, 7), log, attempts: 2, sleep: _ => { });

		Assert.Equal("The Release for PR #4, #7, #9 isn't listed yet; listing again in 2s.\n"
				+ "::warning::No Release is listed yet for PR #7, #9; writing posts for the listed ones only.\n", log.ToString());
	}
}
