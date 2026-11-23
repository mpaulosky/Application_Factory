#:include Landing.cs
#:property PublishAot=false
// PROTOTYPE (research/status-cs-trial, mpaulosky/Application_Factory#97): status.py, with settings.read_repo_list,
// ported line for line, to judge a C# file-based app as the Factory's scripts. Not merged; status.sh still runs
// status.py. One line per open PR across the repos in github/repos.txt; status.py's docstring holds the contract.
//
//     dotnet run --file github/status.cs -- [<owner/repo>...]
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using static Landing;

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
int code;
try
{
	code = Status.Run(args, stdout, stderr);
}
catch (Exception error) // an uncaught exception is a Python traceback: exit 1, not .NET's abort
{
	stderr.Write($"Traceback: {error.GetType().Name}: {error.Message}\n");
	code = 1;
}
stdout.Flush();
return code;

sealed class GhError(string message) : Exception(message);

sealed class NotFound(string message) : Exception(message);

// One PR node of QUERY, with what gh_graphql adds to it: the comments (null: unread or not read), the label events
// (null: unread or not read) and the thread nodes (THREADS_QUERY's when it was read).
sealed class Pr(JsonNode node)
{
	public JsonNode N { get; } = node;
	public (long Total, List<JsonNode?> Nodes)? Comments;
	public List<(string? CreatedAt, bool? Writer, bool Removed, bool CopilotRequested)>? Events;
	public List<JsonNode?> Threads = [.. J.Nodes(J.Req(node, "reviewThreads"))];
}

// Python's dict access on parsed JSON: Req is node["key"] (a missing key or a None parent raises), Get is
// (node or {}).get("key"), and the rest read a value the way status.py's expressions do.
static class J
{
	public static JsonNode? Req(JsonNode? n, string key) =>
			n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : throw new KeyNotFoundException($"'{key}'");

	public static JsonNode? Get(JsonNode? n, string key) => n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

