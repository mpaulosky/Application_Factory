// The Factory's scripts/GateTimings.cs, the step timings summary (mpaulosky/Application_Factory#220): on fixture logs, with git and the environment
// replaced. scripts/tests/timings.test.sh runs scripts/gate-timings.sh itself, end to end.
public class GateTimingsTests
{
	static string Line(string run, string script, string step, string seconds, string status = "passed", string started = "2026-01-01T00:00:00Z", string commit = "c") =>
		$"{run}\t{started}\tb\t{commit}\t{script}\t{step}\t{seconds}\t{status}";

	static readonly string[] Fixture =
	[
		Line("r1", "gate.sh", "lint", "10.0", started: "2026-01-01T00:00:00Z", commit: "aaaa1111"),
		Line("r1", "test.sh", "Hook tests", "300.0", started: "2026-01-01T00:00:00Z"),
		Line("r2", "gate.sh", "lint", "20.0", started: "2026-01-02T00:00:00Z", commit: "bbbb2222"),
		Line("r2", "test.sh", "Hook tests", "200.0", "failed", started: "2026-01-02T00:00:00Z"),
		Line("r2", "gate.sh", "test.sh", "1.0", "skipped", started: "2026-01-02T00:00:00Z"),
		"a line that is not one",
		Line("r3", "gate.sh", "lint", "30.0", started: "2026-01-03T00:00:00Z", commit: "cccc3333"),
		Line("r3", "gate.sh", "a|b", "long", started: "2026-01-03T00:00:00Z"),
		Line("r4", "gate.sh", "lint", "40.0", started: "2026-01-04T00:00:00Z", commit: "dddd4444"),
		Line("r4", "gate.sh", "a|b", "5.0", started: "2026-01-04T00:00:00Z"),
	];

	static (int Code, string Out, string Err) Run(string[] args, string[]? log = null, IDictionary<string, string>? env = null, string? common = "/repo/.git", bool gitFound = true, string? reason = null)
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
			var code = GateTimings.Run(args, stdout, stderr, (_, _) => new GateTimings.GitAnswer(common, gitFound, reason), name => variables.GetValueOrDefault(name));
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
	public void The_header_gives_the_runs_dates_and_the_commits_where_the_halves_meet()
	{
		var table = Table();
		Assert.Contains("the last 4 run(s), started 2026-01-01T00:00:00Z to 2026-01-04T00:00:00Z.\n", table);
		Assert.Contains("The older half ends at commit bbbb2222; the newer half starts at commit cccc3333.\n", table);
	}

	// A full gate run: the self part's step, test.sh and the Sandcastle check (skipped, as on a branch that changed nothing it reads, its 7 s still in the
	// total), plus whatever else it ran.
	static string[] FullRun(string run, string started, params (string Step, string Seconds, string Status)[] extra) =>
	[
		Line(run, "gate.sh", "scripts/tests/gate.test.sh", "100.0", started: started),
		Line(run, "gate.sh", "test.sh", "800.0", started: started),
		Line(run, "gate.sh", "Sandcastle check", "7.0", "skipped", started: started),
		.. extra.Select(step => Line(run, "gate.sh", step.Step, step.Seconds, step.Status, started: started)),
	];

