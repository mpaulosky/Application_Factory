using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

// Gh.cs, what github/'s scripts share: the gh runner, the JSON helpers, the argument parser (held to argparse, which the Python scripts
// parsed with, by running both on the same arguments), Script.G and Script.Run.
public class GhTests
{
	// -- The argument parser --

	// land's options, as the Python declared them to argparse and LandCore.cs declares them to Arguments.
	const string ArgparseLand = """
		import argparse, json, sys
		parser = argparse.ArgumentParser(prog="land")
		parser.add_argument("prs", nargs="*")
		parser.add_argument("--ready", action="store_true")
		parser.add_argument("--once", action="store_true")
		parser.add_argument("--dry-run", action="store_true")
		parser.add_argument("--interval", type=float, default=60)
		parser.add_argument("--timeout", type=float, default=180)
		parser.add_argument("--blog-wait", type=float, default=15)
		args = parser.parse_args(json.loads(sys.argv[1]))
		# The floats as format(x, "g") prints them, which Script.G is held to below: JSON has no inf.
		print(json.dumps([args.prs, args.ready, args.once, args.dry_run, *(format(x, "g") for x in (args.interval, args.timeout, args.blog_wait))]))
		""";

	static Arguments LandParser() => new Arguments("land", "[options] [prs ...]", "land")
			.Flag("--ready", "").Flag("--once", "").Flag("--dry-run", "").FloatOption("--interval", "").FloatOption("--timeout", "").FloatOption("--blog-wait", "");

