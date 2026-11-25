using static SandboxFixtures;

// Tests for github/AutoRolloutSandbox.cs's read of the clone's git config for sign-in secrets (ConfigFiles, ConfigSecrets, Redacted),
// copied from github/tests/test_auto_rollout.py, which stays until the cut-over (mpaulosky/Application_Factory#122). git runs for real,
// with no global or system config.
public class ConfigSecretsTests
{
	static string A(int count) => new('a', count);
	static string One(int count) => new('1', count);

	public static TheoryData<string, string, string[]> Keys => new()
	{
		{ "remote.origin.url", "git@github.com:o/r.git", [] },
		{ "remote.origin.url", "https://github.com/o/r.git", [] },
		{ "remote.origin.url", "ssh://git@github.com/o/r.git", [] },
		{ "remote.origin.url", "https://github.com/o/a@b.git", [] },
		{ "remote.origin.url", "https://ghp_abc@github.com/o/r.git", ["remote.origin.url"] },
		{ "remote.origin.pushurl", "HTTPS://ghp_abc@github.com/o/r.git", ["remote.origin.pushurl"] },
		{ "remote.origin.url", "ssh://u:pw@host/r.git", ["remote.origin.url"] },
		{ "remote.origin.url", "ftp://u@host/r.git", ["remote.origin.url"] },
		{ "remote.origin.url", "ftps://u@host/r.git", ["remote.origin.url"] },
		{ "url.https://x-access-token:t0k@github.com/.insteadOf", "https://github.com/", ["url.https://***@github.com/.insteadof"] },
		{ "url.https://t0k@github.com/.pushInsteadOf", "https://github.com/", ["url.https://***@github.com/.pushinsteadof"] },
		{ "http.https://github.com/.extraheader", "AUTHORIZATION: basic eHg=", ["http.https://github.com/.extraheader"] },
		{ "http.proxy", "http://u:pw@proxy:3128", ["http.proxy"] },
		{ "remote.origin.proxy", "http://u:pw@proxy:3128", ["remote.origin.proxy"] },
		{ "http.proxy", "u:pw@proxy:3128", ["http.proxy"] },
		{ "remote.origin.proxy", ":pw@proxy:3128", ["remote.origin.proxy"] },
		{ "http.proxy", "proxy:3128", [] },
		{ "submodule.lib.url", "https://ghp_abc@github.com/o/lib.git", ["submodule.lib.url"] },
		{ "lfs.url", "https://ghp_abc@lfs.example/o/r", ["lfs.url"] },
		{ "credential.helper", "!f() { echo password=hunter2; }; f", ["credential.helper"] },
		{ "credential.https://github.com.helper", "store --file .git/creds", ["credential.https://github.com.helper"] },
		{ "credential.helper", "!git credential-store --file .git/creds", ["credential.helper"] },
		{ "credential.helper", "!f() { echo PASSWORD=x; }; f", ["credential.helper"] },
		{ "credential.helper", "!f() { echo password = x; }; f", ["credential.helper"] },
		{ "credential.helper", "!my-helper --password x", ["credential.helper"] },
		{ "credential.helper", "!my-helper --pass x", ["credential.helper"] },
		{ "credential.helper", "!pass-git-helper -m ~/.password-store", [] },
		{ "credential.helper", "!f() { echo username=x; echo \"password=$(pass show github)\"; }; f", [] },
		{ "credential.helper", "!f() { echo password=`pass show github`; }; f", [] },
		{ "credential.helper", "!my-helper --pass-store x", [] },
		{ "credential.helper", "!my-helper --password-store=gnome", [] },
		{ "credential.helper", "!my-helper --password $PW", [] },
		{ "credential.helper", "!my-helper --password=x", ["credential.helper"] },
		{ "credential.helper", "!my-helper --file-mode 600", [] },
		{ "credential.helper", "store --file=.git/creds", ["credential.helper"] },
		{ "credential.helper", "!my-helper --passthrough", [] },
		{ "credential.helper", "!my-helper --filesystem", [] },
		{ "credential.helper", "!f() { echo password=\"hunter2\"; }; f", ["credential.helper"] },
		{ "credential.helper", "!f() { echo password='hunter2'; }; f", ["credential.helper"] },
		{ "credential.helper", "!f() { echo password='$uper-secret'; }; f", ["credential.helper"] },
		{ "credential.helper", "!f() { echo password=\"$(echo hunter2)\"; }; f", ["credential.helper"] },
		{ "credential.helper", "!f() { echo password=\"$PW\"; }; f", [] },
		{ "credential.helper", "!f() { echo password=${PW}; }; f", [] },
		{ "credential.helper", "!f() { echo password=$1; }; f", [] },
		{ "credential.helper", "!f() { echo password=\"\"; }; f", [] },
		{ "credential.helper", "!f() { echo password=; }; f", [] },
		{ "credential.helper", "!f() { echo \"password=$(op read op://v/gh/token)\"; }; f", [] },
		{ "credential.helper", "!my-helper --password \"$PW\"", [] },
		{ "credential.helper", "!my-helper --password=$PW", [] },
		{ "credential.helper", "!my-helper --password '$uper'", ["credential.helper"] },
		{ "credential.helper", "!my-helper --password \"$(echo hunter2)\"", ["credential.helper"] },
		{ "sendemail.smtppass", "hunter2", ["sendemail.smtppass"] },
		{ "imap.pass", "hunter2", ["imap.pass"] },
		{ "tool.api-token", "abc", ["tool.api-token"] },
		{ "tool.api-token", "", [] },
		{ "tool.token", "   ", [] },
		{ "x.passwd", "a", ["x.passwd"] },
		{ "x.password", "a", ["x.password"] },
		{ "x.smtppassword", "a", ["x.smtppassword"] },
		{ "x.passphrase", "a", ["x.passphrase"] },
		{ "x.secret", "a", ["x.secret"] },
		{ "x.clientsecret", "a", ["x.clientsecret"] },
		{ "x.apikey", "a", ["x.apikey"] },
		{ "x.api-key", "a", ["x.api-key"] },
		{ "x.access-key", "a", ["x.access-key"] },
		{ "x.secretkey", "a", ["x.secretkey"] },
		{ "x.smtp-pass", "a", ["x.smtp-pass"] },
		{ "hub.oauthtoken", "a", ["hub.oauthtoken"] },
		{ "core.askpass", "/usr/bin/ssh-askpass", [] },
		{ "ssh.askpass", "/usr/lib/ssh/ksshaskpass", [] },
		{ "alias.bypass", "commit --no-verify", [] },
		{ "user.signingkey", "~/.ssh/id_ed25519.pub", [] },
		{ "http.sslkey", "/etc/ssl/me.key", [] },
		{ "x.passthrough", "a", [] },
		{ "x.tokens", "a", [] },
		{ "x.secrets-dir", "a", [] },
		{ "alias.pr", "!curl -H \"Authorization: token abc\" https://api.github.com", ["alias.pr"] },
		{ "alias.pr", "!curl -H 'bearer abcdefgh1234' https://api.github.com", ["alias.pr"] },
		{ "alias.pr", "!curl --oauth2-bearer abc https://api.github.com", [] },
		{ "alias.pr", "!curl --oauth2-bearer abcdefghijkl https://api.github.com", ["alias.pr"] },
		{ "alias.pr", "!curl --oauth2-bearer \"$GITHUB_TOKEN\" https://api.github.com", [] },
		{ "alias.pr", "!curl -H \"Authorization: Bearer $GITHUB_TOKEN\" https://api.github.com", [] },
		{ "alias.pr", "!curl -H \"Authorization: token $TOKEN\" https://api.github.com", [] },
		{ "alias.pr", "!curl -H 'Authorization: `cat t`' https://api.github.com", [] },
		{ "alias.pr", "!curl -H 'Authorization:' https://api.github.com", [] },
		{ "alias.pr", "!echo bearer abcdefgh", ["alias.pr"] },
		{ "alias.pr", "!echo bearer abcdefg", [] },
		{ "alias.pr", "!echo debearer abcdefghijkl", [] },
		{ "alias.pr", "!curl -H 'Authorization: token \"$T\"' https://x", [] },
		{ "alias.pr", "!curl -H 'Authorization: Bearer $GITHUB_TOKEN' https://x", [] },
		{ "alias.pr", "!curl -H \"Authorization: Basic $(echo -n me:hunter2 | base64)\" https://x", ["alias.pr"] },
		{ "alias.pr", "!curl --oauth2-bearer `cat t` x", [] },
		{ "alias.pr", "!curl --oauth2-bearer \"$(printf abcdefghij)\" x", ["alias.pr"] },
		{ "core.sshcommand", "sshpass -p \"$PW\" ssh", [] },
		{ "core.sshcommand", "sshpass -p$PW ssh", [] },
		{ "core.sshcommand", "sshpass -p `cat t` ssh", [] },
		{ "core.sshcommand", "sshpass -p \"$(echo hunter2)\" ssh", ["core.sshcommand"] },
		{ "core.sshcommand", "sshpass -p '$uper-secret' ssh", ["core.sshcommand"] },
		{ "core.sshcommand", "sshpass -p \"hunter2\" ssh", ["core.sshcommand"] },
		{ "core.sshcommand", "sshpass -p $PW ssh", [] },
		{ "core.sshcommand", "sshpass -e ssh", [] },
		{ "core.sshcommand", "sshpass -p hunter2 ssh", ["core.sshcommand"] },
		{ "alias.x", "!echo ghp_" + A(36), ["alias.x"] },
		{ "alias.x", "!echo gho_" + A(36), ["alias.x"] },
		{ "alias.x", "!echo ghp_short", [] },
		{ "alias.x", "!echo ghp_" + A(20), ["alias.x"] },
		{ "alias.x", "!echo ghp_" + A(19), [] },
		{ "alias.x", "!echo xghp_" + A(36), [] },
		{ "alias.x", "!echo ghu_" + A(20), ["alias.x"] },
		{ "alias.x", "!echo ghs_" + A(20), ["alias.x"] },
		{ "alias.x", "!echo ghr_" + A(20), ["alias.x"] },
		{ "alias.x", "!echo github_pat_" + A(20), ["alias.x"] },
		{ "alias.x", "!echo github_pat_" + A(19), [] },
		{ "alias.x", "!echo glpat-" + A(19), [] },
		{ "alias.x", "!echo xoxb-" + One(10), ["alias.x"] },
		{ "alias.x", "!echo xoxb-" + One(9), [] },
		{ "alias.x", "!echo xoxa-" + One(10), ["alias.x"] },
		{ "alias.x", "!echo xoxp-" + One(10), ["alias.x"] },
		{ "alias.x", "!echo xoxr-" + One(10), ["alias.x"] },
		{ "alias.x", "!echo xoxs-" + One(10), ["alias.x"] },
		{ "alias.x", "!echo sk-" + A(20), ["alias.x"] },
		{ "alias.x", "!echo sk-" + A(19), [] },
		{ "alias.x", "!echo AKIA" + new string('A', 15), [] },
		{ "alias.x", "!echo AKIA" + new string('A', 15) + "!", [] },
		{ "alias.x", "!echo github_pat_" + A(30), ["alias.x"] },
		{ "alias.x", "!echo glpat-" + A(20), ["alias.x"] },
		{ "alias.x", "!echo xoxb-" + One(12), ["alias.x"] },
		{ "alias.x", "!echo sk-" + A(24), ["alias.x"] },
		{ "alias.x", "!echo task-" + A(24), [] },
		{ "alias.x", "!echo AKIA" + new string('A', 16), ["alias.x"] },
		{ "alias.x", "!echo akia" + A(16), [] },
		{ "core.sshcommand", "ssh -i key", [] },
		{ "credential.usehttppath", "true", [] },
		{ "credential.helper", "store", [] },
		{ "credential.helper", "cache --timeout 300", [] },
	};

