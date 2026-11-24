// status.cs's logic, which land.cs shares: one line per open PR across the repos in github/repos.txt.
//
//     status.sh                   every repo in github/repos.txt
//     status.sh <owner/repo>...   just these
//
// Each line: repo#number, title, head, draft, merge state, which reviewer reviewed the head (review:copilot, review:claude or review:no),
// open threads, auto-merge, running/failed/cancelled checks, and the landing decision's next action (Landing.cs), then a `notice:` naming
// the resolved Copilot or Claude Review threads nobody replied to. One GraphQL query per repo, plus one for each PR whose threads are all
// resolved and that Copilot or Claude Review reviewed (it reads the thread comments the notice needs; they would cost far more in the repo
// query), and one for each open, ready PR whose head Copilot hasn't reviewed and isn't asked for, or whose review loop could be exhausted
// (its comments, back to PR Auto-Merge's exhaustion comment, for it and the record that Copilot was asked; its review:claude label events,
// all of them, with the permission of each login that added one; and the authors of its open threads, a person's holding the merge),
// read-only. Prints nothing else, apart from one line when no PR is open; a repo that can't be read is reported on stderr and exits 1.
//
// Every listed repo requires checks and runs PR Auto-Merge, which merges a reviewed PR itself: a Baseline repo through Apply, and the
// Factory (repos.txt scope factory) through its copy (ADR 0008).
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using static Landing;

// One `gh api graphql` call: the query, its -F fields ("key=value") and its -f raw strings.
delegate JsonNode? GraphqlCall(string query, IReadOnlyList<string> fields, IReadOnlyList<(string Key, string Value)> raw);

// Reads a repo's open PRs from GitHub, with what each one's decision needs (Fetch). It remembers, for as long as it lives (a land.cs run
// polls through one), the collaborator permissions it read and the PRs whose failed reads it already reported, so a polling land.cs says
// each once. Graphql and Rest are gh's by default; a test hands in its own.
sealed class StatusReader
{
	public GraphqlCall Graphql;
	public Func<string, JsonNode?> Rest;
	public TextWriter Err;
	// {(owner/name, login): whether the login has write access} for the collaborator permission lookups that were read. A lookup that
	// failed isn't kept, so the next read tries again.
	public readonly Dictionary<(string Repo, string Login), bool> Permissions = [];
	// The reads whose failure was already reported ("record", "events", "threads" or "authors", and the PR's id).
	public readonly HashSet<(string Kind, string Id)> Reported = [];

	public StatusReader(Gh? gh = null, TextWriter? err = null)
	{
		gh ??= new Gh();
		Graphql = (query, fields, raw) => gh.Graphql(query, fields, raw);
		Rest = gh.Rest;
		Err = err ?? Console.Error;
	}

