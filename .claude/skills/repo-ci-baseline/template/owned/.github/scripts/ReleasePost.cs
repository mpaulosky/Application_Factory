// release_post.py's logic in C# (ADR 0017 in the Application Factory). release_post.cs is its entry file, and its header the contract;
// backfill_blog_posts.cs writes its posts and tables through it, and release_queue.cs reads the Releases with it.
//
// It writes the release blog post for a merged PR (docs/blogs/{merged-date}-pr-{n}-{slug}.md), the docs/blogs/README.md index, the
// RELEASES_START/END table in README.md (copied to docs/README.md with its relative links rebased), and in the GitHub Pages site
// docs/index.html the RELEASES_HTML releases table and the BLOGS_HTML blog post cards. Each method is the Python function of the same name,
// with the same cases. Where Python's reading of text differs from .NET's, the Python's is kept: a length, a cut and ord() count code
// points (Py.Len, Py.Head); \s is str.isspace()'s (PyS); str.lower() maps U+0130 to two code points (PyLower); re.IGNORECASE matches the
// non-ASCII letters Python's does (Unicode); html.unescape() knows HTML5's names (HtmlEntities.cs); a file is read as strict UTF-8 with
// universal newlines and a BOM kept, as Path.read_text() reads it.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baseline;

// The GitHub API calls the post needs.
interface IReleaseGitHub
{
	JsonNode? Pull(long number);

	string PullTitle(BigInteger number);

	List<JsonNode?> Commits(long number);

	List<JsonNode?> Files(long number);

	List<JsonNode?> Releases();
}

// The calls, made through the gh CLI, as release_post.py's GitHub class makes them.
sealed class ReleaseGitHub(string repository) : IReleaseGitHub
{
	public string Repository { get; } = repository;

	string Api(string path, string? jq = null)
	{
		List<string> command = ["gh", "api", $"repos/{Repository}/{path}", "--paginate"];
		if (!string.IsNullOrEmpty(jq))
			command.AddRange(["--jq", jq]);
		// subprocess.run(text=True): strict UTF-8 and universal newlines.
		return ReleasePost.Universal(ReleasePost.Utf8.GetString(Script.CheckOutput(command)));
	}

	// One JSON record per line. Split on newlines only: jq's @json leaves U+2028 and U+2029 unescaped, and splitlines() would cut a record
	// at either one.
	public static List<JsonNode?> Records(string output) => [.. output.Split('\n').Where(line => Py.Strip(line).Length > 0).Select(line => JsonNode.Parse(line))];

	List<JsonNode?> List(string path, string jq = ".[] | @json") => Records(Api(path, jq));

	public JsonNode? Pull(long number) => JsonNode.Parse(Api($"pulls/{number}"));

	public string PullTitle(BigInteger number) => Py.Strip(Api($"pulls/{number}", ".title"));

	public List<JsonNode?> Commits(long number) => List($"pulls/{number}/commits?per_page=100");

	public List<JsonNode?> Files(long number) => List($"pulls/{number}/files?per_page=100");

	public List<JsonNode?> Releases() => List("releases?per_page=100", ".[] | select(.draft | not) | @json");
}

// The request request_summary() sends, and what a stand-in for urlopen() is given: the URL, the headers and the JSON body.
sealed record SummaryRequest(string Url, IReadOnlyDictionary<string, string> Headers, byte[] Data);

// urllib.request.urlopen(request, timeout): the response's body, or an exception.
delegate byte[] UrlOpen(SummaryRequest request, int timeout);

// A row of the releases tables: release_entries()'s dicts.
sealed class ReleaseEntry
{
	public string Tag = "";
	public string Date = "";
	public string Pr = "";
	public string Title = "";
	public string? Name;
	public string Url = "";
	public string PostUrl = "";
}

// A blog post card: read_blog_posts()'s dicts.
sealed record BlogPost(string File, string Pr, string Title, string Date, string Tag, string Excerpt = "");

// A heading scan_blocks() found: its line, everything before its "#"s, its level and the rest of its line.
readonly record struct MarkdownHeading(int Index, string Prefix, int Level, string Rest);

// A code block scan_blocks() found: a fence, with its info string and indent, or an indented code line (Joins: only blank lines in the same
// containers separate it from the indented code line before it). Lines are the block's line indexes, Content its text.
sealed class CodeBlock
{
	public required string Kind;
	public required List<int> Lines;
	public required List<string> Content;
	public string Info = "";
	public int Indent;
	public bool Nested;
	public bool Joins;
}

// What inline_code() yields: a code span (Content its text), an HTML comment, a link's <destination> or bare destination, or the rest of a
// reference definition (its label, before the destination, and its title, after it).
enum InlineKind
{
	Span,
	Comment,
	AngleDestination,
	BareDestination,
	DefinitionText,
}

readonly record struct InlineItem(int Start, int End, InlineKind Kind, string? Content = null);

static class ReleasePost
{
	public const string DefaultModel = "claude-sonnet-5-5";
	public const string AnthropicUrl = "https://api.anthropic.com/v1/messages";
	public const int ApiTimeoutSeconds = 60;
	public const int DiffLimit = 60_000;
	public const int TableSize = 10;

	// Dependency bumps get the deterministic sections only: a summary of a version bump adds nothing the title doesn't say.
	static readonly HashSet<string> NoSummaryAuthors = ["dependabot[bot]"];

	static readonly string[] Areas = ["src/", "tests/", ".github/", ".sandcastle/", "docs/"];
	const string OtherArea = "other";

	// Lock files are noise, and the rest are written by this script.
	static readonly HashSet<string> DiffExcludedNames = ["package-lock.json", "pnpm-lock.yaml"];
	static readonly HashSet<string> DiffExcludedPaths = ["README.md", "docs/README.md"];
	static readonly string[] DiffExcludedPrefixes = ["docs/blogs/"];

	// Strict UTF-8, which Python reads and writes text with: a byte that isn't UTF-8, or a lone surrogate, is an error.
	public static readonly UTF8Encoding Utf8 = new(false, true);

	// Python's \s in a str pattern (str.isspace()): .NET's \s and the four separators \x1c to \x1f. PyNS is \S.
	const string PyS = @"[\s\x1c-\x1f]";
	const string PyNS = @"[^\s\x1c-\x1f]";
	// \s under re.ASCII.
	const string AS = @"[ \t\n\r\f\v]";
	// Python's \w in a str pattern: str.isalnum() and "_".
	const string PyW = @"[\p{L}\p{Nd}\p{Nl}\p{No}_]";

	static Regex R(string pattern, RegexOptions options = RegexOptions.None) => new(pattern, options | RegexOptions.CultureInvariant);

	// pattern.match(text, pos), pattern.match(text, pos, endpos) and pattern.fullmatch(): a pattern anchored where the match starts.
	static Regex At(string pattern, RegexOptions options = RegexOptions.None) => R($@"\G(?:{pattern})", options);

	static Regex Whole(string pattern, RegexOptions options = RegexOptions.None) => R($@"\G(?:{pattern})\z", options);

	static Match? Match(Regex regex, string text, int pos = 0)
	{
		if (pos > text.Length)
			return null;
		var match = regex.Match(text, pos);
		return match.Success ? match : null;
	}

	// With endpos: the text read as if it ended there.
	static Match? Match(Regex regex, string text, int pos, int end)
	{
		end = Math.Min(end, text.Length);
		if (pos > end)
			return null;
		var match = regex.Match(text, pos, end - pos);
		return match.Success ? match : null;
	}

	// re.IGNORECASE on ASCII letters, as under re.ASCII: each letter of the pattern matches its two cases only.
	static string NoCase(string pattern) =>
			string.Concat(pattern.Select(c => char.IsAsciiLetter(c) ? $"[{char.ToLowerInvariant(c)}{char.ToUpperInvariant(c)}]" : c.ToString()));

	// re.IGNORECASE on a str pattern, for the letters these patterns hold: Python also matches "i" to U+0130 and U+0131, "s" to U+017F (long
	// s) and "k" to U+212A (Kelvin).
	static string Unicode(string pattern) => string.Concat(pattern.Select(c => char.ToLowerInvariant(c) switch
	{
		'i' when char.IsAsciiLetter(c) => "[iI\u0130\u0131]",
		's' when char.IsAsciiLetter(c) => "[sS\u017f]",
		'k' when char.IsAsciiLetter(c) => "[kK\u212a]",
		_ when char.IsAsciiLetter(c) => $"[{char.ToLowerInvariant(c)}{char.ToUpperInvariant(c)}]",
		_ => c.ToString(),
	}));

	public static void Log(TextWriter log, string message)
	{
		log.Write(message + "\n");
		log.Flush();
	}

	// Python's slices, which clamp: text[start:] and text[start:end].
	static string From(string text, int start) => start >= text.Length ? "" : text[start..];

	static string Cut(string text, int start, int end)
	{
		end = Math.Min(end, text.Length);
		return start >= end ? "" : text[start..end];
	}

	// str.lower(): .NET's invariant lowering, but U+0130 lowers to "i" and a combining dot above, as Python's full case mapping does.
	public static string PyLower(string text) => text.ToLowerInvariant().Replace("\u0130", "i\u0307", StringComparison.Ordinal);

	// str.split() with no separator: the runs between whitespace.
	static List<string> PySplit(string text) => [.. Regex.Split(text, PyS + "+").Where(part => part.Length > 0)];

	// str.isalnum() of the code point at text[pos], and how many UTF-16 units it takes.
	static bool IsAlnumAt(string text, int pos, out int width)
	{
		width = pos + 1 < text.Length && char.IsSurrogatePair(text[pos], text[pos + 1]) ? 2 : 1;
		return width == 2 ? IsAlnum(char.ConvertToUtf32(text[pos], text[pos + 1])) : !char.IsSurrogate(text[pos]) && IsAlnum(text[pos]);
	}

	static bool IsAlnum(int codePoint) => CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
			or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
			or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

	// The text's code points, a lone surrogate being one, as Python iterates a str.
	static IEnumerable<int> CodePoints(string text)
	{
		for (var i = 0; i < text.Length; i++)
		{
			if (i + 1 < text.Length && char.IsSurrogatePair(text[i], text[i + 1]))
			{
				yield return char.ConvertToUtf32(text[i], text[i + 1]);
				i++;
			}
			else
				yield return text[i];
		}
	}

	// int() of a run of decimal digits, any Unicode decimal digit among them, astral ones too; past int()'s 4,300 digits, its ValueError.
	static BigInteger ParseInt(string digits) => Py.Int(digits) ?? throw new FormatException($"int() refused the digits {Py.Repr(digits)}");

