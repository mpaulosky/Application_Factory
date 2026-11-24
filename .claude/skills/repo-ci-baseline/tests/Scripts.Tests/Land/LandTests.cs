using System.Globalization;
using System.Text.Json.Nodes;

// Tests for github/LandCore.cs: fixture polls in, gh calls and printed changes out. No GitHub.
public partial class LandTests
{
	static readonly string HEAD = "1234567" + new string('0', 33);
	static readonly string NEW = "abcdef0" + new string('0', 33);
	static string[][] COPILOT => [["copilot-pull-request-reviewer", HEAD]];
	static string[] NOTE => ["copilot-pull-request-reviewer", HEAD, "Copilot was unable to review this pull request because the user who "
			+ "requested the review has reached their quota limit."];
	// Claude backs Copilot up on a head Copilot answered with its quota note, or whose request GitHub dropped (once the ask has settled).
	static string[][] CLAUDE => [NOTE, ["github-actions", HEAD, "<!-- claude-review -->\n**Claude Review**"]];
	static JsonNode[] GREEN => [JsonNode.Parse("""{"__typename": "CheckRun", "name": "Template tests", "status": "COMPLETED", "conclusion": "SUCCESS", "startedAt": "2026-10-07T10:00:00Z"}""")!];
	static JsonNode[] RUNNING => [JsonNode.Parse("""{"__typename": "CheckRun", "name": "Template tests", "status": "IN_PROGRESS", "conclusion": null, "startedAt": "2026-10-07T10:00:00Z"}""")!];

	// SCOPES, built fresh for each Fake, so a test that adds a repo to it changes no other test's.
	static OrderedDictionary<string, string> Scopes() => new() { ["o/app"] = "full", ["o/dots"] = "repo-settings-only" };

	// Copilot reviewed an older commit only: the head has no review, no note and (unless a test says) no request.
	static string[][] OLDER => [["copilot-pull-request-reviewer", NEW]];
	// The two calls that ask Copilot once (mpaulosky/dotfiles#360): the record comment, then the request.
	static readonly string RECORD_CALL = "gh pr comment 7 -R o/app --body " + Landing.CopilotAskedComment;
	const string REQUEST_CALL = "gh pr edit 7 -R o/app --add-reviewer @copilot";
	const string LABEL_CALL = "gh pr edit 7 -R o/app --add-label review:claude";

