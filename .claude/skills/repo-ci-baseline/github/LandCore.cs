// land.cs's logic: watch a round's PRs and act on the landing decision until they land.
//
//     land.sh [options]                     every open PR in github/repos.txt
//     land.sh [options] <owner/repo>...     every open PR in these repos
//     land.sh [options] <owner/repo#n>...   just these PRs
//
// It polls every 60 s by default (--interval). Each poll costs one GraphQL query per repo (StatusCore's Status.Query, about 3 points) and
// one more (1 point) for each PR whose threads are all resolved and that a bot reviewed, to read the comments for the notice, and one for
// each PR that needs the record of Copilot's ask or whose review loop could be exhausted (those also cost a page per 100 comments or label
// events past the first, one for the authors of its open threads, and a REST call for each login that added review:claude, to see who has
// write access: mpaulosky/dotfiles#378), plus the action calls below and a gh pr view for each watched PR that left the open list. They
// all run as the owner's gh login, so they are almost all GraphQL points, out of the account's 5,000 an hour (the 5,000 REST requests an
// hour RELEASE_PR_PAT and Sandcastle spend are left alone, though they share the account): see references/tokens.md.
//
// The printed line carries status.cs's notice for resolved bot threads nobody replied to.
//
// Every watched PR goes through Landing.Decide(), and the command does what the decision says, once per PR head:
//
// - mark ready:        gh pr ready (only with --ready; otherwise a draft waits)
// - update branch:     gh pr update-branch; nobody is asked to review the new head
// - request Copilot:   once per PR, for a head no reviewer has reviewed or is on its way to: gh pr comment with the record that Copilot
//                      was asked (Landing.CopilotAskedMarker), then gh pr edit --add-reviewer @copilot, read back: gh exits 0 even when
//                      GitHub drops the request, so a dropped or refused one is warned about. No request follows a comment that failed.
//                      The record is on the PR, so no run asks twice, whoever ran before
// - add review:claude: gh pr edit --add-label review:claude, so Claude Review reviews a head Copilot can't review (its quota note, or a
//                      request that didn't register: an ask recorded 30 seconds ago with no request pending, which a request read back as
//                      dropped here becomes once the 30 seconds pass: PR Auto-Merge would take the label off sooner,
//                      mpaulosky/dotfiles#378); the decision makes it only once the head's checks are green, and never while PR
//                      Auto-Merge's exhaustion comment is the newest word on the PR (the owner adds the label to start a new loop;
//                      Landing.ExhaustedMarker)
// - arm auto-merge:    gh pr merge --auto --squash --match-head-commit <head>
// - leave to PR Auto-Merge, wait, report blocker, nothing: no call
//
// So a reviewed PR is left to its repo's own PR Auto-Merge workflow (dotfiles carries a copy), and a release-blog PR is armed when its
// workflow hasn't armed it. Release-blog PRs that open in a watched repo are watched too, and after a watched PR in a Baseline repo
// merges, the command waits up to --blog-wait minutes for its release-blog PR.
//
// It prints only changes: one line per PR whose state or action changed (status.sh's line), the calls it made under it, and nothing
// when nothing changed. It exits 0 once every watched PR is merged or closed, or after one pass with --once, and 1 at the time limit,
// when a --once pass can't read a repo, when a repos.txt name isn't owner/repo (before any call), or when it first has to wait a
// --interval Land.Sleep refuses, as time.sleep did (negative, NaN, or 2^63 ns, about 292 years, or more; --once never waits).
// --dry-run prints the calls instead of making them; a usage error exits 2.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using static Landing;

static class Land
{
	// A PR: owner/repo#n or its URL, anchored at both ends (a trailing line break is refused); its repo must be a Status.RepoName too.
	// Its number's digits are any Unicode decimal digits, as Python's \d and int() read them: a surrogate pair is let through here and
	// PrNumber refuses one that isn't a digit.
	static readonly Regex PrRef = new(@"\A(?:https://github\.com/)?([^/#\s]+/[^/#\s]+)(?:#|/pull/)((?:\d|[\uD800-\uDBFF][\uDC00-\uDFFF])+)\z",
			RegexOptions.CultureInvariant);

