using Factory;

// The Factory's scripts/GateTimings.cs, the step timings summary (mpaulosky/Application_Factory#220): on fixture logs, with git and the environment
// replaced. scripts/tests/timings.test.sh runs scripts/gate-timings.sh itself, end to end.
public class GateTimingsTests
{
	static string Line(string run, string script, string step, string seconds, string status = "passed") =>
		$"{run}\t2026-01-01T00:00:00Z\tb\tc\t{script}\t{step}\t{seconds}\t{status}";

	static readonly string[] Fixture =
	[
		Line("r1", "gate.sh", "lint", "10.0"),
		Line("r1", "test.sh", "Hook tests", "300.0"),
		Line("r2", "gate.sh", "lint", "20.0"),
		Line("r2", "test.sh", "Hook tests", "200.0", "failed"),
		Line("r2", "gate.sh", "test.sh", "1.0", "skipped"),
		"a line that is not one",
		Line("r3", "gate.sh", "lint", "30.0"),
		Line("r3", "gate.sh", "a|b", "long"),
		Line("r4", "gate.sh", "lint", "40.0"),
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
			var code = GateTimings.Run(args, stdout, stderr, _ => common, name => variables.GetValueOrDefault(name));
			return (code, stdout.ToString(), stderr.ToString());
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	static string Table(params string[] extra) => Run(["--log", "log.tsv", .. extra], Fixture).Out;

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
		Assert.Contains("the last 4 run(s).", table);
		Assert.Contains("Skipped steps (a cached pass covered them): 1. Lines left out as malformed: 2.\n", table);
	}

	[Fact]
	public void Runs_N_reads_only_the_last_N_runs()
	{
		var table = Table("--runs", "2");
		Assert.Contains("| gate.sh | lint | 2 | 35.0 | 40.0 | 40.0 | +33% |\n", table);
		Assert.DoesNotContain("Hook tests", table);
		Assert.Contains("the last 2 run(s).", table);
	}

	[Fact]
	public void Runs_are_ordered_by_first_appearance_not_by_name()
	{
		// z1 is older than a2 because it appears first; the newer half is a2's.
		var summary = GateTimings.Summarize([Line("z1", "g", "s", "10.0"), Line("a2", "g", "s", "20.0")], 20);
		Assert.Equal("+100%", GateTimings.Change(summary.Steps.Single(), summary.Runs));
		Assert.Equal(20.0, summary.Steps.Single().Latest);
	}

	[Fact]
	public void A_fall_is_a_minus_and_an_older_median_of_zero_has_none()
	{
		var down = GateTimings.Summarize([Line("r1", "g", "s", "40.0"), Line("r2", "g", "s", "30.0")], 20);
		Assert.Equal("-25%", GateTimings.Change(down.Steps.Single(), down.Runs));
		var zero = GateTimings.Summarize([Line("r1", "g", "s", "0.0"), Line("r2", "g", "s", "3.0")], 20);
		Assert.Equal("n/a", GateTimings.Change(zero.Steps.Single(), zero.Runs));
	}

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
	public void The_default_log_is_beside_the_shared_git_folder()
	{
		var result = Run([], common: "/primary/.git/");
		Assert.Equal(1, result.Code);
		Assert.Contains("No step timings log at /primary/docs/timings/gate-timings.tsv yet: a run of scripts/gate.sh writes it.", result.Err);
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
		var result = Run([.. args, "--log", "log.tsv"], Fixture);
		Assert.Equal(2, result.Code);
		Assert.Equal("", result.Out);
		Assert.NotEqual("", result.Err);
	}

	[Fact]
	public void A_missing_log_exits_1_saying_a_gate_run_writes_it()
	{
		var result = Run(["--log", "none.tsv"]);
		Assert.Equal(1, result.Code);
		Assert.Contains("a run of scripts/gate.sh writes it", result.Err);
	}

	[Fact]
	public void A_log_with_no_step_lines_exits_1()
	{
		var result = Run(["--log", "log.tsv"], ["junk"]);
		Assert.Equal(1, result.Code);
		Assert.Contains("holds no step lines", result.Err);
	}
}
