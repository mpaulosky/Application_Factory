using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baseline.PostReview.Tests;

// The Template's .github/scripts/post_claude_review.cs: the script's tests in test_post_claude_review.py, one method each under its name
// (ADR 0017's rule), with the same cases and assertions. In the script's own namespace, so Arguments, Py and Script are its copies of
// Gh.cs's. The tests of claude-review.yml's heredocs and post step stay in the pytest file, and so do the two that build a body from the
// workflow's batch join (the longest joined summary, and one past GitHub's limit): parity case 33 posts a body past that limit.
public sealed partial class PostClaudeReviewTests
{
	const string Head = "abc123";

	static readonly string Patch = string.Join("\n",
			"@@ -1,3 +1,4 @@",
			" unchanged",
			"-removed",
			"+added one",
			"+added two",
			" context",
			"@@ -20,2 +21,2 @@",
			" later context",
			"+later added");

	static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

	static JsonElement Json(JsonNode node) => Json(node.ToJsonString());

	// findings_json(): the summary shape, each finding (path, line, body).
	static string FindingsJson(params (string Path, int Line, string Body)[] findings) => FindingsJsonWith("Looked at the diff.", findings);

	static string FindingsJsonWith(string summary, params (string Path, int Line, string Body)[] findings)
	{
		var list = new JsonArray();
		foreach (var (path, line, body) in findings)
			list.Add(new JsonObject { ["path"] = path, ["line"] = line, ["body"] = body });
		return new JsonObject { ["summary"] = summary, ["findings"] = list }.ToJsonString();
	}

	// structured(): FINDINGS in the structured shape, findings (path, line, body, severity or null), the overview "An overview." unless
	// the report gives one.
	static string Structured(JsonObject? report, params (string Path, int Line, string Body, string? Severity)[] findings)
	{
		report ??= [];
		if (!report.ContainsKey("overview"))
			report["overview"] = "An overview.";
		var list = new JsonArray();
		foreach (var (path, line, body, severity) in findings)
		{
			var finding = new JsonObject { ["path"] = path, ["line"] = line, ["body"] = body };
			if (severity is not null)
				finding["severity"] = severity;
			list.Add(finding);
		}
		report["findings"] = list;
		return report.ToJsonString();
	}

	// GitHub for Run(): the reviews posted, as GitHub got them, and their comments read back with a URL each, unless fail names a call
	// ("post", "read" or "update"). Answer, when set, is what the post returns.
	sealed class FakeGitHub(List<JsonElement>? files = null, Dictionary<string, int>? lengths = null, string? fail = null) : GitHub("octo/demo")
	{
		readonly List<JsonElement> files = files ?? [Json(new JsonObject { ["filename"] = "src/app.py", ["patch"] = Patch })];

		// src/app.py runs to line 30: lines 5-20 and 23-30 are real but outside the diff.
		readonly Dictionary<string, int> lengths = lengths ?? new() { ["src/app.py"] = 30 };

		public List<(BigInteger Number, JsonElement Review)> Posted { get; } = [];
		public List<string> Updates { get; } = [];
		public Func<JsonElement>? Answer { get; init; }

		public override int? LineCount(string path, string reference)
		{
			Assert.Equal(Head, reference);
			return lengths.TryGetValue(path, out var n) ? n : null;
		}

		public override List<JsonElement> PullFiles(BigInteger number) => files;

		public override JsonElement CreateReview(BigInteger number, Review review)
		{
			if (fail == "post")
				throw new CalledProcessError(["gh", "api"], 1, "");
			// A copy: Posted holds the first body, as GitHub got it, even after Run edits the review.
			Posted.Add((number, Json(review.ToJson())));
			return Answer?.Invoke() ?? Json(new JsonObject { ["id"] = 99, ["body"] = review.Body });
		}

		public override List<JsonElement> ReviewComments(BigInteger number, string reviewId)
		{
			Assert.Equal((new BigInteger(7), "99"), (number, reviewId));
			if (fail == "read")
				throw new CalledProcessError(["gh", "api"], 1, "");
			var comments = Posted[^1].Review.GetProperty("comments").EnumerateArray().ToList();
			return comments.Select((comment, index) =>
			{
				var node = JsonNode.Parse(comment.GetRawText())!.AsObject();
				node["html_url"] = $"https://github.com/octo/demo/pull/7#discussion_r{index + 1}";
				return Json(node);
			}).ToList();
		}

		public override void UpdateReview(BigInteger number, string reviewId, string body)
		{
			Assert.Equal((new BigInteger(7), "99"), (number, reviewId));
			if (fail == "update")
				throw new CalledProcessError(["gh", "api"], 1, "");
			Updates.Add(body);
		}

		public JsonElement Review(int index = 0) => Posted[index].Review;

		public string Body(int index = 0) => Review(index).GetProperty("body").GetString()!;

		public List<JsonElement> Comments(int index = 0) => Review(index).GetProperty("comments").EnumerateArray().ToList();
	}

	sealed record Result(int Code, string Out, string Err, FakeGitHub Gh);

