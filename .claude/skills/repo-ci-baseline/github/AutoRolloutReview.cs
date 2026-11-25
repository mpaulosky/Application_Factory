// The headless review session (auto_rollout.py's settings_path to stop_line, ADR 0016): /pre-push-review run with claude -p in a re-Apply
// worktree, sandboxed, in a process group of its own, then checked and recorded. What it may read and run is AutoRolloutSandbox.cs's, what
// it may not change AutoRolloutGuard.cs's, and how its findings are recorded AutoRolloutRecord.cs's.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// The round's record of what a session staged (ReviewRecord.RecordReview, with the round's own process group), which a test replaces.
delegate (int Code, List<string> Broke) Recorder(string worktree, string run, FileStream log, string head, string keep, IList<int> got,
		Func<List<string>> unseen);

static class ReviewSession
{
	// What opens the log line saying why the session wasn't started (StopLine reads it back, from the log's first line only, for the
	// terminal).
	public const string NotStarted = "rollout.sh: not started: ";
	// The marker scripts/review.sh advisory ends its list with, for a session's comment; not shown on the terminal. A copy of
	// review-ledger.mts's own: a test holds them equal.
	public const string SessionMarker = "<!-- claude-session -->";
	// The log's first line once a session is about to start, so whatever the session prints comes after it and can't pass for the round's
	// own NotStarted line.
	public const string SessionStarts = "rollout.sh: the review session starts\n";
	// How the stop for a re-Apply worktree removed since reapply.sh made it begins, so Drive doesn't send the person there.
	public const string WorktreeGone = "the re-Apply worktree is gone: ";
	// A review session's wall clock: one round of parallel reviewers over a large re-Apply, with room to spare.
	public const int ReviewTimeoutMinutes = 180;
	// What RunReview gives back when a group the record left was still there after the kill, so whether it recorded can't be told.
	public const int RecordLeft = 258;

	// Where the session's settings are written: beside its log, in the person's own 0700 folder (LogDir), which the session can't read
	// (CredentialPaths) or write. Not its command line, which any local user reads in /proc/<pid>/cmdline, with every path the sandbox
	// denies.
	public static string SettingsPath(string log) => $"{(log.EndsWith(".log", StringComparison.Ordinal) ? log[..^4] : log)}.settings.json";

	// settings written to SettingsPath(log), 0600 and made new (never through a link or over a file there); its path. Throws IOException
	// worded as Python's OSError.
	public static string WriteSettings(JsonNode settings, string log)
	{
		var path = SettingsPath(log);
		using var file = Files.CreateNew(path, append: false);
		file.Write(Encoding.UTF8.GetBytes(PyLib.JsonDumps(settings)));
		return path;
	}

