using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baseline.Tests;

// test_release_post.py's tests from test_code_spans_may_cross_line_breaks to the last, in the pytest's order: code spans and paragraphs,
// code found before every prose change, links and reference definitions in a post and in the docs README, and the Unicode line separators.
public sealed partial class ReleasePostTests
{
	// shape.format(a=..., b=...).
	static string SanitizerFormat(string shape, string a, string b) => shape.Replace("{a}", a).Replace("{b}", b);

	// outside_code(): the post's lines, after its front matter, that CommonMark reads as neither fenced nor indented code. Raw HTML blocks
	// count as outside: they are what must not get through.
	static List<string> OutsideCodeLines(string post)
	{
		var lines = post.Split("---\n", 3)[2].Split('\n');
		List<CodeBlock> blocks = [];
		ReleasePost.ScanBlocks([.. lines], [], blocks: blocks);
		var code = blocks.SelectMany(block => Enumerable.Range(block.Lines[0], block.Lines[^1] - block.Lines[0] + 1)).ToHashSet();
		return [.. lines.Where((line, index) => !code.Contains(index))];
	}

	static JsonObject SanitizerCommit(string message) => new() { ["sha"] = "abc1234def", ["commit"] = new JsonObject { ["message"] = message } };

	[Theory]
	// A code span may cross a line break; its text stays code, and a risky one becomes <code>
	// with the break as the space CommonMark renders.
	[InlineData("Use `List<T>\nand Map<K>` here", "Use <code>List&#60;T&#62; and Map&#60;K&#62;</code> here")]
	[InlineData("Use `plain\ntext` and <b>", "Use `plain\ntext` and &lt;b>")]
	// A blank line ends the paragraph, so these backticks open no span and <T> is escaped.
	[InlineData("Use `a <T>\n\nb` c", "Use `a &lt;T>\n\nb` c")]
	public void Code_spans_may_cross_line_breaks(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	// Each line that starts another block ends the paragraph, so a code span can't cross it (mpaulosky/dotfiles#57). Each case: its pytest id, and its shape.
	public static readonly TheoryData<string, string> ParagraphBoundaries = new()
	{
		{ "atx-heading", "{a}\n# {b}" },
		{ "list-item-same-level", "- {a}\n- {b}" },
		{ "list-item-outer-level", "- x\n  - {a}\n- {b}" },
		{ "thematic-break", "{a}\n***\n{b}" },
		{ "fence", "{a}\n```\nx\n```\n{b}" },
		{ "setext-underline-dash", "{a}\n---\n{b}" },
		{ "setext-underline-equals", "{a}\n===\n{b}" },
		{ "quote-opens", "{a}\n> {b}" },
		{ "quote-deepens", "> {a}\n> > {b}" },
	};

	[Theory]
	[MemberData(nameof(ParagraphBoundaries))]
	public void A_code_span_ends_with_its_paragraph_in_a_post(string id, string shape)
	{
		_ = id;
		// Paired, the span would hold "<T>" and become <code>; unpaired, "<T>" is escaped text.
		var body = SanitizerFormat(shape, "`a <T>", "b` c");
		Assert.Equal(1, Count(ReleasePost.SanitizeMarkdown(body), "&lt;T>"));
		Assert.DoesNotContain("<code>", ReleasePost.SanitizeMarkdown(body));
	}

	[Theory]
	[MemberData(nameof(ParagraphBoundaries))]
	public void A_code_span_ends_with_its_paragraph_in_the_docs_readme(string id, string shape)
	{
		_ = id;
		// Copilot's example, from mpaulosky/TicketManager#118: both links are prose, so both are rebased.
		var text = SanitizerFormat(shape, "`[a](docs/a.md)", "`[b](docs/b.md)") + "\n";
		Assert.Equal(text.Replace("docs/", ""), ReleasePost.RebaseReadmeLinks(text));
	}

	[Theory]
	[InlineData("lazy-continuation", "> {a}\n{b}")]
	[InlineData("lazy-continuation-at-an-outer-quote", "> > {a}\n> {b}")]
	[InlineData("indented-continuation", "{a}\n    {b}")]
	[InlineData("list-item-continuation", "- {a}\n  {b}")]
	public void A_code_span_still_crosses_a_paragraph_continuation_line(string id, string shape)
	{
		_ = id;
		var post = ReleasePost.SanitizeMarkdown(SanitizerFormat(shape, "`a <T>", "b` c"));
		Assert.Contains("<code>a &#60;T&#62;", post);
		Assert.DoesNotContain("&lt;", post);
		var text = SanitizerFormat(shape, "`[a](docs/a.md)", "`[b](docs/b.md)") + "\n";
		Assert.Equal(text.Replace("docs/b.md", "b.md"), ReleasePost.RebaseReadmeLinks(text));
	}

	[Fact]
	public void A_title_cannot_close_its_blog_index_link_early()
	{
		// "]" would end the label, so the row would link to the title's URL instead of the post.
		ReleasePost.UpdateBlogIndex(tmp, "2026-10-05", "fix: ](https://evil.example) `a[b` x", "p.md");
		var row = Assert.Single(Py.SplitLines(Read(Path.Join(tmp, "README.md"))), line => line.Contains("p.md"));
		Assert.Equal("| 2026-10-05 | [fix: &#93;(https://evil.example) `a[b` x](p.md) | release,automation |", row);
	}

	[Fact]
	public void A_reference_label_with_an_escaped_footnote_opener_is_neutralized()
	{
		var body = "[open][label \\[^x]\n\n[label \\[^x]: javascript:alert(1)";
		Assert.Contains("]: #javascript:", ReleasePost.SanitizeMarkdown(body));
	}

	// Code is published as written: it's found once, on the raw text, and every prose
	// transformation runs only outside it (mpaulosky/dotfiles#58).

	[Theory]
	// A comment or link inside a span is code: it's kept, as entities, not dropped or neutralized.
	[InlineData("Write `<!-- TODO -->` here", "Write <code>&#60;&#33;&#45;&#45; TODO &#45;&#45;&#62;</code> here")]
	[InlineData("Write `[x](javascript:x)`", "Write <code>&#91;x&#93;&#40;javascript&#58;x&#41;</code>")]
	// A comment that starts first hides the backticks inside it, as in CommonMark.
	[InlineData("a <!-- ` --> b ` c", "a  b ` c")]
	[InlineData("a <!-- `x` --> b", "a  b")]
	// A span that starts first holds the comment's opener, so the rest is text.
	[InlineData("`a <!--` b -->", "<code>a &#60;&#33;&#45;&#45;</code> b -->")]
	// An unclosed backtick is text, and the comment after it is still dropped.
	[InlineData("`a <!-- b --> c", "`a  c")]
	public void Code_spans_are_found_before_comments_are_dropped(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Fact]
	public void A_title_keeps_comments_and_links_in_its_code_spans() =>
			Assert.Equal(
					"fix: Drop <code>&#60;&#33;&#45;&#45; x &#45;&#45;&#62;</code>"
					+ " and <code>&#91;a&#93;&#40;javascript&#58;x&#41;</code>",
					ReleasePost.SanitizeInline("fix: Drop `<!-- x -->` and `[a](javascript:x)`"));

	[Theory]
	[InlineData("`<!-- x -->` and `<script>alert(1)</script>`")]
	[InlineData("`<!-- x -->`<script>alert(1)</script>")]
	[InlineData("<!-- ` --> `<script>alert(1)</script>`")]
	[InlineData("`x <!-- `<script>alert(1)</script>-->")]
	[InlineData("<!-- `x -->`<script>alert(1)</script>")]
	[InlineData("[a](x `<script>alert(1)</script>`)")]
	[InlineData("[a](x \"`<script>alert(1)</script>`\")")]
	[InlineData("`a\n<!-- b`\n<script>alert(1)</script>\n-->")]
	[InlineData("`a\nb <!-- c`\n<script>alert(1)</script>\n-->")]
	public void Code_spans_around_comments_and_links_render_inert(string body)
	{
		// However the scanner and the renderer pair the backticks, every "<" is an entity or in <code>.
		var post = ReleasePost.RenderPost(new JsonObject { ["number"] = 7, ["body"] = body }, "T", "v1.2.3", "2026-09-26", [], [], body, "m");
		foreach (var line in OutsideCodeLines(post))
			Assert.False(Regex.IsMatch(line, @"<(?!code>|/code>|!-- \{% (?:end)?raw %\} -->)"), line);
	}

	[Theory]
	// A comment between "](" and the destination is dropped, so the destination is still read.
	[InlineData("[a](<!-- x -->javascript:alert(1))", "[a](#javascript:alert(1))")]
	// A backtick in the destination makes it one link, read whole.
	[InlineData("[a](javascript:`x`)", "[a](#javascript:`x`)")]
	// A "[^1]:" after a code span isn't at the start of its line, so it's no footnote.
	[InlineData("`x` [^1]: javascript:x", "`x` [^1]: #javascript:x")]
	public void Link_destinations_beside_code_are_still_neutralized(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Theory]
	// A fence in a list item, indented code, and indented code in a quote keep their tabs.
	[InlineData("- item\n\n  ```\n  a\tb\n  ```", "- item\n\n```text\na\tb\n```")]
	[InlineData("Text:\n\n    a\tb\n\tc\td", "Text:\n\n```text\na\tb\nc\td\n```")]
	[InlineData(">     a\tb", "```text\na\tb\n```")]
	// A tab that spans the indentation keeps the columns past it, as spaces.
	[InlineData("- item\n\n\t\tcode", "- item\n\n```text\n  code\n```")]
	public void Code_keeps_its_tabs(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Theory]
	[InlineData("```py\ns = '''a\n\n\nb'''\n```", "```py\ns = '''a\n\n\nb'''\n```")]
	[InlineData("Text:\n\n    a\n\n\n\n    b", "Text:\n\n```text\na\n\n\n\nb\n```")]
	[InlineData("- x\n\n  ```\n  a\n\n\n  b\n  ```\n\n\n\nafter", "- x\n\n```text\na\n\n\nb\n```\n\nafter")]
	public void Code_keeps_its_blank_lines(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Fact]
	public void The_readme_keeps_blank_lines_in_its_code()
	{
		var readme = "# T\n\n```py\na\n\n\nb\n```\n\n\n\nText\n\n    c\n\n\n\n    d\n";
		var updated = ReleasePost.UpdateReadme(readme, "<!-- RELEASES_START -->\n<!-- RELEASES_END -->", Repo);
		Assert.StartsWith("# T\n\n```py\na\n\n\nb\n```\n\nText\n\n    c\n\n\n\n    d\n", updated, StringComparison.Ordinal);
	}

	[Fact]
	public void A_link_title_keeps_its_code_in_a_post() =>
			// The title is part of the link, so its backticks open no span and its "<" is escaped as text.
			Assert.Equal("[x](https://a.example \"Use `&lt;b>`\")", ReleasePost.SanitizeMarkdown("[x](https://a.example \"Use `<b>`\")"));

	[Theory]
	[InlineData("[x](docs/a.md \"Use `code`\")\n", "[x](a.md \"Use `code`\")\n")]
	[InlineData("[x](docs/a.md 'Use `code`') and [y](docs/b.md (`b`))\n", "[x](a.md 'Use `code`') and [y](b.md (`b`))\n")]
	// A title's backtick would otherwise pair with a later one and hide the next link.
	[InlineData("[x](a.md \"`\") [y](docs/b.md) `\n", "[x](../a.md \"`\") [y](b.md) `\n")]
	// So would a backtick in the destination.
	[InlineData("[x](docs/a`b.md) [y](docs/b.md) `\n", "[x](a`b.md) [y](b.md) `\n")]
	// A comment hides its backticks, so the link after it is text.
	[InlineData("Text <!-- ` --> [x](docs/a.md) `\n", "Text <!-- ` --> [x](a.md) `\n")]
	public void A_link_title_keeps_its_code_in_the_docs_readme(string text, string expected) => Assert.Equal(expected, ReleasePost.RebaseReadmeLinks(text));

	[Fact]
	public void A_title_cannot_close_its_blog_index_link_when_its_backticks_pair_differently()
	{
		// A parser that reads the first "](" as a link pairs no backticks; one that doesn't pairs
		// two of them around "](https://...)". Every "]" is an entity, so neither reading can link
		// the row elsewhere.
		ReleasePost.UpdateBlogIndex(tmp, "2026-10-05", "a](<a b`>)](https://evil.example) `", "p.md");
		var row = Assert.Single(Py.SplitLines(Read(Path.Join(tmp, "README.md"))), line => line.Contains("p.md"));
		Assert.DoesNotContain("](https://evil", row);
		Assert.EndsWith("](p.md) | release,automation |", row, StringComparison.Ordinal);
	}

	// The pytest's shapes, by their index in its list.
	static readonly Func<int, string>[] CodeFindingShapes =
	[
		n => Times("[a](", n),
		n => Times("](", n) + "`",
		n => Times("](x ", n),
		n => Times("](x \"", n),
		n => Times("<!--`", n),
		n => Times("](((", n),
		n => "](" + Times("(a)", n) + Times("](", n),
		n => Times("[", n) + Times("[a](x)", n),
		// Raw inline HTML in the README: a tag in a quoted paragraph, and openers that never close.
		n => "> " + Times("<a\n> ", n),
		n => "- - > " + Times("<!X\n    > ", n),
		n => "> " + Times("<?\n> ", n),
		n => Times("<? ", n),
		n => Times("<!X ", n),
		n => Times("<![CDATA[ ", n),
	];

	public static readonly TheoryData<int> CodeFindingShapeIndexes = [.. Enumerable.Range(0, CodeFindingShapes.Length)];

	[Theory]
	[MemberData(nameof(CodeFindingShapeIndexes))]
	public void Finding_code_stays_linear(int shape)
	{
		// As in test_scanning_stays_linear_in_nesting_depth_and_length: 4x the size, well under 16x the time.
		double Best(int n)
		{
			var text = CodeFindingShapes[shape](n);
			List<double> runs = [];
			for (var i = 0; i < 3; i++)
			{
				var start = Stopwatch.GetTimestamp();
				ReleasePost.SanitizeMarkdown(text);
				ReleasePost.RebaseReadmeLinks(text);
				runs.Add(Stopwatch.GetElapsedTime(start).TotalSeconds);
			}
			return runs.Min();
		}

		double small = Best(2_000), large = Best(8_000);
		Assert.True(large < Math.Max(small, 0.001) * 10, $"{large} >= {Math.Max(small, 0.001) * 10}");
	}

	[Theory]
	// No "[" opens a label, so "](" starts no link and the backticks pair as a code span.
	[InlineData("Text ](x \"`<T>`\")", "Text ](x \"<code>&#60;T&#62;</code>\")")]
	// An escaped "[" opens no label either.
	[InlineData("\\[x](docs/x.md \"Use `List<T>`\")", "\\[x](docs/x.md \"Use <code>List&#60;T&#62;</code>\")")]
	// A "]" before the "](" closes the only label, so it is text too.
	[InlineData("[a] ](x \"`<T>`\")", "[a] ](x \"<code>&#60;T&#62;</code>\")")]
	// A "[" inside a code span opens no label.
	[InlineData("`[` ](x \"`<T>`\")", "`[` ](x \"<code>&#60;T&#62;</code>\")")]
	// A link can't hold a link, so once the inner one closes, the outer "[" opens nothing.
	[InlineData("[outer [inner](x)](y \"`<T>`\")", "[outer [inner](x)](y \"<code>&#60;T&#62;</code>\")")]
	// An image can, so its "](" still starts a destination, and its title holds no code span.
	[InlineData("![a [b](x)](y \"`<T>`\")", "![a [b](x)](y \"`&lt;T>`\")")]
	public void A_close_bracket_without_a_label_starts_no_link(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Theory]
	// A ">" line is blank inside the quote, so the code on both sides of it is one block.
	[InlineData("> intro\n>\n>     a\n>\n>     b\n", "> intro\n>\n\n```text\na\n\nb\n```")]
	[InlineData(">     a\n>\n>     b", "```text\na\n\nb\n```")]
	[InlineData("> - x\n>\n>       a\n>\n>       b", "> - x\n>\n\n```text\na\n\nb\n```")]
	// A ">" line inside a quoted list item keeps the item open too.
	[InlineData("> -     a\n>\n>       b", "```text\na\n\nb\n```")]
	public void Quoted_indented_code_keeps_its_blank_lines(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Theory]
	// A blank line ends the quote, so the code after it is a second block.
	[InlineData(">     a\n\n    b")]
	// A blank line in the outer quote ends the inner one.
	[InlineData("> >     a\n>\n> >     b")]
	// Each list item holds its own code.
	[InlineData("-     a\n-     b")]
	public void Indented_code_in_different_containers_stays_apart(string body) => Assert.Equal(2, Count(ReleasePost.SanitizeMarkdown(body), "```text"));

	[Theory]
	// A backtick fence's info string can't hold a backtick, so this line is a code span, not a fence.
	[InlineData("```List<T>```", "<code>List&#60;T&#62;</code>")]
	[InlineData("```a``` b", "```a``` b")]
	[InlineData("Text\n\n```Map<K>``` here", "Text\n\n<code>Map&#60;K&#62;</code> here")]
	// So is the opener of a would-be fence, and its next line is prose, escaped; the bare "```" opens a fence.
	[InlineData("```foo`bar\n<b>not code</b>\n```", "```foo`bar\n&lt;b>not code&lt;/b>\n\n```text\n```")]
	// A tilde fence's info string may hold one.
	[InlineData("~~~a`b\nx\n~~~", "```a\nx\n```")]
	public void A_backtick_line_with_backticks_after_it_is_a_code_span(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Theory]
	// An angle-bracket destination becomes the same destination written bare, so no "<" is escaped into it.
	[InlineData("[x](<docs/a.md>)", "[x](docs/a.md)")]
	[InlineData("[x](<docs/a b(1).md> \"t\")", "[x](docs/a%20b%281%29.md \"t\")")]
	[InlineData("[x](<a\\>b.md>)", "[x](a%3Eb.md)")]
	// An escaped punctuation character stays escaped, which a bare destination reads the same way.
	[InlineData("[x](<a\\_b.md>)", "[x](a\\_b.md)")]
	// A backslash that escapes nothing is part of the URL, so it is encoded rather than left to escape what follows.
	[InlineData("[x](<a\\ b.md>)", "[x](a%5C%20b.md)")]
	[InlineData("[x](<a\\\tb.md>)", "[x](a%5C%09b.md)")]
	[InlineData("[x](<a\\b.md>)", "[x](a%5Cb.md)")]
	// Its scheme is still checked.
	[InlineData("[x](<javascript:alert(1)>)", "[x](#javascript:alert%281%29)")]
	// Without a label, it is text.
	[InlineData("Text ](<a.md>)", "Text ](&lt;a.md>)")]
	public void An_angle_bracket_link_destination_keeps_working(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Theory]
	// Quote markers on a span's later lines aren't part of its code.
	[InlineData("> Use `List<T>\n> and Map<K>` here", "> Use <code>List&#60;T&#62; and Map&#60;K&#62;</code> here")]
	[InlineData("> > Use `List<T>\n> and Map<K>` here", "> > Use <code>List&#60;T&#62; and Map&#60;K&#62;</code> here")]
	[InlineData("- Use `List<T>\n  and Map<K>` here", "- Use <code>List&#60;T&#62; and Map&#60;K&#62;</code> here")]
	public void A_code_span_drops_the_container_markers_of_its_later_lines(string body, string expected) =>
			Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Fact]
	public void A_reference_definition_with_backticks_in_its_label_and_title_is_rebased() =>
			Assert.Equal("[`a]: a.md \"`\"\n", ReleasePost.RebaseReadmeLinks("[`a]: docs/a.md \"`\"\n"));

