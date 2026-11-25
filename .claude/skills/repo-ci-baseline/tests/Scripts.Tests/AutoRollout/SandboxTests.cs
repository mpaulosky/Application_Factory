using static SandboxFixtures;

// Tests for github/AutoRolloutSandbox.cs's settings and denials: what the review session's Bash and file tools may read, write and run
// (copied from github/tests/test_auto_rollout.py, deleted with the cut-over, mpaulosky/Application_Factory#122).
public class SandboxTests
{
	static readonly Dictionary<string, string[]> Root = new()
	{
		["/"] = ["bin", "c", "etc", "h", "home", "opt", "t", "tmp", "usr", "w"],
		["/c"] = ["clone", "other"],
		["/c/clone"] = [".git", ".security", "src"],
		["/t"] = ["session", "y"],
		["/w"] = ["wt", "x"],
	};

	[Fact]
	public void The_session_settings_sandbox_its_bash_and_fence_its_file_tools()
	{
		var (home, common) = ("/h", "/c/clone/.git");
		var gitdir = PyLib.Join(PyLib.Join(common, "worktrees"), "wt");
		var settings = SettingsOf("/w/wt", common, gitdir, "/t/session", home: home, configDir: "", environ: Env(), listdir: Tree(Root));
		var sandbox = settings["sandbox"]!;
		Assert.True(sandbox["enabled"]!.GetValue<bool>());
		Assert.True(sandbox["failIfUnavailable"]!.GetValue<bool>());
		Assert.False(sandbox["allowUnsandboxedCommands"]!.GetValue<bool>());
		Assert.Empty(Strs(sandbox["excludedCommands"]));
		var security = "/c/clone/.security";
		List<string> credentials = [.. AutoRollout.CredentialPaths.Select(path => home + path[1..])];
		var filesystem = sandbox["filesystem"]!.AsObject();
		Assert.Equal(["allowWrite", "denyWrite", "denyRead"], filesystem.Select(pair => pair.Key));
		Assert.Equal(["/t/session"], Strs(filesystem["allowWrite"]));
		Assert.Equal(["/w/wt", gitdir], Strs(filesystem["denyWrite"]));
		Assert.Equal([.. credentials, "/c/clone/src", "/c/other", home, "/home", "/opt", "/t/y", "/tmp", "/w/x", .. Held], Strs(filesystem["denyRead"]));
		Assert.Equal(AutoRollout.SandboxDomains, Strs(sandbox["network"]!["allowedDomains"]));
		var allow = Allow(settings);
		Assert.Equal([.. AutoRollout.SessionTools, "Read(//w/wt/**)", $"Read(/{security}/**)", "Read(//t/session/**)", "Edit(//t/session/**)"], allow);
		Assert.Empty(new[] { "Read", "Glob", "Grep" }.Intersect(allow));
		var deny = Deny(settings);
		Assert.Equal(AutoRollout.DeniedTools, deny.Take(AutoRollout.DeniedTools.Count));
		foreach (var path in credentials)
			Assert.True(deny.Contains($"Read(/{path})") && deny.Contains($"Read(/{path}/**)"), path);
		foreach (var path in new[] { "/w/wt", gitdir })
			Assert.True(deny.Contains($"Edit(/{path})") && deny.Contains($"Edit(/{path}/**)"), path);
		Assert.DoesNotContain(allow.Concat(deny), rule => rule.StartsWith("Write(", StringComparison.Ordinal));
	}

	static readonly Dictionary<string, string[]> Home = new()
	{
		["/"] = ["h", "usr"],
		["/h"] = ["github", ".cache", ".config", ".ssh", "notes.txt"],
		["/h/github"] = ["App", "App-worktrees", "Other"],
		["/h/github/App"] = [".git", ".security", "src"],
		["/h/github/App-worktrees"] = ["wt", "old"],
		["/h/.cache"] = ["repo-ci-baseline", "sessions"],
		["/h/.cache/sessions"] = ["mine", "theirs"],
	};

	static IEnumerable<string> FakeListdir(string folder) =>
		Home.TryGetValue(folder, out var names) ? names : throw new IOException($"[Errno 2] No such file or directory: '{folder}'");

	[Fact]
	public void Denied_around_hides_everything_beside_the_way_to_what_is_allowed()
	{
		string[] allowed = ["/h/github/App-worktrees/wt", "/h/github/App/.git", "/h/github/App/.security"];
		Assert.Equal(["/h/.cache", "/h/.config", "/h/.ssh", "/h/github/App/src", "/h/github/App-worktrees/old", "/h/github/Other", "/h/notes.txt"],
			AutoRollout.DeniedAround("/h", allowed, FakeListdir));
		Assert.Equal(["/tmp"], AutoRollout.DeniedAround("/tmp", allowed, FakeListdir));
		Assert.Empty(AutoRollout.DeniedAround("/h/github/App-worktrees/wt", allowed, FakeListdir));
	}

	[Fact]
	public void Denied_around_stops_at_a_folder_on_the_way_it_cant_list()
	{
		var error = Assert.Throws<IOException>(() => AutoRollout.DeniedAround("/h/unlisted", ["/h/unlisted/wt"], FakeListdir));
		Assert.StartsWith("can't list /h/unlisted, which is on the way to the session's folders", error.Message);
		Assert.Equal("/h/notes.txt", AutoRollout.DeniedAround("/h", ["/h/github/App"], FakeListdir)[^1]);
	}

