// The Factory's scripts/GateTimings.cs, the step timings summary (mpaulosky/Application_Factory#220): on fixture logs, with git and the environment
// replaced. scripts/tests/timings.test.sh runs scripts/gate-timings.sh itself, end to end.
public class GateTimingsTests
{
	static string Line(string run, string script, string step, string seconds, string status = "passed", string started = "2026-01-01T00:00:00Z", string commit = "c") =>
		$"{run}\t{started}\tb\t{commit}\t{script}\t{step}\t{seconds}\t{status}";

	static readonly string[] Fixture =
	[
		Line("r1", "gate.sh", "lint", "10.0", started: "2026-01-01T00:00:00Z", commit: "aaaa1111"),
		Line("r1", "test.sh", "Hook tests", "300.0"),
		Line("r2", "gate.sh", "lint", "20.0", started: "2026-01-02T00:00:00Z", commit: "bbbb2222"),
		Line("r2", "test.sh", "Hook tests", "200.0", "failed"),
		Line("r2", "gate.sh", "test.sh", "1.0", "skipped"),
		"a line that is not one",
		Line("r3", "gate.sh", "lint", "30.0", started: "2026-01-03T00:00:00Z", commit: "cccc3333"),
		Line("r3", "gate.sh", "a|b", "long"),
		Line("r4", "gate.sh", "lint", "40.0", started: "2026-01-04T00:00:00Z", commit: "dddd4444"),
		Line("r4", "gate.sh", "a|b", "5.0"),
	];