	// main() with FINDINGS (and REDACTED) in its environment, --repo octo/demo --pr 7 --head abc123 and any extra arguments.
	static Result Run(string findings, FakeGitHub? gh = null, string[]? extra = null, string? redacted = null) =>
			RunWith(["--repo", "octo/demo", "--pr", "7", "--head", Head, .. extra ?? []], findings, gh, redacted);

	static Result RunWith(string[] args, string findings, FakeGitHub? gh = null, string? redacted = null)
	{
		gh ??= new FakeGitHub();
		StringWriter stdout = new(), stderr = new();
		var code = PostClaudeReview.Run(args, name => name switch
		{
			"FINDINGS" => findings,
			"REDACTED" => redacted,
			_ => null,
		}, stdout, stderr, repo =>
		{
			Assert.Equal("octo/demo", repo);
			return gh;
		});
		return new Result(code, stdout.ToString(), stderr.ToString(), gh);
	}

	static int LineOf(JsonElement comment) => comment.GetProperty("line").GetInt32();

	static string BodyOf(JsonElement comment) => comment.GetProperty("body").GetString()!;

	// diff_lines()

	[Fact]
	public void Test_diff_lines_are_the_new_side_lines_a_comment_can_sit_on()
	{
		var lines = PostClaudeReview.DiffLines([Json(new JsonObject { ["filename"] = "src/app.py", ["patch"] = Patch })]);

		Assert.Equal(["src/app.py"], lines.Keys);
		Assert.Equal([1, 2, 3, 4, 21, 22], lines["src/app.py"].Order().Select(n => (int)n));
	}

	[Fact]
	public void Test_line_breaks_other_than_newline_inside_a_line_dont_shift_the_count()
	{
		var patch = string.Join("\n", "@@ -1,2 +1,3 @@", " page one\fpage two", "+added\u0085text\u2028more", " context");

		var lines = PostClaudeReview.DiffLines([Json(new JsonObject { ["filename"] = "doc.txt", ["patch"] = patch })]);

		Assert.Equal([1, 2, 3], lines["doc.txt"].Order().Select(n => (int)n));
	}

	[Fact]
	public void Test_a_file_without_a_patch_has_no_commentable_lines()
	{
		var lines = PostClaudeReview.DiffLines([Json(new JsonObject { ["filename"] = "logo.png" })]);

		Assert.Empty(Assert.Single(lines).Value);
	}

	// main()

	[Fact]
	public void Test_findings_become_one_review_on_the_head_commit()
	{
		var gh = Run(FindingsJson(("src/app.py", 2, "Off by one."), ("src/app.py", 22, "Unused."))).Gh;

		var (number, review) = Assert.Single(gh.Posted);
		Assert.Equal(7, number);
		Assert.Equal(Head, review.GetProperty("commit_id").GetString());
		Assert.Equal("COMMENT", review.GetProperty("event").GetString());
		Assert.Equal(
				"""[{"path":"src/app.py","line":2,"side":"RIGHT","body":"Off by one."},{"path":"src/app.py","line":22,"side":"RIGHT","body":"Unused."}]""",
				JsonSerializer.Serialize(review.GetProperty("comments")));
		Assert.Contains("Looked at the diff.", gh.Body());
	}

	[Fact]
	public void Test_the_marker_is_always_first()
	{
		foreach (var findings in new[] { FindingsJson(), FindingsJson(("src/app.py", 2, "x")), FindingsJson(("other.py", 1, "y")) })
			Assert.StartsWith(PostClaudeReview.Marker + "\n", Run(findings).Gh.Body());
	}

	[Fact]
	public void Test_a_finding_off_the_diff_moves_to_the_body()
	{
		var gh = Run(FindingsJson(("src/app.py", 2, "On the diff."), ("src/app.py", 10, "Off the diff."), ("README.md", 1, "Not changed."))).Gh;

		Assert.Equal([2], gh.Comments().Select(LineOf));
		Assert.Contains("`src/app.py:10`", gh.Body());
		Assert.Contains("Off the diff.", gh.Body());
		Assert.Contains("`README.md:1`", gh.Body());
		Assert.Contains("Not changed.", gh.Body());
	}

	[Fact]
	public void Test_a_finding_off_the_diff_passes_the_step_with_a_warning()
	{
		// The marker, not the check, holds the merge: a failed check would leave the PR UNSTABLE, which PR Auto-Merge never merges.
		var result = Run(FindingsJson(("src/app.py", 2, "On."), ("README.md", 1, "Off.")));

		Assert.Equal(0, result.Code);
		Assert.Single(result.Gh.Posted);
		Assert.Contains("::warning::1 finding(s) outside the diff", result.Out);
		Assert.Contains("hold the merge", result.Gh.Body());
		// PR Auto-Merge reads this: it's the only thing holding the merge.
		Assert.StartsWith(PostClaudeReview.Marker + "\n" + PostClaudeReview.OffDiffMarker + "\n", result.Gh.Body());
	}