	[Theory]
	[MemberData(nameof(Keys))]
	public void A_sign_in_secret_in_the_clones_git_config_is_found_by_its_key(string key, string value, string[] found)
	{
		var tmp = TempDir();
		GitConfigRepo(tmp, (key, value));
		var secrets = AutoRollout.ConfigSecrets(tmp, Path.Combine(tmp, ".git"));
		Assert.Equal(found.Select(name => $"{name} in {Path.Combine(tmp, ".git", "config")}"), secrets);
		// The key alone: the temporary folder's random name could hold one of these by chance.
		Assert.DoesNotContain(secrets.Select(name => name[..name.LastIndexOf(" in ", StringComparison.Ordinal)]),
			name => name.Contains("t0k") || name.Contains("ghp_") || name.Contains("hunter2"));
	}

	[Theory]
	[InlineData("pass")]
	[InlineData("gopass")]
	[InlineData("op")]
	[InlineData("bw")]
	[InlineData("lpass")]
	[InlineData("gpg")]
	[InlineData("secret-tool")]
	[InlineData("security")]
	[InlineData("vault")]
	[InlineData("keyring")]
	[InlineData("cat")]
	public void A_secret_fetched_from_a_store_or_a_file_holds_none(string store)
	{
		foreach (var value in new[] { $"echo password=$({store} show x)", $"echo password=`{store} show x`", $"echo password=$( {store} show x)",
				$"echo password=\"$({store} show x)\"", $"--password $({store} show x)" })
			Assert.False(AutoRollout.HelperSecret.IsMatch(value), value);
		foreach (var value in new[] { $"Authorization: Bearer \"$({store} show x)\"", $"bearer `{store} show x`", $"sshpass -p $({store} show x) ssh" })
			Assert.False(AutoRollout.TokenShape.IsMatch(value), value);
		foreach (var value in new[] { $"echo password=$({store}x show y)", $"echo password=`{store}x y`" })
			Assert.True(AutoRollout.HelperSecret.IsMatch(value), $"{value}: another program than {store}, which may hold it itself");
		Assert.Matches(AutoRollout.TokenShape, $"sshpass -p $({store}ed x) ssh");
	}

