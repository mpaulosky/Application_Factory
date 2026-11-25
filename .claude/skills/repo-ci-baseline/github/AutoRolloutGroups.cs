// How the round runs its children and keeps its signals (auto_rollout.py's held to run_reapply, ADR 0016), as ADR 0017's Update for
// mpaulosky/Application_Factory#157 decides: each group's command is started with posix_spawn in a session of its own, holding the round's
// lock, its leader left unreaped until its group is killed; and SIGINT, SIGTERM and SIGHUP are held with PosixSignalRegistration, an ignored
// one left ignored. --auto is Linux only, so the structs and constants are glibc's on 64-bit Linux.
using System.Runtime.InteropServices;
using System.Text;

// A signal the round passes on once what it held is gone, as auto_rollout.py's passed_on raised KeyboardInterrupt (SIGINT) or
// SystemExit(128 + the signal): the round ends with Code, after printing the command that resumes it.
sealed class Interrupted(int signum) : Exception($"signal {signum}")
{
	public int Signum => signum;
	public int Code => 128 + signum;
}

// A command posix_spawn couldn't start, worded as Python's OSError for it ("[Errno 2] No such file or directory: 'claude'"). PyName is
// the OSError subclass Python raised, for a message that names it.
sealed class SpawnError(int errno, string program) : IOException($"[Errno {errno}] {Marshal.GetPInvokeErrorMessage(errno)}: {Py.ReprString(program)}")
{
	public int Errno => errno;
	public string PyName => errno switch
	{
		2 => "FileNotFoundError",
		1 or 13 => "PermissionError",
		_ => "OSError",
	};
}

// How a group's command is started: its whole environment, the descriptors it keeps (the round's lock), its folder (null: the round's),
// its stdin (StdinDevNull: /dev/null; else the stream, null for the round's own), its stdout (null: the round's own), and its stderr
// (StderrToStdout: its stdout, as 2>&1; else the stream, null for the round's own).
sealed record Spawning(IReadOnlyDictionary<string, string> Env, IReadOnlyList<int> PassFds, string? Cwd = null, FileStream? Stdin = null,
		bool StdinDevNull = false, FileStream? Stdout = null, FileStream? Stderr = null, bool StderrToStdout = false);

// What starts a group's command (posix_spawn in a session of its own: Groups.Spawn), which a test replaces.
delegate IGroupChild Spawner(IReadOnlyList<string> command, Spawning how);

// The first signal of a hold, recorded rather than acted on (DeferredSignals); Got is empty until one comes.
sealed class Deferred : IDisposable
{
	public readonly List<int> Got = [];
	IDisposable? handling;

	internal Deferred(IEnumerable<int> signums) =>
		handling = Signals.Handling(signums, signum =>
		{
			lock (Got)
				if (Got.Count == 0)
					Got.Add(signum);
		});

	public void Dispose()
	{
		handling?.Dispose();
		handling = null;
	}
}

// The round's signals. A handler installed here replaces the one before it for as long as it is installed, as signal.signal does, and the
// one before it is back once it is removed; with none, the runtime's own handling (the signal's default) applies.
static class Signals
{
	public const int SigHup = 1, SigInt = 2, SigKill = 9, SigTerm = 15;

	// What rollout.sh passes on of the signals its caller ignores: the SigIgn mask of /proc/$$/status, read before its exec, since the
	// runtime takes SIGTERM over before the first line runs. Init reads it and drops it from the environment.
	public const string IgnoredVariable = "ROLLOUT_SIGIGN";

	static readonly Lock Gate = new();
	static readonly Dictionary<int, List<Action<int>>> Handlers = [];
	static readonly Dictionary<int, PosixSignalRegistration> Registrations = [];

	// Put SIGTERM back to ignored when rollout.sh's caller ignored it (nohup's SIGHUP and a background job's SIGINT stay ignored by
	// themselves): SIG_IGN through libc, so the round holds nothing for it and its children inherit it, as Python's do.
	public static void Init()
	{
		var mask = Environment.GetEnvironmentVariable(IgnoredVariable);
		Environment.SetEnvironmentVariable(IgnoredVariable, null);
		if (mask is not null && ulong.TryParse(mask, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var bits) && (bits & Bit(SigTerm)) != 0)
			Ignore(SigTerm);
	}