	[Theory]
	// A definition is read before spans are paired, so its label's backtick can't hide the next line's link.
	[InlineData("[`a]: docs/a.md\n[b](docs/b.md) `\n", "[`a]: a.md\n[b](b.md) `\n")]
	// Nor can a span that crosses from its label into its destination.
	[InlineData("[`a]: docs/a.md`\n", "[`a]: a.md`\n")]
	[InlineData("[a]: docs/a.md\n[b]: <docs/b c.md> 'B'\nText [c](docs/c.md)\n", "[a]: a.md\n[b]: <b c.md> 'B'\nText [c](c.md)\n")]
	[InlineData("> [a]: docs/a.md\n- [b]: docs/b.md \"t\"\n", "> [a]: a.md\n- [b]: b.md \"t\"\n")]
	// Anything but a title after the destination makes the line text, and its inline link is still rebased.
	[InlineData("[x]: foo [y](docs/b.md)\n", "[x]: foo [y](b.md)\n")]
	[InlineData("[a]: docs/a.md extra\n", "[a]: docs/a.md extra\n")]
	// A definition can't interrupt a paragraph, and a footnote isn't one.
	[InlineData("Text\n[a]: docs/a.md\n", "Text\n[a]: docs/a.md\n")]
	[InlineData("[^1]: `docs/x`\n", "[^1]: `docs/x`\n")]
	// A "[^...]" label without a footnote ID is a link's.
	[InlineData("[^!]: docs/x.md\n", "[^!]: x.md\n")]
	[InlineData("[^a:b]: docs/x.md\n", "[^a:b]: x.md\n")]
	// A CRLF line ending keeps its "\r" in the README's lines.
	[InlineData("[a]: docs/a.md\r\n[b]: docs/b.md 'B'\r\n", "[a]: a.md\r\n[b]: b.md 'B'\r\n")]
	// A title may start on the next line and hold line endings, LF or CRLF.
	[InlineData("[a]: docs/a.md \"first\nsecond\"\n", "[a]: a.md \"first\nsecond\"\n")]
	[InlineData("[a]: docs/a.md \"first\r\nsecond\"\r\n", "[a]: a.md \"first\r\nsecond\"\r\n")]
	[InlineData("[a]: docs/a.md\n  'T `'\n[b](docs/b.md) `\n", "[a]: a.md\n  'T `'\n[b](b.md) `\n")]
	// A backslash before a title's line ending is a literal backslash, in each kind of title.
	[InlineData("[a]: docs/a.md \"first\\\nsecond\"\n", "[a]: a.md \"first\\\nsecond\"\n")]
	[InlineData("[a]: docs/a.md 'first\\\nsecond'\n", "[a]: a.md 'first\\\nsecond'\n")]
	[InlineData("[a]: docs/a.md (first\\\r\nsecond)\r\n", "[a]: a.md (first\\\r\nsecond)\r\n")]
	// A blank line ends the title, so this is no definition; nor is a title with text after it.
	[InlineData("[a]: docs/a.md \"first\n\nsecond\"\n", "[a]: docs/a.md \"first\n\nsecond\"\n")]
	[InlineData("[a]: docs/a.md\n\"t\" x\n", "[a]: a.md\n\"t\" x\n")]
	// A title can't run into a quote or list item that follows, so their links are still rebased.
	[InlineData("[a]: docs/a.md\n> \"see [b](docs/b.md)\"\n", "[a]: a.md\n> \"see [b](b.md)\"\n")]
	[InlineData("[a]: docs/a.md \"x\n- [b](docs/b.md) y\"\n", "[a]: docs/a.md \"x\n- [b](b.md) y\"\n")]
	public void Reference_definitions_are_read_before_code_spans_in_the_docs_readme(string text, string expected) =>
			Assert.Equal(expected, ReleasePost.RebaseReadmeLinks(text));