	[Theory]
	[InlineData("echo password=$-x")]
	[InlineData("echo password=$ x")]
	[InlineData("echo password=$$")]
	public void A_dollar_that_starts_no_name_is_read_as_a_password(string value) => Assert.Matches(AutoRollout.HelperSecret, value);

	[Fact]
	public void A_secret_in_a_file_the_config_includes_or_a_credential_store_is_found()
	{
		var tmp = TempDir();
		GitConfigRepo(tmp, ("include.path", "secrets.inc"));
		var common = Path.Combine(tmp, ".git");
		File.WriteAllText(Path.Combine(common, "secrets.inc"), "[remote \"origin\"]\n\turl = https://ghp_abc@github.com/o/r\n");
		Assert.Equal([$"remote.origin.url in {Path.Combine(common, "secrets.inc")}"], AutoRollout.ConfigSecrets(tmp, common));
		File.WriteAllText(Path.Combine(common, "credentials"), "https://u:ghp_abc@github.com\n");
		Assert.Equal($"{Path.Combine(common, "credentials")} (a file named for credentials)", AutoRollout.ConfigSecrets(tmp, common)[0]);
		File.Delete(Path.Combine(common, "credentials"));
		File.WriteAllText(Path.Combine(common, "git-credential-cache"), "");
		Assert.Equal($"{Path.Combine(common, "git-credential-cache")} (a file named for credentials)", AutoRollout.ConfigSecrets(tmp, common)[0]);
	}

