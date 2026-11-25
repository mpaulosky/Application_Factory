using System.Globalization;

// What the port changes on purpose in land (ADR 0017's Update for mpaulosky/Application_Factory#119): not moved tests, so LandTests
// keeps the moved pytest suite's, and its count can be checked against that suite's in git history.
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
		// Unicode digits are read as int() reads them, an astral one (a surrogate pair) too; another astral character is refused.
		Assert.True(Land.ParseTargets(["o/app#\u0661\u0662"])["o/app"]!.SetEquals([12]));
		Assert.True(Land.ParseTargets(["o/app#\U0001D7D3"])["o/app"]!.SetEquals([5]));
		Assert.True(Land.ParseTargets(["https://github.com/o/app/pull/1\U0001D7D3"])["o/app"]!.SetEquals([15]));
		Assert.Throws<ArgumentException>(() => Land.ParseTargets(["o/app#1\U0001F600"]));
		Assert.Throws<ArgumentException>(() => Land.ParseTargets(["o/app#\uD835"]));
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

	// Not a moved test: the wait between polls is time.sleep's: a negative or NaN length, or one of 2^63 nanoseconds or more, throws
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

		// time.sleep's bound, 2^63 ns: the double below it is slept (counted, not waited), and it and every one above it throw, as
		// Python 3.12 and 3.14 raise OverflowError for 9223372036.854776 (run here).
		var longest = Land.LongestSleep;
		Assert.Equal(9223372036.854776, longest, 6);
		foreach (var seconds in new[] { Math.BitDecrement(longest), Math.BitDecrement(Math.BitDecrement(longest)) })
		{
			long count = 0;
			Land.Sleep(seconds, _ => count++);
			Assert.Equal((long)Math.Ceiling(seconds / most), count);
		}
		foreach (var bad in new[] { longest, Math.BitIncrement(longest), 1e300, double.MaxValue })
			Assert.Throws<ArgumentOutOfRangeException>(() => Land.Sleep(bad, waits.Add));
	}

	// Not a moved test: GitHub logins ignore case, and land's owner is the target as typed (land.sh MPaulosky/app#5) or as repos.txt spells
	// it, while the record's author is GitHub's spelling; the Python compared them exactly, so land asked Copilot again on every run.
	// A record by github-actions is still matched exactly.
	[Fact]
	public void The_owners_ask_record_counts_whatever_the_case_the_owner_was_given_in()
	{
		const double now = 1_800_000_000.0;
		var written = DateTimeOffset.FromUnixTimeSeconds((long)now - 3600).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
		(string?, string?, string?) Record(string login) => (login, $"Asked Copilot.\n\n{Landing.CopilotAskedMarker}", written);
		Assert.Equal((true, true), Landing.CopilotAsk([Record("mpaulosky")], "MPaulosky", now));
		Assert.Equal((true, true), Landing.CopilotAsk([Record("MPaulosky")], "mpaulosky", now));
		Assert.Equal((false, false), Landing.CopilotAsk([Record("mpaulosky-other")], "MPaulosky", now));
		Assert.Equal((false, false), Landing.CopilotAsk([Record("GitHub-Actions")], "o", now));
	}

	// Not a moved test: gh can write a warning to stderr on a call that succeeded (a deprecation or scope notice, GH_DEBUG); a read takes
	// its answer from stdout, and only a failure's message is read from stderr.
	[Fact]
	public void A_gh_read_that_succeeded_is_answered_by_its_stdout_whatever_its_stderr_says()
	{
		List<string[]> calls = [];
		Func<IReadOnlyList<string>, GhRun> Gh(int code, string stdout, string stderr) => argv => { calls.Add([.. argv]); return new(code, stdout, stderr); };

		Assert.Equal("MERGED", Land.GhPrState("o/app", 7, Gh(0, "MERGED\n", "warning: a newer gh is out\n")));
		Assert.Equal(["pr", "view", "7", "-R", "o/app", "--json", "state", "--jq", ".state"], calls[^1]);
		Assert.True(Land.GhCopilotRequested("o/app", 7, Gh(0, "1\n", "warning: token lacks read:org\n")));
		Assert.False(Land.GhCopilotRequested("o/app", 7, Gh(0, "0\n", "")));
		Assert.Equal("a b", Assert.Throws<GhException>(() => Land.GhPrState("o/app", 7, Gh(1, "partial", "a\nb\n"))).Message);
		Assert.Equal("HTTP 502", Assert.Throws<GhException>(() => Land.GhCopilotRequested("o/app", 7, Gh(1, "HTTP 502\n", ""))).Message);
	}

	// Not a moved test: the times that decide whether the ask has settled and where a review round sits are read as
	// datetime.fromisoformat() read them, with a time with no offset taken as UTC, and nothing DateTimeOffset.TryParse would guess at.
	[Theory]
	[InlineData("2026-01-05T10:00:00Z", 1767607200.0)]
	[InlineData("2026-01-05T10:00:00+00:00", 1767607200.0)]
	[InlineData("2026-01-05T10:00:00", 1767607200.0)]
	[InlineData("2026-01-05 10:00:00Z", 1767607200.0)]
	[InlineData("2026-01-05T12:00:00+02:00", 1767607200.0)]
	[InlineData("2026-01-05T10:00:00.5Z", 1767607200.5)]
	[InlineData("2026-01-05T10:00Z", 1767607200.0)]
	[InlineData("2026-01-05T10", 1767607200.0)]
	[InlineData("2026-01-05T10Z", 1767607200.0)]
	[InlineData("2026-01-05 10", 1767607200.0)]
	[InlineData("2026-01-05T12+02:00", 1767607200.0)]
	[InlineData("2026-01-05", 1767571200.0)]
	[InlineData(null, null)]
	[InlineData("", null)]
	[InlineData("not a date", null)]
	[InlineData("Jan 5 2026", null)]
	[InlineData("1/2/2026", null)]
	[InlineData("2026", null)]
	[InlineData("2026-01-05T10:00:00Z ", null)]
	[InlineData("2026-13-05T10:00:00Z", null)]
	[InlineData("\u0662026-01-05T10:00:00Z", null)]
	public void A_time_is_read_as_fromisoformat_read_it(string? text, double? epoch) => Assert.Equal(epoch, Landing.Epoch(text));

	// Not a moved test: a repo answer without its PR list is a repo land can't read, not one with no open PRs.
	[Theory]
	[InlineData("{}")]
	[InlineData("""{"nodes": null}""")]
	public void A_repo_answer_without_its_pr_list_is_a_repo_land_cant_read(string pullRequests)
	{
		using var err = new StringWriter();
		using var output = new StringWriter();
		var lander = new Lander(Land.ParseTargets(["o/app"]), new Dictionary<string, string>(), output, err)
		{
			Fetch = (_, _) => System.Text.Json.Nodes.JsonNode.Parse($$$"""{"repository": {"pullRequests": {{{pullRequests}}}}}""")!,
		};

		Assert.Equal(1, Land.Run(["--once", "o/app"], output, err, lander: lander, readRepoList: () => new()));
		Assert.Contains("o/app: ", err.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("No open PRs", output.ToString(), StringComparison.Ordinal);
	}
}

// Not moved tests, in LandTests for its fake GitHub.
public partial class LandTests
{
	// Not a moved test: a PR state that isn't MERGED, CLOSED or OPEN (a warning, an empty answer) is unread, so the PR stays watched
	// and the read is reported, rather than called gone and the run ended as if every PR had landed.
	[Theory]
	[InlineData("warning: \u001b[31mupgrade gh")]
	[InlineData("")]
	[InlineData("merged")]
	public void A_pr_state_that_isnt_one_keeps_the_pr_watched(string answer)
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node()) });
		var (lander, output, err) = Start(fake, new() { ["o/app"] = [7] });
		lander.Poll();
		Printed(output);

		fake.Open["o/app"] = Prs();
		fake.Final[("o/app", 7)] = answer;
		Assert.False(lander.Poll().Done);
		Assert.DoesNotContain("app#7 ", Printed(output), StringComparison.Ordinal);
		Assert.Contains("o/app#7: not a PR state: ", err.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", err.ToString(), StringComparison.Ordinal);

		fake.Final[("o/app", 7)] = "CLOSED";
		Assert.True(lander.Poll().Done);
		Assert.Contains("app#7 closed", Printed(output), StringComparison.Ordinal);
	}
}
