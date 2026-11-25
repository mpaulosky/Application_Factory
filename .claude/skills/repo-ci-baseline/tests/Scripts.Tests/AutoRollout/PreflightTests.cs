using System.Text.RegularExpressions;

// Tests for github/AutoRolloutPreflight.cs: copies of github/tests/test_auto_rollout.py's tests of the preflight (ADR 0017's Update for
// mpaulosky/Application_Factory#157), with the same cases and assertions. The pytest originals stay until the cut-over. Where an original also
// checks session_env, which isn't ported yet, that part joins its copy with session_env's port.
public class PreflightTests : IDisposable
{
	readonly string tmp = Directory.CreateTempSubdirectory("preflight-test.").FullName;

	public void Dispose()
	{
		Directory.Delete(tmp, recursive: true);
		GC.SuppressFinalize(this);
	}

	// One call the fake run saw: the command, its folder and environment, and the global.json in that folder when there was one.
	sealed record Call(string[] Command, string? Cwd, IReadOnlyDictionary<string, string>? Env, string? GlobalJson);

	const string Pin = "{\"sdk\": {\"version\": \"10.0.403\"}}";
	const string NoFile = "/nonexistent/global.json";
	static readonly string[] AllTools = [.. AutoRollout.Tools, .. AutoRollout.SandboxTools, "dotnet", "uvx"];

	static Func<string?, Ran> A(int code, string output = "", string error = "") => _ => new Ran(code, output, error);

	static Dictionary<string, string> Env(params string[] pairs) =>
			pairs.Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

	// auto.preflight with which and run faked: which finds what is in onPath, and run answers by the command's first word ("claude auth" for
	// claude's auth status). The Template's global.json is written into the test's folder unless globalJson is null (then none is there).
	(List<string> Problems, List<Call> Calls) FakePreflight(IEnumerable<string>? onPath = null, Dictionary<string, Func<string?, Ran>>? answers = null,
			string? globalJson = Pin, bool inTmp = false, Dictionary<string, string>? environ = null, string platform = "linux",
			string factoryGlobalJson = NoFile, Func<string>? makeTemp = null)
	{
		var path = new HashSet<string>(onPath ?? AllTools);
		var answer = new Dictionary<string, Func<string?, Ran>>
		{
			["shfmt"] = A(0, "v3.14.1\n"),
			["gh"] = A(0),
			["dotnet"] = A(0, "10.0.404\n"),
			["bwrap"] = A(0),
			["claude"] = A(0, "2.1.326 (Claude Code)\n"),
			["ruff"] = A(0, "ruff 0.17.6\n"),
			["claude auth"] = A(0, "{\"loggedIn\": true, \"email\": \"someone@example.com\"}\n"),
		};
		foreach (var (key, value) in answers ?? [])
			answer[key] = value;
		var calls = new List<Call>();
		Ran Run(IReadOnlyList<string> command, string? cwd, IReadOnlyDictionary<string, string>? env)
		{
			string? seen = null;
			if (cwd is not null && File.Exists(Path.Combine(cwd, "global.json")))
				seen = File.ReadAllText(Path.Combine(cwd, "global.json"));
			calls.Add(new Call([.. command], cwd, env, seen));
			return answer[command.Contains("auth") && command[0] == "claude" ? "claude auth" : command[0]](seen);
		}
		var template = inTmp ? Path.Combine(tmp, "global.json") : NoFile;
		if (globalJson is not null && inTmp)
			File.WriteAllText(template, globalJson);
		var problems = AutoRollout.Preflight(tool => path.Contains(tool) ? $"/bin/{tool}" : null, Run, template, environ ?? Env("PATH=/bin"), platform,
				factoryGlobalJson, makeTemp);
		return (problems, calls);
	}

	static string SignedIn(string name) =>
			$"{name} is set, and the review session's shell would see it: unset it for the round (env -u {AutoRollout.ShellQuote(name)} before"
			+ " the command), and sign Claude Code in with its own credentials file (claude /login) or the cloud's profile"
			+ " files, which the sandbox keeps from the shell";

	static string UrlSignIn(string name) =>
			$"{name} holds a URL with a sign-in, which the review session is never given (its shell would see it), so"
			+ " its claude would run without it: for the round, set it to a URL with no sign-in in it (one that takes it"
			+ $" from a file of its own), or unset it (env -u {AutoRollout.ShellQuote(name)} before the command, for one round)";

	static Dictionary<string, string> WithPath(string[] pairs) => Env(["PATH=/bin", .. pairs]);

	// ── What the other side says ────────────────────────────────────────

