// A guard that reads `git status --porcelain` in an Owned shell script names its untracked-files setting: print each call that doesn't and exit 1.
// check-git-status.cs is the entry file; tests/check-git-status.sh runs it.
//
// Exit 0: none found. Exit 1: found, printed as file:line:text. Exit 2: the folder is missing, the folder or a file in it could not be read, or the
// scan itself failed (Checker.Run: nothing was fully checked).
// Without --untracked-files=<mode> or -u[<mode>] (=all, or =no when ignoring them is the point), a user's status.showUntrackedFiles=no hides an untracked file from the
// guard, so a "clean tree" check passes on a dirty one. A script's tests, Markdown and Sandcastle's TypeScript are not scanned: a test sets its own repo's config, and
// Sandcastle runs `git status` in a fresh sandbox clone. Scanned: *.sh and the three git hooks, in any folder below the one given except a `tests` folder, each folder's
// files and then its folders, each in code point order.
//
// A file is read as UTF-8, a byte that isn't replaced by U+FFFD, and \r\n and \r read as \n, as Python's text mode read it. A logical line is a line plus the lines a
// trailing backslash joins to it (not when the line ends in a comment: bash ends a comment at the newline). Only a `git` that can run
// is a call: not one in a comment, in a single-quoted string or in the text of a double-quoted one, but yes one in a `$(...)` or backticks inside a double-quoted string; `git`,
// a backslash-escaped or quoted `git` (`"git"`, `'git'`) is the same command. A call is the words after `git` up to the first unquoted `|`, `;`, `&` or word-starting `#`, or the `)` (or backtick) that closes the
// context the call is in. A substitution inside the call (`$(...)`, `(...)`, backticks, quoted or not) is part of it but blanked, since its words are not the call's: so a `-u` of
// `sort -u` or `git add -u` after the call, or in a substitution, or text in a trailing comment, is not the call's setting. The call's text is split into shell words (quotes, including $'...' and $"...", removed,
// a backslash removed where the shell reads it as an escape, a quoted argument kept whole, see SplitWords); the call is checked when one of its words is `status` and one starts with `--porcelain`, and git's global
// options (-C <dir>, --no-pager, --git-dir=<dir>) come between as ordinary words. Whitespace is Python's str.isspace(), and \w a letter, a number or _, as in the
// Python this was ported from (check-git-status.py), an astral one too (Checker.FoldAstral).
//
// Limits, all on the safe side: quote state is kept per logical line, so a heredoc or multi-line string is scanned as code, and a line that ends inside an open quote (an apostrophe in
// heredoc text, say) is checked again with its quotes ignored, which can flag text that only names a call. A command held in a variable (`"$GIT" status --porcelain`) is not seen.
// A FIFO named *.sh blocks the read, as it did in Python.
using System.Text;
using System.Text.RegularExpressions;

static class GitStatusGuards
{
	const string Usage = "usage: check-git-status.cs <the Template's owned/ folder, which must exist>";

	static readonly HashSet<string> Hooks = ["pre-commit", "pre-push", "post-checkout"];

	static readonly Regex Setting = new(@"^(--untracked-files(=.*)?|-u(all|no|normal)?)$", RegexOptions.CultureInvariant);

	// Python's \s is str.isspace(), and its \w a letter, a number or _.
	const string Space = @"[\s\x1c-\x1f]";

	static readonly Regex Git = new(@"(?<![\p{L}\p{N}_-])(?:""git""|'git'|\\?git)(?=" + Space + ")", RegexOptions.CultureInvariant);

	static readonly Regex OpenEnd = new(@"[|;&)#]", RegexOptions.CultureInvariant);

