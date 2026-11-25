// auto_rollout.py's preflight and sign-in checks, as part of the AutoRollout library (AutoRollout.cs says what the library is and how it is kept in
// step with the Python): what would stop a round partway through, asked before anything runs.
using System.Collections;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

static partial class AutoRollout
{
	// What --auto needs on PATH: reapply.sh and land.sh (git, gh, and dotnet, which runs land.sh's github/land.cs: ADR 0017), the gate (python3,
	// node, pnpm, shfmt) and the review session (claude).
	public static readonly IReadOnlyList<string> Tools = ["git", "gh", "dotnet", "python3", "node", "pnpm", "shfmt", "claude"];
	// What the sandbox runs on (bubblewrap and socat); empty on another system, where the preflight refuses to run at all.
	public static readonly IReadOnlyList<string> SandboxTools = OperatingSystem.IsLinux() ? ["bwrap", "socat"] : [];
	// check_gh_repo.py's SHFMT_RELEASE and SHFMT_OLDEST_MINOR (a test holds them equal): a v3 release from v3.14 on.
	public static readonly Regex ShfmtRelease = new("v3\\.(0|[1-9][0-9]*)\\.[0-9]+");
	public const int ShfmtOldestMinor = 14;
	// The Template gate's RUFF_RELEASE and RUFF_VERSION (a test holds them equal): its Python lint uses an installed ruff only when it is a 0.17
	// release, and otherwise runs the pinned one through uvx.
	public static readonly Regex RuffRelease = new("^ruff 0\\.17\\.[0-9]+$");
	public const string RuffVersion = "0.17.6";
	public const string RuffInstall = "install uv (https://docs.astral.sh/uv/), or put a ruff 0.17 first on PATH (uv tool install"
			+ $" ruff@{RuffVersion} once uv is there)";
	// How the preflight asks whether claude is signed in: with no settings file, as the review session runs.
	public static readonly IReadOnlyList<string> ClaudeAuthStatus = ["claude", "--setting-sources", "", "auth", "status", "--json"];
	// The preflight's try of the review session's sandbox, as the person can run it again.
	public static readonly IReadOnlyList<string> BwrapProbe = ["bwrap", "--unshare-all", "--ro-bind", "/", "/", "--dev", "/dev", "true"];
	// Claude Code's names the session keeps: where its config is and which model, endpoint and cloud it uses, none a secret.
	public static readonly IReadOnlyList<string> ClaudeKept =
	[
		"ANTHROPIC_BASE_URL", "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL",
		"ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_VERTEX_PROJECT_ID",
		"ANTHROPIC_FOUNDRY_RESOURCE", "ANTHROPIC_BEDROCK_BASE_URL", "ANTHROPIC_VERTEX_BASE_URL", "ANTHROPIC_FOUNDRY_BASE_URL",
		"CLAUDE_CONFIG_DIR", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
	];
	// Claude Code's sign-in secrets: the preflight refuses a round with one set, since the session's own claude would need it in the
	// environment its shell inherits.
	public static readonly IReadOnlyList<string> ClaudeSecrets =
			["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_FOUNDRY_API_KEY", "ANTHROPIC_CUSTOM_HEADERS"];
	// A cloud variable that is a secret, or names a file of one (a certificate's or private key's path, a key file).
	public static readonly Regex SignInSecret = new(
			"TOKEN|SECRET|PASSW|API_?KEY|ACCESS_KEY|AUTH(?!OR)|CREDENTIAL|_KEY$|COOKIE|BEARER|CERT|PRIVATE|KEY_?FILE",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	// The proxy variables the session's own claude reaches its endpoint through (curl's and Node's names, either case).
	public static readonly Regex ProxyName = new("^(HTTPS?|ALL|FTP)_PROXY$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	// A proxy written with no scheme, as curl and git take one, whose host has a sign-in before it: a user, with or without a password, or a
	// password alone (user:pass@proxy:3128, token@proxy:3128, :pass@proxy:3128).
	public static readonly Regex BareUserinfo = new($"^[{PySpace}]*(?:[^/@{PySpace}:]+(?::[^/@{PySpace}]*)?|:[^/@{PySpace}]+)@[^/@{PySpace}]");
	// A URL with a sign-in in it: userinfo after any scheme (https://<token>@host, ssh://user:password@host); a bare user (ssh's git@, an http
	// user with no password) is no secret, but over http(s) or ftp(s) the user may be the token. A scheme takes no dot here, so
	// url.https://<token>@host/.insteadOf, a key naming a URL, is read as https. The scheme starts where Python's \b would start it: after no
	// letter, number or _.
	public static readonly Regex UrlUserinfo = new($@"(?<![\p{{L}}\p{{N}}_])([a-z][a-z0-9+-]*)://([^/@{PySpace}]+)@",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	// Each cloud flag Claude Code reads, and the prefix of the variables that cloud signs in with, in auto_rollout.py's order.
	public static readonly IReadOnlyList<KeyValuePair<string, string>> CloudSignIn =
	[
		new("CLAUDE_CODE_USE_BEDROCK", "AWS_"), new("CLAUDE_CODE_USE_VERTEX", "GOOGLE_"), new("CLAUDE_CODE_USE_FOUNDRY", "AZURE_"),
	];
	// The values of a CLAUDE_CODE_USE_* flag that turn its cloud on; any other (0, false, empty) leaves it off.
	public static readonly IReadOnlyList<string> FlagOn = ["1", "true", "yes", "on"];

	// Why shfmt --version's answer won't do for the gate's gh check, or null: check_gh_repo.py's own rule.
	public static string? ShfmtProblem(string versionText)
	{
		var text = PyStrip(versionText);
		var match = new Regex($@"\A(?:{ShfmtRelease})\z").Match(text);
		if (!match.Success || BigInteger.Parse(match.Groups[1].Value) < ShfmtOldestMinor)
			return $"shfmt is {(text.Length > 0 ? text : "silent")}; the gate needs v3.{ShfmtOldestMinor} or a later v3 release";
		return null;
	}

	static readonly Regex SdkVersion = new($"\"sdk\"[{PySpace}]*:[{PySpace}]*\\{{[^{{}}]*?\"version\"[{PySpace}]*:[{PySpace}]*\"([0-9][0-9A-Za-z.+-]{{0,40}})\"");

	// The SDK version a global.json's text names, for the install hint only, or null. dotnet decides what the file means (it allows comments
	// and a byte-order mark, which a JSON parser refuses); this only finds a plain version.
	public static string? SdkHint(string text) => SdkVersion.Match(text) is { Success: true } match ? match.Groups[1].Value : null;

	// shutil.which's answer: the tool's path in PATH's absolute folders (Gh.Which), or null.
	public static string? Which(string tool)
	{
		try
		{
			return Gh.Which(tool, Environment.GetEnvironmentVariable("PATH"));
		}
		catch (FileNotFoundException)
		{
			return null;
		}
	}

	// sys.platform's name for this system.
	public static string Platform => OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32"
			: OperatingSystem.IsFreeBSD() ? "freebsd" : System.Runtime.InteropServices.RuntimeInformation.OSDescription;

	// The process's environment, as os.environ holds it.
	public static IReadOnlyDictionary<string, string> Environ() =>
			Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "", StringComparer.Ordinal);

	// What would stop the round partway through, as a list of problems (empty when there's none). A null argument is its default:
	// templateGlobalJson the Template's global.json, factoryGlobalJson the Factory's ("" for none), environ the process's, platform the host's;
	// makeTemp makes the folder the SDK is asked in.
	public static List<string> Preflight(Func<string, string?>? which = null, Runner? run = null, string? templateGlobalJson = null,
			IReadOnlyDictionary<string, string>? environ = null, string? platform = null, string? factoryGlobalJson = null, Func<string>? makeTemp = null)
	{
		which ??= Which;
		run ??= RunQuiet;
		templateGlobalJson ??= Folders.Default.TemplateGlobalJson;
		factoryGlobalJson ??= Folders.Default.FactoryGlobalJson;
		environ ??= Environ();
		platform ??= Platform;
		var problems = platform.StartsWith("linux", StringComparison.Ordinal) ? new List<string>() :
		[
			$"rollout.sh --auto runs on Linux only ({platform} here): what its review session may read is checked"
					+ " with bubblewrap's sandbox alone; run rollout.sh without --auto, and the review by hand",
		];
		problems.AddRange(Tools.Where(tool => which(tool) is null).Select(tool => $"{tool} isn't on PATH"));
		problems.AddRange(SandboxTools.Where(tool => which(tool) is null).Select(tool => $"{tool} isn't on PATH: the review session's sandbox needs it"));
		problems.AddRange(RuffProblems(which, run));
		problems.AddRange(SignInProblems(environ));
		if (SandboxTools.Count > 0 && which("bwrap") is not null)
		{
			// The sandbox needs unprivileged user namespaces, which a host can refuse (AppArmor's restriction on Ubuntu 24.04, some containers):
			// then the binaries are there and the first review session still can't start.
			var (code, _, error) = run([.. BwrapProbe]);
			if (code != 0)
				problems.Add($"bubblewrap can't make the review session's sandbox here (exit {code}): {Or(PyStrip(error), "no answer")};"
						+ $" try it with: {ShellJoin(BwrapProbe)}. The host may refuse unprivileged user namespaces"
						+ " (Ubuntu's sysctl kernel.apparmor_restrict_unprivileged_userns, kernel.unprivileged_userns_clone,"
						+ " a container without them); where it can't allow them, run rollout.sh without --auto and the"
						+ " review by hand");
		}
		if (which("claude") is not null)
		{
			var (code, _, error) = run(["claude", "--version"]);
			if (code != 0)
				problems.Add($"claude --version failed (exit {code}): {Or(PyStrip(error), "no answer")}");
			else
				problems.AddRange(ClaudeSignInProblems(run, environ));
		}
		if (which("shfmt") is not null)
		{
			// As check_gh_repo.py runs it: PATH alone, stdout alone.
			var (code, output, error) = run(["shfmt", "--version"], env: new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "" });
			var problem = code == 0 ? ShfmtProblem(output) : $"shfmt --version failed (exit {code}): {PyStrip(error)}";
			if (problem is not null)
				problems.Add(problem);
		}
		if (which("gh") is not null)
		{
			var (code, _, error) = run(["gh", "auth", "status"]);
			if (code is 124 or 127)
				problems.Add($"gh auth status didn't run: {PyStrip(error)}");
			else if (code != 0)
				problems.Add($"gh auth status failed (exit {code}): {Or(PyStrip(error), "no answer")}; if it says you're signed out: gh auth login");
		}
		if (LExists(templateGlobalJson))
			problems.AddRange(SdkProblems(templateGlobalJson, which, run, makeTemp: makeTemp));
		// land.sh runs on the Factory's global.json, not the Template's: when the two pins differ, its SDK is checked too, or the round would start
		// and land.sh fail at dotnet run (mpaulosky/Application_Factory#178).
		if (factoryGlobalJson.Length > 0 && LExists(factoryGlobalJson) && !SameFileText(factoryGlobalJson, templateGlobalJson))
			problems.AddRange(SdkProblems(factoryGlobalJson, which, run, "the Factory's global.json, which land.sh runs on", makeTemp));
		return problems;
	}

	static string Or(string text, string otherwise) => text.Length > 0 ? text : otherwise;

	// Whether two files hold the same bytes; false when either can't be read.
	public static bool SameFileText(string path, string other)
	{
		try
		{
			return File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(other));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	// The prefixes of the clouds Claude Code signs in through (CLAUDE_CODE_USE_* on), whose variables it needs.
	public static List<string> KeptClouds(IReadOnlyDictionary<string, string> environ) =>
			[.. CloudSignIn.Where(cloud => FlagOn.Contains(PyStrip(environ.GetValueOrDefault(cloud.Key, "")).ToLowerInvariant())).Select(cloud => cloud.Value)];

	// A sign-in secret in the environment the session would inherit: one of ClaudeSecrets, or a secret of the cloud in use. Its shell would see it,
	// so the round doesn't start. Nor does it when a variable the session's own claude needs (ClaudeKept, the cloud in use's, a proxy's: ProxyName)
	// holds a URL with a sign-in (HoldsSignIn: a proxy's with no scheme too), which the session's environment leaves out: the session would run
	// without its endpoint or proxy, and fail with nothing naming why. Each is named, never its value.
	public static List<string> SignInProblems(IReadOnlyDictionary<string, string> environ)
	{
		var clouds = KeptClouds(environ);
		bool Cloud(string name) => clouds.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
		var found = environ.Where(pair => pair.Value.Length > 0 && (ClaudeSecrets.Contains(pair.Key) || (Cloud(pair.Key) && SignInSecret.IsMatch(pair.Key))))
				.Select(pair => pair.Key).ToList();
		found.Sort(CodePoints);
		var needed = environ.Where(pair => !found.Contains(pair.Key) && HoldsSignIn(pair.Key, pair.Value)
				&& (ClaudeKept.Contains(pair.Key) || ProxyName.IsMatch(pair.Key) || Cloud(pair.Key))).Select(pair => pair.Key).ToList();
		needed.Sort(CodePoints);
		return
		[
			.. found.Select(name => $"{name} is set, and the review session's shell would see it: unset it for the round (env -u {ShellQuote(name)}"
					+ " before the command), and sign Claude Code in with its own credentials file (claude /login) or the cloud's profile files,"
					+ " which the sandbox keeps from the shell"),
			.. needed.Select(name => $"{name} holds a URL with a sign-in, which the review session is never given (its shell would see it),"
					+ " so its claude would run without it: for the round, set it to a URL with no sign-in in it (one that"
					+ $" takes it from a file of its own), or unset it (env -u {ShellQuote(name)} before the command, for one round)"),
		];
	}

	// Whether text holds a URL with a sign-in in it (UrlUserinfo).
	public static bool HasSignIn(string text) => UrlUserinfo.Matches(text).Any(found =>
			found.Groups[2].Value.Contains(':') || found.Groups[1].Value.ToLowerInvariant() is "http" or "https" or "ftp" or "ftps");

	// Whether the variable name=value holds a URL with a sign-in (HasSignIn), or is a proxy (ProxyName) written with no scheme whose host has one
	// before it (BareUserinfo), as curl and git read it.
	public static bool HoldsSignIn(string name, string value) => HasSignIn(value) || (ProxyName.IsMatch(name) && BareUserinfo.IsMatch(value));

	// Whether claude is signed in as the session will be: ClaudeAuthStatus, which reads no settings file as the session reads none (so a sign-in
	// only a settings file's apiKeyHelper gives doesn't count), must say loggedIn. Only its loggedIn is read: the rest of what it prints, the
	// account, is never shown, nor is its stderr. With a cloud's flag on (KeptClouds), Claude Code signs in through the cloud's profile, which it
	// doesn't report, so nothing is asked: the first session is where a cloud sign-in that fails shows.
	public static List<string> ClaudeSignInProblems(Runner run, IReadOnlyDictionary<string, string> environ)
	{
		if (KeptClouds(environ).Count > 0)
			return [];
		var (code, output, _) = run([.. ClaudeAuthStatus]);
		var command = ShellJoin(ClaudeAuthStatus);
		if (code != 0)
			return [$"{command} failed (exit {code}), so whether claude is signed in can't be told: run it to see why, and"
					+ " sign claude in with claude /login if it says you're signed out"];
		JsonValueKind? loggedIn = null;
		try
		{
			using var status = JsonDocument.Parse(output);
			// json.loads keeps a key's last value.
			if (status.RootElement.ValueKind == JsonValueKind.Object)
				foreach (var property in status.RootElement.EnumerateObject())
					if (property.NameEquals("loggedIn"))
						loggedIn = property.Value.ValueKind;
		}
		catch (JsonException)
		{
		}
		if (loggedIn is null)
			return [$"{command} doesn't say whether claude is signed in (no loggedIn in what it prints): update claude, or"
					+ " run rollout.sh without --auto"];
		if (loggedIn != JsonValueKind.True)
			return [$"claude isn't signed in, as {command} says: sign it in with claude /login (the review session reads no"
					+ " settings file, so an apiKeyHelper in one isn't used)"];
		return [];
	}

	// Whether the gate's Python lint can run, as the Template's gate decides: an installed ruff 0.17 release, or uvx to run the pinned one. A
	// re-Apply that writes an Owned Python file needs it.
	public static List<string> RuffProblems(Func<string, string?> which, Runner run)
	{
		if (which("uvx") is not null)
			return [];
		if (which("ruff") is null)
			return [$"neither ruff 0.17 nor uv is installed, and the gate's Python lint needs one: {RuffInstall}"];
		var (code, output, error) = run(["ruff", "--version"]);
		var found = PyStrip(output + error);
		if (code == 0 && FullMatch(RuffRelease, found))
			return [];
		return [$"ruff is '{Or(found, "silent")}' (exit {code}), not a 0.17 release, and uv isn't installed for the gate to run"
				+ $" the pinned one: {RuffInstall}"];
	}

	// Whether an installed .NET SDK satisfies a global.json (name says whose), asked of dotnet in a folder holding a copy.
	public static List<string> SdkProblems(string globalJson, Func<string, string?> which, Runner run, string name = "the Template's global.json",
			Func<string>? makeTemp = null)
	{
		string? version;
		try
		{
			version = SdkHint(Encoding.UTF8.GetString(File.ReadAllBytes(globalJson)));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return [$"can't read {name}: {error.Message}"];
		}
		var named = version is not null ? $".NET SDK {version}" : "the .NET SDK it names";
		if (which("dotnet") is null) // Tools has dotnet, so it is named once, there
			return [];
		Ran answer;
		try
		{
			var folder = (makeTemp ?? (() => Directory.CreateTempSubdirectory("rollout-sdk.").FullName))();
			try
			{
				File.Copy(globalJson, Path.Combine(folder, "global.json"));
				answer = run(["dotnet", "--version"], cwd: folder);
			}
			finally
			{
				Directory.Delete(folder, recursive: true);
			}
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return [$"can't check the .NET SDK (no temporary folder to check it in): {error.Message}"];
		}
		if (answer.Code == 0)
			return [];
		var install = version is not null ? $": dotnetup sdk install {version}" : "";
		var why = answer.Code is 124 or 127 ? $" ({PyStrip(answer.Stderr)})" : "";
		return [$"no installed .NET SDK matches {name} ({named}){install}{why}"];
	}
}
