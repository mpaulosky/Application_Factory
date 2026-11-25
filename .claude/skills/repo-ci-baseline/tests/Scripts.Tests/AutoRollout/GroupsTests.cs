using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Tests for github/AutoRolloutGroups.cs, xUnit copies of github/tests/test_auto_rollout.py's tests of the process groups the round runs its
// commands in (run_bounded and record_review with a real command, wait_for, exit_unreaped, run_in_group, group_hint, end_group and
// group_alive; ADR 0017's Update for mpaulosky/Application_Factory#122): one method each, the same cases and assertions. The two that run a
// real review script and a bounded command were left out of ReviewRecordTests.cs until the spawner (Groups.Spawn) existed. Python's Popen is
// Groups.Spawn and its SpawnedChild here, and a monkeypatch of os.killpg, which EndGroup and GroupAlive call through libc with no seam, is a
// real kill of a group that can't exist (a pid past Linux's pid_max) or of a real group of the test's own.
[Collection(nameof(RoundCollection))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class GroupsTests : IDisposable
{
	readonly string tmp;

	// A pid no process can have (past pid_max, at most 2^22): a kill of its group always finds nothing.
	const int NoSuchPid = 1 << 30;

	// Skips off Linux before anything is made, as SessionGuardTests does.
	public GroupsTests()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		tmp = Directory.CreateTempSubdirectory("auto-rollout.").FullName;
	}

	public void Dispose() => Directory.Delete(tmp, recursive: true);

	string P(params string[] parts) => Path.Combine([tmp, .. parts]);

	[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
	static extern int Kill(int pid, int sig);

	// A process's state, the field after its name in /proc/<pid>/stat, or null when it is gone.
	static string? State(int pid)
	{
		try
		{
			var text = File.ReadAllText($"/proc/{pid}/stat");
			return text[(text.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or IndexOutOfRangeException)
		{
			return null;
		}
	}

	// wait_gone: whether pid ends (gone, or a zombie until its new parent reaps it) within five seconds.
	static bool WaitGone(int pid)
	{
		for (var i = 0; i < 100; i++)
		{
			if (Kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == 3) // ESRCH
				return true;
			if (State(pid) is null or "Z")
				return true;
			Thread.Sleep(50);
		}
		return false;
	}

	// read_pid: the pid a command wrote to path, once it has.
	static int ReadPid(string path)
	{
		for (var i = 0; i < 200; i++)
		{
			if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } text)
				return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
			Thread.Sleep(25);
		}
		throw new InvalidOperationException($"{path} was never written");
	}

	// A command started as the round starts one, with the test process's environment and no lock.
	static IGroupChild Spawn(params string[] command) => Groups.Spawn(command, new Spawning(AutoRollout.Environ(), []));

	const string Head = "cccccccccccccccccccccccccccccccccccccccc";

	const string Stub = """
			#!/bin/sh
			case "$1" in
			prepare) echo "prepare's note" >&2; pwd > "$CALLS/prepare.pwd"; printf '%s\n%s\n' "$KEY" "$HEAD"
			  exit "${PREPARE_EXIT:-0}" ;;
			record)
			  cat > "$CALLS/input.json"
			  printf '%s\n' "$@" > "$CALLS/argv"
			  cp "$4/findings.json" "$CALLS/staged.json"
			  if [ -n "${LEAVE:-}" ]; then sleep 60 & echo $! > "$CALLS/left.pid"; fi
			  echo "Review recorded."
			  echo ".security: R1 is a new entry, 0001" >&2
			  exit "${RECORD_EXIT:-0}" ;;
			esac

			""";

	static readonly JsonObject Open = (JsonObject)JsonNode.Parse("""
			{"id": "R1", "lens": "Bar", "path": "a.py", "line": 1, "summary": "s", "severity": "critical", "security": false,
			 "status": "open", "test": null, "reason": null, "fix": "f"}
			""")!;

	[Fact]
	public void The_record_runs_a_real_review_script_in_a_group_of_its_own_and_logs_both_its_streams()
	{
		var (worktree, calls) = (P("w"), P("calls"));
		Directory.CreateDirectory(Path.Combine(worktree, "scripts"));
		Directory.CreateDirectory(Path.Combine(worktree, "node_modules", ".bin"));
		var tsx = Path.Combine(worktree, "node_modules", ".bin", "tsx");
		File.WriteAllText(tsx, "#!/bin/sh\n");
		File.SetUnixFileMode(tsx, (UnixFileMode)Convert.ToInt32("755", 8));
		Directory.CreateDirectory(calls);
		var script = Path.Combine(worktree, "scripts", "review.sh");
		File.WriteAllText(script, Stub);
		File.SetUnixFileMode(script, (UnixFileMode)Convert.ToInt32("755", 8));
		var run = P("run");
		Directory.CreateDirectory(run);
		File.WriteAllText(Path.Combine(run, ReviewRecord.StagedFile), new JsonArray(Open.DeepClone()).ToJsonString());
		// The pytest's monkeypatch.setenv: the spawner hands the process's environment to the script.
		var env = new Dictionary<string, string> { ["KEY"] = new string('k', 64), ["HEAD"] = Head, ["CALLS"] = calls, ["LEAVE"] = "1" };
		var before = env.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
		var log = P("review.log");
		(int Code, List<string> Broke) result;
		try
		{
			foreach (var (key, value) in env)
				Environment.SetEnvironmentVariable(key, value);
			// The log opened for appending, as the round's (Files.MakeTemp): .NET writes at its own position, which a child writing to the same
			// file doesn't move, where Python's open(log, "w") wrote at the shared one.
			using var out_ = Files.CreateNew(log, append: true);
			out_.Write("the session's output\n"u8);
			out_.Flush();
			result = ReviewRecord.RecordReview(worktree, run, out_, Head, P("log.staged"),
					(command, io) => ReviewRecord.RunBounded(command, io, Groups.SpawnBounded, child => Groups.EndGroup(child)));
		}
		finally
		{
			foreach (var (key, value) in before)
				Environment.SetEnvironmentVariable(key, value);
		}
		Assert.Equal(0, result.Code);
		Assert.Empty(result.Broke);
		var text = File.ReadAllText(log);
		int At(string part) => text.IndexOf(part, StringComparison.Ordinal);
		// The log keeps the order things happened in.
		Assert.True(0 <= At("the session's output") && At("the session's output") < At("prepare's note")
				&& At("prepare's note") < At("recording the 1 finding(s)") && At("recording the 1 finding(s)") < At("Review recorded."), text);
		// record's stderr is logged.
		Assert.Contains(".security: R1 is a new entry, 0001", text, StringComparison.Ordinal);
		Assert.Equal(Head, (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(calls, "input.json")))!["reviewedHead"]);
		Assert.True(JsonNode.DeepEquals(new JsonArray(Open.DeepClone()), JsonNode.Parse(File.ReadAllText(Path.Combine(calls, "staged.json")))));
		Assert.True(WaitGone(ReadPid(Path.Combine(calls, "left.pid"))), "what record left in its group is killed with it");
		Assert.Equal(worktree, File.ReadAllText(Path.Combine(calls, "prepare.pwd")).Trim()); // prepare runs in the worktree
	}

	[Fact]
	public void A_bounded_command_past_its_time_is_stopped_with_its_group()
	{
		var pidFile = P("pid");
		using var devNull = new FileStream("/dev/null", FileMode.Open, FileAccess.Write);
		// Stdin null is /dev/null, and Stderr null its stdout's, so all three are /dev/null as the pytest's DEVNULLs.
		var code = ReviewRecord.RunBounded(["sh", "-c", $"sleep 60 & echo $! > {pidFile}; wait", "x"], new Bounded(tmp, null, devNull, null, null),
				Groups.SpawnBounded, child => Groups.EndGroup(child), timeout: 1);
		Assert.Null(code);
		Assert.True(WaitGone(ReadPid(pidFile)));
	}

	[Fact]
	public void A_leader_that_exited_stays_unreaped_until_its_group_is_ended()
	{
		// Its pid is the group's id: reaped before the group is signalled, it could be another process's by then.
		var session = (SpawnedChild)Spawn("sh", "-c", "exit 3");
		Assert.Equal(3, ReviewRecord.WaitFor(session, 10, []));
		Assert.Equal("Z", State(session.Pid)); // still a zombie
																					 // Python's returncode is None: unreaped, its exit is read again with WNOWAIT.
		Assert.Equal(3, Groups.WaitId(session.Pid, reap: false));
		Assert.True(Groups.EndGroup(session));
		Assert.Equal(3, session.Exit(0));
		// os.waitid's ChildProcessError: no child of that pid is left to wait for.
		Assert.Equal(-1, Groups.WaitId(session.Pid, reap: false));
	}

	[Fact]
	public void Exit_unreaped_gives_a_signal_as_popen_does_and_times_out_as_it_does()
	{
		var killed = Spawn("sh", "-c", "kill -9 $$");
		Assert.Equal(-Signals.SigKill, killed.Exit(10));
		Assert.True(killed.Reap(10));
		var running = Spawn("sleep", "30");
		try
		{
			// subprocess.TimeoutExpired is an Exit of null.
			Assert.Null(running.Exit(0.1));
		}
		finally
		{
			Assert.True(Groups.EndGroup(running));
		}
		// Not this process's child: waited as Popen waits (IGroupChild's Reap is its Exit).
		var fake = new FakeSession(code: 5);
		Assert.Equal(5, fake.Exit(1));
		Assert.True(((IGroupChild)fake).Reap(1));
		Assert.Equal([1.0, 1.0], fake.Waits);
	}

	[Theory]
	[InlineData("waitid")]
	[InlineData("WNOWAIT")]
	public void Without_waitid_a_session_is_waited_as_popen_waits(string missing)
	{
		// The C# has no Python without waitid or WNOWAIT (--auto is Linux only): its counterpart of a session waited as Popen waits is a
		// SpawnedChild once reaped, which answers Exit from what Reap read, waitid no longer asked.
		var session = Spawn("sh", "-c", "exit 5");
		Assert.Equal(5, ReviewRecord.WaitFor(session, 10, []));
		Assert.True(session.Reap(10), missing);
		Assert.Equal(-1, Groups.WaitId(session.Pid, reap: false)); // waitid has nothing left to answer
		Assert.Equal(5, session.Exit(1));
		Assert.True(Groups.EndGroup(session), missing);
		// And a child that isn't left unreaped is waited by its Exit.
		var fake = new FakeSession(code: 5);
		Assert.Equal(5, fake.Exit(1));
		Assert.Equal([1.0], fake.Waits);
		Assert.Equal(6, ReviewRecord.WaitFor(new FakeSession(code: 6), 10, []));
		Assert.Null(new FakeSession(timesOut: true).Exit(1));
	}

	[Fact]
	public void A_group_a_command_left_that_wont_go_is_a_stop_after_its_code()
	{
		var err = new StringWriter();
		var left = Assert.Throws<GroupLeft>(() => Groups.RunInGroup(["/bin/true"], end: _ => false, err: err));
		Assert.Matches(new Regex(@"^what true left in its process group was still there 10 s after it was killed, and holds the round's lock and may"
				+ @" still work in its worktree: see it with pgrep -l -g (\d+), and end it with pkill -KILL -g \1, before you resume\z"), left.Message);
		Assert.Equal("", err.ToString());
	}

	[Fact]
	public void A_signal_while_a_command_runs_still_says_what_it_left_in_its_group()
	{
		IGroupChild Popen(IReadOnlyList<string> command, Spawning how)
		{
			// RunInGroup holds the signals while it starts the command, so the signal is only recorded; its handler runs on another thread.
			SignalSender.Send(Signals.SigHup);
			return new FakeSession(code: 0, pid: NoSuchPid);
		}
		// A SIGHUP stands in for the pytest's SIGINT: .NET runs every registration for a signal, the test runner's Console.CancelKeyPress
		// among them, which would cancel the run.
		Assert.SkipUnless(Signals.Held(Signals.SigHup), "the test host ignores SIGHUP");
		var err = new StringWriter();
		var stopped = Assert.Throws<Interrupted>(() => Groups.RunInGroup(["/x/land.sh"], Popen, _ => false, err: err));
		Assert.Equal(Signals.SigHup, stopped.Signum); // the pytest's KeyboardInterrupt
		Assert.StartsWith("rollout.sh: what land.sh left in its process group was still there", err.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void A_group_that_went_away_says_nothing()
	{
		var err = new StringWriter();
		Assert.Equal(0, Groups.RunInGroup(["/bin/true"], end: _ => true, err: err));
		Assert.Equal("", err.ToString());
	}

	[Fact]
	public void The_group_hint_uses_commands_procps_and_the_bsds_share() =>
			Assert.Equal("see it with pgrep -l -g 42, and end it with pkill -KILL -g 42", ReviewRecord.GroupHint(42));

	[Fact]
	public void End_group_of_a_group_already_gone_returns_at_once()
	{
		var session = Spawn("true");
		// session.wait(): reaped, so the group's kill finds nothing.
		Assert.True(session.Reap(10));
		Assert.True(Groups.EndGroup(session));
	}

	[Fact]
	public void End_group_waits_for_a_live_member_and_gives_up_in_time()
	{
		// The pytest records killpg's signal; EndGroup's kill is libc's, with no seam, so here it goes to a group that can't exist.
		var now = 0.0;
		var session = new FakeSession(pid: NoSuchPid);
		var gone = Groups.EndGroup(session, waitSeconds: 1, sleep: s => now += s, clock: () => now, alive: _ => true);
		Assert.False(gone);
		Assert.Equal([1.0], session.Waits);
		Assert.True(now is >= 1 and < 1.2, $"gave up once its time was spent ({now})");
	}

	[Fact]
	public void End_group_gives_up_on_a_leader_that_wont_die()
	{
		// killpg patched to do nothing: a kill of a group that can't exist does nothing too.
		var session = new FakeSession(timesOut: true, pid: NoSuchPid);
		Assert.False(Groups.EndGroup(session, waitSeconds: 10));
		Assert.Equal([10.0], session.Waits);
	}

	[Fact]
	public void End_group_needs_no_kill_for_a_group_already_gone()
	{
		// killpg's ProcessLookupError is the real kill's ESRCH for a group that can't exist.
		Assert.True(Groups.EndGroup(new FakeSession(pid: NoSuchPid), alive: _ => false));
	}

	[Fact]
	public void Group_alive_counts_live_members_only()
	{
		var proc = P("proc");

		void Member(int pid, string state, int pgrp)
		{
			Directory.CreateDirectory(Path.Combine(proc, $"{pid}"));
			File.WriteAllText(Path.Combine(proc, $"{pid}", "stat"), $"{pid} (some (odd) name) {state} 1 {pgrp} {pgrp} 0 -1\n");
		}

		Member(10, "Z", 77);
		Member(11, "S", 88);
		Directory.CreateDirectory(Path.Combine(proc, "self"));
		Assert.False(Groups.GroupAlive(77, proc), "a zombie alone is gone");
		Member(12, "R", 77);
		Assert.True(Groups.GroupAlive(77, proc));
	}

	[Fact]
	public void Group_alive_reads_the_group_field_not_the_session_or_parent()
	{
		var proc = P("proc");
		Directory.CreateDirectory(Path.Combine(proc, "20"));
		File.WriteAllText(Path.Combine(proc, "20", "stat"), "20 (x) S 77 88 77 0 -1\n"); // parent and session 77, group 88
		Directory.CreateDirectory(Path.Combine(proc, "21"));
		File.WriteAllText(Path.Combine(proc, "21", "stat"), "21 (x) S\n"); // too short to have a group
		Directory.CreateDirectory(Path.Combine(proc, "22")); // no stat: gone while listed
		Assert.False(Groups.GroupAlive(77, proc));
		Assert.True(Groups.GroupAlive(88, proc));
	}

	[Fact]
	public void Group_alive_without_proc_asks_the_kernel()
	{
		// The pytest patches killpg to answer, then to raise ProcessLookupError; GroupAlive's kill(-pgid, 0) is libc's, so the kernel answers
		// here, of a real group while it is there and once it is ended.
		var session = Spawn("sleep", "30");
		try
		{
			Assert.True(Groups.GroupAlive(session.Pid, P("no-proc")));
		}
		finally
		{
			Assert.True(Groups.EndGroup(session));
		}
		Assert.False(Groups.GroupAlive(session.Pid, P("no-proc")));
		Assert.False(Groups.GroupAlive(NoSuchPid, P("no-proc")));
	}
}
