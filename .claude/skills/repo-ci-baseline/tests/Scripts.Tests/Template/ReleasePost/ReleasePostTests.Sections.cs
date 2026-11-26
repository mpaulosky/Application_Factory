using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baseline.Tests;

// test_release_post.py's tests from the deterministic sections through the README's releases section: render_post, nest_headings and
// the section readers, the commits and files sections, the diff, the marker rewriting, the docs/README.md links and the blog post cards.
public sealed partial class ReleasePostTests
{
	// Deterministic sections

	[Fact]
	public void Slugify_matches_the_workflow_slug()
	{
		Assert.Equal("feat-sandcastle-hold-back-issues-whose-blockers-haven-t-landed",
				ReleasePost.Slugify("feat(sandcastle): Hold back issues whose blockers haven't landed"));
		Assert.Equal("pr-update", ReleasePost.Slugify("!!!"));
	}

	[Fact]
	public void Post_title_is_a_valid_yaml_double_quoted_scalar()
	{
		var post = ReleasePost.RenderPost(J("""{"number": 7}"""), "Fix \"quotes\" and a trailing \\", "v1.2.3", "2026-09-26", [], [], null, "m");
		Assert.Contains("post_title: \"Fix \\\"quotes\\\" and a trailing \\\\\"\n", post);
	}

	// Python's \s in a str pattern: .NET's \s and the four separators \x1c-\x1f.
	const string S = @"[\s\x1c-\x1f]";
	static readonly Regex FenceMarker = new(@"^ {0,3}(`{3,}|~{3,})(.*)");
	static readonly Regex Containers = new($@"^(?:{S}*(?:>|[-+*](?={S})|\d+[.)](?={S})))*{S}*");
	static readonly Regex AtxHeading = new($@"^#{{1,6}}({S}|$)");

	// Levels of every line in a rendered post that looks like an ATX heading.
	//
	// This deliberately over-counts rather than re-implementing the parser under
	// test: it strips all quote and list markers and leading indentation from
	// every line, and skips only fences and HTML comments that open at column 0,
	// which are unambiguously top-level (an indented opener may belong to a list
	// item and end with it). So it can never miss a heading (the invariant tests
	// need exactly that), but a "#" line in indented code or in a fence inside a
	// container counts too, so the invariant tests don't use those.
	// nest_headings' exact behaviour inside containers is pinned by its own tests.
	static List<int> HeadingLevels(string post)
	{
		var body = post.Split("---\n", 3)[2];
		string? fence = null;
		var comment = false;
		List<int> levels = [];
		foreach (var line in Py.SplitLines(body))
		{
			if (comment)
			{
				comment = !line.Contains("-->", StringComparison.Ordinal);
				continue;
			}
			var marker = FenceMarker.Match(line);
			if (fence is not null)
			{
				if (marker.Success && marker.Groups[1].Value[0] == fence[0] && marker.Groups[1].Length >= fence.Length && Py.Strip(marker.Groups[2].Value).Length == 0)
					fence = null;
				continue;
			}
			if (marker.Success && marker.Groups[1].Index == 0)
			{
				fence = marker.Groups[1].Value;
				continue;
			}
			if (line.StartsWith("<!--", StringComparison.Ordinal))
			{
				comment = !line[4..].Contains("-->", StringComparison.Ordinal);
				continue;
			}
			var text = Containers.Replace(line, "", 1);
			if (AtxHeading.IsMatch(text))
				levels.Add(text.Length - text.TrimStart('#').Length);
		}
		return levels;
	}

	[Theory]
	[InlineData("> # A\n- # B\n1. # C\n> - > ## D", new[] { 1, 1, 1, 1, 2 })]
	[InlineData("- # A\n  #### B\n    ### C", new[] { 1, 1, 4, 3 })] // continuation lines, at any depth
	[InlineData("- > ```\n  > # counted\n  > ```", new[] { 1, 1 })] // container fences over-count; never miss
	[InlineData("```\n# not a heading\n```\n<!--\n# nor this\n-->\n## Real", new[] { 1, 2 })]
	// An indented opener may belong to a list item and end with it, so it isn't tracked.
	[InlineData("- item\n  ```\n# Root", new[] { 1, 1 })]
	[InlineData("- item\n  <!--\n# Root", new[] { 1, 1 })]
	public void Heading_levels_counts_every_heading_looking_line(string body, int[] expected) =>
			Assert.Equal(expected, HeadingLevels($"---\nx: 1\n---\n# T\n{body}"));

	static void AssertOneH1AndNoSkippedLevels(string post)
	{
		var levels = HeadingLevels(post);
		Assert.Equal(1, levels[0]);
		Assert.Equal(1, levels.Count(level => level == 1));
		Assert.All(levels.Zip(levels.Skip(1)), pair => Assert.True(pair.Second - pair.First <= 1));
	}

