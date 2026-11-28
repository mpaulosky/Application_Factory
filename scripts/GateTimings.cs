// The step timings summary (mpaulosky/Application_Factory#220), which scripts/gate-timings.cs runs and the gate-timings skill reports from: reads the log
// scripts/lib/timings.sh writes for scripts/gate.sh and test.sh, a line a step,
//
//     run  started  branch  commit  script  step  seconds  status
//
// and writes a Markdown table of its last N runs (the runs in the order they first appear), one row per script and step, slowest median first: the run
// count, the median, the slowest and the latest seconds, and the change from the median of the older half of those runs to the newer half's. A run that
// logged a step twice counts its last. A skipped step (a cached pass covered it, or there was nothing for it to check) is counted apart, not timed; a line
// that isn't eight tab-separated fields with a plain decimal for its seconds is left out and counted. Above the table: the runs' first and last start, a
// full gate run's median total, and the commits where the halves meet, so a report needs nothing from the raw log. Every name it prints is cleaned
// (control, line-separator and bidi characters as spaces) and Markdown-escaped, since a line can come from any branch's run or any file.
//
// The log is --log FILE, else GATE_TIMINGS_LOG (an absolute path; a relative one is refused, as the writer refuses it), else
// docs/timings/gate-timings.tsv beside the .git folder the caller's checkout shares with its worktrees (the primary checkout's). A relative --log is the
// caller's, GATE_TIMINGS_CALLER_DIR, which scripts/gate-timings.sh sets since it runs this from scripts/. git is started by its full path from PATH's
// absolute folders, never from the current or the app's folder.
// --help prints the usage and reads nothing (scripts/gate-timings.sh builds the app with it). Exit 0 with the table or the usage, 1 when there is no log or no step line in it (or an unexpected error, said on stderr), 2 for a bad argument.
using System.Diagnostics;
using System.Globalization;
using System.Text;

static class GateTimings
{
	public const int DefaultRuns = 20;
	const string Usage = "Usage: scripts/gate-timings.sh [--runs N] [--log FILE]";

	// One script and step over the runs read: one time per run, with the run's place (1 for the oldest run read).
	public sealed record Step(string Script, string Name, IReadOnlyList<(int Place, double Seconds)> Times)
	{
		public double Median => GateTimings.Median(Times.Select(time => time.Seconds));
		public double Slowest => Times.Max(time => time.Seconds);
		public double Latest => Times.MaxBy(time => time.Place).Seconds;
	}

	// One run read: its first line's start and commit, and the sum of its gate.sh lines' seconds (null when it logged none).
	public sealed record RunInfo(string Started, string Commit, double? GateSeconds);

	public sealed record Summary(IReadOnlyList<RunInfo> RunList, IReadOnlyList<Step> Steps, int Skipped, int Malformed)
	{
		public int Runs => RunList.Count;
	}