	// The defaults of --interval (seconds), --timeout and --blog-wait (minutes).
	public const double DefaultInterval = 60, DefaultTimeout = 180, DefaultBlogWait = 15;

	// {repo: PR numbers, or null for every open PR} from the arguments, in the order the repos were first named; one that is neither
	// owner/repo nor owner/repo#n (or a PR's URL) throws ArgumentException.
	public static OrderedDictionary<string, HashSet<long>?> ParseTargets(IEnumerable<string> args)
	{
		OrderedDictionary<string, HashSet<long>?> targets = [];
		foreach (var arg in args)
		{
			var match = PrRef.Match(arg);
			if (match.Success && Status.RepoName.IsMatch(match.Groups[1].Value))
			{
				var repo = match.Groups[1].Value;
				// The number is read whether or not the repo is already watched whole, so a bad one is refused in any order.
				var number = PrNumber(match.Groups[2].Value, arg);
				if (!targets.TryGetValue(repo, out var numbers))
					targets[repo] = numbers = [];
				numbers?.Add(number);
			}
			else if (!match.Success && Status.RepoName.IsMatch(arg))
				targets[arg] = null;
			else
				throw new ArgumentException($"not owner/repo or owner/repo#n: {arg}");
		}
		return targets;
	}

	// A PR number's digits (any Unicode decimal digit, astral ones too, as int() reads them) as a long. A rune that isn't a decimal digit,
	// or a number past long's range, throws ArgumentException, so it is a usage error rather than another PR's number wrapped around.
	public static long PrNumber(string digits, string arg)
	{
		long number = 0;
		foreach (var rune in digits.EnumerateRunes())
		{
			if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.DecimalDigitNumber)
				throw new ArgumentException($"not owner/repo or owner/repo#n: {arg}");
			try
			{
				number = checked(number * 10 + (long)Rune.GetNumericValue(rune));
			}
			catch (OverflowException)
			{
				throw new ArgumentException($"not owner/repo or owner/repo#n: {arg}");
			}
		}
		return number;
	}

	// The bound of time.sleep's wait: seconds * 1e9 must stay below 2^63 nanoseconds (its PyTime_t), about 292 years, so this double,
	// which rounds to 2^63 ns, is refused and the one below it is not. (On Linux time.sleep also refuses the last few days below it,
	// where its deadline on the monotonic clock overflows; that is not mimicked.)
	public const double LongestSleep = long.MaxValue / 1e9;

	// Waits seconds, as time.sleep does: a negative or NaN length, or one from LongestSleep up (infinity too), throws
	// ArgumentOutOfRangeException (exit 1, as time.sleep's ValueError and OverflowError were) rather than reach Thread.Sleep, which reads
	// -1 ms as forever and refuses more than int.MaxValue ms; a long wait is slept in steps of at most that, a whole number of them, so the
	// loop ends. wait is Thread.Sleep by default.
	public static void Sleep(double seconds, Action<TimeSpan>? wait = null)
	{
		if (double.IsNaN(seconds) || seconds < 0 || seconds >= LongestSleep)
			throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"sleep length must be from 0 to {LongestSleep} seconds");
		wait ??= System.Threading.Thread.Sleep;
		const double most = int.MaxValue / 1000.0;
		var steps = (long)Math.Floor(seconds / most);
		for (long i = 0; i < steps; i++)
			wait(TimeSpan.FromSeconds(most));
		if (seconds - steps * most is var last and > 0)
			wait(TimeSpan.FromSeconds(last));
	}

	// The gh calls that carry out one decision's action; none for the rest.
	public static List<string[]> Commands(string action, string repo, long number, string head)
	{
		string[] pr = [number.ToString(CultureInfo.InvariantCulture), "-R", repo];
		return action switch
		{
			MarkReady => [["gh", "pr", "ready", .. pr]],
			UpdateBranch => [["gh", "pr", "update-branch", .. pr]],
			// The record first: a request without one could be made twice. The loop stops at a failed comment.
			RequestCopilot => [["gh", "pr", "comment", .. pr, "--body", CopilotAskedComment], ["gh", "pr", "edit", .. pr, "--add-reviewer", "@copilot"]],
			RequestClaude => [["gh", "pr", "edit", .. pr, "--add-label", ReviewClaude]],
			ArmAutoMerge => [["gh", "pr", "merge", .. pr, "--auto", "--squash", "--match-head-commit", head]],
			_ => [],
		};
	}

	// Runs one gh call (argv starts with "gh"): (ok, message), the message its stderr, else its stdout, on one line.
	public static (bool Ok, string Message) RunGh(IReadOnlyList<string> argv)
	{
		var run = Gh.Process([.. argv.Skip(1)]);
		return (run.Code == 0, Gh.Message(run).Replace("\n", " ", StringComparison.Ordinal));
	}

	// Runs one gh call that reads an answer (argv starts with "gh"): its stdout, trimmed, when it succeeded, whatever it wrote to stderr
	// (a deprecation or scope warning isn't the answer); a failure throws GhException with RunGh's message. process runs gh (Gh.Process).
	public static string ReadGh(IReadOnlyList<string> argv, Func<IReadOnlyList<string>, GhRun>? process = null)
	{
		var run = (process ?? Gh.Process)([.. argv.Skip(1)]);
		if (run.Code != 0)
			throw new GhException(Gh.Message(run).Replace("\n", " ", StringComparison.Ordinal));
		return run.Stdout.Trim();
	}

	// How many of the PR's review requests and reviews of its head are Copilot's. Copilot's quota note counts here: Copilot was asked
	// about the head and answered, so asking again only brings another note (Decide() calls in Claude). Copilot is its own logins, as
	// Landing.Copilot reads them (#155), not a login that merely contains "copilot".
	public const string CopilotJq = ".headRefOid as $h | [(.reviewRequests[] | .login // .name // \"\"), "
			+ "(.reviews[] | select(.commit.oid == $h) | .author.login)] "
			+ "| map(select(test(\"^(copilot|copilot-pull-request-reviewer)(\\\\[bot\\\\])?$\"; \"i\"))) | length";

	// Whether Copilot's review is requested on the PR, or Copilot has reviewed its head.
	public static bool GhCopilotRequested(string repo, long number, Func<IReadOnlyList<string>, GhRun>? process = null)
	{
		var answer = ReadGh(["gh", "pr", "view", number.ToString(CultureInfo.InvariantCulture), "-R", repo, "--json", "headRefOid,reviewRequests,reviews", "--jq", CopilotJq],
				process);
		return int.Parse(answer, NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture) > 0;
	}

	// MERGED or CLOSED (or OPEN) for a PR that left the open list.
	public static string GhPrState(string repo, long number, Func<IReadOnlyList<string>, GhRun>? process = null) =>
			ReadGh(["gh", "pr", "view", number.ToString(CultureInfo.InvariantCulture), "-R", repo, "--json", "state", "--jq", ".state"], process);

	// land.cs's command line. lander is the watch loop (a new Lander over the targets by default), sleep waits between polls and
	// readRepoList reads the Fleet (Repos.Read).
	public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter err, Lander? lander = null, Action<double>? sleep = null,
			Func<OrderedDictionary<string, string>>? readRepoList = null)
	{
		var parser = new Arguments("land.cs", "[-h] [--ready] [--once] [--dry-run] [--interval INTERVAL] [--timeout TIMEOUT] [--blog-wait BLOG_WAIT] [prs ...]",
						"Watch a round's PRs and act on the landing decision until they land.")
				.Flag("--ready", "mark watched drafts ready (otherwise a draft waits)")
				.Flag("--once", "one pass, then exit")
				.Flag("--dry-run", "print the gh calls instead of making them")
				.FloatOption("--interval", "seconds between polls (default 60)")
				.FloatOption("--timeout", "minutes before giving up (default 180)")
				.FloatOption("--blog-wait", "minutes to wait for a release-blog PR after a Baseline PR merges (default 15)");
		if (parser.Parse(args, output, err) is int exit)
			return exit;
		double interval, timeout, blogWait;
		try
		{
			interval = parser.Float("--interval", DefaultInterval);
			timeout = parser.Float("--timeout", DefaultTimeout);
			blogWait = parser.Float("--blog-wait", DefaultBlogWait);
		}
		catch (ArgumentError error)
		{
			return parser.Error(error.Message, err);
		}
		var once = parser.Has("--once");
		var listed = (readRepoList ?? (() => Repos.Read()))();
		OrderedDictionary<string, HashSet<long>?> targets;
		try
		{
			targets = parser.Positional.Count > 0 ? ParseTargets(parser.Positional) : new(listed.Keys.Select(repo => KeyValuePair.Create(repo, (HashSet<long>?)null)));
		}
		catch (ArgumentException error)
		{
			return parser.Error(error.Message, err);
		}
		// A Fleet registry name that isn't owner/repo is refused before any call, as status.cs refuses it.
		if (parser.Positional.Count == 0 && targets.Keys.FirstOrDefault(repo => !Status.RepoName.IsMatch(repo)) is { } bad)
		{
			err.Write($"repos.txt: not owner/repo: {bad}\n");
			return 1;
		}

		lander ??= new Lander(targets, listed, output, err);
		lander.WantReady = parser.Has("--ready");
		lander.DryRun = parser.Has("--dry-run");
		lander.BlogWait = blogWait * 60;
		sleep ??= seconds => Sleep(seconds);

		var deadline = lander.Clock() + timeout * 60;
		while (true)
		{
			var (done, failed) = lander.Poll();
			if (lander.Watched.Count == 0 && !failed)
			{
				lander.Say($"No open PRs to land in {targets.Count} repo(s).");
				return 0;
			}
			if (done)
			{
				if (!once)
					lander.Say($"All {lander.Watched.Count} watched PR(s) merged or closed.");
				return 0;
			}
			if (once)
				return failed ? 1 : 0;
			if (lander.Clock() >= deadline)
			{
				var still = lander.Watched.Where(kv => kv.Value.Final == "").Select(kv => $"{kv.Key.Repo.Split('/')[^1]}#{kv.Key.Number}")
						.Order(StringComparer.Ordinal);
				var blog = lander.BlogDue.Count > 0 ? "; still waiting for a release-blog PR in " + string.Join(", ", lander.BlogDue.Keys.Order(StringComparer.Ordinal)) : "";
				lander.Say($"Time limit reached; still open: {(still.Any() ? string.Join(", ", still) : "none")}{blog}");
				return 1;
			}
			sleep(interval);
		}
	}
}