	[Fact]
	public void Shfmt_rule_is_check_gh_repos_own()
	{
		var checker = File.ReadAllText(Skill.Path("template", "owned", ".github", "scripts", "check_gh_repo.py"));
		Assert.Contains($"SHFMT_RELEASE = re.compile(r\"{AutoRollout.ShfmtRelease}\")", checker);
		Assert.Contains($"SHFMT_OLDEST_MINOR = {AutoRollout.ShfmtOldestMinor}", checker);
	}

	[Fact]
	public void Ruff_rule_is_the_template_gates_own()
	{
		var gate = File.ReadAllText(Skill.Path("template", "owned", "scripts", "gate.sh"));
		Assert.Contains($"RUFF_RELEASE='{AutoRollout.RuffRelease}'", gate);
		Assert.Contains($"RUFF_VERSION=\"{AutoRollout.RuffVersion}\"", gate);
		Assert.True(gate.Contains("if have_uvx; then"), "the gate falls back to uvx, so uvx alone will do");
	}

	// ── The preflight ───────────────────────────────────────────────────

	[Theory]
	[InlineData("v3.14.0", true), InlineData("v3.14.1\n", true), InlineData("v3.15.0", true), InlineData("v3.100.2", true)]
	[InlineData("v3.13.9", false), InlineData("v3.9.0", false), InlineData("v4.0.0", false), InlineData("v2.20.0", false), InlineData("3.14.0", false)]
	[InlineData("v3.14", false), InlineData("v3.14.0-rc1", false), InlineData("v3.14.0 extra", false), InlineData("v3.014.0", false)]
	[InlineData("", false), InlineData("(devel)", false)]
	public void Shfmt_needs_v3_14_or_a_later_v3_release(string text, bool ok) => Assert.Equal(ok, AutoRollout.ShfmtProblem(text) is null);

	[Fact]
	public void An_empty_shfmt_answer_is_named() =>
			Assert.Equal("shfmt is silent; the gate needs v3.14 or a later v3 release", AutoRollout.ShfmtProblem(""));

	public static TheoryData<string, string?> SdkHints => new()
	{
		{ "{\"sdk\": {\"version\": \"10.0.403\", \"rollForward\": \"latestMinor\"}}", "10.0.403" },
		{ "\uFEFF// a comment\n{\n  /* sdk */ \"sdk\": { \"rollForward\": \"latestMinor\", \"version\": \"10.0.100-rc.2\" },\n}", "10.0.100-rc.2" },
		{ "{\"test\": {\"runner\": \"x\"}}", null },
		{ "{\"sdk\": {\"version\": 10}}", null },
		{ "{\"sdk\": {\"version\": \"$(rm -rf ~)\"}}", null },
		{ "{\"sdk\": {\"version\": \"1" + new string('0', 40) + "\"}}", "1" + new string('0', 40) },
		{ "{\"sdk\": {\"version\": \"1" + new string('0', 41) + "\"}}", null },
		{ "{\"sdk\": {\"a\": {}, \"version\": \"1.0.0\"}}", null },
		{ "{\"sdk\": {}, \"tool\": {\"version\": \"9.0.0\"}}", null },
		{ "{\"sdk\":\r\n {\r\n \"version\" :\t\"10.0.100\"\r\n }}", "10.0.100" },
		{ "{\"sdk\": null}", null },
		{ "[]", null },
		{ "{not json", null },
		{ "", null },
	};

	[Theory]
	[MemberData(nameof(SdkHints))]
	public void Sdk_hint_finds_a_plain_version_only(string text, string? version) => Assert.Equal(version, AutoRollout.SdkHint(text));

	public static TheoryData<string[], string[]> InheritedSecrets => new()
	{
		{ ["ANTHROPIC_API_KEY=k"], ["ANTHROPIC_API_KEY"] },
		{ ["CLAUDE_CODE_OAUTH_TOKEN=t", "ANTHROPIC_AUTH_TOKEN=t"], ["ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN"] },
		{ ["ANTHROPIC_FOUNDRY_API_KEY=k", "ANTHROPIC_CUSTOM_HEADERS=x"], ["ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_FOUNDRY_API_KEY"] },
		{ ["ANTHROPIC_API_KEY="], [] },
		{ ["CLAUDE_CODE_MESSAGING_TOKEN=t", "ANTHROPIC_MODEL=m"], [] },
		{ ["CLAUDE_CODE_USE_BEDROCK=1", "AWS_SECRET_ACCESS_KEY=s", "AWS_ACCESS_KEY_ID=i", "AWS_SESSION_TOKEN=t", "AWS_PROFILE=p", "AWS_REGION=r"],
				["AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN"] },
		{ ["CLAUDE_CODE_USE_BEDROCK=0", "AWS_SECRET_ACCESS_KEY=s"], [] },
		{ ["CLAUDE_CODE_USE_VERTEX=true", "GOOGLE_APPLICATION_CREDENTIALS=/f", "GOOGLE_CLOUD_PROJECT=p"], ["GOOGLE_APPLICATION_CREDENTIALS"] },
		{ ["CLAUDE_CODE_USE_FOUNDRY=1", "AZURE_CLIENT_SECRET=s", "AZURE_TENANT_ID=t"], ["AZURE_CLIENT_SECRET"] },
	};