	[Fact]
	public void Denied_around_takes_a_trailing_slash_and_a_name_that_only_starts_alike()
	{
		Assert.Equal(["/h/github/App-worktrees", "/h/github/Other"], AutoRollout.DeniedAround("/h/github/", ["/h/github/App/"], FakeListdir));
		Assert.Equal(["/h/github/App", "/h/github/App-worktrees/old", "/h/github/Other"],
			AutoRollout.DeniedAround("/h/github", ["/h/github/App-worktrees/wt"], FakeListdir));
	}

	[Theory]
	[InlineData("a[1].txt")]
	[InlineData("notes?.md")]
	[InlineData("b*")]
	[InlineData("{x}")]
	public void Denied_around_refuses_an_entry_named_as_a_pattern(string name)
	{
		var error = Assert.Throws<UnnameableError>(() => AutoRollout.DeniedAround("/h", ["/h/wt"], Tree(new() { ["/h"] = [name, "wt"] })));
		Assert.Contains("which the sandbox may read as a pattern: rename or move it", error.Message);
		Assert.Empty(AutoRollout.DeniedAround("/h", ["/h/my[app]/wt"], Tree(new() { ["/h"] = ["my[app]"], ["/h/my[app]"] = ["wt"] })));
		error = Assert.Throws<UnnameableError>(() => AutoRollout.DeniedAround("/h", ["/h/my[app]/wt"], Tree(new() { ["/h"] = ["my[app]"], ["/h/my[app]"] = ["wt", "other"] })));
		Assert.Matches(@"^'/h/my\[app\]/other' must be hidden from the session, but the folder it is in, '/h/my\[app\]', holds one of .*: rename that folder, or move the clone"
			+ " out of it, then resume", error.Message);
	}

	[Theory]
	[InlineData('*')]
	[InlineData('?')]
	[InlineData('[')]
	[InlineData(']')]
	[InlineData('{')]
	[InlineData('}')]
	public void Each_glob_character_is_refused_alone(char character)
	{
		Assert.Equal("*?[]{}", string.Concat(AutoRollout.GlobCharacters));
		var error = Assert.Throws<UnnameableError>(() => AutoRollout.DeniedAround("/h", ["/h/wt"], Tree(new() { ["/h"] = [$"a{character}b", "wt"] })));
		Assert.Contains("which the sandbox may read as a pattern", error.Message);
	}

	[Fact]
	public void Denied_around_refuses_a_name_that_isnt_utf_8()
	{
		// Python's surrogateescape form of the byte 0xe9, and .NET's reading of it, U+FFFD (on purpose: a rule naming it names another path).
		foreach (var name in new[] { "caf\udce9", "caf\ufffd" })
		{
			var error = Assert.Throws<UnnameableError>(() => AutoRollout.DeniedAround("/h", ["/h/wt"], Tree(new() { ["/h"] = [name, "wt"] })));
			Assert.Contains("isn't UTF-8, so the sandbox can't name it", error.Message);
		}
		var nested = Assert.Throws<UnnameableError>(() =>
			AutoRollout.DeniedAround("/h", ["/h/caf\udce9/wt"], Tree(new() { ["/h"] = ["caf\udce9"], ["/h/caf\udce9"] = ["wt", "other"] })));
		Assert.Matches(@"but the folder it is in, '/h/caf.udce9', isn't UTF-8", nested.Message);
	}

	[Fact]
	public void Denied_around_hides_both_sides_of_a_symlinked_folder()
	{
		var tmp = TempDir();
		var real = Path.Combine(tmp, "data", "github");
		foreach (var folder in new[] { "App-worktrees/wt", "App/.git", "App/.security", "Other" })
			Directory.CreateDirectory(Path.Combine(real, folder));
		File.WriteAllText(Path.Combine(real, "Other", ".env"), "SECRET=1\n");
		Directory.CreateDirectory(Path.Combine(tmp, "home"));
		File.CreateSymbolicLink(Path.Combine(tmp, "home", "github"), real);
		File.WriteAllText(Path.Combine(tmp, "home", "notes.txt"), "x");
		var link = Path.Combine(tmp, "home", "github");
		var reads = AutoRollout.SessionReads(Path.Combine(link, "App-worktrees", "wt"), Path.Combine(link, "App", ".git"),
			Path.Combine(link, "App", ".security"), Path.Combine(tmp, "home", "session"));
		Directory.CreateDirectory(Path.Combine(tmp, "home", "session"));
		var denied = AutoRollout.DeniedAround(tmp, reads);
		Assert.Contains(Path.Combine(link, "Other"), denied);
		Assert.Contains(Path.Combine(real, "Other"), denied);
		Assert.Contains(Path.Combine(tmp, "home", "notes.txt"), denied);
		Assert.DoesNotContain(denied, path => path.StartsWith(Path.Combine(real, "App-worktrees", "wt"), StringComparison.Ordinal));
	}