	[Fact]
	public void Post_starts_with_one_h1_and_never_skips_a_level()
	{
		var commits = Many("""[{"sha": "abc1234def", "commit": {"message": "Add a thing"}}]""");
		var files = Many("""[{"filename": "src/A.cs", "additions": 1, "deletions": 0}]""");
		var post = ReleasePost.RenderPost(J("""{"number": 7, "body": "Why"}"""), "Add a thing", "v1.2.3", "2026-09-26", commits, files, "Short.", "m");
		AssertOneH1AndNoSkippedLevels(post);
	}

	[Theory]
	[InlineData("# Context\n\nWhy.\n\n## Details\n\nHow.")]
	[InlineData("#### Details\n\nHow.")]
	[InlineData("### Deep first\n\n# Then shallow\n\n###### Then very deep")]
	[InlineData("## Plan\n\n```bash\n# a shell comment, not a heading\n```\n\n### Steps")]
	[InlineData("```\n```bash\n# a fence line with an info string doesn't close the fence\n```\n\n# After")]
	[InlineData("> # Context\n\n- # Context\n\n1. # Context")]
	[InlineData("<!--\n```\n-->\n# Context")]
	[InlineData("- # A\n  #### B")]
	public void Headings_in_the_description_and_summary_stay_nested(string description)
	{
		var summary = "# The gist\n\nShort.\n\n### Detail";
		var post = ReleasePost.RenderPost(new JsonObject { ["number"] = 7, ["body"] = description }, "T", "v1.2.3", "2026-09-26", [], [], summary, "m");
		AssertOneH1AndNoSkippedLevels(post);
	}

	[Fact]
	public void Nest_headings_keeps_relative_levels_and_fenced_code()
	{
		var text = "# A\n\n```\n# not a heading\n```\n\n### B\n\n~~~\n## still code\n~~~\n\n## C\n#hashtag";
		Assert.Equal("### A\n\n```\n# not a heading\n```\n\n#### B\n\n~~~\n## still code\n~~~\n\n#### C\n#hashtag", ReleasePost.NestHeadings(text));
	}

	[Fact]
	public void Nest_headings_converts_setext_headings_but_keeps_thematic_breaks()
	{
		var text = "Title\n===\n\nText.\n\n---\n\n- item\n---\n\nSub\n---";
		Assert.Equal("### Title\n\nText.\n\n---\n\n- item\n---\n\n#### Sub", ReleasePost.NestHeadings(text));
	}

	[Fact]
	public void A_closing_fence_followed_by_a_thematic_break_stays_a_fence()
	{
		var text = "```\ncode\n```\n---\n# After";
		Assert.Equal("```\ncode\n```\n---\n### After", ReleasePost.NestHeadings(text));
	}

	[Fact]
	public void Adjacent_thematic_breaks_are_not_setext_text()
	{
		var text = "Text.\n\n---\n---\n\n# After";
		Assert.Equal("Text.\n\n---\n---\n\n### After", ReleasePost.NestHeadings(text));
	}

	[Fact]
	public void A_setext_heading_takes_its_whole_paragraph()
	{
		var text = "Release\nNotes\n===\n\nBody.\n\nSub\nheading\n---\n\n- item\nlazy continuation\n---";
		Assert.Equal("### Release Notes\n\nBody.\n\n#### Sub heading\n\n- item\nlazy continuation\n---", ReleasePost.NestHeadings(text));
	}

	[Theory]
	// Table-like and #hashtag lines are paragraph text, so the underline takes them too.
	[InlineData("text\n| a | b |\n===", "### text | a | b |")]
	[InlineData("#hashtag\nPara\n---", "### #hashtag Para")]
	// A lone === line is text, and becomes part of the heading below it.
	[InlineData("===\nTitle\n---", "### === Title")]
	// Text continuing a list item or quote can't be underlined into a heading.
	[InlineData("1. one\n===\ntext\n---", "1. one\n===\ntext\n---")]
	[InlineData("> quote\n===\n===", "> quote\n===\n===")]
	[InlineData("- item\n\n    indented\nPara\n---", "- item\n\n    indented\nPara\n---")]
	// A quote closes at a blank line; a list closes at a thematic break.
	[InlineData("> quote\n\nPara\n---", "> quote\n\n### Para")]
	[InlineData("- item\ntext\n---\nPara\n---", "- item\ntext\n---\n### Para")]
	public void Setext_headings_follow_commonmark_paragraphs(string text, string expected) =>
			Assert.Equal(expected, ReleasePost.NestHeadings(text));

