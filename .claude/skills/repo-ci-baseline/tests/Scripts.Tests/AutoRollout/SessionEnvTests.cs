using static SandboxFixtures;

// Tests for github/AutoRolloutSandbox.cs's review-session environment (SessionEnv) and the sweeps of what killed runs left, copied from
// github/tests/test_auto_rollout.py, which stays until the cut-over (mpaulosky/Application_Factory#122).
public class SessionEnvTests
{
	[Fact]
	public void A_password_in_a_variables_value_or_a_pwd_name_is_left_out_of_the_sessions_environment()
	{
		var env = AutoRollout.SessionEnv(Env(("MYSQL_PWD", "hunter2"), ("PWD", "/w"), ("CACHE_URL", "redis://:hunter2@cache:6379/0"),
			("MIRROR", "https://me:hunter2@mirror.example/r"), ("REMOTE", "ssh://git@github.com/o/r"), ("PROXY", "http://proxy:3128"), ("LANG", "C.UTF-8"),
			("UPSTREAM", "https://ghp_abc@github.com/o/r"), ("FTP_MIRROR", "ftp://anon@mirror.example/")), TempDir());
		Assert.DoesNotContain(new[] { "MYSQL_PWD", "CACHE_URL", "MIRROR" }, env.ContainsKey);
		Assert.DoesNotContain(new[] { "UPSTREAM", "FTP_MIRROR" }, env.ContainsKey);
		Assert.All(new[] { "PWD", "REMOTE", "PROXY", "LANG" }, name => Assert.True(env.ContainsKey(name), name));
	}

	// Its last assertion, that only the proxies claude reads stop the round (sign_in_problems), ports with the preflight
	// (mpaulosky/Application_Factory#158).
	[Fact]
	public void Any_tools_proxy_written_with_no_scheme_and_a_sign_in_is_left_out_and_doesnt_stop_the_round()
	{
		var proxies = Env(("PIP_PROXY", "u:pw@proxy:3128"), ("npm_config_https_proxy", ":pw@proxy:3128"), ("SOCKS_PROXY", "tok@s:1080"),
			("yarn_proxy", "u:pw@proxy:3128"), ("httpsProxy", "u:pw@proxy:3128"));
		var plain = Env(("NPM_CONFIG_PROXY", "proxy:3128"), ("GLOBAL_AGENT_HTTP_PROXY", "@proxy:3128"), ("PROXYCHAINS", "u:pw@x"),
			("NOPROXY_LIST", "u:pw@x"), ("no_proxy", "u:pw@x"));
		var env = AutoRollout.SessionEnv(proxies.Concat(plain).ToDictionary(), TempDir());
		Assert.DoesNotContain(proxies.Keys, env.ContainsKey);
		Assert.All(plain.Keys, name => Assert.True(env.ContainsKey(name), name));
	}

	[Fact]
	public void The_sessions_path_starts_with_the_folders_its_tools_really_run_from()
	{
		var tmp = TempDir();
		Assert.Equal("/h/.nvm/versions/node/v24/bin:/usr/bin:/bin", AutoRollout.SessionEnv(Env(("PATH", "/usr/bin:/bin")), tmp, ["/h/.nvm/versions/node/v24/bin"])["PATH"]);
		Assert.Equal("/usr/bin", AutoRollout.SessionEnv(Env(("PATH", "/usr/bin")), tmp)["PATH"]);
		Assert.Equal("/n/bin:" + PyLib.DefPath, AutoRollout.SessionEnv(Env(), tmp, ["/n/bin"])["PATH"]);
	}