	static (int Code, string Out, string Err) Run(string[] args, string[]? log = null, IDictionary<string, string>? env = null, string? common = "/repo/.git")
	{
		var dir = Directory.CreateTempSubdirectory("gate-timings-test.").FullName;
		var variables = new Dictionary<string, string>(env ?? new Dictionary<string, string>());
		variables.TryAdd("GATE_TIMINGS_CALLER_DIR", dir);
		if (log is not null)
			File.WriteAllLines(Path.Combine(dir, "log.tsv"), log);
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		try
		{
			var code = GateTimings.Run(args, stdout, stderr, (_, _) => common, name => variables.GetValueOrDefault(name));
			return (code, stdout.ToString(), stderr.ToString());
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	static string Table(params string[] extra) => Run(["--log", "log.tsv", .. extra], Fixture).Out;

	static GateTimings.Step OneStep(params string[] lines) => GateTimings.Summarize(lines, 20).Steps.Single();

	static string ChangeOf(params string[] lines)
	{
		var summary = GateTimings.Summarize(lines, 20);
		return GateTimings.Change(summary.Steps.Single(), summary.Runs);
	}

	[Fact]
	public void A_row_has_its_runs_median_slowest_latest_and_the_change_between_the_halves()
	{
		// lint: 10, 20 in the older half (median 15), 30, 40 in the newer (35): +133%.
		Assert.Contains("| gate.sh | lint | 4 | 25.0 | 40.0 | 40.0 | +133% |\n", Table());
	}

	[Fact]
	public void A_failed_steps_time_counts_and_a_step_only_in_the_older_half_has_no_change() =>
		Assert.Contains("| test.sh | Hook tests | 2 | 250.0 | 300.0 | 200.0 | n/a |\n", Table());

	[Fact]
	public void Rows_go_slowest_median_first()
	{
		var rows = Table().Split('\n').Where(line => line.StartsWith("| gate.sh", StringComparison.Ordinal) || line.StartsWith("| test.sh", StringComparison.Ordinal)).ToList();
		Assert.Equal(["Hook tests", "lint", "a\\|b"], rows.Select(row => row.Split(" | ")[1]));
	}

	[Fact]
	public void A_bar_in_a_name_is_escaped_so_the_row_keeps_its_columns() =>
		Assert.Contains("| gate.sh | a\\|b | 1 | 5.0 | 5.0 | 5.0 | n/a |\n", Table());

	[Fact]
	public void Skipped_steps_and_malformed_lines_are_counted_apart()
	{
		var table = Table();
		Assert.Contains("the last 4 run(s)", table);
		Assert.Contains("Skipped steps (a cached pass covered them, or they had nothing to check): 1. Lines left out as malformed: 2.\n", table);
	}

	[Fact]
	public void The_header_gives_the_runs_dates_a_gate_runs_median_total_and_the_commits_where_the_halves_meet()
	{
		var table = Table();
		Assert.Contains("the last 4 run(s), started 2026-01-01T00:00:00Z to 2026-01-04T00:00:00Z.\n", table);
		// gate.sh's lines per run: 10; 20 + 1 (a skipped step still took its time); 30; 40 + 5. Their median: (21 + 30) / 2.
		Assert.Contains("A gate run's median total: 25.5 s, over the 4 run(s) that logged gate.sh steps", table);
		Assert.Contains("The older half ends at commit bbbb2222; the newer half starts at commit cccc3333.\n", table);
	}

	[Fact]
	public void One_run_has_no_halves_and_a_run_without_gate_sh_has_no_total()
	{
		var table = Run(["--log", "log.tsv"], [Line("r1", "test.sh", "Hook tests", "3.0")]).Out;
		Assert.DoesNotContain("older half ends", table);
		Assert.DoesNotContain("median total", table);
		Assert.Contains("| test.sh | Hook tests | 1 | 3.0 | 3.0 | 3.0 | n/a |\n", table);
	}

	[Fact]
	public void Runs_N_reads_only_the_last_N_runs()
	{
		var table = Table("--runs", "2");
		Assert.Contains("| gate.sh | lint | 2 | 35.0 | 40.0 | 40.0 | +33% |\n", table);
		Assert.DoesNotContain("Hook tests", table);
		Assert.Contains("the last 2 run(s)", table);
	}

	[Fact]
	public void An_odd_run_count_puts_the_middle_run_in_the_newer_half()
	{
		// --runs 3 reads r2, r3, r4: the older half is r2 alone (3 / 2 = 1), the newer r3 and r4: 20 to 35 is +75%.
		Assert.Contains("| gate.sh | lint | 3 | 30.0 | 40.0 | 40.0 | +75% |\n", Table("--runs", "3"));
	}

	[Fact]
	public void Runs_are_ordered_by_first_appearance_not_by_name()
	{
		// z1 is older than a2 because it appears first; the newer half is a2's.
		Assert.Equal("+100%", ChangeOf(Line("z1", "g", "s", "10.0"), Line("a2", "g", "s", "20.0")));
		Assert.Equal(20.0, OneStep(Line("z1", "g", "s", "10.0"), Line("a2", "g", "s", "20.0")).Latest);
	}

	[Fact]
	public void A_step_logged_twice_in_a_run_counts_once_its_last()
	{
		var step = OneStep(Line("r1", "g", "s", "10.0"), Line("r1", "g", "s", "50.0"), Line("r2", "g", "s", "30.0"));
		Assert.Equal(2, step.Times.Count);
		Assert.Equal(40.0, step.Median);
		Assert.Equal(30.0, step.Latest);
	}

	[Fact]
	public void A_fall_is_a_minus_and_an_older_median_of_zero_has_none()
	{
		Assert.Equal("-25%", ChangeOf(Line("r1", "g", "s", "40.0"), Line("r2", "g", "s", "30.0")));
		Assert.Equal("n/a", ChangeOf(Line("r1", "g", "s", "0.0"), Line("r2", "g", "s", "3.0")));
	}

	[Theory]
	[InlineData("100.0", "99.8", "+0%")] // -0.2% rounds to a negative zero
	[InlineData("100.0", "100.2", "+0%")]
	[InlineData("100.0", "100.0", "+0%")]
	[InlineData("100.0", "99.4", "-1%")] // -0.6%
	[InlineData("100.0", "100.5", "+1%")] // a half rounds away from zero
	public void A_change_that_rounds_to_zero_is_plus_zero(string before, string after, string change) =>
		Assert.Equal(change, ChangeOf(Line("r1", "g", "s", before), Line("r2", "g", "s", after)));

	[Theory]
	[InlineData("1.5", true)]
	[InlineData("12", true)]
	[InlineData("", false)]
	[InlineData(".5", false)]
	[InlineData("5.", false)]
	[InlineData("1.2.3", false)]
	[InlineData("-1", false)]
	[InlineData("1e3", false)]
	public void Seconds_must_be_a_plain_decimal(string seconds, bool kept)
	{
		var summary = GateTimings.Summarize([Line("r1", "g", "s", seconds)], 20);
		Assert.Equal(kept ? 0 : 1, summary.Malformed);
		Assert.Equal(kept ? 1 : 0, summary.Steps.Count);
	}

	[Fact]
	public void A_line_without_eight_fields_is_malformed()
	{
		var summary = GateTimings.Summarize([Line("r1", "g", "s", "1.0") + "\textra", "r1\tonly"], 20);
		Assert.Equal(2, summary.Malformed);
		Assert.Equal(0, summary.Runs);
	}

	[Fact]
	public void The_median_of_an_even_count_is_the_mean_of_the_middle_two()
	{
		Assert.Equal(2.5, GateTimings.Median([4.0, 1.0, 3.0, 2.0]));
		Assert.Equal(3.0, GateTimings.Median([5.0, 3.0, 1.0]));
	}

	[Fact]
	public void Names_are_cleaned_of_controls_line_separators_and_bidi_marks_and_their_markdown_escaped()
	{
		var name = "a\u001b]0;t\u0007 \u009b2J \u2028 \u202Eevil\u2066 *b* `c` [d](e) <f> #g _h_ \\i";
		var table = Run(["--log", "log.tsv"], [Line("r1", "gate.sh", name, "1.0", started: "2026\u001b[2J", commit: "c\u202E")]).Out;
		Assert.DoesNotContain(table, c => char.IsControl(c) && c != '\n');
		Assert.DoesNotContain((char)0x2028, table);
		Assert.DoesNotContain((char)0x202E, table);
		Assert.DoesNotContain((char)0x2066, table);
		var row = table.Split('\n').Single(line => line.StartsWith("| gate.sh |", StringComparison.Ordinal));
		Assert.Equal("| gate.sh | a \\]0;t   2J    evil  \\*b\\* \\`c\\` \\[d\\](e) \\<f\\> \\#g \\_h\\_ \\\\i | 1 | 1.0 | 1.0 | 1.0 | n/a |", row);
		Assert.Contains("started 2026 \\[2J to 2026 \\[2J.", table);
	}

	[Fact]
	public void The_log_path_printed_is_cleaned()
	{
		var result = Run(["--log", "bad\u001b[2Jname.tsv"]);
		Assert.Equal(1, result.Code);
		Assert.DoesNotContain('\u001b', result.Err);
	}

	[Fact]
	public void The_log_is_GATE_TIMINGS_LOG_when_log_is_not_given()
	{
		var dir = Directory.CreateTempSubdirectory("gate-timings-env.").FullName;
		try
		{
			File.WriteAllLines(Path.Combine(dir, "env.tsv"), Fixture);
			var result = Run([], env: new Dictionary<string, string> { ["GATE_TIMINGS_LOG"] = Path.Combine(dir, "env.tsv") });
			Assert.Equal(0, result.Code);
			Assert.Contains($"from {Path.Combine(dir, "env.tsv")}:", result.Out);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void Log_wins_over_GATE_TIMINGS_LOG() =>
		Assert.Equal(0, Run(["--log", "log.tsv"], Fixture, new Dictionary<string, string> { ["GATE_TIMINGS_LOG"] = "/nowhere/env.tsv" }).Code);

	[Fact]
	public void An_empty_GATE_TIMINGS_LOG_is_unset()
	{
		var result = Run([], env: new Dictionary<string, string> { ["GATE_TIMINGS_LOG"] = "" }, common: "/primary/.git");
		Assert.Contains("No step timings log at /primary/docs/timings/gate-timings.tsv yet", result.Err);
	}

	[Fact]
	public void A_relative_GATE_TIMINGS_LOG_is_refused_with_exit_2()
	{
		var result = Run([], Fixture, new Dictionary<string, string> { ["GATE_TIMINGS_LOG"] = "log.tsv" });
		Assert.Equal(2, result.Code);
		Assert.Equal("", result.Out);
		Assert.Contains("GATE_TIMINGS_LOG must be an absolute path", result.Err);
	}

	[Fact]
	public void The_default_log_is_beside_the_shared_git_folder()
	{
		var result = Run([], common: "/primary/.git/");
		Assert.Equal(1, result.Code);
		Assert.Contains("No step timings log at /primary/docs/timings/gate-timings.tsv yet: a run of scripts/gate.sh writes it.", result.Err);
	}

	[Theory]
	[InlineData("/srv/bare.git")] // a bare clone, or a separate git folder
	[InlineData("/super/.git/modules/sub")] // a submodule
	public void A_git_folder_that_isnt_a_dot_git_folder_has_no_default_log(string common)
	{
		var result = Run([], common: common);
		Assert.Equal(1, result.Code);
		Assert.Contains("isn't a .git folder", result.Err);
		Assert.DoesNotContain("docs/timings/gate-timings.tsv yet", result.Err);
	}

	[Fact]
	public void Outside_a_checkout_with_no_log_named_it_exits_1()
	{
		var result = Run([], common: null);
		Assert.Equal(1, result.Code);
		Assert.Contains("pass --log FILE", result.Err);
	}

	[Theory]
	[InlineData("--runs", "0")]
	[InlineData("--runs", "x")]
	[InlineData("--runs", "+3")]
	[InlineData("--runs")]
	[InlineData("--log")]
	[InlineData("--log", "")]
	[InlineData("--bogus")]
	public void A_bad_argument_exits_2_and_writes_nothing_to_stdout(params string[] args)
	{
		// The valid option first, so a bad one is last and a missing value really is missing.
		var result = Run(["--log", "log.tsv", .. args], Fixture);
		Assert.Equal(2, result.Code);
		Assert.Equal("", result.Out);
		Assert.NotEqual("", result.Err);
	}

	[Fact]
	public void Help_prints_the_usage_and_reads_no_log()
	{
		var result = Run(["--help"], common: null);
		Assert.Equal(0, result.Code);
		Assert.Equal("Usage: scripts/gate-timings.sh [--runs N] [--log FILE]\n", result.Out.Replace("\r\n", "\n"));
		Assert.Equal("", result.Err);
	}

	[Fact]
	public void A_missing_log_exits_1_saying_a_gate_run_writes_it()
	{
		var result = Run(["--log", "none.tsv"]);
		Assert.Equal(1, result.Code);
		Assert.Contains("a run of scripts/gate.sh writes it", result.Err);
	}

	[Fact]
	public void A_log_that_is_a_folder_exits_1()
	{
		var dir = Directory.CreateTempSubdirectory("gate-timings-folder.").FullName;
		try
		{
			var result = Run(["--log", dir]);
			Assert.Equal(1, result.Code);
			Assert.Contains("No step timings log at", result.Err);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void A_log_with_no_step_lines_exits_1()
	{
		var result = Run(["--log", "log.tsv"], ["junk"]);
		Assert.Equal(1, result.Code);
		Assert.Contains("holds no step lines", result.Err);
	}

	[Fact]
	public void An_unexpected_exception_is_a_message_and_exit_1()
	{
		var stderr = new StringWriter();
		var code = GateTimings.Guarded(() => throw new InvalidOperationException("boom\u001b[2J"), stderr);
		Assert.Equal(1, code);
		Assert.Equal("gate-timings failed: InvalidOperationException: boom [2J\n", stderr.ToString().Replace("\r\n", "\n"));
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	public void Which_takes_only_an_executable_in_an_absolute_PATH_folder()
	{
		var root = Directory.CreateTempSubdirectory("gate-timings-which.").FullName;
		var previous = Directory.GetCurrentDirectory();
		try
		{
			foreach (var folder in new[] { "cwd", "plain", "real" })
				Directory.CreateDirectory(Path.Combine(root, folder));
			File.WriteAllText(Path.Combine(root, "cwd", "git"), "#!/bin/sh\n");
			File.SetUnixFileMode(Path.Combine(root, "cwd", "git"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
			File.WriteAllText(Path.Combine(root, "plain", "git"), "not executable\n");
			File.WriteAllText(Path.Combine(root, "real", "git"), "#!/bin/sh\n");
			File.SetUnixFileMode(Path.Combine(root, "real", "git"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
			Directory.SetCurrentDirectory(Path.Combine(root, "cwd"));
			// ".", a relative folder and an empty entry all name the current folder; a file without an execute bit isn't a command.
			var path = string.Join(Path.PathSeparator, ".", "cwd", "", Path.Combine(root, "plain"), Path.Combine(root, "real"));
			Assert.Equal(Path.Combine(root, "real", "git"), GateTimings.Which("git", path));
			Assert.Null(GateTimings.Which("git", "."));
			Assert.Null(GateTimings.Which("git", null));
		}
		finally
		{
			Directory.SetCurrentDirectory(previous);
			Directory.Delete(root, true);
		}
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	public void GitCommonDir_runs_git_from_PATH_never_one_in_the_folder_it_asks_about()
	{
		var root = Directory.CreateTempSubdirectory("gate-timings-git.").FullName;
		try
		{
			// A fake git in the folder it runs in would print this; with no git on PATH there is no answer at all.
			File.WriteAllText(Path.Combine(root, "git"), "#!/bin/sh\necho /planted/.git\n");
			File.SetUnixFileMode(Path.Combine(root, "git"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
			Assert.Null(GateTimings.GitCommonDir(root, ""));
			Assert.Null(GateTimings.GitCommonDir(root, "."));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
