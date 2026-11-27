using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

// bench/ReviewBench.cs's scoring, argument handling and config swap, and the Template workflow it reads. No Claude. The pytest suite
// bench/tests/test_claude_review_bench.py moved here, one method per test under its name (ADR 0017's rule, Update for
// mpaulosky/Application_Factory#119), so its count can be checked against that file's in git history; BenchPortTests.cs holds what the port adds.
public partial class BenchTests : IDisposable
{
	static readonly string Workflow = Bench.Workflow(Skill.Path("bench"));

	readonly string temp = Directory.CreateTempSubdirectory("bench-test.").FullName;

	public void Dispose()
	{
		Directory.Delete(temp, recursive: true);
		GC.SuppressFinalize(this);
	}

	static JsonObject Finding(string path, string body) => new() { ["path"] = path, ["line"] = 1, ["body"] = body };

	static bool Caught(JsonNode finding, params JsonNode[] found) => Bench.Caught(finding, found).Count > 0;

	static JsonNode Jobs => J.Req(Yaml.Load(File.ReadAllText(Workflow)), "jobs")!;

	static IEnumerable<JsonNode> Steps(string job) => J.Req(J.Req(Jobs, job), "steps")!.AsArray().Select(step => step!);

	// git in folder, hooks and signing off, as the skill's other test repos: the maintainer's global config may set them. Its stdout, trimmed.
	static string Git(string folder, params string[] args) => Run(["git", "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgsign=false",
			"-c", "user.name=t", "-c", "user.email=t@t", "-C", folder, .. args]).Stdout.Trim();

	// One run of argv, which must exit 0 unless allowed to fail: (exit code, stdout). Its stdin is empty: bash reads ~/.bashrc even for -c when
	// its stdin is a socket (as a remote shell daemon's), which the test host's can be, so a step would run the maintainer's own startup files.
	static (int Code, string Stdout) Run(IReadOnlyList<string> argv, string? cwd = null, Dictionary<string, string>? env = null, bool mayFail = false)
	{
		var start = new ProcessStartInfo(argv[0])
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		if (cwd is not null)
			start.WorkingDirectory = cwd;
		if (env is not null)
		{
			start.Environment.Clear();
			foreach (var (key, value) in env)
				start.Environment[key] = value;
		}
		foreach (var arg in argv.Skip(1))
			start.ArgumentList.Add(arg);
		using var run = Process.Start(start)!;
		run.StandardInput.Close();
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = run.StandardOutput.ReadToEnd();
		run.WaitForExit();
		Assert.True(mayFail || run.ExitCode == 0, $"{string.Join(" ", argv)} failed: {err.Result}");
		return (run.ExitCode, output);
	}

	// -- Caught --

	static readonly JsonNode Tab = JsonNode.Parse("""{"paths": ["release_post.py"], "match": [["tab", "tabs", "\\t"]]}""")!;

	[Fact]
	public void A_word_matches_whole_not_inside_another()
	{
		Assert.True(Caught(Tab, Finding(".github/scripts/release_post.py", "A leading tab survives.")));
		Assert.False(Caught(Tab, Finding(".github/scripts/release_post.py", "The table is stable.")));
	}

	[Fact]
	public void Every_group_must_match_and_any_word_in_a_group_does()
	{
		var spec = JsonNode.Parse("""{"match": [["skip", "skipped"], ["label", "rerun"]]}""")!;
		Assert.True(Caught(spec, Finding("a.yml", "A skipped run after another label.")));
		Assert.False(Caught(spec, Finding("a.yml", "A skipped run.")));
	}

	[Fact]
	public void A_phrase_matches_as_a_whole()
	{
		var spec = JsonNode.Parse("""{"match": [["copilot"], ["without a [bot]"]]}""")!;
		Assert.True(Caught(spec, Finding("a.yml", "Copilot acts without a [bot] suffix.")));
		Assert.False(Caught(spec, Finding("a.yml", "Copilot skips every [bot] actor.")));
	}

