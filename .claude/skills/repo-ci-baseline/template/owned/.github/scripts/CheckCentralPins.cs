// Fail on a central NuGet pin that no project references, under transitive pinning: check_central_pins.cs's logic, a port of
// check_central_pins.py (ADR 0017 in the Application Factory). The contract is check_central_pins.cs's header.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

using Microsoft.Win32.SafeHandles;

namespace Baseline;

// The caps a run reads within, the ones check_central_pins.cs's header names. A test lowers one, as the pytest set the module's constant.
sealed record Caps
{
	public int MaxBytes { get; init; } = 4 * 1024 * 1024;
	public long MaxTotalBytes { get; init; } = 64 * 1024 * 1024;
	// A global.json is a few hundred bytes; a larger one is skipped, and so is any past the first MaxGlobalJsonFiles.
	public int MaxGlobalJsonBytes { get; init; } = 64 * 1024;
	public int MaxGlobalJsonFiles { get; init; } = 100;
	public int MaxDepth { get; init; } = 256;
	public long MaxNodes { get; init; } = 250_000;
	// The most combinations of property values one reference may resolve through (each $(Name) with several values multiplies them).
	public int MaxExpansions { get; init; } = 256;
	public long MaxTotalExpansions { get; init; } = 100_000;
	// A name with its properties substituted is read up to MaxValue characters; an id up to MaxId (NuGet's own limit is 100).
	public int MaxValue { get; init; } = 1024;
	public int MaxId { get; init; } = 256;
	public long MaxIds { get; init; } = 250_000;
	public long MaxFiles { get; init; } = 50_000;
	public int MaxShown { get; init; } = 100;
	public int MaxPrinted { get; init; } = 300;
}

// A file or folder that can't be read, with the text C's strerror gives for why (Python's OSError.strerror).
sealed class OsError(string strerror) : Exception(strerror);

// One entry of a folder: its name, whether it is a folder (a symlink to one too, as os.walk's is_dir() follows it), and whether it is a symlink.
readonly record struct Entry(string Name, bool Folder, bool Link);

// What lstat says of a path: a symlink, a folder, or something else of Size bytes (a regular file, or a FIFO, which only the open tells
// apart: the BCL reads no file type without opening the file).
readonly record struct Stat(bool Link, bool Folder, long Size);

// How the check reads the tree. A test overrides one method to stand in for what a tree can't make: a folder that can't be listed (chmod
// doesn't stop root), a read that fails, a path swapped between the lstat and the open.
class Files
{
	public virtual List<Entry> List(string folder)
	{
		try
		{
			return [.. new DirectoryInfo(folder).EnumerateFileSystemInfos().Select(entry =>
					new Entry(entry.Name, entry is DirectoryInfo || (entry.LinkTarget is not null && Directory.Exists(entry.FullName)), entry.LinkTarget is not null))];
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new OsError(StrError(error));
		}
	}

	public virtual Stat LStat(string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (info.LinkTarget is not null)
				return new Stat(true, false, 0);
			if (Directory.Exists(path))
				return new Stat(false, true, 0);
			if (!info.Exists)
				throw new OsError(Marshal.GetPInvokeErrorMessage(2));
			return new Stat(false, false, info.Length);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new OsError(StrError(error));
		}
	}

	// The path opened for reading without following a symlink or waiting on a FIFO (open's O_NOFOLLOW and O_NONBLOCK, through libc: the
	// BCL has neither), the descriptor not passed to a child (O_CLOEXEC).
	public virtual SafeFileHandle Open(string path)
	{
		if (OperatingSystem.IsWindows())
			return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		var flags = OperatingSystem.IsMacOS() ? 0x100 | 0x4 | 0x1000000
				: (RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000) | 0x800 | 0x80000;
		var descriptor = OpenFile(path, flags);
		if (descriptor < 0)
			throw new OsError(Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError()));
		return new SafeFileHandle(descriptor, true);
	}

	// fstat's file type of the open file.
	public virtual FileHandleType TypeOf(SafeFileHandle handle) => handle.Type;

	// Up to count bytes of the open file, from its start.
	public virtual byte[] Read(SafeFileHandle handle, int count)
	{
		var data = new byte[count];
		var read = 0;
		while (read < count && RandomAccess.Read(handle, data.AsSpan(read), read) is var got and > 0)
			read += got;
		return data[..read];
	}

	// sorted() of a property's values, the one sort a test watches.
	public virtual List<string> SortedValues(IEnumerable<string> values) => [.. values.Order(Py.Order)];

	// The strerror text of a failed .NET call: its errno where .NET keeps one (an IOException's HResult), else what the error says.
	public static string StrError(Exception error) => error switch
	{
		OsError os => os.Message,
		UnauthorizedAccessException => Marshal.GetPInvokeErrorMessage(13),
		FileNotFoundException or DirectoryNotFoundException => Marshal.GetPInvokeErrorMessage(2),
		IOException { HResult: > 0 and < 4096 } io => Marshal.GetPInvokeErrorMessage(io.HResult),
		_ => error.Message,
	};

	[DllImport("libc", EntryPoint = "open", SetLastError = true)]
	static extern int OpenFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}