	[Fact]
	public void A_submodules_git_config_is_read_too()
	{
		var tmp = TempDir();
		GitConfigRepo(tmp, ("core.bare", "false"));
		var common = Path.Combine(tmp, ".git");
		Assert.Empty(AutoRollout.ConfigSecrets(tmp, common));
		var lib = Path.Combine(common, "modules", "lib");
		var inner = Path.Combine(lib, "modules", "deep", "inner");
		foreach (var (folder, url) in new[] { (lib, "https://ghp_lib@github.com/o/lib"), (inner, "https://ghp_in@github.com/o/inner") })
		{
			Directory.CreateDirectory(folder);
			File.WriteAllText(Path.Combine(folder, "HEAD"), "ref: refs/heads/main\n");
			File.WriteAllText(Path.Combine(folder, "config"), $"[remote \"origin\"]\n\turl = {url}\n");
		}
		Directory.CreateDirectory(Path.Combine(lib, "worktrees", "w"));
		File.WriteAllText(Path.Combine(lib, "worktrees", "w", "HEAD"), "ref: refs/heads/w\n");
		File.WriteAllText(Path.Combine(lib, "worktrees", "w", "config.worktree"), "[http]\n\tproxy = http://u:pw@p:3128\n");
		Directory.CreateDirectory(Path.Combine(lib, "refs", "remotes", "origin"));
		File.WriteAllText(Path.Combine(lib, "refs", "remotes", "origin", "HEAD"), "ref: refs/remotes/origin/main\n");
		File.WriteAllText(Path.Combine(lib, "refs", "remotes", "origin", "config"), "a branch named config, not a git config\n");
		Directory.CreateDirectory(Path.Combine(lib, "objects"));
		File.WriteAllText(Path.Combine(lib, "objects", "config"), "[remote \"x\"]\n\turl = https://ghp_obj@github.com/o/x\n");
		Assert.Equal([$"remote.origin.url in {Path.Combine(lib, "config")}", $"http.proxy in {Path.Combine(lib, "worktrees", "w", "config.worktree")}",
			$"remote.origin.url in {Path.Combine(inner, "config")}"], AutoRollout.ConfigSecrets(tmp, common));
	}