	[Fact]
	public void A_reference_definition_keeps_its_code_in_a_post() =>
			// The label's backtick pairs with nothing, so the next line's link isn't swallowed into <code>.
			Assert.Equal("[`a]: docs/a.md\n[b](docs/b.md) `", ReleasePost.SanitizeMarkdown("[`a]: docs/a.md\n[b](docs/b.md) `"));

	[Theory]
	// A "](" with no open label is text, so it isn't rebased.
	[InlineData("Plain ](docs/a.md \"x\")\n", "Plain ](docs/a.md \"x\")\n")]
	[InlineData("\\[x](docs/a.md)\n", "\\[x](docs/a.md)\n")]
	public void A_close_bracket_without_a_label_is_not_rebased(string text, string expected) => Assert.Equal(expected, ReleasePost.RebaseReadmeLinks(text));

	[Theory]
	// An inline HTML tag is read whole, so a "]" or backtick in its attributes ends no label and opens no span.
	[InlineData("[<span title=\"]\">x</span>](docs/a.md)\n", "[<span title=\"]\">x</span>](a.md)\n")]
	[InlineData("[<b title='`'>x</b>](docs/a.md) `\n", "[<b title='`'>x</b>](a.md) `\n")]
	// Its own src and href are rebased as before.
	[InlineData("[<img src=\"docs/a.png\" alt=\"]\">](docs/b.md)\n", "[<img src=\"a.png\" alt=\"]\">](b.md)\n")]
	// A "<" that opens no tag is text, and so is an escaped one, so the backticks after it pair.
	[InlineData("[a < b](docs/a.md)\n", "[a < b](a.md)\n")]
	[InlineData("Use \\<span title=\"`\">[x](docs/a.md)`\n", "Use \\<span title=\"`\">[x](docs/a.md)`\n")]
	// So is a processing instruction, a CDATA section and a declaration.
	[InlineData("[<?pi data=\"]\"?>x](docs/a.md)\n", "[<?pi data=\"]\"?>x](a.md)\n")]
	[InlineData("[<![CDATA[a]b]]>x](docs/a.md)\n", "[<![CDATA[a]b]]>x](a.md)\n")]
	[InlineData("[<!X a]b>x](docs/a.md)\n", "[<!X a]b>x](a.md)\n")]
	[InlineData("[<!X\na]b>x](docs/a.md)\n", "[<!X\na]b>x](a.md)\n")]
	// A declaration's name is uppercase letters then whitespace, as GitHub reads it; any other "<!" is text.
	[InlineData("[<!x a]b>x](docs/a.md)\n", "[<!x a]b>x](docs/a.md)\n")]
	[InlineData("[<!Xa]b>x](docs/a.md)\n", "[<!Xa]b>x](docs/a.md)\n")]
	// An unclosed one is text.
	[InlineData("[<? x](docs/a.md)\n", "[<? x](a.md)\n")]
	// In a quote, a tag's later lines carry the quote's markers, which aren't the tag's end.
	[InlineData("> [<span\n> title=\"]\">x</span>](docs/a.md)\n", "> [<span\n> title=\"]\">x</span>](a.md)\n")]
	[InlineData("> > [<span\n> > title=\"]\">x</span>](docs/a.md)\n", "> > [<span\n> > title=\"]\">x</span>](a.md)\n")]
	[InlineData("- > [<span\n  > title=\"]\">x</span>](docs/a.md)\n", "- > [<span\n  > title=\"]\">x</span>](a.md)\n")]
	// Nested list items can indent a later line's quote marker 4 spaces or more.
	[InlineData("- - > [<span\n    > title=\"]\">x</span>](docs/a.md)\n", "- - > [<span\n    > title=\"]\">x</span>](a.md)\n")]
	[InlineData("1. - > [<span\n     > title=\"]\">x</span>](docs/a.md)\n", "1. - > [<span\n     > title=\"]\">x</span>](a.md)\n")]
	[InlineData("10. > [<span\n    > title=\"]\">x</span>](docs/a.md)\n", "10. > [<span\n    > title=\"]\">x</span>](a.md)\n")]
	[InlineData(">\t[<span\n>\ttitle=\"]\">x</span>](docs/a.md)\n", ">\t[<span\n>\ttitle=\"]\">x</span>](a.md)\n")]
	// So do the other forms: a declaration, processing instruction and CDATA section.
	[InlineData("> [<!X\n> a]b>x](docs/a.md)\n", "> [<!X\n> a]b>x](a.md)\n")]
	[InlineData("> [<!X\n> data=\"]\">x](docs/a.md)\n", "> [<!X\n> data=\"]\">x](a.md)\n")]
	[InlineData("> [<?pi\n> a]b?>x](docs/a.md)\n", "> [<?pi\n> a]b?>x](a.md)\n")]
	[InlineData("- - > [<![CDATA[\n    > a]b]]>x](docs/a.md)\n", "- - > [<![CDATA[\n    > a]b]]>x](a.md)\n")]
	// A lazy line has no marker to remove.
	[InlineData("> [<span\ntitle=\"]\">x</span>](docs/a.md)\n", "> [<span\ntitle=\"]\">x</span>](a.md)\n")]
	// Outside a quote, that ">" starts one, so there's no link.
	[InlineData("[<span\n> title=\"]\">x</span>](docs/a.md)\n", "[<span\n> title=\"]\">x</span>](docs/a.md)\n")]
	public void Inline_html_in_a_label_is_read_whole_in_the_docs_readme(string text, string expected) => Assert.Equal(expected, ReleasePost.RebaseReadmeLinks(text));

