using System.Text.Json;
using System.Text.Json.Nodes;

// What the port changes on purpose (ADR 0017's Update for mpaulosky/Application_Factory#119): not moved tests, so StatusTests.cs keeps
// the moved pytest suite's, and its count can be checked against that suite's in git history.
public partial class StatusTests
{
	// Not a moved test: a fork's PR names its own branch, so a release-blog branch counts only on a PR from the repo itself.
	[Fact]
	public void A_forks_pr_on_a_release_blog_branch_is_no_release_blog_pr()
	{
		var blog = With(PrNode(), ("headRefName", "docs/backfill-blog-posts"));
		Assert.True(ToState(blog, Status.RepoFlags("full")).ReleaseBlog);
		Assert.False(ToState(With(blog, ("isCrossRepository", true)), Status.RepoFlags("full")).ReleaseBlog);
		Assert.False(ToState(With(blog, ("isCrossRepository", null)), Status.RepoFlags("full")).ReleaseBlog);
		Assert.False(ToState(With(blog, ("isCrossRepository", "false")), Status.RepoFlags("full")).ReleaseBlog);
		var missing = With(blog);
		missing.Remove("isCrossRepository");
		Assert.False(ToState(missing, Status.RepoFlags("full")).ReleaseBlog);
		Assert.Contains("isCrossRepository", Status.Query, StringComparison.Ordinal);
	}