// An element (tag lowercased, without a prefix) or a comment (tag null), with its attributes, text and children.
sealed class Node(string? tag, Dictionary<string, string>? attributes = null, string text = "")
{
	public string? Tag { get; } = tag;
	public Dictionary<string, string> Attributes { get; } = attributes ?? [];
	public string Text { get; set; } = text;
	public List<Node> Children { get; } = [];

	// This node and every one under it, in document order, without recursion.
	public IEnumerable<Node> Iter()
	{
		var pending = new Stack<Node>([this]);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			yield return node;
			for (var i = node.Children.Count - 1; i >= 0; i--)
				pending.Push(node.Children[i]);
		}
	}
}

// A file this check won't parse.
sealed class Refused(string message) : Exception(message);

// The run needs more than its budget.
sealed class Exhausted(string message) : Exception(message);

// What the whole run may still read.
sealed class Budget
{
	readonly Dictionary<string, long> totals;
	public readonly Dictionary<string, long> Left;

	public Budget(Caps caps)
	{
		totals = new()
		{
			["files"] = caps.MaxFiles,
			["bytes"] = caps.MaxTotalBytes,
			["elements, attributes and comments"] = caps.MaxNodes,
			["ids"] = caps.MaxIds,
			["combinations of property values"] = caps.MaxTotalExpansions,
		};
		Left = new(totals);
	}

	public void Spend(string kind, long amount)
	{
		if (Left[kind] < amount)
			throw new Exhausted($"the repo needs more than the {totals[kind]} {kind} this check reads");
		Left[kind] -= amount;
	}
}

// One file or folder the check couldn't read: where (relative to the root), why, and whether that fails the check.
readonly record struct Problem(string Where, string Why, bool Blocks);

// What check() found: whether transitive pinning is on, the unreferenced pins (central file, id), the references it can't read, the
// files and folders it couldn't read, and the settings it can't read that made transitive pinning count as on (file, value).
sealed record Findings(bool Transitive, List<(string Path, string Name)> Unreferenced, List<string> Unreadable, List<Problem> Problems, List<(string Where, string Value)> UnreadSettings);

sealed class CentralPins(Caps? caps = null, Files? files = null)
{
	// The usage line argparse printed for check_central_pins.py.
	const string Usage = "[-h] [--root ROOT]";
	const string Description = "Fail on a central NuGet pin that no project references, under transitive pinning.";
	const string ReasonMarker = "transitive pin:";

	static readonly Regex MsBuildFile = new(@"\.([a-z0-9]*proj|props|targets)$", RegexOptions.CultureInvariant);
	static readonly HashSet<string> SkippedFolders = ["bin", "obj", "node_modules"];
	static readonly Regex Property = new(@"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)", RegexOptions.CultureInvariant);
	static readonly string[] Unreadable = ["$(", "@(", "%("];
	// What a file that may hold pins or turn transitive pinning on names: lowercased bytes, any NUL dropped so UTF-16 matches too.
	static readonly byte[][] CentralWords = [Encoding.ASCII.GetBytes("packageversion"), Encoding.ASCII.GetBytes("centralpackagetransitivepinningenabled")];

	public Caps Caps { get; } = caps ?? new Caps();
	readonly Files files = files ?? new Files();

	static bool HoldsUnreadable(string text) => Unreadable.Any(marker => text.Contains(marker, StringComparison.Ordinal));

	// text lowercased in ASCII alone: the only characters Python's full lower() turns into the letters "true" and the reason marker are spelled with.
	static string AsciiLower(string text) => string.Create(text.Length, text, (span, source) =>
	{
		for (var i = 0; i < source.Length; i++)
			span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
	});

