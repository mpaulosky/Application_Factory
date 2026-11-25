using System.Globalization;
using System.Text.RegularExpressions;

// Tests for github/Landing.cs: fixture PR states in, one action out. No GitHub.
public class LandingTests
{
	static readonly string Head = new('a', 40);
	static readonly string Old = new('b', 40);
	static readonly string C40 = new('c', 40), D40 = new('d', 40), E40 = new('e', 40), F40 = new('f', 40);

	static HashSet<string> Set(params string[] items) => [.. items];

	// A set's members in ordinal order, so two sets compare as sequences.
	static string[] Sorted(IEnumerable<string> items) => [.. items.Order(StringComparer.Ordinal)];

	static Check Passed(string name, string started = "2026-10-07T10:00:00Z") => new(name, "COMPLETED", "SUCCESS", started);

	static Check Running(string name, string started = "2026-10-07T10:05:00Z") => new(name, "IN_PROGRESS", null, started);

	static Check Failed(string name, string started = "2026-10-07T10:00:00Z") => new(name, "COMPLETED", "FAILURE", started);

	static Check Cancelled(string name, string started = "2026-10-07T10:00:00Z") => new(name, "COMPLETED", "CANCELLED", started);

	// A PR ready to arm: Copilot reviewed the head, no open thread, checks green.
	static readonly PrState Ready = new() { Head = Head, CopilotReviewed = Set(Old, Head), MergeState = "CLEAN", Checks = [Passed("Build Solution"), Passed("Test Suite")] };

	[Fact]
	public void A_reviewed_green_pr_is_armed()
	{
		Assert.Equal(Landing.ArmAutoMerge, Landing.Decide(Ready).Action);
	}

	[Fact]
	public void A_draft_waits_unless_the_caller_wants_it_ready()
	{
		var draft = Ready with { Draft = true };
		Assert.Equal(new Decision(Landing.Wait, "draft"), Landing.Decide(draft));
		Assert.Equal(Landing.MarkReady, Landing.Decide(draft with { WantReady = true }).Action);
	}

	[Fact]
	public void A_head_copilot_has_not_reviewed_waits_while_copilot_is_requested()
	{
		var decision = Landing.Decide(Ready with { CopilotReviewed = Set(Old), CopilotRequested = true });
		Assert.Equal(Landing.Wait, decision.Action);
		Assert.Contains("no Copilot or Claude review of aaaaaaa", decision.Reason);
		Assert.Contains("Copilot is requested", decision.Reason);
	}

	[Fact]
	public void An_unreviewed_head_waits_while_review_claude_is_on()
	{
		// Copilot answered the head with its quota note, so the label stands (with Copilot available it is ignored, #378).
		var decision = Landing.Decide(Ready with { CopilotReviewed = Set(Old), CopilotDeclined = Set(Head), ClaudeRequested = true });
		Assert.Equal(new Decision(Landing.Wait, "no Copilot or Claude review of aaaaaaa yet; review:claude is on"), decision);
	}

	[Fact]
	public void A_claude_review_without_a_quota_note_doesnt_keep_the_label_waiting()
	{
		// Claude reviewed the head but Copilot posted no note: that covers nothing and no more is coming (#270).
		var labelled = Ready with { CopilotReviewed = Set(Old), ClaudeRequested = true, ClaudeReviewed = Set(Head) };
		Assert.Equal(Landing.RequestCopilot, Landing.Decide(labelled).Action);
		// Unless the request was dropped (#318): the ask settled with no request pending, so Copilot can't review this head and Claude's review counts.
		Assert.Equal(Landing.ArmAutoMerge, Landing.Decide(labelled with { CopilotAsked = true, CopilotAskSettled = true }).Action);
	}

	[Fact]
	public void Copilot_is_asked_first_for_a_head_copilot_reviewed_before()
	{
		// Copilot reviewed an older head and nothing is requested now.
		var decision = Landing.Decide(Ready with { CopilotReviewed = Set(Old) });
		Assert.Equal(Landing.RequestCopilot, decision.Action);
		Assert.EndsWith("Copilot not asked yet", decision.Reason);
	}

	[Fact]
	public void Claude_is_called_in_once_copilot_answered_the_head_with_its_quota_note_and_checks_are_green()
	{
		var declined = Ready with { CopilotReviewed = Set(Old), CopilotDeclined = Set(Head) };
		Assert.Equal(Landing.RequestClaude, Landing.Decide(declined).Action);
		var waiting = Landing.Decide(declined with { Checks = [Passed("Build Solution"), Running("Test Suite")] });
		Assert.Equal(Landing.Wait, waiting.Action);
		Assert.EndsWith("waiting for checks before calling in Claude: Test Suite", waiting.Reason);
		// A failed check is a blocker: no Claude review of a head that fails CI.
		Assert.Equal(Landing.Blocker, Landing.Decide(declined with { Checks = [Failed("Test Suite")] }).Action);
	}

	[Fact]
	public void A_dropped_request_calls_in_claude_like_a_quota_note_does()
	{
		// Copilot can't review a head whose request GitHub dropped, so Claude backs it up (#318).
		var dropped = Ready with { CopilotReviewed = Set(Old), CopilotAsked = true, CopilotAskSettled = true };
		var decision = Landing.Decide(dropped);
		Assert.Equal(Landing.RequestClaude, decision.Action);
		Assert.EndsWith("Copilot couldn't review (its request was dropped or its quota is used up), checks green", decision.Reason);
		var waiting = Landing.Decide(dropped with { Checks = [Passed("Build Solution"), Running("Test Suite")] });
		Assert.Equal(Landing.Wait, waiting.Action);
		Assert.EndsWith("waiting for checks before calling in Claude: Test Suite", waiting.Reason);
		Assert.Equal(Landing.Blocker, Landing.Decide(dropped with { Checks = [Failed("Test Suite")] }).Action);
		// Without a drop remembered for this head, Claude's review of it covers nothing: Copilot is asked again.
		Assert.Equal(Landing.RequestCopilot, Landing.Decide(Ready with { CopilotReviewed = Set(Old), ClaudeReviewed = Set(Head) }).Action);
		// Claude's earlier reviews stall nothing: the loop's end is dirty rounds in a row, not a count of reviewed commits (#375).
		var used = dropped with { ClaudeReviewed = Set(C40, D40, E40, Old) };
		Assert.Equal(Landing.RequestClaude, Landing.Decide(used).Action);
	}

	[Fact]
	public void A_registered_copilot_request_waits_even_after_an_earlier_drop()
	{
		var decision = Landing.Decide(Ready with { CopilotReviewed = Set(Old), CopilotRequested = true, CopilotAsked = true, CopilotAskSettled = true });
		Assert.Equal(Landing.Wait, decision.Action);
		Assert.EndsWith("Copilot is requested", decision.Reason);
	}

	[Fact]
	public void No_checks_is_not_green_even_where_checks_arent_required()
	{
		var dropped = Ready with { CopilotReviewed = Set(Old), CopilotDeclined = Set(Head), Checks = [], ChecksRequired = false };
		Assert.Equal(Landing.Wait, Landing.Decide(dropped).Action);
	}

	// Rounds, oldest first, one per token of the pattern: "d" a dirty round, "c" a clean one.
	static Round[] Rounds(string pattern) =>
			[.. pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((token, index) =>
					new Round((index + 1).ToString("x40", CultureInfo.InvariantCulture), token == "d", $"2026-01-02T00:{index.ToString("00", CultureInfo.InvariantCulture)}:00Z"))];

	// A repo whose workflow merges, the head unreviewed, three dirty rounds behind it: the loop is exhausted (mpaulosky/dotfiles#375).
	static readonly PrState Exhausted = Ready with { MergedByWorkflow = true, CopilotReviewed = Set(Old), Rounds = Rounds("d d d") };

	[Fact]
	public void An_exhausted_loop_is_left_to_pr_auto_merge_ready_to_merge_with_the_threads_left_open()
	{
		var decision = Landing.Decide(Exhausted);
		Assert.Equal(Landing.HandOff, decision.Action);
		Assert.StartsWith("review loop exhausted", decision.Reason);
		Assert.Contains("ready to merge", decision.Reason);
		Assert.Contains("0 thread(s) left open", decision.Reason);
		// Neither reviewer is asked for, and the threads and off-diff findings that were open no longer hold it.
		var openThreads = Landing.Decide(Exhausted with { OpenThreads = 2 });
		Assert.Equal(Landing.HandOff, openThreads.Action);
		Assert.Contains("2 thread(s) left open", openThreads.Reason);
		var offDiff = Landing.Decide(Exhausted with { ClaudeOffDiff = Set(Head), ClaudeReviewed = Set(Head) });
		Assert.Equal(Landing.HandOff, offDiff.Action);
	}

