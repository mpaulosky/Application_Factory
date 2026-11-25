// Roll a re-Apply round out across the Baseline repos, TicketManager first (rollout.sh; ADR 0016 for --auto).
//
//     rollout.sh --brings <prs> [--root dir] [--dry-run] [--auto]
//
// The Baseline repos are github/repos.txt's, in its order, without the ones of another scope (the Factory, "factory"); TicketManager goes
// first as the live check of the Template change. Each run:
//
// 1. runs reapply.sh on TicketManager;
// 2. holds the rest until TicketManager's newest re-Apply PR has merged: while it is open, land it (land.sh prints how), fix any finding
//    Template first, then run rollout.sh again. A failed reapply, or a PR or re-Apply branch that can't be read, holds the round too. So
//    does a PR closed without merging, or none found, unless TicketManager already matches the Template: its re-Apply worktree has no
//    commit main lacks, so there is nothing to land, and the rest go on in the same run, as under --auto. A reapply stopped at the gate's
//    review (exit 3) or on findings that wait for a person (exit 4) says what to run first;
// 3. once that PR has merged and reapply.sh found nothing new to push, the rest get reapply.sh in order, one at a time, so one pre-push
//    gate runs at a time. A merged PR while the re-Apply worktree has commits main lacks is an earlier round's (GitHub's list can lag the
//    push that opened the new one), so that holds the round too, as under --auto. A repo that fails is reported and the round goes on.
//
// Each repo's checkout is <root>/<Repo> (default ~/github). A repo in repos.txt with no checkout stops the round before anything runs:
// clone it, or retire it from repos.txt. It ends with the land.sh command that watches the re-Apply PRs it left open, and exits 1 when any
// reapply failed or the round is held for anything but an open PR. --dry-run prints the reapply.sh calls it would make from the PRs as they
// are now, and makes none: the rest's calls only follow when TicketManager's reapply opens no new PR. A dry run reapplies nothing, so it
// can't tell whether TicketManager already matches the Template: with no merged PR it holds the rest (and exits 1) where the real run may
// go on.
//
// --auto runs the whole round in one run, with no person between the steps (AutoRolloutRound.cs, ADR 0016: what its preflight checks, how
// the review session is sandboxed and checked, the lock, the process groups and the signals). With --dry-run it checks the tools and prints
// its plan, and runs nothing.
using System.Diagnostics;

// How TicketManager stands after its reapply: whether reapply.sh went cleanly, and its newest re-Apply PR with its state (OPEN, MERGED or
// CLOSED), or null for both when there is none or it couldn't be read. Ahead: whether its re-Apply worktree has commits main lacks, read
// only when the reapply went cleanly and that PR was read and isn't open; null when unread. Unreadable: the read was tried and failed, so
// whether there is anything to land is unknown.
sealed record Lead(bool Reapplied, long? Pr, string? State, bool? Ahead = null, bool Unreadable = false);

// What runs next: the repos to reapply now, in order, and the repos that wait.
sealed record Step(IReadOnlyList<string> Run, IReadOnlyList<string> Held, string Reason = "");

static class Rollout
{
	public const string LeadRepo = "mpaulosky/TicketManager";
	public const string Branch = "chore/reapply-baseline";
	public const string Description = "Roll a re-Apply round out across the Baseline repos, TicketManager first.";

	// Where each repo's checkout is by default: ~/github/<Repo>.
	public static string DefaultRoot => PyLib.ExpandUser("~/github");

	// What reapply.sh's review refusals ask of the person (its exit 3 and 4, AutoRound's ReviewNeeded and FindingsWait: a test holds them
	// equal), said after "reapply failed on <repo>".
	public static readonly IReadOnlyDictionary<int, string> ReapplyStops = new Dictionary<int, string>
	{
		[3] = ": the gate asks for a review: run /pre-push-review in its re-Apply worktree (or rollout.sh --auto), then run this again",
		[4] = ": review findings wait for you: fix them with /pre-push-review in its re-Apply worktree, or accept them with scripts/review.sh"
				+ " accept-open there, then run this again",
	};

	// The Baseline repos in repos.txt's order, TicketManager first. Throws ArgumentException when TicketManager isn't one.
	public static List<string> RolloutOrder(IEnumerable<KeyValuePair<string, string>> listed)
	{
		List<string> repos = [.. listed.Where(pair => pair.Value == "full").Select(pair => pair.Key)];
		if (!repos.Contains(LeadRepo))
			throw new ArgumentException($"{LeadRepo} isn't a Baseline repo in repos.txt; it goes first as the live check");
		return [.. repos.OrderBy(repo => repo != LeadRepo)];
	}