	[Fact]
	public void The_post_sanitizer_still_escapes_inline_html_in_a_label() =>
			Assert.Equal("[&lt;span title=\"]\">x&lt;/span>](docs/a.md)", ReleasePost.SanitizeMarkdown("[<span title=\"]\">x</span>](docs/a.md)"));

	[Fact]
	public void A_comment_after_a_close_bracket_without_a_label_is_dropped() =>
			// The "](" is text, so the title is no title, and the comment in it is a comment.
			Assert.Equal("Plain ](https://example.com \"\")", ReleasePost.SanitizeMarkdown("Plain ](https://example.com \"<!-- secret -->\")"));

	[Theory]
	// A line ending may separate a link's destination from its title, so the title's backtick pairs with nothing.
	[InlineData("[x](docs/a.md\n \"title `\") [y](docs/b.md) `\n", "[x](a.md\n \"title `\") [y](b.md) `\n")]
	[InlineData("[x](docs/a.md\n\"t\")\n", "[x](a.md\n\"t\")\n")]
	[InlineData("[x](\ndocs/a.md\n)\n", "[x](\na.md\n)\n")]
	// In a quote, the next line's marker is skipped too.
	[InlineData("> [x](docs/a.md\n> \"t `\") [y](docs/b.md) `\n", "> [x](a.md\n> \"t `\") [y](b.md) `\n")]
	// So may a CRLF line ending.
	[InlineData("[x](\r\ndocs/a.md\r\n)\r\n", "[x](\r\na.md\r\n)\r\n")]
	[InlineData("[x](docs/a.md\r\n \"title\")\r\n", "[x](a.md\r\n \"title\")\r\n")]
	// A blank line ends the paragraph, and the link with it.
	[InlineData("[x](docs/a.md\n\n\"t\")\n", "[x](docs/a.md\n\n\"t\")\n")]
	// A backslash before a line ending in a title is a literal backslash.
	[InlineData("[x](docs/a.md \"a\\\nb\")\n", "[x](a.md \"a\\\nb\")\n")]
	[InlineData("[x](docs/a.md\r\n\r\n\"t\")\r\n", "[x](docs/a.md\r\n\r\n\"t\")\r\n")]
	public void A_link_may_hold_a_line_ending_in_the_docs_readme(string text, string expected) => Assert.Equal(expected, ReleasePost.RebaseReadmeLinks(text));

