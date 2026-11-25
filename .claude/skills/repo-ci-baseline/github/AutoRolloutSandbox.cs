// rollout.sh --auto's review-session sandbox, ported from auto_rollout.py (expand_home to session_env) as a library that nothing runs yet
// (ADR 0017's Update for mpaulosky/Application_Factory#157, library first): auto_rollout.py is still what runs, and its docstring holds the
// round's contract. This part says what the unattended review session may read, write and run: the session's reads and writes, what of
// / its Bash is denied (denied_around, read_denials), the tools' own installs (tool_reads), its settings file and command line, the
// clone's git config read for sign-in secrets (config_secrets), the sweeps of what killed runs left, and its environment.
//
// Each method keeps its Python's readings: paths as os.path reads them (PyLib), sorted by code point, symlinks resolved as realpath
// resolves them, and the messages word for word. Where the port differs on purpose it says so.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// One finished command: its exit code, stdout and stderr (run_quiet's tuple).
readonly record struct Ran(int Code, string Out, string Err);

// A name denied_around must hide but a sandbox path can't name (Python's ValueError there).
sealed class UnnameableError(string message) : Exception(message);

// The clone's git folder can't be read whole, so what the session could read of it can't be told (Python's RuntimeError there).
sealed class ConfigUnreadable(string message) : Exception(message);

static partial class AutoRollout
{
	// The skill's folder, as the source file's own path gives it (auto_rollout.py's __file__), and the Factory's checkout above it.
	public static readonly string SkillDir = PyLib.Dirname(PyLib.Dirname(PyLib.RealPath(SourceFile())));
	public static readonly string Factory = PyLib.Dirname(PyLib.Dirname(PyLib.Dirname(SkillDir)));
	static string SourceFile([CallerFilePath] string path = "") => path;

	// The 0700 folder of each review session's log, and of its settings file beside it.
	public static readonly string LogDir = PyLib.ExpandUser("~/.cache/repo-ci-baseline/logs");
	// Where each review session's own temporary folder is made: in home, not /tmp, so the sandbox denies /tmp whole, and beside the
	// cache folder, not in it, since the session can't read that (CredentialPaths). A folder a killed run left is removed by the next
	// (SweepSessions).
	public static readonly string SessionDir = PyLib.ExpandUser("~/.cache/repo-ci-baseline-sessions");

	// What the session may never do, whatever text it reads: the skill's Rules, and the Template-first rule (the Factory's CLAUDE.md).
	public static readonly IReadOnlyList<string> DeniedTools =
	[
		"Bash(git push:*)",
		"Bash(scripts/review.sh accept:*)",
		"Bash(scripts/review.sh accept-open:*)",
		"Bash(gh pr create:*)", "Bash(gh pr edit:*)", "Bash(gh pr comment:*)", "Bash(gh pr review:*)",
		"Bash(gh pr merge:*)", "Bash(gh pr ready:*)", "Bash(gh pr close:*)",
		"Bash(gh issue create:*)", "Bash(gh issue comment:*)", "Bash(gh issue edit:*)", "Bash(gh issue close:*)",
		"Bash(gh label:*)", "Bash(gh release:*)", "Bash(gh repo:*)", "Bash(gh secret:*)", "Bash(gh workflow:*)",
		$"Edit(/{Factory}/**)", "Edit(~/.claude/**)",
	];
	// The built-in tools the session has at all (--tools).
	public static readonly IReadOnlyList<string> SessionBuiltins = ["Read", "Write", "Edit", "Glob", "Grep", "Bash", "Agent", "TodoWrite", "SendMessage"];
	// The tools the session may use without a path, in dontAsk mode; reads and edits are allowed by path only (SessionSettings).
	public static readonly IReadOnlyList<string> SessionTools = ["Bash", "Agent", "Task", "TodoWrite", "SendMessage"];
	// What neither the sandbox's Bash nor the file tools may read: the person's credentials of every kind, shell histories, Claude Code's
	// own sign-in and the other sessions' logs, and the files a secret is often exported or kept in.
	public static readonly IReadOnlyList<string> CredentialPaths =
	[
		"~/.ssh", "~/.config/gh", "~/.config/glab-cli", "~/.config/hub", "~/.git-credentials", "~/.config/git/credentials",
		"~/.netrc", "~/.gnupg", "~/.docker", "~/.aws", "~/.azure", "~/.config/gcloud", "~/.kube", "~/.terraform.d",
		"~/.npmrc", "~/.yarnrc", "~/.yarnrc.yml", "~/.config/pnpm", "~/.nuget/NuGet", "~/.config/NuGet", "~/.pypirc",
		"~/.config/pip", "~/.config/uv", "~/.cargo/credentials", "~/.cargo/credentials.toml", "~/.gem/credentials",
		"~/.m2/settings.xml", "~/.local/share/keyrings", "~/.password-store", "~/.vault-token", "~/.config/op",
		"~/.pgpass", "~/.bash_history", "~/.zsh_history", "~/.local/share/fish/fish_history", "~/.claude.json",
		"~/.claude", "~/.cache/repo-ci-baseline",
		"~/.bashrc", "~/.bash_profile", "~/.bash_login", "~/.profile", "~/.zshrc", "~/.zshenv", "~/.zprofile",
		"~/.zlogin", "~/.config/fish", "~/.gitconfig", "~/.config/git", "~/.microsoft/usersecrets", "~/.aspnet",
		"~/.config/rclone", "~/.s3cfg", "~/.boto", "~/.oci", "~/.config/doctl", "~/.gradle/gradle.properties",
	];
	// The variables that move one of those credentials elsewhere; XDG_CONFIG_HOME, where set, holds XdgConfigSubfolders.
	public static readonly IReadOnlyList<string> CredentialVars =
	[
		"GH_CONFIG_DIR", "DOCKER_CONFIG", "KUBECONFIG", "AWS_SHARED_CREDENTIALS_FILE", "AWS_CONFIG_FILE",
		"NPM_CONFIG_USERCONFIG", "npm_config_userconfig", "GNUPGHOME", "CLOUDSDK_CONFIG", "AZURE_CONFIG_DIR",
		"PASSWORD_STORE_DIR", "GIT_CONFIG_GLOBAL",
	];
	public static readonly IReadOnlyList<string> XdgConfigSubfolders =
		["gh", "glab-cli", "hub", "git", "pnpm", "pip", "uv", "NuGet", "op", "gcloud", "rclone", "doctl", "fish"];
	// What the session's Bash may read besides SessionReads and SessionCommands' own files: the system's own folders, and of /run only
	// NixOS's system links. Every other entry of / is denied (DeniedAround).
	public static readonly IReadOnlyList<string> SystemReads =
	[
		"/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/etc", "/proc", "/dev", "/sys", "/nix",
		"/run/current-system", "/run/booted-system", "/run/wrappers",
	];
	// What of SystemReads holds the person's own, so is denied all the same: shared memory.
	public static readonly IReadOnlyList<string> SystemDenied = ["/dev/shm"];
	// The commands the session's Bash runs that may be installed outside SystemReads: git, and node, which scripts/review.sh stage runs.
	public static readonly IReadOnlyList<string> SessionCommands = ["node", "git"];
	// Characters a sandbox path may read as a pattern: an entry named with one is refused rather than denied (DeniedAround).
	public static readonly IReadOnlySet<char> GlobCharacters = new SortedSet<char>("*?[]{}");
	// The hosts the session's Bash may reach: none.
	public static readonly IReadOnlyList<string> SandboxDomains = [];
	// The headless review's instructions, then the skill's own text from the Template.
	public const string ReviewPrompt = """
		You are running the pre-push-review skill, unattended, for rollout.sh --auto, in {worktree}, the re-Apply worktree of {repo}. The skill's instructions are below, from the Factory's Template; its base directory is {skill_dir}, the worktree's copy of the same files. Follow them, with these changes.
		Nobody will answer a question: never ask one or wait for a reply; where the skill says to ask the person or show them text first, don't, and say so in your last message.
		Review only: this session changes nothing in the repository. Don't edit, fix or commit anything, and don't run the gate (reapply.sh runs it on the push). Run one round over the whole branch, as the skill's last, review-only round: stage open every finding the skill's triage doesn't dismiss, with its suggested fix, and the person fixes or accepts them; a finding the triage dismisses, with the skill's reason, is staged dismissed only when it isn't a security one. Never dismiss a security finding (one the skill's triage marks security): stage it open, a repeat of a fixed .security entry too, with that entry's number in the summary. Give every finding you stage its security mark, as the skill's triage says: true, or false only once you have read the code and it isn't a security weakness; rollout.sh takes a dismissed finding without "security": false for a security one, and stops the round. Leave out every finding's repeats field, whatever entry it would name (fixed, open or accepted): rollout.sh drops it, and the record matches a finding to an entry by its path and summary.
		Your run folder is {run}, made for you: use it where the skill says to make one with mktemp -d, and write nothing anywhere else. The worktree, the clone's git folder, its review ledgers and the .security record are read-only to you.
		Stage the round's findings with scripts/review.sh stage {run} <file> as the skill's step 5 says, as an empty array [] when it keeps none, and stop there: don't run scripts/review.sh record. rollout.sh records what you staged once the session ends, with the key and HEAD of this code.
		These files the branch changes are Owned Baseline files, which the next Apply overwrites: {owned}. A finding in one says, in its fix, what to change in the Factory's template/owned/.
		Don't push, don't file the follow-up issue, and don't edit a PR.
		Your shell runs in a sandbox with no network and no GitHub sign-in: skip the skill's GitHub reads (the PR's threads, the issue) and its git fetch, and say you did. reapply.sh has just fetched origin, so go on with origin/main as it is.
		End with a list of the findings you staged: each one's id, severity, path and line.

		{skill}
		""";

