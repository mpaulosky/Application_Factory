using System.Diagnostics;
using System.Numerics;

namespace Baseline.Tests;

// The Template's .github/scripts/DraftRelease.cs: test_draft_release.py's tests, one method each under its name (ADR 0017's rule), with
// the same cases and assertions, and the port's own after them. In Baseline's namespace, so Script and Arguments are the Template's. The
// planner's source_pr_of() is still release_post.py's, so the tests that hold the markers to it ask Python.
public sealed class DraftReleaseTests : IDisposable
{
	const string Notes = "Source PR: #15\nSource PR URL: https://example.test/pull/15";

	// pytest's tmp_path: a folder of the test's own, removed when it ends.
	readonly string tmp = Directory.CreateTempSubdirectory("draft-release.").FullName;

	public void Dispose() => Directory.Delete(tmp, true);

	// release_post.source_pr_of({"body": body}) from the Template's release_post.py: the PR number, or null for None.
	static BigInteger? SourcePrOf(string body)
	{
		var scripts = Path.Join(Skill.Dir, "template", "owned", ".github", "scripts");
		var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in new[] { "-I", "-c", "import sys; sys.path.insert(0, sys.argv[1]); import release_post as rp; print(rp.source_pr_of({'body': sys.argv[2]}))", scripts, body })
			start.ArgumentList.Add(arg);
		using var run = Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = run.StandardOutput.ReadToEnd().Trim();
		run.WaitForExit();
		Assert.True(run.ExitCode == 0, err.Result);
		return output == "None" ? null : BigInteger.Parse(output, System.Globalization.CultureInfo.InvariantCulture);
	}

	// main(args) with stdin's text: (exit code, stdout, stderr).
	static (int Code, string Out, string Err) RunMain(string[] args, string stdin)
	{
		StringWriter stdout = new(), stderr = new();
		var code = DraftRelease.Run(args, () => stdin, stdout, stderr);
		return (code, stdout.ToString(), stderr.ToString());
	}

	[Fact]
	public void A_draft_without_a_marker_gets_the_notes_appended()
	{
		var body = DraftRelease.PublishBody("Hand-written notes.", 15, Notes);

		Assert.Equal($"Hand-written notes.\n\n{Notes}\n", body);
	}

	[Fact]
	public void An_empty_draft_gets_just_the_notes()
	{
		Assert.Equal($"{Notes}\n", DraftRelease.PublishBody("", 15, Notes));
		Assert.Equal($"{Notes}\n", DraftRelease.PublishBody(null, 15, Notes));
	}

	[Fact]
	public void A_draft_naming_this_pr_on_its_own_line_is_published_unchanged()
	{
		var body = "Notes.\n\n  Source PR: #15  \nMerge commit: abc";

		Assert.Equal(body, DraftRelease.PublishBody(body, 15, Notes));
	}

	[Fact]
	public void A_draft_with_crlf_endings_naming_this_pr_is_published_unchanged()
	{
		var body = "Notes.\r\nSource PR: #15\r\nMerge commit: abc\r\n";

		Assert.Equal(body, DraftRelease.PublishBody(body, 15, Notes));
	}

	[Fact]
	public void A_draft_naming_this_pr_only_inside_a_longer_line_gets_its_own_line()
	{
		var body = "- Source PR: #15";

		var result = DraftRelease.PublishBody(body, 15, Notes);

		Assert.Equal($"{body}\n\n{Notes}\n", result);
		Assert.Equal(15, SourcePrOf(result));
	}

	[Theory]
	[InlineData("Source PR: #5")]
	[InlineData("- Source PR: #5")]
	[InlineData("Notes.\nSource PR: #15\nSource PR: #5")]
	[InlineData("Source PR: #150")]
	[InlineData("Source PR: #1")]
	public void A_draft_naming_another_pr_fails(string body) => Assert.Throws<OtherPrError>(() => DraftRelease.PublishBody(body, 15, Notes));

	[Fact]
	public void Numbers_match_exactly()
	{
		// PR 5 against a draft naming #15 and #50, and the reverse.
		Assert.Contains("#15", Assert.Throws<OtherPrError>(() => DraftRelease.PublishBody("Source PR: #15", 5, Notes)).Message);
		Assert.Contains("#5,", Assert.Throws<OtherPrError>(() => DraftRelease.PublishBody("Source PR: #5", 50, Notes)).Message);
	}

	[Theory]
	[InlineData("Source PR: #7")]
	[InlineData("- Source PR: #7 (manual)")]
	[InlineData("x\n  Source PR: #7\n")]
	[InlineData("**Source PR:** [#7](url)")]
	public void Markers_match_the_planner(string body)
	{
		var named = DraftRelease.NamedPrs(body);
		BigInteger? first = named.Count > 0 ? named[0] : null;

		Assert.Equal(SourcePrOf(body), first);
	}

	[Fact]
	public void Main_prints_the_body()
	{
		var notes = Path.Join(tmp, "notes.txt");
		File.WriteAllText(notes, $"{Notes}\n");

		var (code, output, _) = RunMain(["--pr", "15", "--notes-file", notes], "Draft.");

		Assert.Equal(0, code);
		Assert.Equal($"Draft.\n\n{Notes}\n", output);
	}

	[Fact]
	public void Main_fails_on_another_pr_with_the_error_on_stderr()
	{
		var notes = Path.Join(tmp, "notes.txt");
		File.WriteAllText(notes, Notes);

		var (code, output, err) = RunMain(["--pr", "15", "--tag", "v1.2.3", "--notes-file", notes], "Source PR: #5");

		Assert.Equal(1, code);
		Assert.Equal("", output);
		Assert.Contains("::error::The draft Release v1.2.3 names PR #5, not #15.", err);
	}

	// The port's own: what Python's \d and int() read that .NET's Regex and int.Parse don't, held to the planner.

	[Theory]
	[InlineData("Source PR: #\U0001D7D2\U0001D7D0")]
	[InlineData("Source PR: #\u0664\u0662 and Source PR: #7")]
	[InlineData("Source PR: #042")]
	[InlineData("Source PR: #123456789012345678901234567890")]
	[InlineData("Source PR: #")]
	[InlineData("Source PR: #Source PR: #9")]
	[InlineData("")]
	public void Markers_with_any_decimal_digits_match_the_planner(string body)
	{
		var named = DraftRelease.NamedPrs(body);
		BigInteger? first = named.Count > 0 ? named[0] : null;

		Assert.Equal(SourcePrOf(body), first);
	}

	[Fact]
	public void Every_marker_is_named_in_order() =>
			Assert.Equal([42, 7, 42], DraftRelease.NamedPrs("Source PR: #\U0001D7D2\U0001D7D0\nSource PR: #7 Source PR: #0042"));

	[Theory]
	[InlineData("Source PR: #42", 42)]
	[InlineData("Source PR: #-42", -42)]
	[InlineData("Source PR: #0", 0)]
	public void This_prs_number_as_str_writes_it_on_a_line_of_its_own_is_its_marker(string body, int pr) =>
			// A negative or zero --pr is int() too, and its marker is str() of it: "#-42" names no PR, so it is that one's own line.
			Assert.Equal(body, DraftRelease.PublishBody(body, pr, Notes));

	[Fact]
	public void A_negative_pr_is_another_pr_than_its_number() =>
			Assert.Equal("names PR #42, not #-42", Assert.Throws<OtherPrError>(() => DraftRelease.PublishBody("Source PR: #42", -42, Notes)).Message);

	[Fact]
	public void Main_reads_the_notes_as_utf8_with_their_line_endings_translated_and_its_byte_order_mark_kept()
	{
		var notes = Path.Join(tmp, "notes.txt");
		File.WriteAllBytes(notes, [0xef, 0xbb, 0xbf, (byte)'a', (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', (byte)'c', (byte)'\r', (byte)'\n', (byte)'\n']);

		var (code, output, _) = RunMain(["--pr", "15", "--notes-file", notes], "");

		Assert.Equal(0, code);
		Assert.Equal("\ufeffa\nb\nc\n", output);
	}

	[Fact]
	public void Main_fails_on_notes_that_arent_utf8()
	{
		var notes = Path.Join(tmp, "notes.txt");
		File.WriteAllBytes(notes, [0xff]);

		Assert.Throws<System.Text.DecoderFallbackException>(() => RunMain(["--pr", "15", "--notes-file", notes], ""));
	}

	[Fact]
	public void Main_fails_on_a_missing_notes_file_before_reading_stdin()
	{
		var read = false;

		Assert.Throws<FileNotFoundException>(() => DraftRelease.Run(["--pr", "15", "--notes-file", Path.Join(tmp, "missing")], () =>
		{
			read = true;
			return "";
		}, new StringWriter(), new StringWriter()));
		Assert.False(read);
	}

	[Fact]
	public void Main_names_the_draft_without_a_tag()
	{
		var notes = Path.Join(tmp, "notes.txt");
		File.WriteAllText(notes, Notes);

		var (code, _, err) = RunMain(["--pr", "15", "--notes-file", notes], "Source PR: #5");

		Assert.Equal(1, code);
		Assert.Equal("::error::The draft Release the draft names PR #5, not #15. Fix or delete the draft, then re-run.\n", err);
	}

	[Theory]
	[InlineData("x", "argument --pr: invalid int value: 'x'")]
	[InlineData("it's", "argument --pr: invalid int value: \"it's\"")]
	[InlineData("1__5", "argument --pr: invalid int value: '1__5'")]
	public void Main_refuses_a_pr_that_isnt_an_int(string pr, string error)
	{
		var (code, output, err) = RunMain(["--pr", pr, "--notes-file", "unread"], "");

		Assert.Equal(2, code);
		Assert.Equal("", output);
		Assert.Equal($"usage: draft_release.cs [-h] --pr PR --notes-file NOTES_FILE [--tag TAG]\ndraft_release.cs: error: {error}\n", err);
	}

	[Fact]
	public void Main_prints_its_help()
	{
		var (code, output, _) = RunMain(["--help"], "");

		Assert.Equal(0, code);
		Assert.StartsWith("usage: draft_release.cs [-h] --pr PR --notes-file NOTES_FILE [--tag TAG]\n\nPrint the body to publish a draft Release with", output);
		Assert.Contains("  --pr PR               the PR being released\n", output);
	}
}
