using System.Diagnostics;

// Tests for github/RolloutCore.cs, xUnit copies of github/tests/test_rollout.py (ADR 0017's Update on replacing a pytest suite): the rollout
// order, and the round it drives with reapply and gh faked, one method each, with the same cases and assertions. No GitHub.
// The real branch read's tests set the process's GIT_* variables, as the pytest fixture monkeypatched them, so the class runs in the round's
// collection, alone.
[Collection(nameof(RoundCollection))]
public sealed class RolloutTests
{
	const string TM = Fleets.TM;
	static readonly IReadOnlyList<string> Rest = Fleets.Rest;
	static readonly IReadOnlyList<KeyValuePair<string, string>> Listed = Fleets.Listed;

	static List<string> Order => Rollout.RolloutOrder(Listed);

	// ── The order ───────────────────────────────────────────────────────────────

	[Fact]
	public void Order_is_ticketmanager_then_the_rest_in_the_skills_order_without_the_factory() =>
		Assert.Equal([TM, .. Rest], Rollout.RolloutOrder(Listed));

	[Fact]
	public void Order_leaves_out_every_scope_but_full()
	{
		KeyValuePair<string, string>[] listed = [new(TM, "full"), new("mpaulosky/Application_Factory", "factory"), new("mpaulosky/old", "repo-settings-only")];
		Assert.Equal([TM], Rollout.RolloutOrder(listed));
	}

	[Fact]
	public void Order_puts_ticketmanager_first_wherever_it_is_listed()
	{
		KeyValuePair<string, string>[] listed = [new("mpaulosky/IssueTracker", "full"), new(TM, "full"), new("mpaulosky/Articles", "full")];
		Assert.Equal([TM, "mpaulosky/IssueTracker", "mpaulosky/Articles"], Rollout.RolloutOrder(listed));
	}

	[Fact]
	public void A_round_starts_with_ticketmanager_alone()
	{
		var step = Rollout.NextStep(Order, null);
		Assert.Equal([TM], step.Run);
		Assert.Equal(Rest, step.Held);
	}

	[Fact]
	public void The_rest_are_held_while_ticketmanagers_reapply_pr_is_open()
	{
		var step = Rollout.NextStep(Order, new Lead(true, 129, "OPEN"));
		Assert.Empty(step.Run);
		Assert.Equal(Rest, step.Held);
		Assert.Equal("waiting for TicketManager#129 to merge", step.Reason);
	}

	[Fact]
	public void The_rest_are_held_when_ticketmanagers_reapply_failed()
	{
		var step = Rollout.NextStep(Order, new Lead(false, 128, "MERGED"));
		Assert.Empty(step.Run);
		Assert.Equal(Rest, step.Held);
		Assert.Equal("reapply failed on TicketManager", step.Reason);
	}

	[Fact]
	public void The_rest_are_held_when_ticketmanagers_reapply_pr_was_closed_unmerged()
	{
		var step = Rollout.NextStep(Order, new Lead(true, 129, "CLOSED"));
		Assert.Empty(step.Run);
		Assert.Equal(Rest, step.Held);
		Assert.Equal("TicketManager#129 was closed without merging", step.Reason);
	}

	[Fact]
	public void The_rest_are_held_when_no_reapply_pr_of_ticketmanagers_is_found()
	{
		var step = Rollout.NextStep(Order, new Lead(true, null, null));
		Assert.Empty(step.Run);
		Assert.Equal("no re-Apply PR of TicketManager's found to have merged", step.Reason);
	}

	[Fact]
	public void The_rest_run_when_ticketmanager_pushed_nothing_and_has_no_reapply_pr()
	{
		var step = Rollout.NextStep(Order, new Lead(true, null, null, Ahead: false));
		Assert.Equal(Rest, step.Run);
		Assert.Empty(step.Held);
	}

	[Fact]
	public void The_rest_run_when_ticketmanager_pushed_nothing_and_its_last_pr_was_closed() =>
		Assert.Equal(Rest, Rollout.NextStep(Order, new Lead(true, 129, "CLOSED", Ahead: false)).Run);

