using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

// StatusTests' fixture builders: the GraphQL fetch replaced by fixtures. No GitHub.
public partial class StatusTests
{
	const string Head = "1234567" + "000000000000000000000000000000000";

	// A value a caller may leave out: default(Opt) is not given, any value (null among them, Python's None) is.
	readonly record struct Opt<T>(T Value)
	{
		public bool Given { get; } = true;

		public static implicit operator Opt<T>(T value) => new(value);
	}

	// A reviews entry: (login, oid[, body[, extras]]); extras: At (submittedAt), Threads (inline comments), Merge. A null extra is absent.
	sealed record Rev(string Login, string Oid, string? Body = null, string? At = null, int? Threads = null, bool? Merge = null);

	// The fixtures' numbers are longs, as parsed JSON's (GitHub's answers) read.
	static JsonNode P(string json) => JsonNode.Parse(json)!;

	// A node that can go into another tree: a JsonNode has one parent, where a Python dict can sit in any number of lists.
	static JsonNode? Attach(JsonNode? node) => node?.Parent is null ? node : node.DeepClone();

	// dict(node, key=value, ...): a copy of node with the keys set.
	static JsonObject With(JsonNode node, params (string Key, JsonNode? Value)[] pairs)
	{
		var copy = (JsonObject)node.DeepClone();
		foreach (var (key, value) in pairs)
			copy[key] = Attach(value);
		return copy;
	}

	static JsonNode[] Ev(params JsonNode[] events) => events;