	[Theory]
	[InlineData("> # Context\n> Why.", "> ### Context\n> Why.")]
	[InlineData("- # Context\n  Why.", "- ### Context\n  Why.")]
	[InlineData("> - # Deep\n>   ## Deeper", "> - ### Deep\n>   #### Deeper")]
	// Setext headings inside a quote or list item, and a lazy === that isn't one.
	[InlineData("> Title\n> ===", "> ### Title")]
	[InlineData("- Title\n  ===", "- ### Title")]
	[InlineData("> Title\n===", "> Title\n===")]
	// Fence markers inside an HTML comment are comment text, not code.
	[InlineData("<!--\n```\n-->\n# Context", "<!--\n```\n-->\n### Context")]
	[InlineData("<!-- # not a heading -->\n# Context", "<!-- # not a heading -->\n### Context")]
	// An HTML comment inside a list item is opaque too, fence markers and all.
	[InlineData("- <!--\n  ```\n  -->\n# Context", "- <!--\n  ```\n  -->\n### Context")]
	// Tabs expand to 4-column stops, so a tab-indented line continues the list item.
	[InlineData("-\t# First\n\t# Second", "-   ### First\n    ### Second")]
	// A line that fails to continue a quote or list item may open any list item
	// (GitHub's renderer and CommonMark's reference parser agree); inside the
	// item, only "1." interrupts.
	[InlineData("> paragraph\n2. # Context", "> paragraph\n2. ### Context")]
	[InlineData("- a\n2. # b", "- a\n2. ### b")]
	[InlineData("- a\n  2. # b", "- a\n  2. # b")]
	[InlineData("-\n  # A", "-\n  ### A")]
	public void Nest_headings_reaches_into_containers_and_skips_html_comments(string text, string expected) =>
			Assert.Equal(expected, ReleasePost.NestHeadings(text));

	[Theory]
	[InlineData("<!--\n```\n-->")]
	[InlineData("- <!--\n  ```\n  -->")]
	public void A_fence_in_an_html_comment_does_not_swallow_the_rest_of_the_post(string comment)
	{
		var body = $"{comment}\n\nThe real description.";
		var commits = Many("""[{"sha": "abc1234def", "commit": {"message": "Add a thing"}}]""");
		var post = ReleasePost.RenderPost(new JsonObject { ["number"] = 7, ["body"] = body }, "T", "v1.2.3", "2026-09-26", commits, [], null, "m");
		Assert.Equal("The real description.", ReleasePost.PostExcerpt(post));
		Assert.DoesNotContain("Add a thing", ReleasePost.Section(post, "PR description"));
	}

	[Fact]
	public void Deeply_nested_containers_do_not_exhaust_the_stack()
	{
		// A size-bounded PR body can nest far deeper than Python's recursion limit.
		Assert.EndsWith("> ### Context", ReleasePost.NestHeadings(string.Concat(Enumerable.Repeat("> ", 1100)) + "# Context"));
		Assert.EndsWith("- ### Context", ReleasePost.NestHeadings(string.Concat(Enumerable.Repeat("- ", 1100)) + "# Context"));
	}

	// The pytest's shape lambdas, by their index: ReleasePostTimingTests times them.
	internal static readonly Func<int, string>[] Shapes =
	[
		n => Times("- ", n) + "# Context",
		n => Times("> ", n) + "# Context",
		n => Times("- ", n) + "x" + Times("\n", n) + "  text",
		n => "> x\n" + Times("lazy\n", n),
	];

	public static readonly TheoryData<int> SectionShapeIndexes = [.. Enumerable.Range(0, Shapes.Length)];