	// The document node of data. Throws Refused, XmlException or ArgumentException (an encoding .NET doesn't decode) when it can't be read.
	public Node Parse(byte[] data, Budget budget)
	{
		var document = new Node("#document");
		var stack = new List<Node> { document };
		// Text comes in many pieces (a line, a CDATA section), kept in a builder per element.
		var texts = new List<StringBuilder> { new() };
		using var reader = new XmlTextReader(new MemoryStream(data))
		{
			// Names as written, prefix and all, as expat gives them without namespace processing: a prefix needs no declaration.
			Namespaces = false,
			// A DOCTYPE is parsed only to be refused below, before any entity it declares is used; nothing is fetched.
			DtdProcessing = DtdProcessing.Parse,
			XmlResolver = null,
			// Attribute values and line breaks normalized as the XML spec (and expat) has them, and an undeclared entity an error.
			Normalization = true,
			EntityHandling = EntityHandling.ExpandEntities,
			WhitespaceHandling = WhitespaceHandling.All,
		};
		while (reader.Read())
		{
			switch (reader.NodeType)
			{
				case XmlNodeType.DocumentType:
					// Entities can only be declared inside a DOCTYPE, so refusing it refuses them too.
					throw new Refused("a DOCTYPE or entity declaration, which an MSBuild file never needs");
				case XmlNodeType.Element:
					if (stack.Count > Caps.MaxDepth)
						throw new Refused($"an element nested over {Caps.MaxDepth} deep");
					budget.Spend("elements, attributes and comments", 1 + reader.AttributeCount);
					var attributes = new Dictionary<string, string>();
					for (var i = 0; i < reader.AttributeCount; i++)
					{
						reader.MoveToAttribute(i);
						attributes[reader.Name] = reader.Value;
					}
					reader.MoveToElement();
					var name = reader.Name;
					var node = new Node(Py.Lower(name[(name.LastIndexOf(':') + 1)..]), attributes);
					stack[^1].Children.Add(node);
					if (!reader.IsEmptyElement)
					{
						stack.Add(node);
						texts.Add(new StringBuilder());
					}
					break;
				case XmlNodeType.EndElement:
					stack[^1].Text = texts[^1].ToString();
					stack.RemoveAt(stack.Count - 1);
					texts.RemoveAt(texts.Count - 1);
					break;
				case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
					texts[^1].Append(reader.Value);
					break;
				case XmlNodeType.Comment:
					budget.Spend("elements, attributes and comments", 1);
					stack[^1].Children.Add(new Node(null, text: reader.Value));
					break;
			}
		}
		return document;
	}

	// text with every non-printable character as '?', so a name can't move the terminal or start a line, cut after MaxPrinted.
	public string Printable(string text)
	{
		var length = Py.Len(text);
		if (length > Caps.MaxPrinted)
			text = $"{Py.Head(text, Caps.MaxPrinted)}... ({length - Caps.MaxPrinted} more)";
		var printable = new StringBuilder(text.Length);
		for (var i = 0; i < text.Length; i++)
		{
			if (char.IsSurrogatePair(text, i))
			{
				var rune = new Rune(text[i], text[++i]);
				printable.Append(IsPrintable(rune) ? rune.ToString() : "?");
			}
			else
				printable.Append(!char.IsSurrogate(text[i]) && IsPrintable(new Rune(text[i])) ? text[i] : '?');
		}
		return printable.ToString();
	}

	// str.isprintable() of one character: not in the Other or Separator categories, a space aside.
	static bool IsPrintable(Rune rune) => rune.Value == ' ' || Rune.GetUnicodeCategory(rune) is not (System.Globalization.UnicodeCategory.Control
			or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse
			or System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.LineSeparator
			or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.SpaceSeparator);

	public static string EscapeData(string text) => text.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

	public static string EscapeProperty(string text) => EscapeData(text).Replace(":", "%3A").Replace(",", "%2C");

	// Print one problem: an annotation (kind error, warning or notice) under GitHub Actions, a plain line elsewhere. message must be built
	// from texts already through Printable() (ReportAll does that), so each is cut on its own.
	void Report(TextWriter stdout, string kind, string where, string message)
	{
		where = Printable(where);
		if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
			stdout.Write($"::{kind} file={EscapeProperty(where)}::{EscapeData(message)}\n");
		else
			stdout.Write($"{kind switch { "error" => "❌", "warning" => "⚠️ ", _ => "ℹ️ " }} {message}\n");
	}

	// Report() the first MaxShown (where, detail) items, each text through Printable(), then say how many more there were.
	void ReportAll(TextWriter stdout, string kind, IReadOnlyList<(string Where, string Detail)> items, Func<string, string, string> message)
	{
		foreach (var (where, detail) in items.Take(Caps.MaxShown))
			Report(stdout, kind, where, message(Printable(where), Printable(detail)));
		if (items.Count > Caps.MaxShown)
			stdout.Write($"... and {items.Count - Caps.MaxShown} more like the {Caps.MaxShown} above.\n");
	}

