// A YAML file as the JsonNode tree PyYAML's yaml.safe_load() returned, for the bench and the tests that read the Template's workflows. YamlDotNet
// (the one package the bench takes, ADR 0017's Update for mpaulosky/Application_Factory#161) parses it; this gives each plain scalar the type
// PyYAML's YAML 1.1 resolvers gave it: null (~, null, an empty value), a bool (true, yes, on and false, no, off, in any of their three cases), a
// decimal int or float (with _ between its digits), and a string for anything else. A quoted scalar, a block scalar and a !!str one are strings.
// What these files don't hold is left out on purpose: an int in base 2, 8, 16 or 60, .inf and .nan, a !!timestamp and the << merge key read as
// strings (and the key <<), where PyYAML read them as numbers, dates and merged mappings. A key is its scalar's text (PyYAML's True for on is "on"),
// and a key given twice is refused, as GitHub refuses it in a workflow, where PyYAML kept its last value.
//
// A case's head is read through here too, so a text is refused (YamlException) past fixed limits that the Template's workflows sit far inside:
// MaxText characters, and MaxDepth levels of nesting, MaxNodes nodes and MaxScalarText characters of scalars once its aliases are read in. The
// tree is built from YamlDotNet's parser events with a stack of its own, not through its recursive YamlStream, so no nesting can overflow the
// call stack, and an alias is checked against the depth and charged the whole size of what it names before that is copied, so an alias bomb
// is refused before it grows. An anchor names a node in its own document only.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

static class Yaml
{
	static readonly Regex Null = new(@"\A(?:~|null|Null|NULL|)\z", RegexOptions.CultureInvariant);
	static readonly Regex True = new(@"\A(?:yes|Yes|YES|true|True|TRUE|on|On|ON)\z", RegexOptions.CultureInvariant);
	static readonly Regex False = new(@"\A(?:no|No|NO|false|False|FALSE|off|Off|OFF)\z", RegexOptions.CultureInvariant);
	static readonly Regex Int = new(@"\A[-+]?(?:0|[1-9][0-9_]*)\z", RegexOptions.CultureInvariant);
	// A sign only before a digit: -.5 is a string, as it was to PyYAML.
	static readonly Regex Float = new(@"\A(?:[-+]?[0-9][0-9_]*\.[0-9_]*(?:[eE][-+][0-9]+)?|\.[0-9_]+(?:[eE][-+][0-9]+)?)\z", RegexOptions.CultureInvariant);

	// The limits. The Template's claude-review.yml is about 170,000 characters, with about 300 nodes nested 7 deep.
	public const int MaxText = 1 << 20;
	public const int MaxDepth = 100;
	public const int MaxNodes = 100_000;
	public const long MaxScalarText = 1 << 22;

	// The first document's tree, null for a text with none (as safe_load("") is None). A text YAML can't parse, or one past a limit, throws
	// YamlException.
	public static JsonNode? Load(string text) => Load(text, MaxDepth, MaxNodes, MaxScalarText);

	public static JsonNode? Load(string text, int maxDepth, int maxNodes, long maxScalarText)
	{
		if (text.Length > MaxText)
			throw new YamlException($"a YAML text of more than {MaxText} characters");
		var reader = new Reader(new Parser(new StringReader(text)), maxDepth, maxNodes, maxScalarText);
		reader.Parser.Consume<StreamStart>();
		JsonNode? first = null;
		var documents = 0;
		// Every document is read, as YamlStream read them, so a later one that doesn't parse is refused too; the first is the tree.
		while (reader.Parser.TryConsume<DocumentStart>(out _))
		{
			var root = reader.Document();
			reader.Parser.Consume<DocumentEnd>();
			if (documents++ == 0)
				first = root;
		}
		reader.Parser.Consume<StreamEnd>();
		return first;
	}

	// A node read so far, with what it cost the budget (itself and all it holds) and its height (the levels of mappings and sequences
	// it is, 0 for a scalar), so an alias to it is charged the same again and nests as deep as it does.
	sealed record Anchored(JsonNode? Node, string? Text, int Nodes, long ScalarText, int Height);

	// A mapping or sequence being read: its node, its anchor, the budget spent when it opened, the height of the tallest node in it so
	// far, and, for a mapping, the key waiting for its value (the keys so far are in its JsonObject, which SetKey checks for a duplicate).
	sealed class Open(JsonNode node, string? anchor, int nodes, long scalarText)
	{
		public readonly JsonNode Node = node;
		public readonly string? Anchor = anchor;
		public readonly int Nodes = nodes;
		public readonly long ScalarText = scalarText;
		public int Tallest;
		public string? Key;
	}

	sealed class Reader(IParser parser, int maxDepth, int maxNodes, long maxScalarText)
	{
		public readonly IParser Parser = parser;
		readonly Dictionary<string, Anchored> anchors = [];
		int nodes;
		long scalarText;