	[Fact]
	public void The_median_total_counts_only_full_gate_runs_that_passed()
	{
		string[] log =
		[
			.. FullRun("full1", "2026-01-01T00:00:00Z"), // 907
			.. FullRun("full2", "2026-01-02T00:00:00Z", ("lint", "100.0", "passed")), // 1007
			Line("quick", "gate.sh", "scripts/tests/gate.test.sh", "60.0", started: "2026-01-03T00:00:00Z"), // --quick: no test.sh, no Sandcastle check
			Line("part", "gate.sh", "Sandcastle check", "200.0", started: "2026-01-04T00:00:00Z"), // GATE_PART=sandcastle
			.. FullRun("failed", "2026-01-05T00:00:00Z", ("lint", "5.0", "failed")), // every step, but one failed
			Line("going", "gate.sh", "scripts/tests/gate.test.sh", "100.0", started: "2026-01-06T00:00:00Z"), // still running, or killed
			Line("going", "gate.sh", "test.sh", "800.0", started: "2026-01-06T00:00:00Z"),
			// Each lacking one of the three steps, or failing or skipping one it must run.
			Line("notest", "gate.sh", "scripts/tests/gate.test.sh", "100.0", started: "2026-01-07T00:00:00Z"),
			Line("notest", "gate.sh", "Sandcastle check", "7.0", "skipped", started: "2026-01-07T00:00:00Z"),
			Line("noself", "gate.sh", "test.sh", "800.0", started: "2026-01-08T00:00:00Z"),
			Line("noself", "gate.sh", "Sandcastle check", "7.0", "skipped", started: "2026-01-08T00:00:00Z"),
			Line("testfailed", "gate.sh", "scripts/tests/gate.test.sh", "100.0", started: "2026-01-09T00:00:00Z"),
			Line("testfailed", "gate.sh", "test.sh", "800.0", "failed", started: "2026-01-09T00:00:00Z"),
			Line("testfailed", "gate.sh", "Sandcastle check", "7.0", "skipped", started: "2026-01-09T00:00:00Z"),
			Line("cached", "gate.sh", "scripts/tests/gate.test.sh", "100.0", started: "2026-01-10T00:00:00Z"), // the push after a gate run: test.sh's pass cached
			Line("cached", "gate.sh", "test.sh", "1.0", "skipped", started: "2026-01-10T00:00:00Z"),
			Line("cached", "gate.sh", "Sandcastle check", "7.0", "skipped", started: "2026-01-10T00:00:00Z"),
			Line("selfcached", "gate.sh", "scripts/tests/gate.test.sh", "1.0", "skipped", started: "2026-01-11T00:00:00Z"),
			Line("selfcached", "gate.sh", "test.sh", "800.0", started: "2026-01-11T00:00:00Z"),
			Line("selfcached", "gate.sh", "Sandcastle check", "7.0", "skipped", started: "2026-01-11T00:00:00Z"),
		];
		var table = Run(["--log", "log.tsv"], log).Out;
		Assert.Contains("A full gate run's median total: 957.0 s, over the 2 full gate run(s) of these 11 ", table);
	}

	[Fact]
	public void With_no_full_gate_run_there_is_no_total()
	{
		var table = Table();
		Assert.DoesNotContain("median total", table);
		Assert.Contains("No full gate run among these", table);
	}

	[Fact]
	public void Runs_go_in_the_order_they_started_when_two_gates_append_at_once()
	{
		// b started first but its first step ended after a's began: the halves follow the starts, so b is the older half.
		var summary = GateTimings.Summarize(
		[
			Line("a", "g", "s", "30.0", started: "2026-01-01T00:00:10Z", commit: "aaaa"),
			Line("b", "g", "s", "10.0", started: "2026-01-01T00:00:00Z", commit: "bbbb"),
		], 20);
		Assert.Equal(["bbbb", "aaaa"], summary.RunList.Select(run => run.Commit));
		Assert.Equal("+200%", GateTimings.Change(summary.Steps.Single(), summary.Runs));
	}

	[Fact]
	public void A_runs_start_is_its_earliest_line_for_the_order_and_the_header()
	{
		// gate.sh's test.sh line, started first, is appended after the lines test.sh logged inside it; b started between the two, so a is older.
		var summary = GateTimings.Summarize(
		[
			Line("a", "test.sh", "Hook tests", "10.0", started: "2026-01-01T00:00:20Z", commit: "aaaa"),
			Line("b", "g", "s", "10.0", started: "2026-01-01T00:00:10Z", commit: "bbbb"),
			Line("a", "gate.sh", "test.sh", "30.0", started: "2026-01-01T00:00:00Z", commit: "aaaa"),
		], 20);
		Assert.Equal(["aaaa", "bbbb"], summary.RunList.Select(run => run.Commit));
		Assert.Equal("2026-01-01T00:00:00Z", summary.RunList[0].Started);
		Assert.Contains("started 2026-01-01T00:00:00Z to 2026-01-01T00:00:10Z.\n", GateTimings.Render(summary, "/log.tsv"));
	}