	[Fact]
	public void The_rest_are_held_when_ticketmanager_pushed_commits_no_pr_shows()
	{
		var step = Rollout.NextStep(Order, new Lead(true, null, null, Ahead: true));
		Assert.Empty(step.Run);
		Assert.Equal("no re-Apply PR of TicketManager's found to have merged", step.Reason);
		step = Rollout.NextStep(Order, new Lead(true, 129, "CLOSED", Ahead: true));
		Assert.Equal("TicketManager#129 was closed without merging", step.Reason);
	}

	[Fact]
	public void A_failed_reapply_holds_the_rest_even_with_nothing_pushed()
	{
		var step = Rollout.NextStep(Order, new Lead(false, null, null, Ahead: false));
		Assert.Empty(step.Run);
		Assert.Equal("reapply failed on TicketManager", step.Reason);
	}

	[Fact]
	public void Nothing_pushed_doesnt_let_the_rest_past_an_open_pr()
	{
		var step = Rollout.NextStep(Order, new Lead(true, 129, "OPEN", Ahead: false));
		Assert.Empty(step.Run);
		Assert.Equal("waiting for TicketManager#129 to merge", step.Reason);
	}

	[Fact]
	public void A_merged_pr_with_nothing_pushed_lets_the_rest_run_as_before()
	{
		var step = Rollout.NextStep(Order, new Lead(true, 129, "MERGED", Ahead: false));
		Assert.Equal(Rest, step.Run);
		Assert.Equal("", step.Reason);
	}

	[Fact]
	public void A_merged_pr_while_the_worktree_has_new_commits_is_an_earlier_rounds_and_holds()
	{
		// reapply.sh pushed and opened a new PR, but GitHub's list still shows the last round's merged one.
		var step = Rollout.NextStep(Order, new Lead(true, 129, "MERGED", Ahead: true));
		Assert.Empty(step.Run);
		Assert.Equal(Rest, step.Held);
		Assert.Equal("TicketManager's re-Apply worktree has commits main lacks, but its newest PR shown, #129, has"
				+ " merged: GitHub's list may not show the new one yet; run rollout.sh again", step.Reason);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("CLOSED")]
	[InlineData("MERGED")]
	public void An_unreadable_branch_holds_the_rest_whatever_the_pr(string? state)
	{
		var step = Rollout.NextStep(Order, new Lead(true, 129, state, Unreadable: true));
		Assert.Empty(step.Run);
		Assert.Equal("can't tell whether TicketManager has anything to land: its re-Apply branch can't be read", step.Reason);
	}

	[Fact]
	public void A_ticketmanager_already_current_lets_the_rest_go_on_in_one_run()
	{
		var fake = new Fake(latest: new() { [TM] = null });
		List<string> asked = [];
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r"], fake, ahead: false, asked: asked);
		Assert.Equal(0, code);
		Assert.Equal(Order.Select(Fleets.Short), RanRepos(fake));
		Assert.Contains("TicketManager already matches the Template: nothing to land, so the rest go on", output, StringComparison.Ordinal);
		Assert.Equal(["TicketManager"], asked);
	}

	[Fact]
	public void A_ticketmanager_with_commits_and_no_pr_still_holds_the_rest()
	{
		var fake = new Fake(latest: new() { [TM] = null });
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r"], fake, ahead: true);
		Assert.Equal(1, code);
		Assert.Equal(["TicketManager"], RanRepos(fake));
		Assert.Contains("held:", output, StringComparison.Ordinal);
	}

	[Fact]
	public void An_unreadable_branch_or_pr_holds_the_rest_and_says_why()
	{
		var fake = new Fake(latest: new() { [TM] = null });
		var (code, output, err) = Run(["--brings", "#82", "--root", "/r"], fake, ahead: new InvalidOperationException("not a git repository"));
		Assert.Equal(1, code);
		Assert.Equal(["TicketManager"], RanRepos(fake));
		Assert.Contains("can't read TicketManager's re-Apply branch: not a git repository", err, StringComparison.Ordinal);
		Assert.Contains("rollout: can't tell whether TicketManager has anything to land: its re-Apply branch can't be read; held:", output, StringComparison.Ordinal);
		fake = new Fake(); // the last round's PR, merged: an unreadable branch holds that too, where it used to go on
		(code, output, _) = Run(["--brings", "#82", "--root", "/r"], fake, ahead: new InvalidOperationException("no worktree"));
		Assert.Equal(1, code);
		Assert.Equal(["TicketManager"], RanRepos(fake));
		Assert.Contains("can't tell whether TicketManager has anything to land", output, StringComparison.Ordinal);
		fake = new Fake(unreadable: [TM]);
		List<string> asked = [];
		(code, _, _) = Run(["--brings", "#82", "--root", "/r"], fake, ahead: false, asked: asked);
		Assert.Equal(1, code);
		Assert.Equal(["TicketManager"], RanRepos(fake));
		Assert.Empty(asked); // the PR is unknown, so nothing pushed doesn't decide it
	}