	[Theory]
	[InlineData("[x](docs/a.md\n \"title `\") [y](docs/b.md) `", "[x](docs/a.md\n \"title `\") [y](docs/b.md) `")]
	[InlineData("[x](a.md\n \"Use `<T>`\")", "[x](a.md\n \"Use `&lt;T>`\")")]
	public void A_link_may_hold_a_line_ending_in_a_post(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Fact]
	public void A_reference_definition_inside_a_code_span_is_left_alone() =>
			Assert.Equal("Text `a\n[b]: docs/b.md` c\n", ReleasePost.RebaseReadmeLinks("Text `a\n[b]: docs/b.md` c\n"));

	[Theory]
	// A tag that only starts a line of prose opens no HTML block, so the paragraph is the excerpt.
	[InlineData("<T> is the generic return type.", "<T> is the generic return type.")]
	// Inline HTML is prose too.
	[InlineData("<b>Hello</b> world.", "<b>Hello</b> world.")]
	[InlineData("<div>\nhidden\n</div>\n\nShown.", "Shown.")]
	public void The_excerpt_skips_only_real_html_blocks(string summary, string expected) =>
			Assert.Equal(expected, ReleasePost.FirstParagraph(ReleasePost.SanitizeMarkdown(summary)));

	[Fact]
	public void A_title_that_looks_like_a_reference_definition_keeps_its_code() =>
			// A title is rendered in a heading or table cell, where no definition can start, so its code span pairs as usual.
			Assert.Equal("[Use <code>List&#60;T&#62;</code>]: support", ReleasePost.SanitizeInline("[Use `List<T>`]: support"));

