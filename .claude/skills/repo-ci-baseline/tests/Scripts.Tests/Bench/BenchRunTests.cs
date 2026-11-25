using System.Text;
using System.Text.Json.Nodes;

using YamlDotNet.Core;

// The run mode's parts that no parity suite holds (ADR 0017's Update for mpaulosky/Application_Factory#161: the suite covers --rescore only),
// and the limits the port adds: what a finished claude call becomes, the --max-turns cap, the processes it runs, the prompt file's line endings,
// Claude's stream, and the YAML limits a case's head is read under.
public partial class BenchTests
{
	static Bench.Ran Claude(int code, string stdout, string stderr = "") => new(code, Encoding.UTF8.GetBytes(stdout), stderr);

	const string Review = """{"type": "result", "num_turns": 6, "total_cost_usd": 0.5, "structured_output": {"findings": [{"path": "a", "line": 1, "body": "b"}]}}""";

	// -- What a finished claude call becomes --

	[Fact]
	public void A_structured_review_is_the_outcome_with_its_cost_turns_and_reads()
	{
		var outcome = Bench.Outcome(Claude(0, Review + "\n"), ["--max-turns", "6"], 12);

		Assert.Equal("""{"review":{"findings":[{"path":"a","line":1,"body":"b"}]},"seconds":12,"cost_usd":0.5,"turns":6,"reads":[]}""", outcome.ToJsonString());
	}

	[Fact]
	public void A_review_in_the_result_text_is_read_when_there_is_no_structured_output()
	{
		var outcome = Bench.Outcome(Claude(0, """{"type": "result", "result": "{\"findings\": []}"}""" + "\n"), [], 1);

		Assert.Equal("""{"findings":[]}""", J.Req(outcome, "review")!.ToJsonString());
	}

	[Theory]
	[InlineData(1, "", "it failed", "it failed")]
	[InlineData(1, "only stdout", "", "only stdout")]
	[InlineData(0, "{\"type\": \"assistant\"}\n", "", "no structured review (no result message: ")]
	[InlineData(0, "{\"type\": \"result\", \"result\": \"not json\"}\n", "", "no structured review (")]
	[InlineData(0, "{\"type\": \"result\", \"structured_output\": [1]}\n", "", "review isn't an object with a list of findings: [1]")]
	[InlineData(0, "{\"type\": \"result\", \"structured_output\": {\"findings\": [1]}}\n", "", "review isn't an object with a list of findings: ")]
	public void A_call_that_fails_or_gives_no_review_is_the_cases_error(int code, string stdout, string stderr, string error)
	{
		var outcome = Bench.Outcome(Claude(code, stdout, stderr), [], 3);

		Assert.StartsWith(error, J.S(J.Req(outcome, "error")), StringComparison.Ordinal);
		Assert.Equal(3, J.Int(J.Req(outcome, "seconds")));
		Assert.False(J.Has(outcome, "review"));
	}

	[Theory]
	[InlineData("4", false)]
	[InlineData("5", false)]
	[InlineData("6", true)]
	[InlineData("\"6\"", false)]
	[InlineData("6.0", false)]
	[InlineData("null", false)]
	public void A_turn_count_over_max_turns_is_one_the_action_rejects(string turns, bool rejects)
	{
		var outcome = Bench.Outcome(Claude(0, Review.Replace("\"num_turns\": 6", $"\"num_turns\": {turns}", StringComparison.Ordinal) + "\n"),
				["--model", "m", "--max-turns", "5"], 0);

		Assert.Equal(rejects, J.Has(outcome, "action_rejects"));
		Assert.Equal(rejects, Bench.ActionRejects(JsonNode.Parse(turns), ["--max-turns", "5"]));
	}

	[Fact]
	public void With_no_max_turns_nothing_is_rejected()
	{
		Assert.False(Bench.ActionRejects(JsonValue.Create(1_000_000), ["--model", "m"]));
	}

