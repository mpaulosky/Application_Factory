// PROTOTYPE (research/status-cs-trial, mpaulosky/Application_Factory#97): landing.py ported line for line, to judge a C#
// file-based app as the Factory's scripts. Not merged; the Python stays the one status.sh and land.py run.
//
// The landing decision: what a PR needs next to land, from its state alone. Pure: no GitHub calls. landing.py's
// docstrings hold the rules; each member here names the Python it ports.
using System.Globalization;
using System.Text.RegularExpressions;

static class Landing
{
	public const string Wait = "wait", MarkReady = "mark ready", UpdateBranch = "update branch", RequestCopilot = "request Copilot",
			RequestClaude = "add review:claude", ArmAutoMerge = "arm auto-merge", HandOff = "leave to PR Auto-Merge", Blocker = "report blocker",
			Nothing = "nothing";
	public static readonly string[] Actions = [Wait, MarkReady, UpdateBranch, RequestCopilot, RequestClaude, ArmAutoMerge, HandOff, Blocker, Nothing];

	public static readonly Regex Copilot = new("copilot", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	// ASCII whitespace spelled out, as in landing.py; matched at the start (re.match).
	public static readonly Regex CopilotNoReview = new(@"\A[ \t\r\n]*Copilot was unable to review", RegexOptions.CultureInvariant);
	public const string GithubActions = "github-actions", ClaudeMarker = "<!-- claude-review -->", OffDiffMarker = "<!-- claude-review:off-diff -->",
			ReviewClaude = "review:claude", ExhaustedMarker = "<!-- pr-automerge:review-loop-exhausted",
			CopilotAskedMarker = "<!-- pr-automerge:copilot-asked -->";
	public const int CopilotAskSettleSeconds = 30, MaxClaudeRounds = 3, ReviewCap = 3, UnrepliedNamed = 3;
	public const string CopilotAskedComment = "Copilot's review was requested for this PR. It is asked once per PR; if GitHub drops the request, "
			+ "review:claude calls in Claude instead. mpaulosky/dotfiles#360\n\n" + CopilotAskedMarker;
	public static readonly HashSet<string> ClaudeChecks = ["Check for a merge from main", "Review with Claude", "Post Claude's review"];
	public static readonly HashSet<string> ReleaseBlogBranches = ["docs/release-notes", "docs/backfill-blog-posts"];
	public static readonly HashSet<string> Passed = ["SUCCESS", "NEUTRAL", "SKIPPED"];
	public static readonly HashSet<string> Failed = ["FAILURE", "TIMED_OUT", "STARTUP_FAILURE", "ACTION_REQUIRED", "ERROR", "STALE"];
	public const string Cancelled = "CANCELLED", CopilotReviewer = "copilot-pull-request-reviewer";

	public static string NoBot(string s) => s.EndsWith("[bot]", StringComparison.Ordinal) ? s[..^5] : s;

	// datetime.fromisoformat(text.replace("Z", "+00:00")).timestamp(), or null when missing or unreadable.
	public static double? Epoch(string? text)
	{
		if (text is null || !DateTimeOffset.TryParse(text.Replace("Z", "+00:00"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
			return null;
		return (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;
	}

	// The second line of a body is exactly the off-diff marker (body.split("\n")[1:2] == [OFF_DIFF_MARKER]).
	static bool OffDiff(string? body) => (body ?? "").Split('\n') is { Length: > 1 } lines && lines[1] == OffDiffMarker;

	public static bool BotThread(Thread thread)
	{
		var login = NoBot(thread.Opener);
		if (thread.OpenerKind != "Bot")
			return false;
		return login == CopilotReviewer || (login == GithubActions && thread.OpenerReview.StartsWith(ClaudeMarker, StringComparison.Ordinal));
	}

	public static string[] UnrepliedThreads(IEnumerable<Thread> threads) =>
			[.. threads.Where(t => t.Resolved && BotThread(t) && !t.Partial && t.Repliers.All(r => r == t.Opener)).Select(t => t.Name)];

	public static string UnrepliedNotice(PrState pr)
	{
		var names = pr.UnrepliedThreads;
		if (names.Length == 0)
			return "";
		var more = names.Length - UnrepliedNamed;
		return $"notice: resolved without a reply: {string.Join(", ", names.Take(UnrepliedNamed))}" + (more > 0 ? $" and {more} more" : "");
	}

	// review_rounds(): records are (login, oid, body, submittedAt, inline thread count, is a merge), oldest first.
	public static Round[] ReviewRounds(IEnumerable<(string? Login, string? Oid, string? Body, string? At, long Comments, bool Merge)> records,
			IReadOnlySet<string> declined, bool gone)
	{
		var byCommit = new List<Round>(); // insertion-ordered, as the Python dict with pop and re-insert
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

	public static int TrailingDirty(IReadOnlyList<Round> rounds)
	{
		var count = 0;
		for (var i = rounds.Count - 1; i >= 0 && rounds[i].Dirty; i--)
			count++;
		return count;
	}

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

	public static double? RoundEpoch(Round round) => Epoch(round.At);

	// current_loop(): adds are (epoch seconds, writer True/False/None), removals and copilotRequests epoch seconds.
	public static LoopWalk CurrentLoop(Round[] rounds, IEnumerable<(double Added, bool? Writer)> adds, IEnumerable<double>? removals = null,
			IEnumerable<double>? copilotRequests = null)
	{
		var removed = (removals ?? []).ToArray();
		var asked = (copilotRequests ?? []).ToArray();
		var stamps = rounds.Select(RoundEpoch).Where(s => s is not null).Select(s => s!.Value).ToArray();
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
			// (round_epoch(item) or float("-inf")): an epoch of 0.0 is falsy in Python too.
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

	public static bool ThreadOpenedByPerson(Thread thread) => !BotThread(thread);

	public static HashSet<string> CopilotReviews(IEnumerable<(string? Login, string? Oid, string? Body)> reviews) =>
			[.. reviews.Where(r => !string.IsNullOrEmpty(r.Login) && !string.IsNullOrEmpty(r.Oid) && Copilot.IsMatch(r.Login) && !CopilotNoReview.IsMatch(r.Body ?? "")).Select(r => r.Oid!)];

	public static HashSet<string> CopilotDeclined(IEnumerable<(string? Login, string? Oid, string? Body)> reviews) =>
			[.. reviews.Where(r => !string.IsNullOrEmpty(r.Login) && !string.IsNullOrEmpty(r.Oid) && Copilot.IsMatch(r.Login) && CopilotNoReview.IsMatch(r.Body ?? "")).Select(r => r.Oid!)];

	// copilot_ask(): comments are (login, body, createdAt), oldest first, or null when unread (asked, not settled).
	public static (bool Asked, bool Settled) CopilotAsk(IEnumerable<(string Login, string Body, string? CreatedAt)>? comments, string owner, double now)
	{
		if (comments is null)
			return (true, false);
		foreach (var (login, body, createdAt) in comments)
		{
			var who = NoBot(login);
			if (!body.Contains(CopilotAskedMarker, StringComparison.Ordinal) || who == "" || (who != GithubActions && who != owner))
				continue;
			if (Epoch(createdAt) is not double written)
				return (true, false);
			return (true, now - written >= CopilotAskSettleSeconds);
		}
		return (false, false);
	}

	public static bool IsClaudeReview(string? login, string? oid, string? body) =>
			!string.IsNullOrEmpty(login) && !string.IsNullOrEmpty(oid) && NoBot(login) == GithubActions && (body ?? "").StartsWith(ClaudeMarker, StringComparison.Ordinal);

	public static HashSet<string> ClaudeReviews(IEnumerable<(string? Login, string? Oid, string? Body)> reviews) =>
			[.. reviews.Where(r => IsClaudeReview(r.Login, r.Oid, r.Body)).Select(r => r.Oid!)];

	public static HashSet<string> ClaudeOffDiff(IEnumerable<(string? Login, string? Oid, string? Body)> reviews)
	{
		var latest = new Dictionary<string, string?>();
		foreach (var (login, oid, body) in reviews)
			if (IsClaudeReview(login, oid, body))
				latest[oid!] = body;
		return [.. latest.Where(kv => OffDiff(kv.Value)).Select(kv => kv.Key)];
	}

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

	public static string Short(string oid) => oid.Length > 7 ? oid[..7] : oid;

	static Decision ReviewNeeded(PrState pr, Checks checks)
	{
		var waiting = $"no Copilot or Claude review of {Short(pr.Head)} yet";
		var declined = pr.CopilotDeclined.Contains(pr.Head);
		if (pr.CopilotRequested && !declined)
			return new(Wait, waiting + "; Copilot is requested");
		if (pr.ClaudeRequested && !pr.ClaudeReviewed.Contains(pr.Head) && (declined || pr.CopilotGone))
			return new(Wait, waiting + $"; {ReviewClaude} is on");
		if (!(declined || pr.CopilotGone))
			return pr.CopilotAsked
					? new(Wait, waiting + "; Copilot was asked, waiting for the request to show")
					: new(RequestCopilot, waiting + "; Copilot not asked yet");
		if (pr.ExhaustionMarkerNewest)
			return new(Wait, waiting + $"; the review loop was exhausted and documented, add {ReviewClaude} to start a new loop");
		if (pr.LoopUnread)
			return new(Wait, waiting + "; the review loop's comments or label events couldn't be read, so Claude isn't called in");
		var running = checks.Running.Where(n => !ClaudeChecks.Contains(n)).ToArray();
		if (running.Length > 0)
			return new(Wait, waiting + "; waiting for checks before calling in Claude: " + string.Join(", ", running));
		if (!checks.Passed.Concat(checks.Running).Concat(checks.Failed).Concat(checks.Cancelled).Any(n => !ClaudeChecks.Contains(n)))
			return new(Wait, waiting + "; no checks on the head yet, so Claude waits for CI");
		return new(RequestClaude, waiting + "; Copilot couldn't review (its request was dropped or its quota is used up), checks green");
	}
}

record Check(string Name, string Status, string? Conclusion = null, string StartedAt = "", long Rank = 0);

record Checks(string[] Passed, string[] Running, string[] Failed, string[] Cancelled, string[] Superseded)
{
	public bool Green => Running.Length == 0 && Failed.Length == 0 && Cancelled.Length == 0;
}

record Thread(bool Resolved, string Opener, string OpenerKind, string OpenerReview, string[] Repliers, bool Partial, string Name);

record Round(string Oid, bool Dirty, string At);

record LoopWalk(Round[] Rounds, double? Hit = null, bool Unread = false, bool Restarted = false);

record Decision(string Action, string Reason);

record PrState
{
	public string State { get; init; } = "OPEN";
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
	public string MergeState { get; init; } = "UNKNOWN";
	public Check[] Checks { get; init; } = [];
	public bool ChecksRequired { get; init; } = true;
	public bool AutoMergeArmed { get; init; }
	public bool WantReady { get; init; }
	public bool MergedByWorkflow { get; init; }
	public bool ReleaseBlog { get; init; }

	public string HeadReviewer => Head == "" ? "" : CopilotReviewed.Contains(Head) ? "copilot" : ClaudeBacked.Contains(Head) ? "claude" : "";

	public IReadOnlySet<string> ClaudeBacked => CopilotGone || (Head != "" && CopilotDeclined.Contains(Head))
			? ClaudeReviewed
			: ClaudeReviewed.Where(CopilotDeclined.Contains).ToHashSet();

	public bool CopilotGone => CopilotAsked && CopilotAskSettled && !CopilotRequested;

	public bool ReviewerOnHead => HeadReviewer != "";

	public bool LoopExhausted => MergedByWorkflow && !LoopUnread && Landing.TrailingDirty(Rounds) >= Landing.MaxClaudeRounds;
}