	[Theory]
	[InlineData("2026-01-31T12:00:00Z", true)]
	[InlineData("", false)]
	[InlineData("~", false)]
	[InlineData("9999", false)]
	[InlineData("2026-1-31T12:00:00Z", false)]
	[InlineData("2026-01-31 12:00:00Z", false)]
	[InlineData("2026-01-31T12:00:00", false)]
	[InlineData("2026-01-31T12:00:00Z ", false)]
	[InlineData("2026-01-3xT12:00:00Z", false)]
	[InlineData("2026-01-31T1x:00:00Z", false)] // a non-digit in each part of the time
	[InlineData("2026-01-31T12:0x:00Z", false)]
	[InlineData("2026-01-31T12:00:0xZ", false)]
	[InlineData("2026-01-31T12-00:00Z", false)] // a wrong separator at each place
	[InlineData("2026-01-31T12:00.00Z", false)]
	[InlineData("2026/01-31T12:00:00Z", false)]
	[InlineData("2026-01/31T12:00:00Z", false)]
	[InlineData("2026-01-31T12:00:00Y", false)]
	[InlineData("x026-01-31T12:00:00Z", false)] // the first and last digit
	[InlineData("2026-01-31T12:00:0\u0663Z", false)] // an Arabic-Indic three, a digit to char.IsDigit
	[InlineData("2026-01-31T23:59:59Z", true)]
	public void A_start_must_be_in_the_writers_form(string started, bool kept)
	{
		var summary = GateTimings.Summarize([Line("r1", "g", "s", "1.0", started: started)], 20);
		Assert.Equal(kept ? 0 : 1, summary.Malformed);
		Assert.Equal(kept ? 1 : 0, summary.Runs);
	}

	[Fact]
	public void Two_runs_have_halves_of_one_each()
	{
		var table = Table("--runs", "2");
		Assert.Contains("The older half ends at commit cccc3333; the newer half starts at commit dddd4444.\n", table);
	}

	[Fact]
	public void Three_runs_put_the_middle_one_in_the_newer_half_of_the_commits_line_too() =>
		Assert.Contains("The older half ends at commit bbbb2222; the newer half starts at commit cccc3333.\n", Table("--runs", "3"));

	[Fact]
	public void One_run_has_no_halves_and_a_run_without_gate_sh_has_no_total()
	{
		var table = Run(["--log", "log.tsv"], [Line("r1", "test.sh", "Hook tests", "3.0")]).Out;
		Assert.DoesNotContain("older half ends", table);
		Assert.Contains("No full gate run among these", table);
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
	[InlineData("12345678", true)] // 8 digits
	[InlineData("123456789", true)] // 9, the most
	[InlineData("1234567890", false)] // 10
	[InlineData("1.12", true)]
	[InlineData("1.123", true)] // 3 after the point, the most
	[InlineData("1.1234", false)]
	[InlineData("99999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999.0", false)] // Infinity as a double
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
		var name = "a" + (char)0x1B + "]0;t" + (char)0x07 + " " + (char)0x9B + "2J " + (char)0x2028 + " " + (char)0x202E + "evil" + (char)0x2066
			+ " *b* `c` [d](e) <f> #g _h_ \\i !j ~k &l";
		var table = Run(["--log", "log.tsv"],
			[Line("r1", "gate.sh", name, "1.0", commit: "c" + (char)0x202E), Line("r2", "gate.sh", "other", "1.0", commit: "d" + (char)0x1B + "[2J")]).Out;
		Assert.DoesNotContain(table, c => char.IsControl(c) && c != '\n');
		var row = table.Split('\n').Single(line => line.StartsWith("| gate.sh | a ", StringComparison.Ordinal));
		Assert.Equal("| gate.sh | a \\]0;t   2J    evil  \\*b\\* \\`c\\` \\[d\\](e) \\<f\\> \\#g \\_h\\_ \\\\i \\!j \\~k \\&l | 1 | 1.0 | 1.0 | 1.0 | n/a |", row);
		Assert.Contains("The older half ends at commit c ; the newer half starts at commit d \\[2J.", table);
	}

