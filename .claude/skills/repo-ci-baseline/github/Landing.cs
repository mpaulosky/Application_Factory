// The landing decision: what a PR needs next to land, from its state alone.
//
// Pure: no GitHub calls. status.cs and land.cs fetch a PR's state (StatusCore.cs), build a PrState and call Decide(). The rules are the
// ones the skill lands PRs by (references/automerge.md):
//
// - a merged or closed PR, or one with auto-merge already armed, needs nothing;
// - a failed check (or a cancelled one nothing newer replaced, other than Claude Review's own) is a blocker to report, never something
//   to arm past; so is a merge conflict;
// - a draft is marked ready only when the caller says it should be;
// - resolved Copilot and Claude Review threads nobody replied to are listed in PrState.UnrepliedThreads: a notice beside the decision,
//   never a hold;
// - a branch BEHIND main is updated (nobody is asked to review: Copilot is asked once per PR);
// - a head no reviewer has reviewed waits for Copilot while it's requested, and for Claude while the PR carries review:claude. With
//   neither on its way and Copilot never asked for the PR, Copilot is asked, once: a comment records the ask (CopilotAskedMarker) and
//   the request follows. Claude backs Copilot up only where Copilot can't review that head (CopilotGone): it answered with its quota
//   note (CopilotDeclined), or it was asked, the ask settled (30 seconds) and no request is pending. A request read back as dropped
//   waits out the 30 seconds too: PR Auto-Merge takes review:claude off an ask younger than that, and land.cs would not add it again
//   (mpaulosky/dotfiles#378). A Claude review of any other head covers nothing. Copilot isn't asked again for a later head: a settled
//   record is enough to take it to Claude (mpaulosky/dotfiles#360). Only then is review:claude added, and only once the head has at
//   least one check other than Claude Review's own and every such check is green: a Claude review costs money, and one of a head that
//   fails CI is wasted. Never while PR Auto-Merge's exhaustion comment is the newest word on the PR (ExhaustionMarkerNewest): the
//   owner adds the label again to start a new loop;
// - the review loop (mpaulosky/dotfiles#375) is exhausted after MaxClaudeRounds dirty rounds in a row, in a repo whose workflow merges
//   (re-Apply PRs included, since #174). A round is a review of a non-merge commit (a merge of main is never one), dirty when it raised
//   a thread or an off-diff finding. Neither reviewer is asked for then, and the workflow documents the loop in one comment and lets the
//   PR merge: it no longer waits for a review of the head, nor holds on the threads Copilot and Claude opened and the off-diff findings
//   still open, so the PR goes on to its checks and is left to the workflow. A thread a person opened still holds the merge, and the
//   workflow's comment names it (OpenPersonThreads). The loop restarts at the first review:claude add by a login with write access
//   after the round that exhausted it (CurrentLoop()), and the label is ignored while Copilot can review: Claude reviews only where
//   Copilot can't (mpaulosky/dotfiles#378);
// - auto-merge is armed only once a reviewer has reviewed the current head and no review thread is unresolved. The reviewer is Copilot
//   (an author matching /copilot/i), or Claude as its backup on a head Copilot can't review (its quota note, or a request that didn't
//   register): Claude Review posts as github-actions[bot] with ClaudeMarker first in the body. When Claude's latest review of the head
//   has findings outside the diff (OffDiffMarker on its second line), they open no thread, so the PR waits for a new head;
// - where the repo requires no checks (dotfiles), auto-merge wouldn't wait for CI, so the checks must finish green first;
// - in a Baseline repo, its own PR Auto-Merge workflow merges a reviewed PR through the API once its checks pass (and at an exhausted
//   review loop), and it never arms native auto-merge: so the PR is left to it, never armed around it;
// - a release-blog PR (release.yml's or backfill-blog-posts.yml's branch) is armed as soon as it is open and not blocked, without
//   Copilot's review, as those workflows arm it themselves; one they armed needs nothing.
//
// A cancelled check run is superseded when a newer run of the same check name on the same head exists: CI's concurrency cancels runs,
// and the newest counts.
using System.Globalization;
using System.Text.RegularExpressions;

