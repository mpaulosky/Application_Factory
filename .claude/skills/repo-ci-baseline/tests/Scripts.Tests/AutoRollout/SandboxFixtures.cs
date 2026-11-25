using System.Diagnostics;
using System.Text.Json.Nodes;

// What the AutoRolloutSandbox.cs tests share, as test_auto_rollout.py's helpers had it: the session's settings with no real folder
// listed (the real /, home and /tmp differ between hosts and change while they run), fake folder trees, a temporary folder, and git run
// with no global or system config.
static class SandboxFixtures
{
	// The entry of / that SystemDenied keeps denied in every listing.
	public static readonly string[] Held = ["/dev/shm"];

	// AutoRollout.SessionSettings with no folder listed and no tool found unless a test gives its own (settings_of).
	public static JsonObject SettingsOf(string worktree, string common, string gitdir, string tmp, string? home = null, string? configDir = null,
		IReadOnlyDictionary<string, string>? environ = null, Func<string, IEnumerable<string>>? listdir = null, Func<string, string?>? which = null,
		(List<string> Reads, List<string> First)? tools = null) =>
		AutoRollout.SessionSettings(worktree, common, gitdir, tmp, home, configDir, environ, listdir ?? (_ => []), which ?? (_ => null), tools);

	// A listing of the folders tree names, and nothing in any other (a dict's .get(folder, [])).
	public static Func<string, IEnumerable<string>> Tree(Dictionary<string, string[]> tree) => folder => tree.GetValueOrDefault(folder, []);

	public static List<string> Strs(JsonNode? node) => [.. node!.AsArray().Select(item => item!.GetValue<string>())];

	public static List<string> DenyRead(JsonObject settings) => Strs(settings["sandbox"]!["filesystem"]!["denyRead"]);

	public static List<string> Allow(JsonObject settings) => Strs(settings["permissions"]!["allow"]);

	public static List<string> Deny(JsonObject settings) => Strs(settings["permissions"]!["deny"]);

	// A run of a command that couldn't start.
	public static Ran NoAnswer(IReadOnlyList<string> command) => new(127, "", "not run");

	public static Dictionary<string, string> Env(params (string Name, string Value)[] pairs) => pairs.ToDictionary(pair => pair.Name, pair => pair.Value);

	// A new empty folder, its real path (as pytest's tmp_path is), removed when the test run ends.
	public static string TempDir()
	{
		var folder = PyLib.RealPath(Directory.CreateTempSubdirectory("sandbox-test.").FullName);
		lock (Made)
			Made.Add(folder);
		return folder;
	}

	static readonly List<string> Made = Removed();

	static List<string> Removed()
	{
		List<string> made = [];
		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			foreach (var folder in made)
			{
				try
				{
					Directory.Delete(folder, recursive: true);
				}
				catch (Exception error) when (error is IOException or UnauthorizedAccessException)
				{
				}
			}
		};
		return made;
	}

	// git with no global or system config, failing the test when it fails; stdout.
	public static string Git(params string[] args) => GitIn(null, args);

	public static string GitIn(IReadOnlyDictionary<string, string>? env, params string[] args)
	{
		var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		if (env is not null)
		{
			start.Environment.Clear();
			foreach (var (name, value) in env)
				start.Environment[name] = value;
		}
		start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
		start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
		foreach (var arg in args)
			start.ArgumentList.Add(arg);
		using var git = Process.Start(start)!;
		var output = git.StandardOutput.ReadToEndAsync();
		var err = git.StandardError.ReadToEnd();
		git.WaitForExit();
		Assert.True(env is not null || git.ExitCode == 0, $"git {string.Join(' ', args)}: {err}");
		return output.Result;
	}

	// A git repository at path with each setting added to its config (git_config_repo).
	public static void GitConfigRepo(string path, params (string Key, string Value)[] settings)
	{
		Git("init", "-q", path);
		foreach (var (key, value) in settings)
			Git("-C", path, "config", "--add", key, value);
	}
}