	// What runs next: TicketManager alone, then (once its PR has merged) the rest.
	public static Step NextStep(IReadOnlyList<string> order, Lead? lead)
	{
		var (first, rest) = (order[0], order.Skip(1).ToList());
		var name = AutoRollout.ShortName(first);
		if (lead is null)
			return new([first], rest, $"{name} goes first");
		if (!lead.Reapplied)
			return new([], rest, $"reapply failed on {name}");
		if (lead.Unreadable)
			return new([], rest, $"can't tell whether {name} has anything to land: its re-Apply branch can't be read");
		if (lead.Ahead == false && lead.State is null or "CLOSED")
			// Nothing pushed: reapply.sh found it already matches the Template, so there is no PR to wait for.
			return new(rest, [], $"{name} already matches the Template: nothing to land, so the rest go on");
		if (lead.State == "OPEN")
			return new([], rest, $"waiting for {name}#{lead.Pr} to merge");
		if (lead.State == "CLOSED")
			return new([], rest, $"{name}#{lead.Pr} was closed without merging");
		if (lead.State != "MERGED")
			return new([], rest, $"no re-Apply PR of {name}'s found to have merged");
		if (lead.Ahead == true)
			// reapply.sh starts a merged round's worktree again from main, so commits main lacks are this run's push.
			return new([], rest, $"{name}'s re-Apply worktree has commits main lacks, but its newest PR shown, #{lead.Pr}, has merged: GitHub's list"
					+ " may not show the new one yet; run rollout.sh again");
		return new(rest, []);
	}

	// reapply.sh on one checkout, its output straight through; its exit code.
	public static int? RunReapply(string brings, string path, Folders? folders = null)
	{
		var start = new ProcessStartInfo((folders ?? Folders.Default).Reapply) { UseShellExecute = false };
		foreach (var arg in new[] { "--brings", brings, path })
			start.ArgumentList.Add(arg);
		using var reapply = Process.Start(start)!;
		reapply.WaitForExit();
		return reapply.ExitCode;
	}

	// (number, state) of repo's newest re-Apply PR from repo itself (not a fork's of the same branch name), or null; --auto's lookup, so
	// both modes read the same PR. run stands in for gh, for the tests.
	public static (long Number, string State)? GhLatestPr(string repo) => GhLatestPr(repo, null);

	public static (long Number, string State)? GhLatestPr(string repo, Runner? run) =>
			AutoRound.GhPr(repo, run) is { } found ? (found.Number, found.State) : null;

