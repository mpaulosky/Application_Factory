// The post job's C# path (mpaulosky/Application_Factory#129): a base with post_claude_review.cs has it built alone, with the base's global.json, in a
// folder of its own (ADR 0017's checker layout), and run as a dll; the base's .py runs only while the base has no .cs. Each build is a real dotnet build
// of a few lines, so these take some seconds.
public partial class BenchTests
{
	// A post_claude_review.cs that prints who it is and its arguments, and exits with exit.
	static string CsScript(string name, int exit = 0) =>
		$"#:property PublishAot=false\nSystem.Console.WriteLine(\"{name} \" + string.Join(\" \", args));\nreturn {exit};\n";

	[Fact]
	public void A_base_with_the_cs_builds_it_outside_the_prs_tree_and_runs_it()
	{
		// The base's .py says "the base script" if it runs. The base's C# exits 4, to show its exit is the step's.
		var repo = PostRepo("# Takes --effort.\nprint('the base script')\n", baseCs: CsScript("the base's C#", exit: 4));

		var (code, stdout) = RunStep("Post the review on the head commit", repo);

		Assert.Equal(4, code);
		Assert.Equal("the base's C# --repo o/r --pr 7 --head abc --effort high (claude-sonnet-5-5), 1 batch", stdout.Trim());
		// The build wrote nothing into the checkout, and its runfile folder is under the test's own folder, which Dispose removes.
		Assert.Equal("", Git(repo, "status", "--porcelain", "--ignored"));
		Assert.NotEmpty(Directory.EnumerateFileSystemEntries(Path.Combine(temp, "data", "dotnet", "runfile")));
	}

	// The batch count the C# is given in --effort, as the pytest's table checks it for the .py: a count that isn't a positive number
	// without a leading zero is 1.
	[Theory]
	[InlineData("2", "2 batches")]
	[InlineData("", "1 batch")]
	[InlineData("04", "1 batch")]
	public void A_base_cs_is_given_the_batch_count_in_its_effort(string batches, string effort)
	{
		var repo = PostRepo("# Takes --effort.\nprint('the base script')\n", baseCs: CsScript("the base's C#"));

		var (code, stdout) = RunStep("Post the review on the head commit", repo, extra: new() { ["BATCHES"] = batches });

		Assert.Equal(0, code);
		Assert.Equal($"the base's C# --repo o/r --pr 7 --head abc --effort high (claude-sonnet-5-5), {effort}", stdout.Trim());
	}

