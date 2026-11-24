// What the scripts in github/ share (ADR 0017): the gh runner (Gh: Graphql, Rest, NotFound), the JsonNode helpers (J), the argument
// parser (Arguments) and Script.Run, which turns an uncaught exception into exit 1 with a message rather than .NET's abort (exit 134).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

// A gh call that failed: its stderr, else its stdout, trimmed.
class GhException(string message) : Exception(message);

// A `gh api` call that GitHub answered with 404.
sealed class NotFound(string message) : GhException(message);

// One finished gh call.
readonly record struct GhRun(int Code, string Stdout, string Stderr);

// Runs gh. The runner is the process by default; a test hands in its own.
sealed class Gh(Func<IReadOnlyList<string>, GhRun>? runner = null)
{
	readonly Func<IReadOnlyList<string>, GhRun> runner = runner ?? Process;

	// One gh call, argv without the "gh".
	public GhRun Run(IReadOnlyList<string> argv) => runner(argv);

	public static GhRun Process(IReadOnlyList<string> argv)
	{
		var start = new ProcessStartInfo("gh")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		foreach (var arg in argv)
			start.ArgumentList.Add(arg);
		using var run = System.Diagnostics.Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync();
		var output = run.StandardOutput.ReadToEnd();
		run.WaitForExit();
		return new GhRun(run.ExitCode, output, err.Result);
	}

	// The call's stderr, else its stdout, trimmed: what a failure says.
	public static string Message(GhRun run) => run.Stderr.Trim() is { Length: > 0 } err ? err : run.Stdout.Trim();

	// The parsed JSON of one `gh api <path>` call; a 404 throws NotFound, any other failure GhException.
	public JsonNode? Rest(string path)
	{
		var run = Run(["api", path]);
		if (run.Code != 0)
		{
			if (run.Stderr.Contains("HTTP 404", StringComparison.Ordinal) || run.Stdout.Contains("HTTP 404", StringComparison.Ordinal))
				throw new NotFound(Message(run));
			throw new GhException(Message(run));
		}
		return JsonNode.Parse(run.Stdout);
	}

	// The data of one `gh api graphql` call: fields are "key=value" for -F, raw are -f (strings as they are).
	public JsonNode? Graphql(string query, IEnumerable<string> fields, IEnumerable<(string Key, string Value)> raw)
	{
		List<string> argv = ["api", "graphql", "-f", $"query={query}"];
		foreach (var field in fields)
			argv.AddRange(["-F", field]);
		foreach (var (key, value) in raw)
			argv.AddRange(["-f", $"{key}={value}"]);
		var run = Run(argv);
		if (run.Code != 0)
			throw new GhException(Message(run));
		return J.Req(JsonNode.Parse(run.Stdout), "data");
	}
}

// Python's reading of parsed JSON, which the ported scripts keep: Req is node["key"] (a missing key or a parent that isn't an object
// throws), Get is (node or {}).get("key"), and the rest read a value the way the Python's expressions did.
static class J
{
	public static JsonNode? Req(JsonNode? n, string key) =>
			n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : throw new KeyNotFoundException($"'{key}'");

	public static JsonNode? Get(JsonNode? n, string key) => n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

	public static bool Has(JsonNode? n, string key) => n is JsonObject o && o.ContainsKey(key);

