using System.Text.Json;
using System.Text.Json.Nodes;

// Tests for github/StatusCore.cs (moved from the pytest suite it replaced): the GraphQL fetch replaced by fixtures (StatusFixtures.cs). No GitHub.
public partial class StatusTests
{
	[Fact]
	public void One_line_per_pr_with_the_next_action()
	{
		var node = PrNode(reviews: [new("copilot-pull-request-reviewer", Head)], threads: [true, false],
				contexts: [P("""{"__typename": "CheckRun", "name": "Test Suite", "status": "COMPLETED", "conclusion": "CANCELLED", "startedAt": "2026-10-07T10:00:00Z"}"""),
						P("""{"__typename": "CheckRun", "name": "Test Suite", "status": "IN_PROGRESS", "conclusion": null, "startedAt": "2026-10-07T10:02:00Z"}"""),
						P("""{"__typename": "StatusContext", "context": "codecov", "state": "FAILURE", "createdAt": "2026-10-07T10:01:00Z"}""")]);
		var (code, output, err) = Run(["owner/app"], new() { ["owner/app"] = Repo(node) });
		Assert.True(code == 0 && err == "");
		Assert.Equal(1, Count(output, '\n'));
		Assert.StartsWith("app#7 feat: Something", output, StringComparison.Ordinal);
		foreach (var part in new[] { "1234567", "ready", "CLEAN", "review:copilot", "threads:1", "automerge:off", "running:1", "failed:1", "cancelled:0", "-> report blocker (failed: codecov)" })
			In(part, output);
	}

