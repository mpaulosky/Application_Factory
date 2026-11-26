// release_queue.py's logic in C# (ADR 0017 in the Application Factory): the merged PRs the release workflow still has to release, oldest
// merge first. release_queue.cs is its entry file, and its header the contract. Each method is the Python function of the same name, with
// the same cases; the Releases are read with ReleasePost.cs's gh calls and its source_pr_of() and version_key().
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baseline;

// release_post's gh calls, plus the PRs merged since a point in time.
interface IQueueGitHub : IReleaseGitHub
{
	List<JsonNode?> MergedPulls(string? since);
}

// The calls, made through the gh CLI, as release_queue.py's GitHub class makes them. ClosedPullsPage is virtual, so a test pages over
// canned data as the pytest overrode _closed_pulls_page.
class QueueGitHub(string repository) : IQueueGitHub
{
	readonly ReleaseGitHub gh = new(repository);

	public string Repository => gh.Repository;

	public JsonNode? Pull(long number) => gh.Pull(number);

	public string PullTitle(BigInteger number) => gh.PullTitle(number);

	public List<JsonNode?> Commits(long number) => gh.Commits(number);

	public List<JsonNode?> Files(long number) => gh.Files(number);

	public List<JsonNode?> Releases() => gh.Releases();

	protected virtual List<JsonNode?> ClosedPullsPage(int page)
	{
		var path = $"repos/{Repository}/pulls?state=closed&base=main&sort=updated&direction=desc&per_page={ReleaseQueue.PageSize}&page={page}";
		var output = ReleasePost.Universal(ReleasePost.Utf8.GetString(Script.CheckOutput(["gh", "api", path])));
		return [.. JsonNode.Parse(output)!.AsArray()];
	}

	// Every PR merged into main at or after since (an ISO 8601 time), newest update first.
	//
	// PRs come sorted by last update, newest first, and a PR's update time is never earlier than its merge time. So once one PR was last
	// updated before since, every PR after it was too, and none of them merged after since.
	public List<JsonNode?> MergedPulls(string? since)
	{
		List<JsonNode?> pulls = [];
		for (var page = 1; ; page++)
		{
			var batch = ClosedPullsPage(page);
			if (batch.Count == 0)
				return pulls;
			foreach (var pr in batch)
			{
				if (ReleasePost.Text(ReleasePost.Get(pr, "merged_at")) is { Length: > 0 } mergedAt && Py.CompareCodePoints(mergedAt, Since(since)) >= 0)
				{
					JsonObject pulled = [];
					foreach (var key in new[] { "number", "title", "merged_at", "merge_commit_sha" })
						pulled[key] = ReleasePost.Get(pr, key)?.DeepClone();
					pulled["author"] = ReleasePost.Get(ReleasePost.Get(pr, "user"), "login")?.DeepClone();
					pulls.Add(pulled);
				}
			}
			if (batch.Any(pr => Py.CompareCodePoints(ReleasePost.Text(ReleasePost.Get(pr, "updated_at")) ?? "", Since(since)) < 0))
				return pulls;
		}
	}

	// A cutoff PR with no merge time can't be compared with, as Python's str >= None can't.
	static string Since(string? since) => since ?? throw new InvalidOperationException("the cutoff PR has no merge time to list the merges since");
}

// A listed PR's merge commit isn't on the fetched main, so its place in the order is unknown.
sealed class UnknownMergeOrder(string message) : Exception(message);

static class ReleaseQueue
{
	public const int PageSize = 100;

	public const string SkipMarker = "[skip-release]";
	public const string Dependabot = "dependabot[bot]";

	public const string Usage = "[-h] --repo REPO [--pr PR]";