	// -- Claude's stream --

	[Fact]
	public void A_stream_line_holding_a_raw_line_separator_is_one_message()
	{
		var (result, _) = Bench.Transcript("{\"type\": \"result\", \"structured_output\": {\"findings\": [{\"body\": \"a\u2028b\u2029c\u0085d\"}]}}\r\n");

		Assert.Equal("a\u2028b\u2029c\u0085d", J.S(J.Req(J.Req(J.Req(result, "structured_output"), "findings")![0], "body")));
	}

	// -- The prompt file --

	[Theory]
	[InlineData("a\r\nb\r\n")]
	[InlineData("a\rb\r")]
	[InlineData("a\nb\n")]
	public void A_prompt_files_line_endings_are_read_as_newlines(string text)
	{
		var file = Path.Combine(temp, "prompt.txt");
		File.WriteAllText(file, text);

		Assert.Equal("a\nb\n", Bench.ReadPrompt(file));
	}

	[Fact]
	public void A_crlf_prompt_renders_as_its_lf_copy_does()
	{
		var (prompt, _) = Bench.ReviewStep(Workflow);
		var file = Path.Combine(temp, "prompt.txt");
		File.WriteAllText(file, prompt.Replace("\n", "\r\n", StringComparison.Ordinal));

		string Rendered(string text) => Bench.Render(text, "1", temp, "- piece.diff", "-");

		// The REVIEW_BATCH line is found and dropped in the CRLF copy too; read as it is, its \r keeps the expression in, and Render refuses it.
		Assert.Equal(Rendered(prompt), Rendered(Bench.ReadPrompt(file)));
		Assert.Throws<BenchExit>(() => Rendered(File.ReadAllText(file)));
	}

	// -- The processes it runs --

