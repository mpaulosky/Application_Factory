// How the round records the review a session staged (auto_rollout.py's staged_findings to record_review, ADR 0016), as a library of
// rollout.cs that nothing runs yet (ADR 0017's Update for mpaulosky/Application_Factory#157), kept in step with the Python until the
// cut-over (mpaulosky/Application_Factory#122). The process group a bounded command runs in, and how it is waited on and killed, are the
// cut-over's (ADR 0017's "How rollout's C# runs its children"): RunBounded and RecordReview take them as parameters.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// What a command left in its process group was still there after the group was killed.
sealed class GroupLeft(string message) : Exception(message);

// A command started in a process group of its own, the group's id its pid. Exit waits up to seconds for it to exit and gives its exit
// code (minus the signal that ended it), or null when it hasn't; the leader is left unreaped, so its pid can't be another group's until
// the group is killed.
interface IGroupChild
{
	int Pid { get; }

	int? Exit(double seconds);
}

// Where a bounded command runs and what it reads and writes: its folder, its stdin (null: /dev/null), its stdout, its stderr (null: its
// stdout's, as 2>&1), and the list of deferred signals that stops it (null: none does, as for record, which a kill could leave half-written).
sealed record Bounded(string Cwd, FileStream? Stdin, FileStream Stdout, FileStream? Stderr, IList<int>? Got);

static class ReviewRecord
{
	// What RecordReview gives back besides 0: the session broke a Rule (126, as reapply.sh reads it), the record failed, the session staged
	// nothing.
	public const int SessionBrokeARule = 126;
	public const int RecordFailed = 256;
	public const int NothingStaged = 257;

	// How long scripts/review.sh prepare or record may run, and how long a killed group may take to go.
	public const int RecordTimeoutSeconds = 600;
	public const int GroupWaitSeconds = 10;

	// The file scripts/review.sh stage writes in the session's run folder and record --staged reads, and the HEAD kept beside it.
	public const string StagedFile = "findings.json";
	public const string KeptHeadFile = "HEAD";

	// How a review-only session may stage a finding.
	public static readonly IReadOnlyList<string> StagedStatuses = ["open", "dismissed"];

	// The agent the review is recorded under: the one the session ran.
	public const string ReviewerAgent = "pre-push-reviewer";

	// Whether a staged finding may be a security one, read wider than the ledger's isSecurity, never narrower: graded Warning, not marked
	// security false, or a lens that isn't text or names security once all but its letters are dropped (the ledger strips control and
	// invisible characters from a lens before it reads it).
	public static bool IsSecurity(JsonObject finding)
	{
		var security = finding.TryGetPropertyValue("security", out var flag) ? flag : null;
		return J.S(J.Get(finding, "severity")) == "warning"
				|| security?.GetValueKind() != JsonValueKind.False
				|| J.S(J.Get(finding, "lens")) is not { } lens
				|| Regex.Replace(Py.Lower(lens), "[^a-z]", "").Contains("security", StringComparison.Ordinal);
	}

	// (the findings the session staged in run, the Rules they break): (null, []) when it staged none, and (null, [the rule]) when the file
	// isn't a JSON array of objects that RecordBytes reads. A review-only round stages each finding open, or dismissed when it isn't a
	// security one (only the person decides one): fixed or accepted breaks a rule, and so does a security finding dismissed. A finding's
	// repeats is left to RecordReview, which drops it. Everything else (a field, an id taken) is record's to refuse.
	public static (JsonArray? Findings, List<string> Broke) StagedFindings(string run)
	{
		var path = Path.Combine(run, StagedFile);
		if (Posix.LStat(path) is null)
			return (null, []);
		var findings = Parse(SessionGuard.RecordBytes(path));
		if (findings is not JsonArray array || array.Any(finding => finding is not JsonObject))
			return (null, [$"{path} isn't the JSON array of findings scripts/review.sh stage writes"]);
		List<string> broke = [];
		foreach (var finding in array.Cast<JsonObject>())
		{
			var id = SessionGuard.Shown(J.Get(finding, "id"));
			var status = J.Get(finding, "status");
			if (J.S(status) is not { } text || !StagedStatuses.Contains(text))
				broke.Add($"{id} was staged {SessionGuard.Shown(status)}: a review-only session stages a finding open or dismissed, since it"
						+ " fixes nothing and only the person accepts");
			else if (text == "dismissed" && IsSecurity(finding))
				broke.Add($"{id} was staged dismissed, and may be a security finding (graded warning, a lens naming security, or no"
						+ " \"security\": false): only the person decides one, so the session records it open");
		}
		return (broke.Count > 0 ? null : array, broke);
	}