	// An ISO-8601 time `seconds` ago, as GraphQL reports createdAt.
	static string Ago(double seconds) =>
		DateTime.UtcNow.AddSeconds(-seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

	// The comment that records Copilot was asked, posted `age` seconds ago: (login, body, createdAt).
	static (string Login, string Body, string CreatedAt) Record(double age = 3600, string login = "github-actions") =>
		(login, Landing.CopilotAskedComment, Ago(age));

	// unreadComments is the Python's comments=None: a read that failed.
	static JsonNode Node(long number = 7, string title = "feat: Something", string branch = "feat/x", string? head = null, bool draft = false,
			string mergeState = "CLEAN", string[][]? reviews = null, object[]? threads = null, JsonNode[]? contexts = null, bool armed = false,
			string[]? labels = null, string[]? requested = null, (string Login, string Body, string CreatedAt)[]? comments = null,
			bool unreadComments = false, (string CreatedAt, bool? Writer)[]? events = null)
	{
		head ??= HEAD;
		reviews ??= [];
		threads ??= [];
		contexts ??= [];
		labels ??= [];
		requested ??= [];
		comments ??= [];
		events ??= [];
		var node = new JsonObject
		{
			["number"] = number,
			["title"] = title,
			["state"] = "OPEN",
			["isDraft"] = draft,
			["isCrossRepository"] = false,
			["headRefName"] = branch,
			["headRefOid"] = head,
			["mergeStateStatus"] = mergeState,
			["autoMergeRequest"] = armed ? new JsonObject { ["enabledAt"] = "2026-10-07T10:00:00Z" } : null,
			["labels"] = new JsonObject { ["nodes"] = new JsonArray([.. labels.Select(label => (JsonNode)new JsonObject { ["name"] = label })]) },
			["reviewRequests"] = new JsonObject
			{
				["nodes"] = new JsonArray([.. requested.Select(login => (JsonNode)new JsonObject { ["requestedReviewer"] = new JsonObject { ["login"] = login } })]),
			},
			// A review is (login, oid[, body[, flags...]]); the flag "merge" makes its commit a merge commit and "dirty" gives the
			// review an inline thread. Each is submitted a minute after the one before it.
			["reviews"] = new JsonObject
			{
				["nodes"] = new JsonArray([.. reviews.Select((review, index) => (JsonNode)new JsonObject
				{
					["author"] = new JsonObject { ["login"] = review[0] },
					["commit"] = new JsonObject
					{
						["oid"] = review[1],
						["parents"] = new JsonObject { ["totalCount"] = review.Skip(3).Contains("merge") ? 2 : 1 },
					},
					["submittedAt"] = $"2026-01-02T00:{index:00}:00Z",
					["comments"] = new JsonObject { ["totalCount"] = review.Skip(3).Contains("dirty") ? 1 : 0 },
					["body"] = review.Length > 2 ? review[2] : "",
				})]),
			},
			// The PR's review:claude label events as (createdAt, whether the login that added it has write access); None is a read
			// that failed (mpaulosky/dotfiles#378).
			["labelEvents"] = new JsonArray([.. events.Select(e => (JsonNode)new JsonObject { ["createdAt"] = e.CreatedAt, ["writer"] = e.Writer })]),
			// A thread is True/False (resolved or not) or a full reviewThreads node.
			["reviewThreads"] = new JsonObject
			{
				["nodes"] = new JsonArray([.. threads.Select(thread => thread is bool resolved ? new JsonObject { ["isResolved"] = resolved } : ((JsonNode)thread).DeepClone())]),
			},
			["commits"] = new JsonObject
			{
				["nodes"] = new JsonArray(new JsonObject
				{
					["commit"] = new JsonObject
					{
						["statusCheckRollup"] = contexts.Length > 0
							? new JsonObject { ["contexts"] = new JsonObject { ["nodes"] = new JsonArray([.. contexts.Select(c => c.DeepClone())]) } }
							: null,
					},
				}),
			},
			// The PR's comments as (login, body, createdAt); None is a read that failed.
			["comments"] = unreadComments ? null : new JsonObject
			{
				["totalCount"] = comments.Length,
				["nodes"] = new JsonArray([.. comments.Select(c => (JsonNode)new JsonObject
				{
					["author"] = new JsonObject { ["login"] = c.Login },
					["body"] = c.Body,
					["createdAt"] = c.CreatedAt,
				})]),
			},
		};
		// Read back as gh's JSON is read, so every value is what a parsed reply holds.
		return JsonNode.Parse(node.ToJsonString())!;
	}

	static List<JsonNode> Prs(params JsonNode[] nodes) => [.. nodes];

	// GitHub as the tests set it: open PRs per repo, final states, and a log of gh calls.
	sealed class Fake(Dictionary<string, object> open, Dictionary<(string, long), string>? final = null, string[]? fail = null,
			(string, long)[]? dropped = null, string[]? failCalls = null)
	{
		public readonly Dictionary<string, object> Open = open; // {repo: [node, ...], or the exception its read throws}
		public readonly Dictionary<(string, long), string> Final = final ?? []; // {(repo, number): "MERGED" | "CLOSED"}
		public readonly HashSet<string> Fail = [.. fail ?? []]; // gh subcommands that fail
		public readonly HashSet<string> FailCalls = [.. failCalls ?? []]; // whole gh calls that fail
		public readonly HashSet<(string, long)> Dropped = [.. dropped ?? []]; // {(repo, number)} whose Copilot request GitHub drops
		public readonly List<string> Calls = [];
		public readonly List<(string, long)> ReadBack = [];
		public readonly OrderedDictionary<string, string> Scopes = LandTests.Scopes();
		public double Now;
		public Func<string, long, bool>? CopilotRequested; // null: read back from Dropped

		bool ReadBackCopilot(string repo, long number)
		{
			ReadBack.Add((repo, number));
			return !Dropped.Contains((repo, number));
		}

		public JsonNode Fetch(string owner, string name)
		{
			var repo = $"{owner}/{name}";
			if (Open[repo] is Exception error)
				throw error;
			var nodes = new JsonArray([.. ((List<JsonNode>)Open[repo]).Select(n => n.DeepClone())]);
			return JsonNode.Parse(new JsonObject { ["repository"] = new JsonObject { ["pullRequests"] = new JsonObject { ["nodes"] = nodes } } }.ToJsonString())!;
		}

		public string PrState(string repo, long number) => Final.GetValueOrDefault((repo, number), "OPEN");

		public (bool Ok, string Message) Run(IReadOnlyList<string> argv)
		{
			var call = string.Join(" ", argv);
			Calls.Add(call);
			var failed = Fail.Contains(argv[2]) || FailCalls.Contains(call);
			return (!failed, failed ? "boom" : "");
		}

		public Lander Lander(OrderedDictionary<string, HashSet<long>?> targets, TextWriter output, TextWriter err)
		{
			var copilotRequested = CopilotRequested ?? ReadBackCopilot;
			return new Lander(targets, Scopes, output, err)
			{
				Fetch = Fetch,
				PrState = PrState,
				Run = Run,
				CopilotRequested = copilotRequested,
				Clock = () => Now,
			};
		}
	}

	static Fake MakeFake(Dictionary<string, object> open, Dictionary<(string, long), string>? final = null, string[]? fail = null,
			(string, long)[]? dropped = null, string[]? failCalls = null) =>
		new(open, final, fail, dropped, failCalls);

	// {repo: None} for each repo: every open PR.
	static OrderedDictionary<string, HashSet<long>?> All(params IEnumerable<string> repos) =>
		new(repos.Select(repo => KeyValuePair.Create(repo, (HashSet<long>?)null)));

	static (Lander Lander, StringWriter Out, StringWriter Err) Start(Fake fake, OrderedDictionary<string, HashSet<long>?> targets)
	{
		StringWriter output = new(), err = new();
		return (fake.Lander(targets, output, err), output, err);
	}

	static string Printed(StringWriter output)
	{
		var text = output.ToString();
		output.GetStringBuilder().Clear();
		return text;
	}

	static int Count(string text, char c) => text.Count(ch => ch == c);

	// str.splitlines() for text whose lines end in \n.
	static string[] SplitLines(string text)
	{
		var lines = text.Split('\n');
		return text.EndsWith('\n') ? lines[..^1] : lines;
	}

	// -- The action dispatch --

	[Fact]
	public void A_baseline_repos_reviewed_pr_is_left_to_its_pr_auto_merge()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: COPILOT)) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Contains("-> leave to PR Auto-Merge", Printed(output));
		Assert.Empty(fake.Calls);
	}

	[Fact]
	public void Dotfiles_leaves_a_reviewed_pr_to_its_pr_auto_merge()
	{
		var fake = MakeFake(new() { ["o/dots"] = Prs(Node(reviews: COPILOT, contexts: GREEN)) });
		var (lander, output, _) = Start(fake, All("o/dots"));
		lander.Poll();
		Assert.Contains("-> leave to PR Auto-Merge", Printed(output));
		Assert.Empty(fake.Calls);
	}

	[Fact]
	public void A_behind_branch_is_updated_once_per_head_and_copilot_isnt_asked()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(mergeState: "BEHIND", reviews: COPILOT)) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Equal(["gh pr update-branch 7 -R o/app"], fake.Calls);
		Assert.Empty(fake.ReadBack);
		var text = Printed(output);
		Assert.True(text.Contains("  ran: gh pr update-branch 7 -R o/app", StringComparison.Ordinal) && !text.Contains("  failed:", StringComparison.Ordinal), text);
		// Still BEHIND on the same head (the API lags) with a new thread: printed, not redone.
		fake.Open["o/app"] = Prs(Node(mergeState: "BEHIND", reviews: COPILOT, threads: [false]));
		lander.Poll();
		Assert.Contains("-> update branch", Printed(output));
		Assert.Single(fake.Calls);
	}

	[Fact]
	public void Update_branch_is_just_update_branch_now_that_it_asks_nobody()
	{
		Assert.Equal("update branch", Landing.UpdateBranch);
		Assert.Contains(Landing.UpdateBranch, Landing.Actions);
		Assert.Equal([["gh", "pr", "update-branch", "7", "-R", "o/app"]], Land.Commands(Landing.UpdateBranch, "o/app", 7, HEAD));
	}

	[Fact]
	public void Requesting_copilot_records_the_ask_in_a_comment_first_and_then_requests_the_review()
	{
		Assert.Equal([
			["gh", "pr", "comment", "7", "-R", "o/app", "--body", Landing.CopilotAskedComment],
			["gh", "pr", "edit", "7", "-R", "o/app", "--add-reviewer", "@copilot"]], Land.Commands(Landing.RequestCopilot, "o/app", 7, HEAD));
		Assert.Contains(Landing.CopilotAskedMarker, Landing.CopilotAskedComment, StringComparison.Ordinal);
	}

	[Fact]
	public void A_copilot_request_github_dropped_is_warned_about_and_backed_up_by_claude()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN)) }, dropped: [("o/app", 7)]);
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Equal([RECORD_CALL, REQUEST_CALL], fake.Calls);
		Assert.Equal([("o/app", 7L)], fake.ReadBack);
		var text = Printed(output);
		Assert.Contains("-> request Copilot", text);
		Assert.Contains("  warning: Copilot's review request didn't register on #7 (GitHub drops it once the Copilot code review budget "
				+ "is used up); review:claude backs Copilot up for this PR", text);
		Assert.DoesNotContain("  failed:", text);
		// The same run polls again: the record is on the PR now, 2 s old. PR Auto-Merge takes review:claude off an ask that
		// young, so land.cs waits out the 30 seconds too instead of adding a label that is removed and never added again.
		fake.Open["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, comments: [Record(2)]));
		lander.Poll();
		Assert.DoesNotContain("-> add review:claude", Printed(output));
		Assert.Equal([RECORD_CALL, REQUEST_CALL], fake.Calls);
		fake.Open["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, comments: [Record(31)]));
		lander.Poll();
		Assert.Contains("-> add review:claude", Printed(output));
		Assert.Equal([LABEL_CALL], fake.Calls.Skip(2));
		// A later head: the record is old, so Copilot is not asked again and Claude is called in.
		fake.Open["o/app"] = Prs(Node(head: NEW, reviews: OLDER[..0], contexts: GREEN, comments: [Record(3600)]));
		lander.Poll();
		Assert.Contains("-> add review:claude", Printed(output));
		Assert.Equal([LABEL_CALL], fake.Calls.Skip(3));
	}

	[Fact]
	public void Claude_is_called_in_once_copilot_posted_its_quota_note_and_checks_are_green()
	{
		string[][] note = [["copilot-pull-request-reviewer", HEAD, NOTE[2]]];
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: note, contexts: RUNNING)) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Contains("waiting for checks before calling in Claude: Template tests", Printed(output));
		Assert.Empty(fake.Calls);
		fake.Open["o/app"] = Prs(Node(reviews: note, contexts: GREEN));
		lander.Poll();
		Assert.Equal(["gh pr edit 7 -R o/app --add-label review:claude"], fake.Calls);
		Assert.Contains("-> add review:claude", Printed(output));
		lander.Poll();
		Assert.Single(fake.Calls);
	}

	[Fact]
	public void Copilot_is_asked_first_for_a_head_copilot_reviewed_before_and_only_once()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN)) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Equal([RECORD_CALL, REQUEST_CALL], fake.Calls);
		Assert.Contains("-> request Copilot", Printed(output));
		// It registered: Copilot is requested, so nothing more, and never Claude.
		fake.Open["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, requested: ["Copilot"], comments: [Record(5)]));
		lander.Poll();
		Assert.Contains("Copilot is requested", Printed(output));
		Assert.Equal(2, fake.Calls.Count);
		// Long after, with the request gone (answered by nothing): no second ask, Claude is called in.
		fake.Open["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, comments: [Record(3600)]));
		lander.Poll();
		Assert.Equal([LABEL_CALL], fake.Calls.Skip(2));
	}

	[Fact]
	public void A_copilot_request_github_refused_outright_is_warned_about()
	{
		// TicketManager#141: "Could not add requested reviewers". No request is
		// left to read back, so it's handled like a dropped one.
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN)) }, dropped: [("o/app", 7)], failCalls: [REQUEST_CALL]);
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Equal([RECORD_CALL, REQUEST_CALL], fake.Calls);
		Assert.Equal([("o/app", 7L)], fake.ReadBack);
		var text = Printed(output);
		Assert.Contains($"  warning: {REQUEST_CALL} failed (boom)", text);
		Assert.Contains("review:claude backs Copilot up for this PR", text);
	}

	[Fact]
	public void No_request_is_made_when_the_record_comment_fails()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN)) }, fail: ["comment"]);
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.True(fake.Calls.Count == 1 && fake.Calls[0].StartsWith("gh pr comment 7 -R o/app --body ", StringComparison.Ordinal), string.Join("\n", fake.Calls));
		Assert.Empty(fake.ReadBack);
		Assert.Contains("  failed: gh pr comment 7 -R o/app", Printed(output));
	}

	[Fact]
	public void Claude_isnt_called_in_while_copilot_is_requested_or_the_label_is_on()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(contexts: GREEN, requested: ["copilot-pull-request-reviewer"])) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Contains("Copilot is requested", Printed(output));
		Assert.Empty(fake.Calls);
		// Copilot asked long ago and nothing pending: it can't review, so the label stands and Claude is waited for.
		fake.Open["o/app"] = Prs(Node(contexts: GREEN, labels: ["review:claude"], comments: [Record(3600)]));
		lander.Poll();
		Assert.Contains("review:claude is on", Printed(output));
		Assert.Empty(fake.Calls);
	}

	[Fact]
	public void Claude_is_called_in_for_a_third_round_to_review_a_fix()
	{
		// Two rounds aren't MAX_CLAUDE_ROUNDS (3): a fix for the second's findings is reviewed too (#193).
		string[][] rounds = [.. new[] { new string('1', 40), new string('2', 40) }.Select(sha => new[] { "github-actions", sha, "<!-- claude-review -->\n**Claude Review**" })];
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: [NOTE, .. rounds], contexts: GREEN)) });
		var (lander, _, _) = Start(fake, All("o/app"));
		lander.Poll();
		lander.Poll();
		Assert.Equal(["gh pr edit 7 -R o/app --add-label review:claude"], fake.Calls);
	}

	[Fact]
	public void Claudes_reviews_of_two_commits_and_a_merge_from_main_are_no_reason_to_stop_calling_it_in()
	{
		// Claude reviewed two commits and a merge from main: the merge is no round, and clean rounds never end the loop,
		// so the head gets the label (mpaulosky/dotfiles#375, which replaces #193's "the owner decides").
		var body = "<!-- claude-review -->\n**Claude Review**";
		string[][] rounds = [["github-actions", new string('1', 40), body], ["github-actions", new string('2', 40), body, "merge"], ["github-actions", new string('3', 40), body]];
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: [NOTE, .. rounds], contexts: GREEN)) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		var text = Printed(output); // printed() empties the buffer, so read it once
		Assert.DoesNotContain("the owner decides", text);
		Assert.Equal([LABEL_CALL], fake.Calls);
	}

	[Fact]
	public void A_loop_of_three_dirty_rounds_is_left_to_pr_auto_merge_with_the_threads_left_open()
	{
		var body = "<!-- claude-review -->\n**Claude Review**";
		string[][] reviews = [NOTE, .. "123".Select(sha => new[] { "github-actions", new string(sha, 40), body, "dirty" })];
		// Copilot's threads: a person's would hold the merge (mpaulosky/dotfiles#378).
		object[] threads = [.. Enumerable.Repeat<object>(ThreadBy("copilot-pull-request-reviewer", "Bot"), 2)];
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: reviews, contexts: GREEN, threads: threads)) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		var text = Printed(output);
		Assert.True(text.Contains("-> leave to PR Auto-Merge (review loop exhausted", StringComparison.Ordinal) && text.Contains("2 thread(s) left open", StringComparison.Ordinal), text);
		Assert.Empty(fake.Calls);
	}

	[Fact]
	public void A_dirty_round_after_a_clean_one_doesnt_exhaust_the_loop_and_gets_the_label()
	{
		var body = "<!-- claude-review -->\n**Claude Review**";
		string[][] reviews = [NOTE, ["github-actions", new string('1', 40), body, "dirty"], ["github-actions", new string('2', 40), body, "dirty"],
				["github-actions", new string('3', 40), body], ["github-actions", new string('4', 40), body, "dirty"]];
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: reviews, contexts: GREEN)) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.DoesNotContain("review loop exhausted", Printed(output));
		Assert.Equal([LABEL_CALL], fake.Calls);
	}

	[Fact]
	public void Claude_isnt_called_in_while_the_exhaustion_comment_is_the_newest_word_but_is_once_the_owner_re_adds_the_label()
	{
		var marker = ("github-actions", "Review loop exhausted.\n\n" + Landing.ExhaustedMarker + " " + HEAD + " -->", Ago(3600));
		// Copilot's quota note is on the head and the checks are green, but the label is the owner's to add after the comment.
		var body = "<!-- claude-review -->\n**Claude Review**";
		var dirty3 = "123".Select(sha => new[] { "github-actions", new string(sha, 40), body, "dirty" });
		string[][] cleanAfter = [NOTE, .. dirty3, ["github-actions", new string('9', 40), "<!-- claude-review -->\nNo findings."]];
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: cleanAfter, contexts: GREEN, comments: [marker])) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		var text = Printed(output);
		Assert.True(text.Contains("-> wait", StringComparison.Ordinal) && text.Contains("review:claude", StringComparison.Ordinal), text);
		Assert.Empty(fake.Calls);
		// The owner adds review:claude after the exhausting round (and the loop's rounds start over).
		fake.Open["o/app"] = Prs(Node(reviews: cleanAfter, contexts: GREEN, comments: [marker], events: [(Ago(1800), true)]));
		lander.Poll();
		Assert.Equal([LABEL_CALL], fake.Calls);
	}

	[Fact]
	public void A_failed_review_claude_label_is_reported_as_failed()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: [NOTE], contexts: GREEN)) },
				failCalls: ["gh pr edit 7 -R o/app --add-label review:claude"]);
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		lander.Poll();
		Assert.Contains("  failed: gh pr edit 7 -R o/app --add-label review:claude (boom)", Printed(output));
	}

	[Fact]
	public void A_copilot_request_whose_read_back_fails_is_not_asked_again_next_poll()
	{
		// Copilot is asked once per PR (mpaulosky/dotfiles#360): the record now on the PR, not a retry here, decides what's next.
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN)) });
		fake.CopilotRequested = (_, _) => throw new InvalidOperationException("HTTP 502");
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Contains("couldn't be read back on #7 (HTTP 502)", Printed(output));
		lander.Poll();
		lander.Poll();
		Assert.Equal([RECORD_CALL, REQUEST_CALL], fake.Calls);
	}

	[Fact]
	public void A_copilot_request_that_cant_be_read_back_is_reported_as_failed()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN)) });
		fake.CopilotRequested = (_, _) => throw new InvalidOperationException("HTTP 502");
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Equal(2, fake.Calls.Count);
		Assert.Contains("  failed: Copilot's review request couldn't be read back on #7 (HTTP 502); request it at "
				+ "https://github.com/o/app/pull/7", Printed(output));
	}

	// -- --once asks once and falls back without remembering anything between runs (mpaulosky/dotfiles#360) --

	// read_repo_list() is left as it is in the Python: it reads the skill's own repos.txt (the lander, not the list, decides).
	static (int Code, string Text) Once(Fake fake, string text = "o/app#7")
	{
		var (lander, output, err) = Start(fake, new() { ["o/app"] = [7] });
		var code = Land.Run(["--once", text], output, err, lander, readRepoList: () => Repos.Read(Skill.Path("github")));
		return (code, Printed(output));
	}

	[Fact]
	public void Once_adds_review_claude_after_a_dropped_request_with_nothing_remembered_from_the_run_that_asked()
	{
		// Run 1 asks: the record comment, then the request, read back as dropped.
		var first = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN)) }, dropped: [("o/app", 7)]);
		Assert.Equal(0, Once(first).Code);
		Assert.Equal([RECORD_CALL, REQUEST_CALL], first.Calls);
		// Run 2 is a new process: all it has is the PR, which now carries the record (31 s old) and no request.
		var second = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, comments: [Record(31)])) });
		var (code, text) = Once(second);
		Assert.Equal(0, code);
		Assert.Equal([LABEL_CALL], second.Calls);
		Assert.Contains("-> add review:claude", text);
		Assert.Empty(second.ReadBack);
	}

	[Fact]
	public void Once_waits_while_the_record_is_under_30_seconds_old_and_asks_nothing()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, comments: [Record(10)])) });
		var (code, text) = Once(fake);
		Assert.Equal(0, code);
		Assert.Empty(fake.Calls);
		Assert.True(text.Contains("-> wait", StringComparison.Ordinal) && text.Contains("Copilot was asked", StringComparison.Ordinal), text);
	}

	[Fact]
	public void Once_never_asks_a_second_time_whoever_ran_before()
	{
		foreach (var age in new double[] { 5, 31, 3600 })
		{
			var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, comments: [Record(age)])) });
			Once(fake);
			Assert.True(!fake.Calls.Contains(RECORD_CALL) && !fake.Calls.Contains(REQUEST_CALL), age.ToString(CultureInfo.InvariantCulture));
		}
	}

	[Fact]
	public void Once_waits_while_copilots_request_is_pending_whatever_the_age_of_the_record()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, requested: ["Copilot"], comments: [Record(3600)])) });
		var (_, text) = Once(fake);
		Assert.Empty(fake.Calls);
		Assert.Contains("Copilot is requested", text);
	}

	[Fact]
	public void Once_does_not_ask_or_add_the_label_when_the_comments_could_not_be_read()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, unreadComments: true)) });
		Once(fake);
		Assert.Empty(fake.Calls);
	}

	[Fact]
	public void Once_ignores_a_record_from_a_stranger_and_asks()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, comments: [Record(3600, login: "stranger")])) });
		Once(fake);
		Assert.Equal([RECORD_CALL, REQUEST_CALL], fake.Calls);
	}

	[Fact]
	public void Once_trusts_a_record_from_the_repo_owner()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, comments: [Record(3600, login: "o")])) });
		Once(fake);
		Assert.Equal([LABEL_CALL], fake.Calls);
	}

	[Fact]
	public void A_new_head_after_an_update_isnt_asked_for_either()
	{
		// Behind main with a record on the PR: the update makes a new head; Copilot is not asked about it.
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(mergeState: "BEHIND", reviews: OLDER, comments: [Record(3600)])) });
		var (lander, _, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Equal(["gh pr update-branch 7 -R o/app"], fake.Calls);
		fake.Open["o/app"] = Prs(Node(head: NEW, reviews: [], contexts: GREEN, comments: [Record(3600)]));
		lander.Poll();
		Assert.Equal([LABEL_CALL], fake.Calls.Skip(1));
	}

	[Fact]
	public void Dotfiles_leaves_a_claude_reviewed_pr_to_its_pr_auto_merge()
	{
		var fake = MakeFake(new() { ["o/dots"] = Prs(Node(reviews: CLAUDE, contexts: GREEN)) });
		var (lander, output, _) = Start(fake, All("o/dots"));
		lander.Poll();
		Assert.Contains("-> leave to PR Auto-Merge", Printed(output));
		Assert.Empty(fake.Calls);
	}

	[Fact]
	public void A_draft_is_marked_ready_only_when_asked()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(draft: true)) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Contains("-> wait (draft)", Printed(output));
		Assert.Empty(fake.Calls);

		fake = MakeFake(new() { ["o/app"] = Prs(Node(draft: true)) });
		(lander, output, _) = Start(fake, All("o/app"));
		lander.WantReady = true;
		lander.Poll();
		Assert.Equal(["gh pr ready 7 -R o/app"], fake.Calls);
	}

	[Fact]
	public void A_release_blog_pr_that_opens_is_watched_and_armed_unless_its_workflow_armed_it()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: COPILOT)) });
		var (lander, output, _) = Start(fake, new() { ["o/app"] = [7] });
		lander.Poll();
		Printed(output);

		var blog = Node(number: 8, title: "docs: Add release blog for PR #7 [skip-release]", branch: "docs/release-notes",
				head: NEW, contexts: RUNNING);
		fake.Open["o/app"] = Prs(blog);
		fake.Final[("o/app", 7)] = "MERGED";
		lander.Poll();
		var text = Printed(output);
		Assert.True(text.Contains("app#7 merged", StringComparison.Ordinal) && text.Contains("app#8 docs: Add release blog", StringComparison.Ordinal)
				&& text.Contains("-> arm auto-merge", StringComparison.Ordinal), text);
		Assert.Equal([$"gh pr merge 8 -R o/app --auto --squash --match-head-commit {NEW}"], fake.Calls);

		var armed = Node(number: 9, title: "docs: Backfill blog posts [skip-release]", branch: "docs/backfill-blog-posts",
				armed: true);
		fake.Open["o/app"] = Prs(armed);
		fake.Final[("o/app", 8)] = "MERGED";
		lander.Poll();
		Assert.Contains("-> nothing (auto-merge armed)", Printed(output));
		Assert.Single(fake.Calls);
	}

	[Fact]
	public void A_failed_call_is_reported_and_stops_the_rest_of_that_action()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(mergeState: "BEHIND")) }, fail: ["update-branch"]);
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Contains("  failed: gh pr update-branch 7 -R o/app (boom)", Printed(output));
		Assert.Equal(["gh pr update-branch 7 -R o/app"], fake.Calls);
	}

	[Fact]
	public void Dry_run_prints_the_calls_and_makes_none()
	{
		var blog = Node(number: 8, title: "docs: Release notes [skip-release]", branch: "docs/release-notes", contexts: GREEN);
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(mergeState: "BEHIND"), blog) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.DryRun = true;
		lander.Poll();
		var text = Printed(output);
		Assert.Contains("  would run: gh pr update-branch 7 -R o/app", text);
		Assert.DoesNotContain("--add-reviewer", text);
		Assert.Contains($"  would run: gh pr merge 8 -R o/app --auto --squash --match-head-commit {HEAD}", text);
		Assert.Empty(fake.Calls);
		Assert.Empty(fake.ReadBack);
		lander.Poll();
		Assert.Equal("", Printed(output));
	}

	// -- Resolved bot threads nobody replied to --

	// A resolved Copilot thread at src/a.py:12, then one reply per login.
	static JsonNode BotThread(params string[] replies)
	{
		var comments = new JsonArray(new JsonObject
		{
			["url"] = "u1",
			["author"] = new JsonObject { ["login"] = "copilot-pull-request-reviewer", ["__typename"] = "Bot" },
			["pullRequestReview"] = new JsonObject { ["body"] = "" },
		});
		foreach (var login in replies)
			comments.Add(new JsonObject
			{
				["url"] = "u2",
				["author"] = new JsonObject { ["login"] = login, ["__typename"] = "User" },
				["pullRequestReview"] = null,
			});
		return new JsonObject
		{
			["isResolved"] = true,
			["path"] = "src/a.py",
			["line"] = 12,
			["originalLine"] = 12,
			["comments"] = new JsonObject { ["totalCount"] = comments.Count, ["nodes"] = comments },
		};
	}

	[Fact]
	public void The_line_lists_a_resolved_bot_thread_with_no_reply()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: COPILOT, threads: [BotThread()])) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		var text = Printed(output);
		Assert.True(Count(text, '\n') == 1 && text.TrimEnd('\n').EndsWith("notice: resolved without a reply: src/a.py:12", StringComparison.Ordinal), text);
		Assert.Contains("-> leave to PR Auto-Merge", text);
	}

	[Fact]
	public void A_poll_reprints_the_line_when_only_the_unreplied_threads_change_and_makes_no_call()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: COPILOT, threads: [BotThread()])) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Printed(output);
		lander.Poll();
		Assert.Equal("", Printed(output));
		// Someone replies: the notice goes, and that is a change worth a line.
		fake.Open["o/app"] = Prs(Node(reviews: COPILOT, threads: [BotThread("octo")]));
		lander.Poll();
		var text = Printed(output);
		Assert.True(Count(text, '\n') == 1 && !text.Contains("notice", StringComparison.Ordinal) && text.Contains("-> leave to PR Auto-Merge", StringComparison.Ordinal), text);
		// A thread resolved without a reply appears: another line, with the same action and no gh call.
		fake.Open["o/app"] = Prs(Node(reviews: COPILOT, threads: [BotThread("octo"), BotThread()]));
		lander.Poll();
		text = Printed(output);
		Assert.True(Count(text, '\n') == 1 && text.Contains("notice: resolved without a reply: src/a.py:12", StringComparison.Ordinal), text);
		Assert.Empty(fake.Calls);
	}

	// -- Change-only output --

	[Fact]
	public void Nothing_is_printed_when_nothing_changed()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(contexts: RUNNING, requested: ["Copilot"])) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Equal(1, Count(Printed(output), '\n'));
		for (var i = 0; i < 3; i++)
		{
			lander.Poll();
			Assert.Equal("", Printed(output));
		}
	}

	[Fact]
	public void Only_the_pr_that_changed_is_printed()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(number: 7), Node(number: 8)) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Printed(output);
		fake.Open["o/app"] = Prs(Node(number: 7), Node(number: 8, reviews: COPILOT));
		lander.Poll();
		var text = Printed(output);
		Assert.True(Count(text, '\n') == 1 && text.StartsWith("app#8", StringComparison.Ordinal), text);
	}

	[Fact]
	public void No_open_pr_is_one_line_and_exits_instead_of_repeating()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(), ["o/dots"] = Prs() });
		StringWriter output = new(), err = new();
		List<double> sleeps = [];
		var code = Land.Run([], output, err, fake.Lander(All(fake.Scopes.Keys), output, err), sleeps.Add, () => fake.Scopes);
		Assert.Equal(0, code);
		Assert.Equal("No open PRs to land in 2 repo(s).\n", output.ToString());
		Assert.Empty(sleeps);
	}

	[Fact]
	public void An_unreadable_repo_is_reported_once_until_it_changes()
	{
		var fake = MakeFake(new() { ["o/app"] = new InvalidOperationException("Could not resolve to a Repository") });
		var (lander, output, err) = Start(fake, All("o/app"));
		Assert.Equal((false, true), lander.Poll());
		lander.Poll();
		Assert.Equal("o/app: Could not resolve to a Repository\n", err.ToString());
		Assert.Equal("", Printed(output));
		// A repo never read doesn't count as done, so the watch goes on.
		fake.Open["o/app"] = Prs(Node());
		Assert.Equal((false, false), lander.Poll());
	}

	// -- The loop --

	// main() with each sleep advancing the clock and applying the next change to fake.
	static (int Code, string Out) RunMain(Fake fake, string[] argv, Action[] polls)
	{
		StringWriter output = new(), err = new();
		using var changes = ((IEnumerable<Action>)polls).GetEnumerator();

		void Sleep(double seconds)
		{
			fake.Now += seconds;
			(changes.MoveNext() ? changes.Current : () => { })();
		}

		var targets = Land.ParseTargets(argv.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && !char.IsDigit(a[0])));
		if (targets.Count == 0)
			targets = All(fake.Scopes.Keys);
		var code = Land.Run(argv, output, err, fake.Lander(targets, output, err), Sleep, () => fake.Scopes);
		return (code, output.ToString());
	}

	[Fact]
	public void It_exits_once_every_watched_pr_and_its_release_blog_pr_has_landed()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: COPILOT)) });
		var blog = Node(number: 8, title: "docs: Add release blog for PR #7 [skip-release]", branch: "docs/release-notes",
				armed: true);

		void MergeSeven()
		{
			fake.Open["o/app"] = Prs();
			fake.Final[("o/app", 7)] = "MERGED";
		}

		void BlogOpens()
		{
			fake.Open["o/app"] = Prs(blog);
		}

		void BlogMerges()
		{
			fake.Open["o/app"] = Prs();
			fake.Final[("o/app", 8)] = "MERGED";
		}

		var (code, output) = RunMain(fake, ["o/app#7", "--interval", "60"], [MergeSeven, BlogOpens, BlogMerges]);
		Assert.Equal(0, code);
		Assert.Equal("All 2 watched PR(s) merged or closed.", SplitLines(output)[^1]);
		Assert.True(output.Contains("app#7 merged", StringComparison.Ordinal) && output.Contains("app#8 merged", StringComparison.Ordinal), output);
	}

	[Fact]
	public void A_release_blog_pr_seen_with_the_merge_and_landed_before_the_next_poll_ends_the_wait()
	{
		// TicketManager#131/#132: the blog PR shows up in the same poll as the merge, then lands within 86 s.
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: COPILOT)) });
		var blog = Node(number: 8, title: "docs: Add release blog for PR #7 [skip-release]", branch: "docs/release-notes",
				armed: true);

		void MergeSevenAndBlogOpens()
		{
			fake.Open["o/app"] = Prs(blog);
			fake.Final[("o/app", 7)] = "MERGED";
		}

		void BlogMerges()
		{
			fake.Open["o/app"] = Prs();
			fake.Final[("o/app", 8)] = "MERGED";
		}

		var (code, output) = RunMain(fake, ["o/app#7", "--interval", "60"], [MergeSevenAndBlogOpens, BlogMerges]);
		Assert.Equal(0, code);
		Assert.DoesNotContain("no release-blog PR", output);
		Assert.Equal("All 2 watched PR(s) merged or closed.", SplitLines(output)[^1]);
		Assert.Equal(120, fake.Now); // the poll after the blog PR merged, not --blog-wait later
	}

	[Fact]
	public void Without_a_release_blog_pr_it_gives_up_waiting_after_blog_wait()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: COPILOT)) });

		void MergeSeven()
		{
			fake.Open["o/app"] = Prs();
			fake.Final[("o/app", 7)] = "MERGED";
		}

		var (code, output) = RunMain(fake, ["o/app#7", "--blog-wait", "2"], [MergeSeven]);
		Assert.Equal(0, code);
		Assert.Contains("o/app: no release-blog PR within 2 min", output);
	}

	[Fact]
	public void Dotfiles_merging_expects_no_release_blog_pr()
	{
		var fake = MakeFake(new() { ["o/dots"] = Prs(Node(contexts: RUNNING)) });

		void MergeSeven()
		{
			fake.Open["o/dots"] = Prs();
			fake.Final[("o/dots", 7)] = "MERGED";
		}

		var (code, output) = RunMain(fake, ["o/dots#7"], [MergeSeven]);
		Assert.Equal(0, code);
		Assert.DoesNotContain("release-blog", output);
		Assert.StartsWith("All 1", SplitLines(output)[^1]);
	}

	[Fact]
	public void The_factory_merging_expects_no_release_blog_pr()
	{
		var fake = MakeFake(new() { ["o/factory"] = Prs(Node(contexts: RUNNING)) });
		fake.Scopes["o/factory"] = "factory";

		void MergeSeven()
		{
			fake.Open["o/factory"] = Prs();
			fake.Final[("o/factory", 7)] = "MERGED";
		}

		var (code, output) = RunMain(fake, ["o/factory#7"], [MergeSeven]);
		Assert.Equal(0, code);
		Assert.DoesNotContain("release-blog", output);
		Assert.StartsWith("All 1", SplitLines(output)[^1]);
	}

	[Fact]
	public void It_stops_at_the_time_limit()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(contexts: RUNNING, requested: ["Copilot"])) });
		var (code, output) = RunMain(fake, ["o/app", "--timeout", "3", "--interval", "60"], []);
		Assert.Equal(1, code);
		Assert.Equal("Time limit reached; still open: app#7", SplitLines(output)[^1]);
		Assert.Equal(1, output.Split("-> wait").Length - 1);
	}

	[Fact]
	public void Once_is_a_single_pass()
	{
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(contexts: RUNNING, requested: ["Copilot"])) });
		var (code, output) = RunMain(fake, ["--once", "o/app"], [() => Assert.Fail("slept")]);
		Assert.Equal(0, code);
		Assert.Equal(1, Count(output, '\n'));
	}

	[Fact]
	public void Targets_parse_prs_urls_and_repos()
	{
		var targets = Land.ParseTargets(["o/app#7", "https://github.com/o/app/pull/8", "o/dots"]);
		Assert.Equal(["o/app", "o/dots"], targets.Keys.Order(StringComparer.Ordinal));
		Assert.True(targets["o/app"]!.SetEquals([7, 8]));
		Assert.Null(targets["o/dots"]);
		Assert.Throws<ArgumentException>(() => Land.ParseTargets(["app"]));
	}

	// -- Claude reviews only where Copilot can't; a person's threads hold (mpaulosky/dotfiles#378) --

	// An unresolved reviewThreads node opened by login.
	static JsonNode ThreadBy(string login, string kind = "User", string? review = null) => new JsonObject
	{
		["isResolved"] = false,
		["path"] = "src/a.py",
		["line"] = 3,
		["originalLine"] = 3,
		["comments"] = new JsonObject
		{
			["totalCount"] = 1,
			["nodes"] = new JsonArray(new JsonObject
			{
				["url"] = "https://github.com/o/app/pull/7#discussion_r1",
				["author"] = new JsonObject { ["login"] = login, ["__typename"] = kind },
				["pullRequestReview"] = new JsonObject { ["body"] = review ?? "" },
			}),
		},
	};

	static string[][] EXHAUSTED_REVIEWS => [.. "123".Select(sha => new[] { "copilot-pull-request-reviewer", new string(sha, 40), "", "dirty" })];

	[Fact]
	public void Review_claude_is_ignored_while_copilot_can_review_it_acceptance_criterion_2()
	{
		// Never asked: Copilot is asked, the label notwithstanding.
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, labels: ["review:claude"])) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Equal([RECORD_CALL, REQUEST_CALL], fake.Calls);
		Assert.DoesNotContain("review:claude is on", Printed(output));
		// Asked and a request pending: Copilot is waited for, not Claude.
		fake.Open["o/app"] = Prs(Node(reviews: OLDER, contexts: GREEN, labels: ["review:claude"], requested: ["Copilot"], comments: [Record(3600)]));
		lander.Poll();
		Assert.Contains("Copilot is requested", Printed(output));
		Assert.Equal(2, fake.Calls.Count);
	}

	[Fact]
	public void An_exhausted_loop_waits_for_a_thread_opened_by_a_person_but_not_for_bots_acceptance_criterion_3()
	{
		var person = MakeFake(new() { ["o/app"] = Prs(Node(reviews: EXHAUSTED_REVIEWS, contexts: GREEN, threads: [ThreadBy("octo")])) });
		var (lander, output, _) = Start(person, All("o/app"));
		lander.Poll();
		var held = Printed(output);
		Assert.True(held.Contains("-> wait", StringComparison.Ordinal) && held.Contains("person", StringComparison.Ordinal)
				&& !held.Contains("leave to PR Auto-Merge", StringComparison.Ordinal), held);

		var bots = MakeFake(new()
		{
			["o/app"] = Prs(Node(reviews: EXHAUSTED_REVIEWS, contexts: GREEN,
					threads: [ThreadBy("copilot-pull-request-reviewer", "Bot"),
						ThreadBy("github-actions", "Bot", "<!-- claude-review -->\n**Claude Review**")])),
		});
		(lander, output, _) = Start(bots, All("o/app"));
		lander.Poll();
		Assert.Contains("-> leave to PR Auto-Merge (review loop exhausted", Printed(output));

		var both = MakeFake(new()
		{
			["o/app"] = Prs(Node(reviews: EXHAUSTED_REVIEWS, contexts: GREEN,
					threads: [ThreadBy("copilot-pull-request-reviewer", "Bot"), ThreadBy("octo")])),
		});
		(lander, output, _) = Start(both, All("o/app"));
		lander.Poll();
		Assert.Contains("-> wait", Printed(output));
	}

	[Fact]
	public void A_thread_whose_author_cant_be_read_holds_an_exhausted_loop_acceptance_criterion_3()
	{
		var unreadable = ThreadBy("x");
		unreadable["comments"]!["nodes"]![0]!["author"] = null;
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: EXHAUSTED_REVIEWS, contexts: GREEN, threads: [unreadable])) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		Assert.Contains("-> wait", Printed(output));
	}

	[Fact]
	public void A_label_event_whose_permission_couldnt_be_read_leaves_land_waiting_acceptance_criterion_11()
	{
		// Copilot answered the head with its note, so an add could count; its permission is unreadable, so the loop is unread.
		string[][] reviews = [NOTE, .. "123".Select(sha => new[] { "github-actions", new string(sha, 40), "<!-- claude-review -->\n**Claude Review**", "dirty" })];
		var fake = MakeFake(new() { ["o/app"] = Prs(Node(reviews: reviews, contexts: GREEN, events: [(Ago(1800), null)])) });
		var (lander, output, _) = Start(fake, All("o/app"));
		lander.Poll();
		// Printed() empties the buffer, so it is read once (the Python read it twice, and its second read saw nothing).
		var text = Printed(output);
		Assert.DoesNotContain("leave to PR Auto-Merge", text, StringComparison.Ordinal);
		Assert.DoesNotContain("review loop exhausted", text, StringComparison.Ordinal);
		Assert.Empty(fake.Calls);
	}
}