	// The query's data for one repo.
	//
	// Status.Query reads no thread comments: on 50 PRs of 100 threads they would cost about 53 points a read instead of 3. A PR whose
	// threads are all resolved and that a bot reviewed gets ThreadsQuery (1 point), and its thread nodes are replaced by that query's,
	// which carry the comments the notice reads. So does a PR that could be exhausted and has an unresolved thread: the authors tell the
	// threads a person opened, which hold the merge, from Copilot's and Claude Review's (mpaulosky/dotfiles#378). The notice is advisory,
	// so a failed or empty comments query keeps the comment-less nodes (no notice, and every unresolved thread counts as a person's) and
	// never costs the repo's read; the first failure for a PR is reported on stderr.
	//
	// Status.Query reads no comments either. A PR that needs the record that Copilot was asked (NeedsRecord), or whose review loop could
	// be exhausted (CouldBeExhausted: PR Auto-Merge's exhaustion comment decides), gets CommentsQuery (1 point) and its node a "comments"
	// key, the pages back to the exhaustion comment for a loop that could be exhausted; a failed query sets it to null, which ToState reads
	// as a record nobody can see: neither asked nor called dropped, and the loop never exhausted. A loop that could be exhausted also gets
	// all its review:claude label events, EventsQuery paged, in "labelEvents" (null when a page failed, which ToState reads as a loop it
	// can't tell the state of). The first failure is reported.
	public JsonNode Fetch(string owner, string name)
	{
		var data = Graphql(Status.Query, [$"owner={owner}", $"name={name}"], [])!;
		foreach (var node in J.NodesOf(J.Req(J.Req(data, "repository"), "pullRequests")).OfType<JsonObject>())
		{
			var label = $"{owner}/{name}#{J.Fmt(J.Get(node, "number"))}";
			// The id is read only for a query that needs it, so a node without one is read as far as the Python read it.
			string Id() => J.Fmt(J.Req(node, "id"));
			var candidate = J.S(J.Get(node, "state")) == "OPEN" && !J.B(J.Get(node, "isDraft")) && Status.CouldBeExhausted(node);
			if (Status.NeedsComments(node))
			{
				try
				{
					node["comments"] = ReadComments(Id(), candidate);
				}
				catch (Exception error) // an unread record fails closed, it mustn't make the repo unreadable
				{
					node["comments"] = null;
					ReportOnce(("record", Id()), $"{label}: comments unreadable, Copilot neither asked nor called gone, its review loop not called exhausted: {Repr(error)}\n");
				}
			}
			if (candidate)
			{
				try
				{
					node["labelEvents"] = ReadLabelEvents(owner, name, Id());
				}
				catch (Exception error) // unread events fail closed, they mustn't make the repo unreadable
				{
					node["labelEvents"] = null;
					ReportOnce(("events", Id()), $"{label}: label events unreadable, its review loop not called exhausted: {Repr(error)}\n");
				}
			}
			var threads = J.NodesOf(J.Req(node, "reviewThreads")).ToList();
			var allResolved = threads.All(t => J.B(J.Req(t, "isResolved")));
			var resolved = threads.Count > 0 && allResolved && Status.BotReviewed(node);
			var authors = candidate && !allResolved;
			if (resolved || authors)
			{
				try
				{
					// The answer's list is checked here, so one that isn't a list keeps the threads the first read gave and is reported, rather than
					// stored for ToState's strict read to fail on outside this try.
					var read = J.NodesOf(J.Req(J.Req(Graphql(Status.ThreadsQuery, [], [("id", Id())]), "node"), "reviewThreads"));
					((JsonObject)J.Req(node, "reviewThreads")!)["nodes"] = J.Copy(read);
				}
				catch (Exception error) // an advisory notice mustn't make the repo unreadable
				{
					ReportOnce((resolved ? "threads" : "authors", Id()),
							$"{label}: thread comments unreadable, no notice" + (resolved ? "" : ", every unresolved thread counts as a person's") + $": {Repr(error)}\n");
				}
			}
		}
		return data;
	}

	// The comments CommentsQuery reads for a PR, as its "comments" key: {totalCount, nodes}, oldest first.
	//
	// The newest 100. With toTheMarker (a review loop that could be exhausted) the pages before it too, until a page holds PR Auto-Merge's
	// exhaustion comment or the first comment is read (mpaulosky/dotfiles#378), so a PR with more than 100 comments is read in full, as the
	// workflow reads it. A page that fails throws.
	public JsonObject ReadComments(string nodeId, bool toTheMarker)
	{
		var comments = J.Req(J.Req(Graphql(Status.CommentsQuery, [], [("id", nodeId)]), "node"), "comments");
		var nodes = J.Nodes(comments).ToList();
		var info = J.Get(comments, "pageInfo");
		while (toTheMarker && J.B(J.Get(info, "hasPreviousPage")) && Status.ExhaustionMarkers(nodes).Count == 0)
		{
			var page = J.Req(J.Req(Graphql(Status.CommentsQuery, [], [("id", nodeId), ("cursor", J.Fmt(J.Req(info, "startCursor")))]), "node"), "comments");
			nodes = [.. J.Nodes(page), .. nodes];
			info = J.Get(page, "pageInfo");
		}
		var total = toTheMarker && !J.B(J.Get(info, "hasPreviousPage")) ? nodes.Count : J.Int(J.Get(comments, "totalCount")) ?? nodes.Count;
		return new JsonObject
		{
			["totalCount"] = total,
			["nodes"] = new JsonArray([.. nodes.Select(c => J.Copy(c) ?? new JsonObject())]),
		};
	}

