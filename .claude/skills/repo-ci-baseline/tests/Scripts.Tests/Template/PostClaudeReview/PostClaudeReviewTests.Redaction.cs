using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseline.PostReview.Tests;

// The redaction tests of test_post_claude_review.py. Where the pytest holds the script to claude-review.yml's answer check, these hold
// the port to both, by running the workflow's credential definitions (from its heredoc, as the pytest's answer_check_script() finds them)
// and post_claude_review.py's under python3 on the same text. Last, the script's copies of Gh.cs's classes, held to Gh.cs.
public sealed partial class PostClaudeReviewTests
{
	static readonly string Scripts = Skill.Path("template", "owned", ".github", "scripts");

	// Built at runtime, so this file holds nothing credential-shaped for a review of it to quote.
	const string Jwt = "eyJ" + "hbGciOiJIUzI1NiJ9" + "." + "eyJ" + "zdWIiOiIxMjM0NTY3ODkwIn0" + "." + "abcdefghijklmnop";

	static readonly string[] Tokens =
	[
		"sk-ant-" + "oat01-" + new string('a', 40),
		"ghp_" + new string('b', 36),
		"github_pat_" + new string('c', 22) + "_" + new string('d', 59),
		Jwt,
		// Today's installation token: digits, then a JWT after an underscore.
		"ghs_" + "12345_" + Jwt,
	];

	// Text run up against a token doesn't hide it.
	static readonly string[] Prefixed =
	[
		"x-access-token:" + Tokens[^1] + "@github.com", "token_" + Tokens[1], "token_" + Tokens[2], "x" + Jwt, "token-" + Jwt,
		"docs/session-" + Jwt, "\\n" + Tokens[1], "risk-" + Tokens[0],
	];

	public static TheoryData<string> TokensAndPrefixed => new(Tokens.Concat(Prefixed));

	// Every string of the posted review: its body, and each comment's path and body.
	static List<string> PostedStrings(FakeGitHub gh) =>
			[gh.Body(), .. gh.Comments().SelectMany(c => new[] { c.GetProperty("path").GetString()!, BodyOf(c) })];

	[Theory]
	[MemberData(nameof(TokensAndPrefixed))]
	public void A_credential_is_redacted_everywhere_and_warns(string token)
	{
		var findings = FindingsJsonWith($"Found {token} in the environment.", ("src/app.py", 2, $"The token is {token}."), ($"docs/{token}.md", 1, "Path."));

		var result = Run(findings);

		foreach (var text in PostedStrings(result.Gh))
		{
			Assert.DoesNotContain(token, text);
			Assert.DoesNotContain(Tokens, t => text.Contains(t, StringComparison.Ordinal));
		}
		Assert.Contains(PostClaudeReview.Redacted, BodyOf(result.Gh.Comments()[0]));
		Assert.Contains("3 credential-shaped string(s) were redacted", result.Gh.Body());
		Assert.Contains("::warning::Redacted 3", result.Out);
	}

	[Fact]
	public void A_credential_in_the_structured_fields_is_redacted_too()
	{
		var token = Tokens[1];
		var findings = new JsonObject
		{
			["overview"] = token,
			["changes"] = new JsonArray(token),
			["files"] = new JsonArray(new JsonObject { ["path"] = $"docs/{token}", ["description"] = token }),
			["advisory"] = new JsonArray(token),
			["not_reviewed"] = token,
			["findings"] = new JsonArray(),
		}.ToJsonString();

		var body = Run(findings).Gh.Body();

		Assert.DoesNotContain(token, body);
		Assert.Equal(6, (body.Length - body.Replace(PostClaudeReview.Redacted, "", StringComparison.Ordinal).Length) / PostClaudeReview.Redacted.Length);
		Assert.Contains("6 credential-shaped string(s) were redacted", body);
	}

	[Fact]
	public void The_review_jobs_redactions_are_counted()
	{
		var result = Run(FindingsJson(("src/app.py", 2, "Was [redacted].")), redacted: "2");

		Assert.Contains("2 credential-shaped string(s) were redacted", result.Gh.Body());
		Assert.Contains("::warning::Redacted 2", result.Out);
	}

	[Theory]
	[InlineData("ghs_installation_token_value_for_the_runner_job")]
	[InlineData("github_pat_example_placeholder_value_goes_here_xx")]
	[InlineData("ghp_short")]
	public void Names_shaped_loosely_like_tokens_arent_redacted(string text) =>
			Assert.Equal(text, BodyOf(Run(FindingsJson(("src/app.py", 2, text))).Gh.Comments()[0]));

