// A YAML file as the JsonNode tree PyYAML's yaml.safe_load() returned, for the bench and the tests that read the Template's workflows. YamlDotNet
// (the one package the bench takes, ADR 0017's Update for mpaulosky/Application_Factory#161) parses it; this gives each plain scalar the type
// PyYAML's YAML 1.1 resolvers gave it: null (~, null, an empty value), a bool (true, yes, on and false, no, off, in any of their three cases), a
// decimal int or float (with _ between its digits), and a string for anything else. A quoted scalar, a block scalar and a !!str one are strings.
// What these files don't hold is left out on purpose: an int in base 2, 8, 16 or 60, .inf and .nan, a !!timestamp and the << merge key read as
// strings (and the key <<), where PyYAML read them as numbers, dates and merged mappings. A key is its scalar's text (PyYAML's True for on is "on"),
// and a key given twice is refused, as GitHub refuses it in a workflow, where PyYAML kept its last value.
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

static class Yaml
{
	static readonly Regex Null = new(@"\A(?:~|null|Null|NULL|)\z", RegexOptions.CultureInvariant);
	static readonly Regex True = new(@"\A(?:yes|Yes|YES|true|True|TRUE|on|On|ON)\z", RegexOptions.CultureInvariant);
	static readonly Regex False = new(@"\A(?:no|No|NO|false|False|FALSE|off|Off|OFF)\z", RegexOptions.CultureInvariant);
	static readonly Regex Int = new(@"\A[-+]?(?:0|[1-9][0-9_]*)\z", RegexOptions.CultureInvariant);
	// A sign only before a digit: -.5 is a string, as it was to PyYAML.
	static readonly Regex Float = new(@"\A(?:[-+]?[0-9][0-9_]*\.[0-9_]*(?:[eE][-+][0-9]+)?|\.[0-9_]+(?:[eE][-+][0-9]+)?)\z", RegexOptions.CultureInvariant);

	// The first document's tree, null for a text with none (as safe_load("") is None). A text YAML can't parse throws YamlException.
	public static JsonNode? Load(string text)
	{
		var stream = new YamlStream();
		stream.Load(new StringReader(text));
		return stream.Documents.Count == 0 ? null : Node(stream.Documents[0].RootNode);
	}

	static JsonNode? Node(YamlNode node) => node switch
	{
		YamlMappingNode mapping => Mapping(mapping),
		YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(Node)]),
		YamlScalarNode scalar => Scalar(scalar),
		_ => throw new YamlException($"a YAML node the bench doesn't read: {node.NodeType}"),
	};

	static JsonObject Mapping(YamlMappingNode mapping)
	{
		var result = new JsonObject();
		foreach (var (key, value) in mapping.Children)
			result[key is YamlScalarNode scalar ? scalar.Value ?? "" : throw new YamlException("a mapping key that isn't a scalar")] = Node(value);
		return result;
	}

	static JsonNode? Scalar(YamlScalarNode scalar)
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
			return JsonNode.Parse(BigInteger.Parse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
		// ._ matches the pattern but holds no digit, which PyYAML failed on: it stays a string here. A float is written as repr() writes it, so
		// 1.0 keeps its point and reads as a float, not the int 1 (Py.Dumps tells them apart by the text).
		if (Float.IsMatch(text) && double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
			return JsonNode.Parse(Py.FloatRepr(number));
		return text;
	}
}
