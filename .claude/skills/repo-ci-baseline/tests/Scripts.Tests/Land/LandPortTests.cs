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
	}

	// Not a moved test: the wait between polls is time.sleep's: a negative, NaN or infinite length throws rather than reach
	// Thread.Sleep, which reads -1 ms as forever, and a wait past Thread.Sleep's int.MaxValue ms is slept in steps.
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
	}
}