	[Fact]
	public void A_call_past_its_timeout_is_killed()
	{
		var started = System.Diagnostics.Stopwatch.StartNew();

		Assert.Throws<TimeoutException>(() => Bench.Exec(["sleep", "30"], timeout: 1));
		Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"took {started.Elapsed}");
	}

	[Fact]
	public void A_program_that_isnt_on_path_is_the_cases_error()
	{
		var error = Assert.Throws<CaseError>(() => Bench.Exec(["no-such-program-for-the-bench"]));

		Assert.Equal("no-such-program-for-the-bench isn't on PATH (only its absolute folders are searched)", error.Message);
	}

	[Fact]
	public void A_call_that_exits_other_than_0_names_its_command_and_status()
	{
		var error = Assert.Throws<CaseError>(() => Bench.Check(["sh", "-c", "echo oops >&2; exit 3"]));

		Assert.Equal("Command ['sh', '-c', 'echo oops >&2; exit 3'] returned non-zero exit status 3. oops", error.Message);
	}

	// -- The YAML limits --

	// An alias bomb: each level a list of nine aliases of the level before, so levels levels expand to 9^levels scalars.
	static string AliasBomb(int levels)
	{
		var text = new StringBuilder("l0: &l0 [x, x, x, x, x, x, x, x, x]\n");
		for (var i = 1; i < levels; i++)
			text.Append($"l{i}: &l{i} [{string.Join(", ", Enumerable.Repeat($"*l{i - 1}", 9))}]\n");
		return text.ToString();
	}

	[Fact]
	public void An_alias_bomb_is_refused_and_a_head_holding_one_pins_no_action()
	{
		var bomb = AliasBomb(9) + "jobs: {review: {steps: [{uses: owner/repo@v1}]}}\n";

		Assert.Throws<YamlException>(() => Yaml.Load(bomb));
		Assert.Empty(Bench.PinnedActions(bomb));
	}

	[Theory]
	[InlineData(3, true)]
	[InlineData(4, false)]
	[InlineData(5, false)]
	public void A_text_past_its_node_budget_is_refused(int nodes, bool refused)
	{
		// The mapping, a's scalar, b's list and the scalar its alias names: 4 nodes once the alias is read in, though 3 are written.
		const string text = "a: &a x\nb: [*a]\n";

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text, Yaml.MaxDepth, nodes, Yaml.MaxScalarText));
		else
			Assert.Equal("""{"a":"x","b":["x"]}""", Yaml.Load(text, Yaml.MaxDepth, nodes, Yaml.MaxScalarText)!.ToJsonString());
	}

	[Theory]
	[InlineData(6, true)]
	[InlineData(7, false)]
	[InlineData(8, false)]
	public void An_alias_to_a_list_is_charged_every_node_the_list_holds(int nodes, bool refused)
	{
		// The mapping, a's list and its two scalars, and b's copy of all three: 7 nodes.
		const string text = "a: &a [x, x]\nb: *a\n";

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text, Yaml.MaxDepth, nodes, Yaml.MaxScalarText));
		else
			Assert.Equal("""{"a":["x","x"],"b":["x","x"]}""", Yaml.Load(text, Yaml.MaxDepth, nodes, Yaml.MaxScalarText)!.ToJsonString());
	}

	[Theory]
	[InlineData(1, true)]
	[InlineData(2, false)]
	[InlineData(3, false)]
	public void A_text_nested_past_its_depth_is_refused(int depth, bool refused)
	{
		// The mapping is level 1 and its list level 2; a scalar opens no level.
		const string text = "a: [x]\n";

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text, depth, Yaml.MaxNodes, Yaml.MaxScalarText));
		else
			Assert.Equal("""{"a":["x"]}""", Yaml.Load(text, depth, Yaml.MaxNodes, Yaml.MaxScalarText)!.ToJsonString());
	}

	[Theory]
	[InlineData(-1, false)]
	[InlineData(0, false)]
	[InlineData(1, true)]
	public void The_real_depth_limit_holds_at_its_edge(int past, bool refused)
	{
		var levels = Yaml.MaxDepth + past;
		var text = new string('[', levels) + "x" + new string(']', levels);

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text));
		else
			Assert.NotNull(Yaml.Load(text));
	}

	[Theory]
	[InlineData(-1, false)]
	[InlineData(0, false)]
	[InlineData(1, true)]
	public void The_real_node_limit_holds_at_its_edge(int past, bool refused)
	{
		// The list and its scalars: Yaml.MaxNodes + past nodes.
		var text = "[" + string.Join(",", Enumerable.Repeat("x", Yaml.MaxNodes + past - 1)) + "]";

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text));
		else
			Assert.Equal(Yaml.MaxNodes + past - 1, Yaml.Load(text)!.AsArray().Count);
	}

	[Theory]
	[InlineData(-1, false)]
	[InlineData(0, false)]
	[InlineData(1, true)]
	public void The_real_scalar_text_limit_holds_at_its_edge_counting_each_alias(int past, bool refused)
	{
		// One scalar of 4,096 digits and its aliases: (1 + aliases) * 4,096 characters of scalars once they are read in.
		const int length = 4096;
		var aliases = (int)(Yaml.MaxScalarText / length) - 1 + past;
		var text = $"a: &a {new string('9', length)}\nb: [{string.Join(",", Enumerable.Repeat("*a", aliases))}]\n";

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text));
		else
			Assert.Equal(aliases, J.Req(Yaml.Load(text), "b")!.AsArray().Count);
	}

	[Theory]
	[InlineData(-1, false)]
	[InlineData(0, false)]
	[InlineData(1, true)]
	public void The_real_text_limit_holds_at_its_edge(int past, bool refused)
	{
		var text = "a: " + new string('x', Yaml.MaxText + past - 3);

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text));
		else
			Assert.Equal(Yaml.MaxText + past - 3, J.S(J.Req(Yaml.Load(text), "a"))!.Length);
	}

	[Fact]
	public void Nesting_deep_enough_to_overflow_a_recursive_reader_is_refused_and_pins_no_action()
	{
		var text = new string('[', 200_000) + new string(']', 200_000);

		Assert.Throws<YamlException>(() => Yaml.Load(text));
		Assert.Empty(Bench.PinnedActions(text));
	}

	[Fact]
	public void An_anchor_aliased_inside_itself_is_refused()
	{
		Assert.Throws<YamlException>(() => Yaml.Load("a: &a [*a]\n"));
	}

	[Fact]
	public void An_aliased_node_is_a_copy_not_the_same_node()
	{
		var tree = Yaml.Load("a: &a {x: [1]}\nb: *a\nc: *a\n")!;

		Assert.Equal("""{"a":{"x":[1]},"b":{"x":[1]},"c":{"x":[1]}}""", tree.ToJsonString());
		Assert.NotSame(J.Req(tree, "a"), J.Req(tree, "b"));
	}

	[Fact]
	public void The_template_workflow_is_well_inside_the_yaml_limits()
	{
		var text = File.ReadAllText(Workflow);

		Assert.True(text.Length < Yaml.MaxText / 4, $"{text.Length} characters");
		Assert.NotNull(Yaml.Load(text, Yaml.MaxDepth / 4, Yaml.MaxNodes / 10, Yaml.MaxScalarText / 4));
	}

	[Theory]
	[InlineData(10, true)]
	[InlineData(11, false)]
	[InlineData(12, false)]
	public void An_alias_to_a_list_is_charged_every_character_the_list_holds(long scalarText, bool refused)
	{
		// a's list holds 5 characters (abc, de), and b's copy 5 more: 10, with the 1 of c's own scalar 11.
		const string text = "a: &a [abc, de]\nb: *a\nc: x\n";

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text, Yaml.MaxDepth, Yaml.MaxNodes, scalarText));
		else
			Assert.Equal("""{"a":["abc","de"],"b":["abc","de"],"c":"x"}""", Yaml.Load(text, Yaml.MaxDepth, Yaml.MaxNodes, scalarText)!.ToJsonString());
	}

	[Theory]
	[InlineData(10, true)]
	[InlineData(11, false)]
	[InlineData(12, false)]
	public void An_alias_to_an_anchored_key_is_charged_its_text_as_a_value(long scalarText, bool refused)
	{
		// The key's text is charged only where it is aliased as a value: 1 (a's value), 5 (b's copy of "keyed") and 5 (c's): 11.
		const string text = "&k keyed: 1\nb: *k\nc: *k\n";

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text, Yaml.MaxDepth, Yaml.MaxNodes, scalarText));
		else
			Assert.Equal("""{"keyed":1,"b":"keyed","c":"keyed"}""", Yaml.Load(text, Yaml.MaxDepth, Yaml.MaxNodes, scalarText)!.ToJsonString());
	}

	[Theory]
	[InlineData(4, true)]
	[InlineData(5, false)]
	[InlineData(6, false)]
	public void An_alias_to_an_anchored_key_is_charged_a_node_as_a_value(int nodes, bool refused)
	{
		// The mapping, a's value, and b's and c's copies of the key: 4 nodes, and d's 5.
		const string text = "&k a: 1\nb: *k\nc: *k\nd: x\n";

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text, Yaml.MaxDepth, nodes, Yaml.MaxScalarText));
		else
			Assert.NotNull(Yaml.Load(text, Yaml.MaxDepth, nodes, Yaml.MaxScalarText));
	}

	[Theory]
	[InlineData(3, true)]
	[InlineData(4, false)]
	[InlineData(5, false)]
	public void An_alias_nests_as_deep_as_what_it_names(int depth, bool refused)
	{
		// a's [[x]] is 2 levels under the mapping's 1; copied into b's list, at level 2, it reaches level 4.
		const string text = "a: &a [[x]]\nb: [*a]\n";

		if (refused)
			Assert.Throws<YamlException>(() => Yaml.Load(text, depth, Yaml.MaxNodes, Yaml.MaxScalarText));
		else
			Assert.Equal("""{"a":[["x"]],"b":[[["x"]]]}""", Yaml.Load(text, depth, Yaml.MaxNodes, Yaml.MaxScalarText)!.ToJsonString());
	}

	[Fact]
	public void A_chain_of_aliases_past_the_real_depth_is_refused_and_pins_no_action()
	{
		// Each link nests the last 40 levels deeper, so written 41 deep at most, the third link's copy is past MaxDepth.
		var text = new StringBuilder("jobs:\n  review:\n    steps:\n      - uses: &l0 x\n");
		for (var link = 1; link <= 3; link++)
			text.Append($"      - uses: &l{link} {new string('[', 40)}*l{link - 1}{new string(']', 40)}\n");

		Assert.Throws<YamlException>(() => Yaml.Load(text.ToString()));
		Assert.Empty(Bench.PinnedActions(text.ToString()));
	}

	[Fact]
	public void An_alias_to_a_scalar_nests_no_deeper_and_an_alias_to_null_is_null()
	{
		Assert.Equal("""{"a":null,"b":[null]}""", Yaml.Load("a: &a ~\nb: [*a]\n", 2, Yaml.MaxNodes, Yaml.MaxScalarText)!.ToJsonString());
	}

	[Theory]
	[InlineData("a: &k x\n*k : 1\n", """{"a":"x","x":1}""")]
	[InlineData("&k a: 1\nb: *k\n", """{"a":1,"b":"a"}""")]
	[InlineData("&k on: 1\nb: *k\n", """{"on":1,"b":true}""")]
	public void An_anchored_key_and_an_alias_as_a_key_read_as_their_scalar(string text, string json)
	{
		Assert.Equal(json, Yaml.Load(text)!.ToJsonString());
	}

	[Theory]
	[InlineData("a: &m {x: 1}\n*m : 1\n")]
	[InlineData("a: &l [x]\n*l : 1\n")]
	[InlineData("a: &k x\n*k : 1\nx: 2\n")]
	[InlineData("a: *nope\n")]
	public void An_alias_to_a_collection_as_a_key_a_key_given_twice_through_an_alias_and_an_unknown_alias_are_refused(string text)
	{
		Assert.Throws<YamlException>(() => Yaml.Load(text));
	}

	[Theory]
	[InlineData("a: 1\n---\nb: 2\n", """{"a":1}""")]
	[InlineData("---\na: 1\n...\n---\nb: 2\n", """{"a":1}""")]
	[InlineData("x\n", "\"x\"")]
	public void The_first_document_is_the_tree(string text, string json)
	{
		Assert.Equal(json, Yaml.Load(text)!.ToJsonString());
	}

	[Theory]
	[InlineData("a: 1\n---\nb: [\n")]
	[InlineData("a: &a 1\n---\nb: *a\n")]
	public void A_later_document_that_doesnt_parse_or_aliases_an_earlier_ones_anchor_is_refused(string text)
	{
		Assert.ThrowsAny<YamlException>(() => Yaml.Load(text));
	}

	[Theory]
	[InlineData("a: +5\n", """{"a":5}""")]
	[InlineData("a: -0\n", """{"a":0}""")]
	[InlineData("a: +0\n", """{"a":0}""")]
	[InlineData("a: -1_000\n", """{"a":-1000}""")]
	[InlineData("a: 12345678901234567890123\n", """{"a":12345678901234567890123}""")]
	public void An_int_is_its_digits_without_a_plus_or_underscores(string text, string json)
	{
		Assert.Equal(json, Yaml.Load(text)!.ToJsonString());
	}

	[Fact]
	public void An_int_of_a_million_digits_reads_as_its_digits()
	{
		var digits = "9" + new string('0', 999_999);

		Assert.Equal(digits, J.Req(Yaml.Load($"a: {digits}\n"), "a")!.ToJsonString());
	}

	// -- What Yaml.cs reads differently from PyYAML, on purpose --

	[Fact]
	public void Yaml_reads_other_int_bases_infinities_and_timestamps_as_strings_and_the_merge_key_as_a_key()
	{
		const string text = "a: [0x1F, 0o7, 010, 0b1, 1:30, .inf, -.inf, .nan, 2001-01-01]\nb: {<<: {c: 1}}\n";

		const string expected = """{"a":["0x1F","0o7","010","0b1","1:30",".inf","-.inf",".nan","2001-01-01"],"b":{"<<":{"c":1}}}""";

		Assert.Equal(Py.Dumps(JsonNode.Parse(expected)), Py.Dumps(Yaml.Load(text)));
	}

	[Theory]
	[InlineData("? [a]\n: b\n")]
	[InlineData("? {a: 1}\n: b\n")]
	public void Yaml_refuses_a_key_that_isnt_a_scalar(string text)
	{
		Assert.Throws<YamlException>(() => Yaml.Load(text));
	}

	[Fact]
	public void An_action_whose_owner_or_repo_is_a_dot_or_whose_repo_reads_as_an_option_is_skipped_and_three_dots_are_kept()
	{
		const string workflow = """
			jobs:
			  review:
			    steps:
			      - uses: ./repo@v1
			      - uses: owner/.@v1
			      - uses: owner/-r@v1
			      - uses: owner/repo@...
			""";

		Assert.Equal([("owner", "repo", "...")], Bench.PinnedActions(workflow));
	}
}

