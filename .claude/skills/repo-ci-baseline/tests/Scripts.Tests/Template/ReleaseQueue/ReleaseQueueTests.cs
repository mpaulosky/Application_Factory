using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseline.Tests;

// The Template's .github/scripts/ReleaseQueue.cs: test_release_queue.py's tests, one method each under its name (ADR 0017's rule), with the
// same cases and assertions, then tests of the port's own. The pytest file retired with the cut-over (mpaulosky/Application_Factory#128).
// main() is ReleaseQueue.Run, whose gh, contains and mainOrder stand in as the pytest's did; capsys is the writers it takes.
public sealed class ReleaseQueueTests
{
	static JsonObject Pull(long number, string? mergedAt, string? title = null, string? sha = null, string? updatedAt = null, string? author = "mpaulosky") => new()
	{
		["number"] = number,
		["title"] = title ?? $"feat: PR {number}",
		["merged_at"] = mergedAt,
		["updated_at"] = updatedAt ?? mergedAt ?? "2026-09-29T00:00:00Z",
		["merge_commit_sha"] = sha ?? $"sha{number}",
		["author"] = author,
	};

	static JsonObject Release(string tag, long? pr) => new() { ["tag_name"] = tag, ["body"] = $"Source PR: #{(pr is null ? "None" : pr)}\n" };

	static bool NeverContained(string sha) => false;

	static HashSet<BigInteger> Set(params long[] numbers) => [.. numbers.Select(n => (BigInteger)n)];

	static List<BigInteger> List(params long[] numbers) => [.. numbers.Select(n => (BigInteger)n)];

	// Positions on main in merge-time order, as a fresh fetch of main would give them.
	static Dictionary<string, int> OrderOf(IEnumerable<JsonObject> pulls) =>
			pulls.Where(pr => (string?)pr["merged_at"] is not null).OrderBy(pr => (string)pr["merged_at"]!, StringComparer.Ordinal)
					.Select((pr, position) => ((string)pr["merge_commit_sha"]!, position)).ToDictionary();

	static List<BigInteger> Queue(IEnumerable<JsonObject> pulls, HashSet<BigInteger> released, Func<string, bool> inCutoff, bool hasCutoff, long? trigger = null,
			JsonObject? triggerPull = null, Dictionary<string, int>? mainOrder = null) =>
			ReleaseQueue.Queue(pulls, released, inCutoff, hasCutoff, trigger, triggerPull, mainOrder);

	// queue()

	[Fact]
	public void test_unreleased_prs_are_queued_oldest_merge_first()
	{
		JsonObject[] pulls = [Pull(12, "2026-09-29T10:02:00Z"), Pull(10, "2026-09-29T10:00:00Z"), Pull(11, "2026-09-29T10:01:00Z")];

		Assert.Equal(List(10, 11, 12), Queue(pulls, [], NeverContained, true, mainOrder: OrderOf(pulls)));
	}

	[Fact]
	public void test_released_and_skip_release_prs_are_left_out()
	{
		JsonObject[] pulls =
		[
			Pull(10, "2026-09-29T10:00:00Z"),
			Pull(11, "2026-09-29T10:01:00Z", title: "docs: Add release blog for PR #10 [skip-release]"),
			Pull(12, "2026-09-29T10:02:00Z"),
		];

		Assert.Equal(List(12), Queue(pulls, Set(10), NeverContained, true, mainOrder: OrderOf(pulls)));
	}

	[Fact]
	public void test_a_dependabot_merge_is_folded_into_the_next_release()
	{
		// A dependency bump gets no Release of its own: the next PR's Release tags a later commit, so it contains the bump, and its generated
		// notes list the bump's PR.
		var bump = Pull(30, "2026-09-29T09:00:00Z", title: "chore(deps): bump the all-nuget group with 3 updates", author: "dependabot[bot]");
		var trigger = Pull(31, "2026-09-29T10:00:00Z");
		JsonObject[] pulls = [trigger, bump];

		var queued = Queue(pulls, [], NeverContained, true, trigger: 31, triggerPull: trigger, mainOrder: OrderOf(pulls));

		Assert.Equal(List(31), queued);
	}

