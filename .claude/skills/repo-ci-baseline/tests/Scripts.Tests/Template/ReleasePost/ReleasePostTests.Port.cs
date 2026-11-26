using System.Text;
using System.Text.Json.Nodes;

namespace Baseline.Tests;

// The port's own tests, after the pytest's: where .NET reads text apart from Python, ReleasePost.cs reads it as release_post.py does. Each
// expected value is what the Python gives.
public sealed partial class ReleasePostTests
{
	[Fact]
	public void The_entity_table_holds_every_name_html_unescape_reads()
	{
		// html.entities.html5: 2,231 names, with and without their ";", one of them two code points.
		Assert.Equal(2231, HtmlEntities.Html5.Count);
		Assert.Equal("\u2aa2\u0338", HtmlEntities.Html5["NotNestedGreaterGreater;"]);
		Assert.Equal("&", HtmlEntities.Html5["amp"]);
		Assert.Equal("\U0001d504", HtmlEntities.Html5["Afr;"]);
	}

	[Theory]
	[InlineData("&#x80;", "\u20ac")] // a C1 control read as windows-1252
	[InlineData("&#0;", "\ufffd")]
	[InlineData("&#xD800;", "\ufffd")] // a surrogate
	[InlineData("&#x110000;", "\ufffd")] // past U+10FFFF
	[InlineData("&#65535;", "")] // a noncharacter is dropped
	[InlineData("&#13;", "\r")]
	[InlineData("&notit;", "\u00acit;")] // the longest name that starts it
	[InlineData("&AMP;x", "&x")]
	public void Character_references_are_read_as_html_unescape_reads_them(string text, string expected) =>
			Assert.Equal(expected, ReleasePost.SafeUnescape(text));

	[Theory]
	[InlineData("\u0130stanbul", "i-stanbul")] // str.lower() gives "i" and a combining dot above
	[InlineData("\u212aelvin", "kelvin")] // the Kelvin sign lowers to "k"
	public void A_slug_lowers_the_title_as_python_does(string title, string expected) => Assert.Equal(expected, ReleasePost.Slugify(title));

	[Fact]
	public void Lengths_cuts_and_entities_count_code_points()
	{
		// An astral character is one code point: one entity, one character of an excerpt or a diff.
		Assert.Equal("<code>&#128512;</code>", ReleasePost.CodeName("\U0001f600"));
		Assert.Equal("<code>\U0001d41a</code>", ReleasePost.CodeName("\U0001d41a"));
		Assert.Equal(200, Py.Len(ReleasePost.Shorten(Times("\U0001f600", 250))));
		var diff = ReleasePost.BuildDiff([new JsonObject { ["filename"] = "a", ["patch"] = Times("\U0001f600", 100) }], limit: 50);
		Assert.EndsWith("+ b/a\n" + Times("\U0001f600", 15) + "\n[Diff truncated: showing the first 50 of 136 characters.]\n", diff);
	}

	[Theory]
	[InlineData("20260927", "27 Sep 2026")]
	[InlineData("2026-W39-1", "21 Sep 2026")]
	[InlineData("2026W39", "21 Sep 2026")]
	[InlineData("0999-01-05", "5 Jan 999")]
	[InlineData("20260927xy", "27 Sep 2026")] // ten bytes, the date read from the first eight
	[InlineData("2025-W53-1", "2025-W53-1")] // 2025 has 52 ISO weeks
	[InlineData("2026-9-27", "2026-9-27")]
	[InlineData("2026-02-30", "2026-02-30")]
	public void A_card_date_reads_every_form_fromisoformat_reads(string text, string expected) => Assert.Equal(expected, ReleasePost.DisplayDate(text));

	[Fact]
	public void Ignoring_case_matches_the_letters_python_matches()
	{
		// re.IGNORECASE on a str pattern matches "i" to U+0131 and "s" to U+017F (long s), and [a-z] the Kelvin sign too.
		Assert.Equal("Real.", ReleasePost.FirstParagraph("F\u0131xes #1.\n\nReal."));
		Assert.Equal("Real.", ReleasePost.FirstParagraph("Fixe\u017f #1.\n\nReal."));
		Assert.Equal("<http\u017f://x.test> <HTTPS://y.test>", ReleasePost.SanitizeInline("<http\u017f://x.test> <HTTPS://y.test>"));
		Assert.Equal("\u212a:x", ReleasePost.RebaseLink("\u212a:x"));
	}

	[Fact]
	public void Files_are_read_as_path_read_text_reads_them()
	{
		// Strict UTF-8 with universal newlines: CRLF and CR become LF, and a byte-order mark is a character that stays.
		MakeRepo(tmp);
		var readme = Path.Join(tmp, "README.md");
		File.WriteAllBytes(readme, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Readme.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("Text.\r\n", "Text.\r", StringComparison.Ordinal))]);
		ReleasePost.UpdateTables(Repo, new FakeGitHub(), tmp, log: new StringWriter());
		var written = File.ReadAllBytes(readme);
		Assert.Equal([0xef, 0xbb, 0xbf], written[..3]);
		Assert.DoesNotContain((byte)'\r', written);
		Assert.Contains("Text.\n\n## Releases", Encoding.UTF8.GetString(written));

		File.WriteAllBytes(readme, [0xff, (byte)'x']);
		Assert.Throws<DecoderFallbackException>(() => ReleasePost.UpdateTables(Repo, new FakeGitHub(), tmp, log: new StringWriter()));
	}
}