	[Fact]
	public void Ordinary_text_isnt_redacted()
	{
		const string text = "The ghp_ prefix, sk-ant- keys and a JWT header like eyJ are named here, not quoted.";

		Assert.Equal(text, BodyOf(Run(FindingsJson(("src/app.py", 2, text))).Gh.Comments()[0]));
	}

	// The workflow's and the .py's definitions under python3, each text read from stdin as a JSON list (a long one would pass the
	// kernel's limit on one argument): what source's fn gives for each, as [text, count], or the constants when fn is "constants".
	const string RedactInPython = """
		import json, re, sys, textwrap
		sys.dont_write_bytecode = True
		scripts, workflow, source, fn = sys.argv[1:5]
		if source == "script":
		    sys.path.insert(0, scripts)
		    import post_claude_review as namespace
		    namespace = vars(namespace)
		else:
		    lines = open(workflow, encoding="utf-8").read().splitlines()
		    step = next(i for i, line in enumerate(lines) if line.strip() == "id: answer")
		    start = next(i for i in range(step, len(lines)) if lines[i].strip() == "python3 -I - <<'PY'") + 1
		    end = next(i for i in range(start, len(lines)) if lines[i].strip() == "PY")
		    script = textwrap.dedent("\n".join(lines[start:end])) + "\n"
		    begin = script.index("CREDENTIAL = re.compile(")
		    namespace = {"re": re}
		    exec(script[begin:script.index("\n\n\ndef answer_in", begin)], namespace)
		if fn == "constants":
		    print(json.dumps([namespace["CREDENTIAL"].pattern, namespace["CREDENTIAL_START"].pattern, namespace["TOKEN_CHAIN"].pattern,
		                      namespace["MAX_TOKEN_RUN"]]))
		else:
		    print(json.dumps([list(namespace[fn](text)) for text in json.load(sys.stdin)]))
		""";

	static JsonElement Python(string source, string fn, IEnumerable<string>? texts = null)
	{
		var start = new ProcessStartInfo("python3")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (var arg in new[] { "-I", "-c", RedactInPython, Scripts, Skill.Path("template", "owned", ".github", "workflows", "claude-review.yml"), source, fn })
			start.ArgumentList.Add(arg);
		using var run = Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = run.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		run.StandardInput.Write(JsonSerializer.Serialize(texts ?? []));
		run.StandardInput.Close();
		run.WaitForExit();
		Assert.True(run.ExitCode == 0, $"python3 exited {run.ExitCode}: {err.Result}");
		return Json(output.Result);
	}

	// What the workflow's (or the .py's) fn gives for each text, as the port's tuple.
	static List<(string Text, int Count)> InPython(string source, string fn, params string[] texts) =>
			Python(source, fn, texts).EnumerateArray().Select(pair => (pair[0].GetString()!, pair[1].GetInt32())).ToList();

	static string AJwt(int first = 12, int second = 12, int third = 12, string prefix = "eyJ") =>
			$"{prefix}{new string('a', first)}.eyJ{new string('b', second)}.{new string('c', third)}";

	static readonly string Glued = "ghp_" + new string('A', 36) + "_" + AJwt();

	public static TheoryData<string> CredentialShaped => new(
			AJwt(),
			"x-" + AJwt(),
			Glued,
			"ghs_123_" + AJwt(),
			AJwt(second: 200), // a payload past MaxTokenRun: the signature goes too
			AJwt(first: 200),
			AJwt(third: 300),
			string.Concat(Enumerable.Repeat("eyJ", 70_000)),
			"(" + AJwt() + ")");

	[Theory]
	[MemberData(nameof(CredentialShaped))]
	public void No_credential_shaped_text_survives_redaction(string text)
	{
		var input = $"see {text} end";
		foreach (var (redacted, count) in new[] { PostClaudeReview.RedactCredentials(input), InPython("workflow", "redact_credentials", input)[0] })
		{
			Assert.True(count >= 1);
			Assert.StartsWith("see ", redacted);
			Assert.EndsWith(" end", redacted);
			Assert.DoesNotMatch(PostClaudeReview.Credential, redacted);
			Assert.DoesNotContain("bbbbbbbb", redacted);
			Assert.DoesNotContain("cccccccc", redacted);
		}
	}