	// held: whether a signal is to be handled here, not one the caller ignores (nohup's SIGHUP, a background job's SIGINT): its bit of
	// SigIgn in /proc/self/status, which a registration for one that isn't ignored leaves clear.
	public static bool Held(int signum) => (IgnoredMask() & Bit(signum)) == 0;

	static ulong Bit(int signum) => 1UL << (signum - 1);

	static ulong IgnoredMask()
	{
		try
		{
			foreach (var line in File.ReadLines("/proc/self/status"))
				if (line.StartsWith("SigIgn:", StringComparison.Ordinal))
					return ulong.Parse(line["SigIgn:".Length..].Trim(), System.Globalization.NumberStyles.AllowHexSpecifier, null);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
		{
		}
		return 0;
	}

	// handling: handler for each of signums the caller doesn't ignore, until the result is disposed, when the ones before are back.
	public static IDisposable Handling(IEnumerable<int> signums, Action<int> handler)
	{
		List<int> taken = [];
		lock (Gate)
			foreach (var signum in signums.Distinct())
			{
				if (!Held(signum))
					continue;
				if (!Handlers.TryGetValue(signum, out var stack))
					Handlers[signum] = stack = [];
				stack.Add(handler);
				if (!Registrations.ContainsKey(signum))
					Registrations[signum] = PosixSignalRegistration.Create((PosixSignal)signum, context => OnSignal(signum, context));
				taken.Add(signum);
			}
		return new Release(() =>
		{
			lock (Gate)
				foreach (var signum in taken)
				{
					var stack = Handlers[signum];
					stack.RemoveAt(stack.LastIndexOf(handler));
					if (stack.Count > 0)
						continue;
					Handlers.Remove(signum);
					Registrations.Remove(signum, out var registration);
					registration?.Dispose();
				}
		});
	}

	static void OnSignal(int signum, PosixSignalContext context)
	{
		Action<int>? handler;
		lock (Gate)
			handler = Handlers.TryGetValue(signum, out var stack) && stack.Count > 0 ? stack[^1] : null;
		if (handler is null)
			return;
		context.Cancel = true;
		handler(signum);
	}

	sealed class Release(Action release) : IDisposable
	{
		Action? pending = release;

		public void Dispose()
		{
			pending?.Invoke();
			pending = null;
		}
	}

	// round_signals: for the whole round, SIGINT, SIGTERM and SIGHUP outside a hold call onSignal (the round's: it prints the resume command and
	// ends with 128 + the signal), as auto_rollout.py's SystemExit and KeyboardInterrupt ended it. An ignored one stays ignored.
	public static IDisposable RoundSignals(Action<int> onSignal) => Handling([SigInt, SigTerm, SigHup], onSignal);

	// deferred_signals: while it is held, SIGINT, SIGTERM and SIGHUP are only recorded (the first one, in its Got), never acted on: a signal
	// can't land between starting a group and holding it, or in the middle of ending it. The caller acts on it once the group is gone. An
	// ignored one stays ignored.
	public static Deferred DeferredSignals(IEnumerable<int>? signums = null) => new(signums ?? [SigInt, SigTerm, SigHup]);

	// passed_on: what passes a deferred signal on.
	public static Interrupted PassedOn(int signum) => new(signum);

	// signal(signum, SIG_IGN).
	public static void Ignore(int signum) => SetSignal(signum, 1);

	[DllImport("libc", EntryPoint = "signal")]
	static extern nint SetSignal(int signum, nint handler);
}

// A command posix_spawn started in a session of its own, its pid the group's id. Exit leaves the leader unreaped (waitid's WNOWAIT), so
// its pid can't be another group's until EndGroup has signalled the group and reaped it (Reap).
sealed class SpawnedChild(int pid) : IGroupChild
{
	int? reaped;

	public int Pid => pid;

	// exit_unreaped: its exit code (minus the signal that ended it), or null once seconds pass first.
	public int? Exit(double seconds)
	{
		if (reaped is int done)
			return done;
		return Groups.Poll(seconds, () => Groups.WaitId(pid, reap: false));
	}

	// Reap the leader (after its group is killed): whether it was reaped within seconds.
	public bool Reap(double seconds)
	{
		if (reaped is not null)
			return true;
		reaped = Groups.Poll(seconds, () => Groups.WaitId(pid, reap: true));
		return reaped is not null;
	}
}

static class Groups
{
	// How long a killed group may take to be gone before the round says it didn't.
	public const int GroupWaitSeconds = 10;

