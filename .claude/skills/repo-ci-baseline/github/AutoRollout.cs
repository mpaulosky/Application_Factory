// auto_rollout.py's logic as a library of rollout.cs, ported a part at a time (ADR 0017's Update for mpaulosky/Application_Factory#157): nothing runs
// it yet, and auto_rollout.py stays what rollout.sh --auto runs until the cut-over. This part is auto_rollout.py's short_name to read_skill: where the
// round's files are (Folders), the lock, the Owned files a re-Apply changes, and the skill's text; AutoRolloutPreflight.cs holds the preflight and the
// sign-in checks. A change to those functions in auto_rollout.py changes this file and its xUnit copy (tests/Scripts.Tests/AutoRollout/) too.
//
// Python's readings are kept where .NET's differ: a line ends where str.splitlines() ends one, space is str.isspace(), a text file is read as
// Python's text mode reads it (strict UTF-8, \r\n and \r as \n), and paths sort by code point, as sorted() sorts them.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// One finished command: what run_quiet returns.
readonly record struct Ran(int Code, string Stdout, string Stderr);

// run_quiet's form, which a test replaces: (exit code, stdout, stderr) of a command run in cwd, with env as its whole environment when given.
delegate Ran Runner(IReadOnlyList<string> command, string? cwd = null, IReadOnlyDictionary<string, string>? env = null);

// The skill's folder and the files of it the round runs and reads, as auto_rollout.py's SKILL_DIR, FACTORY, LAND, REAPPLY, OWNED,
// TEMPLATE_GLOBAL_JSON, FACTORY_GLOBAL_JSON, RETIRED, SKILL_FILE and AGENT_FILE name them. Default is the skill rollout.cs is in.
sealed record Folders(string Skill)
{
	public static Folders Default => FromEntry(AppContext.GetData("EntryPointFileDirectoryPath") as string
			?? throw new InvalidOperationException("Folders.Default needs the script's folder when no script file runs"));

	// The skill whose github/ is entry, with its symlinks resolved first, as SKILL_DIR reads os.path.realpath(__file__): run through the
	// ~/.claude/skills/repo-ci-baseline link, the Factory is still the checkout the link points into, not the folders above the link.
	public static Folders FromEntry(string entry) => new(Path.GetDirectoryName(AutoRollout.RealPath(entry))!);

	// The Factory's checkout: the skill is its .claude/skills/repo-ci-baseline.
	public string Factory => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Skill)!)!)!;
	public string Land => Path.Combine(Skill, "land.sh");
	public string Reapply => Path.Combine(Skill, "reapply.sh");
	public string Owned => Path.Combine(Skill, "template", "owned");
	public string TemplateGlobalJson => Path.Combine(Owned, "global.json");
	// The Factory's own global.json, which land.sh and status.sh run on: they run dotnet from github/ (ADR 0017). None found is "".
	public string FactoryGlobalJson => AutoRollout.NearestGlobalJson(Path.Combine(Skill, "github")) ?? "";
	// The Owned files the Template retired, which Apply deletes (apply.sh reads the same file).
	public string Retired => Path.Combine(Skill, "retired.txt");
	// The Template's own copies of the skill the session runs and of its reviewer agent.
	public string SkillFile => Path.Combine(Owned, ".claude", "skills", "pre-push-review", "SKILL.md");
	public string AgentFile => Path.Combine(Owned, ".claude", "agents", "pre-push-reviewer.md");
}