	// Whether the login that did a label event has write access to the repo: true, false, or null when it can't be read.
	//
	// The repository's owner always has it, a bot or an actor that can't be read (a deleted account) never does; any other login is asked
	// once through the collaborators' permission endpoint (admin or write). A 404 from it (a login that doesn't exist) is a definite no
	// write access (mpaulosky/dotfiles#381), kept like a found permission; a network error, a 5xx or a rate limit is null and isn't kept, so
	// the next read tries again.
	public bool? WriterAccess(string owner, string name, JsonNode? actor)
	{
		var login = J.Str(actor, "login");
		if (login == "" || J.S(J.Get(actor, "__typename")) == "Bot")
			return false;
		if (login.ToLowerInvariant() == owner.ToLowerInvariant())
			return true;
		var key = ($"{owner}/{name}", login);
		if (!Permissions.TryGetValue(key, out var writer))
		{
			try
			{
				// An answer that isn't an object is unreadable (the Python's .get raised on it), not a definite no.
				writer = Rest($"repos/{owner}/{name}/collaborators/{login}/permission") is JsonObject answer
						? J.S(J.Get(answer, "permission")) is "admin" or "write"
						: throw new GhException("the permission answer isn't a JSON object");
			}
			catch (NotFound)
			{
				writer = false;
			}
			catch (Exception) // an unreadable permission makes the loop unread, never a restart
			{
				return null;
			}
			Permissions[key] = writer;
		}
		return writer;
	}

	// The PR's review:claude label events, all of them, oldest first (see WriterAccess).
	//
	// An add is {createdAt, writer}; github-actions' removal (PR Auto-Merge's, mpaulosky/dotfiles#381) is {createdAt, removed: true}, and
	// costs no permission lookup. Any other removal is dropped. A request for Copilot's review is {createdAt, copilotRequested: true}.
	// EventsQuery is paged to the end: the add that restarts a loop can follow any number of others (mpaulosky/dotfiles#378). A page that
	// fails throws.
	public JsonArray ReadLabelEvents(string owner, string name, string nodeId)
	{
		List<JsonNode?> events = [];
		string? cursor = null;
		while (true)
		{
			(string, string)[] raw = cursor is { Length: > 0 } ? [("id", nodeId), ("cursor", cursor)] : [("id", nodeId)];
			var page = J.Req(J.Req(Graphql(Status.EventsQuery, [], raw), "node"), "timelineItems");
			events.AddRange(J.Nodes(page));
			var info = J.Get(page, "pageInfo");
			if (!J.B(J.Get(info, "hasNextPage")))
				break;
			cursor = J.Fmt(J.Req(info, "endCursor"));
		}
		var read = new JsonArray();
		foreach (var e in events)
		{
			var createdAt = J.Copy(J.Get(e, "createdAt"));
			if (J.B(e) && J.S(J.Get(e, "__typename")) == "ReviewRequestedEvent")
			{
				var reviewer = J.Get(e, "requestedReviewer");
				if (Copilot.IsMatch(Status.ReviewerName(reviewer)))
					read.Add(new JsonObject { ["createdAt"] = createdAt, ["copilotRequested"] = true });
				continue;
			}
			if (!J.B(e) || J.S(J.Get(J.Get(e, "label"), "name")) != ReviewClaude)
				continue;
			if (J.S(J.Get(e, "__typename")) == "UnlabeledEvent")
			{
				if (J.Str(J.Get(e, "actor"), "login").ToLowerInvariant() == "github-actions")
					read.Add(new JsonObject { ["createdAt"] = createdAt, ["removed"] = true });
			}
			else
				read.Add(new JsonObject { ["createdAt"] = createdAt, ["writer"] = WriterAccess(owner, name, J.Get(e, "actor")) });
		}
		return read;
	}