	// /pre-push-review headless in worktree: (exit code, the session's log, the Rules it broke). 124 when it runs past its time, 127 when it
	// can't start (its settings file can't be written among it), SessionBrokeARule when it broke one (they are listed, and in the log too),
	// RecordFailed when what it staged couldn't be recorded (it is kept, StagedCopy; a group prepare left is listed), RecordLeft (with the
	// group in the list) when the record left a process group the kill didn't end, so whether it recorded can't be told, NothingStaged when
	// it exited 0 having staged nothing. The session runs in Claude Code's sandbox (SessionSettings, in a file beside the log: WriteSettings)
	// with the skill and its agent from the Template, no settings file of its own or the repository's, no GitHub sign-in and a temporary
	// folder of its own, with the run folder it stages its findings in; it leads a process group of its own, which is killed whole whenever
	// it ends. A SIGINT, SIGTERM or SIGHUP is held from the start of the session until the group is gone and, when it exited 0, until what it
	// staged is recorded or kept; then it is passed on (Interrupted), after the kept folder and the command that records it are printed
	// (after RecordLeft, which may have recorded, with a word to check scripts/review.sh report first). Once it has exited 0 and broken no
	// Rule, the round records what it staged (record). The log is 0600 in a 0700 folder of the user's own; the session's temporary folder is
	// made in sessionDir, another such folder, once what killed runs left there is removed (SweepSessions). The settings file is removed
	// whenever the session ends, never one that was at its path before (WriteSettings refuses it), and one a killed run left beside its log
	// is removed first (SweepSettings). A clone whose git folder holds a sign-in secret (ConfigSecrets) isn't reviewed, and entries made
	// beside the session's folders while it ran, which it could read, and secrets added to the clone's git folder meanwhile are named
	// (AfterSession). A null argument is its default; err is the round's stderr, and makeTempFolder (Files.MakeTempFolder) what makes the
	// session's temporary folder, and makeTemp (Files.MakeTemp) what makes its log, which a test replaces.
	public static (int Code, string Log, List<string> Broke) RunReview(string repo, string worktree, Spawner? popen = null,
			Func<string, IReadOnlyList<string>>? changes = null, double timeout = ReviewTimeoutMinutes * 60, string? logDir = null,
			string? sessionDir = null, Func<IGroupChild, bool>? end = null, Func<double>? clock = null, Func<string, Guard>? guard = null,
			Func<Guard, List<string>>? check = null, Func<string>? skill = null, Func<JsonNode>? agents = null, Recorder? record = null,
			Func<string, IEnumerable<string>>? listdir = null, Func<string, string?>? which = null, Func<string, string, List<string>>? secrets = null,
			TextWriter? err = null, Func<string, string, string>? makeTempFolder = null,
			Func<string, string, string, (FileStream File, string Path)>? makeTemp = null)
	{
		logDir ??= AutoRollout.LogDir;
		sessionDir ??= AutoRollout.SessionDir;
		popen ??= Groups.Spawn;
		changes ??= path => AutoRollout.OwnedChanges(path);
		end ??= child => Groups.EndGroup(child);
		guard ??= path => SessionGuard.Read(path, Folders.Default.Owned);
		check ??= before => SessionGuard.BrokenRules(before, Folders.Default.Owned, changes);
		skill ??= () => AutoRollout.ReadSkill();
		agents ??= () => AutoRollout.AgentDefinition();
		record ??= DefaultRecord;
		which ??= PyLib.Which;
		secrets ??= (path, common) => AutoRollout.ConfigSecrets(path, common);
		err ??= Console.Error;
		makeTempFolder ??= Files.MakeTempFolder;
		makeTemp ??= Files.MakeTemp;
		FileStream out_;
		string log;
		try
		{
			AutoRollout.OwnLogDir(logDir);
			AutoRollout.SweepSettings(logDir);
			(out_, log) = makeTemp(logDir, $"review-{AutoRollout.ShortName(repo)}.", ".log");
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return (127, $"(no log: {error.Message})", []);
		}
		using (out_)
		{
			string tmp;
			try
			{
				AutoRollout.OwnLogDir(sessionDir);
				AutoRollout.SweepSessions(sessionDir);
				tmp = makeTempFolder(sessionDir, "rollout-session.");
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				Write(out_, $"{NotStarted}can't set the session up: {error.Message} (the session's own folder: a real folder of yours, not a symlink;"
						+ $" remove or fix it, or run /pre-push-review in {worktree}), then resume\n");
				return (127, log, []);
			}
			try
			{
				var there = AutoRollout.LExists(SettingsPath(log)); // not the round's own: WriteSettings refuses it, and it stays
				try
				{
					return ReviewIn(repo, worktree, out_, log, tmp, popen, changes, timeout, end, clock, guard, check, skill, agents, record, listdir,
							which, secrets, err);
				}
				finally
				{
					if (!there)
						try
						{
							File.Delete(SettingsPath(log)); // a killed run's is removed by the next (SweepSettings)
						}
						catch (Exception error) when (error is IOException or UnauthorizedAccessException)
						{
						}
				}
			}
			finally
			{
				PyLib.RemoveTree(tmp);
			}
		}
	}

	// The record the round makes of what a session staged: ReviewRecord.RecordReview, its prepare and record each in a process group of its own.
	static (int, List<string>) DefaultRecord(string worktree, string run, FileStream log, string head, string keep, IList<int> got,
			Func<List<string>> unseen) =>
		ReviewRecord.RecordReview(worktree, run, log, head, keep,
				(command, io) => ReviewRecord.RunBounded(command, io, Groups.SpawnBounded, child => Groups.EndGroup(child)), got, unseen);

