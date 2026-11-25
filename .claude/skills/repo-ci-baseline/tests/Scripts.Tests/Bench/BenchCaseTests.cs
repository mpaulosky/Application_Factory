using System.Text;
using System.Text.Json.Nodes;

// The run mode whole (mpaulosky/Application_Factory#196): Bench.Run over fixture clones with a fake claude on PATH, which records what it was
// given; and CopyActions over a local remote, with a git on PATH that records or stalls. No Claude and no network.
public sealed partial class BenchPathTests
{
	// The git PATH had before a test put its own first.
	static readonly string RealGit = Gh.Which("git", Environment.GetEnvironmentVariable("PATH"));

	// git in folder, hooks and signing off, with a fixed author: its stdout, trimmed.
	static string Git(string folder, params string[] args) => Bench.Check(["git", "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgsign=false",
			"-c", "user.name=t", "-c", "user.email=t@t", "-C", folder, .. args]).Text.Trim();

	static void Write(string folder, string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, path))!);
		File.WriteAllText(Path.Combine(folder, path), text);
	}

	// A clone under root/name with a base commit and a head on it, the head writing files over the base's: (base, head).
	static (string Base, string Head) Clone(string root, string name, params (string Path, string Text)[] files)
	{
		var repo = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
		Git(repo, "init", "-q");
		Write(repo, "f.py", "old\n");
		Write(repo, "CLAUDE.md", "base instructions\n");
		Git(repo, "add", "-A");
		Git(repo, "commit", "-q", "-m", "base");
		var @base = Git(repo, "rev-parse", "HEAD");
		Write(repo, "f.py", "new\n");
		Write(repo, "head.txt", name + "\n");
		foreach (var (path, text) in files)
			Write(repo, path, text);
		Git(repo, "add", "-A");
		Git(repo, "commit", "-q", "-m", "head");
		return (@base, Git(repo, "rev-parse", "HEAD"));
	}

	// A claude that answers --version, and otherwise records its argv, its folder, the Claude Code config it could load there, its CLAUDE.md
	// and the head it runs on under calls/<n>, sleeps when the tree holds a file called hang, and gives a review of one finding.
	[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
	string FakeClaude()
	{
		var calls = Directory.CreateDirectory(Path.Combine(temp, "calls")).FullName;
		OnPath("claude", $$$"""
			#!/bin/sh
			if [ "$1" = --version ]; then echo '9.9.9 (Claude Code)'; exit 0; fi
			log='{{{calls}}}'/$(ls '{{{calls}}}' | wc -l | tr -d ' ')
			mkdir "$log"
			pwd -P > "$log/cwd"
			for arg in "$@"; do printf '%s\0' "$arg"; done > "$log/argv"
			find . -path ./.git -prune -o \( -iname CLAUDE.md -o -iname CLAUDE.local.md -o -iname .claude \) -print | sort > "$log/config"
			cat CLAUDE.md > "$log/instructions"
			cat head.txt > "$log/head"
			if [ -e hang ]; then sleep 30; fi
			echo '{"type": "result", "num_turns": 2, "total_cost_usd": 0.25, "structured_output": {"summary": "s", "findings": [{"path": "f.py", "line": 1, "body": "a word"}]}}'
			""");
		return calls;
	}

	// What call n of the fake claude recorded.
	string Call(int n, string what) => File.ReadAllText(Path.Combine(temp, "calls", n.ToString(System.Globalization.CultureInfo.InvariantCulture), what));

	// The bench's folder for a run over cases: the cases file, and the Template's workflow where the bench finds it.
	string Here(JsonArray cases)
	{
		var here = Directory.CreateDirectory(Path.Combine(temp, "bench")).FullName;
		var spec = new JsonObject
		{
			["about"] = "fixture",
			["findings"] = new JsonObject { ["w"] = new JsonObject { ["about"] = "a word", ["paths"] = new JsonArray("f.py"), ["match"] = new JsonArray(new JsonArray("word")) } },
			["cases"] = cases,
		};
		File.WriteAllText(Path.Combine(here, "claude-review-cases.json"), spec.ToJsonString());
		var workflows = Directory.CreateDirectory(Path.Combine(temp, "template", "owned", ".github", "workflows")).FullName;
		File.Copy(Bench.Workflow(Skill.Path("bench")), Path.Combine(workflows, "claude-review.yml"), overwrite: true);
		return here;
	}

	static JsonObject Case(string repo, int pr, string @base, string head, string? set = null)
	{
		JsonObject @case = new() { ["repo"] = repo, ["pr"] = pr, ["base"] = @base, ["head"] = head, ["expect"] = new JsonArray("w") };
		if (set is not null)
			@case["set"] = set;
		return @case;
	}

	// One run of the bench: (exit code, stdout, stderr, the results file it wrote, parsed).
	static (int Code, string Output, string Err, JsonNode? Results) Bench_(string here, params string[] args)
	{
		var (output, err) = (new StringWriter(), new StringWriter());
		var code = Bench.Run(args, output, err, here);
		var results = Path.Combine(here, "results");
		var files = Directory.Exists(results) ? Directory.GetFiles(results) : [];
		Assert.True(files.Length <= 1, string.Join(", ", files));
		return (code, output.ToString(), err.ToString(), files.Length == 1 ? JsonNode.Parse(File.ReadAllText(files[0])) : null);
	}

	[Fact]
	public void A_run_reviews_each_case_in_its_own_tree_and_writes_the_results()
	{
		if (OperatingSystem.IsWindows())
			return;
		FakeClaude();
		var root = Path.Combine(temp, "root");
		var one = Clone(root, "one", ("sub/CLAUDE.md", "nested\n"), ("sub/.claude/settings.json", "{}\n"), ("deep/x/claude.local.md", "nested\n"),
				("CLAUDE.md", "head instructions\n"));
		var here = Here([Case("one", 7, one.Base, one.Head)]);

		var (code, output, err, results) = Bench_(here, "--root", root, "--label", "fixture", "--effort", "low");

		Assert.Equal(0, code);
		Assert.Equal("", err);
		Assert.Contains("caught w: a word", output, StringComparison.Ordinal);
		Assert.Contains("\nscore: 1/1 required findings caught\n", output, StringComparison.Ordinal);
		Assert.Equal(["label", "claude_version", "claude_args", "overrides", "prompt", "score", "cases"], results!.AsObject().Select(p => p.Key));
		Assert.Equal("fixture", J.S(results["label"]));
		Assert.Equal("9.9.9 (Claude Code)", J.S(results["claude_version"]));
		var (prompt, claudeArgs) = Bench.ReviewStep(Bench.Workflow(here));
		Assert.Equal(claudeArgs, J.S(results["claude_args"]));
		Assert.Equal(prompt, J.S(results["prompt"]));
		Assert.Equal("""["low",null,null]""", results["overrides"]!.ToJsonString());
		Assert.Equal("[1,1]", results["score"]!.ToJsonString());
		var entry = results["cases"]!.AsArray().Single()!;
		Assert.Equal(["repo", "pr", "base", "head", "expect", "review", "seconds", "cost_usd", "turns", "reads", "bad_lines", "caught"], entry.AsObject().Select(p => p.Key));
		Assert.Equal(one.Head, J.S(entry["head"]));
		Assert.Equal("""{"summary":"s","findings":[{"path":"f.py","line":1,"body":"a word"}]}""", entry["review"]!.ToJsonString());
		Assert.Equal("0.25", entry["cost_usd"]!.ToJsonString());
		Assert.Equal("2", entry["turns"]!.ToJsonString());
		Assert.Equal("[]", entry["reads"]!.ToJsonString());
		Assert.Equal("[]", entry["bad_lines"]!.ToJsonString());
		Assert.Equal("""{"w":true}""", entry["caught"]!.ToJsonString());

		// claude ran in the case's worktree of the head, in a temporary folder of its own, with the workflow's prompt and arguments.
		// The fake records its folder with pwd -P, symlinks resolved, while the prompt names the case's folder as the bench made it, under a
		// TMPDIR that may be a link (macOS's /var/folders is /private/var/folders): so the prompt's folder is the one the rest compares with.
		var cwd = Call(0, "cwd").TrimEnd('\n');
		Assert.Equal("tree", Path.GetFileName(cwd));
		Assert.Equal("one\n", Call(0, "head"));
		var argv = Call(0, "argv").Split('\0')[..^1];
		Assert.Equal("-p", argv[0]);
		var named = System.Text.RegularExpressions.Regex.Match(argv[1], @"(/\S+)/claude-review/diff/001\.diff");
		Assert.True(named.Success, "the prompt names no diff file");
		var caseTemp = named.Groups[1].Value;
		// The case's folder is removed by now, so its parent, which stays, is the part resolved.
		var resolved = Path.Combine(Bench.Check(["realpath", Path.GetDirectoryName(caseTemp)!]).Text.Trim(), Path.GetFileName(caseTemp));
		Assert.Equal(Path.GetDirectoryName(cwd), resolved);
		Assert.DoesNotContain("${{", argv[1], StringComparison.Ordinal);
		var args = Bench.Override(Bench.Override(Bench.ShlexSplit(Bench.Render(claudeArgs, "7", caseTemp)), "low", null, null), null, null, null, settingSources: "");
		Assert.Equal([.. args, "--output-format", "stream-json", "--verbose", "--strict-mcp-config", "--no-session-persistence"], argv[2..]);

		// The base's root CLAUDE.md, and no nested config from the head.
		Assert.Equal("./CLAUDE.md\n", Call(0, "config"));
		Assert.Equal("base instructions\n", Call(0, "instructions"));

		// The worktree and the temporary folder are gone.
		Assert.False(Directory.Exists(caseTemp));
		Assert.Single(Git(Path.Combine(root, "one"), "worktree", "list", "--porcelain").Split('\n'), line => line.StartsWith("worktree ", StringComparison.Ordinal));
	}

	[Fact]
	public void A_case_that_times_out_or_has_no_clone_is_an_error_and_the_run_goes_on()
	{
		if (OperatingSystem.IsWindows())
			return;
		FakeClaude();
		var root = Path.Combine(temp, "root");
		var hangs = Clone(root, "hangs", ("hang", ""));
		var one = Clone(root, "one");
		var here = Here([Case("missing", 1, "aaaaaaa", "bbbbbbb"), Case("hangs", 2, hangs.Base, hangs.Head), Case("one", 3, one.Base, one.Head)]);
		var started = System.Diagnostics.Stopwatch.StartNew();

		var (code, output, _, results) = Bench_(here, "--root", root, "--timeout", "5");

		Assert.Equal(0, code);
		Assert.True(started.Elapsed < TimeSpan.FromSeconds(25), $"took {started.Elapsed}");
		var cases = results!["cases"]!.AsArray();
		Assert.Equal(["bbbbbbb", hangs.Head, one.Head], cases.Select(c => J.S(c!["head"])));
		var missing = J.S(cases[0]!["error"])!;
		Assert.StartsWith($"Command ['git', '-c', 'core.hooksPath=/dev/null', '-C', '{root}/missing', 'worktree', 'add', ", missing, StringComparison.Ordinal);
		Assert.Contains("returned non-zero exit status", missing, StringComparison.Ordinal);
		Assert.Equal("timed out after 5s", J.S(cases[1]!["error"]));
		Assert.False(J.Has(cases[0], "review") || J.Has(cases[1], "review"));
		Assert.Equal("""{"w":false}""", cases[1]!["caught"]!.ToJsonString());
		Assert.True(J.Has(cases[2], "review"));
		Assert.Equal("[1,3]", results["score"]!.ToJsonString());
		Assert.Contains("   error: timed out after 5s\n", output, StringComparison.Ordinal);

		// Each case's worktree and temporary folder are removed, the timed-out one's too.
		foreach (var call in new[] { 0, 1 })
			Assert.False(Directory.Exists(Path.GetDirectoryName(Call(call, "cwd").TrimEnd('\n'))));
		Assert.Equal(["hangs\n", "one\n"], new[] { Call(0, "head"), Call(1, "head") });
		foreach (var repo in new[] { "hangs", "one" })
			Assert.Single(Git(Path.Combine(root, repo), "worktree", "list", "--porcelain").Split('\n'), line => line.StartsWith("worktree ", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("--set", "noise", new[] { "noisy" })]
	[InlineData("--set", "recall", new[] { "one", "other" })]
	[InlineData("--only", "", new[] { "noisy" })]
	public void Set_picks_the_cases_of_a_set_and_only_one_case_in_either(string flag, string value, string[] repos)
	{
		if (OperatingSystem.IsWindows())
			return;
		FakeClaude();
		var root = Path.Combine(temp, "root");
		var one = Clone(root, "one");
		var other = Clone(root, "other");
		var noisy = Clone(root, "noisy");
		var here = Here([Case("one", 1, one.Base, one.Head), Case("other", 2, other.Base, other.Head, "recall"), Case("noisy", 3, noisy.Base, noisy.Head, "noise")]);

		// --only takes a prefix of the head, and finds a case outside the recall set.
		var (code, _, _, results) = Bench_(here, "--root", root, flag, flag == "--only" ? noisy.Head[..7] : value);

		Assert.Equal(0, code);
		Assert.Equal(repos, results!["cases"]!.AsArray().Select(c => J.S(c!["repo"])));
	}

	[Fact]
	public void A_run_without_claude_on_path_ends_with_exit_1_and_says_so()
	{
		var here = Here([Case("one", 1, "a", "b")]);
		Environment.SetEnvironmentVariable("PATH", Directory.CreateDirectory(Path.Combine(temp, "empty")).FullName);

		var (code, output, err, results) = Bench_(here);

		Assert.Equal(1, code);
		Assert.Equal("", output);
		Assert.Equal("claude-review-bench.cs: claude isn't on PATH\n", err);
		Assert.Null(results);
	}

	// -- CopyActions --

	// A head whose review job pins owner/act@v1, and a remote holding that action: (tree, the source, the actions folder).
	(string Tree, Bench.ActionSource Source, string Actions) ActionFixture()
	{
		var remote = Directory.CreateDirectory(Path.Combine(temp, "remote", "owner", "act.git")).FullName;
		Git(remote, "init", "-q");
		Write(remote, "action.yml", "name: act\n");
		Git(remote, "add", "-A");
		Git(remote, "commit", "-q", "-m", "act");
		Git(remote, "tag", "v1");
		var tree = Directory.CreateDirectory(Path.Combine(temp, "tree")).FullName;
		Git(tree, "init", "-q");
		Write(tree, ".github/workflows/claude-review.yml", "jobs:\n  review:\n    steps:\n      - uses: owner/act@v1\n");
		Git(tree, "add", "-A");
		Git(tree, "commit", "-q", "-m", "head");
		return (tree, new(Path.Combine(temp, "cache"), "file://" + Path.Combine(temp, "remote")), Path.Combine(temp, "actions"));
	}

	// A git ahead of the real one that runs script first, for each call whose arguments match pattern (a sh case pattern).
	[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
	void GitWrapper(string pattern, string script) =>
			OnPath("git", $"#!/bin/sh\ncase \"$*\" in {pattern}) {script} ;; esac\nexec '{RealGit}' \"$@\"\n");

	[Fact]
	public void The_heads_pinned_actions_are_copied_from_the_remote()
	{
		var (tree, source, actions) = ActionFixture();

		Bench.CopyActions(tree, "HEAD", actions, source);

		Assert.Equal("name: act\n", File.ReadAllText(Path.Combine(actions, "owner", "act", "v1", "action.yml")));
	}

	[Fact]
	public void Git_is_kept_from_asking_for_credentials()
	{
		if (OperatingSystem.IsWindows())
			return;
		var (tree, source, actions) = ActionFixture();
		var log = Path.Combine(temp, "env");
		GitWrapper("*clone*", $"echo \"prompt=${{GIT_TERMINAL_PROMPT-unset}} askpass=${{GIT_ASKPASS-unset}}\" > '{log}'");
		Environment.SetEnvironmentVariable("GIT_ASKPASS", "/bin/echo");
		try
		{
			Bench.CopyActions(tree, "HEAD", actions, source);
		}
		finally
		{
			Environment.SetEnvironmentVariable("GIT_ASKPASS", null);
		}

		Assert.Equal("prompt=0 askpass=\n", File.ReadAllText(log));
	}

	[Fact]
	public void A_git_call_past_its_time_is_the_cases_error_and_leaves_no_cache()
	{
		if (OperatingSystem.IsWindows())
			return;
		var (tree, source, actions) = ActionFixture();
		// The clone's folder, its last argument, exists part way when the time runs out.
		GitWrapper("*clone*", "eval last=\\${$#}; mkdir -p \"$last\"; sleep 30");
		var started = System.Diagnostics.Stopwatch.StartNew();

		var error = Assert.Throws<CaseError>(() => Bench.CopyActions(tree, "HEAD", actions, source with { Timeout = 1 }));

		Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"took {started.Elapsed}");
		Assert.Matches(@"\ACommand \['git', .*'clone', .*\] timed out after 1 seconds\z", error.Message);
		Assert.False(Directory.Exists(Path.Combine(temp, "cache", "owner", "act")));
		Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(temp, "cache", "owner")));
	}

	[Fact]
	public void A_failed_clone_leaves_the_cache_another_run_made_meanwhile()
	{
		if (OperatingSystem.IsWindows())
			return;
		var (tree, source, actions) = ActionFixture();
		var theirs = Path.Combine(temp, "cache", "owner", "act");
		// Another run fills the cache while this one's clone runs, and this one's fails.
		GitWrapper("*clone*", $"mkdir -p '{theirs}'; touch '{theirs}/HEAD'; exit 1");

		Assert.ThrowsAny<Exception>(() => Bench.CopyActions(tree, "HEAD", actions, source));

		Assert.True(File.Exists(Path.Combine(theirs, "HEAD")), "the other run's cache is removed");
		Assert.Equal([theirs], Directory.GetFileSystemEntries(Path.Combine(temp, "cache", "owner")));
	}

	[Fact]
	public void An_archive_past_its_bytes_is_the_cases_error()
	{
		var (tree, source, actions) = ActionFixture();

		var error = Assert.Throws<OutputPastLimit>(() => Bench.CopyActions(tree, "HEAD", actions, source with { MaxArchive = 1000 }));

		Assert.Matches(@"\ACommand \['git', .*'archive', 'v1'\] wrote more than 1000 bytes, so it was stopped\z", error.Message);
		Assert.False(File.Exists(Path.Combine(actions, "owner", "act", "v1", "action.yml")));
	}

	[Fact]
	public void A_workflow_past_its_bytes_pins_no_action()
	{
		var (tree, source, actions) = ActionFixture();

		Bench.CopyActions(tree, "HEAD", actions, source with { MaxWorkflow = 20 });

		Assert.False(Directory.Exists(actions));
		Assert.False(Directory.Exists(source.Cache));
	}
}