	[Fact]
	public void An_exhausted_loop_still_waits_for_checks_and_conflicts_and_updates_a_behind_branch()
	{
		Assert.Equal(Landing.Blocker, Landing.Decide(Exhausted with { Checks = [Failed("Test Suite")] }).Action);
		Assert.Equal(Landing.Blocker, Landing.Decide(Exhausted with { MergeState = "DIRTY" }).Action);
		Assert.Equal(Landing.UpdateBranch, Landing.Decide(Exhausted with { MergeState = "BEHIND" }).Action);
		Assert.Equal(Landing.Wait, Landing.Decide(Exhausted with { Draft = true }).Action);
		var runningChecks = Landing.Decide(Exhausted with { Checks = [Passed("Build Solution"), Running("Test Suite")] });
		Assert.Equal(Landing.HandOff, runningChecks.Action);
		Assert.EndsWith(", required checks running", runningChecks.Reason);
		Assert.Equal(Landing.Nothing, Landing.Decide(Exhausted with { AutoMergeArmed = true }).Action);
	}

	[Fact]
	public void An_exhausted_loop_holds_a_repo_that_requires_no_checks_until_they_are_green()
	{
		var waiting = Landing.Decide(Exhausted with { ChecksRequired = false, Checks = [Passed("Build Solution"), Running("Test Suite")] });
		Assert.Equal(Landing.Wait, waiting.Action);
		Assert.Contains("no required checks", waiting.Reason);
	}

	[Theory]
	[InlineData("d d d", true)]
	[InlineData("d d d d", true)]
	[InlineData("c d d d", true)]
	[InlineData("d d c d", false)]
	[InlineData("d d", false)]
	[InlineData("d d d c", false)]
	[InlineData("c d d", false)]
	[InlineData("", false)]
	public void The_loop_is_exhausted_after_max_claude_rounds_dirty_rounds_in_a_row(string pattern, bool exhausted)
	{
		var pr = Exhausted with { Rounds = Rounds(pattern) };
		Assert.Equal(exhausted, pr.LoopExhausted);
		if (exhausted)
			Assert.Equal(Landing.HandOff, Landing.Decide(pr).Action);
		else
			// The head is still reviewed before it merges.
			Assert.Equal(Landing.RequestCopilot, Landing.Decide(pr).Action);
	}

	[Fact]
	public void A_loop_is_only_exhausted_in_a_repo_whose_workflow_merges_and_whose_loop_was_read()
	{
		Assert.False((Exhausted with { MergedByWorkflow = false }).LoopExhausted);
		Assert.Equal(Landing.RequestCopilot, Landing.Decide(Exhausted with { MergedByWorkflow = false }).Action);
		// Comments or label events that couldn't be read: never exhausted, so a thread still holds and a review is still wanted.
		var unread = Exhausted with { LoopUnread = true };
		Assert.False(unread.LoopExhausted);
		Assert.Equal(Landing.RequestCopilot, Landing.Decide(unread).Action);
		Assert.Equal(Landing.Wait, Landing.Decide(Ready with { MergedByWorkflow = true, Rounds = Rounds("d d d"), LoopUnread = true, OpenThreads = 1 }).Action);
	}

	[Fact]
	public void A_reviewed_head_below_exhaustion_still_waits_on_an_open_thread_and_off_diff_findings()
	{
		var reviewed = Ready with { MergedByWorkflow = true, Rounds = Rounds("d d") };
		Assert.Equal(Landing.HandOff, Landing.Decide(reviewed).Action);
		Assert.Equal(new Decision(Landing.Wait, "1 open thread(s)"), Landing.Decide(reviewed with { OpenThreads = 1 }));
		var offDiff = Landing.Decide(reviewed with { ClaudeOffDiff = Set(Head) });
		Assert.Equal(Landing.Wait, offDiff.Action);
		Assert.Contains("findings outside the diff", offDiff.Reason);
		// The same head at three dirty rounds in a row is no longer held by either.
		Assert.Equal(Landing.HandOff, Landing.Decide(reviewed with { Rounds = Rounds("d d d"), OpenThreads = 1 }).Action);
		Assert.Equal(Landing.HandOff, Landing.Decide(reviewed with { Rounds = Rounds("d d d"), ClaudeOffDiff = Set(Head) }).Action);
	}

	[Fact]
	public void A_documented_exhausted_loop_is_not_a_reason_to_call_claude_in_until_the_owner_re_adds_the_label()
	{
		var declined = Ready with { MergedByWorkflow = true, CopilotReviewed = Set(Old), CopilotDeclined = Set(Head), Rounds = Rounds("d d c") };
		Assert.Equal(Landing.RequestClaude, Landing.Decide(declined).Action);
		var newest = Landing.Decide(declined with { ExhaustionMarkerNewest = true });
		Assert.Equal(Landing.Wait, newest.Action);
		Assert.Contains("review:claude", newest.Reason);
		Assert.Contains("exhaust", newest.Reason.ToLowerInvariant());
		// The same for a dropped request.
		var dropped = declined with { CopilotDeclined = Set(), CopilotAsked = true, CopilotAskSettled = true };
		Assert.Equal(Landing.RequestClaude, Landing.Decide(dropped).Action);
		Assert.Equal(Landing.Wait, Landing.Decide(dropped with { ExhaustionMarkerNewest = true }).Action);
	}

	[Fact]
	public void Review_cap_matches_pr_auto_merges()
	{
		var text = Skill.Workflow("pr-automerge.yml");
		Assert.Contains($"const COPILOT_REVIEW_CAP = {Landing.ReviewCap};", text);
	}

	[Fact]
	public void The_exhaustion_marker_matches_pr_auto_merges()
	{
		var text = Skill.Workflow("pr-automerge.yml");
		Assert.Equal("<!-- pr-automerge:review-loop-exhausted", Landing.ExhaustedMarker);
		Assert.Contains($"const EXHAUSTED_MARKER = \"{Landing.ExhaustedMarker}\";", text);
		Assert.Equal(Landing.ReviewCap, Landing.MaxClaudeRounds);
		Assert.Equal(3, Landing.ReviewCap);
	}

	[Fact]
	public void Pr_auto_merge_calls_in_claude_on_the_same_terms()
	{
		// PR Auto-Merge adds review:claude itself when Copilot's quota note is the head's only review (#190).
		var text = Skill.Workflow("pr-automerge.yml");
		Assert.Contains($"const MAX_CLAUDE_ROUNDS = {Landing.MaxClaudeRounds};", text);
		var checks = string.Join(", ", Sorted(Landing.ClaudeChecks).Select(name => $"\"{name}\""));
		var found = Regex.Match(text, @"const CLAUDE_CHECKS = \[(.*)\];");
		Assert.True(found.Success);
		Assert.Equal(checks, string.Join(", ", Sorted(found.Groups[1].Value.Split(',').Select(n => n.Trim()))));
	}

	[Fact]
	public void No_checks_at_all_is_not_green()
	{
		var dropped = Ready with { CopilotReviewed = Set(Old), CopilotDeclined = Set(Head), Checks = [] };
		var decision = Landing.Decide(dropped);
		Assert.Equal(Landing.Wait, decision.Action);
		Assert.EndsWith("no checks on the head yet, so Claude waits for CI", decision.Reason);
		// Claude Review's own checks alone aren't CI either.
		Assert.Equal(Landing.Wait, Landing.Decide(dropped with { Checks = [Passed("Check for a merge from main")] }).Action);
	}

	// The Python parametrizes over Check objects; Check is internal, so a public theory takes the check's status instead.
	[Theory]
	[InlineData("QUEUED")]
	[InlineData("WAITING")]
	[InlineData("PENDING")]
	[InlineData("REQUESTED")]
	public void A_queued_pending_or_waiting_check_counts_as_running(string status)
	{
		var check = new Check("Test Suite", status);
		var dropped = Ready with { CopilotReviewed = Set(Old), CopilotDeclined = Set(Head), Checks = [Passed("Build Solution"), check] };
		var decision = Landing.Decide(dropped);
		Assert.Equal(Landing.Wait, decision.Action);
		Assert.EndsWith("calling in Claude: Test Suite", decision.Reason);
	}

	[Fact]
	public void Claude_review_checks_dont_hold_calling_it_in()
	{
		// Its run cancelled when the label came off early, or skipped without the label.
		var unreviewed = Ready with
		{
			CopilotReviewed = Set(Old),
			CopilotDeclined = Set(Head),
			Checks = [Passed("Build Solution"), Cancelled("Check for a merge from main"), Cancelled("Review with Claude"), Cancelled("Post Claude's review")],
		};
		Assert.Equal(Landing.RequestClaude, Landing.Decide(unreviewed).Action);
		Assert.Equal(Landing.RequestClaude, Landing.Decide(unreviewed with { Checks = [Passed("Build Solution"), Running("Review with Claude")] }).Action);
		// Any other cancelled check still needs a rerun.
		Assert.Equal(Landing.Blocker, Landing.Decide(unreviewed with { Checks = [Cancelled("Build Solution")] }).Action);
	}