	[Fact]
	public void A_linked_worktrees_submodules_and_sibling_submodules_are_read_in_order()
	{
		// Each folder listed in reverse, so the order the filesystem happens to list in can't pass for sorted.
		static IEnumerable<FileSystemInfo> Reversed(string folder) => new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderByDescending(entry => entry.Name, StringComparer.Ordinal);

		var tmp = TempDir();
		GitConfigRepo(tmp, ("core.bare", "false"));
		var common = Path.Combine(tmp, ".git");
		var wt = Path.Combine(common, "worktrees", "w");
		Directory.CreateDirectory(wt);
		File.WriteAllText(Path.Combine(wt, "HEAD"), "ref: refs/heads/w\n");
		foreach (var folder in new[] { Path.Combine(common, "modules", "b"), Path.Combine(common, "modules", "a"), Path.Combine(wt, "modules", "sub") })
		{
			Directory.CreateDirectory(folder);
			File.WriteAllText(Path.Combine(folder, "HEAD"), "ref: refs/heads/main\n");
			File.WriteAllText(Path.Combine(folder, "config"), "[remote \"origin\"]\n\turl = https://ghp_x@github.com/o/r\n");
		}
		Assert.Equal([Path.Combine(common, "config"), Path.Combine(wt, "modules", "sub", "config"), Path.Combine(common, "modules", "a", "config"),
			Path.Combine(common, "modules", "b", "config")], AutoRollout.ConfigFiles(common, Reversed));
	}

	[Fact]
	public void An_entry_whose_origin_isnt_a_file_is_named_by_the_file_read()
	{
		var tmp = TempDir();
		File.WriteAllText(Path.Combine(tmp, "config"), "");
		var output = "command line:\0remote.a.url\nhttps://t@h/a\0file:/inc\0remote.b.url\nhttps://t@h/b\0";
		Assert.Equal([$"remote.a.url in {Path.Combine(tmp, "config")}", "remote.b.url in /inc"], AutoRollout.ConfigSecrets("/w", tmp, command => new Ran(0, output, "")));
	}

	[Fact]
	public void A_modules_folder_that_cant_be_listed_stops_the_check()
	{
		var tmp = TempDir();
		GitConfigRepo(tmp, ("core.bare", "false"));
		var lib = Path.Combine(tmp, ".git", "modules", "lib");
		Directory.CreateDirectory(lib);
		IEnumerable<FileSystemInfo> Scan(string folder) =>
			folder == lib ? throw new UnauthorizedAccessException($"Access to the path '{lib}' is denied.") : new DirectoryInfo(folder).EnumerateFileSystemInfos();
		var error = Assert.Throws<ConfigUnreadable>(() => AutoRollout.ConfigFiles(Path.Combine(tmp, ".git"), Scan));
		Assert.Matches(@"can't list .*modules/lib, so what of the clone's git config the session could read can't be told \(Permission denied\): make it listable", error.Message);
	}

	[Fact]
	public void A_git_folder_removed_while_it_is_walked_is_skipped()
	{
		// A worktree pruned and a submodule's folder replaced by a file between the listing that named them and their own.
		var tmp = TempDir();
		GitConfigRepo(tmp, ("core.bare", "false"));
		var common = Path.Combine(tmp, ".git");
		var (gone, file) = (Path.Combine(common, "worktrees", "gone"), Path.Combine(common, "modules", "x"));
		Directory.CreateDirectory(gone);
		Directory.CreateDirectory(file);
		IEnumerable<FileSystemInfo> RemovedMeanwhile(string folder)
		{
			if (folder == gone)
				Directory.Delete(gone);
			if (folder == file)
			{
				Directory.Delete(file);
				File.WriteAllText(file, "");
			}
			return new DirectoryInfo(folder).EnumerateFileSystemInfos();
		}
		Assert.Equal([Path.Combine(common, "config")], AutoRollout.ConfigFiles(common, RemovedMeanwhile));
	}