// One watched PR: its title, the last state printed (null before the first), and MERGED or CLOSED once it has left the open list.
sealed class Watched
{
	public string Title = "";
	public string? Seen;
	public string Final = "";
}

// The watch loop's state across polls; Poll() is one pass. Fetch, PrState, CopilotRequested, Run and Clock are GitHub's and the clock's
// by default; a test hands in its own.
sealed class Lander
{
	public readonly OrderedDictionary<string, HashSet<long>?> Targets; // {repo: numbers, or null for every open PR}
	public readonly IReadOnlyDictionary<string, string> Scopes; // {repo: repos.txt scope}
	public bool WantReady;
	public bool DryRun;
	public double BlogWait = Land.DefaultBlogWait * 60;
	public Func<string, string, JsonNode> Fetch;
	public Func<string, long, string> PrState = (repo, number) => Land.GhPrState(repo, number);
	public Func<string, long, bool> CopilotRequested = (repo, number) => Land.GhCopilotRequested(repo, number);
	public Func<IReadOnlyList<string>, (bool Ok, string Message)> Run = Land.RunGh;
	public Func<double> Clock = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
	public readonly TextWriter Out;
	public readonly TextWriter Err;
	public readonly Dictionary<(string Repo, long Number), Watched> Watched = [];
	public readonly HashSet<(string Repo, long Number, string Action, string Head)> Acted = [];
	public readonly Dictionary<string, double> BlogDue = []; // {repo: deadline} while its blog PR is awaited
	public readonly Dictionary<string, string> Errors = []; // {repo, or repo#number: the last error printed}
	public readonly HashSet<string> Started = []; // repos read at least once