	[Theory]
	// A "#" line inside a raw HTML block is text: GitHub renders it literally.
	[InlineData("<details>\n# x\n</details>", "<details>\n# x\n</details>")]
	[InlineData("<div>\n## y\n</div>\n\n# z", "<div>\n## y\n</div>\n\n### z")]
	[InlineData("<pre>\n# code\n</pre>\n# after", "<pre>\n# code\n</pre>\n### after")]
	[InlineData("- <div>\n  # x\n# root", "- <div>\n  # x\n### root")]
	// Block-tag blocks end at a blank line, so Markdown after one is Markdown again.
	[InlineData("<details>\n<summary>S</summary>\n\n# x\n\n</details>", "<details>\n<summary>S</summary>\n\n### x\n\n</details>")]
	[InlineData("<span>\n# inside\n\n# after", "<span>\n# inside\n\n### after")]
	// A block tag interrupts a paragraph; any other tag doesn't...
	[InlineData("text\n<details>\n# w\n</details>", "text\n<details>\n# w\n</details>")]
	[InlineData("text\n<span>\n# v", "text\n<span>\n### v")]
	// ...unless the line fails to continue its quote, when any HTML block can open.
	[InlineData("> quote\n<span>\n# after", "> quote\n<span>\n# after")]
	// A comment can close on the line that opens it.
	[InlineData("<!-->\n# after", "<!-->\n### after")]
	// Processing instructions, declarations and CDATA stay opaque until their own marker.
	[InlineData("<?php\n# x\n?>\n# after", "<?php\n# x\n?>\n### after")]
	[InlineData("<!DOCTYPE html\n# x\n>\n# after", "<!DOCTYPE html\n# x\n>\n### after")]
	[InlineData("<![CDATA[\n# x\n]]>\n# after", "<![CDATA[\n# x\n]]>\n### after")]
	// A declaration needs an uppercase letter: GitHub reads "<!doctype" as text.
	[InlineData("<!doctype html\n# x", "<!doctype html\n### x")]
	// The tag grammar is ASCII: a non-breaking space or a long s ("\u017f", which
	// Unicode case-folds to "s") makes the line text, so GitHub reads the "#" after it
	// as a heading. ASCII case-insensitivity still applies.
	[InlineData("<div\u00a0>\n# x", "<div\u00a0>\n### x")]
	[InlineData("<\u017ftyle>\n# x", "<\u017ftyle>\n### x")]
	[InlineData("<a\u00a0href=\"x\">\n# y", "<a\u00a0href=\"x\">\n### y")]
	[InlineData("<DIV>\n# x", "<DIV>\n# x")]
	// Only spaces and tabs make a blank line: a non-breaking space doesn't end the block.
	[InlineData("<div>\n\u00a0\n# x", "<div>\n\u00a0\n# x")]
	[InlineData("<div>\n \t\n# x", "<div>\n \t\n### x")]
	public void Nest_headings_leaves_raw_html_blocks_alone(string text, string expected) =>
			Assert.Equal(expected, ReleasePost.NestHeadings(text));

	[Fact]
	public void An_html_block_never_becomes_the_excerpt()
	{
		var body = "<details>\n<summary>Notes</summary>\n\nThe real description.\n\n</details>";
		var post = ReleasePost.RenderPost(new JsonObject { ["number"] = 7, ["body"] = body }, "T", "v1.2.3", "2026-09-26", [], [], null, "m");
		Assert.Equal("The real description.", ReleasePost.PostExcerpt(post));
	}

	[Theory]
	// PR bodies from the GitHub API often use CRLF; GitHub reads these as two H1s.
	[InlineData("Title\r\n===\r\n\r\n# x", "### Title\n\n### x")]
	[InlineData("- item\r\n  ---\r\n", "- ### item\n")]
	[InlineData("<div>\r\n\r\n# x", "<div>\n\n### x")]
	[InlineData("Title\r===", "### Title")] // a lone CR is a line ending too
																					// A non-breaking space is text: this line continues the paragraph instead of ending it.
	[InlineData("para\n\u00a0\n# y", "para\n\u00a0\n### y")]
	// List numbers are ASCII digits: GitHub reads these lines as paragraph text.
	[InlineData("\u0661. # x", "\u0661. # x")]
	[InlineData("\uff11. # x", "\uff11. # x")]
	public void Nest_headings_reads_line_endings_and_blank_lines_as_commonmark_does(string text, string expected) =>
			Assert.Equal(expected, ReleasePost.NestHeadings(text));

	[Fact]
	public void Code_lines_treats_a_trailing_cr_as_part_of_the_line_ending() =>
			Assert.Equal(new HashSet<int> { 0 }, ReleasePost.CodeLines(["<div>\r", "\r", "# x"]));

	[Fact]
	public void A_fence_line_with_an_info_string_does_not_close_the_fence()
	{
		var text = "```\n```python\n# comment\n```\n# Heading";
		Assert.Equal("```\n```python\n# comment\n```\n### Heading", ReleasePost.NestHeadings(text));
	}

	[Fact]
	public void Section_ignores_a_summary_heading_from_inside_the_pr_description()
	{
		var newPost = ReleasePost.RenderPost(
				J("""{"number": 7, "body": "## Summary\n\nFrom the PR body."}"""), "T", "v1.2.3", "2026-09-26", [], [], null, "m");
		var oldPost = "## T\n\n### PR description\n\n## Summary\n\nFrom the PR body.\n\n### Commits\n";
		Assert.Equal("", ReleasePost.Section(newPost, "Summary"));
		Assert.Equal("", ReleasePost.Section(oldPost, "Summary"));
	}