static partial class AutoRollout
{
	// The lock that lets one round run at a time.
	public static string Lock => Path.Combine(Home, ".cache", "repo-ci-baseline", "rollout-auto.lock");
	// reapply.sh's branch and its worktree's folder (a test holds them equal).
	public const string Branch = "chore/reapply-baseline";
	public const string Worktree = "chore-reapply-baseline";
	// The run folder's file the session stages its findings in (review-ledger.mts's STAGED_FILE; a test holds them equal).
	public const string StagedFile = "findings.json";
	// Beside the kept findings: the HEAD the session reviewed, which the command to record them checks.
	public const string KeptHeadFile = "HEAD";
	// The statuses a review-only round stages: a finding it keeps open, or one it dismissed that isn't a security one.
	public static readonly IReadOnlyList<string> StagedStatuses = ["open", "dismissed"];
	// The agent the session's reviewers run as (agent_definition), recorded as the review's reviewerAgent.
	public const string ReviewerAgent = "pre-push-reviewer";
	// The most the staged findings may hold, a hundred times a large review: the session writes them, so a bigger file isn't read.
	public const int RecordBytes = 16 * 1024 * 1024;
	// What the commands run_in_group starts (reapply.sh, with the gate its push runs, and land.sh) are told, so the gate's build leaves no server
	// running after it: one would keep the round's lock (its descriptor is passed on) after the round ends.
	public static readonly IReadOnlyDictionary<string, string> NoBuildServers = new Dictionary<string, string>
	{
		["MSBUILDDISABLENODEREUSE"] = "1",
		["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
		["UseSharedCompilation"] = "false",
	};

	// What os.path.expanduser("~") gives: HOME, else the user's home folder.
	static string Home => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

	public static string ShortName(string repo) => repo[(repo.LastIndexOf('/') + 1)..];

	// The global.json dotnet reads for a command run in folder: the nearest one in it or a folder above, or null.
	public static string? NearestGlobalJson(string folder)
	{
		while (true)
		{
			var path = Path.Combine(folder, "global.json");
			if (LExists(path))
				return path;
			var parent = Path.GetDirectoryName(folder);
			if (parent is null || parent == folder)
				return null;
			folder = parent;
		}
	}

	// The re-Apply worktree reapply.sh makes for the checkout at path, as reapply.sh names it: beside the primary checkout, which git's common
	// folder names even when path is one of its linked worktrees (symlinks resolved). Where git can't say, the path itself is taken as the
	// primary checkout.
	public static string ReviewWorktree(string path, Func<IReadOnlyList<string>, string?>? read = null)
	{
		var common = (read ?? (command => RunText(command)))(["git", "-C", path, "rev-parse", "--path-format=absolute", "--git-common-dir"]);
		var primary = !string.IsNullOrEmpty(common) ? DirName(RealPath(common)) : RealPath(path);
		return Path.Join(DirName(primary), $"{Path.GetFileName(primary)}-worktrees", Worktree);
	}

	// os.path.dirname: everything before the last slash, trailing slashes dropped unless that is all there is.
	static string DirName(string path)
	{
		var slash = path.LastIndexOf('/') + 1;
		var head = path[..slash];
		return head.Length > 0 && head.Trim('/').Length > 0 ? head.TrimEnd('/') : head;
	}

	// os.path.realpath, non-strict: the absolute path with every symlink resolved as far as the path exists, and . and .. read after the
	// symlinks before them, as the kernel reads them. A loop is left as it is.
	public static string RealPath(string path)
	{
		var full = path.StartsWith('/') ? path : Path.Join(Directory.GetCurrentDirectory(), path);
		return Resolve("/", full, 0);
	}

	static string Resolve(string done, string rest, int depth)
	{
		foreach (var part in rest.Split('/'))
		{
			if (part is "" or ".")
				continue;
			if (part == "..")
			{
				done = done == "/" ? "/" : DirName(done);
				continue;
			}
			var next = done == "/" ? "/" + part : done + "/" + part;
			string? target = null;
			try
			{
				target = new FileInfo(next).LinkTarget;
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			if (target is null || depth >= 40)
			{
				done = next;
				continue;
			}
			done = Resolve(target.StartsWith('/') ? "/" : done, target, depth + 1);
		}
		return done;
	}

	// os.path.lexists: whether anything is at path, a dangling symlink too.
	public static bool LExists(string path)
	{
		try
		{
			return File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	// ── Running a command ───────────────────────────────────────────────────

	// How long run_quiet lets a command run.
	public static readonly TimeSpan QuietTimeout = TimeSpan.FromSeconds(120);

	// (exit code, stdout, stderr) of a command; 127 when it can't start, 124 when it runs past two minutes, killed with what it started. Its
	// stdin is /dev/null, and its output is read as Python's text mode read it (a byte that isn't UTF-8 as U+FFFD, \r\n and \r as \n). The
	// program is found in the absolute folders of the PATH it runs with (env's, when given), as Gh.Which finds gh.
	public static Ran RunQuiet(IReadOnlyList<string> command, string? cwd = null, IReadOnlyDictionary<string, string>? env = null) =>
			RunQuiet(command, QuietTimeout, cwd, env);

	// The same, with a limit of its own.
	public static Ran RunQuiet(IReadOnlyList<string> command, TimeSpan timeout, string? cwd = null, IReadOnlyDictionary<string, string>? env = null)
	{
		Process run;
		try
		{
			var program = command[0].Contains('/') ? command[0]
					: Gh.Which(command[0], env is null ? Environment.GetEnvironmentVariable("PATH") : env.GetValueOrDefault("PATH"));
			var start = new ProcessStartInfo(program)
			{
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				StandardOutputEncoding = new UTF8Encoding(false),
				StandardErrorEncoding = new UTF8Encoding(false),
			};
			if (cwd is not null)
				start.WorkingDirectory = cwd;
			foreach (var arg in command.Skip(1))
				start.ArgumentList.Add(arg);
			if (env is not null)
			{
				start.Environment.Clear();
				foreach (var (name, value) in env)
					start.Environment[name] = value;
			}
			run = Process.Start(start)!;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
		{
			return new Ran(127, "", error.Message);
		}
		using (run)
		{
			run.StandardInput.Close();
			var err = run.StandardError.ReadToEndAsync();
			var output = run.StandardOutput.ReadToEndAsync();
			// One deadline for the exit and both pipes, as subprocess.run's communicate(timeout=) has: a command that exits but leaves a child
			// holding its stdout or stderr open would otherwise keep the reads, and the preflight, waiting past the limit.
			var watch = Stopwatch.StartNew();
			var exited = run.WaitForExit(timeout);
			var left = timeout - watch.Elapsed;
			if (!exited || !Task.WaitAll([err, output], left > TimeSpan.Zero ? left : TimeSpan.Zero))
			{
				try
				{
					run.Kill(entireProcessTree: true);
				}
				catch (InvalidOperationException)
				{
					// It had already exited: only the child holding its pipes is left, which subprocess.run doesn't kill either.
				}
				run.WaitForExit(TimeSpan.FromSeconds(5));
				return new Ran(124, "", $"{command[0]} didn't answer in {Script.G(timeout.TotalSeconds)} s");
			}
			run.WaitForExit();
			return new Ran(run.ExitCode, TextMode(output.Result), TextMode(err.Result));
		}
	}

	// A command's stdout with its line breaks at the end dropped, or null when it fails or can't run.
	public static string? RunText(IReadOnlyList<string> command, string? cwd = null)
	{
		var (code, output, _) = RunQuiet(command, cwd);
		return code == 0 ? output.TrimEnd('\n') : null;
	}

	// Python's universal newlines: \r\n and a lone \r read as \n.
	static string TextMode(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

	// A file's text as open(path, encoding="utf-8").read() gives it: strict UTF-8 (a byte that isn't fails with a DecoderFallbackException),
	// a byte-order mark kept, and universal newlines.
	static string ReadText(string path) => TextMode(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)));

	// ── The lock ────────────────────────────────────────────────────────────

	// What Linux's flock(LOCK_EX | LOCK_NB) fails with when another open file holds the lock: EWOULDBLOCK, which .NET gives as an IOException
	// of its own errno (ADR 0017's Update for mpaulosky/Application_Factory#157).
	const int LockHeld = 11;

	// An exclusive lock for this --auto run, held while the returned stream stays open, or null when another run holds it. A FileStream with
	// FileShare.None is flock(LOCK_EX | LOCK_NB) on the open file, the call auto_rollout.py's fcntl.flock makes, so a C# round and a Python round
	// keep each other out. Throws IOException when it can't be taken at all (an unwritable ~/.cache, a filesystem without locks), and when
	// .NET's file locking is turned off (DOTNET_SYSTEM_IO_DISABLEFILELOCKING, System.IO.DisableFileLocking), where the open would take none.
	public static FileStream? TakeLock(string? path = null)
	{
		path ??= Lock;
		if (FileLockingOff())
			throw new IOException($"can't lock {path}: .NET's file locking is turned off (DOTNET_SYSTEM_IO_DISABLEFILELOCKING or System.IO.DisableFileLocking)");
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		try
		{
			return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
		}
		catch (IOException error) when (error.GetType() == typeof(IOException) && error.HResult == LockHeld)
		{
			return null;
		}
	}

	static bool FileLockingOff() =>
			(AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var off) && off)
			|| Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING") is { } value
			&& (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

	// The descriptor of the lock the running round holds (run sets it), which every child it starts is given: the lock is on the open file, so
	// it is held until the last of them is gone, and a SIGKILL of rollout.sh, which leaves a session or a gate running, doesn't let a second
	// --auto run in beside it. Each child's group is killed when it exits, so what it leaves behind doesn't keep the lock after the round.
	public static readonly List<int> LockFds = [];

	// The lock's descriptors, for each group's posix_spawn_file_actions_adddup2 (pass_fds in auto_rollout.py).
	public static IReadOnlyList<int> HeldLockFds() => [.. LockFds];

	// The descriptor a lock TakeLock returned holds, for LockFds.
	public static int Descriptor(FileStream held) => (int)held.SafeFileHandle.DangerousGetHandle();

	// ── The Owned files and the skill ───────────────────────────────────────

	// The paths retired.txt names (its `<sha256>  <path>` lines), or none when there is no such file.
	public static HashSet<string> RetiredPaths(string? path = null)
	{
		path ??= Folders.Default.Retired;
		string text;
		try
		{
			text = ReadText(path);
		}
		catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
		{
			return [];
		}
		var paths = new HashSet<string>(StringComparer.Ordinal);
		foreach (var line in SplitLines(text, keepEnds: false))
		{
			// Sliced by code point, as Python slices a str.
			var runes = line.TrimEnd('\r').EnumerateRunes().Select(rune => rune.ToString()).ToArray();
			if (PyStrip(line).Length > 0 && !line.StartsWith('#') && string.Concat(runes.Skip(64).Take(2)) == "  ")
				paths.Add(string.Concat(runes.Skip(66)));
		}
		return paths;
	}

	// The Owned files the worktree's branch changes against origin/main, a retired one it deletes among them, for the review's prompt. Throws
	// InvalidOperationException when git's diff fails.
	public static List<string> OwnedChanges(string worktree, Runner? run = null, string? owned = null, IReadOnlySet<string>? retired = null)
	{
		run ??= RunQuiet;
		owned ??= Folders.Default.Owned;
		retired ??= RetiredPaths();
		// --no-renames, as checkReview's own diff: a renamed Owned file is a change to the Owned path too.
		var (code, output, error) = run(["git", "-C", worktree, "diff", "--no-renames", "--name-only", "-z", "origin/main...HEAD"]);
		if (code != 0)
			throw new InvalidOperationException($"git diff in {worktree} failed: {PyStrip(error)}");
		var changed = output.Split('\0').Where(path => path.Length > 0 && (File.Exists(Path.Combine(owned, path)) || retired.Contains(path))).ToList();
		changed.Sort(CodePoints);
		return changed;
	}

	// (front matter, body) of a file that opens with a `---` line and closes it with the next line that is `---` alone, or null when it opens
	// with none. Throws InvalidDataException when it is never closed.
	public static (string Head, string Body)? FrontMatter(string text, string path)
	{
		var lines = SplitLines(text, keepEnds: true);
		if (lines.Count == 0 || lines[0].TrimEnd('\r', '\n') != "---")
			return null;
		for (var index = 1; index < lines.Count; index++)
			if (lines[index].TrimEnd('\r', '\n') == "---")
				return (string.Concat(lines.Skip(1).Take(index - 1)), string.Concat(lines.Skip(index + 1)));
		throw new InvalidDataException($"{path}'s front matter is never closed");
	}

	// The skill's instructions, its front matter left out.
	public static string ReadSkill(string? path = null)
	{
		path ??= Folders.Default.SkillFile;
		var text = ReadText(path);
		return FrontMatter(text, path) is { } parts ? parts.Body.TrimStart('\n') : text;
	}

	// The reviewer agent, for --agents: {name: {description, prompt, tools, model}} from its file's front matter (a flat `key: value` list,
	// with `>-`, `>` or `|` folding the lines indented under it) and body. Throws InvalidDataException when a part is missing.
	public static System.Text.Json.Nodes.JsonObject AgentDefinition(string? path = null)
	{
		path ??= Folders.Default.AgentFile;
		var text = ReadText(path);
		var (head, body) = FrontMatter(text, path) ?? throw new InvalidDataException($"{path} has no front matter");
		Dictionary<string, string> fields = [];
		string? key = null;
		foreach (var line in SplitLines(head.Trim('\n'), keepEnds: false))
		{
			if ((line.StartsWith(' ') || line.StartsWith('\t')) && key is not null)
				fields[key] = PyStrip(fields[key] + " " + PyStrip(line));
			else if (line.Contains(':'))
			{
				var cut = line.IndexOf(':');
				(key, var value) = (PyStrip(line[..cut]), PyStrip(line[(cut + 1)..]));
				fields[key] = value is ">-" or ">" or "|" ? "" : value;
			}
		}
		foreach (var needed in new[] { "name", "description", "tools" })
			if (fields.GetValueOrDefault(needed) is null or "")
				throw new InvalidDataException($"{path} has no {needed}");
		var definition = new System.Text.Json.Nodes.JsonObject
		{
			["description"] = fields["description"],
			["prompt"] = body.TrimStart('\n'),
			["tools"] = new System.Text.Json.Nodes.JsonArray([.. fields["tools"].Split(',').Select(PyStrip).Where(tool => tool.Length > 0)
					.Select(tool => (System.Text.Json.Nodes.JsonNode?)tool)]),
		};
		if (fields.GetValueOrDefault("model") is { Length: > 0 } model)
			definition["model"] = model;
		return new System.Text.Json.Nodes.JsonObject { [fields["name"]] = definition };
	}

	// ── Python's readings ───────────────────────────────────────────────────

	// What str.isspace() calls space, for a regex's character class: Python's \s on a str, which .NET's \s isn't (\x1c to \x1f).
	public const string PySpace = @"\t\n\v\f\r\x1c-\x20\x85\xa0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000";

	static readonly Regex PySpaceRun = new($"^[{PySpace}]+|[{PySpace}]+$");

	// str.strip() with no argument.
	public static string PyStrip(string text) => PySpaceRun.Replace(text, "");

	// str.splitlines(keepends): a line ends at \n, \r\n, \r, \v, \f, \x1c, \x1d, \x1e, U+0085, U+2028 or U+2029.
	public static List<string> SplitLines(string text, bool keepEnds)
	{
		var lines = new List<string>();
		var start = 0;
		for (var index = 0; index < text.Length; index++)
		{
			var c = text[index];
			if (c is not ('\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029'))
				continue;
			var end = index;
			if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
				index++;
			lines.Add(text[start..(keepEnds ? index + 1 : end)]);
			start = index + 1;
		}
		if (start < text.Length)
			lines.Add(text[start..]);
		return lines;
	}

	// A regex that must match the whole text, as Python's fullmatch does.
	public static bool FullMatch(Regex regex, string text) => new Regex($@"\A(?:{regex})\z", regex.Options).IsMatch(text);

	// shlex.quote: a word with only shell-safe characters as it is, any other in single quotes.
	public static string ShellQuote(string word) =>
			word.Length == 0 ? "''" : Regex.IsMatch(word, @"[^A-Za-z0-9_@%+=:,./-]") ? $"'{word.Replace("'", "'\"'\"'")}'" : word;

	// shlex.join.
	public static string ShellJoin(IEnumerable<string> words) => string.Join(" ", words.Select(ShellQuote));

	// sorted()'s order of two strings: by code point, where .NET's ordinal compare reads UTF-16 code units (an astral character after U+FFFF).
	public static readonly Comparison<string> CodePoints = (one, two) =>
	{
		var first = one.EnumerateRunes().GetEnumerator();
		var second = two.EnumerateRunes().GetEnumerator();
		while (true)
		{
			var more = first.MoveNext();
			var other = second.MoveNext();
			if (!more || !other)
				return more.CompareTo(other);
			var order = first.Current.Value.CompareTo(second.Current.Value);
			if (order != 0)
				return order;
		}
	};
}