		// One document's root, read with a stack of the mappings and sequences open around the current event. An anchor names a node in
		// its own document only, as in YamlStream and PyYAML; the budget is the whole text's.
		public JsonNode? Document()
		{
			anchors.Clear();
			var open = new Stack<Open>();
			while (true)
			{
				var parsed = Parser.Consume<ParsingEvent>();
				Anchored? done;
				switch (parsed)
				{
					case MappingStart or SequenceStart:
						if (open.TryPeek(out var outer) && outer.Node is JsonObject && outer.Key is null)
							throw new YamlException(parsed.Start, parsed.End, "a mapping key that isn't a scalar");
						if (open.Count + 1 > maxDepth)
							throw new YamlException(parsed.Start, parsed.End, $"YAML nested more than {maxDepth} deep");
						Charge(1, 0, parsed);
						var anchor = parsed is MappingStart mapping ? mapping.Anchor : ((SequenceStart)parsed).Anchor;
						open.Push(new Open(parsed is MappingStart ? new JsonObject() : new JsonArray(), anchor.IsEmpty ? null : anchor.Value, nodes - 1, scalarText));
						continue;
					case MappingEnd or SequenceEnd:
						var closed = open.Pop();
						done = new Anchored(closed.Node, null, nodes - closed.Nodes, scalarText - closed.ScalarText, closed.Tallest + 1);
						if (closed.Anchor is not null)
							anchors[closed.Anchor] = done;
						break;
					case Scalar scalar when open.TryPeek(out var map) && map.Node is JsonObject && map.Key is null:
						// A key: its text, not a node of the tree, so it is charged nothing here: its value is, and written keys' text is bounded by
						// MaxText. An alias to it is charged its text, as a key and as a value (and a node as a value).
						if (!scalar.Anchor.IsEmpty)
							anchors[scalar.Anchor.Value] = new Anchored(Scalar(scalar), scalar.Value, 1, scalar.Value.Length, 0);
						SetKey(map, scalar.Value, parsed);
						continue;
					case Scalar scalar:
						Charge(1, scalar.Value.Length, parsed);
						done = new Anchored(Scalar(scalar), scalar.Value, 1, scalar.Value.Length, 0);
						if (!scalar.Anchor.IsEmpty)
							anchors[scalar.Anchor.Value] = done;
						break;
					case AnchorAlias alias:
						if (!anchors.TryGetValue(alias.Value.Value, out var named))
							throw new YamlException(alias.Start, alias.End, $"an alias to an anchor not yet complete: *{alias.Value.Value}");
						if (open.TryPeek(out var keyed) && keyed.Node is JsonObject && keyed.Key is null)
						{
							var key = named.Text ?? throw new YamlException(alias.Start, alias.End, "a mapping key that isn't a scalar");
							// Charged as the text it repeats, which no limit on the written text bounds.
							Charge(0, key.Length, parsed);
							SetKey(keyed, key, parsed);
							continue;
						}
						// Checked and charged before it is copied: a node aliased past the depth or the budget is refused before it grows.
						if (open.Count + named.Height > maxDepth)
							throw new YamlException(alias.Start, alias.End, $"YAML nested more than {maxDepth} deep once its aliases are read in");
						Charge(named.Nodes, named.ScalarText, parsed);
						done = named with { Node = named.Node?.DeepClone() };
						break;
					default:
						// Not reached: between a document's start and its end YamlDotNet's parser gives only the events above.
						throw new YamlException(parsed.Start, parsed.End, $"a YAML event the bench doesn't read: {parsed.GetType().Name}");
				}
				if (!open.TryPeek(out var parent))
					return done.Node;
				parent.Tallest = Math.Max(parent.Tallest, done.Height);
				if (parent.Node is JsonArray array)
					array.Add(done.Node);
				else
				{
					((JsonObject)parent.Node)[parent.Key!] = done.Node;
					parent.Key = null;
				}
			}
		}

		void SetKey(Open map, string key, ParsingEvent at)
		{
			if (((JsonObject)map.Node).ContainsKey(key))
				throw new YamlException(at.Start, at.End, $"a key given twice: {key}");
			map.Key = key;
		}

		void Charge(int more, long text, ParsingEvent at)
		{
			nodes += more;
			scalarText += text;
			if (nodes > maxNodes)
				throw new YamlException(at.Start, at.End, $"more than {maxNodes} YAML nodes once its aliases are read in");
			if (scalarText > maxScalarText)
				throw new YamlException(at.Start, at.End, $"more than {maxScalarText} characters of YAML scalars once its aliases are read in");
		}
	}

	static JsonNode? Scalar(Scalar scalar)
	{
		var text = scalar.Value ?? "";
		if (scalar.Style is not ScalarStyle.Plain || (!scalar.Tag.IsNonSpecific && !scalar.Tag.IsEmpty && scalar.Tag.Value == "tag:yaml.org,2002:str"))
			return text;
		if (Null.IsMatch(text))
			return null;
		if (True.IsMatch(text) || False.IsMatch(text))
			return True.IsMatch(text);
		var digits = text.Replace("_", "", StringComparison.Ordinal);
		if (Int.IsMatch(text))
		{
			// The digits as written, without a + and with -0 as 0 (Int allows no leading zero), rather than through BigInteger, whose parse
			// and print of a long run of digits cost more than its length.
			var integer = digits.TrimStart('+');
			return JsonNode.Parse(integer == "-0" ? "0" : integer);
		}
		// ._ matches the pattern but holds no digit, which PyYAML failed on: it stays a string here. A float is written as repr() writes it, so
		// 1.0 keeps its point and reads as a float, not the int 1 (Py.Dumps tells them apart by the text).
		if (Float.IsMatch(text) && double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
			return JsonNode.Parse(Py.FloatRepr(number));
		return text;
	}
}