	// child_env: the environment of the commands RunInGroup starts, environ with NoBuildServers.
	public static Dictionary<string, string> ChildEnv(IReadOnlyDictionary<string, string> environ)
	{
		var env = environ.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
		foreach (var (name, value) in AutoRollout.NoBuildServers)
			env[name] = value;
		return env;
	}

	// The program a command runs: command[0] when it holds a /, else the first executable file of that name in the absolute folders of the
	// PATH it is given, as Gh.cs finds gh (Python's Popen took a relative folder too). Throws SpawnError when there is none.
	public static string Program(string name, IReadOnlyDictionary<string, string> env)
	{
		if (name.Contains('/'))
			return name;
		foreach (var folder in (env.GetValueOrDefault("PATH") ?? PyLib.DefPath).Split(':'))
		{
			if (!folder.StartsWith('/'))
				continue;
			var candidate = PyLib.Join(folder, name);
			if (File.Exists(candidate) && Posix.Executable(candidate))
				return candidate;
		}
		throw new SpawnError(2, name);
	}

	// Start command in a session and process group of its own (POSIX_SPAWN_SETSID), with how's environment, folder and streams, its signal
	// mask emptied and SIGPIPE at its default (as Python's restore_signals has it: .NET ignores SIGPIPE), and the descriptors of how.PassFds
	// kept open in it alone (adddup2 of a descriptor onto itself clears its FD_CLOEXEC there), as pass_fds keeps them. Throws SpawnError.
	public static IGroupChild Spawn(IReadOnlyList<string> command, Spawning how)
	{
		var program = Program(command[0], how.Env);
		var actions = Marshal.AllocHGlobal(1024);
		var attributes = Marshal.AllocHGlobal(1024);
		var signals = Marshal.AllocHGlobal(1024);
		try
		{
			Check(posix_spawn_file_actions_init(actions), program);
			Check(posix_spawnattr_init(attributes), program);
			try
			{
				if (how.Cwd is not null)
					Check(posix_spawn_file_actions_addchdir_np(actions, how.Cwd), program);
				if (how.StdinDevNull)
					Check(posix_spawn_file_actions_addopen(actions, 0, "/dev/null", 0, 0), program);
				else if (how.Stdin is not null)
					Check(posix_spawn_file_actions_adddup2(actions, AutoRollout.Descriptor(how.Stdin), 0), program);
				if (how.Stdout is not null)
					Check(posix_spawn_file_actions_adddup2(actions, AutoRollout.Descriptor(how.Stdout), 1), program);
				if (how.StderrToStdout)
					Check(posix_spawn_file_actions_adddup2(actions, 1, 2), program);
				else if (how.Stderr is not null)
					Check(posix_spawn_file_actions_adddup2(actions, AutoRollout.Descriptor(how.Stderr), 2), program);
				foreach (var fd in how.PassFds)
					Check(posix_spawn_file_actions_adddup2(actions, fd, fd), program);
				sigemptyset(signals);
				Check(posix_spawnattr_setsigmask(attributes, signals), program);
				sigaddset(signals, 13); // SIGPIPE
				Check(posix_spawnattr_setsigdefault(attributes, signals), program);
				Check(posix_spawnattr_setflags(attributes, 0x80 | 0x04 | 0x08), program); // POSIX_SPAWN_SETSID | SETSIGDEF | SETSIGMASK
				string?[] argv = [.. command.Skip(1).Prepend(command[0]), null];
				string?[] envp = [.. how.Env.Select(pair => $"{pair.Key}={pair.Value}"), null];
				Check(posix_spawn(out var pid, program, actions, attributes, argv, envp), program);
				return new SpawnedChild(pid);
			}
			finally
			{
				posix_spawnattr_destroy(attributes);
				posix_spawn_file_actions_destroy(actions);
			}
		}
		finally
		{
			Marshal.FreeHGlobal(signals);
			Marshal.FreeHGlobal(attributes);
			Marshal.FreeHGlobal(actions);
		}
	}

	static void Check(int error, string program)
	{
		if (error != 0)
			throw new SpawnError(error, program);
	}