	[Theory]
	[MemberData(nameof(JwtsOnTokens))]
	public void A_jwt_run_straight_onto_a_token_is_redacted_as_the_original_pattern_did(string text)
	{
		// The token's alternative takes the JWT's first segment too, as it always has, so the rest (two segments) is no longer a JWT; the
		// redaction must still act and never match a JWT.
		var (redacted, count) = PostClaudeReview.RedactCredentials(text);

		Assert.True(count >= 1);
		Assert.DoesNotMatch(PostClaudeReview.Credential, redacted);
		Assert.Equal(PostClaudeReview.Credential.Replace(text, "[redacted]"), redacted);
	}

	public static TheoryData<string> JwtsOnTokens => new("ghp_" + new string('A', 36) + AJwt(), "sk-ant-" + new string('a', 30) + "-" + AJwt());

	[Fact]
	public void A_long_jwts_signature_is_replaced_with_its_payload() =>
			Assert.Equal("t=[redacted]", PostClaudeReview.RedactCredentials("t=" + AJwt(second: 400, third: 50)).Text);

	[Fact]
	public void Ordinary_text_with_short_words_is_left_alone()
	{
		var text = "Fix snake_case_names, dotted.names.here and a 64-char hash " + new string('0', 64) + " in src/app.py:12.";

		Assert.Equal((text, 0), PostClaudeReview.RedactCredentials(text));
	}

	// The pytest's seed-299 texts, here from .NET's own Random with that seed: the port gives what the workflow and the .py give.
	[Fact]
	public void The_script_and_the_workflow_redact_alike()
	{
		var random = new Random(299);
		string[] pieces = [AJwt(), AJwt(second: 200), Glued, "ghs_9_" + AJwt(), "sk-ant-" + new string('q', 25), "x-", "eyJ", ".", " ", "/", new string('a', 130), "ok", LongName];
		var texts = Enumerable.Range(0, 300).Select(_ => string.Concat(Enumerable.Range(0, random.Next(1, 7)).Select(_ => pieces[random.Next(pieces.Length)]))).ToArray();

		var port = texts.Select(PostClaudeReview.RedactCredentials).ToList();

		Assert.Equal(InPython("workflow", "redact_credentials", texts), port);
		Assert.Equal(InPython("script", "redact_credentials", texts), port);
	}

	[Fact]
	public void The_redaction_leaves_nothing_the_original_pattern_matches()
	{
		var random = new Random(299);
		string[] pieces = [AJwt(), AJwt(first: 8, second: 8, third: 8), AJwt(second: 200), Glued, "ghp_" + new string('Z', 40), "ghs_9_" + AJwt(),
			"github_pat_" + new string('a', 22) + "_" + new string('b', 59), "sk-ant-" + new string('q', 25), "x-", "_", "-", ".", " ", "eyJ", "/"];
		var matched = 0;
		for (var i = 0; i < 3000; i++)
		{
			var text = string.Concat(Enumerable.Range(0, random.Next(1, 8)).Select(_ => pieces[random.Next(pieces.Length)]));
			matched += PostClaudeReview.Credential.IsMatch(text) ? 1 : 0;

			Assert.False(PostClaudeReview.Credential.IsMatch(PostClaudeReview.RedactCredentials(text).Text), text);
		}
		Assert.True(matched > 1500, $"only {matched} texts held a credential");
	}

	static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

	public static TheoryData<string> LongRedactionInput => new(
			Repeat("eyJ", 70_000),
			Repeat("ghp_1_eyJ", 25_000),
			Repeat("eyJ", 40_000) + "." + Repeat("eyJ", 40_000),
			Repeat("x-", 100_000) + "eyJ",
			Repeat(Repeat("eyJ", 42) + "a" + ".", 1_500), // many runs just under MaxTokenRun, each full of eyJ
			Repeat(Repeat("ghp_1_eyJ", 14) + ".", 1_500),
			Repeat("a.", 100_000));

	[Theory]
	[MemberData(nameof(LongRedactionInput))]
	public void Redacting_takes_linear_time_on_long_input(string text)
	{
		var clock = Stopwatch.StartNew();
		PostClaudeReview.RedactCredentials(text);

		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
	}

	static readonly string LongName = "tests/Get_Articles_When_" + new string('A', 130) + "_Returns_401.cs";

