using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baseline.Tests;

// test_release_post.py from test_readme_legacy_dev_blog_block_is_removed to test_an_oversized_character_reference_is_sanitized_not_fatal:
// the README and blog tables, the blog cards and excerpts, the post's content and the AI summary, and the untrusted Markdown.
public sealed partial class ReleasePostTests
{
	[Fact]
	public void Readme_legacy_dev_blog_block_is_removed()
	{
		MakeRepo(tmp);
		Write(Path.Join(tmp, "README.md"), "# Demo\n\n## Dev Blog\n\n<!-- BLOG_START -->\nx\n<!-- BLOG_END -->\n\n## About\n\nText.\n");
		Run(tmp);
		var readme = Read(Path.Join(tmp, "README.md"));
		Assert.DoesNotContain("Dev Blog", readme);
		Assert.DoesNotContain("BLOG_START", readme);
	}

	[Fact]
	public void Index_html_without_markers_is_left_alone()
	{
		MakeRepo(tmp);
		Write(Path.Join(tmp, "docs", "index.html"), "<html></html>\n");
		Run(tmp);
		Assert.Equal("<html></html>\n", Read(Path.Join(tmp, "docs", "index.html")));
	}

	[Fact]
	public void Running_twice_leaves_every_file_unchanged()
	{
		MakeRepo(tmp);
		Run(tmp);
		var first = ReadAll(tmp);
		Run(tmp);
		Assert.Equal(first, ReadAll(tmp));
	}

	[Fact]
	public void Blog_index_lists_the_post_once()
	{
		MakeRepo(tmp);
		Run(tmp);
		Run(tmp);
		var index = Read(Path.Join(tmp, "docs", "blogs", "README.md"));
		Assert.Equal(1, Count(index, "pr-42-"));
		Assert.Contains("| 2026-09-24 | [feat(ui): Add the \"dark\" theme](2026-09-24-pr-42-feat-ui-add-the-dark-theme.md) | release,automation |", index);
	}

	[Fact]
	public void Blog_index_keeps_posts_whose_title_mentions_date()
	{
		var blogDir = Path.Join(MakeRepo(tmp), "docs", "blogs");
		var older = "| 2026-09-20 | [fix(ui): Date parsing fix](2026-09-20-pr-7-fix-ui-date-parsing-fix.md) | release,automation |";
		foreach (var name in new[] { "2026-09-20-pr-7-fix-ui-date-parsing-fix.md", "2026-09-24-pr-8-feat-ui-next.md" })
			Write(Path.Join(blogDir, name), "post\n");
		ReleasePost.UpdateBlogIndex(blogDir, "2026-09-20", "fix(ui): Date parsing fix", "2026-09-20-pr-7-fix-ui-date-parsing-fix.md");
		ReleasePost.UpdateBlogIndex(blogDir, "2026-09-24", "feat(ui): Next", "2026-09-24-pr-8-feat-ui-next.md");
		var index = Read(Path.Join(blogDir, "README.md"));
		Assert.Contains(older, index);
		Assert.Equal(1, Count(index, "| Date | Title | Tags |"));
	}

	[Fact]
	public void Table_separators_use_the_compact_style()
	{
		// markdownlint MD060 ("compact") needs a space on both sides of every pipe.
		var blogDir = Path.Join(MakeRepo(tmp), "docs", "blogs");
		ReleasePost.UpdateBlogIndex(blogDir, "2026-09-24", "feat(ui): Next", "2026-09-24-pr-8-feat-ui-next.md");
		var index = Read(Path.Join(blogDir, "README.md"));
		Assert.Contains("| ---- | ----- | ---- |", Py.SplitLines(index));
		Assert.Contains("| ------- | ---- | ----- | --------- |", Py.SplitLines(ReleasePost.RenderReleasesMarkdown([])));
	}

	[Fact]
	public void Blog_index_replaces_an_old_style_separator()
	{
		var blogDir = Path.Join(MakeRepo(tmp), "docs", "blogs");
		Write(Path.Join(blogDir, "README.md"), "| Date | Title | Tags |\n|------|-------|------|\n");
		ReleasePost.UpdateBlogIndex(blogDir, "2026-09-24", "feat(ui): Next", "2026-09-24-pr-8-feat-ui-next.md");
		var index = Read(Path.Join(blogDir, "README.md"));
		Assert.DoesNotContain("|------|-------|------|", index);
		Assert.Equal(1, Count(index, "| ---- | ----- | ---- |"));
	}

	// Blog post cards and excerpts