// The parts that need PATH changed, which is the process's own, so they run alone with the other PATH tests.
[Collection(nameof(GhLookupTests))]
public sealed class BenchPathTests : IDisposable
{
	readonly string temp = Directory.CreateTempSubdirectory("bench-path-test.").FullName;
	readonly string? path = Environment.GetEnvironmentVariable("PATH");

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("PATH", path);
		Directory.Delete(temp, recursive: true);
	}

	// A folder in PATH ahead of the system's, holding name with text as its content, executable.
	[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
	void OnPath(string name, string text)
	{
		var bin = Directory.CreateDirectory(Path.Combine(temp, "bin")).FullName;
		File.WriteAllText(Path.Combine(bin, name), text);
		File.SetUnixFileMode(Path.Combine(bin, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		Environment.SetEnvironmentVariable("PATH", $"{bin}:/usr/bin:/bin");
	}

	[Fact]
	public void A_program_on_path_that_cant_be_run_is_the_cases_error()
	{
		if (OperatingSystem.IsWindows())
			return;
		// No #! line and not a binary: execve refuses it with ENOEXEC.
		OnPath("not-runnable", "\u0001\u0002 not a program\n");

		var error = Assert.Throws<CaseError>(() => Bench.Exec(["not-runnable"]));

		Assert.StartsWith("not-runnable couldn't be run: ", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Only_naming_no_case_ends_the_run_with_exit_1_and_says_so()
	{
		if (OperatingSystem.IsWindows())
			return;
		OnPath("claude", "#!/bin/sh\necho 9.9.9\n");
		var here = Directory.CreateDirectory(Path.Combine(temp, "bench")).FullName;
		File.Copy(Skill.Path("bench", "claude-review-cases.json"), Path.Combine(here, "claude-review-cases.json"));
		var workflows = Directory.CreateDirectory(Path.Combine(temp, "template", "owned", ".github", "workflows")).FullName;
		File.Copy(Bench.Workflow(Skill.Path("bench")), Path.Combine(workflows, "claude-review.yml"));
		var (output, err) = (new StringWriter(), new StringWriter());

		var code = Bench.Run(["--only", "no-such-head"], output, err, here);

		Assert.Equal(1, code);
		Assert.Equal("claude-review-bench.cs: no case matches --only no-such-head\n", err.ToString());
		Assert.Empty(Directory.GetFiles(Path.Combine(here, "results")));
	}
}