	public Lander(OrderedDictionary<string, HashSet<long>?> targets, IReadOnlyDictionary<string, string> scopes, TextWriter output, TextWriter err)
	{
		Targets = targets;
		Scopes = scopes;
		Out = output;
		Err = err;
		Fetch = new StatusReader(err: err).Fetch;
	}

	public void Say(string text)
	{
		Out.Write(text + "\n");
		Out.Flush();
	}

	// One pass over every repo: (nothing left to watch, a repo couldn't be read).
	public (bool Done, bool Failed) Poll()
	{
		var failed = false;
		foreach (var (repo, numbers) in Targets)
			failed |= !PollRepo(repo, numbers);
		return (IsDone(), failed);
	}

	bool PollRepo(string repo, HashSet<long>? numbers)
	{
		var (owner, name) = Status.SplitRepo(repo);
		List<JsonNode> nodes;
		try
		{
			nodes = [.. J.NodesOf(J.Req(J.Req(Fetch(owner, name), "repository"), "pullRequests")).Select(n => n!)];
		}
		catch (Exception error) // one unreadable repo mustn't stop the rest
		{
			if (Errors.GetValueOrDefault(repo) != error.Message)
			{
				Err.Write($"{repo}: {error.Message}\n");
				Errors[repo] = error.Message;
			}
			return false;
		}
		Errors.Remove(repo);
		var (checksRequired, mergedByWorkflow) = Status.RepoFlags(Scopes.GetValueOrDefault(repo, "full"));
		var open = new Dictionary<long, JsonNode>();
		foreach (var node in nodes)
			open[J.Int(J.Req(node, "number")) ?? throw new KeyNotFoundException("'number'")] = node;

		if (Started.Add(repo)) // the PRs to watch are fixed on the first read
			foreach (var number in numbers ?? [.. open.Keys])
				Watched.TryAdd((repo, number), new Watched());
		var blogOpen = false;
		foreach (var (number, node) in open)
			if (Status.ToState(node, checksRequired, mergedByWorkflow, WantReady, owner).ReleaseBlog)
			{
				Watched.TryAdd((repo, number), new Watched());
				blogOpen = true;
			}

		foreach (var ((watchedRepo, number), pr) in Watched.OrderBy(kv => kv.Key.Repo, StringComparer.Ordinal).ThenBy(kv => kv.Key.Number).ToList())
		{
			if (watchedRepo != repo || pr.Final != "")
				continue;
			if (open.TryGetValue(number, out var node))
				UpdateOpen(repo, number, pr, node, checksRequired, mergedByWorkflow);
			else
				UpdateGone(repo, number, pr);
		}
		// After the updates, so a release-blog PR that opened in the same poll as the merge that started the wait still ends it.
		if (blogOpen)
			BlogDue.Remove(repo);

		if (BlogDue.TryGetValue(repo, out var due) && Clock() >= due)
		{
			BlogDue.Remove(repo);
			Say($"{repo}: no release-blog PR within {Script.G(BlogWait / 60)} min");
		}
		return true;
	}