	// rollout.sh's command line. listed, reapply, latestPr, exists, auto and ahead stand in for repos.txt, reapply.sh, gh, the checkouts, the
	// --auto round's outside world and whether the checkout at a path has a re-Apply worktree with commits main lacks (throws when git can't
	// say), for the tests, as do advisories (ReviewSession.OpenAdvisories), onSignal (what a signal outside a hold does: AutoRound.Run's) and
	// defaultRoot (DefaultRoot); folders is the skill rollout.cs is in.
	public static int Run(IReadOnlyList<string> argv, TextWriter output, TextWriter err, IEnumerable<KeyValuePair<string, string>>? listed = null,
			Func<string, string, int?>? reapply = null, Func<string, (long Number, string State)?>? latestPr = null, Func<string, bool>? exists = null,
			World? auto = null, Func<string, bool>? ahead = null, Folders? folders = null, Func<string, string?>? advisories = null,
			Action<int, string>? onSignal = null, string? defaultRoot = null)
	{
		defaultRoot ??= DefaultRoot;
		var parser = new Arguments("rollout.cs", "[-h] --brings BRINGS [--root ROOT] [--dry-run] [--auto]", Description)
				.Option("--brings", "the Factory PRs this round brings in, as reapply.sh takes them: '#80,#81' (quoted) or mpaulosky/Application_Factory#80,#81")
				.Option("--root", "the directory holding each repo's checkout (default ~/github)")
				.Flag("--dry-run", "print the reapply.sh calls instead of making them (with --auto: check the tools, print the plan)")
				.Flag("--auto", "check the tools, then reapply, review and land each repo with no person between the steps (ADR 0016)");
		if (parser.Parse(argv, output, err) is int exit)
			return exit;
		if (parser.Values.GetValueOrDefault("--brings") is not { } brings)
			return parser.Error("the following arguments are required: --brings", err);
		var root = parser.Values.GetValueOrDefault("--root") ?? defaultRoot;
		var (dryRun, isAuto) = (parser.Has("--dry-run"), parser.Has("--auto"));
		List<string> order;
		try
		{
			order = RolloutOrder(listed ?? Repos.Read(folders is null ? null : Path.Combine(folders.Skill, "github")));
		}
		catch (ArgumentException error)
		{
			return parser.Error(error.Message, err);
		}
		exists ??= Directory.Exists;

		void Say(string text)
		{
			output.Write(text + "\n");
			output.Flush();
		}

		string Checkout(string repo) => PyLib.Join(root, AutoRollout.ShortName(repo));

		var missing = order.Where(repo => !exists(Checkout(repo))).ToList();
		if (missing.Count > 0)
		{
			foreach (var repo in missing)
				err.Write($"rollout.sh: no checkout at {Checkout(repo)} for {repo}\n");
			err.Write("rollout.sh: nothing run; clone each one, or retire it from github/repos.txt\n");
			return 1;
		}

		if (isAuto)
		{
			// Under --auto, reapply.sh runs in a process group of its own, which a SIGTERM or SIGHUP kills whole.
			var world = auto ?? World.Real(folders ?? Folders.Default);
			if (reapply is not null)
				world = world with { Reapply = reapply };
			return AutoRound.Run(order, brings, root, Checkout, dryRun, world, Say, err, defaultRoot, advisories, onSignal);
		}
		reapply ??= (b, path) => RunReapply(b, path, folders);
		latestPr ??= GhLatestPr;
		ahead ??= path => AutoRound.GitBranch(AutoRollout.ReviewWorktree(path)).Ahead;

		// reapply.sh on one repo (or what it would run); whether it went cleanly.
		bool Apply(string repo)
		{
			var path = Checkout(repo);
			if (dryRun)
			{
				Say($"would run: reapply.sh --brings {PyLib.ShQuote(brings)} {PyLib.ShQuote(path)}");
				return true;
			}
			Say($"── rollout: {AutoRollout.ShortName(repo)}");
			var code = reapply(brings, path);
			if (code != 0)
			{
				err.Write($"rollout.sh: reapply failed on {AutoRollout.ShortName(repo)}{(code is int known ? ReapplyStops.GetValueOrDefault(known, "") : "")}\n");
				return false;
			}
			return true;
		}

		HashSet<string> unread = [];

		// (number, state) of repo's newest re-Apply PR, or (null, null) when there is none or it can't be read.
		(long? Number, string? State) Lookup(string repo)
		{
			try
			{
				return latestPr(repo) is { } found ? (found.Number, found.State) : (null, null);
			}
			catch (Exception error) // reported; the repo's PR is then unknown
			{
				err.Write($"rollout.sh: can't read {repo}'s re-Apply PR: {error.Message}\n");
				unread.Add(repo);
				return (null, null);
			}
		}

		var first = order[0];
		var reapplied = Apply(first);
		var (pr, state) = Lookup(first);
		var lead = new Lead(reapplied, pr, state);
		if (lead.Reapplied && !dryRun && !unread.Contains(first) && lead.State != "OPEN")
			try
			{
				lead = lead with { Ahead = ahead(Checkout(first)) };
			}
			catch (Exception error) // reported; the round is then held
			{
				err.Write($"rollout.sh: can't read {AutoRollout.ShortName(first)}'s re-Apply branch: {error.Message}\n");
				lead = lead with { Unreadable = true };
			}
		var step = NextStep(order, lead);
		if (step.Run.Count > 0 && step.Reason.Length > 0)
			Say($"── rollout: {step.Reason}");
		if (step.Run.Count == 0)
		{
			Say($"rollout: {step.Reason}; held: {string.Join(", ", step.Held.Select(AutoRollout.ShortName))}");
			if (lead.Reapplied && lead.State == "OPEN")
			{
				Say($"  land it with: land.sh {first}#{lead.Pr}, then run rollout.sh again");
				return 0;
			}
			return 1;
		}

		if (dryRun)
			Say($"then, if that leaves {AutoRollout.ShortName(first)} with no new re-Apply PR (its Apply is already on main):");
		List<string> failed = [], landing = [];
		foreach (var repo in step.Run)
		{
			if (!Apply(repo))
			{
				failed.Add(AutoRollout.ShortName(repo));
				continue;
			}
			if (dryRun)
				continue;
			var (number, prState) = Lookup(repo);
			if (prState == "OPEN")
				landing.Add($"{repo}#{number}");
		}
		if (!dryRun)
		{
			Say($"rollout: reapplied {step.Run.Count - failed.Count} of {step.Run.Count} repo(s) after {AutoRollout.ShortName(first)}"
					+ (failed.Count > 0 ? $"; failed: {string.Join(", ", failed)}" : ""));
			if (landing.Count > 0)
				Say($"  land them with: land.sh {string.Join(" ", landing)}");
		}
		else if (failed.Count > 0)
			Say($"rollout: failed: {string.Join(", ", failed)}");
		return failed.Count > 0 ? 1 : 0;
	}
}