	// path's bytes, at most cap of them, or null after adding why to problems. where is path relative to the root.
	byte[]? Read(string path, string where, List<Problem> problems, Budget budget, int cap)
	{
		// An MSBuild file whose bytes weren't read may hold anything; a global.json only SDK references.
		var blocks = Py.Lower(Path.GetFileName(path)) != "global.json";
		byte[] data;
		try
		{
			var info = files.LStat(path);
			if (info.Link)
			{
				problems.Add(new(where, "it is a symlink", blocks));
				return null;
			}
			if (info.Folder)
			{
				problems.Add(new(where, "it is not a regular file", blocks));
				return null;
			}
			if (info.Size > cap)
			{
				problems.Add(new(where, $"it is over {cap} bytes", blocks));
				return null;
			}
			// The path may have changed since the lstat: opened without following a symlink or waiting on a FIFO, and checked again.
			using var handle = files.Open(path);
			if (files.TypeOf(handle) != FileHandleType.RegularFile)
			{
				problems.Add(new(where, "it is not a regular file", blocks));
				return null;
			}
			// One byte past the cap, so a file that grew since the lstat is still caught.
			data = files.Read(handle, cap + 1);
		}
		catch (Exception error) when (error is OsError or IOException or UnauthorizedAccessException)
		{
			problems.Add(new(where, $"it can't be read ({Files.StrError(error)})", blocks));
			return null;
		}
		if (data.Length > cap)
		{
			problems.Add(new(where, $"it is over {cap} bytes", blocks));
			return null;
		}
		budget.Spend("bytes", data.Length);
		return data;
	}

	// Whether an MSBuild file that couldn't be parsed may hold pins or turn transitive pinning on.
	static bool MayBeCentral(string path, byte[] data)
	{
		var folded = data.Where(b => b != 0).Select(b => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b).ToArray();
		return Py.Lower(Path.GetFileName(path)) == "directory.packages.props" || CentralWords.Any(word => folded.AsSpan().IndexOf(word) >= 0);
	}

	// path relative to root, with / between its parts.
	static string Relative(string path, string root) => path == root ? "." : path[(root.EndsWith('/') ? root.Length : root.Length + 1)..];

	// (MSBuild files, global.json files) under root, outside the skipped folders. A folder it can't walk goes in problems. MSBuild files are
	// sorted by path; global.json files by depth, then path, so the root's, the one the .NET host reads for the whole repo, comes first, and
	// any cut at MaxGlobalJsonFiles drops the deepest. The folders are walked as os.walk walks them: each one's subfolders in sorted order,
	// depth first.
	(List<string> MsBuild, List<string> GlobalJson) Walk(string root, List<Problem> problems, Budget budget)
	{
		List<string> msbuild = [], globalJson = [];
		string Where(string folder) => Relative(folder, root) is var relative && relative == "." ? "." : relative + "/";
		var pending = new Stack<string>([root]);
		while (pending.Count > 0)
		{
			var folder = pending.Pop();
			List<Entry> entries;
			try
			{
				entries = files.List(folder);
			}
			catch (OsError error)
			{
				problems.Add(new(Where(folder), $"it can't be listed ({error.Message})", true));
				continue;
			}
			List<string> kept = [];
			foreach (var entry in entries.Where(entry => entry.Folder).OrderBy(entry => entry.Name, Py.Order))
			{
				if (SkippedFolders.Contains(entry.Name) || entry.Name.StartsWith('.'))
					continue;
				if (entry.Link)
				{
					problems.Add(new(Where(Path.Join(folder, entry.Name)), "it is a symlinked folder, which isn't walked", true));
					continue;
				}
				kept.Add(entry.Name);
			}
			foreach (var entry in entries.Where(entry => !entry.Folder))
			{
				var lowered = Py.Lower(entry.Name);
				if (MsBuildFile.IsMatch(lowered))
				{
					budget.Spend("files", 1);
					msbuild.Add(Path.Join(folder, entry.Name));
				}
				else if (lowered == "global.json")
				{
					budget.Spend("files", 1);
					globalJson.Add(Path.Join(folder, entry.Name));
				}
			}
			for (var i = kept.Count - 1; i >= 0; i--)
				pending.Push(Path.Join(folder, kept[i]));
		}
		return ([.. msbuild.Order(PyPath.Order)],
				[.. globalJson.OrderBy(path => path.Split('/').Length).ThenBy(path => path, PyPath.Order)]);
	}

