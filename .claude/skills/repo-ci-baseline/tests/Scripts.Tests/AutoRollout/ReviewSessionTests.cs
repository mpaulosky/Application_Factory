using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

// Tests for github/AutoRolloutReview.cs's ReviewSession.RunReview, xUnit copies of github/tests/test_auto_rollout.py's tests of run_review
// and review_in through review_once, and of the session folder (ADR 0017's "When a pytest suite is replaced" Update for
// mpaulosky/Application_Factory#122): one method each, the same cases and assertions. The pytest's os.kill(os.getpid(), SIGINT) is a real
// signal to the test process here too, sent only while RunReview's hold (Signals.DeferredSignals) is registered, but SIGTERM, not SIGINT:
// .NET runs every registration for a signal, so a SIGINT also reaches the xUnit runner's Ctrl+C handler, which cancels the rest of the run.
// The hold treats the two alike; only the Interrupted it passes on differs (Signum 15, as SystemExit(143), not KeyboardInterrupt).
[Collection(nameof(RoundCollection))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ReviewSessionTests : IDisposable
{
	const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
	static readonly string Head = new('c', 40);

	readonly string tmp;

	// Skips off Linux before anything is made, as SessionGuardTests does.
	public ReviewSessionTests()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		tmp = Directory.CreateTempSubdirectory("auto-rollout.").FullName;
	}

	// The folders a test locked are opened again first, never through a symlink, so the whole tree can be removed.
	public void Dispose()
	{
		void Open(string folder)
		{
			File.SetUnixFileMode(folder, Private);
			foreach (var path in Directory.GetFileSystemEntries(folder))
				if (Posix.LStat(path) is { IsDir: true })
					Open(path);
		}
		Open(tmp);
		Directory.Delete(tmp, recursive: true);
	}

	string P(params string[] parts) => Path.Combine([tmp, .. parts]);

	// Python's ValueError, so a message that names the error's type reads as the original's.
	sealed class ValueError(string message) : Exception(message);

	[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
	static extern int Kill(int pid, int signum);

	// A test host whose caller ignores SIGTERM holds none, so a test that sends one is skipped there, before RunReview (whose record
	// would take a skip thrown inside it for a failed record).
	static void NeedsHeld() => Assert.SkipUnless(Signals.Held(Signals.SigTerm), "the test host ignores SIGTERM");

	// interrupt(): a SIGTERM (for the pytest's SIGINT) to the test process, and time for the hold's handler, which runs on another thread,
	// to record it.
	static void Interrupt()
	{
		Assert.Equal(0, Kill(Environment.ProcessId, Signals.SigTerm));
		Thread.Sleep(200);
	}

	// head_guard: the fake guard, its worktree's HEAD Head.
	Func<string, Guard> HeadGuard() => _ => ReviewFixtures.FakeGuard(tmp) with { WorktreeState = (Head, "branchname", "") };

	// The one log a review in folder wrote (the pytest's glob of *.log).
	static string LogIn(string folder) =>
		Directory.GetFiles(Path.Combine(folder, "logs")).Single(path => path.EndsWith(".log", StringComparison.Ordinal));

	static int Count(string text, string part)
	{
		var (count, at) = (0, 0);
		while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
			(count, at) = (count + 1, at + part.Length);
		return count;
	}

	static void SameJson(JsonNode? expected, JsonNode? actual) =>
			Assert.True(JsonNode.DeepEquals(expected, actual), $"{expected?.ToJsonString()} != {actual?.ToJsonString()}");

	[Fact]
	public void The_review_session_runs_sandboxed_in_the_worktree_with_the_templates_skill()
	{
		var (code, log, seen, worktree) = ReviewFixtures.ReviewOnce(tmp);
		Assert.Equal(0, code);
		var command = seen.Command!.ToList();
		Assert.Equal(["claude", "-p"], command[..2]);
		string Flag(string name) => command[command.IndexOf(name) + 1];

		Assert.True(Flag("--permission-mode") == "dontAsk", "anything not allowed is refused");
		Assert.True(Flag("--setting-sources") == "", "no settings file is loaded: neither the repository's nor the person's");
		Assert.True(Flag("--tools") == string.Join(',', AutoRollout.SessionBuiltins), "only these built-in tools exist in the session");
		Assert.True(command.Contains("--strict-mcp-config") && !command.Contains("--mcp-config"), "no MCP server");
		SameJson(ReviewFixtures.Agents(), JsonNode.Parse(Flag("--agents")));
		Assert.True(!command.Contains("--add-dir") && !command.Contains("--bare"));
		Assert.True(command[(command.IndexOf("--disallowedTools") + 1)..].SequenceEqual(AutoRollout.DeniedTools),
				"the deny rules are passed on the command line too, last, so the variadic flag takes nothing else");
		var clone = P("clone", ".git");
		// the settings are built from the guard's folders
		SameJson(ReviewFixtures.SettingsOf(worktree, clone, Path.Combine(clone, "worktrees", "chore-reapply-baseline"), seen.Tmp!), seen.Settings);
		Assert.True(Path.GetDirectoryName(seen.Tmp) == P("sessions"), "in home, not /tmp, which the sandbox denies");
		Assert.StartsWith("rollout-session.", Path.GetFileName(seen.Tmp), StringComparison.Ordinal);
		Assert.Equal(Private, File.GetUnixFileMode(P("sessions")));
		// the session writes neither the ledgers nor .security, so neither is made for it
		Assert.Equal(new Dictionary<string, bool> { [".git/review"] = false, [".security"] = false }, seen.Folders);
		var run = Path.Combine(seen.Tmp!, "run");
		Assert.True(seen.Run == (run, Private), "its run folder is made for it, the person's own, in its temporary folder");
		SameJson(new JsonArray(seen.Tmp), seen.Settings!["sandbox"]!["filesystem"]!["allowWrite"]);
		Assert.True(seen.Recorded == (worktree, run, true), "once it exits 0 having broken no rule, the round records its run");
		Assert.True(seen.How!.PassFds.Count == 0, "no lock held here, so none is passed on");
		// start_new_session has no flag here: the Spawner always starts its command in a session of its own (Groups.Spawn's SETSID).
		Assert.Equal(worktree, seen.How.Cwd);
		Assert.True(seen.How.StdinDevNull && seen.How.StderrToStdout);
		Assert.True(File.ReadAllText(log).Contains("the session's own output", StringComparison.Ordinal), "the session's output is the log");
		Assert.True(seen.Ended.Count == 1, "its group is ended when it exits normally");
		Assert.True(seen.Guarded == worktree, "what the session may not change is read before it starts");
		var env = seen.How.Env;
		Assert.False(env.ContainsKey("GATE_SKIP_REVIEW"));
		Assert.Equal(Environment.GetEnvironmentVariable("PATH"), env["PATH"]);
		Assert.Equal(Environment.GetEnvironmentVariable("HOME"), env["HOME"]);
		Assert.True(seen.GhConfig!.Count == 0, "gh gets an empty config: signed out");
		var prompt = command[2];
		Assert.True(prompt.EndsWith(ReviewFixtures.SkillText, StringComparison.Ordinal), "the skill's text is the Template's, carried in the prompt");
		Assert.Contains(worktree, prompt, StringComparison.Ordinal);
		Assert.Contains(Fleets.TM, prompt, StringComparison.Ordinal);
		Assert.Contains(".github/hooks/pre-push, scripts/gate.sh", prompt, StringComparison.Ordinal);
		Assert.Contains(Path.Combine(worktree, ".claude", "skills", "pre-push-review"), prompt, StringComparison.Ordinal);
		foreach (var rule in new[]
		{
			"never ask one", "Review only: this session changes nothing in the repository",
			"Don't edit, fix or commit anything, and don't run the gate", "the skill's last, review-only round",
			"stage open every finding the skill's triage doesn't dismiss, with its suggested fix", "Never dismiss a security finding",
			"a repeat of a fixed .security entry too, with that entry's number in the summary.",
			"Give every finding you stage its security mark, as the skill's triage says: true, or false only once you"
				+ " have read the code and it isn't a security weakness; rollout.sh takes a dismissed finding without"
				+ " \"security\": false for a security one, and stops the round.",
			"Leave out every finding's repeats field, whatever entry it would name (fixed, open or accepted): rollout.sh"
				+ " drops it",
			$"Your run folder is {run}, made for you", "write nothing anywhere else",
			"its review ledgers and the .security record are read-only to you",
			$"scripts/review.sh stage {run} <file>", "as an empty array [] when it keeps none",
			"don't run scripts/review.sh record", "rollout.sh records what you staged once the session ends",
			"what to change in the Factory's template/owned/", "Don't push",
			"don't file the follow-up issue", "don't edit a PR", "skip the skill's GitHub reads",
			"and its git fetch", "go on with origin/main as it is", "no network",
			"End with a list of the findings you staged",
		})
			Assert.True(prompt.Contains(rule, StringComparison.Ordinal), rule);
	}

	[Theory]
	[InlineData(1, false)]
	[InlineData(0, true)]
	public void A_session_that_failed_or_broke_a_rule_is_never_recorded(int sessionCode, bool brokeARule)
	{
		var (_, _, seen, _) = ReviewFixtures.ReviewOnce(tmp, session: new FakeSession(code: sessionCode), broke: brokeARule ? ["a rule"] : []);
		Assert.Null(seen.Recorded);
	}

	[Fact]
	public void A_session_whose_group_survived_is_never_recorded()
	{
		var (_, _, seen, _) = ReviewFixtures.ReviewOnce(tmp, end: _ => false);
		Assert.Null(seen.Recorded);
	}

	[Theory]
	[InlineData(ReviewRecord.RecordFailed, null, ReviewRecord.RecordFailed)]
	[InlineData(0, null, 0)]
	[InlineData(ReviewRecord.SessionBrokeARule, "R2 was staged fixed", ReviewRecord.SessionBrokeARule)]
	public void The_records_outcome_is_the_reviews(int recorded, string? rule, int expected)
	{
		List<string> broke = rule is null ? [] : [rule];
		var (got, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, record: (_, _, _, _, _, _, _) => (recorded, [.. broke]));
		Assert.Equal(expected, got);
		Assert.Equal(broke, seen.Broke);
		foreach (var each in broke)
			Assert.Contains($"rollout.sh: the session broke a rule: {each}", File.ReadAllText(log), StringComparison.Ordinal);
	}

	[Fact]
	public void A_failed_record_naming_a_group_prepare_left_is_no_broken_rule()
	{
		List<string> left = ["what review.sh prepare left ..."];
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, record: (_, _, _, _, _, _, _) => (ReviewRecord.RecordFailed, left));
		Assert.Equal(ReviewRecord.RecordFailed, code);
		Assert.Equal(left, seen.Broke);
		Assert.DoesNotContain("broke a rule", File.ReadAllText(log), StringComparison.Ordinal);
	}

	// OSError, PermissionError and SubprocessError are IOException, UnauthorizedAccessException and SpawnError here (a child the record
	// couldn't start, which Python's SubprocessError from preexec_fn stood for), each named as Python names it.
	[Theory]
	[InlineData("OSError", "OSError: no space")]
	[InlineData("PermissionError", "PermissionError: [Errno 13] Permission denied")]
	[InlineData("SpawnError", "FileNotFoundError: [Errno 2] No such file or directory: 'scripts/review.sh'")]
	public void A_record_that_cant_run_is_record_failed_with_its_reason_in_the_log(string kind, string said)
	{
		Exception error = kind switch
		{
			"OSError" => new IOException("no space"),
			"PermissionError" => new UnauthorizedAccessException("[Errno 13] Permission denied"),
			_ => new SpawnError(2, "scripts/review.sh"),
		};
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, record: (_, _, _, _, _, _, _) => throw error);
		Assert.Equal(ReviewRecord.RecordFailed, code);
		Assert.Empty(seen.Broke);
		Assert.Contains($"rollout.sh: can't record the review: {said}", File.ReadAllText(log), StringComparison.Ordinal);
	}

	[Fact]
	public void The_record_is_given_the_head_the_guard_read_and_a_folder_beside_the_log()
	{
		var (_, log, seen, _) = ReviewFixtures.ReviewOnce(tmp);
		Assert.True(seen.RecordHead == "headsha", "the guard's HEAD (not its branch), read before the session");
		Assert.Equal($"{log}.staged", seen.Keep);
		Assert.Equal(ReviewRecord.StagedCopy(log), seen.Keep);
		Assert.True(seen.Got!.Count == 0, "the round's deferred signals, so a signal can stop prepare");
		Assert.True(seen.Unseen!.Count == 0, "the guard's own unseen_changes, asked again just before record runs");
	}

	[Fact]
	public void The_record_asks_the_guards_own_snapshot_again()
	{
		Guard WithFiles(string _) =>
			ReviewFixtures.FakeGuard(tmp) with { Files = new() { ["."] = new Entry("dir", Convert.ToUInt32("40755", 8)) } };

		var (_, _, seen, _) = ReviewFixtures.ReviewOnce(tmp, guard: WithFiles);
		Assert.Single(seen.Unseen!);
		Assert.StartsWith("1 path(s) in w changed during the session", seen.Unseen![0], StringComparison.Ordinal);
	}

	[Fact]
	public void A_signal_during_the_checks_is_held_until_the_findings_are_kept_and_the_command_is_printed()
	{
		NeedsHeld();
		List<int>? got = null;
		var err = new StringWriter();
		var thrown = Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, check: _ =>
		{
			Interrupt();
			return [];
		}, record: (_, _, _, head, keep, signals, _) =>
		{
			got = [.. signals];
			ReviewRecord.WriteStaged(keep, "[]", head);
			return (ReviewRecord.RecordFailed, []);
		}, guard: HeadGuard(), err: err));
		Assert.Equal(Signals.SigTerm, thrown.Signum);
		Assert.True(got is [Signals.SigTerm], "the record is still run, told of the signal, so it keeps the findings");
		var log = LogIn(tmp);
		var keep = ReviewRecord.StagedCopy(log);
		var worktree = ReviewFixtures.DefaultWorktree(tmp);
		Assert.Contains($"rollout.sh: what the session staged is kept in {keep} (see {log}); record it with:"
				+ $" {ReviewRecord.RecordCommand(worktree, keep, Head)}\n", err.ToString(), StringComparison.Ordinal);
	}

	// The original monkeypatched staged_copy, the first thing after the session's group is gone; the C# has no seam there, so the signal
	// comes from end, as the group is found gone, which is as late in the same hold and before the checks.
	[Fact]
	public void A_signal_between_the_session_and_the_checks_is_held_too()
	{
		NeedsHeld();
		var err = new StringWriter();
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, end: _ =>
		{
			Interrupt();
			return true;
		}, record: (_, _, _, head, keep, _, _) =>
		{
			ReviewRecord.WriteStaged(keep, "[]", head);
			return (ReviewRecord.RecordFailed, []);
		}, guard: HeadGuard(), err: err));
		Assert.True(err.ToString().Contains("what the session staged is kept in", StringComparison.Ordinal), "held, so the findings were kept");
	}

	[Fact]
	public void A_signal_after_a_record_that_left_its_group_says_to_check_the_report_first()
	{
		NeedsHeld();
		var worktree = P("Ticket Manager", "chore-reapply-baseline");
		Directory.CreateDirectory(worktree);
		var err = new StringWriter();
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, record: (_, _, _, head, keep, _, _) =>
		{
			ReviewRecord.WriteStaged(keep, "[]", head);
			Interrupt();
			throw new GroupLeft("what review.sh record left ...");
		}, guard: HeadGuard(), worktree: worktree, err: err));
		var said = err.ToString();
		Assert.True(Count(said, "rollout.sh: what review.sh record left ...\n") == 1, "said once, not again as a failed record's");
		Assert.False(said.Contains("broke a rule", StringComparison.Ordinal), "a group the record left isn't a rule the session broke");
		Assert.DoesNotContain("broke a rule", File.ReadAllText(LogIn(tmp)), StringComparison.Ordinal);
		Assert.Contains($"whether the review was recorded can't be told: cd '{worktree}' && scripts/review.sh report shows it;"
				+ " if it wasn't, record it with: ", said, StringComparison.Ordinal);
	}

	[Fact]
	public void A_signal_after_a_prepare_that_left_its_group_names_the_group()
	{
		NeedsHeld();
		var err = new StringWriter();
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, record: (_, _, _, head, keep, _, _) =>
		{
			ReviewRecord.WriteStaged(keep, "[]", head);
			Interrupt();
			return (ReviewRecord.RecordFailed, ["what review.sh prepare left ...: see it with pgrep -l -g 7"]);
		}, guard: HeadGuard(), err: err));
		var said = err.ToString();
		var group = said.IndexOf("rollout.sh: what review.sh prepare left ...: see it with pgrep -l -g 7\n", StringComparison.Ordinal);
		Assert.True(group >= 0 && group < said.IndexOf("what the session staged is kept in", StringComparison.Ordinal),
				"the group, which holds the lock, comes first");
		Assert.False(said.Contains("broke a rule", StringComparison.Ordinal), "a group prepare left isn't a rule the session broke");
		Assert.DoesNotContain("broke a rule", File.ReadAllText(LogIn(tmp)), StringComparison.Ordinal);
	}

	[Fact]
	public void A_signal_with_a_broken_rule_says_the_rule_once_and_no_record_command()
	{
		NeedsHeld();
		var err = new StringWriter();
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, record: (_, _, _, head, keep, _, _) =>
		{
			ReviewRecord.WriteStaged(keep, "[]", head); // kept, so only the broken rule keeps the command off the terminal
			Interrupt();
			return (ReviewRecord.SessionBrokeARule, ["R2 was staged fixed"]);
		}, guard: HeadGuard(), err: err));
		var said = err.ToString();
		Assert.Equal(1, Count(said, "R2 was staged fixed"));
		Assert.Contains("rollout.sh: the session broke a rule: R2 was staged fixed\n", said, StringComparison.Ordinal);
		Assert.DoesNotContain("record it with", said, StringComparison.Ordinal);
	}

	[Fact]
	public void A_signal_with_another_reviews_findings_kept_prints_no_command()
	{
		NeedsHeld();
		var err = new StringWriter();
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, record: (_, _, _, _, keep, _, _) =>
		{
			ReviewRecord.WriteStaged(keep, "[]", new string('f', 40));
			Interrupt();
			return (ReviewRecord.RecordFailed, []);
		}, guard: HeadGuard(), err: err));
		Assert.DoesNotContain("is kept in", err.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void A_signal_with_nothing_kept_prints_no_command()
	{
		NeedsHeld();
		var err = new StringWriter();
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, record: (_, _, _, _, _, _, _) =>
		{
			Interrupt();
			return (0, []);
		}, err: err));
		Assert.DoesNotContain("is kept in", err.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void The_record_start_is_said_on_the_terminal()
	{
		var (_, _, seen, _) = ReviewFixtures.ReviewOnce(tmp);
		Assert.Equal("rollout.sh: recording the review (scripts/review.sh prepare, then record, up to"
				+ " 10 minutes each: an interrupt stops prepare, and waits for record to end)\n", seen.Err);
	}

	[Fact]
	public void A_group_the_record_left_is_its_own_stop_since_it_may_have_recorded()
	{
		NeedsHeld();
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, record: (_, _, _, _, _, _, _) => throw new GroupLeft("what review.sh record left ..."));
		Assert.Equal(ReviewSession.RecordLeft, code);
		Assert.Equal(["what review.sh record left ..."], seen.Broke);
		var text = File.ReadAllText(log);
		Assert.Contains("rollout.sh: what review.sh record left ...\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("broke a rule", text, StringComparison.Ordinal);
		Assert.False(seen.Err.Contains("what review.sh record left", StringComparison.Ordinal), "no signal: the stop says it");

		var again = P("again");
		Directory.CreateDirectory(again);
		var err = new StringWriter();
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(again, record: (_, _, _, _, _, _, _) =>
		{
			Interrupt();
			throw new GroupLeft("what review.sh record left ...");
		}, err: err));
		var said = err.ToString();
		Assert.True(said.Contains("rollout.sh: what review.sh record left ...\n", StringComparison.Ordinal),
				"the signal wins, and the person still hears of the group");
		Assert.False(said.Contains("broke a rule", StringComparison.Ordinal), "a group the record left isn't a rule the session broke");
	}

	// Python's traceback is .NET's stack trace here: the exception's ToString, its "   at " lines.
	[Fact]
	public void A_bug_while_recording_is_record_failed_with_its_traceback_and_the_signal_still_passed_on()
	{
		NeedsHeld();
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, record: (_, _, _, _, _, _, _) => throw new ValueError("a bug"));
		Assert.Equal(ReviewRecord.RecordFailed, code);
		Assert.Empty(seen.Broke);
		var text = File.ReadAllText(log);
		Assert.Contains("rollout.sh: recording the review failed:\n", text, StringComparison.Ordinal);
		Assert.Contains("ValueError: a bug\n   at ", text, StringComparison.Ordinal);

		var again = P("again");
		Directory.CreateDirectory(again);
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(again, record: (_, _, _, _, _, _, _) =>
		{
			Interrupt();
			throw new ValueError("a bug");
		}));
	}

	[Fact]
	public void A_check_that_fails_is_a_broken_rule_not_a_crash()
	{
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, check: _ => throw new ValueError("a bug"));
		Assert.Equal(ReviewRecord.SessionBrokeARule, code);
		Assert.Null(seen.Recorded);
		Assert.Equal(["the checks after the session failed (ValueError: a bug; the traceback is in the log), so what it did can't be told"], seen.Broke);
		Assert.Contains("ValueError: a bug\n   at ", File.ReadAllText(log), StringComparison.Ordinal); // the traceback
	}

	[Fact]
	public void The_record_runs_with_the_signals_held_and_passes_one_on_after()
	{
		NeedsHeld();
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, record: (_, _, log, _, _, _, _) =>
		{
			Interrupt();
			log.Write(Encoding.UTF8.GetBytes("recorded whole\n"));
			log.Flush();
			return (0, []);
		}));
		Assert.Contains("recorded whole", File.ReadAllText(LogIn(tmp)), StringComparison.Ordinal);
	}

	[Fact]
	public void The_sessions_folders_and_settings_are_gone_after_it()
	{
		var (_, _, seen, _) = ReviewFixtures.ReviewOnce(tmp);
		Assert.False(AutoRollout.LExists(seen.How!.Env["GH_CONFIG_DIR"]));
		Assert.False(AutoRollout.LExists(seen.How.Env["TMPDIR"]));
	}

	// The original counted tool_reads' calls through a monkeypatch; the C# has no seam around ToolReads, so this counts which's asks for
	// node, which each ToolReads makes once.
	[Fact]
	public void A_review_runs_its_tools_from_where_they_really_are_and_asks_them_once()
	{
		var home = Environment.GetEnvironmentVariable("HOME");
		Environment.SetEnvironmentVariable("HOME", P("me"));
		try
		{
			var asked = 0;
			var node = P("me", ".nvm", "versions", "node", "v24", "bin", "node");
			var (_, _, seen, _) = ReviewFixtures.ReviewOnce(tmp, which: name =>
			{
				if (name != "node")
					return null;
				asked++;
				return node;
			});
			var path = seen.How!.Env["PATH"];
			Assert.Equal(Path.GetDirectoryName(node), path.Split(':')[0]);
			Assert.EndsWith(Environment.GetEnvironmentVariable("PATH")!, path, StringComparison.Ordinal);
			Assert.True(asked == 1, "the settings, the PATH and the check after the session share one answer");
		}
		finally
		{
			Environment.SetEnvironmentVariable("HOME", home);
		}
	}

	[Fact]
	public void Large_settings_reach_the_session_whole_through_their_file()
	{
		var names = Enumerable.Range(0, 5000).Select(n => $"an-entry-of-the-persons-own-{n:D5}").ToList();
		var root = PyLib.RealPath(tmp).Split('/')[1];
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, listdir: folder => folder == "/" ? [.. names, root] : []);
		Assert.True(code == 0, File.ReadAllText(log));
		Assert.True(seen.Settings!.ToJsonString().Length > 128 * 1024, "no command-line argument could carry them");
		Assert.Contains($"/{names[^1]}", seen.Settings["sandbox"]!["filesystem"]!["denyRead"]!.AsArray().Select(node => (string?)node));
	}

	[Fact]
	public void A_session_folder_not_of_the_users_own_stops_before_the_session()
	{
		Directory.CreateDirectory(P("elsewhere"));
		Directory.CreateSymbolicLink(P("sessions"), P("elsewhere"));
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp);
		Assert.Equal(127, code);
		Assert.Null(seen.Command);
		var text = File.ReadAllText(log);
		Assert.Contains("rollout.sh: not started: can't set the session up:", text, StringComparison.Ordinal);
		Assert.Contains("isn't a folder of the user's own", text, StringComparison.Ordinal);
		Assert.Contains("a real folder of yours, not a symlink; remove or fix it, or run /pre-push-review in", text, StringComparison.Ordinal);
	}

	// The original monkeypatched tempfile.TemporaryDirectory; here RunReview's makeTempFolder, which stands for it, raises the same OSError.
	[Fact]
	public void A_session_folder_that_cant_be_made_stops_before_the_session()
	{
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, makeTempFolder: (_, _) => throw new IOException("[Errno 28] No space left on device"));
		Assert.Equal(127, code);
		Assert.Null(seen.Command);
		Assert.Contains("rollout.sh: not started: can't set the session up: [Errno 28] No space left on device", File.ReadAllText(log),
				StringComparison.Ordinal);
	}

	[Fact]
	public void What_killed_runs_left_in_the_session_folder_is_removed()
	{
		var sessions = P("sessions");
		Directory.CreateDirectory(Path.Combine(sessions, "rollout-session.old", "run"));
		File.WriteAllText(Path.Combine(sessions, "rollout-session.old", "run", "findings.json"), "[]");
		Directory.CreateDirectory(P("elsewhere"));
		File.WriteAllText(P("elsewhere", "keep.txt"), "x");
		Directory.CreateSymbolicLink(Path.Combine(sessions, "rollout-session.link"), P("elsewhere"));
		Directory.CreateDirectory(Path.Combine(sessions, "other"));
		var (code, _, seen, _) = ReviewFixtures.ReviewOnce(tmp);
		Assert.Equal(0, code);
		Assert.False(AutoRollout.LExists(Path.Combine(sessions, "rollout-session.old")));
		Assert.True(PyLib.IsLink(Path.Combine(sessions, "rollout-session.link")) && File.Exists(P("elsewhere", "keep.txt")),
				"a symlink is left, and where it leads untouched");
		Assert.True(Directory.Exists(Path.Combine(sessions, "other")), "only rollout-session.* folders");
		Assert.False(AutoRollout.LExists(seen.Tmp!), "and the session's own is gone once it ends");
	}

	// The original checked that TemporaryDirectory was asked to ignore cleanup errors; here the folder is made through the makeTempFolder
	// seam (asked once, in the session folder) and its removal really fails: the record leaves a file in a folder it can't be removed from.
	[Fact]
	public void The_session_folder_is_left_whole_when_it_cant_be_removed()
	{
		List<(string Folder, string Prefix)> made = [];
		var (code, _, _, _) = ReviewFixtures.ReviewOnce(tmp, makeTempFolder: (folder, prefix) =>
		{
			made.Add((folder, prefix));
			return Files.MakeTempFolder(folder, prefix);
		}, record: (_, run, _, _, _, _, _) =>
		{
			var locked = Path.Combine(run, "locked");
			Directory.CreateDirectory(locked);
			File.WriteAllText(Path.Combine(locked, "findings.json"), "[]");
			File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			return (0, []);
		});
		Assert.True(code == 0, "a failed cleanup can't replace the round's result");
		Assert.Equal([(P("sessions"), "rollout-session.")], made);
	}
}