	static readonly UTF8Encoding Replacing = new(false, false);

	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (args.Length != 1 || args[0].Length == 0 || !Directory.Exists(args[0]))
		{
			stderr.Write(Usage + "\n");
			return 2;
		}
		var (found, error) = Scan(args[0]);
		if (error is not null)
		{
			stderr.Write(error + "\n");
			return 2;
		}
		foreach (var line in found)
			stdout.Write(line + "\n");
		if (found.Count > 0)
		{
			stderr.Write("Give each git status --porcelain above --untracked-files=all (or =no on purpose), so status.showUntrackedFiles can't weaken the check.\n");
			return 1;
		}
		return 0;
	}

	// The file:line:text of each unguarded call, or an error message: the first folder it could not list (the rest is still scanned, and
	// nothing found is printed), or the first file it could not read (which ends the scan).
	public static (List<string> Found, string? Error) Scan(string root)
	{
		var found = new List<string>();
		var failures = new List<string>();
		var error = Walk(root, found, failures);
		return (found, error ?? failures.FirstOrDefault());
	}

	// os.walk, top-down: a folder's files (in code point order), then its folders but tests (in code point order); a symlink to a folder is
	// neither a file nor walked. A path is the folder given joined to the names below it, as os.path.join joined them.
	static string? Walk(string folder, List<string> found, List<string> failures)
	{
		List<FileSystemInfo> entries;
		try
		{
			entries = [.. new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, MatchType = MatchType.Simple })];
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			failures.Add($"could not list {folder}");
			return null;
		}
		var files = entries.Where(e => e is not DirectoryInfo).Select(e => e.Name).ToList();
		var folders = entries.Where(e => e is DirectoryInfo && e.Name != "tests").ToList();
		files.Sort(Checker.CompareCodePoints);
		folders.Sort((a, b) => Checker.CompareCodePoints(a.Name, b.Name));
		foreach (var name in files)
		{
			if (!(name.EndsWith(".sh", StringComparison.Ordinal) || Hooks.Contains(name)))
				continue;
			var path = Join(folder, name);
			string text;
			try
			{
				text = Replacing.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n").Replace('\r', '\n');
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				return $"could not read {path}";
			}
			foreach (var (number, line) in LogicalLines(text))
			{
				if (!Checker.LStrip(line).StartsWith('#') && Unguarded(Checker.FoldAstral(line)))
					found.Add($"{path}:{number}:{Checker.Strip(line)}");
			}
		}
		foreach (var sub in folders)
		{
			if (sub.LinkTarget is null && Walk(Join(folder, sub.Name), found, failures) is { } error)
				return error;
		}
		return null;
	}

	static string Join(string folder, string name) => folder.EndsWith('/') ? folder + name : folder + "/" + name;

	// (number of the first line, joined text).
	public static IEnumerable<(int Number, string Line)> LogicalLines(string text)
	{
		var buf = "";
		var start = 0;
		var number = 0;
		foreach (var line in text.Split('\n'))
		{
			number++;
			if (buf.Length == 0)
				start = number;
			var trimmed = Checker.RStrip(line);
			if (trimmed.EndsWith('\\') && !HasComment(buf + line))
			{
				buf += trimmed[..^1] + " ";
				continue;
			}
			yield return (start, buf + line);
			buf = "";
		}
		if (buf.Length > 0)
			yield return (start, buf);
	}

	enum Context { Sq, Ansi, Dq, Bt, Sub }

	enum Event { None, Close, Sep, Comment }

	// Step the context stack over line[i]: return (width of what was read, event). A stack of contexts (single quote, double quote, substitution, backtick) says where a character
	// is; a substitution inside double quotes opens a code context again. The event is Close for a ) or, when inBt, a backtick that closes the context the scan started in,
	// Sep for an unquoted | ; or & outside any context, Comment for an unquoted # that starts a word outside any context (start is where the scan began), else None.
	static (int, Event) Advance(List<Context> stack, string line, int i, int start, bool inBt = false)
	{
		var c = line[i];
		var next = i + 1 < line.Length ? line[i + 1] : '\0';
		Context? top = stack.Count > 0 ? stack[^1] : null;
		switch (top)
		{
			case Context.Sq:
				if (c == '\'')
					stack.RemoveAt(stack.Count - 1);
				break;
			case Context.Ansi: // $'...': a backslash escapes the next character, so \' does not end it
				if (c == '\\')
					return (2, Event.None);
				if (c == '\'')
					stack.RemoveAt(stack.Count - 1);
				break;
			case Context.Dq:
				if (c == '\\')
					return (2, Event.None);
				if (c == '"')
				{
					stack.RemoveAt(stack.Count - 1);
				}
				else if (c == '$' && next == '(')
				{
					stack.Add(Context.Sub);
					return (2, Event.None);
				}
				else if (c == '`')
				{
					stack.Add(Context.Bt);
				}
				break;
			case Context.Bt:
				if (c == '\\')
					return (2, Event.None);
				if (c == '`')
					stack.RemoveAt(stack.Count - 1);
				else if (c is '"' or '\'')
					stack.Add(c == '"' ? Context.Dq : Context.Sq);
				break;
			default: // a code context: the call itself (no stack) or the inside of a substitution
				if (c == '\\')
					return (2, Event.None);
				if (c == '$' && next == '\'')
				{
					stack.Add(Context.Ansi);
					return (2, Event.None);
				}
				if (c == '\'')
				{
					stack.Add(Context.Sq);
				}
				else if (c == '"')
				{
					stack.Add(Context.Dq);
				}
				else if (c == '`')
				{
					if (inBt && top is null)
						return (1, Event.Close);
					stack.Add(Context.Bt);
				}
				else if (c == '$' && next == '(')
				{
					stack.Add(Context.Sub);
					return (2, Event.None);
				}
				else if (c == '(')
				{
					stack.Add(Context.Sub);
				}
				else if (c == ')')
				{
					if (top != Context.Sub)
						return (1, Event.Close);
					stack.RemoveAt(stack.Count - 1);
				}
				else if (stack.Count == 0 && c is '|' or ';' or '&')
				{
					return (1, Event.Sep);
				}
				else if (stack.Count == 0 && c == '#' && (i == start || Checker.IsSpace(line[i - 1])))
				{
					return (1, Event.Comment);
				}
				break;
		}
		return (1, Event.None);
	}

	// Whether line has a comment (an unquoted # that starts a word) outside any quote.
	static bool HasComment(string line)
	{
		var stack = new List<Context>();
		for (var i = 0; i < line.Length;)
		{
			var (width, ev) = Advance(stack, line, i, 0);
			if (ev == Event.Comment)
				return true;
			i += width;
		}
		return false;
	}

	// Whether line, up to its comment if it has one, ends inside a quote: the quote state of the lines after it is then unknown.
	static bool EndsOpen(string line)
	{
		var stack = new List<Context>();
		for (var i = 0; i < line.Length;)
		{
			var (width, ev) = Advance(stack, line, i, 0);
			if (ev == Event.Comment)
				break;
			i += width;
		}
		return stack.Any(c => c is Context.Sq or Context.Dq or Context.Ansi);
	}

	// The positions of `git` that can run: not in a comment, a single-quoted string or the text of a double-quoted one (a $( ... ) in it still runs).
	// Maps each such position to whether it is inside backticks.
	static Dictionary<int, bool> ExecutableStarts(string line)
	{
		var stack = new List<Context>();
		var runs = new Dictionary<int, bool>();
		for (var i = 0; i < line.Length;)
		{
			Context? top = stack.Count > 0 ? stack[^1] : null;
			if (top is null or Context.Sub or Context.Bt)
				runs[i] = top == Context.Bt;
			var (width, ev) = Advance(stack, line, i, 0);
			if (ev == Event.Comment)
				break;
			i += width;
		}
		return runs;
	}

	// The call's own text from pos: up to the first unquoted | ; & or # outside any substitution, or to the ) (or backtick, when the call is inside backticks) that closes the
	// context the call is in. What is inside a substitution ($( ... ), ( ... ) or backticks, quoted or not) is blanked out: it is another command's words, never the call's
	// `status`, `--porcelain` or setting. Quotes stay in the text, for SplitWords.
	static string CallAfter(string line, int pos, bool inBt = false)
	{
		var stack = new List<Context>();
		var kept = new StringBuilder();
		for (var i = pos; i < line.Length;)
		{
			var hidden = stack.Contains(Context.Sub) || stack.Contains(Context.Bt);
			var (width, ev) = Advance(stack, line, i, pos, inBt);
			if (ev != Event.None)
				break;
			var blank = hidden || stack.Contains(Context.Sub) || stack.Contains(Context.Bt);
			// A width of 2 at the line's last character reads one, as Python's slice did.
			var read = Math.Min(width, line.Length - i);
			kept.Append(blank ? new string(' ', width) : line.Substring(i, read));
			i += width;
		}
		return kept.ToString();
	}

	// The shell words of text: the quotes ('...', "...", $'...', $"...") are removed and a quoted argument stays whole, so `-C "a -u b"` is two words and `"--porcelain"` and
	// `$"--porcelain"` are --porcelain. A backslash is removed, and the next character kept, where the shell reads it as an escape: unquoted, before any character inside $'...'
	// (its escapes such as \n are not translated), and before " \ $ or a backtick inside "..." and $"..."; inside '...' it is kept as it is.
	public static List<string> SplitWords(string text)
	{
		var words = new List<string>();
		var word = new StringBuilder();
		char? quote = null; // ' " or A for $'...'
		var inWord = false;
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (quote == '\'')
			{
				if (c == '\'')
					quote = null;
				else
					word.Append(c);
			}
			else if (quote == 'A')
			{
				if (c == '\'')
				{
					quote = null;
				}
				else if (c == '\\' && i + 1 < text.Length)
				{
					i++;
					word.Append(text[i]);
				}
				else
				{
					word.Append(c);
				}
			}
			else if (quote == '"')
			{
				if (c == '"')
				{
					quote = null;
				}
				else if (c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`')
				{
					i++;
					word.Append(text[i]);
				}
				else
				{
					word.Append(c);
				}
			}
			else if (c == '$' && i + 1 < text.Length && text[i + 1] == '"') // $"...": the same as "..." (a translated string)
			{
				(quote, inWord) = ('"', true);
				i++;
			}
			else if (c == '$' && i + 1 < text.Length && text[i + 1] == '\'') // $'...': a backslash escapes the next character
			{
				(quote, inWord) = ('A', true);
				i++;
			}
			else if (c is '\'' or '"')
			{
				(quote, inWord) = (c, true);
			}
			else if (c == '\\' && i + 1 < text.Length)
			{
				i++;
				word.Append(text[i]);
				inWord = true;
			}
			else if (Checker.IsSpace(c))
			{
				if (inWord)
				{
					words.Add(word.ToString());
					(word, inWord) = (new StringBuilder(), false);
				}
			}
			else
			{
				word.Append(c);
				inWord = true;
			}
		}
		if (inWord)
			words.Add(word.ToString());
		return words;
	}

	static bool Bare(List<string> words) =>
			words.Contains("status") && words.Any(w => w.StartsWith("--porcelain", StringComparison.Ordinal)) && !words.Any(Setting.IsMatch);

	public static bool Unguarded(string line)
	{
		var runs = ExecutableStarts(line);
		foreach (Match match in Git.Matches(line))
		{
			if (runs.TryGetValue(match.Index, out var inBt) && Bare(SplitWords(CallAfter(line, match.Index + match.Length, inBt))))
				return true;
		}
		if (EndsOpen(line)) // fail closed: with the quote state unknown, every `git` on the line counts, up to the next | ; & ) or #
		{
			foreach (Match match in Git.Matches(line))
			{
				var rest = line[(match.Index + match.Length)..];
				if (Bare(SplitWords(OpenEnd.Split(rest, 2)[0])))
					return true;
			}
		}
		return false;
	}
}
