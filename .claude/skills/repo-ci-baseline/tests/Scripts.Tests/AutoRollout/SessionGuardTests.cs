using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// Tests for github/AutoRolloutGuard.cs, xUnit copies of github/tests/test_auto_rollout.py's tests of session_guard, broken_rules and
// the snapshots they compare (ADR 0017's Update for mpaulosky/Application_Factory#157): one method each, the same cases and assertions.
// The pytest originals stay, and auto_rollout.py is still what runs, until the cut-over (mpaulosky/Application_Factory#122).
// The tests that lower SessionGuard.RecordLimit, as the pytest monkeypatched RECORD_BYTES, and those that read it: one collection, so they
// run one at a time.
[CollectionDefinition(nameof(SessionGuardTests))]
public sealed class SessionGuardCollection;

// --auto is Linux only (ADR 0016), and so is what these read: modes, inodes, FIFOs.
[Collection(nameof(SessionGuardTests))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class SessionGuardTests : IDisposable
{
	readonly string tmp;

	// SupportedOSPlatform only quiets the analyzer, so each test skips itself off Linux here, before its folder is made, and so before Dispose's
	// reads of it (a constructor that throws gets no Dispose).
	public SessionGuardTests()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		tmp = Directory.CreateTempSubdirectory("auto-rollout.").FullName;
	}

	// The folders a test locked are opened again first, never through a symlink, so the whole tree can be removed.
	public void Dispose()
	{
		void Open(string folder)
		{
			File.SetUnixFileMode(folder, (UnixFileMode)0x1C0);
			foreach (var path in Directory.GetFileSystemEntries(folder))
				if (Posix.LStat(path) is { IsDir: true })
					Open(path);
		}
		Open(tmp);
		Directory.Delete(tmp, recursive: true);
	}

	string P(params string[] parts) => Path.Combine([tmp, .. parts]);

	static void Write(string path, string text) => File.WriteAllText(path, text);

	// Runs a command, which must succeed, and gives its stdout.
	internal static string Run(string program, IEnumerable<string> args, IDictionary<string, string>? env = null, string? cwd = null)
	{
		var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in args)
			start.ArgumentList.Add(arg);
		foreach (var (key, value) in env ?? new Dictionary<string, string>())
			start.Environment[key] = value;
		if (cwd is not null)
			start.WorkingDirectory = cwd;
		using var run = Process.Start(start)!;
		var output = run.StandardOutput.ReadToEndAsync();
		var err = run.StandardError.ReadToEnd();
		run.WaitForExit();
		Assert.True(run.ExitCode == 0, $"{program} {string.Join(" ", args)}: {err}");
		return output.Result.Trim();
	}

	static readonly Dictionary<string, string> GitEnv = new()
	{
		["GIT_CONFIG_GLOBAL"] = "/dev/null",
		["GIT_CONFIG_NOSYSTEM"] = "1",
		["GIT_AUTHOR_NAME"] = "t",
		["GIT_AUTHOR_EMAIL"] = "t@t",
		["GIT_COMMITTER_NAME"] = "t",
		["GIT_COMMITTER_EMAIL"] = "t@t",
	};

	// A git repo at path with one empty commit on main, and git run in it.
	internal static Func<string[], string> GitRepo(string path)
	{
		string Git(params string[] args) => Run("git", ["-C", path, .. args], GitEnv);
		Directory.CreateDirectory(path);
		Git("init", "-q", "-b", "main");
		Git("commit", "-q", "--allow-empty", "-m", "one");
		return Git;
	}

	internal static void MkFifo(string path, string mode = "600") => Run("mkfifo", ["-m", mode, path]);

	[DllImport("libc", EntryPoint = "geteuid")]
	static extern uint GetEuid();

	static bool Root => GetEuid() == 0;

	static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

	static UnixFileMode Mode(int octal) => (UnixFileMode)octal;

	// A real linked worktree of a primary checkout, with an Owned file equal to the Template's, the clone's ledger folder and the main
	// checkout's .security; the remote read is faked (and recorded), the rest is real git.
	sealed class Guarded
	{
		public readonly string Primary, Worktree, Owned, Ledgers, Security;
		public readonly Func<string[], string> Git;
		public string? Remote = "abc\trefs/heads/chore/reapply-baseline";
		public readonly List<string> Changed = ["scripts/gate.sh"];
		public readonly List<IReadOnlyList<string>> Reads = [];
		public Func<IReadOnlyList<string>, string?> Read;
		public Guard? Taken;

		public Guarded(string tmp)
		{
			(Primary, Worktree, Owned) = (Path.Combine(tmp, "main"), Path.Combine(tmp, "w"), Path.Combine(tmp, "owned"));
			Git = GitRepo(Primary);
			Git(["worktree", "add", "-q", "-b", "chore/reapply-baseline", Worktree]);
			Directory.CreateDirectory(Path.Combine(Owned, "scripts"));
			File.WriteAllText(Path.Combine(Owned, "scripts", "gate.sh"), "gate\n");
			Directory.CreateDirectory(Path.Combine(Worktree, "scripts"));
			File.WriteAllText(Path.Combine(Worktree, "scripts", "gate.sh"), "gate\n");
			Ledgers = Path.Combine(Primary, ".git", "review");
			Directory.CreateDirectory(Ledgers);
			Security = Path.Combine(Primary, ".security");
			Directory.CreateDirectory(Security);
			Read = ReadThrough;
		}

		public string? ReadThrough(IReadOnlyList<string> command)
		{
			Reads.Add(command);
			return command.Contains("ls-remote") ? Remote : SessionGuard.RunText(command);
		}

		public Guard Guard() => Taken = SessionGuard.Read(Worktree, Owned, command => Read(command));

		public List<string> Check(Func<string, IReadOnlyList<string>>? changes = null) =>
				SessionGuard.BrokenRules(Taken!, Owned, changes ?? (_ => [.. Changed]), command => Read(command));

		public string Ledger(string name, string raw)
		{
			var path = Path.Combine(Ledgers, name);
			File.WriteAllText(path, raw);
			return path;
		}

		public string Entry(string name, string status)
		{
			var path = Path.Combine(Security, name);
			File.WriteAllText(path, $"# {name}\n\n- Status: {status}\n- Lens: Security\n\n## Weakness\n\nthe weakness\n");
			return path;
		}
	}

	const string Changed = "{0} changed during the session (its HEAD, its branch or its files): the session reviews only, so it commits and edits nothing";
	const string Unseen = "{0} path(s) in {1} changed during the session, which git status may not show (an ignored file, such as"
			+ " node_modules): {2}. The round's record and the gate on the push run the worktree's files as you, so nothing is run or recorded";
	const string FolderChanged = "{0} in {1} was {2} during the session, which can't write {3}: your own review or record on another"
			+ " branch meanwhile, or a sandbox that didn't hold";

	static string Quoted(string text) => PyText.Ascii(text);

	[Fact]
	public void A_session_that_kept_the_rules_broke_none()
	{
		var g = new Guarded(tmp);
		g.Entry("0001-a.md", "open");
		g.Guard();
		Assert.Empty(g.Check());
		Assert.Equal(g.Ledgers, g.Taken!.Ledgers);
		Assert.Equal(Path.Combine(g.Primary, ".git"), g.Taken.Common);
		Assert.Equal(Path.Combine(g.Primary, ".git", "worktrees", "w"), g.Taken.Gitdir);
		Assert.Equal(g.Security, g.Taken.Security);
		Assert.Equal(Encoding.UTF8.GetBytes($"gitdir: {Path.Combine(g.Primary, ".git", "worktrees", "w")}\n"), g.Taken.Link);
	}

	[Theory]
	[InlineData("commit")]
	[InlineData("edit")]
	[InlineData("new file")]
	[InlineData("branch moved")]
	[InlineData("checkout")]
	public void A_session_that_commits_or_edits_in_the_worktree_is_caught(string how)
	{
		var g = new Guarded(tmp);
		g.Guard();
		string[] wt = ["-C", g.Worktree];
		if (how == "commit")
			g.Git([.. wt, "commit", "-q", "--allow-empty", "-m", "the session's"]);
		else if (how == "edit")
		{
			Write(Path.Combine(g.Worktree, "README.md"), "x");
			g.Git([.. wt, "add", "README.md"]);
		}
		else if (how == "new file")
			Write(Path.Combine(g.Worktree, "src.cs"), "x");
		else if (how == "branch moved")
		{
			g.Git(["commit", "-q", "--allow-empty", "-m", "two"]);
			g.Git(["update-ref", "refs/heads/chore/reapply-baseline", "main"]);
		}
		else
		{
			g.Git([.. wt, "checkout", "-q", "--detach"]);
			g.Git([.. wt, "commit", "-q", "--allow-empty", "-m", "elsewhere"]);
		}
		var named = how switch { "edit" => "'README.md'", "new file" => "'src.cs'", _ => null };
		List<string> expected = [string.Format(Changed, g.Worktree)];
		if (named is not null)
			expected.Add(string.Format(Unseen, 1, g.Worktree, named));
		Assert.Equal(expected, g.Check(_ => []));
	}

	[Theory]
	[InlineData(0, "rev-parse HEAD")]
	[InlineData(1, "rev-parse --verify refs/heads/chore/reapply-baseline")]
	[InlineData(2, "status --porcelain=v1 -z --untracked-files=all")]
	public void The_worktree_read_that_fails_is_the_one_the_hint_names(int failing, string hint)
	{
		// The branch read's --quiet would keep its failure silent: the hint runs it without.
		var g = new Guarded(tmp);
		var commands = SessionGuard.WorktreeStateCommands(g.Worktree);
		g.Read = command => command.SequenceEqual(commands[failing]) ? null : g.ReadThrough(command);
		var refused = Assert.Throws<GuardUnreadable>(() => g.Guard());
		Assert.Equal($"can't read the HEAD, the branch chore/reapply-baseline or the status of {g.Worktree}; see why with: git -C {g.Worktree} {hint}",
				refused.Message);
	}

	[Fact]
	public void A_worktree_state_that_cant_be_read_is_refused_before_and_broken_after()
	{
		var g = new Guarded(tmp);
		g.Read = command => command.Contains("status") ? null : g.ReadThrough(command);
		var refused = Assert.Throws<GuardUnreadable>(() => g.Guard());
		Assert.Equal($"can't read the HEAD, the branch chore/reapply-baseline or the status of {g.Worktree}; see why with: git -C {g.Worktree}"
				+ " status --porcelain=v1 -z --untracked-files=all", refused.Message);
		g.Read = g.ReadThrough;
		g.Guard();
		g.Read = command => command.Contains("HEAD") ? null : g.ReadThrough(command);
		Assert.Equal([$"can't read the HEAD, the branch chore/reapply-baseline or the status of {g.Worktree} after the session"], g.Check(_ => []));
	}

	[Fact]
	public void The_worktree_state_reads_word_for_word()
	{
		Assert.Equal([
			["git", "-C", "/w", "rev-parse", "HEAD"],
			["git", "-C", "/w", "rev-parse", "--verify", "--quiet", "refs/heads/chore/reapply-baseline"],
			["git", "-C", "/w", "status", "--porcelain=v1", "-z", "--untracked-files=all"],
		], SessionGuard.WorktreeStateCommands("/w"));
	}

	[Fact]
	public void The_guard_reads_the_remote_branch_by_its_full_name_before_and_after()
	{
		var g = new Guarded(tmp);
		g.Guard();
		g.Check();
		string[] remote = ["git", "-C", g.Worktree, "ls-remote", "origin", "refs/heads/chore/reapply-baseline"];
		var reads = g.Reads.Where(command => command.Contains("ls-remote")).ToList();
		Assert.Equal(2, reads.Count);
		Assert.All(reads, read => Assert.Equal(remote, read));
	}

	[Fact]
	public void A_guard_that_cant_read_the_link_the_remote_or_the_git_folders_starts_no_session()
	{
		var g = new Guarded(tmp);
		g.Remote = null;
		var refused = Assert.Throws<GuardUnreadable>(() => g.Guard());
		Assert.Equal("can't read the remote chore/reapply-baseline before the session (the network, or git);"
				+ $" see why with: git -C {g.Worktree} ls-remote origin refs/heads/chore/reapply-baseline", refused.Message);
		refused = Assert.Throws<GuardUnreadable>(() => SessionGuard.Read(P("not-a-repo"), g.Owned, _ => ""));
		Assert.Equal($"{P("not-a-repo")}/.git isn't the plain file a linked worktree has (see: ls -la {P("not-a-repo", ".git")})", refused.Message);
		refused = Assert.Throws<GuardUnreadable>(() => SessionGuard.Read(g.Primary, g.Owned, _ => ""));
		Assert.Contains(".git isn't the plain file", refused.Message);
		foreach (var missing in new[] { "--git-common-dir", "--git-dir" })
		{
			refused = Assert.Throws<GuardUnreadable>(() => SessionGuard.Read(g.Worktree, g.Owned,
					c => c.Contains(missing) ? null : c.Contains("ls-remote") ? "" : "/x"));
			// The read that failed.
			Assert.Equal($"can't find the git folders of {g.Worktree} (git 2.31 or later reads them); see why with: git -C {g.Worktree}"
					+ $" rev-parse --path-format=absolute {missing}", refused.Message);
		}
	}

	[Fact]
	public void A_remote_branch_that_doesnt_exist_yet_is_a_reading_not_a_failure()
	{
		var g = new Guarded(tmp);
		g.Remote = "";
		g.Guard();
		Assert.Empty(g.Check());
		g.Remote = "abc\trefs/heads/chore/reapply-baseline";
		Assert.Equal(["the remote chore/reapply-baseline moved during the session (the session, or anyone who pushed to it meanwhile)"], g.Check());
	}

	[Fact]
	public void A_session_after_which_the_remote_moved_or_cant_be_read_is_a_stop()
	{
		var g = new Guarded(tmp);
		g.Guard();
		g.Remote = "def\trefs/heads/chore/reapply-baseline";
		Assert.Equal(["the remote chore/reapply-baseline moved during the session (the session, or anyone who pushed to it meanwhile)"], g.Check());
		g.Remote = null;
		Assert.Equal(["can't read the remote chore/reapply-baseline after the session, so whether it was pushed can't be told"], g.Check());
	}

	[Theory]
	[InlineData("rewritten")]
	[InlineData("removed")]
	[InlineData("symlink")]
	[InlineData("folder")]
	public void A_changed_git_link_is_a_stop_and_no_git_runs_in_the_worktree(string how)
	{
		var g = new Guarded(tmp);
		g.Guard();
		var link = Path.Combine(g.Worktree, ".git");
		File.Delete(link);
		if (how == "rewritten")
			Write(link, $"gitdir: {P("planted")}\n");
		else if (how == "symlink")
			File.CreateSymbolicLink(link, Path.Combine(g.Primary, ".git", "worktrees", "w"));
		else if (how == "folder")
			Directory.CreateDirectory(link);
		g.Reads.Clear();
		List<string> listed = [];
		var broke = g.Check(w =>
		{
			listed.Add(w);
			return [];
		});
		Assert.Single(broke);
		Assert.StartsWith($"{link} changed during the session", broke[0]);
		// Neither the remote read nor the Owned list runs git there.
		Assert.Empty(g.Reads);
		Assert.Empty(listed);
	}

	[Fact]
	public void A_template_that_changed_during_the_session_is_a_stop()
	{
		var g = new Guarded(tmp);
		g.Guard();
		Write(Path.Combine(g.Owned, "new.md"), "x");
		Assert.Equal(["the Template changed during the session (the session, or your own edit in the Factory checkout)"], g.Check());
	}

	[Fact]
	public void An_owned_file_edited_or_deleted_by_the_session_is_caught()
	{
		var g = new Guarded(tmp);
		g.Guard();
		const string owned = "scripts/gate.sh is an Owned file and no longer the Template's: edit the Template, not the copy";
		Write(Path.Combine(g.Worktree, "scripts", "gate.sh"), "gate, edited\n");
		// An ignored file edited: the status reads the same, the snapshot and the Owned check catch it.
		Assert.Equal([owned, string.Format(Unseen, 1, g.Worktree, "'scripts/gate.sh'")], g.Check());
		File.Delete(Path.Combine(g.Worktree, "scripts", "gate.sh"));
		Assert.Equal([string.Format(Changed, g.Worktree), owned, string.Format(Unseen, 1, g.Worktree, "'scripts/gate.sh'")], g.Check());
	}

	[Fact]
	public void An_ignored_file_the_session_changed_is_caught()
	{
		// git status shows no ignored file, and the round runs the worktree's scripts/review.sh (node_modules/.bin/tsx) after the
		// session, outside the sandbox.
		var g = new Guarded(tmp);
		Write(Path.Combine(g.Worktree, ".gitignore"), "node_modules/\n");
		var bin = Path.Combine(g.Worktree, "node_modules", ".bin");
		Directory.CreateDirectory(bin);
		foreach (var name in new[] { "tsx", "a", "b", "c", "d" })
			Write(Path.Combine(bin, name), $"{name}\n");
		g.Guard();
		Assert.Empty(g.Check());
		Write(Path.Combine(bin, "tsx"), "planted\n");
		Assert.Equal([string.Format(Unseen, 1, g.Worktree, "'node_modules/.bin/tsx'")], g.Check());
		foreach (var name in new[] { "d", "c" })
			Write(Path.Combine(bin, name), "planted\n");
		// Three are named whole.
		Assert.Equal([string.Format(Unseen, 3, g.Worktree, "'node_modules/.bin/c', 'node_modules/.bin/d', 'node_modules/.bin/tsx'")], g.Check());
		Write(Path.Combine(bin, "b"), "planted\n");
		// The first three, sorted, and how many more.
		Assert.Equal([string.Format(Unseen, 4, g.Worktree, "'node_modules/.bin/b', 'node_modules/.bin/c', 'node_modules/.bin/d' and 1 more")],
				g.Check());
		Write(Path.Combine(bin, "a"), "planted\n");
		Assert.Equal([string.Format(Unseen, 5, g.Worktree, "'node_modules/.bin/a', 'node_modules/.bin/b', 'node_modules/.bin/c' and 2 more")],
				g.Check());
	}

	[Fact]
	public void A_hard_link_made_elsewhere_changes_nothing()
	{
		// pnpm hard-links node_modules files from its store: another project's install changes their link count and ctime, never their
		// bytes.
		var root = P("w");
		Directory.CreateDirectory(Path.Combine(root, "node_modules"));
		Write(Path.Combine(root, "node_modules", "f"), "x");
		var before = SessionGuard.TreeState(root);
		Run("ln", [Path.Combine(root, "node_modules", "f"), P("elsewhere")]);
		Assert.Empty(SessionGuard.TreeChanges(before, SessionGuard.TreeState(root)));
		File.Delete(P("elsewhere"));
		Assert.Empty(SessionGuard.TreeChanges(before, SessionGuard.TreeState(root)));
	}

	[Theory]
	[InlineData("write", new[] { "a/b/f" })]
	[InlineData("same size", new[] { "a/b/f" })]
	[InlineData("chmod", new[] { "a/b/f" })]
	[InlineData("new file", new[] { "a/b/g" })]
	[InlineData("new folder", new[] { "a/c" })]
	[InlineData("remove", new[] { "a/b/f" })]
	[InlineData("rename", new[] { "a/b/f", "a/b/h" })]
	[InlineData("symlink", new[] { "a/b/l" })]
	[InlineData("relink", new[] { "a/l" })]
	[InlineData("fifo", new[] { "a/b/f" })]
	[InlineData("fifo mode", new[] { "a/p" })]
	[InlineData("nested git", new[] { "a/.git" })]
	[InlineData("root mode", new[] { "." })]
	public void Tree_state_catches_every_kind_of_change_under_the_root(string how, string[] expected)
	{
		var root = P("w");
		Directory.CreateDirectory(Path.Combine(root, "a", "b"));
		var f = Path.Combine(root, "a", "b", "f");
		Write(f, "x");
		File.CreateSymbolicLink(Path.Combine(root, "a", "l"), "b");
		Write(Path.Combine(root, ".git"), "gitdir: x\n");
		Write(Path.Combine(root, "a", ".git"), "a package's own\n");
		MkFifo(Path.Combine(root, "a", "p"));
		var before = SessionGuard.TreeState(root);
		Assert.Empty(SessionGuard.TreeChanges(before, SessionGuard.TreeState(root)));
		switch (how)
		{
			case "write":
				Write(f, "yy");
				break;
			case "same size":
				Write(f, "y");
				break;
			case "chmod":
				File.SetUnixFileMode(f, Mode(0x1C0));
				break;
			case "new file":
				Write(Path.Combine(root, "a", "b", "g"), "y");
				break;
			case "new folder":
				Directory.CreateDirectory(Path.Combine(root, "a", "c"));
				break;
			case "remove":
				File.Delete(f);
				break;
			case "rename":
				File.Move(f, Path.Combine(root, "a", "b", "h"));
				break;
			case "symlink":
				File.CreateSymbolicLink(Path.Combine(root, "a", "b", "l"), "/etc/passwd");
				break;
			case "relink":
				File.Delete(Path.Combine(root, "a", "l"));
				File.CreateSymbolicLink(Path.Combine(root, "a", "l"), "/etc");
				break;
			case "fifo":
				File.Delete(f);
				MkFifo(f);
				break;
			case "fifo mode":
				File.SetUnixFileMode(Path.Combine(root, "a", "p"), Mode(Convert.ToInt32("640", 8)));
				break;
			case "nested git":
				Write(Path.Combine(root, "a", ".git"), "planted\n");
				break;
			default:
				var mode = (int)File.GetUnixFileMode(root);
				File.SetUnixFileMode(root, Mode(mode != Convert.ToInt32("755", 8) ? Convert.ToInt32("755", 8) : Convert.ToInt32("750", 8)));
				break;
		}
		Assert.Equal(expected, SessionGuard.TreeChanges(before, SessionGuard.TreeState(root)));
	}

	[Fact]
	public void Tree_state_leaves_the_git_link_to_its_own_check_and_never_follows_a_symlink()
	{
		var (root, outside) = (P("w"), P("outside"));
		Directory.CreateDirectory(root);
		Directory.CreateDirectory(outside);
		Write(Path.Combine(outside, "f"), "x");
		File.CreateSymbolicLink(Path.Combine(root, "out"), outside);
		Write(Path.Combine(root, ".git"), "gitdir: x\n");
		var before = SessionGuard.TreeState(root);
		Assert.False(before.ContainsKey(".git"));
		Assert.False(before.ContainsKey("out/f"));
		Write(Path.Combine(root, ".git"), "gitdir: y\n");
		Write(Path.Combine(outside, "f"), "y");
		// The .git link is BrokenRules' own check.
		Assert.Empty(SessionGuard.TreeChanges(before, SessionGuard.TreeState(root)));
		Assert.Equal(new Dictionary<string, Entry?> { ["."] = null }, SessionGuard.TreeState(P("none")));
	}

	[Fact]
	public void A_folder_that_cant_be_read_is_read_the_same_each_time_and_its_mode_still_counts()
	{
		Assert.SkipWhen(Root, "root reads a folder whatever its mode");
		var root = P("w");
		Directory.CreateDirectory(Path.Combine(root, "locked"));
		Write(Path.Combine(root, "locked", "f"), "x");
		Write(Path.Combine(root, "secret"), "x");
		File.SetUnixFileMode(Path.Combine(root, "locked"), Mode(0));
		File.SetUnixFileMode(Path.Combine(root, "secret"), Mode(0));
		var before = SessionGuard.TreeState(root);
		Assert.Equal(new Entry("dir", 0x4000), before["locked"]);
		Assert.Equal("unread", before["secret"]!.Kind);
		// Unreadable, but no change: no stop.
		Assert.Empty(SessionGuard.TreeChanges(before, SessionGuard.TreeState(root)));
		File.SetUnixFileMode(Path.Combine(root, "locked"), Mode(0x1C0));
		Assert.Equal(["locked", "locked/f"], SessionGuard.TreeChanges(before, SessionGuard.TreeState(root)));
	}

	[Theory]
	[InlineData("config")]
	[InlineData("config.worktree")]
	[InlineData("hooks")]
	[InlineData("info")]
	[InlineData("worktree config.worktree")]
	public void A_change_to_the_clones_git_config_or_hooks_is_caught_and_no_git_runs(string what)
	{
		var g = new Guarded(tmp);
		Directory.CreateDirectory(Path.Combine(g.Primary, ".git", "hooks"));
		g.Guard();
		Assert.Empty(g.Check());
		var (common, gitdir) = (Path.Combine(g.Primary, ".git"), Path.Combine(g.Primary, ".git", "worktrees", "w"));
		switch (what)
		{
			case "config":
				File.AppendAllText(Path.Combine(common, "config"), "[core]\n\tfsmonitor = /tmp/planted\n");
				break;
			case "config.worktree":
				Write(Path.Combine(common, "config.worktree"), "[core]\n\thooksPath = /tmp\n");
				break;
			case "hooks":
				Write(Path.Combine(common, "hooks", "post-index-change"), "#!/bin/sh\n");
				break;
			case "info":
				Directory.CreateDirectory(Path.Combine(common, "info"));
				Write(Path.Combine(common, "info", "attributes"), "* diff=planted\n");
				break;
			default:
				Write(Path.Combine(gitdir, "config.worktree"), "[core]\n\tfsmonitor = /tmp/planted\n");
				break;
		}
		g.Reads.Clear();
		List<string> listed = [];
		var broke = g.Check(w =>
		{
			listed.Add(w);
			return [];
		});
		Assert.StartsWith($"the clone's git {what} changed during the session (in {common}): git can run a command they name, so it wasn't"
				+ " run there after the session", broke[0]);
		// Neither the remote read nor the Owned list runs git there.
		Assert.Empty(g.Reads);
		Assert.Empty(listed);
	}

	[DllImport("libc", EntryPoint = "utimensat", SetLastError = true)]
	static extern int UtimensAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, long[] times, int flags);

	// os.utime(path, ns=(atime, mtime)).
	static void SetTimes(string path, long atimeNs, long mtimeNs) =>
			Assert.Equal(0, UtimensAt(-100, path, [atimeNs / 1_000_000_000, atimeNs % 1_000_000_000, mtimeNs / 1_000_000_000, mtimeNs % 1_000_000_000], 0));

	static long AtimeNs(string path) => new DateTimeOffset(File.GetLastAccessTimeUtc(path)).ToUnixTimeMilliseconds() * 1_000_000;

	[Fact]
	public void A_file_that_cant_be_read_swapped_for_one_of_its_size_is_a_change()
	{
		Assert.SkipWhen(Root, "root reads a file whatever its mode");
		var root = P("w");
		Directory.CreateDirectory(root);
		var tool = Path.Combine(root, "tool");
		var x = Mode(Convert.ToInt32("111", 8));
		Write(tool, "one");
		File.SetUnixFileMode(tool, x);
		var before = SessionGuard.TreeState(root);
		Assert.Equal("unread", before["tool"]!.Kind);
		var old = Posix.LStat(tool)!.Value;
		Write(Path.Combine(root, "new"), "two");
		File.SetUnixFileMode(Path.Combine(root, "new"), x);
		SetTimes(Path.Combine(root, "new"), AtimeNs(tool), old.MtimeNs);
		File.Move(Path.Combine(root, "new"), tool, overwrite: true);
		// Only the inode tells this swap.
		Assert.Equal(old.MtimeNs, SessionGuard.TreeState(root)["tool"]!.MtimeNs);
		Assert.Equal(["tool"], SessionGuard.TreeChanges(before, SessionGuard.TreeState(root)));
		before = SessionGuard.TreeState(root);
		var inode = Posix.LStat(tool)!.Value.Ino;
		File.SetUnixFileMode(tool, Mode(0x180));
		Write(tool, "six");
		File.SetUnixFileMode(tool, x);
		SetTimes(tool, AtimeNs(tool), old.MtimeNs + 1_000_000_000);
		// Only the mtime tells this rewrite.
		Assert.Equal(inode, Posix.LStat(tool)!.Value.Ino);
		Assert.Equal(["tool"], SessionGuard.TreeChanges(before, SessionGuard.TreeState(root)));
		before = SessionGuard.TreeState(root);
		var kept = Posix.LStat(tool)!.Value;
		File.SetUnixFileMode(tool, Mode(0x180));
		Write(tool, "longer");
		File.SetUnixFileMode(tool, x);
		SetTimes(tool, AtimeNs(tool), kept.MtimeNs);
		var now = Posix.LStat(tool)!.Value;
		// Only the size tells this rewrite.
		Assert.Equal((kept.Ino, kept.MtimeNs, kept.Mode), (now.Ino, now.MtimeNs, now.Mode));
		Assert.Equal(["tool"], SessionGuard.TreeChanges(before, SessionGuard.TreeState(root)));
	}

	[Fact]
	public void The_guards_snapshots_have_no_default_so_a_guard_without_them_cant_be_made()
	{
		var parameters = typeof(Guard).GetConstructors().Single().GetParameters().ToDictionary(p => p.Name!);
		foreach (var name in new[] { "Files", "GitFiles" })
			Assert.False(parameters[name].HasDefaultValue, name);
	}

	[Fact]
	public void Entry_state_reads_a_file_whole_in_chunks()
	{
		byte[] data = [.. Enumerable.Repeat((byte)'a', 3 << 20), (byte)'b'];
		File.WriteAllBytes(P("big"), data);
		Assert.Equal(Sha(data), SessionGuard.EntryState(P("big"))!.Detail);
	}

	[Fact]
	public void An_owned_list_that_fails_after_the_session_is_a_stop()
	{
		var g = new Guarded(tmp);
		g.Guard();
		Assert.Equal(["can't list the Owned files the branch changes: git diff failed"],
				g.Check(_ => throw new InvalidOperationException("git diff failed")));
	}

	public static TheoryData<string, string, string, string> FolderCases()
	{
		var data = new TheoryData<string, string, string, string>();
		foreach (var (where, what) in new[] { ("ledgers", "the review ledgers"), ("security", "the .security record") })
			foreach (var how in new[] { "added", "removed", "changed" })
				foreach (var name in new[] { "l.json", "l.entries", ".lock", "0001-a.md", "README.md", "x.tmp" })
					data.Add(where, what, how, name);
		return data;
	}

	[Theory]
	[MemberData(nameof(FolderCases))]
	public void Any_file_added_removed_or_changed_in_the_ledgers_or_the_security_record_is_caught(string where, string what, string how, string name)
	{
		// The round records, not the session, so the session writes neither folder: whatever a file is called, a lock or a temporary file
		// included, a change is a stop.
		var g = new Guarded(tmp);
		var folder = where == "ledgers" ? g.Ledgers : g.Security;
		if (how != "added")
			Write(Path.Combine(folder, name), "before");
		g.Guard();
		if (how == "removed")
			File.Delete(Path.Combine(folder, name));
		else
			Write(Path.Combine(folder, name), "after");
		Assert.Equal([string.Format(FolderChanged, Quoted(name), folder, how, what)], g.Check());
	}

	[Fact]
	public void Files_there_before_and_unchanged_are_no_stop_whatever_they_hold()
	{
		var g = new Guarded(tmp);
		g.Ledger("old-bad.json", "{");
		g.Ledger("l.json", """{"branch": "chore/reapply-baseline", "head": "0000000000000000000000000000000000000000", "findings": [{"id": "R1", "status": "accepted"}]}""");
		g.Entry("0001-a.md", "fixed");
		Write(Path.Combine(g.Ledgers, ".lock"), "1 abc\n");
		g.Guard();
		Assert.Empty(g.Check());
	}

	[Fact]
	public void A_folder_that_isnt_there_reads_as_empty_and_one_made_with_files_is_caught()
	{
		Assert.Empty(SessionGuard.FolderState(P("none")));
		var g = new Guarded(tmp);
		Directory.Delete(g.Ledgers);
		Directory.Delete(g.Security);
		g.Guard();
		Assert.Empty(g.Check());
		Directory.CreateDirectory(g.Security);
		g.Entry("0001-a.md", "open");
		Assert.Equal([string.Format(FolderChanged, "'0001-a.md'", g.Security, "added", "the .security record")], g.Check());
	}

	[Theory]
	[InlineData("ledgers")]
	[InlineData("security")]
	public void A_file_that_didnt_read_before_and_was_swapped_for_another_is_caught(string where)
	{
		var g = new Guarded(tmp);
		var folder = where == "ledgers" ? g.Ledgers : g.Security;
		Write(P("one"), "x");
		Write(P("two"), "x");
		File.CreateSymbolicLink(Path.Combine(folder, "f"), P("one"));
		g.Guard();
		Assert.Empty(g.Check());
		File.Delete(Path.Combine(folder, "f"));
		File.CreateSymbolicLink(Path.Combine(folder, "f"), P("two"));
		var broke = g.Check();
		Assert.Single(broke);
		Assert.StartsWith($"'f' in {folder} was changed during the session", broke[0]);
	}

	[Theory]
	[InlineData("fifo")]
	[InlineData("zero")]
	[InlineData("dangling")]
	[InlineData("oversized")]
	public void A_file_that_isnt_a_small_regular_one_is_never_read_and_is_caught(string what)
	{
		// Nothing in either folder is read in a way that hangs or fills memory (RecordBytes).
		var g = new Guarded(tmp);
		g.Guard();
		var path = Path.Combine(g.Ledgers, "x.json");
		if (what == "fifo")
			MkFifo(path);
		else if (what == "zero")
			File.CreateSymbolicLink(path, "/dev/zero");
		else if (what == "dangling")
			File.CreateSymbolicLink(path, P("nowhere.json"));
		else
			Write(path, """{"findings": []}""");
		var limit = SessionGuard.RecordLimit;
		if (what == "oversized")
			SessionGuard.RecordLimit = 10;
		try
		{
			Assert.Equal([string.Format(FolderChanged, "'x.json'", g.Ledgers, "added", "the review ledgers")], g.Check());
		}
		finally
		{
			SessionGuard.RecordLimit = limit;
		}
	}

	[Fact]
	public void A_file_name_is_printed_with_its_control_characters_escaped()
	{
		var g = new Guarded(tmp);
		g.Guard();
		Write(Path.Combine(g.Ledgers, "a\x1b[2J\nrollout.sh: all is well\u202e.json"), "x");
		var rule = Assert.Single(g.Check());
		Assert.DoesNotContain("\x1b", rule, StringComparison.Ordinal);
		Assert.DoesNotContain("\n", rule, StringComparison.Ordinal);
		Assert.DoesNotContain("\u202e", rule, StringComparison.Ordinal);
		Assert.StartsWith($@"'a\x1b[2J\nrollout.sh: all is well\u202e.json' in {g.Ledgers} was added during the session", rule);
	}

	[Fact]
	public void Shown_quotes_and_escapes_what_the_session_wrote()
	{
		Assert.Equal("'R1'", SessionGuard.Shown("R1"));
		Assert.Equal(@"'R1\x1b]0;x\x07\n'", SessionGuard.Shown("R1\x1b]0;x\x07\n"));
		Assert.Equal("None", SessionGuard.Shown((System.Text.Json.Nodes.JsonNode?)null));
		Assert.Equal("['x']", SessionGuard.Shown(System.Text.Json.Nodes.JsonNode.Parse("""["x"]""")));
	}

	[Fact]
	public void An_unread_files_identity_is_what_lstat_says_and_where_it_leads()
	{
		File.CreateSymbolicLink(P("l"), "/somewhere");
		var info = Posix.LStat(P("l"))!.Value;
		Assert.Equal($"unread {Convert.ToString(info.Mode, 8)} {info.Ino} {info.Size} {info.MtimeNs} /somewhere", SessionGuard.Identity(P("l"), null));
		Assert.Equal("gone", SessionGuard.Identity(P("missing"), null));
		Assert.Equal(Sha("abc"u8.ToArray()), SessionGuard.Identity("x", "abc"u8.ToArray()));
	}

	[Fact]
	public void Record_bytes_reads_a_regular_file_up_to_its_limit_only()
	{
		var path = P("f");
		var ten = Enumerable.Repeat((byte)'x', 10).ToArray();
		File.WriteAllBytes(path, ten);
		Assert.Equal(ten, SessionGuard.RecordBytes(path, limit: 10));
		Assert.Null(SessionGuard.RecordBytes(path, limit: 9));
		Assert.Equal(ten, SessionGuard.RecordBytes(path, limit: 11));
		Assert.Null(SessionGuard.RecordBytes(P("missing")));
		// A folder isn't read.
		Assert.Null(SessionGuard.RecordBytes(tmp));
	}

	[Fact]
	public void Link_text_reads_only_a_plain_file()
	{
		Assert.Null(SessionGuard.LinkText(tmp));
		File.WriteAllBytes(P(".git"), "gitdir: /x\n"u8.ToArray());
		Assert.Equal("gitdir: /x\n"u8.ToArray(), SessionGuard.LinkText(tmp));
	}

	[Fact]
	public void Run_text_strips_trailing_newlines_only_and_gives_none_for_a_failure()
	{
		Assert.Equal("  a", SessionGuard.RunText(["printf", "  a\\n\\n"]));
		Assert.Null(SessionGuard.RunText(["false"]));
		Assert.Null(SessionGuard.RunText(["/nonexistent/tool"]));
		Assert.Equal(tmp, SessionGuard.RunText(["pwd"], cwd: tmp));
	}

	[Fact]
	public void Run_text_gives_none_for_a_program_not_on_path()
	{
		Assert.Null(SessionGuard.RunText(["no-such-tool-xyz"]));
	}

	[Fact]
	public void A_name_that_isnt_utf8_reads_as_changed_every_time()
	{
		Directory.CreateDirectory(P("tree"));
		Run("sh", ["-c", "touch \"$(printf 'a\\377b')\"", "sh"], cwd: P("tree"));
		var name = "a\uFFFDb";
		Assert.Equal([name], SessionGuard.TreeChanges(SessionGuard.TreeState(P("tree")), SessionGuard.TreeState(P("tree"))));
		Assert.Single(SessionGuard.FolderChanges(P("tree"), SessionGuard.FolderState(P("tree")), "the review ledgers"));
		Assert.NotEqual(SessionGuard.TreeHashes(P("tree"))[name], SessionGuard.TreeHashes(P("tree"))[name]);
		// .NET can't name the file to delete it either.
		Run("rm", ["-rf", P("tree")]);
	}

	[Fact]
	public void Run_text_gives_none_once_its_limit_passes_while_a_child_left_behind_holds_its_output()
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		var text = SessionGuard.RunText(["sh", "-c", "sleep 5 & echo a"], timeoutSeconds: 1);
		Assert.Null(text);
		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
	}

	[Fact]
	public void The_guard_of_a_linked_worktree_reads_the_primary_checkouts_ledgers()
	{
		var primary = P("Repo");
		var git = GitRepo(primary);
		var worktree = P("Repo-worktrees", "chore-reapply-baseline");
		git(["worktree", "add", "-q", "-b", "chore/reapply-baseline", worktree]);
		var ledgers = Path.Combine(primary, ".git", "review");
		Directory.CreateDirectory(ledgers);
		Write(Path.Combine(ledgers, "l.json"), """{"branch": "chore/reapply-baseline", "findings": []}""");
		var owned = P("owned");
		Directory.CreateDirectory(owned);
		string? Read(IReadOnlyList<string> c) => c.Contains("ls-remote") ? "" : SessionGuard.RunText(c);
		var guard = SessionGuard.Read(worktree, owned, Read);
		Assert.Equal(ledgers, guard.Ledgers);
		Assert.Equal(["l.json"], guard.LedgersBefore.Keys);
		Assert.Equal(Path.Combine(primary, ".security"), guard.Security);
		Write(Path.Combine(ledgers, "l.json"), """{"branch": "x", "findings": [{"id": "R1", "status": "accepted"}]}""");
		var broke = SessionGuard.BrokenRules(guard, owned, _ => [], Read);
		Assert.Equal([string.Format(FolderChanged, "'l.json'", ledgers, "changed", "the review ledgers")], broke);
	}
}
