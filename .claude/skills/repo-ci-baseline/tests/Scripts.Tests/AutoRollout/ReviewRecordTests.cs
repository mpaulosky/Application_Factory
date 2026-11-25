using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

// Tests for github/AutoRolloutRecord.cs, xUnit copies of github/tests/test_auto_rollout.py's tests of staged_findings, record_review,
// record_command, write_staged, kept_head and run_bounded (ADR 0017's Update for mpaulosky/Application_Factory#157): one method each, the
// same cases and assertions. Two of them run a real command in a process group of its own
// (test_the_record_runs_a_real_review_script_in_a_group_of_its_own_and_logs_both_its_streams and
// test_a_bounded_command_past_its_time_is_stopped_with_its_group), which needs the spawner the cut-over brings
// (mpaulosky/Application_Factory#122), so they move with it. The pytest originals stay until then.
[Collection(nameof(SessionGuardTests))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ReviewRecordTests : IDisposable
{
	readonly string tmp;
	readonly FileStream log;

	// Skips off Linux before anything is made, as SessionGuardTests does.
	public ReviewRecordTests()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		tmp = Directory.CreateTempSubdirectory("auto-rollout.").FullName;
		log = new FileStream(Path.Combine(tmp, "out.log"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
	}

	public void Dispose()
	{
		log.Dispose();
		Directory.Delete(tmp, recursive: true);
	}

	string P(params string[] parts) => Path.Combine([tmp, .. parts]);

	// What the round wrote to its log.
	string Out()
	{
		using var reader = new FileStream(log.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		return new StreamReader(reader, Encoding.UTF8).ReadToEnd();
	}

	static JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;

	static JsonObject With(JsonObject finding, string json)
	{
		var copy = (JsonObject)finding.DeepClone();
		foreach (var (key, value) in J(json))
			copy[key] = value?.DeepClone();
		return copy;
	}

	static readonly JsonObject Open = J("""
			{"id": "R1", "lens": "Bar", "path": "a.py", "line": 1, "summary": "s", "severity": "critical", "security": false,
			 "status": "open", "test": null, "reason": null, "fix": "f"}
			""");

	const string NotSecurity = """{"severity": "critical", "security": false, "lens": "Tests"}""";

	static void SameJson(JsonNode? expected, JsonNode? actual) =>
			Assert.True(JsonNode.DeepEquals(expected, actual), $"{expected?.ToJsonString()} != {actual?.ToJsonString()}");

	static JsonArray Array(params JsonObject[] findings) => [.. findings.Select(finding => (JsonNode)finding.DeepClone())];

	string Stage(JsonArray? findings = null, byte[]? raw = null)
	{
		var run = P("run");
		Directory.CreateDirectory(run);
		File.WriteAllBytes(Path.Combine(run, ReviewRecord.StagedFile), raw ?? Encoding.UTF8.GetBytes(findings!.ToJsonString()));
		return run;
	}

	[Fact]
	public void Staged_findings_kept_open_or_dismissed_break_nothing()
	{
		var findings = Array(Open, With(Open, """{"id": "R2", "severity": "warning", "security": true, "lens": "Security"}"""),
				With(With(With(Open, """{"id": "R3"}"""), NotSecurity), """{"status": "dismissed", "reason": "r"}"""));
		var (staged, broke) = ReviewRecord.StagedFindings(Stage(findings));
		SameJson(findings, staged);
		Assert.Empty(broke);
		// A round that keeps nothing stages [].
		(staged, broke) = ReviewRecord.StagedFindings(Stage([]));
		SameJson(new JsonArray(), staged);
		Assert.Empty(broke);
	}

	[Fact]
	public void Nothing_staged_is_no_findings_and_no_broken_rule()
	{
		var (staged, broke) = ReviewRecord.StagedFindings(tmp);
		Assert.Null(staged);
		Assert.Empty(broke);
	}

	[Theory]
	[InlineData("\"fixed\"", "'fixed'")]
	[InlineData("\"accepted\"", "'accepted'")]
	[InlineData("null", "None")]
	[InlineData("5", "5")]
	[InlineData("\"Open\"", "'Open'")]
	public void A_finding_staged_fixed_accepted_or_otherwise_breaks_a_rule(string status, string repr)
	{
		var (findings, broke) = ReviewRecord.StagedFindings(Stage(Array(Open, With(Open, $$"""{"id": "R2", "status": {{status}}}"""))));
		Assert.Null(findings);
		Assert.Equal([$"'R2' was staged {repr}: a review-only session stages a finding open or dismissed, since it fixes nothing and only the"
				+ " person accepts"], broke);
	}

	[Theory]
	[InlineData("""{"severity": "warning", "security": false, "lens": "Tests"}""", true)]
	[InlineData("""{"severity": "critical", "security": true, "lens": "Tests"}""", true)]
	[InlineData("""{"severity": "critical", "lens": "Tests"}""", true)]
	[InlineData("""{"severity": "critical", "security": false, "lens": "Bar/Security"}""", true)]
	[InlineData("""{"severity": "critical", "security": false, "lens": "SECURITY"}""", true)]
	[InlineData("""{"severity": "critical", "security": false, "lens": "Sec​urity"}""", true)]
	[InlineData("""{"severity": "critical", "security": false, "lens": "Sec­urity"}""", true)]
	[InlineData("""{"severity": "critical", "security": false, "lens": "Sec urity"}""", true)]
	[InlineData("""{"severity": "critical", "security": false}""", true)]
	[InlineData("""{"severity": "critical", "security": false, "lens": null}""", true)]
	[InlineData("""{"severity": "critical", "security": false, "lens": 5}""", true)]
	[InlineData(NotSecurity, false)]
	[InlineData("""{"severity": "advisory", "security": false, "lens": "Docs match the code"}""", false)]
	public void A_security_finding_staged_dismissed_breaks_a_rule_read_wider_than_the_ledger(string finding, bool security)
	{
		// The ledger reads the lens with its control and invisible characters stripped: read here with all but its letters dropped, a lens
		// can't hide security from the check and show it to the ledger.
		var dismissed = (JsonObject)Open.DeepClone();
		dismissed.Remove("security");
		dismissed.Remove("lens");
		dismissed = With(With(dismissed, finding), """{"status": "dismissed", "reason": "r"}""");
		var (findings, broke) = ReviewRecord.StagedFindings(Stage(Array(dismissed)));
		Assert.Equal(security, ReviewRecord.IsSecurity(dismissed));
		Assert.Equal(security
				? ["'R1' was staged dismissed, and may be a security finding (graded warning, a lens naming security, or no \"security\": false):"
						+ " only the person decides one, so the session records it open"]
				: [], broke);
		SameJson(security ? null : Array(dismissed), findings);
	}

	[Theory]
	[InlineData("""[{"id": "\ud800", "status": "open"}]""")]
	[InlineData("""[{"id": "R1", "status": "open", "lens": ["\udc00"]}]""")]
	[InlineData("""[{"id": "R1", "status": "open", "\ud800": 1}]""")]
	public void Staged_findings_with_a_lone_surrogate_in_a_string_or_a_name_break_a_rule(string raw)
	{
		var run = P("run");
		Directory.CreateDirectory(run);
		File.WriteAllBytes(Path.Combine(run, ReviewRecord.StagedFile), Encoding.ASCII.GetBytes(raw));
		var (findings, broke) = ReviewRecord.StagedFindings(run);
		Assert.Null(findings);
		Assert.Equal([$"{Path.Combine(run, ReviewRecord.StagedFile)} isn't the JSON array of findings scripts/review.sh stage writes"], broke);
	}

	public static TheoryData<string> NotFindings() => new(["{", "{}", """{"findings": []}""", "5", """["x"]""", "[[]]", "\xff", new string('[', 100000)]);

	[Theory]
	[MemberData(nameof(NotFindings))]
	public void Staged_findings_that_arent_a_json_array_of_objects_break_a_rule(string raw)
	{
		var run = P("run");
		Directory.CreateDirectory(run);
		File.WriteAllBytes(Path.Combine(run, ReviewRecord.StagedFile), Encoding.Latin1.GetBytes(raw));
		var (findings, broke) = ReviewRecord.StagedFindings(run);
		Assert.Null(findings);
		Assert.Equal([$"{Path.Combine(run, ReviewRecord.StagedFile)} isn't the JSON array of findings scripts/review.sh stage writes"], broke);
	}

	[Theory]
	[InlineData("fifo")]
	[InlineData("symlink")]
	[InlineData("dangling")]
	[InlineData("folder")]
	[InlineData("oversized")]
	public void Staged_findings_that_arent_a_small_regular_file_are_never_read(string what)
	{
		var run = P("run");
		Directory.CreateDirectory(run);
		var path = Path.Combine(run, ReviewRecord.StagedFile);
		var text = Array(Open).ToJsonString();
		if (what == "fifo")
			SessionGuardTests.MkFifo(path);
		else if (what == "symlink")
		{
			File.WriteAllText(P("secret"), text);
			File.CreateSymbolicLink(path, P("secret"));
		}
		else if (what == "dangling")
			File.CreateSymbolicLink(path, P("nowhere")); // not "nothing staged": a link is there
		else if (what == "folder")
			Directory.CreateDirectory(path);
		else
			File.WriteAllText(path, text);
		var limit = SessionGuard.RecordLimit;
		if (what == "oversized")
			SessionGuard.RecordLimit = 10;
		try
		{
			var (findings, broke) = ReviewRecord.StagedFindings(run);
			Assert.Null(findings);
			Assert.Equal([$"{path} isn't the JSON array of findings scripts/review.sh stage writes"], broke);
		}
		finally
		{
			SessionGuard.RecordLimit = limit;
		}
	}

	[Fact]
	public void Shown_cuts_a_text_just_past_its_length() => Assert.Equal("'" + new string('x', 119) + "... (121 characters)", SessionGuard.Shown(new string('x', 119)));

	[Fact]
	public void Shown_cuts_a_long_text_so_it_cant_flood_the_terminal()
	{
		Assert.Equal("'R1'", SessionGuard.Shown("R1"));
		// 120 characters, quotes included, are shown whole.
		Assert.Equal("'" + new string('x', 118) + "'", SessionGuard.Shown(new string('x', 118)));
		Assert.Equal("'" + new string('x', 119) + "... (10002 characters)", SessionGuard.Shown(new string('x', 10_000)));
	}

	[Fact]
	public void A_staged_id_and_status_are_printed_escaped()
	{
		var (_, broke) = ReviewRecord.StagedFindings(Stage(Array(With(Open, """{"id": "R1\u001b[2J\nrollout.sh: ok", "status": "fixed\u0007"}"""))));
		Assert.Equal([@"'R1\x1b[2J\nrollout.sh: ok' was staged 'fixed\x07': a review-only session stages a finding open or dismissed, since it"
				+ " fixes nothing and only the person accepts"], broke);
	}

	static readonly string Head = new('c', 40);
	static readonly string KeyAndHead = new string('k', 64) + "\n" + Head + "\n";

	// RunBounded as RecordReview runs it with scripts/review.sh: prepare prints the key and HEAD on its stdout and a note on its stderr,
	// record reads its input and prints. A code of null is a command that ran past its time.
	sealed class Recorder((int? Code, string Text)? prepare = null, int? record = 0)
	{
		readonly (int? Code, string Text) prepare = prepare ?? (0, KeyAndHead);
		public readonly List<(IReadOnlyList<string> Command, Bounded Io)> Calls = [];
		public (JsonNode? Findings, UnixFileMode Folder, UnixFileMode File) Staged;
		public JsonNode? Input;

		public int? Call(IReadOnlyList<string> command, Bounded io)
		{
			Calls.Add((command, io));
			if (command[1] == "prepare")
			{
				// prepare makes the folder it is given.
				Assert.True(Path.IsPathRooted(command[2]) && !Path.Exists(command[2]));
				io.Stdout.Write(Encoding.UTF8.GetBytes(prepare.Text));
				io.Stderr!.Write("prepare's stderr\n"u8);
				io.Stderr.Flush();
				return prepare.Code;
			}
			var staged = command[4];
			Staged = (JsonNode.Parse(File.ReadAllText(Path.Combine(staged, ReviewRecord.StagedFile))), File.GetUnixFileMode(staged),
					File.GetUnixFileMode(Path.Combine(staged, ReviewRecord.StagedFile)));
			Input = JsonNode.Parse(new StreamReader(io.Stdin!).ReadToEnd());
			io.Stdout.Write("Review recorded.\n"u8);
			io.Stdout.Flush();
			return record;
		}
	}

	string Keep => P("log.staged");

	// The (exit code, Rules broken) RecordReview gave is code and broke.
	static void Is(int code, (int Code, List<string> Broke) result, params string[] broke)
	{
		Assert.Equal(code, result.Code);
		Assert.Equal(broke, result.Broke);
	}

	(int Code, List<string> Broke) Recorded(JsonArray? findings, Func<IReadOnlyList<string>, Bounded, int?> recorder, string? head = null)
	{
		var run = findings is not null ? Stage(findings) : tmp;
		return ReviewRecord.RecordReview("/w", run, log, head ?? Head, Keep, recorder, installed: _ => true);
	}

	(JsonNode? Findings, UnixFileMode Folder, UnixFileMode File, string? Head) Kept() =>
			(JsonNode.Parse(File.ReadAllText(Path.Combine(Keep, ReviewRecord.StagedFile))), File.GetUnixFileMode(Keep),
					File.GetUnixFileMode(Path.Combine(Keep, ReviewRecord.StagedFile)), ReviewRecord.KeptHead(Keep));

	const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
	const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

	void FailedAndKept(string why)
	{
		Assert.EndsWith($"rollout.sh: {why}, so the review wasn't recorded\n"
				+ $"rollout.sh: what the session staged is kept in {Keep}; once what refused it is put right, record it with:\n"
				+ $"  {ReviewRecord.RecordCommand("/w", Keep, Head)}\n", Out());
		var kept = Kept();
		SameJson(Array(Open), kept.Findings);
		Assert.Equal((Private, PrivateFile, Head), (kept.Folder, kept.File, kept.Head));
	}

	[Fact]
	public void The_round_records_what_the_session_staged_with_the_worktrees_own_review_script()
	{
		var recorder = new Recorder();
		Is(0, Recorded(Array(Open), recorder.Call));
		var ((prepare, prepareIo), (record, recordIo)) = (recorder.Calls[0], recorder.Calls[1]);
		Assert.Equal(2, recorder.Calls.Count);
		Assert.Equal(["/w/scripts/review.sh", "prepare"], prepare.Take(2));
		Assert.Equal("/w", prepareIo.Cwd);
		Assert.Null(prepareIo.Stdin);
		// prepare's notes go to the log.
		Assert.Same(log, prepareIo.Stderr);
		// Its key and HEAD are read, not logged.
		Assert.NotSame(log, prepareIo.Stdout);
		Assert.Equal(["/w/scripts/review.sh", "record", "-", "--staged"], record.Take(4));
		Assert.Equal("/w", recordIo.Cwd);
		// record's .security notes, on its stderr, go to the log too.
		Assert.Same(log, recordIo.Stdout);
		Assert.Null(recordIo.Stderr);
		// record reads a copy of the round's own, not the session's folder.
		Assert.False(record[4].StartsWith(tmp, StringComparison.Ordinal));
		SameJson(J($$"""{"reviewedKey": "{{new string('k', 64)}}", "reviewedHead": "{{Head}}", "rounds": 1, "reviewerAgent": "pre-push-reviewer"}"""),
				recorder.Input);
		// The copy is the person's own, as record wants it.
		SameJson(Array(Open), recorder.Staged.Findings);
		Assert.Equal((Private, PrivateFile), (recorder.Staged.Folder, recorder.Staged.File));
		// A signal can stop prepare, never record.
		Assert.NotNull(prepareIo.Got);
		Assert.Empty(prepareIo.Got);
		Assert.Null(recordIo.Got);
		// The copy is gone after the record.
		Assert.False(Path.Exists(record[4]));
		Assert.Contains("rollout.sh: recording the 1 finding(s) the session staged\nReview recorded.\n", Out(), StringComparison.Ordinal);
		// Nothing is kept once it is recorded.
		Assert.False(Path.Exists(Keep));
	}

	[Fact]
	public void The_round_records_an_empty_review()
	{
		var recorder = new Recorder();
		Is(0, Recorded([], recorder.Call));
		SameJson(new JsonArray(), recorder.Staged.Findings);
	}

	[Fact]
	public void Nothing_staged_is_its_own_stop_and_records_nothing()
	{
		var recorder = new Recorder();
		Is(ReviewRecord.NothingStaged, Recorded(null, recorder.Call));
		Assert.Empty(recorder.Calls);
		Assert.Equal($"rollout.sh: the session staged no findings in {tmp}, not even [], so no review was recorded\n", Out());
	}

	[Fact]
	public void Staged_findings_that_break_a_rule_are_never_recorded()
	{
		var recorder = new Recorder();
		var (code, broke) = Recorded(Array(With(Open, """{"status": "accepted"}""")), recorder.Call);
		Assert.Equal(ReviewRecord.SessionBrokeARule, code);
		Assert.Single(broke);
		Assert.Empty(recorder.Calls);
	}

	[Fact]
	public void A_findings_repeats_is_dropped_and_said()
	{
		// The session could tie a finding to any .security entry, an open one of another branch's, which the person's accept of the finding
		// would then accept: the record matches a repeat by its path and summary (case and spacing aside).
		var recorder = new Recorder();
		Is(0, Recorded(Array(With(Open, """{"repeats": "0007"}"""), With(Open, """{"id": "R2"}""")), recorder.Call));
		SameJson(Array(Open, With(Open, """{"id": "R2"}""")), recorder.Staged.Findings);
		Assert.Contains("rollout.sh: 'R1''s repeats '0007' was dropped: the record matches the finding to a .security entry by its path and summary"
				+ " (case and spacing aside)\n", Out(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("maximum recursion depth exceeded while encoding a JSON object", false)]
	[InlineData("Insufficient stack to continue executing the program safely.", true)]
	public void Findings_that_cant_be_written_again_break_a_rule(string error, bool stack)
	{
		var run = Stage(Array(Open));
		var recorder = new Recorder();
		var (code, broke) = ReviewRecord.RecordReview("/w", run, log, Head, Keep, recorder.Call, installed: _ => true,
				dumps: _ => throw (stack ? new InsufficientExecutionStackException(error) : new InvalidOperationException(error)));
		Assert.Equal(ReviewRecord.SessionBrokeARule, code);
		Assert.Empty(recorder.Calls);
		Assert.Equal([$"{PyText.Ascii(Path.Combine(tmp, "run", ReviewRecord.StagedFile))} nests too deep to be written again"], broke);
	}

	[Theory]
	[InlineData(1, "", 0)]
	[InlineData(0, "only-one-line\n", 1)]
	[InlineData(0, "a\nb\nc\n", 3)]
	[InlineData(3, null, 2)]
	public void A_prepare_that_fails_or_doesnt_print_two_lines_records_nothing_and_keeps_the_findings(int code, string? text, int lines)
	{
		var recorder = new Recorder(prepare: (code, text ?? KeyAndHead));
		Is(ReviewRecord.RecordFailed, Recorded(Array(Open), recorder.Call));
		Assert.Single(recorder.Calls);
		Assert.Contains("prepare's stderr\n", Out(), StringComparison.Ordinal);
		FailedAndKept($"scripts/review.sh prepare exited {code} and printed {lines} line(s), not its key and HEAD");
	}

	[Fact]
	public void A_prepare_that_runs_past_its_time_records_nothing()
	{
		Is(ReviewRecord.RecordFailed, Recorded(Array(Open), new Recorder(prepare: (null, "")).Call));
		FailedAndKept("scripts/review.sh prepare ran past 600 s and was stopped");
	}

	[Theory]
	[InlineData(1, "scripts/review.sh record exited 1")]
	[InlineData(null, "scripts/review.sh record ran past 600 s and was stopped")]
	public void A_record_that_refuses_or_runs_past_its_time_is_record_failed_and_keeps_the_findings(int? code, string why)
	{
		Is(ReviewRecord.RecordFailed, Recorded(Array(Open), new Recorder(record: code).Call));
		FailedAndKept(why);
	}

	[Fact]
	public void Findings_that_cant_be_kept_are_said_to_be_gone_and_no_command_is_given()
	{
		Directory.CreateDirectory(Keep);
		Is(ReviewRecord.RecordFailed, Recorded(Array(Open), new Recorder(record: 1).Call));
		Assert.Contains($"rollout.sh: what the session staged couldn't be kept in {Keep} ([Errno 17] File exists", Out(), StringComparison.Ordinal);
		Assert.EndsWith("rollout.sh: scripts/review.sh record exited 1, so the review wasn't recorded\n", Out());
	}

	[Fact]
	public void A_keep_holding_another_reviews_findings_gives_no_command()
	{
		ReviewRecord.WriteStaged(Keep, "[]", new string('a', 40));
		Is(ReviewRecord.RecordFailed, Recorded(Array(Open), new Recorder(record: 1).Call));
		Assert.EndsWith("rollout.sh: scripts/review.sh record exited 1, so the review wasn't recorded\n", Out());
	}

	public static TheoryData<string> Errors() => new(["exec", "group", "bug"]);

	[Theory]
	[MemberData(nameof(Errors))]
	public void Findings_are_kept_whatever_stops_the_record(string what)
	{
		Exception error = what switch
		{
			"exec" => new IOException("Exec format error"),
			"group" => new GroupLeft("left"),
			_ => new ArgumentException("a bug"),
		};
		int? Bounded(IReadOnlyList<string> command, Bounded io)
		{
			if (command[1] != "prepare")
				throw error;
			io.Stdout.Write(Encoding.UTF8.GetBytes(KeyAndHead));
			return 0;
		}
		Assert.Throws(error.GetType(), () => Recorded(Array(Open), Bounded));
		var kept = Kept();
		SameJson(Array(Open), kept.Findings);
		Assert.Equal((Private, PrivateFile, Head), (kept.Folder, kept.File, kept.Head));
	}

	[Fact]
	public void A_group_prepare_left_is_a_failed_record_that_keeps_the_findings()
	{
		const string left = "what review.sh prepare left in its process group was still there";
		// prepare writes only the round's folder: nothing was recorded, and the stop names the group that holds the lock.
		Is(ReviewRecord.RecordFailed, Recorded(Array(Open), (_, _) => throw new GroupLeft(left)), left);
		FailedAndKept(left);
	}

	[Fact]
	public void Without_tsx_nothing_runs_and_the_findings_are_kept()
	{
		var recorder = new Recorder();
		Is(ReviewRecord.RecordFailed, ReviewRecord.RecordReview("/w", Stage(Array(Open)), log, Head, Keep, recorder.Call, installed: _ => false));
		Assert.Empty(recorder.Calls);
		FailedAndKept("/w/node_modules/.bin/tsx isn't installed (pnpm install in the worktree): scripts/review.sh would install it, changing the"
				+ " worktree the round has just checked");
	}

	[Fact]
	public void Tsx_installed_wants_an_executable()
	{
		Assert.False(ReviewRecord.TsxInstalled(tmp));
		var tsx = P("node_modules", ".bin", "tsx");
		Directory.CreateDirectory(Path.GetDirectoryName(tsx)!);
		File.WriteAllText(tsx, "#!/bin/sh\n");
		Assert.False(ReviewRecord.TsxInstalled(tmp));
		File.SetUnixFileMode(tsx, (UnixFileMode)Convert.ToInt32("755", 8));
		Assert.True(ReviewRecord.TsxInstalled(tmp));
	}

	[Fact]
	public void A_signal_before_prepare_runs_nothing_and_keeps_the_findings()
	{
		var recorder = new Recorder();
		Is(ReviewRecord.RecordFailed, ReviewRecord.RecordReview("/w", Stage(Array(Open)), log, Head, Keep, recorder.Call,
				got: [2], installed: _ => true));
		Assert.Empty(recorder.Calls);
		FailedAndKept("a signal came before scripts/review.sh prepare ran");
	}

	// A child that never exits by itself: each wait is recorded, and runs out.
	sealed class FakeChild(int? code = 0, bool timesOut = false, int pid = 999999) : IGroupChild
	{
		public readonly List<double> Waits = [];

		public int Pid => pid;

		public int? Exit(double seconds)
		{
			Waits.Add(seconds);
			return timesOut ? null : code;
		}
	}

	[Fact]
	public void A_bounded_command_stops_at_a_deferred_signal()
	{
		var session = new FakeChild(timesOut: true);
		var ticks = Enumerable.Repeat(0.0, 10).Concat(Enumerable.Repeat(1e9, 100)).GetEnumerator();
		double Clock() => ticks.MoveNext() ? ticks.Current : throw new InvalidOperationException("the clock ran out");
		var code = ReviewRecord.RunBounded(["/w/scripts/review.sh", "prepare"], new Bounded("/w", null, log, null, [2]), (_, _) => session,
				_ => true, timeout: 1000, clock: Clock);
		// It stops at the first poll once a signal is there.
		Assert.Null(code);
		Assert.Single(session.Waits);
	}

	[Fact]
	public void A_signal_during_prepare_stops_it_and_keeps_the_findings()
	{
		int? Bounded(IReadOnlyList<string> command, Bounded io)
		{
			io.Got!.Add(2);
			return null;
		}
		List<int> got = [];
		Is(ReviewRecord.RecordFailed, ReviewRecord.RecordReview("/w", Stage(Array(Open)), log, Head, Keep, Bounded, got: got,
				installed: _ => true));
		FailedAndKept("scripts/review.sh prepare was stopped by a signal");
	}

	[Fact]
	public void A_change_the_unseen_check_finds_before_record_records_nothing()
	{
		var recorder = new Recorder();
		Is(ReviewRecord.SessionBrokeARule, ReviewRecord.RecordReview("/w", Stage(Array(Open)), log, Head, Keep, recorder.Call, installed: _ => true,
				unseen: () => ["node_modules changed"]), "node_modules changed");
		// record never ran.
		Assert.Equal(["prepare"], recorder.Calls.Select(call => call.Command[1]));
		// Findings for code that changed are kept for nothing.
		Assert.False(Path.Exists(Keep));
	}

	public static TheoryData<string, string?> Heads()
	{
		var a40 = new string('a', 40);
		return new()
		{
			{ a40 + "\n", a40 },
			{ string.Concat(Enumerable.Repeat("0123456789abcdef", 4)), string.Concat(Enumerable.Repeat("0123456789abcdef", 4)) },
			{ new string('a', 39), null },
			{ new string('a', 41), null },
			{ new string('a', 63), null },
			{ new string('a', 65), null },
			{ new string('A', 40), null },
			{ "a; rm -rf ~", null },
			{ a40 + "\n" + new string('b', 40), null },
			{ new string(' ', 70) + a40, null },
			{ a40 + new string(' ', 59) + "x", null },
		};
	}

	[Theory]
	[MemberData(nameof(Heads))]
	public void Kept_head_reads_only_an_object_id(string text, string? head)
	{
		Assert.Null(ReviewRecord.KeptHead(P("none")));
		ReviewRecord.WriteStaged(P("k"), "[]", text);
		Assert.Equal(head, ReviewRecord.KeptHead(P("k")));
		Assert.Equal(PrivateFile, File.GetUnixFileMode(P("k", ReviewRecord.KeptHeadFile)));
	}

	[Fact]
	public void A_head_that_moved_since_the_guard_read_it_is_a_broken_rule_and_records_nothing()
	{
		var recorder = new Recorder();
		var (code, broke) = Recorded(Array(Open), recorder.Call, head: new string('a', 40));
		Assert.Equal(ReviewRecord.SessionBrokeARule, code);
		// record never ran.
		Assert.Single(recorder.Calls);
		// Findings for code that moved are kept for nothing.
		Assert.False(Path.Exists(Keep));
		Assert.Equal([$"the worktree's HEAD was '{Head}' by the time the review was recorded, not {new string('a', 40)} as the session reviewed it:"
				+ " something committed there since, so nothing was recorded"], broke);
	}

	const string Stub = """
			#!/bin/sh
			case "$1" in
			prepare) echo "prepare's note" >&2; pwd > "$CALLS/prepare.pwd"; printf '%s\n%s\n' "$KEY" "$HEAD"
			  exit "${PREPARE_EXIT:-0}" ;;
			record)
			  cat > "$CALLS/input.json"
			  printf '%s\n' "$@" > "$CALLS/argv"
			  cp "$4/findings.json" "$CALLS/staged.json"
			  if [ -n "${LEAVE:-}" ]; then sleep 60 & echo $! > "$CALLS/left.pid"; fi
			  echo "Review recorded."
			  echo ".security: R1 is a new entry, 0001" >&2
			  exit "${RECORD_EXIT:-0}" ;;
			esac

			""";

	// A shell run with env over the process's own: its exit code and stderr.
	static (int Code, string Stderr) Shell(string shell, string command, Dictionary<string, string> env, string? cwd = null)
	{
		var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		start.ArgumentList.Add("-c");
		start.ArgumentList.Add(command);
		foreach (var (key, value) in env)
			start.Environment[key] = value;
		if (cwd is not null)
			start.WorkingDirectory = cwd;
		using var run = Process.Start(start)!;
		var output = run.StandardOutput.ReadToEndAsync();
		var err = run.StandardError.ReadToEnd();
		run.WaitForExit();
		_ = output.Result;
		return (run.ExitCode, err);
	}

	static bool OnPath(string name) =>
			(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(folder => Path.IsPathRooted(folder) && File.Exists(Path.Combine(folder, name)));

	[Fact]
	public void The_kept_findings_record_with_the_command_given_only_at_the_head_reviewed()
	{
		var (worktree, calls) = (P("w t"), P("calls"));
		Directory.CreateDirectory(Path.Combine(worktree, "scripts"));
		Directory.CreateDirectory(calls);
		var script = Path.Combine(worktree, "scripts", "review.sh");
		File.WriteAllText(script, Stub);
		File.SetUnixFileMode(script, (UnixFileMode)Convert.ToInt32("755", 8));
		var env = new Dictionary<string, string> { ["KEY"] = new string('k', 64), ["HEAD"] = Head, ["CALLS"] = calls, ["TMPDIR"] = tmp };
		var keep = P("kept it");
		ReviewRecord.WriteStaged(keep, Array(Open).ToJsonString());
		var input = Path.Combine(calls, "input.json");
		var moved = Shell("sh", ReviewRecord.RecordCommand(worktree, keep, new string('a', 40)), env);
		// A HEAD other than the reviewed one records nothing.
		Assert.NotEqual(0, moved.Code);
		Assert.False(File.Exists(input));
		Assert.EndsWith($"the worktree's HEAD is not {new string('a', 40)}, the one the session reviewed: nothing recorded\n", moved.Stderr);
		var failing = Shell("sh", ReviewRecord.RecordCommand(worktree, keep, Head), new(env) { ["PREPARE_EXIT"] = "3" });
		Assert.NotEqual(0, failing.Code);
		Assert.EndsWith("scripts/review.sh prepare failed: nothing recorded\n", failing.Stderr);
		var words = Shell("sh", ReviewRecord.RecordCommand(worktree, keep, Head), new(env) { ["HEAD"] = $"{Head} extra" });
		Assert.NotEqual(0, words.Code);
		Assert.False(File.Exists(input));
		Assert.EndsWith("scripts/review.sh prepare printed 3 word(s), not its key and HEAD: nothing recorded\n", words.Stderr);
		var gone = Shell("sh", ReviewRecord.RecordCommand(P("gone"), keep, Head), env, cwd: worktree);
		// A missing worktree runs nothing where it was pasted.
		Assert.NotEqual(0, gone.Code);
		Assert.False(File.Exists(input));
		var shells = new[] { "bash", "zsh", "fish" }.Where(OnPath).ToList();
		Assert.Contains("bash", shells);
		foreach (var shell in shells)
		{
			File.Delete(input);
			Assert.Equal(0, Shell(shell, ReviewRecord.RecordCommand(worktree, keep, Head), env).Code);
			SameJson(J($$"""{"reviewedKey": "{{new string('k', 64)}}", "reviewedHead": "{{Head}}", "rounds": 1, "reviewerAgent": "pre-push-reviewer"}"""),
					JsonNode.Parse(File.ReadAllText(input)));
		}
		Assert.Equal(["record", "-", "--staged", keep], File.ReadAllText(Path.Combine(calls, "argv")).Split('\n').Take(4));
		// It runs in the worktree.
		Assert.Equal(worktree, File.ReadAllText(Path.Combine(calls, "prepare.pwd")).Trim());
	}

	[Fact]
	public void A_bounded_command_runs_in_its_own_group_holding_the_lock_and_a_group_it_leaves_is_group_left()
	{
		// The group of its own and the lock's descriptor are the spawner's (the cut-over's posix_spawnp): what RunBounded hands it is the
		// command and where it runs.
		List<(IReadOnlyList<string>, Bounded)> seen = [];
		IGroupChild Spawn(IReadOnlyList<string> command, Bounded io)
		{
			seen.Add((command, io));
			return new FakeChild(code: 0);
		}
		var io = new Bounded("/w", log, log, log, null);
		var left = Assert.Throws<GroupLeft>(() => ReviewRecord.RunBounded(["/w/scripts/review.sh", "record"], io, Spawn, _ => false));
		var (command, given) = Assert.Single(seen);
		Assert.Equal(["/w/scripts/review.sh", "record"], command);
		Assert.Same(io, given);
		var pid = new FakeChild().Pid;
		Assert.Equal("what review.sh record left in its process group was still there 10 s after it was killed, and may still write the review"
				+ $" ledgers and .security: see it with pgrep -l -g {pid}, and end it with pkill -KILL -g {pid}, before you resume", left.Message);
		Assert.Equal(0, ReviewRecord.RunBounded(["/w/scripts/review.sh", "prepare"], io, Spawn, _ => true));
	}

	[Fact]
	public void The_staged_file_and_record_form_are_the_ledgers_own()
	{
		var source = File.ReadAllText(Skill.Path("template", "owned", ".sandcastle", "lib", "review-ledger.mts"));
		Assert.Contains($"const STAGED_FILE = \"{ReviewRecord.StagedFile}\";", source, StringComparison.Ordinal);
		// record takes its input, then --staged and the folder, as RecordReview passes them.
		Assert.Contains("const flag = rest.indexOf(\"--staged\");", source, StringComparison.Ordinal);
		Assert.Contains("flag !== 1", source, StringComparison.Ordinal);
		foreach (var field in new[] { "reviewedKey", "reviewedHead", "rounds", "reviewerAgent" })
			Assert.Contains($"{field}:", source, StringComparison.Ordinal);
		// The review is recorded under the agent the session ran: the Template's one agent definition (agent_definition, which ports with
		// the review-session sandbox, reads its name from there).
		var agent = File.ReadAllLines(Skill.Path("template", "owned", ".claude", "agents", "pre-push-reviewer.md"));
		Assert.Equal("---", agent[0]);
		Assert.Equal($"name: {ReviewRecord.ReviewerAgent}", agent[1]);
	}

	// This part's lines of test_the_sandbox_and_session_lists_word_for_word; the other parts' lists join them as they are ported.
	[Fact]
	public void The_sandbox_and_session_lists_word_for_word()
	{
		Assert.Equal(16 * 1024 * 1024, SessionGuard.RecordLimit);
		Assert.Equal(["open", "dismissed"], ReviewRecord.StagedStatuses);
		Assert.Equal((256, 257, 600, 120, 3, "HEAD"), (ReviewRecord.RecordFailed, ReviewRecord.NothingStaged, ReviewRecord.RecordTimeoutSeconds,
				SessionGuard.ShownChars, SessionGuard.UnseenShown, ReviewRecord.KeptHeadFile));
		// Above every exit code, so a session's can't read as one.
		Assert.True(Math.Min(ReviewRecord.RecordFailed, ReviewRecord.NothingStaged) > 255);
		Assert.Equal((double)ReviewRecord.RecordTimeoutSeconds,
				typeof(ReviewRecord).GetMethod(nameof(ReviewRecord.RunBounded))!.GetParameters().Single(p => p.Name == "timeout").DefaultValue);
		Assert.Equal((10, 126), (ReviewRecord.GroupWaitSeconds, ReviewRecord.SessionBrokeARule));
	}
}