	[Fact]
	public void test_a_dependabot_pr_fetched_on_its_own_is_folded_in_too()
	{
		// A manual run names a PR fetched with its "user", not a listed "author".
		var bump = Pull(30, "2026-09-29T09:00:00Z");
		bump.Remove("author");
		bump["user"] = new JsonObject { ["login"] = "dependabot[bot]" };

		var queued = Queue([], [], NeverContained, true, trigger: 30, triggerPull: bump, mainOrder: OrderOf([bump]));

		Assert.Empty(queued);
	}

	[Fact]
	public void test_prs_already_inside_the_cutoff_release_are_left_out()
	{
		// PRs merged before the newest Release are history, even without a Release of their own (for example, merged before release automation
		// existed).
		JsonObject[] pulls = [Pull(1, "2026-01-01T00:00:00Z"), Pull(20, "2026-09-29T10:00:00Z")];

		var queued = Queue(pulls, [], sha => sha == "sha1", true, mainOrder: OrderOf(pulls));

		Assert.Equal(List(20), queued);
	}

	[Fact]
	public void test_without_any_release_only_the_triggering_pr_is_queued()
	{
		JsonObject[] pulls = [Pull(1, "2026-01-01T00:00:00Z"), Pull(2, "2026-01-02T00:00:00Z")];

		var queued = Queue(pulls, [], NeverContained, false, trigger: 2, mainOrder: OrderOf(pulls));

		Assert.Equal(List(2), queued);
	}

	[Fact]
	public void test_the_triggering_pr_is_queued_even_when_the_listing_missed_it()
	{
		JsonObject[] pulls = [Pull(10, "2026-09-29T10:00:00Z")];
		var trigger = Pull(11, "2026-09-29T10:01:00Z");

		var queued = Queue(pulls, [], NeverContained, true, trigger: 11, triggerPull: trigger, mainOrder: OrderOf([.. pulls, trigger]));

		Assert.Equal(List(10, 11), queued);
	}

	[Fact]
	public void test_a_released_triggering_pr_is_not_queued_again()
	{
		JsonObject[] pulls = [Pull(10, "2026-09-29T10:00:00Z")];

		Assert.Empty(Queue(pulls, Set(10), NeverContained, true, trigger: 10, mainOrder: OrderOf(pulls)));
	}

	[Fact]
	public void test_an_older_owed_pr_goes_ahead_of_a_manually_triggered_newer_one()
	{
		// A manual run for a newer PR must not let it take a version ahead of an older PR still owed one.
		JsonObject[] pulls = [Pull(10, "2026-09-29T10:00:00Z"), Pull(11, "2026-09-29T10:01:00Z")];

		Assert.Equal(List(10, 11), Queue(pulls, [], NeverContained, true, trigger: 11, mainOrder: OrderOf(pulls)));
	}

	[Fact]
	public void test_unmerged_prs_are_left_out()
	{
		JsonObject[] pulls = [Pull(10, null), Pull(11, "2026-09-29T10:01:00Z")];

		Assert.Equal(List(11), Queue(pulls, [], NeverContained, true, mainOrder: OrderOf(pulls)));
	}

	[Fact]
	public void test_prs_merged_in_the_same_second_follow_their_order_on_main()
	{
		// merged_at has one-second resolution; main's history decides a tie.
		JsonObject[] pulls = [Pull(21, "2026-09-29T10:00:00Z", sha: "b"), Pull(20, "2026-09-29T10:00:00Z", sha: "a")];
		Dictionary<string, int> order = new() { ["b"] = 0, ["a"] = 1 }; // b merged first

		var queued = Queue(pulls, [], NeverContained, true, mainOrder: order);

		Assert.Equal(List(21, 20), queued);
	}

	[Fact]
	public void test_a_listed_merge_missing_from_main_stops_the_run_instead_of_guessing_its_place()
	{
		JsonObject[] pulls = [Pull(30, "2026-09-29T10:00:00Z", sha: "known"), Pull(31, "2026-09-29T10:00:00Z", sha: "missing")];

		var error = Assert.Throws<UnknownMergeOrder>(() => Queue(pulls, [], NeverContained, true, mainOrder: new() { ["known"] = 0 }));
		Assert.Contains("#31", error.Message);
	}