	[Fact]
	public void The_sessions_bash_reads_only_its_folders_of_home()
	{
		var settings = SettingsOf("/h/github/App-worktrees/wt", "/h/github/App/.git", "/h/github/App/.git/worktrees/wt", "/h/.cache/sessions/mine",
			home: "/h", environ: Env(), listdir: FakeListdir);
		var denied = DenyRead(settings);
		foreach (var path in new[] { "/h/.config", "/h/notes.txt", "/h/github/Other", "/h/github/App/src", "/h/github/App-worktrees/old", "/h/.cache/sessions/theirs" }.Concat(Held))
			Assert.Contains(path, denied);
		foreach (var path in new[] { "/", "/h", "/h/github", "/h/github/App-worktrees/wt", "/h/github/App/.git", "/h/github/App/.security", "/h/.cache/sessions/mine", "/usr" })
			Assert.DoesNotContain(path, denied);
		Assert.Single(denied, path => path == "/h/.ssh");
		Assert.Single(denied, path => path == "/h/.cache/repo-ci-baseline");
		Assert.Equal(denied.Count, denied.Distinct().Count());
		Assert.False(settings["sandbox"]!["filesystem"]!.AsObject().ContainsKey("allowRead"));
	}

	[Fact]
	public void A_clone_outside_home_keeps_the_applications_beside_it_hidden()
	{
		var tree = Tree(new()
		{
			["/"] = ["h", "home", "opt", "run", "srv", "tmp", "usr"],
			["/home"] = ["someone"],
			["/run"] = ["lock", "user"],
			["/srv"] = ["code"],
			["/srv/code"] = ["App", "App-worktrees", "Other"],
			["/srv/code/App"] = [".git", ".security"],
			["/srv/code/App-worktrees"] = ["wt"],
			["/h"] = [".cache"],
			["/h/.cache"] = ["sessions"],
			["/h/.cache/sessions"] = ["mine"],
		});
		var denied = DenyRead(SettingsOf("/srv/code/App-worktrees/wt", "/srv/code/App/.git", "/srv/code/App/.git/worktrees/wt", "/h/.cache/sessions/mine",
			home: "/h", environ: Env(), listdir: tree));
		foreach (var path in new[] { "/srv/code/Other", "/home", "/opt", "/tmp", "/run/user", "/run/lock" })
			Assert.Contains(path, denied);
		foreach (var path in new[] { "/run", "/usr", "/srv/code/App-worktrees/wt", "/srv/code/App/.git" })
			Assert.DoesNotContain(path, denied);
	}

	[Fact]
	public void The_system_folders_are_read_and_the_rest_of_run_and_dev_shm_denied()
	{
		Assert.Equal(["/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/etc", "/proc", "/dev", "/sys", "/nix",
			"/run/current-system", "/run/booted-system", "/run/wrappers"], AutoRollout.SystemReads);
		Assert.Equal(["/dev/shm"], AutoRollout.SystemDenied);
		string[] names = ["bin", "dev", "etc", "h", "lib", "lib32", "lib64", "libx32", "nix", "proc", "run", "sbin", "sys", "usr"];
		var tree = Tree(new()
		{
			["/"] = names,
			["/h"] = ["wt"],
			["/run"] = ["credentials", "current-system", "booted-system", "docker.sock", "media", "secrets", "user", "wrappers"],
		});
		var denied = DenyRead(SettingsOf("/h/wt", "/h/wt/.git", "/h/wt/.git", "/h/s", home: "/h", environ: Env(), listdir: tree));
		foreach (var path in new[] { "/run/credentials", "/run/docker.sock", "/run/media", "/run/secrets", "/run/user", "/dev/shm" })
			Assert.Contains(path, denied);
		foreach (var path in names.Where(name => name is not ("h" or "run")).Select(name => $"/{name}")
				.Concat(["/run/current-system", "/run/booted-system", "/run/wrappers", "/dev"]))
			Assert.DoesNotContain(path, denied);
	}

	[Fact]
	public void A_clone_on_a_mounted_drive_or_in_shared_memory_stays_readable()
	{
		var tree = Tree(new()
		{
			["/"] = ["dev", "h", "run"],
			["/run"] = ["media", "user"],
			["/run/media"] = ["u"],
			["/run/media/u"] = ["disk", "other"],
			["/run/media/u/disk"] = ["App", "App-worktrees"],
			["/run/media/u/disk/App"] = [".git", ".security"],
			["/run/media/u/disk/App-worktrees"] = ["wt"],
			["/h"] = ["s"],
		});
		var denied = DenyRead(SettingsOf("/run/media/u/disk/App-worktrees/wt", "/run/media/u/disk/App/.git", "/run/media/u/disk/App/.git/worktrees/wt", "/h/s",
			home: "/h", environ: Env(), listdir: tree));
		Assert.Contains("/run/media/u/other", denied);
		Assert.Contains("/run/user", denied);
		Assert.DoesNotContain(denied, path => path.Contains("/run/media/u/disk/App", StringComparison.Ordinal) || path is "/run" or "/run/media");
		var shm = Tree(new() { ["/dev/shm"] = ["work", "theirs"], ["/dev/shm/work"] = ["App"], ["/dev/shm/work/App"] = [".git", "wt"] });
		var found = AutoRollout.ReadDenials(["/dev/shm/work/App/wt", "/dev/shm/work/App/.git"], [], "/h", shm, _ => null);
		Assert.Contains("/dev/shm/theirs", found);
		Assert.DoesNotContain("/dev/shm", found);
		Assert.DoesNotContain("/dev/shm/work/App/wt", found);
	}