	[Fact]
	public void Claude_is_called_in_whatever_it_reviewed_before_so_long_as_the_loop_is_not_exhausted()
	{
		// A fix for the findings of any number of Claude's reviews is reviewed too: only dirty rounds in a row end the loop (#375).
		// The old rule stopped at three reviewed commits and left it to the owner.
		var three = Ready with { CopilotReviewed = Set(), ClaudeReviewed = Set(Old, C40, D40), CopilotDeclined = Set(Head), MergedByWorkflow = false };
		Assert.Equal(Landing.RequestClaude, Landing.Decide(three).Action);
		Assert.Equal(Landing.RequestClaude, Landing.Decide(three with { MergedByWorkflow = true, Rounds = Rounds("d d c") }).Action);
		Assert.Equal(Landing.HandOff, Landing.Decide(three with { MergedByWorkflow = true, Rounds = Rounds("d d d") }).Action);
		Assert.DoesNotContain("the owner decides", Landing.Decide(three).Reason);
	}

	[Fact]
	public void A_claude_review_of_the_head_counts_only_with_copilots_quota_note_on_it()
	{
		var bare = Ready with { CopilotReviewed = Set(), ClaudeReviewed = Set(Head), CopilotRequested = true };
		Assert.Equal("", bare.HeadReviewer);
		Assert.Equal(Landing.Wait, Landing.Decide(bare).Action);
		Assert.Equal("", (bare with { CopilotDeclined = Set(Old) }).HeadReviewer);
		var claude = Ready with { CopilotReviewed = Set(), ClaudeReviewed = Set(Head), CopilotDeclined = Set(Head) };
		Assert.Equal("claude", claude.HeadReviewer);
		Assert.True(claude.ReviewerOnHead);
		var decision = Landing.Decide(claude);
		Assert.Equal(Landing.ArmAutoMerge, decision.Action);
		Assert.StartsWith("Claude reviewed the head", decision.Reason);
		Assert.Equal(Landing.HandOff, Landing.Decide(claude with { MergedByWorkflow = true }).Action);
	}

	[Fact]
	public void A_claude_review_of_an_older_head_doesnt_cover_the_head()
	{
		var decision = Landing.Decide(Ready with { CopilotReviewed = Set(), ClaudeReviewed = Set(Old), CopilotRequested = true });
		Assert.Equal(Landing.Wait, decision.Action);
		Assert.Contains("no Copilot or Claude review of aaaaaaa", decision.Reason);
	}

	[Fact]
	public void Copilot_is_named_when_both_reviewed_the_head()
	{
		var both = Ready with { ClaudeReviewed = Set(Head), CopilotDeclined = Set(Head) };
		Assert.Equal("copilot", both.HeadReviewer);
		Assert.StartsWith("Copilot reviewed the head", Landing.Decide(both).Reason);
		Assert.Equal("", (Ready with { CopilotReviewed = Set() }).HeadReviewer);
	}

	[Fact]
	public void An_open_thread_waits()
	{
		var decision = Landing.Decide(Ready with { OpenThreads = 2 });
		Assert.Equal(new Decision(Landing.Wait, "2 open thread(s)"), decision);
	}

	[Fact]
	public void Below_exhaustion_an_open_thread_waits_after_any_number_of_clean_rounds()
	{
		// Until the loop is exhausted the landing decision never arms past a thread (#375).
		var roundsSoFar = Ready with { CopilotReviewed = Set(C40, D40, Old, Head), OpenThreads = 1 };
		Assert.Equal(new Decision(Landing.Wait, "1 open thread(s)"), Landing.Decide(roundsSoFar));
	}

	[Fact]
	public void A_behind_branch_is_updated_and_nobody_is_asked()
	{
		Assert.Equal("update branch", Landing.UpdateBranch);
		Assert.Contains(Landing.UpdateBranch, Landing.Actions);
		Assert.Equal(Landing.UpdateBranch, Landing.Decide(Ready with { MergeState = "BEHIND" }).Action);
		// Behind wins over a missing review: the update makes a new head to review anyway.
		var behindUnreviewed = Ready with { MergeState = "BEHIND", CopilotReviewed = Set() };
		Assert.Equal(Landing.UpdateBranch, Landing.Decide(behindUnreviewed).Action);
	}

	[Fact]
	public void A_cancelled_check_beside_a_newer_pass_is_not_a_failure()
	{
		Check[] checks = [new("Test Suite", "COMPLETED", "CANCELLED", "2026-10-07T10:00:00Z"), Passed("Test Suite", "2026-10-07T10:03:00Z"), Passed("Build Solution")];
		var summary = Landing.SummarizeChecks(checks);
		Assert.Empty(summary.Failed);
		Assert.Empty(summary.Cancelled);
		Assert.Equal(["Test Suite"], summary.Superseded);
		Assert.True(summary.Green);
		Assert.Equal(Landing.ArmAutoMerge, Landing.Decide(Ready with { Checks = checks }).Action);
	}

	[Fact]
	public void A_cancelled_check_with_nothing_newer_is_a_blocker_told_apart_from_failed()
	{
		Check[] checks = [Passed("Test Suite", "2026-10-07T10:00:00Z"), new("Test Suite", "COMPLETED", "CANCELLED", "2026-10-07T10:03:00Z")];
		var summary = Landing.SummarizeChecks(checks);
		Assert.Equal(["Test Suite"], summary.Cancelled);
		Assert.Empty(summary.Failed);
		var decision = Landing.Decide(Ready with { Checks = checks });
		Assert.Equal(Landing.Blocker, decision.Action);
		Assert.StartsWith("cancelled", decision.Reason);
	}

	[Fact]
	public void A_failed_check_is_a_blocker_not_armed()
	{
		Check[] checks = [Passed("Build Solution"), new("Test Suite", "COMPLETED", "FAILURE", "2026-10-07T10:00:00Z")];
		var decision = Landing.Decide(Ready with { Checks = checks });
		Assert.Equal(new Decision(Landing.Blocker, "failed: Test Suite"), decision);
		// A newer failure replaces an older pass of the same check.
		Check[] newerFailure = [Passed("Test Suite", "2026-10-07T09:00:00Z"), new("Test Suite", "COMPLETED", "TIMED_OUT", "2026-10-07T10:00:00Z")];
		Assert.Equal(Landing.Blocker, Landing.Decide(Ready with { Checks = newerFailure }).Action);
	}

	[Fact]
	public void A_merge_conflict_is_a_blocker()
	{
		Assert.Equal(Landing.Blocker, Landing.Decide(Ready with { MergeState = "DIRTY" }).Action);
	}

	[Fact]
	public void Running_required_checks_still_arm()
	{
		var pr = Ready with { Checks = [Passed("Build Solution"), Running("Test Suite")] };
		Assert.Equal(Landing.ArmAutoMerge, Landing.Decide(pr).Action);
	}

	[Fact]
	public void A_repo_with_no_required_checks_waits_for_green_before_arming()
	{
		var pr = Ready with { ChecksRequired = false, Checks = [Running("Template tests")] };
		var decision = Landing.Decide(pr);
		Assert.Equal(Landing.Wait, decision.Action);
		Assert.Contains("Template tests", decision.Reason);
		var green = pr with { Checks = [Passed("Template tests")] };
		Assert.Equal(Landing.ArmAutoMerge, Landing.Decide(green).Action);
	}

	[Fact]
	public void An_already_armed_pr_needs_nothing()
	{
		var armed = Ready with { AutoMergeArmed = true, CopilotReviewed = Set() };
		Assert.Equal(new Decision(Landing.Nothing, "auto-merge armed"), Landing.Decide(armed));
	}

	[Fact]
	public void A_merged_or_closed_pr_needs_nothing()
	{
		Assert.Equal(new Decision(Landing.Nothing, "merged"), Landing.Decide(Ready with { State = "MERGED" }));
		Assert.Equal(Landing.Nothing, Landing.Decide(Ready with { State = "CLOSED", Draft = true }).Action);
	}

	[Fact]
	public void Copilot_reviews_match_copilots_own_logins()
	{
		(string?, string?, string?)[] reviews = [("copilot-pull-request-reviewer", Head, ""), ("Copilot", Old, null), ("mpaulosky", C40, ""), (null, D40, "")];
		Assert.Equal(Sorted(Set(Head, Old)), Sorted(Landing.CopilotReviews(reviews)));
	}

	[Fact]
	public void A_review_by_a_login_that_contains_copilot_covers_nothing()
	{
		(string?, string?, string?)[] reviews = [("not-copilot-bot", Head, ""), ("my-copilot", Old, "")];
		Assert.Empty(Landing.CopilotReviews(reviews));
		(string?, string?, string?, string?, long, bool)[] records = [Record("not-copilot-bot", "c1", "", "2026-01-02T00:01:00Z", comments: 1)];
		Assert.Empty(Landing.ReviewRounds(records, declined: Set(), gone: false));
	}

	[Fact]
	public void Copilot_reviews_skip_the_quota_note()
	{
		var note = "Copilot was unable to review this pull request because the user who requested the review has reached "
				+ "their quota limit.";
		(string?, string?, string?)[] reviews = [("copilot-pull-request-reviewer", Head, note), ("copilot-pull-request-reviewer", Old, "## Overview")];
		Assert.Equal(Sorted(Set(Old)), Sorted(Landing.CopilotReviews(reviews)));
	}