	static string Iso(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

	// An ISO-8601 time `seconds` ago, as GraphQL reports createdAt.
	static string Ago(double seconds) => Iso(DateTimeOffset.UtcNow.AddSeconds(-seconds));

	// The comment that records Copilot was asked (mpaulosky/dotfiles#360), as COMMENTS_QUERY reports it.
	static JsonObject Asked(double age = 3600, string login = "github-actions") => new()
	{
		["author"] = new JsonObject { ["login"] = login },
		["body"] = "Asked Copilot.\n\n<!-- pr-automerge:copilot-asked -->",
		["createdAt"] = Ago(age),
	};

	static JsonObject CommentsOf(params JsonNode[] nodes) => CommentsOfTotal(null, nodes);

	static JsonObject CommentsOfTotal(int? total, params JsonNode[] nodes) => new()
	{
		["totalCount"] = (long)(total ?? nodes.Length),
		["nodes"] = new JsonArray([.. nodes.Select(Attach)]),
	};

	// A reviews node from (login, oid[, body[, extras]]); extras: at (submittedAt), threads (inline comments), merge.
	static JsonObject ReviewNode(Rev review)
	{
		var commit = new JsonObject { ["oid"] = review.Oid };
		if (review.Merge is bool merge)
			commit["parents"] = new JsonObject { ["totalCount"] = merge ? 2L : 1L };
		var node = new JsonObject { ["author"] = new JsonObject { ["login"] = review.Login }, ["commit"] = commit, ["body"] = review.Body ?? "" };
		if (review.At is not null)
			node["submittedAt"] = review.At;
		if (review.Threads is int threads)
			node["comments"] = new JsonObject { ["totalCount"] = (long)threads };
		return node;
	}

	// pr_node's default for comments: a fresh CommentsOf() per node. Null is not the default, since null is a comments read that failed.
	static JsonObject PrNode(int number = 7, string title = "feat: Something", bool draft = false, string mergeState = "CLEAN", Rev[]? reviews = null,
			object[]? threads = null, JsonNode[]? contexts = null, bool armed = false, Opt<JsonNode?> comments = default, Opt<JsonNode[]?> events = default)
	{
		var commentsNode = comments.Given ? comments.Value : CommentsOf();
		var eventsList = events.Given ? events.Value : [];
		contexts ??= [];
		return new JsonObject
		{
			["id"] = $"PR_{number}",
			["comments"] = Attach(commentsNode),
			// The PR's review:claude label events, all of them oldest first, as gh_graphql reads them (EVENTS_QUERY, paged)
			// and resolves the actor's write access: [{createdAt, writer}]; None is a read that failed (mpaulosky/dotfiles#378).
			["labelEvents"] = eventsList is null ? null : new JsonArray([.. eventsList.Select(Attach)]),
			["number"] = (long)number,
			["title"] = title,
			["state"] = "OPEN",
			["isDraft"] = draft,
			["isCrossRepository"] = false,
			["headRefOid"] = Head,
			["mergeStateStatus"] = mergeState,
			["autoMergeRequest"] = armed ? new JsonObject { ["enabledAt"] = "2026-10-07T10:00:00Z" } : null,
			["reviews"] = new JsonObject { ["nodes"] = new JsonArray([.. (reviews ?? []).Select(ReviewNode)]) },
			// A thread is true/false (resolved or not) or a full node from ThreadNode().
			["reviewThreads"] = new JsonObject
			{
				["nodes"] = new JsonArray([.. (threads ?? []).Select(t => t is bool resolved ? new JsonObject { ["isResolved"] = resolved } : Attach((JsonNode)t))]),
			},
			["commits"] = new JsonObject
			{
				["nodes"] = new JsonArray(new JsonObject
				{
					["commit"] = new JsonObject
					{
						["statusCheckRollup"] = contexts.Length > 0
								? new JsonObject { ["contexts"] = new JsonObject { ["nodes"] = new JsonArray([.. contexts.Select(Attach)]) } }
								: null,
					},
				}),
			},
		};
	}

	// A reviewThreads node with comments: the opener, then one reply per login in replies.
	static JsonObject ThreadNode(string? login = "copilot-pull-request-reviewer", string kind = "Bot", string review = "", string[]? replies = null,
			bool resolved = true, string? path = "src/a.py", int? line = 12, int? total = null)
	{
		var comments = new JsonArray(new JsonObject
		{
			["url"] = "https://github.com/o/r/pull/7#discussion_r1",
			["author"] = string.IsNullOrEmpty(login) ? null : new JsonObject { ["login"] = login, ["__typename"] = kind },
			["pullRequestReview"] = new JsonObject { ["body"] = review },
		});
		foreach (var reply in replies ?? [])
			comments.Add(new JsonObject
			{
				["url"] = "https://github.com/o/r/pull/7#discussion_r2",
				["author"] = new JsonObject { ["login"] = reply, ["__typename"] = "User" },
				["pullRequestReview"] = null,
			});
		return new JsonObject
		{
			["isResolved"] = resolved,
			["path"] = path,
			["line"] = (long?)line,
			["originalLine"] = (long?)line,
			["comments"] = new JsonObject { ["totalCount"] = (long)(total ?? comments.Count), ["nodes"] = comments },
		};
	}

	// The Fleet as read_repo_list() reads it, for a run that doesn't replace it.
	static OrderedDictionary<string, string> FleetList() => Repos.Read(Skill.Path("github"));

	static (int Code, string Out, string Err) Run(string[] argv, Dictionary<string, JsonNode> data, Func<OrderedDictionary<string, string>>? readRepoList = null)
	{
		using var output = new StringWriter();
		using var err = new StringWriter();
		var code = Status.Run(argv, output, err, (owner, name) => data[$"{owner}/{name}"], readRepoList ?? FleetList);
		return (code, output.ToString(), err.ToString());
	}

	static JsonObject Repo(params JsonNode[] nodes) => new()
	{
		["repository"] = new JsonObject { ["pullRequests"] = new JsonObject { ["nodes"] = new JsonArray([.. nodes.Select(Attach)]) } },
	};

	static readonly (bool ChecksRequired, bool MergedByWorkflow) Flags = Status.RepoFlags("full");
	const double Now = 1_800_000_000.0;

	static PrState ToState(JsonNode node, (bool ChecksRequired, bool MergedByWorkflow) flags, string owner = "", double? now = null) =>
			Status.ToState(node, flags.ChecksRequired, flags.MergedByWorkflow, owner: owner, now: now);

	static PrState StateOf(JsonNode node) => ToState(node, Flags, owner: "o", now: Now);

	// Python's str.splitlines() of a status output.
	static string[] Lines(string text)
	{
		var lines = text.Split('\n').ToList();
		if (lines.Count > 0 && lines[^1] == "")
			lines.RemoveAt(lines.Count - 1);
		return [.. lines];
	}

	static int Count(string text, char c) => text.Count(x => x == c);

	static void In(string part, string text, string? because = null) =>
			Assert.True(text.Contains(part, StringComparison.Ordinal), because ?? $"{JsonSerializer.Serialize(part)} not in {JsonSerializer.Serialize(text)}");

	static void NotIn(string part, string text, string? because = null) =>
			Assert.False(text.Contains(part, StringComparison.Ordinal), because ?? $"{JsonSerializer.Serialize(part)} in {JsonSerializer.Serialize(text)}");

	static void EndsWith(string end, string text) =>
			Assert.True(text.EndsWith(end, StringComparison.Ordinal), $"{JsonSerializer.Serialize(text)} doesn't end with {JsonSerializer.Serialize(end)}");

	static void JsonEqual(string expected, JsonNode? actual) =>
			Assert.True(JsonNode.DeepEquals(P(expected), actual), $"expected {expected}, got {actual?.ToJsonString() ?? "null"}");

	static Dictionary<long, JsonNode> ByNumber(JsonNode data) =>
			J.Nodes(J.Req(J.Req(data, "repository"), "pullRequests")).ToDictionary(node => J.Int(J.Req(node, "number"))!.Value, node => node!);

	static JsonNode FirstNode(JsonNode data) => J.Nodes(J.Req(J.Req(data, "repository"), "pullRequests")).First()!;

	static string? RawGet(IReadOnlyList<(string Key, string Value)> raw, string key) =>
			raw.Where(pair => pair.Key == key).Select(pair => (string?)pair.Value).FirstOrDefault();

	static string RawReq(IReadOnlyList<(string Key, string Value)> raw, string key) => RawGet(raw, key) ?? throw new KeyNotFoundException(key);

	// What capsys.readouterr().err gives: the text written so far, which it then forgets.
	static string ReadErr(StringWriter err)
	{
		var text = err.ToString();
		err.GetStringBuilder().Clear();
		return text;
	}

	// Python's RuntimeError.
	static InvalidOperationException RuntimeError(string message) => new(message);

	// -- The record that Copilot was asked (mpaulosky/dotfiles#360) --

	static JsonNode[] Green => [P("""{"__typename": "CheckRun", "name": "Test Suite", "status": "COMPLETED", "conclusion": "SUCCESS", "startedAt": "2026-10-07T10:00:00Z"}""")];

	static Rev[] Older => [new("copilot-pull-request-reviewer", new string('f', 40))];

	// asked(), as of Now: a record `age` seconds old.
	static JsonObject At(double age)
	{
		var node = Asked(0);
		node["createdAt"] = Iso(DateTimeOffset.FromUnixTimeSeconds((long)(Now - age)));
		return node;
	}

	// -- The review loop: dirty rounds in a row (mpaulosky/dotfiles#375) --

	const string CopilotLogin = "copilot-pull-request-reviewer";

	static JsonObject CopilotRequested => new() { ["nodes"] = new JsonArray(new JsonObject { ["requestedReviewer"] = new JsonObject { ["login"] = "Copilot" } }) };

	static string Oid(int number) => number.ToString("x40", CultureInfo.InvariantCulture);

	// A review of a commit of its own at minute (default: number) of 2026-01-02T00, with an inline thread when dirty.
	static Rev RoundOf(int number, bool dirty = true, int? minute = null, bool merge = false, string login = CopilotLogin, string body = "") =>
			new(login, Oid(number), body, $"2026-01-02T00:{minute ?? number:D2}:00Z", dirty ? 1 : 0, merge);

	static Rev[] Dirty3 => [RoundOf(1), RoundOf(2), RoundOf(3)];

	const string ExhaustedBody = "**PR Auto-Merge: the review loop is exhausted.**\n\nNothing is left open.\n\n<!-- pr-automerge:review-loop-exhausted " + Head + " -->";

	// PR Auto-Merge's comment on an exhausted loop, as COMMENTS_QUERY reports it.
	static JsonObject MarkerComment(string at = "2026-01-02T01:00:00Z", string login = "github-actions", string body = ExhaustedBody) => new()
	{
		["author"] = new JsonObject { ["login"] = login },
		["body"] = body,
		["createdAt"] = at,
	};

	// A review:claude label event as to_state reads it: when, and whether the login that added it has write access.
	static JsonObject Relabel(string at = "2026-01-02T02:00:00Z", bool? writer = true) => new() { ["createdAt"] = at, ["writer"] = writer };

	// A request for Copilot's review as to_state reads it (mpaulosky/dotfiles#381).
	static JsonObject CopilotRequest(string at) => new() { ["createdAt"] = at, ["copilotRequested"] = true };

	// PR Auto-Merge's (github-actions) removal of review:claude as to_state reads it (mpaulosky/dotfiles#381): the add it follows
	// was made while Copilot could review, and is ignored.
	static JsonObject Removal(string at = "2026-01-02T02:00:05Z") => new() { ["createdAt"] = at, ["removed"] = true };

	// The ask was recorded long ago and nothing is pending: Copilot is gone, so a review:claude add can restart the loop.
	static JsonObject Gone => At(3600);

	// -- Claude reviews only where Copilot can't; a person's threads hold; every label event is read (mpaulosky/dotfiles#378) --

	static JsonObject CopilotRequestedNode => CopilotRequested;

	const string NoteText = "Copilot was unable to review this pull request because the user who requested the review has reached their quota limit.";

	// -- A person's open threads still hold the merge at an exhausted loop --

	static JsonObject OpenThread(string? login = "copilot-pull-request-reviewer", string kind = "Bot", string review = "") =>
			ThreadNode(login: login, kind: kind, review: review, resolved: false);

	static readonly (string Login, string Review) ClaudeThread = ("github-actions", "<!-- claude-review -->\n**Claude Review**");

	static JsonObject OpenClaudeThread() => OpenThread(login: ClaudeThread.Login, review: ClaudeThread.Review);

	// -- Every review:claude label event is read, a page at a time (acceptance criteria 11 and 12) --

	static JsonObject EventNode(string atTime, string? login, string kind = "User", string label = "review:claude") => new()
	{
		["createdAt"] = atTime,
		["label"] = new JsonObject { ["name"] = label },
		["actor"] = login is null ? null : new JsonObject { ["login"] = login, ["__typename"] = kind },
	};

	static JsonObject EventsPage(JsonNode[] nodes, string? nextCursor = null) => new()
	{
		["node"] = new JsonObject
		{
			["timelineItems"] = new JsonObject
			{
				["pageInfo"] = new JsonObject { ["hasNextPage"] = nextCursor is not null, ["endCursor"] = nextCursor },
				["nodes"] = new JsonArray([.. nodes.Select(Attach)]),
			},
		},
	};

	// GitHub as Fetch reads it: repo data, then per PR id the pages of its comments (newest page first), its label events (oldest page
	// first) and its threads, and the collaborator permission of each login. A page or a permission may be an exception, which is thrown.
	sealed class Served(JsonNode repoData, Dictionary<string, object[]>? comments = null, Dictionary<string, object[]>? events = null,
			Dictionary<string, JsonArray>? threads = null, Dictionary<string, object>? permissions = null)
	{
		readonly Dictionary<string, object[]> comments = comments ?? [];
		readonly Dictionary<string, object[]> events = events ?? [];
		readonly Dictionary<string, JsonArray> threads = threads ?? [];
		readonly Dictionary<string, object> permissions = permissions ?? [];
		public readonly List<(string Kind, string Id)> Calls = []; // (kind, id) per query, and ("permission", login) per REST lookup
		readonly Dictionary<(string, string), int> reads = [];
		public readonly StringWriter Err = new();

		public JsonNode? Graphql(string query, IReadOnlyList<string> fields, IReadOnlyList<(string Key, string Value)> raw)
		{
			var args = string.Join("|", [.. fields, .. raw.Select(pair => pair.Value), .. raw.Select(pair => pair.Key)]);
			foreach (var (kind, constant, pages) in new[] { ("comments", Status.CommentsQuery, comments), ("events", Status.EventsQuery, events) })
			{
				if (query != constant)
					continue;
				var key = (kind, RawGet(raw, "id") ?? fields.First(f => f.StartsWith("id=", StringComparison.Ordinal)).Split('=', 2)[1]);
				Calls.Add(key);
				var index = reads.GetValueOrDefault(key, 0);
				reads[key] = index + 1;
				var page = pages[key.Item2][index];
				if (page is Exception error)
					throw error;
				if (index > 0)
				{
					// A later page is asked for from the cursor the page before gave.
					var previous = (JsonNode)pages[key.Item2][index - 1];
					var info = J.Req(J.Req(J.Req(previous, "node"), kind == "events" ? "timelineItems" : "comments"), "pageInfo");
					var cursor = J.B(J.Get(info, "endCursor")) ? J.Get(info, "endCursor") : J.Get(info, "startCursor");
					Assert.True(args.Contains(J.Fmt(cursor), StringComparison.Ordinal), $"{kind} {args}");
				}
				return (JsonNode)page;
			}
			if (query == Status.ThreadsQuery)
			{
				Calls.Add(("threads", RawReq(raw, "id")));
				return new JsonObject { ["node"] = new JsonObject { ["reviewThreads"] = new JsonObject { ["nodes"] = threads[RawReq(raw, "id")].DeepClone() } } };
			}
			return repoData;
		}

		public JsonNode? Rest(string path)
		{
			var login = path.Split("/collaborators/")[1].Split('/')[0];
			Calls.Add(("permission", login));
			var answer = permissions.GetValueOrDefault(login, "none");
			if (answer is Exception error)
				throw error;
			return new JsonObject { ["permission"] = (string)answer };
		}

		// A reader of its own, as the Python's install() replaced the module's graphql, rest, PERMISSIONS and COMMENTS_FAILED.
		public StatusReader Install() => new(err: Err) { Graphql = Graphql, Rest = Rest };
	}

	static JsonObject CommentsPage(JsonNode[] nodes, int total, string? previous = null) => new()
	{
		["node"] = new JsonObject
		{
			["comments"] = new JsonObject
			{
				["totalCount"] = (long)total,
				["pageInfo"] = new JsonObject { ["hasPreviousPage"] = previous is not null, ["startCursor"] = previous },
				["nodes"] = new JsonArray([.. nodes.Select(Attach)]),
			},
		},
	};

	static JsonObject ExhaustedNode(Opt<JsonNode?> comments = default) => With(PrNode(number: 1, reviews: Dirty3, comments: comments), ("id", "PR_1"));

	static JsonNode[] Filler() => [.. Enumerable.Range(0, 100).Select(_ => P("""{"author": {"login": "someone"}, "body": "Looks good.", "createdAt": "2026-01-02T05:00:00Z"}"""))];

	// -- A recognised restart stays recognised; a 404 permission lookup is no write access (mpaulosky/dotfiles#381) --

	// Copilot's real review of the head, submitted at minute of 2026-01-02T00 (after the add at 00:04).
	static Rev ReviewedHead(int minute = 5, int threads = 1) => new(CopilotLogin, Head, "", $"2026-01-02T00:{minute:D2}:00Z", threads);

	const string AddAfterRounds = "2026-01-02T00:04:00Z";

	static JsonObject UneventNode(string atTime, string login = "github-actions", string kind = "Bot", string label = "review:claude")
	{
		var node = new JsonObject { ["__typename"] = "UnlabeledEvent" };
		foreach (var (key, value) in EventNode(atTime, login, kind, label).ToList())
			node[key] = value?.DeepClone();
		return node;
	}

	static JsonObject RequestNode(string atTime, string login = "Copilot") => new()
	{
		["__typename"] = "ReviewRequestedEvent",
		["createdAt"] = atTime,
		["requestedReviewer"] = new JsonObject { ["login"] = login },
	};

	// A permission lookup is `gh api repos/<owner>/<name>/collaborators/<login>/permission`; these fake gh itself, so they hold
	// whatever way StatusReader reads the failure.
	sealed class FakeGh(string stderr = "", string stdout = "", int code = 1)
	{
		public int Calls;

		public GhRun Run(IReadOnlyList<string> argv)
		{
			Calls++;
			return new GhRun(code, stdout, stderr);
		}
	}

	static FakeGh NotFound404() => new("gh: Not Found (HTTP 404)\n", """{"message":"Not Found","documentation_url":"https://docs.github.com/rest","status":"404"}""");

	static JsonObject User => new() { ["login"] = "ghost", ["__typename"] = "User" };

	static StatusReader LookupWith(FakeGh gh) => new(new Gh(gh.Run));
}