	// A name the session's environment leaves out: a token, secret, password, passphrase, key, credential, auth value or cookie of any
	// tool, the helpers that hand one over, the keyring's bus, the X server's cookie, and the caller's own GIT_CONFIG_ entries.
	public static readonly Regex SecretName = new(
		@"TOKEN|SECRET|PASSW|PASSPHRASE|CREDENTIAL|API_?KEY|AUTH(?!OR)|COOKIE|BEARER|_PAT$|_KEY$|_DSN$|_PWD$|^DATABASE_URL$|ASKPASS"
		+ @"|^GIT_SSH|^SSH_|^DBUS_SESSION_BUS_ADDRESS$|^XAUTHORITY$|^GIT_CONFIG_(COUNT|PARAMETERS|KEY_[0-9]+|VALUE_[0-9]+)$"
		+ @"|^AWS_|^AZURE_|^GOOGLE_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	public static readonly Regex ClaudeSignIn = new(@"^(ANTHROPIC_|CLAUDE_)", RegexOptions.CultureInvariant);
	// Claude Code's names the session keeps: where its config is and which model, endpoint and cloud it uses, none a secret.
	public static readonly IReadOnlyList<string> ClaudeKept =
	[
		"ANTHROPIC_BASE_URL", "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL",
		"ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_VERTEX_PROJECT_ID",
		"ANTHROPIC_FOUNDRY_RESOURCE", "ANTHROPIC_BEDROCK_BASE_URL", "ANTHROPIC_VERTEX_BASE_URL", "ANTHROPIC_FOUNDRY_BASE_URL",
		"CLAUDE_CONFIG_DIR", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
	];
	// A cloud variable that is a secret, or names a file of one: left out by SessionEnv.
	public static readonly Regex SignInSecret = new(
		@"TOKEN|SECRET|PASSW|API_?KEY|ACCESS_KEY|AUTH(?!OR)|CREDENTIAL|_KEY$|COOKIE|BEARER|CERT|PRIVATE|KEY_?FILE",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	// The proxy variables the session's own claude reaches its endpoint through (curl's and Node's names, either case).
	public static readonly Regex ProxyName = new(@"^(HTTPS?|ALL|FTP)_PROXY$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	// A proxy written with no scheme whose host has a sign-in before it (user:pass@proxy:3128, token@proxy:3128, :pass@proxy:3128).
	public static readonly Regex BareUserinfo = new(@"^\s*(?:[^/@\s:]+(?::[^/@\s]*)?|:[^/@\s]+)@[^/@\s]", RegexOptions.CultureInvariant);
	// Any tool's proxy variable, any name ending in proxy; NO_PROXY is a list of hosts, not a proxy.
	public static readonly Regex AnyProxyName = new(@"^(?!no_proxy$).*proxy$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	// Each cloud flag Claude Code reads, and the prefix of the variables that cloud signs in with.
	public static readonly IReadOnlyList<(string Flag, string Prefix)> CloudSignIn =
		[("CLAUDE_CODE_USE_BEDROCK", "AWS_"), ("CLAUDE_CODE_USE_VERTEX", "GOOGLE_"), ("CLAUDE_CODE_USE_FOUNDRY", "AZURE_")];
	// The values of a CLAUDE_CODE_USE_* flag that turn its cloud on.
	public static readonly IReadOnlyList<string> FlagOn = ["1", "true", "yes", "on"];
	// git settings for the session, through GIT_CONFIG_COUNT: no credential helper, every push to GitHub sent to an address that
	// doesn't resolve, and no automatic gc.
	public const string NoPush = "https://push.invalid/";
	public static readonly IReadOnlyList<(string Key, string Value)> SessionGitConfig =
	[
		("credential.helper", ""),
		("credential.https://github.com.helper", ""),
		($"url.{NoPush}.pushInsteadOf", "https://github.com/"),
		($"url.{NoPush}.pushInsteadOf", "git@github.com:"),
		($"url.{NoPush}.pushInsteadOf", "ssh://git@github.com/"),
		("gc.auto", "0"),
	];

	// The installs known to be a tool's own: a version manager's version folder in home, read whole, and Homebrew on Linux, of whose
	// prefix only the formulae's own folders are read (its etc and var hold the services' configs and data).
	public static readonly Regex VersionFolders = new(
		@"\A(?:\.nvm/versions/node/[^/]+|\.asdf/installs/[^/]+/[^/]+|\.local/share/mise/installs/[^/]+/[^/]+"
		+ @"|\.volta/tools/image/[^/]+/[^/]+|(?:\.local/share/fnm|\.fnm)/node-versions/[^/]+/installation"
		+ @"|\.local/share/pnpm/nodejs/[^/]+)\z", RegexOptions.CultureInvariant);
	public static readonly IReadOnlyList<string> HomebrewPrefixes = ["/home/linuxbrew/.linuxbrew"];
	public static readonly IReadOnlyList<string> HomebrewParts = ["bin", "sbin", "Cellar", "opt", "lib", "libexec", "share", "include", "etc/gitconfig"];

	// A URL with a sign-in in it: userinfo after any scheme; a bare user is no secret, but over http(s) or ftp(s) the user may be the
	// token. A scheme takes no dot here, so url.https://<token>@host/.insteadOf, a key naming a URL, is read as https.
	public static readonly Regex UrlUserinfo = new(@"\b([a-z][a-z0-9+-]*)://([^/@\s]+)@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	// The last part of a git config key that holds a password or a token as its value.
	public static readonly Regex SecretKey = new(
		@"\.(?:[a-z0-9-]*(?:token|secret|password|passwd|passphrase|apikey)"
		+ @"|(?:[a-z0-9-]*-)?(?:smtp)?pass|[a-z0-9-]*(?:api|secret|access)-?key)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	// What a value that names a variable starts with: $NAME, ${NAME} or $1, bare or in double quotes, or a command substitution that
	// fetches it from a secret store or a file. It holds no token: it is read when the command runs.
	public const string NamesAVariable = @"""?(?:\$\{?\w|(?:\$\(|`)\s*(?:pass|gopass|op|bw|lpass|gpg|secret-tool|security|vault|keyring"
		+ @"|cat)\b)";
	// A token's own shape, wherever in a value it is, unless it names a variable (NamesAVariable). Only these known forms are found.
	public static readonly Regex TokenShape = new(
		@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_\w{20,}|glpat-[\w-]{20,}|xox[abprs]-[\w-]{10,}"
		+ @"|sk-[\w-]{20,}|AKIA[0-9A-Z]{16})"
		+ @"|(?i:\bauthorization:\s*(?!(?:\w+\s+)?" + NamesAVariable + @")[^\s""']"
		+ @"|\bbearer\s+(?!" + NamesAVariable + @")\S{8,}"
		+ @"|\bsshpass\s+-p\s*(?!" + NamesAVariable + @")\S)", RegexOptions.CultureInvariant);
	// A credential helper that holds the password itself or names a file it is kept in.
	public static readonly Regex HelperSecret = new(
		@"password\s*=\s*(?!" + NamesAVariable + @")[""']?[^\s""';]"
		+ @"|--pass(?:word)?(?:=|\s+)(?!" + NamesAVariable + @")\S|--file(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	// (exit code, stdout, stderr) of a command; 127 when it can't start, 124 when it runs past two minutes. The program is found in
	// PATH's absolute folders only, as Gh.Which finds gh.
	public static Ran RunQuiet(IReadOnlyList<string> command, string? cwd = null, IReadOnlyDictionary<string, string>? env = null)
	{
		Process? run;
		try
		{
			var program = command[0].Contains('/') ? command[0] : Gh.Which(command[0], env is null ? Environment.GetEnvironmentVariable("PATH") : env.GetValueOrDefault("PATH"));
			var start = new ProcessStartInfo(program)
			{
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};
			if (cwd is not null)
				start.WorkingDirectory = cwd;
			if (env is not null)
			{
				start.Environment.Clear();
				foreach (var (name, value) in env)
					start.Environment[name] = value;
			}
			foreach (var arg in command.Skip(1))
				start.ArgumentList.Add(arg);
			run = Process.Start(start)!;
		}
		catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
		{
			return new Ran(127, "", error.Message);
		}
		using (run)
		{
			run.StandardInput.Close();
			var err = run.StandardError.ReadToEndAsync();
			var output = run.StandardOutput.ReadToEndAsync();
			if (!run.WaitForExit(TimeSpan.FromSeconds(120)))
			{
				run.Kill(entireProcessTree: true);
				run.WaitForExit();
				return new Ran(124, "", $"{command[0]} didn't answer in 120 s");
			}
			run.WaitForExit();
			return new Ran(run.ExitCode, output.Result, err.Result);
		}
	}

	// path with a leading ~ (alone, or before a /) replaced by home; ~user and anything else as it is.
	public static string ExpandHome(string path, string home) => path == "~" || path.StartsWith("~/", StringComparison.Ordinal) ? home + path[1..] : path;

	// All the session writes: its temporary folder, the run folder it stages its findings in among it. Not the worktree, the clone's git
	// folder (its review ledgers among it) or the main checkout's .security record: the round records the review, not the session.
	public static List<string> SessionWrites(string tmp) => [tmp];

	// All the session's Bash may read of the person's data, each as given and where its symlinks lead: the worktree, the clone's git
	// folder (git there needs it), the main checkout's .security record and its own temporary folder.
	public static List<string> SessionReads(string worktree, string common, string security, string tmp) =>
		[.. new[] { worktree, common, security, tmp }.SelectMany(path => BothForms(path)).Distinct()];

	// What to deny of root so the sandbox reads only allowed (and the folders on the way to them) there: root itself when nothing
	// allowed is in it, else every entry of each folder on the way that is neither allowed nor on the way to one. Each entry is named as
	// listed. A listing, so an entry made after it is readable (UnlistedSince says which). Throws IOException for a folder on the way
	// that can't be listed and UnnameableError for an entry to deny whose name holds a GlobCharacters character or isn't UTF-8 anywhere
	// in its path. On purpose, a name holding U+FFFD counts as not UTF-8 too: .NET lists a name whose bytes aren't UTF-8 with U+FFFD in
	// their place, so a rule naming it would name another path.
	public static List<string> DeniedAround(string root, IEnumerable<string> allowed, Func<string, IEnumerable<string>>? listdir = null)
	{
		listdir ??= ListDir;
		var allow = allowed.Select(PyLib.NormPath).ToHashSet(StringComparer.Ordinal);

		bool Holds(string folder) => allow.Any(path => path == folder || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal));

		List<string> Walk(string folder)
		{
			if (allow.Contains(folder))
				return [];
			if (!Holds(folder))
				return Deny(folder);
			List<string> names;
			try
			{
				names = [.. listdir(folder).Order(PyLib.CodePoints)];
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				throw new IOException($"can't list {folder}, which is on the way to the session's folders, so what beside them to hide from it"
					+ $" can't be told ({error.Message}): make it listable, or move the clone, then resume");
			}
			return [.. names.SelectMany(name => Walk(PyLib.Join(folder, name)))];
		}

		return Walk(PyLib.NormPath(root));
	}

	static string? Unnameable(string name)
	{
		if (!IsUtf8(name) || name.Contains('\uFFFD'))
			return "isn't UTF-8, so the sandbox can't name it";
		if (name.Any(GlobCharacters.Contains))
			return $"holds one of {string.Concat(GlobCharacters)}, which the sandbox may read as a pattern";
		return null;
	}

	static List<string> Deny(string path)
	{
		var parts = path.Split('/');
		for (var depth = 2; depth <= parts.Length; depth++)
		{
			var why = Unnameable(parts[depth - 1]);
			if (why is null)
				continue;
			var named = string.Join('/', parts[..depth]) is { Length: > 0 } joined ? joined : "/";
			if (named == path)
				throw new UnnameableError($"{PyLib.Repr(path)} must be hidden from the session, but its name {why}: rename or move it, then resume");
			throw new UnnameableError($"{PyLib.Repr(path)} must be hidden from the session, but the folder it is in, {PyLib.Repr(named)}, {why}:"
				+ " rename that folder, or move the clone out of it, then resume");
		}
		return [path];
	}

	static bool IsUtf8(string text)
	{
		try
		{
			_ = new UTF8Encoding(false, true).GetByteCount(text);
			return true;
		}
		catch (EncoderFallbackException)
		{
			return false;
		}
	}

	// The names in folder, as os.listdir lists them.
	static IEnumerable<string> ListDir(string folder) => [.. Directory.EnumerateFileSystemEntries(folder).Select(entry => Path.GetFileName(entry))];

	// What of the install around the binary real to read whole, or [] (ToolReads): a version folder of VersionFolders in one of homes
	// with real in its bin, or a Homebrew prefix's HomebrewParts, never its etc or var, with real in <prefix>/Cellar/<formula>/<version>/.
	// real is a real path, so each prefix is matched, and read, in both its forms. prefixes and realpath stand in for HomebrewPrefixes and
	// the filesystem's own in a test.
	public static List<string> OwnInstall(string real, IEnumerable<string> homes, IEnumerable<string>? prefixes = null, Func<string, string>? realpath = null)
	{
		var homeList = homes.ToList();
		foreach (var brew in (prefixes ?? HomebrewPrefixes).Concat(homeList.Select(home => PyLib.Join(home, ".linuxbrew"))))
		{
			var forms = BothForms(brew, realpath);
			if (forms.Any(form => Regex.IsMatch(real, @"\A" + Regex.Escape(form) + @"/Cellar/[^/]+/[^/]+/.+\z", RegexOptions.CultureInvariant)))
				return [.. forms.SelectMany(form => HomebrewParts.Select(part => PyLib.Join(form, part)))];
		}
		var prefix = PyLib.Dirname(PyLib.Dirname(real));
		if (PyLib.Basename(PyLib.Dirname(real)) != "bin")
			return [];
		return [.. homeList.Where(home => prefix.StartsWith(home.TrimEnd('/') + "/", StringComparison.Ordinal)
			&& VersionFolders.IsMatch(prefix[(home.TrimEnd('/').Length + 1)..])).Select(_ => prefix).Take(1)];
	}

	// (what the session's Bash reads for SessionCommands, the folders to put first on its PATH). Each command's own file as PATH finds
	// it and as it really is (node's process.execPath, git's binary, and git's --exec-path helpers), so a shim or a link isn't what runs
	// in the sandbox; and, only in a layout known to be the tool's own (OwnInstall), its install around the real binary. Anywhere else the
	// files alone. A command PATH finds by a relative path is left out, as is anything shared (home, a folder above it, ~/.local).
	// commands, prefixes and realpath stand in for SessionCommands, HomebrewPrefixes and the filesystem's own in a test.
	public static (List<string> Reads, List<string> First) ToolReads(string home, Func<string, string?>? which = null, Func<IReadOnlyList<string>, Ran>? run = null,
		IEnumerable<string>? commands = null, IEnumerable<string>? prefixes = null, Func<string, string>? realpath = null)
	{
		which ??= PyLib.Which;
		run ??= command => RunQuiet(command);
		realpath ??= PyLib.RealPath;
		var homes = BothForms(PyLib.NormPath(home), realpath);

		bool Shared(string folder) => homes.Any(h => folder == h || h.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal) || folder == PyLib.Join(h, ".local"));

		string? Asked(IReadOnlyList<string> command)
		{
			var (code, output, _) = run(command);
			var answer = code == 0 ? PyLib.Strip(output) : "";
			return PyLib.IsAbs(answer) ? realpath(answer) : null;
		}

		List<string?> reads = [];
		List<string> first = [];
		foreach (var name in commands ?? SessionCommands)
		{
			var found = which(name);
			if (string.IsNullOrEmpty(found) || !PyLib.IsAbs(found))
				continue;
			var real = realpath(found);
			if (name == "node")
				real = Asked([found, "-p", "process.execPath"]) ?? real;
			reads.AddRange([found, real]);
			if (name == "git")
				reads.Add(Asked([real, "--exec-path"]));
			var folder = PyLib.Dirname(real);
			var install = OwnInstall(real, homes, prefixes, realpath);
			if (install.Count > 0)
			{
				// An install of the command's own: its folder goes first on PATH, so the shim found before it doesn't run.
				reads.AddRange(install);
				first.Add(folder);
			}
		}
		return ([.. reads.Distinct().Where(path => !string.IsNullOrEmpty(path) && !Shared(path!)).Select(path => path!)], [.. first.Distinct()]);
	}

	// What the session's Bash is denied of /, besides the credentials: every entry beside the way to reads, SessionCommands' files
	// (ToolReads) and SystemReads (DeniedAround), then of SystemDenied every entry beside the way to reads, each where its symlinks lead
	// too. tools is ToolReads' answer when the caller has it already, so the tools aren't run again.
	public static List<string> ReadDenials(IEnumerable<string> reads, IEnumerable<string> credentials, string home, Func<string, IEnumerable<string>>? listdir = null,
		Func<string, string?>? which = null, (List<string> Reads, List<string> First)? tools = null)
	{
		var readList = reads.ToList();
		var toolReads = (tools ?? ToolReads(home, which)).Reads;
		var system = SystemReads.SelectMany(path => BothForms(path));
		var walked = DeniedAround("/", [.. readList, .. system, .. toolReads], listdir);
		var held = SystemDenied.SelectMany(path => BothForms(path)).SelectMany(form => DeniedAround(form, [.. readList, .. toolReads], listdir));
		var credentialSet = credentials.ToHashSet(StringComparer.Ordinal);
		return [.. walked.Concat(held).Distinct().Where(path => !credentialSet.Contains(path))];
	}

	// What ReadDenials gives now that denials, taken when the session started, lacks: entries made beside the way to its folders while it
	// ran, which it could read. Throws as ReadDenials does.
	public static List<string> UnlistedSince(IEnumerable<string> denials, IEnumerable<string> reads, IEnumerable<string> credentials, string home,
		Func<string, IEnumerable<string>>? listdir = null, Func<string, string?>? which = null, (List<string> Reads, List<string> First)? tools = null)
	{
		var seen = denials.ToHashSet(StringComparer.Ordinal);
		return [.. ReadDenials(reads, credentials, home, listdir, which, tools).Where(path => !seen.Contains(path))];
	}

	// path as an absolute path with no trailing slash: a leading ~ or ~/ is home, and a relative one is taken from rollout.sh's folder.
	// null for an empty or blank one.
	public static string? Absolute(string? path, string home)
	{
		path = PyLib.Strip(path ?? "");
		if (path.Length == 0)
			return null;
		return PyLib.NormPath(PyLib.AbsPath(ExpandHome(path, home)));
	}

	// path and, when a symlink is on the way, where it leads: a rule naming one form only may miss the other.
	public static List<string> BothForms(string path, Func<string, string>? realpath = null) =>
		[.. new[] { path, (realpath ?? PyLib.RealPath)(path) }.Distinct().Order(PyLib.CodePoints)];

	// Claude Code's config folder when CLAUDE_CONFIG_DIR moves it, as an absolute path, or null.
	public static string? ClaudeConfigDir(IReadOnlyDictionary<string, string> environ, string home) => Absolute(environ.GetValueOrDefault("CLAUDE_CONFIG_DIR"), home);

	// Where CredentialVars move a credential to, and XDG_CONFIG_HOME's credential folders, each as an absolute path.
	public static List<string> RelocatedCredentials(IReadOnlyDictionary<string, string> environ, string home)
	{
		List<string?> found = [];
		foreach (var name in CredentialVars)
			found.AddRange(environ.GetValueOrDefault(name, "").Split(':').Select(part => Absolute(part, home)));
		if (Absolute(environ.GetValueOrDefault("XDG_CONFIG_HOME"), home) is { } config)
			found.AddRange(XdgConfigSubfolders.Select(folder => PyLib.Join(config, folder)));
		return [.. found.OfType<string>()];
	}

	// (SessionReads, the credentials, home) for SessionSettings and UnlistedSince: the credentials are CredentialPaths, Claude Code's
	// CLAUDE_CONFIG_DIR and where environ's CredentialVars move one, each as an absolute path and where its symlinks lead. configDir null
	// reads CLAUDE_CONFIG_DIR from environ; given, it is taken in its place ("" for none).
	public static (List<string> Reads, List<string> Credentials, string Home) ReadInputs(string worktree, string common, string tmp, string? home = null,
		string? configDir = null, IReadOnlyDictionary<string, string>? environ = null)
	{
		home ??= PyLib.ExpandUser("~");
		environ ??= Environ();
		configDir = configDir is null ? ClaudeConfigDir(environ, home) : Absolute(configDir, home);
		List<string> moved = configDir is null ? [] : [configDir];
		moved.AddRange(RelocatedCredentials(environ, home));
		List<string> credentials = [.. CredentialPaths.Select(path => ExpandHome(path, home)).Concat(moved).SelectMany(path => BothForms(path)).Distinct()];
		var security = PyLib.Join(PyLib.Dirname(common), ".security");
		return (SessionReads(worktree, common, security, tmp), credentials, home);
	}

	// The process's environment, as os.environ holds it.
	public static Dictionary<string, string> Environ() =>
		Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "", StringComparer.Ordinal);

	// The session's settings, which the round writes to a 0600 file beside its log that --settings names (never its command line, which
	// any local user can read): dontAsk permissions that allow only SessionTools, reads of the worktree, the main checkout's .security and
	// tmp, and edits in tmp alone, and deny the credentials, the Factory, ~/.claude and every edit in the worktree; and a sandbox for its
	// Bash that can't be turned off, refuses to start without its sandbox, reads nothing of / but SessionReads, SystemReads and
	// SessionCommands' own files (ReadDenials) and none of the credentials, writes only SessionWrites, and reaches only SandboxDomains
	// (none). The main checkout is the git common folder's parent, as reapply.sh has it.
	public static JsonObject SessionSettings(string worktree, string common, string gitdir, string tmp, string? home = null, string? configDir = null,
		IReadOnlyDictionary<string, string>? environ = null, Func<string, IEnumerable<string>>? listdir = null, Func<string, string?>? which = null,
		(List<string> Reads, List<string> First)? tools = null)
	{
		var (reads, credentials, resolvedHome) = ReadInputs(worktree, common, tmp, home, configDir, environ);
		var security = PyLib.Join(PyLib.Dirname(common), ".security");
		string[] readOnly = [worktree, gitdir];
		var denials = ReadDenials(reads, credentials, resolvedHome, listdir, which, tools);
		return new JsonObject
		{
			["permissions"] = new JsonObject
			{
				["allow"] = Strings([.. SessionTools, $"Read(/{worktree}/**)", $"Read(/{security}/**)", $"Read(/{tmp}/**)", $"Edit(/{tmp}/**)"]),
				["deny"] = Strings([
					.. DeniedTools,
					.. credentials.Select(path => $"Read(/{path})"), .. credentials.Select(path => $"Read(/{path}/**)"),
					.. readOnly.Select(path => $"Edit(/{path})"), .. readOnly.Select(path => $"Edit(/{path}/**)"),
				]),
			},
			["sandbox"] = new JsonObject
			{
				["enabled"] = true,
				["failIfUnavailable"] = true,
				["allowUnsandboxedCommands"] = false,
				["excludedCommands"] = new JsonArray(),
				["filesystem"] = new JsonObject
				{
					["allowWrite"] = Strings(SessionWrites(tmp)),
					["denyWrite"] = Strings(readOnly),
					["denyRead"] = Strings([.. credentials, .. denials]),
				},
				["network"] = new JsonObject { ["allowedDomains"] = Strings(SandboxDomains) },
			},
		};
	}

	static JsonArray Strings(IEnumerable<string> items) => [.. items.Select(item => (JsonNode?)item)];

	// The headless review's argv: its prompt carries the skill and names run, the folder it stages in; it loads no settings file
	// (--setting-sources ""), no MCP server and only SessionBuiltins; and it gets the sandbox and permissions from settings, the path of
	// the file the round wrote, and the reviewer agent from agents, as json.dumps writes it.
	public static List<string> ReviewCommand(string repo, string worktree, IReadOnlyList<string> owned, string settings, JsonNode agents, string skill, string run)
	{
		var values = new Dictionary<string, string>
		{
			["worktree"] = worktree,
			["repo"] = repo,
			["owned"] = owned.Count > 0 ? string.Join(", ", owned) : "none",
			["skill"] = skill,
			["run"] = run,
			["skill_dir"] = PyLib.Join(PyLib.Join(PyLib.Join(worktree, ".claude"), "skills"), "pre-push-review"),
		};
		var prompt = Regex.Replace(ReviewPrompt, @"\{(\w+)\}", found => values[found.Groups[1].Value]);
		return ["claude", "-p", prompt, "--permission-mode", "dontAsk", "--settings", settings,
			"--setting-sources", "", "--strict-mcp-config", "--tools", string.Join(',', SessionBuiltins),
			"--agents", PyLib.JsonDumps(agents), "--disallowedTools", .. DeniedTools];
	}

	// Whether text holds a URL with a sign-in in it (UrlUserinfo).
	public static bool HasSignIn(string text) =>
		UrlUserinfo.Matches(text).Any(found => found.Groups[2].Value.Contains(':') || found.Groups[1].Value.ToLowerInvariant() is "http" or "https" or "ftp" or "ftps");

	// text with every URL's userinfo replaced by ***, so a key naming a URL (url.<base>.insteadOf) prints no token.
	public static string Redacted(string text) => UrlUserinfo.Replace(text, found => $"{found.Groups[1].Value}://***@");

	// The prefixes of the clouds Claude Code signs in through (CLAUDE_CODE_USE_* on), whose variables it needs.
	public static List<string> KeptClouds(IReadOnlyDictionary<string, string> environ) =>
		[.. CloudSignIn.Where(cloud => FlagOn.Contains(PyLib.Strip(environ.GetValueOrDefault(cloud.Flag, "")).ToLowerInvariant())).Select(cloud => cloud.Prefix)];

	// Whether the variable name=value holds a URL with a sign-in (HasSignIn), or is a proxy (ProxyName) written with no scheme whose host
	// has one before it (BareUserinfo), as curl and git read it.
	public static bool HoldsSignIn(string name, string value) => HasSignIn(value) || (ProxyName.IsMatch(name) && BareUserinfo.IsMatch(value));

	// Every git config file in the clone's git folder common, which the session's Bash reads whole: the config and config.worktree of
	// every git folder in it, where one exists, in the walk's order, each level sorted by name: common's own, then each linked
	// worktree's (worktrees/<name>) followed by its own submodules' (worktrees/<name>/modules), then the main checkout's submodules'
	// (modules) and nested ones. Throws ConfigUnreadable when a folder on the way can't be listed; one removed while it is walked is
	// skipped. scan lists a folder's entries (os.walk's scandir), in a test its own.
	public static List<string> ConfigFiles(string common, Func<string, IEnumerable<FileSystemInfo>>? scan = null)
	{
		scan ??= folder => new DirectoryInfo(folder).EnumerateFileSystemInfos();
		List<string> files = [];

		void Walk(string folder)
		{
			List<FileSystemInfo> entries;
			try
			{
				entries = [.. scan(folder)];
			}
			catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException)
			{
				return;
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				throw new ConfigUnreadable($"can't list {folder}, so what of the clone's git config the session could read can't be told ({PyLib.Strerror(error)}): make it listable");
			}
			// os.walk's split: a link to a folder is a folder, but it isn't walked into.
			var subfolders = entries.Where(entry => entry is DirectoryInfo).ToList();
			var names = entries.Except(subfolders).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
			List<string> next;
			if (folder == common || names.Contains("HEAD"))
			{
				// A git folder: its own config, then only its worktrees' and its submodules' git folders.
				files.AddRange(new[] { "config", "config.worktree" }.Select(name => PyLib.Join(folder, name)));
				next = [.. new[] { "worktrees", "modules" }.Where(name => subfolders.Any(entry => entry.Name == name))];
			}
			else
				next = [.. subfolders.Select(entry => entry.Name).Order(PyLib.CodePoints)];
			foreach (var name in next)
			{
				var path = PyLib.Join(folder, name);
				if (subfolders.First(entry => entry.Name == name).LinkTarget is null)
					Walk(path);
			}
		}

		Walk(common);
		return [.. files.Where(File.Exists)];
	}

	// Where the clone's git folder, which the session's Bash reads whole, holds a sign-in secret: every key of every config file in it
	// (ConfigFiles), include files followed, whose key or value holds a URL with a sign-in (HasSignIn), whose value holds a token's own
	// shape (TokenShape), whose name is a password's or a token's (SecretKey), an http extraheader, or a credential helper holding a
	// password or naming a file (HelperSecret); and a file in common named for credentials. Each named by its key (redacted) and the file
	// that holds it, never its value. Throws ConfigUnreadable when common or a folder under it on the way to a git folder can't be listed,
	// or git can't read a config file that is still there.
	public static List<string> ConfigSecrets(string worktree, string common, Func<IReadOnlyList<string>, Ran>? run = null, Func<string, IEnumerable<FileSystemInfo>>? scan = null)
	{
		run ??= command => RunQuiet(command);
		List<string> names;
		try
		{
			names = [.. ListDir(common).Order(PyLib.CodePoints)];
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new ConfigUnreadable($"can't list the clone's git folder {common}, so whether it holds a sign-in secret can't be told ({error.Message}): make it listable");
		}
		List<string> found = [.. names.Where(name => name.Contains("credential", StringComparison.OrdinalIgnoreCase))
			.Select(name => $"{PyLib.Join(common, name)} (a file named for credentials)")];
		foreach (var path in ConfigFiles(common, scan))
		{
			var (code, output, err) = run(["git", "-C", worktree, "config", "--file", path, "--includes", "--show-origin", "--null", "--list"]);
			if (code != 0 && !File.Exists(path))
				continue; // removed since ConfigFiles listed it (a worktree pruned): nothing in it to read
			if (code != 0)
				throw new ConfigUnreadable($"git can't read {path} ({(Redacted(PyLib.Strip(err)) is { Length: > 0 } why ? why : $"exit {code}")}): repair it (git"
					+ $" config --file {PyLib.ShQuote(path)} --list shows the error)");
			// --show-origin --null: the file, then key\nvalue, each ended by a NUL, so the empty field after the last NUL pairs with nothing
			// and is dropped.
			var fields = output.Split('\0');
			for (var i = 0; i + 1 < fields.Length; i += 2)
			{
				var (origin, entry) = (fields[i], fields[i + 1]);
				var cut = entry.IndexOf('\n');
				var (key, value) = cut < 0 ? (entry, "") : (entry[..cut], entry[(cut + 1)..]);
				var name = key.ToLowerInvariant();
				var helper = name.StartsWith("credential", StringComparison.Ordinal) && name.EndsWith(".helper", StringComparison.Ordinal);
				var proxy = name.EndsWith(".proxy", StringComparison.Ordinal) && BareUserinfo.IsMatch(value); // http.proxy, remote.<name>.proxy
				if (name.EndsWith(".extraheader", StringComparison.Ordinal) || HasSignIn(key) || HasSignIn(value) || proxy || TokenShape.IsMatch(value)
						|| (SecretKey.IsMatch(name) && PyLib.Strip(value).Length > 0) || (helper && HelperSecret.IsMatch(value)))
				{
					var heldIn = origin.StartsWith("file:", StringComparison.Ordinal) ? origin["file:".Length..] : path;
					found.Add($"{Redacted(key)} in {heldIn}");
				}
			}
		}
		return [.. found.Distinct()];
	}

	// Remove the temporary folders killed runs left in sessionDir: rollout-session.* folders of the user's own (a symlink is left, and
	// where it leads). The lock lets one run at a time and a run makes one session at a time, so none of them is live. owner and uid
	// stand in for lstat's st_uid and getuid in a test.
	public static void SweepSessions(string sessionDir, Func<string, uint?>? owner = null, uint? uid = null)
	{
		owner ??= PyLib.Owner;
		uid ??= PyLib.Uid();
		foreach (var name in ListDir(sessionDir).Order(PyLib.CodePoints))
		{
			var path = PyLib.Join(sessionDir, name);
			if (name.StartsWith("rollout-session.", StringComparison.Ordinal) && owner(path) == uid)
				PyLib.RemoveTree(path);
		}
	}

	// Remove the settings files killed runs left beside their logs in logDir: review-*.settings.json entries of the user's own (a symlink
	// is removed, never where it leads). The lock lets one run at a time, so none is in use.
	public static void SweepSettings(string logDir, Func<string, uint?>? owner = null, uint? uid = null)
	{
		owner ??= PyLib.Owner;
		uid ??= PyLib.Uid();
		foreach (var name in ListDir(logDir).Order(PyLib.CodePoints))
		{
			var path = PyLib.Join(logDir, name);
			try
			{
				// os.remove: a link is removed, never where it leads, and a folder is left.
				if (name.StartsWith("review-", StringComparison.Ordinal) && name.EndsWith(".settings.json", StringComparison.Ordinal) && owner(path) == uid
						&& (PyLib.IsLink(path) || !Directory.Exists(path)))
					File.Delete(path);
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	// Make the log folder, or check the one there: a real folder of the user's own, which is then set to 0700. Throws IOException naming
	// what is wrong (a symlink, another owner's folder), since an existing folder is left as it is.
	public static void OwnLogDir(string logDir, uint? uid = null)
	{
		if (OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("rollout.sh --auto runs on Linux only");
		if (PyLib.Dirname(logDir) is { Length: > 0 } parent)
			Directory.CreateDirectory(parent);
		if (!Directory.Exists(logDir))
			Directory.CreateDirectory(logDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		if (PyLib.IsLink(logDir) || !Directory.Exists(logDir) || PyLib.Owner(logDir) != (uid ?? PyLib.Uid()))
			throw new IOException($"{logDir} isn't a folder of the user's own");
		File.SetUnixFileMode(logDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
	}

	// The review session's environment: baseEnv without GATE_SKIP_REVIEW (the gate's review check must run in it), the names SecretName
	// matches, a value holding a URL with a sign-in (HoldsSignIn, and a proxy variable's written with no scheme, any tool's: AnyProxyName),
	// Claude Code's own but ClaudeKept (CLAUDE_CONFIG_DIR made absolute, as the settings deny it), and the cloud in use's secrets; gh
	// pointed at an empty config and git at an empty global one; and SessionGitConfig. GIT_OPTIONAL_LOCKS is off (git status writes no
	// index back). The caches and temporary folders the tools it runs may write are moved into tmp, its own folder. first goes before
	// PATH: the folders SessionCommands really run from (ToolReads).
	public static Dictionary<string, string> SessionEnv(IReadOnlyDictionary<string, string> baseEnv, string tmp, IEnumerable<string>? first = null)
	{
		var clouds = KeptClouds(baseEnv);

		bool Kept(string name, string value)
		{
			if (name == "GATE_SKIP_REVIEW" || HoldsSignIn(name, value))
				return false; // a URL with a sign-in (a password, or a user that may be a token), whatever its name, or a proxy's
			if (AnyProxyName.IsMatch(name) && BareUserinfo.IsMatch(value))
				return false; // another tool's proxy, written with no scheme and a sign-in
			if (ClaudeSignIn.IsMatch(name))
				return ClaudeKept.Contains(name);
			if (clouds.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
				return !SignInSecret.IsMatch(name);
			return !SecretName.IsMatch(name);
		}

		var env = baseEnv.Where(pair => Kept(pair.Key, pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
		var firstList = first?.ToList() ?? [];
		if (firstList.Count > 0)
			env["PATH"] = string.Join(':', [.. firstList, env.GetValueOrDefault("PATH") is { Length: > 0 } path ? path : PyLib.DefPath]);
		(string Name, string Folder)[] folders =
		[
			("GH_CONFIG_DIR", "gh"), ("TMPDIR", "tmp"), ("NUGET_PACKAGES", "nuget-packages"), ("NUGET_HTTP_CACHE_PATH", "nuget-http"),
			("npm_config_store_dir", "pnpm-store"), ("pnpm_config_store_dir", "pnpm-store"), ("XDG_CACHE_HOME", "cache"), ("DOTNET_CLI_HOME", "dotnet-home"),
		];
		foreach (var (name, folder) in folders)
		{
			env[name] = PyLib.Join(tmp, folder);
			Directory.CreateDirectory(env[name]);
		}
		if (ClaudeConfigDir(baseEnv, PyLib.ExpandUser("~")) is { } configDir)
			env["CLAUDE_CONFIG_DIR"] = configDir;
		env["GIT_CONFIG_GLOBAL"] = PyLib.Join(tmp, "gitconfig"); // the person's ~/.gitconfig is kept from the session
		File.WriteAllBytes(env["GIT_CONFIG_GLOBAL"], []);
		env["DOTNET_NOLOGO"] = env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
		env["GIT_OPTIONAL_LOCKS"] = "0"; // git status reads the index without writing it back: the worktree is read-only
		env["GIT_CONFIG_COUNT"] = SessionGitConfig.Count.ToString(CultureInfo.InvariantCulture);
		for (var index = 0; index < SessionGitConfig.Count; index++)
			(env[$"GIT_CONFIG_KEY_{index}"], env[$"GIT_CONFIG_VALUE_{index}"]) = SessionGitConfig[index];
		return env;
	}
}

// Python's readings of a path and of the system, which the ported round keeps: os.path's normpath, join, dirname and realpath,
// os.path.expanduser, shutil.which, str.strip, sorted() of str (by code point), repr() of a str, shlex.quote and json.dumps.
static class PyLib
{
	public const string DefPath = "/bin:/usr/bin"; // os.defpath

	// sorted()'s order of str: by code point, where .NET's ordinal order is by UTF-16 unit.
	public static readonly IComparer<string> CodePoints = Comparer<string>.Create((a, b) =>
	{
		var (one, two) = (a.EnumerateRunes().GetEnumerator(), b.EnumerateRunes().GetEnumerator());
		while (true)
		{
			var (more, others) = (one.MoveNext(), two.MoveNext());
			if (!more || !others)
				return more.CompareTo(others);
			if (one.Current.Value != two.Current.Value)
				return one.Current.Value.CompareTo(two.Current.Value);
		}
	});

	public static bool IsAbs(string path) => path.StartsWith('/');

	// os.path.join of two parts.
	public static string Join(string a, string b) => IsAbs(b) || a.Length == 0 ? b : a.EndsWith('/') ? a + b : a + "/" + b;

	// os.path.dirname: everything before the last /, its trailing slashes dropped unless it is slashes alone.
	public static string Dirname(string path)
	{
		var head = path[..(path.LastIndexOf('/') + 1)];
		return head.Length > 0 && head.Trim('/').Length > 0 ? head.TrimEnd('/') : head;
	}

	public static string Basename(string path) => path[(path.LastIndexOf('/') + 1)..];

	// os.path.normpath: . and empty parts dropped, .. folded, and a leading // kept (POSIX leaves its meaning to the system).
	public static string NormPath(string path)
	{
		if (path.Length == 0)
			return ".";
		var slashes = path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal) ? "//" : IsAbs(path) ? "/" : "";
		List<string> parts = [];
		foreach (var part in path.Split('/'))
		{
			if (part is "" or ".")
				continue;
			if (part != ".." || (slashes.Length == 0 && parts.Count == 0) || (parts.Count > 0 && parts[^1] == ".."))
				parts.Add(part);
			else if (parts.Count > 0)
				parts.RemoveAt(parts.Count - 1);
		}
		var joined = slashes + string.Join('/', parts);
		return joined.Length > 0 ? joined : ".";
	}

	public static string AbsPath(string path) => NormPath(IsAbs(path) ? path : Join(Environment.CurrentDirectory, path));

	// os.path.realpath, not strict: each symlink on the way resolved, the parts past one that doesn't exist kept as they are, and a
	// loop left unresolved where it starts.
	public static string RealPath(string path)
	{
		var rest = new Stack<string>(AbsPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
		var resolved = "/";
		var hops = 0;
		while (rest.TryPop(out var part))
		{
			if (part == ".")
				continue;
			if (part == "..")
			{
				resolved = Dirname(resolved);
				continue;
			}
			var next = Join(resolved, part);
			var target = LinkTarget(next);
			if (target is null || ++hops > 40)
			{
				resolved = next;
				continue;
			}
			if (IsAbs(target))
				resolved = "/";
			foreach (var piece in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
				rest.Push(piece);
		}
		return resolved;
	}

	static string? LinkTarget(string path)
	{
		try
		{
			return new FileInfo(path).LinkTarget;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	public static bool IsLink(string path) => LinkTarget(path) is not null;

	// os.path.expanduser of ~ and ~/...: HOME, its trailing slashes dropped.
	public static string ExpandUser(string path)
	{
		if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal))
			return path;
		var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var expanded = home.TrimEnd('/') + path[1..];
		return expanded.Length > 0 ? expanded : "/";
	}

	// shutil.which: the first executable file called name in a folder of PATH, as given (a relative folder gives a relative path), or
	// name itself when it holds a /.
	public static string? Which(string name)
	{
		static bool Runs(string candidate) =>
			File.Exists(candidate) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);

		if (name.Contains('/'))
			return Runs(name) ? name : null;
		foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? DefPath).Split(':').Distinct())
		{
			var candidate = Join(folder, name);
			if (Runs(candidate))
				return candidate;
		}
		return null;
	}

	// str.strip(): Python's whitespace, which takes in \x1c to \x1f as well as .NET's.
	public static string Strip(string text)
	{
		static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

		var (start, end) = (0, text.Length);
		while (end > 0 && IsSpace(text[end - 1]))
			end--;
		while (start < end && IsSpace(text[start]))
			start++;
		return text[start..end];
	}

	// What an OSError's strerror says for a listing that failed.
	public static string Strerror(Exception error) => error switch
	{
		UnauthorizedAccessException => "Permission denied",
		DirectoryNotFoundException => "No such file or directory",
		_ => error.Message,
	};

	// repr() of a str: in single quotes, or double ones when it holds a ' and no ", with \\, the quote, \t \n \r and the code points
	// str.isprintable() refuses (a control, a lone surrogate, a format character, a separator other than the space) escaped.
	public static string Repr(string text)
	{
		var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
		var output = new StringBuilder().Append(quote);
		for (var i = 0; i < text.Length; i++)
		{
			int point = text[i];
			if (char.IsSurrogatePair(text, i))
				point = char.ConvertToUtf32(text[i], text[++i]);
			var printable = point is < 0xd800 or > 0xdfff && CharUnicodeInfo.GetUnicodeCategory(point) is not (UnicodeCategory.Control or UnicodeCategory.Format
				or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
				or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);
			output.Append(point switch
			{
				_ when point == quote || point == '\\' => $"\\{(char)point}",
				'\t' => @"\t",
				'\n' => @"\n",
				'\r' => @"\r",
				< 0x20 or 0x7f => string.Create(CultureInfo.InvariantCulture, $@"\x{point:x2}"),
				_ when point < 0x7f || printable => char.ConvertFromUtf32(point),
				<= 0xff => string.Create(CultureInfo.InvariantCulture, $@"\x{point:x2}"),
				<= 0xffff => string.Create(CultureInfo.InvariantCulture, $@"\u{point:x4}"),
				_ => string.Create(CultureInfo.InvariantCulture, $@"\U{point:x8}"),
			});
		}
		return output.Append(quote).ToString();
	}

	// shlex.quote: as it is when it holds only characters no shell reads, else in single quotes.
	public static string ShQuote(string text) =>
		text.Length == 0 ? "''" : Regex.IsMatch(text, @"[^A-Za-z0-9_@%+=:,./-]", RegexOptions.CultureInvariant) ? "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'" : text;

	// json.dumps with its defaults: ", " and ": " between the parts, and every character past ASCII escaped.
	public static string JsonDumps(JsonNode? node) => node switch
	{
		null => "null",
		JsonObject o => "{" + string.Join(", ", o.Select(pair => $"{JsonString(pair.Key)}: {JsonDumps(pair.Value)}")) + "}",
		JsonArray a => "[" + string.Join(", ", a.Select(JsonDumps)) + "]",
		JsonValue v when v.TryGetValue<string>(out var s) => JsonString(s),
		JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
		_ => node.ToJsonString(),
	};

	static string JsonString(string text)
	{
		var output = new StringBuilder("\"");
		foreach (var c in text)
			output.Append(c switch
			{
				'"' => "\\\"",
				'\\' => @"\\",
				'\n' => @"\n",
				'\r' => @"\r",
				'\t' => @"\t",
				'\b' => @"\b",
				'\f' => @"\f",
				< ' ' or > '~' => string.Create(CultureInfo.InvariantCulture, $@"\u{(int)c:x4}"),
				_ => c.ToString(),
			});
		return output.Append('"').ToString();
	}

	// rmtree(path, ignore_errors=True) of a real folder: everything in it removed that can be, links removed and never followed. A
	// link or a file at path is left, as rmtree refuses them.
	public static void RemoveTree(string path)
	{
		if (IsLink(path) || !Directory.Exists(path))
			return;
		try
		{
			foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos().ToList())
			{
				try
				{
					if (entry is DirectoryInfo && entry.LinkTarget is null)
						RemoveTree(entry.FullName);
					else
						entry.Delete();
				}
				catch (Exception error) when (error is IOException or UnauthorizedAccessException)
				{
				}
			}
			Directory.Delete(path);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
		}
	}

	// lstat's st_uid, or null when path can't be lstat'ed (gone). The BCL has no call for a file's owner, so this is the runtime's own
	// lstat (libSystem.Native, which System.IO calls), whose FileStatus starts with Flags, Mode, Uid and Gid, four 32-bit fields, on every
	// platform. The buffer is larger than the struct, so a field the runtime adds at its end can't overrun it.
	public static uint? Owner(string path)
	{
		var status = new byte[512];
		return SystemNativeLStat(path, status) == 0 ? BitConverter.ToUInt32(status, 8) : null;
	}

	// os.getuid: the real user's id.
	public static uint Uid() => getuid();

	[DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", SetLastError = true, CharSet = CharSet.Ansi)]
	static extern int SystemNativeLStat(string path, byte[] status);

	[DllImport("libc", EntryPoint = "getuid")]
	static extern uint getuid();
}