	// cutoff_release()

	[Fact]
	public void test_the_cutoff_is_the_newest_published_release_by_version()
	{
		JsonObject[] releases = [Release("v0.0.9", 9), Release("v0.0.10", 10), Release("v0.0.1-1170", null)];

		Assert.Equal(("v0.0.10", (BigInteger?)10), ReleaseQueue.CutoffRelease(releases));
	}

	[Fact]
	public void test_there_is_no_cutoff_without_a_release()
	{
		Assert.Equal(((string?)null, (BigInteger?)null), ReleaseQueue.CutoffRelease([Release("v0.0.1-1170", null)]));
	}

	// GitHub.merged_pulls()

	// MergedPulls() over canned pages instead of the API.
	sealed class PagedGitHub(List<List<JsonObject>> pages) : QueueGitHub("octo/demo")
	{
		public readonly List<int> Fetched = [];

		protected override List<JsonNode?> ClosedPullsPage(int page)
		{
			Fetched.Add(page);
			return page <= pages.Count ? [.. pages[page - 1].Select(pr => pr.DeepClone())] : [];
		}
	}

	static List<long> Numbers(IEnumerable<JsonNode?> pulls) => [.. pulls.Select(pr => (long)pr!["number"]!)];

	[Fact]
	public void test_merged_pulls_pages_back_to_the_boundary_and_stops()
	{
		List<List<JsonObject>> pages =
		[
			[Pull(30, "2026-09-29T12:00:00Z"), Pull(29, null, updatedAt: "2026-09-29T11:00:00Z")],
			[Pull(28, "2026-09-29T10:00:00Z"), Pull(5, "2026-01-01T00:00:00Z")],
			[Pull(4, "2025-12-01T00:00:00Z")],
		];
		var gh = new PagedGitHub(pages);

		var numbers = Numbers(gh.MergedPulls("2026-09-29T09:00:00Z"));

		Assert.Equal([30, 28], numbers);
		Assert.Equal([1, 2], gh.Fetched);
	}

	[Fact]
	public void test_merged_pulls_keeps_each_prs_author()
	{
		// The API names the author under "user"; the queue reads it as "author".
		var listed = Pull(30, "2026-09-29T12:00:00Z");
		listed["user"] = new JsonObject { ["login"] = "dependabot[bot]" };
		var gh = new PagedGitHub([[listed]]);

		var pr = Assert.Single(gh.MergedPulls("2026-09-29T09:00:00Z"));

		Assert.Equal("dependabot[bot]", (string?)pr!["author"]);
	}

	[Fact]
	public void test_merged_pulls_keeps_paging_while_every_pr_is_newer_than_the_boundary()
	{
		List<List<JsonObject>> pages = [.. new long[] { 40, 39, 38 }.Select(n => new List<JsonObject> { Pull(n, "2026-09-29T12:00:00Z") })];
		var gh = new PagedGitHub(pages);

		var numbers = Numbers(gh.MergedPulls("2026-09-29T09:00:00Z"));

		Assert.Equal([40, 39, 38], numbers);
		Assert.Equal([1, 2, 3, 4], gh.Fetched);
	}

	// main()

	sealed class FakeGitHub(List<JsonObject> pulls, List<JsonObject> releases) : IQueueGitHub
	{
		public string? Since;
		public Dictionary<long, JsonObject> Extra = [];

		public List<JsonNode?> MergedPulls(string? since)
		{
			Since = since;
			return [.. pulls.Select(pr => pr.DeepClone())];
		}

		public JsonNode? Pull(long number)
		{
			if (Extra.TryGetValue(number, out var extra))
				return extra.DeepClone();
			var pull = (JsonObject)pulls.First(pr => (long)pr["number"]! == number).DeepClone();
			pull["base"] = new JsonObject { ["ref"] = "main" };
			return pull;
		}

		public string PullTitle(BigInteger number) => throw new NotSupportedException();

		public List<JsonNode?> Commits(long number) => throw new NotSupportedException();

