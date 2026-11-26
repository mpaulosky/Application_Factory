// backfill_blog_posts.py's logic in C# (ADR 0017 in the Application Factory): the release blog posts for past releases, and the release
// tables refreshed once after them. backfill_blog_posts.cs is its entry file, and its header the contract. Each method is the Python
// function of the same name, with the same cases; the posts and tables are ReleasePost.cs's.
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Baseline;

// pr_numbers or wait_for_prs that aren't comma-separated PR numbers, as Python's ValueError.
sealed class PrNumbersError(string message) : Exception(message);

// gh with Releases() fixed to one listing, so the posts and the tables agree.
sealed class ListedReleases(IReleaseGitHub gh, List<JsonNode?> releases) : IReleaseGitHub
{
	public JsonNode? Pull(long number) => gh.Pull(number);

	public string PullTitle(BigInteger number) => gh.PullTitle(number);

	public List<JsonNode?> Commits(long number) => gh.Commits(number);

	public List<JsonNode?> Files(long number) => gh.Files(number);

	public List<JsonNode?> Releases() => releases;
}

static class BackfillBlogPosts
{
	public const int WaitAttempts = 15;
	public const int WaitDelay = 2;

	// argparse's usage line, wrapped where it wraps it.
	public const string Usage = "[-h] [--regenerate REGENERATE]\n                              [--pr-numbers PR_NUMBERS]\n"
			+ "                              [--wait-for-prs WAIT_FOR_PRS] [--repo REPO]";

