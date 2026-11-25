// Tests for the --auto round's stops (github/AutoRolloutRound.cs's AutoRound.Round and Run, through RolloutCore.cs's Rollout.Run): xUnit
// copies of github/tests/test_auto_rollout.py's tests of what holds the rest when TicketManager stops, what one of the rest stopping
// reports, crashes, a land.sh that fails, crashes or leaves its group, resuming, the dry run, the round without --auto and the header
// (ADR 0017's Update for mpaulosky/Application_Factory#122), with the same cases and assertions.
//
// A Python exception's name becomes the C# one the round prints (error.GetType().Name), with the same message: PermissionError is
// UnauthorizedAccessException, OSError IOException (land.sh's own stop names it OSError, as ReviewSession.PyErrorName does), LookupError
// KeyNotFoundException and TypeError InvalidCastException; a traceback on stderr is the exception's stack trace ("   at ").
[Collection(nameof(RoundCollection))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RoundStopsTests
{
	const string TM = Fleets.TM;
	const string Blazor = "mpaulosky/Blazor-Server";
	const string Articles = "mpaulosky/Articles";
	const string IssueManager = "mpaulosky/IssueManager";
	static readonly IReadOnlyList<string> Rest = Fleets.Rest;

	// test_auto_rollout.py's LEFT: a group reapply.sh or land.sh left.
	const string LeftText = "what reapply.sh left in its process group was still there 10 s after it was killed";

	static GroupLeft Left() => new(LeftText);

	// The round needs Linux (ADR 0016): its signals and lock descriptor are POSIX ones.
	public RoundStopsTests() => Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");

	// The repos reapplied when every one is: TicketManager, then the rest by their short names.
	static List<string> All() => [Fleets.Short(TM), .. Rest.Select(Fleets.Short)];

	// round_with_checkout: AutoRound.Round on the fleet, with checkout as the round's checkout of each repo.
	static (int Code, string Out, string Err) RoundWithCheckout(Fleet fleet, Func<string, string> checkout)
	{
		var (output, err) = (new StringWriter(), new StringWriter());
		var code = AutoRound.Round([TM, .. Rest], "#82", "rollout.sh --auto --brings '#82'", checkout, fleet.World(),
				text => output.Write(text + "\n"), err);
		return (code, output.ToString(), err.ToString());
	}

	[Fact]
	public void Auto_holds_the_rest_when_ticketmanagers_reapply_fails()
	{
		using var fleet = new Fleet { Codes = { [TM] = [1] } };
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 1 && fleet.Reapplied().SequenceEqual(["TicketManager"]), output);
		Assert.Contains("TicketManager: reapply.sh exited 1", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Auto_holds_the_rest_when_ticketmanagers_pr_was_closed()
	{
		using (var fleet = new Fleet())
		{
			fleet.Prs[TM] = (129, "CLOSED", Fleets.HeadOf(TM));
			var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
			Assert.Equal(1, code);
			Assert.Contains("rollout: TicketManager#129 is CLOSED, not merged: reopen it (gh pr reopen 129 -R mpaulosky/TicketManager)"
					+ " and land it with land.sh mpaulosky/TicketManager#129, or delete its branch and resume for a new PR; held:", output, StringComparison.Ordinal);
		}
		// The last round's PR, closed at another head, with nothing pushed: it already matches the Template, so the rest go.
		using (var fleet = new Fleet())
		{
			fleet.Prs[TM] = (129, "CLOSED", new string('0', 40));
			var (_, output, _) = fleet.RunAuto(["--brings", "#82"]);
			Assert.Contains("── rollout: TicketManager already matches the Template: nothing to land, so the rest go on", output, StringComparison.Ordinal);
			Assert.True(fleet.Reapplied()[0] == "TicketManager" && fleet.Reapplied()[^1] == "SuggestionApp", output);
		}
	}

	[Fact]
	public void Auto_holds_with_the_gh_error_when_ticketmanagers_pr_cannot_be_read()
	{
		using var fleet = new Fleet { Unreadable = { TM } };
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 1 && fleet.Reapplied().SequenceEqual(["TicketManager"]), output);
		Assert.Contains("TicketManager: can't tell its re-Apply PR (gh failed: HTTP 502): land it with land.sh mpaulosky/TicketManager", output,
				StringComparison.Ordinal);
		Assert.DoesNotContain("#None", output, StringComparison.Ordinal);
	}

	[Fact]
	public void The_rest_go_on_when_ticketmanager_already_matches_the_template()
	{
		// reapply.sh pushed nothing and there is no PR: TicketManager is current, so a resume would stop in the same place.
		using var fleet = new Fleet();
		fleet.Prs[TM] = null;
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 0 && fleet.Sleeps.Count == 0, output);
		Assert.Contains("── rollout: TicketManager already matches the Template: nothing to land, so the rest go on", output, StringComparison.Ordinal);
		Assert.True(fleet.Reapplied()[0] == "TicketManager" && fleet.Reapplied()[^1] == "SuggestionApp");
		Assert.DoesNotContain("held:", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_worktree_whose_branch_cannot_be_read_is_a_stop()
	{
		using (var fleet = new Fleet { BranchFails = { TM } })
		{
			var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
			Assert.Equal(1, code);
			Assert.Contains("TicketManager: can't tell its re-Apply PR (can't read /r/TicketManager-worktrees/chore-reapply-baseline's branch:"
					+ " git rev-parse HEAD exited 128: not a git repository)", output, StringComparison.Ordinal);
		}
		using (var fleet = new Fleet { BranchFails = { Articles } })
		{
			var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
			Assert.True(code == 1 && fleet.Reapplied()[^1] == "SuggestionApp", output);
			Assert.Contains("  Articles: can't tell its re-Apply PR (can't read ", output, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void A_crash_on_ticketmanager_still_ends_with_the_resume_command_and_a_traceback()
	{
		using var fleet = new Fleet { Raises = { [TM] = new UnauthorizedAccessException("[Errno 13] Permission denied") } };
		var (code, output, err) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.Contains("TicketManager: UnauthorizedAccessException: [Errno 13] Permission denied (traceback on stderr); run it by hand with:"
				+ " reapply.sh --brings '#82' /r/TicketManager", output, StringComparison.Ordinal);
		Assert.Contains("resume with:", output, StringComparison.Ordinal);
		Assert.Contains("rollout.sh: TicketManager crashed:\nSystem.UnauthorizedAccessException: [Errno 13] Permission denied", err, StringComparison.Ordinal);
		Assert.Contains("   at ", err, StringComparison.Ordinal); // the traceback
	}

	[Fact]
	public void Auto_reports_each_stopped_repo_among_the_rest_lands_none_of_theirs_and_goes_on()
	{
		// Blazor-Server waits for a person and Articles fails, each with an open PR from an earlier run: neither is landed.
		using var fleet = new Fleet
		{
			Codes = { [Blazor] = [AutoRound.FindingsWait], [Articles] = [1] },
			Opens = new(Rest.Select(repo => new KeyValuePair<string, long>(repo, 300))),
			Unlanded = { $"{Rest[0]}#300" },
		};
		fleet.Prs[Blazor] = (250, "OPEN", new string('0', 40));
		fleet.Prs[Articles] = (251, "OPEN", new string('0', 40));
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.Equal(All(), fleet.Reapplied());
		var landed = fleet.Landed.SelectMany(refs => refs).ToHashSet();
		Assert.DoesNotContain(landed, reference => reference.StartsWith(Blazor, StringComparison.Ordinal) || reference.StartsWith(Articles, StringComparison.Ordinal));
		Assert.Contains($"{IssueManager}#300", landed);
		Assert.Contains("  Blazor-Server: review findings wait for you", output, StringComparison.Ordinal);
		Assert.Contains("  Articles: reapply.sh exited 1", output, StringComparison.Ordinal);
		Assert.Contains("  IssueTracker#300 didn't land (#300 still reads as OPEN): land.sh mpaulosky/IssueTracker#300", output, StringComparison.Ordinal);
		Assert.Contains("rollout: TicketManager and 2 of 5 other repo(s) done; waiting for you:", output, StringComparison.Ordinal);
		Assert.EndsWith("resume with: rollout.sh --auto --brings '#82'", output.TrimEnd(), StringComparison.Ordinal);
	}

	[Fact]
	public void A_rest_repo_whose_pr_cannot_be_read_is_a_stop_not_done()
	{
		using var fleet = new Fleet { Opens = { [TM] = 130, [Rest[0]] = 200 }, Unreadable = { Rest[0] } };
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.Contains("IssueTracker: can't tell its re-Apply PR (gh failed: HTTP 502): land it with land.sh mpaulosky/IssueTracker", output,
				StringComparison.Ordinal);
		Assert.Contains("TicketManager and 4 of 5 other repo(s) done", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_rest_repo_with_nothing_to_push_and_no_pr_is_done_at_once()
	{
		using var fleet = new Fleet();
		fleet.Prs[Rest[0]] = null;
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 0, output);
		Assert.True(fleet.Sleeps.Count == 0 && fleet.Landed.Count == 0);
	}

	[Fact]
	public void A_rest_repo_whose_last_rounds_pr_was_closed_with_nothing_to_push_is_done()
	{
		using var fleet = new Fleet();
		fleet.Prs[Rest[0]] = (77, "CLOSED", new string('0', 40));
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 0, output);
		Assert.True(fleet.Sleeps.Count == 0 && fleet.Landed.Count == 0);
	}

	[Fact]
	public void A_rest_repo_whose_pr_was_closed_is_a_stop()
	{
		using var fleet = new Fleet();
		fleet.Prs[Rest[0]] = (77, "CLOSED", Fleets.HeadOf(Rest[0]));
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.Contains("  IssueTracker#77 is CLOSED, not merged: reopen it (gh pr reopen 77 -R mpaulosky/IssueTracker) and land it"
				+ " with land.sh mpaulosky/IssueTracker#77, or delete its branch and resume for a new PR\n", output, StringComparison.Ordinal);
		Assert.Contains("4 of 5 other repo(s) done", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_repo_with_two_stops_is_counted_once_in_the_summary()
	{
		using var fleet = new Fleet { Opens = { [Rest[0]] = 200, [Rest[1]] = 201 }, LandRaises = new GroupLeft("what land.sh left was still there") };
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.Contains("  land.sh: what land.sh left was still there\n", output, StringComparison.Ordinal);
		// land.sh's stop and each PR's are two stops, but two repos wait, not three
		Assert.Contains("rollout: TicketManager and 3 of 5 other repo(s) done; waiting for you:", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_rest_pr_land_sh_closes_is_a_stop()
	{
		using var fleet = new Fleet { Opens = { [Rest[0]] = 200 }, Closes = { $"{Rest[0]}#200" } };
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.Contains("  IssueTracker#200 is CLOSED, not merged: reopen it (gh pr reopen 200 -R mpaulosky/IssueTracker) and land"
				+ " it with land.sh mpaulosky/IssueTracker#200, or delete its branch and resume for a new PR\n", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_land_sh_that_crashes_for_the_rest_still_reads_and_reports_each_pr()
	{
		using var fleet = new Fleet { Opens = { [Rest[0]] = 200, [Rest[1]] = 201 }, LandRaises = new IOException("[Errno 8] Exec format error") };
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.Contains("rollout.sh: land.sh failed: OSError: [Errno 8] Exec format error", output, StringComparison.Ordinal);
		Assert.Contains("  IssueTracker#200 didn't land (#200 still reads as OPEN): land.sh mpaulosky/IssueTracker#200", output, StringComparison.Ordinal);
		Assert.Contains("  Blazor-Server#201 didn't land (#201 still reads as OPEN)", output, StringComparison.Ordinal);
		Assert.EndsWith("resume with: rollout.sh --auto --brings '#82'", output.TrimEnd(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("mpaulosky/TicketManager")]
	[InlineData("mpaulosky/IssueTracker")]
	public void A_crash_whose_checkout_cant_be_told_still_ends_with_its_stop_and_no_command(string failing)
	{
		string Checkout(string repo) => repo == failing ? throw new KeyNotFoundException("no checkout") : $"/r/{Fleets.Short(repo)}";

		using var fleet = new Fleet();
		var (code, output, err) = RoundWithCheckout(fleet, Checkout);
		Assert.Equal(1, code);
		if (failing == TM)
			Assert.Contains("rollout: TicketManager: KeyNotFoundException: no checkout (traceback on stderr); held: IssueTracker,", output, StringComparison.Ordinal);
		else
			Assert.Contains("  IssueTracker: KeyNotFoundException: no checkout (traceback on stderr)\n", output, StringComparison.Ordinal);
		Assert.DoesNotContain("run it by hand", output, StringComparison.Ordinal);
		Assert.Contains($"rollout.sh: {Fleets.Short(failing)} crashed:\nSystem.Collections.Generic.KeyNotFoundException", err, StringComparison.Ordinal);
		Assert.Contains("   at ", err, StringComparison.Ordinal); // the traceback
	}

	[Fact]
	public void A_land_sh_that_cant_start_for_ticketmanager_names_land_sh_not_reapply_sh()
	{
		using var fleet = new Fleet { Opens = { [TM] = 130 }, LandRaises = new IOException("[Errno 8] Exec format error") };
		var (code, output, err) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 1 && fleet.Reapplied().SequenceEqual(["TicketManager"]), output);
		Assert.Contains("rollout: TicketManager#130: land.sh failed: OSError: [Errno 8] Exec format error; land it with land.sh"
				+ " mpaulosky/TicketManager#130; held:", output, StringComparison.Ordinal);
		Assert.DoesNotContain("run it by hand", output, StringComparison.Ordinal);
		Assert.DoesNotContain("crashed", err, StringComparison.Ordinal);
	}

	[Fact]
	public void A_bug_while_landing_ticketmanager_is_a_crash_with_its_traceback()
	{
		using var fleet = new Fleet { Opens = { [TM] = 130 }, LandRaises = new InvalidCastException("a bug") };
		var (code, output, err) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.DoesNotContain("land.sh failed", output, StringComparison.Ordinal);
		Assert.Contains("InvalidCastException: a bug", err, StringComparison.Ordinal);
		Assert.Contains("   at ", err, StringComparison.Ordinal); // the traceback
		Assert.Contains("rollout: TicketManager: InvalidCastException: a bug (traceback on stderr); run it by hand with: land.sh"
				+ " mpaulosky/TicketManager#130; held:", output, StringComparison.Ordinal);
		Assert.DoesNotContain("reapply.sh", output[..output.IndexOf("held:", StringComparison.Ordinal)], StringComparison.Ordinal);
	}

	[Fact]
	public void A_group_ticketmanagers_reapply_left_holds_the_round()
	{
		using var fleet = new Fleet { Raises = { [TM] = Left() } };
		var (code, output, err) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 1 && fleet.Reapplied().SequenceEqual(["TicketManager"]) && err.Length == 0, output + err);
		Assert.Contains($"rollout: TicketManager: {LeftText}; held: IssueTracker,", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_group_one_of_the_rests_reapply_left_is_a_stop_and_the_round_goes_on()
	{
		using var fleet = new Fleet { Raises = { [Articles] = Left() } };
		var (code, output, err) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 1 && fleet.Reapplied().SequenceEqual(All()), output);
		Assert.DoesNotContain("crashed", err, StringComparison.Ordinal);
		Assert.Contains($"  Articles: {LeftText}\n", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_group_ticketmanagers_land_left_holds_the_round_and_names_land_sh()
	{
		using var fleet = new Fleet { Opens = { [TM] = 130 }, LandRaises = Left() };
		var (code, output, err) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.DoesNotContain("crashed", err, StringComparison.Ordinal);
		Assert.Contains($"rollout: TicketManager#130: {LeftText}; then land it with land.sh mpaulosky/TicketManager#130; held:", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_group_the_rests_land_left_is_a_stop_and_each_pr_is_still_read()
	{
		using var fleet = new Fleet { Opens = { [Rest[0]] = 200 }, LandRaises = Left() };
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.Contains($"  land.sh: {LeftText}\n", output, StringComparison.Ordinal);
		Assert.Contains("  IssueTracker#200 didn't land (#200 still reads as OPEN): land.sh mpaulosky/IssueTracker#200", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_crash_on_one_of_the_rest_is_reported_and_the_round_goes_on()
	{
		using var fleet = new Fleet { Raises = { [Articles] = new IOException("[Errno 8] Exec format error") } };
		var (code, output, err) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.Equal(All(), fleet.Reapplied());
		Assert.Contains("  Articles: IOException: [Errno 8] Exec format error (traceback on stderr); run it by hand with: reapply.sh --brings '#82' /r/Articles",
				output, StringComparison.Ordinal);
		Assert.Contains("rollout.sh: Articles crashed:\nSystem.IO.IOException", err, StringComparison.Ordinal);
		Assert.Contains("   at ", err, StringComparison.Ordinal); // the traceback
	}

	[Fact]
	public void Auto_resumes_with_everything_already_merged()
	{
		using var fleet = new Fleet(); // every repo's last round merged, nothing new to push
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 0, output);
		Assert.True(fleet.Landed.Count == 0 && fleet.Reviewed.Count == 0 && fleet.Sleeps.Count == 0);
		Assert.Equal(All(), fleet.Reapplied());
	}

	[Fact]
	public void Auto_dry_run_checks_the_tools_prints_every_step_and_runs_nothing()
	{
		using var fleet = new Fleet { Locked = true }; // a plan is not a round: it takes no lock
		var (code, output, _) = fleet.RunAuto(["--dry-run", "--brings", "#82,#83"]);
		Assert.Equal(0, code);
		Assert.Equal(["check"], Assert.Single(fleet.Events));
		Assert.True(fleet.Landed.Count == 0 && fleet.Reviewed.Count == 0);
		var lines = output.EndsWith('\n') ? output[..^1].Split('\n') : output.Split('\n'); // Python's splitlines
		Assert.Equal("tools: all there", lines[0]);
		foreach (var (line, repo) in lines[1..7].Zip([TM, .. Rest]))
		{
			var name = Fleets.Short(repo);
			Assert.Equal($"would run: reapply.sh --brings '#82,#83' /r/{name}, with /pre-push-review headless in"
					+ $" /r/{name}-worktrees/chore-reapply-baseline when its gate asks for one", line);
		}
		Assert.Equal("then land TicketManager's re-Apply PR with land.sh before the rest start (at once when it already"
				+ " matches the Template), and the rest's together", lines[7]);
		Assert.Equal(8, lines.Length);
	}

	[Fact]
	public void Auto_dry_run_with_a_problem_prints_no_plan()
	{
		using var fleet = new Fleet { Problems = ["claude isn't on PATH"] };
		var (code, output, err) = fleet.RunAuto(["--dry-run", "--brings", "#82"]);
		Assert.True(code == 1 && output.Length == 0, output);
		Assert.Contains("rollout.sh: claude isn't on PATH", err, StringComparison.Ordinal);
	}

	[Fact]
	public void The_round_without_auto_is_unchanged()
	{
		using var fleet = new Fleet();
		var (output, err) = (new StringWriter(), new StringWriter());
		var code = Rollout.Run(["--brings", "#82", "--root", "/r"], output, err, listed: Fleets.Listed, reapply: fleet.Reapply,
				latestPr: _ => (100, "MERGED"), exists: _ => true, auto: fleet.World(), ahead: _ => false);
		Assert.True(code == 0, output.ToString() + err);
		Assert.DoesNotContain(fleet.Events, e => e is ["check"]);
	}

	[Fact]
	public void The_header_says_what_holds_and_what_only_backs_it_up()
	{
		// auto_rollout.py's docstring is AutoRolloutRound.cs's header comment now, its Python names in their C# forms: the comment lines
		// at the top of the file, joined as the pytest joined the docstring.
		var header = File.ReadLines(Skill.Path("github", "AutoRolloutRound.cs")).TakeWhile(line => line.StartsWith("//", StringComparison.Ordinal))
				.Select(line => line[2..]);
		var doc = string.Join(" ", string.Join("\n", header).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		Assert.Contains("so it reviews only, and runs in Claude Code's sandbox (SessionSettings; ADR 0016)", doc, StringComparison.Ordinal);
		Assert.Contains("It edits, fixes and commits nothing, and runs no gate, so nothing it wrote runs later as the person", doc, StringComparison.Ordinal);
		Assert.Contains("the worktree's HEAD, branch or status changed", doc, StringComparison.Ordinal);
		Assert.Contains("can't leave the sandbox; the session refuses to start without the sandbox (bubblewrap and socat, which the preflight checks for)",
				doc, StringComparison.Ordinal);
		Assert.Contains("writes only a temporary folder of its own (SessionWrites: never the worktree, its git folder, the ledgers or .security)", doc,
				StringComparison.Ordinal);
		Assert.Contains("the round records that with the worktree's scripts/review.sh record (RecordReview)", doc, StringComparison.Ordinal);
		Assert.Contains("the worktree's .git link changed", doc, StringComparison.Ordinal);
		Assert.Contains("git's push is sent nowhere: layers behind the sandbox", doc, StringComparison.Ordinal);
		Assert.Contains("a SIGKILL of rollout.sh can't be caught, and leaves the session running (holding the lock until it ends)", doc, StringComparison.Ordinal);
		Assert.Contains("every child the round starts holds the lock as well", doc, StringComparison.Ordinal);
		Assert.Contains("and of their data only the worktree, the clone's git folder, .security and its own folder", doc, StringComparison.Ordinal);
		Assert.Contains("as listed when the session starts, so an entry made after that is readable", doc, StringComparison.Ordinal);
	}
}