	[Fact]
	public void A_failed_reapply_reads_no_branch()
	{
		var fake = new Fake(latest: new() { [TM] = null }, fail: [TM]);
		List<string> asked = [];
		var (code, output, err) = Run(["--brings", "#82", "--root", "/r"], fake, ahead: new InvalidOperationException("no worktree"), asked: asked);
		Assert.Equal(1, code);
		Assert.Empty(asked);
		Assert.DoesNotContain("can't read", output + err, StringComparison.Ordinal);
		Assert.Contains("reapply failed on TicketManager", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_dry_run_reads_no_branch_and_holds_as_before()
	{
		// A dry run reapplies nothing, so the worktree says nothing of what the reapply would push.
		var fake = new Fake(latest: new() { [TM] = null });
		List<string> asked = [];
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r", "--dry-run"], fake, ahead: false, asked: asked);
		Assert.Equal(1, code);
		Assert.Empty(asked);
		Assert.DoesNotContain("already matches", output, StringComparison.Ordinal);
		Assert.Contains("held:", output, StringComparison.Ordinal);
	}

	[Fact]
	public void The_branch_is_read_unless_the_pr_is_open()
	{
		List<string> asked = [];
		Run(["--brings", "#82", "--root", "/r"], new Fake(opens: new() { [TM] = 130 }), ahead: false, asked: asked);
		Assert.Empty(asked);
		Run(["--brings", "#82", "--root", "/r"], new Fake(), ahead: false, asked: asked);
		Assert.Equal(["TicketManager"], asked);
	}

	[Fact]
	public void A_merged_pr_shown_while_the_worktree_has_new_commits_holds_the_round()
	{
		var fake = new Fake(); // the list still shows TicketManager's last round, merged
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r"], fake, ahead: true);
		Assert.Equal(1, code);
		Assert.Equal(["TicketManager"], RanRepos(fake));
		Assert.Contains("GitHub's list may not show the new one yet; run rollout.sh again; held: IssueTracker", output, StringComparison.Ordinal);
	}

	[Fact]
	public void The_rest_run_in_order_once_ticketmanagers_reapply_pr_has_merged()
	{
		var step = Rollout.NextStep(Order, new Lead(true, 129, "MERGED"));
		Assert.Equal(Rest, step.Run);
		Assert.Empty(step.Held);
	}

	[Fact]
	public void An_order_without_ticketmanager_is_refused()
	{
		// ArgumentException where rollout_order raised ValueError.
		var error = Assert.Throws<ArgumentException>(() => Rollout.RolloutOrder([new KeyValuePair<string, string>("mpaulosky/IssueTracker", "full")]));
		Assert.Contains("TicketManager", error.Message, StringComparison.Ordinal);
	}

	// ── The round ───────────────────────────────────────────────────────────────

	// reapply.sh and the newest re-Apply PR lookup as the tests set them.
	sealed class Fake
	{
		// {repo: (number, state)}; every repo's last round merged unless set
		public readonly Dictionary<string, (long Number, string State)?> Latest;
		readonly HashSet<string> fail; // repos whose reapply exits code
		readonly int code;
		readonly Dictionary<string, long> opens; // {repo: PR number its reapply opens}
		readonly HashSet<string> unreadable; // repos whose lookup fails
		public readonly List<(string Brings, string Path)> Ran = [];

		public Fake(Dictionary<string, (long Number, string State)?>? latest = null, IEnumerable<string>? fail = null,
				Dictionary<string, long>? opens = null, IEnumerable<string>? unreadable = null, int code = 1)
		{
			Latest = Listed.ToDictionary(pair => pair.Key, _ => ((long, string)?)(100, "MERGED"));
			foreach (var (repo, pr) in latest ?? [])
				Latest[repo] = pr;
			this.fail = [.. fail ?? []];
			this.code = code;
			this.opens = opens ?? [];
			this.unreadable = [.. unreadable ?? []];
		}

		public int? Reapply(string brings, string path)
		{
			Ran.Add((brings, path));
			var repo = "mpaulosky/" + Path.GetFileName(path);
			if (fail.Contains(repo))
				return code;
			if (opens.TryGetValue(repo, out var number))
				Latest[repo] = (number, "OPEN");
			return 0;
		}

		public (long Number, string State)? LatestPr(string repo) =>
				unreadable.Contains(repo) ? throw new InvalidOperationException("HTTP 502") : Latest.GetValueOrDefault(repo);
	}

	// Rollout.Run with fake's reapply and PR lookup. ahead: what the branch read answers (a bool) or throws (an exception); the checkout
	// paths it is asked for go into asked. realBranch: the real read, git on the re-Apply worktree, instead. Unlike the pytest's, the read
	// can't answer None (the C# seam is a bool), so the default is false: the same step as None for every PR state but CLOSED or none.
	static (int Code, string Output, string Err) Run(string[] argv, Fake fake, Func<string, bool>? exists = null, object? ahead = null,
			List<string>? asked = null, bool realBranch = false)
	{
		var (output, err) = (new StringWriter(), new StringWriter());

		bool AheadOf(string path)
		{
			asked?.Add(Path.GetFileName(path));
			return ahead is Exception error ? throw error : ahead is true;
		}

		var code = Rollout.Run(argv, output, err, listed: Listed, reapply: fake.Reapply, latestPr: fake.LatestPr, exists: exists ?? (_ => true),
				ahead: realBranch ? null : AheadOf);
		return (code, output.ToString(), err.ToString());
	}

	static List<string> RanRepos(Fake fake) => [.. fake.Ran.Select(ran => Path.GetFileName(ran.Path))];

	static int Count(string text, string part)
	{
		var (count, at) = (0, text.IndexOf(part, StringComparison.Ordinal));
		for (; at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
			count++;
		return count;
	}

	[Fact]
	public void First_run_applies_ticketmanager_only_and_holds_the_rest()
	{
		var fake = new Fake(opens: new() { [TM] = 130 });
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r"], fake);
		Assert.Equal(0, code);
		Assert.Equal([("#82", "/r/TicketManager")], fake.Ran);
		Assert.Contains("waiting for TicketManager#130 to merge", output, StringComparison.Ordinal);
		Assert.Contains("held: IssueTracker, Blazor-Server, IssueManager, Articles, SuggestionApp", output, StringComparison.Ordinal);
		Assert.Contains("land.sh mpaulosky/TicketManager#130", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Once_ticketmanager_merged_the_rest_follow_one_at_a_time_in_order()
	{
		var fake = new Fake(opens: Rest.Select((repo, n) => (repo, n)).ToDictionary(pair => pair.repo, pair => 200L + pair.n));
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r"], fake);
		Assert.Equal(0, code);
		Assert.Equal(["TicketManager", "IssueTracker", "Blazor-Server", "IssueManager", "Articles", "SuggestionApp"], RanRepos(fake));
		Assert.Contains("land.sh mpaulosky/IssueTracker#200 mpaulosky/Blazor-Server#201 mpaulosky/IssueManager#202 "
				+ "mpaulosky/Articles#203 mpaulosky/SuggestionApp#204", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_failed_repo_is_reported_and_the_rest_still_run()
	{
		var fake = new Fake(fail: ["mpaulosky/Blazor-Server"]);
		var (code, output, err) = Run(["--brings", "#82", "--root", "/r"], fake);
		Assert.Equal(1, code);
		Assert.Equal(["IssueManager", "Articles", "SuggestionApp"], RanRepos(fake)[^3..]);
		Assert.Contains("failed: Blazor-Server", output, StringComparison.Ordinal);
		Assert.Contains("Blazor-Server", err, StringComparison.Ordinal);
	}

	[Fact]
	public void A_failed_ticketmanager_holds_the_round_and_exits_1()
	{
		var fake = new Fake(fail: [TM]);
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r"], fake);
		Assert.Equal(1, code);
		Assert.Equal(["TicketManager"], RanRepos(fake));
		Assert.Contains("reapply failed on TicketManager", output, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(3, "reapply failed on TicketManager: the gate asks for a review: run /pre-push-review in its re-Apply worktree"
			+ " (or rollout.sh --auto), then run this again")]
	[InlineData(4, "reapply failed on TicketManager: review findings wait for you: fix them with /pre-push-review in its re-Apply"
			+ " worktree, or accept them with scripts/review.sh accept-open there, then run this again")]
	[InlineData(1, "reapply failed on TicketManager\n")]
	public void A_review_refusal_says_what_reapply_asks_of_the_person(int code, string said)
	{
		var (code_, output, err) = Run(["--brings", "#82", "--root", "/r"], new Fake(fail: [TM], code: code));
		Assert.True(code_ == 1 && (output + err).Contains(said, StringComparison.Ordinal));
	}

	[Fact]
	public void A_missing_checkout_stops_the_round_before_anything_runs()
	{
		var fake = new Fake();
		HashSet<string> missing = ["/r/IssueManager", "/r/Articles"];
		var (code, _, err) = Run(["--brings", "#82", "--root", "/r"], fake, exists: path => !missing.Contains(path));
		Assert.Equal(1, code);
		Assert.Empty(fake.Ran);
		Assert.Contains("no checkout at /r/IssueManager for mpaulosky/IssueManager", err, StringComparison.Ordinal);
		Assert.Contains("no checkout at /r/Articles for mpaulosky/Articles", err, StringComparison.Ordinal);
		Assert.Contains("retire it from github/repos.txt", err, StringComparison.Ordinal);
	}

	[Fact]
	public void A_missing_checkout_stops_a_dry_run_too()
	{
		var (code, output, err) = Run(["--brings", "#82", "--root", "/r", "--dry-run"], new Fake(), exists: path => path != "/r/Articles");
		Assert.Equal(1, code);
		Assert.DoesNotContain("would run", output, StringComparison.Ordinal);
		Assert.Contains("no checkout at /r/Articles", err, StringComparison.Ordinal);
	}

	[Fact]
	public void A_repo_settings_only_repo_needs_no_checkout()
	{
		var (code, _, err) = Run(["--brings", "#82", "--root", "/r", "--dry-run"], new Fake(), exists: path => path != "/r/Application_Factory");
		Assert.Equal(0, code);
		Assert.DoesNotContain("Application_Factory", err, StringComparison.Ordinal);
	}

	[Fact]
	public void Dry_run_runs_nothing_and_shows_the_plan_from_the_open_pr()
	{
		var fake = new Fake(latest: new() { [TM] = (129, "OPEN") });
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r", "--dry-run"], fake);
		Assert.Equal(0, code);
		Assert.Empty(fake.Ran);
		Assert.Contains("would run: reapply.sh --brings '#82' /r/TicketManager", output, StringComparison.Ordinal);
		Assert.Contains("waiting for TicketManager#129 to merge", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_dry_runs_commands_quote_the_checkout_so_they_run_as_printed()
	{
		var (code, output, _) = Run(["--brings", "#82", "--root", "/my repos", "--dry-run"], new Fake());
		Assert.Equal(0, code);
		Assert.Contains("would run: reapply.sh --brings '#82' '/my repos/TicketManager'\n", output, StringComparison.Ordinal);
		Assert.DoesNotContain("/my repos/TicketManager\n", output.Replace("'/my repos/TicketManager'\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
	}

	[Fact]
	public void Dry_run_with_ticketmanager_landed_shows_every_repo()
	{
		var fake = new Fake();
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r", "--dry-run"], fake);
		Assert.Equal(0, code);
		Assert.Empty(fake.Ran);
		Assert.Equal(6, Count(output, "would run: reapply.sh"));
		Assert.Contains("then, if that leaves TicketManager with no new re-Apply PR (its Apply is already on main):", output, StringComparison.Ordinal);
		var then = output.IndexOf("then, if", StringComparison.Ordinal);
		Assert.True(output.IndexOf("/r/TicketManager", StringComparison.Ordinal) < then && then < output.IndexOf("/r/IssueTracker", StringComparison.Ordinal));
	}

	[Fact]
	public void A_failed_lookup_on_ticketmanager_is_reported_as_one_and_holds_the_round()
	{
		var fake = new Fake(unreadable: [TM]);
		var (code, output, err) = Run(["--brings", "#82", "--root", "/r"], fake);
		Assert.Equal(1, code);
		Assert.Equal(["TicketManager"], RanRepos(fake));
		Assert.Contains("can't read mpaulosky/TicketManager's re-Apply PR: HTTP 502", err, StringComparison.Ordinal);
		Assert.DoesNotContain("reapply failed", output + err, StringComparison.Ordinal);
	}

	[Fact]
	public void A_closed_unmerged_reapply_pr_holds_the_round_on_rerun()
	{
		var fake = new Fake(latest: new() { [TM] = (129, "CLOSED") });
		// The pytest's branch read answered None (unread); the C# seam answers a bool, and a closed PR with nothing pushed lets the rest go on
		// (a test above), so the read here says the worktree has commits, the case that holds.
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r"], fake, ahead: true);
		Assert.Equal(1, code);
		Assert.Equal(["TicketManager"], RanRepos(fake));
		Assert.Contains("TicketManager#129 was closed without merging", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_merged_reapply_pr_on_the_rest_is_not_offered_to_land()
	{
		var (code, output, _) = Run(["--brings", "#82", "--root", "/r"], new Fake(opens: new() { ["mpaulosky/Articles"] = 300 }));
		Assert.Equal(0, code);
		Assert.Contains("land them with: land.sh mpaulosky/Articles#300\n", output, StringComparison.Ordinal);
	}

	// ── The branch name ─────────────────────────────────────────────────────────

	[Fact]
	public void Every_copy_of_the_reapply_branch_name_agrees()
	{
		// rollout finds the round's PRs by reapply.sh's branch, so a rename has to reach every copy: the C# has three where the Python had
		// rollout.py's BRANCH.
		var script = File.ReadAllText(Skill.Path("reapply.sh"));
		foreach (var branch in new[] { Rollout.Branch, AutoRollout.Branch, SessionGuard.Branch })
			Assert.Contains($"branch=\"{branch}\"", script, StringComparison.Ordinal);
	}

	// ── The real branch read and PR lookup ──────────────────────────────────────

	static readonly Dictionary<string, string?> GitEnv = new()
	{
		["GIT_DIR"] = null,
		["GIT_WORK_TREE"] = null,
		["GIT_INDEX_FILE"] = null,
		["GIT_PREFIX"] = null,
		["GIT_CONFIG_GLOBAL"] = "/dev/null",
		["GIT_CONFIG_NOSYSTEM"] = "1",
		["GIT_AUTHOR_NAME"] = "t",
		["GIT_AUTHOR_EMAIL"] = "t@t",
		["GIT_COMMITTER_NAME"] = "t",
		["GIT_COMMITTER_EMAIL"] = "t@t",
	};

	// A real TicketManager checkout under a new folder with the re-Apply worktree reapply.sh makes beside it, level with origin/main; a git
	// that runs in it. The test runs inside it with the process's GIT_* variables set as the pytest fixture set them, and they and the folder
	// are put back after.
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	static void WithTicketManager(Action<string, string, Action<string, string[]>> test)
	{
		var root = Directory.CreateTempSubdirectory("rollout.").FullName;
		var saved = GitEnv.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
		try
		{
			foreach (var (name, value) in GitEnv)
				Environment.SetEnvironmentVariable(name, value);
			var primary = Path.Combine(root, "TicketManager");
			var worktree = Path.Combine(root, "TicketManager-worktrees", "chore-reapply-baseline");

			static void Git(string where, params string[] args)
			{
				var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
				foreach (var arg in new[] { "-C", where }.Concat(args))
					start.ArgumentList.Add(arg);
				using var git = Process.Start(start)!;
				var output = git.StandardOutput.ReadToEndAsync();
				var err = git.StandardError.ReadToEnd();
				git.WaitForExit();
				_ = output.Result;
				Assert.True(git.ExitCode == 0, $"git {string.Join(" ", args)}: {err}");
			}

			Directory.CreateDirectory(primary);
			Git(primary, "init", "-q", "-b", "main");
			Git(primary, "commit", "-q", "--allow-empty", "-m", "one");
			Git(primary, "update-ref", "refs/remotes/origin/main", "HEAD");
			Git(primary, "worktree", "add", "-q", "--no-track", "-b", Rollout.Branch, worktree, "origin/main");
			test(root, worktree, Git);
		}
		finally
		{
			foreach (var (name, value) in saved)
				Environment.SetEnvironmentVariable(name, value);
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	public void The_real_branch_read_lets_the_rest_go_on_when_the_worktree_is_level_with_main()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "the real branch read is --auto's, which runs on Linux only");
		WithTicketManager((root, _, _) =>
		{
			var fake = new Fake(latest: new() { [TM] = null });
			var (code, output, err) = Run(["--brings", "#82", "--root", root], fake, realBranch: true);
			Assert.Equal((0, ""), (code, err));
			Assert.Contains("TicketManager already matches the Template: nothing to land, so the rest go on", output, StringComparison.Ordinal);
			Assert.Equal(Order.Select(Fleets.Short), RanRepos(fake));
		});
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	public void The_real_branch_read_holds_the_rest_when_the_worktree_has_commits_main_lacks()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "the real branch read is --auto's, which runs on Linux only");
		WithTicketManager((root, worktree, git) =>
		{
			git(worktree, ["commit", "-q", "--allow-empty", "-m", "re-Apply"]);
			var fake = new Fake(latest: new() { [TM] = null });
			var (code, output, _) = Run(["--brings", "#82", "--root", root], fake, realBranch: true);
			Assert.Equal(1, code);
			Assert.Equal(["TicketManager"], RanRepos(fake));
			Assert.Contains("no re-Apply PR of TicketManager's found to have merged; held:", output, StringComparison.Ordinal);
			fake = new Fake(); // the last round's merged PR, shown while the list lags this push
			(code, output, _) = Run(["--brings", "#82", "--root", root], fake, realBranch: true);
			Assert.Equal(1, code);
			Assert.Equal(["TicketManager"], RanRepos(fake));
			Assert.Contains("GitHub's list may not show the new one yet", output, StringComparison.Ordinal);
		});
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	public void The_real_branch_read_holds_the_rest_and_says_why_when_git_cant_answer()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "the real branch read is --auto's, which runs on Linux only");
		WithTicketManager((root, _, git) =>
		{
			git(Path.Combine(root, "TicketManager"), ["update-ref", "-d", "refs/remotes/origin/main"]);
			var fake = new Fake(latest: new() { [TM] = null });
			var (code, output, err) = Run(["--brings", "#82", "--root", root], fake, realBranch: true);
			Assert.Equal(1, code);
			Assert.Equal(["TicketManager"], RanRepos(fake));
			Assert.Contains("can't read TicketManager's re-Apply branch: git merge-base in", err, StringComparison.Ordinal);
			Assert.Contains("can't tell whether TicketManager has anything to land", output, StringComparison.Ordinal);
		});
	}

	[Fact]
	public void The_plain_modes_lookup_is_autos_without_the_head()
	{
		// The pytest monkeypatched auto_rollout.gh_pr; here gh_pr (AutoRound.GhPr) is the real one, with gh behind its runner seam.
		var answers = new Queue<string>(["130 OPEN abc123\n", ""]);
		List<string> seen = [];
		Ran Gh(IReadOnlyList<string> command, string? cwd = null, IReadOnlyDictionary<string, string>? env = null)
		{
			seen.Add(command[command.ToList().IndexOf("-R") + 1]);
			return new Ran(0, answers.Dequeue(), "");
		}
		Assert.Equal((130L, "OPEN"), Rollout.GhLatestPr(TM, Gh));
		Assert.Null(Rollout.GhLatestPr(TM, Gh));
		Assert.Equal([TM, TM], seen);
	}
}
