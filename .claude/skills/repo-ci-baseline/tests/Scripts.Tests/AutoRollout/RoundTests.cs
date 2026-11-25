// Tests for github/AutoRolloutRound.cs, AutoRolloutReview.cs and RolloutCore.cs, xUnit copies of github/tests/test_auto_rollout.py's tests
// of open_advisories, the Advisory list shown before the push, and the --auto round through rollout.main on the Fleet's fakes (run_auto):
// the preflight, the lock, the review sessions and how they fail, drive, the printed commands and the root, and how a landed PR is read
// (ADR 0017's Update for mpaulosky/Application_Factory#122): one method each, the same cases and assertions.
[Collection(nameof(RoundCollection))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RoundTests : IDisposable
{
	const string TM = Fleets.TM;
	const string Worktree = "/r/TicketManager-worktrees/chore-reapply-baseline";
	static readonly string Head = new('c', 40);
	readonly string tmp;

	// --auto is Linux only (ADR 0016): each test skips itself off Linux before its folder is made.
	public RoundTests()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		tmp = Directory.CreateTempSubdirectory("auto-round.").FullName;
	}

	public void Dispose() => Directory.Delete(tmp, recursive: true);

	static readonly string[] Brings = ["--brings", "#82"];

	// A fleet whose TicketManager reapply.sh exits these codes in order.
	static Fleet WithCodes(params int[] codes)
	{
		var fleet = new Fleet();
		fleet.Codes[TM] = [.. codes.Select(code => (int?)code)];
		return fleet;
	}

	// Fleet.RunAuto with the World changed, where the pytest replaced one of the Fleet's methods (fleet.review, fleet.pr_state): Fleet.World()
	// binds its own methods, so the change is made on the record instead.
	static (int Code, string Out, string Err) RunAuto(Fleet fleet, World world)
	{
		var (output, err) = (new StringWriter(), new StringWriter());
		var code = Rollout.Run(["--auto", "--root", "/r", .. Brings], output, err, listed: Fleets.Listed, reapply: fleet.Reapply, exists: _ => true,
				auto: world, advisories: _ => null, onSignal: (_, _) => { }, defaultRoot: "/r");
		return (code, output.ToString(), err.ToString());
	}

	// drive_once: one Drive of TicketManager whose gate asks for a review and whose session exits code, having (with head) staged [] kept
	// beside its log with that HEAD; the outcome and where the staged findings are kept.
	static (Outcome Outcome, string Keep) DriveOnce(string folder, int code, string[]? broke = null, bool kept = true)
	{
		var log = Path.Combine(folder, "review.log");
		if (kept)
			ReviewRecord.WriteStaged(ReviewRecord.StagedCopy(log), "[]", Head);
		List<string> said = [];
		var outcome = AutoRound.Drive(TM, "/r/TicketManager", "#82", (_, _) => AutoRound.ReviewNeeded,
				(_, _) => (code, log, [.. broke ?? []]), said.Add, _ => null);
		return (outcome, ReviewRecord.StagedCopy(log));
	}

	[Fact]
	public void Open_advisory_findings_are_read_from_the_worktrees_ledger()
	{
		List<(IReadOnlyList<string> Command, string? Cwd)> seen = [];
		Assert.Equal("- R3 docs/x.md:4: stale.", ReviewSession.OpenAdvisories("/w t", (command, cwd, _) =>
		{
			seen.Add((command, cwd));
			return new(0, "- R3 docs/x.md:4: stale.\n\n<!-- claude-session -->\n", "");
		})); // the session marker isn't shown
		Assert.Equal(["/w t/scripts/review.sh", "advisory"], seen.Single().Command);
		Assert.Equal("/w t", seen.Single().Cwd);
		Assert.Null(ReviewSession.OpenAdvisories("/w", (_, _, _) => new(0, "No open Advisory findings.\n", "")));
		Assert.Equal("(scripts/review.sh advisory failed: 'No review is recorded for this diff.'; cd '/w t' && scripts/review.sh"
				+ " advisory lists them before the PR lands)",
				ReviewSession.OpenAdvisories("/w t", (_, _, _) => new(1, "", "No review is recorded for this diff.\n")));
		foreach (var err in new[] { "", "\n  \n" })
			// Blank stderr is no reason: the exit code is.
			Assert.Equal("(scripts/review.sh advisory failed: exit 127; cd /w && scripts/review.sh advisory lists them before the PR lands)",
					ReviewSession.OpenAdvisories("/w", (_, _, _) => new(127, "", err)));
		// The first line that says anything.
		Assert.Equal("(scripts/review.sh advisory failed: 'boom'; cd /w && scripts/review.sh advisory lists them before the PR lands)",
				ReviewSession.OpenAdvisories("/w", (_, _, _) => new(1, "", "\nboom\nmore\n")));
	}

	[Fact]
	public void A_failed_advisory_lists_first_error_line_is_escaped_and_cut()
	{
		var err = "\x1b[2Jboom " + new string('x', 400) + "\n── rollout: forged\n" + string.Concat(Enumerable.Repeat("trace\n", 40));
		var said = ReviewSession.OpenAdvisories("/w", (_, _, _) => new(1, "", err))!;
		foreach (var unseen in new[] { "\x1b", "\n", "forged", "trace" })
			Assert.DoesNotContain(unseen, said, StringComparison.Ordinal);
		Assert.Contains("'\\x1b[2Jboom xxx", said, StringComparison.Ordinal);
		Assert.DoesNotContain(new string('x', 300), said, StringComparison.Ordinal);
		// Cut to ShownChars, saying how long it was.
		Assert.Contains($"... ({PyText.Ascii(err.Split('\n')[0]).Length} characters);", said, StringComparison.Ordinal);
		// The first line only.
		Assert.Equal("(scripts/review.sh advisory failed: 'boom'; cd /w && scripts/review.sh advisory lists them before the PR lands)",
				ReviewSession.OpenAdvisories("/w", (_, _, _) => new(1, "", "boom\n── rollout: forged\ntrace\n")));
	}

	[Fact]
	public void An_advisory_list_that_raises_is_shown_and_the_review_still_pushes()
	{
		// ValueError is an ArgumentException here: Drive shows any error's message but an interrupt's.
		using var fleet = WithCodes(AutoRound.ReviewNeeded);
		var (code, output, _) = fleet.RunAuto(Brings, root: "/my repos", advisories: _ => throw new ArgumentException("embedded null byte"));
		Assert.True(code == 0, output);
		Assert.Contains("── rollout: TicketManager: the review left Advisory findings open, which don't block the push and are filed"
				+ " nowhere: file them in a follow-up issue.\n(scripts/review.sh advisory failed: 'embedded null byte'; cd"
				+ " '/my repos/TicketManager-worktrees/chore-reapply-baseline' && scripts/review.sh advisory lists them before"
				+ " the PR lands)\n", output, StringComparison.Ordinal);
	}

	[Fact]
	public void No_advisory_list_is_shown_when_none_is_open_or_after_a_failed_session()
	{
		List<string> asked = [];
		string? NoAdvisories(string worktree)
		{
			asked.Add(worktree);
			return null;
		}

		using var fleet = WithCodes(AutoRound.ReviewNeeded);
		var (code, output, _) = fleet.RunAuto(Brings, advisories: NoAdvisories);
		Assert.True(code == 0 && !output.Contains("Advisory", StringComparison.Ordinal), output);
		Assert.Equal([Worktree], asked); // read once the review is recorded
		asked.Clear();
		using var failed = WithCodes(AutoRound.ReviewNeeded);
		failed.ReviewCodes[TM] = 1;
		(code, _, _) = failed.RunAuto(Brings, advisories: NoAdvisories);
		Assert.True(code == 1 && asked.Count == 0, "a session that recorded nothing has no list to read");
	}

	[Fact]
	public void A_recorded_reviews_open_advisory_findings_are_shown_before_the_push()
	{
		using var fleet = WithCodes(AutoRound.ReviewNeeded);
		var (code, output, _) = fleet.RunAuto(Brings, advisories: worktree => $"- R3 in {worktree}");
		Assert.True(code == 0, output);
		var said = "── rollout: TicketManager: the review left Advisory findings open, which don't block the push and are filed"
				+ $" nowhere: file them in a follow-up issue.\n- R3 in {Worktree}";
		Assert.Contains(said, output, StringComparison.Ordinal);
		Assert.True(output.IndexOf(said, StringComparison.Ordinal) < output.LastIndexOf("── rollout: TicketManager\n", StringComparison.Ordinal),
				"shown before reapply.sh pushes and the PR lands");
	}

	[Fact]
	public void Auto_reviews_reapplies_and_lands_ticketmanager_then_the_rest()
	{
		using var fleet = WithCodes(AutoRound.ReviewNeeded);
		fleet.Opens = Fleets.AllOpen();
		var (code, output, err) = fleet.RunAuto(Brings);
		Assert.True(code == 0, output + err);
		Assert.Equal([(TM, Worktree)], fleet.Reviewed);
		Assert.Equal(["TicketManager", "TicketManager", .. Fleets.Rest.Select(Fleets.Short)], fleet.Reapplied());
		Assert.Equal(2, fleet.Landed.Count);
		Assert.Equal([$"{TM}#130"], fleet.Landed[0]);
		Assert.Equal([.. Fleets.Rest.Select((repo, n) => $"{repo}#{200 + n}")], fleet.Landed[1]);
		Assert.Contains("rollout: TicketManager and 5 of 5 other repo(s) done\n", output, StringComparison.Ordinal);
		Assert.DoesNotContain("resume with", output, StringComparison.Ordinal);
		Assert.Equal("", err);
	}

	[Fact]
	public void The_lock_is_held_for_the_whole_round_and_released_after()
	{
		using var fleet = new Fleet { Opens = Fleets.AllOpen() };
		fleet.RunAuto(Brings);
		Assert.Single(fleet.Locks);
		// Held while each repo runs.
		Assert.All(fleet.Events.Where(e => (string)e[0] == "reapply"), e => Assert.Equal([false], (List<bool>)e[4]));
		Assert.True(Fleet.Closed(fleet.Locks[0]));
	}

	[Fact]
	public void Auto_runs_the_preflight_first_and_lands_ticketmanager_before_any_other_reapply()
	{
		using var fleet = new Fleet { Opens = new() { [TM] = 130, [Fleets.Rest[0]] = 200 } };
		fleet.RunAuto(Brings);
		Assert.Equal(["check", "reapply", "land", "reapply"], fleet.Events.Take(4).Select(e => (string)e[0]));
		Assert.Equal("IssueTracker", fleet.Events[3][1]);
	}

	[Fact]
	public void Auto_stops_on_a_preflight_problem_with_nothing_run_and_no_lock()
	{
		using var fleet = new Fleet { Problems = ["shfmt isn't on PATH", "gh isn't signed in: gh auth login"] };
		var (code, _, err) = fleet.RunAuto(Brings);
		Assert.Equal(1, code);
		Assert.Equal("check", (string)fleet.Events.Single().Single());
		Assert.Empty(fleet.Locks);
		Assert.Equal("rollout.sh: shfmt isn't on PATH\nrollout.sh: gh isn't signed in: gh auth login\nrollout.sh: nothing run;"
				+ " fix those first, then run: rollout.sh --auto --brings '#82'\n", err);
		using var other = new Fleet { Problems = ["x"] };
		(_, _, err) = other.RunAuto(Brings, root: "/my repos");
		Assert.EndsWith("then run: rollout.sh --auto --brings '#82' --root '/my repos'\n", err, StringComparison.Ordinal); // as it runs
	}

	[Fact]
	public void Auto_stops_when_another_run_holds_the_lock()
	{
		using var fleet = new Fleet { Locked = true };
		var (code, _, err) = fleet.RunAuto(Brings);
		Assert.Equal(1, code);
		Assert.Empty(fleet.Reapplied());
		var held = AutoRollout.Lock;
		Assert.Equal($"rollout.sh: another rollout.sh --auto holds {held}, or a review session or gate an earlier one started"
				+ $" is still running; nothing run. See what holds it with: fuser -v {held} (or lsof {held}), and end"
				+ " it only when no round is running; then run: rollout.sh --auto --brings '#82'\n", err);
	}

	[Fact]
	public void Auto_stops_when_the_lock_cannot_be_taken()
	{
		// PermissionError is UnauthorizedAccessException here, given Python's message, which the round prints as the error's own.
		using var fleet = new Fleet { LockRaises = new UnauthorizedAccessException("[Errno 13] Permission denied") };
		var (code, _, err) = fleet.RunAuto(Brings);
		Assert.True(code == 1 && fleet.Reapplied().Count == 0);
		Assert.Contains($"rollout.sh: can't take the lock {AutoRollout.Lock}: [Errno 13] Permission denied; nothing run. Once it can be"
				+ " taken, run: rollout.sh --auto --brings '#82'\n", err, StringComparison.Ordinal);
	}

	[Fact]
	public void Auto_a_missing_checkout_stops_before_the_preflight()
	{
		using var fleet = new Fleet();
		var (output, err) = (new StringWriter(), new StringWriter());
		var code = Rollout.Run(["--auto", "--brings", "#82", "--root", "/r"], output, err, listed: Fleets.Listed, reapply: fleet.Reapply,
				exists: path => !path.EndsWith("Articles", StringComparison.Ordinal), auto: fleet.World(), onSignal: (_, _) => { });
		Assert.True(code == 1 && fleet.Events.Count == 0);
		Assert.Contains("no checkout at /r/Articles", err.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void Auto_stops_for_a_person_when_findings_wait_and_holds_the_rest()
	{
		using var fleet = WithCodes(AutoRound.ReviewNeeded, AutoRound.FindingsWait);
		var (code, output, _) = fleet.RunAuto(Brings);
		Assert.Equal(1, code);
		Assert.Equal(["TicketManager", "TicketManager"], fleet.Reapplied());
		Assert.Contains($"TicketManager: review findings wait for you (cd {Worktree} && scripts/review.sh report): fix them with"
				+ " /pre-push-review there (one in an Owned file goes to the Factory's template/owned/ first: add that PR to"
				+ $" --brings), or accept them: cd {Worktree} && scripts/review.sh accept-open", output, StringComparison.Ordinal);
		Assert.Contains("held: IssueTracker, Blazor-Server, IssueManager, Articles, SuggestionApp", output, StringComparison.Ordinal);
		Assert.Contains("resume with: rollout.sh --auto --brings '#82'\n", output, StringComparison.Ordinal);
		Assert.Empty(fleet.Landed);
	}

	[Fact]
	public void The_printed_commands_quote_a_root_with_a_space_and_carry_it_when_not_the_default()
	{
		using var fleet = WithCodes(1);
		var (code, output, _) = fleet.RunAuto(Brings, root: "/my repos");
		Assert.Equal(1, code);
		Assert.Contains("retry it with: reapply.sh --brings '#82' '/my repos/TicketManager'", output, StringComparison.Ordinal);
		Assert.Contains("resume with: rollout.sh --auto --brings '#82' --root '/my repos'", output, StringComparison.Ordinal);
		using var waits = WithCodes(AutoRound.ReviewNeeded, AutoRound.FindingsWait);
		(_, output, _) = waits.RunAuto(Brings, root: "/my repos");
		Assert.Contains("cd '/my repos/TicketManager-worktrees/chore-reapply-baseline' && scripts/review.sh accept-open", output, StringComparison.Ordinal);
		using var failed = WithCodes(AutoRound.ReviewNeeded);
		failed.ReviewCodes[TM] = 1;
		(_, output, _) = failed.RunAuto(Brings, root: "/my repos");
		Assert.Contains("run /pre-push-review in '/my repos/TicketManager-worktrees/chore-reapply-baseline', then resume", output, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("/r/")]
	[InlineData("/r/../r")]
	[InlineData("/r")]
	public void A_root_equal_to_the_default_is_left_out(string root)
	{
		using var fleet = WithCodes(1);
		var (_, output, _) = fleet.RunAuto(Brings, root: root);
		Assert.Contains("resume with: rollout.sh --auto --brings '#82'\n", output, StringComparison.Ordinal);
	}

	[Fact]
	public void The_default_root_is_rollouts_own()
	{
		var github = Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "github"); // os.path.expanduser("~/github")
		using var fleet = WithCodes(1);
		var (_, output, _) = fleet.RunAuto(Brings, root: github, defaultRoot: null);
		Assert.Contains("resume with: rollout.sh --auto --brings '#82'\n", output, StringComparison.Ordinal);
		Assert.Equal(github, Rollout.DefaultRoot);
	}

	[Fact]
	public void Auto_gives_up_after_two_review_sessions_when_the_gate_still_asks()
	{
		using var fleet = WithCodes(AutoRound.ReviewNeeded, AutoRound.ReviewNeeded, AutoRound.ReviewNeeded);
		var (code, output, _) = fleet.RunAuto(Brings);
		Assert.Equal(1, code);
		Assert.Equal((2, 3), (fleet.Reviewed.Count, fleet.Reapplied().Count));
		Assert.Contains("the gate still asks for a review after 2 review session(s): main or the Template may have moved", output, StringComparison.Ordinal);
		Assert.Contains("moved since the last one (see /tmp/review-TicketManager.log); run /pre-push-review in", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Auto_reviews_a_second_time_when_the_first_session_left_no_record()
	{
		using var fleet = WithCodes(AutoRound.ReviewNeeded, AutoRound.ReviewNeeded);
		fleet.Opens[TM] = 130;
		var (code, output, _) = fleet.RunAuto(Brings);
		Assert.True(code == 0, output);
		Assert.Equal(2, fleet.Reviewed.Count);
	}

	[Theory]
	[InlineData(124, "ran past 180 minutes and was stopped")]
	[InlineData(127, "couldn't be started: no log could be made")]
	[InlineData(1, "exited 1")]
	[InlineData(ReviewRecord.RecordFailed, "staged findings the round couldn't record (scripts/review.sh prepare or record failed, or the"
			+ " round did; the log says why)")]
	[InlineData(ReviewRecord.NothingStaged, "exited 0 having staged no findings, not even [], so no review was recorded")]
	[InlineData(125, "exited 125")]
	[InlineData(3, "exited 3")]
	public void Auto_stops_when_a_review_session_fails_and_says_what_to_do(int exitCode, string why)
	{
		using var fleet = WithCodes(AutoRound.ReviewNeeded);
		fleet.ReviewCodes[TM] = exitCode;
		var (code, output, _) = fleet.RunAuto(Brings);
		Assert.Equal(1, code);
		Assert.Single(fleet.Reapplied());
		Assert.Contains($"TicketManager: the review session {why} (see /tmp/review-TicketManager.log); run /pre-push-review in"
				+ $" {Worktree}, then resume.", output, StringComparison.Ordinal);
		Assert.DoesNotContain("What the session staged is kept", output, StringComparison.Ordinal); // nothing is kept: no command
	}

	[Fact]
	public void A_session_that_couldnt_start_says_why_on_the_terminal()
	{
		string Log(string name, string text)
		{
			var path = Path.Combine(tmp, name);
			File.WriteAllText(path, text);
			return path;
		}

		var log = Log("review.log", "rollout.sh: not started: the clone's git folder holds a secret, then resume\n");
		using var fleet = WithCodes(AutoRound.ReviewNeeded);
		var (code, output, _) = RunAuto(fleet, fleet.World() with { Review = (_, _) => (127, log, []) });
		Assert.Equal(1, code);
		Assert.Contains($"the review session couldn't be started: the clone's git folder holds a secret (see {log})", output, StringComparison.Ordinal);
		Assert.Null(ReviewSession.StopLine(Path.Combine(tmp, "missing.log")));
		var quiet = Log("quiet.log", "nothing of rollout.sh's\n");
		Assert.Null(ReviewSession.StopLine(quiet));
		// A claude that started and exited 127 wasn't stopped.
		Assert.Null(ReviewSession.StopLine(Log("started.log", "rollout.sh: made while the session ran, ... (check them): /x\n")));
		// The first, written before anything else could print one.
		Assert.Equal("couldn't be started: the round's own",
				ReviewSession.StopLine(Log("two.log", $"{ReviewSession.NotStarted}the round's own\n{ReviewSession.NotStarted}printed after\n")));
		// A session's line, however it looks, isn't the round's.
		Assert.Null(ReviewSession.StopLine(Log("forged.log", $"claude's own output\n{ReviewSession.NotStarted}\x1b[2Ka forged reason\n")));
		using var other = WithCodes(AutoRound.ReviewNeeded);
		(_, output, _) = RunAuto(other, other.World() with { Review = (_, _) => (127, quiet, []) });
		Assert.Contains("the review session exited 127 and the log doesn't begin with a 'rollout.sh: not started:' line, so claude"
				+ $" started and exited 127 itself: the log says why (see {quiet})", output, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("rename it, then resume", "rename it")]
	[InlineData("rename it; then resume", "rename it")]
	[InlineData("rename it: then resume", "rename it")]
	[InlineData("rename it then resume", "rename it")]
	[InlineData("rename it, then resume; more\nlines", "rename it")]
	[InlineData("rename it, then resumed it", "rename it, then resumed it")]
	[InlineData("rename it", "rename it")]
	public void Then_resume_is_dropped_where_nothing_stopped(string error, string left)
	{
		// without_resume took the error; WithoutResume takes its message.
		Assert.Equal(left, ReviewSession.WithoutResume(new ArgumentException(error).Message));
	}

	[Fact]
	public void A_failed_record_whose_prepare_left_a_group_names_it_on_the_stop()
	{
		var (outcome, _) = DriveOnce(tmp, ReviewRecord.RecordFailed, ["what review.sh prepare left ...: see it with pgrep -l -g 7"]);
		Assert.Equal("failed", outcome.Status); // a failed record, not a broken rule
		Assert.Contains("staged findings the round couldn't record (scripts/review.sh prepare or record failed, or the round did;"
				+ " the log says why): what review.sh prepare left ...: see it with pgrep -l -g 7 (see", outcome.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public void A_stop_with_findings_kept_prints_the_command_that_records_them()
	{
		var (outcome, keep) = DriveOnce(tmp, ReviewRecord.RecordFailed);
		var command = ReviewRecord.RecordCommand(Worktree, keep, Head);
		Assert.Equal("failed", outcome.Status);
		Assert.EndsWith($"then resume. What the session staged is kept in {keep}; record it, once that is put right, with: {command}",
				outcome.Detail, StringComparison.Ordinal);
		var none = Directory.CreateDirectory(Path.Combine(tmp, "none")).FullName;
		(outcome, _) = DriveOnce(none, ReviewRecord.RecordFailed, kept: false);
		Assert.EndsWith("then resume.", outcome.Detail, StringComparison.Ordinal); // nothing kept, so no command
	}

	[Fact]
	public void A_group_the_record_left_stops_for_the_person_saying_it_may_have_recorded()
	{
		var (outcome, keep) = DriveOnce(tmp, ReviewSession.RecordLeft, ["what review.sh record left in its process group"]);
		Assert.Equal("person", outcome.Status);
		Assert.StartsWith("the round's record of the review didn't end cleanly: what review.sh record left in"
				+ " its process group. Whether the review was recorded can't be told: cd"
				+ $" {Worktree} && scripts/review.sh report shows it", outcome.Detail, StringComparison.Ordinal);
		Assert.Contains($"record it, once that is put right, with: {ReviewRecord.RecordCommand(Worktree, keep, Head)}", outcome.Detail,
				StringComparison.Ordinal);
	}

	[Fact]
	public void Auto_holds_the_rest_when_ticketmanagers_pr_does_not_land()
	{
		using var fleet = new Fleet { Opens = new() { [TM] = 130 }, Unlanded = [$"{TM}#130"] };
		var (code, output, _) = fleet.RunAuto(Brings);
		Assert.Equal(1, code);
		Assert.Equal(["TicketManager"], fleet.Reapplied());
		Assert.Contains("TicketManager#130 didn't land (see land.sh's output above): land.sh mpaulosky/TicketManager#130", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_landed_pr_is_read_by_its_number_even_after_land_sh_updated_its_branch()
	{
		using var fleet = new Fleet { Opens = new() { [TM] = 130, [Fleets.Rest[0]] = 200 }, Moves = [$"{TM}#130", $"{Fleets.Rest[0]}#200"] };
		var (code, output, _) = fleet.RunAuto(Brings);
		Assert.True(code == 0, output);
		Assert.Empty(fleet.Sleeps);
	}

	[Fact]
	public void Auto_waits_for_a_landed_pr_to_read_as_merged()
	{
		using var fleet = new Fleet { Opens = new() { [TM] = 130 } };
		var still = 2;
		string Lagging(string repo, long number)
		{
			var state = fleet.PrState(repo, number);
			if (repo == TM && state == "MERGED" && still > 0)
			{
				still--;
				return "OPEN";
			}
			return state;
		}

		var (code, output, _) = RunAuto(fleet, fleet.World() with { PrState = Lagging });
		Assert.True(code == 0, output);
		Assert.Equal([10, 10], fleet.Sleeps);
	}

	[Fact]
	public void Auto_holds_when_a_landed_pr_never_reads_as_merged()
	{
		using var fleet = new Fleet { Opens = new() { [TM] = 130 }, StillOpen = [$"{TM}#130"] };
		var (code, output, _) = fleet.RunAuto(Brings);
		Assert.Equal(1, code);
		Assert.Contains("TicketManager: can't tell its re-Apply PR (#130 still reads as OPEN)", output, StringComparison.Ordinal);
		Assert.Equal([10, 10, 10, 10], fleet.Sleeps);
		Assert.Equal(["TicketManager"], fleet.Reapplied());
	}

	[Fact]
	public void A_landed_pr_that_was_closed_holds_the_round()
	{
		using var fleet = new Fleet { Opens = new() { [TM] = 130 }, Closes = [$"{TM}#130"] };
		var (code, output, _) = fleet.RunAuto(Brings);
		Assert.Equal(1, code);
		Assert.Contains($"TicketManager#130 is CLOSED, not merged: reopen it (gh pr reopen 130 -R {TM}) and land it with"
				+ $" land.sh {TM}#130, or delete its branch and resume for a new PR", output, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(AutoRound.LookupTries - 1, true)]
	[InlineData(AutoRound.LookupTries, false)]
	public void A_stale_read_of_the_last_rounds_merged_pr_never_releases_the_rest(int reads, bool lands)
	{
		// The newest PR by the branch's name is the last round's merged one until the list shows the new PR.
		using var fleet = new Fleet { Opens = new() { [TM] = 130 }, Stale = new() { [TM] = reads } };
		var (code, output, _) = fleet.RunAuto(Brings);
		if (lands)
		{
			Assert.True(code == 0, output);
			Assert.Equal([$"{TM}#130"], fleet.Landed[0]); // the new PR is landed, not skipped as merged
			Assert.Equal(Enumerable.Repeat(10.0, reads), fleet.Sleeps);
		}
		else
		{
			Assert.True(code == 1 && fleet.Landed.Count == 0, output);
			Assert.Equal(["TicketManager"], fleet.Reapplied());
			Assert.Contains("#99 is at fffffff, the worktree at ticketm", output, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void An_open_pr_at_another_head_is_not_this_rounds()
	{
		// Nothing ahead of main, and an OPEN PR at another head (its branch moved): not landed.
		using var fleet = new Fleet();
		fleet.Prs[TM] = (131, "OPEN", new string('a', 40));
		var (code, output, _) = fleet.RunAuto(Brings);
		Assert.True(code == 1 && fleet.Landed.Count == 0, output);
		Assert.Contains("TicketManager: can't tell its re-Apply PR (#131 is at aaaaaaa, the worktree at ticketm)", output, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(AutoRound.LookupTries - 1, true)]
	[InlineData(AutoRound.LookupTries, false)]
	public void A_gh_that_fails_and_recovers_is_tried_again(int fails, bool ok)
	{
		using var fleet = new Fleet { Opens = new() { [TM] = 130 }, Failures = new() { [TM] = fails } };
		var (code, output, _) = fleet.RunAuto(Brings);
		Assert.True(code == 0 == ok, output);
		if (!ok)
			Assert.Contains("TicketManager: can't tell its re-Apply PR (gh failed: HTTP 502)", output, StringComparison.Ordinal);
	}
}