	// Every character Clean hides, at both ends of each range, with the neighbours it keeps.
	[Theory]
	[InlineData(0x00)]
	[InlineData(0x1F)]
	[InlineData(0x7F)]
	[InlineData(0x80)]
	[InlineData(0x9F)]
	[InlineData(0xAD)] // soft hyphen
	[InlineData(0x061C)]
	[InlineData(0x180E)]
	[InlineData(0x200B)] // zero-width space
	[InlineData(0x200D)]
	[InlineData(0x200E)]
	[InlineData(0x200F)]
	[InlineData(0x2028)]
	[InlineData(0x2029)]
	[InlineData(0x202A)]
	[InlineData(0x202E)]
	[InlineData(0x2060)]
	[InlineData(0x2064)]
	[InlineData(0x2066)]
	[InlineData(0x2069)]
	[InlineData(0xFEFF)] // byte-order mark
	[InlineData(0xE000)] // private use
	[InlineData(0xE0001)] // tag characters, as surrogate pairs
	[InlineData(0xE0041)]
	[InlineData(0xE007F)]
	[InlineData(0x10FFFD)] // private use, plane 16
	[InlineData(0x0378)] // unassigned
	[InlineData(0xFFFF)] // a noncharacter, unassigned
	[InlineData(0x034F)] // combining grapheme joiner
	[InlineData(0x115F)] // Hangul fillers
	[InlineData(0x1160)]
	[InlineData(0x3164)]
	[InlineData(0xFFA0)]
	[InlineData(0x17B4)] // Khmer inherent vowels
	[InlineData(0x17B5)]
	[InlineData(0x180B)] // Mongolian free variation selectors
	[InlineData(0x180D)]
	[InlineData(0x180F)]
	[InlineData(0x2800)] // braille blank
	[InlineData(0xFE00)] // variation selectors
	[InlineData(0xFE0F)]
	[InlineData(0xE0100)] // variation selectors supplement
	[InlineData(0xE01EF)]
	public void Clean_hides_each_character_that_shows_as_nothing_as_one_space(int code) =>
		Assert.Equal("a b", GateTimings.Clean("a" + char.ConvertFromUtf32(code) + "b"));

	[Theory]
	[InlineData(0x20)]
	[InlineData(0x7E)]
	[InlineData(0xA0)] // no-break space
	[InlineData(0xE9)] // e acute
	[InlineData(0x2030)] // per mille, after the separators and bidi embeddings
	[InlineData(0x1F600)] // an emoji, a surrogate pair kept whole
	[InlineData(0x0377)] // the neighbours of the hidden ones above
	[InlineData(0x034E)]
	[InlineData(0x17B6)]
	[InlineData(0x180A)]
	[InlineData(0x2801)]
	[InlineData(0x3165)]
	[InlineData(0xFE10)]
	[InlineData(0xFF9F)]
	public void Clean_keeps_visible_text_as_it_is(int code)
	{
		var text = "a" + char.ConvertFromUtf32(code) + "b";
		Assert.Equal(text, GateTimings.Clean(text));
	}

	[Fact]
	public void Clean_hides_a_lone_surrogate_as_a_space()
	{
		Assert.Equal("a b", GateTimings.Clean("a" + (char)0xD800 + "b"));
		Assert.Equal("a b", GateTimings.Clean("a" + (char)0xDC00 + "b"));
		Assert.Equal("a ", GateTimings.Clean("a" + (char)0xD83D));
	}

