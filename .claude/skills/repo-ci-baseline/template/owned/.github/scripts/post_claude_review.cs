#:property PublishAot=false
// Post Claude Review's findings as one PR review on the head commit. The C# port of post_claude_review.py (ADR 0017 in the Application
// Factory), which ships beside it until the post job runs this file.
//
// To be called by .github/workflows/claude-review.yml's post job, once Claude has reviewed the PR, when the job is cut over from the .py
// (mpaulosky/Application_Factory#129, with the Updates of ADR 0017 and the skill's ADR 0004 that give the layout below to this file
// too). The job holds a token that can post a review under the marker, so it will run the base's copy of this file, never the PR's, in the
// layout ADR 0017 gives the two checkers: the file alone, taken from the base with git show into a mktemp -d folder, built there with that
// folder as the cwd, and run as a dll, which reads no global.json and runs no MSBuild (the Factory's test.sh builds and runs it so):
//
//     ( cd "$tmp" && dotnet build post_claude_review.cs -o "$tmp/out" )
//     FINDINGS='{"summary": ..., "findings": [...]}' dotnet "$tmp/out/post_claude_review.dll" --repo owner/name --pr 42 --head <sha>
//
// So it is one file that includes nothing and uses the base class library only, and it carries its own copies of what it needs from
// Gh.cs (Arguments, CalledProcessError, Ran, Script, SurrogateEscape and Py, each held equal to Gh.cs's by a Factory test), in a namespace
// of its own so the Factory's test project compiles them beside Gh.cs's. The test project defines BASELINE_TESTS, which leaves out the
// top-level statement below, as it has an entry point of its own.
//
// FINDINGS is the review step's structured output: a summary, plus a path, line and body for each finding. It may instead, or as well,
// describe the PR in parts, each optional: an overview, changes (strings), files ({path, description}), advisory (strings) and
// not_reviewed, and give each finding a severity (high, medium or low). --effort, optional, is free text for the body's footer.
//
// The body reads like Copilot's review: a headline (Changes recommended or No changes recommended), the title of the most severe finding,
// the open issues (a severity badge and a link to each thread, High first), What changed in this PR folded (the summary as it is, then the
// parts), the advisory and unreviewed notes, the findings outside the diff, and a <sub> footer. The thread links exist only once the review
// is posted, so the body is then edited to add them; if reading the threads or the edit fails, the step warns and the first body, with the
// titles unlinked, stays. Claude never holds a write token; this script, in its own job, is what posts. It posts one COMMENT review pinned
// to the head commit, each finding an inline comment on the new side of the diff, so the PR records it the same way it records Copilot's.
//
// GitHub rejects a whole review if one comment sits on a line outside the diff, so a finding whose line isn't in the diff goes into the
// review body instead. A finding on a changed file whose line isn't a line of that file at all (past its end) is Claude citing the wrong
// number, not a finding about unchanged code: it goes on the file's nearest line in the diff, as a thread, with a note. A body finding opens
// no review thread to hold the merge, so the body's second line is OffDiffMarker, which PR Auto-Merge holds on while that review is the
// latest of the head, past the review cap too. The step passes with a warning: a failed check would leave the PR UNSTABLE, which PR
// Auto-Merge never merges, and a later run with its jobs skipped would replace it anyway. Only malformed findings, a failed API call or
// empty findings fail the step (exit 1): the post job runs only once Claude has answered, so empty findings mean the answer quoted a secret
// of the review job and was withheld. A usage error exits 2, as argparse's did.
//
// The review job's check, inline in claude-review.yml, is what withholds it, and it redacts anything shaped like a credential before the
// answer becomes the FINDINGS the post job's log prints. This script redacts again with the same Credential, which must match the
// workflow's, and warns: those matches are public text from the PR far more often than leaks. The body always starts with Marker: the
// review is posted as github-actions[bot], and the marker is how PR Auto-Merge and the skill's landing decision tell it apart from anything
// else posted under that login.
//
// A comment may carry one GitHub suggestion, a fenced block the reader applies to the commented line with one click, and only a single
// line (VetSuggestions): any other is turned into a plain text fence. So is every suggestion in a comment moved to the nearest diff line,
// which would replace the wrong line, and in a body finding, where none can be applied. A body finding is indented under its bullet so its
// paragraphs stay with it.
//
// Text is read as Python read it: a length or a cut counts code points, \s is str.isspace()'s, \w and int() take any Unicode letter or
// digit, and re.IGNORECASE matches "s" to U+017F and "i" to U+0130 and U+0131. tests/parity/post_claude_review/ in the Factory runs this
// file and the .py on the same cases; its README lists what they leave different.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

#if !BASELINE_TESTS
return Baseline.PostReview.Script.Run((stdout, stderr) =>
		Baseline.PostReview.PostClaudeReview.Run(args, Environment.GetEnvironmentVariable, stdout, stderr));
#endif

namespace Baseline.PostReview
{
	// A finding as parse_findings takes it: Severity is null when the finding gives none.
	sealed record Finding(string Path, BigInteger Line, string Body, string? Severity);

	sealed record FileRow(string Path, string Description);

	// The fields that describe the PR, each null when the answer left it out.
	sealed record Report(string? Summary = null, string? Overview = null, List<string>? Changes = null, List<FileRow>? Files = null,
			List<string>? Advisory = null, string? NotReviewed = null);

	// An inline comment of the review, always on the new side of the diff.
	sealed record Comment(string Path, BigInteger Line, string Body);

	sealed class Review(string commitId, string body, List<Comment> comments)
	{
		public string CommitId { get; } = commitId;
		public string Body { get; set; } = body;
		public List<Comment> Comments { get; } = comments;

		// The review as the API takes it: {"commit_id", "event": "COMMENT", "body", "comments": [{path, line, side, body}]}.
		public string ToJson()
		{
			var json = new StringBuilder("{\"commit_id\": ").Append(Quote(CommitId)).Append(", \"event\": \"COMMENT\", \"body\": ").Append(Quote(Body))
					.Append(", \"comments\": [");
			for (var i = 0; i < Comments.Count; i++)
			{
				var c = Comments[i];
				json.Append(i == 0 ? "" : ", ").Append("{\"path\": ").Append(Quote(c.Path)).Append(", \"line\": ").Append(c.Line.ToString(CultureInfo.InvariantCulture))
						.Append(", \"side\": \"RIGHT\", \"body\": ").Append(Quote(c.Body)).Append('}');
			}
			return json.Append("]}").ToString();
		}

		public static string Quote(string text) => JsonSerializer.Serialize(text);
	}

	// An error Python raised, by its name (KeyError, TypeError, ValueError, AttributeError, JSONDecodeError, UnicodeDecodeError). The linking
	// step catches one raised there (a page of the review's comments that isn't JSON or UTF-8 among them) and warns with the name; one raised
	// anywhere else (DiffLines, HunkStart, REDACTED, a page of the PR's files or the posted review's answer that isn't JSON or UTF-8) ends
	// the step at exit 1 through Script.Run, as the .py's traceback did.
	sealed class PyError(string name, string message) : Exception(message)
	{
		public string Name { get; } = name;
	}

	sealed class MalformedFindings(string message) : Exception(message);

	// What Python's json module gives a script: an object's keys in order, the last of a repeated key winning, an integer of up to the
	// 4,300 digits int() reads, and iteration as Python iterates a value (a list's items, a dict's keys, a str's characters).
	static class PyJson
	{
		// .NET reads 64 levels by default, far fewer than Python's json; 1,000 is Python's default recursion limit, and how deep json reads
		// past it depends on Python's version (the parity suite's README leaves it out).
		static readonly JsonDocumentOptions Options = new() { MaxDepth = 1000 };

		public static JsonElement Parse(string text)
		{
			try
			{
				using var document = JsonDocument.Parse(text, Options);
				var root = document.RootElement.Clone();
				RefuseLongIntegers(root);
				return root;
			}
			catch (JsonException error)
			{
				// Where it failed, in its own words (the line, and the byte in it, as System.Text.Json counts them; Python's message gives a
				// line and a column in characters), and no more: .NET's message quotes a character of the text, which is the answer's (or
				// GitHub's), and Python's never did. A position the reader didn't give is 0.
				throw new PyError("JSONDecodeError", string.Create(CultureInfo.InvariantCulture,
						$"invalid JSON: line {(error.LineNumber ?? -1) + 1} byte {(error.BytePositionInLine ?? -1) + 1}"));
			}
		}

		// json.loads reads every integer with int(), which refuses one of more than 4,300 digits (a ValueError, not a JSONDecodeError)
		// wherever it sits in the document; a float of any length is read.
		static void RefuseLongIntegers(JsonElement value)
		{
			switch (value.ValueKind)
			{
				case JsonValueKind.Array:
					foreach (var item in value.EnumerateArray())
						RefuseLongIntegers(item);
					break;
				case JsonValueKind.Object:
					foreach (var property in value.EnumerateObject())
						RefuseLongIntegers(property.Value);
					break;
				case JsonValueKind.Number:
					var raw = value.GetRawText();
					if (IsIntegerText(raw) && raw.TrimStart('-').Length > 4300)
						throw new PyError("ValueError", "Exceeds the limit (4300 digits) for integer string conversion");
					break;
			}
		}