	[Theory]
	// A subject that would start a reference definition gets its first character escaped, so the commit isn't
	// an empty list item: GitHub (cmark-gfm) reads the SHA after it as a parenthesized title, and kramdown takes
	// anything after "[label]:" as the destination. Its code then pairs as in a title.
	[InlineData("[Use `List<T>`]: support", "\\[Use <code>List&#60;T&#62;</code>]: support")]
	[InlineData("[a]: docs/x.md", "\\[a]: docs/x.md")]
	[InlineData("[a b]: c d", "\\[a b]: c d")]
	// A label with an escaped "]" is one to cmark-gfm, and its "\]:" ends one to kramdown.
	[InlineData("[a\\]b]: x", "\\[a\\]b]: x")]
	[InlineData("[a\\]: x", "\\[a\\]: x")]
	// kramdown's footnote and abbreviation definitions.
	[InlineData("[^1]: note", "\\[^1]: note")]
	[InlineData("*[HTML]: Hyper", "\\*[HTML]: Hyper")]
	// So does one after the quote or list markers that would nest a block in the item.
	[InlineData("> [a]: docs/x.md", "> \\[a]: docs/x.md")]
	[InlineData("- [a]: x", "- \\[a]: x")]
	[InlineData("1. [a]: x", "1. \\[a]: x")]
	[InlineData("-\t[a]: x", "- \\[a]: x")]
	[InlineData("> - [^1]: note", "> - \\[^1]: note")]
	[InlineData("* *[HTML]: Hyper", "* \\*[HTML]: Hyper")]
	// Spaces after those markers collapse to one, so the rest is never indented code (where kramdown and
	// cmark-gfm disagree, and an escape would show), and a definition there is escaped in both.
	[InlineData(">     [a]: docs/x.md", "> \\[a]: docs/x.md")]
	[InlineData("-     [a]: docs/x.md", "- \\[a]: docs/x.md")]
	[InlineData(">\t\t[a]: x", "> \\[a]: x")]
	[InlineData(">     some code", "> some code")]
	[InlineData(">     - [a]: x", "> - \\[a]: x")]
	// Or once sanitizing drops a comment before it.
	[InlineData("<!--x-->[a]: docs/x.md", "\\[a]: docs/x.md")]
	// A tab or space the comment left behind is dropped, so the definition is still escaped.
	[InlineData("<!--x-->\t[a]: docs/x.md", "\\[a]: docs/x.md")]
	[InlineData("<!--x--> [a]: docs/x.md", "\\[a]: docs/x.md")]
	[InlineData("> <!--x-->[a]: x", "> \\[a]: x")]
	// Without the space after it, "-" is no list marker, and nothing nests.
	[InlineData("-[a]: x", "-[a]: x")]
	// A leading link or bracketed tag starts no definition, so it's left alone.
	[InlineData("[docs](https://x.test) fix", "[docs](https://x.test) fix")]
	[InlineData("[WIP] fix: x", "[WIP] fix: x")]
	public void A_commit_subject_never_starts_a_reference_definition(string subject, string expected)
	{
		var commit = SanitizerCommit(subject);
		Assert.Equal($"- {expected} (`abc1234`)", Py.SplitLines(ReleasePost.RenderCommits([commit]))[2]);
	}