	static readonly string[] Dropped =
	[
		"GH_TOKEN", "GITHUB_TOKEN", "gh_token", "RELEASE_PR_PAT", "AWS_PROFILE", "MY_API_KEY", "NPM_TOKEN",
		"DB_PASSWORD", "SSH_AUTH_SOCK", "SSH_AGENT_PID", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0",
		"GIT_CONFIG_KEY_9", "GIT_CONFIG_PARAMETERS", "GATE_SKIP_REVIEW", "CLIENT_SECRET", "GOOGLE_APPLICATION_CREDENTIALS",
		"AZURE_CLIENT_ID", "NPM_CONFIG__AUTH", "DOCKER_AUTH_CONFIG", "STRIPE_APIKEY", "API_KEY_PROD", "DATABASE_URL",
		"SENTRY_DSN", "GIT_ASKPASS", "SSH_ASKPASS", "GIT_SSH_COMMAND", "GIT_SSH", "DBUS_SESSION_BUS_ADDRESS",
		"GPG_PASSPHRASE", "SESSION_COOKIE", "MY_BEARER", "DOCKER_CREDENTIAL", "XSSH_AUTH_SOCK", "XAUTHORITY",
		"OPENAI_KEY", "STRIPE_KEY", "GOOGLE_CLOUD_PROJECT", "AZURE_TENANT", "AWS_REGION", "XCLAUDE_TOKEN", "MY_PAT",
		"SENTRY_AUTH", "GIT_SSH_VARIANT", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_AUTH_TOKEN",
		"CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SSE_PORT",
		"CLAUDE_PID", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_ANYTHING",
	];
	static readonly string[] Kept =
	[
		"PATH", "HOME", "LANG", "LD_LIBRARY_PATH", "MONKEY", "KEYBOARD", "MYAWS_X", "GIT_CONFIG_NOSYSTEM", "GIT_AUTHOR_NAME", "XSSH_X",
		.. AutoRollout.ClaudeKept.Where(name => name != "CLAUDE_CONFIG_DIR"),
	];

	[Fact]
	public void The_sessions_environment_has_no_secrets_but_claudes_sign_in()
	{
		var tmp = TempDir();
		var baseEnv = Dropped.Concat(Kept).Concat(["GIT_CONFIG_GLOBAL", "CLAUDE_CONFIG_DIR"]).ToDictionary(name => name, _ => "x");
		var env = AutoRollout.SessionEnv(baseEnv, tmp);
		Assert.Equal(Kept.ToHashSet(), baseEnv.Keys.Where(name => env.GetValueOrDefault(name) == "x").ToHashSet());
		Assert.Equal(PyLib.AbsPath("x"), env["CLAUDE_CONFIG_DIR"]);
		Assert.Equal(Path.Combine(tmp, "gitconfig"), env["GIT_CONFIG_GLOBAL"]);
		Assert.Equal("", File.ReadAllText(Path.Combine(tmp, "gitconfig")));
		var count = int.Parse(env["GIT_CONFIG_COUNT"], System.Globalization.CultureInfo.InvariantCulture);
		Assert.Equal(AutoRollout.SessionGitConfig, Enumerable.Range(0, count).Select(n => (env[$"GIT_CONFIG_KEY_{n}"], env[$"GIT_CONFIG_VALUE_{n}"])));
		Assert.False(env.ContainsKey("GIT_CONFIG_KEY_9"));
		Assert.Equal("0", env["GIT_OPTIONAL_LOCKS"]);
	}

	[Fact]
	public void The_sessions_caches_and_temporary_folders_are_its_own()
	{
		var tmp = TempDir();
		var env = AutoRollout.SessionEnv(Env(("PATH", "/bin")), tmp);
		foreach (var (name, folder) in new[] { ("GH_CONFIG_DIR", "gh"), ("TMPDIR", "tmp"), ("NUGET_PACKAGES", "nuget-packages"), ("NUGET_HTTP_CACHE_PATH", "nuget-http"),
				("npm_config_store_dir", "pnpm-store"), ("pnpm_config_store_dir", "pnpm-store"), ("XDG_CACHE_HOME", "cache"), ("DOTNET_CLI_HOME", "dotnet-home") })
		{
			Assert.Equal(Path.Combine(tmp, folder), env[name]);
			Assert.True(Directory.Exists(Path.Combine(tmp, folder)), name);
		}
		Assert.Equal("1", env["DOTNET_NOLOGO"]);
		Assert.Equal("1", env["DOTNET_CLI_TELEMETRY_OPTOUT"]);
	}

