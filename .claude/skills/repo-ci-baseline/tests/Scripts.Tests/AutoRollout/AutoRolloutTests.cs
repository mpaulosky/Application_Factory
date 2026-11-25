using System.Diagnostics;

// Tests for github/AutoRollout.cs: copies of github/tests/test_auto_rollout.py's tests of the worktree, the lock, the Owned files and the skill's
// text (ADR 0017's Update for mpaulosky/Application_Factory#157), with the same cases and assertions, and the lock's tests the Update names. The
// pytest originals stay until the cut-over.
public class AutoRolloutTests : IDisposable
{
	readonly string tmp = AutoRollout.RealPath(Directory.CreateTempSubdirectory("auto-rollout-test.").FullName);

	public void Dispose()
	{
		Directory.Delete(tmp, recursive: true);
		GC.SuppressFinalize(this);
	}

	static (int Code, string Output) Start(string program, params string[] args)
	{
		var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in args)
			start.ArgumentList.Add(arg);
		start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
		start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
		foreach (var name in new[] { "GIT_AUTHOR_NAME", "GIT_COMMITTER_NAME" })
			start.Environment[name] = "t";
		foreach (var name in new[] { "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_EMAIL" })
			start.Environment[name] = "t@t";
		using var run = Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync();
		var output = run.StandardOutput.ReadToEnd();
		run.WaitForExit();
		return (run.ExitCode, output + err.Result);
	}

	// A repository at path with one commit on main, and the git that runs in it (the pytest's git_repo).
	static Action<string[]> GitRepo(string path)
	{
		Directory.CreateDirectory(path);
		void Git(params string[] args)
		{
			var (code, output) = Start("git", ["-C", path, .. args]);
			Assert.True(code == 0, output);
		}
		Git("init", "-q", "-b", "main");
		Git("commit", "-q", "--allow-empty", "-m", "one");
		return Git;
	}

	// ── What the other side says ────────────────────────────────────────

	[Fact]
	public void The_paths_it_runs_exist()
	{
		// The original's REAPPLY is rollout.py's, which joins this library with rollout's own port.
		static bool Executable(string path) => !OperatingSystem.IsWindows() && File.Exists(path) && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
		var folders = new Folders(Skill.Dir);
		Assert.True(Executable(folders.Land));
		Assert.True(Executable(folders.Reapply));
		Assert.True(Directory.Exists(folders.Owned));
		// test.sh also runs this project from a copy of the skill alone, with no Factory around it.
		if (Path.GetFileName(Skill.Dir) == "repo-ci-baseline")
			Assert.True(File.Exists(Path.Combine(folders.Factory, "CLAUDE.md")), "FACTORY is the Factory's root");
	}

	[Fact]
	public void The_exit_codes_and_worktree_agree_with_reapply_sh()
	{
		// The original's part this library holds: the branch and the worktree's folder. The exit codes join it with the round's port.
		var script = File.ReadAllLines(Skill.Path("reapply.sh"));
		string Constant(string name) => script.First(line => line.StartsWith($"{name}=", StringComparison.Ordinal)).Split('=', 2)[1].Trim('"');
		Assert.Equal(AutoRollout.Worktree, Constant("worktree_name"));
		Assert.Equal(AutoRollout.Branch, Constant("branch"));
	}

	// ── The worktree, the lock, the Owned files ─────────────────────────

	[Fact]
	public void Short_name_is_the_repos_name_alone()
	{
		// Not in the original: short_name has no test of its own there.
		Assert.Equal("TicketManager", AutoRollout.ShortName("mpaulosky/TicketManager"));
		Assert.Equal("app", AutoRollout.ShortName("app"));
	}

	[Fact]
	public void Review_worktree_is_beside_the_checkout_with_symlinks_resolved()
	{
		Directory.CreateDirectory(Path.Combine(tmp, "real", "TicketManager"));
		File.CreateSymbolicLink(Path.Combine(tmp, "link"), Path.Combine(tmp, "real"));
		var expected = Path.Combine(tmp, "real", "TicketManager-worktrees", "chore-reapply-baseline");
		Assert.Equal(expected, AutoRollout.ReviewWorktree(Path.Combine(tmp, "link", "TicketManager")));
		Assert.Equal(expected, AutoRollout.ReviewWorktree(Path.Combine(tmp, "real", "TicketManager") + "/"));
	}

	[Fact]
	public void Review_worktree_of_a_linked_worktree_is_beside_the_primary_checkout()
	{
		var git = GitRepo(Path.Combine(tmp, "TicketManager"));
		git(["worktree", "add", "-q", "-b", "elsewhere", Path.Combine(tmp, "other", "tm-feature")]);
		var expected = Path.Combine(tmp, "TicketManager-worktrees", "chore-reapply-baseline");
		Assert.Equal(expected, AutoRollout.ReviewWorktree(Path.Combine(tmp, "other", "tm-feature")));
		Assert.Equal(expected, AutoRollout.ReviewWorktree(Path.Combine(tmp, "TicketManager")));
		Assert.Equal("/x/Repo-worktrees/chore-reapply-baseline", AutoRollout.ReviewWorktree("/c", read: _ => "/x/Repo/.git"));
	}

	[Fact]
	public void A_lock_that_cannot_be_taken_raises()
	{
		File.WriteAllText(Path.Combine(tmp, "file"), "");
		Assert.ThrowsAny<IOException>(() => AutoRollout.TakeLock(Path.Combine(tmp, "file", "lock")));
	}

	[Fact]
	public void A_second_run_cannot_take_the_lock()
	{
		// The original's second run is another python3; flock's lock is on the open file, so a second open in this process is refused as well,
		// and a second run of auto_rollout.py is what the next test holds.
		var path = Path.Combine(tmp, "sub", "lock");
		var first = AutoRollout.TakeLock(path);
		Assert.NotNull(first);
		Assert.Null(AutoRollout.TakeLock(path));
		first.Dispose();
		using var again = AutoRollout.TakeLock(path);
		Assert.NotNull(again);
	}

	[Fact]
	public void The_lock_is_flocks_own_so_the_python_round_and_flock_n_are_kept_out()
	{
		// ADR 0017's Update for mpaulosky/Application_Factory#157: a FileStream with FileShare.None is flock(LOCK_EX | LOCK_NB), the call
		// auto_rollout.py's take_lock makes, so a C# round and a Python round keep each other out while both exist.
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		var path = Path.Combine(tmp, "lock");
		var python = $"import sys; sys.path.insert(0, sys.argv[1]); import auto_rollout as a; print(a.take_lock(sys.argv[2]) is None)";
		using (var held = AutoRollout.TakeLock(path))
		{
			Assert.NotNull(held);
			Assert.Equal(1, Start("flock", "-n", path, "true").Code);
			Assert.Equal((0, "True\n"), Start("python3", "-I", "-c", python, Skill.Path("github"), path));
		}
		Assert.Equal(0, Start("flock", "-n", path, "true").Code);
		Assert.Equal((0, "False\n"), Start("python3", "-I", "-c", python, Skill.Path("github"), path));
		// And the other way: while flock(1) holds it, the C# can't take it.
		var start = new ProcessStartInfo("flock", ["-n", path, "sleep", "30"]) { UseShellExecute = false };
		using var other = Process.Start(start)!;
		try
		{
			var deadline = DateTime.UtcNow.AddSeconds(10);
			FileStream? taken;
			while ((taken = AutoRollout.TakeLock(path)) is not null && DateTime.UtcNow < deadline)
			{
				taken.Dispose();
				Thread.Sleep(50);
			}
			Assert.Null(taken);
		}
		finally
		{
			other.Kill();
			other.WaitForExit();
		}
	}

	[Fact]
	public void The_locks_descriptor_is_the_open_lock_file_and_held_lock_fds_copies_the_list()
	{
		// Not in the original: held_lock_fds hands each group the descriptor run keeps in LockFds, which the C# reads from the stream.
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		var path = Path.Combine(tmp, "lock");
		using var held = AutoRollout.TakeLock(path)!;
		var fd = AutoRollout.Descriptor(held);
		Assert.Equal(path, new FileInfo($"/proc/self/fd/{fd}").LinkTarget);
		Assert.Empty(AutoRollout.HeldLockFds());
	}

	[Fact]
	public void Owned_changes_lists_only_the_owned_files_the_branch_changes()
	{
		var owned = Path.Combine(tmp, "owned");
		Directory.CreateDirectory(Path.Combine(owned, "scripts"));
		File.WriteAllText(Path.Combine(owned, "scripts", "gate.sh"), "");
		File.WriteAllText(Path.Combine(owned, "a b.md"), "");
		var seen = new List<string[]>();
		Ran Run(IReadOnlyList<string> command, string? cwd, IReadOnlyDictionary<string, string>? env)
		{
			seen.Add([.. command]);
			return new Ran(0, "src/App.cs\0scripts/gate.sh\0a b.md\0scripts\0", "");
		}
		Assert.Equal(["a b.md", "scripts/gate.sh"], AutoRollout.OwnedChanges("/w", Run, owned, new HashSet<string>()));
		Assert.Equal([["git", "-C", "/w", "diff", "--no-renames", "--name-only", "-z", "origin/main...HEAD"]], seen);
		var error = Assert.Throws<InvalidOperationException>(() => AutoRollout.OwnedChanges("/w", (_, _, _) => new Ran(128, "", "bad"), owned, new HashSet<string>()));
		Assert.Contains("git diff in /w failed: bad", error.Message);
	}

	[Fact]
	public void Owned_changes_lists_a_retired_file_the_re_apply_deletes()
	{
		var owned = Directory.CreateDirectory(Path.Combine(tmp, "owned")).FullName;
		static Ran Run(IReadOnlyList<string> command, string? cwd, IReadOnlyDictionary<string, string>? env) => new(0, ".github/scripts/old.py\0src/App.cs\0", "");
		Assert.Equal([".github/scripts/old.py"], AutoRollout.OwnedChanges("/w", Run, owned, new HashSet<string> { ".github/scripts/old.py" }));
		Assert.Empty(AutoRollout.OwnedChanges("/w", Run, owned, new HashSet<string>()));
	}

	[Fact]
	public void Retired_paths_reads_the_paths_of_retired_txt()
	{
		var retired = Path.Combine(tmp, "retired.txt");
		var digest = new string('a', 64);
		File.WriteAllText(retired, $"# note\n\n{digest}  old.py\r\n{digest}  old.py\n{digest}  a b.yml\n");
		Assert.Equal(new HashSet<string> { "old.py", "a b.yml" }, AutoRollout.RetiredPaths(retired));
		Assert.Empty(AutoRollout.RetiredPaths(Path.Combine(tmp, "none.txt")));
	}

	// ── The skill ───────────────────────────────────────────────────────

	public static TheoryData<string, string?, string?> FrontMatters => new()
	{
		{ "", null, null },
		{ "body\n", null, null },
		{ "---\nname: a\n---\nbody", "name: a\n", "body" },
		{ "---\r\nname: a\r\n---\r\nbody", "name: a\r\n", "body" },
		{ "---\n---\n", "", "" },
		{ "--- \nname: a\n---\nbody", null, null },
		{ "---x\nname: a\n---\nbody", null, null },
		{ "---\nname: a\n----\n---\nbody", "name: a\n----\n", "body" },
	};

	[Theory]
	[MemberData(nameof(FrontMatters))]
	public void Front_matter_opens_and_closes_on_a_line_that_is_dashes_alone(string text, string? head, string? body) =>
			Assert.Equal(head is null ? null : (head, body!), AutoRollout.FrontMatter(text, "f.md"));

	[Theory]
	[InlineData("---\n"), InlineData("---\nname: a\n"), InlineData("---\nname: a\n--- \nbody")]
	public void Front_matter_never_closed_is_refused(string text)
	{
		var error = Assert.Throws<InvalidDataException>(() => AutoRollout.FrontMatter(text, "f.md"));
		Assert.Contains("f.md's front matter is never closed", error.Message);
	}

	[Fact]
	public void Read_skill_of_an_empty_file_is_empty()
	{
		File.WriteAllText(Path.Combine(tmp, "s.md"), "");
		Assert.Equal("", AutoRollout.ReadSkill(Path.Combine(tmp, "s.md")));
	}

	[Fact]
	public void The_skill_comes_from_the_templates_file_without_its_front_matter()
	{
		var folders = new Folders(Skill.Dir);
		var text = AutoRollout.ReadSkill(folders.SkillFile);
		Assert.True(!text.StartsWith("---", StringComparison.Ordinal) && text.StartsWith("# Pre-push review", StringComparison.Ordinal));
		Assert.Equal(Path.Combine(folders.Owned, ".claude", "skills", "pre-push-review", "SKILL.md"), folders.SkillFile);
	}

	[Theory]
	[InlineData("# no front matter\n---\nx\n", "# no front matter\n---\nx\n")]
	[InlineData("---\nname: a\ndescription: b --- c\n---\n\n# Body\n", "# Body\n")]
	[InlineData("---\nname: a\n---\n# Body\n---\nafter\n", "# Body\n---\nafter\n")]
	public void Read_skill_leaves_out_the_front_matter_alone(string text, string body)
	{
		File.WriteAllText(Path.Combine(tmp, "s.md"), text);
		Assert.Equal(body, AutoRollout.ReadSkill(Path.Combine(tmp, "s.md")));
	}

	[Fact]
	public void Read_skill_reads_as_pythons_text_mode()
	{
		// Not in the original: open() in text mode reads \r\n and a lone \r as \n, and refuses a byte that isn't UTF-8.
		File.WriteAllText(Path.Combine(tmp, "s.md"), "---\r\nname: a\r\n---\r\n\r\n# Body\rmore\r\n");
		Assert.Equal("# Body\nmore\n", AutoRollout.ReadSkill(Path.Combine(tmp, "s.md")));
		File.WriteAllBytes(Path.Combine(tmp, "bad.md"), [0x23, 0xff]);
		Assert.ThrowsAny<ArgumentException>(() => AutoRollout.ReadSkill(Path.Combine(tmp, "bad.md")));
	}
}