	[Fact]
	public void Copilot_declined_lists_the_heads_copilot_answered_with_its_quota_note()
	{
		var note = "Copilot was unable to review this pull request because the user who requested the review has reached their quota limit.";
		(string?, string?, string?)[] reviews = [("copilot-pull-request-reviewer", Head, note), ("copilot-pull-request-reviewer", Old, "## Overview"), ("mpaulosky", C40, note)];
		Assert.Equal(Sorted(Set(Head)), Sorted(Landing.CopilotDeclined(reviews)));
	}

	[Fact]
	public void A_head_copilot_declined_calls_in_claude_rather_than_copilot_again()
	{
		var declined = Ready with { CopilotReviewed = Set(Old), CopilotDeclined = Set(Head) };
		var decision = Landing.Decide(declined);
		Assert.Equal(Landing.RequestClaude, decision.Action);
		Assert.Contains("Copilot couldn't review", decision.Reason);
		// A request that still shows after the note doesn't hold it.
		Assert.Equal(Landing.RequestClaude, Landing.Decide(declined with { CopilotRequested = true }).Action);
		// Copilot's note on an older head doesn't stop it being asked about this one.
		var older = declined with { CopilotDeclined = Set(Old) };
		Assert.Equal(Landing.RequestCopilot, Landing.Decide(older).Action);
	}

	[Fact]
	public void Claude_reviews_are_marked_github_actions_reviews()
	{
		var marker = Landing.ClaudeMarker + "\n**Claude Review**";
		(string?, string?, string?)[] reviews =
		[
			("github-actions", Head, marker), ("github-actions[bot]", Old, marker),
			("github-actions", C40, "Some other workflow's review."), ("mpaulosky", D40, marker),
			("github-actions", E40, null), (null, F40, marker), ("github-actions", null, marker),
		];
		Assert.Equal(Sorted(Set(Head, Old)), Sorted(Landing.ClaudeReviews(reviews)));
	}

	[Fact]
	public void A_run_not_yet_started_counts_as_newest()
	{
		Check[] checks = [new("Test Suite", "COMPLETED", "CANCELLED", "2026-10-07T10:00:00Z"), new("Test Suite", "QUEUED", null, "")];
		var summary = Landing.SummarizeChecks(checks);
		Assert.Equal(["Test Suite"], summary.Running);
		Assert.Empty(summary.Cancelled);
	}

	[Fact]
	public void A_baseline_repo_leaves_a_reviewed_pr_to_its_pr_auto_merge()
	{
		var baseline = Ready with { MergedByWorkflow = true };
		Assert.Equal(Landing.HandOff, Landing.Decide(baseline).Action);
		// Its gates still come first: nothing is handed off before Copilot reviewed the head.
		Assert.Equal(Landing.Wait, Landing.Decide(baseline with { CopilotReviewed = Set(Old), CopilotRequested = true }).Action);
		Assert.Equal(Landing.Wait, Landing.Decide(baseline with { OpenThreads = 1 }).Action);
		Assert.Equal(Landing.UpdateBranch, Landing.Decide(baseline with { MergeState = "BEHIND" }).Action);
	}

	[Fact]
	public void A_release_blog_pr_is_armed_without_copilot_unless_blocked_or_armed()
	{
		var blog = Ready with { ReleaseBlog = true, MergedByWorkflow = true, CopilotReviewed = Set(), Checks = [Running("Lint Markdown")] };
		Assert.Equal(Landing.ArmAutoMerge, Landing.Decide(blog).Action);
		Assert.Equal(new Decision(Landing.Nothing, "auto-merge armed"), Landing.Decide(blog with { AutoMergeArmed = true }));
		var failing = blog with { Checks = [new("Lint Markdown", "COMPLETED", "FAILURE", "2026-10-07T10:00:00Z")] };
		Assert.Equal(Landing.Blocker, Landing.Decide(failing).Action);
		Assert.Equal(Landing.Blocker, Landing.Decide(blog with { MergeState = "DIRTY" }).Action);
	}

	[Fact]
	public void A_claude_review_of_the_head_with_findings_outside_the_diff_waits()
	{
		var blocked = Ready with { ClaudeReviewed = Set(Head), ClaudeOffDiff = Set(Head) };
		var decision = Landing.Decide(blocked);
		Assert.Equal(new Decision(Landing.Wait, "Claude's review of aaaaaaa has findings outside the diff"), decision);
		Assert.Equal(Landing.ArmAutoMerge, Landing.Decide(blocked with { ClaudeOffDiff = Set(Old) }).Action);
	}

	[Fact]
	public void Off_diff_findings_are_judged_by_the_latest_claude_review_of_each_commit()
	{
		var clean = Landing.ClaudeMarker + "\n**Claude Review**";
		var off = Landing.ClaudeMarker + "\n" + Landing.OffDiffMarker + "\n**Claude Review**";
		(string?, string?, string?)[] reviews =
		[
			("github-actions", Head, clean), ("github-actions", Head, off),
			("github-actions", Old, off), ("github-actions", Old, clean),
			("github-actions", C40, "Not Claude's.\n" + Landing.OffDiffMarker),
			("copilot-pull-request-reviewer", D40, off),
		];
		Assert.Equal(Sorted(Set(Head)), Sorted(Landing.ClaudeOffDiff(reviews)));
	}

	[Fact]
	public void A_run_cancelled_before_it_started_is_superseded_by_a_later_run()
	{
		// No started_at, so only the creation order (rank) says the green run is newer.
		Check[] checks = [new("Test Suite", "COMPLETED", "CANCELLED", "", Rank: 1), new("Test Suite", "COMPLETED", "SUCCESS", "2026-10-07T10:00:00Z", Rank: 2)];
		var summary = Landing.SummarizeChecks(checks);
		Assert.Equal(["Test Suite"], summary.Passed);
		Assert.Empty(summary.Cancelled);
	}

	[Fact]
	public void A_queued_rerun_outranks_an_older_green_run()
	{
		Check[] checks = [new("Test Suite", "COMPLETED", "SUCCESS", "2026-10-07T10:00:00Z", Rank: 1), new("Test Suite", "QUEUED", null, "", Rank: 2)];
		Assert.Equal(["Test Suite"], Landing.SummarizeChecks(checks).Running);
	}

	// -- Resolved bot threads nobody replied to (a notice, not a hold) -----------

	// A resolved Copilot thread at src/a.py:12 with no reply, unless overridden.
	static ReviewThread Thread(bool resolved = true, string opener = Landing.CopilotReviewer, string openerKind = "Bot", string openerReview = "",
			string[]? repliers = null, bool partial = false, string name = "src/a.py:12") =>
			new(resolved, opener, openerKind, openerReview, repliers ?? [], partial, name);

	static ReviewThread ClaudeThread(string[]? repliers = null, string name = "src/a.py:12") =>
			Thread(opener: Landing.GithubActions, openerReview: Landing.ClaudeMarker + "\n**Claude Review**", repliers: repliers, name: name);

	[Fact]
	public void A_resolved_copilot_thread_with_no_reply_is_listed()
	{
		Assert.Equal(["src/a.py:12"], Landing.UnrepliedThreads([Thread()]));
	}

	[Fact]
	public void A_resolved_copilot_thread_with_a_reply_naming_a_commit_is_not_listed()
	{
		Assert.Empty(Landing.UnrepliedThreads([Thread(repliers: ["octo"])]));
	}

	[Fact]
	public void A_reply_is_enough_whatever_it_says_so_sandcastles_and_a_sessions_replies_count()
	{
		// Sandcastle's marker reply and a session's reply come from the owner's login; their text isn't read.
		Assert.Empty(Landing.UnrepliedThreads([Thread(repliers: ["octo"]), Thread(repliers: ["someone-else"], name: "b.py:1")]));
	}

	[Fact]
	public void A_resolved_claude_review_thread_with_no_reply_is_listed()
	{
		Assert.Equal(["src/a.py:12"], Landing.UnrepliedThreads([ClaudeThread()]));
		Assert.Empty(Landing.UnrepliedThreads([ClaudeThread(repliers: ["octo"])]));
	}

	[Fact]
	public void A_github_actions_thread_without_the_claude_marker_is_not_a_bot_thread()
	{
		var other = Thread(opener: Landing.GithubActions, openerReview: "Some other workflow's review.");
		Assert.False(Landing.BotThread(other));
		Assert.Empty(Landing.UnrepliedThreads([other]));
	}

	[Fact]
	public void A_persons_own_resolved_thread_is_not_listed()
	{
		Assert.Empty(Landing.UnrepliedThreads([Thread(opener: "octo", openerKind: "User")]));
	}

	[Fact]
	public void A_user_named_like_copilot_is_not_a_bot_thread()
	{
		Assert.Empty(Landing.UnrepliedThreads([Thread(openerKind: "User")]));
	}