	// (exit code, what it parsed as JSON or "") from argparse, run by python3.
	static (int Code, string Parsed) Argparse(string program, string[] args)
	{
		var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in new[] { "-I", "-c", program, JsonSerializer.Serialize(args) })
			start.ArgumentList.Add(arg);
		using var run = Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = run.StandardOutput.ReadToEnd();
		run.WaitForExit();
		_ = err.Result;
		return (run.ExitCode, run.ExitCode != 0 ? "" : output.StartsWith("usage: ", StringComparison.Ordinal) ? "help" : output.Trim());
	}

	// The same from Arguments, with land's defaults, and the error line it wrote.
	static (int Code, string Parsed, string Error) Parse(string[] args)
	{
		var parser = LandParser();
		var err = new StringWriter();
		var code = parser.Parse(args, new StringWriter(), err);
		if (code is int exit)
			return (exit, exit == 0 ? "help" : "", err.ToString());
		try
		{
			var parsed = new JsonArray(new JsonArray([.. parser.Positional.Select(p => (JsonNode?)p)]), parser.Has("--ready"), parser.Has("--once"),
					parser.Has("--dry-run"), Script.G(parser.Float("--interval", 60)), Script.G(parser.Float("--timeout", 180)), Script.G(parser.Float("--blog-wait", 15)));
			return (0, parsed.ToJsonString(), "");
		}
		catch (ArgumentError error)
		{
			return (parser.Error(error.Message, err), "", err.ToString());
		}
	}

	public static TheoryData<string[]> LandArguments => new()
	{
		new string[] { },
		new[] { "o/app", "--once", "o/lib#7" },
		new[] { "--onc", "o/app" },
		new[] { "--r", "--d", "--o" },
		new[] { "--interval", "5", "--timeout=0.5", "--blog-wait", "1e1" },
		new[] { "--int=5", "--time", "2" },
		new[] { "--interval", "-5" },
		new[] { "--interval", " 7 " },
		new[] { "--interval", "1_000" },
		new[] { "--interval", "inf" },
		new[] { "--", "--once", "-x" },
		new[] { "--interval", "x" },
		new[] { "--interval", "1__0" },
		new[] { "--interval", "_1" },
		new[] { "--interval", "" },
		new[] { "--interval" },
		new[] { "--interval", "--once" },
		new[] { "--once=yes" },
		new[] { "--bogus", "o/app" },
		new[] { "-x" },
		new[] { "-" },
		new[] { "--once", "--once" },
		new[] { "--interval", "5", "--interval", "6" },
		// Positional arguments are read in one run, as argparse reads a nargs="*" positional: a second run after an option is refused.
		new[] { "o/app", "o/lib", "--once" },
		new[] { "--once", "o/app", "o/lib", "--ready" },
		new[] { "o/app", "--once", "o/lib#7" },
		new[] { "--once", "o/app", "--ready", "o/lib" },
		new[] { "o/app", "--interval", "5", "o/lib" },
		new[] { "o/app", "--bogus", "o/lib" },
		new[] { "o/app", "--", "o/lib" },
		new[] { "o/app", "--once", "--", "o/lib" },
		new[] { "--", "o/app", "--once" },
		new[] { "--once", "--", "o/app", "o/lib" },
		// --help is one of the options a prefix can name, and -h with more after it is -h.
		new[] { "--h" },
		new[] { "--he" },
		new[] { "--hel" },
		new[] { "--help" },
		new[] { "--he=1" },
		new[] { "--help=1" },
		new[] { "-h=1" },
		new[] { "--bogus", "-h" },
		new[] { "--interval", "-h" },
		new[] { "--interval", "--h" },
		// A float option's value is read when the option is met, so a bad one ends the parse before a later -h.
		new[] { "--interval", "x", "-h" },
		new[] { "-h", "--interval", "x" },
		new[] { "--interval=x", "--bogus" },
		// One sign at most, and inf or nan behind a - is an option, not a negative number.
		new[] { "--interval=--inf" },
		new[] { "--interval", "+-nan" },
		new[] { "--interval", "-Infinity" },
		new[] { "--interval", "-NaN" },
		new[] { "--interval=-inf" },
		new[] { "--interval", "-.5" },
		new[] { "--interval", "-0" },
		// An argument with a space that names no option is positional.
		new[] { "-x y" },
		new[] { "--interval", "-x y" },
		new[] { "--onc e" },
	};

	[Theory]
	[MemberData(nameof(LandArguments))]
	public void The_parser_reads_lands_arguments_as_argparse_does(string[] args)
	{
		var (code, parsed, error) = Parse(args);
		var (wantCode, wantParsed) = Argparse(ArgparseLand, args);

		Assert.Equal(wantCode, code);
		if (wantParsed == "help" || parsed == "help")
			Assert.Equal(wantParsed, parsed);
		else if (wantCode == 0)
			Assert.True(JsonNode.DeepEquals(JsonNode.Parse(wantParsed), JsonNode.Parse(parsed)), $"argparse: {wantParsed}, Arguments: {parsed}");
		else
			Assert.Matches(@"\Ausage: land .*\nland: error: .+\n\z", error);
	}

	// Not a moved test: gh is found in PATH's absolute folders only, never in the current folder or the program's, which .NET's own
	// lookup tries first.
	[Fact]
	public void Gh_is_found_on_paths_absolute_folders_only()
	{
		if (OperatingSystem.IsWindows())
			return; // the scripts run on Linux and macOS
		var root = Directory.CreateTempSubdirectory("which.");
		try
		{
			var found = Directory.CreateDirectory(Path.Combine(root.FullName, "found")).FullName;
			var plain = Directory.CreateDirectory(Path.Combine(root.FullName, "plain")).FullName;
			var empty = Directory.CreateDirectory(Path.Combine(root.FullName, "empty")).FullName;
			File.WriteAllText(Path.Combine(found, "gh"), "#!/bin/sh\n");
			File.SetUnixFileMode(Path.Combine(found, "gh"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
			File.WriteAllText(Path.Combine(plain, "gh"), "not executable\n");

			Assert.Equal(Path.Combine(found, "gh"), Gh.Which("gh", $"{empty}:{plain}:{found}"));
			// A relative folder, an empty entry (execvp's current folder) and . are skipped, even when they hold a gh.
			var relative = Path.GetRelativePath(Environment.CurrentDirectory, found);
			Assert.Throws<FileNotFoundException>(() => Gh.Which("gh", $"{relative}::.:{empty}"));
			Assert.Throws<FileNotFoundException>(() => Gh.Which("gh", plain));
			Assert.Throws<FileNotFoundException>(() => Gh.Which("gh", ""));
			Assert.Throws<FileNotFoundException>(() => Gh.Which("gh", null));
		}
		finally
		{
			root.Delete(true);
		}
	}

	[Fact]
	public void An_ambiguous_prefix_is_a_usage_error_naming_both_options()
	{
		var parser = new Arguments("x", "", "").Flag("--ready", "").Flag("--retry", "");
		var err = new StringWriter();

		Assert.Equal(2, parser.Parse(["--re"], new StringWriter(), err));
		Assert.Contains("ambiguous option: --re could match --ready, --retry", err.ToString());
		// An exact name is never ambiguous, whatever it prefixes.
		var exact = new Arguments("x", "", "").Flag("--ready", "").Flag("--ready-all", "");
		Assert.Null(exact.Parse(["--ready"], new StringWriter(), new StringWriter()));
		Assert.True(exact.Has("--ready") && !exact.Has("--ready-all"));
	}

	[Fact]
	public void Help_prints_the_usage_and_each_option_and_exits_0()
	{
		var output = new StringWriter();

		Assert.Equal(0, LandParser().Parse(["o/app", "-h", "--bogus"], output, new StringWriter()));
		Assert.StartsWith("usage: land [options] [prs ...]\n", output.ToString());
		Assert.Contains("--interval INTERVAL", output.ToString());
		Assert.Contains("--blog-wait BLOG_WAIT", output.ToString());
		Assert.Equal(0, LandParser().Parse(["--help"], new StringWriter(), new StringWriter()));
	}

	// -- Script.G and Script.Run --

	[Theory]
	[InlineData(60.0)]
	[InlineData(0.5)]
	[InlineData(15.0)]
	[InlineData(0.10000000000000002)] // 0.1 min, as BlogWait / 60 gives it back
	[InlineData(1e-5)]
	[InlineData(0.0001)]
	[InlineData(123456.0)]
	[InlineData(1234567.0)]
	[InlineData(999999.5)]
	[InlineData(-2.5)]
	[InlineData(0.0)]
	[InlineData(-1e-7)]
	[InlineData(double.PositiveInfinity)]
	[InlineData(double.NegativeInfinity)]
	[InlineData(double.NaN)]
	public void G_formats_a_number_as_pythons_g_does(double value)
	{
		var (code, python) = Argparse($"print(format(float('{value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}'), 'g'))", []);

		Assert.Equal(0, code);
		Assert.Equal(python, Script.G(value));
	}

	[Fact]
	public void Run_returns_the_scripts_exit_code_and_turns_an_uncaught_exception_into_exit_1_with_its_message()
	{
		var output = new StringWriter();
		var err = new StringWriter();

		Assert.Equal(3, Script.Run((stdout, stderr) =>
		{
			stdout.Write("out");
			stderr.Write("err");
			return 3;
		}, output, err));
		Assert.Equal("out", output.ToString());
		Assert.Equal("err", err.ToString());
		err = new StringWriter();
		Assert.Equal(1, Script.Run((_, _) => throw new KeyNotFoundException("'data'"), new StringWriter(), err));
		Assert.StartsWith("KeyNotFoundException: 'data'\n", err.ToString());
	}

	// -- The JSON helpers --

	[Fact]
	public void Int_reads_a_whole_number_parsed_or_built_and_nothing_else()
	{
		Assert.Equal(7, J.Int(JsonNode.Parse("7")));
		Assert.Equal(7, J.Int(JsonValue.Create(7)));
		Assert.Equal(7, J.Int(JsonValue.Create(7L)));
		Assert.Null(J.Int(JsonNode.Parse("\"7\"")));
		Assert.Null(J.Int(JsonNode.Parse("7.5")));
		Assert.Null(J.Int(null));
		Assert.Null(J.Int(new JsonObject()));
	}

	[Fact]
	public void The_helpers_read_json_as_the_python_did()
	{
		var node = JsonNode.Parse("""{"a": null, "s": "x", "n": 0, "o": {}, "l": [1]}""");

		Assert.Throws<KeyNotFoundException>(() => J.Req(node, "missing"));
		Assert.Throws<KeyNotFoundException>(() => J.Req(null, "a"));
		Assert.Null(J.Req(node, "a"));
		Assert.Null(J.Get(node, "missing"));
		Assert.True(J.Has(node, "a") && !J.Has(node, "missing"));
		Assert.Equal("", J.Str(node, "a"));
		Assert.Equal("x", J.Str(node, "s"));
		Assert.Equal(["None", "x", "0", "{}"], [J.Fmt(J.Get(node, "a")), J.Fmt(J.Get(node, "s")), J.Fmt(J.Get(node, "n")), J.Fmt(J.Get(node, "o"))]);
		Assert.Equal([false, true, false, false, true, false], [J.B(J.Get(node, "a")), J.B(J.Get(node, "s")), J.B(J.Get(node, "n")), J.B(J.Get(node, "o")),
				J.B(J.Get(node, "l")), J.B(JsonNode.Parse("\"\""))]);
		Assert.Empty(J.Nodes(node));
		Assert.Equal("octo", J.Login(JsonNode.Parse("""{"author": {"login": "octo"}}""")));
		Assert.Equal("", J.Login(JsonNode.Parse("""{"author": null}""")));
	}

	// -- The gh runner --

	sealed class FakeGh(GhRun answer)
	{
		public readonly List<IReadOnlyList<string>> Calls = [];

		public GhRun Run(IReadOnlyList<string> argv)
		{
			Calls.Add(argv);
			return answer;
		}
	}

	[Fact]
	public void Rest_parses_the_answer_and_tells_a_404_from_any_other_failure()
	{
		var ok = new FakeGh(new GhRun(0, """{"permission": "write"}""", ""));
		Assert.Equal("write", J.Str(new Gh(ok.Run).Rest("repos/o/r"), "permission"));
		Assert.Equal(["api", "repos/o/r"], ok.Calls[0]);

		Assert.Throws<NotFound>(() => new Gh(new FakeGh(new GhRun(1, "", "gh: Not Found (HTTP 404)\n")).Run).Rest("x"));
		Assert.Throws<NotFound>(() => new Gh(new FakeGh(new GhRun(1, """{"message":"Not Found","status":"HTTP 404"}""", "")).Run).Rest("x"));
		var other = Assert.Throws<GhException>(() => new Gh(new FakeGh(new GhRun(1, "", "gh: Bad Gateway (HTTP 502)\n")).Run).Rest("x"));
		Assert.IsNotType<NotFound>(other);
		Assert.Equal("gh: Bad Gateway (HTTP 502)", other.Message);
		// With nothing on stderr, the failure is stdout's.
		Assert.Equal("on stdout", Assert.Throws<GhException>(() => new Gh(new FakeGh(new GhRun(1, " on stdout \n", "  ")).Run).Rest("x")).Message);
	}

	[Fact]
	public void Graphql_sends_the_query_its_fields_with_F_and_its_strings_with_f_and_returns_the_data()
	{
		var gh = new FakeGh(new GhRun(0, """{"data": {"node": 1}}""", ""));

		var data = new Gh(gh.Run).Graphql("query { x }", ["owner=o", "name=r"], [("id", "PR_1"), ("cursor", "c")]);

		Assert.Equal("""{"node":1}""", data!.ToJsonString());
		Assert.Equal(["api", "graphql", "-f", "query=query { x }", "-F", "owner=o", "-F", "name=r", "-f", "id=PR_1", "-f", "cursor=c"], gh.Calls[0]);
		Assert.Equal("HTTP 502", Assert.Throws<GhException>(() => new Gh(new FakeGh(new GhRun(1, "", "HTTP 502\n")).Run).Graphql("q", [], [])).Message);
		// An answer with no data is a failure to read, as the Python's ["data"] was.
		Assert.Throws<KeyNotFoundException>(() => new Gh(new FakeGh(new GhRun(0, """{"errors": []}""", "")).Run).Graphql("q", [], []));
	}
}