	[Fact]
	public void A_finding_on_another_file_never_counts() =>
			Assert.False(Caught(Tab, Finding(".github/workflows/ci.yml", "A leading tab survives.")));

	[Fact]
	public void A_path_matches_whole_components_only()
	{
		Assert.True(Caught(Tab, Finding("release_post.py", "A leading tab survives.")));
		Assert.False(Caught(Tab, Finding(".github/scripts/tests/test_release_post.py", "A leading tab survives.")));
	}

	[Fact]
	public void Every_cases_file_finding_has_paths_and_match()
	{
		var spec = JsonNode.Parse(File.ReadAllText(Skill.Path("bench", "claude-review-cases.json")))!;
		var findings = J.Req(spec, "findings")!.AsObject();
		foreach (var (key, entry) in findings)
			Assert.True(J.B(J.Get(entry, "paths")) && J.B(J.Get(entry, "match")), key);
		foreach (var @case in J.Req(spec, "cases")!.AsArray())
			Assert.True(J.Req(@case, "expect")!.AsArray().All(key => findings.ContainsKey(J.S(key)!)), J.S(J.Req(@case, "head")));
	}

	// -- Override --

	static List<string> Args => Bench.ShlexSplit("""--model m --effort medium --allowedTools "Read,Glob,Grep" --disallowedTools "Bash,WebFetch,WebSearch" """);

	[Fact]
	public void An_override_replaces_the_flags_value()
	{
		var args = Bench.Override(Args, "high", "opus", null);
		Assert.Equal("high", args[args.IndexOf("--effort") + 1]);
		Assert.Equal("opus", args[args.IndexOf("--model") + 1]);
	}

	[Fact]
	public void An_override_adds_a_flag_that_was_missing()
	{
		var args = Bench.Override(["--model", "m"], "high", null, null);
		Assert.Equal(["--effort", "high"], args[^2..]);
	}

	[Fact]
	public void Setting_sources_can_be_emptied() =>
			Assert.Equal(["--setting-sources", ""], Bench.Override(["--setting-sources", "user"], null, null, null, settingSources: ""));

	[Fact]
	public void Allowed_tools_are_dropped_from_the_disallowed_list()
	{
		var args = Bench.Override(Args, null, null, "Read,Glob,Grep,WebFetch");
		Assert.Equal("Read,Glob,Grep,WebFetch", args[args.IndexOf("--allowedTools") + 1]);
		Assert.Equal("Bash,WebSearch", args[args.IndexOf("--disallowedTools") + 1]);
	}

	// -- Render --

	[Fact]
	public void Render_fills_in_the_workflow_expressions()
	{
		const string text = "PR #${{ github.event.pull_request.number }}, diff in ${{ runner.temp }}/pr.diff, ${{ github.workspace }}/../../_actions";
		Assert.Equal("PR #7, diff in /tmp/t/pr.diff, /tmp/t/work/repo/repo/../../_actions", Bench.Render(text, "7", "/tmp/t"));
	}

	[Fact]
	public void Render_refuses_an_expression_it_doesnt_know() =>
			Assert.Throws<BenchExit>(() => Bench.Render("${{ secrets.TOKEN }}", "7", "/tmp/t"));

	// -- PinnedActions --

	[Fact]
	public void The_review_jobs_remote_actions_are_found_once_each()
	{
		const string workflow = """

			jobs:
			  review:
			    steps:
			      - uses: actions/checkout@abc123
			      - run: echo hi
			      - uses: anthropics/claude-code-action@def456
			      - uses: actions/checkout@abc123
			      - uses: ./.github/actions/local
			      - uses: owner/repo/sub/path@v1
			  post:
			    steps:
			      - uses: actions/setup-python@v6
			""";
		Assert.Equal([("actions", "checkout", "abc123"), ("anthropics", "claude-code-action", "def456"), ("owner", "repo", "v1")], Bench.PinnedActions(workflow));
	}