	// JSON text without // and /* */ comments or trailing commas, as the .NET host reads global.json. Two passes, as the Python's two
	// regular expressions made them: the first drops each comment outside a string (a string runs to the end of the text when it isn't
	// closed, and so does a /* comment); the second, outside strings, drops a comma before a closing bracket, keeping the space after it,
	// and any comment the first pass left by joining two halves. Comments out first: a comment between a comma and its bracket would
	// otherwise keep the comma. Each pass reads every character once.
	public static string WithoutComments(string text) => Pass(Pass(text, false), true);

	static string Pass(string text, bool commas)
	{
		var kept = new StringBuilder(text.Length);
		var i = 0;
		while (i < text.Length)
		{
			var c = text[i];
			if (c == '"')
			{
				// A string: [^"\\] or \ and any character, then the closing " if there is one.
				var start = i++;
				while (i < text.Length && text[i] != '"')
				{
					if (text[i] != '\\')
						i++;
					else if (i + 1 < text.Length)
						i += 2;
					else
						break;
				}
				if (i < text.Length && text[i] == '"')
					i++;
				kept.Append(text, start, i - start);
			}
			else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
			{
				i += 2;
				while (i < text.Length && text[i] != '\n')
					i++;
			}
			else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
			{
				var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
				i = end < 0 ? text.Length : end + 2;
			}
			else if (commas && c == ',' && FollowedByBracket(text, i + 1) is var after and >= 0)
			{
				kept.Append(text, i + 1, after - i - 1);
				i = after;
			}
			else
			{
				kept.Append(c);
				i++;
			}
		}
		return kept.ToString();
	}

	// Where the } or ] after the whitespace from start is, or -1 when something else comes first (Python's \s(*)(?=[}\]])).
	static int FollowedByBracket(string text, int start)
	{
		var i = start;
		while (i < text.Length && Py.IsSpace(text[i]))
			i++;
		return i < text.Length && text[i] is '}' or ']' ? i : -1;
	}

	// The lowercased ids under a global.json's msbuild-sdks, or null when it isn't JSON the host would read. Each counts as an id; one over
	// MaxId characters can't be a pin this check reads, so it is left out.
	public HashSet<string>? GlobalJsonSdks(byte[] data, Budget budget)
	{
		JsonDocument document;
		try
		{
			var bytes = data.AsSpan();
			if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
				bytes = bytes[3..];
			var text = new UTF8Encoding(false, true).GetString(bytes);
			document = JsonDocument.Parse(WithoutComments(text), new JsonDocumentOptions { MaxDepth = 1000 });
		}
		catch (Exception error) when (error is JsonException or ArgumentException)
		{
			return null;
		}
		using (document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				return [];
			// json.loads keeps a repeated key's last value.
			JsonElement? sdks = null;
			foreach (var property in document.RootElement.EnumerateObject())
			{
				if (property.Name == "msbuild-sdks")
					sdks = property.Value;
			}
			if (sdks is not { ValueKind: JsonValueKind.Object } found)
				return [];
			HashSet<string> names;
			try
			{
				names = [.. found.EnumerateObject().Select(property => property.Name)];
			}
			catch (InvalidOperationException)
			{
				return null;
			}
			budget.Spend("ids", names.Count);
			return [.. names.Where(name => Py.Len(name) <= Caps.MaxId).Select(Py.Lower)];
		}
	}

	// The ids in an Include, Update or Sdk value: split on ';', any '/version' dropped, blanks left out. Every entry counts as an id, blank or
	// not, before the value is split; an absent or empty value counts as none.
	public static IEnumerable<string> Ids(string? value, Budget budget)
	{
		if (string.IsNullOrEmpty(value))
			yield break;
		budget.Spend("ids", value.Count(c => c == ';') + 1);
		foreach (var part in value.Split(';'))
		{
			var stripped = Py.Strip(part);
			var name = Py.Strip(stripped[..(stripped.IndexOf('/') is var slash and >= 0 ? slash : stripped.Length)]);
			if (name.Length > 0)
				yield return name;
		}
	}

	// Add to found (lowercased name -> its values) every property a PropertyGroup in document sets, wherever the group is.
	static void Properties(Node document, Dictionary<string, HashSet<string>> found)
	{
		foreach (var node in document.Iter())
		{
			if (node.Tag != "propertygroup")
				continue;
			foreach (var child in node.Children)
			{
				if (child.Tag is { } tag)
				{
					if (!found.TryGetValue(tag, out var values))
						found[tag] = values = [];
					values.Add(Py.Strip(child.Text));
				}
			}
		}
	}