	[Fact]
	public void The_sessions_bash_reads_its_folders_where_their_symlinks_lead_too()
	{
		var tmp = TempDir();
		var real = Path.Combine(tmp, "real-worktree");
		Directory.CreateDirectory(real);
		File.CreateSymbolicLink(Path.Combine(tmp, "wt"), real);
		Assert.Equal([real, Path.Combine(tmp, "wt"), "/c/.git", "/c/.security", "/t"], AutoRollout.SessionReads(Path.Combine(tmp, "wt"), "/c/.git", "/c/.security", "/t"));
	}

	[Fact]
	public void A_tools_own_install_prefix_is_read_and_put_first_on_path()
	{
		static string? Which(string name) => name == "node" ? "/h/.nvm/versions/node/v24/bin/node" : null;

		var (reads, first) = AutoRollout.ToolReads("/h", Which, NoAnswer);
		Assert.Equal(["/h/.nvm/versions/node/v24/bin/node", "/h/.nvm/versions/node/v24"], reads);
		Assert.Equal(["/h/.nvm/versions/node/v24/bin"], first);
		var tree = Tree(new()
		{
			["/"] = ["h"],
			["/h"] = [".nvm", "wt"],
			["/h/.nvm"] = ["versions"],
			["/h/.nvm/versions"] = ["node"],
			["/h/.nvm/versions/node"] = ["v23", "v24"],
			["/h/.nvm/versions/node/v24"] = ["bin", "include", "lib"],
		});
		var denied = DenyRead(SettingsOf("/h/wt", "/h/wt/.git", "/h/wt/.git", "/h/s", home: "/h", environ: Env(), listdir: tree, which: Which));
		Assert.Contains("/h/.nvm/versions/node/v23", denied);
		Assert.DoesNotContain(denied, path => path.StartsWith("/h/.nvm/versions/node/v24", StringComparison.Ordinal));
	}

	[Fact]
	public void A_shim_is_asked_where_its_tool_really_is()
	{
		var answers = new Dictionary<string, string> { ["node"] = "/h/.volta/tools/image/node/24.1.0/bin/node\n", ["--exec-path"] = "/usr/lib/git-core\n" };
		Ran Run(IReadOnlyList<string> command) => new(0, answers[command.Skip(1).SequenceEqual(["-p", "process.execPath"]) ? "node" : command[^1]], "");
		var which = new Dictionary<string, string> { ["node"] = "/h/.volta/bin/node", ["git"] = "/usr/bin/git" };

		var (reads, first) = AutoRollout.ToolReads("/h", which.GetValueOrDefault, Run);
		Assert.Equal(["/h/.volta/bin/node", "/h/.volta/tools/image/node/24.1.0/bin/node", "/h/.volta/tools/image/node/24.1.0", "/usr/bin/git", "/usr/lib/git-core"], reads);
		Assert.Equal(["/h/.volta/tools/image/node/24.1.0/bin"], first);
		Assert.Equal(["/h/.volta/bin/node"],
			AutoRollout.ToolReads("/h", name => name == "node" ? "/h/.volta/bin/node" : null, command => new Ran(0, "relative/node\n", "")).Reads);
	}

	static List<string> BrewParts(string prefix, Func<string, string>? realpath = null) =>
		[.. AutoRollout.BothForms(prefix, realpath).SelectMany(form => AutoRollout.HomebrewParts.Select(part => $"{form}/{part}"))];