	[Fact]
	public void Test_a_line_past_the_end_of_a_changed_file_goes_on_its_nearest_diff_line()
	{
		// A wrong line number on changed code, not a finding about unchanged code: a thread to resolve, not a hold on the merge.
		var result = Run(FindingsJson(("src/app.py", 598, "Wrong number."), ("src/app.py", 0, "Zero.")));

		var comments = result.Gh.Comments();
		Assert.Equal([(22, "RIGHT"), (1, "RIGHT")], comments.Select(c => (LineOf(c), c.GetProperty("side").GetString())));
		Assert.Contains("cited line 598, which isn't a line of this file", BodyOf(comments[0]));
		Assert.EndsWith("Wrong number.", BodyOf(comments[0]));
		Assert.DoesNotContain(PostClaudeReview.OffDiffMarker, result.Gh.Body());
		Assert.DoesNotContain("::warning::", result.Out);
	}

	const string Suggested = "**Wrong default**\n\nIt is 0.\n\n```suggestion\nvalue = 1\n```";

	[Fact]
	public void Test_a_suggestion_on_the_cited_line_is_kept()
	{
		var gh = Run(FindingsJson(("src/app.py", 22, Suggested))).Gh;

		Assert.Equal(Suggested, BodyOf(gh.Comments()[0]));
	}

	[Fact]
	public void Test_a_suggestion_on_the_nearest_line_is_turned_into_plain_text()
	{
		// The suggestion would replace the nearest diff line, not the line Claude meant.
		var body = BodyOf(Run(FindingsJson(("src/app.py", 598, Suggested))).Gh.Comments()[0]);

		Assert.DoesNotContain("```suggestion", body);
		Assert.Contains("```text\nvalue = 1\n```", body);
	}

	[Theory]
	[InlineData("```suggestion\nx\n```", "```text\nx\n```")]
	[InlineData("````suggestion\nx\n````", "````text\nx\n````")]
	[InlineData("~~~ suggestion\nx\n~~~", "~~~text\nx\n~~~")]
	[InlineData("```SUGGESTION\nx\n```", "```text\nx\n```")]
	[InlineData("   ```suggestion\nx\n```", "   ```text\nx\n```")]
	[InlineData("a `suggestion` word and ```suggestions``` inline", "a `suggestion` word and ```suggestions``` inline")]
	[InlineData("```python\nsuggestion = 1\n```", "```python\nsuggestion = 1\n```")]
	public void Test_defuse_suggestions_changes_only_suggestion_fences(string body, string expected) =>
			Assert.Equal(expected, PostClaudeReview.DefuseSuggestions(body));

	[Theory]
	[InlineData("> ```suggestion\nx\n> ```")]
	[InlineData("- ```suggestion\n  x\n  ```")]
	[InlineData("a\r```suggestion\nx\n```")]
	[InlineData("    ```suggestion\nx\n```")]
	[InlineData("```suggestion foo\nx\n```")]
	[InlineData("```suggestion:-1+1\nx\ny\n```")]
	[InlineData("````\n```suggestion\nx\n```\n````")]
	[InlineData("```suggestion\r\nx\r\n```")]
	[InlineData("```\vsuggestion\nx\n```")]
	[InlineData("```\fsuggestion\nx\n```")]
	public void Test_defuse_suggestions_catches_every_form_a_fence_can_take(string body) =>
			Assert.DoesNotMatch(PostClaudeReview.SuggestionFence, PostClaudeReview.DefuseSuggestions(body));

	[Fact]
	public void Test_a_suggestion_fence_after_a_quote_marker_is_defused_to_text() =>
			Assert.Equal("> ```text\nx", PostClaudeReview.DefuseSuggestions("> ```suggestion\nx"));

	const string Good = "**T**\n\nWhy.\n\n```suggestion\n    value = 1\n```";

	public static TheoryData<string> KeptBodies => new(
			"No suggestion here.",
			Good,
			Good.Replace("```", "~~~", StringComparison.Ordinal),
			"```python\nsuggestion = 1\n```",
			"````suggestion\n```bash\n````", // content that can't close a longer fence
			"```suggestion\n\tvalue = 1  \n```", // a tab and trailing spaces are the line's own
			"```suggestion\n    ```\n```"); // four spaces make it indented text, not a closing fence

	[Theory]
	[MemberData(nameof(KeptBodies))]
	public void Test_vet_suggestions_keeps_no_suggestion_or_one_good_line(string body) =>
			Assert.Equal(body, PostClaudeReview.VetSuggestions(body));