	[Fact]
	public void A_thread_with_no_author_or_another_bots_is_not_a_bot_thread()
	{
		Assert.Empty(Landing.UnrepliedThreads([Thread(opener: "", openerKind: ""), Thread(opener: "dependabot", openerKind: "Bot")]));
	}

	[Fact]
	public void An_unresolved_bot_thread_is_not_listed()
	{
		Assert.Empty(Landing.UnrepliedThreads([Thread(resolved: false)]));
	}

	[Fact]
	public void A_reply_by_the_opener_itself_is_no_reply()
	{
		Assert.Equal(["src/a.py:12"], Landing.UnrepliedThreads([Thread(repliers: [Landing.CopilotReviewer])]));
	}

	[Fact]
	public void A_thread_with_more_comments_than_were_read_counts_as_replied()
	{
		Assert.Empty(Landing.UnrepliedThreads([Thread(partial: true)]));
	}

	[Fact]
	public void Unreplied_threads_keep_the_order_given()
	{
		ReviewThread[] threads = [Thread(name: "b.py:2"), Thread(name: "a.py:1", repliers: ["octo"]), ClaudeThread(name: "c.py:3")];
		Assert.Equal(["b.py:2", "c.py:3"], Landing.UnrepliedThreads(threads));
	}

	[Fact]
	public void The_unreplied_threads_are_a_notice_decide_ignores_them()
	{
		Assert.Equal(Landing.Decide(Ready), Landing.Decide(Ready with { UnrepliedThreads = ["src/a.py:12"] }));
		var waiting = Ready with { CopilotReviewed = Set(Old), CopilotRequested = true };
		Assert.Equal(Landing.Decide(waiting), Landing.Decide(waiting with { UnrepliedThreads = ["src/a.py:12"] }));
		Assert.Equal(Landing.ArmAutoMerge, Landing.Decide(Ready with { UnrepliedThreads = ["src/a.py:12"] }).Action);
	}

	[Fact]
	public void The_notice_names_the_threads_and_is_empty_when_there_are_none()
	{
		Assert.Equal("", Landing.UnrepliedNotice(Ready));
		var one = Ready with { UnrepliedThreads = ["src/a.py:12"] };
		Assert.Equal("notice: resolved without a reply: src/a.py:12", Landing.UnrepliedNotice(one));
		var two = Ready with { UnrepliedThreads = ["src/a.py:12", "b.py:3"] };
		Assert.Equal("notice: resolved without a reply: src/a.py:12, b.py:3", Landing.UnrepliedNotice(two));
	}

	[Fact]
	public void The_notice_names_at_most_three_threads_and_counts_the_rest()
	{
		string[] names = ["a.py:1", "b.py:2", "c.py:3", "d.py:4", "e.py:5"];
		var notice = Landing.UnrepliedNotice(Ready with { UnrepliedThreads = names });
		Assert.Equal("notice: resolved without a reply: a.py:1, b.py:2, c.py:3 and 2 more", notice);
		var exactly = Landing.UnrepliedNotice(Ready with { UnrepliedThreads = names[..3] });
		Assert.Equal("notice: resolved without a reply: a.py:1, b.py:2, c.py:3", exactly);
	}

	// -- Copilot is asked once per PR, then Claude backs it up (mpaulosky/dotfiles#360) --

	const double Now = 1_800_000_000.0;
	const string Marker = Landing.CopilotAskedMarker;

	static string Iso(double epoch) =>
			DateTimeOffset.FromUnixTimeSeconds((long)epoch).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

	// A comment as CopilotAsk reads it: (login, body, created_at).
	static (string?, string?, string?) Comment(string login = "github-actions", double age = 3600, string? body = null) =>
			(login, body ?? $"Asked Copilot.\n\n{Marker}", Iso(Now - age));

	[Fact]
	public void The_record_is_a_comment_with_the_marker_and_the_settle_time_is_30_seconds()
	{
		Assert.Equal("<!-- pr-automerge:copilot-asked -->", Marker);
		Assert.Equal(30, Landing.CopilotAskSettleSeconds);
		Assert.Contains(Marker, Landing.CopilotAskedComment);
	}

	[Fact]
	public void Pr_auto_merge_uses_the_same_marker_and_settle_time()
	{
		var text = Skill.Workflow("pr-automerge.yml");
		Assert.Contains($"const COPILOT_ASKED_MARKER = \"{Marker}\";", text);
		Assert.Contains($"const COPILOT_ASK_SETTLE_MS = {Landing.CopilotAskSettleSeconds} * 1000;", text);
	}

	[Fact]
	public void No_record_means_copilot_was_not_asked()
	{
		Assert.Equal((false, false), Landing.CopilotAsk([], "o", Now));
		Assert.Equal((false, false), Landing.CopilotAsk([Comment(body: "Looks good to me."), Comment(login: "o", body: "thanks")], "o", Now));
	}

	[Fact]
	public void A_record_by_github_actions_or_the_owner_counts()
	{
		Assert.Equal((true, true), Landing.CopilotAsk([Comment("github-actions")], "o", Now));
		Assert.Equal((true, true), Landing.CopilotAsk([Comment("github-actions[bot]")], "o", Now));
		Assert.Equal((true, true), Landing.CopilotAsk([Comment("o")], "o", Now));
	}

	[Fact]
	public void A_record_by_anyone_else_is_ignored()
	{
		foreach (var login in new[] { "stranger", "copilot-pull-request-reviewer", "o-other", "", "github-actions-fake" })
			Assert.True(Landing.CopilotAsk([Comment(login)], "o", Now) == (false, false), login);
		Assert.Equal((false, false), Landing.CopilotAsk([Comment("o")], "", Now));
	}

	[Fact]
	public void The_record_settles_after_30_seconds()
	{
		Assert.Equal((true, false), Landing.CopilotAsk([Comment(age: 29)], "o", Now));
		Assert.Equal((true, true), Landing.CopilotAsk([Comment(age: 30)], "o", Now));
		Assert.Equal((true, true), Landing.CopilotAsk([Comment(age: 31)], "o", Now));
		Assert.Equal((true, false), Landing.CopilotAsk([Comment(age: 0)], "o", Now));
	}

	[Fact]
	public void The_first_record_decides_when_there_are_several()
	{
		// Oldest first: the first record is the ask, a later one (a second asker's) changes nothing.
		Assert.Equal((true, true), Landing.CopilotAsk([Comment(age: 3600), Comment(age: 5)], "o", Now));
		Assert.Equal((true, false), Landing.CopilotAsk([Comment(age: 5), Comment(age: 3600)], "o", Now));
		Assert.Equal((true, false), Landing.CopilotAsk([Comment("stranger", age: 3600), Comment(age: 5)], "o", Now));
	}

	[Fact]
	public void A_record_after_other_comments_is_found()
	{
		Assert.Equal((true, true), Landing.CopilotAsk([Comment(body: "LGTM"), Comment("o", body: "thanks"), Comment(age: 100)], "o", Now));
	}

	[Fact]
	public void Comments_that_could_not_be_read_count_as_asked_but_not_settled()
	{
		Assert.Equal((true, false), Landing.CopilotAsk(null, "o", Now));
	}

	[Fact]
	public void A_record_with_an_unreadable_time_is_asked_but_not_settled()
	{
		foreach (var created in new[] { "not a date", "", null })
			Assert.True(Landing.CopilotAsk([("github-actions", Marker, created)], "o", Now) == (true, false), created ?? "None");
	}

	static readonly PrState Unasked = Ready with { CopilotReviewed = Set(Old) };
	static readonly PrState Asked = Unasked with { CopilotAsked = true };
	static readonly PrState Settled = Asked with { CopilotAskSettled = true };

	[Fact]
	public void Copilot_is_gone_when_asked_and_settled_with_nothing_pending_or_dropped_or_declined()
	{
		Assert.False(Unasked.CopilotGone);
		Assert.False(Asked.CopilotGone);
		Assert.True(Settled.CopilotGone);
		Assert.False((Settled with { CopilotRequested = true }).CopilotGone);
		Assert.False((Unasked with { CopilotAskSettled = true }).CopilotGone);
	}

	[Fact]
	public void An_unasked_head_asks_copilot()
	{
		var decision = Landing.Decide(Unasked);
		Assert.Equal(Landing.RequestCopilot, decision.Action);
		Assert.EndsWith("Copilot not asked yet", decision.Reason);
	}

	[Fact]
	public void An_asked_head_waits_for_the_request_to_show_until_the_record_settles()
	{
		var decision = Landing.Decide(Asked);
		Assert.Equal(Landing.Wait, decision.Action);
		Assert.Contains("Copilot was asked", decision.Reason);
		Assert.Contains("no Copilot or Claude review of aaaaaaa", decision.Reason);
	}

	[Fact]
	public void A_settled_record_with_nothing_pending_calls_in_claude_once_checks_are_green()
	{
		Assert.Equal(Landing.RequestClaude, Landing.Decide(Settled).Action);
		var waiting = Landing.Decide(Settled with { Checks = [Passed("Build Solution"), Running("Test Suite")] });
		Assert.Equal(Landing.Wait, waiting.Action);
		Assert.EndsWith("waiting for checks before calling in Claude: Test Suite", waiting.Reason);
		Assert.Equal(Landing.Blocker, Landing.Decide(Settled with { Checks = [Failed("Test Suite")] }).Action);
		Assert.Equal(Landing.Wait, Landing.Decide(Settled with { Checks = [] }).Action);
	}