	// The lowercased ids name can be, with each $(Name) replaced by every value the repo gives it, or null when it can't be read.
	HashSet<string>? Resolve(string name, Values values, Budget budget, Dictionary<string, HashSet<string>?> cache)
	{
		if (Py.Len(name) > Caps.MaxId)
			return null;
		if (!HoldsUnreadable(name))
			return [Py.Lower(name)];
		if (!cache.TryGetValue(name, out var resolved))
			cache[name] = resolved = Expand(name, values, budget);
		return resolved;
	}

	// The repo's properties: lowercased name -> its values, and lowercased name -> its longest value's length.
	sealed record Values(Dictionary<string, HashSet<string>> Found, Dictionary<string, int> Widest);

	HashSet<string>? Expand(string name, Values values, Budget budget)
	{
		// name is at most MaxId characters, so it holds at most MaxId / 4 placeholders.
		var placeholders = Property.Matches(name).Select(match => match.Groups[1].Value).ToList();
		var keys = placeholders.Select(Py.Lower).Distinct().ToList();
		long count = 1;
		foreach (var key in keys)
		{
			count *= values.Found.TryGetValue(key, out var found) ? found.Count : 0;
			// Past the cap (or at none) it can't grow back under it: a later key multiplies it by at least zero.
			if (count == 0 || count > Caps.MaxExpansions)
				return null;
		}
		// The longest the substituted name can be, from the longest value of each placeholder: past MaxValue it isn't built.
		var longest = Py.Len(Property.Replace(name, "")) + placeholders.Sum(key => (long)values.Widest[Py.Lower(key)]);
		if (longest > Caps.MaxValue)
			return null;
		budget.Spend("combinations of property values", count);
		var choices = keys.Select(key => files.SortedValues(values.Found[key])).ToList();
		HashSet<string> resolved = [];
		// itertools.product: the last key's value changes fastest.
		var index = new int[keys.Count];
		for (var combination = 0L; combination < count; combination++)
		{
			var mapping = new Dictionary<string, string>();
			for (var k = 0; k < keys.Count; k++)
				mapping[keys[k]] = choices[k][index[k]];
			var text = Property.Replace(name, match => mapping[Py.Lower(match.Groups[1].Value)]);
			if (HoldsUnreadable(text))
				return null;
			foreach (var part in Ids(text, budget))
			{
				if (Py.Len(part) > Caps.MaxId)
					return null;
				resolved.Add(Py.Lower(part));
			}
			for (var k = keys.Count - 1; k >= 0; k--)
			{
				if (++index[k] < choices[k].Count)
					break;
				index[k] = 0;
			}
		}
		return resolved;
	}

	// The ids document references, as written.
	static IEnumerable<string> References(Node document, Budget budget)
	{
		foreach (var node in document.Iter())
		{
			IEnumerable<string> found = node.Tag switch
			{
				"packagereference" or "globalpackagereference" or "packagedownload" =>
						Ids(node.Attributes.GetValueOrDefault("Include"), budget).Concat(Ids(node.Attributes.GetValueOrDefault("Update"), budget)),
				"project" or "import" => Ids(node.Attributes.GetValueOrDefault("Sdk"), budget),
				"sdk" => Ids(node.Attributes.GetValueOrDefault("Name"), budget),
				_ => [],
			};
			foreach (var name in found)
				yield return name;
		}
	}

	// (id as written, has a reason) for each PackageVersion in any ItemGroup of document.
	static IEnumerable<(string Name, bool Reason)> Pins(Node document, Budget budget)
	{
		foreach (var group in document.Iter())
		{
			if (group.Tag != "itemgroup")
				continue;
			Node? before = null;
			foreach (var node in group.Children)
			{
				if (node.Tag is null)
				{
					before = node;
					continue;
				}
				if (node.Tag == "packageversion")
				{
					var text = before is not null ? Py.Strip(before.Text) : "";
					var reason = text.Length >= ReasonMarker.Length && AsciiLower(text[..ReasonMarker.Length]) == ReasonMarker
							&& Py.Strip(text[ReasonMarker.Length..]).Length > 0;
					foreach (var name in Ids(node.Attributes.GetValueOrDefault("Include"), budget))
						yield return (name, reason);
				}
				before = null;
			}
		}
	}

