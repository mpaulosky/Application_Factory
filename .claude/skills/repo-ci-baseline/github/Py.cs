// Python's readings of a JSON value (Py), which settings.cs and the bench (../bench/) print and compare by: in a file of its own, so the bench
// includes it without the rest of settings.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Python's readings of a JSON value, which settings.py printed and compared by: repr() and str(), json.dumps(), ==, sorted(), the
// truth of a value (J.B), dict.get() (which fails on anything but an object), str.splitlines() and str.strip(), and base64.b64decode().
static class Py
{
	// The characters str.isspace() counts: char.IsWhiteSpace's, and the four separators \x1c to \x1f.
	static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

	public static string Strip(string text)
	{
		var end = text.Length;
		while (end > 0 && IsSpace(text[end - 1]))
			end--;
		return LStrip(text[..end]);
	}

	public static string LStrip(string text)
	{
		var start = 0;
		while (start < text.Length && IsSpace(text[start]))
			start++;
		return text[start..];
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

	// str.split() with no separator: the runs of characters between spaces (those str.isspace() counts), none of them empty.
	public static List<string> Split(string text)
	{
		List<string> words = [];
		var start = -1;
		for (var i = 0; i <= text.Length; i++)
		{
			if (i < text.Length && !IsSpace(text[i]))
			{
				if (start < 0)
					start = i;
				continue;
			}
			if (start >= 0)
				words.Add(text[start..i]);
			start = -1;
		}
		return words;
	}

	// text[:count] and text[-count:], counted in code points as Python indexes a str: a surrogate pair is one, and so is a lone surrogate.
	public static string Head(string text, int count) => text[..Offset(text, count)];

	public static string Tail(string text, int count) => text[Offset(text, Math.Max(0, Length(text) - count))..];

	static int Length(string text) => Offset(text, int.MaxValue, counting: true);

	// The UTF-16 offset of the code point at index count (the string's length past its last), or with counting the number of code points.
	static int Offset(string text, int count, bool counting = false)
	{
		var (offset, points) = (0, 0);
		while (offset < text.Length && points < count)
		{
			offset += char.IsSurrogatePair(text, offset) ? 2 : 1;
			points++;
		}
		return counting ? points : offset;
	}

	// str.lower(): .NET's invariant lowering, one code point at a time.
	public static string Lower(string text) => text.ToLowerInvariant();

	// base64.b64decode(text): what isn't in the alphabet is dropped first, as its default (validate=False) does.
	public static byte[] B64Decode(string text) =>
			Convert.FromBase64String(new string([.. text.Where(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '=')]));

	// Strings in code point order, as Python sorts them (an ordinal sort puts U+E000 to U+FFFF after the code points above them).
	public static readonly Comparer<string> CodePoints = Comparer<string>.Create(CompareCodePoints);

	static int CompareCodePoints(string? a, string? b)
	{
		if (a is null || b is null)
			return a is null ? b is null ? 0 : -1 : 1;
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

	// d.get(key, fallback) on a dict: anything but an object fails, as None.get() and [].get() do.
	public static JsonNode? Get(JsonNode? node, string key, JsonNode? fallback = null)
	{
		if (node is JsonObject o)
			return o.TryGetPropertyValue(key, out var value) ? value : fallback;
		throw new InvalidOperationException($"'{Kind(node)}' object has no attribute 'get'");
	}

	// d.items() of a dict.
	public static IEnumerable<KeyValuePair<string, JsonNode?>> Pairs(JsonNode? node) =>
			node as JsonObject ?? throw new InvalidOperationException($"'{Kind(node)}' object has no attribute 'items'");

	// What a for loop iterates: a list's items, a dict's keys, a string's characters; anything else isn't iterable.
	public static IEnumerable<JsonNode?> Items(JsonNode? node) => node switch
	{
		JsonArray a => [.. a],
		JsonObject o => [.. o.Select(pair => (JsonNode?)pair.Key)],
		JsonValue v when v.GetValueKind() == JsonValueKind.String => [.. J.S(v)!.EnumerateRunes().Select(r => (JsonNode?)r.ToString())],
		_ => throw new InvalidOperationException($"'{Kind(node)}' object is not iterable"),
	};

	// A list, for adding to: the node itself when it is one.
	public static JsonNode? List(JsonNode? node) => node is JsonArray ? node : throw new InvalidOperationException($"'{Kind(node)}' is not a list");

	// A string where settings.py used one as a string (a name joined, a label's color): anything else fails.
	public static string Text(JsonNode? node) => J.S(node) ?? throw new InvalidOperationException($"expected str instance, {Kind(node)} found");

	static string Kind(JsonNode? node) => node switch
	{
		null => "NoneType",
		JsonObject => "dict",
		JsonArray => "list",
		_ => node.GetValueKind() switch
		{
			JsonValueKind.String => "str",
			JsonValueKind.True or JsonValueKind.False => "bool",
			_ => IsInt(node) ? "int" : "float",
		},
	};

	// Python's int() of decimal digits, any Unicode decimal digit among them.
	public static BigInteger ParseInt(string digits)
	{
		BigInteger value = 0;
		foreach (var rune in digits.EnumerateRunes())
			value = value * 10 + (int)Rune.GetNumericValue(rune);
		return value;
	}

	// A whole number as a JSON value.
	public static JsonNode Number(BigInteger value) => JsonNode.Parse(value.ToString(CultureInfo.InvariantCulture))!;

	// Whether json.loads() reads a number as an int: one with no fraction or exponent.
	static bool IsInt(JsonNode node) => !node.ToJsonString().AsSpan().ContainsAny(".eE");

	// The value isinstance(value, int) holds for (an int, or a bool as 1 or 0), else null.
	public static BigInteger? Int(JsonNode? node) =>
			node is JsonValue && (node.GetValueKind() is JsonValueKind.True or JsonValueKind.False || (node.GetValueKind() == JsonValueKind.Number && IsInt(node)))
					? AsInt(node)
					: null;

	static BigInteger AsInt(JsonNode node) => node.GetValueKind() switch
	{
		JsonValueKind.True => 1,
		JsonValueKind.False => 0,
		_ => BigInteger.Parse(node.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
	};

	static double AsFloat(JsonNode node) => node.GetValueKind() switch
	{
		JsonValueKind.Number when IsInt(node) => (double)AsInt(node),
		JsonValueKind.Number => double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
		_ => (double)AsInt(node),
	};

	static bool IsNumeric(JsonNode? node) => node is JsonValue && node.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

	// a == b: a bool is the int 1 or 0, an int equals a float of its value, a list equals one with equal items in order, and a dict one
	// with the same keys and equal values in any order.
	public static bool Eq(JsonNode? a, JsonNode? b)
	{
		if (a is null || b is null)
			return a is null && b is null;
		if (IsNumeric(a) && IsNumeric(b))
			return (a.GetValueKind() == JsonValueKind.Number && !IsInt(a)) || (b.GetValueKind() == JsonValueKind.Number && !IsInt(b))
					? AsFloat(a) == AsFloat(b)
					: AsInt(a) == AsInt(b);
		return (a, b) switch
		{
			(JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(pair => Eq(pair.First, pair.Second)),
			(JsonObject x, JsonObject y) => x.Count == y.Count && x.All(pair => y.TryGetPropertyValue(pair.Key, out var other) && Eq(pair.Value, other)),
			(JsonValue, JsonValue) when a.GetValueKind() == JsonValueKind.String && b.GetValueKind() == JsonValueKind.String => J.S(a) == J.S(b),
			_ => false,
		};
	}

	// a < b, as sorted() compares: numbers by value, strings by code point, lists by their first unequal items, then by length; any
	// other pair (None among them) can't be ordered, a TypeError in Python.
	public static int Compare(JsonNode? a, JsonNode? b)
	{
		if (IsNumeric(a) && IsNumeric(b))
			return (a!.GetValueKind() == JsonValueKind.Number && !IsInt(a)) || (b!.GetValueKind() == JsonValueKind.Number && !IsInt(b))
					? AsFloat(a).CompareTo(AsFloat(b!))
					: AsInt(a).CompareTo(AsInt(b!));
		if (a is JsonValue && b is JsonValue && a.GetValueKind() == JsonValueKind.String && b.GetValueKind() == JsonValueKind.String)
			return CompareCodePoints(J.S(a), J.S(b));
		if (a is JsonArray x && b is JsonArray y)
		{
			for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
			{
				if (!Eq(x[i], y[i]))
					return Compare(x[i], y[i]);
			}
			return x.Count.CompareTo(y.Count);
		}
		throw new InvalidOperationException($"'<' not supported between instances of '{Kind(a)}' and '{Kind(b)}'");
	}

	// sorted(items), stable as Python's sort is.
	public static JsonArray Sorted(IEnumerable<JsonNode?> items)
	{
		var list = items.ToList();
		return new([.. list.Order(Comparer<JsonNode?>.Create(Compare))]);
	}

	// str(value): a string as it is, anything else as repr() writes it.
	public static string Str(JsonNode? node) => node is JsonValue && node.GetValueKind() == JsonValueKind.String ? J.S(node)! : Repr(node);

	// repr(value) of what json.loads() returned.
	public static string Repr(JsonNode? node) => node switch
	{
		null => "None",
		JsonArray a => "[" + string.Join(", ", a.Select(Repr)) + "]",
		JsonObject o => "{" + string.Join(", ", o.Select(pair => $"{ReprString(pair.Key)}: {Repr(pair.Value)}")) + "}",
		_ => node.GetValueKind() switch
		{
			JsonValueKind.String => ReprString(J.S(node)!),
			JsonValueKind.True => "True",
			JsonValueKind.False => "False",
			_ => IsInt(node) ? AsInt(node).ToString(CultureInfo.InvariantCulture) : FloatRepr(AsFloat(node)),
		},
	};

	// repr() of a str: in single quotes, or double ones when it holds a ' and no ", with \\, the quote, \t \n \r, the other ASCII
	// controls as \xNN, and every code point str.isprintable() refuses (a control, a format character, a lone surrogate, an unassigned or
	// private-use one, a separator other than the space) escaped as \xNN, \uNNNN or \UNNNNNNNN.
	public static string ReprString(string text)
	{
		var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
		var output = new StringBuilder().Append(quote);
		for (var i = 0; i < text.Length; i++)
		{
			int point = text[i];
			if (char.IsSurrogatePair(text, i))
				point = char.ConvertToUtf32(text[i], text[++i]);
			if (point == quote || point == '\\')
				output.Append('\\').Append((char)point);
			else if (point == '\t')
				output.Append(@"\t");
			else if (point == '\n')
				output.Append(@"\n");
			else if (point == '\r')
				output.Append(@"\r");
			else if (point is < 0x20 or 0x7f)
				output.Append(CultureInfo.InvariantCulture, $@"\x{point:x2}");
			else if (point < 0x7f || Printable(point))
				output.Append(char.ConvertFromUtf32(point));
			else if (point <= 0xff)
				output.Append(CultureInfo.InvariantCulture, $@"\x{point:x2}");
			else if (point <= 0xffff)
				output.Append(CultureInfo.InvariantCulture, $@"\u{point:x4}");
			else
				output.Append(CultureInfo.InvariantCulture, $@"\U{point:x8}");
		}
		return output.Append(quote).ToString();
	}

	static bool Printable(int point) =>
			point is < 0xd800 or > 0xdfff
			&& CharUnicodeInfo.GetUnicodeCategory(point) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
					or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
					or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);

	// repr() of a float: the shortest digits that read back as it, positional from 1e-4 up to 1e16 (with .0 when whole), else as d.ddde±XX.
	public static string FloatRepr(double value)
	{
		if (double.IsNaN(value))
			return "nan";
		if (double.IsInfinity(value))
			return value > 0 ? "inf" : "-inf";
		var sign = double.IsNegative(value) ? "-" : "";
		if (value == 0)
			return sign + "0.0";
		var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
		var parts = shortest.Split('E');
		var exponent = parts.Length > 1 ? int.Parse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
		var point = parts[0].IndexOf('.') is var dot and >= 0 ? dot : parts[0].Length;
		var digits = parts[0].Replace(".", "", StringComparison.Ordinal);
		var leading = digits.Length - digits.TrimStart('0').Length;
		digits = digits.TrimStart('0');
		// The scientific exponent of the first digit.
		var power = point - leading + exponent - 1;
		digits = digits.TrimEnd('0') is { Length: > 0 } trimmed ? trimmed : "0";
		if (power is >= -4 and < 16)
		{
			if (power < 0)
				return $"{sign}0.{new string('0', -power - 1)}{digits}";
			var whole = digits.Length > power + 1 ? digits[..(power + 1)] : digits.PadRight(power + 1, '0');
			var fraction = digits.Length > power + 1 ? digits[(power + 1)..] : "0";
			return $"{sign}{whole}.{fraction}";
		}
		var mantissa = digits.Length > 1 ? $"{digits[0]}.{digits[1..]}" : digits;
		return $"{sign}{mantissa}e{(power < 0 ? '-' : '+')}{Math.Abs(power):00}";
	}

	// json.dumps(value): separators ", " and ": ", or with indent the items on lines of their own (",\n" and ": "), every character
	// outside printable ASCII escaped (ensure_ascii), and a float as repr() writes it (NaN and Infinity as JavaScript does).
	public static string Dumps(JsonNode? node, int? indent = null)
	{
		var output = new StringBuilder();
		Dump(node, indent, 0, output);
		return output.ToString();
	}

	static void Dump(JsonNode? node, int? indent, int depth, StringBuilder output)
	{
		string Break(int level) => indent is int width ? "\n" + new string(' ', width * level) : "";
		var separator = indent is null ? ", " : ",";
		switch (node)
		{
			case null:
				output.Append("null");
				return;
			case JsonArray a:
				if (a.Count == 0)
				{
					output.Append("[]");
					return;
				}
				output.Append('[');
				for (var i = 0; i < a.Count; i++)
				{
					output.Append(i > 0 ? separator : "").Append(Break(depth + 1));
					Dump(a[i], indent, depth + 1, output);
				}
				output.Append(Break(depth)).Append(']');
				return;
			case JsonObject o:
				if (o.Count == 0)
				{
					output.Append("{}");
					return;
				}
				output.Append('{');
				var first = true;
				foreach (var (key, value) in o)
				{
					output.Append(first ? "" : separator).Append(Break(depth + 1));
					first = false;
					DumpString(key, output);
					output.Append(": ");
					Dump(value, indent, depth + 1, output);
				}
				output.Append(Break(depth)).Append('}');
				return;
		}
		switch (node.GetValueKind())
		{
			case JsonValueKind.String:
				DumpString(J.S(node)!, output);
				return;
			case JsonValueKind.True:
				output.Append("true");
				return;
			case JsonValueKind.False:
				output.Append("false");
				return;
			default:
				if (IsInt(node))
					output.Append(AsInt(node).ToString(CultureInfo.InvariantCulture));
				else
				{
					var value = AsFloat(node);
					output.Append(double.IsNaN(value) ? "NaN" : double.IsPositiveInfinity(value) ? "Infinity" : double.IsNegativeInfinity(value) ? "-Infinity" : FloatRepr(value));
				}
				return;
		}
	}

	static void DumpString(string text, StringBuilder output)
	{
		output.Append('"');
		foreach (var c in text)
		{
			output.Append(c switch
			{
				'"' => "\\\"",
				'\\' => @"\\",
				'\n' => @"\n",
				'\r' => @"\r",
				'\t' => @"\t",
				'\b' => @"\b",
				'\f' => @"\f",
				>= ' ' and <= '~' => c.ToString(),
				_ => ((int)c).ToString("x4", CultureInfo.InvariantCulture) is var hex ? $@"\u{hex}" : "",
			});
		}
		output.Append('"');
	}
}