	[Theory]
	[MemberData(nameof(InheritedSecrets))]
	public void Preflight_refuses_a_sign_in_secret_the_session_would_inherit(string[] environ, string[] refused)
	{
		// Not a sign-in the session needs in its environment: the messaging token is the caller's, dropped by session_env.
		var (problems, _) = FakePreflight(environ: WithPath(environ));
		Assert.Equal(refused.Select(SignedIn), problems);
	}

	public static TheoryData<string[], string[]> SignInUrls => new()
	{
		{ ["ANTHROPIC_BASE_URL=https://me:key@gateway.example/"], ["ANTHROPIC_BASE_URL"] },
		{ ["ANTHROPIC_BEDROCK_BASE_URL=https://key@bedrock.example/"], ["ANTHROPIC_BEDROCK_BASE_URL"] },
		{ ["HTTPS_PROXY=http://u:pw@proxy:3128", "http_proxy=http://u:pw@proxy:3128", "ALL_PROXY=socks5://u:p@s:1"], ["ALL_PROXY", "HTTPS_PROXY", "http_proxy"] },
		{ ["CLAUDE_CODE_USE_VERTEX=1", "GOOGLE_CLOUD_ENDPOINT=https://u:p@vertex.example/"], ["GOOGLE_CLOUD_ENDPOINT"] },
		{ ["GOOGLE_CLOUD_ENDPOINT=https://u:p@vertex.example/"], [] },
		{ ["ANTHROPIC_BASE_URL=https://gateway.example/", "HTTPS_PROXY=http://proxy:3128", "NO_PROXY=https://u:p@x/", "MIRROR=https://u:p@mirror.example/"], [] },
		// A proxy with no scheme, as curl and git take one: its sign-in is still found.
		{ ["HTTPS_PROXY=u:pw@proxy:3128", "http_proxy=tok@proxy:3128", "all_proxy= u:p@s:1"], ["HTTPS_PROXY", "all_proxy", "http_proxy"] },
		{ ["HTTPS_PROXY=:pw@proxy:3128", "http_proxy= :pw@proxy", "ALL_PROXY=u:p@[::1]:3128"], ["ALL_PROXY", "HTTPS_PROXY", "http_proxy"] },
		{ ["HTTPS_PROXY=proxy:3128", "HTTP_PROXY=proxy.corp/a@b", "NO_PROXY=u:p@x", "MIRROR=u:p@mirror:1", "ALL_PROXY=@proxy:3128", "ftp_proxy=[::1]:3128"], [] },
	};

	[Theory]
	[MemberData(nameof(SignInUrls))]
	public void Preflight_refuses_an_endpoint_or_proxy_whose_url_holds_a_sign_in(string[] environ, string[] refused)
	{
		// session_env would leave each out, so the session's claude would run without its endpoint or proxy (that half of the original joins this
		// copy with session_env's port).
		var (problems, _) = FakePreflight(environ: WithPath(environ));
		Assert.Equal(refused.Select(UrlSignIn), problems);
		var all = string.Join("\n", problems);
		Assert.True(!Env(environ).Values.Any(value => value.Contains('@') && all.Contains(value)), "never the value");
	}

	[Fact]
	public void A_sign_in_secret_named_and_holding_a_url_is_refused_once()
	{
		var (problems, _) = FakePreflight(environ: Env("PATH=/bin", "CLAUDE_CODE_USE_BEDROCK=1", "AWS_ENDPOINT_TOKEN=https://u:p@x/"));
		Assert.Equal([SignedIn("AWS_ENDPOINT_TOKEN")], problems);
	}

