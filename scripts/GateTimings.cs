// The step timings summary (mpaulosky/Application_Factory#220), which scripts/gate-timings.cs runs and the gate-timings skill reports from: reads the log
// scripts/lib/timings.sh writes for scripts/gate.sh and test.sh, a line a step,
//
//     run  started  branch  commit  script  step  seconds  status
//
// and writes a Markdown table of its last N runs (the runs in the order they started, a run's start its earliest line's, then the order they first
// appear on a tie: two gates in two worktrees append to one log at once), one row per script and step, slowest median first: the run count, the median,
// the slowest and the latest seconds, and the change from the median of the older half of those runs to the newer half's. A run that logged a step
// twice counts its last. A skipped step (a cached pass covered it, or there was nothing for it to check) is counted apart, not timed; a line that isn't
// eight tab-separated fields, with a start in the writer's form (2026-01-31T12:00:00Z) and a plain decimal for its seconds (at most 9 digits before the
// point and 3 after), is left out and counted.
// Above the table: the runs' first and last start, the median total of the full gate runs among them, and the commits where the halves meet, so a
// report needs nothing from the raw log. A full gate run logged the self part's step, test.sh and the Sandcastle check, ran the first two rather than
// skipping them on a cached pass, and failed none of its gate.sh steps; a --quick run, one part alone, a cached run, and a run that failed, was killed
// or is still going aren't. Its total is its gate.sh lines' seconds, a skipped Sandcastle check's included. Every name it prints is cleaned (each
// control and format character, line and paragraph separator, private-use and unassigned character and lone surrogate, and the few others that show
// as nothing, the variation selectors and the Hangul fillers among them, as a space: so the bidi controls, zero-width and tag characters too), cut to
// 120 characters in a table cell, and Markdown-escaped, since a line can come from any branch's run or any file.
//
// The log is --log FILE, else GATE_TIMINGS_LOG (an absolute path; a relative one is refused, as the writer refuses it), else
// docs/timings/gate-timings.tsv beside the .git folder that the checkout scripts/gate-timings.sh is in shares with its worktrees (the primary checkout's),
// as scripts/lib/timings.sh writes it: the wrapper names its folder in GATE_TIMINGS_HOME (the caller's folder without it). A relative --log is the
// caller's, GATE_TIMINGS_CALLER_DIR, which the wrapper sets since it runs this from scripts/. git is started by its full path from PATH's absolute
// folders, never from the current or the app's folder.
// Exit 0 with the table or the usage; 1, saying why on stderr, when the default log doesn't exist yet or a log holds no step line (the cases a gate run
// mends), when a log --log or GATE_TIMINGS_LOG names doesn't exist, the log can't be read, git isn't on PATH, the folder isn't a checkout or git can't
// say which it is in (giving git's own words), or on an unexpected error ("gate-timings failed: ..."); 2 for a bad argument.
// --help prints the usage and reads nothing (scripts/gate-timings.sh builds the app with it).
using System.Diagnostics;
using System.Globalization;
using System.Text;

static class GateTimings
{
	public const int DefaultRuns = 20;
	const string Usage = "Usage: scripts/gate-timings.sh [--runs N] [--log FILE] [--help]";

	// One script and step over the runs read: one time per run, with the run's place (1 for the oldest run read).
	public sealed record Step(string Script, string Name, IReadOnlyList<(int Place, double Seconds)> Times)
	{
		public double Median => GateTimings.Median(Times.Select(time => time.Seconds));
		public double Slowest => Times.Max(time => time.Seconds);
		public double Latest => Times.MaxBy(time => time.Place).Seconds;
	}

	// One run read: its earliest line's start, its first line's commit, and the sum of its gate.sh lines' seconds when it was a full gate run (null otherwise).
	public sealed record RunInfo(string Started, string Commit, double? GateSeconds);

	// The gate.sh steps only a full gate run logs all of: the self part's first, the template part's test.sh, and the Sandcastle check, its last.
	// scripts/tests/timings.test.sh checks that scripts/gate.sh still logs each by that name.
	public static readonly string[] FullGateSteps = ["scripts/tests/gate.test.sh", "test.sh", "Sandcastle check"];

	// Of those, the ones a full gate run ran rather than skipped: a cached pass of test.sh makes a run short, not full. The Sandcastle check is skipped
	// on a branch that changed nothing it reads, so a run that skipped it is still full.
	static readonly string[] TimedGateSteps = ["scripts/tests/gate.test.sh", "test.sh"];

	// The most characters of a name a table cell shows; a longer one is cut, ending "...".
	public const int CellRunes = 120;