	// A scripts/review.sh prepare or record of the round's record (ReviewRecord.RunBounded's spawn): io's folder and streams, the round's
	// environment, and the lock.
	public static IGroupChild SpawnBounded(IReadOnlyList<string> command, Bounded io) =>
		Spawn(command, new Spawning(AutoRollout.Environ(), AutoRollout.HeldLockFds(), io.Cwd, io.Stdin, io.Stdin is null, io.Stdout, io.Stderr,
				io.Stderr is null));

	// Poll read until it gives an exit code or seconds pass: the code, or null.
	internal static int? Poll(double seconds, Func<int?> read, Action<double>? sleep = null, Func<double>? clock = null)
	{
		sleep ??= Sleep;
		clock ??= Monotonic;
		var deadline = clock() + seconds;
		while (true)
		{
			if (read() is int code)
				return code;
			if (clock() >= deadline)
				return null;
			sleep(Math.Min(0.05, Math.Max(0.0, deadline - clock())));
		}
	}

	// waitid(P_PID, pid, WEXITED | WNOHANG, with WNOWAIT unless reap): the exit code (minus the signal that ended it) of a child that
	// exited, or null while it runs. A child no longer there to wait for (reaped already) reads as exit code -1 rather than waiting forever.
	internal static int? WaitId(int pid, bool reap)
	{
		var info = new byte[128];
		if (waitid(1, pid, info, 4 | 1 | (reap ? 0 : 0x01000000)) != 0)
			return Marshal.GetLastPInvokeError() == 4 ? null : -1; // EINTR: asked again
		if (BitConverter.ToInt32(info, 16) != pid)
			return null;
		var status = BitConverter.ToInt32(info, 24);
		return BitConverter.ToInt32(info, 8) == 1 ? status : -status; // CLD_EXITED
	}

	public static double Monotonic() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

