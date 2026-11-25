using System.Text;
using System.Text.Json.Nodes;

// What the review session's tests share: test_auto_rollout.py's FakeSession, fake_guard, settings_of and review_once, for ReviewSession.RunReview.

// A session that exits with Code when it is waited on (Exit), or never does (TimesOut: each wait runs out), or throws Raises (an
// Interrupted stands for the pytest's KeyboardInterrupt). Waits holds each wait's seconds.
sealed class FakeSession(int code = 0, bool timesOut = false, Exception? raises = null, int pid = 999999) : IGroupChild
{
	public readonly List<double> Waits = [];

	public int Pid => pid;

	public int? Exit(double seconds)
	{
		Waits.Add(seconds);
		if (raises is not null)
			throw raises;
		return timesOut ? null : code;
	}
}

// What a fake session saw and the round did (review_once's seen).
sealed class ReviewSeen
{
	public (string Worktree, string Run, bool IsDir)? Recorded;
	public string? RecordHead, Keep;
	public IList<int>? Got;
	public List<string>? Unseen;
	public IReadOnlyList<string>? Command;
	public Spawning? How;
	public (string Path, UnixFileMode Mode, bool Regular)? SettingsFile;
	public JsonNode? Settings;
	public string? Tmp;
	public List<string>? GhConfig;
	public Dictionary<string, bool>? Folders;
	public (string Path, UnixFileMode? Mode)? Run;
	public readonly List<IGroupChild> Ended = [];
	public string? Guarded;
	public List<string> Broke = [];
	public string Err = "";
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
static class ReviewFixtures
{
	public const string SkillText = "# Pre-push review\n\nThe skill's own steps.\n";

	public static JsonNode Agents() => JsonNode.Parse("""{"pre-push-reviewer": {"description": "d", "prompt": "p", "tools": ["Read"]}}""")!;

	// The guard a fake session runs under: nothing read of a real worktree, its clone's git folder in tmp.
	public static Guard FakeGuard(string tmp)
	{
		var common = Path.Combine(tmp, "clone", ".git");
		Directory.CreateDirectory(common);
		return new Guard("w", "", [], Path.Combine(common, "review"), [], common, Path.Combine(common, "worktrees", "chore-reapply-baseline"),
				Encoding.UTF8.GetBytes("gitdir: x\n"), Path.Combine(tmp, "clone", ".security"), [], ("headsha", "branchname", ""), SessionGuard.TreeState("w"),
				SessionGuard.GitFilesState(common, Path.Combine(common, "worktrees")));
	}

	// AutoRollout.SessionSettings with NO_LISTING's listdir and which: the real /, home and /tmp differ between hosts and change while they run.
	public static JsonObject SettingsOf(string worktree, string common, string gitdir, string tmp) =>
		AutoRollout.SessionSettings(worktree, common, gitdir, tmp, listdir: _ => [], which: _ => null);

	// The worktree review_once makes when it isn't given one.
	public static string DefaultWorktree(string tmp) => Path.Combine(tmp, "TicketManager-worktrees", "chore-reapply-baseline");

	// RunReview of TicketManager's re-Apply worktree with every outside part faked, as review_once runs it: (exit code, log, what was seen,
	// the worktree). GATE_SKIP_REVIEW is set while it runs, as the pytest's monkeypatch set it, so a test sees it left out of the session's
	// environment. listdir, which and secrets default to NO_LISTING's (nothing listed, no tool found, no secret).
	public static (int Code, string Log, ReviewSeen Seen, string Worktree) ReviewOnce(string tmp, Spawner? popen = null,
			Func<string, IReadOnlyList<string>>? changes = null, string? worktree = null, IGroupChild? session = null, Func<IGroupChild, bool>? end = null,
			IEnumerable<string>? broke = null, Func<double>? clock = null, Func<string, Guard>? guard = null, Recorder? record = null,
			Func<Guard, List<string>>? check = null, Func<string, IEnumerable<string>>? listdir = null, Func<string, string?>? which = null,
			Func<string, string, List<string>>? secrets = null, double timeout = ReviewSession.ReviewTimeoutMinutes * 60)
	{
		var seen = new ReviewSeen();
		session ??= new FakeSession();

		(int, List<string>) FakeRecord(string path, string run, FileStream log, string head, string keep, IList<int> got, Func<List<string>> unseen)
		{
			seen.Recorded = (path, run, Directory.Exists(run));
			(seen.RecordHead, seen.Keep, seen.Got, seen.Unseen) = (head, keep, got, unseen());
			return (0, []);
		}

		IGroupChild FakePopen(IReadOnlyList<string> command, Spawning how)
		{
			(seen.Command, seen.How) = (command, how);
			var path = command[command.ToList().IndexOf("--settings") + 1];
			var info = new FileInfo(path);
			seen.SettingsFile = (path, File.GetUnixFileMode(path), info.LinkTarget is null && info.Exists);
			seen.Settings = JsonNode.Parse(File.ReadAllText(path));
			seen.Tmp = Path.GetDirectoryName(how.Env["TMPDIR"]);
			seen.GhConfig = [.. Directory.EnumerateFileSystemEntries(how.Env["GH_CONFIG_DIR"]).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];
			seen.Folders = new[] { ".git/review", ".security" }.ToDictionary(name => name,
					name => AutoRollout.LExists(Path.Combine(tmp, "clone", name)));
			var run = Path.Combine(seen.Tmp!, "run");
			seen.Run = (run, Directory.Exists(run) ? File.GetUnixFileMode(run) : null);
			how.Stdout!.Write(Encoding.UTF8.GetBytes("the session's own output\n"));
			how.Stdout.Flush();
			return session;
		}

		if (worktree is null)
		{
			worktree = DefaultWorktree(tmp);
			Directory.CreateDirectory(worktree);
		}
		var err = new StringWriter();
		var skip = Environment.GetEnvironmentVariable("GATE_SKIP_REVIEW");
		Environment.SetEnvironmentVariable("GATE_SKIP_REVIEW", "1");
		try
		{
			var brokeList = (broke ?? []).ToList();
			var (code, log, broken) = ReviewSession.RunReview(Fleets.TM, worktree, popen ?? FakePopen,
					changes ?? (_ => [".github/hooks/pre-push", "scripts/gate.sh"]), timeout, Path.Combine(tmp, "logs"), Path.Combine(tmp, "sessions"),
					end ?? (child =>
					{
						seen.Ended.Add(child);
						return true;
					}),
					clock, guard ?? (path =>
					{
						seen.Guarded ??= path;
						return FakeGuard(tmp);
					}),
					check ?? (_ => [.. brokeList]), () => SkillText, Agents, record ?? FakeRecord, listdir ?? (_ => []), which ?? (_ => null),
					secrets ?? ((_, _) => []), err);
			seen.Broke = broken;
			return (code, log, seen, worktree);
		}
		finally
		{
			Environment.SetEnvironmentVariable("GATE_SKIP_REVIEW", skip);
			seen.Err = err.ToString();
		}
	}
}