	[Fact]
	public void A_settled_record_with_a_pending_request_waits()
	{
		var decision = Landing.Decide(Settled with { CopilotRequested = true });
		Assert.Equal(Landing.Wait, decision.Action);
		Assert.EndsWith("Copilot is requested", decision.Reason);
	}

	[Fact]
	public void Claude_is_not_called_in_before_the_record_settles_even_for_a_request_read_back_as_dropped()
	{
		// PR Auto-Merge takes review:claude off an ask younger than 30 seconds, so land.cs waits it out too (mpaulosky/dotfiles#378).
		var decision = Landing.Decide(Asked);
		Assert.Equal(Landing.Wait, decision.Action);
		Assert.Contains("Copilot was asked", decision.Reason);
	}

	[Fact]
	public void Claudes_earlier_reviews_stall_nothing_for_a_settled_record_either()
	{
		var used = Settled with { ClaudeReviewed = Set(C40, D40, E40, Old) };
		Assert.Equal(Landing.RequestClaude, Landing.Decide(used).Action);
	}

	[Fact]
	public void The_label_waits_for_claude_whatever_the_record_says()
	{
		Assert.Equal(Landing.Wait, Landing.Decide(Settled with { ClaudeRequested = true }).Action);
		Assert.Contains("review:claude is on", Landing.Decide(Settled with { ClaudeRequested = true }).Reason);
	}

	[Fact]
	public void Claudes_review_of_the_head_counts_only_where_copilot_is_gone()
	{
		var reviewed = Unasked with { ClaudeReviewed = Set(Head), ClaudeRequested = true };
		Assert.Equal("", reviewed.HeadReviewer);
		Assert.Equal(Landing.RequestCopilot, Landing.Decide(reviewed).Action);
		Assert.Equal("", (reviewed with { CopilotAsked = true }).HeadReviewer);
		var gone = reviewed with { CopilotAsked = true, CopilotAskSettled = true };
		Assert.Equal("claude", gone.HeadReviewer);
		Assert.Equal(Landing.ArmAutoMerge, Landing.Decide(gone).Action);
		Assert.Equal("", (gone with { CopilotRequested = true }).HeadReviewer);
		// Copilot's own review comes first.
		Assert.Equal("copilot", (gone with { CopilotReviewed = Set(Head) }).HeadReviewer);
	}

	[Fact]
	public void Claudes_review_of_an_older_commit_is_backed_by_a_settled_record_but_covers_no_head()
	{
		var older = Settled with { ClaudeReviewed = Set(Old) };
		// It counts as a round (Copilot is gone from the PR), but it is not a review of the head.
		Assert.Equal(Sorted(Set(Old)), Sorted(older.ClaudeBacked));
		Assert.Equal("", older.HeadReviewer);
		Assert.Equal(Landing.RequestClaude, Landing.Decide(older).Action);
		// Still not backed while Copilot may review: the ask is unsettled, or a request is pending.
		Assert.Empty((Asked with { ClaudeReviewed = Set(Old) }).ClaudeBacked);
		Assert.Empty((Settled with { ClaudeReviewed = Set(Old), CopilotRequested = true }).ClaudeBacked);
		// A quota note on that commit backs it whatever the record says.
		Assert.Equal(Sorted(Set(Old)), Sorted((Unasked with { ClaudeReviewed = Set(Old), CopilotDeclined = Set(Old) }).ClaudeBacked));
	}

	// A review as ReviewRounds() reads it: (author login, commit oid, body, submittedAt, inline thread count, is a merge).
	static (string?, string?, string?, string?, long, bool) Record(string login, string oid, string body, string at, long comments = 0, bool merge = false) =>
			(login, oid, body, at, comments, merge);

	const string ClaudeClean = "<!-- claude-review -->\nNo findings.";
	const string ClaudeOffDiffBody = "<!-- claude-review -->\n<!-- claude-review:off-diff -->\n**Claude Review**";
	const string QuotaNote = "Copilot was unable to review this pull request because the user who requested the review has reached their quota limit.";

	static (string, bool)[] OidsAndDirt(IEnumerable<Round> rounds) => [.. rounds.Select(item => (item.Oid, item.Dirty))];

	[Fact]
	public void Review_rounds_counts_copilots_reviews_of_non_merge_commits_oldest_first_and_marks_the_dirty_ones()
	{
		(string?, string?, string?, string?, long, bool)[] reviews =
		[
			Record("copilot-pull-request-reviewer", "c1", "", "2026-01-02T00:01:00Z", comments: 2),
			Record("copilot-pull-request-reviewer", "c2", "", "2026-01-02T00:02:00Z"),
			Record("copilot-pull-request-reviewer", "m1", "", "2026-01-02T00:03:00Z", comments: 1, merge: true),
			Record("copilot-pull-request-reviewer", "c3", QuotaNote, "2026-01-02T00:04:00Z"),
		];
		var roundsFound = Landing.ReviewRounds(reviews, declined: Set(), gone: false);
		Assert.Equal([("c1", true), ("c2", false)], OidsAndDirt(roundsFound));
		Assert.Equal(["2026-01-02T00:01:00Z", "2026-01-02T00:02:00Z"], roundsFound.Select(item => item.At));
	}

	[Fact]
	public void Review_rounds_makes_one_round_of_a_commit_reviewed_twice_dirty_if_either_review_was()
	{
		(string?, string?, string?, string?, long, bool)[] reviews =
		[
			Record("Copilot", "c1", "", "2026-01-02T00:01:00Z", comments: 1),
			Record("Copilot", "c2", "", "2026-01-02T00:02:00Z"),
			Record("Copilot", "c1", "", "2026-01-02T00:03:00Z"),
		];
		var found = Landing.ReviewRounds(reviews, Set(), false);
		// Ordered by each commit's latest review.
		Assert.Equal([("c2", false), ("c1", true)], OidsAndDirt(found));
		Assert.Equal("2026-01-02T00:03:00Z", found[1].At);
	}

	[Fact]
	public void Review_rounds_counts_claudes_reviews_only_where_copilot_cant_review_that_commit()
	{
		(string?, string?, string?, string?, long, bool)[] reviews =
		[
			Record("github-actions", "c1", ClaudeClean, "2026-01-02T00:01:00Z"),
			Record("github-actions", "c2", ClaudeOffDiffBody, "2026-01-02T00:02:00Z"),
			Record("github-actions", "c3", "Some other workflow's review.", "2026-01-02T00:03:00Z", comments: 3),
			Record("github-actions", "m1", ClaudeOffDiffBody, "2026-01-02T00:04:00Z", merge: true),
		];
		// Copilot may still review: none of Claude's reviews is a round.
		Assert.Empty(Landing.ReviewRounds(reviews, Set(), false));
		// Copilot answered c2 with its quota note: that review counts, and an off-diff review is dirty.
		var onlyNoted = Landing.ReviewRounds(reviews, Set("c2"), false);
		Assert.Equal([("c2", true)], OidsAndDirt(onlyNoted));
		// Copilot is gone: every marked Claude review of a non-merge commit counts, whatever its commit.
		var gone = Landing.ReviewRounds(reviews, Set(), true);
		Assert.Equal([("c1", false), ("c2", true)], OidsAndDirt(gone));
	}

	[Fact]
	public void Review_rounds_makes_a_dirty_round_of_a_claude_review_with_inline_threads_too()
	{
		(string?, string?, string?, string?, long, bool)[] reviews = [Record("github-actions", "c1", ClaudeClean, "2026-01-02T00:01:00Z", comments: 2)];
		Assert.Equal([("c1", true)], OidsAndDirt(Landing.ReviewRounds(reviews, Set("c1"), false)));
	}

	[Fact]
	public void A_merge_of_main_is_never_a_round()
	{
		(string?, string?, string?, string?, long, bool)[] reviews =
		[
			Record("Copilot", "c1", "", "2026-01-02T00:01:00Z", comments: 1),
			Record("Copilot", "m1", "", "2026-01-02T00:02:00Z", comments: 1, merge: true),
			Record("Copilot", "c2", "", "2026-01-02T00:03:00Z", comments: 1),
			Record("github-actions", "m2", ClaudeOffDiffBody, "2026-01-02T00:04:00Z", comments: 1, merge: true),
			Record("Copilot", "c3", "", "2026-01-02T00:05:00Z", comments: 1),
		];
		var found = Landing.ReviewRounds(reviews, Set("m2"), true);
		Assert.Equal(["c1", "c2", "c3"], found.Select(item => item.Oid));
		Assert.Equal(3, Landing.TrailingDirty(found));
	}