	static string CardPost(string summary = "Release notes seed for v0.0.3 from PR #42.", string body = "") =>
			$"---\npost_title: \"T\"\nsummary: \"{summary}\"\npost_date: \"2026-09-24\"\n---\n## T\n\n{body}";

	[Fact]
	public void Excerpt_prefers_the_ai_summary_section()
	{
		var text = CardPost(body: "### Summary\n\nClaude wrote this.\n\n### PR description\n\nThe description.\n");
		Assert.Equal("Claude wrote this.", ReleasePost.PostExcerpt(text, "Release notes seed for v0.0.3 from PR #42."));
	}

	[Fact]
	public void Excerpt_uses_a_real_front_matter_summary_over_the_description()
	{
		var text = CardPost(body: "### PR description\n\nThe description.\n");
		Assert.Equal("A hand-written summary.", ReleasePost.PostExcerpt(text, "A hand-written summary."));
	}

	[Fact]
	public void Excerpt_skips_the_seed_summary_headings_and_issue_references()
	{
		var body = "### PR description\n\nFixes #16.\n\n## Problem\n\n"
				+ "`FindBySlugAsync` lowercases the **slug**, see [the issue](https://x/16).\n\n"
				+ "### Commits\n\n- abc Commit subject\n";
		Assert.Equal("FindBySlugAsync lowercases the slug, see the issue.",
				ReleasePost.PostExcerpt(CardPost(body: body), "Release notes seed for v0.0.3 from PR #42."));
	}

	[Fact]
	public void Excerpt_joins_a_list_and_keeps_snake_case()
	{
		var body = "### PR description\n\n## Summary\n\n- Removes the `pull_request` trigger.\n- Keeps _manual_ runs.\n";
		Assert.Equal("Removes the pull_request trigger. Keeps manual runs.", ReleasePost.PostExcerpt(CardPost(body: body)));
	}

	[Fact]
	public void Excerpt_skips_code_blocks_and_tables()
	{
		var body = "### PR description\n\n```bash\necho hi\n\necho there\n```\n\n| a | b |\n| - | - |\n\nReal text.\n";
		Assert.Equal("Real text.", ReleasePost.PostExcerpt(CardPost(body: body)));
	}

	[Fact]
	public void Excerpt_is_empty_without_a_description()
	{
		Assert.Equal("", ReleasePost.PostExcerpt(CardPost(body: "### PR description\n\nNo PR description was provided.\n")));
		Assert.Equal("", ReleasePost.PostExcerpt("no front matter or sections"));
	}

	[Fact]
	public void Long_excerpts_are_cut_at_a_word_with_an_ellipsis()
	{
		var text = string.Concat(Enumerable.Repeat("word ", 100));
		var shortened = ReleasePost.Shorten(Py.Strip(text));
		Assert.True(Py.Len(shortened) <= ReleasePost.ExcerptLength);
		Assert.EndsWith("word…", shortened, StringComparison.Ordinal);
		Assert.Equal("Short enough.", ReleasePost.Shorten("Short enough."));
	}

	[Fact]
	public void Display_date_formats_iso_dates_and_leaves_others_alone()
	{
		Assert.Equal("7 Sep 2026", ReleasePost.DisplayDate("2026-09-07"));
		Assert.Equal("someday", ReleasePost.DisplayDate("someday"));
	}

	[Fact]
	public void Blog_cards_escape_titles_and_excerpts()
	{
		List<BlogPost> posts = [new("a.md", "5", "<b>T</b>", "2026-09-24", "", "a < b & c")];
		var htmlText = string.Join("\n", ReleasePost.RenderBlogsHtml(posts, Repo));
		Assert.Contains("&lt;b&gt;T&lt;/b&gt;", htmlText);
		Assert.Contains("<p class=\"post-excerpt\">a &lt; b &amp; c</p>", htmlText);
		Assert.DoesNotContain("post-tag", htmlText);
	}

	[Fact]
	public void Blog_cards_without_posts_show_an_empty_message()
	{
		Assert.Equal(new List<string> { "<p class=\"post-empty\">No blog posts yet.</p>" }, ReleasePost.RenderBlogsHtml([], Repo));
	}

