using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseline.Tests;

// The Template's .github/scripts/Gh.cs, what its C# scripts share: the argument parser, held to argparse by running both on the same
// arguments with detect_changes.py's options and with discover_tests.py's, a required mutually exclusive group among them; SurrogateEscape,
// held to Python's surrogateescape the same way; Py, held to Python's str methods; Script.Write, Script.Which and Script.Run.
public class TemplateGhTests
{
	// detect_changes.py's options, as it declares them to argparse and DetectChanges.cs to Arguments.
	const string ArgparseDetectChanges = """
		import argparse, json, sys
		parser = argparse.ArgumentParser(prog="detect_changes")
		parser.add_argument("--base", required=True)
		parser.add_argument("--head", default="HEAD")
		parser.add_argument("--output", required=True)
		parser.add_argument("--author", default="")
		parser.add_argument("--head-ref", default="")
		try:
		    args = parser.parse_args(json.loads(sys.argv[1]))
		except SystemExit as stop:
		    sys.exit(stop.code)
		print(json.dumps([args.base, args.head, args.output, args.author, args.head_ref], separators=(",", ":")))
		""";

	static (int Code, string Stdout, string Stderr) Python(string program, params string[] args)
	{
		var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in new[] { "-I", "-c", program }.Concat(args))
			start.ArgumentList.Add(arg);
		using var run = Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = run.StandardOutput.ReadToEnd();
		run.WaitForExit();
		return (run.ExitCode, output, err.Result);
	}

	static Arguments Parser() => new Arguments("detect_changes", "[-h] --base BASE [--head HEAD] --output OUTPUT [--author AUTHOR] [--head-ref HEAD_REF]", "")
			.Required("--base", "").Option("--head", "").Required("--output", "").Option("--author", "").Option("--head-ref", "");

	// (exit code, what it parsed as JSON, or "help", or the error line) from argparse and from Arguments.
	static (int Code, string Parsed) Argparse(string[] args)
	{
		var (code, stdout, stderr) = Python(ArgparseDetectChanges, JsonSerializer.Serialize(args));
		return (code, code == 2 ? stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1] : stdout.StartsWith("usage: ", StringComparison.Ordinal) ? "help" : stdout.Trim());
	}

	static (int Code, string Parsed) Parse(string[] args)
	{
		var parser = Parser();
		var err = new StringWriter();
		if (parser.Parse(args, new StringWriter(), err) is int exit)
			return (exit, exit == 2 ? err.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1] : "help");
		var parsed = new JsonArray(parser.Value("--base", ""), parser.Value("--head", "HEAD"), parser.Value("--output", ""), parser.Value("--author", ""),
				parser.Value("--head-ref", ""));
		return (0, JsonSerializer.Serialize(JsonSerializer.Deserialize<string[]>(parsed.ToJsonString())));
	}

	public static TheoryData<string[]> DetectChangesArguments => new()
	{
		new[] { "--base", "b", "--output", "o" },
		new[] { "--base", "b", "--output", "o", "--head", "h", "--author", "dependabot[bot]", "--head-ref", "dependabot/github_actions/x" },
		new[] { "--base=b", "--output=o", "--author=", "--head-ref=a=b" },
		new[] { "--bas", "b", "--out", "o", "--auth", "a", "--head-r", "r" },
		new[] { "--base", "b", "--output", "o", "--base", "c" },
		new[] { "--output", "o" },
		new[] { "--base", "b" },
		new string[] { },
		new[] { "--he", "x", "--base", "b", "--output", "o" },
		new[] { "--h" },
		new[] { "--hel" },
		new[] { "-h" },
		new[] { "--base", "b", "--output", "o", "extra" },
		new[] { "extra", "--base", "b", "--output", "o" },
		new[] { "--base", "b", "--output", "o", "--bogus" },
		new[] { "--bogus" },
		new[] { "--base", "b", "--output" },
		new[] { "--base", "--output", "o" },
		new[] { "--base", "-5", "--output", "o" },
		new[] { "--base", "-x y", "--output", "o" },
		new[] { "--base", "-x", "--output", "o" },
		new[] { "--base", "b", "--output", "o", "--" },
		new[] { "--base", "b", "--output", "o", "--", "x" },
		new[] { "--", "--base", "b", "--output", "o" },
		new[] { "--base", "b", "--", "--output", "o" },
		new[] { "--base", "b", "--output", "o", "-h" },
		new[] { "--base", "b", "--head-ref=--x", "--output", "o" },
		new[] { "--author", "a", "--bogus" },
	};

	[Theory]
	[MemberData(nameof(DetectChangesArguments))]
	public void Arguments_parse_detect_changes_options_as_argparse_does(string[] args) => Assert.Equal(Argparse(args), Parse(args));

	[Fact]
	public void A_usage_error_names_the_program_and_exits_2()
	{
		var err = new StringWriter();

		Assert.Equal(2, Parser().Parse(["--output", "o"], new StringWriter(), err));
		Assert.Equal("usage: detect_changes [-h] --base BASE [--head HEAD] --output OUTPUT [--author AUTHOR] [--head-ref HEAD_REF]\n"
				+ "detect_changes: error: the following arguments are required: --base\n", err.ToString());
	}

	// discover_tests.py's options, as it declares them to argparse and DiscoverTests.cs to Arguments: --output and --list a required
	// mutually exclusive group.
	const string ArgparseDiscoverTests = """
		import argparse, json, sys
		parser = argparse.ArgumentParser(prog="discover_tests")
		parser.add_argument("--root", default=".")
		mode = parser.add_mutually_exclusive_group(required=True)
		mode.add_argument("--output")
		mode.add_argument("--list", action="store_true")
		try:
		    args = parser.parse_args(json.loads(sys.argv[1]))
		except SystemExit as stop:
		    sys.exit(stop.code)
		print(json.dumps([args.root, args.output, args.list], separators=(",", ":")))
		""";

	public static TheoryData<string[]> DiscoverTestsArguments => new()
	{
		new[] { "--output", "o" },
		new[] { "--list" },
		new[] { "--root", "r", "--list" },
		new[] { "--list", "--root=r" },
		new[] { "--l", "--r", "r" },
		new[] { "--o", "o" },
		new string[] { },
		new[] { "--root", "r" },
		new[] { "--output", "o", "--list" },
		new[] { "--list", "--output", "o" },
		new[] { "--list", "--output" },
		new[] { "--list", "--list" },
		new[] { "--output", "o", "--output", "p" },
		new[] { "--list=x" },
		new[] { "--list", "--output=o" },
		new[] { "--list", "extra" },
		new[] { "--root", "r", "extra" },
		new[] { "extra" },
		new[] { "--list", "--bogus" },
		new[] { "--output", "o", "--list", "--bogus" },
		new[] { "--list", "-h" },
		new[] { "--output", "" },
	};

	// (exit code, what it parsed as JSON, or the error line) from argparse and from Arguments, with discover_tests.py's options.
	static (int Code, string Parsed) ArgparseDiscover(string[] args)
	{
		var (code, stdout, stderr) = Python(ArgparseDiscoverTests, JsonSerializer.Serialize(args));
		return (code, code == 2 ? stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1] : stdout.StartsWith("usage: ", StringComparison.Ordinal) ? "help" : stdout.Trim());
	}

	static (int Code, string Parsed) ParseDiscover(string[] args)
	{
		var parser = new Arguments("discover_tests", "[-h] [--root ROOT] (--output OUTPUT | --list)", "")
				.Option("--root", "").Option("--output", "").Flag("--list", "").OneOf("--output", "--list");
		var err = new StringWriter();
		if (parser.Parse(args, new StringWriter(), err) is int exit)
			return (exit, exit == 2 ? err.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1] : "help");
		var parsed = new JsonArray(parser.Value("--root", "."), parser.Has("--output") ? parser.Value("--output", "") : null, parser.Has("--list"));
		return (0, parsed.ToJsonString());
	}

	[Theory]
	[MemberData(nameof(DiscoverTestsArguments))]
	public void Arguments_parse_a_required_mutually_exclusive_group_as_argparse_does(string[] args) => Assert.Equal(ArgparseDiscover(args), ParseDiscover(args));

	// Text with each line break str.splitlines() knows, a CRLF, blanks str.isspace() counts (\x1f, U+00A0, U+3000 among them) and some
	// it doesn't (U+200B), and a trailing break.
	public static TheoryData<string> PyTexts => new()
	{
		"",
		"a",
		"a\n",
		"a\r\nb\rc\n\nd",
		"a\vb\fc\x1cd\x1de\x1ef\x85g\u2028h\u2029i",
		"\x1f a \u00a0\u3000\t",
		"\u200b a \u200b",
		"\n\n",
		"\r\n\r",
		" \x1c ",
	};

	[Theory]
	[MemberData(nameof(PyTexts))]
	public void Py_strips_and_splits_lines_as_python_does(string text)
	{
		var (code, stdout, _) = Python("import json, sys\ntext = json.loads(sys.argv[1])\nprint(json.dumps([text.strip(), text.splitlines()]))",
				JsonSerializer.Serialize(text));

		Assert.Equal(0, code);
		var python = JsonNode.Parse(stdout)!.AsArray();
		Assert.Equal(python[0]!.GetValue<string>(), Py.Strip(text));
		Assert.Equal(python[1]!.AsArray().Select(line => line!.GetValue<string>()), Py.SplitLines(text));
	}

	// Bytes that are and aren't UTF-8: a lone continuation byte, a cut sequence, an overlong form, an encoded surrogate, a code point past
	// U+10FFFF, and a valid astral character among them.
	public static TheoryData<string> Bytes => new()
	{
		"",
		"6162",
		"ff",
		"80",
		"c3",
		"c341",
		"e282",
		"e28241",
		"c0af",
		"eda080",
		"f4908080",
		"f09f9880",
		"61ff62c3a9e282ac",
		"fffe",
	};

	[Theory]
	[MemberData(nameof(Bytes))]
	public void SurrogateEscape_decodes_as_pythons_surrogateescape(string hex)
	{
		var bytes = Convert.FromHexString(hex);
		// Each UTF-16 code unit of Python's decoding, as hex, which a lone surrogate can't cross JSON as.
		var (_, python, _) = Python("import sys; s = bytes.fromhex(sys.argv[1]).decode('utf-8', 'surrogateescape'); print(s.encode('utf-16-be', 'surrogatepass').hex())", hex);

		var decoded = SurrogateEscape.Decode(bytes);

		Assert.Equal(python.Trim(), string.Concat(decoded.Select(c => ((int)c).ToString("x4"))));
		Assert.Equal(bytes, SurrogateEscape.Encode(decoded));
	}

	[Fact]
	public void Script_write_sends_an_escaped_byte_to_a_stream_as_itself()
	{
		using var stream = new MemoryStream();
		var writer = new StreamWriter(stream, new UTF8Encoding(false));
		writer.Write("é ");

		Script.Write(writer, SurrogateEscape.Decode([0x61, 0xff, 0x0a]));

		Assert.Equal(new byte[] { 0xc3, 0xa9, 0x20, 0x61, 0xff, 0x0a }, stream.ToArray());
	}

	[Theory]
	[InlineData("")]
	[InlineData(".")]
	[InlineData("relative")]
	public void Which_skips_a_folder_that_isnt_absolute(string folder)
	{
		if (OperatingSystem.IsWindows())
			return; // the scripts run on Linux
		var root = Directory.CreateTempSubdirectory("template-which.");
		try
		{
			var here = Directory.CreateDirectory(Path.Combine(root.FullName, folder.Length == 0 ? "." : folder)).FullName;
			File.WriteAllText(Path.Combine(here, "git"), "#!/bin/sh\n");
			File.SetUnixFileMode(Path.Combine(here, "git"), UnixFileMode.UserRead | UnixFileMode.UserExecute);

			// The relative folder is read from the test's own current folder, where it names nothing: so the absolute one is the copy that counts.
			Assert.Throws<FileNotFoundException>(() => Script.Which("git", folder));
			Assert.Equal(Path.Combine(here, "git"), Script.Which("git", $"{folder}:{here}"));
		}
		finally
		{
			root.Delete(true);
		}
	}

	[Fact]
	public void Script_run_turns_an_exception_into_exit_1_and_a_message()
	{
		var err = new StringWriter();

		Assert.Equal(1, Script.Run((_, _) => throw new CalledProcessError(["git", "diff"], 128, "fatal: bad revision\n"), new StringWriter(), err));
		Assert.StartsWith("CalledProcessError: Command 'git diff' returned non-zero exit status 128. It said: fatal: bad revision\n", err.ToString());
	}
}
