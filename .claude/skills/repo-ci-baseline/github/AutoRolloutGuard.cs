// What a review session may not change (auto_rollout.py's session_guard and broken_rules, ADR 0016), as a library of rollout.cs that
// nothing runs yet (ADR 0017's Update for mpaulosky/Application_Factory#157): the Guard read before the session, the Rules it broke read
// after, and the file and tree snapshots both compare. auto_rollout.py is still what runs, and this is kept in step with it until the
// cut-over (mpaulosky/Application_Factory#122). --auto is Linux only, so the reads are glibc's on 64-bit Linux (Posix).
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// What a session may not change couldn't be read before it starts, so it isn't started.
sealed class GuardUnreadable(string message) : Exception(message);

// What one path is, as entry_state reads it: a "dir" and its mode, a "file", its mode and the sha256 of its bytes, a "link" and its
// target (null when it can't be read), an "unread" file's mode, size, inode and mtime, or an "other" (a FIFO, a socket) and its mode.
sealed record Entry(string Kind, uint Mode, string? Detail = null, long Size = 0, ulong Ino = 0, long MtimeNs = 0);

// What a review session may not change, read before it starts (SessionGuard.Read), checked after (SessionGuard.BrokenRules). The
// snapshots have no default, so a guard without them can't be made.
sealed record Guard(string Worktree, string RemoteBranch, Dictionary<string, string?> Template, string Ledgers,
		Dictionary<string, string> LedgersBefore, string Common, string Gitdir, byte[] Link, string Security,
		Dictionary<string, string> SecurityBefore, (string? Head, string? Branch, string? Status) WorktreeState,
		Dictionary<string, Entry?> Files, Dictionary<string, Dictionary<string, Entry?>> GitFiles);

static class SessionGuard
{
	public const string Branch = "chore/reapply-baseline";

	// How many characters of what the session wrote a message shows, and how many changed paths it names.
	public const int ShownChars = 120;
	public const int UnseenShown = 3;

	// The most of a file the session may have written that is read (RecordBytes); a test lowers it, as the pytest monkeypatched it.
	public static int RecordLimit { get; set; } = 16 * 1024 * 1024;

	// A command's stdout, a trailing line break or more dropped, or null when it fails or can't run. The program is found in PATH's
	// absolute folders only (Gh.Which); it reads no stdin and runs for 120 s at most, counting the reads of its output, which a child it
	// leaves behind can hold open: past the limit it gives null.
	public static string? RunText(IReadOnlyList<string> command, string? cwd = null, double timeoutSeconds = 120)
	{
		var start = new ProcessStartInfo
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
		foreach (var arg in command.Skip(1))
			start.ArgumentList.Add(arg);
		Process? run;
		try
		{
			start.FileName = command[0].Contains('/') ? command[0] : Gh.Which(command[0], Environment.GetEnvironmentVariable("PATH"));
			run = Process.Start(start);
		}
		catch (Exception error) when (error is Win32Exception or IOException)
		{
			return null;
		}
		using (run)
		{
			run!.StandardInput.Close();
			var limit = TimeSpan.FromSeconds(timeoutSeconds);
			var clock = Stopwatch.StartNew();
			var output = run.StandardOutput.ReadToEndAsync();
			var err = run.StandardError.ReadToEndAsync();
			if (!run.WaitForExit(limit))
			{
				run.Kill(entireProcessTree: true);
				run.WaitForExit();
				return null;
			}
			run.WaitForExit();
			var left = limit - clock.Elapsed;
			if (!Task.WaitAll([output, err], left > TimeSpan.Zero ? left : TimeSpan.Zero))
				return null;
			return run.ExitCode == 0 ? output.Result.TrimEnd('\n') : null;
		}
	}

