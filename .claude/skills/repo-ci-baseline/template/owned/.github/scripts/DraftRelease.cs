// Print the body to publish a draft Release with, or fail if the draft names another PR: draft_release.cs's logic, a port of
// draft_release.py (ADR 0017 in the Application Factory). The contract is draft_release.cs's header.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Baseline;

// The draft names a PR other than the one being released.
sealed class OtherPrError(string message) : Exception(message);

static class DraftRelease
{
	// The usage line argparse printed for draft_release.py.
	const string Usage = "[-h] --pr PR --notes-file NOTES_FILE [--tag TAG]";

	const string Marker = "Source PR: #";

	// Every PR number a "Source PR: #n" marker in the body names, in order: Python's re.findall(r"Source PR: #(\d+)"), whose \d is any
	// Unicode decimal digit, an astral one too, each read by int() as its value.
	public static List<BigInteger> NamedPrs(string? body)
	{
		body ??= "";
		List<BigInteger> numbers = [];
		for (var at = body.IndexOf(Marker, StringComparison.Ordinal); at >= 0; at = body.IndexOf(Marker, at, StringComparison.Ordinal))
		{
			at += Marker.Length;
			var end = at;
			while (end < body.Length && Rune.TryGetRuneAt(body, end, out var rune) && Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
				end += rune.Utf16SequenceLength;
			if (end > at)
				numbers.Add(Py.Int(body[at..end]) ?? throw new InvalidOperationException($"int() refused the digits {Py.Repr(body[at..end])}"));
			at = end;
		}
		return numbers;
	}

	// The body to publish the draft with. Throws OtherPrError if it names another PR.
	public static string PublishBody(string? body, BigInteger prNumber, string notes)
	{
		body ??= "";
		var others = NamedPrs(body).Where(number => number != prNumber).ToList();
		if (others.Count > 0)
			throw new OtherPrError($"names PR #{Str(others[0])}, not #{Str(prNumber)}");
		// Python's ^ and $ with re.MULTILINE, which .NET's Multiline matches: a line starts at the text's start or after a \n, and ends at
		// a \n or the text's end.
		var ownLine = new Regex($@"^[ \t]*{Regex.Escape(Marker + Str(prNumber))}[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
		if (ownLine.IsMatch(body))
			return body;
		return body.Length > 0 ? $"{body}\n\n{notes}\n" : $"{notes}\n";
	}

	// str() of an int: ASCII digits, and a - when it's negative.
	static string Str(BigInteger number) => number.ToString(CultureInfo.InvariantCulture);

	// The script, reading the draft's body from stdin (a test hands in its own).
	public static int Run(IEnumerable<string> args, Func<string> stdin, TextWriter stdout, TextWriter stderr)
	{
		var parser = new Arguments("draft_release.cs", Usage, "Print the body to publish a draft Release with, or fail if the draft names another PR.")
				.IntOption("--pr", "the PR being released", required: true)
				.Required("--notes-file", "the release notes to append")
				.Option("--tag", "the tag, for the error message");
		if (parser.Parse(args, stdout, stderr) is int exit)
			return exit;

		// open(encoding="utf-8") as Python reads it: strict UTF-8 (a byte order mark kept as U+FEFF), and each \r\n and lone \r read as \n.
		var notes = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(parser.Value("--notes-file", "")))
				.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
		var pr = parser.IntValue("--pr", 0);
		try
		{
			Script.Write(stdout, PublishBody(stdin(), pr, notes));
		}
		catch (OtherPrError error)
		{
			Script.Write(stderr, $"::error::The draft Release {parser.Value("--tag", "the draft")} {error.Message}. Fix or delete the draft, then re-run.\n");
			return 1;
		}
		return 0;
	}
}