	[Theory]
	[InlineData("#", "##")]
	[InlineData("##", "###")]
	public void Excerpt_skips_a_summary_heading_inside_a_fenced_example(string title, string level)
	{
		var fenced = $"```markdown\n{level} Summary\n\nCode sample, not a summary.\n```";
		var post = $"---\npost_title: \"T\"\n---\n{title} T\n\n{level} PR description\n\n"
				+ $"The real description.\n\n{fenced}\n\n{level} Commits\n\n- Add a thing\n";
		Assert.Equal("", ReleasePost.Section(post, "Summary"));
		Assert.Equal("The real description.", ReleasePost.PostExcerpt(post));
	}

	[Fact]
	public void Nested_description_headings_do_not_end_the_section()
	{
		var post = ReleasePost.RenderPost(J("""{"number": 7, "body": "# Context\n\nWhy."}"""), "T", "v1.2.3", "2026-09-26", [], [], null, "m");
		Assert.Equal("Why.", ReleasePost.FirstParagraph(ReleasePost.Section(post, "PR description")));
	}

	[Theory]
	[InlineData("#", "##")]
	[InlineData("##", "###")]
	public void Section_reads_posts_from_before_and_after_the_h1_title(string title, string level)
	{
		// Posts written before the title became an H1 used a ## title and ### section headings.
		var text = $"{title} T\n\n{level} Summary\n\nThe gist.\n\n{level} PR description\n\nWhy.\n";
		Assert.Equal("The gist.", ReleasePost.FirstParagraph(ReleasePost.Section(text, "Summary")));
		Assert.Equal("Why.", ReleasePost.FirstParagraph(ReleasePost.Section(text, "PR description")));
	}

	[Fact]
	public void Commits_section_lists_subjects_only()
	{
		var section = ReleasePost.RenderCommits(new FakeGitHub().Commits(42));
		Assert.Equal(
				"## Commits\n\n"
				+ "- feat(ui): Add theme tokens (`abcdef1`)\n"
				+ "- test(ui): Cover the theme switch (`1234567`)\n",
				section);
	}

	[Fact]
	public void Commits_section_without_commits() => Assert.Contains("No commits were found.", ReleasePost.RenderCommits([]));

	[Fact]
	public void Files_section_groups_by_area_in_fixed_order()
	{
		var section = ReleasePost.RenderFiles(new FakeGitHub().Files(42));
		Assert.Equal(
				"## Files changed\n\n"
				+ "### src/\n\n"
				+ "- `src/Web/Theme.cs` (+10 / -2)\n\n"
				+ "### tests/\n\n"
				+ "- `tests/Web.Tests/ThemeTests.cs` (+5 / -0)\n\n"
				+ "### .github/\n\n"
				+ "- `.github/workflows/ci.yml` (+1 / -1)\n\n"
				+ "### .sandcastle/\n\n"
				+ "- `.sandcastle/main.mts` (+3 / -3)\n\n"
				+ "### docs/\n\n"
				+ "- `docs/adr/0001.md` (+4 / -0)\n\n"
				+ "### other\n\n"
				+ "- `Directory.Packages.props` (+1 / -0)\n"
				+ "- `package-lock.json` (+900 / -800)\n",
				section);
	}

	[Theory]
	[InlineData("package-lock.json", true)]
	[InlineData("web/package-lock.json", true)]
	[InlineData("pnpm-lock.yaml", true)]
	[InlineData("src/AtelierStore.Web/pnpm-lock.yaml", true)]
	[InlineData("poetry.lock", true)]
	[InlineData("docs/blogs/2026-09-24-pr-1-x.md", true)]
	[InlineData("README.md", true)]
	[InlineData("docs/README.md", true)]
	[InlineData("src/README.md", false)]
	[InlineData("docs/adr/0001.md", false)]
	[InlineData("src/Web/Theme.cs", false)]
	public void Diff_exclusions(string path, bool excluded) => Assert.Equal(excluded, ReleasePost.IsExcludedFromDiff(path));

	[Fact]
	public void Build_diff_skips_excluded_files()
	{
		var diff = ReleasePost.BuildDiff(new FakeGitHub().Files(42));
		Assert.Contains("diff --git a/src/Web/Theme.cs b/src/Web/Theme.cs", diff);
		Assert.DoesNotContain("package-lock.json", diff);
	}

	[Fact]
	public void Build_diff_truncates_with_a_note()
	{
		List<JsonNode?> files = [new JsonObject { ["filename"] = "src/big.cs", ["additions"] = 1, ["deletions"] = 0, ["patch"] = "+" + new string('x', 200) }];
		var diff = ReleasePost.BuildDiff(files, limit: 100);
		Assert.StartsWith("diff --git a/src/big.cs b/src/big.cs", diff);
		Assert.Contains("[Diff truncated", diff);
		Assert.True(diff.Length < 200);
	}

	// Marker rewriting