	[Fact]
	public void Trailing_dirty_counts_the_dirty_rounds_back_from_the_newest()
	{
		Assert.Equal(0, Landing.TrailingDirty([]));
		Assert.Equal(1, Landing.TrailingDirty(Rounds("d")));
		Assert.Equal(1, Landing.TrailingDirty(Rounds("d d c d")));
		Assert.Equal(0, Landing.TrailingDirty(Rounds("d d d c")));
		Assert.Equal(3, Landing.TrailingDirty(Rounds("c d d d")));
		Assert.Equal(5, Landing.TrailingDirty(Rounds("d d d d d")));
	}

	// -- Claude reviews only where Copilot can't; a person's threads still hold (mpaulosky/dotfiles#378) --

	static double Epoch(string text) =>
			DateTimeOffset.Parse(text.Replace("Z", "+00:00", StringComparison.Ordinal), CultureInfo.InvariantCulture).ToUnixTimeSeconds();

	// The epoch seconds of minute:second past 2026-01-02T00, which is when Rounds("...") puts its Nth round at minute N-1.
	static double AtMinute(int minute, int second = 0) =>
			Epoch($"2026-01-02T00:{minute.ToString("00", CultureInfo.InvariantCulture)}:{second.ToString("00", CultureInfo.InvariantCulture)}Z");

	[Fact]
	public void The_label_is_ignored_while_copilot_can_review_it_acceptance_criterion_2()
	{
		var labelledHead = Unasked with { ClaudeRequested = true };
		// Never asked: Copilot is asked, whatever the label says.
		Assert.Equal(Landing.RequestCopilot, Landing.Decide(labelledHead).Action);
		// Asked, the request not shown yet: wait for the request, not for Claude.
		var asked = Landing.Decide(labelledHead with { CopilotAsked = true });
		Assert.Equal(Landing.Wait, asked.Action);
		Assert.DoesNotContain("review:claude is on", asked.Reason);
		Assert.Contains("Copilot was asked", asked.Reason);
		// Settled with a request pending: Copilot is waited for.
		var pending = Landing.Decide(labelledHead with { CopilotAsked = true, CopilotAskSettled = true, CopilotRequested = true });
		Assert.Equal(Landing.Wait, pending.Action);
		Assert.EndsWith("Copilot is requested", pending.Reason);
	}

	[Fact]
	public void The_label_stands_where_copilot_cant_review_acceptance_criterion_2()
	{
		foreach (var (gone, pr) in new[]
		{
			("copilot_declined={HEAD}", Unasked with { ClaudeRequested = true, CopilotDeclined = Set(Head) }),
			("copilot_asked=True, copilot_ask_settled=True", Unasked with { ClaudeRequested = true, CopilotAsked = true, CopilotAskSettled = true }),
		})
		{
			var decision = Landing.Decide(pr);
			Assert.True(decision.Action == Landing.Wait && decision.Reason.EndsWith("review:claude is on", StringComparison.Ordinal), gone);
		}
	}

	static ReviewThread ThreadOf(string opener, string kind = "User", string review = "") =>
			Thread(resolved: false, opener: opener, openerKind: kind, openerReview: review);

	[Fact]
	public void A_thread_opened_by_a_person_is_not_a_bot_thread_acceptance_criterion_3()
	{
		Assert.True(Landing.ThreadOpenedByPerson(ThreadOf("octo")));
		// A user named like Copilot, github-actions without the Claude marker, and another bot are not Copilot's or Claude's.
		Assert.True(Landing.ThreadOpenedByPerson(ThreadOf(Landing.CopilotReviewer, kind: "User")));
		Assert.True(Landing.ThreadOpenedByPerson(ThreadOf(Landing.GithubActions, kind: "Bot", review: "Another workflow's review")));
		Assert.True(Landing.ThreadOpenedByPerson(ThreadOf("dependabot", kind: "Bot")));
	}

	[Fact]
	public void A_thread_whose_author_cant_be_read_is_a_persons_acceptance_criterion_3()
	{
		Assert.True(Landing.ThreadOpenedByPerson(ThreadOf("", kind: "")));
	}

	[Fact]
	public void Copilots_and_claudes_threads_are_not_a_persons_acceptance_criterion_3()
	{
		Assert.False(Landing.ThreadOpenedByPerson(ThreadOf(Landing.CopilotReviewer, kind: "Bot")));
		Assert.False(Landing.ThreadOpenedByPerson(ThreadOf("copilot-pull-request-reviewer[bot]", kind: "Bot")));
		Assert.False(Landing.ThreadOpenedByPerson(ThreadOf(Landing.GithubActions, kind: "Bot", review: Landing.ClaudeMarker + "\n**Claude Review**")));
	}

	[Fact]
	public void An_exhausted_loop_waits_on_a_thread_opened_by_a_person_acceptance_criterion_3()
	{
		var held = Landing.Decide(Exhausted with { OpenThreads = 2, OpenPersonThreads = 1 });
		Assert.Equal(Landing.Wait, held.Action);
		Assert.Contains("exhausted", held.Reason);
		Assert.Contains("person", held.Reason);
		Assert.Contains("1", held.Reason);
		// Only the person's thread holds; the same count of bot threads alone does not.
		Assert.Equal(Landing.HandOff, Landing.Decide(Exhausted with { OpenThreads = 2, OpenPersonThreads = 0 }).Action);
		// Both: the person's thread holds.
		Assert.Equal(Landing.Wait, Landing.Decide(Exhausted with { OpenThreads = 3, OpenPersonThreads = 2 }).Action);
	}

	[Fact]
	public void An_exhausted_loop_with_only_bot_threads_left_open_counts_just_those_acceptance_criterion_3()
	{
		var decision = Landing.Decide(Exhausted with { OpenThreads = 3, OpenPersonThreads = 0 });
		Assert.Equal(Landing.HandOff, decision.Action);
		Assert.Contains("3 thread(s) left open", decision.Reason);
	}

	[Fact]
	public void An_exhausted_loop_still_updates_a_behind_branch_and_reports_blockers_before_a_persons_thread_acceptance_criterion_3()
	{
		var held = Exhausted with { OpenThreads = 1, OpenPersonThreads = 1 };
		Assert.Equal(Landing.UpdateBranch, Landing.Decide(held with { MergeState = "BEHIND" }).Action);
		Assert.Equal(Landing.Blocker, Landing.Decide(held with { MergeState = "DIRTY" }).Action);
		Assert.Equal(Landing.Blocker, Landing.Decide(held with { Checks = [Failed("Test Suite")] }).Action);
	}

	[Fact]
	public void Review_rounds_counts_a_copilot_review_with_only_a_body_as_a_clean_round_acceptance_criterion_6()
	{
		(string?, string?, string?, string?, long, bool)[] reviews =
		[
			Record("Copilot", "c1", "", "2026-01-02T00:01:00Z", comments: 1),
			Record("Copilot", "c2", "", "2026-01-02T00:02:00Z", comments: 1),
			Record("Copilot", "c3", "Copilot reviewed 4 out of 4 changed files and generated no comments.", "2026-01-02T00:03:00Z"),
			Record("Copilot", "c4", "", "2026-01-02T00:04:00Z", comments: 1),
		];
		var found = Landing.ReviewRounds(reviews, Set(), false);
		Assert.Equal([("c1", true), ("c2", true), ("c3", false), ("c4", true)], OidsAndDirt(found));
		Assert.Equal(1, Landing.TrailingDirty(found));
		Assert.False((Exhausted with { Rounds = found }).LoopExhausted);
	}

	// CurrentLoop(rounds, adds): adds are (epoch seconds, writer) for each review:claude `labeled` event, oldest first,
	// writer true (write access), false, or null (the permission couldn't be read).
	// Rounds("d d d d d d") puts its Nth round at minute N-1 of 2026-01-02T00, so three dirty rounds hit at minute 2.

	static string[] Oids(IEnumerable<Round> rounds) => [.. rounds.Select(item => item.Oid)];

	[Fact]
	public void With_no_add_the_loop_is_what_the_rounds_say_acceptance_criterion_4()
	{
		var walk = Landing.CurrentLoop(Rounds("d d d"), []);
		Assert.Equal(Rounds("d d d"), walk.Rounds);
		Assert.Equal(AtMinute(2), walk.Hit);
		Assert.False(walk.Restarted);
		Assert.False(walk.Unread);
		var @short = Landing.CurrentLoop(Rounds("d d"), []);
		Assert.Equal(Rounds("d d"), @short.Rounds);
		Assert.Null(@short.Hit);
	}

