// What the two checkers in tests/ share (ADR 0017): Checker.Run, which turns an uncaught exception into exit 2 rather than .NET's abort
// (exit 134), and Python's text rules the ports keep (str.isspace, str.strip, str.splitlines and the code point order of sorted()).
using System.Globalization;
using System.Text;

static class Checker
{
	// The checker's exit code. A crash is a message on stderr and exit 2, "nothing was fully checked": exit 1 means "found", so a broken
	// scan must not read as a finding, and exit 0 as a pass.
	public static int Run(string name, Func<TextWriter, TextWriter, int> main) =>
			Run(name, main, new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true },
					new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

	// The same, on the writers given (UTF-8 stdout and stderr above).
	public static int Run(string name, Func<TextWriter, TextWriter, int> main, TextWriter stdout, TextWriter stderr)
	{
		try
		{
			return main(stdout, stderr);
		}
		catch (Exception error)
		{
			stderr.Write($"{name} failed without checking: {error.GetType().Name}: {error.Message}\n");
			return 2;
		}
		finally
		{
			stdout.Flush();
		}
	}

	// Python's str.isspace() for one character: .NET's char.IsWhiteSpace leaves out the separators U+001C to U+001F.
	public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

	// Python's str.strip(), str.lstrip() and str.rstrip(), with its whitespace.
	public static string Strip(string text) => RStrip(LStrip(text));

	public static string LStrip(string text)
	{
		var start = 0;
		while (start < text.Length && IsSpace(text[start]))
			start++;
		return text[start..];
	}

	public static string RStrip(string text)
	{
		var end = text.Length;
		while (end > 0 && IsSpace(text[end - 1]))
			end--;
		return text[..end];
	}

	// Python's str.splitlines(): a line ends at \n, \r, \r\n, \v, \f, \x1c, \x1d, \x1e, U+0085, U+2028 or U+2029, and a last line
	// with no ending is a line too.
	public static List<string> SplitLines(string text)
	{
		var lines = new List<string>();
		var start = 0;
		var i = 0;
		while (i < text.Length)
		{
			var c = text[i];
			if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
			{
				lines.Add(text[start..i]);
				i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
				start = i;
			}
			else
			{
				i++;
			}
		}
		if (start < text.Length)
			lines.Add(text[start..]);
		return lines;
	}

	// Python's order for two strings: by code point. string.CompareOrdinal compares UTF-16 code units, which puts an astral character
	// (a surrogate pair, from U+D800) before U+E000 to U+FFFF.
	public static int CompareCodePoints(string a, string b)
	{
		var x = a.EnumerateRunes().GetEnumerator();
		var y = b.EnumerateRunes().GetEnumerator();
		while (true)
		{
			var more = x.MoveNext();
			var other = y.MoveNext();
			if (!more || !other)
				return more.CompareTo(other);
			var order = x.Current.Value.CompareTo(y.Current.Value);
			if (order != 0)
				return order;
		}
	}

	// Each character outside the Basic Multilingual Plane (a surrogate pair) replaced by one BMP character of the same class for
	// Python's \d and \w: .NET's regex reads UTF-16 code units, so its \d, \p{L} and \p{N} never match an astral digit or letter, which
	// Python's do. A decimal digit becomes 0, any other letter or number a, anything else U+FFFD. The text is only matched, never printed.
	public static string FoldAstral(string text)
	{
		if (!text.Any(char.IsSurrogate))
			return text;
		var folded = new StringBuilder(text.Length);
		foreach (var rune in text.EnumerateRunes())
		{
			if (rune.IsBmp)
				folded.Append((char)rune.Value);
			else
				folded.Append(Rune.GetUnicodeCategory(rune) switch
				{
					UnicodeCategory.DecimalDigitNumber => '0',
					var category when Rune.IsLetter(rune) || category is UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => 'a',
					_ => '\uFFFD',
				});
		}
		return folded.ToString();
	}
}
