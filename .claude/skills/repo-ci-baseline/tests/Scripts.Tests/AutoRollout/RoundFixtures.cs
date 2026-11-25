// What the round's tests share: test_auto_rollout.py's Fleet, Lock, run_auto and reapplied, for AutoRound's World, with the same fakes.

// The repos the round's tests list, as test_auto_rollout.py's LISTED, TM and REST.
static class Fleets
{
	public const string TM = "mpaulosky/TicketManager";

	public static readonly IReadOnlyList<string> Rest =
			["mpaulosky/IssueTracker", "mpaulosky/Blazor-Server", "mpaulosky/IssueManager", "mpaulosky/Articles", "mpaulosky/SuggestionApp"];

	public static readonly IReadOnlyList<KeyValuePair<string, string>> Listed =
	[
		new(TM, "full"), .. Rest.Select(repo => new KeyValuePair<string, string>(repo, "full")), new("mpaulosky/Application_Factory", "factory"),
	];

	public static string Short(string repo) => repo[(repo.LastIndexOf('/') + 1)..];

	// A repo's head commit in these tests: its name, lowered, padded with 0 to 40 characters.
	public static string HeadOf(string repo) => Short(repo).ToLowerInvariant().PadRight(40, '0')[..40];

	// Each repo's PR open at the worktree's head once it pushes: TicketManager's #130, the rest's #200 on, in order.
	public static Dictionary<string, long> AllOpen() =>
			new([new(TM, 130), .. Rest.Select((repo, n) => new KeyValuePair<string, long>(repo, 200 + n))]);
}

// reapply, review, land, gh and git as the tests set them (test_auto_rollout.py's Fleet).
//
// Codes: {repo: [reapply exit codes in order]}, 0 once they run out. A 0 for a repo in Opens pushes: its branch gets the repo's head beyond
// main, and its PR (from Opens) is OPEN at that head. Prs: {repo: (number, state, head) or null} as gh reads them before the round (default:
// the last round's, merged). Stale: {repo: reads that still show the last round's PR}. Unlanded: refs land.sh leaves open. StillOpen: refs
// that read OPEN after land.sh returned 0. Unreadable: repos gh fails for (Failures: {repo: reads that fail before gh answers}). Closes: refs
// land.sh closes. Each list it records (Events, Reviewed, Landed, Sleeps, Locks) is in the order the round made the calls.
sealed class Fleet : IDisposable
{
	public Dictionary<string, List<int?>> Codes = [];
	public Dictionary<string, long> Opens = [];
	public Dictionary<string, (long Number, string State, string Head)?> Prs =
			Fleets.Listed.ToDictionary(pair => pair.Key, _ => ((long, string, string)?)(100, "MERGED", new string('0', 40)));
	public Dictionary<string, bool> Ahead = [];
	public Dictionary<string, int> ReviewCodes = [];
	public HashSet<string> Unlanded = [], StillOpen = [], Unreadable = [], Closes = [], BranchFails = [], Moves = [];
	public Dictionary<string, int> Stale = [], Failures = [], Unlisted = [], StateFailures = [];
	public List<string> Problems = [];
	public bool Locked { get; set; }
	public Dictionary<string, Exception> Raises = [];
	public Exception? LandRaises { get; set; }
	public Exception? LockRaises { get; set; }
	public int? LandCode { get; set; }
	public Dictionary<string, List<string>> Broke = [];

	// What the round asked, in order: ("reapply", repo, brings, path, each lock's closed), ("review", repo), ("land", refs), ("check").
	public readonly List<object[]> Events = [];
	public readonly List<(string Repo, string Worktree)> Reviewed = [];
	public readonly List<IReadOnlyList<string>> Landed = [];
	public readonly List<double> Sleeps = [];
	public readonly List<FileStream> Locks = [];
	readonly string folder = Directory.CreateTempSubdirectory("fleet-lock.").FullName;

	public void Dispose()
	{
		foreach (var held in Locks)
			held.Dispose();
		Directory.Delete(folder, recursive: true);
	}

	static string RepoOf(string path) => "mpaulosky/" + Path.GetFileName(path);

	// Whether a lock the round took is closed: a disposed FileStream can no longer write.
	public static bool Closed(FileStream held) => !held.CanWrite;

	public int? Reapply(string brings, string path)
	{
		var repo = RepoOf(path);
		Events.Add(["reapply", Fleets.Short(repo), brings, path, Locks.Select(Closed).ToList()]);
		if (Raises.TryGetValue(repo, out var raised))
			throw raised;
		var code = Codes.TryGetValue(repo, out var seq) && seq.Count > 0 ? seq[0] : 0;
		if (seq is { Count: > 0 })
			seq.RemoveAt(0);
		if (code == 0 && Opens.TryGetValue(repo, out var number))
		{
			Ahead[repo] = true;
			Prs[repo] = (number, "OPEN", Fleets.HeadOf(repo));
		}
		return code;
	}