	[Fact]
	public void Real_pages_site_is_unchanged_by_a_second_table_update()
	{
		// The committed docs/index.html must already be what update_tables writes,
		// so a release run only changes it when releases or posts change.
		// Path(__file__).resolve().parents[3]: three folders up from the Template's .github/scripts/tests/.
		var source = Path.GetFullPath(Path.Join(Skill.Path("template", "owned", ".github", "scripts", "tests"), "..", "..", "..", "docs", "index.html"));
		if (!File.Exists(source))
			Assert.Skip("this repo has no docs/index.html Pages site");
		Directory.CreateDirectory(Path.Join(tmp, "docs", "blogs"));
		Write(Path.Join(tmp, "docs", "index.html"), Read(source));
		ReleasePost.UpdateTables(Repo, new FakeGitHub(), tmp, log: new StringWriter());
		var once = Read(Path.Join(tmp, "docs", "index.html"));
		ReleasePost.UpdateTables(Repo, new FakeGitHub(), tmp, log: new StringWriter());
		Assert.Equal(once, Read(Path.Join(tmp, "docs", "index.html")));
		Assert.True(once.Contains("<!-- RELEASES_HTML_START -->", StringComparison.Ordinal) && once.Contains("<!-- BLOGS_HTML_START -->", StringComparison.Ordinal));
	}

	// Post content and the AI summary

	static string PostText(string root) => Read(Path.Join(root, "docs", "blogs", "2026-09-24-pr-42-feat-ui-add-the-dark-theme.md"));

	[Fact]
	public void No_key_writes_post_without_summary()
	{
		MakeRepo(tmp);
		var output = Run(tmp, apiKey: null);
		var post = PostText(tmp);

		Assert.DoesNotContain("## Summary", post);
		Assert.Contains("ai_note: \"Generated by release automation from the PR title, description, commits and changed files. No AI summary.\"", post);
		Assert.Contains("::notice::", output);
		// Front matter fields the workflow always wrote.
		foreach (var line in new[]
		{
			"post_title: \"feat(ui): Add the \\\"dark\\\" theme\"",
			"author1: mpaulosky",
			"post_slug: \"v0.0.3-pr-42\"",
			"microsoft_alias: n/a",
			"  - release:v0.0.3",
			"summary: \"Release notes seed for v0.0.3 from PR #42.\"",
			"post_date: \"2026-09-24\"",
		})
			Assert.Contains(line, post);
		// Nothing renders a featured image, so the post doesn't carry one.
		Assert.DoesNotContain("featured_image", post);
		Assert.True(Index(post, "## PR description") < Index(post, "## Commits"));
		Assert.True(Index(post, "## Commits") < Index(post, "## Files changed"));
		Assert.Contains("Adds a theme.", post);
	}

	// FakeResponse(data): the response body urlopen hands back.
	static byte[] FakeResponse(string data) => Encoding.UTF8.GetBytes(data);