	[Fact]
	public void A_homebrew_install_reads_its_formulaes_folders_not_its_etc_or_var()
	{
		var home = Path.Combine(TempDir(), "me");
		var cellar = Path.Combine(home, ".linuxbrew", "Cellar", "git", "2.50", "bin");
		Directory.CreateDirectory(cellar);
		File.WriteAllText(Path.Combine(cellar, "git"), "");
		Directory.CreateDirectory(Path.Combine(home, ".linuxbrew", "bin"));
		File.CreateSymbolicLink(Path.Combine(home, ".linuxbrew", "bin", "git"), Path.Combine(cellar, "git"));
		var (reads, first) = AutoRollout.ToolReads(home, name => name == "git" ? Path.Combine(home, ".linuxbrew", "bin", name) : null, NoAnswer);
		Assert.Equal([Path.Combine(home, ".linuxbrew", "bin", "git"), Path.Combine(cellar, "git"), .. BrewParts(Path.Combine(home, ".linuxbrew"))], reads);
		Assert.Equal([cellar], first);
		Assert.Equal(["/home/linuxbrew/.linuxbrew"], AutoRollout.HomebrewPrefixes);
		Assert.Equal(["bin", "sbin", "Cellar", "opt", "lib", "libexec", "share", "include", "etc/gitconfig"], AutoRollout.HomebrewParts);
		// The literal paths below stand for a host whose /home is no link, whatever this one's is; a linked /home has
		// A_tools_install_is_found_through_a_linked_home_or_prefix.
		static string Same(string path) => path;
		var brew = "/home/linuxbrew/.linuxbrew/Cellar/git/2.50/bin/git";
		var tools = AutoRollout.ToolReads("/h", name => name == "git" ? brew : null, NoAnswer, realpath: Same);
		Assert.Equal([brew, .. BrewParts("/home/linuxbrew/.linuxbrew", Same)], tools.Reads);
		Assert.Equal(["/home/linuxbrew/.linuxbrew/Cellar/git/2.50/bin"], tools.First);
		var tree = Tree(new()
		{
			["/"] = ["h", "home"],
			["/h"] = ["wt"],
			["/home"] = ["linuxbrew"],
			["/home/linuxbrew"] = [".linuxbrew"],
			["/home/linuxbrew/.linuxbrew"] = ["Cellar", "etc", "opt", "var"],
			["/home/linuxbrew/.linuxbrew/etc"] = ["gitconfig", "redis.conf"],
		});
		var denied = DenyRead(SettingsOf("/h/wt", "/h/wt/.git", "/h/wt/.git", "/h/s", home: "/h", environ: Env(), listdir: tree, tools: tools));
		Assert.Contains("/home/linuxbrew/.linuxbrew/etc/redis.conf", denied);
		Assert.Contains("/home/linuxbrew/.linuxbrew/var", denied);
		Assert.DoesNotContain("/home/linuxbrew/.linuxbrew/etc/gitconfig", denied);
		Assert.DoesNotContain("/home/linuxbrew/.linuxbrew/Cellar", denied);
		foreach (var found in new[] { "/opt/Cellar/git/2.50/bin/git", "/mnt/data/projects/Cellar/x/1/bin/git", "/home/linuxbrew/.linuxbrew/Cellar/git",
				"/home/linuxbrew/.linuxbrew/other/Cellar/git/2/bin/git" })
		{
			var (alone, none) = AutoRollout.ToolReads("/h", name => name == "git" ? found : null, NoAnswer, realpath: Same);
			Assert.Equal([found], alone);
			Assert.Empty(none);
		}
	}

	[Theory]
	[InlineData(".nvm/versions/node/v24")]
	[InlineData(".asdf/installs/nodejs/24.1.0")]
	[InlineData(".local/share/mise/installs/node/24")]
	[InlineData(".volta/tools/image/node/24.1.0")]
	[InlineData(".local/share/fnm/node-versions/v24/installation")]
	[InlineData(".fnm/node-versions/v24/installation")]
	[InlineData(".local/share/pnpm/nodejs/24.1.0")]
	public void A_version_managers_version_folder_is_read_whole(string prefix)
	{
		var found = $"/h/{prefix}/bin/node";
		var (reads, first) = AutoRollout.ToolReads("/h", name => name == "node" ? found : null, NoAnswer);
		Assert.Equal([found, $"/h/{prefix}"], reads);
		Assert.Equal([$"/h/{prefix}/bin"], first);
		(reads, first) = AutoRollout.ToolReads("/home/u", name => name == "node" ? found : null, NoAnswer);
		Assert.Equal([found], reads);
		Assert.Empty(first);
	}

	[Theory]
	[InlineData(".nvm/versions/node")]
	[InlineData(".asdf/installs/nodejs")]
	[InlineData(".asdf/installs/a/b/c")]
	[InlineData(".local/share/mise/installs/node")]
	[InlineData(".local/share/mise/installs/a/b/c")]
	[InlineData(".volta/tools/image/node")]
	[InlineData(".volta/tools/image/a/b/c")]
	[InlineData(".local/share/fnm/node-versions/v24")]
	[InlineData(".local/share/fnm/node-versions/v24/installation/x")]
	[InlineData(".fnm/node-versions/v24/x")]
	[InlineData(".local/share/pnpm/nodejs")]
	[InlineData(".local/share/pnpm/nodejs/24/x")]
	[InlineData("x/.asdf/installs/nodejs/24")]
	[InlineData("x/.local/share/mise/installs/node/24")]
	[InlineData("x/.volta/tools/image/node/24")]
	[InlineData("x/.fnm/node-versions/v24/installation")]
	[InlineData("x/.local/share/pnpm/nodejs/24")]
	public void A_folder_beside_a_version_managers_layout_isnt_read_whole(string prefix)
	{
		var found = $"/h/{prefix}/bin/node";
		var (reads, first) = AutoRollout.ToolReads("/h", name => name == "node" ? found : null, NoAnswer);
		Assert.Equal([found], reads);
		Assert.Empty(first);
	}