	// RunReview's body, once its log is open and its temporary folder made.
	static (int Code, string Log, List<string> Broke) ReviewIn(string repo, string worktree, FileStream out_, string log, string tmp, Spawner popen,
			Func<string, IReadOnlyList<string>> changes, double timeout, Func<IGroupChild, bool> end, Func<double>? clock, Func<string, Guard> guard,
			Func<Guard, List<string>> check, Func<string> skill, Func<JsonNode> agents, Recorder record, Func<string, IEnumerable<string>>? listdir,
			Func<string, string?> which, Func<string, string, List<string>> secrets, TextWriter err)
	{
		(int, string, List<string>) Stop(string text)
		{
			Write(out_, $"{NotStarted}{text}\n");
			return (127, log, []);
		}

		if (!Directory.Exists(worktree))
			return Stop($"{WorktreeGone}{worktree} was removed since reapply.sh made it; resume, and reapply.sh makes it again");
		Guard before;
		List<string> held;
		JsonObject settings;
		(List<string> Reads, List<string> First) tools;
		List<string> command;
		Dictionary<string, string> env;
		string run;
		try
		{
			var owned = changes(worktree);
			before = guard(worktree);
			run = Path.Combine(tmp, "run");
			Posix.MakeDirectory(run, Convert.ToUInt32("700", 8));
			held = secrets(worktree, before.Common);
			if (held.Count > 0)
				return Stop($"the clone's git folder holds a sign-in secret ({string.Join("; ", held)}), which the session, reading that folder, could"
						+ " read. A key is named '<key> in <file>', the file that holds it: git config --file <file> --list shows it whole (a *** in"
						+ " one stands for its sign-in), and git config --file <file> --unset-all <key> removes it; a file named for credentials is"
						+ " named by its path: remove it. Keep the token in a credential helper, then resume");
			var home = PyLib.ExpandUser("~");
			tools = AutoRollout.ToolReads(home, which);
			settings = AutoRollout.SessionSettings(worktree, before.Common, before.Gitdir, tmp, listdir: listdir, which: which, tools: tools);
			string written;
			try
			{
				written = WriteSettings(settings, log);
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				return Stop($"can't write the session's settings file: {error.Message} (its folder, {PyLib.Dirname(log)}, must be writable with room"
						+ $" in it, and a file or link left at that path removed; or run /pre-push-review in {worktree}), then resume");
			}
			command = AutoRollout.ReviewCommand(repo, worktree, [.. owned], written, agents(), skill(), run);
			env = AutoRollout.SessionEnv(AutoRollout.Environ(), tmp, tools.First);
		}
		catch (GuardUnreadable error)
		{
			return Stop(error.Message);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
				or UnnameableError or ConfigUnreadable or System.Text.Json.JsonException or InvalidDataException) // a skill or agent that doesn't read
		{
			return Stop($"can't set the session up: {error.Message}");
		}
		var begun = out_.Seek(0, SeekOrigin.End);
		Write(out_, SessionStarts);
		int? code = null;
		var (started, survived) = (true, false);
		List<string> broke = [];
		List<int> got;
		var keep = "";
		string? head = null;
		IGroupChild? session = null;
		// One hold from the start of the session until what it staged is recorded or kept: a signal anywhere in it, the checks included
		// (they read every file of the worktree), would otherwise lose the run folder, and the session's findings with it.
		using (var hold = Signals.DeferredSignals())
		{
			got = hold.Got;
			try
			{
				session = popen(command, new Spawning(env, AutoRollout.HeldLockFds(), worktree, StdinDevNull: true, Stdout: out_, StderrToStdout: true));
			}
			catch (IOException error)
			{
				out_.SetLength(begun); // no session ran: the reason is the log's first line, as StopLine reads it
				Write(out_, $"{NotStarted}can't start claude: {error.Message}\n");
				started = false;
			}
			if (session is not null)
				try
				{
					code = ReviewRecord.WaitFor(session, timeout, got, clock);
					if (code is null && got.Count == 0)
					{
						Write(out_, $"\nrollout.sh: the review session ran past {Minutes(timeout)} minutes and was stopped\n");
						code = 124;
					}
				}
				finally
				{
					if (!end(session))
					{
						survived = true;
						Write(out_, $"\nrollout.sh: the session's process group was still there {Groups.GroupWaitSeconds} s after it was killed\n");
					}
				}
			if (got.Count > 0 && code != 0) // a session that exited 0 by itself goes on, so what it staged is kept below
				throw Signals.PassedOn(got[0]);
			if (!started)
				return (127, log, []);
			(keep, head) = (ReviewRecord.StagedCopy(log), before.WorktreeState.Head);
			// Warnings, each checked on its own, said before the record and guarded so that neither can stop it.
			AfterSession(out_, "what was made beside the session's folders",
					() => Appeared(settings, AutoRollout.ReadInputs(worktree, before.Common, tmp), listdir, which, tools), err);
			AfterSession(out_, "the clone's git folder for sign-in secrets added", () => AddedSecrets(held, secrets(worktree, before.Common)), err);
			try
			{
				broke = check(before);
			}
			catch (Exception error) // a check that fails can't vouch for the session: a stop, not a crash
			{
				Write(out_, $"rollout.sh: the checks after the session failed:\n{error}\n");
				broke = [$"the checks after the session failed ({error.GetType().Name}: {error.Message}; the traceback is in the log), so what it did"
						+ " can't be told"];
			}
			if (survived)
				// What is left of it can still write after the check read the ledgers.
				broke.Add($"the session's process group was still there {Groups.GroupWaitSeconds} s after it was killed, and can still write:"
						+ $" {ReviewRecord.GroupHint(session!.Pid)}, before you resume");
			if (broke.Count == 0 && code == 0)
			{
				if (got.Count == 0) // with one held, RecordReview keeps the findings and records nothing
					err.Write($"rollout.sh: recording the review (scripts/review.sh prepare, then record, up to {ReviewRecord.RecordTimeoutSeconds / 60}"
							+ " minutes each: an interrupt stops prepare, and waits for record to end)\n");
				try
				{
					(code, broke) = record(worktree, run, out_, head ?? "", keep, got, () => SessionGuard.UnseenChanges(before));
				}
				catch (GroupLeft error)
				{
					// record may have written the ledgers before it left the group, so whether it recorded is unknown.
					Write(out_, $"rollout.sh: {error.Message}\n");
					(code, broke) = (RecordLeft, [error.Message]);
					if (got.Count > 0)
						err.Write($"rollout.sh: {error.Message}\n"); // the signal wins, but the group must be ended
				}
				catch (Exception error) when (error is IOException or UnauthorizedAccessException)
				{
					Write(out_, $"rollout.sh: can't record the review: {PyErrorName(error)}: {error.Message}\n");
					code = ReviewRecord.RecordFailed;
				}
				catch (Exception error) when (error is not Interrupted) // a bug while recording is a stop that keeps the signal, not a crash
				{
					Write(out_, $"rollout.sh: recording the review failed:\n{error}\n");
					code = ReviewRecord.RecordFailed;
				}
			}
		}
		if (got.Count > 0)
		{
			var rules = code is not (ReviewRecord.RecordFailed or RecordLeft) ? broke : []; // else a group the record left, said below
			foreach (var rule in rules) // what the checks found is said before the signal ends the round
			{
				Write(out_, $"rollout.sh: the session broke a rule: {rule}\n");
				err.Write($"rollout.sh: the session broke a rule: {rule}\n");
			}
			if (code == ReviewRecord.RecordFailed)
				foreach (var left in broke) // a group prepare left: it holds the lock, so it must be ended before a record
					err.Write($"rollout.sh: {left}\n");
			if (rules.Count == 0 && head is not null && ReviewRecord.KeptHead(keep) == head)
			{
				// A broken rule means nothing is to be recorded, so no command is printed then. The stop that would print it isn't reached:
				// the signal ends the round here. After RecordLeft the review may be recorded already, so the person checks that before
				// recording it again.
				var maybe = code == RecordLeft
						? $"whether the review was recorded can't be told: cd {PyLib.ShQuote(worktree)} && scripts/review.sh report shows it; if it wasn't, "
						: "";
				err.Write($"rollout.sh: what the session staged is kept in {keep} (see {log}); {maybe}record it with:"
						+ $" {ReviewRecord.RecordCommand(worktree, keep, head)}\n");
			}
			throw Signals.PassedOn(got[0]);
		}
		if (code is ReviewRecord.RecordFailed or RecordLeft)
			return (code.Value, log, broke); // a group the record left behind, not a rule the session broke
		foreach (var rule in broke)
			Write(out_, $"rollout.sh: the session broke a rule: {rule}\n");
		return (broke.Count > 0 ? ReviewRecord.SessionBrokeARule : code!.Value, log, broke);
	}