	[Fact]
	public void A_long_name_is_cut_to_CellRunes_characters_in_a_cell()
	{
		Assert.Equal(120, GateTimings.CellRunes);
		Assert.Equal(new string('x', 119), GateTimings.Cell(new string('x', 119)));
		Assert.Equal(new string('x', 120), GateTimings.Cell(new string('x', 120)));
		Assert.Equal(new string('x', 120) + "...", GateTimings.Cell(new string('x', 121)));
		Assert.Equal(new string('x', 120) + "...", GateTimings.Cell(new string('x', 10_000)));
		var face = char.ConvertFromUtf32(0x1F600); // a surrogate pair, never split
		Assert.Equal(string.Concat(Enumerable.Repeat(face, 120)) + "...", GateTimings.Cell(string.Concat(Enumerable.Repeat(face, 121))));
		Assert.Equal(new string('x', 119) + "\\|...", GateTimings.Cell(new string('x', 119) + "||")); // cut before the escape
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
		Assert.Contains("isn't in a git checkout", result.Err);
		Assert.Contains("pass --log FILE", result.Err);
	}

	[Fact]
	public void Without_git_on_PATH_it_says_so_not_that_the_folder_isnt_a_checkout()
	{
		var result = Run([], common: null, gitFound: false);
		Assert.Equal(1, result.Code);
		Assert.Contains("git isn't in any of PATH's absolute folders", result.Err);
		Assert.DoesNotContain("checkout", result.Err);
	}

	[Fact]
	public void When_git_refuses_the_folder_it_says_what_git_said_not_that_it_isnt_a_checkout()
	{
		var result = Run([], common: null, reason: "fatal: detected dubious ownership in repository at '/x'" + (char)0x1B);
		Assert.Equal(1, result.Code);
		Assert.Contains("git can't say which checkout", result.Err);
		Assert.Contains("(fatal: detected dubious ownership in repository at '/x' ), so the default log can't be found: pass --log FILE.", result.Err);
		Assert.DoesNotContain("isn't in a git checkout", result.Err);
	}

