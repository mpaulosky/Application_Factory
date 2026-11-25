using System.Runtime.InteropServices;

// Tests for github/AutoRolloutGroups.cs and the review session's process group in AutoRolloutReview.cs, xUnit copies of
// github/tests/test_auto_rollout.py's tests of a session past its time, interrupted or signalled, its group's end, deferred_signals,
// round_signals, run_in_group, child_env, run_land and run_reapply, and wait_for (ADR 0017's Update for mpaulosky/Application_Factory#122): one
// method each, the same cases and assertions. Python's KeyboardInterrupt and SystemExit(128 + n) are both the Interrupted exception here, and a
// session's subprocess.TimeoutExpired an Exit that gives null (FakeSession's timesOut). Each signal the tests send to the test process is sent
// only while a hold for it is registered (a DeferredSignals, a RoundSignals or a Handling of the test's own), since the runtime's own handling
// of a SIGTERM or SIGHUP would end the test host. None of them sends a SIGINT: .NET runs every registration for a signal, the test runner's
// Console.CancelKeyPress among them, which cancels the run, so where the pytest sent one a SIGHUP stands in (held the same way).
[Collection(nameof(RoundCollection))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class SessionGroupTests : IDisposable
{
	readonly string tmp;

	// Skips off Linux before anything is made, as SessionGuardTests does.
	public SessionGroupTests()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		tmp = Directory.CreateTempSubdirectory("session-group.").FullName;
	}

	public void Dispose()
	{
		AutoRollout.LockFds.Clear();
		Directory.Delete(tmp, recursive: true);
	}

	string P(params string[] parts) => Path.Combine([tmp, .. parts]);

	[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
	static extern int Kill(int pid, int sig);

	// sigaction with glibc's 64-bit struct sigaction as bytes (sa_handler first, then sa_mask, sa_flags and sa_restorer: 152 bytes), so a
	// disposition is read and put back whole, its flags with it, which signal() would drop.
	[DllImport("libc", EntryPoint = "sigaction", SetLastError = true)]
	static extern int SigAction(int signum, byte[]? act, byte[]? oldact);

	const int SigActionSize = 256;
	const nint SigIgn = 1;

	static nint Disposition(int signum)
	{
		var now = new byte[SigActionSize];
		Assert.Equal(0, SigAction(signum, null, now));
		return (nint)BitConverter.ToInt64(now, 0);
	}

	// The test process sends itself signum and waits for the handler, which runs on another thread. Call it only while a hold for signum is
	// registered.
	static void Send(int signum)
	{
		Assert.NotEqual(Signals.SigInt, signum); // the runner's Console.CancelKeyPress would cancel the run
		Assert.Equal(0, Kill(Environment.ProcessId, signum));
		Thread.Sleep(200);
	}

	// A signal the test host's caller ignores (nohup's SIGHUP, a background job's SIGINT) isn't held, so it can't be tested here.
	static void NeedsHeld(params int[] signums)
	{
		foreach (var signum in signums)
			Assert.SkipUnless(Signals.Held(signum), $"the test host ignores signal {signum}");
	}

	// wait_gone: whether pid ends (gone, or a zombie until its new parent reaps it) within five seconds.
	static bool WaitGone(int pid)
	{
		for (var tries = 0; tries < 100; tries++)
		{
			if (Kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == 3) // ESRCH
				return true;
			string state;
			try
			{
				var text = File.ReadAllText($"/proc/{pid}/stat");
				var fields = text[(text.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (fields.Length == 0)
					return true;
				state = fields[0];
			}
			catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
			{
				return true;
			}
			if (state == "Z")
				return true;
			Thread.Sleep(50);
		}
		return false;
	}

	// read_pid: the pid written to path, once it is.
	static int ReadPid(string path)
	{
		for (var tries = 0; tries < 200; tries++)
		{
			if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } text)
				return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
			Thread.Sleep(25);
		}
		throw new InvalidOperationException($"{path} was never written");
	}

	// real_session: RunReview of a real bash, started by Groups.Spawn in a session and process group of its own with what the round gives it,
	// in place of claude, with the rest faked as review_once fakes it: (exit code, log, the pid script wrote to {pid_file}). With
	// signalAfterStart, the test process sends itself that signal once the script has written its pid, inside the session's hold.
	(int Code, string Log, int Child) RealSession(string script, double timeout, int? signalAfterStart = null)
	{
		var pidFile = P("child.pid");
		var worktree = P("w");
		Directory.CreateDirectory(worktree);

		IGroupChild Popen(IReadOnlyList<string> command, Spawning how)
		{
			var child = Groups.Spawn(["bash", "-c", script.Replace("{pid_file}", pidFile, StringComparison.Ordinal)], how);
			if (signalAfterStart is int signum)
			{
				ReadPid(pidFile);
				Send(signum);
			}
			return child;
		}

		var (code, log, _) = ReviewSession.RunReview(Fleets.TM, worktree, Popen, _ => [], timeout, P("logs"), P("sessions"),
				guard: _ => ReviewFixtures.FakeGuard(tmp), check: _ => [], skill: () => ReviewFixtures.SkillText, agents: ReviewFixtures.Agents,
				record: (_, _, _, _, _, _, _) => (0, []), listdir: _ => [], which: _ => null, secrets: (_, _) => [], err: new StringWriter());
		return (code, log, ReadPid(pidFile));
	}

	// A session whose Exit runs act first, then gives null (its wait ran out), as the pytest's Signalled and _Raising wait.
	sealed class Acting(Action act, int pid = 999999) : IGroupChild
	{
		public int Pid => pid;

		public int? Exit(double seconds)
		{
			act();
			return null;
		}
	}

	[Fact]
	public void A_review_session_past_its_time_is_stopped_and_noted()
	{
		var session = new FakeSession(timesOut: true);
		var now = 0.0;

		double Clock()
		{
			now += 60; // a minute a look
			return now;
		}

		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, session: session, clock: Clock);
		Assert.Equal(124, code);
		const int Limit = ReviewSession.ReviewTimeoutMinutes * 60;
		Assert.True(now >= Limit && Limit > now - 3 * 60, "it polls until its time is spent, and stops");
		Assert.True(session.Waits.Count > 1 && session.Waits.Max() <= 0.5);
		Assert.Equal([session], seen.Ended);
		Assert.Contains($"ran past {ReviewSession.ReviewTimeoutMinutes} minutes", File.ReadAllText(log), StringComparison.Ordinal);
	}

	[Fact]
	public void A_broken_rule_outranks_a_timeout()
	{
		var now = 0.0;
		var (code, log, _, _) = ReviewFixtures.ReviewOnce(tmp, session: new FakeSession(timesOut: true), clock: () => now += 3600, broke: ["x"]);
		Assert.Equal(ReviewRecord.SessionBrokeARule, code);
		var text = File.ReadAllText(log);
		Assert.Contains("ran past", text, StringComparison.Ordinal);
		Assert.Contains("the session broke a rule: x", text, StringComparison.Ordinal);
	}

	[Fact]
	public void An_interrupted_review_session_ends_its_group_and_passes_the_interrupt_on()
	{
		var session = new FakeSession(raises: new Interrupted(Signals.SigInt));
		List<IGroupChild> ended = [];
		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, session: session, end: child =>
		{
			ended.Add(child);
			return true;
		}));
		Assert.Equal([session], ended);
	}

	[Fact]
	public void A_group_that_does_not_go_away_is_a_broken_rule()
	{
		// What is left of it can still write after the check read the ledgers.
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, end: _ => false);
		Assert.Equal(ReviewRecord.SessionBrokeARule, code);
		var rule = $"the session's process group was still there {Groups.GroupWaitSeconds} s after it was killed, and can still"
				+ " write: see it with pgrep -l -g 999999, and end it with pkill -KILL -g 999999, before you resume";
		Assert.Equal([rule], seen.Broke);
		Assert.Contains($"rollout.sh: the session broke a rule: {rule}", File.ReadAllText(log), StringComparison.Ordinal);
	}

	[Fact]
	public void A_timed_out_session_takes_what_it_started_with_it()
	{
		var (code, _, child) = RealSession("sleep 60 & echo $! > {pid_file}; wait", timeout: 2);
		Assert.Equal(124, code);
		Assert.True(WaitGone(child));
	}

	[Fact]
	public void A_session_that_exits_takes_what_it_left_running_with_it()
	{
		var (code, log, child) = RealSession("sleep 60 & echo $! > {pid_file}; echo the session said this; exit 0", timeout: 60);
		Assert.Equal(0, code);
		Assert.True(WaitGone(child));
		Assert.Contains("the session said this", File.ReadAllText(log), StringComparison.Ordinal);
	}

	[Fact]
	public void A_sigterm_during_a_session_ends_its_group_and_becomes_an_exit()
	{
		// The handler before it is a Handling of the test's own, and "put back" is that it gets the next SIGTERM (C# has no getsignal).
		NeedsHeld(Signals.SigTerm);
		List<int> previous = [];
		using (Signals.Handling([Signals.SigTerm], previous.Add))
		{
			var stop = Assert.Throws<Interrupted>(() =>
					RealSession("sleep 60 & echo $! > {pid_file}; wait", timeout: 60, signalAfterStart: Signals.SigTerm));
			Assert.Equal(128 + Signals.SigTerm, stop.Code);
			Assert.True(WaitGone(ReadPid(P("child.pid"))));
			Assert.Empty(previous);
			Send(Signals.SigTerm);
			Assert.True(previous.SequenceEqual([Signals.SigTerm]), "the handler is put back");
		}
	}

	[Fact]
	public void A_signal_while_the_session_starts_still_ends_its_group()
	{
		NeedsHeld(Signals.SigHup);
		var session = new FakeSession(code: 0);

		IGroupChild Popen(IReadOnlyList<string> command, Spawning how)
		{
			Send(Signals.SigHup); // lands between starting the session and holding it
			return session;
		}

		List<IGroupChild> ended = [];
		var stop = Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, popen: Popen, end: child =>
		{
			ended.Add(child);
			return true;
		}));
		Assert.Equal([session], ended);
		Assert.Equal(128 + Signals.SigHup, stop.Code);
	}

	[Fact]
	public void A_signal_while_claude_fails_to_start_is_still_passed_on()
	{
		NeedsHeld(Signals.SigTerm);

		IGroupChild Popen(IReadOnlyList<string> command, Spawning how)
		{
			Send(Signals.SigTerm);
			throw new SpawnError(2, "claude");
		}

		var stop = Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, popen: Popen));
		Assert.Equal(128 + Signals.SigTerm, stop.Code);
	}

	[Fact]
	public void A_second_signal_during_the_cleanup_neither_stops_it_nor_replaces_the_first()
	{
		// A SIGHUP stands in for the pytest's first signal, its SIGINT.
		NeedsHeld(Signals.SigHup, Signals.SigTerm);
		List<IGroupChild> killed = [];

		bool End(IGroupChild child)
		{
			Send(Signals.SigTerm);
			killed.Add(child); // the kill goes on past the second signal
			return true;
		}

		var session = new FakeSession(code: 0);

		IGroupChild Popen(IReadOnlyList<string> command, Spawning how)
		{
			Send(Signals.SigHup);
			return session;
		}

		var stop = Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, popen: Popen, end: End));
		Assert.Equal(Signals.SigHup, stop.Signum); // the first signal, the pytest's KeyboardInterrupt
		Assert.Equal([session], killed);
	}

	[Fact]
	public void A_signal_during_a_session_is_not_called_a_timeout()
	{
		NeedsHeld(Signals.SigTerm);
		var session = new FakeSession(timesOut: true);

		IGroupChild Popen(IReadOnlyList<string> command, Spawning how)
		{
			Send(Signals.SigTerm);
			return session;
		}

		Assert.Throws<Interrupted>(() => ReviewFixtures.ReviewOnce(tmp, popen: Popen));
		var log = Assert.Single(Directory.GetFileSystemEntries(P("logs")));
		Assert.DoesNotContain("ran past", File.ReadAllText(log), StringComparison.Ordinal);
	}

	// raised is the pytest's exception: both are Interrupted here, the exit its Code. The SIGINT case can't send its SIGINT (the runner would
	// cancel the run): it checks what passes it on and that it is held, and is reported skipped.
	[Theory]
	[InlineData(Signals.SigInt, "KeyboardInterrupt")]
	[InlineData(Signals.SigTerm, "SystemExit")]
	[InlineData(Signals.SigHup, "SystemExit")]
	public void Deferred_signals_hold_each_signal_and_restore_the_handlers(int signum, string raised)
	{
		var passed = Signals.PassedOn(signum);
		Assert.Equal(signum, passed.Signum);
		if (raised == "SystemExit")
			Assert.Equal(128 + signum, passed.Code);
		if (signum == Signals.SigInt)
		{
			Assert.SkipUnless(Signals.Held(signum), "the test host ignores SIGINT");
			Assert.Skip("a SIGINT sent to the test host reaches the runner's Console.CancelKeyPress, which cancels the run");
		}
		var other = signum != Signals.SigTerm ? Signals.SigTerm : Signals.SigHup;
		NeedsHeld(signum, other);
		// The handler before it is a Handling of the test's own, and "restored" is that it gets the next signum (C# has no getsignal).
		List<int> before = [];
		using (Signals.Handling([signum], before.Add))
		{
			List<int> got;
			using (var hold = Signals.DeferredSignals())
			{
				got = hold.Got;
				Send(signum);
				Send(other);
			}
			Assert.True(got.SequenceEqual([signum]), "the first signal is the one held");
			Assert.Empty(before);
			Send(signum);
			Assert.Equal([signum], before);
		}
	}

	// An ignored SIGHUP is set through libc's sigaction for the test, its whole disposition put back after.
	[Theory]
	[InlineData("deferred_signals")]
	[InlineData("round_signals")]
	public void An_ignored_signal_stays_ignored(string hold)
	{
		var previous = new byte[SigActionSize];
		var ignore = new byte[SigActionSize];
		BitConverter.TryWriteBytes(ignore.AsSpan(0, 8), (long)SigIgn); // nohup's
		Assert.Equal(0, SigAction(Signals.SigHup, ignore, previous));
		try
		{
			List<int> round = [];
			using (hold == "deferred_signals" ? Signals.DeferredSignals() : Signals.RoundSignals(round.Add))
			{
				Assert.Equal(SigIgn, Disposition(Signals.SigHup));
				Assert.False(Signals.Held(Signals.SigHup));
				Send(Signals.SigHup); // ignored, so safe with nothing registered for it
			}
			Assert.Equal(SigIgn, Disposition(Signals.SigHup));
			Assert.Empty(round);
		}
		finally
		{
			Assert.Equal(0, SigAction(Signals.SigHup, previous, null));
		}
	}

	// The round's onSignal (AutoRound.Run's) is what exits with 128 + the signal; here it records the signal, and the code is PassedOn's.
	[Theory]
	[InlineData(Signals.SigTerm)]
	[InlineData(Signals.SigHup)]
	public void Round_signals_turn_sigterm_and_sighup_into_an_exit(int signum)
	{
		NeedsHeld(signum);
		List<int> before = [];
		using (Signals.Handling([signum], before.Add))
		{
			List<int> stopped = [];
			using (Signals.RoundSignals(stopped.Add))
				Send(signum);
			Assert.Equal([signum], stopped);
			Assert.Equal(128 + signum, Signals.PassedOn(stopped[0]).Code);
			Assert.Empty(before);
			Send(signum);
			Assert.Equal([signum], before); // the handler before it is back
		}
	}

	[Fact]
	public void Run_in_group_starts_its_own_group_and_ends_it_on_an_interrupt()
	{
		// Groups.Spawn always starts its own session (POSIX_SPAWN_SETSID), so Spawning has no start_new_session to compare.
		AutoRollout.LockFds.Clear();
		IReadOnlyList<string>? command = null;
		Spawning? how = null;
		List<IGroupChild> ended = [];

		bool End(IGroupChild child)
		{
			ended.Add(child);
			return true;
		}

		var environ = AutoRollout.Environ().ToDictionary(StringComparer.Ordinal);
		Assert.Equal(3, Groups.RunInGroup(["x"], (c, h) =>
		{
			(command, how) = (c, h);
			return new FakeSession(code: 3);
		}, End));
		Assert.Equal(["x"], command!);
		Assert.Empty(how!.PassFds);
		foreach (var (name, value) in AutoRollout.NoBuildServers)
			environ[name] = value;
		Assert.True(environ.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(how.Env.OrderBy(pair => pair.Key, StringComparer.Ordinal)),
				"no build server is left to outlive it");
		Assert.True(ended.Count == 1, "its group is ended when it exits, too");
		Assert.Throws<Interrupted>(() => Groups.RunInGroup(["x"], (_, _) => new FakeSession(raises: new Interrupted(Signals.SigTerm)), End));
		Assert.Equal(2, ended.Count);
	}

	[Fact]
	public void Run_in_group_holds_a_second_signal_and_passes_the_first_on()
	{
		// A SIGHUP stands in for the pytest's second signal, its SIGINT.
		NeedsHeld(Signals.SigTerm, Signals.SigHup);
		List<IGroupChild> ended = [];
		var signalled = new Acting(() =>
		{
			Send(Signals.SigTerm);
			Send(Signals.SigHup);
		});
		var stop = Assert.Throws<Interrupted>(() => Groups.RunInGroup(["x"], (_, _) => signalled, child =>
		{
			ended.Add(child);
			return true;
		}));
		Assert.Equal(128 + Signals.SigTerm, stop.Code);
		Assert.Single(ended);
	}

	[Fact]
	public void Run_in_group_that_cant_start_passes_a_signal_on_before_its_error()
	{
		NeedsHeld(Signals.SigHup);
		var err = new StringWriter();
		var stop = Assert.Throws<Interrupted>(() => Groups.RunInGroup(["x"], (_, _) =>
		{
			Send(Signals.SigHup);
			throw new SpawnError(2, "x");
		}, err: err));
		Assert.Equal(128 + Signals.SigHup, stop.Code);
		Assert.True(err.ToString().Length == 0, "only a group left behind is written as the signal wins");
		var error = Assert.Throws<SpawnError>(() => Groups.RunInGroup(["/nonexistent/x"]));
		Assert.Equal("FileNotFoundError", error.PyName);
	}

	[Fact]
	public void Run_in_group_passes_the_lock_on_to_the_command()
	{
		Spawning? seen = null;
		AutoRollout.LockFds.Clear();
		AutoRollout.LockFds.Add(7);
		Groups.RunInGroup(["x"], (_, how) =>
		{
			seen = how;
			return new FakeSession();
		});
		Assert.Equal([7], seen!.PassFds);
	}

	[Fact]
	public void What_a_command_leaves_in_its_group_is_ended_and_lets_go_of_the_lock()
	{
		// A build server the gate leaves behind would hold the lock it was passed, and the resume would be refused.
		var lockPath = P("lock");
		var handle = AutoRollout.TakeLock(lockPath)!;
		AutoRollout.LockFds.Clear();
		AutoRollout.LockFds.Add(AutoRollout.Descriptor(handle));
		var pidFile = P("left.pid");
		Assert.Equal(0, Groups.RunInGroup(["bash", "-c", $"sleep 60 & echo $! > {pidFile}"]));
		AutoRollout.LockFds.Clear();
		handle.Dispose();
		Assert.True(WaitGone(ReadPid(pidFile)));
		using var again = AutoRollout.TakeLock(lockPath);
		Assert.True(again is not null, "nothing the command left holds the lock");
	}

	[Fact]
	public void Child_env_adds_the_build_server_switches_to_the_environment()
	{
		Dictionary<string, string> expected = new(StringComparer.Ordinal) { ["PATH"] = "/bin" };
		foreach (var (name, value) in AutoRollout.NoBuildServers)
			expected[name] = value;
		var env = Groups.ChildEnv(new Dictionary<string, string> { ["PATH"] = "/bin", ["UseSharedCompilation"] = "true" });
		Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), env.OrderBy(pair => pair.Key, StringComparer.Ordinal));
	}

	[Fact]
	public void A_sigterm_of_the_round_ends_reapply_and_what_it_started()
	{
		NeedsHeld(Signals.SigTerm);
		var pidFile = P("child.pid");

		IGroupChild Popen(IReadOnlyList<string> command, Spawning how)
		{
			var child = Groups.Spawn(["bash", "-c", $"sleep 60 & echo $! > {pidFile}; wait"], how);
			ReadPid(pidFile);
			Send(Signals.SigTerm);
			return child;
		}

		List<int> round = [];
		Interrupted stop;
		using (Signals.RoundSignals(round.Add))
			stop = Assert.Throws<Interrupted>(() => Groups.RunInGroup(["reapply.sh"], Popen));
		Assert.Equal(143, stop.Code);
		Assert.True(WaitGone(ReadPid(pidFile)));
	}

	// A skill folder whose land.sh and reapply.sh write their arguments, then whether they lead a process group of their own, to <name>.args,
	// and exit 5.
	Folders FakeSkill()
	{
		var skill = P("skill");
		Directory.CreateDirectory(skill);
		foreach (var name in new[] { "land", "reapply" })
		{
			var script = Path.Combine(skill, $"{name}.sh");
			File.WriteAllText(script, "#!/bin/bash\nfields=($(sed 's/.*) //' /proc/$$/stat))\n"
					+ $"{{ printf '%s\\n' \"$0\" \"$@\"; [ \"${{fields[2]}}\" = $$ ] && echo own group; }} > {P($"{name}.args")}\nexit 5\n");
			File.SetUnixFileMode(script, (UnixFileMode)0x1ED); // 0755
		}
		return new Folders(skill);
	}

	string[] Args(string name) => File.ReadAllLines(P($"{name}.args"));

	[Fact]
	public void Run_land_and_run_reapply_run_in_groups_of_their_own()
	{
		// The pytest replaced run_in_group; here they run fake land.sh and reapply.sh in a real group, which say what they were given and
		// that they lead their own group.
		var folders = FakeSkill();
		Assert.Equal(5, Groups.RunLand([$"{Fleets.TM}#130", $"{Fleets.Rest[0]}#5"], folders));
		Assert.Equal(5, Groups.RunReapply("#82", "/r/TicketManager", folders));
		Assert.Equal([folders.Land, $"{Fleets.TM}#130", $"{Fleets.Rest[0]}#5", "own group"], Args("land"));
		Assert.Equal([folders.Reapply, "--brings", "#82", "/r/TicketManager", "own group"], Args("reapply"));
		// auto.REAPPLY == rollout.REAPPLY: both read Folders.Reapply.
		Assert.Equal(Path.Combine(folders.Skill, "reapply.sh"), folders.Reapply);
	}

	[Fact]
	public void Rollout_hands_auto_the_reapply_that_runs_in_a_group()
	{
		// The pytest replaced auto.run to see what rollout.main handed it; Rollout.Run hands --auto World.Real(folders) when no world is given,
		// which has no seam to stop it before its preflight, so this runs World.Real's Reapply, which must be RunReapply's group.
		var folders = FakeSkill();
		Assert.Equal(5, World.Real(folders).Reapply("#82", "/r/TicketManager"));
		Assert.Equal([folders.Reapply, "--brings", "#82", "/r/TicketManager", "own group"], Args("reapply"));
	}

	[Fact]
	public void Wait_for_stops_at_a_signal_or_the_deadline()
	{
		var session = new FakeSession(timesOut: true);
		Assert.Null(ReviewRecord.WaitFor(session, 3600, [Signals.SigTerm]));
		Assert.Single(session.Waits);
		var now = 0.0;
		session = new FakeSession(timesOut: true);
		Assert.Null(ReviewRecord.WaitFor(session, 2, [], () => now += 0.5));
		Assert.True(session.Waits.Count > 0 && session.Waits.Max() <= 0.5);
		Assert.Equal(7, ReviewRecord.WaitFor(new FakeSession(code: 7), 2, []));
	}

	[Fact]
	public void Wait_for_stops_at_the_deadline_itself()
	{
		// The clock reads exactly the deadline on the look after a poll: that is past its time.
		using var ticks = ((IEnumerable<double>)[0.0, 1.0, 2.0]).GetEnumerator();
		var session = new FakeSession(timesOut: true);
		Assert.Null(ReviewRecord.WaitFor(session, 2, [], () => ticks.MoveNext() ? ticks.Current : throw new InvalidOperationException("read past the ticks")));
	}

	[Fact]
	public void A_session_that_broke_a_rule_is_its_own_code_and_says_what()
	{
		var (code, log, _, _) = ReviewFixtures.ReviewOnce(tmp, broke: ["the remote chore/reapply-baseline moved"]);
		Assert.Equal(ReviewRecord.SessionBrokeARule, code);
		Assert.Contains("rollout.sh: the session broke a rule: the remote chore/reapply-baseline moved", File.ReadAllText(log), StringComparison.Ordinal);
	}
}