	// The entry point's wrapper, as the skill's scripts have Script.Run: UTF-8 writers without a BOM, flushed, and an unexpected exception a message on
	// stderr and exit 1, never the runtime's abort.
	public static int Entry(string[] args)
	{
		var utf8 = new UTF8Encoding(false);
		using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };
		using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { NewLine = "\n", AutoFlush = true };
		return Guarded(() => Run(args, stdout, stderr, GitCommonDir, Environment.GetEnvironmentVariable), stderr);
	}

	public static int Guarded(Func<int> run, TextWriter stderr)
	{
		try
		{
			return run();
		}
		catch (Exception error)
		{
			stderr.WriteLine($"gate-timings failed: {error.GetType().Name}: {Clean(error.Message)}");
			return 1;
		}
	}

	public static int Run(string[] args, TextWriter stdout, TextWriter stderr, Func<string, string?, string?> gitCommonDir, Func<string, string?> environment)
	{
		var caller = environment("GATE_TIMINGS_CALLER_DIR");
		if (string.IsNullOrEmpty(caller))
			caller = Directory.GetCurrentDirectory();
		var runs = DefaultRuns;
		string? log = null;
		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--runs" when i + 1 < args.Length:
					if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out runs) || runs < 1)
					{
						stderr.WriteLine($"--runs takes a whole number of 1 or more, not '{Clean(args[i + 1])}'.");
						return 2;
					}
					i++;
					break;
				case "--log" when i + 1 < args.Length && args[i + 1].Length > 0:
					log = args[++i];
					break;
				case "--help" or "-h":
					stdout.WriteLine(Usage);
					return 0;
				default:
					stderr.WriteLine(Usage);
					return 2;
			}
		}
		if (log is null)
		{
			var named = environment("GATE_TIMINGS_LOG");
			if (!string.IsNullOrEmpty(named))
			{
				if (!Path.IsPathRooted(named))
				{
					stderr.WriteLine($"GATE_TIMINGS_LOG must be an absolute path, not '{Clean(named)}' (the gate's run logs nothing to a relative one).");
					return 2;
				}
				log = named;
			}
		}
		if (log is null)
		{
			var common = gitCommonDir(caller, environment("PATH"));
			if (common is null)
			{
				stderr.WriteLine("Not in a git checkout, so there is no docs/timings/gate-timings.tsv to read: pass --log FILE.");
				return 1;
			}
			common = Path.TrimEndingDirectorySeparator(common);
			if (Path.GetFileName(common) != ".git")
			{
				stderr.WriteLine($"The checkout's git folder, {Clean(common)}, isn't a .git folder, so it has no step timings log: pass --log FILE.");
				return 1;
			}
			log = Path.Combine(Path.GetDirectoryName(common)!, "docs", "timings", "gate-timings.tsv");
		}
		log = Path.GetFullPath(log, caller);
		string[] lines;
		try
		{
			lines = File.ReadAllLines(log, new UTF8Encoding(false));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			stderr.WriteLine($"No step timings log at {Clean(log)} yet: a run of scripts/gate.sh writes it.");
			return 1;
		}
		var summary = Summarize(lines, runs);
		if (summary.Runs == 0)
		{
			stderr.WriteLine($"The log at {Clean(log)} holds no step lines yet.");
			return 1;
		}
		stdout.Write(Render(summary, log));
		return 0;
	}

	public static Summary Summarize(IEnumerable<string> lines, int runs)
	{
		var rows = new List<string[]>();
		var malformed = 0;
		foreach (var line in lines)
		{
			var fields = line.Split('\t');
			if (fields.Length != 8 || !IsSeconds(fields[6]))
			{
				malformed++;
				continue;
			}
			rows.Add(fields);
		}
		var order = rows.Select(fields => fields[0]).Distinct().ToList();
		var chosen = order.Skip(Math.Max(0, order.Count - runs)).Select((run, index) => (run, index)).ToDictionary(pair => pair.run, pair => pair.index + 1);
		var skipped = 0;
		// Per script and step, per run place, the last time logged.
		var steps = new Dictionary<(string Script, string Name), SortedDictionary<int, double>>();
		var keys = new List<(string Script, string Name)>();
		var info = new Dictionary<int, RunInfo>();
		foreach (var fields in rows)
		{
			if (!chosen.TryGetValue(fields[0], out var place))
				continue;
			var seconds = double.Parse(fields[6], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
			var run = info.TryGetValue(place, out var known) ? known : new RunInfo(fields[1], fields[3], null);
			if (fields[4] == "gate.sh")
				run = run with { GateSeconds = (run.GateSeconds ?? 0) + seconds };
			info[place] = run;
			if (fields[7] == "skipped")
			{
				skipped++;
				continue;
			}
			var key = (fields[4], fields[5]);
			if (!steps.TryGetValue(key, out var times))
			{
				steps[key] = times = [];
				keys.Add(key);
			}
			times[place] = seconds;
		}
		var list = keys.Select(key => new Step(key.Script, key.Name, steps[key].Select(time => (time.Key, time.Value)).ToList()))
			.OrderByDescending(step => step.Median).ToList();
		var runList = Enumerable.Range(1, chosen.Count).Select(place => info[place]).ToList();
		return new Summary(runList, list, skipped, malformed);
	}

	// The change from the older half of the runs read (places 1 to runs / 2) to the newer half, as "+12%", or "n/a" when either half has no time of the
	// step or the older half's median is 0. A change that rounds to zero is "+0%", whichever side of it it fell.
	public static string Change(Step step, int runs)
	{
		var older = step.Times.Where(time => time.Place <= runs / 2).Select(time => time.Seconds).ToList();
		var newer = step.Times.Where(time => time.Place > runs / 2).Select(time => time.Seconds).ToList();
		if (older.Count == 0 || newer.Count == 0)
			return "n/a";
		var before = Median(older);
		if (before <= 0)
			return "n/a";
		var percent = Math.Round((Median(newer) - before) / before * 100, MidpointRounding.AwayFromZero);
		if (percent == 0)
			percent = 0; // a negative zero, which would print as "-0"
		return (percent >= 0 ? "+" : "") + percent.ToString("0", CultureInfo.InvariantCulture) + "%";
	}

	public static string Render(Summary summary, string log)
	{
		var text = new StringBuilder();
		var runs = summary.RunList;
		text.Append(CultureInfo.InvariantCulture,
			$"Step timings from {Clean(log)}: the last {summary.Runs} run(s), started {Cell(runs[0].Started)} to {Cell(runs[^1].Started)}.\n");
		var totals = runs.Where(run => run.GateSeconds is not null).Select(run => run.GateSeconds!.Value).ToList();
		if (totals.Count > 0)
		{
			text.Append(CultureInfo.InvariantCulture,
				$"A gate run's median total: {Seconds(Median(totals))} s, over the {totals.Count} run(s) that logged gate.sh steps (its test.sh step holds test.sh's rows).\n");
		}
		if (summary.Runs >= 2)
		{
			var half = summary.Runs / 2;
			text.Append(CultureInfo.InvariantCulture,
				$"The older half ends at commit {Cell(runs[half - 1].Commit)}; the newer half starts at commit {Cell(runs[half].Commit)}.\n");
		}
		text.Append('\n');
		text.Append("| Script | Step | Runs | Median (s) | Slowest (s) | Latest (s) | Change |\n");
		text.Append("| --- | --- | ---: | ---: | ---: | ---: | ---: |\n");
		foreach (var step in summary.Steps)
		{
			text.Append(CultureInfo.InvariantCulture,
				$"| {Cell(step.Script)} | {Cell(step.Name)} | {step.Times.Count} | {Seconds(step.Median)} | {Seconds(step.Slowest)} | {Seconds(step.Latest)} | {Change(step, summary.Runs)} |\n");
		}
		text.Append("\nChange is the newer half of these runs' median against the older half's (n/a with too few runs of the step).\n");
		text.Append(CultureInfo.InvariantCulture,
			$"Skipped steps (a cached pass covered them, or they had nothing to check): {summary.Skipped}. Lines left out as malformed: {summary.Malformed}.\n");
		return text.ToString();
	}

	public static double Median(IEnumerable<double> values)
	{
		var sorted = values.Order().ToList();
		var middle = sorted.Count / 2;
		return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
	}

	// The git folder all of the checkout's worktrees share, for a folder in it, absolute, or null outside a checkout or without git on PATH.
	public static string? GitCommonDir(string folder, string? path)
	{
		var git = Which("git", path);
		if (git is null)
			return null;
		try
		{
			using var process = Process.Start(new ProcessStartInfo(git, ["rev-parse", "--path-format=absolute", "--git-common-dir"])
			{
				WorkingDirectory = folder,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				UseShellExecute = false,
			});
			if (process is null)
				return null;
			process.StandardInput.Close();
			var errors = process.StandardError.ReadToEndAsync();
			var output = process.StandardOutput.ReadToEnd().Trim();
			process.WaitForExit();
			_ = errors.GetAwaiter().GetResult();
			return process.ExitCode == 0 && output.Length > 0 ? output : null;
		}
		catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
		{
			return null;
		}
	}

	// The first executable file of that name in PATH's absolute folders, by its full path, or null. .NET's own lookup of a bare name tries the app's and
	// the current folder first, so a git planted in either would run; a relative or empty PATH entry is skipped for the same reason.
	public static string? Which(string name, string? path)
	{
		foreach (var folder in (path ?? "").Split(Path.PathSeparator))
		{
			if (folder.Length == 0 || !Path.IsPathRooted(folder))
				continue;
			var file = Path.Combine(folder, name);
			if (File.Exists(file) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
				return file;
		}
		return null;
	}

	static bool IsSeconds(string text) =>
		text.Length > 0 && text.All(c => char.IsAsciiDigit(c) || c == '.') && char.IsAsciiDigit(text[0]) && char.IsAsciiDigit(text[^1])
		&& text.Count(c => c == '.') <= 1;

	static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

	// Text from the log or the command line, for a terminal: every control character (C0, DEL, C1), line or paragraph separator and bidi control as a space.
	public static string Clean(string text)
	{
		var clean = new StringBuilder(text.Length);
		foreach (var c in text)
		{
			var hidden = char.IsControl(c) || (int)c is 0x2028 or 0x2029 or 0x061C or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);
			clean.Append(hidden ? ' ' : c);
		}
		return clean.ToString();
	}

	// A table cell: cleaned, and each Markdown special escaped (| among them, so the row keeps its columns), so a name renders as the text it is.
	public static string Cell(string text)
	{
		var cell = new StringBuilder(text.Length);
		foreach (var c in Clean(text))
		{
			if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '#' or '!' or '~' or '&')
				cell.Append('\\');
			cell.Append(c);
		}
		return cell.ToString();
	}
}
