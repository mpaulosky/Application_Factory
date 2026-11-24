// What the port changes on purpose in land (ADR 0017's Update for mpaulosky/Application_Factory#119): not moved tests, so LandTests
// keeps test_land.py's and its count can be checked against that file's.
public class LandPortTests
{
	// Not a moved test: a PR number past long's range is a usage error, not another PR's number wrapped around.
	[Fact]
	public void A_pr_number_past_longs_range_is_refused_not_wrapped()
	{
		Assert.True(Land.ParseTargets(["o/app#9223372036854775807"])["o/app"]!.SetEquals([long.MaxValue]));
		Assert.True(Land.ParseTargets(["o/app#0009223372036854775806"])["o/app"]!.SetEquals([long.MaxValue - 1]));
		Assert.Throws<ArgumentException>(() => Land.ParseTargets(["o/app#9223372036854775808"]));
		Assert.Throws<ArgumentException>(() => Land.ParseTargets(["o/app#18446744073709551617"]));
		// Unicode digits are read as int() reads them.
		Assert.True(Land.ParseTargets(["o/app#\u0661\u0662"])["o/app"]!.SetEquals([12]));
		using var err = new StringWriter();
		Assert.Equal(2, Land.Run(["o/app#18446744073709551617"], new StringWriter(), err, readRepoList: () => new()));
		Assert.Contains("land.cs: error: not owner/repo or owner/repo#n: o/app#18446744073709551617", err.ToString());
		// Whatever came before: a repo already watched whole still has a later PR number read.
		Assert.Throws<ArgumentException>(() => Land.ParseTargets(["o/app", "o/app#18446744073709551617"]));
		Assert.Null(Land.ParseTargets(["o/app", "o/app#7"])["o/app"]);
	}

	// Not a moved test: a target is the whole argument, as status.cs's repo names are: a trailing line break, a . or .. side, or a
	// letter GitHub's names don't take is a usage error before any call, rather than a crash in the poll.
	[Theory]
	[InlineData("o/app\n")]
	[InlineData("o/app#7\n")]
	[InlineData("https://github.com/o/app/pull/7\n")]
	[InlineData("../x")]
	[InlineData("x/..")]
	[InlineData("./x")]
	[InlineData("x/.")]
	[InlineData("../x#7")]
	[InlineData("o/\u00e9")]
	[InlineData("o/a#7#8")]
	[InlineData("o/a/b#7")]
	public void A_target_that_isnt_owner_repo_or_a_pr_is_a_usage_error_before_any_call(string target)
	{
		var fetched = 0;
		using var err = new StringWriter();
		var lander = new Lander(new(), new Dictionary<string, string>(), new StringWriter(), err) { Fetch = (_, _) => { fetched++; return new System.Text.Json.Nodes.JsonObject(); } };

		Assert.Equal(2, Land.Run(["--once", target], new StringWriter(), err, lander: lander, readRepoList: () => new()));
		Assert.Contains($"land.cs: error: not owner/repo or owner/repo#n: {target}", err.ToString());
		Assert.Equal(0, fetched);
	}

	[Theory]
	[InlineData("o/app", null)]
	[InlineData("o.x/a_b-c", null)]
	[InlineData("-o/-a", null)]
	[InlineData("..x/x..", null)]
	[InlineData("o/app#7", 7L)]
	[InlineData("https://github.com/o/app/pull/7", 7L)]
	public void An_owner_repo_or_a_pr_is_a_target(string target, long? number)
	{
		var targets = Land.ParseTargets([target]);
		var numbers = Assert.Single(targets).Value;

		Assert.Equal(number, numbers?.Single());
	}

	// Not a moved test: a name in the Fleet registry that isn't owner/repo ends land with exit 1 before any call, not a usage error,
	// since no argument was wrong.
	[Fact]
	public void A_fleet_registry_name_that_isnt_owner_repo_ends_the_run_before_any_call()
	{
		var fetched = 0;
		using var err = new StringWriter();
		var lander = new Lander(new(), new Dictionary<string, string>(), new StringWriter(), err) { Fetch = (_, _) => { fetched++; return new System.Text.Json.Nodes.JsonObject(); } };

		Assert.Equal(1, Land.Run(["--once"], new StringWriter(), err, lander: lander, readRepoList: () => new() { ["o/app"] = "full", ["../x"] = "full" }));
		Assert.Equal("repos.txt: not owner/repo: ../x\n", err.ToString());
		Assert.Equal(0, fetched);
	}

	// Not a moved test: land's own wait is Land.Sleep, so a --interval time.sleep refuses ends the run (exit 1 from Script.Run) when it
	// first has to wait, rather than hanging or throwing past Thread.Sleep's range.
	[Theory]
	[InlineData("-1")]
	[InlineData("nan")]
	[InlineData("1e10")]
	public void Lands_default_wait_refuses_what_time_sleep_refuses(string interval)
	{
		var polls = 0;
		using var err = new StringWriter();
		var lander = new Lander(Land.ParseTargets(["o/app"]), new Dictionary<string, string>(), new StringWriter(), err)
		{
			Fetch = (_, _) => { polls++; throw new InvalidOperationException("unreadable"); },
		};

		Assert.Throws<ArgumentOutOfRangeException>(() => Land.Run([$"--interval={interval}", "o/app"], new StringWriter(), err, lander: lander, readRepoList: () => new()));
		Assert.Equal(1, polls);
	}

	// Not a moved test: the wait between polls is time.sleep's: a negative or NaN length, or one past long.MaxValue nanoseconds, throws
	// rather than reach Thread.Sleep, which reads -1 ms as forever, and a wait past Thread.Sleep's int.MaxValue ms is slept in steps.
	[Fact]
	public void The_wait_between_polls_refuses_what_time_sleep_refuses_and_steps_a_long_one()
	{
		List<TimeSpan> waits = [];
		foreach (var bad in new[] { -0.001, -0.0015, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
			Assert.Throws<ArgumentOutOfRangeException>(() => Land.Sleep(bad, waits.Add));
		Assert.Empty(waits);

		Land.Sleep(0, waits.Add);
		Land.Sleep(-0.0, waits.Add);
		Assert.Empty(waits);
		Land.Sleep(1.5, waits.Add);
		Assert.Equal([TimeSpan.FromSeconds(1.5)], waits);

		const double most = int.MaxValue / 1000.0;
		foreach (var (seconds, steps) in new[] { (most, 1), (most + 1, 2), (most * 2, 2), (most * 2 + 0.5, 3) })
		{
			waits.Clear();
			Land.Sleep(seconds, waits.Add);
			Assert.Equal(steps, waits.Count);
			Assert.All(waits, wait => Assert.InRange(wait.TotalMilliseconds, 0, int.MaxValue));
			Assert.Equal(seconds, waits.Sum(wait => wait.TotalSeconds), 3);
		}

		// time.sleep's longest wait, long.MaxValue ns: it and the double below it are slept (counted, not waited), the next one up throws.
		var longest = Land.LongestSleep;
		Assert.Equal(9223372036.854776, longest, 6);
		foreach (var seconds in new[] { longest, Math.BitDecrement(longest) })
		{
			long count = 0;
			Land.Sleep(seconds, _ => count++);
			Assert.Equal((long)Math.Ceiling(seconds / most), count);
		}
		foreach (var bad in new[] { Math.BitIncrement(longest), 1e300, double.MaxValue })
			Assert.Throws<ArgumentOutOfRangeException>(() => Land.Sleep(bad, waits.Add));
	}
}