	[Fact]
	public void A_tools_install_is_found_through_a_linked_home_or_prefix()
	{
		var tmp = TempDir();
		var real = Path.Combine(tmp, "var", "home");
		var sharedGit = Path.Combine(real, "linuxbrew", ".linuxbrew", "Cellar", "git", "2", "bin");
		var node = Path.Combine(real, "me", ".nvm", "versions", "node", "v24", "bin");
		var moved = Path.Combine(tmp, "disk", "brew", "Cellar", "gh", "1", "bin");
		foreach (var folder in new[] { sharedGit, node, moved })
			Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(sharedGit, "git"), "");
		File.WriteAllText(Path.Combine(node, "node"), "");
		var link = Path.Combine(tmp, "home");
		File.CreateSymbolicLink(link, real);
		var found = new Dictionary<string, string>
		{
			["git"] = Path.Combine(link, "linuxbrew", ".linuxbrew", "Cellar", "git", "2", "bin", "git"),
			["node"] = Path.Combine(link, "me", ".nvm", "versions", "node", "v24", "bin", "node"),
		};
		var (reads, first) = AutoRollout.ToolReads(Path.Combine(link, "me"), found.GetValueOrDefault, NoAnswer, prefixes: [Path.Combine(link, "linuxbrew", ".linuxbrew")]);
		Assert.Contains($"{link}/linuxbrew/.linuxbrew/Cellar", reads);
		Assert.Contains($"{real}/linuxbrew/.linuxbrew/Cellar", reads);
		Assert.Contains(Path.Combine(real, "me", ".nvm", "versions", "node", "v24"), reads);
		Assert.Equal(new HashSet<string> { sharedGit, node }, first.ToHashSet());
		File.CreateSymbolicLink(Path.Combine(real, "me", ".linuxbrew"), Path.Combine(tmp, "disk", "brew"));
		File.WriteAllText(Path.Combine(moved, "gh"), "");
		(reads, _) = AutoRollout.ToolReads(Path.Combine(real, "me"), _ => Path.Combine(moved, "gh"), NoAnswer, commands: ["gh"], prefixes: [Path.Combine(link, "linuxbrew", ".linuxbrew")]);
		Assert.Contains($"{tmp}/disk/brew/Cellar", reads);
		Assert.Contains($"{real}/me/.linuxbrew/Cellar", reads);
	}

	[Fact]
	public void A_tool_in_a_shared_folder_is_read_as_its_file_alone()
	{
		static void FileAlone(string home, string found)
		{
			var (reads, first) = AutoRollout.ToolReads(home, name => name == "git" ? found : null, NoAnswer);
			Assert.Equal([found], reads);
			Assert.Empty(first);
		}

		foreach (var found in new[] { "/h/.local/bin/git", "/h/bin/git", "/h/git" })
			FileAlone("/h", found);
		foreach (var found in new[] { "/opt/zz-none/bin/git", "/mnt/data/bin/git", "/home/other/bin/git", "/home/other/a/b/c/bin/git", "/h/tools/bin/git", "/h/a/b/bin/git",
				"/h/work/clients/acme/bin/node", "/h/.nvm/versions/node/v24/git", "/h/.nvm/versions/node/v24/lib/git", "/h/.nvm/versions/node/v24/x/bin/git",
				"/h/x/.nvm/versions/node/v24/bin/git" })
			FileAlone("/h", found);
		FileAlone("/home/u", "/home/bin/git");
		foreach (var found in new[] { "/opt/bin/git", "/srv/bin/git" })
			FileAlone("/h", found);
		var tree = Tree(new() { ["/"] = ["h", "opt"], ["/h"] = ["wt"], ["/opt"] = ["bin", "other-app"], ["/opt/bin"] = ["git", "secret-tool"] });
		var denied = DenyRead(SettingsOf("/h/wt", "/h/wt/.git", "/h/wt/.git", "/h/s", home: "/h", environ: Env(), listdir: tree,
			which: name => name == "git" ? "/opt/bin/git" : null));
		Assert.Contains("/opt/other-app", denied);
		Assert.Contains("/opt/bin/secret-tool", denied);
		Assert.DoesNotContain("/opt/bin/git", denied);
		var relative = AutoRollout.ToolReads("/h", _ => "node_modules/.bin/node", NoAnswer);
		Assert.Empty(relative.Reads);
		Assert.Empty(relative.First);
		var tmp = TempDir();
		Directory.CreateDirectory(Path.Combine(tmp, "real", "bin"));
		File.CreateSymbolicLink(Path.Combine(tmp, "home"), Path.Combine(tmp, "real"));
		File.WriteAllText(Path.Combine(tmp, "real", "bin", "git"), "");
		var linked = Path.Combine(tmp, "home", "bin", "git");
		var (linkedReads, linkedFirst) = AutoRollout.ToolReads(Path.Combine(tmp, "home"), name => name == "git" ? linked : null, NoAnswer);
		Assert.Equal([linked, Path.Combine(tmp, "real", "bin", "git")], linkedReads);
		Assert.Empty(linkedFirst);
	}

	[Fact]
	public void Unlisted_since_names_only_whats_new()
	{
		var tree = Tree(new() { ["/"] = ["h", "new", "old"], ["/h"] = ["wt"] });
		Assert.Equal(["/new"], AutoRollout.UnlistedSince(["/old", .. Held], ["/h/wt"], [], "/h", tree, _ => null));
	}

	[Fact]
	public void A_listed_credential_that_is_a_symlink_is_denied_where_it_leads()
	{
		// ~/.bashrc and ~/.gitconfig are often links into a dotfiles repo.
		var tmp = TempDir();
		var home = Path.Combine(tmp, "home");
		Directory.CreateDirectory(Path.Combine(home, ".config"));
		Directory.CreateDirectory(Path.Combine(tmp, "dotfiles", "git"));
		File.WriteAllText(Path.Combine(tmp, "dotfiles", "bashrc"), "export TOKEN=x\n");
		File.CreateSymbolicLink(Path.Combine(home, ".bashrc"), Path.Combine(tmp, "dotfiles", "bashrc"));
		File.CreateSymbolicLink(Path.Combine(home, ".config", "git"), Path.Combine(tmp, "dotfiles", "git"));
		var settings = SettingsOf("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home: home, environ: Env());
		var found = DenyRead(settings);
		foreach (var path in new[] { Path.Combine(home, ".bashrc"), Path.Combine(tmp, "dotfiles", "bashrc"), Path.Combine(home, ".config", "git"), Path.Combine(tmp, "dotfiles", "git") })
		{
			Assert.Contains(path, found);
			Assert.Contains($"Read(/{path})", Deny(settings));
		}
		Assert.Equal(found.Count, found.Distinct().Count());
	}

	[Fact]
	public void A_credential_named_twice_is_denied_once()
	{
		var environ = Env(("GIT_CONFIG_GLOBAL", "~/.gitconfig"), ("GH_CONFIG_DIR", "~/.config/gh"), ("KUBECONFIG", "/k:/k"));
		var found = DenyRead(SettingsOf("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home: "/h", environ: environ));
		Assert.Equal(found.Count, found.Distinct().Count());
		Assert.Single(found, path => path == "/h/.gitconfig");
		Assert.Single(found, path => path == "/k");
		Assert.Equal(["/k"], DeniedBeyondTheList(environ));
	}

	[Theory]
	[InlineData("~", "/h")]
	[InlineData("~/x", "/h/x")]
	[InlineData("~user/x", "~user/x")]
	[InlineData("a~/x", "a~/x")]
	[InlineData("/abs", "/abs")]
	public void Expand_home_takes_only_a_tilde_of_its_own(string path, string expanded) => Assert.Equal(expanded, AutoRollout.ExpandHome(path, "/h"));

	// What the settings deny beyond CredentialPaths and Held, each for the file tools too (denied_beyond_the_list).
	public static List<string> DeniedBeyondTheList(IReadOnlyDictionary<string, string> environ, string home = "/h")
	{
		var settings = SettingsOf("/w", "/c/.git", "/c/.git/worktrees/w", "/t", home: home, environ: environ);
		List<string> listed = [.. AutoRollout.CredentialPaths.Select(path => home + path[1..])];
		var found = DenyRead(settings);
		Assert.Equal(listed, found.Take(listed.Count));
		Assert.Equal(Held, found.TakeLast(Held.Length));
		found = found[..^Held.Length];
		foreach (var path in found)
		{
			Assert.Contains($"Read(/{path})", Deny(settings));
			Assert.Contains($"Read(/{path}/**)", Deny(settings));
		}
		return found[listed.Count..];
	}

	[Theory]
	[InlineData("/cfg/claude/", new[] { "/cfg/claude" })]
	[InlineData("/cfg//claude", new[] { "/cfg/claude" })]
	[InlineData("~/cfg", new[] { "/h/cfg" })]
	[InlineData("  /cfg/claude  ", new[] { "/cfg/claude" })]
	[InlineData("", new string[0])]
	[InlineData("   ", new string[0])]
	public void Claude_codes_config_folder_is_denied_as_an_absolute_path(string value, string[] denied) =>
		Assert.Equal(denied, DeniedBeyondTheList(Env(("CLAUDE_CONFIG_DIR", value))));

	[Fact]
	public void Where_the_environment_moves_a_credential_is_denied_too()
	{
		var environ = Env(("GH_CONFIG_DIR", "/gh"), ("DOCKER_CONFIG", "~/dock"), ("KUBECONFIG", "/k/a:/k/b"), ("AWS_SHARED_CREDENTIALS_FILE", "/aws/c"),
			("AWS_CONFIG_FILE", "/aws/f"), ("NPM_CONFIG_USERCONFIG", "/n"), ("npm_config_userconfig", "/n2"), ("GNUPGHOME", "/g"), ("CLOUDSDK_CONFIG", "/gc"),
			("AZURE_CONFIG_DIR", "/az"), ("PASSWORD_STORE_DIR", "/ps"), ("GIT_CONFIG_GLOBAL", "/gitc"), ("XDG_CONFIG_HOME", "/x"), ("KUBE_OTHER", "/no"));
		Assert.Equal(["/gh", "/h/dock", "/k/a", "/k/b", "/aws/c", "/aws/f", "/n", "/n2", "/g", "/gc", "/az", "/ps", "/gitc",
			.. AutoRollout.XdgConfigSubfolders.Select(folder => $"/x/{folder}")], DeniedBeyondTheList(environ));
		Assert.Empty(DeniedBeyondTheList(Env(("KUBECONFIG", ":"), ("XDG_CONFIG_HOME", " "))));
	}

	[Fact]
	public void Expand_home_replaces_a_leading_tilde_only()
	{
		Assert.Equal("/h/.ssh", AutoRollout.ExpandHome("~/.ssh", "/h"));
		Assert.Equal("/a/~b", AutoRollout.ExpandHome("/a/~b", "/h"));
	}

	// The assertions of test_the_sandbox_and_session_lists_word_for_word on this part's lists; ReviewRecordTests and RoundConstantsTests hold
	// the rest of that test's lists.
	[Fact]
	public void The_sandbox_and_session_lists_word_for_word()
	{
		Assert.Equal(["Read", "Write", "Edit", "Glob", "Grep", "Bash", "Agent", "TodoWrite", "SendMessage"], AutoRollout.SessionBuiltins);
		Assert.Equal(["Bash", "Agent", "Task", "TodoWrite", "SendMessage"], AutoRollout.SessionTools);
		Assert.Equal([
			"~/.ssh", "~/.config/gh", "~/.config/glab-cli", "~/.config/hub", "~/.git-credentials", "~/.config/git/credentials",
			"~/.netrc", "~/.gnupg", "~/.docker", "~/.aws", "~/.azure", "~/.config/gcloud", "~/.kube", "~/.terraform.d",
			"~/.npmrc", "~/.yarnrc", "~/.yarnrc.yml", "~/.config/pnpm", "~/.nuget/NuGet", "~/.config/NuGet", "~/.pypirc",
			"~/.config/pip", "~/.config/uv", "~/.cargo/credentials", "~/.cargo/credentials.toml", "~/.gem/credentials",
			"~/.m2/settings.xml", "~/.local/share/keyrings", "~/.password-store", "~/.vault-token", "~/.config/op",
			"~/.pgpass", "~/.bash_history", "~/.zsh_history", "~/.local/share/fish/fish_history", "~/.claude.json",
			"~/.claude", "~/.cache/repo-ci-baseline",
			"~/.bashrc", "~/.bash_profile", "~/.bash_login", "~/.profile", "~/.zshrc", "~/.zshenv", "~/.zprofile",
			"~/.zlogin", "~/.config/fish", "~/.gitconfig", "~/.config/git", "~/.microsoft/usersecrets", "~/.aspnet",
			"~/.config/rclone", "~/.s3cfg", "~/.boto", "~/.oci", "~/.config/doctl", "~/.gradle/gradle.properties"], AutoRollout.CredentialPaths);
		Assert.Equal([
			"GH_CONFIG_DIR", "DOCKER_CONFIG", "KUBECONFIG", "AWS_SHARED_CREDENTIALS_FILE", "AWS_CONFIG_FILE",
			"NPM_CONFIG_USERCONFIG", "npm_config_userconfig", "GNUPGHOME", "CLOUDSDK_CONFIG", "AZURE_CONFIG_DIR",
			"PASSWORD_STORE_DIR", "GIT_CONFIG_GLOBAL"], AutoRollout.CredentialVars);
		Assert.Equal(["gh", "glab-cli", "hub", "git", "pnpm", "pip", "uv", "NuGet", "op", "gcloud", "rclone", "doctl", "fish"], AutoRollout.XdgConfigSubfolders);
		Assert.Empty(AutoRollout.SandboxDomains);
		Assert.Equal("https://push.invalid/", AutoRollout.NoPush);
		Assert.Equal([
			("credential.helper", ""),
			("credential.https://github.com.helper", ""),
			("url.https://push.invalid/.pushInsteadOf", "https://github.com/"),
			("url.https://push.invalid/.pushInsteadOf", "git@github.com:"),
			("url.https://push.invalid/.pushInsteadOf", "ssh://git@github.com/"),
			("gc.auto", "0")], AutoRollout.SessionGitConfig);
		Assert.Equal(["1", "true", "yes", "on"], AutoRollout.FlagOn);
		Assert.Equal([
			"ANTHROPIC_BASE_URL", "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL",
			"ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_VERTEX_PROJECT_ID",
			"ANTHROPIC_FOUNDRY_RESOURCE", "ANTHROPIC_BEDROCK_BASE_URL", "ANTHROPIC_VERTEX_BASE_URL", "ANTHROPIC_FOUNDRY_BASE_URL",
			"CLAUDE_CONFIG_DIR", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY"], AutoRollout.ClaudeKept);
	}

	[Fact]
	public void The_prompt_says_none_when_no_owned_file_changes()
	{
		var agents = new System.Text.Json.Nodes.JsonObject { ["pre-push-reviewer"] = new System.Text.Json.Nodes.JsonObject { ["description"] = "d", ["prompt"] = "p", ["tools"] = new System.Text.Json.Nodes.JsonArray("Read") } };
		var command = AutoRollout.ReviewCommand("mpaulosky/TicketManager", "/w", [], "/l/review.settings.json", agents, "# Pre-push review\n\nThe skill's own steps.\n", "/t/run");
		Assert.Equal("/l/review.settings.json", command[command.IndexOf("--settings") + 1]);
		Assert.Contains("These files the branch changes are Owned Baseline files, which the next Apply overwrites: none.", command[2]);
	}

	[Fact]
	public void The_deny_rules_word_for_word() =>
		Assert.Equal([
			"Bash(git push:*)",
			"Bash(scripts/review.sh accept:*)",
			"Bash(scripts/review.sh accept-open:*)",
			"Bash(gh pr create:*)", "Bash(gh pr edit:*)", "Bash(gh pr comment:*)", "Bash(gh pr review:*)",
			"Bash(gh pr merge:*)", "Bash(gh pr ready:*)", "Bash(gh pr close:*)",
			"Bash(gh issue create:*)", "Bash(gh issue comment:*)", "Bash(gh issue edit:*)", "Bash(gh issue close:*)",
			"Bash(gh label:*)", "Bash(gh release:*)", "Bash(gh repo:*)", "Bash(gh secret:*)", "Bash(gh workflow:*)",
			$"Edit(/{AutoRollout.Factory}/**)", "Edit(~/.claude/**)"], AutoRollout.DeniedTools);
}