	[Fact]
	public void Replace_between_keeps_markers_and_indent()
	{
		var text = "a\n  <!-- X_START -->\n  old\n  <!-- X_END -->\nb\n";
		var output = ReleasePost.ReplaceBetween(text, "X", ["<p>1</p>", "<p>2</p>"]);
		Assert.Equal("a\n  <!-- X_START -->\n  <p>1</p>\n  <p>2</p>\n  <!-- X_END -->\nb\n", output);
		Assert.Equal(output, ReleasePost.ReplaceBetween(output!, "X", ["<p>1</p>", "<p>2</p>"]));
	}

	[Fact]
	public void Replace_between_without_markers_returns_none() => Assert.Null(ReleasePost.ReplaceBetween("no markers", "X", ["y"]));

	[Fact]
	public void Run_leaves_a_missing_docs_readme_missing()
	{
		MakeRepo(tmp);
		File.Delete(Path.Join(tmp, "docs", "README.md"));
		Run(tmp);

		Assert.Contains("v0.0.3", Read(Path.Join(tmp, "README.md")));
		Assert.False(File.Exists(Path.Join(tmp, "docs", "README.md")));
	}

	[Fact]
	public void Run_writes_readme_and_index_tables()
	{
		MakeRepo(tmp);
		Run(tmp);

		var readme = Read(Path.Join(tmp, "README.md"));
		var post = "2026-09-24-pr-42-feat-ui-add-the-dark-theme.md";
		Assert.Contains($"| [v0.0.3](https://github.com/{Repo}/releases/tag/v0.0.3) | 2026-09-24 | feat(ui): Add the \"dark\" theme | [Post](https://github.com/{Repo}/blob/main/docs/blogs/{post}) |", readme);
		Assert.Contains($"| [v0.0.2](https://github.com/{Repo}/releases/tag/v0.0.2) | 2026-09-20 | fix: Older change | — |", readme);
		Assert.Contains("| [v0.0.1](https://github.com/octo/demo/releases/tag/v0.0.1) | 2026-09-19 | Release v0.0.1 | — |", readme);
		Assert.EndsWith("## License\n", readme);
		Assert.Equal(readme, Read(Path.Join(tmp, "docs", "README.md")));

		var index = Read(Path.Join(tmp, "docs", "index.html"));
		Assert.DoesNotContain("old", index);
		Assert.Contains($"<td><a href=\"https://github.com/{Repo}/releases/tag/v0.0.3\">v0.0.3</a></td>", index);
		Assert.Contains("<td>feat(ui): Add the &quot;dark&quot; theme</td>", index);
		Assert.Contains($"<td><a href=\"https://github.com/{Repo}/blob/main/docs/blogs/{post}\">Post</a></td>", index);
		Assert.Contains("<td>—</td>", index);
		// Blog post cards: date, release, linked title, excerpt, source PR
		Assert.Contains("<time datetime=\"2026-09-24\">24 Sep 2026</time><span class=\"post-tag\">v0.0.3</span>", index);
		Assert.Contains($"<h3 class=\"post-title\"><a href=\"https://github.com/{Repo}/blob/main/docs/blogs/{post}\">feat(ui): Add the &quot;dark&quot; theme</a></h3>", index);
		Assert.Contains("<p class=\"post-excerpt\">Adds a theme.</p>", index);
		Assert.Contains($"<p class=\"post-source\"><a href=\"https://github.com/{Repo}/pull/42\">PR #42</a></p>", index);
	}

	// docs/README.md links (from TicketManager #104)

	[Theory]
	// A docs/ prefix is dropped; the docs folder itself becomes ./
	[InlineData("| [Post](docs/blogs/2026-09-28-pr-1-x.md) |\n", "| [Post](blogs/2026-09-28-pr-1-x.md) |\n")]
	[InlineData("[docs](docs/) and [docs](docs)\n", "[docs](./) and [docs](./)\n")]
	// Anything else relative goes up one level.
	[InlineData("[License](LICENSE) and [web](./src/Web)\n", "[License](../LICENSE) and [web](../src/Web)\n")]
	[InlineData("[props](Directory.Packages.props)\n", "[props](../Directory.Packages.props)\n")]
	// Images, titles and angle-bracketed targets keep their form.
	[InlineData("![logo](docs/logo.png \"Logo\")\n", "![logo](logo.png \"Logo\")\n")]
	[InlineData("[x](<docs/a b.md>)\n", "[x](<a b.md>)\n")]
	// Reference definitions and HTML src/href attributes.
	[InlineData("[post]: docs/blogs/README.md\n", "[post]: blogs/README.md\n")]
	[InlineData("<img src=\"docs/banner.png\" alt=\"\">\n", "<img src=\"banner.png\" alt=\"\">\n")]
	[InlineData("<a href='CONTRIBUTING.md'>c</a>\n", "<a href='../CONTRIBUTING.md'>c</a>\n")]
	// Balanced parentheses belong to the destination; every title form is kept.
	[InlineData("[x](docs/a_(b).md) [y](a_(b)_(c).md)\n", "[x](a_(b).md) [y](../a_(b)_(c).md)\n")]
	[InlineData("[a](docs/a.md 'A') [b](b.md (B)) [c](docs/c.md \"C\")\n", "[a](a.md 'A') [b](../b.md (B)) [c](c.md \"C\")\n")]
	// Attribute names in any case, spaced "=", unquoted values.
	[InlineData("<img SRC=docs/banner.png>\n", "<img SRC=banner.png>\n")]
	[InlineData("<a href = \"docs/x.md\">x</a>\n", "<a href = \"x.md\">x</a>\n")]
	[InlineData("<img\nsrc=\"docs/a.png\" data-src=\"docs/b.png\">\n", "<img\nsrc=\"a.png\" data-src=\"docs/b.png\">\n")]
	// ../ is relative too, so it goes up one more level.
	[InlineData("[up](../up.md)\n", "[up](../../up.md)\n")]
	public void Rebase_readme_links(string text, string expected) => Assert.Equal(expected, ReleasePost.RebaseReadmeLinks(text));