	// The text with CRLF and CR as LF, as Python reads text in universal newlines mode.
	public static string Universal(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

	// Path.read_text(encoding="utf-8") and Path.write_text(text, encoding="utf-8").
	public static string ReadText(string path) => Universal(Utf8.GetString(File.ReadAllBytes(path)));

	public static void WriteText(string path, string text) => File.WriteAllBytes(path, Utf8.GetBytes(text));

	// Path.exists(): a file or folder, a symlink followed.
	static bool Exists(string path)
	{
		try
		{
			FileSystemInfo info = new FileInfo(path);
			if (info.LinkTarget is not null)
				info = info.ResolveLinkTarget(true) ?? info;
			return File.Exists(info.FullName) || Directory.Exists(info.FullName);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return false;
		}
	}

	// str(Path(root)): "." parts and repeated slashes dropped, as pathlib writes a path.
	static string PathText(string path)
	{
		var parts = path.Split('/').Where(part => part.Length > 0 && part != ".").ToList();
		var text = (path.StartsWith('/') ? "/" : "") + string.Join('/', parts);
		return text.Length == 0 ? "." : text;
	}

	static string Join(string folder, string name) => folder == "." ? name : folder.EndsWith('/') ? folder + name : folder + "/" + name;

	// The value at key in a dict, or null when there's no such key (or the value isn't a dict).
	public static JsonNode? Get(JsonNode? node, string key) => node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

	// A value used as a string: a JSON string, else null (None, and what "or" would skip).
	public static string? Text(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

	// str() of a JSON value, as an f-string writes it.
	public static string Str(JsonNode? node) => node switch
	{
		null => "None",
		JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
		JsonValue value when value.GetValueKind() == JsonValueKind.True => "True",
		JsonValue value when value.GetValueKind() == JsonValueKind.False => "False",
		_ => node.ToJsonString(),
	};

	public static string Slugify(string title)
	{
		var slug = Regex.Replace(PyLower(title), "[^a-z0-9]", "-");
		slug = Regex.Replace(slug, "-+", "-").Trim('-');
		return slug.Length > 0 ? slug : "pr-update";
	}

	// re.match(r"v?(\d+)\.(\d+)\.(\d+)", tag): a v or none, then three runs of decimal digits (DigitsAt's, as Python's \d reads them) with
	// a . between each, at the tag's start; anything may follow. Their values, or (0, 0, 0) when the tag doesn't start so.
	public static (BigInteger, BigInteger, BigInteger) VersionKey(string? tag)
	{
		tag ??= "";
		var at = tag.StartsWith('v') ? 1 : 0;
		var parts = new BigInteger[3];
		for (var part = 0; part < 3; part++)
		{
			if (part > 0 && (at >= tag.Length || tag[at++] != '.'))
				return (0, 0, 0);
			var length = DigitsAt(tag, at);
			if (length == 0)
				return (0, 0, 0);
			parts[part] = ParseInt(tag.Substring(at, length));
			at += length;
		}
		return (parts[0], parts[1], parts[2]);
	}

	// Post sections

	static readonly Regex LineEnding = R(@"\r\n|\r|\n");

	public static string CommitSubject(JsonNode? commit)
	{
		var message = Text(Get(Get(commit, "commit"), "message")) ?? "";
		// Cut at Markdown's line endings (CRLF, CR, LF) and nothing else: a CR left in the subject would start a new Markdown line, while
		// splitlines() would also cut a subject at a U+2028 or U+2029 that Markdown reads as text.
		var first = Py.Strip(LineEnding.Split(message, 2)[0]);
		return Py.Strip(message).Length > 0 ? first : "(no message)";
	}

	// A commit subject that starts a list item this way, or after the quote or list markers that nest a block in it, would start a definition
	// there, and the item (or the block) would render empty: to GitHub (cmark-gfm) a reference definition whose title is the "(`sha`)" after
	// it, and to kramdown, which takes anything after "[label]:" as the destination, a reference, footnote or abbreviation definition.
	static readonly Regex DefinitionLikeSubject = At(@"\*?\[(?:[^\]\\]|\\.)*\]:|\*?\[[^\]]+\]:");

	public static string RenderCommits(IEnumerable<JsonNode?> commits)
	{
		List<string> lines = ["## Commits", ""];
		var any = false;
		foreach (var commit in commits)
		{
			any = true;
			// Checked as it's published, so a comment the sanitizer drops can't hide one. Escaped, it can't start a definition, so it was
			// rightly sanitized like a title.
			var subject = SanitizeInline(CommitSubject(commit));
			var after = AfterContainers(subject, 0);
			if (after > 0)
			{
				// One space around each marker, so nothing after them is indented code: kramdown and cmark-gfm read that differently, and an
				// escape in code would show.
				var rest = subject[after..].TrimStart(' ', '\t');
				subject = string.Join(" ", PySplit(subject[..after])) + (rest.Length > 0 ? " " + rest : "");
			}
			// Spaces and tabs after the markers are skipped, so a definition behind them is found; with no marker they're only what a dropped
			// comment left, and they go.
			var start = AfterContainers(subject, 0);
			if (start == 0)
				subject = subject.TrimStart(' ', '\t');
			start += From(subject, start).Length - From(subject, start).TrimStart(' ', '\t').Length;
			if (Match(DefinitionLikeSubject, subject, start) is not null)
				subject = subject[..start] + "\\" + subject[start..];
			lines.Add($"- {subject} (`{Py.Head(Text(Get(commit, "sha")) ?? "", 7)}`)");
		}
		if (!any)
			lines.Insert(2, "No commits were found.");
		return string.Join("\n", lines) + "\n";
	}

	static readonly Regex PlainName = Whole(@"[A-Za-z0-9._/@+=,~()-]+(?: [A-Za-z0-9._/@+=,~()-]+)*");

	// A file name as inline code; one with a backtick or a risky character is written as <code> instead.
	public static string CodeName(string name) =>
			// A space at either end would be trimmed by a code span, so the name keeps it as <code>.
			Match(PlainName, name) is not null ? $"`{name}`" : CodeHtml(name, trim: false);

	public static string AreaOf(string path) => Areas.FirstOrDefault(area => path.StartsWith(area, StringComparison.Ordinal)) ?? OtherArea;

	static string FileName(JsonNode? file) =>
			Text(Get(file, "filename")) ?? throw new KeyNotFoundException("filename");

	public static string RenderFiles(IReadOnlyList<JsonNode?> files)
	{
		Dictionary<string, List<JsonNode?>> groups = [];
		foreach (var file in files)
		{
			var area = AreaOf(FileName(file));
			if (!groups.TryGetValue(area, out var group))
				groups[area] = group = [];
			group.Add(file);
		}
		List<string> lines = ["## Files changed", ""];
		if (files.Count == 0)
			lines.Add("No files were changed.");
		foreach (var area in Areas.Append(OtherArea))
		{
			if (!groups.TryGetValue(area, out var group))
				continue;
			lines.AddRange([$"### {area}", ""]);
			foreach (var file in group.OrderBy(FileName, Py.Order))
			{
				var additions = file is JsonObject o && o.ContainsKey("additions") ? Str(o["additions"]) : "0";
				var deletions = file is JsonObject d && d.ContainsKey("deletions") ? Str(d["deletions"]) : "0";
				lines.Add($"- {CodeName(FileName(file))} (+{additions} / -{deletions})");
			}
			lines.Add("");
		}
		return string.Join("\n", lines).TrimEnd('\n') + "\n";
	}

	public static bool IsExcludedFromDiff(string path)
	{
		var name = path[(path.LastIndexOf('/') + 1)..];
		return DiffExcludedNames.Contains(name) || name.EndsWith(".lock", StringComparison.Ordinal) || DiffExcludedPaths.Contains(path)
				|| DiffExcludedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
	}

	// Rebuild a unified diff from the per-file patches the API returns.
	public static string BuildDiff(IEnumerable<JsonNode?> files, int limit = DiffLimit)
	{
		StringBuilder parts = new();
		foreach (var file in files)
		{
			var path = FileName(file);
			if (IsExcludedFromDiff(path))
				continue;
			var patch = Text(Get(file, "patch")) is { Length: > 0 } text ? text : "(no textual diff: binary or too large)";
			parts.Append($"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n{patch}\n");
		}
		var diff = parts.ToString();
		var length = Py.Len(diff);
		if (length > limit)
			diff = Py.Head(diff, limit) + $"\n[Diff truncated: showing the first {limit} of {length} characters.]\n";
		return diff;
	}

	// AI summary

	public static string BuildPrompt(JsonNode? pr, IEnumerable<JsonNode?> commits, string diff)
	{
		var subjects = string.Join("\n", commits.Select(c => $"- {CommitSubject(c)}"));
		return "Write a 3-5 sentence narrative summary of this merged pull request for a release blog post. "
				+ "Explain what changed and why, for a developer who uses the project. "
				+ "Answer with plain prose only: no heading, no lists, no preamble.\n\n"
				+ $"<title>{Text(Get(pr, "title")) ?? ""}</title>\n\n"
				+ $"<description>\n{(Text(Get(pr, "body")) is { Length: > 0 } body ? body : "(none)")}\n</description>\n\n"
				+ $"<commits>\n{(subjects.Length > 0 ? subjects : "(none)")}\n</commits>\n\n"
				+ $"<diff>\n{(diff.Length > 0 ? diff : "(empty)")}\n</diff>\n";
	}

	// urlopen() itself: a POST through HttpClient, any status but a success an error, as urllib raises HTTPError.
	public static byte[] Open(SummaryRequest request, int timeout)
	{
		using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(timeout) };
		using HttpRequestMessage message = new(HttpMethod.Post, request.Url) { Content = new ByteArrayContent(request.Data) };
		foreach (var (name, value) in request.Headers)
		{
			if (!message.Headers.TryAddWithoutValidation(name, value))
				message.Content.Headers.TryAddWithoutValidation(name, value);
		}
		using var response = client.Send(message);
		response.EnsureSuccessStatusCode();
		using MemoryStream body = new();
		response.Content.ReadAsStream().CopyTo(body);
		return body.ToArray();
	}

	// Claude's summary, or null after logging a warning. Never throws.
	public static string? RequestSummary(string apiKey, string model, string prompt, UrlOpen? urlopen = null, TextWriter? log = null)
	{
		log ??= Console.Out;
		urlopen ??= Open;
		JsonObject body = new()
		{
			["model"] = model,
			["max_tokens"] = 1024,
			["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
		};
		SummaryRequest request = new(AnthropicUrl, new Dictionary<string, string>
		{
			["x-api-key"] = apiKey,
			["anthropic-version"] = "2023-06-01",
			["content-type"] = "application/json",
		}, Utf8.GetBytes(body.ToJsonString()));
		string text;
		try
		{
			var payload = JsonNode.Parse(Utf8.GetString(urlopen(request, ApiTimeoutSeconds)));
			if (payload is not JsonObject)
				throw new InvalidOperationException($"the response is {payload?.GetValueKind().ToString() ?? "null"}, not an object");
			// payload.get("content", []): a missing list is none, anything but a list fails, as iterating it would.
			var content = ((JsonObject)payload).TryGetPropertyValue("content", out var listed)
					? listed as JsonArray ?? throw new InvalidOperationException("content isn't a list")
					: [];
			StringBuilder joined = new();
			foreach (var block in content)
			{
				if (block is not JsonObject item)
					throw new InvalidOperationException("a content block isn't an object");
				if (Text(Get(item, "type")) == "text")
					joined.Append(item.TryGetPropertyValue("text", out var part) ? Text(part) ?? throw new InvalidOperationException("a text block's text isn't a string") : "");
			}
			text = Py.Strip(joined.ToString());
		}
		catch (Exception error)
		{
			// Any failure only drops the summary.
			Log(log, $"::warning::AI summary request failed ({error.GetType().Name}: {error.Message}); writing the post without it.");
			return null;
		}
		if (text.Length == 0)
		{
			Log(log, "::warning::AI summary response had no text; writing the post without it.");
			return null;
		}
		return text;
	}

	// Post file and blog index

	static readonly Regex AtxHeading = R(@"^( {0,3})(#{1,6})(?=[ \t]|$)");
	// A backtick fence's info string holds no backtick: "```a```" is a code span.
	static readonly Regex Fence = R(@"^ {0,3}(`{3,}(?=[^`]*$)|~{3,})(.*)$");
	static readonly Regex SetextUnderline = R(@"^ {0,3}(=+|-+)[ \t]*$");
	static readonly Regex ThematicBreak = R(@"^ {0,3}([-*_])([ \t]*\1){2,}[ \t]*$");
	static readonly Regex IndentedCode = R(@"^( {4}|\t)");

	// The end condition of an HTML block: a pattern searched on each line, or Blank, a blank line.
	sealed class HtmlEnd(Regex? pattern)
	{
		public static readonly HtmlEnd Blank = new(null);
		public Regex? Pattern { get; } = pattern;
	}

	// CommonMark's seven kinds of HTML block, as (start, end) pairs. Their lines are raw HTML, so a "#" line inside one is text, not a heading.
	// All the HTML patterns are re.ASCII: the tag grammar is ASCII, so a non-breaking space or "\u017f" (long s, which Unicode case-folds to
	// "s") makes a line paragraph text, as GitHub reads it.
	const string HtmlBlockTags = "address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt"
			+ "|fieldset|figcaption|figure|footer|form|frame|frameset|h1|h2|h3|h4|h5|h6|head|header|hr|html|iframe|legend|li"
			+ "|link|main|menu|menuitem|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot"
			+ "|th|thead|title|tr|track|ul";
	const string HtmlAttribute = AS + @"+[A-Za-z_:][A-Za-z0-9_.:-]*(?:" + AS + "*=" + AS + @"*(?:[^ \t\n\r\f\v""'=<>`]+|'[^']*'|""[^""]*""))?";
	// A complete open or close tag, as CommonMark reads raw inline HTML.
	static readonly Regex InlineHtmlTag = At($@"<[A-Za-z][A-Za-z0-9-]*(?:{HtmlAttribute})*{AS}*/?>|</[A-Za-z][A-Za-z0-9-]*{AS}*>");
	// CommonMark's other raw inline HTML: a processing instruction, CDATA section or declaration, by its opener and the text that closes it. A
	// declaration's name is uppercase letters then whitespace, as GitHub (cmark-gfm) reads it.
	static readonly (string Opener, string Closer)[] InlineHtmlCloses = [("<?", "?>"), ("<![CDATA[", "]]>")];
	static readonly Regex InlineHtmlDeclaration = At("<![A-Z]+" + AS);
	static readonly (Regex Start, HtmlEnd End)[] HtmlBlocks =
	[
		(At($@" {{0,3}}<(?:{NoCase("pre|script|style|textarea")})(?:{AS}|>|$)"), new(R($"</(?:{NoCase("pre|script|style|textarea")})>"))),
		(At(" {0,3}<!--"), new(R("-->"))),
		(At(@" {0,3}<\?"), new(R(@"\?>"))),
		// Kind 4 needs an uppercase letter, as GitHub (cmark-gfm) reads it: "<!doctype" is text.
		(At(" {0,3}<![A-Z]"), new(R(">"))),
		(At(@" {0,3}<!\[CDATA\["), new(R(@"\]\]>"))),
		(At($@" {{0,3}}</?(?:{NoCase(HtmlBlockTags)})(?:{AS}|/?>|$)"), HtmlEnd.Blank),
	];
	// Kind 7: any other complete open or closing tag alone on its line. Unlike the others it can't interrupt a paragraph.
	static readonly Regex HtmlOtherTag = At($@" {{0,3}}(?:<[A-Za-z][A-Za-z0-9-]*(?:{HtmlAttribute})*{AS}*/?>|</[A-Za-z][A-Za-z0-9-]*{AS}*>){AS}*$");
	// The level of the "## " post sections that NestHeadings' text is inserted under.
	const int SectionLevel = 2;
	static readonly Regex QuoteMarker = R(@"^ {0,3}> ?");
	static readonly Regex OneOrBullet = R(@"^ {0,3}([-+*]|1[.)])");
	// Position-anchored, for ScanBlocks, which matches containers at an offset into the line instead of slicing a new suffix for every level.
	static readonly Regex QuoteAt = At(" {0,3}> ?");
	static readonly Regex ListAt = At(@" {0,3}([-+*]|[0-9]{1,9}[.)])(?=[ \t]|$)");
	static readonly Regex SpacesAt = At(" *");

	// Where the run of spaces at pos ends (pos itself past the line's end).
	static int SpacesEnd(string line, int pos) => Match(SpacesAt, line, pos) is { } spaces ? spaces.Index + spaces.Length : pos;

	// Whether a line closes a fence opened by the marker run fence.
	static bool ClosesFence(string fence, string line)
	{
		var match = Fence.Match(line);
		if (!match.Success)
			return false;
		var marker = match.Groups[1].Value;
		return marker[0] == fence[0] && marker.Length >= fence.Length && Py.Strip(match.Groups[2].Value).Length == 0;
	}

	// The end condition of an HTML block opened by this line, or null if it opens none. Kinds 1 to 5 end on the line matching their end
	// pattern (possibly this one), kinds 6 and 7 at a blank line. Inside a paragraph, only kinds 1 to 6 can start.
	static HtmlEnd? HtmlBlockEnd(string line, bool inParagraph = false)
	{
		foreach (var (start, end) in HtmlBlocks)
		{
			if (start.IsMatch(line))
				return end;
		}
		return !inParagraph && HtmlOtherTag.IsMatch(line) ? HtmlEnd.Blank : null;
	}

	// Whether an HTML block with this end condition ends on this (non-blank) line.
	static bool HtmlBlockEnds(HtmlEnd end, string line) => end.Pattern is { } pattern && pattern.IsMatch(line);

	// Indexes of the lines that aren't Markdown text: fenced code and raw HTML blocks, at any depth. A fence closes on a run of the same
	// character at least as long as the opener, with nothing but whitespace after it, or where its quote or list item ends; an HTML block
	// closes as HtmlBlockEnd() describes.
	public static HashSet<int> CodeLines(IEnumerable<string> lines)
	{
		HashSet<int> code = [];
		ScanBlocks([.. lines], [], code);
		return code;
	}

	// (last non-space index, last index of a character a thematic break can't contain), -1 if none.
	static (int LastText, int LastOther) LineBounds(string line)
	{
		var lastText = line.TrimEnd(' ', '\t').Length - 1;
		var lastOther = lastText;
		while (lastOther >= 0 && line[lastOther] is ' ' or '\t' or '-' or '*' or '_')
			lastOther--;
		return (lastText, lastOther);
	}

	// The width of the list item marker and its spacing at pos, or null if none opens there. The content starts after the marker and 1 to 4
	// spaces; with more, or none at all, it starts one column after the marker. bounds is LineBounds(line), so ruling out a thematic break
	// costs O(1) unless the rest of the line could be one.
	static int? ListWidthAt(string line, int pos, (int LastText, int LastOther) bounds)
	{
		var marker = Match(ListAt, line, pos);
		if (marker is null)
			return null;
		if (bounds.LastOther < pos && ThematicBreak.IsMatch(From(line, pos)))
			return null;
		var end = marker.Index + marker.Length;
		var spaces = SpacesEnd(line, end) - end;
		return end - pos + (bounds.LastText >= end && spaces is >= 1 and <= 4 ? spaces : 1);
	}

	// The content column of a list item opened by this line, or null if it opens none.
	static int? ListItemWidth(string line) => ListWidthAt(line, 0, LineBounds(line));

	// Whether a line ends a paragraph in the same container by opening a new block. Only a non-empty list item interrupts, and an ordered one
	// only when numbered 1. (A line that fails to continue a quote or list item is judged differently: there any list item opens a new block.)
	static bool InterruptsParagraph(string line)
	{
		var width = ListItemWidth(line);
		return AtxHeading.IsMatch(line) || Fence.IsMatch(line) || ThematicBreak.IsMatch(line) || HtmlBlockEnd(line, inParagraph: true) is not null
				|| QuoteMarker.IsMatch(line) || (width is { } w && From(line, w).Trim(' ', '\t').Length > 0 && OneOrBullet.IsMatch(line));
	}

	// str.expandtabs(4): each tab to the next multiple of 4 columns, a code point a column, the count starting again after a CR or LF.
	static string ExpandTabs(string text)
	{
		if (!text.Contains('\t'))
			return text;
		StringBuilder out_ = new();
		var column = 0;
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (c == '\t')
			{
				var spaces = 4 - column % 4;
				out_.Append(' ', spaces);
				column += spaces;
				continue;
			}
			out_.Append(c);
			if (i + 1 < text.Length && char.IsSurrogatePair(c, text[i + 1]))
				out_.Append(text[++i]);
			column = c is '\n' or '\r' ? 0 : column + 1;
		}
		return out_.ToString();
	}

	static int Width(string raw, int pos) => pos + 1 < raw.Length && char.IsSurrogatePair(raw[pos], raw[pos + 1]) ? 2 : 1;

	// The raw line from a column of its tab-expanded form on, tabs and all. Columns are only used to find where the code starts; the code
	// itself is the raw text (mpaulosky/dotfiles#58). A tab that straddles the column leaves its remaining columns as spaces, as CommonMark does.
	static string FromColumn(string raw, int column)
	{
		if (raw.EndsWith('\r'))
			raw = raw[..^1];
		var col = 0;
		for (var pos = 0; pos < raw.Length; pos += Width(raw, pos))
		{
			if (col >= column)
				return new string(' ', col - column) + raw[pos..];
			col = raw[pos] == '\t' ? col + 4 - col % 4 : col + 1;
		}
		return new string(' ', Math.Max(col - column, 0));
	}

	// Where a column of a line's tab-expanded form falls in the raw line, past any tab that straddles it.
	static int RawOffset(string raw, int column)
	{
		var col = 0;
		for (var pos = 0; pos < raw.Length; pos += Width(raw, pos))
		{
			if (col >= column)
				return pos;
			col = raw[pos] == '\t' ? col + 4 - col % 4 : col + 1;
		}
		return raw.Length;
	}

	// A fenced code line's text, from the raw line, with its containers and the opener's indent removed. rest is the tab-expanded line from
	// column offset, after its containers.
	static string FenceContent(string raw, int offset, string rest, CodeBlock block)
	{
		var spaces = rest.Length - rest.TrimStart(' ').Length;
		return FromColumn(raw, offset + Math.Min(spaces, block.Indent));
	}

	sealed class Container
	{
		public bool Quote;
		public int Width;
		public bool HasContent;
	}

	// Finds the headings in Markdown lines, including those inside block quotes and list items.
	//
	// Appends (line index, prefix, level, rest of line) to headings for each ATX or setext heading, where the prefix is everything before
	// the "#"s (container markers and indent). The later lines of a setext heading, including its underline, are set to null in lines. When
	// code is a set, the indexes of fenced code and raw HTML block lines are added to it. When blocks is a list, each fenced code block is
	// appended to it (Kind "fence", with its Info), and each indented code line (Kind "indented"), where Nested says whether a quote or list
	// item holds it, and Joins whether only blank lines in the same quotes and list items separate it from the indented code line before it
	// (a ">" line is blank inside its quote). When continued is a set, the indexes of the lines that continue an open paragraph, lazily or
	// not, are added to it; every other line starts a block or is blank. When prefixes is a dictionary, each paragraph line's index maps to
	// the length of its container prefix in the raw line: the markers and indent of the quotes and list items it matched.
	//
	// This follows CommonMark's block parsing in one pass: each line first matches the open quotes and list items, may lazily continue an
	// open paragraph, then opens new containers before its content is read. Columns are measured with tabs expanded to CommonMark's 4-column
	// stops, and a blank line holds only spaces and tabs (a non-breaking space is text). A code block's Content is taken from the raw lines,
	// tabs and all (mpaulosky/dotfiles#58).
	public static void ScanBlocks(List<string?> lines, List<MarkdownHeading> headings, HashSet<int>? code = null, List<CodeBlock>? blocks = null,
			HashSet<int>? continued = null, Dictionary<int, int>? prefixes = null)
	{
		code ??= [];
		continued ??= [];
		// Open containers, outermost first. quoteLevels holds the stack positions of the quotes, so a blank line, which keeps every list item
		// open, finds the first quote it ends in O(1).
		List<Container> stack = [];
		List<int> quoteLevels = [];
		List<(int Index, string Prefix, string Text)>? paragraph = null;
		string? fence = null;
		CodeBlock? fenceBlock = null;
		HtmlEnd? htmlEnd = null;
		var codeRun = false; // whether indented code, then only blank lines, came last in these containers

		void MarkContent()
		{
			if (stack.Count > 0 && !stack[^1].Quote)
				stack[^1].HasContent = true;
		}

		void ParagraphLine(int index, string raw, int offset)
		{
			if (prefixes is not null)
				prefixes[index] = RawOffset(raw, offset);
		}

		for (var index = 0; index < lines.Count; index++)
		{
			var raw = lines[index]!;
			// A trailing "\r" is the rest of a CRLF line ending; tabs expand so columns can be counted in spaces from here on.
			var line = ExpandTabs(raw.EndsWith('\r') ? raw[..^1] : raw);
			var bounds = LineBounds(line);
			var lastText = bounds.LastText;
			var offset = 0;
			int matched;
			if (lastText < 0)
				matched = quoteLevels.Count > 0 ? quoteLevels[0] : stack.Count;
			else
			{
				matched = 0;
				foreach (var container in stack)
				{
					if (container.Quote)
					{
						var marker = Match(QuoteAt, line, offset);
						if (marker is null)
							break;
						offset = marker.Index + marker.Length;
					}
					else if (lastText >= offset)
					{
						if (SpacesEnd(line, offset) - offset < container.Width)
							break;
						offset += container.Width;
					}
					matched++;
				}
			}
			var rest = From(line, offset);
			var followsCode = codeRun;
			codeRun = false;
			var closed = matched < stack.Count;

			if (matched < stack.Count)
			{
				// A line that opens no new block lazily continues the open paragraph. Here any list item or HTML block opens one: the rule that
				// only some of them can interrupt a paragraph applies only once every container matched.
				var opensBlock = InterruptsParagraph(rest) || ListItemWidth(rest) is not null || HtmlBlockEnd(rest) is not null;
				if (paragraph is not null && rest.Trim(' ').Length > 0 && !opensBlock)
				{
					paragraph.Add((index, Cut(line, 0, offset), rest));
					continued.Add(index);
					ParagraphLine(index, raw, offset);
					continue;
				}
				stack.RemoveRange(matched, stack.Count - matched);
				while (quoteLevels.Count > 0 && quoteLevels[^1] >= matched)
					quoteLevels.RemoveAt(quoteLevels.Count - 1);
				paragraph = null;
				fence = null;
				fenceBlock = null;
				htmlEnd = null;
			}

			if (htmlEnd == HtmlEnd.Blank && rest.Trim(' ').Length == 0)
				htmlEnd = null; // the blank line ends the block and is read as usual
			if (fence is not null || htmlEnd is not null)
			{
				code.Add(index);
				if (fence is not null)
				{
					closed = ClosesFence(fence, rest);
					if (fenceBlock is not null)
					{
						fenceBlock.Lines.Add(index);
						if (!closed)
							fenceBlock.Content.Add(FenceContent(raw, offset, rest, fenceBlock));
					}
					if (closed)
					{
						fence = null;
						fenceBlock = null;
					}
				}
				else if (HtmlBlockEnds(htmlEnd!, rest))
					htmlEnd = null;
				continue;
			}

			// Open new quotes and list items. A quote always interrupts a paragraph; a list item only when InterruptsParagraph() says so.
			var opened = stack.Count;
			while (SpacesEnd(line, offset) - offset < 4) // indented code opens no container
			{
				var marker = Match(QuoteAt, line, offset);
				var width = marker is not null ? null : ListWidthAt(line, offset, bounds);
				if (marker is not null)
				{
					MarkContent();
					quoteLevels.Add(stack.Count);
					stack.Add(new Container { Quote = true });
					offset = marker.Index + marker.Length;
				}
				else if (width is { } w && (paragraph is null || InterruptsParagraph(From(line, offset))))
				{
					MarkContent();
					stack.Add(new Container { Width = w, HasContent = lastText >= offset + w });
					offset += w;
				}
				else
					break;
				paragraph = null;
			}
			rest = From(line, offset);

			if (rest.Trim(' ').Length == 0)
			{
				paragraph = null;
				codeRun = followsCode && !closed && stack.Count == opened;
				// An item that starts empty ends at its first blank line (not at its own marker line).
				if (stack.Count == opened && stack.Count > 0 && !stack[^1].Quote && !stack[^1].HasContent)
					stack.RemoveAt(stack.Count - 1);
				continue;
			}
			MarkContent();

			var fenceMatch = Fence.Match(rest);
			var htmlStart = fenceMatch.Success ? null : HtmlBlockEnd(rest, inParagraph: paragraph is not null);
			if (fenceMatch.Success || htmlStart is not null)
			{
				code.Add(index);
				if (fenceMatch.Success)
				{
					fence = fenceMatch.Groups[1].Value;
					if (blocks is not null)
					{
						fenceBlock = new CodeBlock
						{
							Kind = "fence",
							Lines = [index],
							Content = [],
							Info = fenceMatch.Groups[2].Value,
							Indent = rest.Length - rest.TrimStart(' ').Length,
							Nested = stack.Count > 0,
						};
						blocks.Add(fenceBlock);
					}
				}
				else if (!HtmlBlockEnds(htmlStart!, rest))
					htmlEnd = htmlStart;
				paragraph = null;
				continue;
			}
			var heading = AtxHeading.Match(rest);
			if (heading.Success)
			{
				headings.Add(new MarkdownHeading(index, Cut(line, 0, offset) + heading.Groups[1].Value, heading.Groups[2].Length, rest[(heading.Index + heading.Length)..]));
				paragraph = null;
				continue;
			}
			if (paragraph is not null)
			{
				var underline = SetextUnderline.Match(rest);
				if (underline.Success)
				{
					var (first, firstPrefix, _) = paragraph[0];
					var text = string.Join(" ", paragraph.Select(part => part.Text.Trim(' ')));
					headings.Add(new MarkdownHeading(first, firstPrefix, underline.Groups[1].Value[0] == '=' ? 1 : 2, " " + text));
					foreach (var (later, _, _) in paragraph.Skip(1))
						lines[later] = null;
					lines[index] = null;
					paragraph = null;
				}
				else if (ThematicBreak.IsMatch(rest))
					paragraph = null;
				else
				{
					paragraph.Add((index, Cut(line, 0, offset), rest));
					continued.Add(index);
					ParagraphLine(index, raw, offset);
				}
				continue;
			}
			if (IndentedCode.IsMatch(rest))
			{
				if (blocks is not null)
				{
					var content = FromColumn(raw, offset + 4);
					var joins = followsCode && !closed && stack.Count == opened;
					blocks.Add(new CodeBlock { Kind = "indented", Lines = [index], Content = [content], Nested = stack.Count > 0, Joins = joins });
				}
				codeRun = true;
				continue;
			}
			if (ThematicBreak.IsMatch(rest))
				continue;
			paragraph = [(index, Cut(line, 0, offset), rest)];
			ParagraphLine(index, raw, offset);
		}
	}

	// Untrusted Markdown. A PR's title, description and commits, and the AI summary of them, are written into docs/blogs and published by
	// GitHub Pages (Jekyll, with kramdown), so none of them may carry live HTML, a script link, a kramdown attribute list or Liquid. Code
	// keeps its text: a code block is re-emitted as a fence both parsers read the same way, and a code span holding anything that's escaped
	// elsewhere becomes <code> with every symbol as an entity. From mpaulosky/dotfiles#48. Code is found first, on the raw text, and
	// everything else is changed only outside it (mpaulosky/dotfiles#58).

	static readonly HashSet<string> SafeSchemes = ["http", "https", "mailto"];
	static readonly Regex Scheme = At(@"([a-z][a-z0-9+.\-]*):");
	// Besides letters and digits, what a scheme can be written with: its own symbols, the colon, entities (&#9; &colon;) and backslash escapes.
	const string SchemeText = "+.-:&#;\\";
	// The C0 controls and DEL, which don't end a scheme's read: a browser drops them, and the space, from a URL's start, and tabs and line
	// breaks from anywhere in it, and a safe scheme can't hold any of them. A line break does end a reference definition's read (IsLineBreak).
	static bool IsUrlControl(char c) => c < ' ' || c == '\x7f';
	static bool IsUrlDropped(char c) => c <= ' ' || c == '\x7f';
	// What ends a reference definition's destination, though not an inline link's: kramdown keeps a line break in "[a](java\nscript:x)"'s
	// href, and browsers drop it.
	static bool IsLineBreak(char c) => c is '\n' or '\r';
	// Only these autolinks keep their angle brackets; any other "<" is escaped.
	static readonly Regex Autolink = At($@"<(?:{Unicode("https?://|mailto:")})[^\s\x1c-\x1f<>""'`\\]*>");
	static readonly Regex LinkDestinationStart = R(@"\]\(|\]:");
	// A footnote definition's start, with kramdown's footnote ID (\w[\w-]*, and Ruby's \w is ASCII): GitHub Pages reads any other "[^...]:"
	// as a reference definition, a link.
	static readonly Regex FootnoteDefinition = Whole(@"[ \t>]*\[\^[A-Za-z0-9_][A-Za-z0-9_-]*\]:");
	// What InlineCode() stops at: a backtick run, a comment's opener, the "<" that may open an autolink or an inline HTML tag, the "](" before
	// a link destination, and the brackets that open and close a link's label.
	static readonly Regex InlineMark = R(@"`+|<!--|<|\]\(|\[|\]");
	// A CommonMark autolink, URI or email, which InlineCode() reads whole: a "]" or backtick in it closes no label and opens no span. Wider
	// than Autolink, which is what the sanitizer lets keep its angle brackets.
	static readonly Regex InlineAutolink = At(@"<[A-Za-z][A-Za-z0-9+.-]{1,31}:[^\x00-\x20<>]*>"
			+ @"|<[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?"
			+ @"(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*>");
	static readonly Regex ContainerMarker = At(@"[ \t]*(?:>|(?:[-+*]|[0-9]{1,9}[.)])(?=[ \t]|$))");
	static readonly Regex Backticks = R("`+");
	// Characters that make a code span unsafe to leave between backticks: a parser that pairs the backticks differently would read them as
	// HTML, an attribute list, Liquid or a link.
	static readonly Regex CodeSpanRisk = R(@"[<{\]]");
	// The space a link may hold between its parts: spaces and tabs, and at most one line ending (LF or CRLF, which the README's lines keep),
	// after which the next line's quote markers and indentation are skipped too.
	static readonly Regex LinkSpace = At(@"[ \t]*(?:\r?\n[ \t>]*)?");
	// One code point, as Python's "." and a class match one: a surrogate pair is one.
	const string Pair = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";
	// A reference definition's label and colon, read once its line's containers are skipped. A label with a footnote ID, as in
	// FootnoteDefinition, is a footnote's, which GitHub and kramdown read as one, not as a link; any other "[^...]" label is a link's. Its
	// 999 counts code points. Each code point is matched atomically, so a surrogate pair is never retried as two lone units: without that, a
	// label that doesn't close backtracks through every split of its pairs.
	static readonly Regex LinkDefinitionLabel = At($@"[ \t]*\[(?!\^[A-Za-z0-9_][A-Za-z0-9_-]*\])((?>{Pair}|[^\[\]\\\n]|\\(?>{Pair}|.)){{1,999}})\]:[ \t]*");
	// What may follow a definition's destination: a title, on its line or the next, then nothing on the title's last line. A title may hold
	// line endings, but not a blank line. A backslash before one is a literal backslash.
	const string LinkTitleLineEnding = @"\\?\r?\n(?![ \t>]*\r?$)";
	static readonly Regex LinkDefinitionTail = At(
			$@"(?:(?:[ \t]+|[ \t]*\r?\n[ \t>]*)(?:""(?:[^""\\\r\n]|\\.|{LinkTitleLineEnding})*""|'(?:[^'\\\r\n]|\\.|{LinkTitleLineEnding})*'"
			+ $@"|\((?:[^()\\\r\n]|\\.|{LinkTitleLineEnding})*\)))?[ \t]*\r?$",
			RegexOptions.Multiline);
	// How a <destination>'s characters that a bare one can't hold are written in it.
	static readonly Dictionary<char, string> BareDestinationEscapes = new()
	{
		[' '] = "%20",
		['\t'] = "%09",
		['('] = "%28",
		[')'] = "%29",
		['<'] = "%3C",
		['>'] = "%3E",
	};
	// The characters a backslash escapes in CommonMark: ASCII punctuation.
	const string Escapable = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

	static readonly Regex DecimalZeros = R("&#0+(?=[0-9])");
	static readonly Regex HexZeros = R("&#([xX])0+(?=[0-9a-fA-F])");
	// Past 8 digits no reference names a character: the largest is &#1114111; (&#x10FFFF;).
	static readonly Regex OversizedReference = R("&#(?:[xX][0-9a-fA-F]{9,}|[0-9]{9,});?");

	// html.unescape() of text, without the reference too long to convert that makes Python's raise. Leading zeros don't change a reference's
	// value, so they go first, and a reference still too long for any character becomes U+FFFD, as html.unescape makes any out-of-range one.
	public static string SafeUnescape(string text)
	{
		text = HexZeros.Replace(DecimalZeros.Replace(text, "&#"), "&#$1");
		return Html.Unescape(OversizedReference.Replace(text, "\ufffd"));
	}

	// Whether the link destination starting at text[start] has no scheme, or one in SafeSchemes.
	//
	// Browsers drop control characters and whitespace from a URL and decode entities, so "java&#9;script:" is still a javascript: link. The
	// whole destination's scheme is read however long it is: padding ("j&#9;&#9;...avascript:", or an entity's leading zeros) can push it
	// arbitrarily far from the start. The read stops at the first character that can't be part of a scheme, an entity or an escape, so it
	// never runs into the next "](". A control character or DEL (a tab or line break among them) doesn't stop it, nor does a space before
	// the scheme: browsers drop those too, and kramdown writes them into the href as they are ("<\x01javascript:x>"). lineEnds is true for a
	// reference definition's destination, which a line break does end: kramdown and GitHub both stop it there, so the read must not run into
	// the next line's "Note:" and take it for the scheme.
	static bool SafeDestination(string text, int start, bool lineEnds = false)
	{
		var end = start;
		while (end < text.Length && IsUrlDropped(text[end]) && !(lineEnds && IsLineBreak(text[end])))
			end++;
		while (end < text.Length && !(lineEnds && IsLineBreak(text[end])))
		{
			if (IsAlnumAt(text, end, out var width))
				end += width;
			else if (SchemeText.Contains(text[end]) || IsUrlControl(text[end]))
				end++;
			else
				break;
		}
		var candidate = SafeUnescape(Cut(text, start, end)).Replace("\\", "", StringComparison.Ordinal);
		candidate = PyLower(new string([.. candidate.Where(c => !IsUrlDropped(c))]));
		var scheme = Match(Scheme, candidate);
		return scheme is null || SafeSchemes.Contains(scheme.Groups[1].Value);
	}

	// The text with a "#" in front of each link destination whose scheme isn't safe.
	//
	// Any "](" or "]:" counts, wherever it is, since a parser may read a link or reference definition where this one wouldn't; the destination
	// may follow on the next line, after its quote markers. A "#" makes it a harmless fragment link. A footnote definition ("[^1]: text",
	// alone at the start of its line) isn't a link; anything else ending in "]:", such as a label holding an escaped "\[^", is read as one.
	// atLineStart is false for text that follows a code span on its first line.
	public static string NeutralizeLinks(string text, bool atLineStart = true)
	{
		StringBuilder out_ = new();
		var done = 0;
		foreach (Match match in LinkDestinationStart.Matches(text))
		{
			if (match.Value == "]:")
			{
				var lineStart = match.Index == 0 ? 0 : text.LastIndexOf('\n', match.Index - 1) + 1;
				if ((lineStart > 0 || atLineStart) && Match(FootnoteDefinition, text, lineStart, match.Index + match.Length) is not null)
					continue;
			}
			var start = match.Index + match.Length;
			while (start < text.Length && text[start] is ' ' or '\t' or '\n' or '>')
				start++;
			if (start < text.Length && text[start] == '<')
				start++;
			if (start >= done && !SafeDestination(text, start, lineEnds: match.Value == "]:"))
			{
				out_.Append(text, done, start - done).Append('#');
				done = start;
			}
		}
		return out_.Append(text, done, text.Length - done).ToString();
	}

	// A link's <destination> (without its brackets) written as the same destination, bare.
	//
	// The sanitizer escapes every "<" that isn't an autolink, which would put "&lt;" into the URL, so the brackets go and what they allowed is
	// percent-encoded, as CommonMark's reference renderer writes it in the href. A backslash that escapes nothing is part of the URL; left
	// bare, it could escape what follows, so it is percent-encoded too.
	public static string BareDestination(string angle)
	{
		StringBuilder out_ = new();
		var pos = 0;
		while (pos < angle.Length)
		{
			var c = angle[pos];
			string text;
			if (c == '\\' && pos + 1 < angle.Length && Escapable.Contains(angle[pos + 1]))
			{
				if (!BareDestinationEscapes.ContainsKey(angle[pos + 1]))
				{
					out_.Append(angle, pos, 2);
					pos += 2;
					continue;
				}
				pos++;
				c = angle[pos];
				text = c.ToString();
			}
			else if (c == '\\')
				text = "%5C";
			else
				text = c.ToString();
			out_.Append(text.Length == 1 && BareDestinationEscapes.TryGetValue(text[0], out var escaped) ? escaped : text);
			pos++;
		}
		return out_.ToString();
	}

	static bool OddBackslashesBefore(string text, int pos)
	{
		var count = 0;
		while (pos - count > 0 && text[pos - count - 1] == '\\')
			count++;
		return count % 2 == 1;
	}

	// A code span's text as <code>, every character but letters, digits and spaces as an entity. trim strips one space from each end, as
	// CommonMark does for a code span; a file name, which isn't one, keeps them.
	public static string CodeHtml(string content, bool trim = true)
	{
		if (trim && content.Length > 1 && content.StartsWith(' ') && content.EndsWith(' ') && content.Trim(' ').Length > 0)
			content = content[1..^1]; // CommonMark strips one space from each side
		StringBuilder out_ = new("<code>");
		foreach (var codePoint in CodePoints(content))
		{
			if (codePoint == ' ' || (codePoint is < 0xD800 or > 0xDFFF && IsAlnum(codePoint)))
				out_.Append(char.ConvertFromUtf32(codePoint));
			else
				out_.Append("&#").Append(codePoint.ToString(CultureInfo.InvariantCulture)).Append(';');
		}
		return out_.Append("</code>").ToString();
	}

	// Escape the HTML, attribute lists and (with liquid) Liquid in text outside code spans.
	static string EscapeText(string text, bool liquid)
	{
		StringBuilder out_ = new();
		var pos = 0;
		while (pos < text.Length)
		{
			var c = text[pos];
			if (c == '<' && !OddBackslashesBefore(text, pos))
			{
				if (Match(Autolink, text, pos) is { } autolink)
				{
					// An entity isn't decoded in an autolink, so its braces are percent-encoded instead: the link still works and Liquid can't
					// read it (mpaulosky/dotfiles#69).
					var link = autolink.Value;
					out_.Append(liquid ? link.Replace("{", "%7B", StringComparison.Ordinal).Replace("}", "%7D", StringComparison.Ordinal) : link);
					pos = autolink.Index + autolink.Length;
					continue;
				}
				out_.Append("&lt;");
			}
			else if (c == '{' && (liquid || text.AsSpan(pos).StartsWith("{:"))) // even after a backslash: parsers differ
				out_.Append("&#123;");
			else
				out_.Append(c);
			pos++;
		}
		return out_.ToString();
	}

	// Where the line at text[pos] starts once its quote and list item markers are skipped.
	static int AfterContainers(string text, int pos)
	{
		while (true)
		{
			var marker = Match(ContainerMarker, text, pos);
			if (marker is null || marker.Index + marker.Length == pos)
				return pos;
			pos = marker.Index + marker.Length;
		}
	}

	// The line with its first backtick or tilde escaped when it could open a fence. A real fence was re-emitted already, so this one only
	// looked like one where it sat (in an HTML block, say).
	static string EscapeFenceMarker(string line)
	{
		var rest = From(line, AfterContainers(line, 0)).TrimStart(' ', '\t');
		if (Fence.IsMatch(rest))
		{
			var cut = line.Length - rest.Length;
			line = line[..cut] + "\\" + line[cut..];
		}
		return line;
	}

	// The offset, in "\n".Join(lines), of each line but the first that doesn't continue the paragraph before it. continues[i] says whether
	// lines[i] continues a paragraph, as ScanBlocks() reports it in continued. A caller that scanned the whole document passes it, so a run of
	// its lines is read in its place (mpaulosky/dotfiles#58); without it, the lines are scanned on their own.
	static List<int> ParagraphBreaks(IReadOnlyList<string> lines, IReadOnlyList<bool>? continues = null)
	{
		if (continues is null)
		{
			HashSet<int> continued = [];
			ScanBlocks([.. lines], [], continued: continued);
			continues = [.. Enumerable.Range(0, lines.Count).Select(continued.Contains)];
		}
		List<int> breaks = [];
		var offset = 0;
		for (var index = 0; index < lines.Count; index++)
		{
			if (index > 0 && !continues[index])
				breaks.Add(offset);
			offset += lines[index].Length + 1;
		}
		return breaks;
	}

	static int BisectRight(List<int> list, int value)
	{
		int low = 0, high = list.Count;
		while (low < high)
		{
			var mid = (low + high) / 2;
			if (value < list[mid])
				high = mid;
			else
				low = mid + 1;
		}
		return low;
	}

	static int BisectLeft(List<int> list, int value)
	{
		int low = 0, high = list.Count;
		while (low < high)
		{
			var mid = (low + high) / 2;
			if (list[mid] < value)
				low = mid + 1;
			else
				high = mid;
		}
		return low;
	}

	// (destination start, destination end, end) of a complete inline link, read from just after its "](", or null.
	//
	// The end is that of its title and ")". CommonMark reads them when it reaches the "]", before any backtick in them, so they hold no code
	// span: [x](docs/a.md "Use `code`") is one link. Each space between its parts may hold a line ending. The link must end before limit,
	// the end of its paragraph.
	static (int Start, int End, int Tail)? LinkEnd(string text, int start, int limit)
	{
		var space = Match(LinkSpace, text, start);
		start = space is null ? start : space.Index + space.Length;
		var angle = Match(LinkAngleDestination, text, start);
		var end = angle is not null ? angle.Index + angle.Length : BareDestinationEnd(text, start);
		var tail = end is { } e ? Match(LinkInlineTail, text, e) : null;
		return tail is not null && tail.Index + tail.Length <= limit ? (start, end!.Value, tail.Index + tail.Length) : null;
	}

	// Each reference definition in Markdown text, as (start, destination start, destination end, end).
	//
	// A definition starts a paragraph, or follows another one, and is a label and a destination on one line, then an optional title, which may
	// start on the next line and hold line endings within its paragraph, with nothing after it on its last line; anything else there makes the
	// line paragraph text ([x]: foo [y](b.md)). breaks are ParagraphBreaks(). A definition is read before any code span, so a backtick in its
	// label or title pairs with nothing.
	static List<(int Start, int Destination, int End, int Pos)> ReferenceDefinitions(string text, List<int> breaks)
	{
		List<(int, int, int, int)> definitions = [];
		HashSet<int> starts = [0, .. breaks];
		var followsDefinition = false;
		var lineStart = 0;
		while (lineStart <= text.Length)
		{
			var lineEnd = text.IndexOf('\n', lineStart);
			lineEnd = lineEnd < 0 ? text.Length : lineEnd;
			var found = false;
			if (starts.Contains(lineStart) || followsDefinition)
			{
				var label = Match(LinkDefinitionLabel, text, AfterContainers(text, lineStart), lineEnd);
				if (label is not null && Py.Strip(label.Groups[1].Value).Length > 0)
				{
					var start = label.Index + label.Length;
					var angle = Match(LinkAngleDestination, text, start, lineEnd);
					var end = angle is not null ? angle.Index + angle.Length : BareDestinationEnd(text, start);
					// A title can't run into the next paragraph, quote or list item.
					var paragraph = BisectRight(breaks, lineStart);
					var paragraphEnd = paragraph < breaks.Count ? breaks[paragraph] : text.Length;
					var tail = end is { } e ? Match(LinkDefinitionTail, text, e, paragraphEnd) : null;
					if (tail is not null)
					{
						lineEnd = tail.Index + tail.Length; // a title's last line, before its "\n"
						definitions.Add((lineStart, start, end!.Value, lineEnd));
						found = true;
					}
				}
			}
			followsDefinition = found;
			lineStart = lineEnd + 1;
		}
		return definitions;
	}

	// A processing instruction, CDATA section or declaration at text[start], ending before limit, or null. closes caches where each closer
	// next occurs, so text full of unclosed openers is still read in linear time.
	static int? OtherInlineHtml(string text, int start, int limit, Dictionary<string, int> closes)
	{
		string opener, closer;
		if (Match(InlineHtmlDeclaration, text, start) is not null)
			(opener, closer) = ("<!", ">");
		else
		{
			var pair = InlineHtmlCloses.FirstOrDefault(o => text.AsSpan(start).StartsWith(o.Opener));
			if (pair.Opener is null)
				return null;
			(opener, closer) = pair;
		}
		var after = start + opener.Length;
		if (!closes.TryGetValue(closer, out var close) || (0 <= close && close < after))
			close = closes[closer] = text.IndexOf(closer, Math.Min(after, text.Length), StringComparison.Ordinal);
		if (close < 0 || close + closer.Length > limit)
			return null;
		return close + closer.Length;
	}

	// A paragraph's text with its later lines' container prefixes removed, built once per paragraph.
	//
	// CommonMark matches raw inline HTML in a paragraph's text, which leaves out each line's quote markers and list item indent: in
	// "> [<span\n> title="]">", the second ">" is the quote's, not the tag's end, and so is the one in "- - > [<span\n    > title=...".
	// prefixes holds each line's prefix length, as ScanBlocks() reports it, and line is the index of the paragraph's first line.
	sealed class ParagraphText
	{
		readonly List<int> textStarts = []; // where each piece starts in text
		readonly List<int> keptStarts = []; // and in kept
		readonly Dictionary<string, int> closes = []; // as OtherInlineHtml() caches them, in kept
		readonly string kept;

		public ParagraphText(string text, int start, int end, IReadOnlyList<int> prefixes, int line)
		{
			StringBuilder pieces = new();
			var keptLength = 0;
			var pos = start;
			while (pos < end)
			{
				if (pos > start)
				{
					line++;
					pos = Math.Min(pos + (line < prefixes.Count ? prefixes[line] : 0), end);
				}
				var newline = pos < end ? text.IndexOf('\n', pos, end - pos) : -1;
				var lineEnd = newline < 0 ? end : newline + 1;
				textStarts.Add(pos);
				keptStarts.Add(keptLength);
				if (lineEnd > pos)
					pieces.Append(text, pos, lineEnd - pos);
				keptLength += Math.Max(lineEnd - pos, 0);
				pos = lineEnd;
			}
			kept = pieces.ToString();
		}

		// The end of the tag, processing instruction, CDATA section or declaration at text[start], or null.
		public int? Html(int start)
		{
			var piece = BisectRight(textStarts, start) - 1;
			var at = keptStarts[piece] + start - textStarts[piece];
			var tag = Match(InlineHtmlTag, kept, at);
			var whole = tag is not null ? tag.Index + tag.Length : OtherInlineHtml(kept, at, kept.Length, closes);
			if (whole is not { } end)
				return null;
			piece = BisectLeft(keptStarts, end) - 1;
			return textStarts[piece] + end - keptStarts[piece];
		}
	}

	// Each code span, HTML comment and link destination in Markdown text, as (start, end, kind, content).
	//
	// The kind is Comment for a comment, AngleDestination or BareDestination for the destination of an inline link or reference definition,
	// and DefinitionText for the rest of a reference definition. The text is read left to right, as CommonMark reads it, and whatever starts
	// first wins: a backtick before "<!--" opens a span that may hold the comment's opener, and a comment hides the backticks inside it. A run
	// opens a span only where a later run of the same length closes it; a backslash escapes a run's first backtick. A span may cross line
	// breaks, but only within its paragraph (mpaulosky/dotfiles#57); continues is as for ParagraphBreaks(). A comment ends at the first "-->",
	// wherever it is, as the sanitizer has always dropped it. A complete inline link's destination and title are skipped, so their backticks
	// open no span (mpaulosky/dotfiles#58). A "](" is a link's only where an unescaped "[" earlier in the paragraph, outside code and comments,
	// is still open and active; otherwise it is text and its backticks pair. As in CommonMark, a link deactivates the link (not image) openers
	// before it, since a link can't hold a link. A reference definition is read before anything else on its line, as ReferenceDefinitions()
	// finds it; unset definitions for text that can't hold one, such as a title rendered in a heading or table cell. An autolink is skipped
	// whole. With html set, complete raw inline HTML is skipped too, as GitHub reads a README's: a tag, a processing instruction, CDATA section
	// or declaration, even one whose later lines carry container markers, which prefixes (each line's, as ScanBlocks() reports it) says where
	// to skip. So a "]" or backtick in it is neither a label's end nor a span's, and an autolink is any InlineAutolink. The post sanitizer
	// leaves html unset: it escapes every tag, and the "<" of any autolink but an Autolink, so only an Autolink is one in what it publishes,
	// and only that is skipped.
	//
	// Runs and paragraph ends are indexed once, and BareDestinationEnd() caps how far a link is read, so the scan stays linear.
	public static IEnumerable<InlineItem> InlineCode(string text, IReadOnlyList<bool>? continues = null, bool html = false, bool definitions = true,
			IReadOnlyList<int>? prefixes = null)
	{
		var breaks = ParagraphBreaks(text.Split('\n'), continues);
		List<int> paragraphStarts = [0, .. breaks];
		Dictionary<int, ParagraphText> paragraphTexts = []; // each paragraph's, by its index, built at its first "<"
		List<int> lineStarts = html ? [0, .. Regex.Matches(text, "\n").Select(match => match.Index + 1)] : [];
		Dictionary<int, List<int>> startsByLength = [];
		foreach (Match run in Backticks.Matches(text))
		{
			if (!startsByLength.TryGetValue(run.Length, out var runs))
				startsByLength[run.Length] = runs = [];
			runs.Add(run.Index);
		}
		var commentsCanClose = true; // false once a "<!--" found no "-->" after it
		List<bool> labels = []; // the "["s still open in labelsParagraph: whether each is an image's ("![")
		var inactiveBelow = 0; // labels[..inactiveBelow] that aren't images' can't make a link any more
		var labelsParagraph = 0;
		var found = definitions ? ReferenceDefinitions(text, breaks) : [];
		var nextDefinition = 0;
		var pos = 0;
		while (true)
		{
			var mark = pos <= text.Length ? InlineMark.Match(text, pos) : System.Text.RegularExpressions.Match.Empty;
			// A definition a comment hid is text; the next one is read before any mark on its line.
			while (nextDefinition < found.Count && found[nextDefinition].Start < pos)
				nextDefinition++;
			if (nextDefinition < found.Count && (!mark.Success || found[nextDefinition].Start <= mark.Index))
			{
				var (start, destination, end, next) = found[nextDefinition];
				yield return new InlineItem(start, destination, InlineKind.DefinitionText);
				yield return new InlineItem(destination, end, text[destination] == '<' ? InlineKind.AngleDestination : InlineKind.BareDestination);
				yield return new InlineItem(end, next, InlineKind.DefinitionText);
				pos = next;
				nextDefinition++;
				continue;
			}
			if (!mark.Success)
				yield break;
			var markStart = mark.Index;
			var token = mark.Value;
			pos = mark.Index + mark.Length;
			var paragraph = BisectRight(breaks, markStart);
			var paragraphEnd = paragraph < breaks.Count ? breaks[paragraph] : text.Length;
			if (paragraph != labelsParagraph)
			{
				labels = [];
				labelsParagraph = paragraph;
				inactiveBelow = 0;
			}
			if (token is "[" or "]" or "](")
			{
				var escaped = OddBackslashesBefore(text, markStart);
				if (token == "[")
				{
					if (!escaped)
						labels.Add(markStart > 0 && text[markStart - 1] == '!' && !OddBackslashesBefore(text, markStart - 1));
					continue;
				}
				if (escaped || labels.Count == 0)
					continue;
				var image = labels[^1];
				labels.RemoveAt(labels.Count - 1);
				var active = image || labels.Count >= inactiveBelow;
				inactiveBelow = Math.Min(inactiveBelow, labels.Count);
				if (token == "](" && active && LinkEnd(text, pos, paragraphEnd) is { } link)
				{
					if (!image)
						inactiveBelow = labels.Count;
					pos = link.Tail;
					yield return new InlineItem(link.Start, link.End, text[link.Start] == '<' ? InlineKind.AngleDestination : InlineKind.BareDestination);
				}
			}
			else if (token == "<")
			{
				if (OddBackslashesBefore(text, markStart))
					continue;
				int? whole;
				if (html)
				{
					if (!paragraphTexts.TryGetValue(paragraph, out var paragraphText))
					{
						var paragraphStart = paragraphStarts[paragraph];
						var first = BisectLeft(lineStarts, paragraphStart);
						paragraphTexts[paragraph] = paragraphText = new ParagraphText(text, paragraphStart, paragraphEnd, prefixes ?? [], first);
					}
					// An autolink can't cross a line, so it's read in place.
					whole = paragraphText.Html(markStart) ?? (Match(InlineAutolink, text, markStart, paragraphEnd) is { } autolink ? autolink.Index + autolink.Length : null);
				}
				else
					whole = Match(Autolink, text, markStart, paragraphEnd) is { } autolink ? autolink.Index + autolink.Length : null;
				if (whole is { } end)
					pos = end;
			}
			else if (token == "<!--")
			{
				var close = commentsCanClose ? text.IndexOf("-->", pos, StringComparison.Ordinal) : -1;
				if (close < 0)
				{
					commentsCanClose = false;
					continue;
				}
				yield return new InlineItem(markStart, close + 3, InlineKind.Comment);
				pos = close + 3;
			}
			else
			{
				var start = markStart;
				var length = token.Length;
				if (OddBackslashesBefore(text, start))
				{
					start++;
					length--;
					if (length == 0)
						continue;
				}
				var after = start + length;
				if (!startsByLength.TryGetValue(length, out var candidates))
					continue;
				var index = BisectLeft(candidates, after);
				if (index < candidates.Count && candidates[index] < paragraphEnd)
				{
					var closer = candidates[index];
					yield return new InlineItem(start, closer + length, InlineKind.Span, text[after..closer]);
					pos = closer + length;
				}
			}
		}
	}

	// Each code span in Markdown text, as InlineCode() finds them.
	static IEnumerable<InlineItem> CodeSpans(string text, IReadOnlyList<bool>? continues = null) =>
			InlineCode(text, continues).Where(code => code.Kind == InlineKind.Span);

	// Sanitized Markdown made safe as a link's text: brackets outside code spans become entities.
	//
	// A title's "]" would otherwise close the label early, so "fix: ](https://x)" would make the row link somewhere else. A code span keeps its
	// "[". Every "]" becomes an entity, even in what reads as a span here: a span the sanitizer kept as written has none, so any "]" this finds
	// in one comes from a pairing the sanitizer didn't make, and a parser may not make it either.
	public static string LinkLabel(string markdown)
	{
		StringBuilder out_ = new();
		var done = 0;
		foreach (var (start, end, _, _) in CodeSpans(markdown).Append(new InlineItem(markdown.Length, markdown.Length, InlineKind.Span, "")))
		{
			out_.Append(markdown[done..start].Replace("[", "&#91;", StringComparison.Ordinal)).Append(markdown, start, end - start);
			done = end;
		}
		return out_.ToString().Replace("]", "&#93;", StringComparison.Ordinal);
	}

	// Untrusted Markdown text (no code blocks) made safe to publish; HTML comments are dropped.
	//
	// Code spans and comments are found first, on the raw text, so code keeps its text (mpaulosky/dotfiles#58). A span holding anything risky
	// becomes <code>; one that crossed a line break has it as a space, as CommonMark renders it. The rest is prose: the comments are dropped
	// from it, so a destination a comment split is read whole, then its links are neutralized and its HTML escaped. continues and
	// definitions are as for ParagraphBreaks() and InlineCode().
	public static string SanitizeText(string text, IReadOnlyList<bool>? continues = null, bool liquid = false, bool definitions = true)
	{
		StringBuilder out_ = new();
		StringBuilder prose = new(); // the prose since the last span, without its comments
		var atLineStart = true;
		var done = 0;
		foreach (var (start, end, kind, content) in InlineCode(text, continues, definitions: definitions))
		{
			prose.Append(text, done, start - done);
			done = end;
			if (kind == InlineKind.AngleDestination)
			{
				prose.Append(BareDestination(text[(start + 1)..(end - 1)]));
				continue;
			}
			if (kind is InlineKind.BareDestination or InlineKind.DefinitionText)
			{
				prose.Append(text, start, end - start);
				continue;
			}
			if (kind == InlineKind.Comment)
				continue;
			out_.Append(EscapeText(NeutralizeLinks(prose.ToString(), atLineStart), liquid));
			out_.Append(CodeSpanRisk.IsMatch(content!) ? CodeHtml(SpanText(content!)) : text[start..end]);
			prose.Clear();
			atLineStart = false;
		}
		prose.Append(text, done, text.Length - done);
		out_.Append(EscapeText(NeutralizeLinks(prose.ToString(), atLineStart), liquid));
		// Last, so a fence marker that a dropped comment brought to the start of a line is escaped too.
		return string.Join("\n", out_.ToString().Split('\n').Select(EscapeFenceMarker));
	}

	// A code span's content on one line, as CommonMark renders it. Each later line continues the span's paragraph, so whatever starts it
	// before its text is quote markers and indentation: a ">" there that wasn't a marker would open a quote and end the paragraph.
	static string SpanText(string content)
	{
		var lines = content.Split('\n');
		return string.Join(" ", lines.Take(1).Concat(lines.Skip(1).Select(line => line.TrimStart(' ', '\t', '>'))));
	}

	// A single line of untrusted text, such as a PR title or commit subject, made safe to publish. It's rendered in a heading, table cell or
	// commit list item, where no reference definition can start (RenderCommits escapes a subject that would start one), so none is read unless
	// definitions is set.
	public static string SanitizeInline(string text, bool liquid = false, bool definitions = false) =>
			SanitizeText(text, liquid: liquid, definitions: definitions);

	static readonly Regex InfoWord = At("[A-Za-z0-9_+#.-]+");

	// A fenced code block's lines, read the same way by CommonMark and kramdown. The fence is longer than any backtick run in the code, so no
	// line inside closes it, and it always has an info string, so it can't close a fence left open before it.
	static List<string> CodeFence(List<string> content, string info)
	{
		var longest = content.SelectMany(line => Backticks.Matches(line)).Select(run => run.Length).DefaultIfEmpty(0).Max();
		var fence = new string('`', Math.Max(3, longest + 1));
		var word = Match(InfoWord, Py.Strip(info));
		return [fence + (word?.Value ?? "text"), .. content, fence];
	}

	// The fenced and indented code blocks in Markdown lines, as ScanBlocks() lists them. Indented code lines separated only by blank lines in
	// their quotes and list items are one block, the blank lines included. continued is passed on to ScanBlocks().
	static List<CodeBlock> CodeBlocks(IReadOnlyList<string> lines, HashSet<int>? continued = null)
	{
		List<CodeBlock> blocks = [];
		ScanBlocks([.. lines], [], blocks: blocks, continued: continued);
		List<CodeBlock> merged = [];
		foreach (var block in blocks)
		{
			var last = merged.Count > 0 ? merged[^1] : null;
			if (block.Kind == "indented" && block.Joins && last is not null && last.Kind == "indented")
			{
				var gap = block.Lines[0] - last.Lines[^1] - 1;
				last.Content.AddRange(Enumerable.Repeat("", gap).Concat(block.Content));
				last.Lines.Add(block.Lines[^1]);
			}
			else
				merged.Add(block);
		}
		return merged;
	}

	static readonly Regex BlankRuns = R(@"\n{3,}");

	// Untrusted Markdown, such as a PR description, made safe to publish on GitHub Pages.
	//
	// Every code block, nested or not, becomes a top-level fence (a block in a list or quote moves out of it), with a blank line on each side;
	// everything else is text for SanitizeText. The document is scanned once, so each run of text is paired in its place. Runs of blank lines
	// in the text become one; the code keeps its own.
	public static string SanitizeMarkdown(string markdown)
	{
		var lines = LineEnding.Split(markdown);
		HashSet<int> continued = [];
		Dictionary<int, CodeBlock> starts = [];
		foreach (var block in CodeBlocks(lines, continued))
			starts[block.Lines[0]] = block;

		string Text(int first, int end)
		{
			var sanitized = SanitizeText(string.Join("\n", lines[first..end]), [.. Enumerable.Range(first, end - first).Select(continued.Contains)]);
			return BlankRuns.Replace(sanitized, "\n\n").Trim('\n');
		}

		List<string> chunks = [];
		int start = 0, index = 0;
		while (index < lines.Length)
		{
			if (!starts.TryGetValue(index, out var block))
			{
				index++;
				continue;
			}
			chunks.AddRange([Text(start, index), string.Join("\n", CodeFence(block.Content, block.Info))]);
			start = index = block.Lines[^1] + 1;
		}
		chunks.Add(Text(start, lines.Length));
		return string.Join("\n\n", chunks.Where(chunk => chunk.Length > 0));
	}

	static readonly Regex EndrawOpener = R($@"\{{%(?=-?{PyS}*endraw)");

	// Text that can sit inside {% raw %}: each "{%" that would read as {% endraw %} is printed by Liquid instead.
	public static string BreakLiquidRaw(string text) => EndrawOpener.Replace(text, "{% endraw %}{{ '{%' }}{% raw %}");

	// Re-levels the ATX headings in text inserted under a "## " section.
	//
	// Distinct levels keep their order but close up (#, ####, ###### become ###, ####, #####), and no heading sits more than one level below
	// the one before it, so the post keeps a single H1 and never skips a level. Setext headings (a paragraph over a === or --- line, by
	// CommonMark's paragraph rules) become ATX headings first. Headings inside block quotes and list items are re-levelled too, keeping their
	// markers. Fenced code and raw HTML blocks are left alone.
	public static string NestHeadings(string markdown)
	{
		List<string?> lines = [.. LineEnding.Split(markdown)]; // CommonMark's three line endings; PR bodies often use CRLF
		List<MarkdownHeading> headings = []; // in document order
		ScanBlocks(lines, headings);
		var rank = headings.Select(h => h.Level).Distinct().Order().Select((level, i) => (level, i)).ToDictionary(p => p.level, p => p.i);
		var previous = SectionLevel;
		foreach (var (index, prefix, level, rest) in headings)
		{
			var newLevel = Math.Min(Math.Min(SectionLevel + 1 + rank[level], previous + 1), 6);
			lines[index] = prefix + new string('#', newLevel) + rest;
			previous = newLevel;
		}
		return string.Join("\n", lines.Where(line => line is not null));
	}

	public static string RenderPost(JsonNode? pr, string titleLine, string tag, string mergedDate, IReadOnlyList<JsonNode?> commits,
			IReadOnlyList<JsonNode?> files, string? summary, string model)
	{
		var number = Str(pr is JsonObject o && o.TryGetPropertyValue("number", out var n) ? n : throw new KeyNotFoundException("number"));
		// A YAML double-quoted scalar: escape backslashes before quotes.
		var safeTitle = titleLine.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
		var aiNote = "Generated by release automation from the PR title, description, commits and changed files. "
				+ (!string.IsNullOrEmpty(summary) ? $"Includes an AI summary written by {model}." : "No AI summary.");
		var frontMatter = string.Join("\n",
		[
			"---",
			$"post_title: \"{safeTitle}\"",
			"author1: mpaulosky",
			$"post_slug: \"{PyLower(tag)}-pr-{number}\"",
			"microsoft_alias: n/a",
			"categories:",
			"  - engineering",
			"tags:",
			$"  - release:{PyLower(tag)}",
			"  - automation",
			$"ai_note: \"{aiNote}\"",
			$"summary: \"Release notes seed for {tag} from PR #{number}.\"",
			$"post_date: \"{mergedDate}\"",
			"---",
			"",
		]);
		List<string> sections =
		[
			$"# {SanitizeInline(titleLine)}\n\n- **Release tag:** `{tag}`\n- **Source PR:** [#{number}]({Text(Get(pr, "html_url")) ?? ""})\n",
		];
		if (!string.IsNullOrEmpty(summary))
			sections.Add($"## Summary\n\n{NestHeadings(SanitizeMarkdown(summary))}\n");
		var body = Py.Strip(Text(Get(pr, "body")) ?? "");
		if (body.Length == 0)
			body = "No PR description was provided.";
		sections.Add($"## PR description\n\n{NestHeadings(SanitizeMarkdown(body))}\n");
		sections.Add(RenderCommits(commits));
		sections.Add(RenderFiles(files));
		// Jekyll runs Liquid over the page first, even inside code; raw keeps the text as written. The tags sit in HTML comments, so neither
		// GitHub's view of the file nor the Pages site shows them.
		return frontMatter + "<!-- {% raw %} -->\n" + BreakLiquidRaw(string.Join("\n", sections)) + "\n<!-- {% endraw %} -->\n";
	}

	static readonly Regex LinkedPost = R(@"\]\(([^)]+\.md)\)");

	public static void UpdateBlogIndex(string blogDir, string mergedDate, string titleLine, string postName)
	{
		var blogIndex = Path.Combine(blogDir, "README.md");
		var existing = Exists(blogIndex) ? ReadText(blogIndex) : "";

		// Keep the post rows, skipping the header and its separator. Matching the header's first cell exactly keeps posts whose title
		// mentions "Date".
		List<string> rows = [];
		// Cut at Markdown's line endings only: splitlines() would cut a row at a U+2028 or U+2029 in a title, and neither half would be kept.
		foreach (var raw in LineEnding.Split(existing))
		{
			var line = Py.Strip(raw);
			if (!(line.StartsWith('|') && line.EndsWith('|')))
				continue;
			var firstCell = Py.Strip(line.Trim('|').Split('|')[0]);
			if (firstCell == "Date" || firstCell.All(c => c is '-' or ':' or ' '))
				continue;
			rows.Add(line);
		}

		// Drop this post's old row, and rows whose post was deleted (WritePost removes a PR's post when its title or merge date gives it a
		// new name).
		bool LinkedPostExists(string row)
		{
			var linked = LinkedPost.Match(row);
			return !linked.Success || Exists(Path.Combine(blogDir, linked.Groups[1].Value));
		}

		var rowTitle = LinkLabel(SanitizeInline(titleLine, liquid: true)).Replace("|", "\\|", StringComparison.Ordinal);
		rows = [.. rows.Where(r => !r.Contains($"({postName})", StringComparison.Ordinal) && LinkedPostExists(r))];
		rows.Insert(0, $"| {mergedDate} | [{rowTitle}]({postName}) | release,automation |");

		// Stable sort by date only, so same-day rows keep their merge order.
		static string RowDate(string row) => Py.Strip(row.Trim('|').Split('|')[0]);
		rows = [.. rows.OrderByDescending(RowDate, Py.Order)];

		WriteText(blogIndex, string.Join("\n",
		[
			"## Release review posts",
			"",
			"This directory contains concise release-review posts for merged PR releases.",
			"",
			"| Date | Title | Tags |",
			"| ---- | ----- | ---- |",
			.. rows,
			"",
		]));
	}

	static readonly Regex PrOfName = R(@"-pr-(\d+)-");
	static readonly Regex FrontMatter = At(@"---\n(.*?)\n---", RegexOptions.Singleline);
	static readonly Regex PostTitle = R("^post_title: \"(.*)\"$", RegexOptions.Multiline);
	static readonly Regex PostDate = R("^post_date: \"(.*)\"$", RegexOptions.Multiline);
	static readonly Regex PostTag = R($"^{PyS}*- release:({PyNS}+)$", RegexOptions.Multiline);
	static readonly Regex PostSummary = R("^summary: \"(.*)\"$", RegexOptions.Multiline);

	// The entries of a folder whose names match a pattern, as Path.glob() lists them: files and folders, hidden ones too, in the folder's order.
	static IEnumerable<string> Glob(string folder, Func<string, bool> matches) =>
			Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder).Where(path => matches(Path.GetFileName(path))) : [];

	// pathlib's stem: the name without its last suffix, a leading dot being no suffix.
	static string Stem(string name)
	{
		var dot = name.LastIndexOf('.');
		return dot > 0 && dot < name.Length - 1 ? name[..dot] : name;
	}

	public static List<BlogPost> ReadBlogPosts(string blogDir)
	{
		List<BlogPost> posts = [];
		foreach (var path in Glob(blogDir, name => name.EndsWith(".md", StringComparison.Ordinal)))
		{
			var name = Path.GetFileName(path);
			var pr = PrOfName.Match(name);
			if (!pr.Success)
				continue;
			var text = ReadText(path);
			var front = Match(FrontMatter, text)?.Groups[1].Value ?? "";
			var title = PostTitle.Match(front);
			var date = PostDate.Match(front);
			var tag = PostTag.Match(front);
			var summary = PostSummary.Match(front);
			posts.Add(new BlogPost(
					name,
					pr.Groups[1].Value,
					title.Success ? title.Groups[1].Value.Replace("\\\"", "\"", StringComparison.Ordinal) : Stem(name),
					date.Success ? date.Groups[1].Value : Py.Head(name, 10),
					tag.Success ? tag.Groups[1].Value : "",
					PostExcerpt(text, summary.Success ? summary.Groups[1].Value.Replace("\\\"", "\"", StringComparison.Ordinal) : "")));
		}
		return posts;
	}

	// Blog card excerpts

	public const int ExcerptLength = 200;
	static readonly Regex SeedSummary = R("^Release notes seed for ");
	// Paragraphs that only point at an issue ("Fixes #16.") say nothing on a card.
	static readonly Regex IssueReference = R($@"^(?:(?:{Unicode("fixes|closes|resolves|refs|part of")}){PyS}+#\d+[\s\x1c-\x1f,.]*)+$");

	// A card's excerpt: the AI summary section, a real front matter summary, else the PR description. RenderPost writes the AI summary into a
	// "## Summary" section and a "Release notes seed" line into the front matter, so the seed line is skipped.
	public static string PostExcerpt(string text, string summary = "")
	{
		var aiSummary = FirstParagraph(Section(text, "Summary"));
		if (aiSummary.Length > 0)
			return Shorten(aiSummary);
		if (summary.Length > 0 && !SeedSummary.IsMatch(summary))
			return Shorten(PlainText(summary));
		var description = FirstParagraph(Section(text, "PR description"));
		if (description.Length > 0 && description != "No PR description was provided.")
			return Shorten(description);
		return "";
	}

	// The body of the post's "## {heading}" section, up to the next heading of the same level.
	//
	// Posts written before the title became an H1 start with a "## " title and use "### " for these sections. Only the post's own section level
	// is read, and lines inside fenced code are never headings, so a heading from the PR description (nested, or in a code example) never
	// passes for a section.
	public static string Section(string text, string heading)
	{
		var lines = text.Split('\n');
		var code = CodeLines(lines);
		var headings = Enumerable.Range(0, lines.Length).Where(index => !code.Contains(index) && AtxHeading.IsMatch(lines[index])).ToList();
		if (headings.Count == 0)
			return "";
		var level = lines[headings[0]].StartsWith("# ", StringComparison.Ordinal) ? "##" : "###";
		var starts = headings.Where(index => lines[index].StartsWith(level + " ", StringComparison.Ordinal)).ToList();
		for (var position = 0; position < starts.Count; position++)
		{
			var start = starts[position];
			if (lines[start] == $"{level} {heading}")
			{
				var end = position + 1 < starts.Count ? starts[position + 1] : lines.Length;
				return string.Join("\n", lines[(start + 1)..end]) + (end < lines.Length ? "\n" : "");
			}
		}
		return "";
	}

	static readonly Regex BlankLineSplit = R($@"\n{PyS}*\n");
	static readonly Regex ListItemStart = R($@"^(?:[-*+]|\d+[.)])(?:{PyS}+|$)");

	// The first paragraph of prose or list text, as plain text.
	public static string FirstParagraph(string markdown)
	{
		foreach (var block in BlankLineSplit.Split(BlankCode(markdown)))
		{
			var lines = Py.SplitLines(Py.Strip(block)).Select(Py.Strip).ToList();
			// "&lt;" opens what was an HTML block before the post escaped it, if what follows opens one.
			var html = lines.Count > 0 && lines[0].StartsWith("&lt;", StringComparison.Ordinal) && HtmlBlockEnd("<" + lines[0][4..]) is not null;
			if (lines.Count == 0 || html || new[] { "#", "|", "<!--", ">" }.Any(prefix => lines[0].StartsWith(prefix, StringComparison.Ordinal)))
				continue;
			// Join list items and wrapped lines into one line of text.
			var plain = PlainText(string.Join(" ", lines.Select(line => ListItemStart.Replace(line, "", 1))));
			if (plain.Length > 0 && !IssueReference.IsMatch(plain))
				return plain;
		}
		return "";
	}

	// The Markdown with fenced code and raw HTML block lines blanked, so they split and skip like blank lines.
	static string BlankCode(string markdown)
	{
		var lines = markdown.Split('\n');
		var code = CodeLines(lines);
		return string.Join("\n", lines.Select((line, index) => code.Contains(index) ? "" : line));
	}

	static readonly Regex PlainLink = R(@"!?\[([^\]]*)\]\([^)]*\)");
	static readonly Regex PlainCode = R("`([^`]*)`");
	static readonly Regex PlainStars = R($@"(\*\*|\*)({PyNS}(?:.*?{PyNS})?)\1");
	// Underscores only mark emphasis at word edges, so snake_case names keep theirs.
	static readonly Regex PlainUnderscores = R($@"(?<!{PyW})(__|_)({PyNS}(?:.*?{PyNS})?)\1(?!{PyW})");
	static readonly Regex PlainCodeTags = R("</?code>");
	static readonly Regex PlainEscapes = R(@"\\([!-/:-@\[-`{-~])");
	static readonly Regex Spaces = R(PyS + "+");

	// Drop Markdown link, code and emphasis markup, keeping the words.
	public static string PlainText(string markdown)
	{
		var text = PlainLink.Replace(markdown, "$1");
		text = PlainCode.Replace(text, "$1");
		text = PlainStars.Replace(text, "$2");
		text = PlainUnderscores.Replace(text, "$2");
		// What SanitizeMarkdown escaped reads as the text it stands for.
		text = PlainCodeTags.Replace(text, "");
		text = SafeUnescape(PlainEscapes.Replace(text, "$1"));
		return Py.Strip(Spaces.Replace(text, " "));
	}

	// Cut text to at most limit characters (code points) at a word boundary, marking the cut with an ellipsis.
	public static string Shorten(string text, int limit = ExcerptLength)
	{
		if (Py.Len(text) <= limit)
			return text;
		var head = Py.Head(text, limit - 1);
		var space = head.LastIndexOf(' ');
		var cut = (space < 0 ? head : head[..space]).TrimEnd(' ', ',', ';', ':', '.', '-', '–', '—');
		return cut + "…";
	}

	// Same-day items are ordered by release version, which follows merge order.
	static readonly Comparer<(string Date, (BigInteger, BigInteger, BigInteger) Version)> Newest =
			Comparer<(string Date, (BigInteger, BigInteger, BigInteger) Version)>.Create((a, b) =>
					Py.CompareCodePoints(a.Date, b.Date) is var order and not 0 ? order : a.Version.CompareTo(b.Version));

	public static List<BlogPost> NewestFirst(IEnumerable<BlogPost> posts) => [.. posts.OrderByDescending(p => (p.Date, VersionKey(p.Tag)), Newest)];

	static List<ReleaseEntry> NewestFirst(IEnumerable<ReleaseEntry> entries) => [.. entries.OrderByDescending(e => (e.Date, VersionKey(e.Tag)), Newest)];

	// Release tables

	const string SourcePr = "Source PR: #";

	// The length of the run of decimal digits at text[at], astral ones too, as Python's \d reads them: .NET's \d sees an astral digit (𝟒)
	// as two code units, neither a digit.
	public static int DigitsAt(string text, int at)
	{
		var end = at;
		while (end < text.Length && Rune.TryGetRuneAt(text, end, out var rune) && Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
			end += rune.Utf16SequenceLength;
		return end - at;
	}

	// The PR number in the release body's "Source PR: #n" line, or null: re.search(r"Source PR: #(\d+)"), so the first marker with digits
	// after it, read by their value. draft_release's markers are read the same way, so every parser agrees on which PR a body names.
	public static BigInteger? SourcePrOf(JsonNode? release)
	{
		var body = Text(Get(release, "body")) ?? "";
		for (var at = body.IndexOf(SourcePr, StringComparison.Ordinal); at >= 0; at = body.IndexOf(SourcePr, at, StringComparison.Ordinal))
		{
			at += SourcePr.Length;
			if (DigitsAt(body, at) is var length and > 0)
				return ParseInt(body.Substring(at, length));
		}
		return null;
	}

	// The docs/blogs posts for a PR, sorted by name (so by date). A post belongs to the PR its name's first "-pr-N-" names, as ReadBlogPosts
	// reads it: the title slug after it can hold another ("...-pr-7-revert-pr-4-changes.md" is PR 7's, not PR 4's).
	public static List<string> PostsForPr(string blogDir, string prNumber) =>
			[.. Glob(blogDir, name => name.EndsWith(".md", StringComparison.Ordinal) && PrOfName.Match(name) is { Success: true } pr
					&& pr.Groups[1].Value == prNumber)
				.Order(Py.Order)];

	public static string PostUrl(string repository, string name) => $"https://github.com/{repository}/blob/main/docs/blogs/{name}";

	static string TagName(JsonNode? release) => Text(Get(release, "tag_name")) ?? throw new KeyNotFoundException("tag_name");

	// The newest releases, newest first. current is a release that is being created now and so isn't in the Releases API response yet (its
	// Tag, Date, Pr and Title).
	public static List<ReleaseEntry> ReleaseEntries(IReleaseGitHub gh, string repository, string blogDir, ReleaseEntry? current = null)
	{
		List<ReleaseEntry> entries = current is null ? [] : [new ReleaseEntry { Tag = current.Tag, Date = current.Date, Pr = current.Pr, Title = current.Title }];
		foreach (var release in gh.Releases())
		{
			// On a re-run the release being created is already in the response.
			if (current is not null && TagName(release) == current.Tag)
				continue;
			var sourcePr = SourcePrOf(release);
			entries.Add(new ReleaseEntry
			{
				Tag = TagName(release),
				Date = Py.Head(Text(Get(release, "published_at")) ?? "", 10),
				Pr = sourcePr is { } pr && !pr.IsZero ? pr.ToString(CultureInfo.InvariantCulture) : "",
				Title = "",
				Name = Text(Get(release, "name")) is { Length: > 0 } name ? name : TagName(release),
			});
		}
		// Sort before slicing: a re-run for an older PR must not pin its row above newer releases.
		entries = [.. NewestFirst(entries).Take(TableSize)];

		foreach (var entry in entries)
		{
			if (entry.Title.Length == 0 && entry.Pr.Length > 0)
			{
				try
				{
					entry.Title = gh.PullTitle(BigInteger.Parse(entry.Pr, CultureInfo.InvariantCulture));
				}
				catch (CalledProcessError)
				{
					entry.Title = "";
				}
			}
			if (entry.Title.Length == 0)
				entry.Title = !string.IsNullOrEmpty(entry.Name) ? entry.Name : entry.Tag;
			entry.Url = $"https://github.com/{repository}/releases/tag/{entry.Tag}";
			entry.PostUrl = "";
			if (entry.Pr.Length > 0 && PostsForPr(blogDir, entry.Pr) is { Count: > 0 } posts)
				entry.PostUrl = PostUrl(repository, Path.GetFileName(posts[^1]));
		}
		return entries;
	}

	public static string RenderReleasesMarkdown(IEnumerable<ReleaseEntry> entries)
	{
		List<string> rows = [];
		foreach (var entry in entries)
		{
			var blogCell = entry.PostUrl.Length > 0 ? $"[Post]({entry.PostUrl})" : "—";
			var title = SanitizeInline(entry.Title, liquid: true).Replace("|", "\\|", StringComparison.Ordinal);
			rows.Add($"| [{entry.Tag}]({entry.Url}) | {entry.Date} | {title} | {blogCell} |");
		}
		return string.Join("\n",
		[
			"<!-- RELEASES_START -->",
			"",
			"| Version | Date | Title | Blog post |",
			"| ------- | ---- | ----- | --------- |",
			.. rows,
			"",
			"<!-- RELEASES_END -->",
		]);
	}

	static string Link(string href, string text) => $"<a href=\"{Html.Escape(href)}\">{Html.Escape(text)}</a>";

	static List<string> RenderTable(string[] headers, List<string[]> rows, string empty)
	{
		List<string> lines = ["<div class=\"table-wrap\">", "  <table>", "    <thead>", "      <tr>"];
		lines.AddRange(headers.Select(h => $"        <th>{Html.Escape(h)}</th>"));
		lines.AddRange(["      </tr>", "    </thead>", "    <tbody>"]);
		if (rows.Count == 0)
			lines.Add($"      <tr><td colspan=\"{headers.Length}\">{Html.Escape(empty)}</td></tr>");
		foreach (var cells in rows)
		{
			lines.Add("      <tr>");
			lines.AddRange(cells.Select(cell => $"        <td>{cell}</td>"));
			lines.Add("      </tr>");
		}
		lines.AddRange(["    </tbody>", "  </table>", "</div>"]);
		return lines;
	}

	public static List<string> RenderReleasesHtml(IEnumerable<ReleaseEntry> entries) =>
			RenderTable(["Version", "Date", "Title", "Blog post"],
					[.. entries.Select(e => new[] { Link(e.Url, e.Tag), Html.Escape(e.Date), Html.Escape(e.Title), e.PostUrl.Length > 0 ? Link(e.PostUrl, "Post") : "—" })],
					"No releases yet.");

	static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

	// "2026-09-27" as "27 Sep 2026", or the text unchanged when it isn't an ISO date (datetime.date.fromisoformat()'s forms).
	public static string DisplayDate(string isoDate) =>
			FromIsoFormat(isoDate) is { } date
					? $"{date.Day:00} {Months[date.Month - 1]} {date.Year.ToString(CultureInfo.InvariantCulture)}".TrimStart('0')
					: isoDate;

	// date.fromisoformat() as Python 3.12's C code reads it: YYYY-MM-DD, YYYYMMDD, YYYY-Www, YYYYWww, YYYY-Www-D or YYYYWwwD, in ASCII digits,
	// the length counted in UTF-8 bytes (7, 8 or 10) and anything past the date's own characters ignored, then the date checked; null when it
	// isn't one.
	static DateOnly? FromIsoFormat(string text)
	{
		byte[] bytes;
		try
		{
			bytes = Utf8.GetBytes(text);
		}
		catch (EncoderFallbackException)
		{
			return null;
		}
		if (bytes.Length is not (7 or 8 or 10))
			return null;
		var p = 0;
		bool Digits(int count, out int value)
		{
			value = 0;
			for (var i = 0; i < count; i++, p++)
			{
				if (p >= bytes.Length || bytes[p] is < (byte)'0' or > (byte)'9')
					return false;
				value = value * 10 + bytes[p] - '0';
			}
			return true;
		}
		if (!Digits(4, out var year))
			return null;
		var separator = p < bytes.Length && bytes[p] == '-';
		if (separator)
			p++;
		try
		{
			if (p < bytes.Length && bytes[p] == 'W')
			{
				p++;
				if (!Digits(2, out var week))
					return null;
				var day = 1;
				if (p < bytes.Length)
				{
					if (separator && bytes[p++] != '-')
						return null;
					if (!Digits(1, out day))
						return null;
				}
				if (week <= 0 || week > ISOWeek.GetWeeksInYear(year) || day is <= 0 or >= 8)
					return null;
				return DateOnly.FromDateTime(ISOWeek.ToDateTime(year, week, day == 7 ? DayOfWeek.Sunday : (DayOfWeek)day));
			}
			if (!Digits(2, out var month))
				return null;
			if (separator && (p >= bytes.Length || bytes[p++] != '-'))
				return null;
			if (!Digits(2, out var dayOfMonth))
				return null;
			return new DateOnly(year, month, dayOfMonth);
		}
		catch (ArgumentOutOfRangeException)
		{
			return null;
		}
	}

	// The newest posts as cards: date, release, linked title, excerpt and source PR.
	public static List<string> RenderBlogsHtml(IEnumerable<BlogPost> posts, string repository)
	{
		var newest = NewestFirst(posts).Take(TableSize).ToList();
		if (newest.Count == 0)
			return ["<p class=\"post-empty\">No blog posts yet.</p>"];
		List<string> lines = ["<ul class=\"post-grid\">"];
		foreach (var p in newest)
		{
			var meta = $"<time datetime=\"{Html.Escape(p.Date)}\">{Html.Escape(DisplayDate(p.Date))}</time>";
			if (p.Tag.Length > 0)
				meta += $"<span class=\"post-tag\">{Html.Escape(p.Tag)}</span>";
			lines.AddRange(
			[
				"  <li class=\"post-card\">",
				"    <article>",
				$"      <p class=\"post-meta\">{meta}</p>",
				$"      <h3 class=\"post-title\">{Link(PostUrl(repository, p.File), p.Title)}</h3>",
			]);
			if (p.Excerpt.Length > 0)
				lines.Add($"      <p class=\"post-excerpt\">{Html.Escape(p.Excerpt)}</p>");
			var prLink = Link($"https://github.com/{repository}/pull/{p.Pr}", $"PR #{p.Pr}");
			lines.AddRange([$"      <p class=\"post-source\">{prLink}</p>", "    </article>", "  </li>"]);
		}
		lines.Add("</ul>");
		return lines;
	}

	// Replace what's between <!-- {name}_START/END --> with lines, or null without markers.
	public static string? ReplaceBetween(string text, string name, IEnumerable<string> lines)
	{
		var marker = Regex.Escape(name);
		var match = R($"^(?<indent>[ \\t]*)<!-- {marker}_START -->.*?<!-- {marker}_END -->", RegexOptions.Singleline | RegexOptions.Multiline).Match(text);
		if (!match.Success)
			return null;
		var indent = match.Groups["indent"].Value;
		var block = string.Join("\n", lines.Select(line => indent + line).Prepend($"{indent}<!-- {name}_START -->").Append($"{indent}<!-- {name}_END -->"));
		return text[..match.Index] + block + text[(match.Index + match.Length)..];
	}

	// Markdown with each run of empty lines outside code cut to one; code keeps its own (mpaulosky/dotfiles#58).
	static string CollapseBlankLines(string markdown)
	{
		var lines = markdown.Split('\n');
		HashSet<int> code = [.. CodeBlocks(lines).SelectMany(block => Enumerable.Range(block.Lines[0], block.Lines[^1] - block.Lines[0] + 1))];
		List<string> out_ = [];
		for (var index = 0; index < lines.Length; index++)
		{
			if (!(lines[index].Length == 0 && out_.Count > 0 && out_[^1].Length == 0 && !code.Contains(index)))
				out_.Add(lines[index]);
		}
		return string.Join("\n", out_);
	}

	static readonly Regex LegacyBlog = R($@"(?:^## Dev Blog[^\n]*\n{PyS}*)?<!-- BLOG_START -->.*?<!-- BLOG_END -->\n?", RegexOptions.Singleline | RegexOptions.Multiline);
	static readonly Regex ReleasesBlock = R("<!-- RELEASES_START -->.*?<!-- RELEASES_END -->", RegexOptions.Singleline);
	static readonly Regex About = R(@"^## About[^\n]*\n", RegexOptions.Multiline);
	static readonly Regex NextHeading = R("^## ", RegexOptions.Multiline);

	public static string UpdateReadme(string readme, string releasesBlock, string repository)
	{
		// Migrate READMEs written by the previous workflow: drop the legacy Dev Blog section and its BLOG_START/BLOG_END block.
		readme = LegacyBlog.Replace(readme, "");
		readme = Py.Strip(readme).Length > 0 ? CollapseBlankLines(readme).Trim('\n') + "\n" : "";

		if (readme.Contains("<!-- RELEASES_START -->", StringComparison.Ordinal) && readme.Contains("<!-- RELEASES_END -->", StringComparison.Ordinal))
			return ReleasesBlock.Replace(readme, _ => releasesBlock);

		var releasesSection = "## Releases\n\n" + releasesBlock + $"\n\n[All releases →](https://github.com/{repository}/releases)\n";
		var about = About.Match(readme);
		if (about.Success)
		{
			var afterAbout = about.Index + about.Length;
			var nextHeading = NextHeading.Match(readme[afterAbout..]);
			if (nextHeading.Success)
			{
				var insertAt = afterAbout + nextHeading.Index;
				return readme[..insertAt] + releasesSection + "\n" + readme[insertAt..];
			}
		}
		var suffix = readme.EndsWith('\n') || readme.Length == 0 ? "" : "\n";
		var separator = readme.Length > 0 ? "\n" : "";
		return readme + suffix + separator + releasesSection;
	}

	// path is the file, and shown is how a message names it (str() of the Python's Path).
	static void UpdateIndexHtml(string path, string shown, List<ReleaseEntry> entries, List<BlogPost> posts, string repository, TextWriter log)
	{
		if (!Exists(path))
		{
			Log(log, $"::notice::{shown} not found; skipping the Pages tables.");
			return;
		}
		var text = ReadText(path);
		foreach (var (name, lines) in new[] { ("RELEASES_HTML", RenderReleasesHtml(entries)), ("BLOGS_HTML", RenderBlogsHtml(posts, repository)) })
		{
			if (ReplaceBetween(text, name, lines) is { } updated)
				text = updated;
			else
				Log(log, $"::notice::{shown} has no {name}_START/END markers; leaving that table alone.");
		}
		WriteText(path, text);
	}

	// docs/README.md links

	// README.md links by path from the repository root (docs/blogs/post.md, src/Web); copied verbatim into docs/ they would resolve one
	// directory too deep. From TicketManager#104.

	// What follows an inline link's destination: an optional "title", 'title' or (title), then the closing parenthesis. As in LinkSpace, each
	// space between them may hold a line ending and the next line's containers.
	static readonly Regex LinkInlineTail = At(
			@"(?:(?:[ \t]*\r?\n[ \t>]*|[ \t]+)(?:""(?:[^""\\]|\\[\s\S])*""|'(?:[^'\\]|\\[\s\S])*'|\((?:[^()\\]|\\[\s\S])*\)))?"
			+ @"[ \t]*(?:\r?\n[ \t>]*)?\)");
	static readonly Regex LinkAngleDestination = At(@"<((?:[^<>\n\\]|\\.)+)>");
	// cmark's limit on nested parentheses in a bare destination.
	const int LinkParenDepth = 32;
	// Not preceded by a name character, so data-src is not src, but a tag's continuation line may start with one. ASCII, as for HtmlBlocks:
	// "\u017f" must not case-fold into "src".
	static readonly Regex LinkHtmlAttribute = R(
			$@"(?<![A-Za-z0-9_-])((?:{NoCase("src|href")}){AS}*={AS}*)(?:""([^""]+)""|'([^']+)'|([^ \t\n\r\f\v""'=<>`]+))");
	static readonly Regex AbsoluteTarget = R(@"^[a-zA-Z\u0130\u0131\u017f\u212a][a-zA-Z0-9+.\-\u0130\u0131\u017f\u212a]*:");

	// A link target as seen from docs/ rather than the repository root.
	public static string RebaseLink(string target)
	{
		if (AbsoluteTarget.IsMatch(target) || target.StartsWith('#') || target.StartsWith('?') || target.StartsWith('/'))
			return target;
		var path = target.StartsWith("./", StringComparison.Ordinal) ? target[2..] : target;
		if (path == "docs" || path.StartsWith("docs/", StringComparison.Ordinal) || path.StartsWith("docs#", StringComparison.Ordinal)
				|| path.StartsWith("docs?", StringComparison.Ordinal))
		{
			var rebased = path["docs".Length..].TrimStart('/');
			return rebased.Length > 0 ? rebased : "./";
		}
		return "../" + path;
	}

	// The end of the bare link destination at line[start], or null if there is none.
	//
	// It runs to the first space or control character, or the first ")" that closes no "(" of its own, so docs/a_(b).md is one destination; a
	// backslash escapes the next character. Parentheses nest at most LinkParenDepth deep, as CommonMark lets a parser limit them: each "](" is
	// read up to there at most, so text full of them is still read in linear time.
	static int? BareDestinationEnd(string line, int start)
	{
		var depth = 0;
		var pos = start;
		while (pos < line.Length)
		{
			var c = line[pos];
			if (c == '\\' && pos + 1 < line.Length && !Py.IsSpace(line[pos + 1]))
			{
				pos += 2;
				continue;
			}
			if (c <= ' ' || c == '\x7f')
				break;
			if (c == '(')
			{
				depth++;
				if (depth > LinkParenDepth)
					return null;
			}
			else if (c == ')')
			{
				if (depth == 0)
					break;
				depth--;
			}
			pos++;
		}
		if (depth > 0 || pos == start || line[start] == '<')
			return null;
		return pos;
	}

	// The line with each ](destination) rebased; anything that isn't a whole inline link is left alone.
	static string RebaseInlineLinks(string line)
	{
		StringBuilder out_ = new();
		var done = 0;
		var opener = line.IndexOf("](", StringComparison.Ordinal);
		while (opener >= 0)
		{
			var start = opener + 2;
			while (start < line.Length && line[start] is ' ' or '\t')
				start++;
			int targetStart;
			int? targetEnd, end;
			if (Match(LinkAngleDestination, line, start) is { } angle)
			{
				targetStart = angle.Groups[1].Index;
				targetEnd = angle.Groups[1].Index + angle.Groups[1].Length;
				end = angle.Index + angle.Length;
			}
			else
			{
				targetStart = start;
				targetEnd = end = BareDestinationEnd(line, start);
			}
			var tail = end is { } e ? Match(LinkInlineTail, line, e) : null;
			if (tail is not null)
			{
				out_.Append(line, done, targetStart - done).Append(RebaseLink(line[targetStart..targetEnd!.Value]));
				done = targetEnd.Value;
				opener = line.IndexOf("](", tail.Index + tail.Length, StringComparison.Ordinal);
			}
			else
				opener = line.IndexOf("](", opener + 2, StringComparison.Ordinal);
		}
		return out_.Append(line, done, line.Length - done).ToString();
	}

	// A src= or href= attribute with its value rebased, keeping its spacing and quotes.
	static string RebaseHtmlAttribute(Match match)
	{
		foreach (var (group, quote) in new[] { (2, "\""), (3, "'"), (4, "") })
		{
			if (match.Groups[group].Success)
				return match.Groups[1].Value + quote + RebaseLink(match.Groups[group].Value) + quote;
		}
		return match.Value;
	}

	static string RebaseHtmlAttributes(string text) => LinkHtmlAttribute.Replace(text, RebaseHtmlAttribute);

	// Lines of Markdown text with their links rebased; code spans, which may cross lines, are left alone.
	//
	// Links are found as InlineCode() finds them: a reference definition is read before any code span, so code in its label doesn't hide its
	// destination, and so is an inline link's destination and title, so code in its title doesn't either (mpaulosky/dotfiles#58). A "](" with
	// no open label is text, and so is a line that only starts like a definition. An inline HTML tag is read whole, as GitHub reads it, so a
	// "]" in its attributes closes no label. continues is as for ParagraphBreaks(), and prefixes as for InlineCode(); a caller that scanned the
	// whole document passes both, otherwise the lines are scanned on their own.
	static List<string> RebaseTextLinks(IReadOnlyList<string> lines, IReadOnlyList<bool>? continues = null, IReadOnlyList<int>? prefixes = null)
	{
		if (lines.Count == 0)
			return [];
		if (continues is null)
		{
			HashSet<int> continued = [];
			Dictionary<int, int> found = [];
			ScanBlocks([.. lines], [], continued: continued, prefixes: found);
			continues = [.. Enumerable.Range(0, lines.Count).Select(continued.Contains)];
			prefixes = [.. Enumerable.Range(0, lines.Count).Select(index => found.GetValueOrDefault(index))];
		}
		var text = string.Join("\n", lines);
		StringBuilder out_ = new();
		var done = 0;
		foreach (var (start, end, kind, _) in InlineCode(text, continues, html: true, prefixes: prefixes))
		{
			out_.Append(RebaseHtmlAttributes(text[done..start]));
			if (kind == InlineKind.AngleDestination)
				out_.Append('<').Append(RebaseLink(text[(start + 1)..(end - 1)])).Append('>');
			else if (kind == InlineKind.BareDestination)
				out_.Append(RebaseLink(text[start..end]));
			else
				out_.Append(text, start, end - start);
			done = end;
		}
		out_.Append(RebaseHtmlAttributes(text[done..]));
		return [.. out_.ToString().Split('\n')];
	}

	// The README text with every relative link rebased for docs/README.md; code is left alone.
	//
	// Fenced and indented code blocks, at any depth, are copied as they are, and so are code spans. In a raw HTML block backticks are text, so
	// its whole line is rebased. The README is scanned once, so each run of text is paired in its place.
	public static string RebaseReadmeLinks(string markdown)
	{
		var lines = markdown.Split('\n');
		HashSet<int> raw = [];
		List<CodeBlock> blocks = [];
		HashSet<int> continued = [];
		Dictionary<int, int> prefixes = [];
		ScanBlocks([.. lines], [], raw, blocks, continued, prefixes);
		HashSet<int> code = [.. blocks.SelectMany(block => block.Lines)];

		List<string> Text(int first, int end) => RebaseTextLinks(lines[first..end], [.. Enumerable.Range(first, end - first).Select(continued.Contains)],
				[.. Enumerable.Range(first, end - first).Select(index => prefixes.GetValueOrDefault(index))]);

		List<string> out_ = [];
		var start = 0; // the first line of the run of text since the last code or HTML line
		for (var index = 0; index < lines.Length; index++)
		{
			if (!code.Contains(index) && !raw.Contains(index))
				continue;
			out_.AddRange(Text(start, index));
			start = index + 1;
			out_.Add(code.Contains(index) ? lines[index] : RebaseHtmlAttributes(RebaseInlineLinks(lines[index])));
		}
		out_.AddRange(Text(start, lines.Length));
		return string.Join("\n", out_);
	}

	// Write the post for a merged PR and its blog index row; return (merged date, title line).
	public static (string MergedDate, string TitleLine) WritePost(IReleaseGitHub gh, long prNumber, string tag, string root = ".", string? apiKey = null,
			string model = DefaultModel, UrlOpen? urlopen = null, TextWriter? log = null)
	{
		log ??= Console.Out;
		var pr = gh.Pull(prNumber);
		var commits = gh.Commits(prNumber);
		var files = gh.Files(prNumber);

		var titleLine = Py.Strip(Text(Get(pr, "title")) ?? "");
		if (titleLine.Length == 0)
			titleLine = $"PR #{prNumber}";
		var mergedDate = Py.Head(Text(Get(pr, "merged_at")) ?? "", 10);
		if (mergedDate.Length == 0)
			mergedDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

		string? summary = null;
		var author = Text(Get(Get(pr, "user"), "login")) ?? "";
		if (NoSummaryAuthors.Contains(author))
			Log(log, $"::notice::PR #{prNumber} is by {author}; writing the post without an AI summary.");
		else if (!string.IsNullOrEmpty(apiKey))
			summary = RequestSummary(apiKey, model, BuildPrompt(pr, commits, BuildDiff(files)), urlopen, log);
		else
			Log(log, "::notice::ANTHROPIC_API_KEY is not set; writing the post without an AI summary.");

		var blogDir = Path.Combine(root, "docs", "blogs");
		Directory.CreateDirectory(blogDir);
		var postName = $"{mergedDate}-pr-{prNumber}-{Slugify(titleLine)}.md";
		// A renamed PR, or an older post dated differently, would otherwise leave two posts for one PR.
		foreach (var oldPost in PostsForPr(blogDir, prNumber.ToString(CultureInfo.InvariantCulture)))
		{
			if (Path.GetFileName(oldPost) != postName)
			{
				File.Delete(oldPost);
				Log(log, $"Removed docs/blogs/{Path.GetFileName(oldPost)}");
			}
		}
		WriteText(Path.Combine(blogDir, postName), RenderPost(pr, titleLine, tag, mergedDate, commits, files, summary, model));
		Log(log, $"Wrote docs/blogs/{postName}");
		UpdateBlogIndex(blogDir, mergedDate, titleLine, postName);
		return (mergedDate, titleLine);
	}

	// Rewrite the README, docs/README.md and docs/index.html release and blog tables.
	public static void UpdateTables(string repository, IReleaseGitHub gh, string root = ".", ReleaseEntry? current = null, TextWriter? log = null)
	{
		log ??= Console.Out;
		var blogDir = Path.Combine(root, "docs", "blogs");
		// The Releases tables come from the GitHub Releases API, not the blog index, so releases without a blog post are listed too.
		var entries = ReleaseEntries(gh, repository, blogDir, current);

		var readmePath = Path.Combine(root, "README.md");
		var readme = Exists(readmePath) ? ReadText(readmePath) : "";
		readme = UpdateReadme(readme, RenderReleasesMarkdown(entries), repository);
		WriteText(readmePath, readme);
		// Only a repo that publishes its README as the Pages landing page keeps this copy; don't create one in a repo whose docs site has its own.
		var docsReadme = Path.Combine(root, "docs", "README.md");
		if (Exists(docsReadme))
			WriteText(docsReadme, RebaseReadmeLinks(readme));

		UpdateIndexHtml(Path.Combine(root, "docs", "index.html"), Join(Join(PathText(root), "docs"), "index.html"), entries, ReadBlogPosts(blogDir), repository, log);
	}

	public static void Run(string repository, long prNumber, string tag, IReleaseGitHub gh, string root = ".", string? apiKey = null, string model = DefaultModel,
			UrlOpen? urlopen = null, TextWriter? log = null)
	{
		var (mergedDate, titleLine) = WritePost(gh, prNumber, tag, root, apiKey, model, urlopen, log);
		// This runs before "Create tag and release", so the new release is passed in.
		ReleaseEntry current = new() { Tag = tag, Date = mergedDate, Pr = prNumber.ToString(CultureInfo.InvariantCulture), Title = titleLine };
		UpdateTables(repository, gh, root, current, log);
	}

	public const string Usage = "[-h] --pr PR --tag TAG [--repo REPO]";

	// The repository named by REPOSITORY, else GITHUB_REPOSITORY (an empty one counts as unset), as the scripts' --repo default. getenv
	// reads a variable (the process's environment, or a test's).
	public static string? DefaultRepository(Func<string, string?> getenv) => getenv("REPOSITORY") is { Length: > 0 } repository ? repository : getenv("GITHUB_REPOSITORY");

	// A variable stripped of its blanks, or null when it is unset or only blanks.
	public static string? Setting(string name, Func<string, string?> getenv) => Py.Strip(getenv(name) ?? "") is { Length: > 0 } value ? value : null;

	// release_post.py's main(): the command line, then Run with the gh CLI, the key and the model from the environment. Its log is stdout.
	// gh and getenv stand in for the gh CLI and the environment in a test, and root for the current folder.
	public static int Command(IEnumerable<string> args, TextWriter stdout, TextWriter stderr, Func<string, IReleaseGitHub>? gh = null,
			Func<string, string?>? getenv = null, string root = ".")
	{
		getenv ??= Environment.GetEnvironmentVariable;
		var parser = new Arguments("release_post.cs", Usage, "Write the release blog post for a merged PR and refresh the release tables.")
				.IntOption("--pr", "merged PR number", required: true)
				.Required("--tag", "release tag for the PR, e.g. v0.0.32")
				.Option("--repo", "owner/name (default: $REPOSITORY or $GITHUB_REPOSITORY)");
		if (parser.Parse(args, stdout, stderr) is int exit)
			return exit;
		var repository = parser.Has("--repo") ? parser.Value("--repo", "") : DefaultRepository(getenv);
		if (string.IsNullOrEmpty(repository))
			return parser.Error("--repo is required when REPOSITORY and GITHUB_REPOSITORY are unset", stderr);

		Run(repository, (long)parser.IntValue("--pr", 0), Py.Strip(parser.Value("--tag", "")), (gh ?? (name => new ReleaseGitHub(name)))(repository), root,
				apiKey: Setting("ANTHROPIC_API_KEY", getenv), model: Setting("ANTHROPIC_MODEL", getenv) ?? DefaultModel, log: stdout);
		return 0;
	}
}

// Python's html.escape() and html.unescape().
static class Html
{
	public static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
			.Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#x27;", StringComparison.Ordinal);

	static readonly Regex CharRef = new(@"&(#[0-9]+;?|#[xX][0-9a-fA-F]+;?|[^\t\n\f <&#;]{1,32};?)", RegexOptions.CultureInvariant);

	// The numeric references HTML5 maps to another character (the C1 controls read as windows-1252, NUL as U+FFFD), and the code points it
	// drops.
	static readonly Dictionary<int, string> InvalidCharRefs = new[] { (0x00, "\ufffd"), (0x0d, "\r") }
			.Concat(("\u20ac\u0081\u201a\u0192\u201e\u2026\u2020\u2021\u02c6\u2030\u0160\u2039\u0152\u008d\u017d\u008f"
					+ "\u0090\u2018\u2019\u201c\u201d\u2022\u2013\u2014\u02dc\u2122\u0161\u203a\u0153\u009d\u017e\u0178").Select((c, i) => (0x80 + i, c.ToString())))
			.ToDictionary();

	static bool InvalidCodePoint(int value) =>
			value is >= 0x1 and <= 0x8 or 0xb or >= 0xe and <= 0x1f or >= 0x7f and <= 0x9f or >= 0xfdd0 and <= 0xfdef || (value & 0xfffe) == 0xfffe;

	// html.unescape(): every named and numeric character reference, by HTML5's rules for valid and invalid ones.
	public static string Unescape(string text) => !text.Contains('&') ? text : CharRef.Replace(text, match =>
	{
		var s = match.Groups[1].Value;
		if (s[0] == '#')
		{
			var digits = s.TrimEnd(';');
			var value = digits[1] is 'x' or 'X'
					? BigInteger.Parse("0" + digits[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
					: BigInteger.Parse(digits[1..], CultureInfo.InvariantCulture);
			if (value > 0x10FFFF)
				return "\ufffd";
			var codePoint = (int)value;
			if (InvalidCharRefs.TryGetValue(codePoint, out var replaced))
				return replaced;
			if (codePoint is >= 0xD800 and <= 0xDFFF)
				return "\ufffd";
			return InvalidCodePoint(codePoint) ? "" : char.ConvertFromUtf32(codePoint);
		}
		if (HtmlEntities.Html5.TryGetValue(s, out var named))
			return named;
		// The longest name that starts it, as the standard defines.
		for (var x = s.Length - 1; x > 1; x--)
		{
			if (HtmlEntities.Html5.TryGetValue(s[..x], out var prefix))
				return prefix + s[x..];
		}
		return "&" + s;
	});
}