	[Theory]
	// An autolink is read whole, so a "]" in it closes no label and the link around it is still rebased.
	[InlineData("![<https://example.test/a]b>](docs/logo.png)\n", "![<https://example.test/a]b>](logo.png)\n")]
	[InlineData("[<mailto:a]b@example.test>](docs/a.md)\n", "[<mailto:a]b@example.test>](a.md)\n")]
	public void An_autolink_in_a_label_does_not_close_it(string text, string expected) => Assert.Equal(expected, ReleasePost.RebaseReadmeLinks(text));

	[Theory]
	// Only a definition's destination is rebased: its label and title are text, even where they read like HTML.
	[InlineData("[api]: docs/a.md \"Use href='docs/b.md'\"\n", "[api]: a.md \"Use href='docs/b.md'\"\n")]
	[InlineData("[src='docs/b.md']: docs/a.md\n", "[src='docs/b.md']: a.md\n")]
	[InlineData("[api]: docs/a.md\n(Use href='docs/b.md')\n", "[api]: a.md\n(Use href='docs/b.md')\n")]
	public void A_reference_definition_keeps_its_label_and_title_as_written(string text, string expected) =>
			Assert.Equal(expected, ReleasePost.RebaseReadmeLinks(text));

	[Theory]
	// The sanitizer escapes this autolink's "<", so in the post it's text: its backtick pairs as the post reads it.
	[InlineData("<https://x.test/a`b> `<i>`", "&lt;https://x.test/a`b> `&lt;i>`")]
	[InlineData("[<https://x.test/a`]b> `<i>`](docs/a.md)", "[&lt;https://x.test/a<code>&#93;b&#62; </code>&lt;i>`](docs/a.md)")]
	public void An_autolink_the_sanitizer_escapes_is_scanned_as_text(string body, string expected) => Assert.Equal(expected, ReleasePost.SanitizeMarkdown(body));