	// Writes message on stderr the first time key fails, so a polling land.cs says it once.
	void ReportOnce((string, string) key, string message)
	{
		if (Reported.Add(key))
			Err.Write(message);
	}

	// An exception as the Python's repr printed it: its type and its message.
	static string Repr(Exception error) => $"{error.GetType().Name}('{error.Message}')";
}

static class Status
{
	public const string Query = """

		query($owner: String!, $name: String!) {
		  repository(owner: $owner, name: $name) {
		    pullRequests(states: OPEN, first: 50, orderBy: {field: CREATED_AT, direction: ASC}) {
		      nodes {
		        id number title state isDraft isCrossRepository headRefName headRefOid mergeStateStatus
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

	// A page of up to 100 comments of one PR, the newest 100 by default and the page before $cursor when given (startCursor of the page
	// read last), for the record that Copilot was asked (mpaulosky/dotfiles#360) and PR Auto-Merge's comment on an exhausted review loop
	// (mpaulosky/dotfiles#375). A loop that could be exhausted pages back to its comment, or the first comment (mpaulosky/dotfiles#378).
	public const string CommentsQuery = """

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

	// The comments of one PR's review threads, for the notice: 1 + 100 requests, 1 point.
	public const string ThreadsQuery = """

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

	// The PR's label events, oldest first, a page of 100 at a time after $cursor (mpaulosky/dotfiles#378): every one is read, since the
	// owner's add of review:claude that restarts a loop can come after any number of others. The removals are read too
	// (mpaulosky/dotfiles#381): the one PR Auto-Merge makes at once is what tells an ignored add from a restart, unless a request for
	// Copilot's review came between the add and the removal, which the review requests tell.
	public const string EventsQuery = """

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

	public const int TitleWidth = 40;

	// The (login, oid, body) of each of Status.Query's reviews.
	static (string? Login, string? Oid, string? Body)[] Reviews(JsonNode node) =>
			[.. J.NodesOf(J.Req(node, "reviews")).Select(r => (J.S(J.Get(J.Get(r, "author"), "login")), J.S(J.Get(J.Get(r, "commit"), "oid")), J.S(J.Get(r, "body"))))];

	// Whether Copilot or Claude Review ever reviewed this PR, from Status.Query's reviews.
	public static bool BotReviewed(JsonNode node) =>
			J.NodesOf(J.Req(node, "reviews")).Any(r => Copilot.IsMatch(J.Login(r)) || IsClaudeReview(J.S(J.Get(J.Get(r, "author"), "login")), "-", J.S(J.Get(r, "body"))));

	// A requested reviewer's name: a Bot or a User has a login, a team a name.
	public static string ReviewerName(JsonNode? reviewer) => J.S(J.Get(reviewer, "login")) is { Length: > 0 } login ? login : J.Str(reviewer, "name");

	// Whether Copilot is among the PR's review requests (a login or a team name, like reapply.sh's copilot_jq).
	static bool CopilotIsRequested(JsonNode node) =>
			J.Nodes(J.Get(node, "reviewRequests")).Any(request => Copilot.IsMatch(ReviewerName(J.Get(request, "requestedReviewer"))));

	static string HeadOf(JsonNode node) => J.S(J.Req(node, "headRefOid")) ?? "";

	// Whether the record that Copilot was asked matters for this PR: open, ready, no real Copilot review of the head, no request.
	public static bool NeedsRecord(JsonNode node)
	{
		if (J.S(J.Get(node, "state")) != "OPEN" || J.B(J.Get(node, "isDraft")))
			return false;
		if (CopilotIsRequested(node))
			return false;
		return !CopilotReviews(Reviews(node)).Contains(HeadOf(node));
	}