	// What the check finds under root (resolved). Throws Exhausted when the repo needs more than the run's budget.
	public Findings Check(string root)
	{
		var budget = new Budget(Caps);
		List<Problem> problems = [];
		var (msbuild, globalJson) = Walk(root, problems, budget);
		List<(string Path, Node Document)> documents = [];
		foreach (var path in msbuild)
		{
			var where = Relative(path, root);
			if (Read(path, where, problems, budget, Caps.MaxBytes) is not { } data)
				continue;
			try
			{
				documents.Add((path, Parse(data, budget)));
			}
			catch (Exception error) when (error is XmlException or Refused or ArgumentException or NotSupportedException)
			{
				problems.Add(new(where, $"it isn't well-formed MSBuild XML ({error.Message.TrimEnd('.')})", MayBeCentral(path, data)));
			}
		}
		HashSet<string> referenced = [];
		foreach (var path in globalJson.Skip(Caps.MaxGlobalJsonFiles))
		{
			// It can hide only SDK references, which makes the check stricter.
			problems.Add(new(Relative(path, root), $"it is past the first {Caps.MaxGlobalJsonFiles} global.json files, which are all this check reads", false));
		}
		foreach (var path in globalJson.Take(Caps.MaxGlobalJsonFiles))
		{
			if (Read(path, Relative(path, root), problems, budget, Caps.MaxGlobalJsonBytes) is not { } data)
				continue;
			if (GlobalJsonSdks(data, budget) is { } sdks)
				referenced.UnionWith(sdks);
			else
				problems.Add(new(Relative(path, root), "it isn't JSON the .NET host reads", false));
		}
		Dictionary<string, HashSet<string>> found = [];
		foreach (var (_, document) in documents)
			Properties(document, found);
		var values = new Values(found, found.ToDictionary(pair => pair.Key, pair => pair.Value.Max(Py.Len)));
		Dictionary<string, HashSet<string>?> cache = [];
		// The setting is on when any file sets it to true, or to a value holding $(Name) that resolves, as an id does, to true for one
		// combination of values. A value without $(, @( or %( is compared as written, whatever its length. One that holds them and can't
		// be resolved counts as on, so the check fails closed: under it an unreferenced pin either holds a package back or does nothing.
		// Resolving splits on ';' and drops '/...', so $(P) with P set to "true;x" counts as on, which is on the safe side.
		var transitive = false;
		List<(string Where, string Value)> unreadSettings = [];
		HashSet<(string Where, string Value)> named = [];
		foreach (var (path, document) in documents)
		{
			foreach (var node in document.Iter())
			{
				if (node.Tag != "centralpackagetransitivepinningenabled")
					continue;
				var value = Py.Strip(node.Text);
				if (!HoldsUnreadable(value))
				{
					transitive = transitive || AsciiLower(value) == "true";
					continue;
				}
				var resolved = Resolve(value, values, budget, cache);
				if (resolved is null)
				{
					if (named.Add((Relative(path, root), value)))
						unreadSettings.Add((Relative(path, root), value));
				}
				else if (resolved.Contains("true"))
					transitive = true;
			}
		}
		if (!transitive && unreadSettings.Count > 0)
			transitive = true;
		else
			unreadSettings = [];
		List<string> unreadable = [];
		HashSet<string> unread = [];
		foreach (var (_, document) in documents)
		{
			foreach (var name in References(document, budget))
			{
				if (Resolve(name, values, budget, cache) is { } resolved)
					referenced.UnionWith(resolved);
				else if (unread.Add(name))
					unreadable.Add(name);
			}
		}
		List<(string Path, string Name)> unreferenced = [];
		HashSet<(string Path, string Key)> reported = [];
		var builtPin = false;
		foreach (var (path, document) in documents)
		{
			if (Py.Lower(Path.GetFileName(path)) != "directory.packages.props")
				continue;
			foreach (var (name, reason) in Pins(document, budget))
			{
				if (reason)
					continue;
				// Only a $(Name) can take a value from a file the check couldn't read, and only a name that is readable once its properties
				// are substituted can become a pin: a bare $(, @( or %( can't be read whatever the files say.
				builtPin = builtPin || (Property.IsMatch(name) && !HoldsUnreadable(Property.Replace(name, "")));
				if (Resolve(name, values, budget, cache) is not { } resolved)
					continue;
				foreach (var key in resolved.Where(key => !referenced.Contains(key)).Order(Py.Order))
				{
					if (reported.Add((path, key)))
						unreferenced.Add((path, resolved.Count == 1 ? name : key));
				}
			}
		}
		if (transitive && builtPin)
		{
			// A skipped MSBuild file may set a value of the property such a pin is built from, so it could hide a pin. With transitive
			// pinning off no pin is reported, and a skipped file whose bytes name the setting (in ASCII, or UTF-16 or UTF-32 with the NULs
			// dropped) already fails; one in an encoding that spells it otherwise is the gap MayBeCentral() leaves.
			problems = [.. problems.Select(problem => problem with { Blocks = problem.Blocks || !Py.Lower(problem.Where).EndsWith("global.json", StringComparison.Ordinal) })];
		}
		return new Findings(transitive, unreferenced, unreadable, problems, unreadSettings);
	}