	public static string? S(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

	public static string Str(JsonNode? n, string key) => S(Get(n, key)) ?? ""; // (n.get(key) or "")

	public static long? Int(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<long>(out var l) ? l : null;

	public static bool B(JsonNode? v) => v switch
	{
		null => false,
		JsonObject o => o.Count > 0,
		JsonArray a => a.Count > 0,
		JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
		JsonValue jv when jv.TryGetValue<string>(out var s) => s.Length > 0,
		JsonValue jv when jv.TryGetValue<double>(out var d) => d != 0,
		_ => true,
	};

	public static string Fmt(JsonNode? v) => v is null ? "None" : S(v) ?? v.ToJsonString();

	public static IEnumerable<JsonNode?> Nodes(JsonNode? n) => Get(n, "nodes") is JsonArray a ? a : [];

	public static string Login(JsonNode? n) => Str(Get(n, "author"), "login");
}

static class Status
{
	const string Query = """

        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, first: 50, orderBy: {field: CREATED_AT, direction: ASC}) {
              nodes {
                id number title state isDraft headRefName headRefOid mergeStateStatus
                autoMergeRequest { enabledAt }
                labels(first: 20) { nodes { name } }
                reviewRequests(first: 20) { nodes { requestedReviewer {
                  ... on Bot { login }
                  ... on User { login }
                  ... on Team { name }
                } } }
                reviews(last: 100) { nodes { author { login } commit { oid parents { totalCount } } body submittedAt comments { totalCount } } }
                reviewThreads(first: 100) { nodes { isResolved } }
                commits(last: 1) { nodes { commit { statusCheckRollup { contexts(first: 100) { nodes {
                  __typename
                  ... on CheckRun { databaseId name status conclusion startedAt }
                  ... on StatusContext { context state createdAt }
                } } } } } }
              }
            }
          }
        }

        """;

	const string CommentsQuery = """

        query($id: ID!, $cursor: String) {
          node(id: $id) {
            ... on PullRequest {
              # comments(last: 100) is the newest page; with $cursor, the 100 before it.
              comments(last: 100, before: $cursor) {
                totalCount
                pageInfo { hasPreviousPage startCursor }
                nodes { author { login } body createdAt }
              }
            }
          }
        }

        """;

	const string ThreadsQuery = """

        query($id: ID!) {
          node(id: $id) {
            ... on PullRequest {
              reviewThreads(first: 100) { nodes {
                isResolved path line originalLine
                comments(first: 10) { totalCount nodes { url author { login __typename } pullRequestReview { body } } }
              } }
            }
          }
        }

        """;

	const string EventsQuery = """

        query($id: ID!, $cursor: String) {
          node(id: $id) {
            ... on PullRequest {
              timelineItems(first: 100, after: $cursor, itemTypes: [LABELED_EVENT, UNLABELED_EVENT, REVIEW_REQUESTED_EVENT]) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  ... on LabeledEvent { __typename createdAt label { name } actor { login __typename } }
                  ... on UnlabeledEvent { __typename createdAt label { name } actor { login __typename } }
                  ... on ReviewRequestedEvent { __typename createdAt requestedReviewer {
                    ... on Bot { login }
                    ... on User { login }
                    ... on Team { name }
                  } }
                }
              }
            }
          }
        }

        """;

	const int TitleWidth = 40;
	static readonly Dictionary<string, bool> Permissions = [];
	static readonly HashSet<string> CommentsFailed = [];
	static TextWriter Err = Console.Error;

	static (int Code, string Out, string Err) Gh(IEnumerable<string> argv)
	{
		var start = new ProcessStartInfo("gh")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		foreach (var arg in argv)
			start.ArgumentList.Add(arg);
		using var run = Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync();
		var output = run.StandardOutput.ReadToEnd();
		run.WaitForExit();
		return (run.ExitCode, output, err.Result);
	}

	static JsonNode? Rest(string path)
	{
		var (code, output, err) = Gh(["api", path]);
		if (code != 0)
		{
			var message = err.Trim() is { Length: > 0 } e ? e : output.Trim();
			if (err.Contains("HTTP 404") || output.Contains("HTTP 404"))
				throw new NotFound(message);
			throw new GhError(message);
		}
		return JsonNode.Parse(output);
	}

	// fields are "key=value" for -F, raw are -f (strings as they are).
	static JsonNode? Graphql(string query, string[] fields, params (string Key, string Value)[] raw)
	{
		List<string> argv = ["api", "graphql", "-f", $"query={query}"];
		foreach (var field in fields)
			argv.AddRange(["-F", field]);
		foreach (var (key, value) in raw)
			argv.AddRange(["-f", $"{key}={value}"]);
		var (code, output, err) = Gh(argv);
		if (code != 0)
			throw new GhError(err.Trim() is { Length: > 0 } e ? e : output.Trim());
		return J.Req(JsonNode.Parse(output), "data");
	}

	static (string? Login, string? Oid, string? Body)[] Reviews(JsonNode node) =>
			[.. J.Nodes(J.Req(node, "reviews")).Select(r => (J.S(J.Get(J.Get(r, "author"), "login")), J.S(J.Get(J.Get(r, "commit"), "oid")), J.S(J.Get(r, "body"))))];

	static bool BotReviewed(JsonNode node) =>
			J.Nodes(J.Req(node, "reviews")).Any(r => Copilot.IsMatch(J.Login(r)) || IsClaudeReview(J.S(J.Get(J.Get(r, "author"), "login")), "-", J.S(J.Get(r, "body"))));

	static bool CopilotIsRequested(JsonNode node) =>
			J.Nodes(J.Get(node, "reviewRequests")).Select(r => J.Get(r, "requestedReviewer"))
					.Any(who => Copilot.IsMatch(J.S(J.Get(who, "login")) is { Length: > 0 } l ? l : J.Str(who, "name")));

	static bool NeedsRecord(JsonNode node)
	{
		if (J.S(J.Get(node, "state")) != "OPEN" || J.B(J.Get(node, "isDraft")))
			return false;
		if (CopilotIsRequested(node))
			return false;
		return !CopilotReviews(Reviews(node)).Contains(J.S(J.Req(node, "headRefOid"))!);
	}

	static bool CouldBeExhausted(JsonNode node)
	{
		var requested = CopilotIsRequested(node);
		var declined = CopilotDeclined(Reviews(node));
		return LongestDirty(ReviewRounds(ReviewRecords(node), declined, !requested || declined.Contains(J.S(J.Req(node, "headRefOid"))!))) >= MaxClaudeRounds;
	}

	static bool NeedsComments(JsonNode node) =>
			J.S(J.Get(node, "state")) == "OPEN" && (NeedsRecord(node) || (!J.B(J.Get(node, "isDraft")) && CouldBeExhausted(node)));

	static List<JsonNode?> ExhaustionMarkers(IEnumerable<JsonNode?> nodes) =>
			[.. nodes.Where(c => NoBot(J.Login(c)) == GithubActions && J.Str(c, "body").Contains(ExhaustedMarker, StringComparison.Ordinal))];

	static (long, List<JsonNode?>) ReadComments(string nodeId, bool toTheMarker)
	{
		var comments = J.Req(J.Req(Graphql(CommentsQuery, [], ("id", nodeId)), "node"), "comments");
		var nodes = J.Nodes(comments).ToList();
		var info = J.Get(comments, "pageInfo");
		while (toTheMarker && J.B(J.Get(info, "hasPreviousPage")) && ExhaustionMarkers(nodes).Count == 0)
		{
			var page = J.Req(J.Req(Graphql(CommentsQuery, [], ("id", nodeId), ("cursor", J.Fmt(J.Req(info, "startCursor")))), "node"), "comments");
			nodes = [.. J.Nodes(page), .. nodes];
			info = J.Get(page, "pageInfo");
		}
		long total = toTheMarker && !J.B(J.Get(info, "hasPreviousPage")) ? nodes.Count : J.Int(J.Get(comments, "totalCount")) ?? nodes.Count;
		return (total, nodes);
	}

	static bool? WriterAccess(string owner, string name, JsonNode? actor)
	{
		var login = J.Str(actor, "login");
		if (login == "" || J.S(J.Get(actor, "__typename")) == "Bot")
			return false;
		if (login.ToLowerInvariant() == owner.ToLowerInvariant())
			return true;
		var key = $"{owner}/{name}\0{login}";
		if (!Permissions.ContainsKey(key))
		{
			try
			{
				Permissions[key] = J.S(J.Get(Rest($"repos/{owner}/{name}/collaborators/{login}/permission"), "permission")) is "admin" or "write";
			}
			catch (NotFound)
			{
				Permissions[key] = false;
			}
			catch (Exception) // an unreadable permission makes the loop unread, never a restart
			{
				return null;
			}
		}
		return Permissions[key];
	}

	static List<(string?, bool?, bool, bool)> ReadLabelEvents(string owner, string name, string nodeId)
	{
		List<JsonNode?> events = [];
		string? cursor = null;
		while (true)
		{
			(string, string)[] raw = cursor is { Length: > 0 } ? [("id", nodeId), ("cursor", cursor)] : [("id", nodeId)];
			var page = J.Req(J.Req(Graphql(EventsQuery, [], raw), "node"), "timelineItems");
			events.AddRange(J.Nodes(page));
			var info = J.Get(page, "pageInfo");
			if (!J.B(J.Get(info, "hasNextPage")))
				break;
			cursor = J.S(J.Req(info, "endCursor"));
		}
		List<(string?, bool?, bool, bool)> read = [];
		foreach (var e in events)
		{
			var createdAt = J.S(J.Get(e, "createdAt"));
			if (J.B(e) && J.S(J.Get(e, "__typename")) == "ReviewRequestedEvent")
			{
				var reviewer = J.Get(e, "requestedReviewer");
				if (Copilot.IsMatch(J.S(J.Get(reviewer, "login")) is { Length: > 0 } l ? l : J.Str(reviewer, "name")))
					read.Add((createdAt, null, false, true));
				continue;
			}
			if (!J.B(e) || J.S(J.Get(J.Get(e, "label"), "name")) != ReviewClaude)
				continue;
			if (J.S(J.Get(e, "__typename")) == "UnlabeledEvent")
			{
				if (J.Str(J.Get(e, "actor"), "login").ToLowerInvariant() == "github-actions")
					read.Add((createdAt, null, true, false));
			}
			else
				read.Add((createdAt, WriterAccess(owner, name, J.Get(e, "actor")), false, false));
		}
		return read;
	}

	static void ReportOnce(string key, string message)
	{
		if (CommentsFailed.Add(key))
			Err.Write(message);
	}

	static string Repr(Exception error) => $"{error.GetType().Name}('{error.Message}')";

	// gh_graphql(): QUERY's data for one repo, with the comments, label events and thread comments its PRs need.
	static List<Pr> FetchPrs(string owner, string name)
	{
		var data = Graphql(Query, [$"owner={owner}", $"name={name}"]);
		List<Pr> prs = [.. J.Nodes(J.Req(J.Req(data, "repository"), "pullRequests")).Select(n => new Pr(n!))];
		foreach (var pr in prs)
		{
			var node = pr.N;
			var id = J.Fmt(J.Req(node, "id"));
			var label = $"{owner}/{name}#{J.Fmt(J.Get(node, "number"))}";
			var candidate = J.S(J.Get(node, "state")) == "OPEN" && !J.B(J.Get(node, "isDraft")) && CouldBeExhausted(node);
			if (NeedsComments(node))
			{
				try
				{
					pr.Comments = ReadComments(id, candidate);
				}
				catch (Exception error) // an unread record fails closed, it mustn't make the repo unreadable
				{
					pr.Comments = null;
					ReportOnce($"record\0{id}", $"{label}: comments unreadable, Copilot neither asked nor called gone, its review loop not called exhausted: {Repr(error)}\n");
				}
			}
			if (candidate)
			{
				try
				{
					pr.Events = ReadLabelEvents(owner, name, id);
				}
				catch (Exception error) // unread events fail closed, they mustn't make the repo unreadable
				{
					pr.Events = null;
					ReportOnce($"events\0{id}", $"{label}: label events unreadable, its review loop not called exhausted: {Repr(error)}\n");
				}
			}
			var allResolved = pr.Threads.All(t => J.B(J.Req(t, "isResolved")));
			var resolved = pr.Threads.Count > 0 && allResolved && BotReviewed(node);
			var authors = candidate && !allResolved;
			if (resolved || authors)
			{
				try
				{
					pr.Threads = [.. J.Req(J.Req(J.Req(Graphql(ThreadsQuery, [], ("id", id)), "node"), "reviewThreads"), "nodes")!.AsArray()];
				}
				catch (Exception error) // an advisory notice mustn't make the repo unreadable
				{
					ReportOnce(resolved ? $"threads\0{id}" : $"authors\0{id}",
							$"{label}: thread comments unreadable, no notice" + (resolved ? "" : ", every unresolved thread counts as a person's") + $": {Repr(error)}\n");
				}
			}
		}
		return prs;
	}

	static Check ToCheck(JsonNode node)
	{
		if (J.S(J.Get(node, "__typename")) == "StatusContext")
		{
			var state = J.S(J.Get(node, "state"));
			var context = J.S(J.Req(node, "context"))!;
			return state is "PENDING" or "EXPECTED"
					? new Check(context, "PENDING", null, J.Str(node, "createdAt"))
					: new Check(context, "COMPLETED", state, J.Str(node, "createdAt"));
		}
		return new Check(J.S(J.Req(node, "name"))!, J.S(J.Req(node, "status"))!, J.S(J.Get(node, "conclusion")), J.Str(node, "startedAt"),
				J.Int(J.Get(node, "databaseId")) ?? 0);
	}

	// printable(): every character str.isprintable() refuses becomes its repr escape; thread paths are PR-controlled.
	static string Printable(string text)
	{
		var result = new StringBuilder();
		foreach (var cp in CodePoints(text))
		{
			int value = cp.Length == 2 ? char.ConvertToUtf32(cp[0], cp[1]) : cp[0];
			if (IsPrintable(value))
				result.Append(cp);
			else
				result.Append(value switch
				{
					'\t' => @"\t",
					'\n' => @"\n",
					'\r' => @"\r",
					< 0x100 => $"\\x{value:x2}",
					< 0x10000 => $"\\u{value:x4}",
					_ => $"\\U{value:x8}",
				});
		}
		return result.ToString();
	}

	static bool IsPrintable(int value)
	{
		if (value == ' ')
			return true;
		if (value is >= 0xD800 and <= 0xDFFF)
			return false;
		return CharUnicodeInfo.GetUnicodeCategory(value) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
				or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
				or UnicodeCategory.SpaceSeparator);
	}

	// The string's code points (Python's characters), a lone surrogate as one.
	static List<string> CodePoints(string text)
	{
		List<string> points = [];
		for (var i = 0; i < text.Length; i++)
			if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
				points.Add(text.Substring(i++, 2));
			else
				points.Add(text[i].ToString());
		return points;
	}

	static string ThreadName(JsonNode? node)
	{
		var path = J.Str(node, "path");
		if (path == "")
			return "thread";
		var number = J.B(J.Get(node, "line")) ? J.Get(node, "line") : J.Get(node, "originalLine");
		return Printable(J.B(number) ? $"{path}:{J.Fmt(number)}" : path);
	}

	static Thread ToThread(JsonNode? node)
	{
		var comments = J.Get(node, "comments");
		var nodes = J.Nodes(comments).ToList();
		var author = nodes.Count > 0 ? J.Get(nodes[0], "author") : null;
		var review = nodes.Count > 0 ? J.Get(nodes[0], "pullRequestReview") : null;
		return new Thread(
				J.B(J.Get(node, "isResolved")),
				J.Str(author, "login"),
				J.Str(author, "__typename"),
				J.Str(review, "body"),
				[.. nodes.Skip(1).Select(J.Login)],
				(J.Int(J.Get(comments, "totalCount")) ?? nodes.Count) > nodes.Count,
				ThreadName(node));
	}

	static (bool, bool) AskedCopilot(Pr pr, string owner, double now)
	{
		if (pr.Comments is not var (total, nodes))
			return CopilotAsk(null, owner, now);
		var found = CopilotAsk(nodes.Select(c => (J.Login(c), J.Str(c, "body"), J.S(J.Get(c, "createdAt")))), owner, now);
		// More comments than were read and no record among them: it may be on a page that wasn't read.
		if (found == (false, false) && total > nodes.Count)
			return CopilotAsk(null, owner, now);
		return found;
	}

	static (string?, string?, string?, string?, long, bool)[] ReviewRecords(JsonNode node) =>
			[.. J.Nodes(J.Req(node, "reviews")).Select(r => (J.S(J.Get(J.Get(r, "author"), "login")), J.S(J.Get(J.Get(r, "commit"), "oid")), J.S(J.Get(r, "body")),
						J.S(J.Get(r, "submittedAt")), J.Int(J.Get(J.Get(r, "comments"), "totalCount")) ?? 0,
						(J.Int(J.Get(J.Get(J.Get(r, "commit"), "parents"), "totalCount")) ?? 1) > 1))];

	static double Instant(string? text) => Epoch(text) ?? double.NegativeInfinity;

	static (Round[], bool, bool) ReviewLoop(Pr pr, Round[] rounds)
	{
		if (LongestDirty(rounds) < MaxClaudeRounds)
			return (rounds, false, false);
		if (pr.Comments is not var (total, nodes) || pr.Events is not { } events)
			return (rounds, false, true);
		var adds = events.Where(e => !e.Removed && !e.CopilotRequested).Select(e => (Instant(e.CreatedAt), e.Writer));
		var removals = events.Where(e => e.Removed).Select(e => Instant(e.CreatedAt));
		var requests = events.Where(e => e.CopilotRequested).Select(e => Instant(e.CreatedAt));
		var walk = CurrentLoop(rounds, adds, removals, requests);
		var markers = ExhaustionMarkers(nodes).Select(c => Instant(J.S(J.Get(c, "createdAt")))).ToList();
		if (walk.Unread || (markers.Count == 0 && total > nodes.Count))
			return (walk.Rounds, false, true);
		return (walk.Rounds, walk.Hit is double hit && markers.Any(m => m >= hit), false);
	}

	static PrState ToState(Pr pr, bool checksRequired, bool mergedByWorkflow, string owner, double now)
	{
		var node = pr.N;
		var (asked, settled) = AskedCopilot(pr, owner, now);
		var commits = J.Nodes(J.Req(node, "commits")).ToList();
		var rollup = commits.Count > 0 ? J.Req(J.Req(commits[0], "commit"), "statusCheckRollup") : null;
		var contexts = J.B(rollup) ? J.Nodes(J.Req(rollup, "contexts")) : [];
		var reviews = Reviews(node);
		var copilotIsRequested = CopilotIsRequested(node);
		var labels = J.Nodes(J.Get(node, "labels")).Where(J.B).Select(l => J.S(J.Req(l, "name"))).ToHashSet();
		var declined = CopilotDeclined(reviews);
		var head = J.S(J.Req(node, "headRefOid"))!;
		var gone = (asked && settled && !copilotIsRequested) || declined.Contains(head);
		var (rounds, markerNewest, unread) = ReviewLoop(pr, ReviewRounds(ReviewRecords(node), declined, gone));
		var threads = pr.Threads.Select(t => (Resolved: J.B(J.Req(t, "isResolved")), Thread: ToThread(t))).ToList();
		return new PrState
		{
			State = J.S(J.Req(node, "state"))!,
			Draft = J.B(J.Req(node, "isDraft")),
			Head = head,
			CopilotReviewed = CopilotReviews(reviews),
			CopilotDeclined = declined,
			Rounds = rounds,
			ExhaustionMarkerNewest = markerNewest,
			LoopUnread = unread,
			ClaudeReviewed = ClaudeReviews(reviews),
			ClaudeOffDiff = ClaudeOffDiff(reviews),
			CopilotRequested = copilotIsRequested,
			ClaudeRequested = labels.Contains(ReviewClaude),
			CopilotAsked = asked,
			CopilotAskSettled = settled,
			OpenThreads = threads.Count(t => !t.Resolved),
			OpenPersonThreads = threads.Count(t => !t.Resolved && ThreadOpenedByPerson(t.Thread)),
			UnrepliedThreads = UnrepliedThreads(threads.Select(t => t.Thread)),
			MergeState = J.S(J.Req(node, "mergeStateStatus"))!,
			Checks = [.. contexts.Where(J.B).Select(c => ToCheck(c!))],
			ChecksRequired = checksRequired,
			AutoMergeArmed = J.Req(node, "autoMergeRequest") is not null,
			MergedByWorkflow = mergedByWorkflow,
			ReleaseBlog = J.S(J.Get(node, "headRefName")) is { } branch && ReleaseBlogBranches.Contains(branch),
		};
	}

	static string Shorten(string text)
	{
		var points = CodePoints(text);
		return points.Count <= TitleWidth ? text : string.Concat(points.Take(TitleWidth - 1)) + "…";
	}

	static string Line(string repo, Pr pr, PrState state)
	{
		var checks = SummarizeChecks(state.Checks);
		var decision = Decide(state);
		var name = repo.Split('/')[^1];
		var notice = UnrepliedNotice(state);
		var title = Shorten(J.S(J.Req(pr.N, "title"))!);
		title += new string(' ', Math.Max(0, TitleWidth - CodePoints(title).Count));
		return $"{name}#{J.Fmt(J.Req(pr.N, "number"))} {title} {Short(state.Head)}"
				+ $" {(state.Draft ? "draft" : "ready")} {state.MergeState}"
				+ $" review:{(state.HeadReviewer is { Length: > 0 } r ? r : "no")} threads:{state.OpenThreads}"
				+ $" automerge:{(state.AutoMergeArmed ? "on" : "off")}"
				+ $" running:{checks.Running.Length} failed:{checks.Failed.Length} cancelled:{checks.Cancelled.Length}"
				+ $" -> {decision.Action} ({decision.Reason})" + (notice != "" ? $" {notice}" : "");
	}

	// settings.read_repo_list(): {owner/repo: scope} from repos.txt beside this file, in the file's order.
	static List<KeyValuePair<string, string>> ReadRepoList()
	{
		var here = (string)AppContext.GetData("EntryPointFileDirectoryPath")!;
		List<KeyValuePair<string, string>> repos = [];
		foreach (var line in File.ReadAllText(Path.Combine(here, "repos.txt")).Split(['\r', '\n', '\v', '\f', '\x1c', '\x1d', '\x1e', '\x85', '\u2028', '\u2029']))
		{
			var words = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (words.Length == 0)
				continue;
			var at = repos.FindIndex(kv => kv.Key == words[0]);
			var entry = KeyValuePair.Create(words[0], words.Length > 1 ? words[1] : "full");
			if (at >= 0)
				repos[at] = entry;
			else
				repos.Add(entry);
		}
		return repos;
	}

	public static int Run(string[] args, TextWriter output, TextWriter err)
	{
		Err = err;
		// argparse: positional repos, -h, "--" ends the options; any other option is refused with exit 2.
		List<string> named = [], unknown = [];
		var positional = false;
		foreach (var arg in args)
		{
			if (!positional && arg == "--")
				positional = true;
			else if (!positional && arg.StartsWith('-') && arg != "-")
			{
				if (arg is "-h" or "--help")
				{
					output.Write("usage: status.cs [-h] [repos ...]\n\nOne line per open PR across the repos in github/repos.txt.\n");
					return 0;
				}
				unknown.Add(arg);
			}
			else
				named.Add(arg);
		}
		if (unknown.Count > 0)
		{
			err.Write($"usage: status.cs [-h] [repos ...]\nstatus.cs: error: unrecognized arguments: {string.Join(" ", unknown)}\n");
			return 2;
		}
		var listed = ReadRepoList();
		var repos = named.Count > 0 ? named : [.. listed.Select(kv => kv.Key)];
		List<string> lines = [];
		var status = 0;
		foreach (var repo in repos)
		{
			var slash = repo.IndexOf('/');
			if (slash < 0)
				throw new ArgumentException("not enough values to unpack (expected 2, got 1)");
			var (owner, name) = (repo[..slash], repo[(slash + 1)..]);
			List<Pr> prs;
			try
			{
				prs = FetchPrs(owner, name);
			}
			catch (Exception error) // one unreadable repo mustn't hide the rest
			{
				err.Write($"{repo}: {error.Message}\n");
				status = 1;
				continue;
			}
			// repo_flags(): every repos.txt scope requires checks and runs PR Auto-Merge.
			var now = (DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;
			foreach (var pr in prs)
				lines.Add(Line(repo, pr, ToState(pr, checksRequired: true, mergedByWorkflow: true, owner, now)));
		}
		if (lines.Count > 0)
			output.Write(string.Join("\n", lines) + "\n");
		else if (status == 0)
			output.Write($"No open PRs in {repos.Count} repo(s).\n");
		return status;
	}
}