	// str.isdigit(): every character a digit, decimal or not (a superscript ² is one), and at least one.
	static bool IsDigit(string text)
	{
		if (text.Length == 0)
			return false;
		for (var i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
		{
			if (CharUnicodeInfo.GetDigitValue(text, i) < 0)
				return false;
		}
		return true;
	}

	// Parse "1, #3" into {1, 3}; empty text means no limit (null).
	public static HashSet<BigInteger>? ParsePrNumbers(string? text)
	{
		HashSet<string> numbers = [.. (text ?? "").Split(',').Select(part => Py.Strip(part).TrimStart('#'))];
		numbers.Remove("");
		if (numbers.Count == 0)
			return null;
		if (!numbers.All(IsDigit))
			throw new PrNumbersError($"pr_numbers must be comma-separated PR numbers, got {Py.Repr(text ?? "")}");
		// int() reads a decimal digit only: a digit isdigit() takes that isn't one (²) is int()'s error.
		return [.. numbers.Select(n => Py.Int(n) ?? throw new PrNumbersError($"invalid literal for int() with base 10: {Py.Repr(n)}"))];
	}

	// The non-draft releases, once every PR in waitFor has one, or after the last attempt.
	public static List<JsonNode?> ListedReleasesOf(IReleaseGitHub gh, ISet<BigInteger>? waitFor, TextWriter log, int attempts = WaitAttempts, int delay = WaitDelay,
			Action<int>? sleep = null)
	{
		sleep ??= seconds => Thread.Sleep(TimeSpan.FromSeconds(seconds));
		for (var attempt = 1; ; attempt++)
		{
			var releases = gh.Releases();
			HashSet<BigInteger> named = [.. releases.Select(ReleasePost.SourcePrOf).OfType<BigInteger>()];
			var missing = (waitFor ?? new HashSet<BigInteger>()).Where(number => !named.Contains(number)).Order().ToList();
			if (missing.Count == 0)
				return releases;
			var prs = string.Join(", ", missing.Select(number => $"#{number}"));
			if (attempt >= attempts)
			{
				ReleasePost.Log(log, $"::warning::No Release is listed yet for PR {prs}; writing posts for the listed ones only.");
				return releases;
			}
			ReleasePost.Log(log, $"The Release for PR {prs} isn't listed yet; listing again in {delay}s.");
			sleep(delay);
		}
	}

	public static bool HasPost(string blogDir, BigInteger prNumber) => ReleasePost.PostsForPr(blogDir, prNumber.ToString(CultureInfo.InvariantCulture)).Count > 0;

	// The (pr, tag) pairs to write, oldest release first.
	public static List<(BigInteger Pr, string Tag)> SelectReleases(IEnumerable<JsonNode?> releases, string blogDir, TextWriter log, bool regenerate = false,
			ISet<BigInteger>? only = null)
	{
		List<(BigInteger Pr, string Tag)> selected = [];
		foreach (var release in releases)
		{
			if (ReleasePost.SourcePrOf(release) is not { } prNumber)
			{
				ReleasePost.Log(log, $"::notice::{ReleasePost.Str(ReleasePost.Get(release, "tag_name"))} has no 'Source PR: #n' line; skipping it.");
				continue;
			}
			if (only is not null && !only.Contains(prNumber))
				continue;
			if (regenerate || !HasPost(blogDir, prNumber))
				selected.Add((prNumber, ReleasePost.Text(ReleasePost.Get(release, "tag_name")) ?? throw new KeyNotFoundException("tag_name")));
		}
		// sorted() is stable, as OrderBy is.
		return [.. selected.OrderBy(pair => ReleasePost.VersionKey(pair.Tag))];
	}

	// Write the selected posts, then the tables once; return the PR numbers written.
	public static List<BigInteger> Run(string repository, IReleaseGitHub gh, string root = ".", bool regenerate = false, ISet<BigInteger>? only = null,
			string? apiKey = null, string model = ReleasePost.DefaultModel, UrlOpen? urlopen = null, ISet<BigInteger>? waitFor = null, Action<int>? sleep = null,
			TextWriter? log = null)
	{
		log ??= Console.Out;
		var blogDir = Path.Combine(root, "docs", "blogs");
		var listed = new ListedReleases(gh, ListedReleasesOf(gh, waitFor, log, sleep: sleep));
		var selected = SelectReleases(listed.Releases(), blogDir, log, regenerate, only);
		if (selected.Count == 0)
			ReleasePost.Log(log, "Every selected release already has a blog post; refreshing the tables only.");

		foreach (var (prNumber, tag) in selected)
			ReleasePost.WritePost(listed, (long)prNumber, tag, root, apiKey, model, urlopen, log);
		// Even with no post to write: a README-only PR merged with [skip-release] releases nothing, and this is what copies its README to
		// docs/README.md.
		ReleasePost.UpdateTables(repository, listed, root, log: log);
		return [.. selected.Select(pair => pair.Pr)];
	}

	// What Command hands Run: the inputs main() read, for a test to check.
	public sealed record Inputs(string Repository, bool Regenerate, ISet<BigInteger>? Only, string? ApiKey, string Model, ISet<BigInteger>? WaitFor);

	// backfill_blog_posts.py's main(): the command line, then Run with the gh CLI, the key and the model from the environment. Its log is
	// stdout. run and getenv stand in for Run and the environment in a test.
	public static int Command(IEnumerable<string> args, TextWriter stdout, TextWriter stderr, Action<Inputs>? run = null, Func<string, string?>? getenv = null)
	{
		getenv ??= Environment.GetEnvironmentVariable;
		var parser = new Arguments("backfill_blog_posts.cs", Usage, "Write release blog posts for past releases and refresh the release tables.")
				.Option("--regenerate", "'true' also rewrites posts that already exist (default: false)")
				.Option("--pr-numbers", "comma-separated source PR numbers to limit the run to")
				.Option("--wait-for-prs", "comma-separated PR numbers just released; list again until each has a Release")
				.Option("--repo", "owner/name (default: $REPOSITORY or $GITHUB_REPOSITORY)");
		if (parser.Parse(args, stdout, stderr) is int exit)
			return exit;
		var repository = parser.Has("--repo") ? parser.Value("--repo", "") : ReleasePost.DefaultRepository(getenv);
		if (string.IsNullOrEmpty(repository))
			return parser.Error("--repo is required when REPOSITORY and GITHUB_REPOSITORY are unset", stderr);
		HashSet<BigInteger>? only, waitFor;
		try
		{
			only = ParsePrNumbers(parser.Value("--pr-numbers", ""));
			waitFor = ParsePrNumbers(parser.Value("--wait-for-prs", ""));
		}
		catch (PrNumbersError error)
		{
			return parser.Error(error.Message, stderr);
		}

		Inputs inputs = new(repository, Py.Lower(Py.Strip(parser.Value("--regenerate", "false"))) == "true", only, ReleasePost.Setting("ANTHROPIC_API_KEY", getenv),
				ReleasePost.Setting("ANTHROPIC_MODEL", getenv) ?? ReleasePost.DefaultModel, waitFor);
		(run ?? (given => Run(given.Repository, new ReleaseGitHub(given.Repository), ".", given.Regenerate, given.Only, given.ApiKey, given.Model,
				waitFor: given.WaitFor, log: stdout)))(inputs);
		return 0;
	}
}