	[Theory]
	[InlineData("AWS_DB_PASSWORD", true), InlineData("AWS_API_KEY", true), InlineData("AWS_APIKEY", true), InlineData("AWS_AUTH_X", true)]
	[InlineData("AWS_SERVICE_KEY", true), InlineData("AWS_COOKIE", true), InlineData("AWS_BEARER_TOKEN_BEDROCK", true), InlineData("AWS_BEARER_X", true)]
	[InlineData("AWS_SECRET_ACCESS_KEY", true), InlineData("AWS_ACCESS_KEY_ID", true), InlineData("AWS_SESSION_TOKEN", true)]
	[InlineData("AWS_SHARED_CREDENTIALS_FILE", true), InlineData("AWS_CLIENT_CERTIFICATE_PATH", true), InlineData("AWS_PRIVATE_KEY_PATH", true)]
	[InlineData("AWS_KEYFILE", true), InlineData("AWS_KEY_FILE", true), InlineData("AWS_Secret_x", true)]
	[InlineData("AWS_AUTHORITY_HOST", false), InlineData("AWS_KEYBOARD", false), InlineData("AWS_DEFAULT_REGION", false), InlineData("AWS_PROFILE", false)]
	[InlineData("AWS_Region", false), InlineData("AWS_KEY_ID", false)]
	public void A_cloud_secret_is_refused_and_dropped_and_the_rest_kept(string name, bool secret)
	{
		// The original also holds session_env to the same reading; that half joins this copy with session_env's port.
		var (problems, _) = FakePreflight(environ: Env("PATH=/bin", "CLAUDE_CODE_USE_BEDROCK=1", $"{name}=v"));
		Assert.Equal(secret ? [SignedIn(name)] : [], problems);
	}

	[Fact]
	public void Preflight_runs_on_linux_only()
	{
		var (problems, _) = FakePreflight(platform: "darwin");
		Assert.Equal("rollout.sh --auto runs on Linux only (darwin here): what its review session may read is"
				+ " checked with bubblewrap's sandbox alone; run rollout.sh without --auto, and the review by hand", problems[0]);
		Assert.Empty(FakePreflight(platform: "linux").Problems);
		// The host's own platform by default, as sys.platform names it.
		Assert.Equal(OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : AutoRollout.Platform, AutoRollout.Platform);
	}

	[Fact]
	public void Preflight_passes_with_every_tool_and_the_templates_sdk()
	{
		var (problems, calls) = FakePreflight(inTmp: true);
		Assert.Empty(problems);
		var dotnet = calls.Where(call => call.Command.SequenceEqual(["dotnet", "--version"])).ToList();
		Assert.Single(dotnet);
		Assert.True(dotnet[0].Cwd != tmp, "dotnet runs in a folder of its own, holding a copy");
		Assert.Equal(Pin, dotnet[0].GlobalJson);
		Assert.False(Directory.Exists(dotnet[0].Cwd), "and the folder is gone after");
	}