	[Fact]
	public void A_head_without_a_review_job_pins_nothing()
	{
		Assert.Empty(Bench.PinnedActions(""));
		Assert.Empty(Bench.PinnedActions("jobs: {build: {steps: []}}"));
	}

	// -- UseBaseSettings --

	[Fact]
	public void The_heads_claude_config_is_replaced_by_the_bases()
	{
		Git(temp, "init", "-q");
		File.WriteAllText(Path.Combine(temp, "CLAUDE.md"), "base instructions\n");
		Git(temp, "add", "-A");
		Git(temp, "commit", "-q", "-m", "base");
		var @base = Git(temp, "rev-parse", "HEAD");
		File.WriteAllText(Path.Combine(temp, "CLAUDE.md"), "head instructions\n");
		File.WriteAllText(Path.Combine(temp, ".mcp.json"), "{\"mcpServers\": {\"x\": {\"command\": \"evil\"}}}\n");
		Directory.CreateDirectory(Path.Combine(temp, ".claude"));
		File.WriteAllText(Path.Combine(temp, ".claude", "settings.json"), "{\"hooks\": {}}\n");

		Bench.UseBaseSettings(temp, @base);

		Assert.Equal("base instructions\n", File.ReadAllText(Path.Combine(temp, "CLAUDE.md")));
		Assert.False(File.Exists(Path.Combine(temp, ".mcp.json")));
		Assert.False(Directory.Exists(Path.Combine(temp, ".claude")));
	}

	// -- ReviewStep, against the real Template workflow --

	[Fact]
	public void The_benchmark_can_read_the_template_workflows_review_step()
	{
		var (prompt, claudeArgs) = Bench.ReviewStep(Workflow);
		var rendered = Bench.ShlexSplit(Bench.Render(claudeArgs, "7", "/tmp/t"));
		Assert.Contains("#7", Bench.Render(prompt, "7", "/tmp/t", pieces: "- /tmp/t/claude-review/diff/001.diff (3 lines: source)",
				context: "- /tmp/t/claude-review/context/001.txt (9 lines)"), StringComparison.Ordinal);
		// The Review step's env: batch 1's pieces in place, and its note's line gone, since a case reviews one batch alone.
		const string pieces = "- /tmp/t/claude-review/diff/001.diff (3 lines: source)";
		Assert.Equal(1, Count(prompt, "${{ env.REVIEW_PIECES }}"));
		Assert.Equal(1, Count(prompt, "${{ env.REVIEW_BATCH }}\n"));
		var alone = Bench.Render(prompt, "7", "/tmp/t", pieces, context: "-");
		Assert.Contains(pieces, alone, StringComparison.Ordinal);
		Assert.DoesNotContain("REVIEW_", alone, StringComparison.Ordinal);
		Assert.DoesNotContain("${{", alone, StringComparison.Ordinal);
		Assert.Equal(Count(prompt, "\n") - 1 + Count(pieces, "\n"), Count(alone, "\n"));
		Assert.Throws<BenchExit>(() => Bench.Render(prompt, "7", "/tmp/t"));  // the diff step's pieces must be filled in
		Assert.Throws<BenchExit>(() => Bench.Render(prompt, "7", "/tmp/t", pieces: "-"));  // and its context pieces
		foreach (var flag in new[] { "--model", "--effort", "--json-schema" })
			Assert.True(rendered.Contains(flag), flag);
		// The token's guards (mpaulosky/Application_Factory#121): no tool granted outright, so reads stay in the checkout and the --add-dir
		// directories; no repo settings; /proc denied.
		Assert.DoesNotContain("--allowedTools", rendered);
		Assert.DoesNotContain("--allowed-tools", rendered);
		Assert.Equal("user", rendered[rendered.IndexOf("--setting-sources") + 1]);
		var added = rendered.Select((arg, i) => (arg, i)).Where(pair => pair.arg == "--add-dir").Select(pair => rendered[pair.i + 1]).ToList();
		Assert.Equal(["/tmp/t/claude-review", "/tmp/t/work/repo/repo/../../_actions"], added);
		var disallowed = rendered[rendered.IndexOf("--disallowedTools") + 1].Split(',');
		Assert.Contains("Read(//proc/**)", disallowed);
		// The action writes the job's GITHUB_TOKEN into the checkout's .git/config.
		Assert.Contains("Read(**/.git/**)", disallowed);
		Assert.Subset(disallowed.ToHashSet(), new HashSet<string> { "Bash", "Edit", "Write", "WebFetch", "WebSearch" });
	}