	[Fact]
	public void A_base_cs_without_a_global_json_fails()
	{
		var repo = PostRepo(null, baseCs: CsScript("the base's C#"), baseGlobalJson: false);

		var (code, stdout) = RunStep("Post the review on the head commit", repo);

		Assert.Equal(1, code);
		Assert.StartsWith("::error::The base has .github/scripts/post_claude_review.cs but no global.json", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void A_base_cs_that_doesnt_build_fails_and_shows_the_build()
	{
		var repo = PostRepo(null, baseCs: "#:property PublishAot=false\nthis is not C#\n");

		var (code, stdout) = RunStep("Post the review on the head commit", repo);

		Assert.Equal(1, code);
		Assert.Contains("error CS", stdout, StringComparison.Ordinal);
		Assert.EndsWith("::error::The base's .github/scripts/post_claude_review.cs didn't build, so Claude's review isn't posted.", stdout.Trim(),
			StringComparison.Ordinal);
	}

	// The build takes the base's pin: one naming an SDK that doesn't exist fails it, where a build that read no global.json would pass.
	[Fact]
	public void A_base_cs_is_built_with_the_bases_pin()
	{
		var repo = PostRepo(null, baseCs: CsScript("the base's C#"), basePin: """{"sdk": {"version": "0.0.2", "rollForward": "disable"}}""");

		var (code, stdout) = RunStep("Post the review on the head commit", repo);

		Assert.Equal(1, code);
		Assert.Contains("0.0.2", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("the base's C#", stdout, StringComparison.Ordinal);
	}

	// The step before Setup .NET hands it the base's pin, never the PR's, and only when HEAD is a merge commit and the base has a .cs and a
	// global.json to build it with: not for a plain commit, nor for a base with only the .py (the PR that brings the .cs), whose global.json
	// the post step doesn't need.
	[Theory]
	[InlineData(true, true, true, true)]
	[InlineData(false, true, true, false)]
	[InlineData(true, false, true, false)]
	[InlineData(true, true, false, false)]
	public void The_sdk_step_takes_the_bases_pin_only_for_a_base_cs(bool cs, bool merge, bool globalJson, bool needed)
	{
		var repo = PostRepo(cs ? null : "# Takes --effort.\n", merge, baseCs: cs ? CsScript("the base's C#") : null, baseGlobalJson: globalJson);
		var output = Path.Combine(temp, "output");
		File.WriteAllText(output, "");

		var (code, _) = RunStep("Take the base's SDK pin", repo, extra: new() { ["GITHUB_OUTPUT"] = output });

		Assert.Equal(0, code);
		Assert.Equal(needed ? "needed=true\n" : "", File.ReadAllText(output));
		var pin = Path.Combine(temp, "temp", "base-sdk", "global.json");
		Assert.Equal(needed, File.Exists(pin));
		if (needed)
			Assert.Equal(File.ReadAllText(Skill.Path("template", "owned", "global.json")), File.ReadAllText(pin));
	}

	// The .py's --effort check is the .py's alone: a base with the .cs runs it beside a .py from before --effort.
	[Fact]
	public void A_base_cs_runs_beside_a_py_from_before_effort()
	{
		var repo = PostRepo("print('the old base script')\n", baseCs: CsScript("the base's C#", exit: 4));

		var (code, stdout) = RunStep("Post the review on the head commit", repo);

		Assert.Equal(4, code);
		Assert.StartsWith("the base's C# --repo o/r", stdout, StringComparison.Ordinal);
	}

	// A withheld or malformed answer fails before any build, so a base .cs that doesn't build can't hide it, and nothing is built.
	[Theory]
	[InlineData("", "::error::Claude's answer was withheld for quoting a secret")]
	[InlineData("""{"malformed": "x"}""", "::error::Claude's answer didn't fit the schema")]
	public void A_withheld_answer_fails_before_the_base_cs_is_built(string findings, string error)
	{
		var repo = PostRepo(null, baseCs: "#:property PublishAot=false\nthis is not C#\n");

		var (code, stdout) = RunStep("Post the review on the head commit", repo, findings);

		Assert.Equal(1, code);
		Assert.StartsWith(error, stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("didn't build", stdout, StringComparison.Ordinal);
		Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(temp, "tmp")));
	}

	// The build gets neither the token nor the answer; the dll that posts gets both. A dotnet ahead on PATH records each build's
	// environment, then runs the real one.
	[Fact]
	[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
	public void The_build_gets_no_token_and_the_dll_gets_it()
	{
		var repo = PostRepo(null, baseCs: "#:property PublishAot=false\nSystem.Console.WriteLine(\"token \" + System.Environment.GetEnvironmentVariable(\"GH_TOKEN\")"
			+ " + \" findings \" + System.Environment.GetEnvironmentVariable(\"FINDINGS\"));\n");
		var dotnet = Run(["bash", "-c", "command -v dotnet"], temp, new() { ["PATH"] = Environment.GetEnvironmentVariable("PATH")! }).Stdout.Trim();
		var shim = Path.Combine(temp, "shim");
		Directory.CreateDirectory(shim);
		var log = Path.Combine(temp, "build-env");
		File.WriteAllText(Path.Combine(shim, "dotnet"), $"#!/usr/bin/env bash\n[[ $1 != build ]] || env > '{log}'\nexec '{dotnet}' \"$@\"\n");
		File.SetUnixFileMode(Path.Combine(shim, "dotnet"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

		var (code, stdout) = RunStep("Post the review on the head commit", repo, """{"findings": []}""", new()
		{
			["PATH"] = shim + ":" + Environment.GetEnvironmentVariable("PATH"),
			["GH_TOKEN"] = "the-token",
			["REDACTED"] = "0",
		});

		Assert.Equal(0, code);
		Assert.Equal("""token the-token findings {"findings": []}""", stdout.Trim());
		var env = File.ReadAllLines(log);
		Assert.Contains(env, line => line.StartsWith("DOTNET_NOLOGO=", StringComparison.Ordinal));
		Assert.DoesNotContain(env, line => line.StartsWith("GH_TOKEN=", StringComparison.Ordinal));
		Assert.DoesNotContain(env, line => line.StartsWith("FINDINGS=", StringComparison.Ordinal));
		Assert.DoesNotContain(env, line => line.StartsWith("REDACTED=", StringComparison.Ordinal));
		// Nor under another name.
		Assert.DoesNotContain(env, line => line.Contains("the-token", StringComparison.Ordinal));
		Assert.DoesNotContain(env, line => line.Contains("\"findings\"", StringComparison.Ordinal));
	}

	[Fact]
	public void Setup_dotnet_takes_the_pin_the_sdk_step_wrote()
	{
		var steps = Steps("post").ToList();
		var names = steps.Select(step => Py.Text(J.Req(step, "name"))).ToList();
		Assert.Equal(["Checkout the merge commit and its base", "Take the base's SDK pin", "Setup .NET", "Post the review on the head commit"], names);
		var setup = steps[2];
		Assert.StartsWith("actions/setup-dotnet@", Py.Text(J.Req(setup, "uses")), StringComparison.Ordinal);
		Assert.Equal("steps.sdk.outputs.needed == 'true'", Py.Text(J.Req(setup, "if")));
		Assert.Equal("${{ runner.temp }}/base-sdk/global.json", Py.Text(J.Req(J.Req(setup, "with"), "global-json-file")));
		Assert.Equal("sdk", Py.Text(J.Req(steps[1], "id")));
		// Time for the SDK install and a cold build as well as the post.
		Assert.True(J.Int(J.Req(Jobs["post"], "timeout-minutes"))!.Value >= 10);
	}
}