	// The sha256 of a file's bytes, or null when it can't be read.
	public static string? FileHash(string path)
	{
		try
		{
			return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	// {path relative to folder: sha256} for every file under folder, as os.walk lists them: a symlink to a folder is neither hashed nor
	// followed, and any other symlink is hashed through.
	public static Dictionary<string, string?> TreeHashes(string folder)
	{
		var hashes = new Dictionary<string, string?>();
		void Walk(string path, string prefix)
		{
			foreach (var name in List(path))
			{
				var child = Path.Combine(path, name);
				var rel = prefix + name;
				if (Posix.LStat(child) is { IsDir: true })
					Walk(child, rel + "/");
				else if (!Directory.Exists(child))
					hashes[rel] = Unreachable(child, name) ? Unseen() : FileHash(child);
			}
		}
		Walk(folder, "");
		return hashes;
	}

	// Whether name, just listed in a folder, can't be reached by path: .NET decodes a name that isn't UTF-8 with U+FFFD, so its path names
	// nothing, where os.walk kept the bytes (surrogateescape). Such a name reads as a change every time (Unseen), so a file the session made
	// or changed under it still breaks a Rule; the round fails closed on a tree that holds one.
	static bool Unreachable(string path, string name) => name.Contains('\uFFFD') && Posix.LStat(path) is null;

	// What a read gives a name it can't reach: different every time, so no two reads match.
	static string Unseen() => $"unseen {Guid.NewGuid():N}";

	// The names in a folder, sorted by code point, every one of them (dot files too); none when it can't be listed.
	static List<string> List(string folder)
	{
		try
		{
			var names = Directory.GetFileSystemEntries(folder, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })
					.Select(path => Path.GetFileName(path)).ToList();
			names.Sort(Py.CodePoints);
			return names;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	// The bytes of a file the session may have written, or null unless it is a regular file of at most limit bytes (RecordLimit by
	// default): it is opened without following a symlink and without waiting (a FIFO), and nothing else (a device, a folder) is read.
	public static byte[]? RecordBytes(string path, int? limit = null)
	{
		var most = limit ?? RecordLimit;
		var fd = Posix.OpenNoFollow(path);
		if (fd < 0)
			return null;
		try
		{
			if (Posix.FStat(fd) is not { IsRegular: true })
				return null;
			var data = Posix.ReadUpTo(fd, most + 1L);
			return data is not null && data.Length <= most ? data : null;
		}
		finally
		{
			Posix.Close(fd);
		}
	}

	// What a file is, to tell whether it changed: the sha256 of the bytes RecordBytes read, or, for one it didn't read (a symlink, a
	// FIFO, one too big), what lstat says of it and where a symlink leads, so swapping one such file for another still reads as a change.
	public static string Identity(string path, byte[]? data)
	{
		if (data is not null)
			return Convert.ToHexStringLower(SHA256.HashData(data));
		if (Posix.LStat(path) is not { } info)
			return "gone";
		var target = info.IsLink ? Posix.ReadLink(path) : "";
		if (target is null)
			return "gone";
		return $"unread {Convert.ToString(info.Mode, 8)} {info.Ino} {info.Size} {info.MtimeNs} {target}";
	}

	// {name: identity} for each file in a folder the session may not write (the clone's review ledgers, the main checkout's .security
	// record), each read by RecordBytes; empty when the folder isn't there or can't be listed.
	public static Dictionary<string, string> FolderState(string folder)
	{
		var state = new Dictionary<string, string>();
		foreach (var name in List(folder))
		{
			var path = Path.Combine(folder, name);
			state[name] = Unreachable(path, name) ? Unseen() : Identity(path, RecordBytes(path));
		}
		return state;
	}

	// Text the session wrote (a finding's id or status, a file name), as a message may print it: quoted, with every control character and
	// anything not ASCII escaped (Python's ascii()), so it can't move the cursor or fake a line of its own, and cut to ShownChars, so it
	// can't flood the terminal.
	public static string Shown(string text) => Cut(PyText.Ascii(text));

	public static string Shown(System.Text.Json.Nodes.JsonNode? value) => Cut(PyText.Ascii(value));

	static string Cut(string text) => text.Length <= ShownChars ? text : $"{text[..ShownChars]}... ({text.Length} characters)";

	// The bytes of the worktree's .git link (the file a linked worktree has in place of a git folder), or null when it isn't a plain
	// file: a symlink, a folder or nothing is never the link git wrote.
	public static byte[]? LinkText(string worktree)
	{
		var path = Path.Combine(worktree, ".git");
		if (Posix.LStat(path) is not { IsRegular: true })
			return null;
		try
		{
			return File.ReadAllBytes(path);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	// The read of the remote branch, before the session and after it: whether the session pushed.
	public static string[] RemoteBranchCommand(string worktree) => ["git", "-C", worktree, "ls-remote", "origin", $"refs/heads/{Branch}"];

	// The reads of what the session leaves of the worktree: its HEAD, its branch and its status (untracked files included). The session
	// reviews only, so all three are as they were before it.
	public static string[][] WorktreeStateCommands(string worktree) =>
	[
		["git", "-C", worktree, "rev-parse", "HEAD"],
		["git", "-C", worktree, "rev-parse", "--verify", "--quiet", $"refs/heads/{Branch}"],
		["git", "-C", worktree, "status", "--porcelain=v1", "-z", "--untracked-files=all"],
	];

	// (HEAD, branch, status) of the worktree, a null in place of a read that failed.
	public static (string?, string?, string?) WorktreeState(string worktree, Func<IReadOnlyList<string>, string?> read)
	{
		var commands = WorktreeStateCommands(worktree);
		return (read(commands[0]), read(commands[1]), read(commands[2]));
	}

	// Read what the session may not change: the worktree's .git link (git obeys the folder it names), the remote branch (the session
	// doesn't push), the Template in owned (its fixes go Template first, by a person), and the clone's review ledgers and the main
	// checkout's .security record (FolderState: the round records, not the session), every path of the worktree (TreeState: content, mode
	// and link target, ignored files included) and the clone's git config, config.worktree, hooks and info (GitFilesState). BrokenRules
	// compares them after the session, and the record reads the worktree again just before it runs. Throws GuardUnreadable when the link,
	// the remote or the clone's git folders can't be read, since a check against nothing would pass whatever happened.
	public static Guard Read(string worktree, string owned, Func<IReadOnlyList<string>, string?>? read = null)
	{
		read ??= command => RunText(command);
		var link = LinkText(worktree) ?? throw new GuardUnreadable($"{worktree}/.git isn't the plain file a linked worktree has (see: ls -la"
				+ $" {PyText.ShQuote(Path.Combine(worktree, ".git"))})");
		var remote = read(RemoteBranchCommand(worktree)) ?? throw new GuardUnreadable($"can't read the remote {Branch} before the session"
				+ $" (the network, or git); see why with: {PyText.ShJoin(RemoteBranchCommand(worktree))}");
		string[][] folders = [.. new[] { "--git-common-dir", "--git-dir" }.Select(which => new[] { "git", "-C", worktree, "rev-parse", "--path-format=absolute", which })];
		var (common, gitdir) = (read(folders[0]), read(folders[1]));
		if (string.IsNullOrEmpty(common) || string.IsNullOrEmpty(gitdir))
		{
			var failed = string.IsNullOrEmpty(common) ? folders[0] : folders[1];
			throw new GuardUnreadable($"can't find the git folders of {worktree} (git 2.31 or later reads them); see why with: {PyText.ShJoin(failed)}");
		}
		var state = WorktreeState(worktree, read);
		string?[] values = [state.Item1, state.Item2, state.Item3];
		if (Array.IndexOf(values, null) is var missing and >= 0)
		{
			// --quiet keeps the read's failure silent; the hint runs it without, so it says why.
			var failed = WorktreeStateCommands(worktree)[missing].Where(part => part != "--quiet");
			throw new GuardUnreadable($"can't read the HEAD, the branch {Branch} or the status of {worktree}; see why with: {PyText.ShJoin(failed)}");
		}
		var ledgers = Path.Combine(common, "review");
		var security = Path.Combine(Path.GetDirectoryName(common)!, ".security");
		return new Guard(worktree, remote, TreeHashes(owned), ledgers, FolderState(ledgers), common, gitdir, link, security,
				FolderState(security), state, TreeState(worktree), GitFilesState(common, gitdir));
	}

	// The Rules a finished session broke, as a list of what happened (empty when it broke none): the worktree's .git link changed (then no
	// git command runs in the worktree, since git would obey whatever folder it now names), the remote branch moved or can't be read, the
	// worktree's HEAD, branch or status changed, the Template changed, an Owned file the branch changes (changes: the Owned list, which
	// throws IOException or InvalidOperationException when it can't be read) is no longer the Template's as it was when the session
	// started, or a file of the ledgers or of the .security record was added, removed or changed (FolderChanges): the session writes
	// neither, so a person's own review on another branch meanwhile stops the round as well. A change in the clone's git config or hooks
	// (GitFilesState) breaks one, and then no git runs; and last, any path of the worktree whose content, mode or link target changed
	// (TreeState: an ignored file git status doesn't show, such as node_modules/.bin/tsx, which the record runs) breaks one too.
	public static List<string> BrokenRules(Guard guard, string owned, Func<string, IReadOnlyList<string>> changes,
			Func<IReadOnlyList<string>, string?>? read = null)
	{
		read ??= command => RunText(command);
		List<string> broke = [];
		var linkKept = LinkText(guard.Worktree) is { } now && now.AsSpan().SequenceEqual(guard.Link);
		var gitNow = GitFilesState(guard.Common, guard.Gitdir);
		// GitFilesState reads the same names each time; one the guard lacks counts as changed.
		var gitChanged = gitNow.Keys.Where(name => !guard.GitFiles.TryGetValue(name, out var before) || !Same(gitNow[name], before)).ToList();
		if (!linkKept)
			broke.Add($"{guard.Worktree}/.git changed during the session: git wasn't run there after it, and the link must be put back"
					+ $" ({PyText.BytesRepr(guard.Link)}) before it is");
		else if (gitChanged.Count > 0)
		{
			broke.Add($"the clone's git {string.Join(", ", gitChanged)} changed during the session (in {guard.Common}): git can run a command"
					+ " they name, so it wasn't run there after the session; see what changed with"
					+ $" ls -la {PyText.ShQuote(Path.Combine(guard.Common, "hooks"))} and cat {PyText.ShQuote(Path.Combine(guard.Common, "config"))}");
			linkKept = false;
		}
		else
		{
			var remote = read(RemoteBranchCommand(guard.Worktree));
			if (remote is null)
				broke.Add($"can't read the remote {Branch} after the session, so whether it was pushed can't be told");
			else if (remote != guard.RemoteBranch)
				broke.Add($"the remote {Branch} moved during the session (the session, or anyone who pushed to it meanwhile)");
			var state = WorktreeState(guard.Worktree, read);
			if (state.Item1 is null || state.Item2 is null || state.Item3 is null)
				broke.Add($"can't read the HEAD, the branch {Branch} or the status of {guard.Worktree} after the session");
			else if (state != guard.WorktreeState)
				broke.Add($"{guard.Worktree} changed during the session (its HEAD, its branch or its files): the session reviews only, so it"
						+ " commits and edits nothing");
		}
		if (!Same(TreeHashes(owned), guard.Template))
			broke.Add("the Template changed during the session (the session, or your own edit in the Factory checkout)");
		if (linkKept)
		{
			IReadOnlyList<string> changed;
			try
			{
				changed = changes(guard.Worktree);
			}
			catch (Exception error) when (error is IOException or InvalidOperationException)
			{
				broke.Add($"can't list the Owned files the branch changes: {error.Message}");
				changed = [];
			}
			foreach (var path in changed)
				if (FileHash(Path.Combine(guard.Worktree, path)) != guard.Template.GetValueOrDefault(path))
					broke.Add($"{path} is an Owned file and no longer the Template's: edit the Template, not the copy");
		}
		broke.AddRange(FolderChanges(guard.Ledgers, guard.LedgersBefore, "the review ledgers"));
		broke.AddRange(FolderChanges(guard.Security, guard.SecurityBefore, "the .security record"));
		// Last, so the files are read as near as can be to the record, which runs them (the record reads them again).
		broke.AddRange(UnseenChanges(guard));
		return broke;
	}

	// The Rule broken when a path of the worktree changed since the guard read it (TreeState: content, mode or link target, so an ignored
	// file git status doesn't show, such as node_modules/.bin/tsx, counts): one rule naming the first UnseenShown of them, relative to the
	// worktree, and how many there are; none when none did.
	public static List<string> UnseenChanges(Guard guard)
	{
		var touched = TreeChanges(guard.Files, TreeState(guard.Worktree));
		if (touched.Count == 0)
			return [];
		var named = string.Join(", ", touched.Take(UnseenShown).Select(Shown));
		var more = touched.Count > UnseenShown ? $" and {touched.Count - UnseenShown} more" : "";
		return [$"{touched.Count} path(s) in {guard.Worktree} changed during the session, which git status may not show (an ignored file,"
				+ $" such as node_modules): {named}{more}. The round's record and the gate on the push run the worktree's files as you, so"
				+ " nothing is run or recorded"];
	}

	// What changed in a folder the session may not write (FolderState before it, and now): every file added, removed or changed. The
	// session's sandbox writes only its own temporary folder, so a change is the person's own review or record on another branch
	// meanwhile, or a sandbox that didn't hold.
	public static List<string> FolderChanges(string folder, Dictionary<string, string> before, string what)
	{
		var after = FolderState(folder);
		List<string> broke = [];
		var names = before.Keys.Union(after.Keys).ToList();
		names.Sort(Py.CodePoints);
		foreach (var name in names)
		{
			var (had, has) = (before.TryGetValue(name, out var was), after.TryGetValue(name, out var now));
			if (had && has && was == now)
				continue;
			var how = !had ? "added" : !has ? "removed" : "changed";
			broke.Add($"{Shown(name)} in {folder} was {how} during the session, which can't write {what}: your own review or record on another"
					+ " branch meanwhile, or a sandbox that didn't hold");
		}
		return broke;
	}

	// What one path is, read without following a symlink, to tell whether it changed: a folder's mode, a regular file's mode and the
	// sha256 of its bytes (read without waiting on it), a symlink's target, of a regular file that can't be read its mode, size, inode and
	// mtime (Unread), and of anything else (a FIFO, a socket) what lstat says of its kind and mode. Times and link counts are left out for
	// all but the unread file, so another project's pnpm install, which hard-links the same store files, changes nothing here; null when
	// the path isn't there.
	public static Entry? EntryState(string path)
	{
		if (Posix.LStat(path) is not { } info)
			return null;
		if (info.IsLink)
			return new Entry("link", 0, Posix.ReadLink(path));
		if (info.IsDir)
			return new Entry("dir", info.Mode);
		if (!info.IsRegular)
			return new Entry("other", info.Mode);
		var fd = Posix.OpenNoFollow(path);
		if (fd < 0)
			return Unread(info);
		try
		{
			using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			var chunk = new byte[1 << 20];
			while (true)
			{
				var read = Posix.Read(fd, chunk);
				if (read < 0)
					return Unread(info);
				if (read == 0)
					break;
				digest.AppendData(chunk, 0, read);
			}
			return new Entry("file", info.Mode, Convert.ToHexStringLower(digest.GetHashAndReset()));
		}
		finally
		{
			Posix.Close(fd);
		}
	}

	// What EntryState says of a regular file it can't read (one only its owner may run, mode 0111, say): its mode, size, inode and mtime,
	// so a same-size file swapped in, which the round's scripts could still run, counts as a change. Only these files are judged by times,
	// so pnpm's hard links elsewhere still change nothing.
	static Entry Unread(Posix.Stat info) => new("unread", info.Mode, null, info.Size, info.Ino, info.MtimeNs);

	// {path relative to root: EntryState} for root and everything under it, symlinks not followed, the names in skip at its top left out
	// (a worktree's .git link has a check of its own; a .git folder there is still walked, as os.walk walked it). A folder that can't be
	// listed is there with its mode, as it was before, and what is in it can't be read by the round's scripts either.
	public static Dictionary<string, Entry?> TreeState(string root) => TreeState(root, [".git"]);

	public static Dictionary<string, Entry?> TreeState(string root, IReadOnlyCollection<string> skip)
	{
		var state = new Dictionary<string, Entry?> { ["."] = EntryState(root) };
		void Walk(string folder, string prefix)
		{
			foreach (var name in List(folder))
			{
				var path = Path.Combine(folder, name);
				if (prefix.Length > 0 || !skip.Contains(name))
					state[prefix + name] = Unreachable(path, name) ? new Entry(Unseen(), 0) : EntryState(path);
				if (Posix.LStat(path) is { IsDir: true })
					Walk(path, prefix + name + "/");
			}
		}
		if (Directory.Exists(root))
			Walk(root, "");
		return state;
	}

	// What git reads in the clone's git folders that could run a command (core.fsmonitor, core.hooksPath, a diff.external or filter driver,
	// the hooks): the shared config and config.worktree, the worktree's own config.worktree, and the hooks and info folders, each as
	// TreeState reads it (a file as its "." alone).
	public static Dictionary<string, Dictionary<string, Entry?>> GitFilesState(string common, string gitdir)
	{
		(string Name, string Path)[] paths =
		[
			("config", Path.Combine(common, "config")),
			("config.worktree", Path.Combine(common, "config.worktree")),
			("worktree config.worktree", Path.Combine(gitdir, "config.worktree")),
			("hooks", Path.Combine(common, "hooks")),
			("info", Path.Combine(common, "info")),
		];
		return paths.ToDictionary(item => item.Name, item => Directory.Exists(item.Path)
				? TreeState(item.Path, [])
				: new Dictionary<string, Entry?> { ["."] = EntryState(item.Path) });
	}

	// The paths whose state differs between two TreeState reads, sorted by code point.
	public static List<string> TreeChanges(Dictionary<string, Entry?> before, Dictionary<string, Entry?> after)
	{
		// As dict.get compares them: a path one read lacks is null there, the same as a path that isn't there.
		var paths = before.Keys.Union(after.Keys).Where(path => !Equals(before.GetValueOrDefault(path), after.GetValueOrDefault(path))).ToList();
		paths.Sort(Py.CodePoints);
		return paths;
	}

	// Whether two reads hold the same keys with the same values.
	public static bool Same<T>(Dictionary<string, T> a, Dictionary<string, T> b) =>
			a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && Equals(pair.Value, other));
}

// Python's text forms that the messages carry: ascii() of a value, repr() of bytes, and shlex's quote() and join().
static class PyText
{
	// ascii(text): repr() with every character past ASCII escaped as \xNN, \uNNNN or \UNNNNNNNN.
	public static string Ascii(string text) => Escape(Py.ReprString(text));

	public static string Ascii(System.Text.Json.Nodes.JsonNode? value) => Escape(Py.Repr(value));

	// A surrogate pair is one code point; a lone surrogate, which a JSON string can hold, is escaped as itself.
	static string Escape(string repr)
	{
		var output = new StringBuilder();
		for (var i = 0; i < repr.Length; i++)
		{
			int point = repr[i];
			if (char.IsSurrogatePair(repr, i))
				point = char.ConvertToUtf32(repr[i], repr[++i]);
			output.Append(point switch
			{
				< 0x80 => ((char)point).ToString(),
				<= 0xff => $@"\x{point:x2}",
				<= 0xffff => $@"\u{point:x4}",
				_ => $@"\U{point:x8}",
			});
		}
		return output.ToString();
	}

	// repr(data) of bytes: b'...' (b"..." when it holds a ' and no "), with \\, the quote, \t \n \r, and every byte outside printable ASCII
	// as \xNN.
	public static string BytesRepr(byte[] data)
	{
		var quote = data.Contains((byte)'\'') && !data.Contains((byte)'"') ? '"' : '\'';
		var output = new StringBuilder("b").Append(quote);
		foreach (var b in data)
			output.Append(b switch
			{
				_ when b == quote || b == '\\' => $"\\{(char)b}",
				(byte)'\t' => @"\t",
				(byte)'\n' => @"\n",
				(byte)'\r' => @"\r",
				< 0x20 or >= 0x7f => $@"\x{b:x2}",
				_ => ((char)b).ToString(),
			});
		return output.Append(quote).ToString();
	}

	static readonly Regex Safe = new(@"\A[a-zA-Z0-9_@%+=:,./-]+\z", RegexOptions.CultureInvariant);

	// shlex.quote(word): as it is when it holds only characters a shell takes literally, else in single quotes, each ' as '"'"'.
	public static string ShQuote(string word) => word.Length == 0 ? "''" : Safe.IsMatch(word) ? word : $"'{word.Replace("'", "'\"'\"'")}'";

	// shlex.join(words).
	public static string ShJoin(IEnumerable<string> words) => string.Join(" ", words.Select(ShQuote));
}

// The file reads the guard needs that the BCL lacks (an lstat with the inode and the mtime in nanoseconds, an open that follows no
// symlink and never waits, a mkdir that fails on a folder already there, access(X_OK)), through libc. statx's layout is the same on
// every Linux architecture; O_NOFOLLOW is the one flag whose value differs between x64 and arm64.
static class Posix
{
	public readonly record struct Stat(uint Mode, ulong Ino, long Size, long MtimeNs)
	{
		public bool IsLink => (Mode & 0xF000) == 0xA000;
		public bool IsDir => (Mode & 0xF000) == 0x4000;
		public bool IsRegular => (Mode & 0xF000) == 0x8000;
	}

	const int AtFdcwd = -100, AtSymlinkNofollow = 0x100, AtEmptyPath = 0x1000;
	const uint StatxBasicStats = 0x7ff;

	// lstat(path), or null when it fails.
	public static Stat? LStat(string path) => StatOf(AtFdcwd, path, AtSymlinkNofollow);

	// fstat(fd), or null when it fails.
	public static Stat? FStat(int fd) => StatOf(fd, "", AtEmptyPath);

	static Stat? StatOf(int dirfd, string path, int flags)
	{
		var buffer = new byte[256];
		if (Statx(dirfd, path, flags, StatxBasicStats, buffer) != 0)
			return null;
		var mtime = BitConverter.ToInt64(buffer, 112) * 1_000_000_000 + BitConverter.ToUInt32(buffer, 120);
		return new Stat(BitConverter.ToUInt16(buffer, 28), BitConverter.ToUInt64(buffer, 32), (long)BitConverter.ToUInt64(buffer, 40), mtime);
	}

	// Where a symlink leads, or null when it can't be read.
	public static string? ReadLink(string path)
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

	static int ONoFollow => RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000;

	// open(path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC): its descriptor, or -1 when it fails.
	public static int OpenNoFollow(string path) => Open(path, ONoFollow | 0x800 | 0x80000);

	// read(fd) into buffer: how many bytes, 0 at the end, -1 on an error.
	public static int Read(int fd, byte[] buffer) => (int)ReadFd(fd, buffer, buffer.Length);

	// Up to limit bytes of fd, or null on an error.
	public static byte[]? ReadUpTo(int fd, long limit)
	{
		using var data = new MemoryStream();
		var chunk = new byte[1 << 16];
		while (data.Length < limit)
		{
			var read = (int)ReadFd(fd, chunk, (int)Math.Min(chunk.Length, limit - data.Length));
			if (read < 0)
				return null;
			if (read == 0)
				break;
			data.Write(chunk, 0, read);
		}
		return data.ToArray();
	}

	public static void Close(int fd) => CloseFd(fd);

	// mkdir(path, mode), or an IOException worded as Python's OSError ("[Errno 17] File exists: '<path>'").
	public static void MakeDirectory(string path, uint mode)
	{
		if (Mkdir(path, mode) == 0)
			return;
		var errno = Marshal.GetLastPInvokeError();
		throw new IOException($"[Errno {errno}] {Marshal.GetPInvokeErrorMessage(errno)}: {Py.ReprString(path)}");
	}

	// access(path, X_OK).
	public static bool Executable(string path) => Access(path, 1) == 0;

	[DllImport("libc", EntryPoint = "statx", SetLastError = true)]
	static extern int Statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buffer);

	[DllImport("libc", EntryPoint = "open", SetLastError = true)]
	static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

	[DllImport("libc", EntryPoint = "read", SetLastError = true)]
	static extern nint ReadFd(int fd, byte[] buffer, nint count);

	[DllImport("libc", EntryPoint = "close", SetLastError = true)]
	static extern int CloseFd(int fd);

	[DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
	static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

	[DllImport("libc", EntryPoint = "access", SetLastError = true)]
	static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
