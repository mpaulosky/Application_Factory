using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// What the port must hold beyond the moved tests (ADR 0017's Update for mpaulosky/Application_Factory#120): not moved tests, so
// SettingsTests.cs keeps the moved pytest suite's, and its count can be checked against that suite's in git history. settings.py stays
// beside the C# until rollout.py is ported, so the Python readings the output depends on are checked against it, run by python3.
public partial class SettingsTests
{
	// What python3 prints for script, with input on its stdin: settings.py's folder is first on sys.path, so `import settings` reads it.
	static string Python(string script, string input)
	{
		var start = new ProcessStartInfo("python3")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			StandardInputEncoding = new UTF8Encoding(false),
		};
		foreach (var arg in new[] { "-I", "-c", $"import sys; sys.path.insert(0, sys.argv[1])\n{script}", Github })
			start.ArgumentList.Add(arg);
		start.Environment["PYTHONUTF8"] = "1";
		start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
		using var run = Process.Start(start)!;
		run.StandardInput.Write(input);
		run.StandardInput.Close();
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = run.StandardOutput.ReadToEnd();
		run.WaitForExit();
		Assert.True(run.ExitCode == 0, $"python3 failed: {err.Result}");
		return output;
	}

	// An Api that fails the test on any call: a run that must stop before GitHub.
	static JsonNode? NoCall(string method, string path, JsonNode? body = null) => throw new InvalidOperationException($"called {method} {path}");

	[Theory]
	[InlineData("app")]
	[InlineData("o/../x")]
	[InlineData("../app")]
	[InlineData("o/app\n")]
	[InlineData("o/a pp")]
	[InlineData("@o/app")]
	public void A_repo_argument_that_isnt_owner_repo_is_a_usage_error_before_any_call(string repo)
	{
		using var err = new StringWriter();
		Assert.Equal(2, Settings.Run([repo], new StringWriter(), err, NoCall, Github));
		Assert.Contains($"not owner/repo: {repo}", err.ToString(), StringComparison.Ordinal);
		Assert.Equal(2, Settings.Run(["--suspend", repo], new StringWriter(), new StringWriter(), NoCall, Github));
	}

	[Fact]
	public void A_registry_line_that_isnt_owner_repo_exits_1_before_any_call()
	{
		var folder = CopyGithub();
		File.WriteAllText(Path.Combine(folder, "repos.txt"), "o/app\no/..\n");
		using var err = new StringWriter();
		Assert.Equal(1, Settings.Run(["--all"], new StringWriter(), err, NoCall, folder));
		Assert.Equal("repos.txt: not owner/repo: o/..\n", err.ToString());
	}

	[Fact]
	public void Gh_api_sends_the_method_path_and_accept_header_and_a_body_on_stdin()
	{
		List<(List<string> Argv, string? Input)> calls = [];
		GhRun Answer(IReadOnlyList<string> argv, string? input)
		{
			calls.Add(([.. argv], input));
			return new(0, argv[2] == "GET" ? """{"a": 1}""" : " \n", "");
		}
		var api = Settings.GhApi(Answer);

		Assert.True(JsonNode.DeepEquals(P("""{"a": 1}"""), api("GET", "repos/o/app")));
		Assert.Null(api("PATCH", "repos/o/app", P("""{"allow_auto_merge": true, "name": "é"}""")));

		Assert.Equal(["api", "-X", "GET", "repos/o/app", "-H", "Accept: application/vnd.github+json"], calls[0].Argv);
		Assert.Null(calls[0].Input);
		Assert.Equal(["api", "-X", "PATCH", "repos/o/app", "-H", "Accept: application/vnd.github+json", "--input", "-"], calls[1].Argv);
		Assert.True(JsonNode.DeepEquals(P("""{"allow_auto_merge": true, "name": "é"}"""), JsonNode.Parse(calls[1].Input!)));
	}

	[Theory]
	[InlineData("gh: Not Found (HTTP 404)\n", "", 404, "HTTP 404: gh: Not Found (HTTP 404)")]
	[InlineData("", " out only \n", 0, "HTTP 0: out only")]
	[InlineData("\x1f boom \x1c", "", 0, "HTTP 0: boom")]
	public void A_failed_gh_api_call_is_an_api_error_with_the_status_its_stderr_names(string stderr, string stdout, int status, string message)
	{
		var api = Settings.GhApi((_, _) => new(1, stdout, stderr));
		var error = Assert.Throws<ApiError>(() => api("GET", "repos/o/app"));
		Assert.Equal(status, error.Status);
		Assert.Equal(message, error.Message);
	}

	// The JSON values settings.py printed by repr(), str() and json.dumps(), compact and with indent=2, as python3 prints them.
	static readonly string[] Values = ["null", "true", "false", "0", "-0", "15368", "123456789012345678901234567890", "1.0", "1.5", "-2.5", "1e16", "1e15",
			"123456789.125", "0.0001", "0.00001", "1e-7", "1.7976931348623157e308", "5e-324", "-0.0", "\"plain\"", "\"it's\"", "\"say \\\"hi\\\" it's\"",
			"\"tab\\tnl\\ncr\\r\\b\\f\"", "\"\\u0001\\u001f\\u007f\\u0085\\u00a0\\u00ad\\u00e9\"", "\"\\u200b\\u2028\\u2029\\u3000\\ue000\\uffff\\u0378\"",
			"\"\\ud83d\\ude00 astral \\udb40\\udc01\"", "\"back\\\\slash\"", "\"\u2192 arrow\"", "[]", "{}", "[1, \"a\", null, [true, 2.5]]",
			"{\"a\": {\"b\": []}, \"c\": [1.5, {}], \"\\u00e9\": \"x\"}"];

	[Fact]
	public void Repr_str_and_json_dumps_print_what_python_prints()
	{
		var lines = Python("""
				import json
				for text in json.load(sys.stdin):
				    v = json.loads(text)
				    print(json.dumps([repr(v), str(v), json.dumps(v), json.dumps(v, indent=2)]))
				""", JsonSerializer.Serialize(Values)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(Values.Length, lines.Length);
		for (var i = 0; i < Values.Length; i++)
		{
			var want = (JsonArray)JsonNode.Parse(lines[i])!;
			var value = JsonNode.Parse(Values[i]);
			Assert.Equal((string)want[0]!, Py.Repr(value));
			Assert.Equal((string)want[1]!, Py.Str(value));
			Assert.Equal((string)want[2]!, Py.Dumps(value));
			Assert.Equal((string)want[3]!, Py.Dumps(value, indent: 2));
		}
	}

	[Fact]
	public void Sorted_and_equality_read_values_as_python_does()
	{
		string[] lists = ["""["b", "a", "\uffff", "\ud83d\ude00", "\u00e9", "B", ""]""", """[["PR title", 15368], ["Build", 15368], ["Build", 1]]""",
				"""[1, true, 0.5, false, -3]""", """[[2], [1, 5], [1], []]"""];
		var sorted = Python("""
				import json
				for text in json.load(sys.stdin):
				    print(json.dumps(sorted(json.loads(text))))
				""", JsonSerializer.Serialize(lists)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
		for (var i = 0; i < lists.Length; i++)
			Assert.Equal(sorted[i], Py.Dumps(Py.Sorted(((JsonArray)JsonNode.Parse(lists[i])!).Select(J.Copy))));
		// None can't be ordered against a number: Python's TypeError, an exception here.
		Assert.Throws<InvalidOperationException>(() => Py.Sorted([P("""["Build", null]"""), P("""["Build", 15368]""")]));

		string[][] pairs = [["1", "true"], ["1", "1.0"], ["0", "false"], ["\"1\"", "1"], ["""{"a": 1, "b": 2}""", """{"b": 2, "a": 1}"""], ["[1, 2]", "[2, 1]"],
				["null", "false"], ["1.5", "1.5"], ["[]", "{}"], ["\"\"", "null"], ["{\"a\": null}", "{}"]];
		var equal = Python("""
				import json
				print(json.dumps([json.loads(a) == json.loads(b) for a, b in json.load(sys.stdin)]))
				""", JsonSerializer.Serialize(pairs));
		Assert.Equal(equal.Trim(), Py.Dumps(new JsonArray([.. pairs.Select(pair => (JsonNode)Py.Eq(JsonNode.Parse(pair[0]), JsonNode.Parse(pair[1])))])));
	}

	// Workflow texts that settings.py's line readers could read differently from the C#'s: line breaks str.splitlines() splits on, the
	// whitespace Python's \s and strip() count, quoting, comments and templated names.
	static readonly string[] WorkflowTexts = ["\"on\":\r\n  pull_request:\r\n    types: [opened, synchronize]\r\njobs:\r\n  build:\r\n    name: Build\r\n",
			"on:\x1c  pull_request:\x1djobs:\x1e  a:\x85    name: A\u2028  b:\u2029    steps:\n      - uses: x/y@v1\n",
			"on:\n  pull_request:\n    branches: [ 'main' , \"dev\" ]  # both\njobs:\n  a:\n    name:\t'Quoted'\x1f\n  b:\n    name: \"x ${{ y }}\"\n",
			"on:\n  - pull_request\njobs:\n  c:\n    steps:\n      -   uses:   \"docker://a/b@sha256:" + new string('c', 64) + "\"  # pinned\n      - uses: a/b@" + new string('D', 40) + "\n",
			"on:\n  pull_request:\n    types:\n      - opened  # first\n      # a comment\n      - synchronize\n    branches-ignore:\n      - 'feature/**'\njobs:\n  a:\n",
			"\"on\": [push, pull_request]\njobs:\n  a:\n    name: A\nother:\n  b:\n    name: B\njobs:\n  c:\n",
			"on: pull_request # opened\njobs:\n  x-y_z:\n    runs-on: ubuntu\n    name: late\n      - uses: ./local\n      - uses: owner/repo/path@main\n"];

	[Fact]
	public void The_workflow_readers_read_as_settings_py_reads()
	{
		var python = Python("""
				import json, settings
				for text in json.load(sys.stdin):
				    names, unpinned = settings.workflow_facts(text)
				    print(json.dumps([names, unpinned, settings.runs_on_every_pr(text), settings.runs_on_every_pr(text, "dev"),
				                      settings.runs_on_every_pr(text, "feature/x")]))
				""", JsonSerializer.Serialize(WorkflowTexts)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
		for (var i = 0; i < WorkflowTexts.Length; i++)
		{
			var (names, unpinned) = Settings.WorkflowFacts(WorkflowTexts[i]);
			var mine = new JsonArray(new JsonArray([.. names.Select(n => (JsonNode)n)]), new JsonArray([.. unpinned.Select(u => (JsonNode)u)]),
					Settings.RunsOnEveryPr(WorkflowTexts[i]), Settings.RunsOnEveryPr(WorkflowTexts[i], "dev"), Settings.RunsOnEveryPr(WorkflowTexts[i], "feature/x"));
			Assert.Equal(python[i], Py.Dumps(mine));
		}
	}

	[Fact]
	public void Branch_patterns_match_as_settings_py_matches_them()
	{
		string[][] cases = [["\ud83d\ude00?", ""], ["\ud83d\ude00?", "\ud83d\ude00"], ["a\ud83d\ude00+b", "a\ud83d\ude00\ud83d\ude00b"], ["\\\ud83d\ude00", "\ud83d\ude00"],
				["[z-a]ain", "main"], ["a\\", "a\\"], ["a++", "a+"], ["a??", "a?"], ["**/x", "a/b/x"], ["*/x", "a/b/x"], ["(m)", "(m)"], ["m|n", "m"],
				["{1}", "{1}"], ["a.b", "a.b"], ["[m-]ain", "-ain"], ["re lease", "re lease"], ["#x", "#x"], ["\\[m]", "[m]"]];
		var python = Python("""
				import json, settings
				print(json.dumps([settings.branch_matches(p, b) for p, b in json.load(sys.stdin)]))
				""", JsonSerializer.Serialize(cases));
		Assert.Equal(python.Trim(), Py.Dumps(new JsonArray([.. cases.Select(c => (JsonNode)Settings.BranchMatches(c[0], c[1]))])));
	}

	[Fact]
	public void Splitlines_strip_and_b64decode_read_text_as_python_does()
	{
		string[] texts = ["a\r\nb\rc\nd\ve\ff\x1cg\x1dh\x1ei\x85j\u2028k\u2029l\r\n\r\n", "", "\n", "x\n\n", " \t\x1c\x1f\u00a0\u3000 mid \x1f\x85\u2028\n"];
		var python = Python("""
				import json
				for text in json.load(sys.stdin):
				    print(json.dumps([text.splitlines(), text.strip(), text.lstrip()]))
				""", JsonSerializer.Serialize(texts)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
		for (var i = 0; i < texts.Length; i++)
			Assert.Equal(python[i], Py.Dumps(new JsonArray(new JsonArray([.. Py.SplitLines(texts[i]).Select(l => (JsonNode)l)]), Py.Strip(texts[i]), Py.LStrip(texts[i]))));

		// GitHub wraps a blob's base64 at 60 characters; anything outside the alphabet is dropped, as b64decode's default does.
		var encoded = "aGVs\nbG8s IHdv*cmxk\r\n";
		var decoded = Python("""
				import base64
				print(base64.b64decode(sys.stdin.read()).hex())
				""", encoded);
		Assert.Equal(decoded.Trim(), Convert.ToHexStringLower(Py.B64Decode(encoded)));
	}

	[Fact]
	public void The_review_cap_reads_any_unicode_digit_as_python_does()
	{
		// A cap of digits is read as their number (an Arabic-Indic or an astral 3 is 3, and 3 then the astral 3 is 33); one that isn't
		// digits (an emoji) is no match, so the line after it, which says 3, is read. The astral 3 is MATHEMATICAL BOLD DIGIT THREE.
		var astral = "\U0001D7D1";
		foreach (var (cap, detail) in new[] { ("\u0663", ""), (astral, ""), ("3" + astral, $"cap 3{astral};"), ("\U0001F600", "") })
		{
			var fake = new FakeGitHub();
			fake.Workflows[".github/workflows/pr-automerge.yml"] = $"const COPILOT_REVIEW_CAP = {cap};\nconst COPILOT_REVIEW_CAP = 3;\n";
			var finding = Settings.Check(fake.Api, Github, RepoName).First(f => f.Area == "review cap");
			Assert.True(detail == "" ? finding.Status == Settings.OK : finding.Status == Settings.Manual && finding.Detail.StartsWith(detail, StringComparison.Ordinal),
					finding.Detail);
		}
	}
}

// Gh.Process with input: the body reaches gh's stdin whole, however long. PATH is the process's own, so this runs with GhLookupTests,
// alone, after the parallel tests.
[Collection(nameof(GhLookupTests))]
public class GhInputTests
{
	[Fact]
	public void Process_writes_its_input_to_ghs_stdin()
	{
		if (OperatingSystem.IsWindows())
			return; // the scripts run on Linux and macOS
		var root = Directory.CreateTempSubdirectory("gh-input.");
		var path = Environment.GetEnvironmentVariable("PATH");
		try
		{
			File.WriteAllText(Path.Combine(root.FullName, "gh"), "#!/bin/sh\nwc -c\n");
			File.SetUnixFileMode(Path.Combine(root.FullName, "gh"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
			Environment.SetEnvironmentVariable("PATH", $"{root.FullName}:/usr/bin:/bin");

			// Over a pipe's buffer (64 KiB), in UTF-8 with no byte-order mark.
			var body = new string('é', 100_000);
			Assert.Equal("200000", Gh.Process(["api"], body).Stdout.Trim());
			Assert.Equal("0", Gh.Process(["api"], "").Stdout.Trim());
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", path);
			root.Delete(true);
		}
	}

	// A gh that fails before it reads its body (not logged in, say) closes the pipe: its exit code and stderr still come back, as
	// settings.py's communicate() gave them, so GhApi raises ApiError with gh's message rather than a broken pipe escaping.
	[Theory]
	[InlineData(0)]
	[InlineData(10)]
	[InlineData(200_000)]
	public void Process_returns_ghs_failure_when_gh_exits_without_reading_its_input(int length)
	{
		if (OperatingSystem.IsWindows())
			return; // the scripts run on Linux and macOS
		var root = Directory.CreateTempSubdirectory("gh-input.");
		var path = Environment.GetEnvironmentVariable("PATH");
		try
		{
			File.WriteAllText(Path.Combine(root.FullName, "gh"), "#!/bin/sh\nexec 0<&-\necho 'gh: Bad credentials (HTTP 401)' >&2\nexit 4\n");
			File.SetUnixFileMode(Path.Combine(root.FullName, "gh"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
			Environment.SetEnvironmentVariable("PATH", $"{root.FullName}:/usr/bin:/bin");

			for (var attempt = 0; attempt < 5; attempt++)
			{
				var run = Gh.Process(["api"], new string('x', length));
				Assert.Equal(4, run.Code);
				Assert.Equal("gh: Bad credentials (HTTP 401)\n", run.Stderr);
			}
			var error = Assert.Throws<ApiError>(() => Settings.GhApi()("PATCH", "repos/o/app", new JsonObject { ["a"] = new string('x', length) }));
			Assert.Equal(401, error.Status);
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", path);
			root.Delete(true);
		}
	}
}