	// timeout // 60, as Python prints it: an int for a whole number of seconds, else a float.
	static string Minutes(double timeout) =>
		timeout == Math.Floor(timeout) ? ((long)timeout / 60).ToString(System.Globalization.CultureInfo.InvariantCulture)
			: Py.FloatRepr(Math.Floor(timeout / 60));

	// The name of the Python OSError a .NET error stands for, in a message that names it.
	public static string PyErrorName(Exception error) => error switch
	{
		SpawnError spawn => spawn.PyName,
		UnauthorizedAccessException => "PermissionError",
		FileNotFoundException or DirectoryNotFoundException => "FileNotFoundError",
		_ => "OSError",
	};

	// Text written to the log at once, at its end (the session, which shares it, has written there meanwhile).
	static void Write(FileStream log, string text)
	{
		log.Write(Encoding.UTF8.GetBytes(text));
		log.Flush();
	}

	// What to say of entries made beside the session's folders while it ran (UnlistedSince, from ReadInputs' inputs): it could read those,
	// since its sandbox was set before them. The person's other programs or Claude Code itself, which runs outside the sandbox (a .claude
	// folder in the main checkout, say), made them: the session writes only its own folder. A list of lines, none when nothing appeared.
	public static List<string> Appeared(JsonNode settings, (List<string> Reads, List<string> Credentials, string Home) inputs,
			Func<string, IEnumerable<string>>? listdir = null, Func<string, string?>? which = null, (List<string> Reads, List<string> First)? tools = null)
	{
		List<string> made;
		try
		{
			var denials = settings["sandbox"]!["filesystem"]!["denyRead"]!.AsArray().Select(node => (string)node!);
			made = AutoRollout.UnlistedSince(denials, inputs.Reads, inputs.Credentials, inputs.Home, listdir, which, tools);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or UnnameableError or ArgumentException)
		{
			return [$"what was made beside the session's folders while it ran can't be told ({WithoutResume(error.Message)})"];
		}
		if (made.Count == 0)
			return [];
		return ["made while the session ran, after its sandbox was set, so it could read them (check them): " + string.Join(", ", made.Take(20))
				+ (made.Count > 20 ? $" (and {made.Count - 20} more)" : "")];
	}

