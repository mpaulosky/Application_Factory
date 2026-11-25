// The Fleet registry, github/repos.txt, as settings.py's read_repo_list() reads it (a test holds the two equal while settings.py is left).
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;

static class Repos
{
	// owner/repo: ASCII letters and digits, _, . and - on each side of one /, as GitHub names are, and neither side . or .. (a path
	// step in the REST calls); a trailing line break too is refused. status.cs, land.cs and settings.cs refuse any other name before a call.
	public static readonly Regex Name = new(@"\A(?!\.\.?/)[A-Za-z0-9_.-]+/(?!\.\.?\z)[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant);

	// The line breaks str.splitlines() splits on, and the extra whitespace str.split() splits on that char.IsWhiteSpace doesn't know.
	static readonly char[] LineBreaks = ['\n', '\r', '\v', '\f', '\x1c', '\x1d', '\x1e', '\x85', '\u2028', '\u2029'];

	// {owner/repo: scope} from repos.txt, in the file's order; scope is "full", "factory" (the Factory: every check of a full repo, but not
	// a Baseline repo to roll out or release) or "repo-settings-only". folder defaults to the folder of the script that runs.
	public static OrderedDictionary<string, string> Read(string? folder = null)
	{
		folder ??= AppContext.GetData("EntryPointFileDirectoryPath") as string
				?? throw new InvalidOperationException("Repos.Read needs the folder repos.txt is in when no script file runs");
		OrderedDictionary<string, string> repos = [];
		// UTF-8 as read_text() reads it: a byte-order mark is kept as a character and bytes that aren't UTF-8 throw.
		var text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(Path.Combine(folder, "repos.txt")));
		// "\r\n" is one break, as splitlines() reads it; the empty line Split leaves between them is skipped like any blank line.
		foreach (var line in text.Split(LineBreaks))
		{
			var words = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
					.SelectMany(word => word.Split(['\x1c', '\x1d', '\x1e', '\x1f'], StringSplitOptions.RemoveEmptyEntries)).ToArray();
			if (words.Length > 0)
				repos[words[0]] = words.Length > 1 ? words[1] : "full";
		}
		return repos;
	}
}

// The Fleet registry read the first time a key or scope is asked for, not before: status.cs and land.cs need it for every repo when
// no target is named, but only for a scope when one is, so a run that never gets that far (a usage error, --help, a repo whose read
// failed) doesn't need a readable repos.txt. When it is needed, its error is the one Repos.Read throws; a named repo isn't given
// "full" in its place, since a wrong scope would decide a wrong landing action (mpaulosky/Application_Factory#178).
sealed class LazyRepos(Func<OrderedDictionary<string, string>> read) : IReadOnlyDictionary<string, string>
{
	readonly Lazy<OrderedDictionary<string, string>> repos = new(read);

	public string this[string key] => repos.Value[key];
	public IEnumerable<string> Keys => repos.Value.Keys;
	public IEnumerable<string> Values => repos.Value.Values;
	public int Count => repos.Value.Count;
	public bool ContainsKey(string key) => repos.Value.ContainsKey(key);
	public bool TryGetValue(string key, out string value) => repos.Value.TryGetValue(key, out value!);
	public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => repos.Value.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