	[Theory]
	[InlineData("```suggestion\na\nb\n```")] // two lines
	[InlineData("```suggestion\n\n```")] // nothing
	[InlineData("```suggestion\n   \n```")] // blank
	[InlineData("```suggestion:-1+1\nx\n```")] // the multi-line form
	[InlineData("```suggestion\nx\n```\n\n```suggestion\ny\n```")] // two blocks
	[InlineData("```suggestion\ntoken = [redacted]\n```")] // a click would write the redaction
	[InlineData("``` suggestion\nx\n```")] // not exactly the info string
	[InlineData("- ```suggestion\n  x\n  ```")] // not on its own lines
	[InlineData("```suggestion\nx\r\n```")] // a lone CR in the block
	[InlineData("```suggestion\n```\n```")] // the content closes the block: a click would delete the line
	[InlineData("~~~suggestion\n~~~\n~~~")]
	[InlineData("```suggestion\n  ```\n```")]
	[InlineData("````suggestion\n`````\n````")]
	[InlineData("```suggestion\nvalue = 1\n")] // never closed
	[InlineData("```suggestion\nvalue = 1\n~~~")] // closed by the other marker
	[InlineData("```suggestion\n\u200b\n```")] // a zero-width space is not a visible line
	[InlineData("```suggestion\nvalue\u202e = 1\n```")] // a bidirectional override
	[InlineData("```suggestion\nvalue\0 = 1\n```")]
	[InlineData("```suggestion\nvalue\u2028= 1\n```")]
	[InlineData("```suggestion\n\u3164\n```")] // a Hangul filler renders blank
	[InlineData("```suggestion\n \u2800 \n```")] // a blank Braille cell
	[InlineData("```suggestion\n\u0301\n```")] // a lone combining mark
	[InlineData("```suggestion\nx\n````")] // a closing fence longer than the opener
	[InlineData("```suggestion\nx\n  ```")] // a closing fence indented
	[InlineData("```\vsuggestion\nx\ny\n```")]
	public void Test_vet_suggestions_defuses_anything_but_one_good_line(string body)
	{
		var vetted = PostClaudeReview.VetSuggestions(body);

		Assert.NotEqual(body, vetted);
		Assert.DoesNotMatch(PostClaudeReview.SuggestionFence, vetted);
	}

	[Fact]
	public void Test_an_on_diff_comment_goes_through_vet_suggestions()
	{
		var body = BodyOf(Run(FindingsJson(("src/app.py", 22, "**T**\n\n```suggestion\na\nb\n```"))).Gh.Comments()[0]);

		Assert.DoesNotContain("```suggestion", body);
		Assert.Contains("```text\na\nb\n```", body);
	}

	[Fact]
	public void Test_a_finding_off_the_diff_keeps_a_table_and_a_list_under_its_bullet()
	{
		var lines = Run(FindingsJson(("src/app.py", 10, "**T**\n\n| a | b |\n| - | - |\n| 1 | 2 |\n\n- one\n- two"))).Gh.Body().Split('\n');

		foreach (var row in new[] { "  | a | b |", "  | - | - |", "  | 1 | 2 |", "  - one", "  - two" })
			Assert.Contains(row, lines);
	}

	[Fact]
	public void Test_a_finding_off_the_diff_keeps_its_paragraphs_under_its_bullet_and_loses_its_suggestion()
	{
		var body = Run(FindingsJson(("src/app.py", 10, "**Title**\n\nWhat goes wrong.\n\n```suggestion\nx = 1\n```"))).Gh.Body();

		Assert.Contains("- `src/app.py:10`: **Title**\n  \n  What goes wrong.", body);
		Assert.DoesNotContain("```suggestion", body);
		Assert.Contains("  ```text\n  x = 1\n  ```", body);
	}

	[Fact]
	public void Test_a_real_line_outside_the_hunks_still_holds_the_merge()
	{
		var gh = Run(FindingsJson(("src/app.py", 10, "Unchanged code."))).Gh;

		Assert.Empty(gh.Comments());
		Assert.StartsWith(PostClaudeReview.Marker + "\n" + PostClaudeReview.OffDiffMarker + "\n", gh.Body());
	}

	[Fact]
	public void Test_an_unreadable_file_length_keeps_the_hold()
	{
		// Unknown length: it may be a real line, so it's treated as one.
		var gh = Run(FindingsJson(("src/app.py", 598, "Maybe real.")), new FakeGitHub(lengths: [])).Gh;

		Assert.Empty(gh.Comments());
		Assert.Contains(PostClaudeReview.OffDiffMarker, gh.Body());
	}

	[Fact]
	public void Test_a_file_the_diff_doesnt_touch_still_holds_the_merge()
	{
		var gh = Run(FindingsJson(("README.md", 999, "Not changed."))).Gh;

		Assert.Empty(gh.Comments());
		Assert.Contains("`README.md:999`", gh.Body());
	}

	public static TheoryData<byte[], int> FileTexts => new()
	{
		{ [], 0 },
		{ "one\n"u8.ToArray(), 1 },
		{ "one\ntwo"u8.ToArray(), 2 },
		{ Encoding.UTF8.GetBytes("one\fstill one\u0085and\u2028one\n"), 1 },
		{ "a\nb\n"u8.ToArray(), 2 },
		{ [.. "caf"u8, 0xe9, .. "\nna"u8, 0xef, .. "ve\n"u8], 2 },
		{ "one\rstill one\n"u8.ToArray(), 1 },
	};

	// The pytest stubs subprocess.run; here the count is GitHub.CountLines, which LineCount gives gh's bytes to, and the path's quoting
	// is PostClaudeReview.Quote, which LineCount puts in the URL. Parity cases 04 and 05 run LineCount itself, on gh's answer and on its failure.
	[Theory]
	[MemberData(nameof(FileTexts))]
	public void Test_a_files_length_counts_newlines_only(byte[] text, int lines)
	{
		// Bytes, as gh returns them: a file that isn't UTF-8 (the Latin-1 case) still counts, and a lone \r isn't a newline.
		Assert.Equal(lines, GitHub.CountLines(text));
		Assert.Equal("docs/a%20b.md", PostClaudeReview.Quote("docs/a b.md"));
	}

