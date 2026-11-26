// What the C# scripts in .github/scripts/ share (ADR 0017 in the Application Factory): the argument parser (Arguments, held to argparse,
// which the Python scripts parsed with), Script.Run, which turns an uncaught exception into exit 1 with a message rather than .NET's abort
// (exit 134), Script.Which, which finds a program in PATH's absolute folders only, and SurrogateEscape, Python's reading of bytes that aren't
// UTF-8. The gh runner joins it with the first script here that calls gh. In a namespace of its own, so the Factory's test project can
// compile it beside the Factory's own Gh.cs, whose classes have the same names.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Baseline;

// argparse's part the scripts use: options with one value (--name value or --name=value), required ones among them, flags, a long option
// given by any unique prefix of its name (--help's too), -h and --help, and -- ending the options. An argument that matches no option is a
// value when it looks like a negative number (argparse's -\.?\d) or holds a space. A script with no positional arguments refuses one as
// argparse does. A usage error exits 2 with the usage and the error on stderr, as argparse does.
sealed class Arguments(string prog, string usage, string description, bool positionals = false)
{
	const string HelpName = "--help";
	readonly List<(string Name, bool TakesValue, string Help)> options = [];
	readonly List<string> required = [];
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
				Values[option.Name] = null;
				continue;
			}
			if (value is null)
			{
				if (i + 1 >= rest.Count || LooksLikeOption(rest[i + 1]))
					return Error($"argument {option.Name}: expected one argument", err);
				value = rest[++i];
			}
			Values[option.Name] = value;
		}
		// argparse names the required options left out before the arguments it didn't recognize.
		if (required.Where(name => !Values.ContainsKey(name)).ToList() is { Count: > 0 } missing)
			return Error($"the following arguments are required: {string.Join(", ", missing)}", err);
		if (unknown.Count > 0)
			return Error($"unrecognized arguments: {string.Join(" ", unknown)}", err);
		return null;
	}

	public bool Has(string name) => Values.ContainsKey(name);

	// An option's value, else its default.
	public string Value(string name, string fallback) => Values.TryGetValue(name, out var value) && value is not null ? value : fallback;

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
