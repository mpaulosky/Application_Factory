// What the scripts in github/ share (ADR 0017): the gh runner (Gh: Graphql, Rest, NotFound), the JsonNode helpers (J), the argument
// parser (Arguments) and Script.Run, which turns an uncaught exception into exit 1 with a message rather than .NET's abort (exit 134).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
		var start = new ProcessStartInfo(Which("gh", Environment.GetEnvironmentVariable("PATH")))
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

	// The first executable file called name in a folder of path (PATH's form), as execvp finds it but for the folders that aren't
	// absolute, which are skipped: .NET's own lookup tries the program's folder and the current one before PATH, so a gh there would run
	// with the owner's token. None found throws FileNotFoundException, as starting a missing program did.
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

	// A whole number, parsed or built in code (a JsonValue built from an int doesn't read as a long).
	public static long? Int(JsonNode? v) => v is JsonValue jv ? jv.TryGetValue<long>(out var l) ? l : jv.TryGetValue<int>(out var i) ? i : null : null;

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

	// n["nodes"], iterated: a missing key, a parent that isn't an object, or a value that isn't a list throws, as the Python's KeyError or
	// TypeError did, so an answer that lacks a list is unread rather than read as empty.
	public static JsonArray NodesOf(JsonNode? n) =>
			Req(n, "nodes") as JsonArray ?? throw new InvalidOperationException("'nodes' is not a list");

	// (n.get("author") or {}).get("login") or "".
	public static string Login(JsonNode? n) => Str(Get(n, "author"), "login");

	// A copy of a node that can go into another tree (a JsonNode has one parent).
	public static JsonNode? Copy(JsonNode? n) => n?.DeepClone();
}

// argparse's part the scripts use: positional arguments, flags, options with one value (--name value or --name=value), a float option
// whose value is read when it is met, a long option given by any unique prefix of its name (--help's too), -h and --help, and -- ending
// the options. An argument that matches no option is positional when it looks like a negative number (argparse's -\.?\d) or holds a
// space. A usage error exits 2 with the usage and the error on stderr, as argparse does.
sealed class Arguments(string prog, string usage, string description)
{
	const string HelpName = "--help";
	readonly List<(string Name, bool TakesValue, string Help)> options = [];
	readonly HashSet<string> floats = [];
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

	// An option whose value is a float, refused as a usage error when it is met, as argparse's type=float is.
	public Arguments FloatOption(string name, string help)
	{
		floats.Add(name);
		return Option(name, help);
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
				(taken ? unknown : Positional).Add(arg);
				continue;
			}
			if (arg == "--")
			{
				positional = true;
				if (taken)
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
			// LooksLikeOption read an argument with a space or a negative number that matches nothing as positional, so this is an unknown option.
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
				Values[option.Name] = null;
				continue;
			}
			if (value is null)
			{
				if (i + 1 >= rest.Count || LooksLikeOption(rest[i + 1]))
					return Error($"argument {option.Name}: expected one argument", err);
				value = rest[++i];
			}
			if (floats.Contains(option.Name))
			{
				try
				{
					ParseFloat(option.Name, value);
				}
				catch (ArgumentError error)
				{
					return Error(error.Message, err);
				}
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
		return ParseFloat(name, text);
	}

	static double ParseFloat(string name, string text)
	{
		var trimmed = AsciiDigits(text).Trim();
		var sign = trimmed.StartsWith('-') ? -1 : 1;
		// One sign at most: float() refuses "--inf".
		var bare = (trimmed.StartsWith('-') || trimmed.StartsWith('+') ? trimmed[1..] : trimmed).ToLowerInvariant();
		if (bare is "inf" or "infinity")
			return sign * double.PositiveInfinity;
		if (bare == "nan")
			return double.NaN;
		// An underscore only between two digits, as float() takes it.
		if (Regex.IsMatch(trimmed, @"(?<![0-9])_|_(?![0-9])")
				|| !double.TryParse(trimmed.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
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

	// The options a name (before any =) names: itself, else every one it is a prefix of, --help among them; a name that doesn't start
	// with -- names only itself (-h is --help).
	List<(string Name, bool TakesValue, string Help)> Matches(string name)
	{
		List<(string Name, bool TakesValue, string Help)> all = [.. options, (HelpName, false, "")];
		if (name == "-h")
			return [all[^1]];
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

	// text with every Unicode decimal digit made its ASCII one, astral ones too, as float() and Python's \d read them (-\u0665 is -5).
	static string AsciiDigits(string text)
	{
		var ascii = new StringBuilder(text.Length);
		foreach (var rune in text.EnumerateRunes())
			ascii.Append(Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber ? (char)('0' + (int)Rune.GetNumericValue(rune)) : rune.ToString());
		return ascii.ToString();
	}

	// argparse's _negative_number_matcher as Python 3.14 has it, on AsciiDigits' text: a - then a digit, or a . and a digit, with anything after (-5x and -1e3
	// are values). 3.12 and earlier take only the -5 and -.5 forms and read -5x and -1e3 as options, so GhTests leaves those to a fixed
	// expectation rather than the machine's python3.
	static readonly Regex NegativeNumber = new(@"\A-\.?\d", RegexOptions.CultureInvariant);
}

// A usage error found after the arguments parsed (a bad value); the caller hands it to Arguments.Error.
sealed class ArgumentError(string message) : Exception(message);

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
