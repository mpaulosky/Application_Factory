using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

// Tests for github/AutoRolloutReview.cs, xUnit copies of github/tests/test_auto_rollout.py's tests of how a review session ends (its settings
// file removed and swept, the signals that land after or while it runs, the rule it broke said before one) and of what the round says around
// it (ADR 0016's text, the lock's resume command, the unset hints; ADR 0017's Update for mpaulosky/Application_Factory#122): one method
// each, the same cases and assertions. Python's KeyboardInterrupt is the Interrupted exception, and its os.kill(os.getpid(), SIGINT) a libc
// kill of the test process with SIGHUP, sent only while RunReview holds the signals: .NET runs every registration for a signal, the test
// runner's Console.CancelKeyPress among them, which a SIGINT would cancel the run with (as SessionGroupTests.cs has it).
[Collection(nameof(RoundCollection))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ReviewSessionEndTests : IDisposable
{
	readonly string tmp;

	// Skips off Linux before anything is made, as SessionGuardTests does.
	public ReviewSessionEndTests()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		tmp = Directory.CreateTempSubdirectory("auto-rollout.").FullName;
	}

	public void Dispose() => Directory.Delete(tmp, recursive: true);

	string P(params string[] parts) => Path.Combine([tmp, .. parts]);

	[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
	static extern int Kill(int pid, int sig);

	// os.kill(os.getpid(), SIGINT), a SIGHUP here, while a hold is registered: its handler runs on another thread, so this waits for it to
	// record the signal.
	static void Interrupt()
	{
		Assert.Equal(0, Kill(Environment.ProcessId, Signals.SigHup));
		Thread.Sleep(200);
	}

	// A SIGHUP the test host's caller ignores (nohup's) isn't held, so it can't be sent here.
	static void NeedsHeld() => Assert.SkipUnless(Signals.Held(Signals.SigHup), "the test host ignores SIGHUP");

	// logs_left: the settings files left in a run's log folder.
	static List<string> LogsLeft(string root) =>
			[.. Directory.EnumerateFileSystemEntries(Path.Combine(root, "logs"), "*.settings.json").Select(Path.GetFileName).OfType<string>()
					.Order(StringComparer.Ordinal)];

	[Fact]
	public void The_adr_says_what_the_sandbox_doesnt_cover()
	{
		var adr = Directory.GetFiles(Path.GetFullPath(Path.Combine(Skill.Dir, "..", "..", "..", "docs", "adr")), "0016-*.md").Single();
		var text = string.Join(' ', File.ReadAllText(adr).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		void Has(string part) => Assert.Contains(part, text, StringComparison.Ordinal);
		void HasNot(string part, string why) => Assert.False(text.Contains(part, StringComparison.Ordinal), why);
		Has("The sandbox holds only as far as Claude Code enforces it");
		Has("its reads are denials listed as the session starts, so what is made beside its folders while it runs"
				+ " (another checkout's `.env`, say) is readable until it ends, and is named then");
		HasNot("a list of what to hide", "the walk from / replaced the list");
		Has("`rollout.sh --auto` runs on Linux only");
		Has("with no host to reach, what the session reads can leave only through what it writes");
		Has("It reaches no host");
		HasNot("reachable for anything", "no host is on the allowed list");
		Has("A SIGKILL of `rollout.sh` can't be caught");
		HasNot("however it is spelled", "the earlier overclaims are gone");
		HasNot("this is not a sandbox", "the earlier overclaims are gone");
	}

	[Fact]
	public void The_settings_file_is_removed_however_the_session_ends()
	{
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(P("a"), session: new FakeSession(raises: new Interrupted(Signals.SigInt))));
		Assert.Empty(LogsLeft(P("a"))); // an interrupt

		var (code, _, _, _) = ReviewFixtures.ReviewOnce(P("b"), popen: (_, _) => throw new SpawnError(2, "claude"));
		Assert.Equal(127, code);
		Assert.Empty(LogsLeft(P("b"))); // claude can't start

		// The pytest patches session_env, which has no seam: the agent definition, read next to it after the settings file is written, throws
		// the same ValueError (ArgumentException) instead.
		string log;
		(code, log, _, _) = ReviewFixtures.ReviewOnce(P("c"), agents: () => throw new ArgumentException("a bug"));
		Assert.Equal(127, code);
		Assert.Contains("can't set the session up: a bug", File.ReadAllText(log), StringComparison.Ordinal);
		Assert.Empty(LogsLeft(P("c"))); // a stop after the file is written, before the session

		// RuntimeError is InvalidOperationException.
		(code, _, _, _) = ReviewFixtures.ReviewOnce(P("d"), record: (_, _, _, _, _, _, _) => throw new InvalidOperationException("a bug"));
		Assert.Equal(ReviewRecord.RecordFailed, code);
		Assert.Empty(LogsLeft(P("d"))); // a record that raises
	}

	[Fact]
	public void Settings_files_killed_runs_left_are_removed_by_the_next()
	{
		var logs = P("logs");
		Directory.CreateDirectory(logs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		var target = P("elsewhere.settings.json");
		File.WriteAllText(target, "{}");
		File.WriteAllText(Path.Combine(logs, "review-TicketManager.old.settings.json"), "{}");
		File.CreateSymbolicLink(Path.Combine(logs, "review-Articles.old.settings.json"), target);
		File.WriteAllText(Path.Combine(logs, "review-TicketManager.old.log"), "kept");
		File.WriteAllText(Path.Combine(logs, "notes.settings.json"), "kept");
		var (code, _, _, _) = ReviewFixtures.ReviewOnce(tmp);
		Assert.Equal(0, code);
		// Only the round's own kind, and the new one is gone too.
		Assert.Equal(["notes.settings.json"], LogsLeft(tmp));
		// A link is removed, never where it leads.
		Assert.True(File.Exists(target));
		Assert.True(File.Exists(Path.Combine(logs, "review-TicketManager.old.log")));
	}

	[Fact]
	public void The_settings_files_folder_is_hidden_from_the_session_and_not_writable()
	{
		var home = PyLib.ExpandUser("~");
		var folder = Path.GetDirectoryName(ReviewSession.SettingsPath(Path.Combine(AutoRollout.LogDir, "review-TicketManager.x.log")))!;
		Assert.Equal(AutoRollout.LogDir, folder);
		var settings = AutoRollout.SessionSettings("/w/wt", "/c/clone/.git", "/c/clone/.git/worktrees/wt", "/t/session", home: home, configDir: "",
				environ: new Dictionary<string, string>(), listdir: _ => [], which: _ => null);

		bool Covers(string path) => folder == path || folder.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal);

		static List<string> Strings(JsonNode? node) => [.. node!.AsArray().Select(item => (string)item!)];

		var filesystem = settings["sandbox"]!["filesystem"]!;
		var denied = Strings(filesystem["denyRead"]).Where(Covers).ToList();
		Assert.True(denied.Count > 0, "the sandbox's Bash can't read it");
		var deny = Strings(settings["permissions"]!["deny"]);
		Assert.True(denied.Any(path => deny.Contains($"Read(/{path}/**)")), "nor can Read");
		Assert.False(Strings(filesystem["allowWrite"]).Any(Covers), "nor write it");
		static string Edited(string rule)
		{
			var path = rule["Edit(/".Length..];
			path = path.EndsWith("/**)", StringComparison.Ordinal) ? path[..^"/**)".Length] : path;
			return path.EndsWith(')') ? path[..^1] : path;
		}
		var edits = Strings(settings["permissions"]!["allow"]).Where(rule => rule.StartsWith("Edit(", StringComparison.Ordinal)).Select(Edited).ToList();
		Assert.True(edits.Count > 0 && !edits.Any(Covers), "nor Edit it");
	}

	[Fact]
	public void A_signal_after_the_session_exits_by_itself_keeps_what_it_staged()
	{
		NeedsHeld();
		// The signal lands while the group is reaped: the session finished, so its findings are kept, not lost.
		var head = new string('a', 40);
		var err = new StringWriter();
		Guard WithHead(string worktree) => ReviewFixtures.FakeGuard(tmp) with { WorktreeState = (head, "branchname", "") };
		bool End(IGroupChild session)
		{
			Interrupt();
			return true;
		}
		// The round's own record (its RecordReview), which, with the signal held, records nothing.
		(int, List<string>) Record(string worktree, string run, FileStream out_, string at, string keep, IList<int> got, Func<List<string>> unseen)
		{
			File.WriteAllText(Path.Combine(run, ReviewRecord.StagedFile), "[]");
			return ReviewRecord.RecordReview(worktree, run, out_, at, keep,
					(command, io) => ReviewRecord.RunBounded(command, io, Groups.SpawnBounded, child => Groups.EndGroup(child)), got, unseen);
		}
		var stopped = Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, guard: WithHead, end: End, record: Record, err: err));
		Assert.Equal(Signals.SigHup, stopped.Signum);
		var log = Assert.Single(Directory.GetFiles(P("logs"), "*.log"));
		var keep = $"{log}.staged";
		Assert.True(JsonNode.DeepEquals(new JsonArray(), JsonNode.Parse(File.ReadAllText(Path.Combine(keep, ReviewRecord.StagedFile)))));
		var said = err.ToString();
		Assert.Contains($"what the session staged is kept in {keep}", said, StringComparison.Ordinal);
		Assert.Contains("record it with: sh -c ", said, StringComparison.Ordinal);
		Assert.False(said.Contains("recording the review", StringComparison.Ordinal), "nothing is recorded with a signal held");
		Assert.Empty(LogsLeft(tmp));
	}

	// A session still running: a wait with a time sends the SIGINT and runs out (the pytest's TimeoutExpired is an Exit of null).
	sealed class Running : IGroupChild
	{
		public int Pid => 1 << 30; // no process's: the end is faked anyway

		public int? Exit(double seconds)
		{
			Interrupt(); // while it still runs
			return null;
		}
	}

	[Fact]
	public void A_signal_while_a_session_runs_still_ends_the_round_at_once()
	{
		NeedsHeld();
		List<object[]> recorded = [];
		List<Guard> checkedGuards = [];
		bool End(IGroupChild session)
		{
			Interrupt();
			return true;
		}
		List<string> Check(Guard guard)
		{
			checkedGuards.Add(guard);
			return [];
		}
		(int, List<string>) Record(string worktree, string run, FileStream out_, string head, string keep, IList<int> got, Func<List<string>> unseen)
		{
			recorded.Add([worktree, run]);
			return (0, []);
		}
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(P("a"), session: new FakeSession(code: 1), end: End, check: Check, record: Record));
		// A session that didn't exit 0: no check runs, nothing to record.
		Assert.Empty(recorded);
		Assert.Empty(checkedGuards);

		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(P("b"), session: new Running(), check: Check, record: Record));
		// Stopped mid-review: no check runs, nothing to record.
		Assert.Empty(recorded);
		Assert.Empty(checkedGuards);
	}

	[Fact]
	public void The_lock_stops_resume_with_the_rounds_own_root()
	{
		using var locked = new Fleet { Locked = true };
		var (_, _, err) = locked.RunAuto(["--brings", "#82"], root: "/my repos");
		Assert.EndsWith("then run: rollout.sh --auto --brings '#82' --root '/my repos'\n", err);
		// PermissionError(13, "Permission denied") is UnauthorizedAccessException.
		using var refused = new Fleet { LockRaises = new UnauthorizedAccessException("[Errno 13] Permission denied") };
		(_, _, err) = refused.RunAuto(["--brings", "#82"], root: "/my repos");
		Assert.EndsWith("Once it can be taken, run: rollout.sh --auto --brings '#82' --root '/my repos'\n", err);
	}

	[Fact]
	public void The_unset_hints_quote_the_variables_name()
	{
		var problems = AutoRollout.SignInProblems(new Dictionary<string, string>
		{
			["CLAUDE_CODE_USE_BEDROCK"] = "1",
			["AWS_X;Y_TOKEN"] = "t",
			["AWS_ODD NAME"] = "https://u:p@x/",
		});
		Assert.Equal(2, problems.Count);
		Assert.Contains("(env -u 'AWS_X;Y_TOKEN' before the command)", problems[0], StringComparison.Ordinal);
		Assert.Contains("(env -u 'AWS_ODD NAME' before the command, for one round)", problems[1], StringComparison.Ordinal);
	}

	[Fact]
	public void A_rule_broken_before_a_signal_is_said_on_the_terminal()
	{
		NeedsHeld();
		List<object[]> recorded = [];
		bool End(IGroupChild session)
		{
			Interrupt();
			return true;
		}
		(int, List<string>) Record(string worktree, string run, FileStream out_, string head, string keep, IList<int> got, Func<List<string>> unseen)
		{
			recorded.Add([worktree, run]);
			return (0, []);
		}
		var err = new StringWriter();
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(P("a"), end: End, broke: ["the worktree changed"], record: Record, err: err));
		var said = err.ToString();
		Assert.Contains("rollout.sh: the session broke a rule: the worktree changed\n", said, StringComparison.Ordinal);
		Assert.Empty(recorded);
		Assert.False(said.Contains("record it with", StringComparison.Ordinal), said);
		Assert.False(said.Contains("recording the review", StringComparison.Ordinal), said);
		var log = Assert.Single(Directory.GetFiles(P("a", "logs"), "*.log"));
		Assert.Contains("rollout.sh: the session broke a rule: the worktree changed\n", File.ReadAllText(log), StringComparison.Ordinal);

		bool Stuck(IGroupChild session)
		{
			Interrupt();
			return false;
		}
		err = new StringWriter();
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(P("b"), end: Stuck, err: err));
		Assert.Contains($"rollout.sh: the session broke a rule: the session's process group was still there {ReviewRecord.GroupWaitSeconds} s after it"
				+ " was killed", err.ToString(), StringComparison.Ordinal);
	}
}
