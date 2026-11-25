using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// What the bench's port holds beyond the moved tests (ADR 0017's Update for mpaulosky/Application_Factory#161), so BenchTests.cs keeps the moved
// pytest suite's count: the Python readings it re-implements, held to python3's own (argparse, shlex, str.split() and slicing), PyYAML's readings
// held to what PyYAML 6.0 gave on the same text, the changes the port makes on purpose, and the YamlDotNet pin, in step in both places it is pinned.
public partial class BenchTests
{
	// What python3 prints for program, given input (JSON) on its stdin, in UTF-8 mode.
	static string Python(string program, string input)
	{
		var start = new ProcessStartInfo("python3")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			StandardInputEncoding = new UTF8Encoding(false),
		};
		foreach (var arg in new[] { "-I", "-B", "-X", "utf8", "-c", program })
			start.ArgumentList.Add(arg);
		using var run = Process.Start(start)!;
		run.StandardInput.Write(input);
		run.StandardInput.Close();
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = run.StandardOutput.ReadToEnd();
		run.WaitForExit();
		Assert.True(run.ExitCode == 0, $"python3 failed: {err.Result}");
		return output.TrimEnd('\n');
	}

	static string Json(IEnumerable<string> words) => Py.Dumps(new JsonArray([.. words.Select(word => (JsonNode?)word)]));

	// -- The arguments, held to argparse --

	// The bench's options as claude-review-bench.py declared them, but --prompt-file and --root as str rather than Path: the C# keeps the text
	// the caller gave, which pathlib would have tidied (a/ is a), and opens the same file.
	const string ArgparseBench = """
		import argparse, contextlib, io, json, sys
		parser = argparse.ArgumentParser(prog="claude-review-bench.cs")
		parser.add_argument("--effort")
		parser.add_argument("--model")
		parser.add_argument("--prompt-file")
		parser.add_argument("--tools")
		parser.add_argument("--only")
		parser.add_argument("--set", default="recall")
		parser.add_argument("--root", default="ROOT")
		parser.add_argument("--timeout", type=int, default=1800)
		parser.add_argument("--label", default="")
		parser.add_argument("--rescore", nargs="+")
		try:
		    with contextlib.redirect_stdout(io.StringIO()):
		        args = parser.parse_args(json.loads(sys.stdin.read()))
		except SystemExit as exit:
		    print("help" if exit.code == 0 else f"exit {exit.code}")
		    sys.exit(0)
		print(json.dumps([args.effort, args.model, args.prompt_file, args.tools, args.only, args.set, args.root, args.timeout, args.label, args.rescore]))
		""";

	static string ParseBench(string[] args)
	{
		var parser = Bench.Parser();
		var err = new StringWriter();
		if (parser.Parse(args, new StringWriter(), err) is int exit)
			return exit == 0 ? "help" : $"exit {exit}";
		if (parser.Positional.Count > 0)
			return $"exit {parser.Error("unrecognized arguments", err)}";
		long timeout;
		try
		{
			timeout = parser.Int("--timeout", 1800);
		}
		catch (ArgumentError)
		{
			return "exit 2";
		}
		JsonNode? Value(string name, string? fallback = null) => parser.Values.TryGetValue(name, out var value) ? value : fallback;
		return Py.Dumps(new JsonArray(Value("--effort"), Value("--model"), Value("--prompt-file"), Value("--tools"), Value("--only"), Value("--set", "recall"),
				Value("--root", "ROOT"), timeout, Value("--label", ""),
				parser.Lists.TryGetValue("--rescore", out var files) ? new JsonArray([.. files.Select(file => (JsonNode?)file)]) : null));
	}

	public static TheoryData<string[]> BenchArguments => new()
	{
		new string[] { },
		new[] { "--rescore", "a.json", "b.json" },
		new[] { "--rescore=a.json" },
		new[] { "--rescore=a.json", "b.json" },
		new[] { "--rescore" },
		new[] { "--rescore", "--" },
		new[] { "--rescore", "a.json", "--label", "x", "b.json" },
		new[] { "--rescore", "a.json", "--rescore", "b.json" },
		new[] { "--rescore", "-5" },
		new[] { "--rescore", "a.json", "-x" },
		new[] { "--rescore", "a b.json", "--root", "/r" },
		new[] { "--res", "a.json" },
		new[] { "--r", "a.json" },
		new[] { "--ro", "/r" },
		new[] { "--timeout", "60" },
		new[] { "--timeout", " 7 " },
		new[] { "--timeout", "1_0" },
		new[] { "--timeout", "1__0" },
		new[] { "--timeout", "x" },
		new[] { "--timeout", "1.5" },
		new[] { "--timeout", "-5" },
		new[] { "--timeout", "+5" },
		new[] { "--timeout", "\u0665" },
		new[] { "--timeout", "\u00b2" },
		new[] { "--timeout", "" },
		new[] { "--timeout=x", "--help" },
		new[] { "--only", "" },
		new[] { "--set", "noise", "--only", "4b9", "--effort", "max", "--model", "m", "--tools", "Read", "--prompt-file", "p.txt", "--label", "l" },
		new[] { "stray" },
		new[] { "--bogus" },
		new[] { "--help" },
		new[] { "-h" },
	};

	[Theory]
	[MemberData(nameof(BenchArguments))]
	public void The_benchs_arguments_parse_as_argparse_parsed_them(string[] args) =>
			Assert.Equal(Python(ArgparseBench, JsonSerializer.Serialize(args)), ParseBench(args));

	// -- The Python readings --

	public static TheoryData<string> ShlexTexts => new()
	{
		"a b\tc\r\nd",
		"\"a b\" c",
		"'a\\b' \"a\\\"b\" \"a\\\\b\" \"a\\nb\"",
		"a\\ b a\\\\b \\'",
		"\"\" '' a\"b\"c'd'",
		"--json-schema '{\"type\": \"object\", \"x\": [1, 2]}' --add-dir ${{ runner.temp }}",
		"# not a comment",
		"\"unclosed",
		"'unclosed",
		"trailing\\",
	};

	[Theory]
	[MemberData(nameof(ShlexTexts))]
	public void Shlex_split_splits_as_pythons_shlex_did(string text)
	{
		string words;
		try
		{
			words = Json(Bench.ShlexSplit(text));
		}
		catch (FormatException)
		{
			words = "error";
		}
		Assert.Equal(Python("import json, shlex, sys\ntry: print(json.dumps(shlex.split(json.load(sys.stdin))))\nexcept ValueError: print('error')",
				JsonSerializer.Serialize(text)), words);
	}

	[Fact]
	public void The_template_workflows_claude_args_split_as_shlex_split_them()
	{
		var (_, claudeArgs) = Bench.ReviewStep(Workflow);
		Assert.Equal(Python("import json, shlex, sys\nprint(json.dumps(shlex.split(json.load(sys.stdin))))", JsonSerializer.Serialize(claudeArgs)),
				Json(Bench.ShlexSplit(claudeArgs)));
	}

	public static TheoryData<string> Texts => new()
	{
		"",
		"  a\tb\nc\u2028d\u001ce\u00a0f\u0085g\u200bh\u3000i  ",
		"\U0001f600a\U0001f600b\U0001f600c\U0001f600",
		"abcdef",
	};

	[Theory]
	[MemberData(nameof(Texts))]
	public void Split_and_slicing_read_a_str_as_python_does(string text)
	{
		var mine = new JsonArray(Json(Py.Split(text)), Py.Head(text, 3), Py.Tail(text, 3), Py.Head(text, 0), Py.Tail(text, 1), Py.Tail(text, 100));
		Assert.Equal(Python("import json, sys\nt = json.load(sys.stdin)\nprint(json.dumps([json.dumps(t.split()), t[:3], t[-3:], t[:0], t[-1:], t[-100:]]))",
				JsonSerializer.Serialize(text)), Py.Dumps(mine));
	}

	// -- The YAML readings --

	[Fact]
	public void Yaml_reads_a_plain_scalar_as_pyyaml_did()
	{
		const string text = """
			nulls: [~, null, Null, NULL, ]
			empty:
			bools: [true, True, TRUE, yes, Yes, YES, on, On, ON, false, False, FALSE, no, No, NO, off, Off, OFF]
			not-bools: [y, n, tRUE, "true", 'yes', !!str on]
			ints: [0, 7, -3, +4, 1_000, 123456789012345678901234567890]
			floats: [1.5, -0.5, 1., .5, 1.0e+3, 1_0.2_5]
			strings: [1e3, 6.02e23, -.5, abc, "1", '2', 1.2.3, ._]
			block: |
			  a
			  b
			folded: >-
			  c
			  d
			on: push
			alias: &a {x: 1}
			again: *a
			""";
		// PyYAML 6.0.3's yaml.safe_load() of the text, its keys as str() made them but on, which is "on" here where PyYAML made it True.
		const string pyyaml = """
			{"nulls": [null, null, null, null], "empty": null, "bools": [true, true, true, true, true, true, true, true, true, false, false, false, false, false,
			false, false, false, false], "not-bools": ["y", "n", "tRUE", "true", "yes", "on"], "ints": [0, 7, -3, 4, 1000, 123456789012345678901234567890],
			"floats": [1.5, -0.5, 1.0, 0.5, 1000.0, 10.25], "strings": ["1e3", "6.02e23", "-.5", "abc", "1", "2", "1.2.3", "._"], "block": "a\nb\n",
			"folded": "c d", "on": "push", "alias": {"x": 1}, "again": {"x": 1}}
			""";
		Assert.Equal(Py.Dumps(JsonNode.Parse(pyyaml)), Py.Dumps(Yaml.Load(text)));
	}

	[Fact]
	public void Yaml_reads_an_empty_text_as_none_and_refuses_one_it_cant_parse_or_a_key_given_twice()
	{
		Assert.Null(Yaml.Load(""));
		Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() => Yaml.Load("a: [b"));
		// PyYAML kept the last value; GitHub refuses such a workflow, and so does YamlDotNet.
		Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() => Yaml.Load("a: 1\na: 2\n"));
		Assert.Empty(Bench.PinnedActions("a: [b"));
	}

	// -- What the port changes on purpose --

	[Fact]
	public void An_action_whose_owner_repo_or_ref_steps_out_or_reads_as_an_option_is_skipped()
	{
		const string workflow = """
			jobs:
			  review:
			    steps:
			      - uses: ../repo@v1
			      - uses: owner/..@v1
			      - uses: owner/repo@..
			      - uses: owner/repo@.
			      - uses: -o/repo@v1
			      - uses: owner/repo@--output=/tmp/x
			      - uses: owner/repo@v1
			""";
		Assert.Equal([("owner", "repo", "v1")], Bench.PinnedActions(workflow));
	}

	[Fact]
	public void A_linked_claude_config_is_removed_not_what_it_points_to()
	{
		var tree = Path.Combine(temp, "tree");
		var outside = Path.Combine(temp, "outside");
		Directory.CreateDirectory(tree);
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "keep.txt"), "kept\n");
		Git(tree, "init", "-q");
		Git(tree, "commit", "-q", "--allow-empty", "-m", "base");
		Directory.CreateSymbolicLink(Path.Combine(tree, ".claude"), outside);
		File.CreateSymbolicLink(Path.Combine(tree, ".mcp.json"), Path.Combine(temp, "missing.json"));

		Bench.UseBaseSettings(tree, "HEAD");

		Assert.Equal("kept\n", File.ReadAllText(Path.Combine(outside, "keep.txt")));
		Assert.Null(new FileInfo(Path.Combine(tree, ".claude")).LinkTarget);
		Assert.False(Path.Exists(Path.Combine(tree, ".claude")));
		Assert.Null(new FileInfo(Path.Combine(tree, ".mcp.json")).LinkTarget);
	}

	[Fact]
	public void A_review_turn_count_over_max_turns_is_flagged_from_the_transcripts_numbers()
	{
		var (result, reads) = Bench.Transcript("{\"type\": \"assistant\", \"message\": {\"content\": [{\"type\": \"tool_use\", \"name\": \"Glob\", \"input\": {\"pattern\": \"*.md\"}}]}}\n"
				+ "{\"type\": \"assistant\", \"message\": {\"content\": [{\"type\": \"tool_use\", \"name\": \"Grep\", \"input\": {}}]}}\n"
				+ "{\"type\": \"result\", \"num_turns\": 6}\n");
		// A message with no id is a turn of its own, named by the number of turns before it; a call with no file or pattern is named by its tool.
		Assert.Equal("""[["*.md"],["Grep"]]""", reads.ToJsonString());
		Assert.Equal(6, J.Int(J.Req(result, "num_turns")));
	}

	// -- The YamlDotNet pin --

	[Fact]
	public void Yamldotnet_is_pinned_alike_in_the_bench_and_the_test_project()
	{
		var bench = Regex.Match(File.ReadAllText(Skill.Path("bench", "claude-review-bench.cs")), @"^#:package YamlDotNet@(\S+)$", RegexOptions.Multiline);
		var project = Regex.Match(File.ReadAllText(Skill.Path("tests", "Scripts.Tests", "Scripts.Tests.csproj")), "<PackageReference Include=\"YamlDotNet\" Version=\"([^\"]+)\"");
		Assert.True(bench.Success && project.Success, "a YamlDotNet pin is missing from claude-review-bench.cs or Scripts.Tests.csproj");
		Assert.Equal(project.Groups[1].Value, bench.Groups[1].Value);
		JsonNode? Locked(params string[] path) => J.Req(J.Req(J.Req(JsonNode.Parse(File.ReadAllText(Skill.Path(path))), "dependencies"), "net11.0"), "YamlDotNet");
		var (benchLock, projectLock) = (Locked("bench", "packages.lock.json"), Locked("tests", "Scripts.Tests", "packages.lock.json"));
		Assert.Equal(bench.Groups[1].Value, J.S(J.Req(benchLock, "resolved")));
		Assert.Equal(J.S(J.Req(projectLock, "resolved")), J.S(J.Req(benchLock, "resolved")));
		Assert.Equal(J.S(J.Req(projectLock, "contentHash")), J.S(J.Req(benchLock, "contentHash")));
		Assert.Contains("#:property RestoreLockedMode=true", File.ReadAllText(Skill.Path("bench", "claude-review-bench.cs")), StringComparison.Ordinal);
	}
}