	[Theory]
	[InlineData("CLAUDE_CODE_USE_BEDROCK", "AWS_")]
	[InlineData("CLAUDE_CODE_USE_VERTEX", "GOOGLE_")]
	[InlineData("CLAUDE_CODE_USE_FOUNDRY", "AZURE_")]
	public void A_cloud_sign_in_keeps_its_own_variables(string flag, string prefix)
	{
		Assert.Contains((flag, prefix), AutoRollout.CloudSignIn);
		var others = AutoRollout.CloudSignIn.Select(cloud => cloud.Prefix).Where(p => p != prefix).ToList();
		var baseEnv = Env((flag, "1"), ($"{prefix}PROFILE", "p"), ("GH_TOKEN", "t"), ($"{prefix}SECRET_ACCESS_KEY", "s"));
		foreach (var other in others)
			baseEnv[$"{other}PROFILE"] = "q";
		var env = AutoRollout.SessionEnv(baseEnv, TempDir());
		Assert.Equal("p", env[$"{prefix}PROFILE"]);
		Assert.False(env.ContainsKey("GH_TOKEN"));
		Assert.False(env.ContainsKey($"{prefix}SECRET_ACCESS_KEY"), "even the cloud in use's secrets are left out");
		Assert.DoesNotContain(others, other => env.ContainsKey($"{other}PROFILE"));
	}

	[Theory]
	[InlineData("1", true)]
	[InlineData("true", true)]
	[InlineData(" Yes ", true)]
	[InlineData("ON", true)]
	[InlineData("0", false)]
	[InlineData("false", false)]
	[InlineData("", false)]
	[InlineData("no", false)]
	[InlineData("off", false)]
	[InlineData("2", false)]
	public void A_cloud_flag_is_on_only_for_a_value_that_turns_it_on(string value, bool kept) =>
		Assert.Equal(kept, AutoRollout.SessionEnv(Env(("CLAUDE_CODE_USE_BEDROCK", value), ("AWS_PROFILE", "p")), TempDir()).ContainsKey("AWS_PROFILE"));

	[Fact]
	public void Git_in_the_sessions_environment_cannot_push_to_github()
	{
		var tmp = TempDir();
		var repo = Path.Combine(tmp, "r");
		Directory.CreateDirectory(repo);
		Git("-C", repo, "init", "-q", "-b", "main");
		var env = AutoRollout.SessionEnv(AutoRollout.Environ(), Path.Combine(tmp, "s"));
		foreach (var url in new[] { "https://github.com/acme/widget.git", "git@github.com:acme/widget.git", "ssh://git@github.com/acme/widget" })
		{
			GitIn(AutoRollout.Environ(), "-C", repo, "remote", "remove", "origin");
			Git("-C", repo, "remote", "add", "origin", url);
			var push = GitIn(env, "-C", repo, "remote", "get-url", "--push", "origin").Trim();
			var fetch = GitIn(env, "-C", repo, "remote", "get-url", "origin").Trim();
			Assert.StartsWith(AutoRollout.NoPush, push);
			Assert.Equal(url, fetch);
		}
		Assert.Equal([""], GitIn(env, "-C", repo, "config", "--get-all", "credential.helper").Split('\n')[..^1]);
	}

	[Fact]
	public void An_entry_gone_while_the_session_folder_is_swept_is_skipped()
	{
		var tmp = TempDir();
		Directory.CreateDirectory(Path.Combine(tmp, "rollout-session.gone"));
		Directory.CreateDirectory(Path.Combine(tmp, "rollout-session.old"));
		AutoRollout.SweepSessions(tmp, path => path.EndsWith(".gone", StringComparison.Ordinal) ? null : PyLib.Owner(path));
		Assert.False(Directory.Exists(Path.Combine(tmp, "rollout-session.old")));
	}

	[Fact]
	public void Another_users_leftover_in_the_session_folder_is_left()
	{
		var tmp = TempDir();
		Directory.CreateDirectory(Path.Combine(tmp, "rollout-session.theirs"));
		AutoRollout.SweepSessions(tmp, uid: PyLib.Owner(tmp) + 1);
		Assert.True(Directory.Exists(Path.Combine(tmp, "rollout-session.theirs")));
	}

	[Fact]
	public void The_settings_sweep_leaves_another_users_files_and_goes_past_what_it_cant_remove()
	{
		var tmp = TempDir();
		Directory.CreateDirectory(Path.Combine(tmp, "review-0.settings.json")); // listed first: it can't be removed as a file
		File.WriteAllText(Path.Combine(tmp, "review-a.settings.json"), "{}");
		AutoRollout.SweepSettings(tmp, uid: PyLib.Owner(tmp) + 1);
		Assert.True(File.Exists(Path.Combine(tmp, "review-a.settings.json")), "another user's file is left");
		AutoRollout.SweepSettings(tmp);
		Assert.False(File.Exists(Path.Combine(tmp, "review-a.settings.json")));
		Assert.True(Directory.Exists(Path.Combine(tmp, "review-0.settings.json")));
	}
}