	[Fact]
	public void When_git_says_it_isnt_a_repository_it_isnt_a_checkout()
	{
		var result = Run([], common: null, reason: "fatal: not a git repository (or any of the parent directories): .git");
		Assert.Equal(1, result.Code);
		Assert.Contains("isn't in a git checkout", result.Err);
		Assert.DoesNotContain("git can't say", result.Err);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void Without_GATE_TIMINGS_HOME_the_default_log_is_found_from_the_callers_folder(string? home)
	{
		var asked = new List<string>();
		var env = new Dictionary<string, string> { ["GATE_TIMINGS_CALLER_DIR"] = "/caller" };
		if (home is not null)
			env["GATE_TIMINGS_HOME"] = home;
		var code = GateTimings.Run([], new StringWriter(), new StringWriter(), (folder, _) =>
		{
			asked.Add(folder);
			return new GateTimings.GitAnswer("/caller/.git", true);
		}, name => env.GetValueOrDefault(name));
		Assert.Equal(1, code); // no log there
		Assert.Equal(["/caller"], asked);
	}

	[Fact]
	public void The_default_log_is_found_from_GATE_TIMINGS_HOME_the_wrappers_folder_not_the_callers()
	{
		var asked = new List<string>();
		var env = new Dictionary<string, string> { ["GATE_TIMINGS_HOME"] = "/factory/scripts", ["GATE_TIMINGS_CALLER_DIR"] = "/elsewhere" };
		var code = GateTimings.Run([], new StringWriter(), new StringWriter(), (folder, _) =>
		{
			asked.Add(folder);
			return new GateTimings.GitAnswer("/factory/.git", true);
		}, name => env.GetValueOrDefault(name));
		Assert.Equal(1, code); // no log there
		Assert.Equal(["/factory/scripts"], asked);
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

	[Theory]
	[InlineData("--help")]
	[InlineData("-h")]
	public void Help_prints_the_usage_and_reads_no_log(string flag)
	{
		var result = Run([flag], common: null);
		Assert.Equal(0, result.Code);
		Assert.Equal("Usage: scripts/gate-timings.sh [--runs N] [--log FILE] [--help]\n", result.Out.Replace("\r\n", "\n"));
		Assert.Equal("", result.Err);
	}

	// The default log missing is The_default_log_is_beside_the_shared_git_folder's: a gate run writes it. A path someone gave may just be wrong.
	[Fact]
	public void A_missing_log_named_by_log_or_GATE_TIMINGS_LOG_says_to_check_the_path()
	{
		var result = Run(["--log", "none.tsv"]);
		Assert.Equal(1, result.Code);
		Assert.Contains("none.tsv: check the path --log or GATE_TIMINGS_LOG gives.", result.Err);
		Assert.DoesNotContain("writes it", result.Err);
		var named = Run([], env: new Dictionary<string, string> { ["GATE_TIMINGS_LOG"] = "/nowhere/none.tsv" });
		Assert.Equal(1, named.Code);
		Assert.Contains("No step timings log at /nowhere/none.tsv: check the path", named.Err);
	}

	[Fact]
	public void A_log_that_is_a_folder_exits_1()
	{
		var dir = Directory.CreateTempSubdirectory("gate-timings-folder.").FullName;
		try
		{
			var result = Run(["--log", dir]);
			Assert.Equal(1, result.Code);
			Assert.Contains("Can't read the step timings log at", result.Err);
			Assert.Contains("it is a folder", result.Err);
			Assert.DoesNotContain("a run of scripts/gate.sh writes it", result.Err);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	public void A_log_that_cant_be_read_exits_1_with_the_reason()
	{
		var dir = Directory.CreateTempSubdirectory("gate-timings-unreadable.").FullName;
		var log = Path.Combine(dir, "log.tsv");
		File.WriteAllLines(log, Fixture);
		File.SetUnixFileMode(log, UnixFileMode.None);
		try
		{
			var readable = true;
			try
			{
				File.ReadAllBytes(log);
			}
			catch (UnauthorizedAccessException)
			{
				readable = false;
			}
			Assert.SkipWhen(readable, "this user (root) reads a file with no permissions");
			var result = Run(["--log", log]);
			Assert.Equal(1, result.Code);
			Assert.Contains($"Can't read the step timings log at {log}: ", result.Err);
			Assert.DoesNotContain("it is a folder", result.Err);
			Assert.DoesNotContain("writes it", result.Err);
			Assert.DoesNotContain("check the path", result.Err);
		}
		finally
		{
			File.SetUnixFileMode(log, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

	sealed class FailingFlush : StringWriter
	{
		public override void Flush() => throw new IOException("No space left on device");
	}

	[Fact]
	public void A_flush_that_fails_at_the_end_is_a_message_and_exit_1()
	{
		var stderr = new StringWriter();
		var env = new Dictionary<string, string>();
		var code = GateTimings.RunFlushed(["--help"], new FailingFlush(), stderr, (_, _) => new GateTimings.GitAnswer(null, true), name => env.GetValueOrDefault(name));
		Assert.Equal(1, code);
		Assert.Equal("gate-timings failed: IOException: No space left on device\n", stderr.ToString().Replace("\r\n", "\n"));
	}

	[Fact]
	public void An_unexpected_exception_is_a_message_and_exit_1()
	{
		var stderr = new StringWriter();
		var code = GateTimings.Guarded(() => throw new InvalidOperationException("boom\u001b[2J"), stderr);
		Assert.Equal(1, code);
		Assert.Equal("gate-timings failed: InvalidOperationException: boom [2J\n", stderr.ToString().Replace("\r\n", "\n"));
	}

	// Neither test changes the process's current folder, which every test class running beside them shares.
	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	public void Which_takes_only_an_executable_in_an_absolute_PATH_folder()
	{
		var root = Directory.CreateTempSubdirectory("gate-timings-which.").FullName;
		try
		{
			foreach (var folder in new[] { "relative", "plain", "real" })
				Directory.CreateDirectory(Path.Combine(root, folder));
			FakeGit(Path.Combine(root, "relative"), "/relative/.git");
			File.WriteAllText(Path.Combine(root, "plain", "git"), "not executable\n");
			FakeGit(Path.Combine(root, "real"), "/real/.git");
			// A relative entry that names the fake from the current folder, as "." or "bin" would name one there; an empty entry; a file with no execute bit.
			var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.Combine(root, "relative"));
			Assert.False(Path.IsPathRooted(relative));
			Assert.True(File.Exists(Path.Combine(relative, "git")));
			var path = string.Join(Path.PathSeparator, relative, "", Path.Combine(root, "plain"), Path.Combine(root, "real"));
			Assert.Equal(Path.Combine(root, "real", "git"), GateTimings.Which("git", path));
			Assert.Null(GateTimings.Which("git", relative));
			Assert.Null(GateTimings.Which("git", ""));
			Assert.Null(GateTimings.Which("git", null));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	static void FakeGit(string folder, string answer) => FakeGitScript(folder, $"echo {answer}");

	// Written by a child shell, not this process: a child another test class forks while this process held the file open for writing would keep it
	// open until its exec, and starting the script then fails with "Text file busy".
	static void FakeGitScript(string folder, string body)
	{
		using var shell = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", "cat > \"$1\" && chmod 500 \"$1\"", "sh", Path.Combine(folder, "git")])
		{
			RedirectStandardInput = true,
			UseShellExecute = false,
		})!;
		shell.StandardInput.Write($"#!/bin/sh\n{body}\n");
		shell.StandardInput.Close();
		shell.WaitForExit();
		Assert.Equal(0, shell.ExitCode);
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	public void GitCommonDir_starts_the_git_Which_found_on_PATH_and_no_other()
	{
		var root = Directory.CreateTempSubdirectory("gate-timings-git.").FullName;
		try
		{
			foreach (var folder in new[] { "asked", "first", "second" })
				Directory.CreateDirectory(Path.Combine(root, folder));
			FakeGit(Path.Combine(root, "asked"), "/asked/.git"); // in the folder it asks about
			FakeGit(Path.Combine(root, "first"), "/first/.git");
			FakeGit(Path.Combine(root, "second"), "/second/.git");
			var asked = Path.Combine(root, "asked");
			Assert.Equal(new GateTimings.GitAnswer("/first/.git", true),
				GateTimings.GitCommonDir(asked, string.Join(Path.PathSeparator, Path.Combine(root, "first"), Path.Combine(root, "second"))));
			Assert.Equal(new GateTimings.GitAnswer("/second/.git", true), GateTimings.GitCommonDir(asked, Path.Combine(root, "second")));
			Assert.Equal(new GateTimings.GitAnswer(null, false), GateTimings.GitCommonDir(asked, ""));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	public void GitCommonDir_gives_gits_first_line_on_stderr_or_why_it_didnt_start_when_it_finds_no_checkout()
	{
		var root = Directory.CreateTempSubdirectory("gate-timings-git-fails.").FullName;
		try
		{
			foreach (var folder in new[] { "refuses", "silent" })
				Directory.CreateDirectory(Path.Combine(root, folder));
			FakeGitScript(Path.Combine(root, "refuses"), "echo >&2\necho \"fatal: detected dubious ownership in repository at '/x'\" >&2\necho 'hint: more' >&2\nexit 128");
			FakeGitScript(Path.Combine(root, "silent"), "exit 3");
			Assert.Equal(new GateTimings.GitAnswer(null, true, "fatal: detected dubious ownership in repository at '/x'"), GateTimings.GitCommonDir(root, Path.Combine(root, "refuses")));
			Assert.Equal(new GateTimings.GitAnswer(null, true, "git exited 3"), GateTimings.GitCommonDir(root, Path.Combine(root, "silent")));
			var gone = GateTimings.GitCommonDir(Path.Combine(root, "no-such-folder"), Path.Combine(root, "silent"));
			Assert.Null(gone.CommonDir);
			Assert.True(gone.GitFound);
			Assert.False(string.IsNullOrEmpty(gone.Reason));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