	[Fact]
	public void An_add_by_a_writer_after_the_exhausting_round_starts_a_new_loop_acceptance_criterion_4()
	{
		var walk = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(2, 30), true)]);
		Assert.True(walk.Restarted);
		Assert.False(walk.Unread);
		Assert.Empty(walk.Rounds);
		Assert.Null(walk.Hit);
	}

	[Fact]
	public void Rounds_before_the_add_dont_count_in_the_new_loop_acceptance_criterion_4()
	{
		// A fourth round came before the add and the fifth and sixth after it.
		var walk = Landing.CurrentLoop(Rounds("d d d d d d"), [(AtMinute(3, 30), true)]);
		Assert.Equal(Oids(Rounds("d d d d d d")[4..]), Oids(walk.Rounds));
		Assert.True(walk.Restarted);
		Assert.Null(walk.Hit);
	}

	[Fact]
	public void An_add_before_the_exhausting_round_is_no_restart_acceptance_criterion_4()
	{
		var walk = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(1, 30), true), (AtMinute(0, 30), true)]);
		Assert.False(walk.Restarted);
		Assert.Equal(Rounds("d d d"), walk.Rounds);
		Assert.Equal(AtMinute(2), walk.Hit);
		// At the very moment of the round is not after it either.
		Assert.False(Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(2), true)]).Restarted);
	}

	[Fact]
	public void An_add_while_the_loop_is_not_exhausted_never_restarts_it_acceptance_criterion_4()
	{
		// PR Auto-Merge's own PAT adds the label for a later head in the loop: it mustn't discard the loop's rounds.
		var walk = Landing.CurrentLoop(Rounds("d d c d"), [(AtMinute(1, 30), true), (AtMinute(3, 30), true)]);
		Assert.False(walk.Restarted);
		Assert.Equal(Rounds("d d c d"), walk.Rounds);
	}

	[Fact]
	public void A_second_exhaustion_is_restarted_by_the_next_add_after_it_acceptance_criterion_4()
	{
		var pattern = Rounds("d d d d d d");
		var walk = Landing.CurrentLoop(pattern, [(AtMinute(2, 30), true), (AtMinute(5, 30), true)]);
		Assert.True(walk.Restarted);
		Assert.Empty(walk.Rounds);
		Assert.Null(walk.Hit);
		// An add in the middle of the second loop (before its third dirty round) doesn't restart it.
		var middle = Landing.CurrentLoop(pattern, [(AtMinute(2, 30), true), (AtMinute(4, 30), true)]);
		Assert.True(middle.Restarted);
		Assert.Equal(pattern[3..], middle.Rounds);
		Assert.Equal(AtMinute(5), middle.Hit);
	}

	[Fact]
	public void Only_a_login_with_write_access_restarts_a_loop_acceptance_criterion_11()
	{
		Assert.False(Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(3), false)]).Restarted);
		Assert.Equal(Rounds("d d d"), Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(3), false)]).Rounds);
		// The first add by a writer counts, after any number by readers.
		var walk = Landing.CurrentLoop(Rounds("d d d d d"), [(AtMinute(2, 10), false), (AtMinute(2, 20), false), (AtMinute(2, 30), true)]);
		Assert.True(walk.Restarted);
		Assert.Equal(Oids(Rounds("d d d d d")[3..]), Oids(walk.Rounds));
	}

	[Fact]
	public void An_add_whose_permission_cant_be_read_starts_no_loop_and_leaves_the_loop_unread_acceptance_criterion_11()
	{
		var walk = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(3), null)]);
		Assert.True(walk.Unread);
		Assert.False(walk.Restarted);
		// A reader's add before it is skipped, and the unreadable one still decides nothing.
		Assert.True(Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(3), false), (AtMinute(4), null)]).Unread);
	}

	[Fact]
	public void A_writers_add_before_an_unreadable_one_is_the_restart_acceptance_criterion_11()
	{
		var walk = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(3), true), (AtMinute(4), null)]);
		Assert.True(walk.Restarted);
		Assert.False(walk.Unread);
	}

	[Fact]
	public void A_round_with_no_known_time_ends_the_walk_without_a_restart_acceptance_criterion_4()
	{
		Round[] unknown = [.. Enumerable.Range(1, 3).Select(index => new Round(index.ToString("x40", CultureInfo.InvariantCulture), true, ""))];
		var walk = Landing.CurrentLoop(unknown, [(AtMinute(3), true)]);
		Assert.False(walk.Restarted);
		Assert.Equal(unknown, walk.Rounds);
	}

	[Fact]
	public void A_loop_restarted_by_a_writer_is_not_exhausted_and_asks_for_a_review_acceptance_criterion_4()
	{
		var walk = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(3), true)]);
		var pr = Exhausted with { Rounds = walk.Rounds };
		Assert.False(pr.LoopExhausted);
		Assert.Equal(Landing.RequestCopilot, Landing.Decide(pr).Action);
	}

	// CurrentLoop(rounds, adds, removals) (mpaulosky/dotfiles#381): removals are the epoch seconds of PR Auto-Merge's (github-actions)
	// removals of review:claude. An add counts unless one of them follows it before the next round submitted after it; the
	// add is then the one PR Auto-Merge took off at once because Copilot could review. Whether Copilot can review now no
	// longer decides it, so callers pass every add.

	[Fact]
	public void An_add_that_pr_auto_merge_removed_before_the_next_round_is_no_restart_acceptance_criterion_2()
	{
		var walk = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(2, 30), true)], removals: [AtMinute(2, 31)]);
		Assert.False(walk.Restarted);
		Assert.Equal(Rounds("d d d"), walk.Rounds);
		Assert.Equal(AtMinute(2), walk.Hit);
	}

	[Fact]
	public void An_add_stays_a_restart_when_the_removal_comes_after_the_next_round_acceptance_criterion_1()
	{
		var walk = Landing.CurrentLoop(Rounds("d d d d"), [(AtMinute(2, 30), true)], removals: [AtMinute(3, 30)]);
		Assert.True(walk.Restarted);
		Assert.Equal(Oids(Rounds("d d d d")[3..]), Oids(walk.Rounds));
	}

	[Fact]
	public void A_removal_before_the_add_does_not_cancel_it_acceptance_criterion_1()
	{
		// PR Auto-Merge takes the label off at exhaustion, before the writer's add that restarts the loop.
		var walk = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(2, 30), true)], removals: [AtMinute(2, 10)]);
		Assert.True(walk.Restarted);
		Assert.Empty(walk.Rounds);
	}

	[Fact]
	public void A_removal_cancels_only_the_add_it_follows_acceptance_criterion_2()
	{
		var walk = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(2, 30), true), (AtMinute(2, 40), true)], removals: [AtMinute(2, 31)]);
		Assert.True(walk.Restarted);
		Assert.Empty(walk.Rounds);
		// The second add is the restart: the rounds after it count, those between the two adds don't exist here.
		var later = Landing.CurrentLoop(Rounds("d d d d d"), [(AtMinute(2, 30), true), (AtMinute(3, 30), true)], removals: [AtMinute(2, 31)]);
		Assert.True(later.Restarted);
		Assert.Equal(Oids(Rounds("d d d d d")[4..]), Oids(later.Rounds));
	}

	[Fact]
	public void A_removal_after_a_copilot_request_is_not_the_removal_of_an_ignored_add_acceptance_criterion_1()
	{
		// d d d, a writer's add while Copilot can't review, then a request for Copilot's review goes pending and PR Auto-Merge
		// takes the label off (ignored while Copilot can review). The removal was made for the request, so the add still counts.
		var walk = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(2, 30), true)], removals: [AtMinute(2, 50)], copilotRequests: [AtMinute(2, 40)]);
		Assert.True(walk.Restarted);
		Assert.Empty(walk.Rounds);
		// A new dirty review of the new head then makes the streak 1, not 4.
		var later = Landing.CurrentLoop(Rounds("d d d d"), [(AtMinute(2, 30), true)], removals: [AtMinute(2, 50)], copilotRequests: [AtMinute(2, 40)]);
		Assert.True(later.Restarted);
		Assert.Single(later.Rounds);
	}

	[Fact]
	public void A_copilot_request_before_the_add_or_after_the_removal_does_not_save_the_add_acceptance_criterion_2()
	{
		var before = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(2, 30), true)], removals: [AtMinute(2, 31)], copilotRequests: [AtMinute(2, 10)]);
		Assert.False(before.Restarted);
		var after = Landing.CurrentLoop(Rounds("d d d"), [(AtMinute(2, 30), true)], removals: [AtMinute(2, 31)], copilotRequests: [AtMinute(2, 45)]);
		Assert.False(after.Restarted);
	}

	[Fact]
	public void A_restarted_loop_is_not_exhausted_again_by_the_old_streak_plus_one_dirty_round_acceptance_criterion_1()
	{
		// d d d, a writer's add, Copilot reviews the new head and raises a thread: the streak is 1, not 4.
		var walk = Landing.CurrentLoop(Rounds("d d d d"), [(AtMinute(2, 30), true)]);
		var pr = Exhausted with { Rounds = walk.Rounds };
		Assert.Single(walk.Rounds);
		Assert.Equal(1, Landing.TrailingDirty(walk.Rounds));
		Assert.False(pr.LoopExhausted);
	}

	[Fact]
	public void An_add_by_a_login_without_write_access_is_still_no_restart_when_nothing_removed_it_acceptance_criterion_2()
	{
		var walk = Landing.CurrentLoop(Rounds("d d d d"), [(AtMinute(2, 30), false)], removals: []);
		Assert.False(walk.Restarted);
		Assert.Equal(Rounds("d d d d"), walk.Rounds);
	}
}
