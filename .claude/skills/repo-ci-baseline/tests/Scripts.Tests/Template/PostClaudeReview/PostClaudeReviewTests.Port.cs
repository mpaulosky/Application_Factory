using System.ComponentModel;
using System.Text.Json;

namespace Baseline.PostReview.Tests;

// What the port does that the pytest file had no need to test, since Python did it: how gh's failures and output are read, and how values
// json.loads made behave where the script reads them. Each outcome is the .py's on the same input (diff_lines() under python3), or, for gh,
// what its capture_output did.
public sealed partial class PostClaudeReviewTests
{
	// A folder with a gh that writes a workflow command to stderr and exits 1, as a failing gh might echo the request in its error, or that
	// runs the script given.
	sealed class FailingGh : IDisposable
	{
		public string Path { get; } = Directory.CreateTempSubdirectory("post-claude-review-gh-").FullName;

		public string Gh => System.IO.Path.Combine(Path, "gh");

		// script, when given, is the gh's body instead.
		public FailingGh(string? script = null)
		{
			File.WriteAllText(Gh, "#!/bin/sh\n" + (script ?? "echo '::warning::from gh' >&2\necho '::error::from gh' >&2\nexit 1\n"));
			// The scripts run on Linux runners; the guard is for the analyzer.
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(Gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		public void Dispose() => Directory.Delete(Path, true);
	}

	[Fact]
	public void Gh_s_stderr_stays_out_of_a_failed_calls_error_and_the_log()
	{
		using var folder = new FailingGh();
		var error = Assert.Throws<CalledProcessError>(() => new GitHub("octo/demo", () => folder.Gh).PullFiles(7));
		Assert.Equal("Command 'gh api --paginate --slurp repos/octo/demo/pulls/7/files?per_page=100' returned non-zero exit status 1.", error.Message);

		// The step's whole output, as Script.Run writes the failure: no line the runner would read as a workflow command.
		StringWriter stdout = new(), stderr = new();
		var code = Script.Run((o, e) => PostClaudeReview.Run(["--repo", "octo/demo", "--pr", "7", "--head", Head],
				name => name == "FINDINGS" ? FindingsJson() : null, o, e, repo => new GitHub(repo, () => folder.Gh)), stdout, stderr);
		Assert.Equal(1, code);
		Assert.Contains("CalledProcessError: Command 'gh api --paginate --slurp", stderr.ToString());
		Assert.DoesNotContain("from gh", stdout.ToString() + stderr.ToString());
	}

	// gh's stderr is read to its end and dropped: a gh that writes more than a pipe holds (64 KB on Linux) to it still finishes, and its
	// stdout comes through.
	[Fact]
	public async Task A_gh_that_writes_much_to_stderr_still_finishes()
	{
		using var folder = new FailingGh("head -c 300000 /dev/zero | tr '\\0' x >&2\necho '[[{\"filename\": \"a.py\"}]]'\n");
		var call = Task.Run(() => new GitHub("octo/demo", () => folder.Gh).PullFiles(7), TestContext.Current.CancellationToken);
		// A TimeoutException when gh's stderr filled its pipe, so the call never returned.
		var files = await call.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
		Assert.Equal("a.py", Assert.Single(files).GetProperty("filename").GetString());
	}

	// The linking step's warning names the error as the .py's type(error).__name__ did for gh missing and for any other OSError.
	public static TheoryData<string, string> LinkErrors => new()
	{
		{ "FileNotFoundException", "FileNotFoundError" },
		{ "IOException", "OSError" },
		{ "Win32Exception", "OSError" },
	};

	[Theory]
	[MemberData(nameof(LinkErrors))]
	public void A_linking_failure_is_named_as_python_named_it(string thrown, string name)
	{
		Exception error = thrown switch
		{
			"FileNotFoundException" => new FileNotFoundException("gh"),
			"IOException" => new IOException("read"),
			_ => new Win32Exception(2),
		};
		var result = Run(FindingsJson(("src/app.py", 2, "On.")), new FakeGitHub { ReadError = error });
		Assert.Equal(0, result.Code);
		Assert.Contains($"::warning::Couldn't link the review's threads from its body ({name}),", result.Out);
	}

	// diff_lines() on a listing GitHub never sends: an entry that isn't an object, a filename that can't be a dict key, a patch that is
	// truthy but not a str (each a TypeError or AttributeError, so the step fails), and every falsy patch (no lines).
	public static TheoryData<string, string, string> MalformedListings => new()
	{
		{ "\"a.py\"", "TypeError", "" },
		{ "null", "TypeError", "" },
		{ "5", "TypeError", "" },
		{ "[1]", "TypeError", "" },
		{ "{\"filename\": [1]}", "TypeError", "unhashable type: 'list'" },
		{ "{\"filename\": {\"a\": 1}}", "TypeError", "unhashable type: 'dict'" },
		{ "{\"filename\": \"a\", \"patch\": 5}", "AttributeError", "'int' object has no attribute 'split'" },
		{ "{\"filename\": \"a\", \"patch\": [1]}", "AttributeError", "'list' object has no attribute 'split'" },
		{ "{\"filename\": \"a\", \"patch\": {\"a\": 1}}", "AttributeError", "'dict' object has no attribute 'split'" },
		{ "{\"filename\": \"a\", \"patch\": true}", "AttributeError", "'bool' object has no attribute 'split'" },
		{ "{\"filename\": \"a\", \"patch\": 1e400}", "AttributeError", "'float' object has no attribute 'split'" },
		{ "{\"filename\": \"a\", \"patch\": 1E5}", "AttributeError", "'float' object has no attribute 'split'" },
		{ "{\"filename\": \"a\", \"patch\": -0.5}", "AttributeError", "'float' object has no attribute 'split'" },
	};

	[Theory]
	[MemberData(nameof(MalformedListings))]
	public void A_malformed_listing_fails_as_diff_lines_did(string file, string name, string message)
	{
		var error = Assert.Throws<PyError>(() => PostClaudeReview.DiffLines([Json(file)]));
		Assert.Equal(name, error.Name);
		Assert.Contains(message, error.Message);
	}

	// A patch of the longest integer Parse lets through is truthy (a double reads it as inf), so it fails as an int's would.
	[Fact]
	public void A_patch_of_4300_digits_is_a_truthy_int()
	{
		var error = Assert.Throws<PyError>(() => PostClaudeReview.DiffLines([Json($"{{\"filename\": \"a\", \"patch\": {new string('1', 4300)}}}")]));
		Assert.Equal("AttributeError", error.Name);
		Assert.Contains("'int' object has no attribute 'split'", error.Message);
	}

	public static TheoryData<string> FalsyPatches => ["0", "-0", "0.0", "0e5", "0E5", "1e-400", "1E-400", "-0.0", "[]", "{}", "false", "null", "\"\""];

	[Theory]
	[MemberData(nameof(FalsyPatches))]
	public void A_falsy_patch_is_no_patch(string patch)
	{
		var lines = PostClaudeReview.DiffLines([Json($"{{\"filename\": \"a\", \"patch\": {patch}}}")]);
		Assert.Empty(Assert.Contains("a", lines));
	}

	[Fact]
	public void A_filename_that_isnt_a_str_takes_no_finding()
	{
		// The .py keys it by the number, null or bool; a finding's path is a str, so none can sit on it.
		foreach (var name in new[] { "5", "null", "true" })
			Assert.Empty(PostClaudeReview.DiffLines([Json($"{{\"filename\": {name}, \"patch\": \"@@ -1 +1 @@\\n+x\"}}")]));
	}

	// for item in value: a dict gives its keys in order, a str its code points (an astral one whole).
	[Fact]
	public void Iterating_a_dict_or_a_str_is_as_python_iterates_it()
	{
		Assert.Equal(["b", "a"], PyJson.Iterate(Json("{\"b\": 1, \"a\": 2}")).Select(item => item.GetString()!));
		Assert.Equal(["a", "\U0001D7D3", "b"], PyJson.Iterate(Json("\"a\U0001D7D3b\"")).Select(item => item.GetString()!));
		Assert.Equal("TypeError", Assert.Throws<PyError>(() => PyJson.Iterate(Json("5"))).Name);
	}

	// json.loads refuses an integer past 4,300 digits wherever it is (a ValueError), and reads a float of any length and nesting deeper
	// than .NET's default of 64.
	[Fact]
	public void Parse_takes_python_s_integer_limit_and_depth()
	{
		var digits = new string('1', 4300);
		Assert.Equal(JsonValueKind.Object, PyJson.Parse($"{{\"a\": [-{digits}]}}").ValueKind);
		Assert.Equal(JsonValueKind.Object, PyJson.Parse($"{{\"a\": 1.{digits}1, \"b\": {digits}1e1, \"c\": -{digits}1E-1}}").ValueKind);
		Assert.Equal("ValueError", Assert.Throws<PyError>(() => PyJson.Parse($"{{\"a\": [{digits}1]}}")).Name);
		Assert.Equal("ValueError", Assert.Throws<PyError>(() => PyJson.Parse($"[{{\"a\": -{digits}1}}]")).Name);
		// 1,000 deep, the most JsonDocument is let read; Python's own limit depends on its version (the README's Not covered).
		Assert.Equal(JsonValueKind.Array, PyJson.Parse(new string('[', 1000) + new string(']', 1000)).ValueKind);
	}

	// A gh that exits without reading its input: the write of a body larger than a pipe holds breaks, and the call fails as gh's exit
	// does (a CalledProcessError, as the .py's subprocess.run raised), not with the write's IOException, which the linking step would
	// name OSError.
	[Fact]
	public void A_gh_that_reads_no_input_fails_as_its_exit()
	{
		using var folder = new FailingGh("exit 1\n");
		var gh = new GitHub("octo/demo", () => folder.Gh);
		var body = new string('x', 300_000);
		Assert.Throws<CalledProcessError>(() => gh.CreateReview(7, new Review(Head, body, [])));
		Assert.Throws<CalledProcessError>(() => gh.UpdateReview(7, "99", body));
	}

	// str() of what json.loads gave, under python3, for every kind of value: the posted review's id goes into a URL through it.
	public static TheoryData<string> JsonValues =>
	[
		"\"abc\"", "null", "true", "false", "99", "-99", "99.0", "-0.0", "0.0", "1e16", "1e15", "1.5e-7", "0.0001", "0.00001", "123.456",
		"1234567890123456.5", "123456789012345678.0", "1e22", "-2.5e-300", "5e-324", "1.7976931348623157e308", "1e400", "-1e400",
		"[1, \"a\", null, true, 2.5]", "[]", "{}", "{\"b\": 1, \"a\": [2.5], \"b\": 3}", "[\"it's\", \"a \\\"quote\\\"\", \"\\n\"]",
		"{\"k\": {\"n\": [{}]}}",
	];

	[Theory]
	[MemberData(nameof(JsonValues))]
	public async Task Str_is_python_s_str_of_the_value(string json)
	{
		var start = new System.Diagnostics.ProcessStartInfo("python3")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (var arg in new[] { "-I", "-c", "import json, sys; sys.stdout.write(str(json.loads(sys.argv[1])))", json })
			start.ArgumentList.Add(arg);
		using var run = System.Diagnostics.Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var expected = await run.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		await run.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.True(run.ExitCode == 0, $"python3 exited {run.ExitCode}: {await err}");
		Assert.Equal(expected, PyJson.Str(PyJson.Parse(json)));
	}

	// thread_links() keyed a dict by (path, body): a posted comment with a URL whose path or body is a list or dict can't be a key, so
	// the .py raised TypeError and the step warned; one without a URL was never keyed.
	public static TheoryData<string, string?> UnhashableComments => new()
	{
		{ "{\"html_url\": \"https://x/1\", \"path\": [1], \"body\": \"b\"}", "unhashable type: 'list'" },
		{ "{\"html_url\": \"https://x/1\", \"path\": \"src/app.py\", \"body\": {\"a\": 1}}", "unhashable type: 'dict'" },
		{ "{\"html_url\": \"https://x/1\", \"path\": {}, \"body\": []}", "unhashable type: 'dict'" },
		{ "{\"html_url\": \"http://x/1\", \"path\": [1], \"body\": \"b\"}", null },
		{ "{\"path\": [1], \"body\": \"b\"}", null },
		{ "{\"html_url\": \"https://x/1\", \"path\": 5, \"body\": null}", null },
	};

	[Theory]
	[MemberData(nameof(UnhashableComments))]
	public void A_posted_comment_that_cant_be_a_key_fails_as_thread_links_did(string comment, string? message)
	{
		List<Comment> comments = [new("src/app.py", 2, "b")];
		if (message is null)
		{
			Assert.Single(PostClaudeReview.ThreadLinks(comments, [Json(comment)]));
			return;
		}
		var error = Assert.Throws<PyError>(() => PostClaudeReview.ThreadLinks(comments, [Json(comment)]));
		Assert.Equal(("TypeError", message), (error.Name, error.Message));
	}

	// An answer that isn't JSON fails with where it failed, as Python's JSONDecodeError gives it, and no character of the answer.
	[Fact]
	public void A_not_json_answer_names_where_and_quotes_none_of_it()
	{
		var error = Assert.Throws<MalformedFindings>(() => PostClaudeReview.ParseFindings("{\"summary\": \"ok\"}\nZqx"));
		Assert.Equal("not JSON (invalid JSON: line 2 byte 1)", error.Message);
		Assert.Equal("JSONDecodeError", Assert.Throws<PyError>(() => PyJson.Parse("Zqx")).Name);
		Assert.DoesNotContain("Z", Assert.Throws<PyError>(() => PyJson.Parse("Zqx")).Message);
	}

	// Python writes a negative int with '-' whatever the locale; .NET's BigInteger takes the current culture's sign, so the port gives
	// every number it writes the invariant culture's.
	[Fact]
	public void A_negative_number_is_written_with_python_s_minus_in_any_culture()
	{
		var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
		culture.NumberFormat.NegativeSign = "\u2212";
		var before = System.Globalization.CultureInfo.CurrentCulture;
		System.Globalization.CultureInfo.CurrentCulture = culture;
		try
		{
			var result = Run(FindingsJson(("src/app.py", -3, "On."), ("docs/b.md", -4, "Off.")), redacted: "-2");
			var body = result.Gh.Posted[0].Review.GetProperty("body").GetString()!;
			var thread = result.Gh.Posted[0].Review.GetProperty("comments")[0].GetProperty("body").GetString()!;
			Assert.Contains("Claude cited line -3,", thread);
			Assert.Contains("`docs/b.md:-4`", body);
			Assert.Contains("-2 credential-shaped string(s) were redacted", body);
			Assert.Contains("::warning::Redacted -2 credential-shaped", result.Out);
			Assert.DoesNotContain("\u2212", body + thread + result.Out);
		}
		finally
		{
			System.Globalization.CultureInfo.CurrentCulture = before;
		}
	}
}