	// What git said of a folder: the shared git folder (null when git gave none), whether a git was found to ask at all, and, when it gave none, git's
	// first line on stderr or why it couldn't start.
	public sealed record GitAnswer(string? CommonDir, bool GitFound, string? Reason = null);

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
		return RunFlushed(args, stdout, stderr, GitCommonDir, Environment.GetEnvironmentVariable);
	}

	// Run, with stdout flushed inside the guard, so a write that fails at the end (a full disk) is a message and exit 1 too, and the writer's dispose
	// has nothing left to write.
	public static int RunFlushed(string[] args, TextWriter stdout, TextWriter stderr, Func<string, string?, GitAnswer> gitCommonDir, Func<string, string?> environment) =>
		Guarded(() =>
		{
			var code = Run(args, stdout, stderr, gitCommonDir, environment);
			stdout.Flush();
			return code;
		}, stderr);

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

	public static int Run(string[] args, TextWriter stdout, TextWriter stderr, Func<string, string?, GitAnswer> gitCommonDir, Func<string, string?> environment)
	{
		var caller = environment("GATE_TIMINGS_CALLER_DIR");
		if (string.IsNullOrEmpty(caller))
			caller = Directory.GetCurrentDirectory();
		var runs = DefaultRuns;
		string? log = null;
		var named = false; // the log is one --log or GATE_TIMINGS_LOG names, not the default
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
					named = true;
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
			var variable = environment("GATE_TIMINGS_LOG");
			if (!string.IsNullOrEmpty(variable))
			{
				if (!Path.IsPathRooted(variable))
				{
					stderr.WriteLine($"GATE_TIMINGS_LOG must be an absolute path, not '{Clean(variable)}' (the gate's run logs nothing to a relative one).");
					return 2;
				}
				log = variable;
				named = true;
			}
		}
		if (log is null)
		{
			var home = environment("GATE_TIMINGS_HOME");
			var folder = string.IsNullOrEmpty(home) ? caller : home;
			var answer = gitCommonDir(folder, environment("PATH"));
			if (!answer.GitFound)
			{
				stderr.WriteLine("git isn't in any of PATH's absolute folders, so the default log can't be found: pass --log FILE.");
				return 1;
			}
			var common = answer.CommonDir;
			if (common is null)
			{
				if (answer.Reason is null || answer.Reason.Contains("not a git repository", StringComparison.Ordinal))
					stderr.WriteLine($"{Clean(folder)} isn't in a git checkout, so there is no docs/timings/gate-timings.tsv to read: pass --log FILE.");
				else
					stderr.WriteLine($"git can't say which checkout {Clean(folder)} is in ({Clean(answer.Reason)}), so the default log can't be found: pass --log FILE.");
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
		catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
		{
			stderr.WriteLine(named
				? $"No step timings log at {Clean(log)}: check the path --log or GATE_TIMINGS_LOG gives."
				: $"No step timings log at {Clean(log)} yet: a run of scripts/gate.sh writes it.");
			return 1;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			var what = Directory.Exists(log) ? "it is a folder" : Clean(error.Message);
			stderr.WriteLine($"Can't read the step timings log at {Clean(log)}: {what}.");
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
			if (fields.Length != 8 || !IsStarted(fields[1]) || !IsSeconds(fields[6]))
			{
				malformed++;
				continue;
			}
			rows.Add(fields);
		}
		// Each run's earliest start (in the writer's form, ISO 8601 UTC, so ordinal order is time order), then its first line's place, for a tie.
		var order = rows.Select((fields, index) => (Run: fields[0], Started: fields[1], Index: index))
			.GroupBy(row => row.Run)
			.Select(group => (Run: group.Key, Started: group.Select(row => row.Started).Min(StringComparer.Ordinal)!, First: group.First().Index))
			.OrderBy(run => run.Started, StringComparer.Ordinal).ThenBy(run => run.First)
			.Select(run => run.Run).ToList();
		var chosen = order.Skip(Math.Max(0, order.Count - runs)).Select((run, index) => (run, index)).ToDictionary(pair => pair.run, pair => pair.index + 1);
		var skipped = 0;
		// Per script and step, per run place, the last time logged.
		var steps = new Dictionary<(string Script, string Name), SortedDictionary<int, double>>();
		var keys = new List<(string Script, string Name)>();
		var info = new Dictionary<int, RunInfo>();
		var gateSeconds = new Dictionary<int, double>();
		var gateSteps = new Dictionary<int, HashSet<string>>();
		var gateFailed = new HashSet<int>();
		var gateSkipped = new Dictionary<int, HashSet<string>>();
		foreach (var fields in rows)
		{
			if (!chosen.TryGetValue(fields[0], out var place))
				continue;
			var seconds = double.Parse(fields[6], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
			if (!info.TryGetValue(place, out var known))
				info[place] = new RunInfo(fields[1], fields[3], null);
			else if (string.CompareOrdinal(fields[1], known.Started) < 0)
				info[place] = known with { Started = fields[1] };
			if (fields[4] == "gate.sh")
			{
				gateSeconds[place] = gateSeconds.GetValueOrDefault(place) + seconds;
				if (!gateSteps.TryGetValue(place, out var seen))
					gateSteps[place] = seen = [];
				seen.Add(fields[5]);
				if (fields[7] == "failed")
					gateFailed.Add(place);
				if (fields[7] == "skipped")
				{
					if (!gateSkipped.TryGetValue(place, out var passed))
						gateSkipped[place] = passed = [];
					passed.Add(fields[5]);
				}
			}
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
		var runList = Enumerable.Range(1, chosen.Count).Select(place =>
			gateSteps.TryGetValue(place, out var seen) && FullGateSteps.All(seen.Contains) && !gateFailed.Contains(place)
				&& !(gateSkipped.TryGetValue(place, out var passed) && TimedGateSteps.Any(passed.Contains))
				? info[place] with { GateSeconds = gateSeconds[place] }
				: info[place]).ToList();
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
				$"A full gate run's median total: {Seconds(Median(totals))} s, over the {totals.Count} full gate run(s) of these {summary.Runs} (its test.sh step holds test.sh's rows).\n");
		}
		else
		{
			text.Append("No full gate run among these (one that ran the self part and test.sh, not a cached pass of them, and logged the Sandcastle check, none of its steps failed), so no total.\n");
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

	// The git folder all of the checkout's worktrees share, for a folder in it, absolute (null outside a checkout, or when git fails, with git's first line
	// on stderr or why it didn't start), and whether git was on PATH at all.
	public static GitAnswer GitCommonDir(string folder, string? path)
	{
		var git = Which("git", path);
		if (git is null)
			return new GitAnswer(null, false);
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
				return new GitAnswer(null, true, "git didn't start");
			process.StandardInput.Close();
			var errors = process.StandardError.ReadToEndAsync();
			var output = process.StandardOutput.ReadToEnd().Trim();
			process.WaitForExit();
			var said = errors.GetAwaiter().GetResult();
			if (process.ExitCode == 0 && output.Length > 0)
				return new GitAnswer(output, true);
			var first = said.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
			return new GitAnswer(null, true, first ?? $"git exited {process.ExitCode}");
		}
		catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
		{
			return new GitAnswer(null, true, error.Message);
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

	// A plain decimal: 1 to 9 digits, then optionally a point and 1 to 3 more (the writer prints a tenth). A longer one is malformed, not Infinity.
	public static bool IsSeconds(string text)
	{
		var point = text.IndexOf('.');
		var whole = point < 0 ? text : text[..point];
		var part = point < 0 ? "0" : text[(point + 1)..];
		return whole.Length is >= 1 and <= 9 && part.Length is >= 1 and <= 3 && whole.All(char.IsAsciiDigit) && part.All(char.IsAsciiDigit);
	}

	// A start in the writer's form, date -u +%Y-%m-%dT%H:%M:%SZ: 2026-01-31T12:00:00Z. Any other text would sort among the starts as no time does.
	public static bool IsStarted(string text)
	{
		const string form = "0000-00-00T00:00:00Z";
		if (text.Length != form.Length)
			return false;
		for (var i = 0; i < form.Length; i++)
		{
			if (form[i] == '0' ? !char.IsAsciiDigit(text[i]) : text[i] != form[i])
				return false;
		}
		return true;
	}

	static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

	// Text from the log or the command line, for a terminal and an agent: every rune that is a control (C0, DEL, C1), a format character (the bidi
	// controls, zero-width characters, the byte-order mark, the soft hyphen, the tag characters), a line or paragraph separator, private-use or unassigned,
	// a lone surrogate, and the few others that show as nothing (Blank), as one space each. What is left is text a person sees as it is.
	public static string Clean(string text)
	{
		var clean = new StringBuilder(text.Length);
		for (var i = 0; i < text.Length;)
		{
			if (Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var used) != System.Buffers.OperationStatus.Done)
			{
				clean.Append(' ');
				i += Math.Max(used, 1);
				continue;
			}
			i += used;
			var hidden = Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
				or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate
				|| Blank(rune.Value);
			if (hidden)
				clean.Append(' ');
			else
				clean.Append(rune.ToString());
		}
		return clean.ToString();
	}

	// The runs outside those categories that still show as nothing and take no room: the combining grapheme joiner, the Hangul fillers, the Khmer
	// inherent vowels, the Mongolian free variation selectors, the braille blank and both blocks of variation selectors.
	static bool Blank(int value) => value is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or (>= 0x180B and <= 0x180D) or 0x180F or 0x2800 or 0x3164
		or (>= 0xFE00 and <= 0xFE0F) or 0xFFA0 or (>= 0xE0100 and <= 0xE01EF);

	// A table cell: cleaned, cut to CellRunes characters (a longer name ends "..."), and each Markdown special escaped (| among them, so the row keeps
	// its columns), so a name renders as the text it is.
	public static string Cell(string text)
	{
		var clean = Clean(text);
		var runes = clean.EnumerateRunes().ToList();
		if (runes.Count > CellRunes)
			clean = string.Concat(runes.Take(CellRunes).Select(rune => rune.ToString())) + "...";
		var cell = new StringBuilder(clean.Length);
		foreach (var c in clean)
		{
			if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '#' or '!' or '~' or '&')
				cell.Append('\\');
			cell.Append(c);
		}
		return cell.ToString();
	}
}