	[Fact]
	public void Summary_is_included_when_the_api_answers()
	{
		MakeRepo(tmp);
		SummaryRequest? capturedRequest = null;
		int? capturedTimeout = null;

		byte[] Urlopen(SummaryRequest request, int timeout)
		{
			capturedRequest = request;
			capturedTimeout = timeout;
			JsonObject payload = new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "This release adds a dark theme." }) };
			return FakeResponse(payload.ToJsonString());
		}

		Run(tmp, apiKey: "sk-test", model: "claude-test", urlopen: Urlopen);
		var post = PostText(tmp);

		Assert.True(Index(post, "## Summary\n\nThis release adds a dark theme.\n") < Index(post, "## PR description"));
		Assert.Contains("Includes an AI summary written by claude-test.", post);
		Assert.Contains("summary: \"Release notes seed for v0.0.3 from PR #42.\"", post);

		var request = capturedRequest!;
		Assert.Equal(60, capturedTimeout);
		Assert.Equal("https://api.anthropic.com/v1/messages", request.Url);
		Assert.Equal("sk-test", request.Headers["x-api-key"]);
		var sent = JsonNode.Parse(Encoding.UTF8.GetString(request.Data))!;
		Assert.Equal("claude-test", (string?)sent["model"]);
		var prompt = (string)sent["messages"]![0]!["content"]!;
		Assert.Contains("feat(ui): Add theme tokens", prompt);
		Assert.Contains("diff --git a/src/Web/Theme.cs", prompt);
		Assert.DoesNotContain("+lock", prompt);
	}

	// The errors urlopen raised in the pytest, as the .NET exceptions that stand for them.
	static Exception ApiError(string error) => error switch
	{
		// urllib.error.HTTPError("https://api.anthropic.com/v1/messages", 529, "Overloaded", {}, None)
		"HTTPError" => new HttpRequestException("Overloaded", null, (HttpStatusCode)529),
		// urllib.error.URLError("no route")
		"URLError" => new HttpRequestException("no route"),
		// TimeoutError("timed out")
		"TimeoutError" => new TimeoutException("timed out"),
		// ValueError("bad json")
		"ValueError" => new JsonException("bad json"),
		_ => throw new ArgumentOutOfRangeException(nameof(error)),
	};

	[Theory]
	[InlineData("HTTPError")]
	[InlineData("URLError")]
	[InlineData("TimeoutError")]
	[InlineData("ValueError")]
	public void Api_failure_writes_post_without_summary(string error)
	{
		MakeRepo(tmp);

		byte[] Urlopen(SummaryRequest request, int timeout) => throw ApiError(error);

		var output = Run(tmp, apiKey: "sk-test", urlopen: Urlopen);
		var post = PostText(tmp);

		Assert.DoesNotContain("## Summary", post);
		Assert.Contains("No AI summary.", post);
		Assert.Contains("::warning::", output);
	}

	[Fact]
	public void Empty_api_answer_is_treated_as_no_summary()
	{
		MakeRepo(tmp);

		static byte[] Urlopen(SummaryRequest request, int timeout) => FakeResponse("{\"content\": []}");

		var output = Run(tmp, apiKey: "sk-test", urlopen: Urlopen);
		Assert.DoesNotContain("## Summary", PostText(tmp));
		Assert.Contains("::warning::", output);
	}

	[Fact]
	public void Dependabot_pr_gets_no_summary_even_with_a_key()
	{
		MakeRepo(tmp);
		FakeGitHub gh = new();
		gh.Pulls[42]["user"] = new JsonObject { ["login"] = "dependabot[bot]" };

		List<SummaryRequest> calls = [];

		byte[] Urlopen(SummaryRequest request, int timeout)
		{
			calls.Add(request);
			return FakeResponse("{\"content\": [{\"type\": \"text\", \"text\": \"Summary.\"}]}");
		}

		var output = Run(tmp, gh: gh, apiKey: "sk-test", urlopen: Urlopen);
		var post = PostText(tmp);
		Assert.Empty(calls);
		Assert.DoesNotContain("## Summary", post);
		Assert.Contains("No AI summary.", post);
		Assert.Contains("::notice::", output);
	}

	[Fact]
	public void Rewriting_a_post_under_a_new_name_drops_the_old_one()
	{
		var blogDir = Path.Join(MakeRepo(tmp), "docs", "blogs");
		var stale = "2026-09-23-pr-42-old-title.md";
		Write(Path.Join(blogDir, stale), "old\n");
		ReleasePost.UpdateBlogIndex(blogDir, "2026-09-23", "Old title", stale);
		Write(Path.Join(blogDir, "2026-09-23-pr-420-other.md"), "other\n");
		ReleasePost.UpdateBlogIndex(blogDir, "2026-09-23", "Other", "2026-09-23-pr-420-other.md");
		// PR 7's post, whose title slug names PR 42 (mpaulosky/Application_Factory#211).
		Write(Path.Join(blogDir, "2026-09-24-pr-7-revert-pr-42-changes.md"), "revert\n");

		Run(tmp);

		Assert.False(File.Exists(Path.Join(blogDir, stale)));
		Assert.True(File.Exists(Path.Join(blogDir, "2026-09-23-pr-420-other.md")));
		Assert.True(File.Exists(Path.Join(blogDir, "2026-09-24-pr-7-revert-pr-42-changes.md")));
		var index = Read(Path.Join(blogDir, "README.md"));
		Assert.DoesNotContain(stale, index);
		Assert.Contains("2026-09-23-pr-420-other.md", index);
		Assert.Equal(1, Count(index, "pr-42-"));
	}

	[Fact]
	public void A_post_belongs_to_the_pr_its_name_starts_with()
	{
		// The title slug after "-pr-N-" can name another PR (mpaulosky/Application_Factory#211).
		string own = "2026-09-20-pr-7-fix-a.md", other = "2026-09-24-pr-8-revert-pr-7-changes.md";
		foreach (var name in new[] { own, other, "2026-09-25-pr-70-x.md", "2026-09-26-pr-17-y.md" })
			Write(Path.Join(tmp, name), "x\n");
		Assert.Equal([own], ReleasePost.PostsForPr(tmp, "7").Select(Path.GetFileName));
		Assert.Equal([other], ReleasePost.PostsForPr(tmp, "8").Select(Path.GetFileName));
		Assert.Empty(ReleasePost.PostsForPr(tmp, "4"));
		var entries = ReleasePost.ReleaseEntries(new FakeGitHub(), Repo, tmp);
		Assert.Equal([ReleasePost.PostUrl(Repo, own)], entries.Where(entry => entry.Pr == "7").Select(entry => entry.PostUrl));
	}

	[Fact]
	public void Update_tables_without_a_new_release_lists_existing_releases()
	{
		MakeRepo(tmp);
		ReleasePost.UpdateTables(Repo, new FakeGitHub(), root: tmp, log: new StringWriter());
		var readme = Read(Path.Join(tmp, "README.md"));
		Assert.DoesNotContain("v0.0.3", readme);
		Assert.Contains($"| [v0.0.2](https://github.com/{Repo}/releases/tag/v0.0.2) | 2026-09-20 | fix: Older change | — |", readme);
		Assert.Contains("v0.0.2", Read(Path.Join(tmp, "docs", "index.html")));
	}

	// Untrusted Markdown (mpaulosky/dotfiles#48)

	public static readonly TheoryData<string> Hostile = new(
		"<script>alert(1)</script>",
		"Text <img src=x onerror=alert(1)> more",
		"<div>\n# x\n</div>",
		// Escaping the HTML block's lines must not bring its fence line to life and expose the later code.
		"<div>\n```\n</div>\n\n```\n<script>alert(1)</script>\n```",
		"```js title\n<script>alert(1)</script>",  // unclosed, and kramdown reads no fence with a two-word info
		"- item\n  ```\n  <script>alert(1)</script>\n  ```",
		"> ```\n> <script>alert(1)</script>",
		"x `y\n`<script>alert(1)</script>`",
		"<!-- a --><script>alert(1)</script>");

	// The post's lines, after its front matter, that CommonMark reads as neither fenced nor indented code.
	// Raw HTML blocks count as outside: they are what must not get through.
	static List<string> OutsideCode(string post)
	{
		var lines = post.Split("---\n", 3)[2].Split('\n');
		List<CodeBlock> blocks = [];
		ReleasePost.ScanBlocks([.. lines], [], blocks: blocks);
		HashSet<int> code = [.. blocks.SelectMany(block => Enumerable.Range(block.Lines[0], block.Lines[^1] - block.Lines[0] + 1))];
		return [.. lines.Where((line, index) => !code.Contains(index))];
	}

	[Theory]
	[MemberData(nameof(Hostile))]
	public void No_raw_html_reaches_the_post_outside_code(string body)
	{
		var post = ReleasePost.RenderPost(new JsonObject { ["number"] = 7, ["body"] = body }, "T", "v1.2.3", "2026-09-26", [], [], body, "m");
		foreach (var line in OutsideCode(post))
		{
			// Only the raw-tag comments, the <code> spans the sanitizer writes, and safe autolinks.
			Assert.False(Regex.IsMatch(line, @"<(?!code>|/code>|https?://|mailto:|!-- \{% (?:end)?raw %\} -->)"), line);
		}
	}

	[Fact]
	public void The_pr_title_is_escaped_in_the_post_and_both_tables()
	{
		var title = "feat: Add <b>bold</b> and {{ site.x }}";
		var post = ReleasePost.RenderPost(J("""{"number": 7}"""), title, "v1.2.3", "2026-09-26", [], [], null, "m");
		Assert.Contains("# feat: Add &lt;b>bold&lt;/b> and {{ site.x }}\n", post);  // inside {% raw %}
		Assert.Contains("post_title: \"feat: Add <b>bold</b> and {{ site.x }}\"", post);  // YAML, never rendered as Markdown

		ReleasePost.UpdateBlogIndex(tmp, "2026-09-26", title, "2026-09-26-pr-7-x.md");
		var row = Read(Path.Join(tmp, "README.md"));
		Assert.Contains("[feat: Add &lt;b>bold&lt;/b> and &#123;&#123; site.x }}](2026-09-26-pr-7-x.md)", row);

		ReleaseEntry entry = new() { Tag = "v1.2.3", Url = "u", Date = "2026-09-26", Title = title, PostUrl = "" };
		Assert.Contains("| feat: Add &lt;b>bold&lt;/b> and &#123;&#123; site.x }} |", ReleasePost.RenderReleasesMarkdown([entry]));
	}

	[Theory]
	[InlineData("fix: <https://example.test/{{site.title}}>", "<https://example.test/%7B%7Bsite.title%7D%7D>")]
	[InlineData("fix: <https://example.test/{%include%}>", "<https://example.test/%7B%include%%7D>")]
	[InlineData("fix: <mailto:a{{x}}@b.c>", "<mailto:a%7B%7Bx%7D%7D@b.c>")]
	public void Liquid_in_a_title_autolink_is_encoded_in_both_tables(string title, string encoded)
	{
		// The tables have no {% raw %} wrapper, so an autolink can't keep its braces (mpaulosky/dotfiles#69).
		// %7B and %7D are the URL's own encoding of the braces, so the link still works.
		ReleasePost.UpdateBlogIndex(tmp, "2026-10-05", title, "p.md");
		var row = Assert.Single(Py.SplitLines(Read(Path.Join(tmp, "README.md"))), line => line.Contains("p.md", StringComparison.Ordinal));
		Assert.Contains(encoded, row);
		Assert.True(!row.Contains("{{", StringComparison.Ordinal) && !row.Contains("{%", StringComparison.Ordinal));

		ReleaseEntry entry = new() { Tag = "v1.2.3", Url = "u", Date = "2026-10-05", Title = title, PostUrl = "" };
		var table = ReleasePost.RenderReleasesMarkdown([entry]);
		Assert.Contains(encoded, table);
		Assert.True(!table.Contains("{{", StringComparison.Ordinal) && !table.Contains("{%", StringComparison.Ordinal));

		// The post is inside {% raw %}, so its autolink keeps its text.
		var post = ReleasePost.RenderPost(J("""{"number": 7}"""), title, "v1.2.3", "2026-10-05", [], [], null, "m");
		Assert.Contains($"# {title}\n", post);
	}

	[Fact]
	public void The_pr_body_is_escaped()
	{
		var post = ReleasePost.RenderPost(new JsonObject { ["number"] = 7, ["body"] = "Why <img src=x onerror=alert(1)>" }, "T", "v1", "2026-09-26", [], [], null, "m");
		Assert.Contains("Why &lt;img src=x onerror=alert(1)>", ReleasePost.Section(post, "PR description"));
	}

	[Fact]
	public void The_ai_summary_is_escaped()
	{
		var post = ReleasePost.RenderPost(J("""{"number": 7}"""), "T", "v1", "2026-09-26", [], [], "Adds <iframe src=x></iframe>.", "m");
		Assert.Contains("Adds &lt;iframe src=x>&lt;/iframe>.", ReleasePost.Section(post, "Summary"));
	}

	[Fact]
	public void Commit_subjects_are_escaped()
	{
		var commits = Many("""[{"sha": "abc1234def", "commit": {"message": "fix: Drop <script> {: .x}\n\nbody"}}]""");
		Assert.Contains("- fix: Drop &lt;script> &#123;: .x} (`abc1234`)", ReleasePost.RenderCommits(commits));
	}

	[Theory]
	[InlineData("src/Web/Theme.cs", "`src/Web/Theme.cs`")]
	[InlineData("docs/a b (1).md", "`docs/a b (1).md`")]
	[InlineData("src/a`b.cs", "<code>src&#47;a&#96;b&#46;cs</code>")]  // a backtick can't close the span early
	[InlineData("src/<x>.cs", "<code>src&#47;&#60;x&#62;&#46;cs</code>")]
	[InlineData(" a.md ", "<code> a&#46;md </code>")]  // a code span would trim one space from each end
	public void File_names_are_code_that_cannot_break_out(string name, string expected)
	{
		List<JsonNode?> files = [new JsonObject { ["filename"] = name, ["additions"] = 1, ["deletions"] = 0 }];
		Assert.Equal($"- {expected} (+1 / -0)", Py.SplitLines(ReleasePost.RenderFiles(files))[^1]);
	}

	[Fact]
	public void Code_keeps_its_text()
	{
		var body = "```cs\nList<T> x = new();\n```\n\nUse `List<T>`, or `plain`.\n\n    indented <b>";
		Assert.Equal(
				"```cs\nList<T> x = new();\n```\n\n"
				+ "Use <code>List&#60;T&#62;</code>, or `plain`.\n\n"
				+ "```text\nindented <b>\n```",
				ReleasePost.SanitizeMarkdown(body));
	}

	[Fact]
	public void A_code_block_is_rewritten_as_a_fence_both_parsers_agree_on()
	{
		// Unclosed, a two-word info string (kramdown reads no fence there), and backticks inside.
		Assert.Equal("`````js\n````\n<b>\n`````", ReleasePost.SanitizeMarkdown("~~~js title\n````\n<b>"));
	}

	[Fact]
	public void A_code_block_in_a_list_moves_out_of_it()
	{
		var body = "1. Run:\n   ```bash\n   cat <file>\n   ```\n2. Next";
		Assert.Equal("1. Run:\n\n```bash\ncat <file>\n```\n\n2. Next", ReleasePost.SanitizeMarkdown(body));
	}

	[Fact]
	public void A_fence_line_that_was_not_a_fence_is_escaped()
	{
		Assert.Equal("&lt;div>\n\\```\n&lt;/div>", ReleasePost.SanitizeMarkdown("<div>\n```\n</div>"));
	}

	[Fact]
	public void Html_comments_are_dropped()
	{
		Assert.Equal("a  b\n\nc", ReleasePost.SanitizeMarkdown("a <!-- hidden --> b\n<!--\nmulti\n-->\nc"));
	}

	public static readonly TheoryData<string, string> UnsafeLinks = new()
	{
		{ "[a](javascript:alert(1))", "[a](#javascript:alert(1))" },
		{ "[a](JaVa&#115;cript:x)", "[a](#JaVa&#115;cript:x)" },  // entities decode in a destination
		{ "[a](java&#9;script:x)", "[a](#java&#9;script:x)" },  // browsers drop the tab
		// Padding can push the scheme any distance from the start: the whole destination is read.
		{ "[a](j" + Times("&#9;", 60) + "avascript:x)", "[a](#j" + Times("&#9;", 60) + "avascript:x)" },
		{ "[a](java&#" + Times("0", 250) + "9;script:x)", "[a](#java&#" + Times("0", 250) + "9;script:x)" },
		{ "![a](data:text/html,x)", "![a](#data:text/html,x)" },
		{ "[a](<vbscript:x>)", "[a](#vbscript:x)" },
		// Browsers drop a control character or DEL, so none ends the scheme's read (mpaulosky/Application_Factory#211).
		{ "[a](<\x01javascript:x>)", "[a](#\x01javascript:x)" },
		{ "[a](\x01javascript:x)", "[a](#\x01javascript:x)" },
		{ "[a](java\x7fscript:x)", "[a](#java\x7fscript:x)" },
		{ "[a]: \x1fjavascript:x", "[a]: #\x1fjavascript:x" },
		{ "[a](<\x01https://example.com>)", "[a](\x01https://example.com)" },
		{ "[a]: javascript:x", "[a]: #javascript:x" },
		{ "> [a]:\n> javascript:x", "> [a]:\n> #javascript:x" },
		{ "[a](https://example.com) [b](mailto:a@b.c) [c](#top) [d](docs/x.md)",
			"[a](https://example.com) [b](mailto:a@b.c) [c](#top) [d](docs/x.md)" },
		{ "[^1]: Note: a footnote", "[^1]: Note: a footnote" },
		{ "> [^note]: javascript: is how it starts", "> [^note]: javascript: is how it starts" },
		// Only a footnote definition alone at the start of its line is exempt; an escaped "\\[^" is a reference label.
		{ "[open][label \\[^x]\n[label \\[^x]: javascript:x", "[open][label \\[^x]\n[label \\[^x]: #javascript:x" },
		{ "[a [^x]: javascript:x", "[a [^x]: #javascript:x" },
		{ "x [^1]: javascript:x", "x [^1]: #javascript:x" },
		// kramdown's footnote IDs are \w[\w-]*; any other "[^...]:" is a reference definition.
		{ "[^!]: javascript:x", "[^!]: #javascript:x" },
		{ "[^a:b]: javascript:x", "[^a:b]: #javascript:x" },
		{ "[^-x]: javascript:x", "[^-x]: #javascript:x" },
		{ "[^é]: javascript:x", "[^é]: #javascript:x" },
		{ "[^note-2_b]: javascript: is how it starts", "[^note-2_b]: javascript: is how it starts" },
	};

	[Theory]
	[MemberData(nameof(UnsafeLinks))]
	public void Link_destinations_with_unsafe_schemes_are_neutralized(string text, string expected)
	{
		Assert.Equal(expected, ReleasePost.SanitizeMarkdown(text));
	}

	// A tab, line break, other C0 control or DEL inside the scheme, or a space before it, doesn't end its read
	// (mpaulosky/Application_Factory#211): kramdown writes them into the href, and browsers drop them.
	[Theory]
	[InlineData("[a](<\x01javascript:alert(1)>)", "[a](<#\x01javascript:alert(1)>)")]
	[InlineData("[a](<java\tscript:alert(1)>)", "[a](<#java\tscript:alert(1)>)")]
	[InlineData("[a](<java\nscript:x>)", "[a](<#java\nscript:x>)")]
	[InlineData("[a](<java\rscript:x>)", "[a](<#java\rscript:x>)")]
	[InlineData("[a](< \x01 javascript:x>)", "[a](<# \x01 javascript:x>)")]
	[InlineData("[a](<\x01 \x7fhttps://example.com>) [b](<\tdocs/x.md>)", "[a](<\x01 \x7fhttps://example.com>) [b](<\tdocs/x.md>)")]
	public void Controls_in_a_scheme_dont_hide_it(string text, string expected)
	{
		Assert.Equal(expected, ReleasePost.NeutralizeLinks(text));
	}

	[Fact]
	public void Autolinks_keep_working()
	{
		Assert.Equal("See <https://example.com/a?b=1> or <mailto:a@b.c>, not &lt;a@b.c>.",
				ReleasePost.SanitizeMarkdown("See <https://example.com/a?b=1> or <mailto:a@b.c>, not <a@b.c>."));
	}

	[Theory]
	[InlineData("para\n{: onclick=\"alert(1)\"}", "para\n&#123;: onclick=\"alert(1)\"}")]
	[InlineData("*a*{: .x}", "*a*&#123;: .x}")]
	[InlineData("{::nomarkdown}x{:/}", "&#123;::nomarkdown}x&#123;:/}")]
	[InlineData("\\{: .x}", "\\&#123;: .x}")]
	[InlineData("`{:x}`", "<code>&#123;&#58;x&#125;</code>")]
	public void Kramdown_attribute_lists_and_extensions_are_neutralized(string text, string expected)
	{
		Assert.Equal(expected, ReleasePost.SanitizeMarkdown(text));
	}

	[Fact]
	public void The_post_is_liquid_raw_and_its_text_cannot_end_the_raw_block()
	{
		var body = "Jinja: {{ x }} {% if y %}\n\n```\n{% endraw %}{%- endraw -%}\n```";
		var post = ReleasePost.RenderPost(new JsonObject { ["number"] = 7, ["body"] = body }, "T", "v1", "2026-09-26", [], [], null, "m");
		var afterFrontMatter = post.Split("---\n", 3)[2];
		Assert.StartsWith("<!-- {% raw %} -->\n# T\n", afterFrontMatter, StringComparison.Ordinal);
		Assert.EndsWith("\n<!-- {% endraw %} -->\n", post, StringComparison.Ordinal);
		Assert.Equal(3, Count(post, "{% endraw %}"));  // the closing tag and one per broken {%
		Assert.Contains("{% endraw %}{{ '{%' }}{% raw %} endraw %}", post);
		Assert.Contains("{% endraw %}{{ '{%' }}{% raw %}- endraw -%}", post);
	}

	[Fact]
	public void Excerpts_read_escaped_text_as_the_text_it_stands_for()
	{
		var post = ReleasePost.RenderPost(new JsonObject { ["number"] = 7, ["body"] = "Returns `List<T>` for <id> & {: x}." }, "T", "v1", "2026-09-26", [], [], null, "m");
		Assert.Equal("Returns List<T> for <id> & {: x}.", ReleasePost.PostExcerpt(post));
	}

	public static readonly TheoryData<string, string> Unescapes = new()
	{
		{ "&#9; &#x9; &#0009; &#x0000a;", "\t \t \t \n" },
		{ "&#" + Times("0", 5000) + "9;", "\t" },  // leading zeros past Python's 4300-digit limit
		{ "&#x" + Times("0", 5000) + "41;", "A" },
		{ "&#" + Times("9", 5000) + ";", "\ufffd" },  // no character has a number that long
		{ "&#; &#x; a &amp; b", "&#; &#x; a & b" },
	};

	[Theory]
	[MemberData(nameof(Unescapes))]
	public void Safe_unescape_never_raises(string text, string expected)
	{
		Assert.Equal(expected, ReleasePost.SafeUnescape(text));
	}

	[Fact]
	public void An_oversized_character_reference_is_sanitized_not_fatal()
	{
		// html.unescape raises on it, which would stop the release before its post was written.
		var padded = "&#" + Times("0", 5000) + "9;";
		Assert.Equal($"[a](#java{padded}script:x)", ReleasePost.NeutralizeLinks($"[a](java{padded}script:x)"));
		var post = ReleasePost.RenderPost(new JsonObject { ["number"] = 7, ["body"] = $"Text {padded} [a](java{padded}script:x)" }, "T", "v1", "2026-09-26", [], [], null, "m");
		Assert.Contains("](#java", post);
		Assert.StartsWith("Text", ReleasePost.PostExcerpt(post), StringComparison.Ordinal);
	}
}