	// Long tokens that start no credential: a generated test's name, an identifier, a hex digest, a base64 blob.
	public static TheoryData<string> LongPlainTokens => new(
			LongName,
			"Get" + Repeat("Articles", 30) + "ForTheCurrentUser",
			Repeat("9f86d081884c7d659a2feaa0c55ad015", 8),
			Repeat("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo", 5) + "==",
			"Company." + Repeat("Segment", 30) + ".Service");

	[Theory]
	[MemberData(nameof(LongPlainTokens))]
	public void A_long_token_that_starts_no_credential_is_left_alone_with_no_warning(string text)
	{
		var sentence = $"The finding is in {text}, which is not a secret.";

		Assert.Equal((sentence, 0), PostClaudeReview.RedactCredentials(sentence));
		Assert.Equal((sentence, 0), InPython("workflow", "redact_credentials", sentence)[0]);
	}

	[Fact]
	public void A_long_file_name_quoted_in_a_finding_body_and_the_summary_is_left_alone()
	{
		var (report, findings, count) = PostClaudeReview.Redact(new Report(Summary: $"| {LongName} | added |"), [new Finding("a.py", 3, $"See `{LongName}`.", null)]);

		Assert.Contains(LongName, report.Summary);
		Assert.Contains(LongName, findings[0].Body);
		Assert.Equal(0, count);
	}

	// Each one starts a credential and holds a run over MaxTokenRun. Credential alone either leaves part of it behind (the ghp, ghu and
	// sk-ant cases) or matches none of it (the others), so only the long-run rule replaces it whole, and each start it looks for is pinned.
	public static TheoryData<string> LongCredentialStarts => new(
			"ghp_" + new string('A', 40) + "_" + new string('b', 200),
			"ghu_" + new string('A', 40) + "_" + new string('b', 200),
			"sk-ant-" + new string('a', 30) + "." + new string('b', 200),
			"github_pat_" + new string('a', 300),
			"prefix" + new string('q', 140) + "eyJ" + new string('q', 140), // eyJ in the middle
			"ghp_1." + new string('b', 200), // a start in the first segment
			new string('a', 200) + ".eyJ" + new string('b', 20), // a start in a later segment
			"sk-ant-" + new string('a', 5) + "." + new string('b', 200)); // a start before the long segment

	[Theory]
	[MemberData(nameof(LongCredentialStarts))]
	public void A_long_token_that_starts_a_credential_is_still_replaced_whole(string text)
	{
		var input = $"see {text} end";

		Assert.Equal(("see [redacted] end", 1), PostClaudeReview.RedactCredentials(input));
		Assert.Equal(("see [redacted] end", 1), InPython("workflow", "redact_credentials", input)[0]);
	}

	[Fact]
	public void A_long_run_in_a_chain_with_no_start_is_left_alone_wherever_it_sits()
	{
		var text = "see " + new string('x', 200) + ".Service and Company." + new string('y', 200) + " end";

		Assert.Equal((text, 0), PostClaudeReview.RedactCredentials(text));
		Assert.Equal((text, 0), InPython("workflow", "redact_credentials", text)[0]);
	}

	// The port's patterns are the workflow's and the .py's, character for character: .NET reads each as Python's re does (every construct
	// in them means the same in both), and the parity suite and the tests above check that it acts the same.
	[Theory]
	[InlineData("workflow")]
	[InlineData("script")]
	public void The_workflow_carries_the_scripts_long_run_rule(string source)
	{
		var constants = Python(source, "constants");

		Assert.Equal(PostClaudeReview.CredentialPattern, constants[0].GetString());
		Assert.Equal(PostClaudeReview.CredentialStartPattern, constants[1].GetString());
		Assert.Equal(PostClaudeReview.TokenChainPattern, constants[2].GetString());
		Assert.Equal(PostClaudeReview.MaxTokenRun, constants[3].GetInt32());
		Assert.Equal(PostClaudeReview.CredentialPattern, PostClaudeReview.Credential.ToString());
		Assert.Equal(PostClaudeReview.CredentialStartPattern, PostClaudeReview.CredentialStart.ToString());
		Assert.Equal(PostClaudeReview.TokenChainPattern, PostClaudeReview.TokenChain.ToString());
	}

	public static TheoryData<string> TextCredentials => new(new[] { "sk-ant-" + new string('a', 20) }
			.Concat("pousr".Select(letter => $"gh{letter}_" + new string('A', 36)))
			.Concat(["ghs_123_" + AJwt(), "github_pat_" + new string('a', 22) + "_" + new string('b', 59), AJwt()]));