	public static void Sleep(double seconds) => Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0.0, seconds)));

	// group_alive: whether a process other than a zombie is left in the process group. A killed member stays a zombie until it is reaped
	// (by init, once its parent is gone), which where nothing reaps orphans is never: it is gone all the same. Without /proc, whether the
	// group exists at all.
	public static bool GroupAlive(int pgid, string proc = "/proc")
	{
		if (!Directory.Exists(proc))
			return kill(-pgid, 0) == 0 || Marshal.GetLastPInvokeError() != 3; // ESRCH: nothing in the group
		IEnumerable<string> entries;
		try
		{
			entries = [.. Directory.EnumerateFileSystemEntries(proc).Select(Path.GetFileName).OfType<string>()];
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return false;
		}
		foreach (var entry in entries)
		{
			if (entry.Length == 0 || !entry.All(char.IsAsciiDigit))
				continue;
			string[] fields;
			try
			{
				var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(proc, entry, "stat")));
				var close = text.LastIndexOf(')');
				if (close < 0)
					continue;
				fields = text[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				continue;
			}
			if (fields.Length > 2 && fields[2] == pgid.ToString(System.Globalization.CultureInfo.InvariantCulture) && fields[0] != "Z")
				return true;
		}
		return false;
	}

	// end_group: kill the child's whole process group, reap its leader, and wait until no live process is left in the group, for at most
	// waitSeconds in all: whether it is gone. The group's id is the leader's pid, which can't be another group's while the leader is
	// unreaped (Exit leaves it a zombie) or a member is left, so the signal reaches only this group: the leader is reaped only after it.
	public static bool EndGroup(IGroupChild child, double waitSeconds = GroupWaitSeconds, Action<double>? sleep = null, Func<double>? clock = null,
			Func<int, bool>? alive = null)
	{
		sleep ??= Sleep;
		clock ??= Monotonic;
		alive ??= pgid => GroupAlive(pgid);
		var deadline = clock() + waitSeconds;
		kill(-child.Pid, Signals.SigKill); // ESRCH: nothing is left in the group
		if (!child.Reap(waitSeconds))
			return false; // the leader didn't die: stuck in the kernel
		while (alive(child.Pid))
		{
			if (clock() >= deadline)
				return false;
			sleep(0.1);
		}
		return true;
	}

	// The message of the GroupLeft a command's group raises when it is still there after the kill.
	public static string LeftMessage(string program, int pid) =>
		$"what {Path.GetFileName(program)} left in its process group was still there {GroupWaitSeconds} s after it was killed, and holds the round's"
		+ $" lock and may still work in its worktree: {ReviewRecord.GroupHint(pid)}, before you resume";

	// run_in_group: a command, its output straight through, in a process group of its own, with ChildEnv: its exit code. Its group is
	// killed whole however it ends: after it exits, so nothing it left there (a build server) keeps the round's lock, which it is passed;
	// and on a SIGTERM or SIGHUP that ends the round or an interrupt, so neither it nor anything it started (the gate a push runs) goes on
	// working in a worktree after rollout.sh is gone. The signals are held from before it starts until its group is gone (a second one
	// meanwhile is dropped), then the first is passed on, as for a review session; a command that can't start throws its SpawnError, and
	// one whose group is still there after the kill throws GroupLeft (a stop: it can still work in the worktree, and holds the lock), each
	// after a signal that came meanwhile, which is passed on in their place (a GroupLeft is then written to err, so the person still hears
	// of the group).
	public static int? RunInGroup(IReadOnlyList<string> command, Spawner? popen = null, Func<IGroupChild, bool>? end = null, Func<double>? clock = null,
			TextWriter? err = null, IReadOnlyDictionary<string, string>? environ = null)
	{
		popen ??= Spawn;
		end ??= child => EndGroup(child);
		int? code = null;
		Exception? failed = null;
		List<int> got;
		using (var hold = Signals.DeferredSignals())
		{
			got = hold.Got;
			IGroupChild? child = null;
			try
			{
				child = popen(command, new Spawning(ChildEnv(environ ?? AutoRollout.Environ()), AutoRollout.HeldLockFds()));
			}
			catch (IOException error)
			{
				failed = error;
			}
			if (child is not null)
				try
				{
					code = ReviewRecord.WaitFor(child, double.PositiveInfinity, got, clock);
				}
				finally
				{
					// However it ends: what it left running in its group (a build server) holds the round's lock.
					if (!end(child))
						failed = new GroupLeft(LeftMessage(command[0], child.Pid));
				}
		}
		if (got.Count > 0)
		{
			if (failed is GroupLeft)
				(err ?? Console.Error).Write($"rollout.sh: {failed.Message}\n"); // the signal wins, but the person still has to end the group
			throw Signals.PassedOn(got[0]);
		}
		if (failed is not null)
			throw failed;
		return code;
	}

	// run_land: land.sh on the PRs; its exit code (0 once each has merged or closed).
	public static int? RunLand(IReadOnlyList<string> refs, Folders? folders = null) => RunInGroup([(folders ?? Folders.Default).Land, .. refs]);

	// run_reapply: reapply.sh on one checkout; its exit code.
	public static int? RunReapply(string brings, string path, Folders? folders = null) =>
		RunInGroup([(folders ?? Folders.Default).Reapply, "--brings", brings, path]);

	[DllImport("libc", SetLastError = true)]
	static extern int kill(int pid, int sig);

	[DllImport("libc", SetLastError = true)]
	static extern int waitid(int idtype, int id, byte[] infop, int options);

	// LPStr is UTF-8 on Linux, which LPUTF8Str can't be in an array.
	[DllImport("libc")]
	static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint fileActions, nint attributes,
			[MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv,
			[MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] envp);

	[DllImport("libc")]
	static extern int posix_spawn_file_actions_init(nint actions);

	[DllImport("libc")]
	static extern int posix_spawn_file_actions_destroy(nint actions);

	[DllImport("libc")]
	static extern int posix_spawn_file_actions_adddup2(nint actions, int fd, int newfd);

	[DllImport("libc")]
	static extern int posix_spawn_file_actions_addopen(nint actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int oflag, uint mode);

	[DllImport("libc")]
	static extern int posix_spawn_file_actions_addchdir_np(nint actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

	[DllImport("libc")]
	static extern int posix_spawnattr_init(nint attributes);

	[DllImport("libc")]
	static extern int posix_spawnattr_destroy(nint attributes);

	[DllImport("libc")]
	static extern int posix_spawnattr_setflags(nint attributes, short flags);

	[DllImport("libc")]
	static extern int posix_spawnattr_setsigmask(nint attributes, nint mask);

	[DllImport("libc")]
	static extern int posix_spawnattr_setsigdefault(nint attributes, nint mask);

	[DllImport("libc")]
	static extern int sigemptyset(nint set);

	[DllImport("libc")]
	static extern int sigaddset(nint set, int signum);
}