	public static string? S(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

	// (n.get(key) or ""), for a string.
	public static string Str(JsonNode? n, string key) => S(Get(n, key)) ?? "";

	public static long? Int(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<long>(out var l) ? l : null;

	// Python's truth of a JSON value.
	public static bool B(JsonNode? v) => v switch
	{
		null => false,
		JsonObject o => o.Count > 0,
		JsonArray a => a.Count > 0,
		JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
		JsonValue jv when jv.TryGetValue<string>(out var s) => s.Length > 0,
		JsonValue jv when jv.TryGetValue<double>(out var d) => d != 0,
		_ => true,
	};

	// A value as Python's f-string prints it: None, a string as it is, anything else as JSON.
	public static string Fmt(JsonNode? v) => v is null ? "None" : S(v) ?? v.ToJsonString();

	// (n.get("nodes") or []).
	public static IEnumerable<JsonNode?> Nodes(JsonNode? n) => Get(n, "nodes") is JsonArray a ? a : [];

	// (n.get("author") or {}).get("login") or "".
	public static string Login(JsonNode? n) => Str(Get(n, "author"), "login");

	// A copy of a node that can go into another tree (a JsonNode has one parent).
	public static JsonNode? Copy(JsonNode? n) => n?.DeepClone();
}

// argparse's part the scripts use: positional arguments, flags, options with one value (--name value or --name=value), a long
// option given by any unique prefix of its name, -h and --help, and -- ending the options. A usage error exits 2 with the usage and
// the error on stderr, as argparse does.
sealed class Arguments(string prog, string usage, string description)
{
	readonly List<(string Name, bool TakesValue, string Help)> options = [];
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

	public string Usage => $"usage: {prog} {usage}\n";

	// The exit code a usage error or --help ends with, after writing what it says; null when the arguments parsed.
	public int? Parse(IEnumerable<string> args, TextWriter output, TextWriter err)
	{
		var rest = args.ToList();
		List<string> unknown = [];
		var positional = false;
		for (var i = 0; i < rest.Count; i++)
		{
			var arg = rest[i];
			if (positional || !LooksLikeOption(arg))
			{
				Positional.Add(arg);
				continue;
			}
			if (arg == "--")
			{
				positional = true;
				continue;
			}
			if (arg is "-h" or "--help")
			{
				output.Write(Help());
				return 0;
			}
			var (name, value) = arg.IndexOf('=') is var eq and > 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
			var matches = name.StartsWith("--", StringComparison.Ordinal)
					? options.Where(o => o.Name == name || o.Name.StartsWith(name, StringComparison.Ordinal)).ToList()
					: [];
			if (matches.Count > 1 && matches.Find(o => o.Name == name) is { Name: not null } exact)
				matches = [exact];
			if (matches.Count > 1)
				return Error($"ambiguous option: {name} could match {string.Join(", ", matches.Select(o => o.Name))}", err);
			if (matches.Count == 0)
			{
				unknown.Add(arg);
				continue;
			}
			var option = matches[0];
			if (!option.TakesValue)
			{
				if (value is not null)
					return Error($"argument {option.Name}: ignored explicit argument '{value}'", err);
				Values[option.Name] = null;
				continue;
			}
			if (value is null)
			{
				if (i + 1 >= rest.Count || (LooksLikeOption(rest[i + 1]) && !IsNegativeNumber(rest[i + 1])))
					return Error($"argument {option.Name}: expected one argument", err);
				value = rest[++i];
			}
			Values[option.Name] = value;
		}
		if (unknown.Count > 0)
			return Error($"unrecognized arguments: {string.Join(" ", unknown)}", err);
		return null;
	}

	public bool Has(string name) => Values.ContainsKey(name);

	// A float option's value as Python's float() reads it, else its default; a usage error is thrown as ArgumentError.
	public double Float(string name, double fallback)
	{
		if (!Values.TryGetValue(name, out var text) || text is null)
			return fallback;
		var trimmed = text.Trim().Replace("_", "", StringComparison.Ordinal);
		var sign = trimmed.StartsWith('-') ? -1 : 1;
		var bare = trimmed.TrimStart('+', '-').ToLowerInvariant();
		if (bare is "inf" or "infinity")
			return sign * double.PositiveInfinity;
		if (bare == "nan")
			return double.NaN;
		if (text.Contains("__", StringComparison.Ordinal) || !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
			throw new ArgumentError($"argument {name}: invalid float value: '{text}'");
		return number;
	}

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
			text.Append($"  {(takesValue ? $"{name} {name.TrimStart('-').ToUpperInvariant().Replace('-', '_')}" : name),-22}{help}\n");
		return text.ToString();
	}

	static bool LooksLikeOption(string arg) => arg.Length > 1 && arg[0] == '-' && !IsNegativeNumber(arg);

	static bool IsNegativeNumber(string arg) =>
			arg.Length > 1 && arg[0] == '-' && double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}

// A usage error found after the arguments parsed (a bad value); the caller hands it to Arguments.Error.
sealed class ArgumentError(string message) : Exception(message);

static class Script
{
	// The script's exit code; an uncaught exception is a message on stderr and exit 1, as a Python traceback was.
	public static int Run(Func<TextWriter, TextWriter, int> main)
	{
		var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
		var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
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

	// Python's format(value, "g"): six significant digits, no trailing zeros, an exponent outside 1e-4 to 1e6.
	public static string G(double value)
	{
		if (double.IsNaN(value))
			return "nan";
		if (double.IsInfinity(value))
			return value > 0 ? "inf" : "-inf";
		if (value == 0)
			return double.IsNegative(value) ? "-0" : "0";
		var exponent = (int)Math.Floor(Math.Log10(Math.Abs(double.Parse(value.ToString("E5", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))));
		if (exponent is >= -4 and < 6)
		{
			var text = value.ToString("F" + (5 - exponent), CultureInfo.InvariantCulture);
			return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
		}
		var mantissa = (value / Math.Pow(10, exponent)).ToString("F5", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
		return $"{mantissa}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
	}
}