	[Fact]
	public void Test_a_failed_post_fails_the_step() =>
			Assert.Throws<CalledProcessError>(() => Run(FindingsJson(("README.md", 1, "Off.")), new FakeGitHub(fail: "post")));

	[Fact]
	public void Test_findings_on_the_diff_only_pass()
	{
		var gh = Run(FindingsJson(("src/app.py", 2, "On."))).Gh;

		Assert.DoesNotContain("hold the merge", gh.Body());
		Assert.DoesNotContain(PostClaudeReview.OffDiffMarker, gh.Body());
	}

	[Fact]
	public void Test_no_findings_says_so()
	{
		var gh = Run(FindingsJson()).Gh;

		Assert.Empty(gh.Comments());
		Assert.Equal("### 🟢 No changes recommended", PostClaudeReview.NoChangesRecommended);
		Assert.Equal(PostClaudeReview.NoChangesRecommended, gh.Body().Split('\n')[1]);
		Assert.DoesNotContain("open issue", gh.Body());
	}

	// The body's layout

	// sections(): the body's parts, as the blank lines between them split it (a <details> block is one part).
	static List<string> Sections(string body)
	{
		List<string> parts = [], current = [];
		var depth = 0;
		foreach (var line in body.Split('\n'))
		{
			depth += Regex.Count(line, "<details") - Regex.Count(line, "</details>");
			if (line.Length == 0 && depth == 0 && current.Count > 0)
			{
				parts.Add(string.Join("\n", current));
				current = [];
			}
			else if (line.Length > 0 || depth != 0)
			{
				current.Add(line);
			}
		}
		if (current.Count > 0)
			parts.Add(string.Join("\n", current));
		return parts;
	}

	[Fact]
	public void Test_a_string_summary_renders_the_new_layout_with_the_summary_folded_as_it_is()
	{
		const string summary = "**Overview**\n\nAdds a cache.\n\n**Advisory (not blocking)**\n\n- A doc nit.";
		var body = Run(FindingsJsonWith(summary, ("src/app.py", 2, "**Off by one**\n\nThe loop stops early."))).Gh.Body();

		var lines = body.Split('\n');
		Assert.Equal(PostClaudeReview.Marker, lines[0]);
		Assert.Equal("### 🟡 Changes recommended", lines[1]);
		Assert.Equal("Off by one", lines[3]);
		Assert.Contains("<details open><summary><b>1 open issue</b></summary>\n\n- Off by one\n\n</details>", body);
		Assert.Contains($"<details><summary><b>What changed in this PR</b></summary>\n\n{summary}\n\n</details>", body);
		Assert.Equal("<sub>Claude Review</sub>", lines[^1]);
		Assert.DoesNotContain("**Claude Review**", body);
	}

	[Fact]
	public void Test_the_structured_input_renders_each_section()
	{
		var findings = Structured(new JsonObject
		{
			["overview"] = "Adds a shared cache.",
			["changes"] = new JsonArray("Adds `cache.sh`.", "Wires it into CI."),
			["files"] = new JsonArray(new JsonObject { ["path"] = "src/app.py", ["description"] = "The cache | its setup" }),
			["advisory"] = new JsonArray("A comment is stale."),
			["not_reviewed"] = "The vendored code.",
		},
				("src/app.py", 2, "**Cache init can delete another run's cache**\n\nDetail.", "high"),
				("src/app.py", 22, "**Child outlives the command**\n\nDetail.", "medium"));

		var gh = Run(findings, extra: ["--effort", "high (claude-sonnet-5-5), 2 batches"]).Gh;

		Assert.Equal(
		[
			$"{PostClaudeReview.Marker}\n### 🟡 Changes recommended",
			"Cache init can delete another run's cache",
			"<details open><summary><b>2 open issues</b></summary>\n\n"
					+ "- ![High](https://img.shields.io/badge/High-d1242f?style=flat-square) Cache init can delete another run's cache\n"
					+ "- ![Medium](https://img.shields.io/badge/Medium-bf8700?style=flat-square) Child outlives the command\n\n"
					+ "</details>",
			"<details><summary><b>What changed in this PR</b></summary>\n\nAdds a shared cache.\n\n**Changes:**\n\n"
					+ "- Adds `cache.sh`.\n- Wires it into CI.\n\n| File | Description |\n| --- | --- |\n"
					+ "| `src/app.py` | The cache \\| its setup |\n\n</details>",
			"**Advisory (not blocking)**",
			"- A comment is stale.",
			"**Not reviewed**",
			"The vendored code.",
			"<sub>Claude Review · Review effort: high (claude-sonnet-5-5), 2 batches</sub>",
		], Sections(gh.Body()));
	}