	[Fact]
	public void A_long_title_is_shortened()
	{
		var (_, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(PrNode(title: new string('x', 80))) });
		In(new string('x', Status.TitleWidth - 1) + "\u2026", output);
		NotIn(new string('x', Status.TitleWidth), output);
	}

	[Fact]
	public void No_open_pr_is_one_clear_line()
	{
		var (code, output, _) = Run(["owner/app", "owner/lib"], new() { ["owner/app"] = Repo(), ["owner/lib"] = Repo() });
		Assert.True(code == 0 && output == "No open PRs in 2 repo(s).\n", output);
	}

	[Fact]
	public void An_unreadable_repo_is_reported_and_the_rest_still_print()
	{
		JsonNode Fetch(string owner, string name)
		{
			if (name == "gone")
				throw RuntimeError("Could not resolve to a Repository");
			return Repo(PrNode(draft: true));
		}

		using var output = new StringWriter();
		using var err = new StringWriter();
		var code = Status.Run(["owner/gone", "owner/app"], output, err, Fetch, FleetList);
		Assert.True(code == 1);
		In("owner/gone: Could not resolve", err.ToString());
		In("app#7", output.ToString());
		In("-> wait (draft)", output.ToString());
	}

	[Fact]
	public void Dotfiles_leaves_a_reviewed_pr_to_its_pr_auto_merge_like_a_baseline_repo()
	{
		var reviewed = PrNode(reviews: [new("Copilot", Head)]);
		var (_, output, _) = Run([], new() { ["owner/app"] = Repo(reviewed), ["owner/dots"] = Repo(reviewed) },
				() => new() { ["owner/app"] = "full", ["owner/dots"] = "repo-settings-only" });
		var lines = Lines(output);
		Assert.Equal(2, lines.Length);
		var (app, dots) = (lines[0], lines[1]);
		In("-> leave to PR Auto-Merge", app);
		In("-> leave to PR Auto-Merge", dots);
	}

	[Fact]
	public void Copilot_requests_and_the_review_claude_label_are_read()
	{
		var node = With(PrNode(), ("labels", P("""{"nodes": [{"name": "docs-only"}, {"name": "review:claude"}]}""")),
				("reviewRequests", P("""{"nodes": [{"requestedReviewer": {"login": "someone"}}, {"requestedReviewer": {"login": "Copilot"}}]}""")));
		var pr = ToState(node, Status.RepoFlags("full"));
		Assert.True(pr.CopilotRequested && pr.ClaudeRequested);
		// A person or a team requested isn't Copilot; an empty requestedReviewer (a deleted account) is nothing.
		var other = With(PrNode(), ("labels", P("""{"nodes": []}""")),
				("reviewRequests", P("""{"nodes": [{"requestedReviewer": {"name": "core-team"}}, {"requestedReviewer": null}]}""")));
		pr = ToState(other, Status.RepoFlags("full"));
		Assert.True(!pr.CopilotRequested && !pr.ClaudeRequested);
		// A node without either field, as older fixtures have, reads as neither.
		Assert.False(ToState(PrNode(), Status.RepoFlags("full")).CopilotRequested);
	}

	[Fact]
	public void A_one_shot_read_shows_the_copilot_step_for_an_unreviewed_head()
	{
		// It can't know an earlier request for the head was dropped, so it never says Claude.
		var green = new[] { P("""{"__typename": "CheckRun", "name": "Test Suite", "status": "COMPLETED", "conclusion": "SUCCESS", "startedAt": "2026-10-07T10:00:00Z"}""") };
		var (_, output, _) = Run([], new() { ["owner/app"] = Repo(PrNode(contexts: green)) }, () => new() { ["owner/app"] = "full" });
		In("-> request Copilot", output);
	}

	[Fact]
	public void A_reapply_pr_reaches_the_review_cap_like_any_other()
	{
		// #174: a re-Apply PR is no longer exempt from PR Auto-Merge's cap.
		var node = With(PrNode(), ("headRefName", "chore/reapply-baseline"));
		// PrState's Equals compares its sets and arrays by reference; Python's == compares values, as the JSON does.
		Assert.Equal(JsonSerializer.Serialize(ToState(node, Status.RepoFlags("full"))),
				JsonSerializer.Serialize(ToState(With(node, ("headRefName", "feat/x")), Status.RepoFlags("full"))));
	}

	[Fact]
	public void A_release_blog_pr_is_told_apart_by_its_branch()
	{
		var blog = ToState(With(PrNode(), ("headRefName", "docs/release-notes")), Status.RepoFlags("full"));
		var other = ToState(With(PrNode(), ("headRefName", "feat/x")), Status.RepoFlags("full"));
		Assert.True(blog.ReleaseBlog && !other.ReleaseBlog);
		Assert.True(blog.MergedByWorkflow && blog.ChecksRequired);
		Assert.Equal((true, true), Status.RepoFlags("repo-settings-only"));
	}

	[Fact]
	public void The_review_column_names_the_reviewer_of_the_head()
	{
		var claude = PrNode(reviews: [new("copilot-pull-request-reviewer", Head, "Copilot was unable to review this pull request because the quota is used up."),
				new("github-actions", Head, "<!-- claude-review -->\n**Claude Review**")]);
		var unmarked = PrNode(number: 8, reviews: [new("github-actions", Head, "Something else.")]);
		var (_, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(claude, unmarked) });
		var lines = Lines(output);
		Assert.Equal(2, lines.Length);
		var (first, second) = (lines[0], lines[1]);
		In("review:claude", first);
		In("Claude reviewed the head", first);
		In("review:no", second);
		// Without Copilot's quota note on the head, Claude's review covers nothing (#270).
		var bare = PrNode(reviews: [new("github-actions", Head, "<!-- claude-review -->\n**Claude Review**")]);
		(_, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(bare) });
		In("review:no", output);
	}

	[Fact]
	public void The_query_fetches_review_bodies_and_whether_each_commit_is_a_merge()
	{
		In("author { login } commit { oid parents { totalCount } } body", Status.Query);
	}

	[Fact]
	public void Claude_findings_outside_the_diff_hold_the_landing_decision()
	{
		var off = "<!-- claude-review -->\n<!-- claude-review:off-diff -->\n**Claude Review**";
		var (_, output, _) = Run(["owner/app"], new()
		{
			["owner/app"] = Repo(PrNode(reviews: [new("copilot-pull-request-reviewer", Head, "Copilot was unable to review this pull request because the quota is used up."), new("github-actions", Head, off)])),
		});
		In("review:claude", output);
		In("-> wait (Claude's review of 1234567 has findings outside the diff)", output);
	}

	[Fact]
	public void Check_runs_carry_their_database_id_as_rank()
	{
		var check = Status.ToCheck(P("""{"__typename": "CheckRun", "databaseId": 113578170924, "name": "Test Suite", "status": "COMPLETED", "conclusion": "CANCELLED", "startedAt": null}"""));
		Assert.True(check.Rank == 113578170924 && check.StartedAt == "");
	}

	// -- Resolved bot threads nobody replied to --

	const string Notice = "notice: resolved without a reply: ";
	const string ClaudeReview = "<!-- claude-review -->\n**Claude Review**";

	static string LineOf(params object[] threads)
	{
		var (_, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(PrNode(threads: threads)) });
		return output;
	}

	[Fact]
	public void A_resolved_bot_thread_with_no_reply_ends_the_line_with_a_notice()
	{
		var output = LineOf(ThreadNode());
		Assert.Equal(1, Count(output, '\n'));
		EndsWith(Notice + "src/a.py:12", output.TrimEnd('\n'));
		In("threads:0", output);
	}

	[Fact]
	public void A_resolved_claude_review_thread_with_no_reply_is_noticed_too()
	{
		EndsWith(Notice + "src/a.py:12", LineOf(ThreadNode(login: "github-actions", review: ClaudeReview)).TrimEnd('\n'));
	}

	[Fact]
	public void A_thread_with_a_reply_has_no_notice()
	{
		NotIn("notice", LineOf(ThreadNode(replies: ["octo"])));
	}

	[Fact]
	public void A_persons_thread_and_a_sandcastle_replied_thread_have_no_notice()
	{
		NotIn("notice", LineOf(ThreadNode(login: "octo", kind: "User")));
		// Sandcastle's reply is posted under the owner's login, so it is just a reply.
		NotIn("notice", LineOf(ThreadNode(replies: ["octo"]), ThreadNode(login: "octo", kind: "User")));
	}

	[Fact]
	public void Another_workflows_github_actions_thread_and_an_unresolved_bot_thread_have_no_notice()
	{
		NotIn("notice", LineOf(ThreadNode(login: "github-actions", review: "Some other workflow's review.")));
		var output = LineOf(ThreadNode(resolved: false));
		NotIn("notice", output);
		In("threads:1", output);
	}

	[Fact]
	public void A_thread_with_more_comments_than_were_read_has_no_notice()
	{
		NotIn("notice", LineOf(ThreadNode(total: 11)));
	}

	[Fact]
	public void A_thread_without_a_path_or_a_line_is_named_by_what_it_has()
	{
		EndsWith(Notice + "src/a.py", LineOf(ThreadNode(path: "src/a.py", line: null)).TrimEnd('\n'));
		EndsWith(Notice + "thread", LineOf(ThreadNode(path: null, line: null)).TrimEnd('\n'));
		var original = With(ThreadNode(line: null), ("originalLine", 4L));
		EndsWith(Notice + "src/a.py:4", LineOf(original).TrimEnd('\n'));
	}

	[Fact]
	public void The_notice_names_at_most_three_threads()
	{
		var output = LineOf([.. Enumerable.Range(1, 5).Select(n => (object)ThreadNode(path: $"f{n}.py", line: n))]);
		EndsWith(Notice + "f1.py:1, f2.py:2, f3.py:3 and 2 more", output.TrimEnd('\n'));
	}

	[Fact]
	public void A_path_with_a_newline_or_an_escape_is_printed_escaped()
	{
		var output = LineOf(ThreadNode(path: "a\nb\u001b[31m.py"));
		Assert.Equal(1, Count(output, '\n'));
		NotIn("\u001b", output);
		In("\\n", output);
		In("\\x1b", output);
	}

	[Fact]
	public void The_notice_leaves_the_landing_decision_alone()
	{
		Rev[] reviewed = [new("copilot-pull-request-reviewer", Head)];
		var plain = Run(["owner/app"], new() { ["owner/app"] = Repo(PrNode(reviews: reviewed, threads: [true])) }).Out;
		var noticed = Run(["owner/app"], new() { ["owner/app"] = Repo(PrNode(reviews: reviewed, threads: [ThreadNode()])) }).Out;
		Assert.StartsWith(plain.TrimEnd('\n'), noticed, StringComparison.Ordinal);
		EndsWith(Notice + "src/a.py:12", noticed.TrimEnd('\n'));
		Assert.Equal(plain.Split(" -> ")[1].TrimEnd('\n'), noticed.Split(" -> ")[1].Split(" notice:")[0]);
	}

	[Fact]
	public void Threads_with_no_comments_key_print_as_before()
	{
		var output = LineOf(true, false);
		NotIn("notice", output);
		In("threads:1", output);
		NotIn("notice", LineOf(P("""{"isResolved": true, "comments": null}""")));
	}

	[Fact]
	public void To_state_lists_the_unreplied_threads()
	{
		var node = PrNode(threads: [ThreadNode(), ThreadNode(replies: ["octo"], path: "b.py"), ThreadNode(login: "octo", kind: "User")]);
		Assert.Equal(["src/a.py:12"], ToState(node, Status.RepoFlags("full")).UnrepliedThreads);
	}

	[Fact]
	public void The_threads_query_fetches_the_threads_comments_their_authors_and_reviews()
	{
		foreach (var field in new[] { "isResolved", "path", "line", "originalLine", "comments(", "totalCount", "url", "author { login __typename }", "pullRequestReview { body }" })
			In(field, Status.ThreadsQuery, field);
	}

	[Fact]
	public void The_repo_query_reads_no_thread_comments_so_it_stays_at_about_three_points()
	{
		var threads = Status.Query[Status.Query.IndexOf("reviewThreads", StringComparison.Ordinal)..];
		threads = threads[..threads.IndexOf("commits(", StringComparison.Ordinal)];
		NotIn("comments", threads);
		In("isResolved", threads);
		In("id number", Status.Query);
	}

	// Replaces the reader's Graphql: the repo query answers repoData, the threads query a PR's nodes by id; returns the calls.
	static (StatusReader Reader, List<string?> Calls) FakeGraphql(JsonNode repoData, Dictionary<string, JsonArray> threadsById)
	{
		List<string?> calls = [];
		var reader = new StatusReader(err: new StringWriter())
		{
			Graphql = (query, fields, raw) =>
			{
				if (query == Status.CommentsQuery)
					return new JsonObject { ["node"] = new JsonObject { ["comments"] = CommentsOf() } };
				if (query == Status.EventsQuery)
					return P("""{"node": {"timelineItems": {"pageInfo": {"hasNextPage": false, "endCursor": null}, "nodes": []}}}""");
				calls.Add(RawGet(raw, "id"));
				if (query == Status.ThreadsQuery)
					return new JsonObject { ["node"] = new JsonObject { ["reviewThreads"] = new JsonObject { ["nodes"] = threadsById[RawReq(raw, "id")].DeepClone() } } };
				return repoData;
			},
		};
		return (reader, calls);
	}

	[Fact]
	public void Only_a_pr_with_every_thread_resolved_and_a_bot_review_gets_the_comments_query()
	{
		Rev[] reviewed = [new("copilot-pull-request-reviewer", Head)];
		var done = With(PrNode(number: 1, reviews: reviewed, threads: [true, true]), ("id", "PR_done"));
		var openThread = With(PrNode(number: 2, reviews: reviewed, threads: [true, false]), ("id", "PR_open"));
		var unreviewed = With(PrNode(number: 3, threads: [true]), ("id", "PR_unreviewed"));
		var noThreads = With(PrNode(number: 4, reviews: reviewed), ("id", "PR_none"));
		var claude = With(PrNode(number: 5, reviews: [new("github-actions", Head, "<!-- claude-review -->\nOK")], threads: [true]), ("id", "PR_claude"));
		var someone = With(PrNode(number: 6, reviews: [new("github-actions", Head, "another workflow")], threads: [true]), ("id", "PR_other"));
		var (reader, calls) = FakeGraphql(Repo(done, openThread, unreviewed, noThreads, claude, someone),
				new() { ["PR_done"] = new JsonArray(ThreadNode()), ["PR_claude"] = new JsonArray(ThreadNode(replies: ["octo"])) });
		var data = reader.Fetch("owner", "app");
		Assert.Equal([null, "PR_done", "PR_claude"], calls);
		var byNumber = ByNumber(data);
		Assert.Equal(1, J.Int(J.Req(J.Req(J.Req(byNumber[1], "reviewThreads"), "nodes")![0], "comments")!["totalCount"]));
		JsonEqual("""[{"isResolved": true}, {"isResolved": false}]""", J.Req(J.Req(byNumber[2], "reviewThreads"), "nodes"));
		var state = ToState(byNumber[1], Status.RepoFlags("full"));
		Assert.Equal(["src/a.py:12"], state.UnrepliedThreads);
		Assert.Empty(ToState(byNumber[5], Status.RepoFlags("full")).UnrepliedThreads);
	}

	[Fact]
	public void A_failed_or_empty_comments_query_keeps_the_repo_readable_and_gives_no_notice()
	{
		Rev[] reviewed = [new("copilot-pull-request-reviewer", Head)];
		var boom = With(PrNode(number: 1, reviews: reviewed, threads: [true]), ("id", "PR_boom"));
		var gone = With(PrNode(number: 2, reviews: reviewed, threads: [true]), ("id", "PR_gone"));
		var fine = With(PrNode(number: 3, reviews: reviewed, threads: [true]), ("id", "PR_fine"));
		var data = Repo(boom, gone, fine);
		using var capsys = new StringWriter();

		var reader = new StatusReader(err: capsys)
		{
			Graphql = (query, fields, raw) =>
			{
				if (query != Status.ThreadsQuery)
					return data;
				if (RawReq(raw, "id") == "PR_boom")
					throw RuntimeError("502 Bad Gateway");
				if (RawReq(raw, "id") == "PR_gone")
					return P("""{"node": null}""");
				return new JsonObject { ["node"] = new JsonObject { ["reviewThreads"] = new JsonObject { ["nodes"] = new JsonArray(ThreadNode()) } } };
			},
		};
		using var output = new StringWriter();
		using var err = new StringWriter();
		Assert.Equal(0, Status.Run(["owner/app"], output, err, reader.Fetch, FleetList));
		var lines = Lines(output.ToString());
		Assert.Equal(3, lines.Length);
		NotIn("notice", lines[0]);
		NotIn("notice", lines[1]);
		EndsWith("notice: resolved without a reply: src/a.py:12", lines[2]);
		In("502 Bad Gateway", ReadErr(capsys));
		reader.Fetch("owner", "app");
		Assert.Equal(0, Count(ReadErr(capsys), "502 Bad Gateway"));
	}

	static int Count(string text, string part)
	{
		var count = 0;
		for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
			count++;
		return count;
	}

	// -- The record that Copilot was asked (mpaulosky/dotfiles#360) --

	[Fact]
	public void To_state_reads_the_record_and_when_it_settles()
	{
		var state = ToState(PrNode(comments: CommentsOf(At(31))), Flags, owner: "o", now: Now);
		Assert.True(state.CopilotAsked && state.CopilotAskSettled);
		state = ToState(PrNode(comments: CommentsOf(At(29))), Flags, owner: "o", now: Now);
		Assert.True(state.CopilotAsked && !state.CopilotAskSettled);
		state = ToState(PrNode(comments: CommentsOf()), Flags, owner: "o", now: Now);
		Assert.True(!state.CopilotAsked && !state.CopilotAskSettled);
	}

	[Fact]
	public void To_state_trusts_a_record_only_from_github_actions_or_the_owner()
	{
		foreach (var (login, owner, counts) in new[] { ("github-actions", "o", true), ("o", "o", true), ("stranger", "o", false), ("o", "p", false) })
		{
			var state = ToState(PrNode(comments: CommentsOf(With(At(3600), ("author", new JsonObject { ["login"] = login })))), Flags, owner: owner, now: Now);
			Assert.True(state.CopilotAsked == counts, $"({login}, {owner})");
		}
		var nobody = With(At(3600), ("author", null));
		Assert.False(ToState(PrNode(comments: CommentsOf(nobody)), Flags, owner: "o", now: Now).CopilotAsked);
	}

	[Fact]
	public void Comments_that_were_not_read_count_as_asked_but_not_settled()
	{
		var failed = PrNode(comments: null);
		var absent = PrNode();
		absent.Remove("comments");
		// 150 comments with 1 read and no record among them: the record may be on a page that wasn't read.
		var partial = PrNode(comments: CommentsOfTotal(150, With(At(3600), ("body", "LGTM"))));
		foreach (var node in new[] { failed, absent, partial })
		{
			var state = ToState(node, Flags, owner: "o", now: Now);
			Assert.True(state.CopilotAsked && !state.CopilotAskSettled);
		}
	}

	[Fact]
	public void A_record_on_the_page_counts_even_when_there_are_more_comments()
	{
		var state = ToState(PrNode(comments: CommentsOfTotal(150, At(3600))), Flags, owner: "o", now: Now);
		Assert.True(state.CopilotAsked && state.CopilotAskSettled);
	}

	[Fact]
	public void The_line_asks_copilot_for_an_unasked_head_and_calls_in_claude_for_a_settled_record()
	{
		var unasked = PrNode(reviews: Older, contexts: Green);
		var settled = PrNode(number: 8, reviews: Older, contexts: Green, comments: CommentsOf(Asked(3600, login: "octo")));
		var fresh = PrNode(number: 9, reviews: Older, contexts: Green, comments: CommentsOf(Asked(5)));
		var unread = PrNode(number: 10, reviews: Older, contexts: Green);
		unread["comments"] = null;
		var (_, output, _) = Run([], new() { ["octo/app"] = Repo(unasked, settled, fresh, unread) }, () => new() { ["octo/app"] = "full" });
		var lines = Lines(output);
		In("-> request Copilot", lines[0]);
		// The owner is the repo's owner, taken from its name.
		In("-> add review:claude", lines[1]);
		In("-> wait", lines[2]);
		In("Copilot was asked", lines[2]);
		In("-> wait", lines[3]);
		NotIn("-> request Copilot", lines[3]);
		NotIn("-> add review:claude", lines[3]);
	}

	[Fact]
	public void The_comments_query_reads_the_last_100_comments_with_their_author_body_and_time()
	{
		foreach (var field in new[] { "$id: ID!", "comments(last: 100)", "totalCount", "author { login }", "body", "createdAt" })
			In(field, Status.CommentsQuery, field);
		NotIn("comments(", Status.Query);
	}

	// Replaces the reader's Graphql: the repo query answers repoData, the comments query a PR's comments by id (an exception is
	// thrown); returns the ids asked.
	static (StatusReader Reader, List<string> AskedIds) CommentsGraphql(JsonNode repoData, Dictionary<string, object> commentsById, TextWriter? err = null)
	{
		List<string> askedIds = [];
		var reader = new StatusReader(err: err ?? new StringWriter())
		{
			Graphql = (query, fields, raw) =>
			{
				if (query == Status.EventsQuery)
					return P("""{"node": {"timelineItems": {"pageInfo": {"hasNextPage": false, "endCursor": null}, "nodes": []}}}""");
				if (query == Status.CommentsQuery)
				{
					askedIds.Add(RawReq(raw, "id"));
					var answer = commentsById[RawReq(raw, "id")];
					if (answer is Exception error)
						throw error;
					return new JsonObject { ["node"] = new JsonObject { ["comments"] = ((JsonNode)answer).DeepClone() } };
				}
				if (query == Status.ThreadsQuery)
					throw new InvalidOperationException("AssertionError: no thread comments wanted here");
				return repoData;
			},
		};
		return (reader, askedIds);
	}

	[Fact]
	public void Only_an_open_ready_pr_whose_head_copilot_hasnt_reviewed_or_been_asked_for_gets_the_comments_query()
	{
		Rev[] onHead = [new("copilot-pull-request-reviewer", Head)];
		var wanted = PrNode(number: 1, reviews: Older, comments: null);
		var neverReviewed = PrNode(number: 2, comments: null);
		var draft = PrNode(number: 3, draft: true, reviews: Older, comments: null);
		var reviewed = PrNode(number: 4, reviews: onHead, comments: null);
		var requested = With(PrNode(number: 5, reviews: Older, comments: null),
				("reviewRequests", P("""{"nodes": [{"requestedReviewer": {"login": "Copilot"}}]}""")));
		var (reader, askedIds) = CommentsGraphql(Repo(wanted, neverReviewed, draft, reviewed, requested),
				new() { ["PR_1"] = CommentsOf(Asked(3600)), ["PR_2"] = CommentsOf(Asked(3600), Asked(5)) });
		var data = reader.Fetch("owner", "app");
		Assert.Equal(["PR_1", "PR_2"], askedIds);
		var byNumber = ByNumber(data);
		Assert.True(J.Int(J.Req(J.Req(byNumber[1], "comments"), "totalCount")) == 1 && J.Int(J.Req(J.Req(byNumber[2], "comments"), "totalCount")) == 2);
		Assert.Equal(new JsonNode?[] { null, null, null }, new long[] { 3, 4, 5 }.Select(number => J.Req(byNumber[number], "comments")));
	}

	[Fact]
	public void A_failed_comments_query_keeps_the_repo_readable_and_makes_the_record_unread()
	{
		var boom = PrNode(number: 1, reviews: Older, contexts: Green);
		var fine = PrNode(number: 2, reviews: Older, contexts: Green);
		using var capsys = new StringWriter();
		var (reader, askedIds) = CommentsGraphql(Repo(boom, fine), new() { ["PR_1"] = RuntimeError("502 Bad Gateway"), ["PR_2"] = CommentsOf(Asked(3600)) }, capsys);
		using var output = new StringWriter();
		using var err = new StringWriter();
		Assert.Equal(0, Status.Run(["owner/app"], output, err, reader.Fetch, FleetList));
		var lines = Lines(output.ToString());
		Assert.Equal(["PR_1", "PR_2"], askedIds);
		// PR 1's record is unread: neither a Copilot request nor Claude is called for.
		NotIn("-> request Copilot", lines[0]);
		NotIn("-> add review:claude", lines[0]);
		In("-> wait", lines[0]);
		In("-> add review:claude", lines[1]);
		In("502 Bad Gateway", ReadErr(capsys));
	}

	// -- The review loop: dirty rounds in a row (mpaulosky/dotfiles#375) --

	[Fact]
	public void The_line_shows_an_exhausted_loop_as_ready_to_merge_with_the_threads_left_open()
	{
		var node = PrNode(reviews: Dirty3, threads: [OpenThread(), OpenThread(), ThreadNode(resolved: true)], contexts: Green);
		var (_, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(node) });

		In("threads:2", output);
		In("-> leave to PR Auto-Merge (review loop exhausted", output);
		In("ready to merge", output);
		In("2 thread(s) left open", output);
	}

	[Fact]
	public void The_line_asks_for_the_head_review_below_exhaustion()
	{
		foreach (var reviews in new Rev[][] { Dirty3[..2], [RoundOf(1), RoundOf(2), RoundOf(3, dirty: false), RoundOf(4)], [.. Dirty3, RoundOf(5, dirty: false)] })
		{
			var node = PrNode(reviews: reviews, threads: [false], contexts: Green);
			var (_, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(node) });

			NotIn("review loop exhausted", output);
			In("-> request Copilot", output, string.Join(", ", reviews.Select(r => r.ToString())));
		}
	}

	[Fact]
	public void A_merge_of_main_is_never_a_round_in_the_status_line_either()
	{
		var merge = RoundOf(9, merge: true, minute: 9);
		var two = PrNode(reviews: [RoundOf(1), RoundOf(2), merge], contexts: Green);
		var three = PrNode(reviews: [RoundOf(1), merge, RoundOf(2), RoundOf(3)], contexts: Green);
		var state = StateOf(two);
		Assert.Equal([Oid(1), Oid(2)], state.Rounds.Select(item => item.Oid));
		Assert.False(state.LoopExhausted);
		Assert.True(StateOf(three).LoopExhausted);
		Assert.Equal([true, true, true], StateOf(three).Rounds.Select(item => item.Dirty));
	}

	[Fact]
	public void To_state_reads_the_rounds_with_their_time_and_whether_they_raised_threads()
	{
		var state = StateOf(PrNode(reviews: [RoundOf(1), RoundOf(2, dirty: false), new(CopilotLogin, new string('f', 40), "")]));

		Assert.Equal([(true, "2026-01-02T00:01:00Z"), (false, "2026-01-02T00:02:00Z")], state.Rounds[..2].Select(item => (item.Dirty, item.At)));
		// A review with no submission time or thread count, as older fixtures give, is a clean round with no time.
		Assert.Equal((new string('f', 40), false), (state.Rounds[2].Oid, state.Rounds[2].Dirty));
	}

	[Fact]
	public void Claudes_dirty_reviews_are_rounds_where_copilot_is_gone_or_noted_and_not_otherwise()
	{
		Rev[] claude = [.. new[] { 1, 2, 3 }.Select(n => RoundOf(n, login: "github-actions", body: "<!-- claude-review -->\nA finding."))];
		var offDiff = RoundOf(4, login: "github-actions", body: "<!-- claude-review -->\n<!-- claude-review:off-diff -->\n**Claude Review**", dirty: false);
		// Copilot may still review: Claude's reviews are no rounds.
		Assert.Empty(StateOf(PrNode(reviews: claude)).Rounds);
		// The ask was recorded long ago and nothing is pending: Copilot is gone, so every Claude review counts.
		var gone = StateOf(PrNode(reviews: claude, comments: CommentsOf(At(3600))));
		Assert.Equal([true, true, true], gone.Rounds.Select(item => item.Dirty));
		Assert.True(gone.LoopExhausted);
		// An off-diff finding makes a round dirty.
		Assert.Equal([true], StateOf(PrNode(reviews: [offDiff], comments: CommentsOf(At(3600)))).Rounds.Select(item => item.Dirty));
		// A request still pending: Copilot isn't gone.
		var pending = With(PrNode(reviews: claude, comments: CommentsOf(At(3600))), ("reviewRequests", CopilotRequested));
		Assert.Empty(StateOf(pending).Rounds);
		// A quota note on those commits backs them whatever the record says.
		Rev[] notes = [.. new[] { 1, 2, 3 }.Select(n => new Rev(CopilotLogin, Oid(n), "Copilot was unable to review this pull request because the quota is used up."))];
		Assert.True(StateOf(PrNode(reviews: [.. notes, .. claude])).LoopExhausted);
	}

	[Fact]
	public void An_exhaustion_comment_with_no_relabel_after_it_is_the_newest_word()
	{
		var node = PrNode(reviews: Dirty3, comments: CommentsOf(MarkerComment()));
		var state = StateOf(node);

		Assert.True(state.ExhaustionMarkerNewest);
		// Nothing changes about the rounds or the merge: the loop is still exhausted.
		Assert.True(state.LoopExhausted && state.Rounds.Length == 3);
		Assert.False(StateOf(PrNode(reviews: Dirty3)).ExhaustionMarkerNewest);
	}

	[Fact]
	public void The_owner_re_adding_review_claude_after_the_comment_starts_a_new_loop()
	{
		// Three dirty rounds before the label's event, and a fourth before it too: none of them counts any more.
		var before = PrNode(reviews: [.. Dirty3, RoundOf(4, minute: 30)], comments: CommentsOf(Gone, MarkerComment()), events: Ev(Relabel()));
		var state = StateOf(before);
		Assert.False(state.ExhaustionMarkerNewest);
		Assert.True(state.Rounds.Length == 0 && !state.LoopExhausted);
		// A round after the event is the new loop's first.
		var fresh = new Rev(CopilotLogin, Oid(5), "", "2026-01-02T03:00:00Z", 1, false);
		var after = StateOf(PrNode(reviews: [.. Dirty3, fresh], comments: CommentsOf(Gone, MarkerComment()), events: Ev(Relabel())));
		Assert.Equal([Oid(5)], after.Rounds.Select(item => item.Oid));
		Assert.False(after.LoopExhausted);
	}

	[Fact]
	public void A_label_event_before_the_exhausting_round_or_by_a_reader_is_no_restart()
	{
		// DIRTY3's third round is at 00:02. An add before it is part of the loop; an add by a login without write access
		// (acceptance criterion 11) starts nothing.
		foreach (var events in new JsonNode[][] { [Relabel(at: "2026-01-02T00:01:30Z")], [Relabel(writer: false)], [] })
		{
			var state = StateOf(PrNode(reviews: Dirty3, comments: CommentsOf(Gone, MarkerComment()), events: events));
			Assert.True(state.ExhaustionMarkerNewest && state.LoopExhausted, new JsonArray([.. events.Select(Attach)]).ToJsonString());
		}
	}

	[Fact]
	public void Only_the_first_relabel_after_the_exhausting_round_restarts_the_loop_and_a_later_add_inside_the_new_loop_keeps_its_rounds()
	{
		// PR Auto-Merge's PAT adds the label as the owner, later in the loop (before its third dirty round): that must not
		// discard the loop's rounds.
		Rev[] rounds = [.. new[] { 1, 2, 3 }.Select(n => new Rev(CopilotLogin, Oid(n), "", $"2026-01-02T03:{n:D2}:00Z", 1))];
		var node = PrNode(reviews: [.. Dirty3, .. rounds], comments: CommentsOf(Gone, MarkerComment()),
				events: Ev(Relabel("2026-01-02T02:00:00Z"), Relabel("2026-01-02T03:02:30Z")));
		var state = StateOf(node);

		Assert.True(state.Rounds.Length == 3 && state.LoopExhausted && !state.ExhaustionMarkerNewest);
	}

	[Fact]
	public void Only_github_actions_comments_count_as_the_exhaustion_record()
	{
		foreach (var login in new[] { "stranger", "o", null })
		{
			var comment = login is not null ? MarkerComment(login: login) : With(MarkerComment(), ("author", null));
			Assert.False(StateOf(PrNode(reviews: Dirty3, comments: CommentsOf(comment))).ExhaustionMarkerNewest, login ?? "None");
		}
		Assert.True(StateOf(PrNode(reviews: Dirty3, comments: CommentsOf(MarkerComment(login: "github-actions[bot]")))).ExhaustionMarkerNewest);
	}

	[Fact]
	public void The_latest_exhaustion_comment_of_the_new_loop_is_the_newest_word()
	{
		var first = MarkerComment(at: "2026-01-02T01:00:00Z");
		var second = MarkerComment(at: "2026-01-02T05:00:00Z");
		Rev[] newLoop = [.. new[] { 1, 2, 3 }.Select(n => new Rev(CopilotLogin, Oid(n), "", $"2026-01-02T03:{n:D2}:00Z", 1))];
		var node = PrNode(reviews: [.. Dirty3, .. newLoop], comments: CommentsOf(Gone, first, second), events: Ev(Relabel("2026-01-02T02:00:00Z")));
		// The second loop was exhausted too and the second comment documents it: nobody has added the label since.
		var state = StateOf(node);
		Assert.True(state.ExhaustionMarkerNewest && state.LoopExhausted);
		// A comment older than the second loop's exhausting round doesn't: that loop isn't documented yet.
		Assert.False(StateOf(PrNode(reviews: [.. Dirty3, .. newLoop], comments: CommentsOf(Gone, first), events: Ev(Relabel("2026-01-02T02:00:00Z")))).ExhaustionMarkerNewest);
	}

	[Fact]
	public void A_loop_whose_comments_or_label_events_cant_be_read_is_never_exhausted()
	{
		foreach (var comments in new JsonNode?[] { null, CommentsOfTotal(150, With(At(3600), ("body", "LGTM"))) })
		{
			var state = StateOf(PrNode(reviews: Dirty3, comments: comments));
			Assert.True(state.LoopUnread && !state.LoopExhausted, comments?.ToJsonString() ?? "None");
		}
		// A comment is there but the label events are not: no telling where the new loop starts.
		var node = PrNode(reviews: Dirty3, comments: CommentsOf(MarkerComment()), events: null);
		Assert.True(StateOf(node).LoopUnread && !StateOf(node).LoopExhausted);
		// The page is partial but holds the comment: read.
		var found = StateOf(PrNode(reviews: Dirty3, comments: CommentsOfTotal(150, MarkerComment())));
		Assert.True(!found.LoopUnread && found.LoopExhausted);
		// Fewer than three dirty rounds can't be exhausted, so unread comments say nothing.
		Assert.False(StateOf(PrNode(reviews: Dirty3[..2], comments: null)).LoopUnread);
	}

	[Fact]
	public void An_unread_loop_is_not_shown_as_ready_to_merge()
	{
		var node = PrNode(reviews: Dirty3, comments: null);
		var (_, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(node) });

		NotIn("review loop exhausted", output);
		NotIn("-> leave to PR Auto-Merge", output);
	}

	[Fact]
	public void The_repo_query_reads_each_reviews_time_and_thread_count()
	{
		var reviews = Status.Query[Status.Query.IndexOf("reviews(last: 100)", StringComparison.Ordinal)..];
		reviews = reviews[..reviews.IndexOf("reviewThreads", StringComparison.Ordinal)];
		In("submittedAt", reviews);
		In("comments { totalCount }", reviews);
		// The existing fields stay.
		In("author { login } commit { oid parents { totalCount } } body", Status.Query);
		// The label events are not the repo query's any more (acceptance criterion 12): a "last 30" misses the add on a PR with many.
		NotIn("timelineItems(", Status.Query);
	}

	[Fact]
	public void A_pr_that_could_be_exhausted_gets_the_comments_query_even_when_copilot_is_requested()
	{
		// The ask's record isn't needed (a request is pending), but the exhaustion comment and the new loop's start are.
		var exhausted = With(PrNode(number: 1, reviews: Dirty3, comments: null), ("reviewRequests", CopilotRequested));
		var two = With(PrNode(number: 2, reviews: Dirty3[..2], comments: null), ("reviewRequests", CopilotRequested));
		var cleanLast = With(PrNode(number: 3, reviews: [.. Dirty3, RoundOf(4, dirty: false)], comments: null), ("reviewRequests", CopilotRequested));
		var (reader, askedIds) = CommentsGraphql(Repo(exhausted, two, cleanLast), new() { ["PR_1"] = CommentsOf(MarkerComment()), ["PR_3"] = CommentsOf(MarkerComment()) });
		var data = reader.Fetch("owner", "app");
		var byNumber = ByNumber(data);

		// The longest dirty run counts, not the trailing one (review_loop's test): PR 3's loop may have been restarted since
		// its three dirty rounds, so its comments and label events are read too; PR 2 never reached the cap.
		Assert.Equal(["PR_1", "PR_3"], askedIds);
		Assert.True(J.Int(J.Req(J.Req(byNumber[1], "comments"), "totalCount")) == 1 && J.Int(J.Req(J.Req(byNumber[3], "comments"), "totalCount")) == 1);
		Assert.Null(J.Req(byNumber[2], "comments"));
	}

	[Fact]
	public void A_restarted_loop_whose_newest_rounds_are_clean_reads_its_label_events_and_is_not_unread()
	{
		// d d d, a writer re-adds review:claude, one clean round, then a head nobody has reviewed: the trailing dirty streak is 0,
		// but the loop reached the cap once, so the comments and events must be read for the walk (mpaulosky/dotfiles#378).
		var clean = new Rev(CopilotLogin, Oid(4), "", "2026-01-02T03:00:00Z", 0, false);
		var node = PrNode(reviews: [.. Dirty3, clean], comments: CommentsOf(Gone, MarkerComment()), events: Ev(Relabel("2026-01-02T01:00:00Z")));
		var state = StateOf(node);

		Assert.True(!state.LoopUnread && !state.LoopExhausted);
		Assert.Equal([Oid(4)], state.Rounds.Select(item => item.Oid));
	}

	[Fact]
	public void A_failed_comments_query_for_a_pr_that_could_be_exhausted_makes_its_loop_unread()
	{
		var node = With(PrNode(number: 1, reviews: Dirty3, comments: null, contexts: Green), ("reviewRequests", CopilotRequested));
		using var capsys = new StringWriter();
		var (reader, _) = CommentsGraphql(Repo(node), new() { ["PR_1"] = RuntimeError("502 Bad Gateway") }, capsys);
		using var output = new StringWriter();
		using var err = new StringWriter();

		Assert.Equal(0, Status.Run(["owner/app"], output, err, reader.Fetch, FleetList));
		NotIn("review loop exhausted", output.ToString());
		In("502 Bad Gateway", ReadErr(capsys));
	}

	// -- Claude reviews only where Copilot can't; a person's threads hold; every label event is read (mpaulosky/dotfiles#378) --

	[Fact]
	public void A_review_claude_add_by_a_writer_after_the_exhausting_round_starts_a_new_loop_with_no_comment_acceptance_criterion_4()
	{
		var node = PrNode(reviews: [.. Dirty3, RoundOf(4, minute: 30)], comments: CommentsOf(Gone), events: Ev(Relabel()));
		var state = StateOf(node);

		Assert.True(state.Rounds.Length == 0 && !state.LoopExhausted && !state.ExhaustionMarkerNewest && !state.LoopUnread);
	}

	[Fact]
	public void The_add_is_ignored_while_copilot_can_review_acceptance_criteria_1_and_4()
	{
		// Never asked, or a request pending even after a long-settled record: Copilot may still review, so the label means nothing.
		// PR Auto-Merge removed each add at once (mpaulosky/dotfiles#381): that removal is what makes the add ignored.
		var neverAsked = PrNode(reviews: Dirty3, events: Ev(Relabel(), Removal()));
		var pending = With(PrNode(reviews: Dirty3, comments: CommentsOf(Gone), events: Ev(Relabel(), Removal())), ("reviewRequests", CopilotRequestedNode));
		var justAsked = PrNode(reviews: Dirty3, comments: CommentsOf(At(5)), events: Ev(Relabel(), Removal()));
		foreach (var node in new[] { neverAsked, pending, justAsked })
		{
			var state = StateOf(node);
			Assert.True(state.LoopExhausted && state.Rounds.Length == 3, (node.ContainsKey("reviewRequests") ? node["reviewRequests"] : node["comments"])?.ToJsonString());
		}
	}

	[Fact]
	public void The_add_counts_when_copilots_note_is_on_the_head_acceptance_criterion_4()
	{
		var note = new Rev(CopilotLogin, Head, NoteText);
		var state = StateOf(PrNode(reviews: [.. Dirty3, note], events: Ev(Relabel())));

		Assert.True(state.Rounds.Length == 0 && !state.LoopExhausted);
	}

	[Fact]
	public void The_add_is_ignored_when_copilot_has_really_reviewed_the_head_acceptance_criterion_1()
	{
		// A settled ask and no pending request is also what a finished Copilot review leaves behind (#270): not "gone".
		var reviewed = new Rev(CopilotLogin, Head, "", "2026-01-02T00:03:00Z", 1);
		var state = StateOf(PrNode(reviews: [RoundOf(1), RoundOf(2), RoundOf(3), reviewed], comments: CommentsOf(Gone),
				events: Ev(Relabel("2026-01-02T00:04:00Z"), Removal("2026-01-02T00:04:05Z"))));

		Assert.True(state.LoopExhausted);
	}

	[Fact]
	public void A_review_claude_add_by_a_login_without_write_access_starts_no_loop_acceptance_criterion_11()
	{
		var state = StateOf(PrNode(reviews: Dirty3, comments: CommentsOf(Gone), events: Ev(Relabel(writer: false))));

		Assert.True(state.LoopExhausted && !state.LoopUnread);
	}

	[Fact]
	public void A_review_claude_add_whose_permission_couldnt_be_read_leaves_the_loop_unread_acceptance_criterion_11()
	{
		var state = StateOf(PrNode(reviews: Dirty3, comments: CommentsOf(Gone), events: Ev(Relabel(writer: null))));

		Assert.True(state.LoopUnread && !state.LoopExhausted);
	}

	[Fact]
	public void The_first_add_by_a_writer_counts_after_adds_by_readers_acceptance_criterion_11()
	{
		var events = Ev(Relabel("2026-01-02T01:00:00Z", writer: false), Relabel("2026-01-02T01:30:00Z", writer: true), Relabel("2026-01-02T02:00:00Z", writer: null));
		var state = StateOf(PrNode(reviews: Dirty3, comments: CommentsOf(Gone), events: events));

		Assert.True(state.Rounds.Length == 0 && !state.LoopUnread);
	}

	[Fact]
	public void Label_events_that_were_not_read_leave_a_loop_that_could_be_exhausted_unread_acceptance_criterion_12()
	{
		var absent = PrNode(reviews: Dirty3, comments: CommentsOf(Gone));
		absent.Remove("labelEvents");
		foreach (var node in new[] { absent, PrNode(reviews: Dirty3, comments: CommentsOf(Gone), events: null) })
		{
			var state = StateOf(node);
			Assert.True(state.LoopUnread && !state.LoopExhausted);
		}
		// Fewer than three dirty rounds can't be exhausted, so unread events say nothing.
		Assert.False(StateOf(PrNode(reviews: Dirty3[..2], comments: CommentsOf(Gone), events: null)).LoopUnread);
	}

	[Fact]
	public void An_unread_label_events_loop_is_not_shown_as_ready_to_merge_acceptance_criterion_12()
	{
		var node = PrNode(reviews: Dirty3, comments: CommentsOf(Gone), events: null, contexts: Green);
		var (_, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(node) });

		NotIn("review loop exhausted", output);
		NotIn("-> leave to PR Auto-Merge", output);
	}

	// -- A person's open threads still hold the merge at an exhausted loop --

	[Fact]
	public void To_state_counts_the_open_threads_a_person_opened_acceptance_criterion_3()
	{
		var persons = StateOf(PrNode(reviews: Dirty3, threads: [OpenThread(login: "octo", kind: "User")]));
		var bots = StateOf(PrNode(reviews: Dirty3, threads: [OpenThread(), OpenClaudeThread()]));
		var both = StateOf(PrNode(reviews: Dirty3, threads: [OpenThread(), OpenThread(login: "octo", kind: "User"), OpenClaudeThread()]));

		Assert.Equal((1, 1), (persons.OpenThreads, persons.OpenPersonThreads));
		Assert.Equal((2, 0), (bots.OpenThreads, bots.OpenPersonThreads));
		Assert.Equal((3, 1), (both.OpenThreads, both.OpenPersonThreads));
	}

	[Fact]
	public void A_thread_whose_author_cant_be_read_counts_as_a_persons_acceptance_criterion_3()
	{
		var unreadable = StateOf(PrNode(reviews: Dirty3, threads: [OpenThread(login: null)]));
		// A thread node without its comments (the repo query reads none) has no opener either.
		var uncommented = StateOf(PrNode(reviews: Dirty3, threads: [false]));

		Assert.True(unreadable.OpenPersonThreads == 1 && uncommented.OpenPersonThreads == 1);
	}

	[Fact]
	public void A_resolved_thread_of_a_person_holds_nothing_acceptance_criterion_3()
	{
		var state = StateOf(PrNode(reviews: Dirty3, threads: [ThreadNode(login: "octo", kind: "User", resolved: true), OpenThread()]));

		Assert.True(state.OpenThreads == 1 && state.OpenPersonThreads == 0);
	}

	[Fact]
	public void The_line_for_an_exhausted_loop_waits_on_a_persons_thread_and_hands_off_for_bot_threads_acceptance_criterion_3()
	{
		var person = PrNode(reviews: Dirty3, threads: [OpenThread(), OpenThread(login: "octo", kind: "User")], contexts: Green);
		var bots = PrNode(number: 8, reviews: Dirty3, threads: [OpenThread(), OpenClaudeThread()], contexts: Green);
		var (_, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(person, bots) });
		var lines = Lines(output);
		Assert.Equal(2, lines.Length);
		var (held, handed) = (lines[0], lines[1]);

		In("-> wait", held);
		In("person", held);
		NotIn("-> leave to PR Auto-Merge", held);
		In("-> leave to PR Auto-Merge (review loop exhausted", handed);
		In("2 thread(s) left open", handed);
	}

	[Fact]
	public void A_pr_that_could_be_exhausted_with_open_threads_gets_the_threads_query_for_their_authors_acceptance_criterion_3()
	{
		var exhausted = With(PrNode(number: 1, reviews: Dirty3, threads: [false, true], comments: CommentsOf(MarkerComment())), ("id", "PR_ex"));
		var below = With(PrNode(number: 2, reviews: Dirty3[..2], threads: [false], comments: null), ("id", "PR_below"));
		var resolved = With(PrNode(number: 3, reviews: Dirty3, threads: [true], comments: CommentsOf(MarkerComment())), ("id", "PR_resolved"));
		List<string> threadsAsked = [];

		var reader = new StatusReader(err: new StringWriter())
		{
			Graphql = (query, fields, raw) =>
			{
				if (query == Status.EventsQuery)
					return P("""{"node": {"timelineItems": {"pageInfo": {"hasNextPage": false, "endCursor": null}, "nodes": []}}}""");
				if (query == Status.CommentsQuery)
					return new JsonObject { ["node"] = new JsonObject { ["comments"] = CommentsOf(MarkerComment()) } };
				if (query == Status.ThreadsQuery)
				{
					threadsAsked.Add(RawReq(raw, "id"));
					return new JsonObject
					{
						["node"] = new JsonObject { ["reviewThreads"] = new JsonObject { ["nodes"] = new JsonArray(OpenThread(login: "octo", kind: "User"), ThreadNode(resolved: true)) } },
					};
				}
				return Repo(exhausted, below, resolved);
			},
		};
		var data = reader.Fetch("owner", "app");
		var byNumber = ByNumber(data);

		Assert.Contains("PR_ex", threadsAsked);
		Assert.DoesNotContain("PR_below", threadsAsked);
		Assert.Equal(1, ToState(byNumber[1], Flags, owner: "o", now: Now).OpenPersonThreads);
	}

	[Fact]
	public void A_failed_threads_read_leaves_every_open_thread_a_persons_acceptance_criterion_3()
	{
		var exhausted = With(PrNode(number: 1, reviews: Dirty3, threads: [false], comments: CommentsOf(MarkerComment())), ("id", "PR_ex"));
		using var capsys = new StringWriter();

		var reader = new StatusReader(err: capsys)
		{
			Graphql = (query, fields, raw) =>
			{
				if (query == Status.EventsQuery)
					return P("""{"node": {"timelineItems": {"pageInfo": {"hasNextPage": false, "endCursor": null}, "nodes": []}}}""");
				if (query == Status.CommentsQuery)
					return new JsonObject { ["node"] = new JsonObject { ["comments"] = CommentsOf(MarkerComment()) } };
				if (query == Status.ThreadsQuery)
					throw RuntimeError("502 Bad Gateway");
				return Repo(exhausted);
			},
		};
		var data = reader.Fetch("owner", "app");
		var node = FirstNode(data);

		Assert.Equal(1, ToState(node, Flags, owner: "o", now: Now).OpenPersonThreads);
		In("502 Bad Gateway", ReadErr(capsys));
	}

	// -- Every review:claude label event is read, a page at a time (acceptance criteria 11 and 12) --

	[Fact]
	public void The_events_query_pages_through_the_review_claude_events_acceptance_criterion_12()
	{
		In("timelineItems", Status.EventsQuery);
		In("LABELED_EVENT", Status.EventsQuery);
		foreach (var field in new[] { "$id: ID!", "first: 100", "after:", "pageInfo", "hasNextPage", "endCursor", "createdAt", "label { name }", "actor", "login", "__typename" })
			In(field, Status.EventsQuery, field);
	}

	[Fact]
	public void The_comments_query_pages_backwards_acceptance_criterion_9()
	{
		foreach (var field in new[] { "comments(last: 100", "before", "pageInfo", "hasPreviousPage", "startCursor" })
			In(field, Status.CommentsQuery, field);
	}

	static bool? Writer(JsonNode? labelEvent) => J.Req(labelEvent, "writer") is JsonNode writer ? writer.GetValue<bool>() : null;

	[Fact]
	public void More_than_30_label_events_after_the_exhausting_round_are_all_read_acceptance_criterion_12()
	{
		JsonNode[] strangers = [.. Enumerable.Range(0, 30).Select(minute => EventNode($"2026-01-02T01:{minute:D2}:00Z", "stranger"))];
		JsonNode[] later = [EventNode("2026-01-02T02:00:00Z", "owner"), .. Enumerable.Range(1, 9).Select(minute => EventNode($"2026-01-02T02:{minute:D2}:00Z", "stranger"))];
		// The 35th event is the owner's; the repo's owner has write access without a lookup.
		object[] pages = [EventsPage(strangers, "c1"), EventsPage(later)];
		var served = new Served(Repo(ExhaustedNode(comments: null)), comments: new() { ["PR_1"] = [CommentsPage([Gone], 1)] },
				events: new() { ["PR_1"] = pages }, permissions: new() { ["stranger"] = "read" });
		var data = served.Install().Fetch("owner", "app");
		var node = FirstNode(data);

		var labelEvents = (JsonArray)J.Req(node, "labelEvents")!;
		Assert.Equal(40, labelEvents.Count);
		Assert.Equal(Enumerable.Repeat<bool?>(false, 30), labelEvents.Take(30).Select(Writer));
		JsonEqual("""{"createdAt": "2026-01-02T02:00:00Z", "writer": true}""", labelEvents[30]);
		Assert.Equal(2, served.Calls.Count(call => call == ("events", "PR_1")));
		var state = ToState(node, Flags, owner: "owner", now: Now);
		Assert.True(state.Rounds.Length == 0 && !state.LoopExhausted && !state.LoopUnread);
	}

	[Fact]
	public void A_page_of_label_events_that_cant_be_read_marks_the_loop_unread_acceptance_criterion_12()
	{
		object[] pages = [EventsPage([EventNode("2026-01-02T01:00:00Z", "stranger")], "c1"), RuntimeError("502 Bad Gateway")];
		var served = new Served(Repo(ExhaustedNode(comments: null)), comments: new() { ["PR_1"] = [CommentsPage([Gone], 1)] }, events: new() { ["PR_1"] = pages },
				permissions: new() { ["stranger"] = "read" });
		var data = served.Install().Fetch("owner", "app");
		var node = FirstNode(data);

		Assert.Null(J.Req(node, "labelEvents"));
		In("502 Bad Gateway", ReadErr(served.Err));
		var state = ToState(node, Flags, owner: "owner", now: Now);
		Assert.True(state.LoopUnread && !state.LoopExhausted);
	}

	[Fact]
	public void Only_review_claude_events_are_kept_and_each_login_is_asked_about_once_acceptance_criterion_11()
	{
		JsonNode[] events =
		[
			EventNode("2026-01-02T01:00:00Z", "stranger"),
			EventNode("2026-01-02T01:01:00Z", "stranger"),
			EventNode("2026-01-02T01:02:00Z", "colleague"),
			EventNode("2026-01-02T01:03:00Z", "enhancer", label: "enhancement"),
			EventNode("2026-01-02T01:04:00Z", "github-actions", kind: "Bot"),
			EventNode("2026-01-02T01:05:00Z", null),
			EventNode("2026-01-02T01:06:00Z", "flaky"),
			EventNode("2026-01-02T01:07:00Z", "owner"),
		];
		var served = new Served(Repo(ExhaustedNode(comments: null)), comments: new() { ["PR_1"] = [CommentsPage([Gone], 1)] }, events: new() { ["PR_1"] = [EventsPage(events)] },
				permissions: new() { ["stranger"] = "read", ["colleague"] = "write", ["flaky"] = RuntimeError("502") });
		var node = FirstNode(served.Install().Fetch("owner", "app"));

		Assert.Equal(
		[
			("01:00:00", false), ("01:01:00", false), ("01:02:00", true),
			// A bot, and an actor that can't be read (a deleted account), never have write access; a failed lookup is unread.
			("01:04:00", false), ("01:05:00", false), ("01:06:00", (bool?)null), ("01:07:00", true),
		], ((JsonArray)J.Req(node, "labelEvents")!).Select(e => (J.S(J.Req(e, "createdAt"))![^9..^1], Writer(e))));
		var lookups = served.Calls.Where(call => call.Kind == "permission").Select(call => call.Id).ToList();
		Assert.True(lookups.Distinct().Order(StringComparer.Ordinal).SequenceEqual(lookups.Order(StringComparer.Ordinal)), string.Join(", ", lookups));
		Assert.True(!lookups.Contains("owner") && !lookups.Contains("github-actions") && !lookups.Contains("enhancer"));
		Assert.Superset(new HashSet<string> { "stranger", "colleague", "flaky" }, lookups.ToHashSet());
	}

	[Fact]
	public void The_permission_of_a_login_is_asked_through_the_collaborators_permission_endpoint_acceptance_criterion_11()
	{
		List<string> paths = [];
		var served = new Served(Repo(ExhaustedNode(comments: null)), comments: new() { ["PR_1"] = [CommentsPage([Gone], 1)] },
				events: new() { ["PR_1"] = [EventsPage([EventNode("2026-01-02T01:00:00Z", "colleague")])] }, permissions: new() { ["colleague"] = "admin" });
		var reader = served.Install();
		reader.Rest = path =>
		{
			paths.Add(path);
			return served.Rest(path);
		};
		var node = FirstNode(reader.Fetch("owner", "app"));

		Assert.Equal(["repos/owner/app/collaborators/colleague/permission"], paths);
		Assert.True(Writer(J.Req(node, "labelEvents")![0]) is true);
	}

	[Fact]
	public void A_pr_that_cant_be_exhausted_reads_no_label_events()
	{
		var served = new Served(Repo(With(PrNode(number: 1, reviews: Dirty3[..2], comments: null), ("id", "PR_1"))), comments: new() { ["PR_1"] = [CommentsPage([Gone], 1)] });
		served.Install().Fetch("owner", "app");

		Assert.DoesNotContain(served.Calls, call => call.Kind == "events");
	}

	[Fact]
	public void A_pr_with_more_than_100_comments_and_no_marker_on_the_last_page_reads_the_earlier_pages_acceptance_criterion_9()
	{
		var filler = Filler();
		object[] comments = [CommentsPage(filler, 150, previous: "p1"), CommentsPage([Gone, MarkerComment()], 150)];
		var served = new Served(Repo(ExhaustedNode(comments: null)), comments: new() { ["PR_1"] = comments }, events: new() { ["PR_1"] = [EventsPage([])] });
		var node = FirstNode(served.Install().Fetch("owner", "app"));

		Assert.Equal(2, served.Calls.Count(call => call == ("comments", "PR_1")));
		var state = ToState(node, Flags, owner: "o", now: Now);
		Assert.True(state.ExhaustionMarkerNewest && state.LoopExhausted && !state.LoopUnread);
	}

	[Fact]
	public void Comments_that_cant_be_paged_in_full_leave_the_loop_unread_acceptance_criterion_9()
	{
		var filler = Filler();
		object[] comments = [CommentsPage(filler, 150, previous: "p1"), RuntimeError("502 Bad Gateway")];
		var served = new Served(Repo(ExhaustedNode(comments: null)), comments: new() { ["PR_1"] = comments }, events: new() { ["PR_1"] = [EventsPage([])] });
		var node = FirstNode(served.Install().Fetch("owner", "app"));
		var state = ToState(node, Flags, owner: "o", now: Now);

		Assert.True(state.LoopUnread && !state.LoopExhausted);
		In("502 Bad Gateway", ReadErr(served.Err));
	}

	[Fact]
	public void A_marker_on_the_last_page_needs_no_earlier_page_acceptance_criterion_9()
	{
		object[] comments = [CommentsPage([Gone, MarkerComment()], 150, previous: "p1")];
		var served = new Served(Repo(ExhaustedNode(comments: null)), comments: new() { ["PR_1"] = comments }, events: new() { ["PR_1"] = [EventsPage([])] });
		served.Install().Fetch("owner", "app");

		Assert.Equal(1, served.Calls.Count(call => call == ("comments", "PR_1")));
	}

	[Fact]
	public void A_pr_that_cant_be_exhausted_reads_one_page_of_comments_acceptance_criterion_9()
	{
		var filler = Filler();
		var node = With(PrNode(number: 1, reviews: Older, comments: null), ("id", "PR_1"));
		var served = new Served(Repo(node), comments: new() { ["PR_1"] = [CommentsPage(filler, 150, previous: "p1")] });
		served.Install().Fetch("owner", "app");

		Assert.Equal(1, served.Calls.Count(call => call == ("comments", "PR_1")));
	}

	// -- A recognised restart stays recognised; a 404 permission lookup is no write access (mpaulosky/dotfiles#381) --

	[Fact]
	public void A_restart_stays_recognised_once_copilot_really_reviews_a_later_head_acceptance_criterion_1()
	{
		// d d d, a writer's add made while Copilot couldn't review, then Copilot really reviews the new head and raises a thread.
		var state = StateOf(PrNode(reviews: [.. Dirty3, ReviewedHead()], comments: CommentsOf(Gone), events: Ev(Relabel(AddAfterRounds))));

		Assert.True(state.Rounds.Select(item => item.Dirty).SequenceEqual([true]), "the streak is 1, not 4");
		Assert.True(!state.LoopExhausted && !state.LoopUnread);
	}

	[Fact]
	public void A_restart_stays_recognised_while_a_copilot_request_is_pending_acceptance_criterion_1()
	{
		var node = With(PrNode(reviews: Dirty3, comments: CommentsOf(Gone), events: Ev(Relabel(AddAfterRounds))), ("reviewRequests", CopilotRequestedNode));
		var state = StateOf(node);

		Assert.True(state.Rounds.Length == 0 && !state.LoopExhausted && !state.LoopUnread);
	}

	[Fact]
	public void A_restart_stays_recognised_after_a_clean_copilot_review_too_acceptance_criterion_1()
	{
		var state = StateOf(PrNode(reviews: [.. Dirty3, ReviewedHead(threads: 0)], comments: CommentsOf(Gone), events: Ev(Relabel(AddAfterRounds))));

		Assert.True(state.Rounds.Select(item => item.Dirty).SequenceEqual([false]) && !state.LoopExhausted);
	}

	[Fact]
	public void The_new_loop_is_exhausted_by_three_dirty_rounds_after_the_restart_not_by_the_old_ones_acceptance_criterion_1()
	{
		Rev[] two = [RoundOf(4, minute: 5), ReviewedHead(minute: 6)];
		var state = StateOf(PrNode(reviews: [.. Dirty3, .. two], comments: CommentsOf(Gone), events: Ev(Relabel(AddAfterRounds))));
		Assert.True(state.Rounds.Length == 2 && !state.LoopExhausted);

		Rev[] three = [RoundOf(4, minute: 5), RoundOf(5, minute: 6), ReviewedHead(minute: 7)];
		state = StateOf(PrNode(reviews: [.. Dirty3, .. three], comments: CommentsOf(Gone), events: Ev(Relabel(AddAfterRounds))));
		Assert.True(state.Rounds.Length == 3 && state.LoopExhausted);
	}

	[Fact]
	public void An_add_that_pr_auto_merge_removed_is_no_restart_even_when_copilot_cant_review_now_acceptance_criterion_2()
	{
		var state = StateOf(PrNode(reviews: Dirty3, comments: CommentsOf(Gone), events: Ev(Relabel(AddAfterRounds), Removal("2026-01-02T00:04:05Z"))));

		Assert.True(state.LoopExhausted && state.Rounds.Length == 3 && !state.LoopUnread);
	}

	[Fact]
	public void An_add_that_pr_auto_merge_removed_is_no_restart_after_copilot_reviewed_the_head_acceptance_criterion_2()
	{
		var events = Ev(Relabel(AddAfterRounds), Removal("2026-01-02T00:04:05Z"));
		var state = StateOf(PrNode(reviews: [.. Dirty3, ReviewedHead()], comments: CommentsOf(Gone), events: events));

		Assert.True(state.LoopExhausted && state.Rounds.Length == 4);
	}

	[Fact]
	public void A_removal_after_the_next_round_does_not_undo_the_restart_acceptance_criterion_1()
	{
		var events = Ev(Relabel(AddAfterRounds), Removal("2026-01-02T00:30:00Z"));
		var state = StateOf(PrNode(reviews: [.. Dirty3, ReviewedHead()], comments: CommentsOf(Gone), events: events));

		Assert.True(state.Rounds.Length == 1 && !state.LoopExhausted);
	}

	[Fact]
	public void An_add_by_a_login_without_write_access_is_no_restart_after_copilot_reviewed_the_head_acceptance_criterion_2()
	{
		var state = StateOf(PrNode(reviews: [.. Dirty3, ReviewedHead()], comments: CommentsOf(Gone), events: Ev(Relabel(AddAfterRounds, writer: false))));

		Assert.True(state.LoopExhausted && state.Rounds.Length == 4);
	}

	[Fact]
	public void The_events_query_reads_unlabeled_events_too_acceptance_criterion_1()
	{
		In("UNLABELED_EVENT", Status.EventsQuery);
		In("LABELED_EVENT", Status.EventsQuery);
		In("UnlabeledEvent", Status.EventsQuery);
		In("__typename", Status.EventsQuery);
	}

	[Fact]
	public void Gh_graphql_keeps_prs_removals_of_review_claude_and_drops_other_removals_acceptance_criterion_1()
	{
		JsonNode[] events =
		[
			EventNode("2026-01-02T01:00:00Z", "colleague"),
			UneventNode("2026-01-02T01:00:05Z"),
			UneventNode("2026-01-02T01:01:00Z", login: "octo", kind: "User"),
			UneventNode("2026-01-02T01:02:00Z", label: "enhancement"),
		];
		var served = new Served(Repo(ExhaustedNode(comments: null)), comments: new() { ["PR_1"] = [CommentsPage([Gone], 1)] }, events: new() { ["PR_1"] = [EventsPage(events)] },
				permissions: new() { ["colleague"] = "write" });
		var node = FirstNode(served.Install().Fetch("owner", "app"));

		JsonEqual("""[{"createdAt": "2026-01-02T01:00:00Z", "writer": true}, {"createdAt": "2026-01-02T01:00:05Z", "removed": true}]""", J.Req(node, "labelEvents"));
		Assert.True(served.Calls.Where(call => call.Kind == "permission").Select(call => call.Id).SequenceEqual(["colleague"]), "a removal costs no permission lookup");
		// d d d at 00:01..00:03 are before the add at 01:00, which PR Auto-Merge removed five seconds later: still exhausted.
		var state = ToState(node, Flags, owner: "owner", now: Now);
		Assert.True(state.LoopExhausted);
	}

	[Fact]
	public void Gh_graphql_keeps_a_restart_that_a_later_review_of_copilot_would_have_hidden_acceptance_criterion_1()
	{
		// The add at 01:00 follows d d d; Copilot really reviews the head at 02:00 and raises a thread.
		var late = new Rev(CopilotLogin, Head, "", "2026-01-02T02:00:00Z", 1);
		var node = With(PrNode(number: 1, reviews: [.. Dirty3, late], comments: null), ("id", "PR_1"));
		var served = new Served(Repo(node), comments: new() { ["PR_1"] = [CommentsPage([Gone], 1)] },
				events: new() { ["PR_1"] = [EventsPage([EventNode("2026-01-02T01:00:00Z", "owner")])] });
		var read = FirstNode(served.Install().Fetch("owner", "app"));
		var state = ToState(read, Flags, owner: "owner", now: Now);

		Assert.True(state.Rounds.Length == 1 && !state.LoopExhausted);
	}

	[Fact]
	public void The_events_query_reads_review_requests_too_acceptance_criterion_1()
	{
		In("REVIEW_REQUESTED_EVENT", Status.EventsQuery);
		In("ReviewRequestedEvent", Status.EventsQuery);
	}

	[Fact]
	public void Gh_graphql_keeps_a_copilot_request_and_a_removal_after_it_leaves_the_restart_acceptance_criterion_1()
	{
		// d d d, the owner's add at 01:00, a request for Copilot's review at 01:00:03 (a ruleset on a push), and PR Auto-Merge's
		// removal at 01:00:05 for that request: the add still counts, and a dirty review at 02:00 is a streak of 1, not 4.
		var late = new Rev(CopilotLogin, Head, "", "2026-01-02T02:00:00Z", 1);
		var node = With(PrNode(number: 1, reviews: [.. Dirty3, late], comments: null), ("id", "PR_1"));
		JsonNode[] events = [EventNode("2026-01-02T01:00:00Z", "owner"), RequestNode("2026-01-02T01:00:03Z"),
				RequestNode("2026-01-02T01:00:04Z", login: "someone-else"), UneventNode("2026-01-02T01:00:05Z")];
		var served = new Served(Repo(node), comments: new() { ["PR_1"] = [CommentsPage([Gone], 1)] }, events: new() { ["PR_1"] = [EventsPage(events)] });
		var read = FirstNode(served.Install().Fetch("owner", "app"));

		JsonEqual("""
				[{"createdAt": "2026-01-02T01:00:00Z", "writer": true}, {"createdAt": "2026-01-02T01:00:03Z", "copilotRequested": true},
				 {"createdAt": "2026-01-02T01:00:05Z", "removed": true}]
				""", J.Req(read, "labelEvents"));
		var state = ToState(read, Flags, owner: "owner", now: Now);
		Assert.True(state.Rounds.Length == 1 && !state.LoopExhausted);
	}

	[Fact]
	public void To_state_counts_an_add_whose_removal_followed_a_copilot_request_but_not_one_that_did_not_acceptance_criterion_1()
	{
		var asked = PrNode(reviews: Dirty3, comments: CommentsOf(Gone),
				events: Ev(Relabel(), CopilotRequest("2026-01-02T02:00:02Z"), Removal("2026-01-02T02:00:05Z")));
		Assert.True(!StateOf(asked).LoopExhausted && !StateOf(asked).ExhaustionMarkerNewest);
		var plain = PrNode(reviews: Dirty3, comments: CommentsOf(Gone), events: Ev(Relabel(), Removal()));
		Assert.True(StateOf(plain).LoopExhausted);
	}

	// A permission lookup is `gh api repos/<owner>/<name>/collaborators/<login>/permission`; these fake gh itself (FakeGh), so they
	// hold whatever way StatusReader reads the failure.

	[Fact]
	public void A_404_from_the_permission_lookup_is_no_write_access_acceptance_criterion_3()
	{
		var gh = NotFound404();
		var reader = LookupWith(gh);

		Assert.True(reader.WriterAccess("owner", "app", User) is false);
	}

	[Fact]
	public void A_404_is_cached_for_the_run_like_a_found_permission_acceptance_criterion_3()
	{
		var gh = NotFound404();
		var reader = LookupWith(gh);

		Assert.True(reader.WriterAccess("owner", "app", User) is false);
		Assert.True(reader.WriterAccess("owner", "app", User) is false);
		Assert.Equal(1, gh.Calls);
	}

	[Fact]
	public void A_404_for_one_login_leaves_another_logins_lookup_alone_acceptance_criterion_3()
	{
		var reader = LookupWith(NotFound404());
		Assert.True(reader.WriterAccess("owner", "app", User) is false);

		reader.Rest = new Gh(new FakeGh(stdout: """{"permission":"write"}""", code: 0).Run).Rest;
		Assert.True(reader.WriterAccess("owner", "app", new JsonObject { ["login"] = "colleague", ["__typename"] = "User" }) is true);
	}

	[Fact]
	public void A_5xx_a_network_error_and_a_rate_limit_are_still_unread_and_not_cached_acceptance_criterion_3()
	{
		var failures = new (string Name, string Stderr, string Stdout)[]
		{
			("500", "gh: Internal Server Error (HTTP 500)\n", """{"message":"Server Error","status":"500"}"""),
			("502", "gh: Bad Gateway (HTTP 502)\n", ""),
			("503", "gh: Service Unavailable (HTTP 503)\n", ""),
			("network", "error connecting to api.github.com\ncheck your internet connection or https://githubstatus.com\n", ""),
			("403 rate limit", "gh: API rate limit exceeded for user ID 1. (HTTP 403)\n", """{"message":"API rate limit exceeded for user ID 1.","status":"403"}"""),
			("429", "gh: Too Many Requests (HTTP 429)\n", ""),
		};
		foreach (var (name, stderr, stdout) in failures)
		{
			var gh = new FakeGh(stderr, stdout);
			var reader = LookupWith(gh);

			Assert.True(reader.WriterAccess("owner", "app", User) is null, name);
			Assert.True(reader.WriterAccess("owner", "app", User) is null && gh.Calls == 2, $"{name}: a failed lookup isn't kept");
		}
	}

	[Fact]
	public void A_404_does_not_leave_the_loop_unread_but_a_5xx_does_acceptance_criterion_3()
	{
		JsonNode[] events = [EventNode("2026-01-02T01:00:00Z", "ghost")];

		(JsonNode Node, PrState State) Read(FakeGh failure)
		{
			var node = With(PrNode(number: 1, reviews: Dirty3, comments: null), ("id", "PR_1"));
			var reader = new Served(Repo(node), comments: new() { ["PR_1"] = [CommentsPage([Gone], 1)] }, events: new() { ["PR_1"] = [EventsPage(events)] }).Install();
			reader.Rest = new Gh(failure.Run).Rest;
			var readNode = FirstNode(reader.Fetch("owner", "app"));
			return (readNode, ToState(readNode, Flags, owner: "owner", now: Now));
		}

		var (node, state) = Read(NotFound404());
		JsonEqual("""[{"createdAt": "2026-01-02T01:00:00Z", "writer": false}]""", J.Req(node, "labelEvents"));
		Assert.True(state.LoopExhausted && !state.LoopUnread);

		(node, state) = Read(new FakeGh(stderr: "gh: Internal Server Error (HTTP 500)\n"));
		JsonEqual("""[{"createdAt": "2026-01-02T01:00:00Z", "writer": null}]""", J.Req(node, "labelEvents"));
		Assert.True(state.LoopUnread && !state.LoopExhausted);
	}
}