	[Fact]
	public void Rebase_readme_links_keeps_absolute_anchor_query_and_root_relative_links()
	{
		var text = "[a](https://example.com/docs/x.md) [b](#usage) [c](/docs/x) [d](mailto:me@example.com) [e](?tab=all)\n";
		Assert.Equal(text, ReleasePost.RebaseReadmeLinks(text));
	}

	[Fact]
	public void Rebase_readme_links_leaves_fenced_code_alone()
	{
		var text = "```md\n[x](docs/a.md)\n```\n[y](docs/b.md)\n````\n```\n[z](docs/c.md)\n````\n";
		Assert.Equal("```md\n[x](docs/a.md)\n```\n[y](b.md)\n````\n```\n[z](docs/c.md)\n````\n", ReleasePost.RebaseReadmeLinks(text));
	}

	[Fact]
	public void Rebase_readme_links_leaves_fenced_code_in_block_quotes_alone()
	{
		// A fence closes at its own quote depth, or ends with its quote.
		var text = "> ```md\n> [a](docs/a.md)\n> ```\n> [b](docs/b.md)\n"
				+ ">  > ~~~\n> > [c](docs/c.md)\n> > ~~~\n"
				+ "> ```\n> [d](docs/d.md)\n[e](docs/e.md)\n";
		Assert.Equal(
				"> ```md\n> [a](docs/a.md)\n> ```\n> [b](b.md)\n"
				+ ">  > ~~~\n> > [c](docs/c.md)\n> > ~~~\n"
				+ "> ```\n> [d](docs/d.md)\n[e](e.md)\n",
				ReleasePost.RebaseReadmeLinks(text));
	}

	[Theory]
	[InlineData("Write `[x](docs/a.md)` for a link.\n")]
	[InlineData("Use `<a href=\"docs/a.md\">` or ``<img src=\"docs/b.png\">``.\n")]
	[InlineData("``[x](docs/a.md) with ` inside``\n")]
	[InlineData("> - `[x](docs/a.md)`\n")]
	// A span may cross a line break inside a paragraph.
	[InlineData("See `[x](docs/a.md)\nand [y](docs/b.md)` here.\n")]
	[InlineData("Use `<a\nhref=\"docs/a.md\">` there.\n")]
	// Indented code, at the top level, after a blank line in a list item, and in a quote.
	[InlineData("Example:\n\n    [x](docs/a.md)\n    [y]: docs/b.md\n    <img src=\"docs/c.png\">\n")]
	[InlineData("- item\n\n      [x](docs/a.md)\n")]
	[InlineData(">     [x](docs/a.md)\n")]
	// A fence in a list item, indented past the item's content column.
	[InlineData("1. item\n\n   ```\n   [x](docs/a.md)\n   ```\n")]
	public void Rebase_readme_links_leaves_code_spans_and_indented_code_alone(string text) => Assert.Equal(text, ReleasePost.RebaseReadmeLinks(text));