	// Whether the PR's review loop could be exhausted: MaxClaudeRounds dirty rounds in a row anywhere, before any restart.
	//
	// The longest run counts, not the trailing one, as in ReviewLoop(): a restarted loop whose newest rounds are clean or unreviewed has
	// still to be walked (mpaulosky/dotfiles#378). Claude's reviews count here as if Copilot were gone unless a request is pending, since
	// only the comments say whether it is; the exact answer is ToState()'s.
	public static bool CouldBeExhausted(JsonNode node)
	{
		var requested = CopilotIsRequested(node);
		var declined = CopilotDeclined(Reviews(node));
		return LongestDirty(ReviewRounds(ReviewRecords(node), declined, !requested || declined.Contains(HeadOf(node)))) >= MaxClaudeRounds;
	}

	// Whether the PR's comments are read: for the record of Copilot's ask (NeedsRecord) or for its review loop's exhaustion comment.
	public static bool NeedsComments(JsonNode node) =>
			J.S(J.Get(node, "state")) == "OPEN" && (NeedsRecord(node) || (!J.B(J.Get(node, "isDraft")) && CouldBeExhausted(node)));

	// The comments among CommentsQuery's nodes that are PR Auto-Merge's (github-actions) exhaustion comments.
	public static List<JsonNode?> ExhaustionMarkers(IEnumerable<JsonNode?> nodes) =>
			[.. nodes.Where(c => NoBot(J.Login(c)) == GithubActions && J.Str(c, "body").Contains(ExhaustedMarker, StringComparison.Ordinal))];

	// A check's name or context is made printable here, so every reason and line that names it is: a fork's workflow names its jobs, and
	// anyone with write access names a status context. A name already printable is unchanged, so ClaudeChecks still matches it.
	public static Check ToCheck(JsonNode node)
	{
		if (J.S(J.Get(node, "__typename")) == "StatusContext")
		{
			var state = J.S(J.Get(node, "state"));
			var context = Printable(J.S(J.Req(node, "context")) ?? "");
			return state is "PENDING" or "EXPECTED"
					? new Check(context, "PENDING", null, J.Str(node, "createdAt"))
					: new Check(context, "COMPLETED", state, J.Str(node, "createdAt"));
		}
		return new Check(Printable(J.S(J.Req(node, "name")) ?? ""), J.S(J.Req(node, "status")) ?? "", J.S(J.Get(node, "conclusion")), J.Str(node, "startedAt"),
				J.Int(J.Get(node, "databaseId")) ?? 0);
	}

