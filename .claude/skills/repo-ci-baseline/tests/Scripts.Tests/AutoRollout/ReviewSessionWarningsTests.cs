using System.Text.Json.Nodes;

// Tests for github/AutoRolloutReview.cs's warnings after a session (Appeared, AddedSecrets, AfterSession), its log folder and settings
// file (SettingsPath, WriteSettings) and the ways a session fails to start, and for github/AutoRollout.cs's AgentDefinition: xUnit copies
// of github/tests/test_auto_rollout.py's tests of them (ADR 0017's Update for mpaulosky/Application_Factory#122), one method each, with
// the same cases and assertions. A Python exception's name in a message is .NET's here (KeyError is KeyNotFoundException, RuntimeError
// InvalidOperationException), and a traceback is .NET's stack trace.
[Collection(nameof(RoundCollection))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ReviewSessionWarningsTests : IDisposable
{
	readonly string tmp;

	// Skips off Linux before anything is made, as SessionGuardTests does.
	public ReviewSessionWarningsTests()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		tmp = Directory.CreateTempSubdirectory("auto-rollout.").FullName;
	}

	public void Dispose() => Directory.Delete(tmp, recursive: true);

	string P(params string[] parts) => Path.Combine([tmp, .. parts]);

	static readonly string[] Held = ["/dev/shm"];

	static JsonNode HeldSettings() => new JsonObject
	{
		["sandbox"] = new JsonObject { ["filesystem"] = new JsonObject { ["denyRead"] = new JsonArray([.. Held.Select(path => (JsonNode?)path)]) } },
	};

	// The first part of tmp's real path: the entry of / the session's folders are under.
	string RootOf() => PyLib.RealPath(tmp).Split('/')[1];

	// A listdir that lists / as root alone, then with new-project beside it, and every other folder as empty.
	Func<string, IEnumerable<string>> NewProjectListing()
	{
		var calls = 0;
		return folder =>
		{
			if (folder != "/")
				return [];
			calls++;
			return [RootOf(), .. calls > 1 ? new[] { "new-project" } : []];
		};
	}

	// A listdir like {"/": names}.get: any other folder listed is a bug of the test, as None would be.
	static Func<string, IEnumerable<string>> Only(params string[] names) => folder => new Dictionary<string, string[]> { ["/"] = names }[folder];

	[Fact]
	public void A_clone_with_a_sign_in_secret_isnt_reviewed()
	{
		List<(string, string)> asked = [];
		var (code, log, seen, worktree) = ReviewFixtures.ReviewOnce(tmp, secrets: (w, c) =>
		{
			asked.Add((w, c));
			return ["remote.origin.url"];
		});
		Assert.True(code == 127 && seen.Command is null);
		Assert.True(asked.Count == 1 && asked[0].Item1 == worktree, "asked once, before the session");
		var text = File.ReadAllText(log);
		Assert.Contains("rollout.sh: not started: the clone's git folder holds a sign-in secret (remote.origin.url)", text, StringComparison.Ordinal);
		Assert.Contains("A key is named '<key> in <file>', the file that holds it: git config --file <file> --list shows it whole"
				+ " (a *** in one stands for its sign-in), and git config --file <file> --unset-all <key> removes it; a file"
				+ " named for credentials is named by its path: remove it. Keep the token in a credential helper, then"
				+ " resume", text, StringComparison.Ordinal);
	}

	[Fact]
	public void A_secret_added_to_the_clones_git_folder_while_the_session_ran_is_named()
	{
		List<string> calls = [];
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, secrets: (worktree, _) =>
		{
			calls.Add(worktree);
			return calls.Count > 1 ? ["remote.origin.url"] : [];
		});
		Assert.True(code == 0 && calls.Count == 2, "asked before the session and after it");
		const string Said = "rollout.sh: added to the clone's git folder while the session ran, so it could read them: remote.origin.url";
		Assert.Contains(Said, File.ReadAllText(log), StringComparison.Ordinal);
		Assert.Contains(Said, seen.Err, StringComparison.Ordinal);
		Assert.Equal(["added to the clone's git folder while the session ran, so it could read them: b"], ReviewSession.AddedSecrets(["a"], ["a", "b"]));
		Assert.Empty(ReviewSession.AddedSecrets(["a"], ["a"]));
	}

	[Fact]
	public void What_appears_beside_the_sessions_folders_while_it_runs_is_named()
	{
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, listdir: NewProjectListing());
		Assert.True(code == 0, "a warning, not a broken rule");
		const string Said = "rollout.sh: made while the session ran, after its sandbox was set, so it could read them (check them): /new-project";
		Assert.Contains(Said, File.ReadAllText(log), StringComparison.Ordinal);
		Assert.Contains(Said, seen.Err, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(20, "")]
	[InlineData(21, " (and 1 more)")]
	[InlineData(25, " (and 5 more)")]
	public void What_appeared_is_named_twenty_at_most(int count, string shown)
	{
		var names = Enumerable.Range(0, count).Select(index => $"n{index:D2}").ToArray();
		var said = ReviewSession.Appeared(HeldSettings(), (["/h/wt"], [], "/h"), Only(names), _ => null);
		Assert.Equal(["made while the session ran, after its sandbox was set, so it could read them (check them): "
				+ string.Join(", ", names.Take(20).Select(name => $"/{name}")) + shown], said);
	}

	[Fact]
	public void A_name_that_cant_be_denied_makes_what_appeared_untold()
	{
		var said = ReviewSession.Appeared(HeldSettings(), (["/h/wt"], [], "/h"), Only("a*b"), _ => null);
		Assert.StartsWith("what was made beside the session's folders while it ran can't be told ('/a*b' must be", said[0], StringComparison.Ordinal);
		Assert.EndsWith("rename or move it)", said[0], StringComparison.Ordinal); // no 'then resume': nothing stopped
	}

	// A TextWriter that is closed: each write throws, as Python's closed sys.stderr raises ValueError.
	sealed class Closed : TextWriter
	{
		public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

		public override void Write(char value) => throw new ObjectDisposedException("stderr", "I/O operation on closed file");

		public override void Write(string? value) => throw new ObjectDisposedException("stderr", "I/O operation on closed file");
	}

	[Fact]
	public void A_failing_warning_or_a_closed_stderr_loses_no_review()
	{
		static List<string> Broken() => throw new KeyNotFoundException("x");

		string Read(FileStream log)
		{
			log.Flush();
			return File.ReadAllText(log.Name);
		}

		var err = new StringWriter();
		using (var out_ = new FileStream(P("one.log"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
		{
			ReviewSession.AfterSession(out_, "the thing", Broken, err);
			// The stack trace in the log: .NET's, where the pytest's starts "Traceback (most recent call last):".
			Assert.StartsWith("System.Collections.Generic.KeyNotFoundException: x", Read(out_), StringComparison.Ordinal);
			Assert.EndsWith("rollout.sh: checking the thing failed (KeyNotFoundException: x); check it yourself\n", Read(out_), StringComparison.Ordinal);
		}

		using (var out_ = new FileStream(P("two.log"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
		{
			ReviewSession.AfterSession(out_, "the thing", () => ["one", "two"], new Closed());
			Assert.Equal("rollout.sh: one\nrollout.sh: two\n", Read(out_));
		}

		// Python's Full log raises OSError(28) on each write; /dev/full does the same for real (unbuffered, so nothing is left to flush).
		err = new StringWriter();
		using (var full = new FileStream("/dev/full", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0))
		{
			ReviewSession.AfterSession(full, "the thing", Broken, err);
			ReviewSession.AfterSession(full, "the thing", () => ["one"], err);
		}
		Assert.EndsWith("rollout.sh: one\n", err.ToString(), StringComparison.Ordinal); // a log that can't be written loses no review
		Assert.Equal("rename it", ReviewSession.WithoutResume("rename it, then resume; more"));
	}

	[Fact]
	public void A_failing_check_after_the_session_neither_stops_the_record_nor_hides_the_other()
	{
		List<string> asked = [];
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, listdir: NewProjectListing(), secrets: (worktree, _) =>
		{
			asked.Add(worktree);
			if (asked.Count > 1)
				throw new InvalidOperationException("git can't read /c/config (bad), then resume");
			return [];
		});
		Assert.True(code == 0 && seen.Recorded is not null, "recorded all the same");
		var text = File.ReadAllText(log);
		// the other check's warning is kept
		Assert.Contains("rollout.sh: made while the session ran, after its sandbox was set, so it could read them (check them): /new-project", text,
				StringComparison.Ordinal);
		Assert.Contains("rollout.sh: checking the clone's git folder for sign-in secrets added failed (InvalidOperationException: git can't"
				+ " read /c/config (bad)); check it yourself", text, StringComparison.Ordinal);
	}

	[Fact]
	public void A_warning_that_raises_past_its_guard_still_lets_the_review_be_recorded()
	{
		// The pytest replaces appeared itself, which the C# has no seam for: here the second listing of / throws what Appeared doesn't catch,
		// so the exception leaves Appeared just as the replaced one's did.
		var lists = 0;
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, listdir: folder =>
		{
			if (folder == "/" && ++lists > 1)
				throw new KeyNotFoundException("x");
			return folder == "/" ? [RootOf()] : [];
		});
		Assert.True(code == 0 && seen.Recorded is not null);
		Assert.Contains("rollout.sh: checking what was made beside the session's folders failed (KeyNotFoundException: x)", File.ReadAllText(log),
				StringComparison.Ordinal);
	}

	[Fact]
	public void What_appeared_during_the_session_that_cant_be_told_is_said()
	{
		List<string> calls = [];
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp, listdir: folder =>
		{
			calls.Add(folder);
			if (folder == "/" && calls.Count(called => called == "/") > 1)
				throw new IOException("[Errno 13] Permission denied");
			return folder == "/" ? [RootOf()] : [];
		});
		Assert.Equal(0, code);
		const string Said = "rollout.sh: what was made beside the session's folders while it ran can't be told (can't list /";
		Assert.Contains(Said, File.ReadAllText(log), StringComparison.Ordinal);
		Assert.Contains(Said, seen.Err, StringComparison.Ordinal);
	}

	[Fact]
	public void Nothing_new_beside_the_sessions_folders_says_nothing()
	{
		var (code, log, _, _) = ReviewFixtures.ReviewOnce(tmp);
		Assert.True(code == 0 && !File.ReadAllText(log).Contains("made while the session ran", StringComparison.Ordinal));
	}

	[Fact]
	public void The_agent_comes_from_the_templates_file()
	{
		// The Template's own file, as Folders.Default names it when rollout.cs runs (the test host runs no script file).
		var folders = new Folders(Skill.Dir);
		var agents = AutoRollout.AgentDefinition(folders.AgentFile);
		Assert.Equal(["pre-push-reviewer"], agents.Select(pair => pair.Key));
		var agent = agents["pre-push-reviewer"]!;
		Assert.True(JsonNode.DeepEquals(new JsonArray("Read", "Grep", "Glob"), agent["tools"]) && (string?)agent["model"] == "sonnet");
		var description = (string)agent["description"]!;
		Assert.True(description.StartsWith("A read-only reviewer for the pre-push-review skill", StringComparison.Ordinal) && !description.Contains('\n'));
		Assert.StartsWith("# Pre-push reviewer", (string)agent["prompt"]!, StringComparison.Ordinal);
		Assert.Equal(Path.Combine(folders.Owned, ".claude", "agents", "pre-push-reviewer.md"), folders.AgentFile);
	}

	[Theory]
	[InlineData("# no front matter\n", "has no front matter")]
	[InlineData("---\nname: x\ndescription: y\n---\nbody\n", "has no tools")]
	[InlineData("---\nname: x\ntools: Read\n---\nbody\n", "has no description")]
	[InlineData("---\ndescription: y\ntools: Read\n---\nbody\n", "has no name")]
	[InlineData("---\nname: x\ndescription: y\ntools: Read\nbody without a close\n", "front matter is never closed")]
	[InlineData("----\nname: x\n---\nbody\n", "has no front matter")]
	public void An_agent_file_that_doesnt_read_is_refused(string text, string problem)
	{
		File.WriteAllText(P("a.md"), text);
		// ValueError is InvalidDataException.
		var error = Assert.Throws<InvalidDataException>(() => AutoRollout.AgentDefinition(P("a.md")));
		Assert.Contains(problem, error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(">-")]
	[InlineData(">")]
	[InlineData("|")]
	public void An_agent_files_folded_fields_and_body_are_read_whole(string marker)
	{
		File.WriteAllText(P("a.md"), $"---\nname: x\ndescription: {marker}\n  one\n\ttwo\ntools: Read, Grep ,\n---\n\n- a list first\n---\nmore\n");
		var agent = AutoRollout.AgentDefinition(P("a.md"))["x"];
		var expected = JsonNode.Parse("""{"description": "one two", "prompt": "- a list first\n---\nmore\n", "tools": ["Read", "Grep"]}""");
		// no model: none is passed; a body that opens with a dash or holds a rule is kept whole
		Assert.True(JsonNode.DeepEquals(expected, agent), agent?.ToJsonString());
	}

	[Fact]
	public void The_review_log_is_the_owners_alone()
	{
		var (_, log, _, _) = ReviewFixtures.ReviewOnce(tmp);
		Assert.True(log.StartsWith(P("logs"), StringComparison.Ordinal) && Path.GetFileName(log).StartsWith("review-TicketManager.", StringComparison.Ordinal)
				&& log.EndsWith(".log", StringComparison.Ordinal));
		Assert.Equal(0, (int)File.GetUnixFileMode(log) & 0x3F);
		Assert.Equal((UnixFileMode)0x1C0, File.GetUnixFileMode(P("logs")));
		Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".cache", "repo-ci-baseline", "logs"), AutoRollout.LogDir);
	}

	[Fact]
	public void An_existing_log_folder_is_set_to_0700_and_a_symlink_is_refused()
	{
		Directory.CreateDirectory(P("logs"), (UnixFileMode)0x1ED);
		File.SetUnixFileMode(P("logs"), (UnixFileMode)0x1ED);
		ReviewFixtures.ReviewOnce(tmp);
		Assert.Equal((UnixFileMode)0x1C0, File.GetUnixFileMode(P("logs")));
		Directory.CreateDirectory(P("elsewhere"));
		var link = P("linked");
		File.CreateSymbolicLink(link, P("elsewhere"));
		var (code, log, _) = ReviewSession.RunReview(Fleets.TM, tmp, logDir: link, sessionDir: P("sessions"), listdir: _ => [], which: _ => null,
				secrets: (_, _) => [], err: new StringWriter());
		Assert.True(code == 127 && log.Contains("isn't a folder of the user's own", StringComparison.Ordinal), log);
		// The pytest makes getuid another user's for run_review, which has no uid seam: RunReview's log is OwnLogDir's error, so it is asked
		// with the other uid itself.
		var error = Assert.Throws<IOException>(() => AutoRollout.OwnLogDir(P("logs"), PyLib.Owner(P("logs"))!.Value + 1));
		Assert.Contains("isn't a folder of the user's own", error.Message, StringComparison.Ordinal); // another user's folder is refused too
	}

	[Fact]
	public void The_sessions_settings_are_a_private_file_beside_its_log_never_its_command_line()
	{
		var (code, log, seen, _) = ReviewFixtures.ReviewOnce(tmp);
		Assert.Equal(0, code);
		var (path, mode, regular) = seen.SettingsFile!.Value;
		Assert.True(path == log[..^".log".Length] + ".settings.json" && Path.GetDirectoryName(path) == P("logs"));
		// a file of the person's own, in LogDir, which the session can't read
		Assert.Equal(((UnixFileMode)0x180, true), (mode, regular));
		// no user reads them in cmdline
		Assert.DoesNotContain(seen.Command!, arg => arg.Contains("denyRead", StringComparison.Ordinal) || arg.Contains("allowedDomains", StringComparison.Ordinal));
		Assert.True((bool?)seen.Settings!["sandbox"]!["enabled"] == true);
		Assert.False(AutoRollout.LExists(path), "removed once the session ends");
	}

	[Theory]
	[InlineData("file")]
	[InlineData("link")]
	public void A_settings_file_already_there_stops_the_session_before_it_starts(string planted)
	{
		var target = P("elsewhere.json");

		// mkstemp, then a file or a link planted where the settings go, as the pytest's patched mkstemp does.
		(FileStream, string) MakeTemp(string folder, string prefix, string suffix)
		{
			var (handle, log) = Files.MakeTemp(folder, prefix, suffix);
			var settings = log[..^".log".Length] + ".settings.json";
			if (planted == "file")
				File.WriteAllText(settings, "{}");
			else
				File.CreateSymbolicLink(settings, target);
			return (handle, log);
		}

		List<IReadOnlyList<string>> started = [];
		var (code, log, _, worktree) = ReviewFixtures.ReviewOnce(tmp, popen: (command, _) =>
		{
			started.Add(command);
			return new FakeSession();
		}, makeTemp: MakeTemp);
		Assert.True(code == 127 && started.Count == 0 && !File.Exists(target));
		var settings = log[..^".log".Length] + ".settings.json";
		Assert.True(AutoRollout.LExists(settings) && (new FileInfo(settings).LinkTarget is not null) == (planted == "link"), "what was there is left");
		// As drive shows it: the resume it adds itself is cut, and what is left reads whole.
		Assert.Equal($"couldn't be started: can't write the session's settings file: [Errno 17] File exists: '{settings}' (its"
				+ $" folder, {P("logs")}, must be writable with room in it, and a file or link left at that path removed;"
				+ $" or run /pre-push-review in {worktree})", ReviewSession.StopLine(log));
	}

	[Theory]
	[InlineData("skill", "x.md: front matter is never closed")]
	[InlineData("agents", "[Errno 2] No such file")]
	[InlineData("agents", "has no tools")]
	public void A_skill_or_agent_that_doesnt_read_starts_no_session(string which, string message)
	{
		// ValueError is InvalidDataException, and OSError(2, "No such file") an IOException worded as Python words it.
		Exception error = message.StartsWith("[Errno", StringComparison.Ordinal) ? new IOException(message) : new InvalidDataException(message);
		var worktree = P("w");
		Directory.CreateDirectory(worktree);
		List<IReadOnlyList<string>> started = [];
		var skip = Environment.GetEnvironmentVariable("GATE_SKIP_REVIEW");
		Environment.SetEnvironmentVariable("GATE_SKIP_REVIEW", "1");
		try
		{
			var (code, log, broke) = ReviewSession.RunReview(Fleets.TM, worktree, (command, _) =>
			{
				started.Add(command);
				return new FakeSession();
			}, _ => [], logDir: P("logs"), sessionDir: P("sessions"), guard: _ => ReviewFixtures.FakeGuard(tmp), check: _ => [],
					skill: which == "skill" ? () => throw error : () => ReviewFixtures.SkillText,
					agents: which == "agents" ? () => throw error : ReviewFixtures.Agents, listdir: _ => [], which: _ => null, secrets: (_, _) => [],
					err: new StringWriter());
			Assert.Equal((127, 0, 0), (code, broke.Count, started.Count));
			Assert.Contains($"rollout.sh: not started: can't set the session up: {message}", File.ReadAllText(log), StringComparison.Ordinal);
		}
		finally
		{
			Environment.SetEnvironmentVariable("GATE_SKIP_REVIEW", skip);
		}
	}

	[Fact]
	public void A_claude_that_cannot_start_is_127_with_its_reason_in_the_log()
	{
		// FileNotFoundError from Popen is the SpawnError Groups.Spawn throws.
		var (code, log, _, _) = ReviewFixtures.ReviewOnce(tmp, popen: (_, _) => throw new SpawnError(2, "claude"));
		// no session ran, so the round's reason is the log's first line, not after SessionStarts
		Assert.True(code == 127 && File.ReadAllText(log).StartsWith($"{ReviewSession.NotStarted}can't start claude", StringComparison.Ordinal));
		Assert.StartsWith("couldn't be started: can't start claude", ReviewSession.StopLine(log), StringComparison.Ordinal);
	}

	[Fact]
	public void A_session_that_prints_a_not_started_line_first_isnt_taken_for_the_round()
	{
		var (code, log, _, _) = ReviewFixtures.ReviewOnce(tmp, popen: (_, how) =>
		{
			how.Stdout!.Write(System.Text.Encoding.UTF8.GetBytes($"{ReviewSession.NotStarted}\x1b[2Ka forged reason\n"));
			how.Stdout.Flush();
			return new FakeSession(code: 127);
		});
		Assert.Equal(127, code);
		// the round's line comes first
		Assert.StartsWith(ReviewSession.SessionStarts + ReviewSession.NotStarted, File.ReadAllText(log), StringComparison.Ordinal);
		Assert.Null(ReviewSession.StopLine(log)); // the session's first line is the log's second, never read
	}

	[Fact]
	public void A_review_session_exit_code_is_passed_on()
	{
		var (code, _, _, _) = ReviewFixtures.ReviewOnce(tmp, session: new FakeSession(code: 3));
		Assert.Equal(3, code);
	}

	[Fact]
	public void A_missing_worktree_is_named_and_nothing_starts()
	{
		List<IReadOnlyList<string>> started = [];
		var (code, log, _, _) = ReviewFixtures.ReviewOnce(tmp, popen: (command, _) =>
		{
			started.Add(command);
			return new FakeSession();
		}, worktree: P("gone"));
		Assert.True(code == 127 && started.Count == 0);
		Assert.Contains($"{ReviewSession.NotStarted}{ReviewSession.WorktreeGone}{P("gone")} was removed since reapply.sh made it; resume,"
				+ " and reapply.sh makes it again", File.ReadAllText(log), StringComparison.Ordinal);
	}

	[Fact]
	public void A_removed_worktree_isnt_where_the_stop_sends_the_person()
	{
		var log = P("review.log");
		File.WriteAllText(log, $"{ReviewSession.NotStarted}{ReviewSession.WorktreeGone}/r/w was removed since reapply.sh made it; resume, and"
				+ " reapply.sh makes it again\n");
		var outcome = AutoRound.Drive(Fleets.TM, "/r/TicketManager", "#82", (_, _) => AutoRound.ReviewNeeded, (_, _) => (127, log, []), _ => { });
		Assert.Equal(new Outcome("failed", $"the review session couldn't be started: {ReviewSession.WorktreeGone}/r/w was"
				+ " removed since reapply.sh made it; resume, and reapply.sh makes it again (see"
				+ $" {log})."), outcome);
		File.WriteAllText(log, $"{ReviewSession.NotStarted}can't set the session up: no room\n");
		var other = AutoRound.Drive(Fleets.TM, "/r/TicketManager", "#82", (_, _) => AutoRound.ReviewNeeded, (_, _) => (127, log, []), _ => { });
		Assert.Equal($"the review session couldn't be started: can't set the session up: no room (see {log});"
				+ " run /pre-push-review in /r/TicketManager-worktrees/chore-reapply-baseline, then resume.", other.Detail);
	}

	[Theory]
	[InlineData("changes")]
	[InlineData("guard")]
	public void A_session_that_cant_be_set_up_never_starts(string where)
	{
		List<IReadOnlyList<string>> started = [];
		Spawner popen = (command, _) =>
		{
			started.Add(command);
			return new FakeSession();
		};
		// RuntimeError is InvalidOperationException.
		var (code, log, _, _) = where == "guard"
				? ReviewFixtures.ReviewOnce(tmp, popen: popen, guard: _ => throw new GuardUnreadable("can't read the remote"))
				: ReviewFixtures.ReviewOnce(tmp, popen: popen, changes: _ => throw new InvalidOperationException("git diff failed"));
		Assert.True(code == 127 && started.Count == 0);
		Assert.Contains(where == "guard" ? "rollout.sh: not started: can't read the remote" : "git diff failed", File.ReadAllText(log), StringComparison.Ordinal);
	}

	[Fact]
	public void A_log_that_cannot_be_made_is_127()
	{
		var (code, log, _) = ReviewSession.RunReview(Fleets.TM, tmp, logDir: P("logs"), sessionDir: P("sessions"), err: new StringWriter(),
				makeTemp: (_, _, _) => throw new IOException("[Errno 28] No space left on device"));
		Assert.True(code == 127 && log.Contains("No space left on device", StringComparison.Ordinal), log);
	}
}