		public List<JsonNode?> Files(long number) => throw new NotSupportedException();

		public List<JsonNode?> Releases() => [.. releases.Select(release => release.DeepClone())];
	}

	// main(argv, gh=gh, contains=contains, main_order=main_order): its exit code, stdout and stderr.
	static (int Code, string Out, string Err) CallMain(string[] argv, FakeGitHub gh, Func<string, string, bool> contains, Dictionary<string, int> mainOrder)
	{
		StringWriter stdout = new(), stderr = new();
		var code = ReleaseQueue.Run(argv, stdout, stderr, _ => gh, contains, mainOrder);
		return (code, stdout.ToString(), stderr.ToString());
	}

	static List<long> Json(string output) => JsonSerializer.Deserialize<List<long>>(output)!;

	[Fact]
	public void test_main_prints_the_queue_as_json_and_lists_merges_since_the_cutoff_pr()
	{
		FakeGitHub gh = new([Pull(9, "2026-09-29T09:00:00Z"), Pull(10, "2026-09-29T10:00:00Z"), Pull(11, "2026-09-29T10:01:00Z")], [Release("v0.0.9", 9)]);

		var (_, output, _) = CallMain(["--repo", "octo/demo", "--pr", "11"], gh, (tag, sha) => sha == "sha9", new() { ["sha9"] = 0, ["sha10"] = 1, ["sha11"] = 2 });

		Assert.Equal([10, 11], Json(output));
		Assert.Equal("2026-09-29T09:00:00Z", gh.Since);
	}

	[Fact]
	public void test_main_skips_a_manually_named_pr_merged_into_another_branch()
	{
		// A manual run can name any PR. One merged elsewhere is never owed a release from main, so it's left out instead of failing the run on
		// a merge commit main doesn't have.
		var other = Pull(12, "2026-09-29T10:05:00Z");
		other["base"] = new JsonObject { ["ref"] = "develop" };
		FakeGitHub gh = new([Pull(9, "2026-09-29T09:00:00Z"), Pull(10, "2026-09-29T10:00:00Z")], [Release("v0.0.9", 9)]) { Extra = new() { [12] = other } };

		var (_, output, _) = CallMain(["--repo", "octo/demo", "--pr", "12"], gh, (tag, sha) => sha == "sha9", new() { ["sha9"] = 0, ["sha10"] = 1 });

		Assert.Equal([10], Json(output));
	}

	[Fact]
	public void test_main_ignores_a_tag_whose_release_was_never_published()
	{
		// PR 10 got its tag, but the run failed before the Release: it's still owed one, so the cutoff stays at the last published Release (PR 9).
		FakeGitHub gh = new([Pull(9, "2026-09-29T09:00:00Z"), Pull(10, "2026-09-29T10:00:00Z")], [Release("v0.0.9", 9)]);
		List<string> checkedTags = [];

		bool Contains(string tag, string sha)
		{
			checkedTags.Add(tag);
			return sha == "sha9";
		}

		var (_, output, _) = CallMain(["--repo", "octo/demo"], gh, Contains, new() { ["sha9"] = 0, ["sha10"] = 1 });

		Assert.Equal([10], Json(output));
		Assert.Equal(["v0.0.9"], checkedTags.Distinct());
	}

	// The port's own.

	[Fact]
	public void The_queue_and_the_cutoff_go_to_stderr_and_only_the_json_to_stdout()
	{
		FakeGitHub gh = new([Pull(9, "2026-09-29T09:00:00Z"), Pull(10, "2026-09-29T10:00:00Z"), Pull(11, "2026-09-29T10:01:00Z")], [Release("v0.0.9", 9)]);

		var (code, output, err) = CallMain(["--repo", "octo/demo"], gh, (tag, sha) => sha == "sha9", new() { ["sha9"] = 0, ["sha10"] = 1, ["sha11"] = 2 });

		Assert.Equal(0, code);
		// json.dumps() spacing.
		Assert.Equal("[10, 11]\n", output);
		Assert.Equal("Newest published Release: v0.0.9 (PR #9). Queue: [10, 11].\n", err);
	}