	// Not a moved test: the title is PR-controlled, so what the terminal would act on is escaped, and the title is cut after that.
	[Fact]
	public void A_titles_control_and_format_characters_are_escaped_before_it_is_cut()
	{
		var (code, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(PrNode(title: "a\u001b[31mb\u202ec\td\ne")) });
		Assert.Equal(0, code);
		In(@"a\x1b[31mb\u202ec\td\ne", output);
		NotIn("\u001b", output);
		NotIn("\u202e", output);
		Assert.Single(output.TrimEnd('\n').Split('\n'));
		// A title of control characters past the width is cut by what it prints as.
		var (_, cut, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(PrNode(title: new string('\u0007', 20))) });
		var printed = string.Concat(Enumerable.Repeat(@"\x07", 20));
		Assert.True(printed.Length > Status.TitleWidth);
		In(Status.Shorten(printed), cut);
	}

	// Not a moved test: a repo that isn't owner/repo is a usage error before any gh call, since gh -F reads a value starting with @
	// from a file.
	[Theory]
	[InlineData("@/etc/passwd/x")]
	[InlineData("owner/app\n")]
	[InlineData("owner/app/x")]
	[InlineData("owner")]
	[InlineData("/app")]
	[InlineData("owner/")]
	[InlineData("own er/app")]
	[InlineData("../..")]
	[InlineData("./x")]
	[InlineData("x/..")]
	[InlineData("x/.")]
	[InlineData("..//x")]
	[InlineData("o/\u00e9")]
	[InlineData("\u0661/x")]
	public void A_repo_that_isnt_owner_repo_is_a_usage_error_before_any_call(string repo)
	{
		var fetched = 0;
		using var output = new StringWriter();
		using var err = new StringWriter();
		var code = Status.Run(["owner/app", repo], output, err, (_, _) => { fetched++; return Repo(); }, FleetList);
		Assert.Equal(2, code);
		Assert.Equal(0, fetched);
		In($"status.cs: error: not owner/repo: {repo}", err.ToString());
		Assert.Throws<ArgumentException>(() => Status.SplitRepo(repo));
		Assert.Equal(("o.x", "a_b-c"), Status.SplitRepo("o.x/a_b-c"));
	}

	// Names a dot starts or ends, but that are no path step, and GitHub's other characters, are owner/repo.
	[Theory]
	[InlineData("a/b")]
	[InlineData("-a/-b")]
	[InlineData("..x/x..")]
	[InlineData(".x/x.")]
	[InlineData("...//...", false)]
	[InlineData(".../...")]
	[InlineData("A_9/z-.")]
	public void A_repo_name_of_githubs_characters_is_owner_repo(string repo, bool valid = true) =>
		Assert.Equal(valid, Status.RepoName.IsMatch(repo));

	// Not a moved test: a bad name in the Fleet registry ends status with exit 1 before any call, not a usage error.
	[Fact]
	public void A_fleet_registry_name_that_isnt_owner_repo_ends_the_run_before_any_call()
	{
		var fetched = 0;
		using var output = new StringWriter();
		using var err = new StringWriter();
		var code = Status.Run([], output, err, (_, _) => { fetched++; return Repo(); }, () => new() { ["owner/app"] = "full", ["x/.."] = "full" });

		Assert.Equal(1, code);
		Assert.Equal(0, fetched);
		Assert.Equal("repos.txt: not owner/repo: x/..\n", err.ToString());
	}

	// Not a moved test: an answer gh gave that isn't an object (the Python's .get raised on it) leaves the permission unread, and
	// isn't kept.
	[Theory]
	[InlineData("null")]
	[InlineData("[]")]
	[InlineData("\"write\"")]
	public void A_permission_answer_that_isnt_an_object_is_unread_and_not_kept(string answer)
	{
		var gh = new FakeGh(stdout: answer, code: 0);
		var reader = LookupWith(gh);

		Assert.True(reader.WriterAccess("owner", "app", User) is null);
		Assert.True(reader.WriterAccess("owner", "app", User) is null && gh.Calls == 2);
		// An object without the key is still a definite no.
		Assert.True(LookupWith(new FakeGh(stdout: "{}", code: 0)).WriterAccess("owner", "app", User) is false);
	}

	// Not a moved test: a list the Python indexed (["nodes"]) that's missing, null or not a list fails the read, as its KeyError or
	// TypeError did, rather than read as empty: no open PRs, no checks, no reviews or no open threads on an answer that held none.
	[Theory]
	[InlineData("{}")]
	[InlineData("""{"nodes": null}""")]
	[InlineData("""{"nodes": {}}""")]
	public void A_repo_answer_without_its_pr_list_makes_the_repo_unreadable(string pullRequests)
	{
		var reader = new StatusReader(err: new StringWriter()) { Graphql = (_, _, _) => P($$$"""{"repository": {"pullRequests": {{{pullRequests}}}}}""") };
		using var output = new StringWriter();
		using var err = new StringWriter();

		Assert.Equal(1, Status.Run(["owner/app"], output, err, reader.Fetch, FleetList));
		Assert.StartsWith("owner/app: ", err.ToString(), StringComparison.Ordinal);
		Assert.Equal("", output.ToString());
	}

	[Theory]
	[InlineData("commits", "{}")]
	[InlineData("commits", """{"nodes": null}""")]
	[InlineData("reviews", """{"nodes": null}""")]
	[InlineData("reviewThreads", "{}")]
	[InlineData("reviewThreads", """{"nodes": "x"}""")]
	public void A_pr_whose_list_is_missing_isnt_decided_on(string key, string value)
	{
		var node = PrNode(contexts: Green);
		node[key] = P(value);

		Assert.Contains("'nodes'", Assert.ThrowsAny<Exception>(() => StateOf(node)).Message, StringComparison.Ordinal);
		Assert.Contains("'nodes'", Assert.ThrowsAny<Exception>(() => Run(["owner/app"], new() { ["owner/app"] = Repo(node) })).Message, StringComparison.Ordinal);
	}

	// Not a moved test: Status.Run reads the repo's PR list again after the fetch, outside its per-repo try, so a fetch that hands back
	// an answer without it ends the run (exit 1 through Script.Run) rather than print that the repo has no open PRs.
	[Theory]
	[InlineData("{}")]
	[InlineData("""{"nodes": null}""")]
	public void Run_refuses_a_fetched_answer_without_its_pr_list(string pullRequests)
	{
		using var output = new StringWriter();
		using var err = new StringWriter();
		var error = Assert.ThrowsAny<Exception>(() =>
				Status.Run(["owner/app"], output, err, (_, _) => P($$$"""{"repository": {"pullRequests": {{{pullRequests}}}}}"""), FleetList));

		Assert.Contains("'nodes'", error.Message, StringComparison.Ordinal);
		Assert.Equal("", output.ToString());
	}

	// Not a moved test: whether a bot reviewed the PR is read strictly too, so a reviews list that isn't one isn't "no bot review".
	[Theory]
	[InlineData("{}")]
	[InlineData("""{"nodes": null}""")]
	public void Bot_reviewed_refuses_a_reviews_answer_without_its_list(string reviews)
	{
		var node = PrNode();
		node["reviews"] = P(reviews);

		Assert.Contains("'nodes'", Assert.ThrowsAny<Exception>(() => Status.BotReviewed(node)).Message, StringComparison.Ordinal);
	}

	[Fact]
	public void A_check_rollup_without_its_contexts_list_isnt_decided_on()
	{
		var node = PrNode(contexts: Green);
		((JsonObject)J.Get(J.Get(J.NodesOf(J.Req(node, "commits"))[0], "commit"), "statusCheckRollup")!)["contexts"] = new JsonObject();

		Assert.Contains("'nodes'", Assert.ThrowsAny<Exception>(() => StateOf(node)).Message, StringComparison.Ordinal);
	}

	// Not a moved test: the fetch reads a PR's thread list strictly, so a null one makes the repo unreadable.
	[Fact]
	public void Fetch_refuses_a_pr_whose_thread_list_is_null()
	{
		var node = PrNode();
		node["reviewThreads"] = new JsonObject { ["nodes"] = null };
		var reader = new StatusReader(err: new StringWriter()) { Graphql = (_, _, _) => Repo(node) };

		Assert.Contains("'nodes' is not a list", Assert.ThrowsAny<Exception>(() => reader.Fetch("owner", "app")).Message, StringComparison.Ordinal);
	}

	// Not a moved test: the threads query only adds to what the first read gave (a notice), so an answer whose list isn't one keeps the
	// first read's threads and is reported, and the run still prints the PR's line.
	[Theory]
	[InlineData("""{"node": {"reviewThreads": {"nodes": null}}}""")]
	[InlineData("""{"node": {"reviewThreads": {}}}""")]
	public void A_threads_answer_without_its_list_keeps_the_first_reads_threads(string answer)
	{
		Rev[] reviewed = [new("copilot-pull-request-reviewer", Head)];
		var done = With(PrNode(number: 1, reviews: reviewed, threads: [true, true]), ("id", "PR_done"));
		var err = new StringWriter();
		var reader = new StatusReader(err: err)
		{
			Graphql = (query, _, _) =>
				query == Status.ThreadsQuery ? P(answer)
				: query == Status.CommentsQuery ? new JsonObject { ["node"] = new JsonObject { ["comments"] = CommentsOf() } }
				: query == Status.EventsQuery ? P("""{"node": {"timelineItems": {"pageInfo": {"hasNextPage": false, "endCursor": null}, "nodes": []}}}""")
				: Repo(done),
		};
		using var output = new StringWriter();
		using var runErr = new StringWriter();

		Assert.Equal(0, Status.Run(["owner/app"], output, runErr, reader.Fetch, FleetList));
		Assert.StartsWith("app#1 ", output.ToString(), StringComparison.Ordinal);
		Assert.Contains("owner/app#1: thread comments unreadable, no notice", err.ToString(), StringComparison.Ordinal);
	}

	// Not a moved test: a check's name is the PR's (a fork's workflow names its jobs) or a writer's (a status context), so what the
	// terminal would act on is escaped in every reason that names it.
	[Fact]
	public void A_checks_control_and_format_characters_are_escaped()
	{
		var named = "a\u001b[31mb\u202ec";
		var failed = P($$"""{"__typename": "CheckRun", "name": {{JsonSerializer.Serialize(named)}}, "status": "COMPLETED", "conclusion": "FAILURE", "startedAt": "2026-10-07T10:00:00Z"}""");
		var context = P($$"""{"__typename": "StatusContext", "context": {{JsonSerializer.Serialize(named)}}, "state": "PENDING", "createdAt": "2026-10-07T10:00:00Z"}""");
		Assert.Equal("a\\x1b[31mb\\u202ec", Status.ToCheck(failed).Name);
		Assert.Equal("a\\x1b[31mb\\u202ec", Status.ToCheck(context).Name);

		var (code, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(PrNode(contexts: [failed])) });
		Assert.Equal(0, code);
		In("failed: a\\x1b[31mb\\u202ec", output);
		NotIn("\u001b", output);
		NotIn("\u202e", output);
	}
}