	public (int Code, string Log, List<string> Broke) Review(string repo, string worktree)
	{
		Events.Add(["review", Fleets.Short(repo)]);
		Reviewed.Add((repo, worktree));
		return (ReviewCodes.GetValueOrDefault(repo, 0), $"/tmp/review-{Fleets.Short(repo)}.log", [.. Broke.GetValueOrDefault(repo, [])]);
	}

	public int? Land(IReadOnlyList<string> refs)
	{
		Events.Add(["land", refs.ToList()]);
		Landed.Add([.. refs]);
		if (LandRaises is not null)
			throw LandRaises;
		foreach (var reference in refs)
		{
			var repo = reference.Split('#')[0];
			var (number, _, head) = Prs[repo]!.Value;
			if (Moves.Contains(reference))
				head = new string('e', 40); // land.sh updated the branch before it merged
			if (Closes.Contains(reference))
				Prs[repo] = (number, "CLOSED", head);
			else if (!Unlanded.Contains(reference))
				Prs[repo] = (number, "MERGED", head);
		}
		if (LandCode is not null)
			return LandCode;
		return Unlanded.Overlaps(refs) ? 1 : 0;
	}

	public (long Number, string State, string Head)? PrOf(string repo)
	{
		if (Unreadable.Contains(repo))
			throw new InvalidOperationException("HTTP 502");
		if (Failures.GetValueOrDefault(repo) > 0)
		{
			Failures[repo]--;
			throw new InvalidOperationException("HTTP 502");
		}
		if (Ahead.GetValueOrDefault(repo) && Unlisted.GetValueOrDefault(repo) > 0)
		{
			Unlisted[repo]--;
			return null; // pushed, and not listed yet
		}
		if (Stale.GetValueOrDefault(repo) > 0)
		{
			Stale[repo]--;
			return (99, "MERGED", new string('f', 40));
		}
		return Prs[repo];
	}

	public string PrState(string repo, long number)
	{
		if (Unreadable.Contains(repo))
			throw new InvalidOperationException("HTTP 502");
		if (StateFailures.GetValueOrDefault(repo) > 0)
		{
			StateFailures[repo]--;
			throw new InvalidOperationException("HTTP 503");
		}
		var state = Prs[repo]!.Value.State;
		return state == "MERGED" && StillOpen.Contains($"{repo}#{number}") ? "OPEN" : state;
	}

	public (string Head, bool Ahead) BranchOf(string worktree)
	{
		var name = Path.GetFileName(Path.GetDirectoryName(worktree))!;
		var repo = "mpaulosky/" + (name.EndsWith("-worktrees", StringComparison.Ordinal) ? name[..^"-worktrees".Length] : name);
		if (BranchFails.Contains(repo))
			throw new InvalidOperationException("git rev-parse HEAD exited 128: not a git repository");
		return (Fleets.HeadOf(repo), Ahead.GetValueOrDefault(repo));
	}

	public List<string> Check()
	{
		Events.Add(["check"]);
		return Problems;
	}

	public FileStream? Lock()
	{
		if (LockRaises is not null)
			throw LockRaises;
		if (Locked)
			return null;
		Locks.Add(new FileStream(Path.Combine(folder, $"lock{Locks.Count}"), FileMode.Create, FileAccess.Write));
		return Locks[^1];
	}

	// The World a round of these fakes runs in.
	public World World() => new(Reapply, Review, Land, PrOf, PrState, BranchOf, Check, Lock, Sleeps.Add);

	// The repos reapplied, in order (test_auto_rollout.py's reapplied).
	public List<string> Reapplied() => [.. Events.Where(e => (string)e[0] == "reapply").Select(e => (string)e[1])];

	// rollout.sh --auto --root root argv... on this fleet (test_auto_rollout.py's run_auto): (exit code, stdout, stderr). Advisory lists
	// are asked of asked (the pytest's autouse no_advisories: none), and a signal outside a hold is recorded in signals, never ends the test.
	public (int Code, string Out, string Err) RunAuto(IReadOnlyList<string> argv, string root = "/r", string? defaultRoot = "/r",
			Func<string, string?>? advisories = null, List<int>? signals = null)
	{
		var (output, err) = (new StringWriter(), new StringWriter());
		var code = Rollout.Run(["--auto", "--root", root, .. argv], output, err, listed: Fleets.Listed, reapply: Reapply, exists: _ => true,
				auto: World(), advisories: advisories ?? (_ => null), onSignal: (signum, _) => signals?.Add(signum), defaultRoot: defaultRoot);
		return (code, output.ToString(), err.ToString());
	}
}
