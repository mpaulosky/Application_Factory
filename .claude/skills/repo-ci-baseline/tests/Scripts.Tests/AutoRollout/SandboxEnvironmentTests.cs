using static SandboxFixtures;

// Tests for github/AutoRolloutSandbox.cs that set the process's own environment or current folder (monkeypatch.setenv and chdir in
// github/tests/test_auto_rollout.py), so they run alone, after the parallel tests.
[Collection(nameof(GhLookupTests))]
public class SandboxEnvironmentTests
{
	// Runs body with the variables set (a null value unset) and the current folder at cwd, then puts them back.
	static void With(Dictionary<string, string?> variables, Action body, string? cwd = null)
	{
		var before = variables.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
		var folder = Environment.CurrentDirectory;
		try
		{
			foreach (var (name, value) in variables)
				Environment.SetEnvironmentVariable(name, value);
			if (cwd is not null)
				Environment.CurrentDirectory = cwd;
			body();
		}
		finally
		{
			foreach (var (name, value) in before)
				Environment.SetEnvironmentVariable(name, value);
			Environment.CurrentDirectory = folder;
		}
	}

	[Fact]
	public void The_session_settings_read_the_real_environment_by_default() =>
		// The round passes no environ: what the person's environment moves is denied all the same.
		With(new() { ["CLAUDE_CONFIG_DIR"] = "/cfg/claude", ["KUBECONFIG"] = "/k/config", ["XDG_CONFIG_HOME"] = "/x" }, () =>
		{
			var settings = SettingsOf("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home: "/h");
			foreach (var path in new[] { "/cfg/claude", "/k/config", "/x/gh", "/x/git" })
			{
				Assert.Contains(path, DenyRead(settings));
				Assert.Contains($"Read(/{path}/**)", Deny(settings));
			}
		});

	[Fact]
	public void The_session_settings_keep_claude_codes_own_config_folder_from_it() =>
		With(new() { ["CLAUDE_CONFIG_DIR"] = "/cfg/claude" }, () =>
		{
			var settings = SettingsOf("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home: "/h", environ: AutoRollout.Environ());
			Assert.Contains("/cfg/claude", DenyRead(settings));
			Assert.Equal(["/cfg/claude"], SandboxTests.DeniedBeyondTheList(Env(("CLAUDE_CONFIG_DIR", "/cfg/claude"))));
			Assert.Empty(SandboxTests.DeniedBeyondTheList(Env()));
		});

	[Fact]
	public void A_relative_or_symlinked_config_folder_is_denied_where_it_really_is()
	{
		var tmp = TempDir();
		Directory.CreateDirectory(Path.Combine(tmp, "real"));
		File.CreateSymbolicLink(Path.Combine(tmp, "link"), Path.Combine(tmp, "real"));
		With([], () =>
		{
			Assert.Equal([Path.Combine(tmp, "link"), Path.Combine(tmp, "real")], SandboxTests.DeniedBeyondTheList(Env(("CLAUDE_CONFIG_DIR", "link"))));
			var env = AutoRollout.SessionEnv(Env(("CLAUDE_CONFIG_DIR", "link/")), Path.Combine(tmp, "t"));
			Assert.Equal(Path.Combine(tmp, "link"), env["CLAUDE_CONFIG_DIR"]);
		}, cwd: tmp);
	}

	[Fact]
	public void The_session_settings_expand_the_real_home_and_take_the_main_checkout_from_git()
	{
		var tmp = TempDir();
		With(new() { ["HOME"] = Path.Combine(tmp, "me"), ["CLAUDE_CONFIG_DIR"] = null }, () =>
		{
			var settings = SettingsOf("/w/wt", "/x/Repo.git", "/x/Repo.git/worktrees/wt", "/t", environ: Env());
			Assert.Equal(Path.Combine(tmp, "me", ".ssh"), DenyRead(settings)[0]);
			Assert.DoesNotContain(DenyRead(settings), path => path.Contains('~'));
			Assert.Contains("Read(//x/.security/**)", Allow(settings));
		});
	}

	[Fact]
	public void The_session_folder_defaults_to_one_beside_the_cache_outside_tmp()
	{
		Assert.Equal(PyLib.ExpandUser("~/.cache/repo-ci-baseline-sessions"), AutoRollout.SessionDir);
		var home = PyLib.ExpandUser("~");
		foreach (var listed in AutoRollout.CredentialPaths)
		{
			var path = AutoRollout.ExpandHome(listed, home);
			Assert.True(AutoRollout.SessionDir != path && !AutoRollout.SessionDir.StartsWith(path + "/", StringComparison.Ordinal), path);
		}
		Assert.False(AutoRollout.SessionDir.StartsWith("/tmp/", StringComparison.Ordinal) || AutoRollout.SessionDir.StartsWith("/var/tmp/", StringComparison.Ordinal));
	}

	// Not moved: the Factory's checkout is found from the source file's own path, as auto_rollout.py finds it from __file__
	// (test_the_paths_it_runs_exist holds that for the Python).
	[Fact]
	public void The_factory_and_the_skill_are_found_from_the_source_file()
	{
		Assert.Equal(PyLib.RealPath(Skill.Dir), AutoRollout.SkillDir);
		Assert.True(File.Exists(Path.Combine(AutoRollout.Factory, "CLAUDE.md")));
	}
}