	static int Count(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

	[Fact]
	public void A_failed_or_timed_out_review_doesnt_fail_the_check()
	{
		// mpaulosky/Application_Factory#123: a failed check leaves the PR UNSTABLE, which PR Auto-Merge never merges.
		var jobs = Jobs;
		var review = J.Req(jobs, "review")!;
		var steps = Steps("review").Where(step => Py.Str(Py.Get(step, "id", "")).StartsWith("claude_", StringComparison.Ordinal)).ToList();
		Assert.True(steps.Count > 1);
		Assert.All(steps, step => Assert.Equal(JsonValueKind.True, J.Req(step, "continue-on-error")?.GetValueKind()));
		// The job outlasts every batch's review.
		Assert.True(steps.Sum(step => J.Int(J.Req(step, "timeout-minutes"))!.Value) < J.Int(J.Req(review, "timeout-minutes")));
		// Gated on a flag that can't hold a secret, not on findings, which GitHub empties when they quote one: the post job then fails on them.
		var postIf = Py.Text(J.Req(J.Req(jobs, "post"), "if"));
		Assert.Contains("needs.review.outputs.reviewed == 'true'", postIf, StringComparison.Ordinal);
		Assert.DoesNotContain("findings", postIf, StringComparison.Ordinal);
		var outputs = J.Req(review, "outputs");
		Assert.Equal("${{ " + string.Join(" || ", steps.Select(step => $"steps.{Py.Text(J.Req(step, "id"))}.outputs.structured_output != ''")) + " }}",
				Py.Text(J.Req(outputs, "reviewed")));
		// Findings come from the answer check, never straight from Claude's step, whose value the post job's log would print.
		Assert.Equal("${{ steps.answer.outputs.findings }}", Py.Text(J.Req(outputs, "findings")));
		var answer = Steps("review").First(step => Py.Eq(Py.Get(step, "id"), "answer"));
		// Nothing publishes Claude's transcript before that check runs.
		foreach (var step in steps)
		{
			var with = J.Req(step, "with");
			Assert.Equal(JsonValueKind.False, J.Req(with, "display_report")?.GetValueKind());
			Assert.Equal(JsonValueKind.False, J.Req(with, "show_full_output")?.GetValueKind());
			// Nor does a debug run, where the action shows the full output anyway.
			Assert.Contains("runner.debug != '1'", Py.Text(J.Req(step, "if")), StringComparison.Ordinal);
			Assert.True(Py.Eq(J.Req(J.Req(step, "env"), "ACTIONS_STEP_DEBUG"), "false"));
		}
		// The check reads the answer from the execution file: its env takes only whether each batch answered.
		var env = Py.Str(Py.Get(answer, "env"));
		Assert.DoesNotContain("structured_output", System.Text.RegularExpressions.Regex.Replace(env, @"steps\.claude_\d\.outputs\.structured_output != ''", ""),
				StringComparison.Ordinal);
		// The check holds the OAuth token, so it runs inline from the workflow, never from a script the PR could change.
		var answerRun = Py.Text(J.Req(answer, "run"));
		Assert.Contains("python3 -I - <<'PY'", answerRun, StringComparison.Ordinal);
		Assert.DoesNotContain(".github/scripts", answerRun, StringComparison.Ordinal);
		// The post job, which can post under the marker, runs the base's script.
		var post = Steps("post").First(step => Py.Eq(Py.Get(step, "name"), "Post the review on the head commit"));
		var postRun = Py.Text(J.Req(post, "run"));
		Assert.Contains("git show \"HEAD^1:.github/scripts/post_claude_review.py\"", postRun, StringComparison.Ordinal);
		Assert.Contains("python3 -I \"$script\"", postRun, StringComparison.Ordinal);
		Assert.DoesNotContain("python3 .github", postRun, StringComparison.Ordinal);
		var checkout = Steps("post").First(step => Py.Str(Py.Get(step, "uses", "")).Contains("checkout", StringComparison.Ordinal));
		Assert.True(Py.Eq(J.Req(J.Req(checkout, "with"), "fetch-depth"), 2));
		Assert.False(J.Has(J.Req(checkout, "with"), "ref"));
		// And Claude may answer only in the fields that get posted.
		var claudeArgs = Py.Text(J.Req(J.Req(steps[^1], "with"), "claude_args"));
		Assert.Equal(3, Count(claudeArgs, "\"additionalProperties\": false"));
	}

	// -- BadLines (mpaulosky/Application_Factory#122) --

	[Fact]
	public void A_line_past_the_end_or_a_missing_file_is_bad()
	{
		Git(temp, "init", "-q");
		File.WriteAllText(Path.Combine(temp, "three.md"), "a\nb\nc\n");
		File.WriteAllText(Path.Combine(temp, "no-newline.md"), "a\nb");
		Git(temp, "add", "-A");
		Git(temp, "commit", "-q", "-m", "c");
		var findings = JsonNode.Parse("""
			[{"path": "three.md", "line": 3}, {"path": "three.md", "line": 4}, {"path": "three.md", "line": 0},
			 {"path": "no-newline.md", "line": 2}, {"path": "gone.md", "line": 1}]
			""")!.AsArray();

		Assert.Equal("""["three.md:4","three.md:0","gone.md:1"]""", Bench.BadLines(temp, "HEAD", findings)!.ToJsonString());
	}

	[Fact]
	public void Bad_lines_is_none_without_the_clone() =>
			Assert.Null(Bench.BadLines(Path.Combine(temp, "missing"), "HEAD", [new JsonObject { ["path"] = "a", ["line"] = 1 }]));

	// -- The post step takes the script from the merge commit's first parent (mpaulosky/Application_Factory#134) --

	// A repo whose HEAD is a merge commit (a plain commit when merge is false) whose base has baseScript as its post_claude_review.py
	// (none when null), baseCs as its post_claude_review.cs (none when null) and the Template's global.json (basePin instead, when given;
	// none when baseGlobalJson is false). The PR, whatever the base has, changes the .py to one that must never run and brings a .cs of its
	// own, a global.json pinning an SDK that doesn't exist and a Directory.Build.props that fails any build: a step that ran the PR's script,
	// took its .cs (as the PR that brings the .cs to a .py base does), or built in its tree or with its pin, would print or fail.
	string PostRepo(string? baseScript, bool merge = true, string? baseCs = null, bool baseGlobalJson = true, string? basePin = null)
	{
		var repo = Path.Combine(temp, "repo");
		Directory.CreateDirectory(repo);
		Git(repo, "init", "-q", "-b", "main");
		var scripts = Path.Combine(repo, ".github", "scripts");
		Directory.CreateDirectory(scripts);
		File.WriteAllText(Path.Combine(repo, "README.md"), "base\n");
		if (baseScript is not null)
			File.WriteAllText(Path.Combine(scripts, "post_claude_review.py"), baseScript);
		if (baseCs is not null)
			File.WriteAllText(Path.Combine(scripts, "post_claude_review.cs"), baseCs);
		if (baseGlobalJson)
			File.WriteAllText(Path.Combine(repo, "global.json"), basePin ?? File.ReadAllText(Skill.Path("template", "owned", "global.json")));
		Git(repo, "add", "-A");
		Git(repo, "commit", "-q", "-m", "base");
		Git(repo, "checkout", "-q", "-b", "pr");
		// The PR's own copies, which the step must never run or build with.
		File.WriteAllText(Path.Combine(scripts, "post_claude_review.py"), "import sys\nprint('the PR script')\nsys.exit(3)\n");
		File.WriteAllText(Path.Combine(scripts, "post_claude_review.cs"), CsScript("the PR's C#", exit: 3));
		File.WriteAllText(Path.Combine(repo, "global.json"), """{"sdk": {"version": "0.0.1", "rollForward": "disable"}}""");
		File.WriteAllText(Path.Combine(repo, "Directory.Build.props"),
			"""<Project><Target Name="PrTree" BeforeTargets="Build"><Error Text="the build read the PR's tree" /></Target></Project>""");
		Git(repo, "add", "-A");
		Git(repo, "commit", "-q", "-m", "pr");
		if (merge)
		{
			Git(repo, "checkout", "-q", "main");
			Git(repo, "merge", "-q", "--no-ff", "-m", "merge", "pr");
		}
		return repo;
	}

	// The post job's step named name, run in repo as the runner would, with the post step's environment and extra on top.
	(int Code, string Stdout) RunStep(string name, string repo, string findings = """{"summary": "s", "findings": []}""",
		Dictionary<string, string>? extra = null)
	{
		var run = Py.Text(J.Req(Steps("post").First(step => Py.Eq(Py.Get(step, "name"), name)), "run"));
		var runnerTemp = Path.Combine(temp, "temp");
		Directory.CreateDirectory(runnerTemp);
		// The step's mktemp -d build folder lands here, so Dispose removes it (the step ends in exec, and a runner is thrown away).
		var tmp = Path.Combine(temp, "tmp");
		Directory.CreateDirectory(tmp);
		// dotnet stops when XDG_DATA_HOME names a folder that isn't there.
		var data = Path.Combine(temp, "data");
		Directory.CreateDirectory(data);
		Dictionary<string, string> env = new()
		{
			["PATH"] = Environment.GetEnvironmentVariable("PATH")!,
			// For dotnet's own folders (its NuGet cache, its first-run marker), as on a runner.
			["HOME"] = Environment.GetEnvironmentVariable("HOME")!,
			["RUNNER_TEMP"] = runnerTemp,
			["TMPDIR"] = tmp,
			// And a file-based app's build folder (dotnet/runfile/, named by the file's path, new on every run), which on Linux goes under
			// XDG_DATA_HOME, not TMPDIR: under the host's ~/.local/share, each build would leave one there.
			["XDG_DATA_HOME"] = data,
			// The step's git calls read no git config of the host's.
			["GIT_CONFIG_GLOBAL"] = "/dev/null",
			["GIT_CONFIG_NOSYSTEM"] = "1",
			["REPOSITORY"] = "o/r",
			["PR_NUMBER"] = "7",
			["HEAD_SHA"] = "abc",
			["FINDINGS"] = findings,
			["REVIEW_EFFORT"] = "high (claude-sonnet-5-5)",
		};
		foreach (var (key, value) in extra ?? [])
			env[key] = value;
		return Run(["bash", "-e", "-c", run], repo, env, mayFail: true);
	}

	(int Code, string Stdout) RunPostStep(string? baseScript, string findings = """{"summary": "s", "findings": []}""", bool merge = true) =>
		RunStep("Post the review on the head commit", PostRepo(baseScript, merge), findings);

	[Fact]
	public void The_post_step_runs_the_bases_script_not_the_prs()
	{
		// A script that takes --effort: the step passes one an older script would refuse (mpaulosky/Application_Factory#202).
		// The PR brings a .cs, as the PR that brings the .cs to a .py base does: the base's .py still runs, not the PR's C#.
		var (code, stdout) = RunPostStep("# Takes --effort.\nimport sys\nprint('the base script', *sys.argv[1:])\n");

		Assert.Equal(0, code);
		Assert.Equal("the base script --repo o/r --pr 7 --head abc --effort high (claude-sonnet-5-5), 1 batch", stdout.Trim());
	}

	[Fact]
	public void A_base_without_the_script_warns_and_posts_nothing()
	{
		var (code, stdout) = RunPostStep(null);

		Assert.Equal(0, code);
		Assert.StartsWith("::warning::The base has no .github/scripts/post_claude_review.cs or .py yet", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("the PR script", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void A_base_without_the_script_still_fails_a_withheld_answer()
	{
		var (code, stdout) = RunPostStep(null, findings: "");

		Assert.Equal(1, code);
		Assert.Contains("rotate CLAUDE_CODE_OAUTH_TOKEN", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void A_checkout_that_isnt_a_merge_commit_fails()
	{
		var (code, stdout) = RunPostStep("print('the base script')\n", merge: false);

		Assert.Equal(1, code);
		Assert.StartsWith("::error::The checkout isn't the PR's merge commit", stdout, StringComparison.Ordinal);
	}

	// -- DiffStep and SplitDiff, against the real Template workflow --

	[Fact]
	public void The_benchmark_splits_the_diff_with_the_workflows_own_step()
	{
		Directory.CreateDirectory(Path.Combine(temp, "claude-review"));
		var diff = string.Concat(Enumerable.Range(0, 3).Select(i => $"diff --git a/f{i}.py b/f{i}.py\n--- a/f{i}.py\n+++ b/f{i}.py\n@@ -1 +1 @@\n-old\n+new\n"));
		File.WriteAllText(Path.Combine(temp, "claude-review", "pr.diff"), diff);
		// Not a git repo, so there are no files to read whole.
		var (pieces, context) = Bench.SplitDiff(Bench.DiffStep(Workflow), temp, temp, "base", "head");
		var folder = Path.Combine(temp, "claude-review", "diff");
		Assert.Equal($"- {folder}/001.diff (18 lines: source)", pieces);
		Assert.Equal("(none)", context);
		Assert.Equal(diff, File.ReadAllText(Path.Combine(folder, "001.diff")));
		Assert.True(File.Exists(Path.Combine(folder, "INDEX")));
	}

	// -- Transcript --

	[Fact]
	public void The_transcript_gives_the_result_and_each_turns_reads()
	{
		string[] lines =
		[
			"""{"type": "system", "subtype": "init"}""",
			"""{"type": "assistant", "message": {"id": "m1", "content": [{"type": "tool_use", "name": "Read", "input": {"file_path": "/t/diff/INDEX"}}]}}""",
			"""{"type": "assistant", "message": {"id": "m1", "content": [{"type": "tool_use", "name": "Read", "input": {"file_path": "/t/diff/0001.diff"}}]}}""",
			"""{"type": "user", "message": {"content": []}}""",
			"""{"type": "assistant", "message": {"id": "m2", "content": [{"type": "text", "text": "done"}]}}""",
			"""{"type": "result", "num_turns": 6, "structured_output": {"summary": "s", "findings": []}}""",
		];
		var (result, reads) = Bench.Transcript(string.Join("\n", lines) + "\n");
		Assert.Equal(6, J.Int(J.Req(result, "num_turns")));
		Assert.Equal("""[["/t/diff/INDEX","/t/diff/0001.diff"]]""", reads.ToJsonString());
	}

	[Fact]
	public void A_transcript_without_a_result_is_an_error() =>
			Assert.Throws<JsonException>(() => Bench.Transcript("{\"type\": \"system\"}\n"));
}