		// The value of the key, the last one where the object repeats it, as json.loads keeps it.
		public static bool TryGet(JsonElement value, string key, out JsonElement found)
		{
			found = default;
			var any = false;
			if (value.ValueKind != JsonValueKind.Object)
				return false;
			foreach (var property in value.EnumerateObject())
			{
				if (property.Name == key)
				{
					found = property.Value;
					any = true;
				}
			}
			return any;
		}

		public static string? String(JsonElement value, string key) =>
				TryGet(value, key, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;

		// A JSON number json.loads reads as an int: one with no fraction and no exponent (e or E).
		static bool IsIntegerText(string raw) => raw.IndexOfAny(['.', 'e', 'E']) < 0;

		// An integer as json.loads reads one: a number with no fraction and no exponent, of any size up to the 4,300 digits Parse lets
		// through; null for anything else (a float, a bool).
		public static BigInteger? Integer(JsonElement value)
		{
			if (value.ValueKind != JsonValueKind.Number)
				return null;
			var raw = value.GetRawText();
			if (!IsIntegerText(raw))
				return null;
			return BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
		}

		// for item in value: a list's items, a dict's keys and a str's characters as strings; anything else is a TypeError.
		public static IEnumerable<JsonElement> Iterate(JsonElement value)
		{
			switch (value.ValueKind)
			{
				case JsonValueKind.Array:
					return value.EnumerateArray().ToList();
				case JsonValueKind.Object:
					return Keys(value).Select(AsJson).ToList();
				case JsonValueKind.String:
					return value.GetString()!.EnumerateRunes().Select(rune => AsJson(rune.ToString())).ToList();
				default:
					throw new PyError("TypeError", $"'{TypeName(value)}' object is not iterable");
			}
		}

		static IEnumerable<string> Keys(JsonElement value)
		{
			List<string> keys = [];
			foreach (var property in value.EnumerateObject())
			{
				if (!keys.Contains(property.Name))
					keys.Add(property.Name);
			}
			return keys;
		}

		static JsonElement AsJson(string text)
		{
			using var document = JsonDocument.Parse(Review.Quote(text));
			return document.RootElement.Clone();
		}

		public static string TypeName(JsonElement value) => value.ValueKind switch
		{
			JsonValueKind.Object => "dict",
			JsonValueKind.Array => "list",
			JsonValueKind.String => "str",
			JsonValueKind.True or JsonValueKind.False => "bool",
			JsonValueKind.Null => "NoneType",
			_ => Integer(value) is null ? "float" : "int",
		};

		// str(value) of what json.loads gave, as an f-string writes it: a str as it is, None, True and False, an int's digits, a float as
		// repr() writes it, and a list or dict as repr() writes them.
		public static string Str(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : Repr(value);

		static string Repr(JsonElement value) => value.ValueKind switch
		{
			JsonValueKind.String => Py.Repr(value.GetString()!),
			JsonValueKind.Null => "None",
			JsonValueKind.True => "True",
			JsonValueKind.False => "False",
			JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(Repr)) + "]",
			JsonValueKind.Object => "{" + string.Join(", ", Keys(value).Select(key =>
			{
				TryGet(value, key, out var item);
				return $"{Py.Repr(key)}: {Repr(item)}";
			})) + "}",
			_ => Integer(value) is { } integer ? integer.ToString(CultureInfo.InvariantCulture) : FloatRepr(value.GetDouble()),
		};

		// repr(float): the shortest digits that read back as the number (the digits "R" gives), written fixed when the decimal exponent is
		// from -4 to 15 and in e-notation, with a sign and two digits at least, otherwise; "R" itself switches to E-notation at 15.
		static string FloatRepr(double number)
		{
			if (double.IsInfinity(number))
				return number > 0 ? "inf" : "-inf";
			if (number == 0)
				return double.IsNegative(number) ? "-0.0" : "0.0";
			var sign = number < 0 ? "-" : "";
			var shortest = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture);
			// "R" is d.dddE+xx or a fixed form with or without a point: its digits, and the exponent of the first nonzero one.
			var e = shortest.IndexOf('E', StringComparison.Ordinal);
			var mantissa = e < 0 ? shortest : shortest[..e];
			var exponent = e < 0 ? 0 : int.Parse(shortest[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
			var point = mantissa.IndexOf('.', StringComparison.Ordinal);
			var digits = point < 0 ? mantissa : mantissa[..point] + mantissa[(point + 1)..];
			exponent += (point < 0 ? mantissa.Length : point) - 1;
			exponent -= digits.Length - digits.TrimStart('0').Length;
			digits = digits.Trim('0');
			if (exponent is >= -4 and < 16)
			{
				if (exponent < 0)
					return $"{sign}0.{new string('0', -exponent - 1)}{digits}";
				var whole = digits.Length > exponent + 1 ? digits[..(exponent + 1)] : digits.PadRight(exponent + 1, '0');
				var fraction = digits.Length > exponent + 1 ? digits[(exponent + 1)..] : "0";
				return $"{sign}{whole}.{fraction}";
			}
			var rest = digits.Length > 1 ? "." + digits[1..] : "";
			var power = Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
			return $"{sign}{digits[0]}{rest}e{(exponent < 0 ? '-' : '+')}{power}";
		}
	}

	// The calls this script makes, through the gh CLI. Each method is virtual, so a test answers them without gh.
	// locateGh finds the gh to run: Script.Which's search of PATH, unless a test gives another.
	class GitHub(string repository, Func<string>? locateGh = null)
	{
		public string Repository { get; } = repository;

		// How many lines the file has at ref, or null when it can't be read.
		public virtual int? LineCount(string path, string reference)
		{
			byte[] data;
			try
			{
				data = Gh(["api", "-H", "Accept: application/vnd.github.raw", $"repos/{Repository}/contents/{PostClaudeReview.Quote(path)}?ref={reference}"]);
			}
			catch (Exception error) when (error is CalledProcessError or IOException or Win32Exception)
			{
				return null;
			}
			return CountLines(data);
		}

		// Line numbers count newlines only, as in DiffLines(); a last line without one still counts. Counted in bytes, so a file that
		// isn't UTF-8 can't fail the step, and a lone \r isn't read as a newline.
		public static int CountLines(byte[] data)
		{
			var newlines = data.Count(b => b == (byte)'\n');
			return newlines + (data.Length == 0 || data[^1] == (byte)'\n' ? 0 : 1);
		}

		public virtual List<JsonElement> PullFiles(BigInteger number) =>
				Pages(Text(Gh(["api", "--paginate", "--slurp", string.Create(CultureInfo.InvariantCulture, $"repos/{Repository}/pulls/{number}/files?per_page=100")])));

		// Posts the review and returns it as GitHub answered (its id among it).
		public virtual JsonElement CreateReview(BigInteger number, Review review)
		{
			var output = Text(Gh(["api", "--method", "POST", string.Create(CultureInfo.InvariantCulture, $"repos/{Repository}/pulls/{number}/reviews"), "--input", "-"], review.ToJson()));
			return PyJson.Parse(output.Length > 0 ? output : "{}");
		}

		public virtual List<JsonElement> ReviewComments(BigInteger number, string reviewId) =>
				Pages(Text(Gh(["api", "--paginate", "--slurp", string.Create(CultureInfo.InvariantCulture, $"repos/{Repository}/pulls/{number}/reviews/{reviewId}/comments?per_page=100")])));

		public virtual void UpdateReview(BigInteger number, string reviewId, string body) =>
				Gh(["api", "--method", "PUT", string.Create(CultureInfo.InvariantCulture, $"repos/{Repository}/pulls/{number}/reviews/{reviewId}"), "--input", "-"], $"{{\"body\": {Review.Quote(body)}}}");

		// [item for page in pages for item in page], as the Python read a --slurp answer.
		static List<JsonElement> Pages(string text) => PyJson.Iterate(PyJson.Parse(text)).SelectMany(PyJson.Iterate).ToList();

		// gh's stdout as text=True read it: strict UTF-8, with \r\n and a lone \r read as \n.
		static string Text(byte[] output)
		{
			try
			{
				return new UTF8Encoding(false, true).GetString(output).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
			}
			catch (DecoderFallbackException error)
			{
				throw new PyError("UnicodeDecodeError", error.Message);
			}
		}

		// Runs gh with argv, input on its stdin, and returns its stdout; a failure is a CalledProcessError without what gh said, as the .py's
		// capture_output kept it out of the log: GitHub's error text can echo the request, which holds the answer's text, and the runner
		// would read a line of it that starts with :: as a workflow command.
		byte[] Gh(IReadOnlyList<string> argv, string input = "")
		{
			var start = new ProcessStartInfo(locateGh?.Invoke() ?? Script.Which("gh", Environment.GetEnvironmentVariable("PATH")))
			{
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			foreach (var arg in argv)
				start.ArgumentList.Add(arg);
			using var run = Process.Start(start)!;
			using MemoryStream output = new();
			// gh's stderr is read to its end, so gh never blocks on a full pipe, and dropped.
			var reads = Task.WhenAll(run.StandardOutput.BaseStream.CopyToAsync(output), run.StandardError.BaseStream.CopyToAsync(Stream.Null));
			try
			{
				run.StandardInput.BaseStream.Write(new UTF8Encoding(false).GetBytes(input));
				run.StandardInput.Close();
			}
			catch (IOException)
			{
				// gh exited without reading its input; its exit code says why.
			}
			run.WaitForExit();
			// GetResult throws a failed read's IOException itself, which the linking step catches as the .py caught OSError; Wait would wrap it.
			reads.GetAwaiter().GetResult();
			if (run.ExitCode != 0)
				throw new CalledProcessError(["gh", .. argv], run.ExitCode, "");
			return output.ToArray();
		}
	}

	static class PostClaudeReview
	{
		// Accepted limit (ADR 0004 of the repo-ci-baseline skill, in mpaulosky/Application_Factory): the marker proves the login, not that
		// Claude wrote the review; see claude-review.yml.
		public const string Marker = "<!-- claude-review -->";
		// The second line of a review with findings outside the diff.
		public const string OffDiffMarker = "<!-- claude-review:off-diff -->";

		// Text shaped like a credential, in the formats the tokens are issued in, so a long snake_case name such as
		// ghs_installation_token_value isn't one: Anthropic keys and OAuth tokens, GitHub tokens (classic, fine-grained and today's
		// ghs_<digits>_<JWT> installation tokens), and JWTs. No boundary on the left, so text run up against a token (token_ghp_...,
		// x-eyJ...) can't hide it; a match only warns, so redacting a little too much costs little. The pattern's text is the .py's and the
		// workflow's, character for character (a Factory test holds it to both), and every class in it is ASCII, which .NET and Python read alike.
		public const string CredentialPattern =
				@"sk-ant-[A-Za-z0-9_-]{20,}"
				+ @"|gh[pousr]_[0-9]+_eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+"
				+ @"|gh[pousr]_[A-Za-z0-9]{36,}"
				+ @"|github_pat_[A-Za-z0-9]{22}_[A-Za-z0-9]{59}"
				+ @"|eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}";
		public static readonly Regex Credential = new(CredentialPattern, RegexOptions.CultureInvariant);
		public const string Redacted = "[redacted]";

		// Credential scans from every `eyJ`, and a long run of token characters (the first segment of a JWT holds the letters e, y and J) is
		// rescanned from each one: quadratic on text a diff can steer. So a token, with the dots joining its segments, that holds a run longer
		// than MaxTokenRun and one of the strings every Credential alternative starts with (CredentialStart) is replaced whole before
		// Credential looks at the text: it leaves nothing of a long JWT (its signature included) behind. A long token without one can't
		// match Credential, and legitimate review text has them (a generated test's file name, a long identifier, a digest), so it is left
		// alone. What is left has no run holding a start longer than MaxTokenRun characters, which Credential scans in time linear in the text.
		public const int MaxTokenRun = 128;
		public const string TokenChainPattern = @"[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*";
		public static readonly Regex TokenChain = new(TokenChainPattern, RegexOptions.CultureInvariant);
		public const string CredentialStartPattern = @"eyJ|gh[pousr]_|sk-ant-|github_pat_";
		public static readonly Regex CredentialStart = new(CredentialStartPattern, RegexOptions.CultureInvariant);

		public const int MaxPath = 1024;

		// Python's \s in a str pattern (str.isspace()): .NET's \s and the four separators \x1c to \x1f.
		const string Space = @"\s\x1c-\x1f";

		// A fence whose info string starts with `suggestion`, anywhere in a body (after a list or quote marker, or a lone CR, too): GitHub
		// shows its content as a change the reader can apply with one click to the line the comment sits on, and the legacy
		// `suggestion:-1+1` form changes more. Any whitespace but a line break may sit between the fence and the info string. A match
		// starts only at the start of a run of fence characters, so a long run costs a linear scan. The .py's pattern is
		// (?i)((?<!`)`{3,}|(?<!~)~{3,})[^\S\r\n]*suggestion\b: here its whitespace is Python's, its case-insensitive letters are the ones
		// Python's re matches (s to U+017F, i to U+0130 and U+0131), and its \b is Python's \w ending.
		public static readonly Regex SuggestionFence = new(
				@"((?<!`)`{3,}|(?<!~)~{3,})[" + Space + @"-[\r\n]]*[sSſ][uU][gG][gG][eE][sSſ][tT][iIİı][oO][nN](?![\p{L}\p{N}_])",
				RegexOptions.CultureInvariant);
		// The one form a comment may keep: a fence on its own lines whose info string is exactly `suggestion`, holding one non-empty line.
		// The leading class and \S are disjoint, so matching is linear.
		public static readonly Regex OneLineSuggestion = new(
				@"^(`{3,}|~{3,})suggestion[ \t]*\n([" + Space + @"-[\n\r]]*[^" + Space + @"][^\n\r]*)\n\1[ \t]*$",
				RegexOptions.Multiline | RegexOptions.CultureInvariant);

		static readonly string[] Severities = ["high", "medium", "low"];

		public const string ChangesRecommended = "### 🟡 Changes recommended";
		public const string NoChangesRecommended = "### 🟢 No changes recommended";
		public const string OffDiffHeader = "Findings outside the diff (no thread to resolve, so they hold the merge until the next push gets a fresh review):";
		// A shields.io badge for each severity, in Copilot's pill colours (red, amber, blue). GitHub serves the image through its camo
		// proxy, so a reader's browser never contacts shields.io, and the URL carries only the level; the alt text is the level, so the list
		// reads without the image.
		static readonly Dictionary<string, string> BadgeColours = new() { ["high"] = "d1242f", ["medium"] = "bf8700", ["low"] = "0969da" };
		public const int MaxTitle = 200;
		// GitHub refuses a review body over 65,536 characters (code points, as Python counts them). The markers and the findings outside
		// the diff are never cut: What changed goes first, then Advisory and Not reviewed, then the open issues past what fits. The effort
		// in the footer is capped at MaxEffort.
		public const int MaxBody = 65_536;
		public const int MaxEffort = 200;
		public const string WhatChangedCut = "_What changed in this PR was left out to fit GitHub's limit on a review's length._";
		public const string ExtrasCut = "_Advisory and Not reviewed were left out to fit GitHub's limit on a review's length._";

		// Characters that render blank but are letters or symbols (Hangul fillers, the blank Braille cell, Khmer inherent vowels), which
		// \s does not match.
		const string BlankFillers = "ᅟᅠ឴឵⠀ㅤﾠ";

		// The text with credential-shaped text replaced, and how many were replaced.
		public static (string Text, int Count) RedactCredentials(string text)
		{
			var count = 0;
			text = TokenChain.Replace(text, match =>
			{
				var chain = match.Value;
				if (chain.Split('.').Any(run => run.Length > MaxTokenRun) && CredentialStart.IsMatch(chain))
				{
					count++;
					return Redacted;
				}
				return chain;
			});
			var found = 0;
			text = Credential.Replace(text, _ =>
			{
				found++;
				return Redacted;
			});
			return (text, count + found);
		}

		// A finding's path with credential-shaped text replaced, and how many were replaced. Not the long-run rule: a long name that holds
		// one of CredentialStart's strings without being a credential (a generated test's name with `eyJ` in it) would be replaced whole by
		// it, and a path that no longer matches the diff leaves its finding outside it. A long name with none of them is left alone by
		// RedactCredentials already. A path past MaxPath is no real path and gets the full rule.
		public static (string Text, int Count) RedactPath(string path)
		{
			if (Py.Len(path) > MaxPath)
				return RedactCredentials(path);
			var found = 0;
			var text = Credential.Replace(path, _ =>
			{
				found++;
				return Redacted;
			});
			return (text, found);
		}

		// The report and findings from the review step's JSON, or MalformedFindings. The report is the fields that describe the PR, each
		// optional: today's summary (Markdown, posted as it is), or the structured overview, changes, files, advisory and not_reviewed. An
		// answer needs a summary or an overview. A finding may carry a severity.
		public static (Report Report, List<Finding> Findings) ParseFindings(string text)
		{
			JsonElement data;
			try
			{
				data = PyJson.Parse(text);
			}
			catch (PyError error) when (error.Name == "JSONDecodeError")
			{
				throw new MalformedFindings($"not JSON ({error.Message})");
			}
			if (data.ValueKind != JsonValueKind.Object || (PyJson.String(data, "summary") is null && PyJson.String(data, "overview") is null))
				throw new MalformedFindings("no summary string");
			foreach (var key in new[] { "summary", "overview", "not_reviewed" })
			{
				if (PyJson.TryGet(data, key, out var value) && value.ValueKind != JsonValueKind.String)
					throw new MalformedFindings($"{key} is not a string");
			}
			foreach (var key in new[] { "changes", "advisory" })
			{
				if (PyJson.TryGet(data, key, out var value) && !IsStrings(value))
					throw new MalformedFindings($"{key} is not a list of strings");
			}
			if (PyJson.TryGet(data, "files", out var files) && !(files.ValueKind == JsonValueKind.Array
					&& files.EnumerateArray().All(file => PyJson.String(file, "path") is not null && PyJson.String(file, "description") is not null)))
				throw new MalformedFindings("files is not a list of {path, description} strings");
			if (!PyJson.TryGet(data, "findings", out var list) || list.ValueKind != JsonValueKind.Array)
				throw new MalformedFindings("findings is not a list");
			List<Finding> findings = [];
			var index = 0;
			foreach (var finding in list.EnumerateArray())
			{
				var line = PyJson.TryGet(finding, "line", out var number) ? PyJson.Integer(number) : null;
				if (PyJson.String(finding, "path") is not { } path || line is null || PyJson.String(finding, "body") is not { } body)
					throw new MalformedFindings($"finding {index} needs a string path, an integer line and a string body");
				string? severity = null;
				if (PyJson.TryGet(finding, "severity", out var level))
				{
					severity = level.ValueKind == JsonValueKind.String ? level.GetString() : null;
					if (severity is null || !Severities.Contains(severity))
						throw new MalformedFindings($"finding {index} has a severity other than high, medium or low");
				}
				findings.Add(new Finding(path, line.Value, body, severity));
				index++;
			}
			// Only the validated fields: an extra one of another type would break the redaction.
			var report = new Report(
					PyJson.String(data, "summary"),
					PyJson.String(data, "overview"),
					PyJson.TryGet(data, "changes", out var changes) ? Strings(changes) : null,
					PyJson.TryGet(data, "files", out _) ? files.EnumerateArray().Select(file => new FileRow(PyJson.String(file, "path")!, PyJson.String(file, "description")!)).ToList() : null,
					PyJson.TryGet(data, "advisory", out var advisory) ? Strings(advisory) : null,
					PyJson.String(data, "not_reviewed"));
			return (report, findings);
		}

		static bool IsStrings(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);

		static List<string> Strings(JsonElement value) => value.EnumerateArray().Select(item => item.GetString()!).ToList();

		// Each file's new-side line numbers in the diff: the lines a review comment can sit on. files are the PR's files as GitHub lists
		// them: a file that isn't an object with a filename fails, as the Python's file["filename"] did.
		public static Dictionary<string, HashSet<BigInteger>> DiffLines(IEnumerable<JsonElement> files)
		{
			Dictionary<string, HashSet<BigInteger>> lines = [];
			foreach (var file in files)
			{
				if (file.ValueKind != JsonValueKind.Object)
					throw new PyError("TypeError", $"{PyJson.TypeName(file)} indices must be integers or slices, not str");
				if (!PyJson.TryGet(file, "filename", out var name))
					throw new PyError("KeyError", "'filename'");
				// A filename that isn't a string is no path a finding can name; a list or dict can't be a key at all.
				if (name.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
					throw new PyError("TypeError", $"unhashable type: '{PyJson.TypeName(name)}'");
				HashSet<BigInteger> commentable = name.ValueKind == JsonValueKind.String
						? lines.TryGetValue(name.GetString()!, out var known) ? known : lines[name.GetString()!] = []
						: [];
				var patch = PyJson.TryGet(file, "patch", out var value) ? value : default;
				if (patch.ValueKind != JsonValueKind.String)
				{
					// file.get("patch") or "": a falsy value is no patch, anything else has no split().
					if (Truthy(patch))
						throw new PyError("AttributeError", $"'{PyJson.TypeName(patch)}' object has no attribute 'split'");
					continue;
				}
				BigInteger? line = null;
				// GitHub's patches are newline-delimited; a split on other line breaks would also split on form feeds and other line
				// breaks inside a line's content.
				foreach (var row in patch.GetString()!.Split('\n'))
				{
					if (HunkStart(row) is { } start)
						line = start;
					else if (line is not null && !row.StartsWith('-') && !row.StartsWith('\\'))
					{
						commentable.Add(line.Value);
						line++;
					}
				}
			}
			return lines;
		}

		static bool Truthy(JsonElement value) => value.ValueKind switch
		{
			JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
			JsonValueKind.String => value.GetString()!.Length > 0,
			JsonValueKind.Array => value.GetArrayLength() > 0,
			JsonValueKind.Object => value.EnumerateObject().Any(),
			// By its value, as Python's bool() reads the int or float json.loads made: 0e5 and 1e-400 are 0.0, and 1e400 is inf. A double
			// gives an int's answer too: a non-zero one is never 0.0 (a long one is inf), and -0 is -0.0, which is 0.
			JsonValueKind.Number => double.Parse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture) != 0,
			_ => true,
		};

		// The new-side start of a hunk, "@@ -a,b +c,d @@" (",b" and ",d" optional), as the .py's ^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@ reads
		// it: \d is any Unicode decimal digit, an astral one too, and c is read by its value; null for a row that isn't one.
		static BigInteger? HunkStart(string row)
		{
			var at = 0;
			bool Literal(string text)
			{
				if (!row.AsSpan(at).StartsWith(text, StringComparison.Ordinal))
					return false;
				at += text.Length;
				return true;
			}
			string? Digits()
			{
				var start = at;
				while (at < row.Length && Rune.TryGetRuneAt(row, at, out var rune) && Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
					at += rune.Utf16SequenceLength;
				return at > start ? row[start..at] : null;
			}
			bool Range()
			{
				if (Digits() is null)
					return false;
				var mark = at;
				if (Literal(",") && Digits() is null)
					at = mark;
				return true;
			}
			if (!Literal("@@ -") || !Range() || !Literal(" +"))
				return null;
			var number = Digits();
			if (number is null)
				return null;
			var mark = at;
			if (Literal(",") && Digits() is null)
				at = mark;
			if (!Literal(" @@"))
				return null;
			return Py.Int(number) ?? throw new PyError("ValueError", "Exceeds the limit (4300 digits) for integer string conversion");
		}

		// The summary and findings with credential-shaped text replaced, and how many were replaced: every string of the report is cleaned,
		// a file's path as a path.
		public static (Report Report, List<Finding> Findings, int Count) Redact(Report report, List<Finding> findings)
		{
			var count = 0;
			string Clean(string text, Func<string, (string, int)>? scan = null)
			{
				var (cleaned, n) = (scan ?? RedactCredentials)(text);
				count += n;
				return cleaned;
			}
			string? CleanOrNull(string? text) => text is null ? null : Clean(text);
			List<string>? CleanAll(List<string>? texts) => texts?.Select(text => Clean(text)).ToList();
			var cleaned = new Report(CleanOrNull(report.Summary), CleanOrNull(report.Overview), CleanAll(report.Changes),
					report.Files?.Select(file => new FileRow(Clean(file.Path, RedactPath), Clean(file.Description))).ToList(),
					CleanAll(report.Advisory), CleanOrNull(report.NotReviewed));
			var redacted = findings.Select(f => f with { Path = Clean(f.Path, RedactPath), Body = Clean(f.Body) }).ToList();
			return (cleaned, redacted, count);
		}

		// The body with each suggestion fence turned into a plain text fence, so no one-click suggestion applies.
		public static string DefuseSuggestions(string body) => SuggestionFence.Replace(body, "$1text");

		// Whether `line` can be the one line of a suggestion that `fence` opened. Not when it would close the block (a suggestion with no
		// content deletes the line), nor when it holds a character the reader can't see in the diff: a control or format character
		// (zero-width, bidirectional) or a Unicode line or paragraph separator, or when nothing in it is visible (spaces, combining marks and
		// the blank fillers only).
		public static bool SuggestionLineIsSafe(string fence, string line)
		{
			var closes = Regex.IsMatch(line, @"\A {0,3}" + Regex.Escape(fence[0].ToString()) + "{" + fence.Length.ToString(CultureInfo.InvariantCulture) + @",}[ \t]*\z",
					RegexOptions.CultureInvariant);
			var categories = CodePoints(line).Select(point => (point.Text, Category: point.Category)).ToList();
			var hidden = categories.Any(c => c.Category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
					|| (c.Category == UnicodeCategory.Control && c.Text != "\t"));
			var blank = categories.All(c => BlankFillers.Contains(c.Text, StringComparison.Ordinal)
					|| c.Category is UnicodeCategory.SpaceSeparator or UnicodeCategory.Control or UnicodeCategory.NonSpacingMark);
			return !closes && !hidden && !blank;
		}

		// Each code point of text with its category, as Python iterates a str: a lone surrogate is a code point of its own.
		static IEnumerable<(string Text, UnicodeCategory Category)> CodePoints(string text)
		{
			for (var i = 0; i < text.Length; i++)
			{
				if (char.IsSurrogatePair(text, i))
				{
					yield return (text.Substring(i, 2), CharUnicodeInfo.GetUnicodeCategory(text, i));
					i++;
				}
				else
					yield return (text[i].ToString(), char.GetUnicodeCategory(text[i]));
			}
		}

		// The body as it is when it holds no suggestion, or exactly one well-formed single-line suggestion. Any other suggestion (several,
		// several lines, the multi-line form, an empty or hidden one, text redacted from a credential) is defused: the prompt asks for one
		// line only, but a suggestion is applied with one click and what the model writes comes from a PR it read, so the script holds the rule.
		public static string VetSuggestions(string body)
		{
			var fences = SuggestionFence.Matches(body).Count;
			if (fences == 0)
				return body;
			var block = OneLineSuggestion.Match(body);
			if (fences == 1 && block.Success && !block.Groups[2].Value.Contains(Redacted, StringComparison.Ordinal)
					&& SuggestionLineIsSafe(block.Groups[1].Value, block.Groups[2].Value))
				return body;
			return DefuseSuggestions(body);
		}

		// The severity's badge and a space, or nothing for a finding without one.
		public static string Badge(string? severity)
		{
			if (severity is null || !BadgeColours.TryGetValue(severity, out var colour))
				return "";
			var level = char.ToUpperInvariant(severity[0]) + severity[1..];
			return $"![{level}](https://img.shields.io/badge/{level}-{colour}?style=flat-square) ";
		}

		static readonly Regex BoldTitle = new(@"\A[" + Space + @"]*\*\*(.+?)\*\*", RegexOptions.CultureInvariant);

		// A finding's title: the bold first line Claude starts it with, without the **, else its first line. On one line, and at most
		// MaxTitle characters.
		public static string TitleOf(string body)
		{
			var first = Py.Strip(body).Split('\n', 2)[0];
			var bold = BoldTitle.Match(first);
			var title = string.Join(" ", Split(bold.Success ? bold.Groups[1].Value : first));
			return Py.Len(title) <= MaxTitle ? title : RStrip(Py.Head(title, MaxTitle - 1)) + "…";
		}

		// str.split() with no separator: the runs of text between Python's whitespace.
		public static List<string> Split(string text)
		{
			List<string> words = [];
			var start = -1;
			for (var i = 0; i <= text.Length; i++)
			{
				var space = i == text.Length || Py.IsSpace(text[i]);
				if (!space && start < 0)
					start = i;
				else if (space && start >= 0)
				{
					words.Add(text[start..i]);
					start = -1;
				}
			}
			return words;
		}

		static string RStrip(string text)
		{
			var end = text.Length;
			while (end > 0 && Py.IsSpace(text[end - 1]))
				end--;
			return text[..end];
		}

		// The items sorted High, Medium, Low, then unrated, keeping their order within each.
		static List<T> BySeverity<T>(IEnumerable<T> items, Func<T, string?> severity) =>
				items.OrderBy(item => severity(item) is { } level && Array.IndexOf(Severities, level) is var rank and >= 0 ? rank : Severities.Length).ToList();

		static string EscapeLinkText(string text) => Regex.Replace(text, @"([\\\[\]])", @"\$1");

		// Text that stays in one cell of a Markdown table.
		static string Cell(string text) => string.Join(" ", Split(text)).Replace("|", "\\|", StringComparison.Ordinal);

		// A Markdown list, each item's later lines indented under its bullet.
		static string Bullets(IEnumerable<string> items) => string.Join("\n", items.Select(item => "- " + Py.Strip(item).Replace("\n", "\n  ", StringComparison.Ordinal)));

		// The What changed part: today's summary as it is, then the structured overview, changes and files.
		static string WhatChanged(Report report)
		{
			List<string> parts = [Py.Strip(report.Summary ?? ""), Py.Strip(report.Overview ?? "")];
			if (report.Changes is { Count: > 0 })
				parts.Add("**Changes:**\n\n" + Bullets(report.Changes));
			if (report.Files is { Count: > 0 })
				parts.Add(string.Join("\n", new[] { "| File | Description |", "| --- | --- |" }.Concat(report.Files.Select(file => $"| `{Cell(file.Path)}` | {Cell(file.Description)} |"))));
			return string.Join("\n\n", parts.Where(part => part.Length > 0));
		}

		// The inline comments (with the finding each is for) and the off-diff bullets. lineCount(path) is the file's length at the head, or
		// null if unknown.
		public static (List<(Finding Finding, Comment Comment)> Threads, List<string> OffDiff) PlaceFindings(
				IEnumerable<Finding> findings, Dictionary<string, HashSet<BigInteger>> commentable, Func<string, int?>? lineCount = null)
		{
			lineCount ??= _ => null;
			List<(Finding, Comment)> threads = [];
			List<string> offDiff = [];
			foreach (var finding in findings)
			{
				var (path, line, body) = (finding.Path, finding.Line, finding.Body);
				var lines = commentable.TryGetValue(path, out var known) ? known : [];
				if (lines.Contains(line))
					threads.Add((finding, new Comment(path, line, VetSuggestions(body))));
				else if (lines.Count > 0 && (line < 1 || (lineCount(path) is { } length && line > length)))
				{
					// Not a line of the file: Claude got the number wrong.
					var nearest = lines.OrderBy(n => BigInteger.Abs(n - line)).ThenBy(n => n).First();
					var note = string.Create(CultureInfo.InvariantCulture, $"_Claude cited line {line}, which isn't a line of this file, so this is on the nearest line in the diff._");
					// A suggestion would replace the nearest line, not the one Claude meant.
					threads.Add((finding, new Comment(path, nearest, $"{note}\n\n{DefuseSuggestions(body)}")));
				}
				else
				{
					// In the review body a suggestion can't be applied, and a body of several paragraphs stays under its bullet when indented.
					var indented = DefuseSuggestions(body).Replace("\n", "\n  ", StringComparison.Ordinal);
					offDiff.Add(string.Create(CultureInfo.InvariantCulture, $"- `{path}:{line}`: {indented}"));
				}
			}
			return (threads, offDiff);
		}

		// The review's body in Copilot's layout, within MaxBody when the markers and off-diff findings allow. links[i] is the URL of
		// threads[i]'s thread, or null; without links the titles aren't linked.
		public static string RenderBody(Report report, List<Finding> findings, List<(Finding Finding, Comment Comment)> threads, List<string> offDiff,
				string? effort = null, IReadOnlyList<string>? notes = null, IReadOnlyList<string?>? links = null)
		{
			notes ??= [];
			links ??= threads.Select(_ => (string?)null).ToList();
			if (links.Count != threads.Count)
				throw new ArgumentException("one link for each thread");
			var issues = BySeverity(threads.Zip(links), pair => pair.First.Finding.Severity)
					.Select(pair => Badge(pair.First.Finding.Severity) + (pair.Second is { } url
							? $"[{EscapeLinkText(TitleOf(pair.First.Finding.Body))}]({url})"
							: TitleOf(pair.First.Finding.Body)))
					.ToList();
			var changed = WhatChanged(report);
			effort = effort is { Length: > 0 } ? string.Join(" ", Split(effort)) : "";
			if (Py.Len(effort) > MaxEffort)
				effort = Py.Head(effort, MaxEffort - 1) + "…";
			var footer = effort.Length > 0 ? $"Claude Review · Review effort: {effort}" : "Claude Review";
			footer = footer.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
			var hasExtras = report.Advisory is { Count: > 0 } || Py.Strip(report.NotReviewed ?? "").Length > 0;

			string Compose(string changed, int shown, bool extras = true)
			{
				List<string> parts = [findings.Count > 0 ? ChangesRecommended : NoChangesRecommended];
				if (findings.Count > 0)
					parts.Add(TitleOf(BySeverity(findings, finding => finding.Severity)[0].Body));
				if (issues.Count > 0)
				{
					var count = $"{issues.Count} open issue" + (issues.Count == 1 ? "" : "s");
					var listed = string.Join("\n", issues.Take(shown).Select(issue => $"- {issue}"));
					if (shown < issues.Count)
						listed += $"\n\n_{issues.Count - shown} more in the threads below, left out to fit GitHub's limit._";
					parts.Add($"<details open><summary><b>{count}</b></summary>\n\n{listed}\n\n</details>");
				}
				if (changed.Length > 0)
					parts.Add($"<details><summary><b>What changed in this PR</b></summary>\n\n{changed}\n\n</details>");
				if (!extras)
					parts.Add(ExtrasCut);
				else
				{
					if (report.Advisory is { Count: > 0 })
						parts.Add("**Advisory (not blocking)**\n\n" + Bullets(report.Advisory));
					if (Py.Strip(report.NotReviewed ?? "").Length > 0)
						parts.Add("**Not reviewed**\n\n" + Py.Strip(report.NotReviewed!));
				}
				parts.AddRange(notes);
				// The last section with bullets: PR Auto-Merge reads every "- " line after its header as a finding.
				if (offDiff.Count > 0)
					parts.Add(OffDiffHeader + "\n\n" + string.Join("\n", offDiff));
				parts.Add($"<sub>{footer}</sub>");
				var markers = offDiff.Count > 0 ? $"{Marker}\n{OffDiffMarker}" : Marker;
				return $"{markers}\n" + string.Join("\n\n", parts);
			}

			var body = Compose(changed, issues.Count);
			if (Py.Len(body) > MaxBody && changed.Length > 0)
			{
				changed = WhatChangedCut;
				body = Compose(changed, issues.Count);
			}
			var extras = true;
			if (Py.Len(body) > MaxBody && hasExtras)
			{
				extras = false;
				body = Compose(changed, issues.Count, extras);
			}
			var shown = issues.Count;
			while (Py.Len(body) > MaxBody && shown > 0)
			{
				shown--;
				body = Compose(changed, shown, extras);
			}
			return body;
		}

		// The review to post, and how to render its body with the threads' links: inline comments for findings on the diff, the rest in the
		// body. lineCount(path) is the file's length at the head, or null if unknown; notes are paragraphs for the body, before the findings
		// outside the diff.
		public static (Review Review, Func<IReadOnlyList<string?>, string> Linked) BuildReview(Report report, List<Finding> findings,
				Dictionary<string, HashSet<BigInteger>> commentable, string head, Func<string, int?>? lineCount = null, string? effort = null,
				IReadOnlyList<string>? notes = null)
		{
			var (threads, offDiff) = PlaceFindings(findings, commentable, lineCount);
			var body = RenderBody(report, findings, threads, offDiff, effort, notes);
			var review = new Review(head, body, threads.Select(thread => thread.Comment).ToList());
			return (review, links => RenderBody(report, findings, threads, offDiff, effort, notes, links));
		}

		// The URL of each comment's thread, from the review's comments as GitHub lists them, or null. A comment is matched by its path and
		// body; one left unmatched takes the URL at its place when GitHub lists as many comments as were posted (it lists them in the order
		// they were made). Only a string starting with https:// is a URL.
		public static List<string?> ThreadLinks(IReadOnlyList<Comment> comments, IReadOnlyList<JsonElement> posted)
		{
			var urls = posted.Select(c => PyJson.String(c, "html_url") is { } url && url.StartsWith("https://", StringComparison.Ordinal) ? url : null).ToList();
			Dictionary<(string, string), Queue<string>> byKey = [];
			for (var i = 0; i < posted.Count; i++)
			{
				if (urls[i] is null)
					continue;
				// The .py keyed a dict by (path, body), which a list or a dict in it can't be (a TypeError, so the step warns); a path or body
				// that is another kind of value never equals a comment's.
				foreach (var key in new[] { "path", "body" })
				{
					if (PyJson.TryGet(posted[i], key, out var part) && part.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
						throw new PyError("TypeError", $"unhashable type: '{PyJson.TypeName(part)}'");
				}
				if (urls[i] is { } url && PyJson.String(posted[i], "path") is { } path && PyJson.String(posted[i], "body") is { } body)
				{
					if (!byKey.TryGetValue((path, body), out var queue))
						byKey[(path, body)] = queue = new Queue<string>();
					queue.Enqueue(url);
				}
			}
			List<string?> links = [];
			for (var index = 0; index < comments.Count; index++)
			{
				if (byKey.TryGetValue((comments[index].Path, comments[index].Body), out var matched) && matched.Count > 0)
					links.Add(matched.Dequeue());
				else if (posted.Count == comments.Count)
					links.Add(urls[index]);
				else
					links.Add(null);
			}
			return links;
		}

		// urllib.parse.quote(path): every byte of its UTF-8 percent-encoded but ASCII letters, digits and _.-~/. A lone surrogate can't be
		// encoded, as it couldn't be in Python.
		public static string Quote(string path)
		{
			var quoted = new StringBuilder();
			foreach (var b in new UTF8Encoding(false, true).GetBytes(path))
			{
				var c = (char)b;
				if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-' or '~' or '/')
					quoted.Append(c);
				else
					quoted.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
			}
			return quoted.ToString();
		}

		const string Prog = "post_claude_review.cs";

		// The script: parse the arguments and FINDINGS (from env), post the review through github(repo), or a GitHub on gh, and print what
		// was posted. The exit code; a gh call that fails throws, which Script.Run makes exit 1.
		public static int Run(IReadOnlyList<string> args, Func<string, string?> env, TextWriter stdout, TextWriter stderr, Func<string, GitHub>? github = null)
		{
			var parser = new Arguments(Prog, "[-h] [--repo REPO] [--pr PR] [--head HEAD] [--effort EFFORT]",
							"Post Claude Review's findings as one PR review on the head commit.")
					.Option("--repo", "owner/name")
					.IntOption("--pr", "the PR to review")
					.Option("--head", "the head commit Claude reviewed")
					.Option("--effort", "the review effort for the body's footer, such as 'high (claude-sonnet-5-5), 2 batches'");
			if (parser.Parse(args, stdout, stderr) is { } code)
				return code;
			var repo = parser.Value("--repo", "");
			var pr = parser.IntValue("--pr", 0);
			var head = parser.Value("--head", "");
			var effort = parser.Has("--effort") ? parser.Value("--effort", "") : null;
			if (repo.Length == 0 || pr.IsZero || head.Length == 0)
				return parser.Error("--repo, --pr and --head are required to post", stderr);

			var text = env("FINDINGS") ?? "";
			if (Py.Strip(text).Length == 0)
			{
				stderr.Write("post_claude_review.cs: Claude Review's findings are empty, though Claude answered: the answer quoted a secret of the review job and was withheld.\n");
				stdout.Write("::error::Claude's review reached the post job empty: it quoted a secret of the review job, so the review job's check (or GitHub's masking) "
						+ "withheld it. A prompt-injected diff may have got Claude to quote CLAUDE_CODE_OAUTH_TOKEN, the only long-lived secret in that job (its "
						+ "GITHUB_TOKEN expires with the job): check the PR and the review job's log, and rotate CLAUDE_CODE_OAUTH_TOKEN.\n");
				return 1;
			}
			Report report;
			List<Finding> parsed;
			try
			{
				(report, parsed) = ParseFindings(text);
			}
			catch (MalformedFindings error)
			{
				stderr.Write($"post_claude_review.cs: Claude Review's findings are malformed: {error.Message}\n");
				return 1;
			}

			(report, parsed, var redactedHere) = Redact(report, parsed);
			// The review job's check redacts first; this counts what it did too.
			// A BigInteger, as Python's int is of any size.
			BigInteger redacted = redactedHere;
			if (env("REDACTED") is { Length: > 0 } counted)
				redacted += Py.Int(counted) ?? throw new PyError("ValueError", $"invalid literal for int() with base 10: {Py.Repr(counted)}");
			List<string> notes = redacted != 0 ? [string.Create(CultureInfo.InvariantCulture, $"{redacted} credential-shaped string(s) were redacted from this review.")] : [];

			var gh = (github ?? (name => new GitHub(name)))(repo);
			var (review, linked) = BuildReview(report, parsed, DiffLines(gh.PullFiles(pr)), head, path => gh.LineCount(path, head), effort, notes);
			var posted = gh.CreateReview(pr, review);
			if (review.Comments.Count > 0)
			{
				// The threads' URLs exist only once the review is posted, so the body is edited to link them. The edit fires
				// pull_request_review "edited", which PR Review Submitted ignores, and the markers and off-diff findings are the same in both bodies.
				try
				{
					if (posted.ValueKind != JsonValueKind.Object)
						throw new PyError("TypeError", $"'{PyJson.TypeName(posted)}' object is not subscriptable");
					if (!PyJson.TryGet(posted, "id", out var id))
						throw new PyError("KeyError", "'id'");
					var links = ThreadLinks(review.Comments, gh.ReviewComments(pr, PyJson.Str(id)));
					var body = linked(links);
					if (links.Any(link => link is not null) && body != review.Body)
					{
						gh.UpdateReview(pr, PyJson.Str(id), body);
						review.Body = body;
					}
				}
				catch (Exception error) when (error is PyError or CalledProcessError or IOException or Win32Exception)
				{
					var name = error switch
					{
						PyError py => py.Name,
						CalledProcessError => "CalledProcessError",
						FileNotFoundException => "FileNotFoundError",
						_ => "OSError",
					};
					stdout.Write($"::warning::Couldn't link the review's threads from its body ({name}), so it lists their titles without links.\n");
				}
			}
			var inBody = parsed.Count - review.Comments.Count;
			stdout.Write(string.Create(CultureInfo.InvariantCulture, $"Posted Claude's review of {head} on PR #{pr}: {review.Comments.Count} inline, {inBody} in the body.\n"));
			if (inBody > 0)
				stdout.Write($"::warning::{inBody} finding(s) outside the diff are in the review body; its off-diff marker holds the merge until the next push gets a fresh review.\n");
			if (redacted != 0)
				stdout.Write(string.Create(CultureInfo.InvariantCulture, $"::warning::Redacted {redacted} credential-shaped string(s) from Claude's review. They aren't this job's secrets (an answer quoting those is ")
						+ "withheld), so they are most likely sample or placeholder tokens from the PR.\n");
			return 0;
		}
	}

	// Gh.cs's copies start here: Arguments, CalledProcessError, Ran, Script, SurrogateEscape and Py, which a Factory test holds equal to
	// Gh.cs's line for line (indented one tab more).
	// argparse's part the scripts use: options with one value (--name value or --name=value), required ones among them, flags, a long option
	// given by any unique prefix of its name (--help's too), -h and --help, and -- ending the options. An argument that matches no option is a
	// value when it looks like a negative number (argparse's -\.?\d) or holds a space. A script with no positional arguments refuses one as
	// argparse does. OneOf is a required mutually exclusive group, and IntOption argparse's type=int. A usage error exits 2 with the usage and the error on stderr, as argparse does.
	sealed class Arguments(string prog, string usage, string description, bool positionals = false)
	{
		const string HelpName = "--help";
		readonly List<(string Name, bool TakesValue, string Help)> options = [];
		readonly List<string> required = [];
		readonly List<string[]> groups = [];
		readonly HashSet<string> ints = [];
		public readonly Dictionary<string, string?> Values = [];
		public readonly List<string> Positional = [];

		public Arguments Flag(string name, string help)
		{
			options.Add((name, false, help));
			return this;
		}

		public Arguments Option(string name, string help)
		{
			options.Add((name, true, help));
			return this;
		}

		// An option the arguments must give, as argparse's required=True: one left out is a usage error naming every one left out.
		public Arguments Required(string name, string help)
		{
			required.Add(name);
			return Option(name, help);
		}

		// An option whose value must be one Python's int() reads, as argparse's type=int: one that isn't is a usage error as soon as it is
		// read, before an unknown or missing option is reported. Required, as Required is, when required is set.
		public Arguments IntOption(string name, string help, bool required = false)
		{
			ints.Add(name);
			return required ? Required(name, help) : Option(name, help);
		}

		// Options already added of which exactly one must be given, as argparse's add_mutually_exclusive_group(required=True): giving a second
		// is a usage error naming it and the first, and giving none is one naming them all.
		public Arguments OneOf(params string[] names)
		{
			groups.Add(names);
			return this;
		}

		public string Usage => $"usage: {prog} {usage}\n";

		// The exit code a usage error or --help ends with, after writing what it says; null when the arguments parsed.
		public int? Parse(IEnumerable<string> args, TextWriter output, TextWriter err)
		{
			var rest = args.ToList();
			List<string> unknown = [];
			// positional: past a --, where nothing is an option; taken: the positional arguments were read in one run, and an option followed
			// them, so any later one is refused, as argparse refuses a second run of a nargs="*" positional.
			bool positional = false, taken = false;
			for (var i = 0; i < rest.Count; i++)
			{
				var arg = rest[i];
				if (positional || !LooksLikeOption(arg))
				{
					(taken || !positionals ? unknown : Positional).Add(arg);
					continue;
				}
				if (arg == "--")
				{
					positional = true;
					// A parser with no positional arguments has nothing to give the -- to, so argparse reports it with what follows it.
					if (taken || !positionals)
						unknown.Add(arg);
					continue;
				}
				taken |= Positional.Count > 0;
				var (name, value) = arg.IndexOf('=') is var eq and > 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
				// -h with more after it, as argparse reads it in 3.11 to 3.14: -h=x is -h given x, -h-x is -h given -x, and anything else
				// (-hx, -hx=1) is -h and further short flags, -h first, so it is help.
				if (!arg.StartsWith("--", StringComparison.Ordinal) && arg.StartsWith("-h", StringComparison.Ordinal))
					(name, value) = ("-h", arg.Length > 2 && arg[2] is '=' or '-' ? arg[2] == '=' ? arg[3..] : arg[2..] : null);
				var matches = Matches(name);
				if (matches.Count > 1)
					return Error($"ambiguous option: {name} could match {string.Join(", ", matches.Select(o => o.Name))}", err);
				// LooksLikeOption read an argument with a space or a negative number that matches nothing as a value, so this is an unknown option.
				if (matches.Count == 0)
				{
					unknown.Add(arg);
					continue;
				}
				var option = matches[0];
				if (option.Name == HelpName)
				{
					if (value is not null)
						return Error($"argument -h/--help: ignored explicit argument '{value}'", err);
					output.Write(Help());
					return 0;
				}
				if (!option.TakesValue)
				{
					if (value is not null)
						return Error($"argument {option.Name}: ignored explicit argument '{value}'", err);
					if (Conflict(option.Name) is { } flagConflict)
						return Error($"argument {option.Name}: not allowed with argument {flagConflict}", err);
					Values[option.Name] = null;
					continue;
				}
				if (value is null)
				{
					if (i + 1 >= rest.Count || LooksLikeOption(rest[i + 1]))
						return Error($"argument {option.Name}: expected one argument", err);
					value = rest[++i];
				}
				if (ints.Contains(option.Name) && Py.Int(value) is null)
					return Error($"argument {option.Name}: invalid int value: {Py.Repr(value)}", err);
				if (Conflict(option.Name) is { } conflict)
					return Error($"argument {option.Name}: not allowed with argument {conflict}", err);
				Values[option.Name] = value;
			}
			// argparse names the required options left out before the arguments it didn't recognize.
			if (required.Where(name => !Values.ContainsKey(name)).ToList() is { Count: > 0 } missing)
				return Error($"the following arguments are required: {string.Join(", ", missing)}", err);
			if (groups.Find(group => !group.Any(Values.ContainsKey)) is { } none)
				return Error($"one of the arguments {string.Join(" ", none)} is required", err);
			if (unknown.Count > 0)
				return Error($"unrecognized arguments: {string.Join(" ", unknown)}", err);
			return null;
		}

		public bool Has(string name) => Values.ContainsKey(name);

		// The option given before this one that shares a OneOf group with it, or null.
		string? Conflict(string name) =>
				groups.Where(group => group.Contains(name)).SelectMany(group => group).FirstOrDefault(other => other != name && Values.ContainsKey(other));

		// An option's value, else its default.
		public string Value(string name, string fallback) => Values.TryGetValue(name, out var value) && value is not null ? value : fallback;

		// An IntOption's value as int() reads it, else its default.
		public BigInteger IntValue(string name, BigInteger fallback) => Values.TryGetValue(name, out var value) && value is not null ? Py.Int(value)!.Value : fallback;

		// A usage error: the usage and the error on stderr, exit 2.
		public int Error(string message, TextWriter err)
		{
			err.Write($"{Usage}{prog}: error: {message}\n");
			return 2;
		}

		string Help()
		{
			var text = new StringBuilder($"{Usage}\n{description}\n\noptions:\n  -h, --help            show this help message and exit\n");
			foreach (var (name, takesValue, help) in options)
			{
				var metavar = name.TrimStart('-').ToUpperInvariant().Replace('-', '_');
				text.Append($"  {(takesValue ? $"{name} {metavar}" : name),-22}{help}\n");
			}
			return text.ToString();
		}

		// The options a name (before any =) names: itself, else every one it is a prefix of, --help among them; a name that doesn't start
		// with -- names only itself (-h is --help).
		List<(string Name, bool TakesValue, string Help)> Matches(string name)
		{
			List<(string Name, bool TakesValue, string Help)> all = [(HelpName, false, ""), .. options];
			if (name == "-h")
				return [all[0]];
			if (all.Find(o => o.Name == name) is { Name: not null } exact)
				return [exact];
			return name.StartsWith("--", StringComparison.Ordinal) && name.Length > 2 ? all.Where(o => o.Name.StartsWith(name, StringComparison.Ordinal)).ToList() : [];
		}

		// Whether argparse reads arg as an option (O) rather than a value (A): a - and more, unless it names no option and looks like a
		// negative number or holds a space.
		bool LooksLikeOption(string arg)
		{
			if (arg.Length < 2 || arg[0] != '-')
				return false;
			if (arg == "--")
				return true;
			var name = arg.IndexOf('=') is var eq and > 0 ? arg[..eq] : arg;
			if (!arg.StartsWith("--", StringComparison.Ordinal) && arg.StartsWith("-h", StringComparison.Ordinal))
				return true;
			if (Matches(name).Count > 0)
				return true;
			return !(NegativeNumber.IsMatch(AsciiDigits(arg)) || arg.Contains(' '));
		}

		// text with every Unicode decimal digit made its ASCII one, astral ones too, as Python's \d reads them (-٥ is -5).
		static string AsciiDigits(string text)
		{
			var ascii = new StringBuilder(text.Length);
			foreach (var rune in text.EnumerateRunes())
				ascii.Append(Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber ? (char)('0' + (int)Rune.GetNumericValue(rune)) : rune.ToString());
			return ascii.ToString();
		}

		// argparse's _negative_number_matcher as Python 3.14 has it, on AsciiDigits' text: a - then a digit, or a . and a digit, with anything
		// after (-5x and -1e3 are values).
		static readonly Regex NegativeNumber = new(@"\A-\.?\d", RegexOptions.CultureInvariant);
	}

	// A program the script ran that failed, as Python's CalledProcessError: its argv and exit status, and what it wrote to stderr.
	sealed class CalledProcessError(IReadOnlyList<string> argv, int code, string stderr)
			: Exception($"Command '{string.Join(" ", argv)}' returned non-zero exit status {code}.{(stderr.Trim() is { Length: > 0 } said ? $" It said: {said}" : "")}")
	{
		public int Code { get; } = code;
	}

	// One finished program: its exit code, and its stdout and stderr as bytes.
	readonly record struct Ran(int Code, byte[] Stdout, byte[] Stderr);

	static class Script
	{
		// The script's exit code; an uncaught exception is a message on stderr and exit 1, as a Python traceback was.
		public static int Run(Func<TextWriter, TextWriter, int> main) =>
				Run(main, new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true },
						new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

		// The same, on the writers given (UTF-8 stdout and stderr above).
		public static int Run(Func<TextWriter, TextWriter, int> main, TextWriter stdout, TextWriter stderr)
		{
			try
			{
				return main(stdout, stderr);
			}
			catch (Exception error)
			{
				stderr.Write($"{error.GetType().Name}: {error.Message}\n{error.StackTrace}\n");
				return 1;
			}
			finally
			{
				stdout.Flush();
			}
		}

		// Writes text as Python's print() writes it under surrogateescape: a byte SurrogateEscape.Decode kept as a lone surrogate goes out as
		// that byte. A StreamWriter (the script's stdout) gets the bytes on its stream, after what it holds; any other writer (a test's) gets
		// the text, as SurrogateEscape.Decode reads those bytes back.
		public static void Write(TextWriter writer, string text)
		{
			if (writer is StreamWriter stream)
			{
				stream.Flush();
				stream.BaseStream.Write(SurrogateEscape.Encode(text));
				stream.BaseStream.Flush();
			}
			else
				writer.Write(SurrogateEscape.Decode(SurrogateEscape.Encode(text)));
		}

		// The first executable file called name in a folder of path (PATH's form), as execvp finds it but for the folders that aren't
		// absolute, which are skipped: .NET's own lookup tries the program's folder and the current one before PATH, and a script runs from
		// the pull request's checkout, where a file of that name is the PR's. None found throws FileNotFoundException, as starting a missing
		// program did.
		public static string Which(string name, string? path)
		{
			foreach (var folder in (path ?? "").Split(':'))
			{
				if (!Path.IsPathRooted(folder))
					continue;
				var candidate = Path.Combine(folder, name);
				if (File.Exists(candidate) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
					return candidate;
			}
			throw new FileNotFoundException($"{name} isn't on PATH (only its absolute folders are searched)", name);
		}

		// Runs argv (the program first, found by Which) in cwd, or the current folder, with stdin from /dev/null and both outputs read whole,
		// as subprocess.run(capture_output=True) does.
		public static Ran Capture(IReadOnlyList<string> argv, string? cwd = null)
		{
			var start = new ProcessStartInfo(Which(argv[0], Environment.GetEnvironmentVariable("PATH")))
			{
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				WorkingDirectory = cwd ?? "",
			};
			foreach (var arg in argv.Skip(1))
				start.ArgumentList.Add(arg);
			using var run = Process.Start(start)!;
			run.StandardInput.Close();
			using MemoryStream output = new(), err = new();
			var reads = Task.WhenAll(run.StandardOutput.BaseStream.CopyToAsync(output), run.StandardError.BaseStream.CopyToAsync(err));
			run.WaitForExit();
			reads.Wait();
			return new Ran(run.ExitCode, output.ToArray(), err.ToArray());
		}

		// Capture, and a CalledProcessError when the program fails, as subprocess.run(check=True) raises.
		public static byte[] CheckOutput(IReadOnlyList<string> argv, string? cwd = null)
		{
			var ran = Capture(argv, cwd);
			if (ran.Code != 0)
				throw new CalledProcessError(argv, ran.Code, SurrogateEscape.Decode(ran.Stderr));
			return ran.Stdout;
		}
	}

	// Python's "surrogateescape" error handler for UTF-8: a byte that isn't part of a valid UTF-8 sequence reads as the lone surrogate
	// U+DC80 to U+DCFF, and is written back as that byte, so a name that isn't UTF-8 goes out as it came in. A valid sequence is one
	// Python's strict decoder takes: no overlong form, no encoded surrogate, nothing past U+10FFFF (Rune's rules are the same).
	static class SurrogateEscape
	{
		public static string Decode(ReadOnlySpan<byte> bytes)
		{
			var text = new StringBuilder(bytes.Length);
			while (!bytes.IsEmpty)
			{
				if (Rune.DecodeFromUtf8(bytes, out var rune, out var used) == System.Buffers.OperationStatus.Done)
				{
					text.Append(rune.ToString());
					bytes = bytes[used..];
					continue;
				}
				// Each byte of an invalid sequence is escaped on its own, as Python escapes each byte of the range it can't decode: none of the
				// bytes after the first can start a valid sequence.
				text.Append((char)(0xDC00 + bytes[0]));
				bytes = bytes[1..];
			}
			return text.ToString();
		}

		public static byte[] Encode(string text)
		{
			var bytes = new List<byte>(text.Length);
			Span<byte> buffer = stackalloc byte[4];
			for (var i = 0; i < text.Length; i++)
			{
				var c = text[i];
				if (c is >= '\uDC80' and <= '\uDCFF')
				{
					bytes.Add((byte)(c - 0xDC00));
					continue;
				}
				var rune = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? new Rune(c, text[++i])
						: Rune.IsValid(c) ? new Rune(c) : Rune.ReplacementChar;
				bytes.AddRange(buffer[..rune.EncodeToUtf8(buffer)]);
			}
			return [.. bytes];
		}
	}

	// Python's str methods the scripts read text with: str.strip(), str.splitlines() and str.lower(), and int() and repr() of a str.
	static class Py
	{
		// The characters str.isspace() counts, and Python's \s in a str pattern: char.IsWhiteSpace's, and the four separators \x1c to \x1f.
		public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

		// len(): the code points, a surrogate pair being one and a lone surrogate one too.
		public static int Len(string text)
		{
			var length = text.Length;
			for (var i = 0; i + 1 < text.Length; i++)
			{
				if (char.IsSurrogatePair(text[i], text[i + 1]))
				{
					length--;
					i++;
				}
			}
			return length;
		}

		// text[:count]: the first count code points.
		public static string Head(string text, int count)
		{
			var end = 0;
			for (var taken = 0; taken < count && end < text.Length; taken++)
				end += end + 1 < text.Length && char.IsSurrogatePair(text[end], text[end + 1]) ? 2 : 1;
			return text[..end];
		}

		// Two strings in Python's order: by code point, so an astral character sorts after U+FFFF (an ordinal sort compares UTF-16 units).
		public static int CompareCodePoints(string a, string b)
		{
			var (x, y) = (a.EnumerateRunes().GetEnumerator(), b.EnumerateRunes().GetEnumerator());
			while (true)
			{
				bool more = x.MoveNext(), other = y.MoveNext();
				if (!more || !other)
					return more == other ? 0 : more ? 1 : -1;
				if (x.Current != y.Current)
					return x.Current.Value.CompareTo(y.Current.Value);
			}
		}

		// sorted() of strings: Python's order above.
		public static readonly Comparer<string> Order = Comparer<string>.Create((a, b) => CompareCodePoints(a!, b!));

		public static string Strip(string text)
		{
			int start = 0, end = text.Length;
			while (end > 0 && IsSpace(text[end - 1]))
				end--;
			while (start < end && IsSpace(text[start]))
				start++;
			return text[start..end];
		}

		// The line breaks str.splitlines() splits on, "\r\n" being one.
		public static List<string> SplitLines(string text)
		{
			List<string> lines = [];
			var start = 0;
			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] is not ('\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029'))
					continue;
				lines.Add(text[start..i]);
				if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
					i++;
				start = i + 1;
			}
			if (start < text.Length)
				lines.Add(text[start..]);
			return lines;
		}

		// str.lower(): .NET's invariant lowering, one code point at a time, as the Factory's own Py.Lower is.
		public static string Lower(string text) => text.ToLowerInvariant();

		// int(text), or null where it raises ValueError: blanks around (str.isspace()'s, but only \t, \n, \v, \f, \r and the space below
		// U+0080, so not \x1c to \x1f), a sign, then decimal digits, any Unicode decimal
		// digit among them (an astral one too), with a single underscore only between two. A number of any size, as Python's, up to its
		// default limit of 4,300 digits (sys.get_int_max_str_digits()), past which int() refuses one.
		public static BigInteger? Int(string text)
		{
			const int MaxDigits = 4300;
			static bool Blank(char c) => c < '\x80' ? c is ' ' or (>= '\t' and <= '\r') : IsSpace(c);
			var start = 0;
			var end = text.Length;
			while (end > 0 && Blank(text[end - 1]))
				end--;
			while (start < end && Blank(text[start]))
				start++;
			var digits = text[start..end];
			var negative = digits.StartsWith('-');
			if (negative || digits.StartsWith('+'))
				digits = digits[1..];
			BigInteger value = 0;
			bool any = false, underscore = false;
			var count = 0;
			foreach (var rune in digits.EnumerateRunes())
			{
				if (rune.Value == '_' && any && !underscore)
				{
					underscore = true;
					continue;
				}
				if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.DecimalDigitNumber || ++count > MaxDigits)
					return null;
				value = value * 10 + (int)Rune.GetNumericValue(rune);
				(any, underscore) = (true, false);
			}
			return any && !underscore ? negative ? -value : value : null;
		}

		// repr() of a str: in single quotes, or double ones when it holds a ' and no ", with a backslash, that quote, \t, \n and \r escaped,
		// and each character str.isprintable() refuses (a control, format, private use, unassigned or surrogate code point, or a separator
		// but the space) written as \xhh, \uhhhh or \Uhhhhhhhh.
		public static string Repr(string text)
		{
			var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
			var repr = new StringBuilder().Append(quote);
			for (var i = 0; i < text.Length; i++)
			{
				// A lone surrogate is a code point of its own, as Python reads it.
				var rune = char.IsSurrogatePair(text, i) ? new Rune(text[i], text[++i]) : (Rune?)null;
				int code = rune?.Value ?? text[i];
				var category = rune is { } pair ? Rune.GetUnicodeCategory(pair) : char.GetUnicodeCategory(text[i]);
				repr.Append(code switch
				{
					'\\' => @"\\",
					'\t' => @"\t",
					'\n' => @"\n",
					'\r' => @"\r",
					_ when code == quote => $"\\{quote}",
					_ when category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
							or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
							|| (category == UnicodeCategory.SpaceSeparator && code != ' ') =>
							code <= 0xff ? $"\\x{code:x2}" : code <= 0xffff ? $"\\u{code:x4}" : $"\\U{code:x8}",
					_ => rune?.ToString() ?? text[i].ToString(),
				});
			}
			return repr.Append(quote).ToString();
		}
	}
	// Gh.cs's copies end here.
}