	// The script: exit 0 on a pass, 1 on a failure, 2 on a usage error.
	public int Run(IEnumerable<string> args, TextWriter stdout, TextWriter stderr)
	{
		var parser = new Arguments("check_central_pins.cs", Usage, Description).Option("--root", "the repo root");
		if (parser.Parse(args, stdout, stderr) is int exit)
			return exit;
		var given = parser.Value("--root", ".");
		var root = PyPath.Resolve(given);
		if (!Directory.Exists(root))
			return parser.Error($"--root {Printable(given)} is not a folder", stderr);

		Findings findings;
		try
		{
			findings = Check(root);
		}
		catch (Exhausted error)
		{
			Report(stdout, "error", ".", $"Not checked: {error.Message}.");
			return 1;
		}
		ReportAll(stdout, "notice", findings.UnreadSettings, (where, value) =>
				$"{where} sets CentralPackageTransitivePinningEnabled to {value}, which this check can't read without evaluating MSBuild, so it counts transitive pinning as on.");
		var skipped = findings.Problems.Where(problem => !problem.Blocks).Select(problem => (problem.Where, problem.Why)).ToList();
		var blocking = findings.Problems.Where(problem => problem.Blocks).Select(problem => (problem.Where, problem.Why)).ToList();
		ReportAll(stdout, "notice", skipped, (where, why) => $"{where} was skipped: {why}. A reference or property in it wasn't seen.");
		ReportAll(stdout, "error", blocking, (where, why) =>
				$"{where} can't be checked: {why}. It may hold central pins, turn transitive pinning on or set a property a pin is built from.");
		var status = blocking.Count > 0 ? 1 : 0;
		if (!findings.Transitive)
		{
			if (blocking.Count == 0)
				stdout.Write("Transitive pinning is off in every MSBuild file read (or there is no central package management): no pin found can hold a package back.\n");
		}
		else if (findings.Unreferenced.Count == 0)
		{
			if (blocking.Count == 0)
				stdout.Write("Every central pin is referenced by a project, or kept with a reason.\n");
		}
		else
		{
			var pins = findings.Unreferenced.Select(pin => (Relative(pin.Path, root), pin.Name)).ToList();
			ReportAll(stdout, findings.Unreadable.Count > 0 ? "warning" : "error", pins, (where, name) => $"{name} is pinned in {where} but no project references it");
			if (findings.Unreadable.Count > 0)
			{
				var names = findings.Unreadable.Select(Printable).Order(Py.Order).ToList();
				var shown = string.Join(", ", names.Take(Caps.MaxShown)) + (names.Count > Caps.MaxShown ? $" and {names.Count - Caps.MaxShown} more" : "");
				stdout.Write($"Reported as warnings: a reference this check can't read without evaluating MSBuild ({shown}) may name any of the pins above.\n");
			}
			else
			{
				status = 1;
				stderr.Write("Under CentralPackageTransitivePinningEnabled such a pin holds the package at its version wherever it comes in through another one, "
						+ "and Dependabot doesn't raise it when it bumps that one, so restore fails with NU1109. Delete each pin above. "
						+ "To keep one on purpose (a security fix, or a package an SDK references itself), put a comment right before it: "
						+ "<!-- Transitive pin: <the reason> -->.\n");
			}
		}
		if (blocking.Count > 0)
		{
			stdout.Write("Not a pass: the files and folders above couldn't be checked, and may hold what the check looks for.\n");
			stderr.Write("Each file or folder that can't be checked must be readable for the check to pass: a regular file (not a symlink) "
					+ $"of at most {Caps.MaxBytes} bytes holding well-formed XML without a DOCTYPE, nested at most {Caps.MaxDepth} deep, in an encoding "
					+ ".NET decodes; or a folder that isn't a symlink and can be listed.\n");
		}
		return status;
	}
}
