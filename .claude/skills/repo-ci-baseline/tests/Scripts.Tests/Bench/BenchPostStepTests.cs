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
		// The build wrote nothing into the checkout.
		Assert.Equal("", Git(repo, "status", "--porcelain", "--ignored"));
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

	// The step before Setup .NET hands it the base's pin, never the PR's, and only when the base has a .cs to build with it.
	[Theory]
	[InlineData(true, true, true)]
	[InlineData(false, true, false)]
	[InlineData(true, false, false)]
	public void The_sdk_step_takes_the_bases_pin_only_for_a_base_cs(bool cs, bool merge, bool needed)
	{
		var repo = PostRepo(cs ? null : "# Takes --effort.\n", merge, baseCs: cs ? CsScript("the base's C#") : null);
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

	[Fact]
	public void Setup_dotnet_takes_the_pin_the_sdk_step_wrote()
	{
		var steps = Steps("post").ToList();
		var names = steps.Select(step => Py.Text(J.Req(step, "name"))).ToList();
		Assert.Equal(["Checkout the merge commit and its base", "Take the base's SDK pin", "Setup .NET", "Post the review on the head commit"], names);
		var setup = steps[2];
		Assert.Equal("steps.sdk.outputs.needed == 'true'", Py.Text(J.Req(setup, "if")));
		Assert.Equal("${{ runner.temp }}/base-sdk/global.json", Py.Text(J.Req(J.Req(setup, "with"), "global-json-file")));
		Assert.Equal("sdk", Py.Text(J.Req(steps[1], "id")));
	}
}
