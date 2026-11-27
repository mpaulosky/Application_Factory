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
}