	// What to say of sign-in secrets added to the clone's git folder while the session ran (ConfigSecrets' entries).
	public static List<string> AddedSecrets(IEnumerable<string> before, IEnumerable<string> now)
	{
		var had = before.ToHashSet(StringComparer.Ordinal);
		var added = now.Where(key => !had.Contains(key)).ToList();
		return added.Count > 0 ? [$"added to the clone's git folder while the session ran, so it could read them: {string.Join(", ", added)}"] : [];
	}

	// Say lines() (checking what checked names) in the log and on err: warnings, not broken Rules, so nothing in them, not an exception, a
	// log that can't be written or a closed err, may stop the round from recording or keeping what the session staged. A check that fails
	// says so, its stack trace in the log.
	public static void AfterSession(FileStream out_, string checked_, Func<List<string>> lines, TextWriter? err = null)
	{
		err ??= Console.Error;
		List<string> said;
		try
		{
			said = lines();
		}
		catch (Exception error) when (error is not Interrupted) // a warning that fails is a line in the log, not a lost review
		{
			said = [$"checking {checked_} failed ({error.GetType().Name}: {WithoutResume(error.Message)}); check it yourself"];
			try
			{
				Write(out_, $"{error}\n");
			}
			catch (Exception written) when (written is IOException or ObjectDisposedException or NotSupportedException)
			{
			}
		}
		foreach (var text in said)
		{
			try
			{
				Write(out_, $"rollout.sh: {text}\n");
			}
			catch (Exception written) when (written is IOException or ObjectDisposedException or NotSupportedException)
			{
				// the other one may have it; the review goes on either way
			}
			try
			{
				err.Write($"rollout.sh: {text}\n");
			}
			catch (Exception written) when (written is IOException or ObjectDisposedException or NotSupportedException)
			{
			}
		}
	}

	// error's text without its closing 'then resume' and what follows it: said where nothing stopped.
	public static string WithoutResume(string error) => Regex.Replace(error, @"[,;:]?\s*then resume\b.*$", "", RegexOptions.Singleline);

	// 'couldn't be started: <why>', from the log's first line when it is a NotStarted line, or null. RunReview writes one first, to a new
	// log, only when no session ran; before a session starts it writes SessionStarts first, so a claude that started and exited 127 has no
	// line of its own read, the first included.
	public static string? StopLine(string? log)
	{
		if (log is null)
			return null;
		string first;
		try
		{
			using var reader = new StreamReader(log, new UTF8Encoding(false, false));
			first = ReadLine(reader);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return null;
		}
		return first.StartsWith(NotStarted, StringComparison.Ordinal) ? $"couldn't be started: {WithoutResume(PyLib.Strip(first[NotStarted.Length..]))}" : null;
	}