	void UpdateOpen(string repo, long number, Watched pr, JsonNode node, bool checksRequired, bool mergedByWorkflow)
	{
		var state = Status.ToState(node, checksRequired, mergedByWorkflow, WantReady, Status.SplitRepo(repo).Owner);
		var decision = Decide(state);
		pr.Title = J.Fmt(J.Req(node, "title"));
		var seen = string.Join("\0", state.State, state.Head, state.Draft, state.MergeState, state.HeadReviewer, state.OpenThreads,
				string.Join("\x1f", state.UnrepliedThreads), state.UnrepliedThreads.Length, state.AutoMergeArmed, decision.Action, decision.Reason);
		if (seen == pr.Seen)
			return;
		pr.Seen = seen;
		Say(Status.Line(repo, node, state));
		var calls = Land.Commands(decision.Action, repo, number, state.Head);
		if (calls.Count == 0 || !Acted.Add((repo, number, decision.Action, state.Head)))
			return;
		foreach (var argv in calls)
		{
			if (DryRun)
			{
				Say("  would run: " + string.Join(" ", argv));
				continue;
			}
			var (ok, message) = Run(argv);
			if (argv.Contains("--add-reviewer"))
			{
				// A request GitHub refuses outright is no worse than one it drops: the read-back finds none, and the warning says so.
				if (ok)
					Say("  ran: " + string.Join(" ", argv));
				else
					Say("  warning: " + string.Join(" ", argv) + " failed" + (message != "" ? $" ({message})" : ""));
				// A read-back that fails isn't retried by asking again: Copilot is asked once per PR, and the record now on it decides what
				// the next poll does.
				ReadBackCopilot(repo, number);
				continue;
			}
			Say((ok ? "  ran: " : "  failed: ") + string.Join(" ", argv) + (message != "" && !ok ? $" ({message})" : ""));
			if (!ok)
				break;
		}
	}