static class Landing
{
	public const string Wait = "wait", MarkReady = "mark ready", UpdateBranch = "update branch", RequestCopilot = "request Copilot",
			RequestClaude = "add review:claude", ArmAutoMerge = "arm auto-merge", HandOff = "leave to PR Auto-Merge", Blocker = "report blocker",
			Nothing = "nothing";
	public static readonly string[] Actions = [Wait, MarkReady, UpdateBranch, RequestCopilot, RequestClaude, ArmAutoMerge, HandOff, Blocker, Nothing];

	public static readonly Regex Copilot = new("copilot", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	// Copilot's review of a head once the account's Copilot code review quota is used up: it reviewed nothing, so it isn't a review. PR
	// Auto-Merge's COPILOT_NO_REVIEW and claude-review.yml skip it the same way. ASCII whitespace spelled out, not \s: gh's gojq (RE2)
	// reads \s as ASCII only, JS and .NET as Unicode. Matched at the start of the body.
	public static readonly Regex CopilotNoReview = new(@"\A[ \t\r\n]*Copilot was unable to review", RegexOptions.CultureInvariant);
	// Claude Review's login (GraphQL drops the "[bot]" suffix, REST keeps it) and the marker its review bodies start with;
	// post_claude_review.py writes both.
	public const string GithubActions = "github-actions", ClaudeMarker = "<!-- claude-review -->", OffDiffMarker = "<!-- claude-review:off-diff -->",
			ReviewClaude = "review:claude";
	// The hidden marker PR Auto-Merge's comment on an exhausted review loop ends with, followed by the head and " -->"
	// (mpaulosky/dotfiles#375). The Template's pr-automerge.yml has the same prefix (a test holds them equal).
	public const string ExhaustedMarker = "<!-- pr-automerge:review-loop-exhausted";
	// The record that PR Auto-Merge or land.cs asked Copilot to review the PR (mpaulosky/dotfiles#360): a comment containing the marker.
	// The Template's pr-automerge.yml has the same marker and settle time.
	public const string CopilotAskedMarker = "<!-- pr-automerge:copilot-asked -->";
	public const int CopilotAskSettleSeconds = 30;
	public const string CopilotAskedComment = "Copilot's review was requested for this PR. It is asked once per PR; if GitHub drops the request, "
			+ "review:claude calls in Claude instead. mpaulosky/dotfiles#360\n\n" + CopilotAskedMarker;
	// Claude Review's own checks (claude-review.yml's job names). Removing the label cancels a run in progress and its next add starts a
	// fresh one, so a cancelled run of these needs no rerun, and they don't gate calling it in.
	public static readonly IReadOnlySet<string> ClaudeChecks = new HashSet<string> { "Check for a merge from main", "Review with Claude", "Post Claude's review" };
	// The owner's cost rule: the dirty rounds in a row, Copilot's and Claude's together, after which the PR's review loop is exhausted
	// (mpaulosky/dotfiles#375). 3, so a fix for a second round's findings is reviewed too (#193): Claude reviews every push while
	// review:claude is on, until a review comes back clean. PR Auto-Merge calls in Claude too, when Copilot's quota note is the head's
	// only review (#190) or Copilot was asked and its request didn't register within 30 seconds (mpaulosky/dotfiles#360), and takes
	// review:claude off at an exhausted loop: its MAX_CLAUDE_ROUNDS and CLAUDE_CHECKS must match these.
	public const int MaxClaudeRounds = 3;
	// The Template's pr-automerge.yml keeps this under its old name, COPILOT_REVIEW_CAP, because github-settings.sh reads it, and must
	// match it. It equals MaxClaudeRounds and bounds nothing here: LoopExhausted uses that.
	public const int ReviewCap = 3;
	// The head branches release.yml and backfill-blog-posts.yml open their blog PRs from.
	public static readonly IReadOnlySet<string> ReleaseBlogBranches = new HashSet<string> { "docs/release-notes", "docs/backfill-blog-posts" };
	public static readonly IReadOnlySet<string> Passed = new HashSet<string> { "SUCCESS", "NEUTRAL", "SKIPPED" };
	public static readonly IReadOnlySet<string> Failed = new HashSet<string> { "FAILURE", "TIMED_OUT", "STARTUP_FAILURE", "ACTION_REQUIRED", "ERROR", "STALE" };
	public const string Cancelled = "CANCELLED";
	public const string CopilotReviewer = "copilot-pull-request-reviewer";
	public const int UnrepliedNamed = 3;

	public static string NoBot(string s) => s.EndsWith("[bot]", StringComparison.Ordinal) ? s[..^5] : s;

	// An ISO-8601 time as epoch seconds, or null when it is missing or unreadable. A time with no offset is UTC.
	public static double? Epoch(string? text)
	{
		if (text is null || !DateTimeOffset.TryParse(text.Replace("Z", "+00:00", StringComparison.Ordinal), CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal, out var at))
			return null;
		return (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;
	}

	// Whether the second line of a body is exactly the off-diff marker.
	static bool OffDiff(string? body) => (body ?? "").Split('\n') is { Length: > 1 } lines && lines[1] == OffDiffMarker;

	// A Copilot or Claude Review thread, by the rule claude-review.yml reviews threads by: the opening comment's author is the Bot
	// copilot-pull-request-reviewer, or github-actions with the Claude marker first in the opening review's body.
	public static bool BotThread(ReviewThread thread)
	{
		var login = NoBot(thread.Opener);
		if (thread.OpenerKind != "Bot")
			return false;
		return login == CopilotReviewer || (login == GithubActions && thread.OpenerReview.StartsWith(ClaudeMarker, StringComparison.Ordinal));
	}

	// The names of the resolved bot threads nobody but their opener commented on. A reply's text isn't read: Sandcastle's and a session's
	// replies come from the owner's login and count as replies. A thread with more comments than were read counts as replied.
	public static string[] UnrepliedThreads(IEnumerable<ReviewThread> threads) =>
			[.. threads.Where(t => t.Resolved && BotThread(t) && !t.Partial && t.Repliers.All(r => r == t.Opener)).Select(t => t.Name)];

	// The line's notice for the resolved bot threads nobody replied to; "" when there are none.
	public static string UnrepliedNotice(PrState pr)
	{
		var names = pr.UnrepliedThreads;
		if (names.Length == 0)
			return "";
		var more = names.Length - UnrepliedNamed;
		return $"notice: resolved without a reply: {string.Join(", ", names.Take(UnrepliedNamed))}" + (more > 0 ? $" and {more} more" : "");
	}

	// The PR's review rounds, oldest first. records are (author login, commit oid, body, submittedAt, inline thread count, is a merge), in
	// the order GitHub lists reviews (oldest first). A review counts when it is Copilot's own (not its quota note), or Claude Review's as
	// Copilot's backup: Claude's review counts when Copilot answered that commit with its quota note (declined holds its oid) or Copilot
	// can't review the PR now (gone: its quota note is on the head, or its request didn't register). A round is one non-merge commit; a
	// review of a merge commit, a merge of main included, is never a round. A round is dirty when any of its counted reviews raised an
	// inline thread or, for Claude, findings outside the diff, and sits where its latest counted review does. PR Auto-Merge's
	// reviewRounds() does the same.
	public static Round[] ReviewRounds(IEnumerable<(string? Login, string? Oid, string? Body, string? At, long Comments, bool Merge)> records,
			IReadOnlySet<string> declined, bool gone)
	{
		var byCommit = new List<Round>(); // in the order each commit's latest counted review came
		foreach (var (login, oid, body, at, comments, merge) in records)
		{
			if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(oid) || merge)
				continue;
			bool offDiff;
			if (Copilot.IsMatch(login))
			{
				if (CopilotNoReview.IsMatch(body ?? ""))
					continue;
				offDiff = false;
			}
			else if (IsClaudeReview(login, oid, body))
			{
				if (!(gone || declined.Contains(oid)))
					continue;
				offDiff = OffDiff(body);
			}
			else
				continue;
			var index = byCommit.FindIndex(r => r.Oid == oid);
			var earlier = index >= 0 ? byCommit[index] : null;
			if (index >= 0)
				byCommit.RemoveAt(index);
			byCommit.Add(new Round(oid, comments != 0 || offDiff || (earlier?.Dirty ?? false), at ?? ""));
		}
		return [.. byCommit];
	}

	// How many of the newest rounds in a row were dirty.
	public static int TrailingDirty(IReadOnlyList<Round> rounds)
	{
		var count = 0;
		for (var i = rounds.Count - 1; i >= 0 && rounds[i].Dirty; i--)
			count++;
		return count;
	}

	// The longest run of dirty rounds in a row anywhere in the rounds (a loop that reached the cap may have been restarted since).
	public static int LongestDirty(IEnumerable<Round> rounds)
	{
		int longest = 0, streak = 0;
		foreach (var round in rounds)
		{
			streak = round.Dirty ? streak + 1 : 0;
			longest = Math.Max(longest, streak);
		}
		return longest;
	}

	// The round's submission time in epoch seconds, or null when it is unknown.
	public static double? RoundEpoch(Round round) => Epoch(round.At);

	// Walks the loop's generations: the rounds that count after the restarts (mpaulosky/dotfiles#378).
	//
	// rounds are the PR's Rounds, oldest first; adds are (epoch seconds, writer) for each review:claude `labeled` event, oldest first,
	// writer true (the actor has write access to the repo), false, or null (the permission couldn't be read). A loop that reached
	// MaxClaudeRounds dirty rounds in a row is restarted by the first add after the round that got it there by a login with write access:
	// only the rounds after the add count. An add by anyone else is ignored, and one whose permission can't be read leaves the loop unread
	// (fail closed: no restart, and the PR isn't called exhausted).
	//
	// removals are the epoch seconds of PR Auto-Merge's (github-actions) removals of review:claude. An add made while Copilot could
	// review is the one PR Auto-Merge removes at once, so an add is ignored when a removal follows it before the next round submitted
	// after it, unless a request for Copilot's review (copilotRequests, epoch seconds) came in between: the removal was then made for that
	// request, not for the add. Whether Copilot can review now doesn't decide it, so a recognised restart can't flip back when Copilot
	// reviews a later head (mpaulosky/dotfiles#381); callers pass every add. PR Auto-Merge's reviewLoopState() does the same.
	public static LoopWalk CurrentLoop(Round[] rounds, IEnumerable<(double Added, bool? Writer)> adds, IEnumerable<double>? removals = null,
			IEnumerable<double>? copilotRequests = null)
	{
		var removed = (removals ?? []).ToArray();
		var asked = (copilotRequests ?? []).ToArray();
		var stamps = rounds.Select(RoundEpoch).OfType<double>().ToArray();
		bool Removed(double added)
		{
			var after = stamps.Where(s => s > added).ToArray();
			var until = after.Length > 0 ? after.Min() : double.PositiveInfinity;
			return removed.Any(r => added <= r && r < until && !asked.Any(a => added < a && a <= r));
		}
		var kept = adds.Where(a => !Removed(a.Added)).ToArray();
		var start = double.NegativeInfinity;
		var restarted = false;
		while (true)
		{
			// A round with no time, or at the epoch itself, sorts before every restart, as the Python's (round_epoch(item) or -inf) did.
			var generation = rounds.Where(r => double.IsNegativeInfinity(start) || (RoundEpoch(r) is double e && e != 0 ? e : double.NegativeInfinity) > start).ToArray();
			double? hit = null;
			var streak = 0;
			foreach (var item in generation)
			{
				streak = item.Dirty ? streak + 1 : 0;
				if (streak == MaxClaudeRounds)
				{
					hit = RoundEpoch(item);
					break;
				}
			}
			if (streak < MaxClaudeRounds || hit is null)
				return new LoopWalk(generation, null, false, restarted);
			double? restart = null;
			foreach (var (added, writer) in kept)
			{
				if (added <= hit)
					continue;
				if (writer is null)
					return new LoopWalk(generation, hit, true, restarted);
				if (writer.Value)
				{
					restart = added;
					break;
				}
			}
			if (restart is null)
				return new LoopWalk(generation, hit, false, restarted);
			start = restart.Value;
			restarted = true;
		}
	}

	// Whether a thread holds the merge at an exhausted loop (mpaulosky/dotfiles#378): every thread but Copilot's and Claude Review's
	// does, one whose author can't be read included.
	public static bool ThreadOpenedByPerson(ReviewThread thread) => !BotThread(thread);

	// The commit oids reviewed by Copilot, from (author login, commit oid, body) triples.
	public static HashSet<string> CopilotReviews(IEnumerable<(string? Login, string? Oid, string? Body)> reviews) =>
			[.. reviews.Where(r => !string.IsNullOrEmpty(r.Login) && !string.IsNullOrEmpty(r.Oid) && Copilot.IsMatch(r.Login) && !CopilotNoReview.IsMatch(r.Body ?? "")).Select(r => r.Oid!)];

	// The commit oids Copilot answered with its quota note. Copilot was asked about these heads and can't review them, so asking again
	// only brings another note: Decide() calls in Claude instead.
	public static HashSet<string> CopilotDeclined(IEnumerable<(string? Login, string? Oid, string? Body)> reviews) =>
			[.. reviews.Where(r => !string.IsNullOrEmpty(r.Login) && !string.IsNullOrEmpty(r.Oid) && Copilot.IsMatch(r.Login) && CopilotNoReview.IsMatch(r.Body ?? "")).Select(r => r.Oid!)];

	// (asked, settled): whether the PR carries the record that Copilot was asked, and whether it has settled.
	//
	// comments are (author login, body, createdAt ISO-8601) triples, oldest first, or null when they couldn't be read, which counts as
	// asked but not settled: nothing asks, and nothing is called dropped, on a record nobody can see. Only github-actions (PR Auto-Merge)
	// or the repository owner (land.cs) can write the record: anyone can comment the marker. The first record decides; it settles
	// CopilotAskSettleSeconds after it was written, and one with an unreadable time never settles. now is epoch seconds.
	public static (bool Asked, bool Settled) CopilotAsk(IEnumerable<(string? Login, string? Body, string? CreatedAt)>? comments, string owner, double now)
	{
		if (comments is null)
			return (true, false);
		foreach (var (login, body, createdAt) in comments)
		{
			var who = NoBot(login ?? "");
			if (!(body ?? "").Contains(CopilotAskedMarker, StringComparison.Ordinal) || who == "" || (who != GithubActions && who != owner))
				continue;
			if (Epoch(createdAt) is not double written)
				return (true, false);
			return (true, now - written >= CopilotAskSettleSeconds);
		}
		return (false, false);
	}

	public static bool IsClaudeReview(string? login, string? oid, string? body) =>
			!string.IsNullOrEmpty(login) && !string.IsNullOrEmpty(oid) && NoBot(login) == GithubActions && (body ?? "").StartsWith(ClaudeMarker, StringComparison.Ordinal);

	// The commit oids reviewed by Claude Review, from (author login, commit oid, body) triples.
	public static HashSet<string> ClaudeReviews(IEnumerable<(string? Login, string? Oid, string? Body)> reviews) =>
			[.. reviews.Where(r => IsClaudeReview(r.Login, r.Oid, r.Body)).Select(r => r.Oid!)];

	// The commit oids whose latest Claude review has findings outside the diff. reviews are oldest first, so a later review of the same
	// commit replaces an earlier one.
	public static HashSet<string> ClaudeOffDiff(IEnumerable<(string? Login, string? Oid, string? Body)> reviews)
	{
		var latest = new Dictionary<string, string?>();
		foreach (var (login, oid, body) in reviews)
			if (IsClaudeReview(login, oid, body))
				latest[oid!] = body;
		return [.. latest.Where(kv => OffDiff(kv.Value)).Select(kv => kv.Key)];
	}

	// Sorts the head's checks into passed, running, failed and cancelled. Runs are grouped by name and the newest (the last created, by
	// rank; else the latest StartedAt, a run not yet started counting as newest) is the check's result. Older runs are ignored, and an
	// older cancelled run is listed as superseded.
	public static Checks SummarizeChecks(IEnumerable<Check> checks)
	{
		List<string> passed = [], running = [], failed = [], cancelled = [], superseded = [];
		foreach (var group in checks.GroupBy(c => c.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
		{
			var runs = group.OrderBy(r => r.Rank).ThenBy(r => r.StartedAt == "").ThenBy(r => r.StartedAt, StringComparer.Ordinal).ToList();
			var newest = runs[^1];
			if (runs.Take(runs.Count - 1).Any(r => r.Conclusion == Cancelled))
				superseded.Add(group.Key);
			if (newest.Status != "COMPLETED")
				running.Add(group.Key);
			else if (newest.Conclusion == Cancelled)
				cancelled.Add(group.Key);
			else if (newest.Conclusion is not null && Passed.Contains(newest.Conclusion))
				passed.Add(group.Key);
			else
				failed.Add(group.Key);
		}
		return new Checks([.. passed], [.. running], [.. failed], [.. cancelled], [.. superseded]);
	}

	// The one next action for pr.
	public static Decision Decide(PrState pr)
	{
		if (pr.State != "OPEN")
			return new(Nothing, pr.State.ToLowerInvariant());
		if (pr.AutoMergeArmed)
			return new(Nothing, "auto-merge armed");
		var checks = SummarizeChecks(pr.Checks);
		if (checks.Failed.Length > 0)
			return new(Blocker, "failed: " + string.Join(", ", checks.Failed));
		var cancelled = checks.Cancelled.Where(n => !ClaudeChecks.Contains(n)).ToArray();
		if (cancelled.Length > 0)
			return new(Blocker, "cancelled, needs a rerun: " + string.Join(", ", cancelled));
		if (pr.MergeState == "DIRTY")
			return new(Blocker, "merge conflict with main");
		if (pr.Draft)
			return pr.WantReady ? new(MarkReady, "draft") : new(Wait, "draft");
		if (pr.ReleaseBlog)
			return new(ArmAutoMerge, "release-blog PR, not armed by its workflow");
		if (pr.MergeState == "BEHIND")
			return new(UpdateBranch, "behind main");
		var exhausted = pr.LoopExhausted;
		if (!pr.ReviewerOnHead && !exhausted)
			return ReviewNeeded(pr, checks);
		// An exhausted loop is documented in a comment and merges with the threads Copilot and Claude opened and the off-diff findings
		// open: neither holds it (mpaulosky/dotfiles#375). A thread a person opened still does (#378).
		if (exhausted && pr.OpenPersonThreads != 0)
			return new(Wait, $"review loop exhausted, but {pr.OpenPersonThreads} thread(s) opened by a person hold the merge");
		if (!exhausted)
		{
			if (pr.OpenThreads != 0)
				return new(Wait, $"{pr.OpenThreads} open thread(s)");
			if (pr.ClaudeOffDiff.Contains(pr.Head))
				return new(Wait, $"Claude's review of {Short(pr.Head)} has findings outside the diff");
		}
		if (checks.Running.Length > 0 && !pr.ChecksRequired)
			return new(Wait, "no required checks; waiting for: " + string.Join(", ", checks.Running));
		var reviewed = exhausted
				? $"review loop exhausted ({MaxClaudeRounds} rounds with findings in a row), ready to merge, {pr.OpenThreads - pr.OpenPersonThreads} thread(s) left open"
				: $"{char.ToUpperInvariant(pr.HeadReviewer[0])}{pr.HeadReviewer[1..]} reviewed the head, no open threads";
		var reason = reviewed + (checks.Green ? "" : ", required checks running");
		return pr.MergedByWorkflow ? new(HandOff, reason) : new(ArmAutoMerge, reason);
	}

	// The first 7 characters of an oid (all of a shorter one).
	public static string Short(string oid) => oid.Length > 7 ? oid[..7] : oid;

	// The decision for an open, current PR whose head no reviewer has reviewed.
	static Decision ReviewNeeded(PrState pr, Checks checks)
	{
		var waiting = $"no Copilot or Claude review of {Short(pr.Head)} yet";
		// Copilot answered this head with its quota note, so its request (if it still shows) brings no review: go on to Claude.
		var declined = pr.CopilotDeclined.Contains(pr.Head);
		if (pr.CopilotRequested && !declined)
			return new(Wait, waiting + "; Copilot is requested");
		// The label means Claude is on its way only until it has reviewed the head: a review of a head Copilot neither declined nor is gone
		// from covers nothing, and none further is coming. Claude reviews only where Copilot can't: with Copilot available the label is
		// ignored (mpaulosky/dotfiles#378).
		if (pr.ClaudeRequested && !pr.ClaudeReviewed.Contains(pr.Head) && (declined || pr.CopilotGone))
			return new(Wait, waiting + $"; {ReviewClaude} is on");
		if (!(declined || pr.CopilotGone))
			return pr.CopilotAsked
					? new(Wait, waiting + "; Copilot was asked, waiting for the request to show")
					: new(RequestCopilot, waiting + "; Copilot not asked yet");
		// PR Auto-Merge documented an exhausted loop and nobody has added the label since: automation never adds it then
		// (mpaulosky/dotfiles#375).
		if (pr.ExhaustionMarkerNewest)
			return new(Wait, waiting + $"; the review loop was exhausted and documented, add {ReviewClaude} to start a new loop");
		// A loop that could be exhausted and can't be read might be: a paid review of it could be wasted.
		if (pr.LoopUnread)
			return new(Wait, waiting + "; the review loop's comments or label events couldn't be read, so Claude isn't called in");
		var running = checks.Running.Where(n => !ClaudeChecks.Contains(n)).ToArray();
		if (running.Length > 0)
			return new(Wait, waiting + "; waiting for checks before calling in Claude: " + string.Join(", ", running));
		// No checks yet (CI hasn't created them) isn't green; every listed repo has CI, dotfiles included.
		if (!checks.Passed.Concat(checks.Running).Concat(checks.Failed).Concat(checks.Cancelled).Any(n => !ClaudeChecks.Contains(n)))
			return new(Wait, waiting + "; no checks on the head yet, so Claude waits for CI");
		return new(RequestClaude, waiting + "; Copilot couldn't review (its request was dropped or its quota is used up), checks green");
	}
}

// One check run (or commit status) on the PR's head. Status is GitHub's CheckStatusState (QUEUED, IN_PROGRESS, COMPLETED, ...);
// Conclusion is set once COMPLETED. Rank is a check run's databaseId, which orders runs of the same name by creation, as GitHub (and PR
// Auto-Merge) does; a commit status has none, so its StartedAt (an ISO-8601 timestamp) orders it instead.
sealed record Check(string Name, string Status, string? Conclusion = null, string StartedAt = "", long Rank = 0);

// The head's checks after superseded runs are dropped, by name: Cancelled is the newest run of its name, so not superseded; Superseded is
// cancelled, with a newer run of the same name.
sealed record Checks(string[] Passed, string[] Running, string[] Failed, string[] Cancelled, string[] Superseded)
{
	public bool Green => Running.Length == 0 && Failed.Length == 0 && Cancelled.Length == 0;
}

// One review thread, as much of it as the unreplied-thread notice reads: Opener, OpenerKind ("Bot", "User" or "") and OpenerReview (the
// review body) are the thread's opening comment's author and review; Repliers are the logins of the later comments; Partial: more
// comments than were read; Name: the printable-safe path:line.
sealed record ReviewThread(bool Resolved, string Opener, string OpenerKind, string OpenerReview, string[] Repliers, bool Partial, string Name);

// One review round: a counted review of a non-merge commit (see ReviewRounds()). At is the submission time of the commit's latest counted
// review, as GitHub reports it (an ISO-8601 timestamp).
sealed record Round(string Oid, bool Dirty, string At);

// Where a PR's review loop stands after the walk over its restarts (see CurrentLoop()): Rounds, the final generation's, oldest first;
// Hit, the submission time (epoch seconds) of the round that brought that generation to MaxClaudeRounds dirty rounds in a row, null when
// it never got there; Unread, the permission of a candidate add couldn't be read; Restarted, at least one add started a new loop.
sealed record LoopWalk(Round[] Rounds, double? Hit = null, bool Unread = false, bool Restarted = false);

sealed record Decision(string Action, string Reason);

// What Decide() reads about one PR.
//
// CopilotReviewed: the commit oids Copilot reviewed (see CopilotReviews()). ClaudeReviewed: the commit oids Claude Review reviewed.
// ClaudeOffDiff: the commit oids whose latest Claude review has findings outside the diff. ChecksRequired: whether the repo requires
// checks before a merge; without them native auto-merge merges at once, so green checks must come first. CopilotRequested: Copilot is
// among the PR's pending review requests. ClaudeRequested: the PR carries review:claude. CopilotAsked: the PR carries the record that
// Copilot was asked (see CopilotAsk()), or the record couldn't be read, which counts as asked so nothing asks twice. CopilotAskSettled:
// the record is at least CopilotAskSettleSeconds old, long enough for GitHub to have registered the request. UnrepliedThreads: names of
// the resolved bot threads nobody replied to; a notice, not a hold: Decide() ignores it. CopilotDeclined: the commit oids Copilot
// answered with its quota note; on the head it calls in Claude. Claude's review of a commit counts when that commit is in
// CopilotDeclined, or Copilot is gone (see ClaudeBacked). Rounds: the PR's review rounds since its loop began, oldest first; StatusCore
// cuts them at the owner's re-add of review:claude after an exhaustion comment. ExhaustionMarkerNewest: PR Auto-Merge's exhaustion
// comment is on the PR and review:claude hasn't been added since: no review is asked for. LoopUnread: the comments or label events that
// tell the loop's state couldn't be read, so it is never called exhausted. WantReady: the caller's say that a draft should be marked
// ready. MergedByWorkflow: the repo's own PR Auto-Merge workflow merges it (a Baseline repo), so it is left to that workflow rather than
// armed. ReleaseBlog: a release-blog PR (see ReleaseBlogBranches).
sealed record PrState
{
	public string State { get; init; } = "OPEN"; // OPEN, MERGED or CLOSED
	public bool Draft { get; init; }
	public string Head { get; init; } = "";
	public IReadOnlySet<string> CopilotReviewed { get; init; } = new HashSet<string>();
	public IReadOnlySet<string> ClaudeReviewed { get; init; } = new HashSet<string>();
	public IReadOnlySet<string> ClaudeOffDiff { get; init; } = new HashSet<string>();
	public bool CopilotRequested { get; init; }
	public bool ClaudeRequested { get; init; }
	public bool CopilotAsked { get; init; }
	public bool CopilotAskSettled { get; init; }
	public IReadOnlySet<string> CopilotDeclined { get; init; } = new HashSet<string>();
	public Round[] Rounds { get; init; } = [];
	public bool ExhaustionMarkerNewest { get; init; }
	public bool LoopUnread { get; init; }
	public int OpenThreads { get; init; }
	public int OpenPersonThreads { get; init; }
	public string[] UnrepliedThreads { get; init; } = [];
	public string MergeState { get; init; } = "UNKNOWN"; // GitHub's mergeStateStatus
	public Check[] Checks { get; init; } = [];
	public bool ChecksRequired { get; init; } = true;
	public bool AutoMergeArmed { get; init; }
	public bool WantReady { get; init; }
	public bool MergedByWorkflow { get; init; }
	public bool ReleaseBlog { get; init; }

	// "copilot" or "claude", whichever reviewed the head (Copilot first), else "".
	public string HeadReviewer => Head == "" ? "" : CopilotReviewed.Contains(Head) ? "copilot" : ClaudeBacked.Contains(Head) ? "claude" : "";

	// The commit oids whose Claude review counts: Copilot couldn't review that commit. It posted its quota note on it, or Copilot can't
	// review the PR now: its quota note is on the head, or it is gone from it (see CopilotGone). Then Claude's review of any commit
	// counts: the head moves on (a merge of main, a fix) and its earlier reviews stay rounds.
	public IReadOnlySet<string> ClaudeBacked => CopilotGone || (Head != "" && CopilotDeclined.Contains(Head))
			? ClaudeReviewed
			: ClaudeReviewed.Where(CopilotDeclined.Contains).ToHashSet();

	// Copilot won't review the head: its request didn't register. Copilot was asked for the PR, the ask settled and no request is
	// pending (a pending one is waited for, as a request GitHub keeps is not a drop). A drop read back at once is not enough: the
	// workflows count Copilot as gone only after the settle, so the label would be taken off again.
	public bool CopilotGone => CopilotAsked && CopilotAskSettled && !CopilotRequested;

	public bool ReviewerOnHead => HeadReviewer != "";

	// The review loop is exhausted: MaxClaudeRounds dirty rounds in a row. PR Auto-Merge then documents the loop in a comment and merges
	// the PR without a review of the head and with threads open. Only a repo whose workflow merges has one, and a loop whose comments or
	// label events couldn't be read is never called exhausted.
	public bool LoopExhausted => MergedByWorkflow && !LoopUnread && Landing.TrailingDirty(Rounds) >= Landing.MaxClaudeRounds;
}
