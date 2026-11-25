using System.Diagnostics;

// Tests for what the round reads and how it ends (github/AutoRolloutRound.cs's GhPr, GhPrState and GitBranch, AutoRollout.OwnedChanges,
// Folders.FromEntry, and AutoRound.Run's stops, interrupts and lock): xUnit copies of github/tests/test_auto_rollout.py's tests of git_branch,
// gh_pr, PR_JQ, gh_pr_state, owned_changes' names, the PR lookups' waits and retries, an interrupted round and the lock passed on (ADR 0017's
// Update on replacing a pytest suite, for mpaulosky/Application_Factory#122): one method each, the same cases and assertions. Where the
// pytest monkeypatched subprocess.run, these pass a Runner, the seam GhPr, GhPrState and GitBranch take.
[Collection(nameof(RoundCollection))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RoundReadsTests : IDisposable
{
	readonly string tmp;

	// Skips off Linux before anything is made, as SessionGuardTests does.
	public RoundReadsTests()
	{
		Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");
		tmp = AutoRollout.RealPath(Directory.CreateTempSubdirectory("round-reads.").FullName);
	}

	// rm, since .NET can't name the file Owned_changes_reads_a_name_that_isnt_utf_8 makes (its name isn't UTF-8).
	public void Dispose() => SessionGuardTests.Run("rm", ["-rf", "--", tmp]);

	static readonly string TM = Fleets.TM;
	static readonly IReadOnlyList<string> Rest = Fleets.Rest;

	// git with no global or system config, and a committer, as the pytest's git() runs it.
	static readonly Dictionary<string, string> GitEnv = new()
	{
		["GIT_CONFIG_GLOBAL"] = "/dev/null",
		["GIT_CONFIG_NOSYSTEM"] = "1",
		["GIT_AUTHOR_NAME"] = "t",
		["GIT_AUTHOR_EMAIL"] = "t@t",
		["GIT_COMMITTER_NAME"] = "t",
		["GIT_COMMITTER_EMAIL"] = "t@t",
	};

	string Git(params string[] args) => SessionGuardTests.Run("git", ["-C", tmp, .. args], GitEnv);

	// A Runner that answers each call with the next of answers, and records the commands.
	static Runner Answering(List<IReadOnlyList<string>> seen, params Ran[] answers)
	{
		var next = 0;
		return (command, _, _) =>
		{
			seen.Add(command);
			return answers[next++];
		};
	}

	// rollout.sh --auto --brings '#82' on fleet, with reapply in place of the fleet's (the pytest's fleet.reapply = ...), as Fleet.RunAuto runs it.
	static (int Code, string Out, string Err) RunAuto(Fleet fleet, Func<string, string, int?> reapply)
	{
		var (output, err) = (new StringWriter(), new StringWriter());
		var code = Rollout.Run(["--auto", "--root", "/r", "--brings", "#82"], output, err, listed: Fleets.Listed, reapply: reapply, exists: _ => true,
				auto: fleet.World() with { Reapply = reapply }, advisories: _ => null, onSignal: (_, _) => { }, defaultRoot: "/r");
		return (code, output.ToString(), err.ToString());
	}

	[Fact]
	public void Git_branch_says_whether_the_branch_has_commits_main_lacks()
	{
		// The pytest set LC_ALL and GIT_CEILING_DIRECTORIES in the process; here they go to git through the Runner, so the process's own stay.
		var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
				.ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "", StringComparer.Ordinal);
		env["LC_ALL"] = "C";
		env["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(tmp)!;
		Ran Run(IReadOnlyList<string> command, string? cwd, IReadOnlyDictionary<string, string>? _) => AutoRollout.RunQuiet(command, cwd, env);

		var error = Assert.Throws<InvalidOperationException>(() => AutoRound.GitBranch(tmp, Run));
		Assert.Matches("git rev-parse HEAD in .* exited 128: fatal: not a git repository", error.Message);
		Git("init", "-q", "-b", "main");
		Git("commit", "-q", "--allow-empty", "-m", "one");
		Git("update-ref", "refs/remotes/origin/main", "HEAD");
		Assert.Equal((Git("rev-parse", "HEAD"), false), AutoRound.GitBranch(tmp, Run));
		Git("commit", "-q", "--allow-empty", "-m", "two");
		Assert.Equal((Git("rev-parse", "HEAD"), true), AutoRound.GitBranch(tmp, Run));
		Git("update-ref", "-d", "refs/remotes/origin/main");
		error = Assert.Throws<InvalidOperationException>(() => AutoRound.GitBranch(tmp, Run));
		Assert.Matches("git merge-base in .* exited 128: fatal: Not a valid object name origin/main", error.Message);
	}

	[Fact]
	public void Gh_pr_asks_for_every_state_and_the_repos_own_prs()
	{
		List<IReadOnlyList<string>> seen = [];
		var run = Answering(seen, new Ran(0, "130 OPEN abc123\n", ""), new Ran(0, "", ""), new Ran(1, "", "HTTP 502"));
		Assert.Equal((130L, "OPEN", "abc123"), AutoRound.GhPr(TM, run));
		Assert.Equal(["gh", "pr", "list", "-R", TM, "--head", AutoRollout.Branch, "--state", "all", "--limit", "100",
			"--json", "number,state,headRefOid,isCrossRepository", "--jq", AutoRound.PrJq], seen[0]);
		// The pytest read timeout=120 off the call; a Runner takes none, so this checks the limit of RunQuiet, the default Runner.
		Assert.Equal(TimeSpan.FromSeconds(120), AutoRollout.QuietTimeout);
		Assert.Null(AutoRound.GhPr(TM, run));
		var error = Assert.Throws<InvalidOperationException>(() => AutoRound.GhPr(TM, run));
		Assert.Contains("HTTP 502", error.Message, StringComparison.Ordinal);
	}

	public static TheoryData<string, string> PrLists => new()
	{
		{ "[]", "" },
		{ """[{"number": 130, "state": "OPEN", "headRefOid": "abc", "isCrossRepository": false}]""", "130 OPEN abc" },
		{
			"""[{"number": 140, "state": "OPEN", "headRefOid": "f0f", "isCrossRepository": true},"""
			+ """ {"number": 130, "state": "MERGED", "headRefOid": "abc", "isCrossRepository": false}]""",
			"130 MERGED abc"
		},
		{ """[{"number": 140, "state": "OPEN", "headRefOid": "f0f", "isCrossRepository": true}]""", "" },
	};

	[Theory]
	[MemberData(nameof(PrLists))]
	public void The_pr_jq_takes_the_newest_pr_from_the_repo_itself(string prs, string line)
	{
		Assert.SkipUnless(Gh.Which("jq", Environment.GetEnvironmentVariable("PATH")) is not null, "jq isn't installed");
		var start = new ProcessStartInfo("jq") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
		start.ArgumentList.Add("-r");
		start.ArgumentList.Add(AutoRound.PrJq);
		using var done = Process.Start(start)!;
		done.StandardInput.Write(prs);
		done.StandardInput.Close();
		var output = done.StandardOutput.ReadToEnd();
		done.WaitForExit();
		Assert.Equal(0, done.ExitCode);
		Assert.Equal(line, output.Trim());
	}

	[Fact]
	public void Gh_pr_state_reads_one_pr_by_number()
	{
		List<IReadOnlyList<string>> seen = [];
		var run = Answering(seen, new Ran(0, "MERGED\n", ""), new Ran(1, "", "HTTP 404"), new Ran(0, "\n", ""));
		Assert.Equal("MERGED", AutoRound.GhPrState(TM, 130, run));
		Assert.Equal(["gh", "pr", "view", "130", "-R", TM, "--json", "state", "--jq", ".state"], seen[0]);
		// timeout=120, as in Gh_pr_asks_for_every_state_and_the_repos_own_prs: RunQuiet's limit.
		Assert.Equal(TimeSpan.FromSeconds(120), AutoRollout.QuietTimeout);
		var error = Assert.Throws<InvalidOperationException>(() => AutoRound.GhPrState(TM, 130, run));
		Assert.Contains("HTTP 404", error.Message, StringComparison.Ordinal);
		error = Assert.Throws<InvalidOperationException>(() => AutoRound.GhPrState(TM, 130, run));
		Assert.Contains("gh pr view exited 0", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Owned_changes_reads_a_name_that_isnt_utf_8()
	{
		Git("init", "-q", "-b", "main");
		Git("commit", "-q", "--allow-empty", "-m", "one");
		Git("update-ref", "refs/remotes/origin/main", "HEAD");
		Directory.CreateDirectory(Path.Combine(tmp, "scripts"));
		File.WriteAllText(Path.Combine(tmp, "scripts", "gate.sh"), "x");
		// .NET's file names are UTF-8, so sh makes the one with the byte 0xff.
		SessionGuardTests.Run("sh", ["-c", ": > \"$(printf 'bad\\377name')\""], cwd: tmp);
		Git("add", "-A");
		Git("commit", "-q", "-m", "two");
		var owned = Path.Combine(tmp, "owned", "scripts");
		Directory.CreateDirectory(owned);
		File.WriteAllText(Path.Combine(owned, "gate.sh"), "");
		var retired = AutoRollout.RetiredPaths(new Folders(Skill.Dir).Retired); // the pytest's default RETIRED
		Assert.Equal(["scripts/gate.sh"], AutoRollout.OwnedChanges(tmp, owned: Path.Combine(tmp, "owned"), retired: retired));
	}

	public static TheoryData<string, int, bool> Lookups => new()
	{
		{ Fleets.TM, AutoRound.LookupTries - 1, true },
		{ Fleets.TM, AutoRound.LookupTries, false },
		{ Fleets.Rest[0], AutoRound.LookupTries - 1, true },
		{ Fleets.Rest[0], AutoRound.LookupTries, false },
	};

	[Theory]
	[MemberData(nameof(Lookups))]
	public void A_pushed_pr_the_list_doesnt_show_yet_is_waited_for(string repo, int reads, bool ok)
	{
		using var fleet = new Fleet { Opens = { [repo] = 300 }, Unlisted = { [repo] = reads } };
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True((code == 0) == ok, output);
		if (ok)
			Assert.Contains(fleet.Landed, refs => refs.Any(reference => reference == $"{repo}#300"));
		else
		{
			Assert.Contains($"{Fleets.Short(repo)}: can't tell its re-Apply PR (no re-Apply PR shows head {Fleets.HeadOf(repo)[..7]})", output,
					StringComparison.Ordinal);
			Assert.DoesNotContain(fleet.Landed, refs => refs.Any(reference => reference == $"{repo}#300")); // never counted as nothing to land
		}
	}

	[Fact]
	public void An_open_pr_at_the_worktrees_own_head_with_nothing_ahead_is_landed()
	{
		// A resume: the push went out in an earlier run, so the branch is level with what the PR shows.
		using var fleet = new Fleet();
		fleet.Prs[TM] = (131, "OPEN", Fleets.HeadOf(TM));
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 0, output);
		Assert.Equal([$"{TM}#131"], fleet.Landed[0]);
	}

	[Theory]
	[MemberData(nameof(Lookups))]
	public void A_landed_pr_read_that_fails_is_tried_again(string repo, int fails, bool ok)
	{
		using var fleet = new Fleet { Opens = { [repo] = 300 }, StateFailures = { [repo] = fails } };
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True((code == 0) == ok, output);
		if (!ok)
			Assert.Contains("(gh failed: HTTP 503)", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_land_sh_that_exits_non_zero_for_the_rest_says_so()
	{
		using var fleet = new Fleet { Opens = { [Rest[0]] = 200 }, LandCode = 1 };
		var (_, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Contains("rollout.sh: land.sh exited 1 (see its output above)", output, StringComparison.Ordinal);
	}

	[Fact]
	public void An_interrupted_round_prints_the_resume_command_and_releases_the_lock()
	{
		// KeyboardInterrupt and SystemExit(143) are the Interrupted of a SIGINT and a SIGTERM here.
		foreach (var (stop, expected) in new[] { (new Interrupted(Signals.SigInt), 130), (new Interrupted(Signals.SigTerm), 143) })
		{
			using var fleet = new Fleet { Raises = { [TM] = stop } };
			var (result, _, err) = fleet.RunAuto(["--brings", "#82"], root: "/my repos");
			Assert.Equal(expected, result);
			Assert.Contains("rollout.sh: interrupted; resume with: rollout.sh --auto --brings '#82' --root '/my repos'", err, StringComparison.Ordinal);
			Assert.True(Fleet.Closed(fleet.Locks[0]));
		}
	}

	[Theory]
	[InlineData("gh_pr")]
	[InlineData("gh_pr_state")]
	[InlineData("git_branch")]
	public void Gh_and_git_output_that_isnt_utf_8_is_read(string call)
	{
		// The pytest checked errors="replace" on each call; here each call runs RunQuiet, the default Runner, on a command that writes the
		// byte 0xff, and its message carries U+FFFD in its place.
		List<IReadOnlyList<string>> seen = [];
		Ran Run(IReadOnlyList<string> command, string? cwd, IReadOnlyDictionary<string, string>? env)
		{
			seen.Add(command);
			return AutoRollout.RunQuiet(["sh", "-c", "printf 'bad \\377 byte' >&2; exit 1"], cwd, env);
		}

		Action read = call switch
		{
			"gh_pr" => () => AutoRound.GhPr(TM, Run),
			"gh_pr_state" => () => AutoRound.GhPrState(TM, 1, Run),
			_ => () => AutoRound.GitBranch("/w", Run),
		};
		var error = Assert.Throws<InvalidOperationException>(read);
		Assert.Contains("bad � byte", error.Message, StringComparison.Ordinal);
		Assert.NotEmpty(seen);
	}

	[Fact]
	public void A_read_that_answers_after_a_gh_failure_is_what_the_stop_says()
	{
		using var fleet = new Fleet { Opens = { [TM] = 130 }, StateFailures = { [TM] = 1 }, StillOpen = { $"{TM}#130" } };
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Equal(1, code);
		Assert.Contains("TicketManager: can't tell its re-Apply PR (#130 still reads as OPEN)", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_session_that_broke_a_rule_stops_for_the_person_without_reapplying()
	{
		using var fleet = new Fleet
		{
			Codes = { [TM] = [AutoRound.ReviewNeeded] },
			ReviewCodes = { [TM] = ReviewRecord.SessionBrokeARule },
			Broke = { [TM] = ["the remote chore/reapply-baseline moved: the session isn't to push", "x"] },
		};
		var (code, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.True(code == 1 && fleet.Reapplied().Count == 1);
		var here = "/r/TicketManager-worktrees/chore-reapply-baseline";
		Assert.Contains("TicketManager: the review session broke a rule, so nothing was recorded: the remote chore/reapply-baseline"
				+ " moved: the session isn't to push (and 1 more); see /tmp/review-TicketManager.log. Put it right before you"
				+ $" resume, since a resume starts from what it finds: git -C {here} status --ignored shows the worktree,"
				+ $" cd {here} && scripts/review.sh report shows the review ledgers, and cd {here} && scripts/review.sh"
				+ " security shows the .security record", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Rollouts_reapply_path_has_its_symlinks_resolved()
	{
		// The pytest ran rollout.py through a link to the skill and read its REAPPLY; rollout.cs finds its folders with Folders.FromEntry.
		var link = Path.Combine(tmp, "skill");
		Directory.CreateSymbolicLink(link, Skill.Dir);
		Assert.Equal(AutoRollout.RealPath(Skill.Path("reapply.sh")), Folders.FromEntry(Path.Combine(link, "github")).Reapply);
	}

	[Fact]
	public void A_round_with_ticketmanager_alone_holds_none()
	{
		// The pytest passed None for what the round never calls here; these throw if called.
		static T Unused<T>() => throw new InvalidOperationException("not used");
		var output = new StringWriter();
		var world = new World((_, _) => 1, (_, _) => Unused<(int, string, List<string>)>(), _ => Unused<int?>(),
				_ => Unused<(long, string, string)?>(), (_, _) => Unused<string>(), _ => Unused<(string, bool)>(), Unused<List<string>>,
				Unused<FileStream?>, _ => Unused<int>());
		var code = AutoRound.Round([TM], "#82", "rollout.sh --auto --brings '#82'", repo => $"/r/{Fleets.Short(repo)}", world,
				text => output.Write(text + "\n"), new StringWriter());
		Assert.True(code == 1 && output.ToString().Contains("held: none", StringComparison.Ordinal));
	}

	[Fact]
	public void Git_branch_gives_up_on_a_git_that_hangs()
	{
		List<IReadOnlyList<string>> seen = [];
		AutoRound.GitBranch("/w", (command, _, _) =>
		{
			seen.Add(command);
			return new Ran(0, "abc\n", "");
		});
		// The pytest read timeout=120 off both calls; a Runner takes none, so this checks the limit of RunQuiet, the default Runner.
		Assert.True(AutoRollout.QuietTimeout == TimeSpan.FromSeconds(120) && seen.Count == 2);
	}

	[Fact]
	public void A_sigterm_while_a_repo_is_reapplied_ends_the_round_with_its_resume_command()
	{
		// The pytest's fake reapply sent the SIGTERM itself; here what the fake runs, in a group of its own as RunReapply runs reapply.sh
		// (Groups.RunInGroup), sends it, so it lands in the hold a real reapply.sh has, and the round ends once that group is gone.
		using var fleet = new Fleet();
		int? Terminated(string brings, string path)
		{
			Groups.RunInGroup(["sh", "-c", "kill -TERM \"$1\"; sleep 30", "sh", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
			return fleet.Reapply(brings, path);
		}

		var (code, _, err) = RunAuto(fleet, Terminated);
		Assert.Equal(128 + Signals.SigTerm, code);
		Assert.Contains("rollout.sh: interrupted; resume with: rollout.sh --auto --brings '#82'", err, StringComparison.Ordinal);
		Assert.True(Fleet.Closed(fleet.Locks[0]));
	}

	[Fact]
	public void The_round_passes_its_lock_to_what_it_runs_and_lets_go_after()
	{
		// The pytest's fake lock was descriptor 42; the Fleet's is a real file, so each read is checked against its descriptor.
		using var fleet = new Fleet();
		List<(IReadOnlyList<int> Held, int Lock)> held = [];
		int? Reapply(string brings, string path)
		{
			held.Add((AutoRollout.HeldLockFds(), AutoRollout.Descriptor(fleet.Locks[^1])));
			return fleet.Reapply(brings, path);
		}

		RunAuto(fleet, Reapply);
		// A child a SIGKILL of rollout.sh leaves running keeps the lock.
		Assert.NotEmpty(held);
		Assert.All(held, read => Assert.Equal([read.Lock], read.Held));
		Assert.Empty(AutoRollout.HeldLockFds());
	}

	[Fact]
	public void A_session_that_broke_one_rule_names_it_alone()
	{
		using var fleet = new Fleet
		{
			Codes = { [TM] = [AutoRound.ReviewNeeded] },
			ReviewCodes = { [TM] = ReviewRecord.SessionBrokeARule },
			Broke = { [TM] = ["just this"] },
		};
		var (_, output, _) = fleet.RunAuto(["--brings", "#82"]);
		Assert.Contains("the review session broke a rule, so nothing was recorded: just this; see ", output, StringComparison.Ordinal);
		Assert.DoesNotContain("(and", output, StringComparison.Ordinal);
	}
}