	// Reads Copilot's review request back, and warns when GitHub dropped it. Nothing is remembered: the record on the PR settles after 30
	// seconds, and the decision calls in Claude then, as PR Auto-Merge would take the label off sooner (mpaulosky/dotfiles#378). A
	// read-back that fails returns false; otherwise true.
	bool ReadBackCopilot(string repo, long number)
	{
		var url = $"https://github.com/{repo}/pull/{number}";
		try
		{
			if (CopilotRequested(repo, number))
				return true;
		}
		catch (Exception error) // reported like a failed call
		{
			Say($"  failed: Copilot's review request couldn't be read back on #{number} ({error.Message}); request it at {url}");
			return false;
		}
		Say($"  warning: Copilot's review request didn't register on #{number} (GitHub drops it once the Copilot code review budget is used up); "
				+ $"{ReviewClaude} backs Copilot up for this PR once the request has been missing for 30 seconds and its checks are green");
		return true;
	}

	void UpdateGone(string repo, long number, Watched pr)
	{
		string final;
		try
		{
			final = PrState(repo, number);
			// Only MERGED and CLOSED end the watch and OPEN keeps it; any other answer is unread and reported, so a PR that may still be
			// open isn't called gone.
			if (final is not ("MERGED" or "CLOSED" or "OPEN"))
				throw new GhException($"not a PR state: {Status.Printable(final)}");
		}
		catch (Exception error) // reported, and tried again next poll
		{
			var key = $"{repo}#{number}";
			if (Errors.GetValueOrDefault(key) != error.Message)
			{
				Err.Write($"{key}: {error.Message}\n");
				Errors[key] = error.Message;
			}
			return;
		}
		if (final == "OPEN") // not on the first page of open PRs, or the list lagged
			return;
		pr.Final = final;
		Say($"{repo.Split('/')[^1]}#{number} {final.ToLowerInvariant()}");
		// A PR seen open that merges in a Baseline repo starts a release, and with it a release-blog PR; a [skip-release] one (a blog PR)
		// doesn't, and the Factory ("factory") and dotfiles (repo-settings-only) have none.
		if (final == "MERGED" && pr.Seen is not null && !pr.Title.Contains("[skip-release]", StringComparison.Ordinal)
				&& Scopes.GetValueOrDefault(repo, "full") == "full")
			BlogDue[repo] = Clock() + BlogWait;
	}

	bool IsDone() => Targets.Keys.All(Started.Contains) && BlogDue.Count == 0 && Watched.Values.All(pr => pr.Final != "");
}