	[Fact]
	public void An_empty_queue_with_no_release_says_so()
	{
		FakeGitHub gh = new([], []);

		var (code, output, err) = CallMain(["--repo", "octo/demo"], gh, (tag, sha) => throw new InvalidOperationException("no cutoff to look in"), []);

		Assert.Equal(0, code);
		Assert.Equal("[]\n", output);
		Assert.Equal("Newest published Release: none. Queue: empty.\n", err);
		Assert.Null(gh.Since);
	}

	[Fact]
	public void A_release_number_past_64_bits_is_read_as_python_reads_it()
	{
		// source_pr_of() reads any run of digits, and int() has no limit.
		var huge = BigInteger.Parse("123456789012345678901234567890", System.Globalization.CultureInfo.InvariantCulture);
		JsonObject release = new() { ["tag_name"] = "v1.0.0", ["body"] = $"Source PR: #{huge}\n" };

		Assert.Equal(("v1.0.0", (BigInteger?)huge), ReleaseQueue.CutoffRelease([release]));
	}

	[Theory]
	[InlineData("v1.2.3", true)]
	[InlineData("v1.2", false)]
	[InlineData("1.2.3", false)]
	[InlineData("v1.2.3-rc1", false)]
	[InlineData("v1.2.3\n", false)]
	[InlineData("v\U0001D7CF.\u0661.0", true)]
	[InlineData("v1..2", false)]
	[InlineData("V1.2.3", false)]
	public void A_cutoff_tag_is_vmajor_minor_patch_and_nothing_more(string tag, bool cutoff)
	{
		// re.fullmatch(r"v\d+\.\d+\.\d+"): Python's \d reads astral and Arabic-Indic digits too.
		JsonObject release = new() { ["tag_name"] = tag, ["body"] = "Source PR: #4\n" };

		Assert.Equal(cutoff, ReleaseQueue.CutoffRelease([release]).Tag is not null);
	}

	[Fact]
	public void Of_two_releases_of_one_version_the_first_listed_is_the_cutoff()
	{
		// max() keeps the first of equal keys.
		Assert.Equal(("v1.0.0", (BigInteger?)1), ReleaseQueue.CutoffRelease([Release("v1.0.0", 1), Release("v1.0.0", 2)]));
	}

	[Fact]
	public void A_merge_commit_that_is_missing_counts_as_missing_from_main()
	{
		var pull = Pull(40, "2026-09-29T10:00:00Z");
		pull["merge_commit_sha"] = null;

		var error = Assert.Throws<UnknownMergeOrder>(() => Queue([pull], [], NeverContained, true, mainOrder: []));
		Assert.Equal("merge commits of #40 aren't on the fetched main; retry the run", error.Message);
	}

	[Fact]
	public void A_later_listing_of_the_same_pr_is_the_one_read()
	{
		// A dict keyed by number keeps the later value. Here the later one has [skip-release], so the PR is out.
		JsonObject[] pulls = [Pull(10, "2026-09-29T10:00:00Z"), Pull(10, "2026-09-29T10:00:00Z", title: "x [skip-release]"), Pull(11, "2026-09-29T10:01:00Z")];

		Assert.Equal(List(11), Queue(pulls, [], NeverContained, true, mainOrder: new() { ["sha10"] = 0, ["sha11"] = 1 }));
	}

	[Theory]
	[InlineData("--pr", "five", "argument --pr: invalid int value: 'five'")]
	[InlineData("--bogus", null, "unrecognized arguments: --bogus")]
	public void A_usage_error_exits_2_with_argparses_message(string option, string? value, string message)
	{
		StringWriter stdout = new(), stderr = new();
		string[] argv = value is null ? ["--repo", "o/app", option] : ["--repo", "o/app", option, value];

		var code = ReleaseQueue.Run(argv, stdout, stderr, _ => throw new InvalidOperationException("no gh call on a usage error"));

		Assert.Equal(2, code);
		Assert.Equal("", stdout.ToString());
		Assert.Equal($"usage: release_queue.cs {ReleaseQueue.Usage}\nrelease_queue.cs: error: {message}\n", stderr.ToString());
	}
}