	[Theory]
	// Links around and after code spans are still rebased, and so is one whose text is code.
	[InlineData("`docs/a.md` is [here](docs/a.md)\n", "`docs/a.md` is [here](a.md)\n")]
	[InlineData("[`a.md`](docs/a.md) and `x` <img src=\"docs/b.png\">\n", "[`a.md`](a.md) and `x` <img src=\"b.png\">\n")]
	// A blank line ends the paragraph, so these backticks pair with nothing across it.
	[InlineData("a `[x](docs/a.md)\n\n[y](docs/b.md)` b\n", "a `[x](a.md)\n\n[y](b.md)` b\n")]
	// So does a blank line with a CRLF ending, which keeps its "\r" in the README's lines.
	[InlineData("a `[x](docs/a.md)\r\n\r\n[y](docs/b.md)` b\r\n", "a `[x](a.md)\r\n\r\n[y](b.md)` b\r\n")]
	// After a span that crossed lines, the rest of its last line is text again.
	[InlineData("`x\ny` [z](docs/a.md)\n", "`x\ny` [z](a.md)\n")]
	// Backtick runs of different lengths don't pair, so this holds no code span.
	[InlineData("``[x](docs/a.md)`\n", "``[x](a.md)`\n")]
	// An escaped backtick opens no span.
	[InlineData("\\`[x](docs/a.md)`\n", "\\`[x](a.md)`\n")]
	// A reference definition is read before code spans, so code in its label doesn't hide it.
	[InlineData("[`a`]: docs/a.md\n", "[`a`]: a.md\n")]
	// Four spaces continuing a paragraph are text, not indented code.
	[InlineData("Text\n    [x](docs/a.md)\n", "Text\n    [x](a.md)\n")]
	// Backticks in a raw HTML block are text, not code spans.
	[InlineData("<div>\n`<img src=\"docs/a.png\">`\n</div>\n", "<div>\n`<img src=\"a.png\">`\n</div>\n")]
	public void Rebase_readme_links_still_rebases_text_beside_code(string text, string expected) =>
			Assert.Equal(expected, ReleasePost.RebaseReadmeLinks(text));

	[Fact]
	public void Run_rebases_links_in_docs_readme_only()
	{
		MakeRepo(tmp);
		var links = "[Architecture](docs/ARCHITECTURE.md), [props](Directory.Packages.props) and [docs](docs).\n";
		var readmePath = Path.Join(tmp, "README.md");
		Write(readmePath, Read(readmePath) + "\n" + links);
		Run(tmp);

		var readme = Read(readmePath);
		Assert.Contains(links, readme);
		var docsReadme = Read(Path.Join(tmp, "docs", "README.md"));
		Assert.Contains("[Architecture](ARCHITECTURE.md), [props](../Directory.Packages.props) and [docs](./).\n", docsReadme);
		Assert.Equal(ReleasePost.RebaseReadmeLinks(readme), docsReadme);
	}

	[Fact]
	public void Blog_post_cards_are_newest_first_and_capped_at_ten()
	{
		var blogDir = Path.Join(MakeRepo(tmp), "docs", "blogs");
		for (var n = 1; n < 13; n++)
			Write(Path.Join(blogDir, $"2026-09-{n:D2}-pr-{n}-post-{n}.md"),
					"---\n"
					+ $"post_title: \"Post <{n}>\"\n"
					+ "tags:\n"
					+ $"  - release:v0.0.{n}\n"
					+ "  - automation\n"
					+ $"post_date: \"2026-09-{n:D2}\"\n"
					+ "---\nbody\n");
		var posts = ReleasePost.ReadBlogPosts(blogDir);
		var lines = ReleasePost.RenderBlogsHtml(posts, Repo);
		var cards = lines.Where(line => Py.Strip(line) == "<li class=\"post-card\">").ToList();
		Assert.Equal(10, cards.Count);
		var htmlText = string.Join("\n", lines);
		Assert.Contains("Post &lt;12&gt;", htmlText);
		Assert.DoesNotContain("Post &lt;2&gt;", htmlText);
		Assert.True(Index(htmlText, "2026-09-12") < Index(htmlText, "2026-09-11"));
	}

	[Fact]
	public void Same_day_posts_are_ordered_by_release_version()
	{
		List<BlogPost> posts =
		[
			new(Date: "2026-09-24", Tag: "v0.0.9", Pr: "4", Title: "a", File: "a.md"),
			new(Date: "2026-09-24", Tag: "v0.0.32", Pr: "28", Title: "b", File: "b.md"),
		];
		Assert.Equal(["v0.0.32", "v0.0.9"], ReleasePost.NewestFirst(posts).Select(p => p.Tag));
	}

	[Fact]
	public void Readme_without_markers_gets_releases_section_after_about()
	{
		MakeRepo(tmp);
		Write(Path.Join(tmp, "README.md"), "# Demo\n\n## About\n\nText.\n\n## License\n");
		Run(tmp);
		var readme = Read(Path.Join(tmp, "README.md"));
		Assert.True(Index(readme, "## About") < Index(readme, "## Releases"));
		Assert.True(Index(readme, "## Releases") < Index(readme, "## License"));
		Assert.Contains($"[All releases →](https://github.com/{Repo}/releases)", readme);
	}
}