	[Fact]
	public void Preflight_runs_shfmt_with_path_alone_as_the_gh_check_does()
	{
		var (_, calls) = FakePreflight(inTmp: true);
		var shfmt = calls.First(call => call.Command.SequenceEqual(["shfmt", "--version"]));
		Assert.Equal(new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "" }, shfmt.Env);
	}

	public static TheoryData<string> EachTool => [.. AutoRollout.Tools];

	[Theory]
	[MemberData(nameof(EachTool))]
	public void Preflight_names_each_missing_tool_and_runs_nothing_of_it(string tool)
	{
		var (problems, calls) = FakePreflight(onPath: AllTools.Where(t => t != tool), inTmp: true);
		Assert.Equal([$"{tool} isn't on PATH"], problems);
		Assert.DoesNotContain(calls, call => call.Command[0] == tool);
	}

	[Fact]
	public void Preflight_refuses_an_old_shfmt_and_a_gh_that_fails()
	{
		var (problems, _) = FakePreflight(answers: new() { ["shfmt"] = A(0, "v3.13.0\n"), ["gh"] = A(1, "", "not logged in\n") }, inTmp: true);
		Assert.Equal(["shfmt is v3.13.0; the gate needs v3.14 or a later v3 release",
				"gh auth status failed (exit 1): not logged in; if it says you're signed out: gh auth login"], problems);
		(problems, _) = FakePreflight(answers: new() { ["gh"] = A(1) }, inTmp: true);
		Assert.Equal(["gh auth status failed (exit 1): no answer; if it says you're signed out: gh auth login"], problems);
	}

	[Fact]
	public void Preflight_names_each_missing_sandbox_tool()
	{
		Assert.SkipWhen(AutoRollout.SandboxTools.Count == 0, "the sandbox needs no tools here");
		var (problems, calls) = FakePreflight(onPath: [.. AutoRollout.Tools, "dotnet", "uvx"], inTmp: true);
		Assert.Equal(AutoRollout.SandboxTools.Select(tool => $"{tool} isn't on PATH: the review session's sandbox needs it"), problems);
		Assert.DoesNotContain(calls, call => call.Command[0] == "bwrap");
	}

	const string RuffInstall = "install uv (https://docs.astral.sh/uv/), or put a ruff 0.17 first on PATH (uv tool install ruff@0.17.6 once uv is there)";

	public static TheoryData<string[], int, string, string, string?> PythonLints => new()
	{
		{ ["uvx"], -1, "", "", null },
		{ ["uvx", "ruff"], 0, "ruff 0.16.0\n", "", null },
		{ ["ruff"], 0, "ruff 0.17.0\n", "", null },
		{ ["ruff"], 0, "ruff 0.17.12\n", "", null },
		{ ["ruff"], 0, "", "ruff 0.17.6\n", null },
		{ ["ruff"], 0, "ruff 0.16.9\n", "", "ruff is 'ruff 0.16.9' (exit 0), not a 0.17 release" },
		{ ["ruff"], 0, "ruff 0.18.0\n", "", "ruff is 'ruff 0.18.0' (exit 0), not a 0.17 release" },
		{ ["ruff"], 0, "ruff 0.17.6 extra\n", "", "ruff is 'ruff 0.17.6 extra' (exit 0), not a 0.17 release" },
		{ ["ruff"], 2, "", "", "ruff is 'silent' (exit 2), not a 0.17 release" },
		{ ["ruff"], 1, "ruff 0.17.6\n", "", "ruff is 'ruff 0.17.6' (exit 1), not a 0.17 release" },
		{ [], -1, "", "", $"neither ruff 0.17 nor uv is installed, and the gate's Python lint needs one: {RuffInstall}" },
	};

	// ruffCode -1 is no ruff answer given: the default one.
	[Theory]
	[MemberData(nameof(PythonLints))]
	public void Preflight_wants_the_gates_python_lint(string[] onPath, int ruffCode, string ruffOut, string ruffErr, string? problem)
	{
		var answers = ruffCode >= 0 ? new Dictionary<string, Func<string?, Ran>> { ["ruff"] = A(ruffCode, ruffOut, ruffErr) } : null;
		var (problems, calls) = FakePreflight(onPath: [.. AutoRollout.Tools, .. AutoRollout.SandboxTools, "dotnet", .. onPath], answers: answers, inTmp: true);
		if (problem is null)
			Assert.Empty(problems);
		else
		{
			Assert.True(problems.Count == 1 && problems[0].StartsWith(problem, StringComparison.Ordinal), string.Join("\n", problems));
			if (problem.Contains("release"))
				Assert.EndsWith($"and uv isn't installed for the gate to run the pinned one: {RuffInstall}", problems[0]);
			Assert.True(!problems[0].Split("install uv")[0].Contains("uv tool install"), "never a uv command before uv is there");
		}
		if (onPath.Contains("uvx"))
			Assert.True(!calls.Any(call => call.Command[0] == "ruff"), "with uvx there is no need to ask ruff");
	}

	[Fact]
	public void Preflight_tries_the_sandbox_and_claude()
	{
		var (problems, calls) = FakePreflight(inTmp: true);
		Assert.Contains(calls, call => call.Command.SequenceEqual(["claude", "--version"]));
		if (AutoRollout.SandboxTools.Count > 0)
			Assert.Contains(calls, call => call.Command.SequenceEqual(["bwrap", "--unshare-all", "--ro-bind", "/", "/", "--dev", "/dev", "true"]));
		(problems, _) = FakePreflight(answers: new() { ["bwrap"] = A(1, "", "bwrap: setting up uid map: Permission denied\n"), ["claude"] = A(127, "", "No such file") },
				inTmp: true);
		var expected = new List<string> { "claude --version failed (exit 127): No such file" };
		if (AutoRollout.SandboxTools.Count > 0)
			expected.Insert(0, "bubblewrap can't make the review session's sandbox here (exit 1): bwrap: setting up uid map:"
					+ " Permission denied; try it with: bwrap --unshare-all --ro-bind / / --dev /dev true. The host"
					+ " may refuse unprivileged user namespaces (Ubuntu's sysctl"
					+ " kernel.apparmor_restrict_unprivileged_userns, kernel.unprivileged_userns_clone, a container"
					+ " without them); where it can't allow them, run rollout.sh without --auto and the review by hand");
		Assert.Equal(expected, problems);
		(problems, _) = FakePreflight(answers: new() { ["bwrap"] = A(1), ["claude"] = A(1) }, inTmp: true);
		Assert.All(problems, problem => Assert.StartsWith("no answer", problem.Split(": ", 2)[1]));
	}

	const string Auth = "claude --setting-sources '' auth status --json";
	const string NotSignedIn = $"claude isn't signed in, as {Auth} says: sign it in with claude /login (the review session reads no"
			+ " settings file, so an apiKeyHelper in one isn't used)";
	const string CantTell = $"{Auth} doesn't say whether claude is signed in (no loggedIn in what it prints): update claude, or run rollout.sh without --auto";

	public static TheoryData<int, string, string, string?> AuthAnswers => new()
	{
		{ 0, "{\"loggedIn\": true, \"email\": \"someone@example.com\"}", "", null },
		{ 0, "{\"loggedIn\": false, \"email\": \"someone@example.com\"}", "", NotSignedIn },
		{ 0, "{\"loggedIn\": \"true\"}", "", NotSignedIn },
		{ 0, "{\"loggedIn\": null}", "", NotSignedIn },
		{ 0, "{\"email\": \"someone@example.com\"}", "", CantTell },
		{ 0, "[]", "", CantTell },
		{ 0, "not json", "", CantTell },
		{ 0, "", "", CantTell },
		{ 1, "{\"loggedIn\": true}", "someone@example.com x",
				$"{Auth} failed (exit 1), so whether claude is signed in can't be told: run it to see why, and sign claude in with"
				+ " claude /login if it says you're signed out" },
		{ 124, "", "claude didn't answer in 120 s",
				$"{Auth} failed (exit 124), so whether claude is signed in can't be told: run it to see why, and sign claude in with"
				+ " claude /login if it says you're signed out" },
	};

	[Theory]
	[MemberData(nameof(AuthAnswers))]
	public void Preflight_wants_claude_signed_in_and_shows_nothing_of_the_account(int code, string output, string error, string? problem)
	{
		var (problems, calls) = FakePreflight(answers: new() { ["claude auth"] = A(code, output, error) }, inTmp: true);
		Assert.Equal(problem is null ? [] : [problem], problems);
		Assert.Equal(["claude", "--setting-sources", "", "auth", "status", "--json"], AutoRollout.ClaudeAuthStatus);
		Assert.True(calls.Any(call => call.Command.SequenceEqual(AutoRollout.ClaudeAuthStatus)), "it reads no settings file, as the session");
		Assert.True(!problems.Any(text => text.Contains("someone@example.com")), "neither the account nor stderr is printed");
	}

	public static TheoryData<string> CloudFlags => [.. AutoRollout.CloudSignIn.Select(cloud => cloud.Key).Order(StringComparer.Ordinal)];

	[Theory]
	[MemberData(nameof(CloudFlags))]
	public void A_cloud_sign_in_isnt_asked_of_claude_auth_status(string flag)
	{
		var (problems, calls) = FakePreflight(answers: new() { ["claude auth"] = A(0, "{\"loggedIn\": false}") }, inTmp: true, environ: Env("PATH=/bin", $"{flag}=1"));
		Assert.Empty(problems);
		Assert.DoesNotContain(calls, call => call.Command.SequenceEqual(AutoRollout.ClaudeAuthStatus));
	}

	[Fact]
	public void A_cloud_flag_thats_off_still_asks_claude_auth_status()
	{
		var (problems, _) = FakePreflight(answers: new() { ["claude auth"] = A(0, "{\"loggedIn\": false}") }, inTmp: true,
				environ: Env("PATH=/bin", "CLAUDE_CODE_USE_BEDROCK=0"));
		Assert.Equal([NotSignedIn], problems);
	}

	[Fact]
	public void A_claude_that_doesnt_run_isnt_asked_whether_it_is_signed_in()
	{
		var (problems, calls) = FakePreflight(answers: new() { ["claude"] = A(127, "", "No such file") }, inTmp: true);
		Assert.Equal(["claude --version failed (exit 127): No such file"], problems);
		Assert.DoesNotContain(calls, call => call.Command.SequenceEqual(AutoRollout.ClaudeAuthStatus));
	}

	[Fact]
	public void Preflight_names_an_sdk_it_cant_check()
	{
		var (problems, _) = FakePreflight(inTmp: true, makeTemp: () => throw new IOException("No space left on device"));
		Assert.Equal(["can't check the .NET SDK (no temporary folder to check it in): No space left on device"], problems);
	}

	[Fact]
	public void Preflight_reads_shfmts_version_from_stdout_alone() =>
			Assert.Empty(FakePreflight(answers: new() { ["shfmt"] = A(0, "v3.14.1\n", "a shim's warning\n") }, inTmp: true).Problems);

	[Fact]
	public void Preflight_names_a_failing_shfmt() =>
			Assert.Equal(["shfmt --version failed (exit 2): bad flag"], FakePreflight(answers: new() { ["shfmt"] = A(2, "", "bad flag") }, inTmp: true).Problems);

	[Theory]
	[InlineData(124), InlineData(127)]
	public void A_gh_that_does_not_answer_is_not_called_signed_out(int code) =>
			Assert.Equal(["gh auth status didn't run: gh didn't answer in 120 s"],
					FakePreflight(answers: new() { ["gh"] = A(code, "", "gh didn't answer in 120 s") }, inTmp: true).Problems);

	[Fact]
	public void Preflight_names_the_templates_sdk_and_how_to_install_it() =>
			Assert.Equal(["no installed .NET SDK matches the Template's global.json (.NET SDK 10.0.403): dotnetup sdk install 10.0.403"],
					FakePreflight(answers: new() { ["dotnet"] = A(155, "", "A compatible .NET SDK was not found.") }, inTmp: true).Problems);

	[Fact]
	public void Preflight_never_builds_an_install_command_from_an_unreadable_version() =>
			Assert.Equal(["no installed .NET SDK matches the Template's global.json (the .NET SDK it names)"],
					FakePreflight(answers: new() { ["dotnet"] = A(1) }, globalJson: "{\"sdk\": {\"version\": \"$(x)\"}}", inTmp: true).Problems);

	[Fact]
	public void Preflight_wants_dotnet_for_land_sh_and_asks_it_for_the_sdk_only_when_the_template_has_a_global_json()
	{
		// land.sh runs github/land.cs (ADR 0017), so dotnet is wanted with or without the Template's global.json, and named once.
		var noDotnet = AllTools.Where(tool => tool != "dotnet").ToArray();
		Assert.Contains("dotnet", AutoRollout.Tools);
		Assert.Equal(["dotnet isn't on PATH"], FakePreflight(onPath: noDotnet, globalJson: null, inTmp: true).Problems);
		var (problems, calls) = FakePreflight(globalJson: null, inTmp: true);
		Assert.True(problems.Count == 0 && !calls.Any(call => call.Command[0] == "dotnet"));
		// With the global.json written: still named once.
		Assert.Equal(["dotnet isn't on PATH"], FakePreflight(onPath: noDotnet, inTmp: true).Problems);
	}

	[Fact]
	public void Preflight_names_an_unreadable_global_json_and_runs_no_dotnet()
	{
		Directory.CreateDirectory(Path.Combine(tmp, "global.json"));
		var (problems, calls) = FakePreflight(globalJson: null, inTmp: true);
		Assert.True(problems.Count == 1 && problems[0].StartsWith("can't read the Template's global.json: ", StringComparison.Ordinal), string.Join("\n", problems));
		Assert.DoesNotContain(calls, call => call.Command[0] == "dotnet");
	}

	[Theory]
	[InlineData(127, "No such file"), InlineData(124, "dotnet didn't answer in 120 s")]
	public void Preflight_says_why_dotnet_did_not_answer(int code, string error) =>
			Assert.Equal([$"no installed .NET SDK matches the Template's global.json (.NET SDK 10.0.403): dotnetup sdk install 10.0.403 ({error})"],
					FakePreflight(answers: new() { ["dotnet"] = A(code, "", error) }, inTmp: true).Problems);

	[Fact]
	public void Preflight_checks_the_real_templates_global_json_by_default()
	{
		var folders = new Folders(Skill.Dir);
		Assert.Equal(Path.Combine(folders.Owned, "global.json"), folders.TemplateGlobalJson);
		Assert.True(File.Exists(folders.TemplateGlobalJson));
	}

	[Fact]
	public void Preflight_checks_the_factorys_global_json_too_when_its_pin_differs()
	{
		// land.sh runs on the Factory's global.json (mpaulosky/Application_Factory#178): a pin the installed SDKs don't satisfy is named, though the
		// Template's is satisfied.
		var factory = Directory.CreateDirectory(Path.Combine(tmp, "factory")).FullName;
		var factoryJson = Path.Combine(factory, "global.json");
		File.WriteAllText(factoryJson, "{\"sdk\": {\"version\": \"11.0.100\"}}");
		var installed = new Dictionary<string, Func<string?, Ran>>
		{
			["dotnet"] = pin => pin!.Contains("10.0.403") ? new Ran(0, "10.0.404\n", "") : new Ran(155, "", "A compatible .NET SDK was not found."),
		};
		var (problems, calls) = FakePreflight(answers: installed, inTmp: true, factoryGlobalJson: factoryJson);
		Assert.Equal(["no installed .NET SDK matches the Factory's global.json, which land.sh runs on (.NET SDK 11.0.100): dotnetup sdk install 11.0.100"], problems);
		Assert.Equal([Pin, "{\"sdk\": {\"version\": \"11.0.100\"}}"], calls.Where(call => call.Command[0] == "dotnet").Select(call => call.GlobalJson));
		// The same pin in both is asked of dotnet once.
		File.WriteAllText(factoryJson, Pin);
		(problems, calls) = FakePreflight(answers: installed, inTmp: true, factoryGlobalJson: factoryJson);
		Assert.True(problems.Count == 0 && calls.Count(call => call.Command[0] == "dotnet") == 1);
	}

	[Fact]
	public void Preflight_checks_the_global_json_land_sh_runs_on_by_default()
	{
		var folders = new Folders(Skill.Dir);
		Assert.Equal(AutoRollout.NearestGlobalJson(Skill.Path("github")), folders.FactoryGlobalJson);
		Assert.True(File.Exists(folders.FactoryGlobalJson));
		// The nearest one in the folder or above it, as dotnet finds it.
		var deep = Directory.CreateDirectory(Path.Combine(tmp, "a", "b")).FullName;
		File.WriteAllText(Path.Combine(tmp, "global.json"), "{}");
		Assert.Equal(Path.Combine(tmp, "global.json"), AutoRollout.NearestGlobalJson(deep));
		File.WriteAllText(Path.Combine(tmp, "a", "global.json"), "{}");
		Assert.Equal(Path.Combine(tmp, "a", "global.json"), AutoRollout.NearestGlobalJson(deep));
	}

	[Fact]
	public void Run_quiet_reads_bytes_that_arent_utf_8() =>
			Assert.Equal(new Ran(0, "a" + (char)0xFFFD + "b", ""), AutoRollout.RunQuiet(["printf", "a\\377b"]));

	[Fact]
	public void Run_quiet_passes_its_limits_and_the_env()
	{
		// The original stubs subprocess.run to read what it is handed; this runs a shell that prints what it was given: its folder, its whole
		// environment (PATH alone), and whether its stdin is empty.
		Assert.Equal(TimeSpan.FromSeconds(120), AutoRollout.QuietTimeout);
		var shell = AutoRollout.Which("sh")!;
		var (code, output, _) = AutoRollout.RunQuiet([shell, "-c", "pwd; echo \"$PATH ${HOME-no home}\"; read line && echo read || echo eof"], tmp,
				new Dictionary<string, string> { ["PATH"] = "/p" });
		Assert.Equal(0, code);
		Assert.Equal($"{AutoRollout.RealPath(tmp)}\n/p no home\neof\n", output);
	}

	[Fact]
	public void Run_quiet_turns_a_missing_command_and_a_timeout_into_codes()
	{
		Assert.Equal(127, AutoRollout.RunQuiet(["/nonexistent/tool"]).Code);
		Assert.Equal(127, AutoRollout.RunQuiet(["no-such-tool-anywhere"]).Code);
		Assert.Equal(new Ran(124, "", "sleep didn't answer in 0.2 s"), AutoRollout.RunQuiet(["sleep", "5"], TimeSpan.FromSeconds(0.2)));
	}

	[Fact]
	public void Run_quiet_times_out_when_a_child_keeps_its_pipes_after_it_exits()
	{
		// Not in the original, whose subprocess.run(timeout=) bounds the pipes' reads as well: the command exits at once, but the sleep it
		// leaves behind holds stdout open, so only one deadline over the exit and both reads keeps the limit.
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var ran = AutoRollout.RunQuiet(["sh", "-c", "sleep 5 & exit 0"], TimeSpan.FromSeconds(0.5));
		Assert.Equal(new Ran(124, "", "sh didn't answer in 0.5 s"), ran);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
	}

	[Fact]
	public void Python_s_readings_of_space_and_line_breaks()
	{
		// Not in the original: the readings the port re-implements, where .NET's own differ.
		Assert.Equal("a", AutoRollout.PyStrip("\u001C\u001F a \u3000\n"));
		Assert.Equal(["a\n", "b\r\n", "c\r", "d\v", "e\u001E", "f\u2029", "g"], AutoRollout.SplitLines("a\nb\r\nc\rd\ve\u001Ef\u2029g", keepEnds: true));
		Assert.Equal(["a", "", "b\u001Fc"], AutoRollout.SplitLines("a\n\nb\u001Fc\n", keepEnds: false));
		Assert.Equal("'' a 'b c' 'it'\"'\"'s' -x=1,y/z@%+:", string.Join(" ", new[] { "", "a", "b c", "it's", "-x=1,y/z@%+:" }.Select(AutoRollout.ShellQuote)));
		var sorted = new List<string> { "\uFFFD", "\U0001F600", "a-b/x", "a/x" };
		sorted.Sort(AutoRollout.CodePoints);
		Assert.Equal(["a-b/x", "a/x", "\uFFFD", "\U0001F600"], sorted);
		Assert.True(AutoRollout.HasSignIn("x=https://tok@host"));
		Assert.False(AutoRollout.HasSignIn("ssh://git@host"));
		Assert.True(AutoRollout.HasSignIn("ssh://git:pw@host"));
		Assert.False(AutoRollout.HasSignIn("_https://tok@host"), "a scheme starts after no word character, as \\b reads it");
		Assert.Matches($"[{AutoRollout.PySpace}]", "\u001C");
	}
}