	[Fact]
	public void A_config_file_removed_before_git_reads_it_is_skipped()
	{
		var tmp = TempDir();
		GitConfigRepo(tmp, ("core.bare", "false"));
		var common = Path.Combine(tmp, ".git");
		Directory.CreateDirectory(Path.Combine(common, "worktrees", "w"));
		File.WriteAllText(Path.Combine(common, "worktrees", "w", "HEAD"), "ref: refs/heads/w\n");
		var target = Path.Combine(common, "worktrees", "w", "config.worktree");
		File.WriteAllText(target, "");
		Ran Pruned(IReadOnlyList<string> command)
		{
			if (command[5] != target)
				return new Ran(0, "", "");
			File.Delete(target);
			return new Ran(128, "", "fatal: unable to read config file");
		}
		Assert.Empty(AutoRollout.ConfigSecrets("/w", common, Pruned));
		File.WriteAllText(target, "");
		var error = Assert.Throws<ConfigUnreadable>(() =>
			AutoRollout.ConfigSecrets("/w", common, command => command[5] == target ? new Ran(128, "", "fatal: bad config") : new Ran(0, "", "")));
		Assert.Matches(@"git can't read .*config.worktree \(fatal: bad config\)", error.Message);
	}

	[Fact]
	public void A_git_folder_that_cant_be_listed_stops_the_round()
	{
		var error = Assert.Throws<ConfigUnreadable>(() => AutoRollout.ConfigSecrets("/w", "/nowhere/.git", command => new Ran(0, "", "")));
		Assert.StartsWith("can't list the clone's git folder /nowhere/.git, so whether it holds a sign-in secret can't be told", error.Message);
	}

	[Fact]
	public void A_worktrees_folder_that_cant_be_listed_stops_the_round()
	{
		if (OperatingSystem.IsWindows())
			return; // the scripts run on Linux and macOS
		Assert.SkipWhen(PyLib.Uid() == 0, "root lists a folder whatever its mode");
		var tmp = TempDir();
		var worktrees = Path.Combine(tmp, "worktrees");
		Directory.CreateDirectory(worktrees);
		File.SetUnixFileMode(worktrees, UnixFileMode.None);
		try
		{
			var error = Assert.Throws<ConfigUnreadable>(() => AutoRollout.ConfigSecrets("/w", tmp, command => new Ran(0, "", "")));
			Assert.Matches(@"can't list .*/worktrees, so what of the clone's git config the session could read can't be told \(Permission denied\)", error.Message);
		}
		finally
		{
			File.SetUnixFileMode(worktrees, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	[Fact]
	public void Redacted_hides_every_urls_sign_in() =>
		Assert.Equal("url.https://***@a/.insteadof ssh://***@b", AutoRollout.Redacted("url.https://x:t0k@a/.insteadof ssh://u:p@b"));

	[Theory]
	[InlineData("fatal: bad config https://t0k@host\n", "fatal: bad config https://***@host")]
	[InlineData("", "exit 128")]
	public void A_git_config_that_cant_be_read_is_an_error(string error, string why)
	{
		var tmp = TempDir();
		var config = Path.Combine(tmp, "config");
		File.WriteAllText(config, "");
		var raised = Assert.Throws<ConfigUnreadable>(() => AutoRollout.ConfigSecrets("/w", tmp, command => new Ran(128, "", error)));
		Assert.Equal($"git can't read {config} ({why}): repair it (git config --file {config} --list shows the error)", raised.Message);
	}

	[Fact]
	public void A_linked_worktrees_own_config_is_read_only_with_the_extension()
	{
		var tmp = TempDir();
		var (clone, worktree) = (Path.Combine(tmp, "clone"), Path.Combine(tmp, "wt"));
		Git("init", "-q", clone);
		Git("-C", clone, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "one");
		Git("-C", clone, "worktree", "add", "-q", worktree);
		var common = Path.Combine(clone, ".git");
		Git("-C", clone, "config", "extensions.worktreeConfig", "true");
		Assert.Empty(AutoRollout.ConfigSecrets(worktree, common));
		Git("-C", worktree, "config", "--worktree", "remote.origin.url", "https://tok@github.com/o/r");
		var own = Path.Combine(Directory.EnumerateDirectories(Path.Combine(common, "worktrees")).Single(), "config.worktree");
		Assert.Equal([$"remote.origin.url in {own}"], AutoRollout.ConfigSecrets(worktree, common));
		var other = Path.Combine(tmp, "other");
		Git("-C", clone, "worktree", "add", "-q", other);
		Git("-C", other, "config", "--worktree", "http.proxy", "http://u:p@proxy");
		Git("-C", clone, "config", "--worktree", "lfs.url", "https://tok@lfs.example");
		var found = AutoRollout.ConfigSecrets(worktree, common);
		Assert.Contains($"lfs.url in {Path.Combine(common, "config.worktree")}", found);
		Assert.Contains(found, line => line.StartsWith("http.proxy in ", StringComparison.Ordinal) && line.Contains("/worktrees/"));
	}
}