	// readline() of a text file: up to and with its first \n (\r and \r\n read as one, as text mode reads them).
	static string ReadLine(StreamReader reader)
	{
		var line = new StringBuilder();
		while (reader.Read() is var c and >= 0)
		{
			if (c == '\r')
			{
				if (reader.Peek() == '\n')
					reader.Read();
				return line.Append('\n').ToString();
			}
			line.Append((char)c);
			if (c == '\n')
				break;
		}
		return line.ToString();
	}

	// What the review just recorded in worktree leaves open of Advisory findings, as scripts/review.sh advisory lists them, or null when it
	// leaves none: the round files no issue and the PR lands, so the terminal is where the person sees them. When the list can't be read,
	// its error's first line (Shown: escaped and cut) and the command to read it.
	public static string? OpenAdvisories(string worktree, Runner? run = null)
	{
		run ??= AutoRollout.RunQuiet;
		var (code, output, error) = run([Path.Combine(worktree, "scripts", "review.sh"), "advisory"], worktree);
		var text = PyLib.Strip(output.Replace(SessionMarker, "", StringComparison.Ordinal));
		if (code == 0)
			return text.Length == 0 || text == "No open Advisory findings." ? null : text;
		var lines = AutoRollout.SplitLines(PyLib.Strip(error), keepEnds: false);
		return AdvisoriesFailed(worktree, lines.Count > 0 ? SessionGuard.Shown(lines[0]) : $"exit {code}");
	}

	// What is shown for an Advisory list that can't be read (why is already Shown's or the exit code), and the command that reads it.
	public static string AdvisoriesFailed(string worktree, string why) =>
		$"(scripts/review.sh advisory failed: {why}; cd {PyLib.ShQuote(worktree)} && scripts/review.sh advisory lists them before the PR lands)";
}

// The files the round makes as Python's tempfile and os.open make them.
static class Files
{
	const string Letters = "abcdefghijklmnopqrstuvwxyz0123456789_";

	// tempfile's random part of a name: eight of its letters.
	static string RandomPart() => new([.. Enumerable.Range(0, 8).Select(_ => Letters[Random.Shared.Next(Letters.Length)])]);

	// open(path, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600), with O_APPEND when append: a file of the user's own made new,
	// never through a link or over one there. With O_APPEND every write goes to the file's end, after what a child writing to the same
	// file (a session's log) wrote there, which .NET's own position doesn't know of. Throws IOException worded as Python's OSError.
	public static FileStream CreateNew(string path, bool append)
	{
		var flags = 0x2 | 0x40 | 0x80 | ONoFollow | 0x80000 | (append ? 0x400 : 0);
		var fd = Open(path, flags, Convert.ToUInt32("600", 8));
		if (fd < 0)
		{
			var errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
			throw new IOException($"[Errno {errno}] {System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(errno)}: {Py.ReprString(path)}");
		}
		return new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true), FileAccess.ReadWrite);
	}

	// mkstemp(prefix=, suffix=, dir=): a new 0600 file in folder, open for appending, and its path.
	public static (FileStream File, string Path) MakeTemp(string folder, string prefix, string suffix)
	{
		for (var tries = 0; ; tries++)
		{
			var path = PyLib.Join(folder, $"{prefix}{RandomPart()}{suffix}");
			try
			{
				return (CreateNew(path, append: true), path);
			}
			catch (IOException error) when (tries < 100 && error.Message.StartsWith("[Errno 17]", StringComparison.Ordinal))
			{
			}
		}
	}

	// mkdtemp(prefix=, dir=): a new 0700 folder in folder; its path.
	public static string MakeTempFolder(string folder, string prefix)
	{
		for (var tries = 0; ; tries++)
		{
			var path = PyLib.Join(folder, $"{prefix}{RandomPart()}");
			try
			{
				Posix.MakeDirectory(path, Convert.ToUInt32("700", 8));
				return path;
			}
			catch (IOException error) when (tries < 100 && error.Message.StartsWith("[Errno 17]", StringComparison.Ordinal))
			{
			}
		}
	}

	static int ONoFollow => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture is System.Runtime.InteropServices.Architecture.Arm64
			or System.Runtime.InteropServices.Architecture.Arm ? 0x8000 : 0x20000;

	[System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
	static extern int Open([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
}