	[Fact]
	public void List_reads_a_record_whose_text_holds_unicode_line_separators()
	{
		// jq's @json leaves U+2028 and U+2029 unescaped, and str.splitlines() splits on both.
		List<JsonNode> records =
		[
			new JsonObject { ["filename"] = "a.md", ["patch"] = "x\u2028y\u2029z" },
			new JsonObject { ["filename"] = "b.md", ["patch"] = "plain" },
		];
		// json.dumps(record, ensure_ascii=False) for each, a line each: the output the patched _api returned.
		var output = "{\"filename\": \"a.md\", \"patch\": \"x\u2028y\u2029z\"}\n{\"filename\": \"b.md\", \"patch\": \"plain\"}\n";

		var files = ReleaseGitHub.Records(output);
		Assert.Equal(records.Count, files.Count);
		Assert.All(records.Zip(files), pair => Assert.True(JsonNode.DeepEquals(pair.First, pair.Second), pair.Second?.ToJsonString()));
	}

	[Theory]
	[InlineData("Fix a\u2028b\r\n\nBody line", "Fix a\u2028b")]
	[InlineData("Fix a\u2029b\nBody", "Fix a\u2029b")]
	[InlineData("Fix\r# Heading", "Fix")] // a lone CR is a Markdown line ending
	[InlineData("Fix\rBody", "Fix")]
	[InlineData("Fix\r```\rmore", "Fix")]
	[InlineData("Fix\r[x]: y", "Fix")]
	public void Commit_subject_ends_at_markdowns_line_endings_and_keeps_other_separators(string message, string expected) =>
			Assert.Equal(expected, ReleasePost.CommitSubject(new JsonObject { ["commit"] = new JsonObject { ["message"] = message } }));

	[Theory]
	[InlineData("\u2028")]
	[InlineData("\u2029")]
	public void A_subject_with_a_unicode_separator_renders_as_one_list_item(string separator)
	{
		List<JsonNode?> commits = [SanitizerCommit($"Fix a{separator}b\n\nBody")];

		Assert.Equal($"- Fix a{separator}b (`abc1234`)", ReleasePost.RenderCommits(commits).Split('\n')[2]);
	}

	[Fact]
	public void A_subject_with_a_lone_cr_renders_only_the_part_before_it()
	{
		List<JsonNode?> commits = [SanitizerCommit("Fix\r# Heading")];

		Assert.Equal("- Fix (`abc1234`)", ReleasePost.RenderCommits(commits).Split('\n')[2]);
	}

	[Theory]
	[InlineData("\u2028")]
	[InlineData("\u2029")]
	public void Blog_index_keeps_a_row_whose_title_holds_a_unicode_separator(string separator)
	{
		var blogDir = Path.Join(MakeRepo(tmp), "docs", "blogs");
		string first = "2026-09-20-pr-7-fix-a.md", second = "2026-09-24-pr-8-feat-next.md";
		foreach (var name in new[] { first, second })
			Write(Path.Join(blogDir, name), "post\n");
		var row = $"| 2026-09-20 | [fix: A{separator}B]({first}) | release,automation |";

		ReleasePost.UpdateBlogIndex(blogDir, "2026-09-20", $"fix: A{separator}B", first);
		ReleasePost.UpdateBlogIndex(blogDir, "2026-09-24", "feat: Next", second);

		var index = Read(Path.Join(blogDir, "README.md"));
		Assert.Equal(1, index.Split('\n').Count(line => line.Contains($"({first})")));
		Assert.Contains(row, index.Split('\n'));
	}
}
