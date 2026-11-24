// A bare #N in an Owned file: print each one and exit 1. check-issue-refs.cs is the entry file; tests/check-issue-refs.sh runs it.
//
// Exit 0: none found. Exit 1: found, printed as <path below the folder>:<line>: <the line, stripped>. Exit 2: nothing was (fully) checked: the
// folder is missing, the arguments are wrong, a file or directory is unreadable, or the checker crashed (Checker.Run).
// test.sh runs it, and so does the Factory's scripts/gate.sh (also with --quick), so the rule fails before test.sh does.
// The rule, with its reasons, is the comment in test.sh's "Issue references in Owned files name their repo" step. It reads as
// check-issue-refs.py did, Python's rules kept where .NET's differ: a line ends where str.splitlines() ends one, \d and \w read as
// Python's (an astral digit or letter too, Checker.FoldAstral), a line is stripped of str.isspace() characters, and the files are taken
// in the order sorted() gave pathlib paths, one path segment at a time by code point (so a/x comes before a-b/x).
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

static class IssueRefs
{
	const string Usage = "usage: check-issue-refs.cs <the Template's owned/ folder, which must exist>";

	static readonly Regex Citation = new(@"\((?:[^()]*, )?#\d+\)", RegexOptions.CultureInvariant);

	// Python's \w is a letter, a number or _ (.NET's adds the marks and connector punctuation), and \b after a digit is a non-word
	// character or the end.
	static readonly Regex Bare = new(@"(?<![\p{L}\p{N}_/&])#\d+(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant);

	// Examples, not references: the exact text each one sits in. Not followed by a digit, so "Fixes #42" leaves "Fixes #421" whole.
	static readonly string[] Examples = ["`Fixes #123`", "`Refs #456`", "Fixes #42", "\"1, #3\"", "(\"Fixes #16.\")", "so PR #1 doesn't", "release for #10."];

	static readonly Regex[] ExampleRegexes = [.. Examples.Select(e => new Regex(Regex.Escape(e) + @"(?!\d)", RegexOptions.CultureInvariant))];

	static readonly UTF8Encoding Strict = new(false, true);

	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		// pathlib read an empty argument as the current folder.
		if (args.Length != 1 || !Directory.Exists(args[0].Length == 0 ? "." : args[0]))
		{
			// A folder that isn't there has no files to find a bare #N in, which must not read as a pass.
			stderr.Write(Usage + "\n");
			return 2;
		}
		var root = args[0].Length == 0 ? "." : args[0];
		var files = new List<string[]>();
		if (Walk(root, [], files, stderr) is { } listError)
			return listError;
		files.Sort(CompareSegments);
		var found = new List<string>();
		foreach (var relative in files)
		{
			var path = Path.Combine([root, .. relative]);
			string text;
			try
			{
				// Only a regular file is read: a FIFO would block the open. A name that resolves to nothing (dangling, looping, or through a
				// file) has nothing to check; any other error is a file it didn't check, which must not read as a pass or as a bare #N (exit 1).
				var (kind, errno) = Stat(path);
				if (kind == Kind.Gone || kind == Kind.Other)
					continue;
				if (kind == Kind.Error)
				{
					stderr.Write($"{path}: {Marshal.GetPInvokeErrorMessage(errno)}\n");
					return 2;
				}
				text = Strict.GetString(File.ReadAllBytes(path));
			}
			catch (DecoderFallbackException)
			{
				continue;
			}
			catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
			{
				continue;
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				stderr.Write($"{path}: {error.Message}\n");
				return 2;
			}
			found.AddRange(Check(string.Join('/', relative), text));
		}
		foreach (var line in found)
			stdout.Write(line + "\n");
		return found.Count > 0 ? 1 : 0;
	}

	// The findings in one file's text, at its path below the folder (/-separated): a citation in parentheses anywhere, tests
	// included; elsewhere every #N outside the tests' fixture data, except HTML entities and the exact examples; and in a Sandcastle
	// *.test.mts, whose strings carry fixture data, only on its // comment lines.
	public static IEnumerable<string> Check(string relative, string text)
	{
		var inTests = relative.Split('/').Contains("tests");
		var isTsTest = relative.Split('/')[^1].EndsWith(".test.mts", StringComparison.Ordinal);
		var number = 0;
		foreach (var line in Checker.SplitLines(text))
		{
			number++;
			var folded = Checker.FoldAstral(line);
			var withoutExamples = folded;
			foreach (var example in ExampleRegexes)
				withoutExamples = example.Replace(withoutExamples, "");
			if (Citation.IsMatch(folded)
				|| (Bare.IsMatch(withoutExamples) && !inTests && (!isTsTest || Checker.LStrip(line).StartsWith("//", StringComparison.Ordinal))))
				yield return $"{relative}:{number}: {Checker.Strip(line)}";
		}
	}

	// Every name below folder that isn't a directory, as the segments below the root, as os.walk listed them: a symlink to a directory
	// is neither walked nor a file, and a name whose type can't be read is a file. A directory it can't list is a set of files it didn't
	// check, which must not read as a pass: it ends the run (exit 2) before any file is read.
	static int? Walk(string root, string[] below, List<string[]> files, TextWriter stderr)
	{
		var folder = Path.Combine([root, .. below]);
		List<FileSystemInfo> entries;
		try
		{
			entries = [.. new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, MatchType = MatchType.Simple })];
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			stderr.Write($"could not list {folder}: {error.Message}\n");
			return 2;
		}
		foreach (var entry in entries)
		{
			string[] name = [.. below, entry.Name];
			if (entry is not DirectoryInfo)
				files.Add(name);
			else if (entry.LinkTarget is null && Walk(root, name, files, stderr) is { } error)
				return error;
		}
		return null;
	}

	// sorted() on pathlib paths: segment by segment, each by code point, a path before the longer paths it starts.
	static int CompareSegments(string[] a, string[] b)
	{
		for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
		{
			var order = Checker.CompareCodePoints(a[i], b[i]);
			if (order != 0)
				return order;
		}
		return a.Length.CompareTo(b.Length);
	}

	enum Kind { Regular, Other, Gone, Error }

	// The type of what path names, symlinks followed, as os.stat gave it: a regular file, something else (a directory, a FIFO, a
	// socket, a device), gone (ENOENT, ENOTDIR or ELOOP: dangling, looping or through a file), or an error, with its errno. The BCL
	// has no call that tells a FIFO from a regular file, or ELOOP from ENOENT, so this is the runtime's own stat (libSystem.Native,
	// which System.IO calls): its FileStatus starts with two int32s, Flags and Mode, and Mode's S_IFMT bits are the same on every
	// platform. The buffer is larger than the struct, so a field the runtime adds at its end can't overrun it.
	static (Kind, int) Stat(string path)
	{
		var status = new byte[512];
		if (SystemNativeStat(path, status) != 0)
		{
			var errno = Marshal.GetLastPInvokeError();
			var eloop = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 62 : 40;
			return (errno is 2 or 20 || errno == eloop ? Kind.Gone : Kind.Error, errno);
		}
		var mode = BitConverter.ToInt32(status, 4);
		return ((mode & 0xF000) == 0x8000 ? Kind.Regular : Kind.Other, 0);
	}

	[DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", SetLastError = true, CharSet = CharSet.Ansi)]
	static extern int SystemNativeStat(string path, byte[] status);
}