	[Fact]
	public void Test_a_section_left_out_of_the_input_is_left_out_of_the_body()
	{
		var body = Run(Structured(new JsonObject { ["overview"] = "Only an overview." }, ("src/app.py", 2, "**T**", null))).Gh.Body();

		Assert.Contains("Only an overview.", body);
		foreach (var absent in new[] { "**Changes:**", "| File |", "**Advisory", "**Not reviewed**", "Findings outside the diff" })
			Assert.DoesNotContain(absent, body);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("  ")]
	public void Test_without_an_effort_the_footer_says_only_claude_review(string? effort)
	{
		var gh = Run(FindingsJson(), extra: effort is null ? [] : ["--effort", effort]).Gh;

		Assert.EndsWith("\n\n<sub>Claude Review</sub>", gh.Body());
	}

	[Fact]
	public void Test_the_effort_is_kept_on_one_line_and_cant_open_a_tag()
	{
		var gh = Run(FindingsJson(), extra: ["--effort", "high\n- <b>x</b>"]).Gh;

		Assert.EndsWith("\n\n<sub>Claude Review · Review effort: high - &lt;b&gt;x&lt;/b&gt;</sub>", gh.Body());
	}

	[Theory]
	[InlineData(null, "low", "medium", "two")]
	[InlineData("low", "high", "high", "one")]
	[InlineData(null, null, null, "zero")]
	[InlineData("medium", "medium", "low", "zero")]
	public void Test_the_top_finding_is_the_most_severe_first_on_a_tie(string? first, string? second, string? third, string top)
	{
		string?[] severities = [first, second, third];
		var findings = Structured(null, [.. new[] { "zero", "one", "two" }.Select((name, i) => ("src/app.py", 2, $"**{name}**\n\nBody.", severities[i]))]);

		Assert.Equal(top, Sections(Run(findings).Gh.Body())[1]);
	}

