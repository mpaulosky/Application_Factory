// The step timings summary (mpaulosky/Application_Factory#220), which scripts/gate-timings.cs runs and the gate-timings skill reports from: reads the log
// scripts/lib/timings.sh writes for scripts/gate.sh and test.sh, a line a step,
//
//     run  started  branch  commit  script  step  seconds  status
//
// and writes a Markdown table of its last N runs (the runs in the order they first appear), one row per script and step, slowest median first: the run
// count, the median, the slowest and the latest seconds, and the change from the median of the older half of those runs to the newer half's. A skipped step
// (a cached pass covered it) is counted apart, not timed; a line that isn't eight tab-separated fields with a number for its seconds is left out and counted.
// Exit 0 with the table, 1 when there is no log or no step line in it, 2 for a bad argument. The log is --log FILE, else GATE_TIMINGS_LOG, else
// docs/timings/gate-timings.tsv beside the git folder the caller's checkout shares with its worktrees (the primary checkout's); a relative path is
// the caller's, GATE_TIMINGS_CALLER_DIR, which scripts/gate-timings.sh sets since it runs this from scripts/.
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Factory;

static class GateTimings
{
	public const int DefaultRuns = 20;
	const string Usage = "Usage: scripts/gate-timings.sh [--runs N] [--log FILE]";

	// One script and step over the runs read: each time with the run's place (1 for the oldest run read).
	public sealed record Step(string Script, string Name, IReadOnlyList<(int Place, double Seconds)> Times)
	{
		public double Median => GateTimings.Median(Times.Select(time => time.Seconds));
		public double Slowest => Times.Max(time => time.Seconds);
		// The time of the newest run that has the step; a run that logged it twice keeps its last.
		public double Latest => Times.Last(time => time.Place == Times.Max(other => other.Place)).Seconds;
	}

	public sealed record Summary(int Runs, IReadOnlyList<Step> Steps, int Skipped, int Malformed);

	public static int Run(string[] args, TextWriter stdout, TextWriter stderr, Func<string, string?> gitCommonDir, Func<string, string?> environment)
	{
		var caller = environment("GATE_TIMINGS_CALLER_DIR");
		if (string.IsNullOrEmpty(caller))
			caller = Directory.GetCurrentDirectory();
		var runs = DefaultRuns;
		string? log = environment("GATE_TIMINGS_LOG");
		if (string.IsNullOrEmpty(log))
			log = null;
		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--runs" when i + 1 < args.Length:
					if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out runs) || runs < 1)
					{
						stderr.WriteLine($"--runs takes a whole number of 1 or more, not '{args[i + 1]}'.");
						return 2;
					}
					i++;
					break;
				case "--log" when i + 1 < args.Length && args[i + 1].Length > 0:
					log = args[++i];
					break;
				default:
					stderr.WriteLine(Usage);
					return 2;
			}
		}
		if (log is null)
		{
			var common = gitCommonDir(caller);
			if (common is null)
			{
				stderr.WriteLine("Not in a git checkout, so there is no docs/timings/gate-timings.tsv to read: pass --log FILE.");
				return 1;
			}
			log = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(common))!, "docs", "timings", "gate-timings.tsv");
		}
		log = Path.GetFullPath(log, caller);
		string[] lines;
		try
		{
			lines = File.ReadAllLines(log, new UTF8Encoding(false));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			stderr.WriteLine($"No step timings log at {log} yet: a run of scripts/gate.sh writes it.");
			return 1;
		}
		var summary = Summarize(lines, runs);
		if (summary.Runs == 0)
		{
			stderr.WriteLine($"The log at {log} holds no step lines yet.");
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
		var steps = new Dictionary<(string Script, string Name), List<(int, double)>>();
		var keys = new List<(string Script, string Name)>();
		foreach (var fields in rows)
		{
			if (!chosen.TryGetValue(fields[0], out var place))
				continue;
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
			times.Add((place, double.Parse(fields[6], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)));
		}
		var list = keys.Select(key => new Step(key.Script, key.Name, steps[key])).OrderByDescending(step => step.Median).ToList();
		return new Summary(chosen.Count, list, skipped, malformed);
	}

	// The change from the older half of the runs read (places 1 to runs / 2) to the newer half, as "+12%", or "n/a" when either half has no time of the
	// step or the older half's median is 0.
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
		return (percent >= 0 ? "+" : "") + percent.ToString("0", CultureInfo.InvariantCulture) + "%";
	}

	public static string Render(Summary summary, string log)
	{
		var text = new StringBuilder();
		text.Append(CultureInfo.InvariantCulture, $"Step timings from {log}: the last {summary.Runs} run(s).\n\n");
		text.Append("| Script | Step | Runs | Median (s) | Slowest (s) | Latest (s) | Change |\n");
		text.Append("| --- | --- | ---: | ---: | ---: | ---: | ---: |\n");
		foreach (var step in summary.Steps)
		{
			text.Append(CultureInfo.InvariantCulture,
				$"| {Cell(step.Script)} | {Cell(step.Name)} | {step.Times.Count} | {Seconds(step.Median)} | {Seconds(step.Slowest)} | {Seconds(step.Latest)} | {Change(step, summary.Runs)} |\n");
		}
		text.Append("\nChange is the newer half of these runs' median against the older half's (n/a with too few runs of the step).\n");
		text.Append(CultureInfo.InvariantCulture,
			$"Skipped steps (a cached pass covered them): {summary.Skipped}. Lines left out as malformed: {summary.Malformed}.\n");
		return text.ToString();
	}

	public static double Median(IEnumerable<double> values)
	{
		var sorted = values.Order().ToList();
		var middle = sorted.Count / 2;
		return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
	}

	// The git folder all of the checkout's worktrees share, for a folder in it, absolute, or null outside a checkout or without git.
	public static string? GitCommonDir(string folder)
	{
		try
		{
			using var git = Process.Start(new ProcessStartInfo("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"])
			{
				WorkingDirectory = folder,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				UseShellExecute = false,
			});
			if (git is null)
				return null;
			git.StandardInput.Close();
			var errors = git.StandardError.ReadToEndAsync();
			var output = git.StandardOutput.ReadToEnd().Trim();
			git.WaitForExit();
			_ = errors.GetAwaiter().GetResult();
			return git.ExitCode == 0 && output.Length > 0 ? output : null;
		}
		catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
		{
			return null;
		}
	}

	static bool IsSeconds(string text) =>
		text.Length > 0 && text.All(c => char.IsAsciiDigit(c) || c == '.') && char.IsAsciiDigit(text[0]) && char.IsAsciiDigit(text[^1])
		&& text.Count(c => c == '.') <= 1;

	static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

	// A table cell: a | escaped, so the row keeps its columns.
	static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