	// json.loads(data.decode("utf-8")), or null when it isn't strict UTF-8 or JSON, or nests deeper than Python's parser goes. An object's
	// repeated key keeps its first place and its last value, as a dict does.
	static JsonNode? Parse(byte[]? data)
	{
		if (data is null)
			return null;
		try
		{
			var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
			using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 1000 });
			return Node(document.RootElement);
		}
		catch (Exception error) when (error is JsonException or ArgumentException)
		{
			return null;
		}
	}

	static JsonNode? Node(JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Array:
				return new JsonArray([.. element.EnumerateArray().Select(Node)]);
			case JsonValueKind.Object:
				var obj = new JsonObject();
				foreach (var property in element.EnumerateObject())
					obj[property.Name] = Node(property.Value);
				return obj;
			case JsonValueKind.Null:
				return null;
			default:
				return JsonNode.Parse(element.GetRawText());
		}
	}

	// Where the findings a session staged are kept when the round can't record them: beside its log, in a 0700 folder whose
	// findings.json is 0600, as scripts/review.sh record --staged reads them.
	public static string StagedCopy(string log) => $"{log}.staged";

	// The command the person runs to record findings kept in staged themselves, once what refused them is put right: the key and HEAD of
	// the worktree's code now, from scripts/review.sh prepare, given to scripts/review.sh record, only while that HEAD is head, the one the
	// session reviewed (a commit since would have the findings vouch for code the session never read); otherwise it says why and records
	// nothing. It is one sh -c, so it runs the same pasted into zsh or fish as into bash.
	public static string RecordCommand(string worktree, string staged, string head)
	{
		var script = $"cd {PyText.ShQuote(worktree)} || exit 1\n"
				+ "out=$(scripts/review.sh prepare \"$(mktemp -d)\") || { echo \"scripts/review.sh prepare failed: nothing recorded\" >&2; exit 1; }\n"
				+ "set -- $out\n"
				+ "if [ \"$#\" -ne 2 ]; then echo \"scripts/review.sh prepare printed $# word(s), not its key and HEAD: nothing recorded\" >&2; exit 1; fi\n"
				+ $"if [ \"$2\" != {PyText.ShQuote(head)} ]; then echo \"the worktree's HEAD is not {head}, the one the session reviewed: nothing"
				+ " recorded\" >&2; exit 1; fi\n"
				+ "printf '{\"reviewedKey\":\"%s\",\"reviewedHead\":\"%s\",\"rounds\":1,\"reviewerAgent\":"
				+ $"\"{ReviewerAgent}\"}}' \"$1\" \"$2\" | scripts/review.sh record - --staged {PyText.ShQuote(staged)}\n";
		return $"sh -c {PyText.ShQuote(script)}";
	}

	// Make folder 0700 and write text to its findings.json, 0600, never over a file already there; with head, the HEAD the session
	// reviewed, to its HEAD file as well (KeptHead reads it).
	public static void WriteStaged(string folder, string text, string? head = null)
	{
		Posix.MakeDirectory(folder, Convert.ToUInt32("700", 8));
		foreach (var (name, content) in new[] { (StagedFile, text), (KeptHeadFile, head) })
		{
			if (content is null)
				continue;
			var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
			if (!OperatingSystem.IsWindows())
				options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
			using var file = new FileStream(Path.Combine(folder, name), options);
			file.Write(Encoding.UTF8.GetBytes(content));
		}
	}

	static readonly Regex ObjectId = new(@"\A[0-9a-f]{40}([0-9a-f]{24})?\z", RegexOptions.CultureInvariant);

	// The HEAD the session reviewed, as RecordReview kept it beside the findings in keep; null when none is kept. The round writes it,
	// outside the sandbox, so unlike the log the session can't have written it.
	public static string? KeptHead(string keep)
	{
		string head;
		try
		{
			using var reader = new StreamReader(Path.Combine(keep, KeptHeadFile), Encoding.UTF8);
			var buffer = new char[100];
			head = Py.Strip(new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length)));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return null;
		}
		return ObjectId.IsMatch(head) ? head : null;
	}

	// How the person sees and ends what is left in a process group, with commands both procps and the BSDs have.
	public static string GroupHint(int pgid) => $"see it with pgrep -l -g {pgid}, and end it with pkill -KILL -g {pgid}";

	// The child's exit code, or null once timeout seconds pass or a deferred signal arrives (got holds it). It polls, since with the
	// signals deferred nothing interrupts a wait.
	public static int? WaitFor(IGroupChild child, double timeout, IList<int> got, Func<double>? clock = null)
	{
		clock ??= Monotonic;
		var deadline = clock() + timeout;
		while (true)
		{
			if (child.Exit(Math.Max(0.0, Math.Min(0.5, deadline - clock()))) is int code)
				return code;
			if (got.Count > 0 || clock() >= deadline)
				return null;
		}
	}

	static double Monotonic() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

	// A command the round runs to record a review (scripts/review.sh prepare or record), in a process group of its own that holds the
	// round's lock (spawn starts it so): its exit code, or null when it ran past timeout seconds or a deferred signal came (io.Got holds
	// it: given for prepare, which writes only a folder of its own, and not for record, which a kill could leave half-written). Its group
	// is killed whole however it ends (end: whether it is gone), so nothing it started (tsx's node) goes on writing the ledgers or holding
	// their lock, and a terminal's interrupt reaches only the round. Throws GroupLeft when the group is still there after the kill.
	public static int? RunBounded(IReadOnlyList<string> command, Bounded io, Func<IReadOnlyList<string>, Bounded, IGroupChild> spawn,
			Func<IGroupChild, bool> end, double timeout = RecordTimeoutSeconds, Func<double>? clock = null)
	{
		var child = spawn(command, io);
		try
		{
			return WaitFor(child, timeout, io.Got ?? [], clock);
		}
		finally
		{
			if (!end(child))
				throw new GroupLeft($"what {Path.GetFileName(command[0])} {command[1]} left in its process group was still there"
						+ $" {GroupWaitSeconds} s after it was killed, and may still write the review ledgers and .security: {GroupHint(child.Pid)},"
						+ " before you resume");
		}
	}

	// Whether the worktree's node_modules/.bin/tsx is there for scripts/review.sh to run, so it installs nothing.
	public static bool TsxInstalled(string worktree) => Posix.Executable(Path.Combine(worktree, "node_modules", ".bin", "tsx"));

	// Record the findings the session staged in run, as the review's record: scripts/review.sh prepare in a folder of the round's own
	// gives the key and HEAD of the code, and scripts/review.sh record takes them, with the staged findings copied there 0600 (record reads
	// only a file of the person's own). Both are the worktree's, its Owned copies the Template's, and run as the gate on the push runs
	// them, outside the sandbox (bounded: RunBounded with the cut-over's group); unseen() is asked once more just before record runs
	// (SessionGuard.UnseenChanges). head is the HEAD the guard read before the session: a prepare at another one means something committed
	// there since, and nothing is recorded. A finding's repeats is dropped (and said in the log): it would tie the finding to a .security
	// entry, which the person's accept of the finding then accepts too, and the session could name any entry, an open one of another
	// branch's; the record then matches it by its path and summary, and one worded otherwise gets an entry of its own, open. What the
	// session staged is kept in keep (StagedCopy) before anything runs, and removed once it is recorded or a Rule is found broken (the code
	// changed since the session read it, so it vouches for none of it); any other failure, a GroupLeft or a bug too, leaves it there. tsx
	// not installed is a failure before prepare runs, since scripts/review.sh would install it into the worktree just checked.
	//
	// (exit code, the Rules broken): 0 when it recorded; NothingStaged when the session staged nothing, not even []; RecordFailed when
	// prepare or record refused, ran past its time, was stopped by a signal (got), can't be told to have printed the key and HEAD, or
	// prepare's group was left (it writes only the round's own folder, so nothing was recorded; the list then holds that group, not a Rule,
	// since it still holds the lock), and the log says why and how to record the kept findings; SessionBrokeARule with the Rules when the
	// staged findings break one (StagedFindings), HEAD moved, or unseen() names one; a GroupLeft from record is thrown. prepare's stdout
	// is read for its key and HEAD; everything else either prints goes to log. dumps is json.dumps (Py.Dumps), which a test makes fail.
	public static (int Code, List<string> Broke) RecordReview(string worktree, string run, FileStream log, string head, string keep,
			Func<IReadOnlyList<string>, Bounded, int?> bounded, IList<int>? got = null, Func<List<string>>? unseen = null,
			Func<string, bool>? installed = null, Func<JsonNode, string>? dumps = null)
	{
		got ??= [];
		var (findings, broke) = StagedFindings(run);
		if (broke.Count > 0)
			return (SessionBrokeARule, broke);
		if (findings is null)
		{
			Say(log, $"rollout.sh: the session staged no findings in {run}, not even [], so no review was recorded\n");
			return (NothingStaged, []);
		}
		foreach (var finding in findings.Cast<JsonObject>())
			if (finding.TryGetPropertyValue("repeats", out var repeats))
			{
				Say(log, $"rollout.sh: {SessionGuard.Shown(J.Get(finding, "id"))}'s repeats {SessionGuard.Shown(repeats)} was dropped: the record"
						+ " matches the finding to a .security entry by its path and summary (case and spacing aside)\n");
				finding.Remove("repeats");
			}
		string text;
		try
		{
			text = (dumps ?? (node => Py.Dumps(node)))(findings);
		}
		catch (Exception error) when (error is InvalidOperationException or InsufficientExecutionStackException)
		{
			return (SessionBrokeARule, [$"{SessionGuard.Shown(Path.Combine(run, StagedFile))} nests too deep to be written again"]);
		}
		var review = Path.Combine(worktree, "scripts", "review.sh");
		try
		{
			WriteStaged(keep, text, head);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			Say(log, $"rollout.sh: what the session staged couldn't be kept in {keep} ({error.Message}): if the record fails, it is gone\n");
		}

		(int, List<string>) Failed(string why, List<string>? left = null)
		{
			Say(log, $"rollout.sh: {why}, so the review wasn't recorded\n");
			if (File.Exists(Path.Combine(keep, StagedFile)) && KeptHead(keep) == head)
				Say(log, $"rollout.sh: what the session staged is kept in {keep}; once what refused it is put right, record it with:\n"
						+ $"  {RecordCommand(worktree, keep, head)}\n");
			return (RecordFailed, left ?? []);
		}

		(int, List<string>) BrokeRule(List<string> rules)
		{
			// The code changed since the session read it: what it staged vouches for none of it, so nothing is kept.
			RemoveTree(keep);
			return (SessionBrokeARule, rules);
		}

		if (got.Count > 0)
			return Failed("a signal came before scripts/review.sh prepare ran");
		if (!(installed ?? TsxInstalled)(worktree))
			return Failed($"{Path.Combine(worktree, "node_modules", ".bin", "tsx")} isn't installed (pnpm install in the worktree):"
					+ " scripts/review.sh would install it, changing the worktree the round has just checked");
		var own = Directory.CreateTempSubdirectory("rollout-record.").FullName;
		try
		{
			var staged = Path.Combine(own, "staged");
			WriteStaged(staged, text);
			int? code;
			string[] lines;
			using (var keyAndHead = new FileStream(Path.Combine(own, "prepared"), FileMode.Create, FileAccess.ReadWrite))
			{
				try
				{
					code = bounded([review, "prepare", Path.Combine(own, "prepare")], new Bounded(worktree, null, keyAndHead, log, got));
				}
				catch (GroupLeft error)
				{
					// prepare writes only the round's own folder, so nothing was recorded; the group still holds the lock, so the stop
					// names it (a resume would be refused until it is ended).
					return Failed(error.Message, [error.Message]);
				}
				keyAndHead.Position = 0;
				var buffer = new char[4096];
				using var reader = new StreamReader(keyAndHead, Encoding.UTF8, leaveOpen: true);
				lines = Words(new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length)));
			}
			if (code is null && got.Count > 0)
				return Failed("scripts/review.sh prepare was stopped by a signal");
			if (code is null)
				return Failed($"scripts/review.sh prepare ran past {RecordTimeoutSeconds} s and was stopped");
			if (code != 0 || lines.Length != 2)
				return Failed($"scripts/review.sh prepare exited {code} and printed {lines.Length} line(s), not its key and HEAD");
			var (key, preparedHead) = (lines[0], lines[1]);
			if (preparedHead != head)
				return BrokeRule([$"the worktree's HEAD was {SessionGuard.Shown(preparedHead)} by the time the review was recorded, not {head}"
						+ " as the session reviewed it: something committed there since, so nothing was recorded"]);
			var unseenRules = (unseen ?? (() => []))();
			if (unseenRules.Count > 0)
				return BrokeRule(unseenRules);
			var given = Path.Combine(own, "review.json");
			File.WriteAllText(given, Py.Dumps(new JsonObject
			{
				["reviewedKey"] = key,
				["reviewedHead"] = preparedHead,
				["rounds"] = 1,
				["reviewerAgent"] = ReviewerAgent,
			}));
			Say(log, $"rollout.sh: recording the {findings.Count} finding(s) the session staged\n");
			using (var reviewInput = new FileStream(given, FileMode.Open, FileAccess.Read))
				code = bounded([review, "record", "-", "--staged", staged], new Bounded(worktree, reviewInput, log, null, null));
			log.Flush();
			if (code is null)
				return Failed($"scripts/review.sh record ran past {RecordTimeoutSeconds} s and was stopped");
			if (code != 0)
				return Failed($"scripts/review.sh record exited {code}");
		}
		finally
		{
			RemoveTree(own);
		}
		RemoveTree(keep);
		return (0, []);
	}

	// Text written to the log at once, so it comes before whatever a command the round starts writes there.
	static void Say(FileStream log, string text)
	{
		log.Write(Encoding.UTF8.GetBytes(text));
		log.Flush();
	}

	// str.split(): the words between runs of what str.isspace() counts.
	static string[] Words(string text)
	{
		List<string> words = [];
		var start = -1;
		for (var i = 0; i <= text.Length; i++)
		{
			var space = i == text.Length || char.IsWhiteSpace(text[i]) || text[i] is >= '\x1c' and <= '\x1f';
			if (space && start >= 0)
			{
				words.Add(text[start..i]);
				start = -1;
			}
			else if (!space && start < 0)
				start = i;
		}
		return [.. words];
	}

	// shutil.rmtree(path, ignore_errors=True).
	static void RemoveTree(string path)
	{
		try
		{
			Directory.Delete(path, recursive: true);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
		}
	}
}