	[Fact]
	public void Test_open_issues_sort_high_medium_low_then_unrated_keeping_claudes_order_within_each()
	{
		(string Name, string? Severity)[] order = [("a", null), ("b", "low"), ("c", "high"), ("d", "medium"), ("e", "high"), ("f", null), ("g", "low")];
		var body = Run(Structured(null, [.. order.Select(o => ("src/app.py", 2, $"**{o.Name}**", o.Severity))])).Gh.Body();

		var listed = body.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal))
				.Select(line => line[(line.LastIndexOf(' ') + 1)..].Trim('[', ']').Split("](")[0]);
		Assert.Equal(["c", "e", "d", "b", "g", "a", "f"], listed);
	}

	[Fact]
	public void Test_a_finding_without_a_severity_has_no_badge()
	{
		var body = Run(Structured(null, ("src/app.py", 2, "**Plain**", null))).Gh.Body();

		Assert.Contains("- Plain\n", body);
		Assert.DoesNotContain("img.shields.io", body);
	}

	[Theory]
	[InlineData("**Bold title.** Then the rest.\n\nMore.", "Bold title.")]
	[InlineData("No bold title here.\n\nSecond paragraph.", "No bold title here.")]
	[InlineData("\n  Leading blank line, **not bold at the start**", "Leading blank line, **not bold at the start**")]
	[InlineData("**  Spaced   title  **", "Spaced title")]
	public void Test_a_findings_title_is_its_bold_first_line_or_else_its_first_line(string body, string title) =>
			Assert.Equal(title, PostClaudeReview.TitleOf(body));

	[Fact]
	public void Test_a_long_title_is_cut_with_an_ellipsis()
	{
		var title = PostClaudeReview.TitleOf("**" + string.Concat(Enumerable.Repeat("word ", 100)) + "**");

		Assert.True(title.Length <= PostClaudeReview.MaxTitle);
		Assert.EndsWith("…", title);
	}

	[Fact]
	public void Test_the_markers_and_the_off_diff_section_are_as_before_and_last_with_bullets()
	{
		var findings = Structured(new JsonObject
		{
			["overview"] = "O.",
			["changes"] = new JsonArray("c"),
			["advisory"] = new JsonArray("a"),
			["not_reviewed"] = "n",
		},
				("src/app.py", 2, "**On**\n\nText.", "high"), ("README.md", 1, "**Off one**\n\nLine two.", "low"), ("src/app.py", 10, "Off two", null));

		var lines = Run(findings).Gh.Body().Split('\n');

		Assert.Equal(PostClaudeReview.Marker, lines[0]);
		Assert.Equal(PostClaudeReview.OffDiffMarker, lines[1]);
		var header = Array.IndexOf(lines, "Findings outside the diff (no thread to resolve, so they hold the merge until the next push gets a fresh review):");
		var after = lines[(header + 1)..];
		Assert.Equal(["- `README.md:1`: **Off one**", "- `src/app.py:10`: Off two"], after.Where(line => line.StartsWith("- ", StringComparison.Ordinal)));
		Assert.Contains("  Line two.", after);
		Assert.StartsWith("<sub>", after[^1]);
	}

	[Fact]
	public void Test_the_thread_links_are_added_by_editing_the_posted_review()
	{
		var gh = Run(Structured(null, ("src/app.py", 2, "**Low one**", "low"), ("src/app.py", 22, "**High [one]**", "high"), ("README.md", 1, "Off.", null))).Gh;

		var first = gh.Body();
		Assert.DoesNotContain("discussion_r", first);
		var linked = Assert.Single(gh.Updates);
		Assert.Contains("- ![High](https://img.shields.io/badge/High-d1242f?style=flat-square) "
				+ "[High \\[one\\]](https://github.com/octo/demo/pull/7#discussion_r2)", linked);
		Assert.Contains("- ![Low](https://img.shields.io/badge/Low-0969da?style=flat-square) "
				+ "[Low one](https://github.com/octo/demo/pull/7#discussion_r1)", linked);
		// PR Auto-Merge reads the same markers and off-diff section before and after the edit.
		Assert.Equal(first.Split('\n')[..2], linked.Split('\n')[..2]);
		Assert.Equal(first[first.IndexOf("Findings outside", StringComparison.Ordinal)..], linked[linked.IndexOf("Findings outside", StringComparison.Ordinal)..]);
	}

	[Fact]
	public void Test_no_edit_when_no_thread_was_opened() => Assert.Empty(Run(FindingsJson(("README.md", 1, "Off."))).Gh.Updates);

	[Theory]
	[InlineData("read")]
	[InlineData("update")]
	public void Test_a_failed_link_read_or_update_warns_and_keeps_the_first_body(string fail)
	{
		var result = Run(FindingsJson(("src/app.py", 2, "**Title**")), new FakeGitHub(fail: fail));

		Assert.Equal(0, result.Code);
		Assert.Empty(result.Gh.Updates);
		Assert.Contains("- Title\n", result.Gh.Body());
		Assert.Contains("::warning::Couldn't link the review's threads", result.Out);
	}

	[Fact]
	public void Test_a_post_that_returns_no_id_warns_and_keeps_the_first_body()
	{
		var result = Run(FindingsJson(("src/app.py", 2, "**Title**")), new FakeGitHub { Answer = () => Json("{}") });

		Assert.Empty(result.Gh.Updates);
		Assert.Contains("::warning::Couldn't link", result.Out);
	}

	[Fact]
	public void Test_thread_links_match_by_path_and_body_and_fall_back_to_order()
	{
		List<Comment> comments = [new("a", 1, "x"), new("a", 1, "y")];
		static List<JsonElement> Posted(string json) => Json(json).EnumerateArray().ToList();

		Assert.Equal(["https://g/1", "https://g/2"], PostClaudeReview.ThreadLinks(comments,
				Posted("""[{"path": "a", "body": "y", "html_url": "https://g/2"}, {"path": "a", "body": "x", "html_url": "https://g/1"}]""")));
		// GitHub changed a body: it takes the URL at its place.
		Assert.Equal(["https://g/1", "https://g/2"], PostClaudeReview.ThreadLinks(comments,
				Posted("""[{"path": "a", "body": "x\r\n", "html_url": "https://g/1"}, {"path": "a", "body": "y", "html_url": "https://g/2"}]""")));
		// A different count, or a URL that isn't https, links nothing it can't match.
		Assert.Equal([null, null], PostClaudeReview.ThreadLinks(comments, Posted("""[{"path": "a", "body": "q", "html_url": "https://g/1"}]""")));
		Assert.Equal([null, null], PostClaudeReview.ThreadLinks(comments,
				Posted("""[{"path": "a", "body": "x", "html_url": "javascript:x"}, {"path": "a", "body": "y"}]""")));
	}

	[Fact]
	public void Test_open_issues_past_the_limit_are_left_out_with_a_note()
	{
		var findings = new JsonArray();
		for (var i = 0; i < 400; i++)
			findings.Add(new JsonObject { ["path"] = "src/app.py", ["line"] = 2, ["body"] = $"**{new string('t', 199)}**", ["severity"] = "high" });
		var body = Run(new JsonObject { ["summary"] = new string('s', 1000), ["findings"] = findings }.ToJsonString()).Gh.Body();

		Assert.True(body.Length <= PostClaudeReview.MaxBody);
		Assert.Contains(PostClaudeReview.WhatChangedCut, body);
		Assert.Matches(@"_\d+ more in the threads below, left out to fit GitHub's limit\._", body);
		Assert.Contains("400 open issues", body);
	}

	public static TheoryData<string> LongExtras => new(
			new JsonObject { ["not_reviewed"] = new string('n', 70_000) }.ToJsonString(),
			new JsonObject { ["advisory"] = new JsonArray([.. Enumerable.Repeat(new string('a', 1000), 70).Select(a => JsonValue.Create(a))]) }.ToJsonString());

	[Theory]
	[MemberData(nameof(LongExtras))]
	public void Test_long_advisory_or_not_reviewed_is_left_out_to_fit_and_keeps_the_off_diff(string extras)
	{
		var answer = JsonNode.Parse(extras)!.AsObject();
		answer["overview"] = "Short.";
		answer["findings"] = new JsonArray(new JsonObject { ["path"] = "README.md", ["line"] = 1, ["body"] = "Off the diff, whole." });

		var body = Run(answer.ToJsonString()).Gh.Body();

		Assert.True(body.Length <= PostClaudeReview.MaxBody);
		Assert.Contains(PostClaudeReview.ExtrasCut, body);
		Assert.Contains(PostClaudeReview.WhatChangedCut, body);
		Assert.StartsWith($"{PostClaudeReview.Marker}\n{PostClaudeReview.OffDiffMarker}\n", body);
		Assert.Contains("- `README.md:1`: Off the diff, whole.", body);
	}

	[Fact]
	public void Test_short_advisory_and_not_reviewed_are_kept()
	{
		var body = Run("""{"overview": "Short.", "advisory": ["Rename it."], "not_reviewed": "The lock file.", "findings": []}""").Gh.Body();

		Assert.DoesNotContain(PostClaudeReview.ExtrasCut, body);
		Assert.Contains("- Rename it.", body);
		Assert.Contains("The lock file.", body);
	}

	[Fact]
	public void Test_a_long_effort_is_capped_in_the_footer()
	{
		var body = Run(FindingsJson(), extra: ["--effort", new string('e', 70_000)]).Gh.Body();

		Assert.True(body.Length <= PostClaudeReview.MaxBody);
		Assert.EndsWith($"<sub>Claude Review · Review effort: {new string('e', PostClaudeReview.MaxEffort - 1)}…</sub>", body);
	}

	[Fact]
	public void Test_a_files_entry_keeps_only_path_and_description()
	{
		const string answer = """{"overview": "o", "files": [{"path": "src/a.py", "description": "d", "additions": 1}], "findings": []}""";

		var (report, _) = PostClaudeReview.ParseFindings(answer);
		var body = Run(answer).Gh.Body();

		Assert.Equal([new FileRow("src/a.py", "d")], report.Files!);
		Assert.Contains("`src/a.py`", body);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("""{"findings": []}""")]
	[InlineData("""{"summary": "s", "findings": [{"path": "a.py", "body": "no line"}]}""")]
	[InlineData("""{"summary": "s", "findings": [{"path": "a.py", "line": "2", "body": "b"}]}""")]
	[InlineData("""{"summary": "s", "findings": "none"}""")]
	[InlineData("""{"summary": "s", "findings": [{"path": "a.py", "line": 2, "body": "b", "severity": "critical"}]}""")]
	[InlineData("""{"summary": "s", "findings": [{"path": "a.py", "line": 2, "body": "b", "severity": "High"}]}""")]
	[InlineData("""{"summary": "s", "findings": [{"path": "a.py", "line": 2, "body": "b", "severity": null}]}""")]
	[InlineData("""{"summary": "s", "changes": "one change", "findings": []}""")]
	[InlineData("""{"summary": "s", "changes": ["ok", 3], "findings": []}""")]
	[InlineData("""{"summary": "s", "files": [{"path": "a.py"}], "findings": []}""")]
	[InlineData("""{"summary": "s", "files": {"path": "a.py", "description": "d"}, "findings": []}""")]
	[InlineData("""{"summary": "s", "files": [{"path": 1, "description": "d"}], "findings": []}""")]
	[InlineData("""{"summary": "s", "advisory": "a", "findings": []}""")]
	[InlineData("""{"summary": "s", "not_reviewed": ["n"], "findings": []}""")]
	[InlineData("""{"overview": 3, "findings": []}""")]
	[InlineData("""{"summary": 3, "overview": "o", "findings": []}""")]
	public void Test_malformed_findings_fail_with_a_clear_message_and_post_nothing(string findings)
	{
		var result = Run(findings);

		Assert.Equal(1, result.Code);
		Assert.Contains("Claude Review's findings", result.Err);
		Assert.Empty(result.Gh.Posted);
	}

	[Theory]
	[InlineData("")]
	[InlineData("  \n")]
	public void Test_empty_findings_say_a_secret_was_withheld_and_post_nothing(string findings)
	{
		// The post job runs only once Claude has answered, so empty findings mean the answer quoted a secret and was withheld.
		var result = Run(findings);

		Assert.Equal(1, result.Code);
		Assert.StartsWith("::error::", result.Out);
		Assert.Contains("quoted a secret", result.Out);
		Assert.Contains("rotate CLAUDE_CODE_OAUTH_TOKEN", result.Out);
		Assert.Empty(result.Gh.Posted);
	}

	[Fact]
	public void Test_posting_needs_repo_pr_and_head() => Assert.Equal(2, RunWith(["--repo", "octo/demo"], FindingsJson()).Code);

	public static TheoryData<string> LongSuggestionBodies => new(
			"```suggestion\n" + new string('a', 200_000),
			new string('`', 200_000),
			new string('~', 200_000),
			new string('`', 100_000) + new string(' ', 100_000),
			new string('~', 100_000) + new string('\t', 100_000),
			"```" + new string(' ', 200_000));

	[Theory]
	[MemberData(nameof(LongSuggestionBodies))]
	public void Test_suggestion_handling_takes_linear_time_on_long_input(string body)
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		PostClaudeReview.VetSuggestions(body);
		PostClaudeReview.DefuseSuggestions(body);

		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
	}
}