	static readonly Regex ReleaseTag = new(@"\Av\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant);

	// A PR's number, as the JSON holds it.
	public static BigInteger Number(JsonNode? pr) => BigInteger.Parse(ReleasePost.Get(pr, "number")!.ToJsonString(), CultureInfo.InvariantCulture);

	// (tag, source PR number) of the newest published Release by version, or (null, null).
	public static (string? Tag, BigInteger? Pr) CutoffRelease(IEnumerable<JsonNode?> releases)
	{
		(string? Tag, BigInteger? Pr) newest = (null, null);
		foreach (var release in releases)
		{
			var tag = ReleasePost.Text(ReleasePost.Get(release, "tag_name")) ?? "";
			if (!ReleaseTag.IsMatch(tag) || ReleasePost.SourcePrOf(release) is not { } pr)
				continue;
			// max() keeps the first of equal keys.
			if (newest.Tag is null || ReleasePost.VersionKey(tag).CompareTo(ReleasePost.VersionKey(newest.Tag)) > 0)
				newest = (tag, pr);
		}
		return newest;
	}

	// {merge commit sha: position} along main's first-parent history, oldest first.
	//
	// Fetches main first, so every PR already listed as merged has its commit here.
	public static Dictionary<string, int> MainHistoryOrder()
	{
		Script.CheckOutput(["git", "fetch", "--quiet", "origin", "main"]);
		var output = ReleasePost.Utf8.GetString(Script.CheckOutput(["git", "rev-list", "--first-parent", "--reverse", "FETCH_HEAD"]));
		Dictionary<string, int> order = [];
		foreach (var (sha, position) in output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select((sha, position) => (sha, position)))
			order[sha] = position;
		return order;
	}

	// Whether the commit is the tag's commit or one of its ancestors.
	public static bool TagContains(string tag, string sha) => Script.Capture(["git", "merge-base", "--is-ancestor", sha, tag]).Code == 0;

	// The PR author's login: listed PRs carry "author", a PR fetched on its own carries "user".
	public static string? AuthorOf(JsonNode? pr) =>
			ReleasePost.Text(ReleasePost.Get(pr, "author")) is { Length: > 0 } author ? author : ReleasePost.Text(ReleasePost.Get(ReleasePost.Get(pr, "user"), "login"));

	public static bool Owed(JsonNode? pr, ISet<BigInteger> released, Func<string, bool> inCutoff) =>
			ReleasePost.Text(ReleasePost.Get(pr, "merged_at")) is { Length: > 0 }
			&& !(ReleasePost.Text(ReleasePost.Get(pr, "title")) ?? "").Contains(SkipMarker, StringComparison.Ordinal)
			&& AuthorOf(pr) != Dependabot
			&& !released.Contains(Number(pr))
			&& !inCutoff(ReleasePost.Text(ReleasePost.Get(pr, "merge_commit_sha")) ?? "");

	// The PR numbers owed a release, oldest merge first.
	//
	// Order comes from each merge commit's position on main (mainOrder), never from merged_at: that has one-second resolution, so two merges
	// in the same second would tie. main is fetched after the PRs are listed, so every one of them should be there; if one isn't, throw
	// rather than guess its place.
	public static List<BigInteger> Queue(IEnumerable<JsonNode?> pulls, ISet<BigInteger> released, Func<string, bool> inCutoff, bool hasCutoff, BigInteger? trigger = null,
			JsonNode? triggerPull = null, IReadOnlyDictionary<string, int>? mainOrder = null)
	{
		mainOrder ??= new Dictionary<string, int>();
		// A dict keyed by number: a later PR of the same number takes the first one's place.
		List<BigInteger> numbers = [];
		Dictionary<BigInteger, JsonNode?> byNumber = [];
		void Put(BigInteger number, JsonNode? pr)
		{
			if (!byNumber.ContainsKey(number))
				numbers.Add(number);
			byNumber[number] = pr;
		}
		foreach (var pr in pulls)
			Put(Number(pr), pr);
		if (trigger is { } triggered && !byNumber.ContainsKey(triggered) && triggerPull is not null)
			Put(triggered, triggerPull);
		// A first release: don't sweep up history, just release what triggered the run.
		if (!hasCutoff)
			numbers = trigger is { } only && byNumber.ContainsKey(only) ? [only] : [];
		var pending = numbers.Select(number => byNumber[number]).Where(pr => Owed(pr, released, inCutoff)).ToList();
		var missing = pending.Where(pr => ReleasePost.Text(ReleasePost.Get(pr, "merge_commit_sha")) is not { } sha || !mainOrder.ContainsKey(sha)).ToList();
		if (missing.Count > 0)
		{
			var named = string.Join(", ", missing.Select(pr => $"#{Number(pr)}"));
			throw new UnknownMergeOrder($"merge commits of {named} aren't on the fetched main; retry the run");
		}
		return [.. pending.OrderBy(pr => mainOrder[ReleasePost.Text(ReleasePost.Get(pr, "merge_commit_sha"))!]).Select(Number)];
	}

	// Python's repr() of a list of ints, which json.dumps() writes the same: [3, 2, 4].
	public static string ListText(IEnumerable<BigInteger> numbers) => $"[{string.Join(", ", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)))}]";

	// release_queue.py's main(): the queue as JSON on stdout, and what it read on stderr. gh, contains and mainOrder stand in for the gh
	// CLI, git merge-base and main's fetched history in a test.
	public static int Run(IEnumerable<string> args, TextWriter stdout, TextWriter stderr, Func<string, IQueueGitHub>? gh = null, Func<string, string, bool>? contains = null,
			IReadOnlyDictionary<string, int>? mainOrder = null)
	{
		var parser = new Arguments("release_queue.cs", Usage, "Print, as a JSON list, the merged PRs the release workflow still has to release.")
				.Required("--repo", "owner/name")
				.IntOption("--pr", "the PR that triggered this run");
		if (parser.Parse(args, stdout, stderr) is int exit)
			return exit;
		contains ??= TagContains;
		BigInteger? trigger = parser.Has("--pr") ? parser.IntValue("--pr", 0) : null;

		var github = (gh ?? (repository => new QueueGitHub(repository)))(parser.Value("--repo", ""));
		var releases = github.Releases();
		HashSet<BigInteger> released = [.. releases.Select(ReleasePost.SourcePrOf).OfType<BigInteger>()];
		var (cutoffTag, cutoffPr) = CutoffRelease(releases);

		var pulls = cutoffTag is not null ? github.MergedPulls(ReleasePost.Text(ReleasePost.Get(github.Pull((long)cutoffPr!.Value), "merged_at"))) : [];
		JsonNode? triggerPull = null;
		if (trigger is { } number && pulls.All(pr => Number(pr) != number))
		{
			var fetched = github.Pull((long)number);
			// A manual run can name any PR; only one merged into main can be owed a release.
			if (ReleasePost.Text(ReleasePost.Get(ReleasePost.Get(fetched, "base"), "ref")) == "main")
				triggerPull = fetched;
		}

		var result = Queue(pulls, released, sha => cutoffTag is not null && sha.Length > 0 && contains(cutoffTag, sha), cutoffTag is not null, trigger, triggerPull,
				// Fetched only now, after the PRs are listed, so each listed merge is on it.
				mainOrder ?? MainHistoryOrder());
		// stdout carries only the JSON the workflow reads; the explanation goes to stderr.
		var cutoff = cutoffTag is not null ? $"{cutoffTag} (PR #{cutoffPr})" : "none";
		stderr.Write($"Newest published Release: {cutoff}. Queue: {(result.Count > 0 ? ListText(result) : "empty")}.\n");
		stdout.Write(ListText(result) + "\n");
		return 0;
	}
}