	[Theory]
	[MemberData(nameof(TextCredentials))]
	public void Every_text_credential_matches_starts_with_a_credential_start(string sample)
	{
		// Credential can only match inside a token that CredentialStart finds, or the long-run rule would skip a run Credential scans slowly.
		var match = PostClaudeReview.Credential.Match(sample);

		Assert.True(match.Success, sample);
		Assert.Matches(PostClaudeReview.CredentialStart, match.Value);
	}

	[Fact]
	public void A_long_file_name_in_a_finding_path_is_left_alone()
	{
		var (_, findings, count) = PostClaudeReview.Redact(new Report(Summary: "s"), [new Finding(LongName, 3, "b", null)]);

		Assert.Equal(LongName, findings[0].Path);
		Assert.Equal(0, count);
	}

	[Fact]
	public void A_long_name_that_holds_a_credential_start_without_being_one_is_left_alone_in_a_finding_path()
	{
		var path = "tests/Get_eyJ_" + new string('A', 130) + "_Returns_401.cs";
		var (_, findings, count) = PostClaudeReview.Redact(new Report(Summary: "s"), [new Finding(path, 3, "b", null)]);

		Assert.Equal(path, findings[0].Path);
		Assert.Equal(0, count);
		// The long-run rule, which a path does not get, would have replaced it whole.
		Assert.Equal(("tests/[redacted]", 1), PostClaudeReview.RedactCredentials(path));
	}

	[Fact]
	public void A_credential_in_a_finding_path_is_still_redacted()
	{
		var (_, findings, count) = PostClaudeReview.Redact(new Report(Summary: "s"), [new Finding("src/" + AJwt() + ".cs", 3, "b", null)]);

		Assert.DoesNotContain("bbbbbbbb", findings[0].Path);
		Assert.True(count >= 1);
	}

	[Fact]
	public void A_path_past_the_limit_gets_the_full_rule()
	{
		var clock = Stopwatch.StartNew();
		var (_, findings, _) = PostClaudeReview.Redact(new Report(Summary: "s"), [new Finding("x/" + Repeat("eyJ", 70_000), 3, "b", null)]);

		Assert.Equal("x/[redacted]", findings[0].Path);
		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
	}

	[Fact]
	public void The_workflow_redacts_paths_like_the_script()
	{
		string[] paths = [LongName, "src/" + AJwt(), "x/" + Repeat("eyJ", 1000)];

		Assert.Equal(paths.Select(PostClaudeReview.RedactPath), InPython("workflow", "redact_path", paths));
		Assert.Equal((LongName, 0), PostClaudeReview.RedactPath(LongName));
	}

	// The script is one file (the checker layout), so it carries its own copies of Gh.cs's Arguments, CalledProcessError, Ran, Script,
	// SurrogateEscape and Py, one tab further in. They must stay Gh.cs's: a fix there is a fix here, made by copying the block again.
	[Fact]
	public void The_scripts_copies_of_gh_cs_are_gh_cs_line_for_line()
	{
		var gh = File.ReadAllLines(Path.Combine(Scripts, "Gh.cs"));
		var from = Array.FindIndex(gh, line => line.StartsWith("// argparse's part the scripts use", StringComparison.Ordinal));
		var to = Array.FindIndex(gh, line => line.StartsWith("// pathlib's Path.resolve()", StringComparison.Ordinal));
		Assert.True(from > 0 && to > from, "Gh.cs no longer starts its Arguments or its PyPath with the comment this test looks for");
		var expected = gh[from..to].Reverse().SkipWhile(line => line.Length == 0).Reverse().ToList();

		var script = File.ReadAllLines(Path.Combine(Scripts, "post_claude_review.cs"));
		var start = Array.FindIndex(script, line => line.StartsWith("\t// Gh.cs's copies start here:", StringComparison.Ordinal));
		var end = Array.FindIndex(script, line => line == "\t// Gh.cs's copies end here.");
		Assert.True(start > 0 && end > start, "post_claude_review.cs no longer marks its copies of Gh.cs's classes");
		var copies = script[(start + 1)..end].SkipWhile(line => line.StartsWith("\t// ", StringComparison.Ordinal) && !line.StartsWith("\t// argparse", StringComparison.Ordinal))
				.Select(line => line.Length == 0 ? line : line.StartsWith('\t') ? line[1..] : "(not indented) " + line).ToList();

		Assert.Equal(expected, copies);
	}
}