	// text with every character Python's str.isprintable() refuses replaced by its repr escape. Every PR- or writer-controlled string status
	// and land print goes through it: a PR's title, a thread's path, a check run's name and a status context.
	public static string Printable(string text)
	{
		var result = new StringBuilder();
		foreach (var point in CodePoints(text))
		{
			var value = point.Length == 2 ? char.ConvertToUtf32(point[0], point[1]) : point[0];
			if (IsPrintable(value))
				result.Append(point);
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

	// A thread's path:line, or what of them it has, printable; "thread" with no path.
	static string ThreadName(JsonNode? node)
	{
		var path = J.Str(node, "path");
		if (path == "")
			return "thread";
		var number = J.B(J.Get(node, "line")) ? J.Get(node, "line") : J.Get(node, "originalLine");
		return Printable(J.B(number) ? $"{path}:{J.Fmt(number)}" : path);
	}

	// A ReviewThread from one reviewThreads node; a node without comments has no opener and is no bot thread.
	public static ReviewThread ToThread(JsonNode? node)
	{
		var comments = J.Get(node, "comments");
		var nodes = J.Nodes(comments).ToList();
		var author = nodes.Count > 0 ? J.Get(nodes[0], "author") : null;
		var review = nodes.Count > 0 ? J.Get(nodes[0], "pullRequestReview") : null;
		return new ReviewThread(
				J.B(J.Get(node, "isResolved")),
				J.Str(author, "login"),
				J.Str(author, "__typename"),
				J.Str(review, "body"),
				[.. nodes.Skip(1).Select(J.Login)],
				(J.Int(J.Get(comments, "totalCount")) ?? nodes.Count) > nodes.Count,
				ThreadName(node));
	}

	// ToState()'s per-repo settings for a repos.txt scope: a Baseline repo ("full") requires checks and runs PR Auto-Merge, and so does the
	// Factory ("factory") and repo-settings-only, with a copy of it.
	public static (bool ChecksRequired, bool MergedByWorkflow) RepoFlags(string scope) => (true, true);

	// CopilotAsk() for a node's "comments" (CommentsQuery's): absent or null, or a page that hides the record, is unread.
	static (bool, bool) AskedCopilot(JsonNode node, string owner, double now)
	{
		var comments = J.Get(node, "comments");
		if (comments is null)
			return CopilotAsk(null, owner, now);
		var nodes = J.Nodes(comments).ToList();
		var found = CopilotAsk(nodes.Select(c => ((string?)J.Login(c), (string?)J.Str(c, "body"), J.S(J.Get(c, "createdAt")))), owner, now);
		// More comments than were read and no record among them: it may be on a page that wasn't read.
		if (found == (false, false) && (J.Int(J.Get(comments, "totalCount")) ?? nodes.Count) > nodes.Count)
			return CopilotAsk(null, owner, now);
		return found;
	}

	// Its strict read can't fail first: every caller reads the same list through Reviews() before it.
	// The reviews of Status.Query's node as ReviewRounds() reads them: (login, oid, body, submittedAt, inline thread count, is a merge).
	static (string?, string?, string?, string?, long, bool)[] ReviewRecords(JsonNode node) =>
			[.. J.NodesOf(J.Req(node, "reviews")).Select(r => (J.S(J.Get(J.Get(r, "author"), "login")), J.S(J.Get(J.Get(r, "commit"), "oid")), J.S(J.Get(r, "body")),
						J.S(J.Get(r, "submittedAt")), J.Int(J.Get(J.Get(r, "comments"), "totalCount")) ?? 0,
						(J.Int(J.Get(J.Get(J.Get(r, "commit"), "parents"), "totalCount")) ?? 1) > 1))];

	// An ISO-8601 time as epoch seconds; -infinity when it is missing or unreadable, which sorts before every time.
	static double Instant(JsonNode? text) => Epoch(J.S(text)) ?? double.NegativeInfinity;

	// (rounds, exhaustion marker newest, loop unread) for a node's review rounds, after the loop's restarts.
	//
	// The rounds that count are the final generation's (Landing.CurrentLoop): a loop that reached MaxClaudeRounds dirty rounds in a row
	// restarts at the first review:claude added after the round that got it there by a login with write access, comment or none, unless
	// PR Auto-Merge removed that add before the next round (the add made while Copilot could review), with no request for Copilot's
	// review in between. Whether Copilot can review now doesn't decide it, so a restart stays recognised (mpaulosky/dotfiles#381). PR
	// Auto-Merge's exhaustion comment (github-actions, ExhaustedMarker) is read from the node's "comments"; it is the newest word when it
	// is no older than the round that exhausted the final generation. Comments, label events or a permission that can't be read make the
	// loop unread when it reached the cap, so it never is called exhausted (reviewLoopState does the same).
	static (Round[], bool, bool) ReviewLoop(JsonNode node, Round[] rounds)
	{
		if (LongestDirty(rounds) < MaxClaudeRounds)
			return (rounds, false, false);
		var comments = J.Get(node, "comments");
		var events = J.Get(node, "labelEvents");
		if (comments is null || events is null)
			return (rounds, false, true);
		var nodes = J.Nodes(comments).ToList();
		var all = events is JsonArray list ? list.ToList() : [];
		var adds = all.Where(e => !J.B(J.Get(e, "removed")) && !J.B(J.Get(e, "copilotRequested")))
				.Select(e => (Instant(J.Get(e, "createdAt")), J.Get(e, "writer") is { } writer ? J.B(writer) : (bool?)null));
		var removals = all.Where(e => J.B(J.Get(e, "removed"))).Select(e => Instant(J.Get(e, "createdAt")));
		var requests = all.Where(e => J.B(J.Get(e, "copilotRequested"))).Select(e => Instant(J.Get(e, "createdAt")));
		var walk = CurrentLoop(rounds, adds, removals, requests);
		var markers = ExhaustionMarkers(nodes).Select(c => Instant(J.Get(c, "createdAt"))).ToList();
		// More comments than were read and no record among them: it may be on a page that wasn't read.
		if (walk.Unread || (markers.Count == 0 && (J.Int(J.Get(comments, "totalCount")) ?? nodes.Count) > nodes.Count))
			return (walk.Rounds, false, true);
		return (walk.Rounds, walk.Hit is double hit && markers.Any(m => m >= hit), false);
	}

	// A PrState from one pullRequests node of Status.Query, as Fetch() left it. owner is the repository's owner, who with github-actions may
	// write the record that Copilot was asked; now is epoch seconds (default: the clock), which the record's 30 seconds are counted to.
	public static PrState ToState(JsonNode node, bool checksRequired = true, bool mergedByWorkflow = false, bool wantReady = false, string owner = "",
			double? now = null)
	{
		var (asked, settled) = AskedCopilot(node, owner, now ?? Now());
		var commits = J.NodesOf(J.Req(node, "commits")).ToList();
		var rollup = commits.Count > 0 ? J.Req(J.Req(commits[0], "commit"), "statusCheckRollup") : null;
		var contexts = J.B(rollup) ? J.NodesOf(J.Req(rollup, "contexts")) : [];
		var reviews = Reviews(node);
		var copilotIsRequested = CopilotIsRequested(node);
		var labels = J.Nodes(J.Get(node, "labels")).Where(J.B).Select(l => J.S(J.Req(l, "name"))).ToHashSet();
		var declined = CopilotDeclined(reviews);
		var head = HeadOf(node);
		// Copilot can't review the PR (PrState.ClaudeBacked): Claude's review of any commit then counts as a round.
		var gone = (asked && settled && !copilotIsRequested) || declined.Contains(head);
		var (rounds, markerNewest, unread) = ReviewLoop(node, ReviewRounds(ReviewRecords(node), declined, gone));
		var threads = J.NodesOf(J.Req(node, "reviewThreads")).Select(t => (Resolved: J.B(J.Req(t, "isResolved")), Thread: ToThread(t))).ToList();
		return new PrState
		{
			State = J.S(J.Req(node, "state")) ?? "",
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
			MergeState = J.S(J.Req(node, "mergeStateStatus")) ?? "",
			Checks = [.. contexts.Where(J.B).Select(c => ToCheck(c!))],
			ChecksRequired = checksRequired,
			AutoMergeArmed = J.Req(node, "autoMergeRequest") is not null,
			WantReady = wantReady,
			MergedByWorkflow = mergedByWorkflow,
			// A fork's PR can have a branch of any name, so only a branch in the repo itself (isCrossRepository false, read as false: a
			// missing value is a fork's) is a release-blog PR, which land.cs arms with no review.
			ReleaseBlog = J.S(J.Get(node, "headRefName")) is { } branch && ReleaseBlogBranches.Contains(branch)
					&& J.Get(node, "isCrossRepository") is JsonValue fork && fork.TryGetValue<bool>(out var crossRepository) && !crossRepository,
		};
	}

	// The clock, in epoch seconds.
	public static double Now() => (DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;

	// text cut to width characters (code points), its last one an ellipsis.
	public static string Shorten(string text, int width = TitleWidth)
	{
		var points = CodePoints(text);
		return points.Count <= width ? text : string.Concat(points.Take(width - 1)) + "…";
	}

	public static string Line(string repo, JsonNode node, PrState pr)
	{
		var checks = SummarizeChecks(pr.Checks);
		var decision = Decide(pr);
		var name = repo.Split('/')[^1];
		var notice = UnrepliedNotice(pr);
		// The title is PR-controlled, so it is printable before it is cut: a control character can't reach the terminal.
		var title = Shorten(Printable(J.Fmt(J.Req(node, "title"))));
		title += new string(' ', Math.Max(0, TitleWidth - CodePoints(title).Count));
		return $"{name}#{J.Fmt(J.Req(node, "number"))} {title} {(pr.Head.Length > 7 ? pr.Head[..7] : pr.Head)}"
				+ $" {(pr.Draft ? "draft" : "ready")} {pr.MergeState}"
				+ $" review:{(pr.HeadReviewer is { Length: > 0 } reviewer ? reviewer : "no")} threads:{pr.OpenThreads}"
				+ $" automerge:{(pr.AutoMergeArmed ? "on" : "off")}"
				+ $" running:{checks.Running.Length} failed:{checks.Failed.Length} cancelled:{checks.Cancelled.Length}"
				+ $" -> {decision.Action} ({decision.Reason})" + (notice != "" ? $" {notice}" : "");
	}

	// status.cs's command line. fetch reads one repo (StatusReader.Fetch by default) and readRepoList the Fleet (Repos.Read).
	public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter err, Func<string, string, JsonNode>? fetch = null,
			Func<OrderedDictionary<string, string>>? readRepoList = null)
	{
		var parser = new Arguments("status.cs", "[-h] [repos ...]", "One line per open PR across the repos in github/repos.txt.");
		if (parser.Parse(args, output, err) is int exit)
			return exit;
		fetch ??= new StatusReader(err: err).Fetch;
		var listed = (readRepoList ?? (() => Repos.Read()))();
		var repos = parser.Positional.Count > 0 ? parser.Positional : [.. listed.Keys];
		// A name gh would read otherwise (-F reads a value starting with @ from a file, and . or .. would step out of a REST path) is
		// refused before any call: a usage error when it was an argument, and exit 1 when it came from the Fleet registry.
		if (repos.FirstOrDefault(repo => !RepoName.IsMatch(repo)) is { } bad)
		{
			if (parser.Positional.Count > 0)
				return parser.Error($"not owner/repo: {bad}", err);
			err.Write($"repos.txt: not owner/repo: {bad}\n");
			return 1;
		}

		List<string> lines = [];
		var status = 0;
		foreach (var repo in repos)
		{
			var (owner, name) = SplitRepo(repo);
			JsonNode data;
			try
			{
				data = fetch(owner, name);
			}
			catch (Exception error) // one unreadable repo mustn't hide the rest
			{
				err.Write($"{repo}: {error.Message}\n");
				status = 1;
				continue;
			}
			var (checksRequired, mergedByWorkflow) = RepoFlags(listed.GetValueOrDefault(repo, "full"));
			foreach (var node in J.NodesOf(J.Req(J.Req(data, "repository"), "pullRequests")))
				lines.Add(Line(repo, node!, ToState(node!, checksRequired, mergedByWorkflow, owner: owner)));
		}
		if (lines.Count > 0)
			output.Write(string.Join("\n", lines) + "\n");
		else if (status == 0)
			output.Write($"No open PRs in {repos.Count} repo(s).\n");
		return status;
	}

	// owner/repo: ASCII letters and digits, _, . and - on each side of one /, as GitHub names are, and neither side . or .. (a path
	// step in the REST calls); a trailing line break too is refused.
	public static readonly Regex RepoName = new(@"\A(?!\.\.?/)[A-Za-z0-9_.-]+/(?!\.\.?\z)[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant);

	// owner/name split at its /; anything but owner/repo throws.
	public static (string Owner, string Name) SplitRepo(string repo)
	{
		var slash = repo.IndexOf('/');
		if (!RepoName.IsMatch(repo))
			throw new ArgumentException($"not owner/repo: {repo}");
		return (repo[..slash], repo[(slash + 1)..]);
	}
}
